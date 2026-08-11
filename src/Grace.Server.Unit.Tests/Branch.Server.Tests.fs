namespace Grace.Server.Tests

open Grace.Actors.Branch
open Grace.Shared.Parameters.Branch
open Grace.Shared.Constants
open Grace.Shared.Validation.Errors
open Grace.Types.Branch
open Grace.Types.Common
open Grace.Types.Reference
open Grace.Types.Repository
open NodaTime
open NUnit.Framework
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Simulates durable Branch state with a bounded number of definite write failures.
type private ScriptedBranchEventState(initialDurableEvents: seq<BranchEvent>, failedWrites: int, failedReads: int) =
    let mutable durableEvents = List<BranchEvent>(initialDurableEvents)
    let mutable activationEvents = List<BranchEvent>(durableEvents)
    let mutable remainingFailedWrites = failedWrites
    let mutable remainingFailedReads = failedReads
    let mutable writeCount = 0

    member _.WriteCount = writeCount

    member _.CreateReactivatedState() = ScriptedBranchEventState(durableEvents, 0, 0)

    interface IPersistentState<List<BranchEvent>> with
        member _.State
            with get () = activationEvents
            and set value = activationEvents <- value

        member _.Etag = null
        member _.RecordExists = durableEvents.Count > 0

        member _.ReadStateAsync() =
            if remainingFailedReads > 0 then
                remainingFailedReads <- remainingFailedReads - 1
                Task.FromException(InvalidOperationException("simulated Branch reload failure"))
            else
                activationEvents <- List<BranchEvent>(durableEvents)
                Task.CompletedTask

        member _.WriteStateAsync() =
            writeCount <- writeCount + 1

            if remainingFailedWrites > 0 then
                remainingFailedWrites <- remainingFailedWrites - 1
                Task.FromException(InvalidOperationException("simulated Branch persistence failure"))
            else
                durableEvents <- List<BranchEvent>(activationEvents)
                Task.CompletedTask

        member _.ClearStateAsync() =
            durableEvents.Clear()
            activationEvents.Clear()
            Task.CompletedTask

/// Simulates a Branch write that commits durably before returning an unknown failure outcome.
type private UnknownOutcomeBranchEventState(initialDurableEvents: seq<BranchEvent>) =
    let mutable durableEvents = List<BranchEvent>(initialDurableEvents)
    let mutable activationEvents = List<BranchEvent>(durableEvents)
    let mutable firstWrite = true

    member _.CreateReactivatedState() = UnknownOutcomeBranchEventState(durableEvents)

    interface IPersistentState<List<BranchEvent>> with
        member _.State
            with get () = activationEvents
            and set value = activationEvents <- value

        member _.Etag = null
        member _.RecordExists = durableEvents.Count > 0

        member _.ReadStateAsync() =
            activationEvents <- List<BranchEvent>(durableEvents)
            Task.CompletedTask

        member _.WriteStateAsync() =
            durableEvents <- List<BranchEvent>(activationEvents)

            if firstWrite then
                firstWrite <- false
                Task.FromException(InvalidOperationException("simulated unknown Branch write outcome"))
            else
                Task.CompletedTask

        member _.ClearStateAsync() =
            durableEvents.Clear()
            activationEvents.Clear()
            Task.CompletedTask

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

    /// Verifies every public Reference producer uses the same missing-identity validation result.
    [<Test>]
    member _.``all reference producer parameters require a stable reference id``() =
        let parameterIdentities =
            [|
                CreateBranchParameters().ReferenceId
                AssignParameters().ReferenceId
                RebaseParameters().ReferenceId
                CreateReferenceParameters().ReferenceId
                CommitReferenceParameters().ReferenceId
            |]

        for parameterIdentity in parameterIdentities do
            match (Grace.Server.Branch.validateReferenceId parameterIdentity)
                .Result
                with
            | Error error -> Assert.That(error, Is.EqualTo(BranchError.InvalidReferenceId))
            | Ok _ -> Assert.Fail("Expected an empty ReferenceId to be rejected for every producer parameter family.")

        Assert.That(
            (Grace.Server.Branch.validateReferenceId (Guid.NewGuid()))
                .Result
            |> Result.isOk,
            Is.True
        )

/// Covers deterministic identities used by durable orchestration producers.
[<Parallelizable(ParallelScope.All)>]
type ReferenceProducerIdentityTests() =

    /// Verifies repository initialization derives stable, role-separated child identities.
    [<Test>]
    member _.RepositoryInitialWorkflowIdentitiesAreStableAndRoleSeparated() =
        let repositoryId = Guid.Parse("11111111-7300-4000-8000-111111111111")
        let branchId = Grace.Actors.Repository.buildInitialWorkflowId "branch" repositoryId

        Assert.That(Grace.Actors.Repository.buildInitialWorkflowId "branch" repositoryId, Is.EqualTo(branchId))
        Assert.That(Grace.Actors.Repository.buildInitialWorkflowId "promotion-reference" repositoryId, Is.Not.EqualTo(branchId))
        Assert.That(Grace.Actors.Repository.buildInitialWorkflowId "rebase-reference" repositoryId, Is.Not.EqualTo(branchId))
        Assert.That(Grace.Actors.Repository.buildInitialWorkflowId "directory-version" repositoryId, Is.Not.EqualTo(branchId))

    /// Verifies Repository Create replay uses immutable creation facts after mutable Repository settings change.
    [<Test>]
    member _.RepositoryCreateRetryUsesImmutableCreationFacts() =
        let repositoryId = Guid.Parse("22222222-7300-4000-8000-222222222222")
        let ownerId = Guid.Parse("33333333-7300-4000-8000-333333333333")
        let organizationId = Guid.Parse("44444444-7300-4000-8000-444444444444")
        let repositoryName = RepositoryName "stable-reference-retry"
        let provider = ObjectStorageProvider.AzureBlobStorage

        let created = RepositoryEventType.Created(repositoryName, repositoryId, ownerId, organizationId, provider)

        let historyAfterMutation =
            [
                created
                RepositoryEventType.NameSet(RepositoryName "renamed-after-create")
                RepositoryEventType.ObjectStorageProviderSet ObjectStorageProvider.AWSS3
            ]

        let exact = RepositoryCommand.Create(repositoryName, repositoryId, ownerId, organizationId, provider)
        let renamed = RepositoryCommand.Create(RepositoryName "renamed-before-retry", repositoryId, ownerId, organizationId, provider)
        let changedProvider = RepositoryCommand.Create(repositoryName, repositoryId, ownerId, organizationId, ObjectStorageProvider.AWSS3)

        Assert.That(Grace.Actors.Repository.createCommandMatchesCreationEvent created exact, Is.True)
        Assert.That(Grace.Actors.Repository.createCommandMatchesCreationHistory historyAfterMutation exact, Is.True)
        Assert.That(Grace.Actors.Repository.createCommandMatchesCreationHistory historyAfterMutation renamed, Is.False)
        Assert.That(Grace.Actors.Repository.createCommandMatchesCreationHistory historyAfterMutation changedProvider, Is.False)

    /// Verifies Repository bootstrap accepts a persisted exact empty root while rejecting conflicting deterministic reuse.
    [<Test>]
    member _.RepositoryInitialDirectoryRetryRequiresExactImmutableData() =
        let directoryId = Guid.Parse("55555555-7300-4000-8000-555555555555")

        let expected =
            Grace.Types.Common.DirectoryVersion.CreateWithHashes
                directoryId
                (Guid.Parse("66666666-7300-4000-8000-666666666666"))
                (Guid.Parse("77777777-7300-4000-8000-777777777777"))
                (Guid.Parse("88888888-7300-4000-8000-888888888888"))
                RootDirectoryPath
                (Sha256Hash "empty-sha")
                (Blake3Hash "empty-blake3")
                (Collections.Generic.List<DirectoryVersionId>())
                (Collections.Generic.List<FileVersion>())
                0L

        let persisted =
            Grace.Types.Common.DirectoryVersion.CreateWithHashes
                expected.DirectoryVersionId
                expected.OwnerId
                expected.OrganizationId
                expected.RepositoryId
                expected.RelativePath
                expected.Sha256Hash
                expected.Blake3Hash
                (Collections.Generic.List<DirectoryVersionId>())
                (Collections.Generic.List<FileVersion>())
                expected.Size

        Assert.That(Grace.Actors.Repository.initialDirectoryMatches expected persisted, Is.True)

        persisted.Blake3Hash <- Blake3Hash "conflicting"
        Assert.That(Grace.Actors.Repository.initialDirectoryMatches expected persisted, Is.False)

/// Covers Branch aggregate retry decisions for caller-owned Reference identities.
[<Parallelizable(ParallelScope.All)>]
type BranchActorReferenceRetryTests() =

    let ownerId = Guid.Parse("11111111-7291-4000-8000-111111111111")
    let organizationId = Guid.Parse("22222222-7291-4000-8000-222222222222")
    let repositoryId = Guid.Parse("33333333-7291-4000-8000-333333333333")
    let branchId = Guid.Parse("44444444-7291-4000-8000-444444444444")
    let referenceId = Guid.Parse("55555555-7291-4000-8000-555555555555")
    let directoryVersionId = Guid.Parse("66666666-7291-4000-8000-666666666666")
    let sha256Hash = Sha256Hash "commit-sha256"
    let blake3Hash = Blake3Hash "commit-blake3"
    let referenceText = ReferenceText "idempotent commit"
    let earlier = Instant.FromUtc(2026, 7, 24, 12, 0)
    let later = Instant.FromUtc(2026, 7, 24, 12, 1)

    /// Builds the durable Commit Reference produced before an HTTP outcome becomes unknown.
    let persistedCommitAt referenceIdentity timestamp =
        { ReferenceDto.Default with
            ReferenceId = referenceIdentity
            OwnerId = ownerId
            OrganizationId = organizationId
            RepositoryId = repositoryId
            BranchId = branchId
            DirectoryId = directoryVersionId
            Sha256Hash = sha256Hash
            Blake3Hash = blake3Hash
            ReferenceType = ReferenceType.Commit
            ReferenceText = referenceText
            UpdatedAt = Some timestamp
        }

    /// Builds the durable Commit Reference produced before an HTTP outcome becomes unknown.
    let persistedCommit () = persistedCommitAt referenceId earlier

    /// Builds a Branch projection with explicit latest Reference slots.
    let branchProjection latestReference latestCommit =
        { BranchDto.Default with LatestReference = latestReference; LatestCommit = latestCommit; ShouldRecomputeLatestReferences = false }

    /// Builds the Reference Create command used by the Commit witness.
    let referenceCommand (text: string) =
        ReferenceCommand.Create(
            referenceId,
            ownerId,
            organizationId,
            repositoryId,
            branchId,
            directoryVersionId,
            sha256Hash,
            blake3Hash,
            ReferenceType.Commit,
            ReferenceText text,
            List.empty
        )

    /// Verifies an exact retry with the original correlation repairs an absent projection without a durable Branch transition.
    [<Test>]
    member _.SameCorrelationRetryAfterUnknownOutcomeRecoversProjectionOnly() =
        let durableCommit = persistedCommit ()
        let disposition = classifyReferenceOperation (referenceCommand (string referenceText)) (Some durableCommit)
        let recovered, changed = reconcileReferenceProjection (branchProjection ReferenceDto.Default ReferenceDto.Default) durableCommit

        Assert.That(disposition, Is.EqualTo(ReferenceOperationDisposition.MatchingRetry))
        Assert.That(shouldRejectDuplicateCorrelation true disposition, Is.False)
        Assert.That(shouldApplyReferenceEvent disposition, Is.False)
        Assert.That(changed, Is.True)
        Assert.That(recovered.LatestCommit, Is.EqualTo(durableCommit))
        Assert.That(recovered.LatestReference, Is.EqualTo(durableCommit))
        Assert.That(recovered.ShouldRecomputeLatestReferences, Is.True)
        Assert.That(shouldPublishBranchEvent (Committed(durableCommit, directoryVersionId, sha256Hash, blake3Hash, referenceText)), Is.False)

    /// Verifies eligible Watch transitions persist in Branch order without duplicating Reference-owned publication.
    [<Test>]
    member _.EligibleWatchTransitionsPersistWithoutBranchPublication() =
        let durableCommit = persistedCommit ()
        let committed = Committed(durableCommit, directoryVersionId, sha256Hash, blake3Hash, referenceText)
        let checkpointed = Checkpointed(durableCommit, directoryVersionId, sha256Hash, blake3Hash, referenceText)
        let saved = Saved(durableCommit, directoryVersionId, sha256Hash, blake3Hash, referenceText)

        for branchEventType in [ committed; checkpointed; saved ] do
            Assert.That(shouldPersistBranchEvent branchEventType, Is.True)
            Assert.That(shouldPublishBranchEvent branchEventType, Is.False)

    /// Verifies an exact retry repairs a missing eligible replay event once without taking over Reference publication.
    [<Test>]
    member _.MatchingRetryRepairsMissingWatchTransitionExactlyOnce() =
        let durableCommit = persistedCommit ()
        let committed = Committed(durableCommit, directoryVersionId, sha256Hash, blake3Hash, referenceText)
        let persistedEvent = ({ Event = committed; Metadata = EventMetadata.New "persisted-commit" GraceSystemUser }: BranchEvent)

        Assert.That(shouldRepairMatchingRetryBranchEvent ReferenceOperationDisposition.MatchingRetry committed Seq.empty, Is.True)

        Assert.That(
            shouldRepairMatchingRetryBranchEvent ReferenceOperationDisposition.MatchingRetry committed [ persistedEvent ],
            Is.False,
            "A retry after the Branch event was persisted must not append a duplicate."
        )

        Assert.That(shouldRepairMatchingRetryBranchEvent ReferenceOperationDisposition.NewReference committed Seq.empty, Is.False)
        Assert.That(shouldPublishBranchEvent committed, Is.False, "The repair must never duplicate Reference-owned publication.")

    /// A failed repair write reloads durable evidence, retries once, and remains exactly once after reactivation.
    [<Test>]
    member _.MatchingRetryRepairRequiresDurableWriteAndSurvivesReactivation() =
        task {
            let durableCommit = persistedCommit ()
            let committed = Committed(durableCommit, directoryVersionId, sha256Hash, blake3Hash, referenceText)
            let branchEvent = ({ Event = committed; Metadata = EventMetadata.New "repair-write" GraceSystemUser }: BranchEvent)
            let state = ScriptedBranchEventState(Seq.empty, 1, 0)
            let persistentState = state :> IPersistentState<List<BranchEvent>>

            match! persistBranchEventWithDurableRecovery persistentState branchEvent with
            | BranchEventPersistenceOutcome.Persisted -> Assert.Fail("The forced first write must not report durable success.")
            | BranchEventPersistenceOutcome.FailedRecovered error -> Assert.That(error.Message, Is.EqualTo("simulated Branch persistence failure"))
            | BranchEventPersistenceOutcome.FailedUnrecoverable _ -> Assert.Fail("The durable reload should succeed in this witness.")

            Assert.That(persistentState.State, Is.Empty, "Failed activation-only mutation must be replaced by durable state.")
            Assert.That(shouldRepairMatchingRetryBranchEvent ReferenceOperationDisposition.MatchingRetry committed persistentState.State, Is.True)

            match! persistBranchEventWithDurableRecovery persistentState branchEvent with
            | BranchEventPersistenceOutcome.Persisted -> ()
            | outcome -> Assert.Fail($"The eventual successful write returned {outcome}.")

            Assert.That(state.WriteCount, Is.EqualTo(2))
            Assert.That(persistentState.State, Has.Count.EqualTo(1))
            Assert.That(shouldRepairMatchingRetryBranchEvent ReferenceOperationDisposition.MatchingRetry committed persistentState.State, Is.False)

            let reactivated = state.CreateReactivatedState() :> IPersistentState<List<BranchEvent>>
            Assert.That(reactivated.State, Has.Count.EqualTo(1))
            Assert.That(shouldRepairMatchingRetryBranchEvent ReferenceOperationDisposition.MatchingRetry committed reactivated.State, Is.False)
            Assert.That(shouldPublishBranchEvent committed, Is.False)
        }

    /// A failed write plus failed durable reload is never admitted as success and requires actor deactivation.
    [<Test>]
    member _.MatchingRetryRepairReloadFailureRequiresDeactivation() =
        task {
            let durableCommit = persistedCommit ()
            let committed = Committed(durableCommit, directoryVersionId, sha256Hash, blake3Hash, referenceText)
            let branchEvent = ({ Event = committed; Metadata = EventMetadata.New "repair-reload" GraceSystemUser }: BranchEvent)
            let state = ScriptedBranchEventState(Seq.empty, 1, 1) :> IPersistentState<List<BranchEvent>>

            match! persistBranchEventWithDurableRecovery state branchEvent with
            | BranchEventPersistenceOutcome.FailedUnrecoverable (writeError, reloadError) as outcome ->
                Assert.That(writeError.Message, Is.EqualTo("simulated Branch persistence failure"))
                Assert.That(reloadError.Message, Is.EqualTo("simulated Branch reload failure"))
                Assert.That(branchPersistenceRequiresDeactivation outcome, Is.True)
            | outcome -> Assert.Fail($"Expected an unrecoverable persistence outcome, got {outcome}.")
        }

    /// An unknown write outcome reloads the committed event and prevents an exact retry from appending it again.
    [<Test>]
    member _.MatchingRetryUnknownWriteOutcomeUsesReloadedDurableEvidence() =
        task {
            let durableCommit = persistedCommit ()
            let committed = Committed(durableCommit, directoryVersionId, sha256Hash, blake3Hash, referenceText)
            let branchEvent = ({ Event = committed; Metadata = EventMetadata.New "repair-unknown" GraceSystemUser }: BranchEvent)
            let scriptedState = UnknownOutcomeBranchEventState(Seq.empty)
            let state = scriptedState :> IPersistentState<List<BranchEvent>>

            match! persistBranchEventWithDurableRecovery state branchEvent with
            | BranchEventPersistenceOutcome.FailedRecovered error -> Assert.That(error.Message, Is.EqualTo("simulated unknown Branch write outcome"))
            | outcome -> Assert.Fail($"Expected a recovered unknown write outcome, got {outcome}.")

            Assert.That(state.State, Has.Count.EqualTo(1))
            Assert.That(shouldRepairMatchingRetryBranchEvent ReferenceOperationDisposition.MatchingRetry committed state.State, Is.False)

            let reactivated = scriptedState.CreateReactivatedState() :> IPersistentState<List<BranchEvent>>
            Assert.That(reactivated.State, Has.Count.EqualTo(1))
            Assert.That(shouldRepairMatchingRetryBranchEvent ReferenceOperationDisposition.MatchingRetry committed reactivated.State, Is.False)
        }

    /// Verifies a fresh-correlation retry against a completed projection is a projection no-op.
    [<Test>]
    member _.FreshCorrelationRetryAfterCompletedCommitIsProjectionNoOp() =
        let durableCommit = persistedCommit ()
        let disposition = classifyReferenceOperation (referenceCommand (string referenceText)) (Some durableCommit)
        let current = branchProjection durableCommit durableCommit
        let recovered, changed = reconcileReferenceProjection current durableCommit

        Assert.That(disposition, Is.EqualTo(ReferenceOperationDisposition.MatchingRetry))
        Assert.That(shouldRejectDuplicateCorrelation false disposition, Is.False)
        Assert.That(shouldApplyReferenceEvent disposition, Is.False)
        Assert.That(changed, Is.False)
        Assert.That(recovered, Is.EqualTo(current))

    /// Verifies a fresh-correlation retry advances stale projection slots from the newer durable Commit.
    [<Test>]
    member _.FreshCorrelationRetryRecoversStaleProjection() =
        let durableCommit = persistedCommitAt referenceId later
        let staleCommit = persistedCommitAt (Guid.Parse("77777777-7291-4000-8000-777777777777")) earlier
        let disposition = classifyReferenceOperation (referenceCommand (string referenceText)) (Some durableCommit)
        let recovered, changed = reconcileReferenceProjection (branchProjection staleCommit staleCommit) durableCommit

        Assert.That(disposition, Is.EqualTo(ReferenceOperationDisposition.MatchingRetry))
        Assert.That(shouldRejectDuplicateCorrelation false disposition, Is.False)
        Assert.That(changed, Is.True)
        Assert.That(recovered.LatestCommit, Is.EqualTo(durableCommit))
        Assert.That(recovered.LatestReference, Is.EqualTo(durableCommit))
        Assert.That(recovered.ShouldRecomputeLatestReferences, Is.True)

    /// Verifies a late retry cannot replace newer latest Commit or Reference projections.
    [<Test>]
    member _.LateOlderRetryPreservesNewerProjection() =
        let olderCommit = persistedCommit ()
        let newerCommit = persistedCommitAt (Guid.Parse("77777777-7291-4000-8000-777777777777")) later
        let current = branchProjection newerCommit newerCommit
        let recovered, changed = reconcileReferenceProjection current olderCommit

        Assert.That(changed, Is.False)
        Assert.That(recovered.LatestCommit, Is.EqualTo(newerCommit))
        Assert.That(recovered.LatestReference, Is.EqualTo(newerCommit))
        Assert.That(recovered.ShouldRecomputeLatestReferences, Is.False)

    /// Verifies stable Reference reuse with different Commit data remains rejected.
    [<Test>]
    member _.SameReferenceIdWithDifferentCommitDataIsRejected() =
        let disposition = classifyReferenceOperation (referenceCommand "different commit") (Some(persistedCommit ()))

        Assert.That(disposition, Is.EqualTo(ReferenceOperationDisposition.ConflictingReference))
        Assert.That(shouldApplyReferenceEvent disposition, Is.False)

    /// Verifies duplicate correlation handling remains strict for a new Commit identity.
    [<Test>]
    member _.NewReferenceStillRejectsDuplicateCorrelation() =
        let disposition = classifyReferenceOperation (referenceCommand (string referenceText)) None

        Assert.That(disposition, Is.EqualTo(ReferenceOperationDisposition.NewReference))
        Assert.That(shouldRejectDuplicateCorrelation true disposition, Is.True)
        Assert.That(shouldApplyReferenceEvent disposition, Is.True)

    /// Verifies operation classification never uses ReferenceType as a reliability category.
    [<Test>]
    member _.ReferenceOperationClassificationIsTypeNeutral() =
        let referenceTypes =
            [
                ReferenceType.Promotion
                ReferenceType.Commit
                ReferenceType.Checkpoint
                ReferenceType.Save
                ReferenceType.Tag
                ReferenceType.External
                ReferenceType.Rebase
            ]

        for referenceType in referenceTypes do
            let stored =
                { ReferenceDto.Default with
                    ReferenceId = referenceId
                    OwnerId = ownerId
                    OrganizationId = organizationId
                    RepositoryId = repositoryId
                    BranchId = branchId
                    DirectoryId = directoryVersionId
                    Sha256Hash = sha256Hash
                    Blake3Hash = blake3Hash
                    ReferenceType = referenceType
                    ReferenceText = referenceText
                    UpdatedAt = Some earlier
                }

            let command =
                ReferenceCommand.Create(
                    referenceId,
                    ownerId,
                    organizationId,
                    repositoryId,
                    branchId,
                    directoryVersionId,
                    sha256Hash,
                    blake3Hash,
                    referenceType,
                    referenceText,
                    List.empty
                )

            Assert.That(
                classifyReferenceOperation command (Some stored),
                Is.EqualTo(ReferenceOperationDisposition.MatchingRetry),
                $"Expected {referenceType} to use the same matching-retry rule."
            )

            Assert.That(
                classifyReferenceOperation command None,
                Is.EqualTo(ReferenceOperationDisposition.NewReference),
                $"Expected {referenceType} to use the same new-operation rule."
            )

    /// Verifies a Rebase projection event cannot replace the caller-owned operation Reference identity in response metadata.
    [<Test>]
    member _.RebaseResponseMetadataPreservesOperationReferenceId() =
        let operationReferenceId = Guid.Parse("77777777-7300-4000-8000-777777777777")
        let basedOnReferenceId = Guid.Parse("88888888-7300-4000-8000-888888888888")
        let properties = Dictionary<string, string>()
        properties[nameof ReferenceId] <- $"{operationReferenceId}"

        applyReferenceIdMetadata properties basedOnReferenceId

        Assert.That(properties[nameof ReferenceId], Is.EqualTo($"{operationReferenceId}"))

        let initiallyEmptyProperties = Dictionary<string, string>()
        applyReferenceIdMetadata initiallyEmptyProperties basedOnReferenceId

        Assert.That(initiallyEmptyProperties[nameof ReferenceId], Is.EqualTo($"{basedOnReferenceId}"))

    /// Verifies historical Promotions remain reconstruction inputs after Assign and Promotion are both disabled.
    [<Test>]
    member _.DisabledPromotionPermissionsStillReconstructHistoricalPromotion() =
        let assignOnly = { BranchDto.Default with AssignEnabled = true }
        let promotionOnly = { BranchDto.Default with PromotionEnabled = true }
        let both = { assignOnly with PromotionEnabled = true }
        let bothDisabled = BranchDto.Default

        for branch in
            [
                assignOnly
                promotionOnly
                both
                bothDisabled
            ] do
            Assert.That(
                projectionReconstructionReferenceTypes branch,
                Does.Contain(ReferenceType.Promotion),
                "Durable ordinary Promotions must remain visible to activation reconstruction after permissions change."
            )

    /// Verifies an exact Rebase retry repairs both latest Reference and BasedOn projections.
    [<Test>]
    member _.ExactRebaseRetryRecoversProjectionWithoutReplacingANewerBase() =
        let basedOnReference = { persistedCommitAt (Guid.Parse("88888888-7291-4000-8000-888888888888")) earlier with ReferenceType = ReferenceType.Promotion }

        let rebaseReference =
            { persistedCommitAt referenceId earlier with
                ReferenceType = ReferenceType.Rebase
                Links =
                    [
                        ReferenceLinkType.BasedOn basedOnReference.ReferenceId
                    ]
            }

        let retryCommand =
            ReferenceCommand.Create(
                rebaseReference.ReferenceId,
                rebaseReference.OwnerId,
                rebaseReference.OrganizationId,
                rebaseReference.RepositoryId,
                rebaseReference.BranchId,
                rebaseReference.DirectoryId,
                rebaseReference.Sha256Hash,
                rebaseReference.Blake3Hash,
                ReferenceType.Rebase,
                rebaseReference.ReferenceText,
                rebaseReference.Links
            )

        let retryDisposition = classifyReferenceOperation retryCommand (Some rebaseReference)
        let recovered, changed = reconcileRebaseProjection (branchProjection ReferenceDto.Default ReferenceDto.Default) rebaseReference basedOnReference

        Assert.That(retryDisposition, Is.EqualTo(ReferenceOperationDisposition.MatchingRetry))
        Assert.That(shouldApplyReferenceEvent retryDisposition, Is.False, "An accepted-but-response-lost retry must not add another durable Rebase transition.")
        Assert.That(changed, Is.True)
        Assert.That(recovered.LatestReference, Is.EqualTo(rebaseReference))
        Assert.That(recovered.BasedOn, Is.EqualTo(basedOnReference))

        let newerReference = persistedCommitAt (Guid.Parse("99999999-7291-4000-8000-999999999999")) later
        let current = { branchProjection newerReference ReferenceDto.Default with BasedOn = newerReference }
        let lateRecovery, lateChanged = reconcileRebaseProjection current rebaseReference basedOnReference

        Assert.That(lateChanged, Is.False)
        Assert.That(lateRecovery.BasedOn, Is.EqualTo(newerReference))

    /// Verifies failed and nonterminal PromotionSet References cannot affect the public Branch projection.
    [<Test>]
    member _.PromotionSetProjectionRequiresSucceededTerminalOutput() =
        let promotionSetId = Guid.Parse("aaaaaaa1-7300-4000-8000-aaaaaaaaaaa1")

        let ordinaryPromotion = { persistedCommitAt (Guid.Parse("bbbbbbb1-7300-4000-8000-bbbbbbbbbbb1")) earlier with ReferenceType = ReferenceType.Promotion }

        let nonterminalPromotion =
            { ordinaryPromotion with
                ReferenceId = Guid.Parse("ccccccc1-7300-4000-8000-ccccccccccc1")
                Links =
                    [
                        ReferenceLinkType.IncludedInPromotionSet promotionSetId
                    ]
            }

        let terminalPromotion =
            { nonterminalPromotion with
                ReferenceId = Guid.Parse("ddddddd1-7300-4000-8000-ddddddddddd1")
                Links =
                    [
                        ReferenceLinkType.IncludedInPromotionSet promotionSetId
                        ReferenceLinkType.PromotionSetTerminal promotionSetId
                    ]
            }

        Assert.That(canProjectPromotionReference Option.None Option.None ordinaryPromotion, Is.True)

        Assert.That(
            canProjectPromotionReference
                (Some Grace.Types.PromotionSet.PromotionSetStatus.Succeeded)
                (Some nonterminalPromotion.ReferenceId)
                nonterminalPromotion,
            Is.False
        )

        Assert.That(
            canProjectPromotionReference (Some Grace.Types.PromotionSet.PromotionSetStatus.Failed) (Some terminalPromotion.ReferenceId) terminalPromotion,
            Is.False
        )

        Assert.That(
            canProjectPromotionReference (Some Grace.Types.PromotionSet.PromotionSetStatus.Running) (Some terminalPromotion.ReferenceId) terminalPromotion,
            Is.False
        )

        Assert.That(
            canProjectPromotionReference
                (Some Grace.Types.PromotionSet.PromotionSetStatus.Succeeded)
                (Some(Guid.Parse("eeeeeee1-7300-4000-8000-eeeeeeeeeee1")))
                terminalPromotion,
            Is.False
        )

        Assert.That(
            canProjectPromotionReference (Some Grace.Types.PromotionSet.PromotionSetStatus.Succeeded) (Some terminalPromotion.ReferenceId) terminalPromotion,
            Is.True
        )

    /// Verifies Branch Create retry comparison remains anchored to immutable durable creation facts.
    [<Test>]
    member _.BranchCreateRetryUsesImmutableCreationFacts() =
        let parentBranchId = Guid.Parse("10101010-7300-4000-8000-101010101010")
        let basedOn = Guid.Parse("20202020-7300-4000-8000-202020202020")
        let createReferenceId = Guid.Parse("30303030-7300-4000-8000-303030303030")
        let branchName = BranchName "immutable-create"

        let permissions =
            [
                ReferenceType.Promotion
                ReferenceType.Commit
            ]

        let created = BranchEventType.Created(branchId, branchName, parentBranchId, basedOn, ownerId, organizationId, repositoryId, permissions)

        let exact =
            BranchCommand.Create(branchId, branchName, parentBranchId, basedOn, createReferenceId, ownerId, organizationId, repositoryId, List.rev permissions)

        let conflicting =
            BranchCommand.Create(
                branchId,
                branchName,
                parentBranchId,
                Guid.Parse("40404040-7300-4000-8000-404040404040"),
                createReferenceId,
                ownerId,
                organizationId,
                repositoryId,
                permissions
            )

        let conflictingPermissions =
            BranchCommand.Create(
                branchId,
                branchName,
                parentBranchId,
                basedOn,
                createReferenceId,
                ownerId,
                organizationId,
                repositoryId,
                [ ReferenceType.Promotion ]
            )

        Assert.That(createCommandMatchesCreationEvent created exact, Is.True)
        Assert.That(createCommandMatchesCreationEvent created conflicting, Is.False)
        Assert.That(createCommandMatchesCreationEvent created conflictingPermissions, Is.False)

    /// Verifies the newest projectable Promotion or Rebase transition exclusively chooses public BasedOn.
    [<Test>]
    member _.NewestBaseChangingTransitionWins() =
        let durableBase = { persistedCommitAt (Guid.Parse("50505050-7300-4000-8000-505050505050")) earlier with CreatedAt = earlier }

        let olderPromotion =
            { persistedCommitAt (Guid.Parse("60606060-7300-4000-8000-606060606060")) earlier with ReferenceType = ReferenceType.Promotion; CreatedAt = earlier }

        let rebaseTransition =
            { persistedCommitAt (Guid.Parse("70707070-7300-4000-8000-707070707070")) later with ReferenceType = ReferenceType.Rebase; CreatedAt = later }

        let rebaseTarget =
            { persistedCommitAt (Guid.Parse("80808080-7300-4000-8000-808080808080")) earlier with ReferenceType = ReferenceType.Promotion; CreatedAt = earlier }

        let newerPromotion = { olderPromotion with ReferenceId = Guid.Parse("81818181-7300-4000-8000-818181818181"); CreatedAt = later }
        let olderRebaseTransition = { rebaseTransition with ReferenceId = Guid.Parse("82828282-7300-4000-8000-828282828282"); CreatedAt = earlier }
        let tiedPromotion = { newerPromotion with ReferenceId = Guid.Parse("83838383-7300-4000-8000-838383838383") }

        Assert.That(selectBasedOnProjection durableBase (Some(rebaseTransition, rebaseTarget)) (Some olderPromotion), Is.EqualTo(rebaseTarget))
        Assert.That(selectBasedOnProjection durableBase (Some(olderRebaseTransition, rebaseTarget)) (Some newerPromotion), Is.EqualTo(newerPromotion))
        Assert.That(selectBasedOnProjection durableBase (Some(rebaseTransition, rebaseTarget)) (Some tiedPromotion), Is.EqualTo(tiedPromotion))
        Assert.That(selectBasedOnProjection durableBase None None, Is.EqualTo(durableBase))

    /// Verifies a just-created durable Promotion remains a recomputation candidate before storage queries catch up.
    [<Test>]
    member _.CurrentDurablePromotionBridgesQueryVisibilityLag() =
        let currentPromotion =
            { persistedCommitAt (Guid.Parse("90909090-7300-4000-8000-909090909090")) later with ReferenceType = ReferenceType.Promotion; CreatedAt = later }

        let staleQueryCopy =
            { currentPromotion with
                Links =
                    [
                        ReferenceLinkType.IncludedInPromotionSet(Guid.Parse("91919191-7300-4000-8000-919191919191"))
                    ]
            }

        let candidates = orderPromotionCandidates [| staleQueryCopy |] None (Some currentPromotion)

        Assert.That(candidates, Has.Length.EqualTo(1))
        Assert.That(candidates[0], Is.EqualTo(currentPromotion))

/// Proves Connect boundary selection follows durable branch-event order instead of Reference timestamps.
[<Parallelizable(ParallelScope.All)>]
type ReferenceMaterializationBoundarySelectionTests() =
    let repositoryId = Guid.Parse("11111111-8020-4000-8000-111111111111")
    let branchId = Guid.Parse("22222222-8020-4000-8000-222222222222")

    let candidate position referenceId directoryId referenceType establishesBranchBase : Grace.Server.Branch.ReferenceMaterializationBoundaryCandidate =
        {
            EventPosition = position
            RepositoryId = repositoryId
            BranchId = branchId
            Reference =
                { ReferenceDto.Default with
                    ReferenceId = referenceId
                    RepositoryId = repositoryId
                    BranchId = branchId
                    DirectoryId = directoryId
                    Sha256Hash = Sha256Hash $"sha-{position}"
                    Blake3Hash = Blake3Hash $"blake3-{position}"
                    ReferenceType = referenceType
                    CreatedAt = Instant.FromUnixTimeSeconds(100L - position)
                }
            EstablishesBranchBase = establishesBranchBase
            EligibleForWatchReplay =
                not establishesBranchBase
                && (referenceType = ReferenceType.Save
                    || referenceType = ReferenceType.Commit
                    || referenceType = ReferenceType.Checkpoint)
        }

    /// A later event wins even when its wall-clock timestamp is older.
    [<Test>]
    member _.ReferenceTypeSelectionUsesEventPosition() =
        let parameters = GetReferenceMaterializationBoundaryParameters()
        parameters.ReferenceType <- "Save"
        let olderId = Guid.Parse("33333333-8020-4000-8000-333333333333")
        let laterId = Guid.Parse("44444444-8020-4000-8000-444444444444")
        let laterRoot = Guid.Parse("55555555-8020-4000-8000-555555555555")

        let result =
            Grace.Server.Branch.trySelectReferenceMaterializationBoundary
                parameters
                [|
                    candidate 2L olderId (Guid.NewGuid()) ReferenceType.Save false
                    candidate 7L laterId laterRoot ReferenceType.Save false
                |]

        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.DirectoryId, Is.EqualTo(laterRoot))
        Assert.That(result.Value.EventCursor, Is.EqualTo("branch-event-v1:7"))

    /// Cross-root selectors cannot receive a cursor for an unrelated branch event.
    [<Test>]
    member _.UnknownDirectorySelectionHasNoBoundary() =
        let parameters = GetReferenceMaterializationBoundaryParameters()
        parameters.DirectoryVersionId <- Guid.Parse("66666666-8020-4000-8000-666666666666")

        let result =
            Grace.Server.Branch.trySelectReferenceMaterializationBoundary
                parameters
                [|
                    candidate 3L (Guid.NewGuid()) (Guid.Parse("77777777-8020-4000-8000-777777777777")) ReferenceType.Commit false
                |]

        Assert.That(result, Is.EqualTo(None))

    /// Default selection prefers the latest promotion, then the branch base recorded by Created or Rebased.
    [<Test>]
    member _.DefaultSelectionUsesPromotionThenBranchBase() =
        let parameters = GetReferenceMaterializationBoundaryParameters()
        let branchBase = candidate 0L (Guid.NewGuid()) (Guid.NewGuid()) ReferenceType.Commit true
        let promotion = candidate 5L (Guid.NewGuid()) (Guid.NewGuid()) ReferenceType.Promotion false
        let laterSave = candidate 7L (Guid.NewGuid()) (Guid.NewGuid()) ReferenceType.Save false

        let promoted = Grace.Server.Branch.trySelectReferenceMaterializationBoundary parameters [| branchBase; promotion; laterSave |]
        let basedOnly = Grace.Server.Branch.trySelectReferenceMaterializationBoundary parameters [| branchBase |]

        Assert.That(promoted.Value.DirectoryId, Is.EqualTo(promotion.Reference.DirectoryId))
        Assert.That(promoted.Value.EventCursor, Is.EqualTo("branch-event-v1:5"))
        Assert.That(basedOnly.Value.DirectoryId, Is.EqualTo(branchBase.Reference.DirectoryId))

    /// A stale Reference cache cannot override the durable DirectoryVersion hashes in the Connect materialization boundary.
    [<Test>]
    member _.MaterializationBoundaryUsesCanonicalDurableDirectoryVersionHashes() =
        let selected = candidate 8L (Guid.NewGuid()) (Guid.NewGuid()) ReferenceType.Promotion false

        let staleBoundary = Grace.Server.Branch.trySelectReferenceMaterializationBoundary (GetReferenceMaterializationBoundaryParameters()) [| selected |]

        let canonicalDirectoryVersion =
            DirectoryVersion.CreateWithHashes
                selected.Reference.DirectoryId
                (Guid.Parse("33333333-8020-4000-8000-333333333333"))
                (Guid.Parse("44444444-8020-4000-8000-444444444444"))
                repositoryId
                RootDirectoryPath
                (Sha256Hash "canonical-sha256")
                (Blake3Hash "canonical-blake3")
                (List<DirectoryVersionId>())
                (List<FileVersion>())
                0L

        let result =
            staleBoundary
            |> Option.bind (fun boundary -> Grace.Server.Branch.tryCreateCanonicalReferenceMaterializationBoundary boundary canonicalDirectoryVersion)

        Assert.That(result.IsSome, Is.True)
        Assert.That(staleBoundary.Value.Sha256Hash, Is.Not.EqualTo(canonicalDirectoryVersion.Sha256Hash))
        Assert.That(staleBoundary.Value.Blake3Hash, Is.Not.EqualTo(canonicalDirectoryVersion.Blake3Hash))
        Assert.That(result.Value.DirectoryId, Is.EqualTo(canonicalDirectoryVersion.DirectoryVersionId))
        Assert.That(result.Value.Sha256Hash, Is.EqualTo(canonicalDirectoryVersion.Sha256Hash))
        Assert.That(result.Value.Blake3Hash, Is.EqualTo(canonicalDirectoryVersion.Blake3Hash))
        Assert.That(result.Value.EventCursor, Is.EqualTo("branch-event-v1:8"))

    /// A boundary cannot be returned when the selected Reference and durable DirectoryVersion identities disagree.
    [<Test>]
    member _.MaterializationBoundaryRejectsMismatchedDurableDirectoryVersion() =
        let selected = candidate 9L (Guid.NewGuid()) (Guid.NewGuid()) ReferenceType.Promotion false

        let selectedBoundary = Grace.Server.Branch.trySelectReferenceMaterializationBoundary (GetReferenceMaterializationBoundaryParameters()) [| selected |]

        let unrelatedDirectoryVersion =
            DirectoryVersion.CreateWithHashes
                (Guid.NewGuid())
                (Guid.Parse("33333333-8020-4000-8000-333333333333"))
                (Guid.Parse("44444444-8020-4000-8000-444444444444"))
                repositoryId
                RootDirectoryPath
                (Sha256Hash "unrelated-sha256")
                (Blake3Hash "unrelated-blake3")
                (List<DirectoryVersionId>())
                (List<FileVersion>())
                0L

        let result =
            selectedBoundary
            |> Option.bind (fun boundary -> Grace.Server.Branch.tryCreateCanonicalReferenceMaterializationBoundary boundary unrelatedDirectoryVersion)

        Assert.That(result, Is.EqualTo(None))

    /// The latest exact eligible local root tuple wins even when an earlier eligible event has the same immutable root.
    [<Test>]
    member _.MissingCursorResolutionUsesLatestExactEligibleRootTuple() =
        let matching = candidate 3L (Guid.NewGuid()) (Guid.NewGuid()) ReferenceType.Save false
        let laterDuplicate = { matching with EventPosition = 4L; Reference = { matching.Reference with ReferenceId = Guid.NewGuid() } }
        let parameters = ResolveReferenceEventBoundaryParameters()
        parameters.DirectoryVersionId <- matching.Reference.DirectoryId
        parameters.Sha256Hash <- matching.Reference.Sha256Hash
        parameters.Blake3Hash <- matching.Reference.Blake3Hash

        let result = Grace.Server.Branch.tryResolveReferenceEventBoundary repositoryId branchId parameters 6 [| matching; laterDuplicate |]

        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.DirectoryId, Is.EqualTo(matching.Reference.DirectoryId))
        Assert.That(result.Value.Sha256Hash, Is.EqualTo(matching.Reference.Sha256Hash))
        Assert.That(result.Value.Blake3Hash, Is.EqualTo(matching.Reference.Blake3Hash))
        Assert.That(result.Value.EventCursor, Is.EqualTo("branch-event-v1:4"))

    /// Exact roots carried only by ineligible or branch-base events establish the immutable snapshot tail.
    [<Test>]
    member _.MissingCursorResolutionBaselinesExactIneligibleAndParentBaseRoots() =
        let cases =
            [|
                ReferenceType.Tag, false
                ReferenceType.Promotion, false
                ReferenceType.External, false
                ReferenceType.Commit, true
                ReferenceType.Rebase, true
            |]

        for referenceType, establishesBranchBase in cases do
            let ineligible = candidate 2L (Guid.NewGuid()) (Guid.NewGuid()) referenceType establishesBranchBase
            let parameters = ResolveReferenceEventBoundaryParameters()
            parameters.DirectoryVersionId <- ineligible.Reference.DirectoryId
            parameters.Sha256Hash <- ineligible.Reference.Sha256Hash
            parameters.Blake3Hash <- ineligible.Reference.Blake3Hash

            let result = Grace.Server.Branch.tryResolveReferenceEventBoundary repositoryId branchId parameters 6 [| ineligible |]

            Assert.That(result.IsSome, Is.True)
            Assert.That(result.Value.EventCursor, Is.EqualTo("branch-event-v1:5"), $"{referenceType}, base={establishesBranchBase}")

    /// An unmatched local root is preserved while the same immutable snapshot tail becomes its conservative boundary.
    [<Test>]
    member _.MissingCursorResolutionUsesConservativeSnapshotBaseline() =
        let parameters = ResolveReferenceEventBoundaryParameters()
        parameters.DirectoryVersionId <- Guid.Parse("88888888-8040-4000-8000-888888888888")
        parameters.Sha256Hash <- Sha256Hash "local-unmatched-sha"
        parameters.Blake3Hash <- Blake3Hash "local-unmatched-blake3"

        let result =
            Grace.Server.Branch.tryResolveReferenceEventBoundary
                repositoryId
                branchId
                parameters
                6
                [|
                    candidate 3L (Guid.NewGuid()) (Guid.NewGuid()) ReferenceType.Save false
                |]

        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.DirectoryId, Is.EqualTo(parameters.DirectoryVersionId))
        Assert.That(result.Value.Sha256Hash, Is.EqualTo(parameters.Sha256Hash))
        Assert.That(result.Value.Blake3Hash, Is.EqualTo(parameters.Blake3Hash))
        Assert.That(result.Value.EventCursor, Is.EqualTo("branch-event-v1:5"))

    /// Empty branch history cannot invent an opaque cursor for an otherwise complete local root tuple.
    [<Test>]
    member _.MissingCursorResolutionLeavesEmptyHistoryUnresolved() =
        let parameters = ResolveReferenceEventBoundaryParameters()
        parameters.DirectoryVersionId <- Guid.NewGuid()
        parameters.Sha256Hash <- Sha256Hash "empty-history-sha"
        parameters.Blake3Hash <- Blake3Hash "empty-history-blake3"

        let result = Grace.Server.Branch.tryResolveReferenceEventBoundary repositoryId branchId parameters 0 Array.empty

        Assert.That(result, Is.EqualTo(None))

    /// A matching tuple from another branch cannot authorize recovery in the requested branch.
    [<Test>]
    member _.MissingCursorResolutionRejectsCrossBranchMatch() =
        let otherBranchId = Guid.Parse("99999999-8040-4000-8000-999999999999")
        let localRoot = candidate 2L (Guid.NewGuid()) (Guid.NewGuid()) ReferenceType.Commit false

        let crossBranch = { localRoot with BranchId = otherBranchId; Reference = { localRoot.Reference with BranchId = otherBranchId } }

        let parameters = ResolveReferenceEventBoundaryParameters()
        parameters.DirectoryVersionId <- localRoot.Reference.DirectoryId
        parameters.Sha256Hash <- localRoot.Reference.Sha256Hash
        parameters.Blake3Hash <- localRoot.Reference.Blake3Hash

        let result = Grace.Server.Branch.tryResolveReferenceEventBoundary repositoryId branchId parameters 6 [| crossBranch |]

        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.EventCursor, Is.EqualTo("branch-event-v1:5"))

/// Proves Watch replay is closed by one immutable branch-event snapshot and server-owned cursor interpretation.
[<Parallelizable(ParallelScope.All)>]
type ReferenceEventReplayTests() =
    let repositoryId = Guid.Parse("11111111-8030-4000-8000-111111111111")
    let branchId = Guid.Parse("22222222-8030-4000-8000-222222222222")

    /// Builds a durable Branch event with deterministic Reference and root identity.
    let referenceEvent position referenceType =
        let referenceId = Guid.Parse($"{position + 10:D8}-8030-4000-8000-222222222222")
        let directoryId = Guid.Parse($"{position + 20:D8}-8030-4000-8000-222222222222")

        let reference =
            { ReferenceDto.Default with
                ReferenceId = referenceId
                OwnerId = Guid.Parse("33333333-8030-4000-8000-333333333333")
                OrganizationId = Guid.Parse("44444444-8030-4000-8000-444444444444")
                RepositoryId = repositoryId
                BranchId = branchId
                DirectoryId = directoryId
                Sha256Hash = Sha256Hash $"sha-{position}"
                Blake3Hash = Blake3Hash $"blake3-{position}"
                ReferenceType = referenceType
                ReferenceText = ReferenceText $"reference-{position}"
            }

        let event =
            match referenceType with
            | ReferenceType.Commit -> BranchEventType.Committed(reference, directoryId, reference.Sha256Hash, reference.Blake3Hash, reference.ReferenceText)
            | ReferenceType.Checkpoint ->
                BranchEventType.Checkpointed(reference, directoryId, reference.Sha256Hash, reference.Blake3Hash, reference.ReferenceText)
            | ReferenceType.Save -> BranchEventType.Saved(reference, directoryId, reference.Sha256Hash, reference.Blake3Hash, reference.ReferenceText)
            | ReferenceType.Tag -> BranchEventType.Tagged(reference, directoryId, reference.Sha256Hash, reference.Blake3Hash, reference.ReferenceText)
            | _ -> invalidArg (nameof referenceType) "Unsupported replay test Reference type."

        ({ Event = event; Metadata = EventMetadata.New $"correlation-{position}" GraceSystemUser }: BranchEvent)

    /// Eligible events preserve durable order and exact positions while ineligible events still close the scanned range.
    [<Test>]
    member _.EligibleEventsRemainOrderedAcrossIneligibleEvents() =
        let events =
            [|
                referenceEvent 0 ReferenceType.Tag
                referenceEvent 1 ReferenceType.Save
                referenceEvent 2 ReferenceType.Commit
                ({ Event = BranchEventType.NameSet(BranchName "renamed"); Metadata = EventMetadata.New "correlation-3" GraceSystemUser }: BranchEvent)
                referenceEvent 4 ReferenceType.Checkpoint
            |]

        let result = Grace.Server.Branch.replayReferenceEventsAfterCursor repositoryId branchId repositoryId branchId "branch-event-v1:0" events

        match result with
        | Error failure -> Assert.Fail($"Expected replay success, got {failure}.")
        | Ok replay ->
            Assert.That(
                replay.Events
                |> Array.map (fun replayEvent -> replayEvent.EventCursor)
                |> String.concat "|",
                Is.EqualTo("branch-event-v1:1|branch-event-v1:2|branch-event-v1:4")
            )

            Assert.That(
                replay.Events
                |> Array.map (fun replayEvent -> string replayEvent.Reference.ReferenceType)
                |> String.concat "|",
                Is.EqualTo("ReferenceType.Save|ReferenceType.Commit|ReferenceType.Checkpoint")
            )

            Assert.That(replay.ScannedThroughCursor, Is.EqualTo("branch-event-v1:4"))

    /// A publication-failed Reference retry appends its absent replay transition once at the durable Branch order boundary.
    [<Test>]
    member _.MatchingRetryRepairReplaysExactlyOnceInDurableOrder() =
        let events =
            ResizeArray<BranchEvent>(
                [
                    referenceEvent 0 ReferenceType.Tag
                    referenceEvent 1 ReferenceType.Commit
                ]
            )

        let repaired = referenceEvent 2 ReferenceType.Save

        if shouldRepairMatchingRetryBranchEvent ReferenceOperationDisposition.MatchingRetry repaired.Event events then
            events.Add repaired

        if shouldRepairMatchingRetryBranchEvent ReferenceOperationDisposition.MatchingRetry repaired.Event events then
            events.Add repaired

        match Grace.Server.Branch.replayReferenceEventsAfterCursor repositoryId branchId repositoryId branchId "branch-event-v1:0" events with
        | Error failure -> Assert.Fail($"Expected replay success, got {failure}.")
        | Ok replay ->
            Assert.That(replay.Events, Has.Length.EqualTo(2))

            Assert.That(
                replay.Events
                |> Array.map (fun replayEvent -> $"{replayEvent.EventCursor}:{replayEvent.Reference.ReferenceType}")
                |> String.concat "|",
                Is.EqualTo("branch-event-v1:1:ReferenceType.Commit|branch-event-v1:2:ReferenceType.Save")
            )

            Assert.That(replay.ScannedThroughCursor, Is.EqualTo("branch-event-v1:2"))

    /// An empty eligible interval advances only to the exact end of the immutable scanned snapshot.
    [<Test>]
    member _.EmptyEligibleIntervalReturnsScannedThroughClosure() =
        let events =
            [|
                referenceEvent 0 ReferenceType.Save
                referenceEvent 1 ReferenceType.Tag
                ({ Event = BranchEventType.NameSet(BranchName "renamed"); Metadata = EventMetadata.New "correlation-2" GraceSystemUser }: BranchEvent)
            |]

        match Grace.Server.Branch.replayReferenceEventsAfterCursor repositoryId branchId repositoryId branchId "branch-event-v1:0" events with
        | Error failure -> Assert.Fail($"Expected replay success, got {failure}.")
        | Ok replay ->
            Assert.That(replay.Events, Is.Empty)
            Assert.That(replay.ScannedThroughCursor, Is.EqualTo("branch-event-v1:2"))

    /// Cursor syntax, version, future position, and scope are rejected without a partial replay response.
    [<TestCase("not-a-cursor", ReferenceReplayCursorFailure.Malformed)>]
    [<TestCase("branch-event-v2:0", ReferenceReplayCursorFailure.UnsupportedVersion)>]
    [<TestCase("branch-event-v1:9", ReferenceReplayCursorFailure.Future)>]
    member _.InvalidCursorIsTyped(cursor, expectedFailure: int) =
        let result =
            Grace.Server.Branch.replayReferenceEventsAfterCursor
                repositoryId
                branchId
                repositoryId
                branchId
                cursor
                [|
                    referenceEvent 0 ReferenceType.Save
                |]

        match result with
        | Ok _ -> Assert.Fail("Expected cursor rejection.")
        | Error failure -> Assert.That(failure, Is.EqualTo(enum<ReferenceReplayCursorFailure> expectedFailure))

    /// Cursor scope is validated separately from its opaque position token.
    [<Test>]
    member _.CrossScopeCursorIsTyped() =
        let events =
            [|
                referenceEvent 0 ReferenceType.Save
            |]

        let crossRepository = Grace.Server.Branch.replayReferenceEventsAfterCursor repositoryId branchId (Guid.NewGuid()) branchId "branch-event-v1:0" events

        let crossBranch = Grace.Server.Branch.replayReferenceEventsAfterCursor repositoryId branchId repositoryId (Guid.NewGuid()) "branch-event-v1:0" events

        match crossRepository with
        | Ok _ -> Assert.Fail("Expected repository-scoped cursor rejection.")
        | Error failure -> Assert.That(failure, Is.EqualTo(ReferenceReplayCursorFailure.RepositoryMismatch))

        match crossBranch with
        | Ok _ -> Assert.Fail("Expected branch-scoped cursor rejection.")
        | Error failure -> Assert.That(failure, Is.EqualTo(ReferenceReplayCursorFailure.BranchMismatch))
