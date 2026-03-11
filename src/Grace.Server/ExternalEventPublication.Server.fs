namespace Grace.Server

open Azure.Identity
open Azure.Messaging.ServiceBus
open Grace.Actors
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Shared
open Grace.Shared.AzureEnvironment
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Events
open Grace.Types.ExternalEvents
open Grace.Types.Queue
open Grace.Types.Reference
open Grace.Types.Types
open Grace.Types.Validation
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Net
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading.Tasks

module ExternalEventPublication =

    [<Literal>]
    let private ReplaySourcePartitionKey = "ExternalEventReplaySource"

    [<CLIMutable>]
    type private ReplaySourceDocument =
        {
            id: string
            PartitionKey: string
            EventId: string
            EventName: string
            EventVersion: int
            SourceKind: string
            ActorType: string
            ActorId: string
            CorrelationId: CorrelationId
            RecordedAt: Instant
            RawGraceEvent: GraceEvent option
            RuntimeSource: AgentRuntimeLifecycleSource option
            ValidationRequestedSource: ValidationRequestedReplaySource option
        }

    type private ValidationTriggerContext =
        {
            RepositoryId: RepositoryId
            TargetBranchId: BranchId
            TargetBranchName: BranchName
            PromotionSetId: PromotionSetId option
            StepsComputationAttempt: int option
        }

    let private log = loggerFactory.CreateLogger("ExternalEventPublication.Server")
    let private defaultAzureCredential = lazy (DefaultAzureCredential())

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

    let private validationSetsByRepository = ConcurrentDictionary<RepositoryId, ConcurrentDictionary<ValidationSetId, ValidationSetDto>>()

    let private getValidationSetCache (repositoryId: RepositoryId) =
        validationSetsByRepository.GetOrAdd(repositoryId, (fun _ -> ConcurrentDictionary<ValidationSetId, ValidationSetDto>()))

    let private rememberValidationSetInCache (validationSet: ValidationSetDto) =
        let repositoryCache = getValidationSetCache validationSet.RepositoryId
        repositoryCache[validationSet.ValidationSetId] <- validationSet

    let private forgetValidationSetFromCache (repositoryId: RepositoryId) (validationSetId: ValidationSetId) =
        match validationSetsByRepository.TryGetValue(repositoryId) with
        | true, repositoryCache ->
            let mutable removed = Unchecked.defaultof<ValidationSetDto>

            repositoryCache.TryRemove(validationSetId, &removed)
            |> ignore

            if repositoryCache.IsEmpty then
                let mutable ignored = Unchecked.defaultof<ConcurrentDictionary<ValidationSetId, ValidationSetDto>>

                validationSetsByRepository.TryRemove(repositoryId, &ignored)
                |> ignore
        | _ -> ()

    let private getCachedValidationSets (repositoryId: RepositoryId) =
        match validationSetsByRepository.TryGetValue(repositoryId) with
        | true, repositoryCache ->
            repositoryCache.Values
            |> Seq.filter (fun validationSet -> validationSet.DeletedAt.IsNone)
            |> Seq.toList
        | _ -> []

    let private mergeValidationSets (cachedValidationSets: ValidationSetDto list) (storedValidationSets: ValidationSetDto list) =
        let merged = Dictionary<ValidationSetId, ValidationSetDto>()

        storedValidationSets
        |> List.iter (fun validationSet -> merged[validationSet.ValidationSetId] <- validationSet)

        cachedValidationSets
        |> List.iter (fun validationSet -> merged[validationSet.ValidationSetId] <- validationSet)

        merged.Values |> Seq.toList

    let private promotionSetContexts = ConcurrentDictionary<string, ValidationTriggerContext>()

    let private promotionSetContextCacheKey (repositoryId: RepositoryId) (promotionSetId: PromotionSetId) = $"{repositoryId}:{promotionSetId}"

    let private rememberPromotionSetContextInCache (context: ValidationTriggerContext) =
        match context.PromotionSetId with
        | Some promotionSetId -> promotionSetContexts[promotionSetContextCacheKey context.RepositoryId promotionSetId] <- context
        | None -> ()

    let private tryGetPromotionSetContextFromCache (repositoryId: RepositoryId) (promotionSetId: PromotionSetId) =
        match promotionSetContexts.TryGetValue(promotionSetContextCacheKey repositoryId promotionSetId) with
        | true, cachedContext -> Some cachedContext
        | _ -> None

    let private forgetPromotionSetContextFromCache (repositoryId: RepositoryId) (promotionSetId: PromotionSetId) =
        let mutable ignored = Unchecked.defaultof<ValidationTriggerContext>

        promotionSetContexts.TryRemove(promotionSetContextCacheKey repositoryId promotionSetId, &ignored)
        |> ignore


    let internal absorbValidationSetEvent (validationSetEvent: ValidationSetEvent) =
        let tryGetGuidProperty (name: string) =
            match validationSetEvent.Metadata.Properties.TryGetValue(name) with
            | true, value ->
                let mutable parsed = Guid.Empty

                if String.IsNullOrWhiteSpace value |> not
                   && Guid.TryParse(value, &parsed)
                   && parsed <> Guid.Empty then
                    Some parsed
                else
                    None
            | _ -> None

        match validationSetEvent.Event with
        | ValidationSetEventType.Created validationSet
        | ValidationSetEventType.Updated validationSet -> rememberValidationSetInCache validationSet
        | ValidationSetEventType.LogicalDeleted _ ->
            match tryGetGuidProperty (nameof RepositoryId), tryGetGuidProperty "ActorId" with
            | Some repositoryId, Some validationSetId -> forgetValidationSetFromCache repositoryId validationSetId
            | _ -> ()

    let private getValidationSetsForTriggerAsync (repositoryId: RepositoryId) (correlationId: CorrelationId) =
        task {
            let cachedValidationSets = getCachedValidationSets repositoryId
            let! storedValidationSets = getValidationSets repositoryId 500 false correlationId

            storedValidationSets
            |> List.iter rememberValidationSetInCache

            return
                if storedValidationSets.IsEmpty then
                    cachedValidationSets
                else
                    mergeValidationSets cachedValidationSets storedValidationSets
        }

    let private addGuidProperty (properties: IDictionary<string, obj>) (name: string) (value: Guid option) =
        value
        |> Option.filter (fun guidValue -> guidValue <> Guid.Empty)
        |> Option.map box
        |> addProperty properties name

    let private parseReplaySourceKind value =
        match value with
        | "raw-grace-event" -> Some ReplaySourceKind.RawGraceEvent
        | "agent-runtime-lifecycle" -> Some ReplaySourceKind.AgentRuntimeLifecycle
        | "validation-requested" -> Some ReplaySourceKind.ValidationRequested
        | _ -> None

    let private toReplaySourceDocument (source: DurableReplaySourceRecord) =
        {
            id = source.EventId
            PartitionKey = ReplaySourcePartitionKey
            EventId = source.EventId
            EventName = source.EventName
            EventVersion = source.EventVersion
            SourceKind = ReplaySourceKind.toString source.SourceKind
            ActorType = source.ActorType
            ActorId = source.ActorId
            CorrelationId = source.CorrelationId
            RecordedAt = source.RecordedAt
            RawGraceEvent = source.RawGraceEvent
            RuntimeSource = source.RuntimeSource
            ValidationRequestedSource = source.ValidationRequestedSource
        }

    let private tryFromReplaySourceDocument (document: ReplaySourceDocument) =
        match parseReplaySourceKind document.SourceKind with
        | Some sourceKind ->
            Some
                {
                    EventId = document.EventId
                    EventName = document.EventName
                    EventVersion = document.EventVersion
                    SourceKind = sourceKind
                    ActorType = document.ActorType
                    ActorId = document.ActorId
                    CorrelationId = document.CorrelationId
                    RecordedAt = document.RecordedAt
                    RawGraceEvent = document.RawGraceEvent
                    RuntimeSource = document.RuntimeSource
                    ValidationRequestedSource = document.ValidationRequestedSource
                }
        | None -> None

    let private replayRequestOptions correlationId =
        let correlationIdValue = $"{correlationId}"
        ItemRequestOptions(AddRequestHeaders = fun headers -> headers.Add(Constants.CorrelationIdHeaderKey, correlationIdValue))

    let private rememberReplaySourceAsync (source: DurableReplaySourceRecord) =
        task {
            try
                let document = toReplaySourceDocument source

                let! _ =
                    ApplicationContext.cosmosContainer.UpsertItemAsync<ReplaySourceDocument>(
                        document,
                        PartitionKey(document.PartitionKey),
                        replayRequestOptions source.CorrelationId
                    )

                return Ok()
            with
            | ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed remembering replay source for canonical external event {EventName} ({EventId}).",
                    getCurrentInstantExtended (),
                    getMachineName,
                    source.CorrelationId,
                    source.EventName,
                    source.EventId
                )

                return Error ex.Message
        }

    let internal tryGetReplaySourceAsync eventId =
        task {
            try
                let! response =
                    ApplicationContext.cosmosContainer.ReadItemAsync<ReplaySourceDocument>(
                        eventId,
                        PartitionKey(ReplaySourcePartitionKey),
                        replayRequestOptions eventId
                    )

                return tryFromReplaySourceDocument response.Resource
            with
            | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.NotFound -> return None
            | ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Node: {HostName}; Failed reading replay source for eventId {EventId}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    eventId
                )

                return None
        }

    let internal deleteReplaySourceForTestsAsync eventId =
        task {
            try
                let! _ =
                    ApplicationContext.cosmosContainer.DeleteItemAsync<ReplaySourceDocument>(
                        eventId,
                        PartitionKey(ReplaySourcePartitionKey),
                        replayRequestOptions eventId
                    )

                return Ok()
            with
            | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.NotFound -> return Ok()
            | ex -> return Error ex.Message
        }

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

    let private normalizeWorkItemLocatorKind kind =
        match (kind
               |> Option.ofObj
               |> Option.defaultValue String.Empty)
            .Trim()
            .ToLowerInvariant()
            with
        | "id" -> "id"
        | "number" -> "number"
        | _ -> "opaque"

    let internal lifecycleStateToString state =
        match state with
        | AgentSessionLifecycleState.Inactive -> "inactive"
        | AgentSessionLifecycleState.Active -> "active"
        | AgentSessionLifecycleState.Stopping -> "stopping"
        | AgentSessionLifecycleState.Stopped -> "stopped"

    let private addRuntimeContextPayload (context: AgentRuntimeSourceContext) (payloadNode: JsonObject) =
        payloadNode["agentId"] <- JsonValue.Create(context.AgentId)
        payloadNode["sessionId"] <- JsonValue.Create(context.SessionId)
        payloadNode["repositoryId"] <- JsonValue.Create(context.RepositoryId)
        payloadNode["ownerId"] <- JsonValue.Create(context.OwnerId)
        payloadNode["organizationId"] <- JsonValue.Create(context.OrganizationId)

        context.BranchId
        |> Option.iter (fun value -> payloadNode["branchId"] <- JsonValue.Create(value))

        context.WorkItemId
        |> Option.iter (fun value -> payloadNode["workItemId"] <- JsonValue.Create(value))

        context.WorkItemLocator
        |> Option.iter (fun locator ->
            let node = JsonObject()
            node["raw"] <- JsonValue.Create(locator.Raw)
            node["kind"] <- JsonValue.Create(normalizeWorkItemLocatorKind locator.Kind)
            payloadNode["workItemLocator"] <- node)

        context.PromotionSetId
        |> Option.iter (fun value -> payloadNode["promotionSetId"] <- JsonValue.Create(value))

        payloadNode["lifecycleState"] <- JsonValue.Create(lifecycleStateToString context.LifecycleState)

    let buildRuntimeEnvelope (runtimeSource: AgentRuntimeLifecycleSource) =
        let payloadNode = JsonObject()
        let context = AgentRuntimeLifecycleSource.context runtimeSource
        addRuntimeContextPayload context payloadNode

        let eventName, occurredAt =
            match runtimeSource with
            | WorkStarted started ->
                payloadNode["startedAt"] <- JsonValue.Create(started.Context.OccurredAt)

                started.Message
                |> Option.iter (fun value -> payloadNode["message"] <- JsonValue.Create(value))

                started.OperationId
                |> Option.iter (fun value -> payloadNode["operationId"] <- JsonValue.Create(value))

                payloadNode["wasIdempotentReplay"] <- JsonValue.Create(started.WasIdempotentReplay)

                started.Source
                |> Option.iter (fun value -> payloadNode["source"] <- JsonValue.Create(value))

                CanonicalEventName.AgentWorkStarted, started.Context.OccurredAt
            | WorkStopped stopped ->
                payloadNode["stoppedAt"] <- JsonValue.Create(stopped.Context.OccurredAt)

                let resultNode = JsonObject()
                resultNode["message"] <- JsonValue.Create(stopped.Result.Message)
                resultNode["operationId"] <- JsonValue.Create(stopped.Result.OperationId)
                resultNode["wasIdempotentReplay"] <- JsonValue.Create(stopped.Result.WasIdempotentReplay)
                payloadNode["result"] <- resultNode

                CanonicalEventName.AgentWorkStopped, stopped.Context.OccurredAt

        match
            CanonicalExternalEventEnvelope.tryCreate
                eventName
                occurredAt
                context.CorrelationId
                context.OwnerId
                context.OrganizationId
                context.RepositoryId
                { ActorType = "AgentSession"; ActorId = context.SessionId }
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
                    RawEventCase = AgentRuntimeLifecycleSource.eventName runtimeSource
                    CorrelationId = context.CorrelationId
                    Reason =
                        Grace.Server.ExternalEvents.OversizeDocument(
                            (CanonicalExternalEventEnvelope.measureSize envelope)
                                .ByteCount
                        )
                }
        | Error error ->
            Grace.Server.ExternalEvents.Failed
                {
                    RawEventCase = AgentRuntimeLifecycleSource.eventName runtimeSource
                    CorrelationId = context.CorrelationId
                    Reason = Grace.Server.ExternalEvents.MissingActorIdentity error
                }

    let private createValidationsSummary (validations: Validation list) =
        {
            ValidationCount = validations.Length
            Validations =
                validations
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

    let private createValidationRequestedReplaySource
        (validationSet: ValidationSetDto)
        (sourceEnvelope: CanonicalExternalEventEnvelope)
        (context: ValidationTriggerContext)
        (matchedRules: MatchedRule list)
        =
        {
            OccurredAt = getCurrentInstant ()
            CorrelationId = sourceEnvelope.CorrelationId
            OwnerId = validationSet.OwnerId
            OrganizationId = validationSet.OrganizationId
            RepositoryId = validationSet.RepositoryId
            ValidationSetId = validationSet.ValidationSetId
            SourceEventId = sourceEnvelope.EventId
            SourceEventName = sourceEnvelope.EventName
            TargetBranchId = context.TargetBranchId
            TargetBranchName = context.TargetBranchName
            PromotionSetId = context.PromotionSetId
            StepsComputationAttempt = context.StepsComputationAttempt
            MatchedRules = matchedRules
            ValidationsSummary = createValidationsSummary validationSet.Validations
        }

    let buildValidationRequestedEnvelope (validationSource: ValidationRequestedReplaySource) =
        let payload =
            {|
                validationSetId = validationSource.ValidationSetId
                promotionSetId = validationSource.PromotionSetId
                stepsComputationAttempt = validationSource.StepsComputationAttempt
                targetBranchId = validationSource.TargetBranchId
                targetBranchName = validationSource.TargetBranchName
                sourceEventName = validationSource.SourceEventName
                matchedRules = validationSource.MatchedRules
                validationsSummary = validationSource.ValidationsSummary
            |}

        match
            CanonicalExternalEventEnvelope.tryCreate
                CanonicalEventName.ValidationRequested
                validationSource.OccurredAt
                validationSource.CorrelationId
                validationSource.OwnerId
                validationSource.OrganizationId
                validationSource.RepositoryId
                { ActorType = "ValidationSet"; ActorId = $"{validationSource.ValidationSetId}" }
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
                    CorrelationId = validationSource.CorrelationId
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
                    CorrelationId = validationSource.CorrelationId
                    Reason = Grace.Server.ExternalEvents.MissingActorIdentity error
                }

    let private createGraceEventReplaySource (envelope: CanonicalExternalEventEnvelope) (graceEvent: GraceEvent) =
        {
            EventId = envelope.EventId
            EventName = envelope.EventName
            EventVersion = envelope.EventVersion
            SourceKind = ReplaySourceKind.RawGraceEvent
            ActorType =
                envelope.ActorType
                |> Option.defaultValue String.Empty
            ActorId =
                envelope.ActorId
                |> Option.defaultValue String.Empty
            CorrelationId = envelope.CorrelationId
            RecordedAt = getCurrentInstant ()
            RawGraceEvent = Some graceEvent
            RuntimeSource = None
            ValidationRequestedSource = None
        }

    let private createRuntimeReplaySource (envelope: CanonicalExternalEventEnvelope) (runtimeSource: AgentRuntimeLifecycleSource) =
        {
            EventId = envelope.EventId
            EventName = envelope.EventName
            EventVersion = envelope.EventVersion
            SourceKind = ReplaySourceKind.AgentRuntimeLifecycle
            ActorType =
                envelope.ActorType
                |> Option.defaultValue String.Empty
            ActorId =
                envelope.ActorId
                |> Option.defaultValue String.Empty
            CorrelationId = envelope.CorrelationId
            RecordedAt = getCurrentInstant ()
            RawGraceEvent = None
            RuntimeSource = Some runtimeSource
            ValidationRequestedSource = None
        }

    let private createValidationRequestedRecord (envelope: CanonicalExternalEventEnvelope) (validationSource: ValidationRequestedReplaySource) =
        {
            EventId = envelope.EventId
            EventName = envelope.EventName
            EventVersion = envelope.EventVersion
            SourceKind = ReplaySourceKind.ValidationRequested
            ActorType =
                envelope.ActorType
                |> Option.defaultValue String.Empty
            ActorId =
                envelope.ActorId
                |> Option.defaultValue String.Empty
            CorrelationId = envelope.CorrelationId
            RecordedAt = getCurrentInstant ()
            RawGraceEvent = None
            RuntimeSource = None
            ValidationRequestedSource = Some validationSource
        }

    let internal matchesBranchGlob (branchName: BranchName) (branchNameGlob: string) =
        let normalizedPattern = if String.IsNullOrWhiteSpace branchNameGlob then "*" else branchNameGlob.Trim()

        let regexPattern =
            "^"
            + Regex
                .Escape(normalizedPattern)
                .Replace("\\*", ".*")
            + "$"

        Regex.IsMatch(
            $"{branchName}",
            regexPattern,
            RegexOptions.IgnoreCase
            ||| RegexOptions.CultureInvariant
        )

    let internal getMatchingValidationSets sourceEventName branchName (validationSets: ValidationSetDto list) =
        validationSets
        |> List.choose (fun validationSet ->
            let matchedRules =
                validationSet.Rules
                |> List.indexed
                |> List.choose (fun (ruleIndex, rule) ->
                    if rule.EventNames |> List.contains sourceEventName
                       && matchesBranchGlob branchName rule.BranchNameGlob then
                        Some { RuleIndex = ruleIndex; EventNames = rule.EventNames; BranchNameGlob = rule.BranchNameGlob }
                    else
                        None)

            if matchedRules.IsEmpty then None else Some(validationSet, matchedRules))

    let private parseGuid (value: string) =
        let mutable parsed = Guid.Empty

        if String.IsNullOrWhiteSpace value |> not
           && Guid.TryParse(value, &parsed)
           && parsed <> Guid.Empty then
            Some parsed
        else
            None

    let private tryGetRepositoryIdFromMetadata (metadata: EventMetadata) =
        match metadata.Properties.TryGetValue(nameof RepositoryId) with
        | true, value -> parseGuid value
        | _ -> None

    let private tryGetActorIdFromMetadata (metadata: EventMetadata) =
        match metadata.Properties.TryGetValue("ActorId") with
        | true, actorId -> parseGuid actorId
        | _ -> None

    let private tryGetPromotionSetIdFromMetadata (metadata: EventMetadata) = tryGetActorIdFromMetadata metadata

    let private tryGetTerminalPromotionSetId (links: ReferenceLinkType seq) =
        links
        |> Seq.tryPick (fun link ->
            match link with
            | ReferenceLinkType.PromotionSetTerminal promotionSetId -> Some promotionSetId
            | _ -> None)

    let private getBranchDto branchId repositoryId correlationId =
        task {
            let branchActorProxy = Branch.CreateActorProxy branchId repositoryId correlationId
            return! branchActorProxy.Get correlationId
        }

    let private getPromotionSetContext (repositoryId: RepositoryId) (promotionSetId: PromotionSetId) (correlationId: CorrelationId) =
        task {
            let promotionSetActorProxy = PromotionSet.CreateActorProxy promotionSetId repositoryId correlationId
            let! exists = promotionSetActorProxy.Exists correlationId

            if exists then
                let! promotionSet = promotionSetActorProxy.Get correlationId
                let! branch = getBranchDto promotionSet.TargetBranchId repositoryId correlationId
                return Some(promotionSet, branch)
            else
                return None
        }

    let internal absorbPromotionSetEvent (promotionSetEvent: Grace.Types.PromotionSet.PromotionSetEvent) =
        task {
            let correlationId = promotionSetEvent.Metadata.CorrelationId

            match promotionSetEvent.Event with
            | Grace.Types.PromotionSet.PromotionSetEventType.Created (promotionSetId, _, _, repositoryId, targetBranchId) ->
                let! branch = getBranchDto targetBranchId repositoryId correlationId

                rememberPromotionSetContextInCache
                    {
                        RepositoryId = repositoryId
                        TargetBranchId = targetBranchId
                        TargetBranchName = branch.BranchName
                        PromotionSetId = Some promotionSetId
                        StepsComputationAttempt = None
                    }
            | Grace.Types.PromotionSet.PromotionSetEventType.LogicalDeleted _ ->
                match tryGetRepositoryIdFromMetadata promotionSetEvent.Metadata, tryGetPromotionSetIdFromMetadata promotionSetEvent.Metadata with
                | Some repositoryId, Some promotionSetId -> forgetPromotionSetContextFromCache repositoryId promotionSetId
                | _ -> ()
            | _ ->
                match tryGetRepositoryIdFromMetadata promotionSetEvent.Metadata, tryGetPromotionSetIdFromMetadata promotionSetEvent.Metadata with
                | Some repositoryId, Some promotionSetId ->
                    let! promotionSetContext = getPromotionSetContext repositoryId promotionSetId correlationId

                    match promotionSetContext with
                    | Some (promotionSet, branch) ->
                        rememberPromotionSetContextInCache
                            {
                                RepositoryId = repositoryId
                                TargetBranchId = branch.BranchId
                                TargetBranchName = branch.BranchName
                                PromotionSetId = Some promotionSet.PromotionSetId
                                StepsComputationAttempt = Some promotionSet.StepsComputationAttempt
                            }
                    | None -> ()
                | _ -> ()
        }

    let private tryResolveValidationContextForGraceEvent (graceEvent: GraceEvent) (correlationId: CorrelationId) =
        task {
            match graceEvent with
            | QueueEvent queueEvent ->
                match queueEvent.Event with
                | PromotionQueueEventType.PromotionSetEnqueued promotionSetId
                | PromotionQueueEventType.PromotionSetDequeued promotionSetId ->
                    match tryGetRepositoryIdFromMetadata queueEvent.Metadata with
                    | Some repositoryId ->
                        let! promotionSetContext = getPromotionSetContext repositoryId promotionSetId correlationId

                        match promotionSetContext with
                        | Some (promotionSet, branch) ->
                            return
                                Some
                                    {
                                        RepositoryId = repositoryId
                                        TargetBranchId = branch.BranchId
                                        TargetBranchName = branch.BranchName
                                        PromotionSetId = Some promotionSet.PromotionSetId
                                        StepsComputationAttempt = Some promotionSet.StepsComputationAttempt
                                    }
                        | None -> return None
                    | None -> return None
                | _ -> return None
            | PromotionSetEvent promotionSetEvent ->
                match tryGetPromotionSetIdFromMetadata promotionSetEvent.Metadata, tryGetRepositoryIdFromMetadata promotionSetEvent.Metadata with
                | Some promotionSetId, Some repositoryId ->
                    let! promotionSetContext = getPromotionSetContext repositoryId promotionSetId correlationId

                    match promotionSetContext with
                    | Some (promotionSet, branch) ->
                        return
                            Some
                                {
                                    RepositoryId = repositoryId
                                    TargetBranchId = branch.BranchId
                                    TargetBranchName = branch.BranchName
                                    PromotionSetId = Some promotionSet.PromotionSetId
                                    StepsComputationAttempt = Some promotionSet.StepsComputationAttempt
                                }
                    | None -> return None
                | _ -> return None
            | ReferenceEvent referenceEvent ->
                match referenceEvent.Event with
                | ReferenceEventType.Created (_, _, _, repositoryId, branchId, _, _, referenceType, _, links) when referenceType = ReferenceType.Promotion ->
                    match tryGetTerminalPromotionSetId links with
                    | Some promotionSetId ->
                        let! branchDto = getBranchDto branchId repositoryId correlationId
                        let! promotionSetContext = getPromotionSetContext repositoryId promotionSetId correlationId

                        let stepsComputationAttempt =
                            promotionSetContext
                            |> Option.map (fun (promotionSet, _) -> promotionSet.StepsComputationAttempt)

                        return
                            Some
                                {
                                    RepositoryId = repositoryId
                                    TargetBranchId = branchId
                                    TargetBranchName = branchDto.BranchName
                                    PromotionSetId = Some promotionSetId
                                    StepsComputationAttempt = stepsComputationAttempt
                                }
                    | None -> return None
                | _ -> return None
            | _ -> return None
        }

    let private tryResolveValidationContextForRuntime (runtimeSource: AgentRuntimeLifecycleSource) =
        task {
            let context = AgentRuntimeLifecycleSource.context runtimeSource

            if context.RepositoryId = RepositoryId.Empty then
                return None
            else
                match context.PromotionSetId with
                | Some promotionSetId ->
                    match tryGetPromotionSetContextFromCache context.RepositoryId promotionSetId with
                    | Some cachedContext -> return Some cachedContext
                    | None ->
                        let! promotionSetContext = getPromotionSetContext context.RepositoryId promotionSetId context.CorrelationId

                        match promotionSetContext with
                        | Some (promotionSet, branch) ->
                            let resolvedContext =
                                {
                                    RepositoryId = context.RepositoryId
                                    TargetBranchId = branch.BranchId
                                    TargetBranchName = branch.BranchName
                                    PromotionSetId = Some promotionSet.PromotionSetId
                                    StepsComputationAttempt = Some promotionSet.StepsComputationAttempt
                                }

                            rememberPromotionSetContextInCache resolvedContext
                            return Some resolvedContext
                        | None ->
                            match context.BranchId with
                            | Some branchId ->
                                let! branchDto = getBranchDto branchId context.RepositoryId context.CorrelationId

                                return
                                    Some
                                        {
                                            RepositoryId = context.RepositoryId
                                            TargetBranchId = branchId
                                            TargetBranchName = branchDto.BranchName
                                            PromotionSetId = Some promotionSetId
                                            StepsComputationAttempt = None
                                        }
                            | None -> return None
                | None ->
                    match context.BranchId with
                    | Some branchId ->
                        let! branchDto = getBranchDto branchId context.RepositoryId context.CorrelationId

                        return
                            Some
                                {
                                    RepositoryId = context.RepositoryId
                                    TargetBranchId = branchId
                                    TargetBranchName = branchDto.BranchName
                                    PromotionSetId = None
                                    StepsComputationAttempt = None
                                }
                    | None -> return None
        }

    let private emitExternalEventSafely (emitExternalEvent: CanonicalExternalEventEnvelope -> Task<unit>) (envelope: CanonicalExternalEventEnvelope) =
        task {
            try
                do! emitExternalEvent envelope
            with
            | ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed routing external event {EventName} ({EventId}).",
                    getCurrentInstantExtended (),
                    getMachineName,
                    envelope.CorrelationId,
                    envelope.EventName,
                    envelope.EventId
                )
        }

    let private recordSynchronousValidationAsync
        (validationSet: ValidationSetDto)
        (validation: Validation)
        (context: ValidationTriggerContext)
        (sourceEnvelope: CanonicalExternalEventEnvelope)
        =
        task {
            let correlationId = sourceEnvelope.CorrelationId
            let validationResultId = Guid.NewGuid()
            let validationResultActorProxy = ValidationResult.CreateActorProxy validationResultId context.RepositoryId correlationId
            let metadata = EventMetadata.New correlationId GraceSystemUser
            metadata.Properties[ nameof RepositoryId ] <- $"{context.RepositoryId}"
            metadata.Properties[ "ActorId" ] <- $"{validationResultId}"

            let validationResultDto =
                { ValidationResultDto.Default with
                    ValidationResultId = validationResultId
                    OwnerId = validationSet.OwnerId
                    OrganizationId = validationSet.OrganizationId
                    RepositoryId = context.RepositoryId
                    ValidationSetId = Some validationSet.ValidationSetId
                    PromotionSetId = context.PromotionSetId
                    StepsComputationAttempt = context.StepsComputationAttempt
                    ValidationName = validation.Name
                    ValidationVersion = validation.Version
                    Output =
                        {
                            Status = ValidationStatus.Pass
                            Summary = $"Synchronous validation '{validation.Name}' recorded automatically from {sourceEnvelope.EventName}."
                            ArtifactIds = []
                        }
                    OnBehalfOf = [ UserId GraceSystemUser ]
                    CreatedAt = getCurrentInstant ()
                }

            match! validationResultActorProxy.Handle (ValidationResultCommand.Record validationResultDto) metadata with
            | Ok _ -> ()
            | Error graceError ->
                log.LogWarning(
                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed recording synchronous validation result for ValidationSetId {ValidationSetId}. Error: {GraceError}",
                    getCurrentInstantExtended (),
                    getMachineName,
                    correlationId,
                    validationSet.ValidationSetId,
                    graceError
                )
        }

    let private executeValidationSetValidations
        (validationSet: ValidationSetDto)
        (context: ValidationTriggerContext)
        (sourceEnvelope: CanonicalExternalEventEnvelope)
        =
        task {
            let mutable validationIndex = 0

            while validationIndex < validationSet.Validations.Length do
                let validation = validationSet.Validations[validationIndex]

                if validation.ExecutionMode = ValidationExecutionMode.Synchronous then
                    do! recordSynchronousValidationAsync validationSet validation context sourceEnvelope

                validationIndex <- validationIndex + 1
        }

    let private emitValidationRequestedEvents
        (emitExternalEvent: CanonicalExternalEventEnvelope -> Task<unit>)
        (sourceEnvelope: CanonicalExternalEventEnvelope)
        (context: ValidationTriggerContext)
        =
        task {
            let correlationId = sourceEnvelope.CorrelationId
            let! validationSets = getValidationSetsForTriggerAsync context.RepositoryId correlationId
            let matchingValidationSets = getMatchingValidationSets sourceEnvelope.EventName context.TargetBranchName validationSets
            let mutable index = 0

            while index < matchingValidationSets.Length do
                let validationSet, matchedRules = matchingValidationSets[index]
                let validationSource = createValidationRequestedReplaySource validationSet sourceEnvelope context matchedRules

                match buildValidationRequestedEnvelope validationSource with
                | Grace.Server.ExternalEvents.Published envelope ->
                    let replaySource = createValidationRequestedRecord envelope validationSource

                    match! rememberReplaySourceAsync replaySource with
                    | Ok () ->
                        match! publishCanonicalEvent envelope with
                        | Ok () -> do! emitExternalEventSafely emitExternalEvent envelope
                        | Error _ -> ()
                    | Error _ -> ()
                | Grace.Server.ExternalEvents.InternalOnly _ -> ()
                | Grace.Server.ExternalEvents.Failed failure ->
                    log.LogWarning(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed building validation-requested event for ValidationSetId {ValidationSetId}: {Reason}",
                        getCurrentInstantExtended (),
                        getMachineName,
                        failure.CorrelationId,
                        validationSet.ValidationSetId,
                        failure.Reason
                    )

                do! executeValidationSetValidations validationSet context sourceEnvelope
                index <- index + 1
        }

    let private postSourceEvent
        (emitExternalEvent: CanonicalExternalEventEnvelope -> Task<unit>)
        (envelope: CanonicalExternalEventEnvelope)
        (replaySource: DurableReplaySourceRecord)
        (validationContext: ValidationTriggerContext option)
        =
        task {
            match! rememberReplaySourceAsync replaySource with
            | Error _ -> ()
            | Ok () ->
                match! publishCanonicalEvent envelope with
                | Ok () ->
                    do! emitExternalEventSafely emitExternalEvent envelope

                    match validationContext with
                    | Some current when
                        not (String.Equals(envelope.EventName, CanonicalEventName.toString CanonicalEventName.ValidationRequested, StringComparison.Ordinal))
                        ->
                        do! emitValidationRequestedEvents emitExternalEvent envelope current
                    | _ -> ()
                | Error _ -> ()
        }

    let publishGraceEvent (emitExternalEvent: CanonicalExternalEventEnvelope -> Task<unit>) (graceEvent: GraceEvent) =
        task {
            match Grace.Server.ExternalEvents.buildGraceEvent graceEvent with
            | Grace.Server.ExternalEvents.Published envelope ->
                let replaySource = createGraceEventReplaySource envelope graceEvent
                let! validationContext = tryResolveValidationContextForGraceEvent graceEvent envelope.CorrelationId
                do! postSourceEvent emitExternalEvent envelope replaySource validationContext
            | Grace.Server.ExternalEvents.InternalOnly _ -> ()
            | Grace.Server.ExternalEvents.Failed failure ->
                log.LogWarning(
                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed building canonical external event {RawEventCase}: {Reason}",
                    getCurrentInstantExtended (),
                    getMachineName,
                    failure.CorrelationId,
                    failure.RawEventCase,
                    failure.Reason
                )
        }

    let publishRuntimeSource (emitExternalEvent: CanonicalExternalEventEnvelope -> Task<unit>) (runtimeSource: AgentRuntimeLifecycleSource) =
        task {
            match buildRuntimeEnvelope runtimeSource with
            | Grace.Server.ExternalEvents.Published envelope ->
                let replaySource = createRuntimeReplaySource envelope runtimeSource
                let! validationContext = tryResolveValidationContextForRuntime runtimeSource
                do! postSourceEvent emitExternalEvent envelope replaySource validationContext
            | Grace.Server.ExternalEvents.InternalOnly _ -> ()
            | Grace.Server.ExternalEvents.Failed failure ->
                log.LogWarning(
                    "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed building runtime canonical event {RawEventCase}: {Reason}",
                    getCurrentInstantExtended (),
                    getMachineName,
                    failure.CorrelationId,
                    failure.RawEventCase,
                    failure.Reason
                )
        }

    let private rebuildFromReplaySource eventId (source: DurableReplaySourceRecord) =
        match source.SourceKind, source.RawGraceEvent, source.RuntimeSource, source.ValidationRequestedSource with
        | ReplaySourceKind.RawGraceEvent, Some graceEvent, _, _ -> Grace.Server.ExternalEvents.buildGraceEvent graceEvent
        | ReplaySourceKind.AgentRuntimeLifecycle, _, Some runtimeSource, _ -> buildRuntimeEnvelope runtimeSource
        | ReplaySourceKind.ValidationRequested, _, _, Some validationSource -> buildValidationRequestedEnvelope validationSource
        | _ ->
            Grace.Server.ExternalEvents.Failed
                {
                    RawEventCase = RawEventCase.derived source.EventName
                    CorrelationId = source.CorrelationId
                    Reason =
                        Grace.Server.ExternalEvents.InvalidPayload(
                            $"Replay source for eventId '{eventId}' is incomplete or does not match source kind '{ReplaySourceKind.toString source.SourceKind}'."
                        )
                }

    let rebuildForReplayAsync eventId =
        task {
            let! replaySource = tryGetReplaySourceAsync eventId

            return
                replaySource
                |> Option.map (rebuildFromReplaySource eventId)
        }
