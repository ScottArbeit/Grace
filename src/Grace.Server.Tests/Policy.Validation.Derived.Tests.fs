namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared.Parameters.Policy
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.Types
open Grace.Types.Validation
open NUnit.Framework
open NodaTime
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type PolicyValidationDerivedTests() =
    let metadata correlationId timestamp =
        { Timestamp = timestamp; CorrelationId = correlationId; Principal = "tester"; Properties = Dictionary<string, string>() }

    [<Test>]
    member _.ValidationResultRejectsDuplicateCorrelationIds() =
        let timestamp = Instant.FromUtc(2025, 3, 1, 0, 0)
        let eventMetadata = metadata "corr-validation" timestamp

        let validationResultEvent: ValidationResultEvent = { Event = ValidationResultEventType.Recorded ValidationResultDto.Default; Metadata = eventMetadata }

        let duplicate = Grace.Actors.ValidationResult.hasDuplicateCorrelationId [ validationResultEvent ] eventMetadata

        let different = Grace.Actors.ValidationResult.hasDuplicateCorrelationId [ validationResultEvent ] { eventMetadata with CorrelationId = "corr-other" }

        Assert.That(duplicate, Is.True)
        Assert.That(different, Is.False)

    [<Test>]
    member _.DerivedComputationQuickScanPredicateMatchesReferenceTypes() =
        Assert.That(DerivedComputation.shouldRecordQuickScan ReferenceType.Commit, Is.True)
        Assert.That(DerivedComputation.shouldRecordQuickScan ReferenceType.Checkpoint, Is.True)
        Assert.That(DerivedComputation.shouldRecordQuickScan ReferenceType.Promotion, Is.True)
        Assert.That(DerivedComputation.shouldRecordQuickScan ReferenceType.Save, Is.False)

    [<Test>]
    member _.PolicyAcknowledgeRejectsMissingSnapshotId() =
        let parameters = AcknowledgePolicyParameters(TargetBranchId = Guid.NewGuid().ToString(), PolicySnapshotId = String.Empty)

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
