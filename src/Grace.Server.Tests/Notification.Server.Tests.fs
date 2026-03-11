namespace Grace.Server.Tests

open Grace.Server
open Grace.Server.Notification
open Grace.Server.Tests.Services
open Grace.Actors
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Branch
open Grace.Types.ExternalEvents
open Grace.Types.Types
open Grace.Types.Validation
open NUnit.Framework
open System
open System.Net
open System.Threading.Tasks

[<Parallelizable(ParallelScope.All)>]
type NotificationServerTests() =

    [<TestCase("main", "main", true)>]
    [<TestCase("MAIN", "main", true)>]
    [<TestCase("release/2026.02", "release/*", true)>]
    [<TestCase("feature/promo-set", "feature/*", true)>]
    [<TestCase("main", "*", true)>]
    [<TestCase("release", "main", false)>]
    [<TestCase("feature/promo", "release/*", false)>]
    member _.BranchNameGlobMatchingIsCaseInsensitiveAndSupportsWildcard(branchName: string, glob: string, expected: bool) =
        let actual = Subscriber.matchesBranchGlob branchName glob
        Assert.That(actual, Is.EqualTo(expected))

[<NonParallelizable>]
type ExternalEventPublicationIntegrationTests() =

    let currentState () =
        {
            App = App |> Option.get
            Client = Client
            GraceServerBaseAddress = graceServerBaseAddress
            ServiceBusConnectionString = serviceBusConnectionString
            ServiceBusTopic = serviceBusTopic
            ServiceBusServerSubscription = serviceBusServerSubscription
            ServiceBusTestSubscription = serviceBusTestSubscription
        }

    let createRepositoryAsync () =
        task {
            let repositoryId = $"{Guid.NewGuid()}"
            let parameters = Parameters.Repository.CreateRepositoryParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.RepositoryName <- $"ExternalEvents{Guid.NewGuid():N}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/repository/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            repositoryIds <- Array.append repositoryIds [| repositoryId |]
            return repositoryId
        }

    let getPrimaryBranchAsync (repositoryId: string) =
        task {
            let parameters = Parameters.Repository.GetBranchesParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/repository/getBranches", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore

            let! returnValue = deserializeContent<Types.GraceReturnValue<BranchDto array>> response

            return
                returnValue.ReturnValue
                |> Array.tryHead
                |> Option.defaultWith (fun () -> failwith $"Repository {repositoryId} did not return any branches.")
        }

    let createValidationSetAsync (repositoryId: string) (branch: BranchDto) (rules: ValidationSetRule list) (validations: Validation list) =
        task {
            let validationSetId = $"{Guid.NewGuid()}"
            let parameters = Parameters.Validation.CreateValidationSetParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.ValidationSetId <- validationSetId
            parameters.TargetBranchId <- $"{branch.BranchId}"
            parameters.Rules <- rules
            parameters.Validations <- validations
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/validation-set/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            return validationSetId
        }

    let createPromotionSetAsync (repositoryId: string) (branch: BranchDto) =
        task {
            let promotionSetId = $"{Guid.NewGuid()}"
            let parameters = Parameters.PromotionSet.CreatePromotionSetParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.PromotionSetId <- promotionSetId
            parameters.TargetBranchId <- $"{branch.BranchId}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/promotion-set/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            return promotionSetId
        }

    let startAgentSessionAsync (repositoryId: string) (promotionSetId: string) (agentId: string) (workItemIdOrNumber: string) (correlationId: string) =
        task {
            let operationId = $"start-{Guid.NewGuid():N}"
            let parameters = Parameters.Common.StartAgentSessionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.AgentId <- agentId
            parameters.AgentDisplayName <- "Codex Test Agent"
            parameters.WorkItemIdOrNumber <- workItemIdOrNumber
            parameters.PromotionSetId <- promotionSetId
            parameters.Source <- "codex-test"
            parameters.OperationId <- operationId
            parameters.CorrelationId <- correlationId

            let! response = Client.PostAsync("/agent/session/start", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore

            let! returnValue = deserializeContent<Types.GraceReturnValue<AgentSessionOperationResult>> response
            return returnValue.ReturnValue
        }

    let stopAgentSessionAsync (repositoryId: string) (agentId: string) (sessionId: string) (workItemIdOrNumber: string) (correlationId: string) =
        task {
            let operationId = $"stop-{Guid.NewGuid():N}"
            let parameters = Parameters.Common.StopAgentSessionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.AgentId <- agentId
            parameters.SessionId <- sessionId
            parameters.WorkItemIdOrNumber <- workItemIdOrNumber
            parameters.StopReason <- "integration-test"
            parameters.OperationId <- operationId
            parameters.CorrelationId <- correlationId

            let! response = Client.PostAsync("/agent/session/stop", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore

            let! returnValue = deserializeContent<Types.GraceReturnValue<AgentSessionOperationResult>> response
            return returnValue.ReturnValue
        }

    let createValidation name mode = { Name = name; Version = "1.0.0"; ExecutionMode = mode; RequiredForApply = true }

    let canReadValidationSetAsync (repositoryId: string) (validationSetId: string) =
        task {
            let parameters = Parameters.Validation.GetValidationSetParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.ValidationSetId <- validationSetId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/validation-set/get", createJsonContent parameters)
            return response.IsSuccessStatusCode
        }

    let canReadPromotionSetAsync (repositoryId: string) (promotionSetId: string) =
        task {
            let parameters = Parameters.PromotionSet.GetPromotionSetParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.PromotionSetId <- promotionSetId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/promotion-set/get", createJsonContent parameters)
            return response.IsSuccessStatusCode
        }

    let waitForPromotionSetVisibleAsync (repositoryId: string) (promotionSetId: string) =
        task {
            let deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30.0)
            let mutable isVisible = false

            while not isVisible && DateTime.UtcNow < deadline do
                let! canReadPromotionSet = canReadPromotionSetAsync repositoryId promotionSetId
                isVisible <- canReadPromotionSet

                if not isVisible then do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            if not isVisible then
                Assert.Fail($"Timed out waiting for promotion set {promotionSetId} to become HTTP-readable.")

            do! Task.Delay(TimeSpan.FromSeconds(2.0))
        }

    let waitForValidationSetsVisibleAsync (repositoryId: string) (expectedValidationSetIds: string list) =
        task {
            let expectedIds = expectedValidationSetIds |> Set.ofList
            let deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30.0)
            let mutable visibleIds = Set.empty

            while visibleIds <> expectedIds
                  && DateTime.UtcNow < deadline do
                let! lookups =
                    expectedValidationSetIds
                    |> List.map (fun validationSetId ->
                        task {
                            let! canReadValidationSet = canReadValidationSetAsync repositoryId validationSetId
                            return validationSetId, canReadValidationSet
                        })
                    |> Task.WhenAll

                visibleIds <-
                    lookups
                    |> Array.choose (fun (validationSetId, canReadValidationSet) -> if canReadValidationSet then Some validationSetId else None)
                    |> Array.filter (String.IsNullOrWhiteSpace >> not)
                    |> Set.ofArray
                    |> Set.intersect expectedIds

                if visibleIds <> expectedIds then
                    do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            if visibleIds <> expectedIds then
                let missingIds = expectedIds - visibleIds
                let expectedText = String.Join(", ", expectedIds)
                let visibleText = String.Join(", ", visibleIds)
                let missingText = String.Join(", ", missingIds)

                Assert.Fail(
                    $"Timed out waiting for validation sets to become HTTP-readable. Expected={expectedText}; Visible={visibleText}; Missing={missingText}"
                )

            do! Task.Delay(TimeSpan.FromSeconds(5.0))
        }

    let collectByCorrelationAsync (correlationId: string) (timeoutSeconds: float) =
        task {
            let! matches, _ =
                AspireTestHost.collectCanonicalExternalEventsAsync
                    (currentState ())
                    (fun envelope -> envelope.CorrelationId = correlationId)
                    (TimeSpan.FromSeconds(timeoutSeconds))
                    (TimeSpan.FromSeconds(1.5))

            return matches
        }

    let countByEventName eventName (envelopes: CanonicalExternalEventEnvelope list) =
        envelopes
        |> List.filter (fun envelope -> envelope.EventName = eventName)
        |> List.length

    [<Test>]
    member _.ResendRebuildsCanonicalEnvelopeFromDurableSourceWithSameEventId() =
        task {
            let state = currentState ()
            let correlationId = generateCorrelationId ()
            let newName = $"OwnerResend{Guid.NewGuid():N}"

            let! _ = AspireTestHost.drainServiceBusAsync state

            let parameters = Parameters.Owner.SetOwnerNameParameters()
            parameters.OwnerId <- ownerId
            parameters.NewName <- newName
            parameters.CorrelationId <- correlationId

            let! response = Client.PostAsync("/owner/setName", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore

            let! originalEnvelope =
                AspireTestHost.waitForCanonicalExternalEventAsync
                    state
                    (fun envelope ->
                        envelope.EventName = CanonicalEventName.toString CanonicalEventName.OwnerUpdated
                        && envelope
                            .Payload
                            .GetProperty("changed")
                            .GetProperty("ownerName")
                            .GetString() = newName)
                    $"original canonical owner.updated event for {newName}"

            let eventId = originalEnvelope.EventId
            let resendParameters = Parameters.ExternalEvent.ResendExternalEventParameters()
            resendParameters.EventId <- eventId
            resendParameters.CorrelationId <- generateCorrelationId ()

            let! resendResponse = Client.PostAsync("/external-event/resend", createJsonContent resendParameters)
            resendResponse.EnsureSuccessStatusCode() |> ignore

            let! resentEnvelope =
                AspireTestHost.waitForCanonicalExternalEventAsync state (fun envelope -> envelope.EventId = eventId) $"resent canonical event {eventId}"

            Assert.That(resentEnvelope.EventId, Is.EqualTo(originalEnvelope.EventId))
            Assert.That(resentEnvelope.CorrelationId, Is.EqualTo(originalEnvelope.CorrelationId))
            Assert.That(resentEnvelope.EventName, Is.EqualTo(originalEnvelope.EventName))

            Assert.That(
                resentEnvelope
                    .Payload
                    .GetProperty("changed")
                    .GetProperty("ownerName")
                    .GetString(),
                Is.EqualTo(newName)
            )

            Assert.That(resentEnvelope.Payload.ToString(), Is.EqualTo(originalEnvelope.Payload.ToString()))
        }

    [<Test>]
    member _.ResendFailsClearlyWhenDurableReplaySourceIsMissing() =
        task {
            let resendParameters = Parameters.ExternalEvent.ResendExternalEventParameters()
            resendParameters.EventId <- $"Owner_{Guid.NewGuid()}_missing-replay"
            resendParameters.CorrelationId <- generateCorrelationId ()

            let! resendResponse = Client.PostAsync("/external-event/resend", createJsonContent resendParameters)
            Assert.That(resendResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))

            let! error = deserializeContent<Types.GraceError> resendResponse
            Assert.That(error.Error, Does.Contain("No replayable source was found"))
        }

    [<Test>]
    member _.RuntimeCanonicalEventsTriggerExactlyOneValidationRequestedForAsyncMatchingSet() =
        task {
            let state = currentState ()
            let! repositoryId = createRepositoryAsync ()
            let! branch = getPrimaryBranchAsync repositoryId
            let! promotionSetId = createPromotionSetAsync repositoryId branch
            let runtimeEventName = CanonicalEventName.toString CanonicalEventName.AgentWorkStarted
            let validationRequestedEventName = CanonicalEventName.toString CanonicalEventName.ValidationRequested

            let! validationSetId =
                createValidationSetAsync
                    repositoryId
                    branch
                    [
                        { EventNames = [ runtimeEventName ]; BranchNameGlob = $"{branch.BranchName}" }
                    ]
                    [
                        createValidation "async-a" ValidationExecutionMode.AsyncCallback
                        createValidation "async-b" ValidationExecutionMode.AsyncCallback
                    ]

            do! waitForPromotionSetVisibleAsync repositoryId promotionSetId
            do! waitForValidationSetsVisibleAsync repositoryId [ validationSetId ]

            let agentId = $"codex-agent-{Guid.NewGuid():N}"
            let! _ = AspireTestHost.drainServiceBusAsync state
            let! operationResult = startAgentSessionAsync repositoryId promotionSetId agentId "123" (generateCorrelationId ())

            Assert.That(operationResult.Session.SessionId, Is.Not.Empty)

            let! matches =
                AspireTestHost.waitForCanonicalExternalEventsAsync
                    state
                    (fun envelope ->
                        (envelope.EventName = runtimeEventName
                         && envelope
                             .Payload
                             .GetProperty("agentId")
                             .GetString() = agentId)
                        || (envelope.EventName = validationRequestedEventName
                            && envelope
                                .Payload
                                .GetProperty("validationSetId")
                                .GetGuid() = Guid.Parse(validationSetId)))
                    2
                    $"runtime start plus validation.requested for {agentId}"

            let sourceEnvelope =
                matches
                |> List.find (fun envelope -> envelope.EventName = runtimeEventName)

            Assert.That(countByEventName runtimeEventName matches, Is.EqualTo(1))
            Assert.That(countByEventName validationRequestedEventName matches, Is.EqualTo(1))

            Assert.That(
                sourceEnvelope
                    .Payload
                    .GetProperty("lifecycleState")
                    .GetString(),
                Is.EqualTo("active")
            )

            Assert.That(
                sourceEnvelope
                    .Payload
                    .GetProperty("startedAt")
                    .GetString(),
                Is.Not.Null
            )

            Assert.That(
                sourceEnvelope
                    .Payload
                    .GetProperty("message")
                    .GetString(),
                Is.EqualTo("Agent work session started.")
            )

            Assert.That(
                sourceEnvelope
                    .Payload
                    .GetProperty("operationId")
                    .GetString(),
                Is.Not.Empty
            )

            Assert.That(
                sourceEnvelope
                    .Payload
                    .GetProperty("wasIdempotentReplay")
                    .GetBoolean(),
                Is.False
            )

            Assert.That(
                sourceEnvelope
                    .Payload
                    .GetProperty("source")
                    .GetString(),
                Is.EqualTo("codex-test")
            )
        }

    [<Test>]
    member _.RuntimeCanonicalEventsTriggerExactlyOneValidationRequestedForSynchronousMatchingSet() =
        task {
            let state = currentState ()
            let! repositoryId = createRepositoryAsync ()
            let! branch = getPrimaryBranchAsync repositoryId
            let! promotionSetId = createPromotionSetAsync repositoryId branch
            let runtimeEventName = CanonicalEventName.toString CanonicalEventName.AgentWorkStarted
            let validationRequestedEventName = CanonicalEventName.toString CanonicalEventName.ValidationRequested

            let! validationSetId =
                createValidationSetAsync
                    repositoryId
                    branch
                    [
                        { EventNames = [ runtimeEventName ]; BranchNameGlob = $"{branch.BranchName}" }
                    ]
                    [
                        createValidation "sync-a" ValidationExecutionMode.Synchronous
                        createValidation "sync-b" ValidationExecutionMode.Synchronous
                    ]

            do! waitForPromotionSetVisibleAsync repositoryId promotionSetId
            do! waitForValidationSetsVisibleAsync repositoryId [ validationSetId ]

            let agentId = $"codex-agent-{Guid.NewGuid():N}"
            let! _ = AspireTestHost.drainServiceBusAsync state
            let! operationResult = startAgentSessionAsync repositoryId promotionSetId agentId "456" (generateCorrelationId ())

            Assert.That(operationResult.Session.SessionId, Is.Not.Empty)

            let! matches =
                AspireTestHost.waitForCanonicalExternalEventsAsync
                    state
                    (fun envelope ->
                        (envelope.EventName = runtimeEventName
                         && envelope
                             .Payload
                             .GetProperty("agentId")
                             .GetString() = agentId)
                        || (envelope.EventName = validationRequestedEventName
                            && envelope
                                .Payload
                                .GetProperty("validationSetId")
                                .GetGuid() = Guid.Parse(validationSetId)))
                    2
                    $"runtime start plus validation.requested for {agentId}"

            Assert.That(countByEventName runtimeEventName matches, Is.EqualTo(1))
            Assert.That(countByEventName validationRequestedEventName matches, Is.EqualTo(1))
        }

    [<Test>]
    member _.RuntimeCanonicalEventsEmitOneValidationRequestedPerMatchingSetAndDoNotRecurseDerivedRules() =
        task {
            let state = currentState ()
            let! repositoryId = createRepositoryAsync ()
            let! branch = getPrimaryBranchAsync repositoryId
            let! promotionSetId = createPromotionSetAsync repositoryId branch
            let runtimeEventName = CanonicalEventName.toString CanonicalEventName.AgentWorkStarted
            let validationRequestedEventName = CanonicalEventName.toString CanonicalEventName.ValidationRequested

            let! primaryValidationSetId =
                createValidationSetAsync
                    repositoryId
                    branch
                    [
                        { EventNames = [ runtimeEventName ]; BranchNameGlob = $"{branch.BranchName}" }
                    ]
                    [
                        createValidation "async-primary" ValidationExecutionMode.AsyncCallback
                    ]

            let! secondaryValidationSetId =
                createValidationSetAsync
                    repositoryId
                    branch
                    [
                        { EventNames = [ runtimeEventName ]; BranchNameGlob = $"{branch.BranchName}" }
                    ]
                    [
                        createValidation "async-secondary" ValidationExecutionMode.AsyncCallback
                    ]

            let! recursiveValidationSetId =
                createValidationSetAsync
                    repositoryId
                    branch
                    [
                        { EventNames = [ validationRequestedEventName ]; BranchNameGlob = $"{branch.BranchName}" }
                    ]
                    [
                        createValidation "should-not-recurse" ValidationExecutionMode.AsyncCallback
                    ]

            let expectedValidationSetIds =
                Set.ofList [ Guid.Parse(primaryValidationSetId)
                             Guid.Parse(secondaryValidationSetId) ]

            do!
                waitForValidationSetsVisibleAsync
                    repositoryId
                    [
                        primaryValidationSetId
                        secondaryValidationSetId
                        recursiveValidationSetId
                    ]

            let agentId = $"codex-agent-{Guid.NewGuid():N}"
            let! _ = AspireTestHost.drainServiceBusAsync state
            let! operationResult = startAgentSessionAsync repositoryId promotionSetId agentId "789" (generateCorrelationId ())

            Assert.That(operationResult.Session.SessionId, Is.Not.Empty)

            let! matches =
                AspireTestHost.waitForCanonicalExternalEventsAsync
                    state
                    (fun envelope ->
                        (envelope.EventName = runtimeEventName
                         && envelope
                             .Payload
                             .GetProperty("agentId")
                             .GetString() = agentId)
                        || (envelope.EventName = validationRequestedEventName
                            && expectedValidationSetIds.Contains(
                                envelope
                                    .Payload
                                    .GetProperty("validationSetId")
                                    .GetGuid()
                            )))
                    3
                    $"runtime start plus two validation.requested events for {agentId}"

            Assert.That(countByEventName runtimeEventName matches, Is.EqualTo(1))
            Assert.That(countByEventName validationRequestedEventName matches, Is.EqualTo(2))
        }

    [<Test>]
    member _.RuntimeStoppedEventUsesNestedResultPayloadWhenPublishedThroughHttpSessionStop() =
        task {
            let state = currentState ()
            let! repositoryId = createRepositoryAsync ()
            let! branch = getPrimaryBranchAsync repositoryId
            let! promotionSetId = createPromotionSetAsync repositoryId branch

            let agentId = $"codex-agent-{Guid.NewGuid():N}"
            let! _ = AspireTestHost.drainServiceBusAsync state
            let! startResult = startAgentSessionAsync repositoryId promotionSetId agentId "987" (generateCorrelationId ())
            let! stopResult = stopAgentSessionAsync repositoryId agentId startResult.Session.SessionId "987" (generateCorrelationId ())

            Assert.That(stopResult.Session.SessionId, Is.EqualTo(startResult.Session.SessionId))

            let! stoppedEnvelope =
                AspireTestHost.waitForCanonicalExternalEventAsync
                    state
                    (fun envelope ->
                        envelope.EventName = CanonicalEventName.toString CanonicalEventName.AgentWorkStopped
                        && envelope
                            .Payload
                            .GetProperty("sessionId")
                            .GetString() = startResult.Session.SessionId)
                    $"stopped runtime canonical event for {agentId}"

            let payload = stoppedEnvelope.Payload
            let mutable ignored = Unchecked.defaultof<System.Text.Json.JsonElement>

            Assert.That(payload.GetProperty("lifecycleState").GetString(), Is.EqualTo("stopped"))
            Assert.That(payload.GetProperty("stoppedAt").GetString(), Is.Not.Null)

            Assert.That(
                payload
                    .GetProperty("result")
                    .GetProperty("message")
                    .GetString(),
                Does.StartWith("Agent work session stopped")
            )

            Assert.That(
                payload
                    .GetProperty("result")
                    .GetProperty("operationId")
                    .GetString(),
                Is.Not.Empty
            )

            Assert.That(
                payload
                    .GetProperty("result")
                    .GetProperty("wasIdempotentReplay")
                    .GetBoolean(),
                Is.False
            )

            Assert.That(payload.TryGetProperty("message", &ignored), Is.False)
            Assert.That(payload.TryGetProperty("operationId", &ignored), Is.False)
            Assert.That(payload.TryGetProperty("wasIdempotentReplay", &ignored), Is.False)
        }
