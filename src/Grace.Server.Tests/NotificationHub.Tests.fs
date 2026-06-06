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

module private NotificationHubTestHelpers =

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

    let waitForAutomationEventAsync
        (connection: HubConnection)
        (repositoryId: string)
        (agentId: string)
        (expectedEventType: AutomationEventType)
        (triggerAsync: unit -> Task)
        =
        task {
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))

            let completion = TaskCompletionSource<AutomationEventEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously)

            use subscription =
                connection.On<AutomationEventEnvelope>(
                    "NotifyAutomationEvent",
                    Action<AutomationEventEnvelope> (fun envelope ->
                        if envelope.RepositoryId = Guid.Parse(repositoryId)
                           && envelope.ActorId.Equals(agentId, StringComparison.OrdinalIgnoreCase)
                           && envelope.EventType = expectedEventType then
                            completion.TrySetResult(envelope) |> ignore)
                )

            use _registration =
                cts.Token.Register (fun () ->
                    completion.TrySetException(TimeoutException($"Timed out waiting for {expectedEventType} SignalR automation event."))
                    |> ignore)

            do! connection.InvokeAsync("RegisterRepository", Guid.Parse(repositoryId), cts.Token)
            do! triggerAsync ()
            return! completion.Task
        }

[<NonParallelizable>]
type NotificationHubTests() =

    [<Test>]
    member _.AuthenticatedClientReceivesRepositoryAutomationEvent() =
        task {
            let repositoryId = repositoryIds[0]
            let agentId = $"signalr-agent-{Guid.NewGuid():N}"
            let workItemId = "262"
            let operationId = $"signalr-start-{Guid.NewGuid():N}"

            use connection = NotificationHubTestHelpers.createConnection true
            do! connection.StartAsync()

            let! envelope =
                NotificationHubTestHelpers.waitForAutomationEventAsync connection repositoryId agentId AutomationEventType.AgentWorkStarted (fun () ->
                    task {
                        let! result = NotificationHubTestHelpers.startAgentSessionAsync repositoryId agentId workItemId operationId

                        Assert.That(result.ReturnValue.Session.SessionId, Is.EqualTo(operationId))
                    }
                    :> Task)

            Assert.That(envelope.RepositoryId, Is.EqualTo(Guid.Parse(repositoryId)))
            Assert.That(envelope.ActorId, Is.EqualTo(agentId))
            Assert.That(envelope.EventType, Is.EqualTo(AutomationEventType.AgentWorkStarted))
            Assert.That(envelope.CorrelationId, Is.Not.Empty)
            Assert.That(envelope.DataJson, Does.Contain(operationId))
        }

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
