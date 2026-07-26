namespace Grace.Server.Tests

open Grace.Actors.ValidationResult
open Grace.Server
open Grace.Shared.Parameters.Policy
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.Common
open Grace.Types.Events
open Grace.Types.Reference
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
        { Timestamp = timestamp
          CorrelationId = correlationId
          Principal = "tester"
          ClientType = Microsoft.FSharp.Core.Option.None
          Properties = Dictionary<string, string>() }

    let ownerId = Guid.Parse("11111111-7291-4000-8000-111111111111")
    let organizationId = Guid.Parse("22222222-7291-4000-8000-222222222222")
    let repositoryId = Guid.Parse("33333333-7291-4000-8000-333333333333")
    let branchId = Guid.Parse("44444444-7291-4000-8000-444444444444")
    let referenceId = Guid.Parse("55555555-7291-4000-8000-555555555555")
    let directoryVersionId = Guid.Parse("66666666-7291-4000-8000-666666666666")
    let createdAt = Instant.FromUtc(2026, 7, 24, 12, 34)

    /// Builds the persisted Commit Created event consumed by quick-scan derivation.
    let commitCreatedEvent () : ReferenceEvent =
        { Event =
            ReferenceEventType.Created(
                referenceId,
                ownerId,
                organizationId,
                repositoryId,
                branchId,
                directoryVersionId,
                Sha256Hash "commit-sha256",
                Blake3Hash "commit-blake3",
                ReferenceType.Commit,
                ReferenceText "quick-scan replay",
                []
            )
          Metadata = metadata "quick-scan-correlation" createdAt }

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

    /// Verifies correlation replay rejection takes precedence over matching or conflicting deterministic identity data.
    [<Test>]
    member _.ValidationResultRecordAttemptClassifiesCorrelationReplayBeforeIdentityData() =
        let incoming = DerivedComputation.buildQuickScanValidationResult (commitCreatedEvent ())

        let recordedEvent = { Event = ValidationResultEventType.Recorded incoming; Metadata = metadata "persisted-correlation" createdAt }

        let stored = durableRecordForComparison [ recordedEvent ] ValidationResultDto.Default
        let retry = { incoming with CreatedAt = createdAt + Duration.FromSeconds(1L) }
        let conflicting = { retry with ValidationVersion = "conflicting-version" }

        let duplicateMatching = classifyRecordAttempt [ recordedEvent ] stored retry (metadata "persisted-correlation" createdAt)

        let duplicateConflicting = classifyRecordAttempt [ recordedEvent ] stored conflicting (metadata "persisted-correlation" createdAt)

        let freshMatching = classifyRecordAttempt [ recordedEvent ] stored retry (metadata "fresh-matching-correlation" createdAt)

        let freshConflicting = classifyRecordAttempt [ recordedEvent ] stored conflicting (metadata "fresh-conflicting-correlation" createdAt)

        Assert.That(duplicateMatching, Is.EqualTo(ValidationResultRecordDisposition.DuplicateCorrelationReplay))
        Assert.That(duplicateConflicting, Is.EqualTo(ValidationResultRecordDisposition.DuplicateCorrelationReplay))
        Assert.That(freshMatching, Is.EqualTo(ValidationResultRecordDisposition.MatchingResult))
        Assert.That(freshConflicting, Is.EqualTo(ValidationResultRecordDisposition.ConflictingResult))

    /// Verifies that derived Computation Quick Scan Predicate Matches Reference Types.
    [<Test>]
    member _.DerivedComputationQuickScanPredicateMatchesReferenceTypes() =
        Assert.That(DerivedComputation.shouldRecordQuickScan ReferenceType.Commit, Is.True)
        Assert.That(DerivedComputation.shouldRecordQuickScan ReferenceType.Checkpoint, Is.True)
        Assert.That(DerivedComputation.shouldRecordQuickScan ReferenceType.Promotion, Is.True)
        Assert.That(DerivedComputation.shouldRecordQuickScan ReferenceType.Save, Is.False)

    /// Verifies quick-scan identity is versioned, repeatable, and scoped by both Repository and Reference.
    [<Test>]
    member _.QuickScanValidationResultIdentityIsDeterministicAndScoped() =
        let expected = Guid.Parse("37322fc3-668f-5584-aa71-8b13c305d81c")
        let same = DerivedComputation.buildQuickScanValidationResultId repositoryId referenceId
        let otherRepository = DerivedComputation.buildQuickScanValidationResultId (Guid.Parse("77777777-7291-4000-8000-777777777777")) referenceId
        let otherReference = DerivedComputation.buildQuickScanValidationResultId repositoryId (Guid.Parse("88888888-7291-4000-8000-888888888888"))

        Assert.That(same, Is.EqualTo(expected))
        Assert.That(DerivedComputation.buildQuickScanValidationResultId repositoryId referenceId, Is.EqualTo(same))
        Assert.That(otherRepository, Is.Not.EqualTo(same))
        Assert.That(otherReference, Is.Not.EqualTo(same))
        Assert.That(same.ToString("D").Substring(14, 1), Is.EqualTo("5"))

    /// Verifies quick-scan result creation uses the persisted Created-event timestamp and deterministic identity.
    [<Test>]
    member _.QuickScanValidationResultUsesPersistedEventTimestamp() =
        let result = DerivedComputation.buildQuickScanValidationResult (commitCreatedEvent ())

        Assert.That(result.ValidationResultId, Is.EqualTo(DerivedComputation.buildQuickScanValidationResultId repositoryId referenceId))
        Assert.That(result.CreatedAt, Is.EqualTo(createdAt))
        Assert.That(result.RepositoryId, Is.EqualTo(repositoryId))
        Assert.That(result.ValidationName, Is.EqualTo("quick-scan"))
        Assert.That(result.ValidationVersion, Is.EqualTo("1.0"))

    /// Verifies replay derives the same durable quick-scan value without current Policy snapshot input.
    [<Test>]
    member _.QuickScanValidationResultIsIndependentOfCurrentPolicySnapshot() =
        let first = DerivedComputation.buildQuickScanValidationResult (commitCreatedEvent ())
        let replay = DerivedComputation.buildQuickScanValidationResult (commitCreatedEvent ())

        Assert.That(replay, Is.EqualTo(first))
        Assert.That(replay.Output.Status, Is.EqualTo(ValidationStatus.Pass))
        Assert.That(replay.Output.ArtifactIds, Is.Empty)
        Assert.That(replay.Output.Summary, Does.Not.Contain("policySnapshotId"))

    /// Verifies sequential duplicate quick-scan records add one durable result and then converge as a matching replay.
    [<Test>]
    member _.SequentialDuplicateQuickScanRecordConvergesWithoutSecondEvent() =
        let incoming = DerivedComputation.buildQuickScanValidationResult (commitCreatedEvent ())
        let firstDisposition = classifyRecord ValidationResultDto.Default incoming

        let stored =
            ValidationResultDto.UpdateDto
                { Event = ValidationResultEventType.Recorded incoming
                  Metadata = { metadata "first-record" createdAt with Principal = Grace.Shared.Constants.GraceSystemUser } }
                ValidationResultDto.Default

        let retryDisposition = classifyRecord stored incoming

        Assert.That(firstDisposition, Is.EqualTo(ValidationResultRecordDisposition.NewResult))
        Assert.That(shouldApplyRecord firstDisposition, Is.True)
        Assert.That(retryDisposition, Is.EqualTo(ValidationResultRecordDisposition.MatchingResult))
        Assert.That(shouldApplyRecord retryDisposition, Is.False)

    /// Verifies actor serialization makes racing duplicates converge after the first durable Record wins.
    [<Test>]
    member _.RacingDuplicateQuickScanRecordConvergesAfterFirstSerializedWrite() =
        let incoming = DerivedComputation.buildQuickScanValidationResult (commitCreatedEvent ())
        let firstDisposition = classifyRecord ValidationResultDto.Default incoming

        let stored =
            ValidationResultDto.UpdateDto
                { Event = ValidationResultEventType.Recorded incoming
                  Metadata = { metadata "race-winner" createdAt with Principal = Grace.Shared.Constants.GraceSystemUser } }
                ValidationResultDto.Default

        let serializedLoserDisposition = classifyRecord stored incoming

        Assert.That(shouldApplyRecord firstDisposition, Is.True)
        Assert.That(serializedLoserDisposition, Is.EqualTo(ValidationResultRecordDisposition.MatchingResult))
        Assert.That(shouldApplyRecord serializedLoserDisposition, Is.False)

    /// Verifies replay comparison uses the original Record rather than projection-added principal metadata.
    [<Test>]
    member _.ValidationResultReplayUsesOriginalPersistedRecord() =
        let incoming = { DerivedComputation.buildQuickScanValidationResult (commitCreatedEvent ()) with OnBehalfOf = [] }

        let recordedEvent =
            { Event = ValidationResultEventType.Recorded incoming
              Metadata = { metadata "record-with-principal" createdAt with Principal = "projection-principal" } }

        let projected = ValidationResultDto.UpdateDto recordedEvent ValidationResultDto.Default
        let stored = durableRecordForComparison [ recordedEvent ] projected

        Assert.That(projected.OnBehalfOf, Is.Not.EqualTo(incoming.OnBehalfOf))
        Assert.That(stored, Is.EqualTo(incoming))
        Assert.That(classifyRecord stored incoming, Is.EqualTo(ValidationResultRecordDisposition.MatchingResult))

    /// Verifies deterministic identity reuse with conflicting durable quick-scan data remains rejected.
    [<Test>]
    member _.ConflictingStoredQuickScanResultIsRejected() =
        let incoming = DerivedComputation.buildQuickScanValidationResult (commitCreatedEvent ())
        let stored = { incoming with ValidationVersion = "conflicting-version"; UpdatedAt = Some createdAt }
        let disposition = classifyRecord stored incoming

        Assert.That(disposition, Is.EqualTo(ValidationResultRecordDisposition.ConflictingResult))
        Assert.That(shouldApplyRecord disposition, Is.False)

    /// Verifies that policy Acknowledge Rejects Missing Snapshot Id.
    [<Test>]
    member _.PolicyAcknowledgeRejectsMissingSnapshotId() =
        let parameters = AcknowledgePolicyParameters(TargetBranchId = Guid.NewGuid().ToString(), PolicySnapshotId = String.Empty)

        let validations = Policy.validateAcknowledgeParameters parameters

        let error = validations |> getFirstError |> Async.AwaitTask |> Async.RunSynchronously

        Assert.That(error, Is.EqualTo(Some PolicyError.InvalidPolicySnapshotId))

    /// Verifies that policy Acknowledge Rejects Invalid Branch Id.
    [<Test>]
    member _.PolicyAcknowledgeRejectsInvalidBranchId() =
        let parameters = AcknowledgePolicyParameters(TargetBranchId = "not-a-guid", PolicySnapshotId = "snapshot")

        let validations = Policy.validateAcknowledgeParameters parameters

        let error = validations |> getFirstError |> Async.AwaitTask |> Async.RunSynchronously

        Assert.That(error, Is.EqualTo(Some PolicyError.InvalidTargetBranchId))
