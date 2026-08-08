namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Automation
open Grace.Types.Common
open Grace.Types.Reference
open Microsoft.AspNetCore.Http.Connections.Client
open Microsoft.AspNetCore.SignalR.Client
open Microsoft.Extensions.DependencyInjection
open NUnit.Framework
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Net.Http
open System.Threading
open System.Threading.Tasks

/// Groups shared helpers for notification hub test helpers.
module private NotificationHubTestHelpers =

    /// Builds a deterministic connection for integration setup fixture for the server integration notification Hub assertions.
    let createConnection includeAuthentication =
        let builder = HubConnectionBuilder()

        builder
            .WithUrl(
                $"{graceServerBaseAddress}/notifications",
                fun (options: HttpConnectionOptions) -> if includeAuthentication then options.Headers.Add("x-grace-user-id", testUserId)
            )
            .AddJsonProtocol(fun options -> options.PayloadSerializerOptions <- Constants.JsonSerializerOptions)
            .WithAutomaticReconnect()
            .Build()

    /// Creates an authenticated HTTP caller with optional Watch-origin delivery metadata.
    let createSaveClient (watchProcessId: Guid option) =
        let client = new HttpClient(BaseAddress = Uri graceServerBaseAddress)
        client.DefaultRequestHeaders.Add("x-grace-user-id", testUserId)

        watchProcessId
        |> Option.iter (fun processId -> client.DefaultRequestHeaders.Add(Constants.WatchProcessIdHeaderKey, processId.ToString("N")))

        client

    /// Posts one same-root Save through the authenticated production route and returns after the durable command succeeds.
    let saveReferenceAsync (client: HttpClient) repositoryId (branch: Grace.Types.Branch.BranchDto) referenceId message =
        task {
            let parameters = Parameters.Branch.CreateReferenceParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- string branch.BranchId
            parameters.ReferenceId <- referenceId
            parameters.DirectoryVersionId <- branch.BasedOn.DirectoryId
            parameters.Sha256Hash <- string branch.BasedOn.Sha256Hash
            parameters.Blake3Hash <- string branch.BasedOn.Blake3Hash
            parameters.Message <- message
            parameters.CorrelationId <- generateCorrelationId ()

            use! response = client.PostAsync("/branch/save", createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.IsSuccessStatusCode, Is.True, body)
        }

    /// Defines start agent session behavior for the surrounding tests used by the server integration notification Hub scenario.
    let startAgentSessionAsync repositoryId agentId workItemId operationId =
        task {
            let parameters = Parameters.Common.StartAgentSessionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.AgentId <- agentId
            parameters.AgentDisplayName <- $"Agent {agentId}"
            parameters.WorkItemIdOrNumber <- workItemId
            parameters.PromotionSetId <- $"{Guid.NewGuid()}"
            parameters.Source <- "signalr-server-test"
            parameters.OperationId <- operationId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/agent/session/start", createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            response.EnsureSuccessStatusCode() |> ignore
            return deserialize<GraceReturnValue<AgentSessionOperationResult>> body
        }

    /// Tries to resolve stop agent session without failing the caller.
    let tryStopAgentSessionAsync repositoryId agentId sessionId workItemId =
        task {
            if not <| String.IsNullOrWhiteSpace sessionId then
                try
                    let parameters = Parameters.Common.StopAgentSessionParameters()
                    parameters.OwnerId <- ownerId
                    parameters.OrganizationId <- organizationId
                    parameters.RepositoryId <- repositoryId
                    parameters.AgentId <- agentId
                    parameters.SessionId <- sessionId
                    parameters.WorkItemIdOrNumber <- workItemId
                    parameters.StopReason <- "signalr test cleanup"
                    parameters.OperationId <- $"cleanup-stop-{Guid.NewGuid():N}"
                    parameters.CorrelationId <- generateCorrelationId ()

                    let! response = Client.PostAsync("/agent/session/stop", createJsonContent parameters)

                    if not response.IsSuccessStatusCode then
                        let! body = response.Content.ReadAsStringAsync()
                        TestContext.Error.WriteLine($"SignalR session cleanup failed: {response.StatusCode}; {body}")
                with
                | ex -> TestContext.Error.WriteLine($"SignalR session cleanup threw: {ex}")
        }

    let waitForAutomationEventAsync
        (connection: HubConnection)
        (unexpectedConnection: HubConnection)
        (repositoryId: string)
        (unexpectedRepositoryId: string)
        (agentId: string)
        (expectedEventType: AutomationEventType)
        (operationId: string)
        (triggerAsync: unit -> Task)
        =
        task {
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))

            let completion = TaskCompletionSource<AutomationEventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously)
            let unexpectedCompletion = TaskCompletionSource<AutomationEventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously)

            /// Defines matches expected event behavior for the surrounding tests used by the server integration notification Hub scenario.
            let matchesExpectedEvent (envelope: AutomationEventEnvelope) =
                envelope.ActorId.Equals(agentId, StringComparison.OrdinalIgnoreCase)
                && envelope.EventType = expectedEventType
                && envelope.DataJson.Contains(operationId, StringComparison.OrdinalIgnoreCase)

            use subscription =
                connection.On<AutomationEventEnvelope>(
                    "NotifyAutomationEvent",
                    Action<AutomationEventEnvelope> (fun envelope ->
                        if envelope.RepositoryId = Guid.Parse(repositoryId)
                           && matchesExpectedEvent envelope then
                            completion.TrySetResult(envelope) |> ignore)
                )

            use unexpectedSubscription =
                unexpectedConnection.On<AutomationEventEnvelope>(
                    "NotifyAutomationEvent",
                    Action<AutomationEventEnvelope> (fun envelope ->
                        if matchesExpectedEvent envelope then
                            unexpectedCompletion.TrySetResult(envelope)
                            |> ignore)
                )

            use _registration =
                cts.Token.Register (fun () ->
                    completion.TrySetException(TimeoutException($"Timed out waiting for {expectedEventType} SignalR automation event."))
                    |> ignore)

            do! connection.InvokeAsync("RegisterRepository", Guid.Parse(repositoryId), cts.Token)
            do! unexpectedConnection.InvokeAsync("RegisterRepository", Guid.Parse(unexpectedRepositoryId), cts.Token)
            do! triggerAsync ()

            let! firstObserved = Task.WhenAny(completion.Task, unexpectedCompletion.Task)

            if obj.ReferenceEquals(firstObserved, unexpectedCompletion.Task) then
                let! unexpectedEnvelope = unexpectedCompletion.Task

                Assert.Fail(
                    $"SignalR connection registered to repository {unexpectedRepositoryId} received event {unexpectedEnvelope.EventType} for repository {unexpectedEnvelope.RepositoryId}."
                )

            let! envelope = completion.Task
            let! negativeObservation = Task.WhenAny(unexpectedCompletion.Task, Task.Delay(TimeSpan.FromSeconds(1.0), cts.Token))

            if obj.ReferenceEquals(negativeObservation, unexpectedCompletion.Task) then
                let! unexpectedEnvelope = unexpectedCompletion.Task

                Assert.Fail(
                    $"SignalR connection registered to repository {unexpectedRepositoryId} received event {unexpectedEnvelope.EventType} for repository {unexpectedEnvelope.RepositoryId}."
                )

            return envelope
        }

/// Covers notification hub scenarios.
[<NonParallelizable>]
type NotificationHubTests() =

    /// Verifies Watch-originated Save delivery excludes only its exact source while preserving peers, branch isolation, and reconnect fallback.
    [<Test>]
    member _.WatchSaveExcludesExactSourceAndReconnectFallsBackToOrdinaryGroupDelivery() =
        task {
            let repositoryId = repositoryIds[0]
            let! defaultBranch = BranchServerTestHelpers.getBranchAsync repositoryId repositoryDefaultBranchIds[0]
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId defaultBranch $"signalr-source-{Guid.NewGuid():N}"
            let! otherBranch = BranchServerTestHelpers.createBranchAsync repositoryId defaultBranch $"signalr-isolation-{Guid.NewGuid():N}"
            let sourceProcessId = Guid.NewGuid()
            let peerProcessId = Guid.NewGuid()
            let otherProcessId = Guid.NewGuid()
            let targetReferenceId = Guid.NewGuid()
            let fallbackReferenceId = Guid.NewGuid()
            let otherBranchReferenceId = Guid.NewGuid()
            let sourceCounts = ConcurrentDictionary<Guid, int>()
            let peerCounts = ConcurrentDictionary<Guid, int>()
            let otherCounts = ConcurrentDictionary<Guid, int>()
            let sourceFallback = TaskCompletionSource<CurrentBranchReferenceNotification>(TaskCreationOptions.RunContinuationsAsynchronously)
            let peerTarget = TaskCompletionSource<CurrentBranchReferenceNotification>(TaskCreationOptions.RunContinuationsAsynchronously)
            let peerFallback = TaskCompletionSource<CurrentBranchReferenceNotification>(TaskCreationOptions.RunContinuationsAsynchronously)
            let otherBranchMarker = TaskCompletionSource<CurrentBranchReferenceNotification>(TaskCreationOptions.RunContinuationsAsynchronously)

            /// Records one connection's exact Reference deliveries and completes only the expected deterministic markers.
            let observe
                (counts: ConcurrentDictionary<Guid, int>)
                (target: TaskCompletionSource<CurrentBranchReferenceNotification> option)
                (fallback: TaskCompletionSource<CurrentBranchReferenceNotification> option)
                (otherMarker: TaskCompletionSource<CurrentBranchReferenceNotification> option)
                (payload: CurrentBranchReferenceNotification)
                =
                counts.AddOrUpdate(payload.ReferenceId, 1, (fun _ count -> count + 1))
                |> ignore

                if payload.ReferenceId = targetReferenceId then
                    target
                    |> Option.iter (fun completion -> completion.TrySetResult(payload) |> ignore)
                elif payload.ReferenceId = fallbackReferenceId then
                    fallback
                    |> Option.iter (fun completion -> completion.TrySetResult(payload) |> ignore)
                elif payload.ReferenceId = otherBranchReferenceId then
                    otherMarker
                    |> Option.iter (fun completion -> completion.TrySetResult(payload) |> ignore)

            use sourceConnection = NotificationHubTestHelpers.createConnection true
            use peerConnection = NotificationHubTestHelpers.createConnection true
            use otherConnection = NotificationHubTestHelpers.createConnection true

            use sourceSubscription =
                sourceConnection.On<CurrentBranchReferenceNotification>(
                    "NotifyCurrentBranchReference",
                    Action<CurrentBranchReferenceNotification>(observe sourceCounts None (Some sourceFallback) None)
                )

            use peerSubscription =
                peerConnection.On<CurrentBranchReferenceNotification>(
                    "NotifyCurrentBranchReference",
                    Action<CurrentBranchReferenceNotification>(observe peerCounts (Some peerTarget) (Some peerFallback) None)
                )

            use otherSubscription =
                otherConnection.On<CurrentBranchReferenceNotification>(
                    "NotifyCurrentBranchReference",
                    Action<CurrentBranchReferenceNotification>(observe otherCounts None None (Some otherBranchMarker))
                )

            do! sourceConnection.StartAsync()
            do! peerConnection.StartAsync()
            do! otherConnection.StartAsync()

            use cts = new CancellationTokenSource(TimeSpan.FromSeconds(45.0))
            let repositoryGuid = Guid.Parse repositoryId

            do! sourceConnection.InvokeAsync("RegisterCurrentBranchSource", repositoryGuid, branch.BranchId, sourceProcessId.ToString("N"), cts.Token)

            do! peerConnection.InvokeAsync("RegisterCurrentBranchSource", repositoryGuid, branch.BranchId, peerProcessId.ToString("N"), cts.Token)

            do! otherConnection.InvokeAsync("RegisterCurrentBranchSource", repositoryGuid, otherBranch.BranchId, otherProcessId.ToString("N"), cts.Token)

            use sourceClient = NotificationHubTestHelpers.createSaveClient (Some sourceProcessId)
            do! NotificationHubTestHelpers.saveReferenceAsync sourceClient repositoryId branch targetReferenceId "hosted source exclusion proof"

            let! _ = peerTarget.Task.WaitAsync(cts.Token)

            // These completed hub invocations are per-connection ordering barriers for the preceding server fan-out.
            do! sourceConnection.InvokeAsync("RegisterCurrentBranch", repositoryGuid, branch.BranchId, cts.Token)

            do! peerConnection.InvokeAsync("RegisterCurrentBranchSource", repositoryGuid, branch.BranchId, peerProcessId.ToString("N"), cts.Token)

            do! otherConnection.InvokeAsync("RegisterCurrentBranch", repositoryGuid, otherBranch.BranchId, cts.Token)

            Assert.That(sourceCounts.GetValueOrDefault(targetReferenceId), Is.Zero, "the exact source connection must not receive its own Save")
            Assert.That(peerCounts.GetValueOrDefault(targetReferenceId), Is.EqualTo(1), "a distinct same-branch Watch process must receive the Save once")
            Assert.That(otherCounts.GetValueOrDefault(targetReferenceId), Is.Zero, "another branch must not receive the Save")

            do! sourceConnection.StopAsync(cts.Token)
            do! sourceConnection.StartAsync(cts.Token)
            do! sourceConnection.InvokeAsync("RegisterCurrentBranch", repositoryGuid, branch.BranchId, cts.Token)

            use ordinaryClient = NotificationHubTestHelpers.createSaveClient None
            do! NotificationHubTestHelpers.saveReferenceAsync ordinaryClient repositoryId branch fallbackReferenceId "hosted reconnect fallback proof"
            let! _ = sourceFallback.Task.WaitAsync(cts.Token)
            let! _ = peerFallback.Task.WaitAsync(cts.Token)

            do! NotificationHubTestHelpers.saveReferenceAsync ordinaryClient repositoryId otherBranch otherBranchReferenceId "hosted branch isolation marker"

            let! _ = otherBranchMarker.Task.WaitAsync(cts.Token)

            // Confirm each non-target connection has processed all frames queued before the branch-isolation marker.
            do! sourceConnection.InvokeAsync("RegisterCurrentBranch", repositoryGuid, branch.BranchId, cts.Token)
            do! peerConnection.InvokeAsync("RegisterCurrentBranch", repositoryGuid, branch.BranchId, cts.Token)
            do! otherConnection.InvokeAsync("RegisterCurrentBranch", repositoryGuid, otherBranch.BranchId, cts.Token)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(sourceCounts.GetValueOrDefault(fallbackReferenceId), Is.EqualTo(1))
                    Assert.That(peerCounts.GetValueOrDefault(fallbackReferenceId), Is.EqualTo(1))
                    Assert.That(otherCounts.GetValueOrDefault(fallbackReferenceId), Is.Zero)
                    Assert.That(sourceCounts.GetValueOrDefault(otherBranchReferenceId), Is.Zero)
                    Assert.That(peerCounts.GetValueOrDefault(otherBranchReferenceId), Is.Zero)
                    Assert.That(otherCounts.GetValueOrDefault(otherBranchReferenceId), Is.EqualTo(1)))
            )
        }

    /// Verifies the authenticated client receives repository automation event scenario.
    [<Test>]
    member _.AuthenticatedClientReceivesRepositoryAutomationEvent() =
        task {
            let repositoryId = repositoryIds[0]
            let unexpectedRepositoryId = repositoryIds[1]
            let agentId = $"signalr-agent-{Guid.NewGuid():N}"
            let workItemId = "262"
            let operationId = $"signalr-start-{Guid.NewGuid():N}"
            let mutable sessionId = String.Empty

            use connection = NotificationHubTestHelpers.createConnection true
            use unexpectedConnection = NotificationHubTestHelpers.createConnection true
            do! connection.StartAsync()
            do! unexpectedConnection.StartAsync()

            try
                let! envelope =
                    NotificationHubTestHelpers.waitForAutomationEventAsync
                        connection
                        unexpectedConnection
                        repositoryId
                        unexpectedRepositoryId
                        agentId
                        AutomationEventType.AgentWorkStarted
                        operationId
                        (fun () ->
                            task {
                                let! result = NotificationHubTestHelpers.startAgentSessionAsync repositoryId agentId workItemId operationId
                                sessionId <- result.ReturnValue.Session.SessionId

                                Assert.That(result.ReturnValue.Session.SessionId, Is.EqualTo(operationId))
                            }
                            :> Task)

                Assert.That(envelope.RepositoryId, Is.EqualTo(Guid.Parse(repositoryId)))
                Assert.That(envelope.ActorId, Is.EqualTo(agentId))
                Assert.That(envelope.EventType, Is.EqualTo(AutomationEventType.AgentWorkStarted))
                Assert.That(envelope.CorrelationId, Is.Not.Empty)
                Assert.That(envelope.DataJson, Does.Contain(operationId))
            with
            | ex ->
                do! NotificationHubTestHelpers.tryStopAgentSessionAsync repositoryId agentId sessionId workItemId
                return raise ex

            do! NotificationHubTestHelpers.tryStopAgentSessionAsync repositoryId agentId sessionId workItemId
        }

    /// Verifies the unauthenticated client cannot connect to notifications hub scenario.
    [<Test>]
    member _.UnauthenticatedClientCannotConnectToNotificationsHub() =
        task {
            use connection = NotificationHubTestHelpers.createConnection false

            try
                do! connection.StartAsync()
                Assert.Fail("Unauthenticated SignalR connection unexpectedly succeeded.")
            with
            | :? HttpRequestException as ex -> Assert.That(ex.Message, Does.Contain("401").Or.Contain("Unauthorized"))
            | :? InvalidOperationException as ex -> Assert.That(ex.Message, Does.Contain("401").Or.Contain("Unauthorized"))
        }
