namespace Grace.Server.Tests

open Grace.Actors.Branch
open Grace.Shared.Parameters.Branch
open Grace.Shared.Validation.Errors
open Grace.Types.Branch
open Grace.Types.Common
open Grace.Types.Reference
open NUnit.Framework
open System

/// Covers branch Server Validation behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type BranchServerValidationTests() =

    /// Constructs parameters fixtures used by the server unit branch assertions.
    let createParameters directoryVersionId sha256Hash blake3Hash =
        let parameters = CreateReferenceParameters()
        parameters.DirectoryVersionId <- directoryVersionId
        parameters.Sha256Hash <- sha256Hash
        parameters.Blake3Hash <- blake3Hash
        parameters.Message <- "reference message"
        parameters

    /// Verifies that reference root locator validation rejects empty locator before command resolution.
    [<Test>]
    member _.``reference root locator validation rejects empty locator before command resolution``() =
        let parameters = createParameters DirectoryVersionId.Empty (Sha256Hash String.Empty) (Blake3Hash String.Empty)

        let result =
            (Grace.Server.Branch.validateReferenceRootLocator parameters)
                .Result

        match result with
        | Ok _ -> Assert.Fail("Expected an empty reference root locator to be rejected.")
        | Error error -> Assert.That(error, Is.EqualTo(BranchError.EitherDirectoryVersionIdOrSha256HashRequired))

    /// Verifies that reference root locator validation accepts Blake3-only locator.
    [<Test>]
    member _.``reference root locator validation accepts Blake3-only locator``() =
        let parameters =
            createParameters DirectoryVersionId.Empty (Sha256Hash String.Empty) (Blake3Hash "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adcd1e8c76d9a8885f16a39f")

        let result =
            (Grace.Server.Branch.validateReferenceRootLocator parameters)
                .Result

        Assert.That(Result.isOk result, Is.True)

    /// Verifies that reference root locator validation accepts legacy directory version id locator.
    [<Test>]
    member _.``reference root locator validation accepts legacy directory version id locator``() =
        let parameters = createParameters (Guid.NewGuid()) (Sha256Hash String.Empty) (Blake3Hash String.Empty)

        let result =
            (Grace.Server.Branch.validateReferenceRootLocator parameters)
                .Result

        Assert.That(Result.isOk result, Is.True)

    /// Verifies Commit rejects a missing stable Reference identity before command dispatch.
    [<Test>]
    member _.``commit validation requires a stable reference id``() =
        let parameters = CommitReferenceParameters()

        let missingResult =
            (Grace.Server.Branch.validateCommitReferenceId parameters)
                .Result

        parameters.ReferenceId <- Guid.NewGuid()

        let presentResult =
            (Grace.Server.Branch.validateCommitReferenceId parameters)
                .Result

        match missingResult with
        | Error error -> Assert.That(error, Is.EqualTo(BranchError.InvalidReferenceId))
        | Ok _ -> Assert.Fail("Expected an empty Commit ReferenceId to be rejected.")

        Assert.That(Result.isOk presentResult, Is.True)

/// Covers Branch aggregate retry decisions for caller-owned Commit identities.
[<Parallelizable(ParallelScope.All)>]
type BranchActorCommitRetryTests() =

    let ownerId = Guid.Parse("11111111-7291-4000-8000-111111111111")
    let organizationId = Guid.Parse("22222222-7291-4000-8000-222222222222")
    let repositoryId = Guid.Parse("33333333-7291-4000-8000-333333333333")
    let branchId = Guid.Parse("44444444-7291-4000-8000-444444444444")
    let referenceId = Guid.Parse("55555555-7291-4000-8000-555555555555")
    let directoryVersionId = Guid.Parse("66666666-7291-4000-8000-666666666666")
    let sha256Hash = Sha256Hash "commit-sha256"
    let blake3Hash = Blake3Hash "commit-blake3"
    let referenceText = ReferenceText "idempotent commit"

    /// Builds the durable Commit Reference produced before an HTTP outcome becomes unknown.
    let persistedCommit () =
        { ReferenceDto.Default with
            ReferenceId = referenceId
            OwnerId = ownerId
            OrganizationId = organizationId
            RepositoryId = repositoryId
            BranchId = branchId
            DirectoryId = directoryVersionId
            Sha256Hash = sha256Hash
            Blake3Hash = blake3Hash
            ReferenceType = ReferenceType.Commit
            ReferenceText = referenceText
            UpdatedAt = Some(Grace.Shared.Utilities.getCurrentInstant ())
        }

    /// Builds the Branch Commit command that owns the stable Reference identity.
    let commitCommand (text: string) = BranchCommand.Commit(referenceId, directoryVersionId, sha256Hash, blake3Hash, ReferenceText text)

    /// Verifies an exact retry with the original correlation bypasses duplicate-correlation rejection and emits no second transition.
    [<Test>]
    member _.SameCorrelationRetryAfterUnknownOutcomeReusesCommitWithoutSecondEvent() =
        let disposition = classifyCommitReference ownerId organizationId repositoryId branchId (commitCommand (string referenceText)) (Some(persistedCommit ()))

        Assert.That(disposition, Is.EqualTo(CommitReferenceDisposition.MatchingRetry))
        Assert.That(shouldRejectDuplicateCorrelation true disposition, Is.False)
        Assert.That(shouldApplyCommitEvent disposition, Is.False)

        let committedEventCount =
            1
            + (if shouldApplyCommitEvent disposition then 1 else 0)

        Assert.That(committedEventCount, Is.EqualTo(1), "The retry must not append a second Committed event.")

    /// Verifies an exact retry with a fresh correlation republishes the Reference but emits no second Branch transition.
    [<Test>]
    member _.FreshCorrelationRetryAfterUnknownOutcomeReusesCommitWithoutSecondEvent() =
        let disposition = classifyCommitReference ownerId organizationId repositoryId branchId (commitCommand (string referenceText)) (Some(persistedCommit ()))

        Assert.That(disposition, Is.EqualTo(CommitReferenceDisposition.MatchingRetry))
        Assert.That(shouldRejectDuplicateCorrelation false disposition, Is.False)
        Assert.That(shouldApplyCommitEvent disposition, Is.False)

        let committedEventCount =
            1
            + (if shouldApplyCommitEvent disposition then 1 else 0)

        Assert.That(committedEventCount, Is.EqualTo(1), "The retry must not append a second Committed event.")

    /// Verifies stable Reference reuse with different Commit data remains rejected.
    [<Test>]
    member _.SameReferenceIdWithDifferentCommitDataIsRejected() =
        let disposition = classifyCommitReference ownerId organizationId repositoryId branchId (commitCommand "different commit") (Some(persistedCommit ()))

        Assert.That(disposition, Is.EqualTo(CommitReferenceDisposition.ConflictingReference))
        Assert.That(shouldApplyCommitEvent disposition, Is.False)

    /// Verifies duplicate correlation handling remains strict for a new Commit identity.
    [<Test>]
    member _.NewCommitStillRejectsDuplicateCorrelation() =
        let disposition = classifyCommitReference ownerId organizationId repositoryId branchId (commitCommand (string referenceText)) None

        Assert.That(disposition, Is.EqualTo(CommitReferenceDisposition.NewCommit))
        Assert.That(shouldRejectDuplicateCorrelation true disposition, Is.True)
        Assert.That(shouldApplyCommitEvent disposition, Is.True)
