namespace Grace.Server.Tests

open Grace.Server
open Grace.Server.Gates
open Grace.Types.Policy
open Grace.Types.PromotionGroup
open Grace.Types.Queue
open Grace.Types.RequiredAction
open Grace.Types.Types
open NUnit.Framework
open NodaTime
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type QueueGatesConflict() =
    let metadata timestamp = { Timestamp = timestamp; CorrelationId = "corr-queue"; Principal = "tester"; Properties = Dictionary<string, string>() }

    let buildCandidate status policySnapshotId conflicts =
        { IntegrationCandidate.Default with
            CandidateId = Guid.NewGuid()
            OwnerId = Guid.NewGuid()
            OrganizationId = Guid.NewGuid()
            RepositoryId = Guid.NewGuid()
            TargetBranchId = Guid.NewGuid()
            PolicySnapshotId = policySnapshotId
            Status = status
            Conflicts = conflicts
            CreatedAt = Instant.FromUtc(2025, 1, 1, 0, 0) }

    let buildRequiredAction actionType reason =
        { RequiredActionType = actionType
          TargetId = None
          Reason = reason
          Parameters = Dictionary<string, string>()
          SuggestedCliCommand = None
          SuggestedApiCall = None }

    [<Test>]
    member _.PromotionQueueDtoUpdatesDeterministically() =
        let branchId = Guid.NewGuid()
        let candidateId = Guid.NewGuid()
        let snapshotId = PolicySnapshotId "policy"

        let events: PromotionQueueEvent list =
            [ { Event = PromotionQueueEventType.Initialized(branchId, snapshotId); Metadata = metadata (Instant.FromUtc(2025, 1, 1, 0, 0)) }
              { Event = PromotionQueueEventType.CandidateEnqueued candidateId; Metadata = metadata (Instant.FromUtc(2025, 1, 1, 0, 1)) }
              { Event = PromotionQueueEventType.RunningCandidateSet(Some candidateId); Metadata = metadata (Instant.FromUtc(2025, 1, 1, 0, 2)) }
              { Event = PromotionQueueEventType.Paused; Metadata = metadata (Instant.FromUtc(2025, 1, 1, 0, 3)) }
              { Event = PromotionQueueEventType.Resumed; Metadata = metadata (Instant.FromUtc(2025, 1, 1, 0, 4)) } ]

        let finalState =
            events
            |> List.fold (fun state ev -> PromotionQueueDto.UpdateDto ev state) PromotionQueue.Default

        let secondPass =
            events
            |> List.fold (fun state ev -> PromotionQueueDto.UpdateDto ev state) PromotionQueue.Default

        let stateMatch = finalState = secondPass
        let branchMatch = finalState.TargetBranchId = branchId
        let candidatesMatch = finalState.CandidateIds = [ candidateId ]
        let runningMatch = finalState.State = QueueState.Running

        Assert.That(stateMatch, Is.True)
        Assert.That(branchMatch, Is.True)
        Assert.That(candidatesMatch, Is.True)
        Assert.That(runningMatch, Is.True)

    [<Test>]
    member _.IntegrationCandidateTracksConflictsAndReceipts() =
        let conflict = { ConflictAnalysis.Default with FilePath = "src/Auth.fs" }
        let receiptId = Guid.NewGuid()

        let start = buildCandidate CandidateStatus.Pending (PolicySnapshotId "policy") []

        let conflictEvent: CandidateEvent = { Event = CandidateEventType.ConflictAdded conflict; Metadata = metadata (Instant.FromUtc(2025, 1, 2, 0, 0)) }

        let receiptEvent: CandidateEvent =
            { Event = CandidateEventType.ConflictReceiptAdded receiptId; Metadata = metadata (Instant.FromUtc(2025, 1, 2, 0, 1)) }

        let withConflict = IntegrationCandidateDto.UpdateDto conflictEvent start
        let withReceipt = IntegrationCandidateDto.UpdateDto receiptEvent withConflict

        let conflictsMatch = withReceipt.Conflicts = [ conflict ]
        let receiptsMatch = withReceipt.ConflictReceiptIds = [ receiptId ]
        let updatedMatch = withReceipt.UpdatedAt = Some(Instant.FromUtc(2025, 1, 2, 0, 1))

        Assert.That(conflictsMatch, Is.True)
        Assert.That(receiptsMatch, Is.True)
        Assert.That(updatedMatch, Is.True)

    [<Test>]
    member _.RetryCandidateClearsRequiredActionsAndSetsPending() =
        let action = buildRequiredAction RequiredActionType.ResolveConflict "Resolve conflict."
        let candidate = { IntegrationCandidate.Default with Status = CandidateStatus.Failed; RequiredActions = [ action ] }

        let clearedEvent: CandidateEvent = { Event = CandidateEventType.RequiredActionsCleared; Metadata = metadata (Instant.FromUtc(2025, 1, 3, 0, 0)) }

        let statusEvent: CandidateEvent =
            { Event = CandidateEventType.StatusSet CandidateStatus.Pending; Metadata = metadata (Instant.FromUtc(2025, 1, 3, 0, 1)) }

        let updated =
            candidate
            |> IntegrationCandidateDto.UpdateDto clearedEvent
            |> IntegrationCandidateDto.UpdateDto statusEvent

        Assert.That(updated.RequiredActions, Is.Empty)
        Assert.That(updated.Status, Is.EqualTo(CandidateStatus.Pending))

    [<Test>]
    member _.CanceledCandidateStatusIsRecorded() =
        let candidate = { IntegrationCandidate.Default with Status = CandidateStatus.Running }
        let event: CandidateEvent = { Event = CandidateEventType.StatusSet CandidateStatus.Canceled; Metadata = metadata (Instant.FromUtc(2025, 1, 4, 0, 0)) }

        let updated = IntegrationCandidateDto.UpdateDto event candidate

        Assert.That(updated.Status, Is.EqualTo(CandidateStatus.Canceled))

    [<Test>]
    member _.CandidateDequeuedRemovesFromQueue() =
        let candidateId = Guid.NewGuid()

        let queue =
            { PromotionQueue.Default with
                TargetBranchId = Guid.NewGuid()
                CandidateIds = [ candidateId ]
                RunningCandidateId = Some candidateId
                State = QueueState.Running }

        let event: PromotionQueueEvent =
            { Event = PromotionQueueEventType.CandidateDequeued candidateId; Metadata = metadata (Instant.FromUtc(2025, 1, 5, 0, 0)) }

        let updated = PromotionQueueDto.UpdateDto event queue

        Assert.That(updated.CandidateIds, Is.Empty)

    [<Test>]
    member _.RequiredActionsIncludePolicyConflictAndGate() =
        let conflict = { ConflictAnalysis.Default with FilePath = "src/Auth.fs" }
        let candidate = buildCandidate CandidateStatus.Blocked (PolicySnapshotId String.Empty) [ conflict ]

        let actions = Grace.Server.Queue.computeRequiredActions candidate
        let types = actions |> List.map (fun action -> action.RequiredActionType)

        Assert.That(types, Does.Contain(RequiredActionType.AcknowledgePolicyChange))
        Assert.That(types, Does.Contain(RequiredActionType.ResolveConflict))
        Assert.That(types, Does.Contain(RequiredActionType.RunGate))

    [<Test>]
    member _.RequiredActionsIncludeFixGateFailureOnlyForFailedCandidates() =
        let candidate = buildCandidate CandidateStatus.Blocked (PolicySnapshotId "policy") []

        let blockedActions = Grace.Server.Queue.computeRequiredActions candidate
        let blockedTypes = blockedActions |> List.map (fun action -> action.RequiredActionType)

        Assert.That(blockedTypes, Does.Not.Contain(RequiredActionType.FixGateFailure))

        let failedCandidate = buildCandidate CandidateStatus.Failed (PolicySnapshotId "policy") []
        let failedActions = Grace.Server.Queue.computeRequiredActions failedCandidate
        let failedTypes = failedActions |> List.map (fun action -> action.RequiredActionType)

        Assert.That(failedTypes, Does.Contain(RequiredActionType.FixGateFailure))

    [<Test>]
    member _.GateResultsRespectPolicySnapshotAndRegistry() =
        let candidate = buildCandidate CandidateStatus.Pending (PolicySnapshotId "policy") []
        let context = { GateContext.Candidate = candidate; PolicySnapshot = None; CorrelationId = "corr-gate"; Principal = UserId "tester" }

        let policyAttestation = Gates.runGate "policy" context |> Async.AwaitTask |> Async.RunSynchronously
        Assert.That(policyAttestation.Result, Is.EqualTo(GateResult.Block))
        Assert.That(policyAttestation.Summary, Does.Contain("missing"))

        let missingAttestation = Gates.runGate "missing" context |> Async.AwaitTask |> Async.RunSynchronously
        Assert.That(missingAttestation.Result, Is.EqualTo(GateResult.Block))
        Assert.That(missingAttestation.GateVersion, Is.EqualTo("unknown"))

    [<Test>]
    member _.BuildTestGateDefaultsToSkipped() =
        let candidate = buildCandidate CandidateStatus.Pending (PolicySnapshotId "policy") []
        let context = { GateContext.Candidate = candidate; PolicySnapshot = None; CorrelationId = "corr-gate"; Principal = UserId "tester" }

        let attestation = Gates.runGate "build-test" context |> Async.AwaitTask |> Async.RunSynchronously

        Assert.That(attestation.Result, Is.EqualTo(GateResult.Skipped))

    [<Test>]
    member _.QueueInitializationRequiresPolicySnapshotWhenQueueMissing() =
        let requiresSnapshot = Grace.Server.Queue.requiresPolicySnapshotForInitialization false String.Empty

        let noRequirementWhenProvided = Grace.Server.Queue.requiresPolicySnapshotForInitialization false "snapshot"

        let noRequirementWhenQueueExists = Grace.Server.Queue.requiresPolicySnapshotForInitialization true String.Empty

        Assert.That(requiresSnapshot, Is.True)
        Assert.That(noRequirementWhenProvided, Is.False)
        Assert.That(noRequirementWhenQueueExists, Is.False)
