namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Automation
open Grace.Types.Common
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Threading.Tasks

module private AgentSessionTestHelpers =

    let private postSessionAsync<'TResponse> (path: string) (parameters: obj) =
        task {
            let! response = Client.PostAsync(path, createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return deserialize<GraceReturnValue<'TResponse>> body
        }

    let private createBaseParameters<'T when 'T :> Parameters.Common.AgentSessionParameters and 'T: (new: unit -> 'T)> repositoryId agentId agentDisplayName =
        let parameters = new 'T()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.AgentId <- agentId
        parameters.AgentDisplayName <- agentDisplayName
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let startParameters repositoryId agentId workItemId operationId =
        let parameters = createBaseParameters<Parameters.Common.StartAgentSessionParameters> repositoryId agentId $"Agent {agentId}"

        parameters.WorkItemIdOrNumber <- workItemId
        parameters.PromotionSetId <- $"{Guid.NewGuid()}"
        parameters.Source <- "server-tests"
        parameters.OperationId <- operationId
        parameters

    let stopParameters repositoryId agentId sessionId workItemId operationId =
        let parameters = createBaseParameters<Parameters.Common.StopAgentSessionParameters> repositoryId agentId $"Agent {agentId}"

        parameters.SessionId <- sessionId
        parameters.WorkItemIdOrNumber <- workItemId
        parameters.StopReason <- "test complete"
        parameters.OperationId <- operationId
        parameters

    let statusParameters repositoryId agentId sessionId workItemId =
        let parameters = createBaseParameters<Parameters.Common.GetAgentSessionStatusParameters> repositoryId agentId $"Agent {agentId}"

        parameters.SessionId <- sessionId
        parameters.WorkItemIdOrNumber <- workItemId
        parameters

    let activeParameters repositoryId agentId workItemId =
        let parameters = createBaseParameters<Parameters.Common.GetActiveAgentSessionParameters> repositoryId agentId $"Agent {agentId}"

        parameters.WorkItemIdOrNumber <- workItemId
        parameters

    let listActiveParameters repositoryId agentId maximumSessionCount =
        let parameters = createBaseParameters<Parameters.Common.ListActiveAgentSessionsParameters> repositoryId agentId $"Agent {agentId}"

        parameters.MaximumSessionCount <- maximumSessionCount
        parameters

    let startAsync parameters = postSessionAsync<AgentSessionOperationResult> "/agent/session/start" parameters

    let stopAsync parameters = postSessionAsync<AgentSessionOperationResult> "/agent/session/stop" parameters

    let statusAsync parameters = postSessionAsync<AgentSessionOperationResult> "/agent/session/status" parameters

    let activeAsync parameters = postSessionAsync<AgentSessionOperationResult> "/agent/session/active" parameters

    let listActiveAsync parameters = postSessionAsync<AgentSessionListResult> "/agent/session/listActive" parameters

[<NonParallelizable>]
type AgentSessionServerTests() =

    [<Test>]
    member _.AgentSessionLifecyclePreservesReplayConflictWrongRepositoryAndStopIdempotency() =
        task {
            let repositoryId = repositoryIds[0]
            let wrongRepositoryId = repositoryIds[1]
            let agentId = $"agent-{Guid.NewGuid():N}"
            let firstWorkItem = "262"
            let secondWorkItem = "263"
            let firstStartOperationId = $"start-{Guid.NewGuid():N}"

            let! started = AgentSessionTestHelpers.startAsync (AgentSessionTestHelpers.startParameters repositoryId agentId firstWorkItem firstStartOperationId)

            Assert.That(started.ReturnValue.WasIdempotentReplay, Is.False)
            Assert.That(started.ReturnValue.OperationId, Is.EqualTo(firstStartOperationId))
            Assert.That(started.ReturnValue.Session.SessionId, Is.EqualTo(firstStartOperationId))
            Assert.That(started.ReturnValue.Session.AgentId, Is.EqualTo(agentId))
            Assert.That(started.ReturnValue.Session.WorkItemIdOrNumber, Is.EqualTo(firstWorkItem))
            Assert.That(started.ReturnValue.Session.Source, Is.EqualTo("server-tests"))
            Assert.That(started.ReturnValue.Session.LifecycleState, Is.EqualTo(AgentSessionLifecycleState.Active))

            let! duplicateStart =
                AgentSessionTestHelpers.startAsync (AgentSessionTestHelpers.startParameters repositoryId agentId firstWorkItem firstStartOperationId)

            Assert.That(duplicateStart.ReturnValue.WasIdempotentReplay, Is.True)
            Assert.That(duplicateStart.ReturnValue.Session.SessionId, Is.EqualTo(started.ReturnValue.Session.SessionId))

            let conflictingStartParameters = AgentSessionTestHelpers.startParameters repositoryId agentId secondWorkItem $"start-{Guid.NewGuid():N}"

            let! conflictingStart = Client.PostAsync("/agent/session/start", createJsonContent conflictingStartParameters)

            let! conflictingStartBody = conflictingStart.Content.ReadAsStringAsync()
            Assert.That(conflictingStart.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), conflictingStartBody)
            Assert.That(conflictingStartBody, Does.Contain(firstWorkItem))

            let! active = AgentSessionTestHelpers.activeAsync (AgentSessionTestHelpers.activeParameters repositoryId agentId firstWorkItem)

            Assert.That(active.ReturnValue.Session.SessionId, Is.EqualTo(started.ReturnValue.Session.SessionId))
            Assert.That(active.ReturnValue.Session.LifecycleState, Is.EqualTo(AgentSessionLifecycleState.Active))

            let! listed = AgentSessionTestHelpers.listActiveAsync (AgentSessionTestHelpers.listActiveParameters repositoryId agentId 10)

            Assert.That(
                listed.ReturnValue.Sessions
                |> List.exists (fun session -> session.SessionId = started.ReturnValue.Session.SessionId),
                Is.True
            )

            let! wrongRepositoryStatus =
                AgentSessionTestHelpers.statusAsync (
                    AgentSessionTestHelpers.statusParameters wrongRepositoryId agentId started.ReturnValue.Session.SessionId firstWorkItem
                )

            Assert.That(wrongRepositoryStatus.ReturnValue.Session.LifecycleState, Is.EqualTo(AgentSessionLifecycleState.Inactive))

            let! wrongRepositoryStop =
                AgentSessionTestHelpers.stopAsync (
                    AgentSessionTestHelpers.stopParameters
                        wrongRepositoryId
                        agentId
                        started.ReturnValue.Session.SessionId
                        firstWorkItem
                        $"stop-wrong-{Guid.NewGuid():N}"
                )

            Assert.That(wrongRepositoryStop.ReturnValue.WasIdempotentReplay, Is.True)
            Assert.That(wrongRepositoryStop.ReturnValue.Session.LifecycleState, Is.EqualTo(AgentSessionLifecycleState.Inactive))

            let! stillActive =
                AgentSessionTestHelpers.statusAsync (
                    AgentSessionTestHelpers.statusParameters repositoryId agentId started.ReturnValue.Session.SessionId firstWorkItem
                )

            Assert.That(stillActive.ReturnValue.Session.LifecycleState, Is.EqualTo(AgentSessionLifecycleState.Active))

            let stopOperationId = $"stop-{Guid.NewGuid():N}"

            let! stopped =
                AgentSessionTestHelpers.stopAsync (
                    AgentSessionTestHelpers.stopParameters repositoryId agentId started.ReturnValue.Session.SessionId firstWorkItem stopOperationId
                )

            Assert.That(stopped.ReturnValue.WasIdempotentReplay, Is.False)
            Assert.That(stopped.ReturnValue.OperationId, Is.EqualTo(stopOperationId))
            Assert.That(stopped.ReturnValue.Session.LifecycleState, Is.EqualTo(AgentSessionLifecycleState.Stopped))

            let! duplicateStop =
                AgentSessionTestHelpers.stopAsync (
                    AgentSessionTestHelpers.stopParameters repositoryId agentId started.ReturnValue.Session.SessionId firstWorkItem stopOperationId
                )

            Assert.That(duplicateStop.ReturnValue.WasIdempotentReplay, Is.True)
            Assert.That(duplicateStop.ReturnValue.Session.LifecycleState, Is.EqualTo(AgentSessionLifecycleState.Stopped))

            let! inactiveAfterStop =
                AgentSessionTestHelpers.statusAsync (
                    AgentSessionTestHelpers.statusParameters repositoryId agentId started.ReturnValue.Session.SessionId firstWorkItem
                )

            Assert.That(inactiveAfterStop.ReturnValue.Session.LifecycleState, Is.EqualTo(AgentSessionLifecycleState.Inactive))

            let secondStartOperationId = $"start-{Guid.NewGuid():N}"

            let! replacementStart =
                AgentSessionTestHelpers.startAsync (AgentSessionTestHelpers.startParameters repositoryId agentId secondWorkItem secondStartOperationId)

            Assert.That(replacementStart.ReturnValue.WasIdempotentReplay, Is.False)
            Assert.That(replacementStart.ReturnValue.Session.SessionId, Is.EqualTo(secondStartOperationId))
            Assert.That(replacementStart.ReturnValue.Session.WorkItemIdOrNumber, Is.EqualTo(secondWorkItem))
            Assert.That(replacementStart.ReturnValue.Session.LifecycleState, Is.EqualTo(AgentSessionLifecycleState.Active))
        }
