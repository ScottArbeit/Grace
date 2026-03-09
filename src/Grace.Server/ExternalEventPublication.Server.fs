namespace Grace.Server

open Azure.Identity
open Azure.Messaging.ServiceBus
open Grace.Server.ApplicationContext
open Grace.Shared
open Grace.Shared.AzureEnvironment
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Events
open Grace.Types.ExternalEvents
open Grace.Types.Types
open Grace.Types.Validation
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks

module ExternalEventPublication =

    type ReplaySource =
        | GraceEventSource of GraceEvent
        | RuntimeLifecycleSource of AgentRuntimeLifecycleSource
        | CanonicalEnvelopeSource of CanonicalExternalEventEnvelope

    let private log = loggerFactory.CreateLogger("ExternalEventPublication.Server")
    let private defaultAzureCredential = lazy (DefaultAzureCredential())
    let private replaySources = ConcurrentDictionary<string, ReplaySource>(StringComparer.Ordinal)

    let private canonicalServiceBusClient =
        lazy
            match pubSubSettings.AzureServiceBus with
            | Some settings when settings.UseManagedIdentity ->
                let fullyQualifiedNamespace =
                    if not (String.IsNullOrWhiteSpace settings.FullyQualifiedNamespace) then
                        settings.FullyQualifiedNamespace
                    else
                        AzureEnvironment.tryGetServiceBusFullyQualifiedNamespace ()
                        |> Option.defaultWith (fun () -> invalidOp "Azure Service Bus namespace is required for managed identity.")

                ServiceBusClient(fullyQualifiedNamespace, defaultAzureCredential.Value)
            | Some settings -> ServiceBusClient(settings.ConnectionString)
            | None -> invalidOp "Azure Service Bus settings are required for canonical external event publication."

    let private canonicalServiceBusSender = lazy (canonicalServiceBusClient.Value.CreateSender(pubSubSettings.AzureServiceBus.Value.TopicName))

    let private addProperty (properties: IDictionary<string, obj>) (name: string) (value: obj option) =
        match value with
        | Some propertyValue -> properties[name] <- propertyValue
        | None -> ()

    let private addGuidProperty (properties: IDictionary<string, obj>) (name: string) (value: Guid option) =
        value
        |> Option.filter (fun guidValue -> guidValue <> Guid.Empty)
        |> Option.map box
        |> addProperty properties name

    let publishCanonicalEvent (envelope: CanonicalExternalEventEnvelope) =
        task {
            try
                let body = CanonicalExternalEventEnvelope.serializeToUtf8Bytes envelope
                let message = ServiceBusMessage(body)
                message.ContentType <- "application/json"
                message.MessageId <- envelope.EventId
                message.CorrelationId <- envelope.CorrelationId
                message.Subject <- envelope.EventName
                message.ApplicationProperties[ "eventName" ] <- envelope.EventName
                message.ApplicationProperties[ "eventVersion" ] <- envelope.EventVersion

                envelope.ActorType
                |> Option.map box
                |> addProperty message.ApplicationProperties "actorType"

                envelope.ActorId
                |> Option.map box
                |> addProperty message.ApplicationProperties "actorId"

                addGuidProperty message.ApplicationProperties (nameof OwnerId) envelope.OwnerId
                addGuidProperty message.ApplicationProperties (nameof OrganizationId) envelope.OrganizationId
                addGuidProperty message.ApplicationProperties (nameof RepositoryId) envelope.RepositoryId
                do! canonicalServiceBusSender.Value.SendMessageAsync(message)
                return Ok()
            with
            | ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed publishing canonical external event {EventName} ({EventId}).",
                    getCurrentInstantExtended (),
                    getMachineName,
                    envelope.CorrelationId,
                    envelope.EventName,
                    envelope.EventId
                )

                return Error ex.Message
        }

    let rememberGraceEventSource (envelope: CanonicalExternalEventEnvelope) (graceEvent: GraceEvent) =
        replaySources[envelope.EventId] <- GraceEventSource graceEvent

    let rememberRuntimeSource (envelope: CanonicalExternalEventEnvelope) (runtimeSource: AgentRuntimeLifecycleSource) =
        replaySources[envelope.EventId] <- RuntimeLifecycleSource runtimeSource

    let rememberEnvelopeSource (envelope: CanonicalExternalEventEnvelope) = replaySources[envelope.EventId] <- CanonicalEnvelopeSource envelope

    let tryGetReplaySource eventId =
        match replaySources.TryGetValue(eventId) with
        | true, source -> Some source
        | _ -> None

    let private workItemLocatorKindToString kind =
        match kind with
        | WorkItemLocatorKind.Id -> "id"
        | WorkItemLocatorKind.Number -> "number"
        | WorkItemLocatorKind.Opaque -> "opaque"

    let buildRuntimeEnvelope (runtimeSource: AgentRuntimeLifecycleSource) =
        let payloadNode = JsonObject()
        payloadNode["agentId"] <- JsonValue.Create(runtimeSource.AgentId)
        payloadNode["sessionId"] <- JsonValue.Create(runtimeSource.SessionId)
        payloadNode["repositoryId"] <- JsonValue.Create(runtimeSource.RepositoryId)
        payloadNode["ownerId"] <- JsonValue.Create(runtimeSource.OwnerId)
        payloadNode["organizationId"] <- JsonValue.Create(runtimeSource.OrganizationId)

        runtimeSource.BranchId
        |> Option.iter (fun value -> payloadNode["branchId"] <- JsonValue.Create(value))

        runtimeSource.WorkItemId
        |> Option.iter (fun value -> payloadNode["workItemId"] <- JsonValue.Create(value))

        runtimeSource.WorkItemLocator
        |> Option.iter (fun locator ->
            let node = JsonObject()
            node["raw"] <- JsonValue.Create(locator.Raw)

            node["kind"] <- JsonValue.Create(
                workItemLocatorKindToString (
                    match locator.Kind with
                    | "id" -> WorkItemLocatorKind.Id
                    | "number" -> WorkItemLocatorKind.Number
                    | _ -> WorkItemLocatorKind.Opaque
                )
            )

            payloadNode["workItemLocator"] <- node)

        runtimeSource.PromotionSetId
        |> Option.iter (fun value -> payloadNode["promotionSetId"] <- JsonValue.Create(value))

        payloadNode[if runtimeSource.EventName = CanonicalEventName.toString CanonicalEventName.AgentWorkStarted then
                        "startedAt"
                    else
                        "stoppedAt"] <- JsonValue.Create(runtimeSource.OccurredAt)

        payloadNode["lifecycleState"] <- JsonValue.Create(runtimeSource.LifecycleState)

        runtimeSource.Message
        |> Option.iter (fun value -> payloadNode["message"] <- JsonValue.Create(value))

        runtimeSource.OperationId
        |> Option.iter (fun value -> payloadNode["operationId"] <- JsonValue.Create(value))

        payloadNode["wasIdempotentReplay"] <- JsonValue.Create(runtimeSource.WasIdempotentReplay)

        runtimeSource.Source
        |> Option.iter (fun value -> payloadNode["source"] <- JsonValue.Create(value))

        runtimeSource.Result
        |> Option.iter (fun result ->
            let node = JsonObject()
            node["message"] <- JsonValue.Create(result.Message)
            node["operationId"] <- JsonValue.Create(result.OperationId)
            node["wasIdempotentReplay"] <- JsonValue.Create(result.WasIdempotentReplay)
            payloadNode["result"] <- node)

        match CanonicalEventName.tryParse runtimeSource.EventName with
        | Some eventName ->
            match
                CanonicalExternalEventEnvelope.tryCreate
                    eventName
                    runtimeSource.OccurredAt
                    runtimeSource.CorrelationId
                    runtimeSource.OwnerId
                    runtimeSource.OrganizationId
                    runtimeSource.RepositoryId
                    { ActorType = "AgentSession"; ActorId = runtimeSource.SessionId }
                    (JsonSerializer.SerializeToElement(payloadNode, CanonicalExternalEventEnvelope.canonicalJsonSerializerOptions))
                with
            | Ok envelope when
                (CanonicalExternalEventEnvelope.measureSize envelope)
                    .ByteCount < CanonicalExternalEventEnvelope.MaxDocumentBytes
                ->
                Grace.Server.ExternalEvents.Published envelope
            | Ok envelope ->
                Grace.Server.ExternalEvents.Failed
                    {
                        RawEventCase = runtimeSource.EventName
                        CorrelationId = runtimeSource.CorrelationId
                        Reason =
                            Grace.Server.ExternalEvents.OversizeDocument(
                                (CanonicalExternalEventEnvelope.measureSize envelope)
                                    .ByteCount
                            )
                    }
            | Error error ->
                Grace.Server.ExternalEvents.Failed
                    {
                        RawEventCase = runtimeSource.EventName
                        CorrelationId = runtimeSource.CorrelationId
                        Reason = Grace.Server.ExternalEvents.MissingActorIdentity error
                    }
        | None -> Grace.Server.ExternalEvents.InternalOnly runtimeSource.EventName

    let buildValidationRequestedEnvelope
        (validationSet: ValidationSetDto)
        (sourceEnvelope: CanonicalExternalEventEnvelope)
        (targetBranchId: BranchId)
        (targetBranchName: BranchName)
        (promotionSetId: PromotionSetId option)
        (stepsComputationAttempt: int option)
        (matchedRules: MatchedRule list)
        =
        let validationsSummary =
            {
                ValidationCount = validationSet.Validations.Length
                Validations =
                    validationSet.Validations
                    |> List.map (fun validation ->
                        {
                            Name = validation.Name
                            Version = validation.Version
                            ExecutionMode =
                                match validation.ExecutionMode with
                                | ValidationExecutionMode.Synchronous -> "synchronous"
                                | ValidationExecutionMode.AsyncCallback -> "async-callback"
                            RequiredForApply = validation.RequiredForApply
                        })
            }

        let payload =
            {|
                validationSetId = validationSet.ValidationSetId
                promotionSetId = promotionSetId
                stepsComputationAttempt = stepsComputationAttempt
                targetBranchId = targetBranchId
                targetBranchName = targetBranchName
                sourceEventName = sourceEnvelope.EventName
                matchedRules = matchedRules
                validationsSummary = validationsSummary
            |}

        match
            CanonicalExternalEventEnvelope.tryCreate
                CanonicalEventName.ValidationRequested
                (getCurrentInstant ())
                sourceEnvelope.CorrelationId
                validationSet.OwnerId
                validationSet.OrganizationId
                validationSet.RepositoryId
                { ActorType = "ValidationSet"; ActorId = $"{validationSet.ValidationSetId}" }
                (CanonicalExternalEventEnvelope.toPayloadElement payload)
            with
        | Ok envelope when
            (CanonicalExternalEventEnvelope.measureSize envelope)
                .ByteCount < CanonicalExternalEventEnvelope.MaxDocumentBytes
            ->
            Grace.Server.ExternalEvents.Published envelope
        | Ok envelope ->
            Grace.Server.ExternalEvents.Failed
                {
                    RawEventCase = RawEventCase.derived "grace.validation.requested"
                    CorrelationId = sourceEnvelope.CorrelationId
                    Reason =
                        Grace.Server.ExternalEvents.OversizeDocument(
                            (CanonicalExternalEventEnvelope.measureSize envelope)
                                .ByteCount
                        )
                }
        | Error error ->
            Grace.Server.ExternalEvents.Failed
                {
                    RawEventCase = RawEventCase.derived "grace.validation.requested"
                    CorrelationId = sourceEnvelope.CorrelationId
                    Reason = Grace.Server.ExternalEvents.MissingActorIdentity error
                }

    let rebuildForReplay eventId =
        match tryGetReplaySource eventId with
        | Some (GraceEventSource graceEvent) -> Some(Grace.Server.ExternalEvents.buildGraceEvent graceEvent)
        | Some (RuntimeLifecycleSource runtimeSource) -> Some(buildRuntimeEnvelope runtimeSource)
        | Some (CanonicalEnvelopeSource envelope) -> Some(Grace.Server.ExternalEvents.Published envelope)
        | None -> None
