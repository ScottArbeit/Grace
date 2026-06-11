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
