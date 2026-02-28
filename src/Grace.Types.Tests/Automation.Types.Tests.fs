namespace Grace.Types.Tests

open Grace.Shared
open Grace.Shared.Parameters.Common
open Grace.Types.Automation
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
type AutomationTypesTests() =
    [<Test>]
    member _.AutomationEventTypeIncludesAgentLifecycleCases() =
        let eventTypes = Utilities.listCases<AutomationEventType>() |> Set.ofArray

        Assert.Multiple(fun () ->
            Assert.That(eventTypes.Contains("AgentWorkStarted"), Is.True)
            Assert.That(eventTypes.Contains("AgentWorkStopped"), Is.True)
            Assert.That(eventTypes.Contains("AgentSummaryAdded"), Is.True)
            Assert.That(eventTypes.Contains("AgentBootstrapped"), Is.True)
        )

    [<Test>]
    member _.StartAgentSessionParametersDefaultRequiredFieldsAreEmpty() =
        let parameters = StartAgentSessionParameters()

        Assert.Multiple(fun () ->
            Assert.That(parameters.CorrelationId, Is.EqualTo(String.Empty))
            Assert.That(parameters.WorkItemIdOrNumber, Is.EqualTo(String.Empty))
            Assert.That(parameters.OperationId, Is.EqualTo(String.Empty))
            Assert.That(parameters.AgentId, Is.EqualTo(String.Empty))
        )

    [<Test>]
    member _.StartAgentSessionParametersRoundTripsSerialization() =
        let parameters = StartAgentSessionParameters()
        parameters.CorrelationId <- "corr-session-start"
        parameters.OwnerId <- "owner-1"
        parameters.OrganizationId <- "org-1"
        parameters.RepositoryId <- "repo-1"
        parameters.AgentId <- "agent-1"
        parameters.AgentDisplayName <- "Codex"
        parameters.WorkItemIdOrNumber <- "42"
        parameters.PromotionSetId <- "promotion-set-9"
        parameters.Source <- "codex"
        parameters.OperationId <- "operation-123"

        let json = Utilities.serialize parameters
        let deserialized = Utilities.deserialize<StartAgentSessionParameters> json

        Assert.Multiple(fun () ->
            Assert.That(deserialized.CorrelationId, Is.EqualTo("corr-session-start"))
            Assert.That(deserialized.WorkItemIdOrNumber, Is.EqualTo("42"))
            Assert.That(deserialized.PromotionSetId, Is.EqualTo("promotion-set-9"))
            Assert.That(deserialized.OperationId, Is.EqualTo("operation-123"))
        )
