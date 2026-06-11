namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open NUnit.Framework
open System
open System.Collections.Generic
open System.Net
open System.Net.Http

module DirectoryVersionServerTestHelpers =
    type DirectoryVersionModel = Grace.Types.Common.DirectoryVersion

    let createDirectoryVersion
        (directoryVersionId: DirectoryVersionId)
        (repositoryId: string)
        (relativePath: RelativePath)
        (childDirectoryVersions: DirectoryVersionModel seq)
        : DirectoryVersionModel
        =
        let childDirectoryVersions = childDirectoryVersions |> Seq.toArray

        let childDirectoryIds =
            childDirectoryVersions
            |> Seq.map (fun directoryVersion -> directoryVersion.DirectoryVersionId)

        let entries =
            childDirectoryVersions
            |> Seq.map (fun directoryVersion ->
                DirectoryVersionPreimageEntry.Directory
                    directoryVersion.RelativePath
                    directoryVersion.Size
                    directoryVersion.Blake3Hash
                    directoryVersion.Sha256Hash)
            |> Seq.toArray

        let sha256Hash = computeSha256ForDirectoryEntries relativePath entries
        let blake3Hash = computeBlake3ForDirectory relativePath entries

        DirectoryVersionModel.CreateWithHashes
            directoryVersionId
            (Guid.Parse ownerId)
            (Guid.Parse organizationId)
            (Guid.Parse repositoryId)
            relativePath
            sha256Hash
            blake3Hash
            (List<DirectoryVersionId>(childDirectoryIds))
            (List<FileVersion>())
            0L

    let createParameters (directoryVersion: DirectoryVersionModel) =
        let parameters = Parameters.DirectoryVersion.CreateParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- $"{directoryVersion.RepositoryId}"
        parameters.DirectoryVersionId <- $"{directoryVersion.DirectoryVersionId}"
        parameters.DirectoryVersion <- directoryVersion
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let getParameters (repositoryId: string) (directoryVersionId: DirectoryVersionId) =
        let parameters = Parameters.DirectoryVersion.GetParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.DirectoryVersionId <- $"{directoryVersionId}"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let getBySha256HashParameters (repositoryId: string) (directoryVersionId: DirectoryVersionId) (sha256Hash: Sha256Hash) =
        let parameters = Parameters.DirectoryVersion.GetBySha256HashParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.DirectoryVersionId <- $"{directoryVersionId}"
        parameters.Sha256Hash <- sha256Hash
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let saveParameters (repositoryId: string) (directoryVersions: DirectoryVersionModel seq) =
        let parameters = Parameters.DirectoryVersion.SaveDirectoryVersionsParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CorrelationId <- generateCorrelationId ()

        for directoryVersion in directoryVersions do
            parameters.DirectoryVersions.Add(directoryVersion)

        parameters

    let assertOk (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("application/json"))
        }

    let assertBadRequestGraceError (expectedError: string) (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            let error = deserialize<GraceError> body
            Assert.That(error.Error, Is.EqualTo(expectedError))
            Assert.That(error.CorrelationId, Is.Not.Empty)
        }

    let createDirectoryVersionAsync (directoryVersion: DirectoryVersionModel) =
        task {
            let! response = Client.PostAsync("/directory/create", createJsonContent (createParameters directoryVersion))
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<string>> response
            Assert.That(returnValue.ReturnValue, Is.EqualTo("Directory version command succeeded."))
            Assert.That(returnValue.Properties.ContainsKey(nameof DirectoryVersionId), Is.True)
        }

    let getDirectoryVersionAsync (repositoryId: string) (directoryVersionId: DirectoryVersionId) =
        task {
            let! response = Client.PostAsync("/directory/get", createJsonContent (getParameters repositoryId directoryVersionId))
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<DirectoryVersionDto>> response
            return returnValue.ReturnValue
        }

    let assertDirectoryVersionDto (expected: DirectoryVersionModel) (actual: DirectoryVersionDto) =
        Assert.That(actual.DirectoryVersion.DirectoryVersionId, Is.EqualTo(expected.DirectoryVersionId))
        Assert.That(actual.DirectoryVersion.OwnerId, Is.EqualTo(expected.OwnerId))
        Assert.That(actual.DirectoryVersion.OrganizationId, Is.EqualTo(expected.OrganizationId))
        Assert.That(actual.DirectoryVersion.RepositoryId, Is.EqualTo(expected.RepositoryId))
        Assert.That(actual.DirectoryVersion.RelativePath, Is.EqualTo(expected.RelativePath))
        Assert.That(actual.DirectoryVersion.Sha256Hash, Is.EqualTo(expected.Sha256Hash))
        Assert.That(actual.DirectoryVersion.Blake3Hash, Is.EqualTo(expected.Blake3Hash))
        Assert.That(actual.DirectoryVersion.HashesValidated, Is.True)

    let assertDirectoryVersion (expected: DirectoryVersionModel) (actual: DirectoryVersionModel) =
        Assert.That(actual.DirectoryVersionId, Is.EqualTo(expected.DirectoryVersionId))
        Assert.That(actual.OwnerId, Is.EqualTo(expected.OwnerId))
        Assert.That(actual.OrganizationId, Is.EqualTo(expected.OrganizationId))
        Assert.That(actual.RepositoryId, Is.EqualTo(expected.RepositoryId))
        Assert.That(actual.RelativePath, Is.EqualTo(expected.RelativePath))
        Assert.That(actual.Sha256Hash, Is.EqualTo(expected.Sha256Hash))
        Assert.That(actual.Blake3Hash, Is.EqualTo(expected.Blake3Hash))
        Assert.That(actual.HashesValidated, Is.True)

[<NonParallelizable>]
type DirectoryVersionServer() =

    [<Test>]
    member _.CreateGetGetByShaAndRecursiveRoutesPreserveDirectoryDtoShapeAndIdentity() =
        task {
            let repositoryId = repositoryIds[0]
            let childId = Guid.NewGuid()
            let rootId = Guid.NewGuid()

            let child = DirectoryVersionServerTestHelpers.createDirectoryVersion childId repositoryId "/src/" []

            let root = DirectoryVersionServerTestHelpers.createDirectoryVersion rootId repositoryId "/" [ child ]

            do! DirectoryVersionServerTestHelpers.createDirectoryVersionAsync child
            do! DirectoryVersionServerTestHelpers.createDirectoryVersionAsync root

            let! fetched = DirectoryVersionServerTestHelpers.getDirectoryVersionAsync repositoryId rootId
            DirectoryVersionServerTestHelpers.assertDirectoryVersionDto root fetched

            let getByShaParameters = DirectoryVersionServerTestHelpers.getBySha256HashParameters repositoryId rootId root.Sha256Hash

            let! getByShaResponse = Client.PostAsync("/directory/getBySha256Hash", createJsonContent getByShaParameters)
            do! DirectoryVersionServerTestHelpers.assertOk getByShaResponse
            let! getBySha = deserializeContent<GraceReturnValue<DirectoryVersionServerTestHelpers.DirectoryVersionModel>> getByShaResponse

            DirectoryVersionServerTestHelpers.assertDirectoryVersion root getBySha.ReturnValue

            let! recursiveResponse =
                Client.PostAsync(
                    "/directory/getDirectoryVersionsRecursive",
                    createJsonContent (DirectoryVersionServerTestHelpers.getParameters repositoryId rootId)
                )

            do! DirectoryVersionServerTestHelpers.assertOk recursiveResponse
            let! recursive = deserializeContent<GraceReturnValue<DirectoryVersionDto array>> recursiveResponse

            let recursiveDirectories =
                recursive.ReturnValue
                |> Seq.map (fun dto -> dto.DirectoryVersion)
                |> Seq.toArray

            Assert.That(recursiveDirectories, Has.Length.EqualTo(2))

            Assert.That(
                recursiveDirectories
                |> Array.exists (fun directoryVersion -> directoryVersion.DirectoryVersionId = rootId),
                Is.True
            )

            Assert.That(
                recursiveDirectories
                |> Array.exists (fun directoryVersion -> directoryVersion.DirectoryVersionId = childId),
                Is.True
            )
        }

    [<Test>]
    member _.SaveDirectoryVersionsCreatesMissingDirectoriesAndKeepsMissingGetAsGraceError() =
        task {
            let repositoryId = repositoryIds[1]
            let childId = Guid.NewGuid()
            let rootId = Guid.NewGuid()

            let child = DirectoryVersionServerTestHelpers.createDirectoryVersion childId repositoryId "/docs/" []

            let root = DirectoryVersionServerTestHelpers.createDirectoryVersion rootId repositoryId "/" [ child ]

            let! saveResponse =
                Client.PostAsync(
                    "/directory/saveDirectoryVersions",
                    createJsonContent (DirectoryVersionServerTestHelpers.saveParameters repositoryId [ child; root ])
                )

            do! DirectoryVersionServerTestHelpers.assertOk saveResponse
            let! saveReturnValue = deserializeContent<GraceReturnValue<string>> saveResponse
            Assert.That(saveReturnValue.ReturnValue, Is.EqualTo("Uploaded new directory versions."))

            let! fetchedRoot = DirectoryVersionServerTestHelpers.getDirectoryVersionAsync repositoryId rootId
            DirectoryVersionServerTestHelpers.assertDirectoryVersionDto root fetchedRoot
            Assert.That(fetchedRoot.DirectoryVersion.Directories, Has.Count.EqualTo(1))
            Assert.That(fetchedRoot.DirectoryVersion.Directories, Does.Contain(childId))

            let! fetchedChild = DirectoryVersionServerTestHelpers.getDirectoryVersionAsync repositoryId childId
            DirectoryVersionServerTestHelpers.assertDirectoryVersionDto child fetchedChild

            let missingId = Guid.NewGuid()

            let! missingResponse =
                Client.PostAsync("/directory/get", createJsonContent (DirectoryVersionServerTestHelpers.getParameters repositoryId missingId))

            do!
                DirectoryVersionServerTestHelpers.assertBadRequestGraceError
                    (DirectoryVersionError.getErrorMessage DirectoryVersionError.DirectoryDoesNotExist)
                    missingResponse
        }
