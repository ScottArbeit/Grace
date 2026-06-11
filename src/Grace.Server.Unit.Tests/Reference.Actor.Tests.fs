namespace Grace.Server.Unit.Tests

open Grace.Actors.Reference
open Grace.Shared
open Grace.Types.Common
open NUnit.Framework
open System
open System.Collections.Generic

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
