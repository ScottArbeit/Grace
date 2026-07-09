namespace Grace.Server.Unit.Tests

open Grace.Actors.Reference
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Reference
open Grace.Types.Visibility
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks

/// Covers reference Actor Hash Validation behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type ReferenceActorHashValidationTests() =

    let correlationId = "reference-root-hash-validation-tests"
    let ownerId = Guid.Parse("11111111-bbbb-4444-8888-111111111111")
    let organizationId = Guid.Parse("22222222-bbbb-4444-8888-222222222222")
    let repositoryId = Guid.Parse("33333333-bbbb-4444-8888-333333333333")
    let directoryVersionId = Guid.Parse("44444444-bbbb-4444-8888-444444444444")
    let sha256Hash = Sha256Hash "root-sha256"
    let blake3Hash = Blake3Hash "root-blake3"

    let branchId = Guid.Parse("55555555-bbbb-4444-8888-555555555555")
    let referenceId = Guid.Parse("66666666-bbbb-4444-8888-666666666666")
    let referenceText = ReferenceText "matching replay"

    /// Builds directory Version With Hashes test data for the server unit reference Actor scenarios in this file.
    let directoryVersionWithHashes sha blake3 =
        DirectoryVersion.CreateWithHashes
            directoryVersionId
            ownerId
            organizationId
            repositoryId
            (RelativePath ".")
            sha
            blake3
            (List<DirectoryVersionId>())
            (List<FileVersion>())
            0L

    /// Builds child Directory Version With Hashes test data for the server unit reference Actor scenarios in this file.
    let childDirectoryVersionWithHashes sha blake3 =
        DirectoryVersion.CreateWithHashes
            directoryVersionId
            ownerId
            organizationId
            repositoryId
            (RelativePath $"child/{Guid.NewGuid():N}")
            sha
            blake3
            (List<DirectoryVersionId>())
            (List<FileVersion>())
            0L

    /// Verifies that missing Root Blake3 Fails Before Reference Creation.
    [<Test>]
    member _.MissingRootBlake3FailsBeforeReferenceCreation() =
        let directoryVersion = directoryVersionWithHashes sha256Hash (Blake3Hash String.Empty)

        let result = validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId sha256Hash blake3Hash directoryVersion

        match result with
        | Ok _ -> Assert.Fail("Expected missing root Blake3Hash to fail.")
        | Error error ->
            Assert.That(error.Error, Does.Contain("must include Blake3Hash"))
            Assert.That(error.Properties[nameof DirectoryVersionId], Is.EqualTo(string directoryVersionId))

    /// Verifies that empty Command Blake3 Fails Before Reference Creation.
    [<Test>]
    member _.EmptyCommandBlake3FailsBeforeReferenceCreation() =
        let directoryVersion = directoryVersionWithHashes sha256Hash blake3Hash

        let result =
            validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId sha256Hash (Blake3Hash String.Empty) directoryVersion

        match result with
        | Ok _ -> Assert.Fail("Expected empty command Blake3Hash to fail.")
        | Error error -> Assert.That(error.Error, Does.Contain("command must include"))

    /// Verifies that legacy Root Directory Version With Empty Blake3 Allows Empty Command Blake3.
    [<Test>]
    member _.LegacyRootDirectoryVersionWithEmptyBlake3AllowsEmptyCommandBlake3() =
        let directoryVersion = directoryVersionWithHashes sha256Hash (Blake3Hash String.Empty)

        let result =
            validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId sha256Hash (Blake3Hash String.Empty) directoryVersion

        match result with
        | Ok _ -> ()
        | Error error -> Assert.Fail($"Expected legacy empty Blake3Hash root to be tolerated, but got {error.Error}.")

    /// Verifies that non Root Directory Version Fails Before Reference Creation.
    [<Test>]
    member _.NonRootDirectoryVersionFailsBeforeReferenceCreation() =
        let directoryVersion = childDirectoryVersionWithHashes sha256Hash blake3Hash

        let result = validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId sha256Hash blake3Hash directoryVersion

        match result with
        | Ok _ -> Assert.Fail("Expected non-root DirectoryVersion to fail.")
        | Error error -> Assert.That(error.Error, Does.Contain("repository root path"))

    /// Verifies that mismatched Root Hashes Fail Before Reference Creation.
    [<Test>]
    member _.MismatchedRootHashesFailBeforeReferenceCreation() =
        let directoryVersion = directoryVersionWithHashes sha256Hash blake3Hash

        let shaResult =
            validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId (Sha256Hash "wrong-sha") blake3Hash directoryVersion

        let blakeResult =
            validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId sha256Hash (Blake3Hash "wrong-blake3") directoryVersion

        match shaResult, blakeResult with
        | Error shaError, Error blakeError ->
            Assert.That(shaError.Error, Does.Contain("Sha256Hash does not match"))
            Assert.That(blakeError.Error, Does.Contain("Blake3Hash does not match"))
        | _ -> Assert.Fail("Expected both mismatched hash validations to fail.")

    /// Verifies that create Command Replay Matches Durable Created Reference.
    [<Test>]
    member _.CreateCommandReplayMatchesDurableCreatedReference() =
        let links =
            [
                ReferenceLinkType.BasedOn(Guid.Parse("77777777-bbbb-4444-8888-777777777777"))
            ]

        let referenceDto =
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
                Links = links
                UpdatedAt = Some(getCurrentInstant ())
            }

        let matchingCommand =
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
                referenceText,
                links
            )

        let mismatchedCommand =
            ReferenceCommand.Create(
                referenceId,
                ownerId,
                organizationId,
                repositoryId,
                branchId,
                directoryVersionId,
                sha256Hash,
                Blake3Hash "different-blake3",
                ReferenceType.Commit,
                referenceText,
                links
            )

        Assert.That(createCommandMatchesReference referenceDto matchingCommand, Is.True)
        Assert.That(createCommandMatchesReference referenceDto mismatchedCommand, Is.False)
        Assert.That(createCommandMatchesReference ReferenceDto.Default matchingCommand, Is.False)

    /// Verifies that legacy Created Event With Empty Blake3 Hydrates From Matching Root Directory Version.
    [<Test>]
    member _.LegacyCreatedEventWithEmptyBlake3HydratesFromMatchingRootDirectoryVersion() =
        task {
            let directoryVersion = directoryVersionWithHashes sha256Hash blake3Hash

            let legacyCreatedEvent =
                {
                    Event =
                        ReferenceEventType.Created(
                            referenceId,
                            ownerId,
                            organizationId,
                            repositoryId,
                            branchId,
                            directoryVersionId,
                            sha256Hash,
                            Blake3Hash String.Empty,
                            ReferenceType.Commit,
                            ReferenceText "legacy commit",
                            Seq.empty
                        )
                    Metadata =
                        {
                            Timestamp = getCurrentInstant ()
                            CorrelationId = correlationId
                            Principal = "legacy-replay-test"
                            ClientType = None
                            Properties = Dictionary<string, string>()
                        }
                }

            /// Extracts directory Version from the scenario result so assertions stay focused on server unit reference Actor behavior.
            let getDirectoryVersion (requestedRepositoryId: RepositoryId) (requestedDirectoryId: DirectoryVersionId) (requestedCorrelationId: CorrelationId) =
                Assert.That(requestedRepositoryId, Is.EqualTo(repositoryId))
                Assert.That(requestedDirectoryId, Is.EqualTo(directoryVersionId))
                Assert.That(requestedCorrelationId, Is.EqualTo(correlationId))
                Task.FromResult directoryVersion

            let! repairedEvent, wasRepaired = repairLegacyCreatedEventBlake3 getDirectoryVersion legacyCreatedEvent
            Assert.That(wasRepaired, Is.True)

            let repairedDto = ReferenceDto.UpdateDto repairedEvent ReferenceDto.Default
            Assert.That(repairedDto.Sha256Hash, Is.EqualTo(sha256Hash))
            Assert.That(repairedDto.Blake3Hash, Is.EqualTo(blake3Hash))
        }

    /// Verifies that legacy Created Event With Empty Blake3 Hydrates From Root Sha Prefix.
    [<Test>]
    member _.LegacyCreatedEventWithEmptyBlake3HydratesFromRootShaPrefix() =
        task {
            let referenceId = Guid.Parse("77777777-bbbb-4444-8888-777777777777")
            let branchId = Guid.Parse("88888888-bbbb-4444-8888-888888888888")
            let fullSha256Hash = Sha256Hash "abcdef0123456789"
            let prefixSha256Hash = Sha256Hash "abcdef"
            let directoryVersion = directoryVersionWithHashes fullSha256Hash blake3Hash

            let legacyCreatedEvent =
                {
                    Event =
                        ReferenceEventType.Created(
                            referenceId,
                            ownerId,
                            organizationId,
                            repositoryId,
                            branchId,
                            directoryVersionId,
                            prefixSha256Hash,
                            Blake3Hash String.Empty,
                            ReferenceType.Commit,
                            ReferenceText "legacy prefix commit",
                            Seq.empty
                        )
                    Metadata =
                        {
                            Timestamp = getCurrentInstant ()
                            CorrelationId = correlationId
                            Principal = "legacy-prefix-replay-test"
                            ClientType = None
                            Properties = Dictionary<string, string>()
                        }
                }

            let getDirectoryVersion _ _ _ = Task.FromResult directoryVersion

            let! repairedEvent, wasRepaired = repairLegacyCreatedEventBlake3 getDirectoryVersion legacyCreatedEvent
            Assert.That(wasRepaired, Is.True)

            let repairedDto = ReferenceDto.UpdateDto repairedEvent ReferenceDto.Default
            Assert.That(repairedDto.Sha256Hash, Is.EqualTo(fullSha256Hash))
            Assert.That(repairedDto.Blake3Hash, Is.EqualTo(blake3Hash))
        }

    /// Verifies that legacy Created Event With Empty Blake3 Does Not Hydrate From Non Root Or Wrong Sha Prefix.
    [<Test>]
    member _.LegacyCreatedEventWithEmptyBlake3DoesNotHydrateFromNonRootOrWrongShaPrefix() =
        task {
            let referenceId = Guid.Parse("99999999-bbbb-4444-8888-999999999999")
            let branchId = Guid.Parse("aaaaaaaa-bbbb-4444-8888-aaaaaaaaaaaa")
            let fullSha256Hash = Sha256Hash "abcdef0123456789"

            /// Constructs event fixtures used by the server unit reference Actor assertions.
            let createEvent storedSha256Hash =
                {
                    Event =
                        ReferenceEventType.Created(
                            referenceId,
                            ownerId,
                            organizationId,
                            repositoryId,
                            branchId,
                            directoryVersionId,
                            storedSha256Hash,
                            Blake3Hash String.Empty,
                            ReferenceType.Commit,
                            ReferenceText "legacy mismatch commit",
                            Seq.empty
                        )
                    Metadata =
                        {
                            Timestamp = getCurrentInstant ()
                            CorrelationId = correlationId
                            Principal = "legacy-mismatch-replay-test"
                            ClientType = None
                            Properties = Dictionary<string, string>()
                        }
                }

            let nonRootDirectoryVersion = childDirectoryVersionWithHashes fullSha256Hash blake3Hash
            let getNonRootDirectoryVersion _ _ _ = Task.FromResult nonRootDirectoryVersion
            let! _, nonRootWasRepaired = repairLegacyCreatedEventBlake3 getNonRootDirectoryVersion (createEvent (Sha256Hash "abcdef"))

            let rootDirectoryVersion = directoryVersionWithHashes fullSha256Hash blake3Hash
            let getRootDirectoryVersion _ _ _ = Task.FromResult rootDirectoryVersion
            let! _, wrongPrefixWasRepaired = repairLegacyCreatedEventBlake3 getRootDirectoryVersion (createEvent (Sha256Hash "123456"))

            Assert.That(nonRootWasRepaired, Is.False)
            Assert.That(wrongPrefixWasRepaired, Is.False)
        }

    /// Verifies that save Create Applies Manifest Contribution Boundary Before Created Event Persists.
    [<Test>]
    member _.SaveCreateAppliesManifestContributionBoundaryBeforeCreatedEventPersists() =
        let actorPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "Reference.Actor.fs"))
        let actorSource = File.ReadAllText actorPath
        let eventPlanningStart = actorSource.IndexOf("let! (referenceEventTypeResult", StringComparison.Ordinal)

        Assert.That(eventPlanningStart, Is.GreaterThanOrEqualTo(0), "The ReferenceActor event-planning block must be present.")

        let createBranchStart = actorSource.IndexOf("| Create (referenceId,", eventPlanningStart, StringComparison.Ordinal)

        Assert.That(createBranchStart, Is.GreaterThanOrEqualTo(0), "The ReferenceActor Create branch must be present.")

        let addLinkBranchStart = actorSource.IndexOf("| AddLink link ->", createBranchStart, StringComparison.Ordinal)

        Assert.That(addLinkBranchStart, Is.GreaterThan(createBranchStart), "The ReferenceActor Create branch must have a bounded source slice.")

        let createBranch = actorSource.Substring(createBranchStart, addLinkBranchStart - createBranchStart)

        let validateIndex = createBranch.IndexOf("validateRootDirectoryVersionHashes repositoryId directoryId sha256Hash blake3Hash", StringComparison.Ordinal)

        Assert.That(validateIndex, Is.GreaterThanOrEqualTo(0), "Create must validate root directory hashes before planning Created.")

        let boundaryIndex =
            createBranch.IndexOf("applyReferenceManifestBoundary referenceId repositoryId directoryId referenceType", validateIndex, StringComparison.Ordinal)

        Assert.That(
            boundaryIndex,
            Is.GreaterThan(validateIndex),
            "Save create must apply manifest contribution side effects after hash validation and before planning Created."
        )

        let createdIndex = createBranch.IndexOf("Created(", boundaryIndex, StringComparison.Ordinal)

        Assert.That(
            createdIndex,
            Is.GreaterThan(boundaryIndex),
            "A failed Save manifest contribution boundary must return Error before Created is planned for persistence."
        )

        let applyResultStart = actorSource.IndexOf("match referenceEventTypeResult with", addLinkBranchStart, StringComparison.Ordinal)

        Assert.That(applyResultStart, Is.GreaterThan(addLinkBranchStart), "ReferenceActor must apply the selected event after command planning.")

        let handleEnd = actorSource.IndexOf("match! isValid command metadata with", applyResultStart, StringComparison.Ordinal)

        Assert.That(handleEnd, Is.GreaterThan(applyResultStart), "ReferenceActor event application must have a bounded source slice.")

        let applyResultSlice = actorSource.Substring(applyResultStart, handleEnd - applyResultStart)
        let applyEventIndex = applyResultSlice.IndexOf("let! returnValue = this.ApplyEvent referenceEvent", StringComparison.Ordinal)

        Assert.That(
            applyResultSlice,
            Does.Contain("return! this.ApplyEvent referenceEvent"),
            "ReferenceActor must persist the planned reference event through ApplyEvent after pre-persistence validation."
        )

        Assert.That(
            applyEventIndex,
            Is.LessThan(0),
            "ReferenceActor must not keep the legacy ApplyEvent binding that allowed post-persistence boundary failures."
        )

        Assert.That(
            applyResultSlice,
            Does.Not.Contain("applyReferenceManifestBoundary referenceId repositoryId directoryId referenceType"),
            "Save manifest contribution boundary failures must not occur after ApplyEvent persists Created."
        )

    /// Verifies that manifest Expiry Boundary Only Applies To Save References Until Commit Checkpoint Fanout Is Wired.
    [<Test>]
    member _.ManifestExpiryBoundaryOnlyAppliesToSaveReferencesUntilCommitCheckpointFanoutIsWired() =
        let referenceOfType referenceType = { ReferenceDto.Default with ReferenceId = Guid.NewGuid(); ReferenceType = referenceType }

        Assert.That(shouldApplyManifestExpiryBoundary (referenceOfType ReferenceType.Save), Is.True)
        Assert.That(shouldApplyManifestExpiryBoundary (referenceOfType ReferenceType.Commit), Is.False)
        Assert.That(shouldApplyManifestExpiryBoundary (referenceOfType ReferenceType.Checkpoint), Is.False)
        Assert.That(shouldApplyManifestExpiryBoundary ReferenceDto.Default, Is.False)

    /// Verifies that manifest contribution boundary predicates keep range retention scoped while ownership tracks promotion refs.
    [<Test>]
    member _.ManifestContributionBoundaryPredicateKeepsCommitCheckpointOutOfUnwiredWorkflow() =
        let actorPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "Reference.Actor.fs"))
        let actorSource = File.ReadAllText actorPath
        let predicateStart = actorSource.IndexOf("let appliesRepositoryManifestBoundary referenceType =", StringComparison.Ordinal)
        let boundaryStart = actorSource.IndexOf("let applyReferenceManifestBoundary", predicateStart, StringComparison.Ordinal)

        Assert.That(predicateStart, Is.GreaterThanOrEqualTo(0), "The ReferenceActor repository manifest-boundary predicate must be present.")
        Assert.That(boundaryStart, Is.GreaterThan(predicateStart), "The manifest-boundary predicate slice must be bounded.")

        let predicateSource = actorSource.Substring(predicateStart, boundaryStart - predicateStart)

        Assert.That(predicateSource, Does.Contain("referenceType = ReferenceType.Save"))
        Assert.That(predicateSource, Does.Contain("referenceType = ReferenceType.Promotion"))
        Assert.That(predicateSource, Does.Not.Contain("ReferenceType.Commit"))
        Assert.That(predicateSource, Does.Not.Contain("ReferenceType.Checkpoint"))

        let repositoryPredicateStart = predicateSource.IndexOf("let appliesRepositoryManifestBoundary", StringComparison.Ordinal)
        let ownershipPredicateStart = predicateSource.IndexOf("let appliesOwnershipManifestBoundary", StringComparison.Ordinal)

        Assert.That(repositoryPredicateStart, Is.GreaterThanOrEqualTo(0))
        Assert.That(ownershipPredicateStart, Is.GreaterThan(repositoryPredicateStart))

        let repositoryPredicateSource = predicateSource.Substring(repositoryPredicateStart, ownershipPredicateStart - repositoryPredicateStart)

        Assert.That(repositoryPredicateSource, Does.Contain("referenceType = ReferenceType.Save"))
        Assert.That(repositoryPredicateSource, Does.Not.Contain("ReferenceType.Promotion"))

    /// Verifies that manifest Contribution Boundary Traversals Force Regeneration Instead Of Cached Recursive Results.
    [<Test>]
    member _.ManifestContributionBoundaryTraversalsForceRegenerationInsteadOfCachedRecursiveResults() =
        let actorPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "Reference.Actor.fs"))
        let actorSource = File.ReadAllText actorPath

        /// Asserts the boundary Forces Regeneration condition so failures identify the violated server unit reference Actor invariant.
        let assertBoundaryForcesRegeneration (boundaryStartText: string) (boundaryEndText: string) =
            let boundaryStart = actorSource.IndexOf(boundaryStartText, StringComparison.Ordinal)

            Assert.That(boundaryStart, Is.GreaterThanOrEqualTo(0), $"Expected ReferenceActor boundary `{boundaryStartText}` to be present.")

            let boundaryEnd = actorSource.IndexOf(boundaryEndText, boundaryStart, StringComparison.Ordinal)

            Assert.That(boundaryEnd, Is.GreaterThan(boundaryStart), $"Expected ReferenceActor boundary `{boundaryStartText}` to have a bounded source slice.")

            let boundarySource = actorSource.Substring(boundaryStart, boundaryEnd - boundaryStart)

            Assert.That(
                boundarySource,
                Does.Contain("GetRecursiveDirectoryVersions true"),
                $"Expected ReferenceActor boundary `{boundaryStartText}` to bypass cached partial recursive directory results."
            )

            Assert.That(
                boundarySource,
                Does.Not.Contain("GetRecursiveDirectoryVersions false"),
                $"ReferenceActor boundary `{boundaryStartText}` must not trust cached partial recursive directory results."
            )

        assertBoundaryForcesRegeneration "let! boundaryResult =" "match boundaryResult with"

        assertBoundaryForcesRegeneration
            "let applyReferenceManifestBoundary referenceId repositoryId directoryId referenceType ="
            "let applyReferenceManifestExpiryBoundary referenceId repositoryId directoryId referenceType ="

        assertBoundaryForcesRegeneration
            "let applyReferenceManifestExpiryBoundary referenceId repositoryId directoryId referenceType ="
            "let existingReferenceReturnValue () ="

    /// Verifies that reference reveal idempotency uses durable operation id instead of correlation id.
    [<Test>]
    member _.RevealIdempotencyUsesOperationId() =
        let metadata =
            {
                Timestamp = getCurrentInstant ()
                CorrelationId = "reveal-correlation-1"
                Principal = "reviewer@example.test"
                ClientType = None
                Properties = Dictionary<string, string>()
            }

        let revealEvent =
            {
                Event =
                    ReferenceEventType.Revealed(
                        "reveal-operation-1",
                        "reviewer@example.test",
                        "accepted work",
                        ResourceVisibility.Private,
                        ResourceVisibility.Public
                    )
                Metadata = metadata
            }

        let events = [ revealEvent ]

        Assert.That(revealEventMatchesCommand "reveal-operation-1" "accepted work" "reviewer@example.test" ResourceVisibility.Public events, Is.True)

        Assert.That(revealEventMatchesCommand "reveal-operation-1" "different reason" "reviewer@example.test" ResourceVisibility.Public events, Is.False)

        Assert.That(
            tryFindRevealEventByOperationId "reveal-operation-1" events
            |> Option.isSome,
            Is.True
        )

        Assert.That(
            tryFindRevealEventByOperationId "other-operation" events
            |> Option.isNone,
            Is.True
        )
