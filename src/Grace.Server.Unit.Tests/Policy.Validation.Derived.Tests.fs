namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared.Parameters.Policy
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.Common
open Grace.Types.Validation
open NUnit.Framework
open NodaTime
open System
open System.Collections.Generic

/// Covers policy Validation Derived behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type PolicyValidationDerivedTests() =
    /// Constructs metadata fixtures used by the server unit policy Validation Derived assertions.
    let metadata correlationId timestamp =
        {
            Timestamp = timestamp
            CorrelationId = correlationId
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    /// Verifies that validation Result Rejects Duplicate Correlation Ids.
    [<Test>]
    member _.ValidationResultRejectsDuplicateCorrelationIds() =
        let timestamp = Instant.FromUtc(2025, 3, 1, 0, 0)
        let eventMetadata = metadata "corr-validation" timestamp

        let validationResultEvent: ValidationResultEvent = { Event = ValidationResultEventType.Recorded ValidationResultDto.Default; Metadata = eventMetadata }

        let duplicate = Grace.Actors.ValidationResult.hasDuplicateCorrelationId [ validationResultEvent ] eventMetadata

        let different = Grace.Actors.ValidationResult.hasDuplicateCorrelationId [ validationResultEvent ] { eventMetadata with CorrelationId = "corr-other" }

        Assert.That(duplicate, Is.True)
        Assert.That(different, Is.False)

    /// Verifies that derived Computation Quick Scan Predicate Matches Reference Types.
    [<Test>]
    member _.DerivedComputationQuickScanPredicateMatchesReferenceTypes() =
        Assert.That(DerivedComputation.shouldRecordQuickScan ReferenceType.Commit, Is.True)
        Assert.That(DerivedComputation.shouldRecordQuickScan ReferenceType.Checkpoint, Is.True)
        Assert.That(DerivedComputation.shouldRecordQuickScan ReferenceType.Promotion, Is.True)
        Assert.That(DerivedComputation.shouldRecordQuickScan ReferenceType.Save, Is.False)

    /// Verifies that policy Acknowledge Rejects Missing Snapshot Id.
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

    /// Verifies that policy Acknowledge Rejects Invalid Branch Id.
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
