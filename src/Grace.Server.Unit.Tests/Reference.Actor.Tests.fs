namespace Grace.Server.Unit.Tests

open Grace.Actors.Reference
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Reference
open NUnit.Framework
open System
open System.Collections.Generic
open System.Threading.Tasks

[<Parallelizable(ParallelScope.All)>]
type ReferenceActorHashValidationTests() =

    let correlationId = "reference-root-hash-validation-tests"
    let ownerId = Guid.Parse("11111111-bbbb-4444-8888-111111111111")
    let organizationId = Guid.Parse("22222222-bbbb-4444-8888-222222222222")
    let repositoryId = Guid.Parse("33333333-bbbb-4444-8888-333333333333")
    let directoryVersionId = Guid.Parse("44444444-bbbb-4444-8888-444444444444")
    let sha256Hash = Sha256Hash "root-sha256"
    let blake3Hash = Blake3Hash "root-blake3"

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

    [<Test>]
    member _.MissingRootBlake3FailsBeforeReferenceCreation() =
        let directoryVersion = directoryVersionWithHashes sha256Hash (Blake3Hash String.Empty)

        let result = validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId sha256Hash blake3Hash directoryVersion

        match result with
        | Ok _ -> Assert.Fail("Expected missing root Blake3Hash to fail.")
        | Error error ->
            Assert.That(error.Error, Does.Contain("must include Blake3Hash"))
            Assert.That(error.Properties[nameof DirectoryVersionId], Is.EqualTo(string directoryVersionId))

    [<Test>]
    member _.EmptyCommandBlake3FailsBeforeReferenceCreation() =
        let directoryVersion = directoryVersionWithHashes sha256Hash blake3Hash

        let result =
            validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId sha256Hash (Blake3Hash String.Empty) directoryVersion

        match result with
        | Ok _ -> Assert.Fail("Expected empty command Blake3Hash to fail.")
        | Error error -> Assert.That(error.Error, Does.Contain("command must include"))

    [<Test>]
    member _.LegacyRootDirectoryVersionWithEmptyBlake3AllowsEmptyCommandBlake3() =
        let directoryVersion = directoryVersionWithHashes sha256Hash (Blake3Hash String.Empty)

        let result =
            validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId sha256Hash (Blake3Hash String.Empty) directoryVersion

        match result with
        | Ok _ -> ()
        | Error error -> Assert.Fail($"Expected legacy empty Blake3Hash root to be tolerated, but got {error.Error}.")

    [<Test>]
    member _.NonRootDirectoryVersionFailsBeforeReferenceCreation() =
        let directoryVersion = childDirectoryVersionWithHashes sha256Hash blake3Hash

        let result = validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryVersionId sha256Hash blake3Hash directoryVersion

        match result with
        | Ok _ -> Assert.Fail("Expected non-root DirectoryVersion to fail.")
        | Error error -> Assert.That(error.Error, Does.Contain("repository root path"))

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

    [<Test>]
    member _.LegacyCreatedEventWithEmptyBlake3HydratesFromMatchingRootDirectoryVersion() =
        task {
            let referenceId = Guid.Parse("55555555-bbbb-4444-8888-555555555555")
            let branchId = Guid.Parse("66666666-bbbb-4444-8888-666666666666")
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

    [<Test>]
    member _.LegacyCreatedEventWithEmptyBlake3DoesNotHydrateFromNonRootOrWrongShaPrefix() =
        task {
            let referenceId = Guid.Parse("99999999-bbbb-4444-8888-999999999999")
            let branchId = Guid.Parse("aaaaaaaa-bbbb-4444-8888-aaaaaaaaaaaa")
            let fullSha256Hash = Sha256Hash "abcdef0123456789"

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
