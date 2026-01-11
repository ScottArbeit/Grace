namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared.Parameters.Policy
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.Review
open Grace.Types.Types
open NUnit.Framework
open NodaTime
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type PolicyStage0DerivedTests() =
    let metadata correlationId timestamp =
        { Timestamp = timestamp; CorrelationId = correlationId; Principal = "tester"; Properties = Dictionary<string, string>() }

    [<Test>]
    member _.Stage0RejectsDuplicateCorrelationIds() =
        let timestamp = Instant.FromUtc(2025, 3, 1, 0, 0)
        let eventMetadata = metadata "corr-stage0" timestamp
        let stage0Event: Stage0Event = { Event = Stage0EventType.Recorded Stage0Analysis.Default; Metadata = eventMetadata }

        let duplicate = Grace.Actors.Stage0.hasDuplicateCorrelationId [ stage0Event ] eventMetadata
        let different = Grace.Actors.Stage0.hasDuplicateCorrelationId [ stage0Event ] { eventMetadata with CorrelationId = "corr-other" }

        Assert.That(duplicate, Is.True)
        Assert.That(different, Is.False)

    [<Test>]
    member _.DerivedComputationStage0PredicateMatchesReferenceTypes() =
        Assert.That(DerivedComputation.shouldRecordStage0 ReferenceType.Commit, Is.True)
        Assert.That(DerivedComputation.shouldRecordStage0 ReferenceType.Checkpoint, Is.True)
        Assert.That(DerivedComputation.shouldRecordStage0 ReferenceType.Promotion, Is.True)
        Assert.That(DerivedComputation.shouldRecordStage0 ReferenceType.Save, Is.False)

    [<Test>]
    member _.PolicyAcknowledgeRejectsMissingSnapshotId() =
        let parameters = AcknowledgePolicyParameters(TargetBranchId = System.Guid.NewGuid().ToString(), PolicySnapshotId = String.Empty)

        let validations = Policy.validateAcknowledgeParameters parameters

        let error =
            validations
            |> getFirstError
            |> Async.AwaitTask
            |> Async.RunSynchronously

        Assert.That(error, Is.EqualTo(Some PolicyError.InvalidPolicySnapshotId))

    [<Test>]
    member _.PolicyAcknowledgeRejectsInvalidBranchId() =
        let parameters = AcknowledgePolicyParameters(TargetBranchId = "not-a-guid", PolicySnapshotId = "snapshot")

        let validations = Policy.validateAcknowledgeParameters parameters

        let error =
            validations
            |> getFirstError
            |> Async.AwaitTask
            |> Async.RunSynchronously

        Assert.That(error, Is.EqualTo(Some PolicyError.InvalidTargetBranchId))
