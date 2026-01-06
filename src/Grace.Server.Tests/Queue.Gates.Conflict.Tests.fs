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
    member _.RequiredActionsIncludePolicyConflictAndGate() =
        let conflict = { ConflictAnalysis.Default with FilePath = "src/Auth.fs" }
        let candidate = buildCandidate CandidateStatus.Blocked (PolicySnapshotId String.Empty) [ conflict ]

        let actions = Grace.Server.Queue.computeRequiredActions candidate
        let types = actions |> List.map (fun action -> action.RequiredActionType)

        Assert.That(types, Does.Contain(RequiredActionType.AcknowledgePolicyChange))
        Assert.That(types, Does.Contain(RequiredActionType.ResolveConflict))
        Assert.That(types, Does.Contain(RequiredActionType.RunGate))

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
