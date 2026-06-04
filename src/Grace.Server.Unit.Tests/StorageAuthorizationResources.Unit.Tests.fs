namespace Grace.Server.Tests

open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Parameters
open Grace.Types.Authorization
open Grace.Types.Common
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
type StorageAuthorizationResourcesTests() =
    let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")

    let fileVersion (relativePath: RelativePath) =
        let value = FileVersion()
        value.RelativePath <- relativePath
        value

    let assertPathResource (expectedPath: RelativePath) resource =
        match resource with
        | Resource.Path (actualOwnerId, actualOrganizationId, actualRepositoryId, actualPath) ->
            Assert.That(actualOwnerId, Is.EqualTo(ownerId))
            Assert.That(actualOrganizationId, Is.EqualTo(organizationId))
            Assert.That(actualRepositoryId, Is.EqualTo(repositoryId))
            Assert.That(actualPath, Is.EqualTo(expectedPath))
        | other -> Assert.Fail($"Expected Resource.Path but received {other}.")

    [<Test>]
    member _.UploadMetadataFileVersionsMapToPathResources() =
        let parameters = Storage.GetUploadMetadataForFilesParameters()

        parameters.FileVersions <-
            [|
                fileVersion "src/app.fs"
                fileVersion "docs/readme.md"
            |]

        let resources = StorageAuthorizationResources.uploadMetadataResources ownerId organizationId repositoryId parameters

        Assert.That(resources.Length, Is.EqualTo(2))
        assertPathResource "src/app.fs" resources[0]
        assertPathResource "docs/readme.md" resources[1]

    [<Test>]
    member _.UploadUriFileVersionsMapToPathResources() =
        let parameters = Storage.GetUploadUriParameters()

        parameters.FileVersions <-
            [|
                fileVersion "src/upload.fs"
                fileVersion "assets/icon.png"
            |]

        let resources = StorageAuthorizationResources.uploadUriResources ownerId organizationId repositoryId parameters

        Assert.That(resources.Length, Is.EqualTo(2))
        assertPathResource "src/upload.fs" resources[0]
        assertPathResource "assets/icon.png" resources[1]

    [<Test>]
    member _.DownloadUriFileVersionMapsToSinglePathResource() =
        let parameters = Storage.GetDownloadUriParameters()
        parameters.FileVersion <- fileVersion "src/download.fs"

        let resource = StorageAuthorizationResources.downloadUriResource ownerId organizationId repositoryId parameters

        assertPathResource "src/download.fs" resource

    [<Test>]
    member _.ContentBlockUploadHonorsExplicitAuthorizedScope() =
        let parameters = Storage.GetContentBlockUploadUriParameters()
        parameters.ContentBlockAddress <- "sha256-explicit"
        parameters.AuthorizedScope <- "manifest/file.bin"

        let resource = StorageAuthorizationResources.contentBlockUploadResource ownerId organizationId repositoryId parameters

        assertPathResource "manifest/file.bin" resource

    [<Test>]
    member _.ContentBlockUploadFallsBackToContentBlockObjectKeyWhenScopeIsBlank() =
        let parameters = Storage.GetContentBlockUploadUriParameters()
        parameters.ContentBlockAddress <- "sha256-blank-scope"
        parameters.AuthorizedScope <- "   "

        let resource = StorageAuthorizationResources.contentBlockUploadResource ownerId organizationId repositoryId parameters

        assertPathResource (StorageKeys.contentBlockObjectKey "sha256-blank-scope") resource

    [<Test>]
    member _.ContentBlockDownloadUsesContentBlockObjectKey() =
        let parameters = Storage.GetContentBlockDownloadUriParameters()
        parameters.ContentBlockAddress <- "sha256-download"

        let resource = StorageAuthorizationResources.contentBlockDownloadResource ownerId organizationId repositoryId parameters

        assertPathResource (StorageKeys.contentBlockObjectKey "sha256-download") resource

    [<Test>]
    member _.UploadSessionStorageUsesAuthorizedScope() =
        let parameters = Storage.UploadSessionStorageParameters()
        parameters.AuthorizedScope <- "sessions/session-file.bin"

        let resource = StorageAuthorizationResources.uploadSessionResource ownerId organizationId repositoryId parameters

        assertPathResource "sessions/session-file.bin" resource

    [<Test>]
    member _.EmptyUploadFileListReturnsEmptyResourceList() =
        let metadataParameters = Storage.GetUploadMetadataForFilesParameters()
        let uploadUriParameters = Storage.GetUploadUriParameters()

        let metadataResources = StorageAuthorizationResources.uploadMetadataResources ownerId organizationId repositoryId metadataParameters

        let uploadUriResources = StorageAuthorizationResources.uploadUriResources ownerId organizationId repositoryId uploadUriParameters

        Assert.That(metadataResources, Is.Empty)
        Assert.That(uploadUriResources, Is.Empty)
