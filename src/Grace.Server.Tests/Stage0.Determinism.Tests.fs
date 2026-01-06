namespace Grace.Server.Tests

open Grace.Types.Policy
open Grace.Types.Review
open Grace.Types.Types
open NUnit.Framework
open NodaTime
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type Stage0Determinism() =
    let metadata timestamp = { Timestamp = timestamp; CorrelationId = "corr-1"; Principal = "tester"; Properties = Dictionary<string, string>() }

    [<Test>]
    member _.UpdateUsesEventPayloadAndTimestamp() =
        let timestamp = Instant.FromUtc(2025, 1, 1, 0, 0)
        let referenceId = Guid.NewGuid()

        let riskProfile =
            { DeterministicRiskProfile.Default with ReferenceId = referenceId; PolicySnapshotId = PolicySnapshotId "policy"; CreatedAt = timestamp }

        let analysis =
            { Stage0Analysis.Default with
                Stage0AnalysisId = Guid.NewGuid()
                ReferenceId = referenceId
                PolicySnapshotId = PolicySnapshotId "policy"
                RiskProfile = riskProfile
                CreatedAt = timestamp }

        let stage0Event: Stage0Event = { Event = Stage0EventType.Recorded analysis; Metadata = metadata timestamp }
        let updated = Stage0AnalysisDto.UpdateDto stage0Event Stage0Analysis.Default

        Assert.That(updated.Stage0AnalysisId, Is.EqualTo(analysis.Stage0AnalysisId))
        Assert.That(updated.ReferenceId, Is.EqualTo(analysis.ReferenceId))
        Assert.That(updated.RiskProfile, Is.EqualTo(analysis.RiskProfile))
        Assert.That(updated.UpdatedAt, Is.EqualTo(Some timestamp))

    [<Test>]
    member _.UpdateIsDeterministicForSameEvent() =
        let timestamp = Instant.FromUtc(2025, 2, 1, 0, 0)
        let referenceId = Guid.NewGuid()

        let analysis =
            { Stage0Analysis.Default with
                Stage0AnalysisId = Guid.NewGuid()
                ReferenceId = referenceId
                PolicySnapshotId = PolicySnapshotId "policy"
                RiskProfile = { DeterministicRiskProfile.Default with ReferenceId = referenceId }
                CreatedAt = timestamp }

        let stage0Event: Stage0Event = { Event = Stage0EventType.Recorded analysis; Metadata = metadata timestamp }
        let first = Stage0AnalysisDto.UpdateDto stage0Event Stage0Analysis.Default
        let second = Stage0AnalysisDto.UpdateDto stage0Event Stage0Analysis.Default

        Assert.That(first, Is.EqualTo(second))
