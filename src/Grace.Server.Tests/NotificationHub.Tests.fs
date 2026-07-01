namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Automation
open Grace.Types.Common
open Microsoft.AspNetCore.Http.Connections.Client
open Microsoft.AspNetCore.SignalR.Client
open Microsoft.Extensions.DependencyInjection
open NUnit.Framework
open System
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
