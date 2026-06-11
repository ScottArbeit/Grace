namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Common
open Grace.Types.Diff
open NUnit.Framework
open System
open System.Net

module DiffServerTestHelpers =
    let getDiffParameters (repositoryId: string) (directoryVersionId1: DirectoryVersionId) (directoryVersionId2: DirectoryVersionId) =
        let parameters = Parameters.Diff.GetDiffParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.DirectoryVersionId1 <- directoryVersionId1
        parameters.DirectoryVersionId2 <- directoryVersionId2
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let getDiffBySha256HashParameters (repositoryId: string) (sha256Hash1: Sha256Hash) (sha256Hash2: Sha256Hash) =
        let parameters = Parameters.Diff.GetDiffBySha256HashParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.Sha256Hash1 <- sha256Hash1
        parameters.Sha256Hash2 <- sha256Hash2
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let getDiffByBlake3HashParameters (repositoryId: string) (blake3Hash1: Blake3Hash) (blake3Hash2: Blake3Hash) =
        let parameters = Parameters.Diff.GetDiffByBlake3HashParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.Blake3Hash1 <- blake3Hash1
        parameters.Blake3Hash2 <- blake3Hash2
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let private prefix (length: int) (hash: Blake3Hash) = (string hash).Substring(0, length)

    let createSameBlake3PrefixDirectoryPair repositoryId pathPrefix =
        let candidates =
            [|
                for index in 0..512 ->
                    DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId (RelativePath $"/{pathPrefix}/{index}/") []
            |]

        let pair =
            candidates
            |> Array.groupBy (fun directoryVersion -> prefix 2 directoryVersion.Blake3Hash)
            |> Array.tryPick (fun (sharedPrefix, matches) -> if matches.Length >= 2 then Some(matches[0], matches[1], sharedPrefix) else None)

        match pair with
        | Some pair -> pair
        | None -> failwith "Could not generate same-prefix BLAKE3 directory versions for diff route tests."

[<NonParallelizable>]
type DiffServer() =

    [<Test>]
    member _.DiffRoutesRejectInvalidDirectoryIdsAndShaInputsBeforeReturningSuccessEnvelopes() =
        task {
            let repositoryId = repositoryIds[2]

            let invalidDirectoryParameters = DiffServerTestHelpers.getDiffParameters repositoryId Guid.Empty (Guid.NewGuid())

            let! invalidDirectoryResponse = Client.PostAsync("/diff/getDiff", createJsonContent invalidDirectoryParameters)
            let! invalidDirectoryBody = invalidDirectoryResponse.Content.ReadAsStringAsync()
            Assert.That(invalidDirectoryResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), invalidDirectoryBody)
            let invalidDirectoryError = deserialize<GraceError> invalidDirectoryBody
            Assert.That(invalidDirectoryError.Error, Is.EqualTo(DiffError.getErrorMessage DiffError.InvalidDirectoryVersionId))
            Assert.That(invalidDirectoryError.CorrelationId, Is.Not.Empty)

            let invalidHashParameters = DiffServerTestHelpers.getDiffBySha256HashParameters repositoryId "not-a-sha" "also-not-a-sha"

            let! invalidHashResponse = Client.PostAsync("/diff/getDiffBySha256Hash", createJsonContent invalidHashParameters)
            let! invalidHashBody = invalidHashResponse.Content.ReadAsStringAsync()
            Assert.That(invalidHashResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), invalidHashBody)
            let invalidHashError = deserialize<GraceError> invalidHashBody
            Assert.That(invalidHashError.Error, Is.EqualTo(DiffError.getErrorMessage DiffError.InvalidSha256Hash))
            Assert.That(invalidHashError.CorrelationId, Is.Not.Empty)
        }

    [<Test>]
    member _.GetDiffByBlake3HashFullHashMatchesSha256Diff() =
        task {
            let repositoryId = repositoryIds[0]
            let child = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId $"/src/{Guid.NewGuid():N}/" []
            let rootWithChild = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId "/" [ child ]
            let emptyRoot = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId $"/empty-{Guid.NewGuid():N}/" []

            do! DirectoryVersionServerTestHelpers.createDirectoryVersionAsync child
            do! DirectoryVersionServerTestHelpers.createDirectoryVersionAsync rootWithChild
            do! DirectoryVersionServerTestHelpers.createDirectoryVersionAsync emptyRoot

            let! blake3Response =
                Client.PostAsync(
                    "/diff/getDiffByBlake3Hash",
                    createJsonContent (DiffServerTestHelpers.getDiffByBlake3HashParameters repositoryId rootWithChild.Blake3Hash emptyRoot.Blake3Hash)
                )

            let! blake3Body = blake3Response.Content.ReadAsStringAsync()
            Assert.That(blake3Response.StatusCode, Is.EqualTo(HttpStatusCode.OK), blake3Body)
            let blake3Diff = deserialize<GraceReturnValue<DiffDto>> blake3Body

            let! shaResponse =
                Client.PostAsync(
                    "/diff/getDiffBySha256Hash",
                    createJsonContent (DiffServerTestHelpers.getDiffBySha256HashParameters repositoryId rootWithChild.Sha256Hash emptyRoot.Sha256Hash)
                )

            let! shaBody = shaResponse.Content.ReadAsStringAsync()
            Assert.That(shaResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), shaBody)
            let shaDiff = deserialize<GraceReturnValue<DiffDto>> shaBody

            Assert.That(blake3Diff.ReturnValue.HasDifferences, Is.EqualTo(shaDiff.ReturnValue.HasDifferences))
            Assert.That(blake3Diff.ReturnValue.Differences.Count, Is.EqualTo(shaDiff.ReturnValue.Differences.Count))
            Assert.That(blake3Diff.ReturnValue.DirectoryVersionId1, Is.EqualTo(shaDiff.ReturnValue.DirectoryVersionId1))
            Assert.That(blake3Diff.ReturnValue.DirectoryVersionId2, Is.EqualTo(shaDiff.ReturnValue.DirectoryVersionId2))
        }

    [<Test>]
    member _.GetDiffByBlake3HashRejectsMalformedZeroAndAmbiguousInputsDistinctly() =
        task {
            let repositoryId = repositoryIds[1]
            let first, second, sharedPrefix = DiffServerTestHelpers.createSameBlake3PrefixDirectoryPair repositoryId $"ambiguous-diff-{Guid.NewGuid():N}"
            do! DirectoryVersionServerTestHelpers.createDirectoryVersionAsync first
            do! DirectoryVersionServerTestHelpers.createDirectoryVersionAsync second

            let malformed = DiffServerTestHelpers.getDiffByBlake3HashParameters repositoryId (Blake3Hash "not-a-blake3") first.Blake3Hash

            let! malformedResponse = Client.PostAsync("/diff/getDiffByBlake3Hash", createJsonContent malformed)
            do! DirectoryVersionServerTestHelpers.assertBadRequestGraceError (DiffError.getErrorMessage DiffError.InvalidBlake3Hash) malformedResponse

            let zero =
                DiffServerTestHelpers.getDiffByBlake3HashParameters
                    repositoryId
                    (Blake3Hash "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
                    first.Blake3Hash

            let! zeroResponse = Client.PostAsync("/diff/getDiffByBlake3Hash", createJsonContent zero)
            let! zeroBody = zeroResponse.Content.ReadAsStringAsync()
            Assert.That(zeroResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), zeroBody)
            Assert.That((deserialize<GraceError> zeroBody).Error, Does.Contain("No DirectoryVersion matched Blake3Hash1 prefix"))

            let ambiguous = DiffServerTestHelpers.getDiffByBlake3HashParameters repositoryId (Blake3Hash sharedPrefix) first.Blake3Hash

            let! ambiguousResponse = Client.PostAsync("/diff/getDiffByBlake3Hash", createJsonContent ambiguous)
            let! ambiguousBody = ambiguousResponse.Content.ReadAsStringAsync()
            Assert.That(ambiguousResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), ambiguousBody)
            Assert.That((deserialize<GraceError> ambiguousBody).Error, Does.Contain("Blake3Hash1 prefix"))
            Assert.That((deserialize<GraceError> ambiguousBody).Error, Does.Contain("ambiguous"))
        }

    [<Test>]
    member _.GetDiffByBlake3HashHandlesIdenticalRootsAndRepositoryScope() =
        task {
            let repositoryId = repositoryIds[2]
            let otherRepositoryId = repositoryIds[0]
            let root = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId $"/scope-{Guid.NewGuid():N}/" []
            do! DirectoryVersionServerTestHelpers.createDirectoryVersionAsync root

            let! identicalResponse =
                Client.PostAsync(
                    "/diff/getDiffByBlake3Hash",
                    createJsonContent (DiffServerTestHelpers.getDiffByBlake3HashParameters repositoryId root.Blake3Hash root.Blake3Hash)
                )

            let! identicalBody = identicalResponse.Content.ReadAsStringAsync()
            Assert.That(identicalResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), identicalBody)
            let identicalDiff = deserialize<GraceReturnValue<DiffDto>> identicalBody
            Assert.That(identicalDiff.ReturnValue.HasDifferences, Is.False)
            Assert.That(identicalDiff.ReturnValue.DirectoryVersionId1, Is.EqualTo(root.DirectoryVersionId))
            Assert.That(identicalDiff.ReturnValue.DirectoryVersionId2, Is.EqualTo(root.DirectoryVersionId))

            let scopedToOtherRepository = DiffServerTestHelpers.getDiffByBlake3HashParameters otherRepositoryId root.Blake3Hash root.Blake3Hash

            let! scopedResponse = Client.PostAsync("/diff/getDiffByBlake3Hash", createJsonContent scopedToOtherRepository)
            let! scopedBody = scopedResponse.Content.ReadAsStringAsync()
            Assert.That(scopedResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), scopedBody)
            Assert.That((deserialize<GraceError> scopedBody).Error, Does.Contain("No DirectoryVersion matched Blake3Hash1 prefix"))
        }
