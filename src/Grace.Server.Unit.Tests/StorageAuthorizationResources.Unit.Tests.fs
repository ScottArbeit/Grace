namespace Grace.Server.Tests

open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Parameters
open Grace.Types.Authorization
open Grace.Types.Common
open NUnit.Framework
open System

/// Covers storage Authorization Resources behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type StorageAuthorizationResourcesTests() =
    let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")

    /// Builds file Version test data for the server unit storage Authorization Resources scenarios in this file.
    let fileVersion (relativePath: RelativePath) =
        let value = FileVersion()
        value.RelativePath <- relativePath
        value

    /// Asserts the path Resource condition so failures identify the violated server unit storage Authorization Resources invariant.
    let assertPathResource (expectedPath: RelativePath) resource =
        match resource with
        | Resource.Path (actualOwnerId, actualOrganizationId, actualRepositoryId, actualPath) ->
            Assert.That(actualOwnerId, Is.EqualTo(ownerId))
            Assert.That(actualOrganizationId, Is.EqualTo(organizationId))
            Assert.That(actualRepositoryId, Is.EqualTo(repositoryId))
            Assert.That(actualPath, Is.EqualTo(expectedPath))
        | other -> Assert.Fail($"Expected Resource.Path but received {other}.")

    /// Asserts the missing Download Scope condition so failures identify the violated server unit storage Authorization Resources invariant.
    let assertMissingDownloadScope (authorizedScope: RelativePath) =
        let parameters = Storage.GetContentBlockDownloadUriParameters()
        parameters.ContentBlockAddress <- "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
        parameters.AuthorizedScope <- authorizedScope

        let result = StorageAuthorizationResources.tryContentBlockDownloadResource "unit-test-correlation" ownerId organizationId repositoryId parameters

        match result with
        | Error error -> Assert.That(error.Error, Is.EqualTo("AuthorizedScope is required for ContentBlock manifest download authorization."))
        | Ok resource -> Assert.Fail($"Expected missing AuthorizedScope to be rejected but received {resource}.")

    /// Verifies that upload Metadata File Versions Map To Path Resources.
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

    /// Verifies that upload Uri File Versions Map To Path Resources.
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

    /// Verifies that download Uri File Version Maps To Single Path Resource.
    [<Test>]
    member _.DownloadUriFileVersionMapsToSinglePathResource() =
        let parameters = Storage.GetDownloadUriParameters()
        parameters.FileVersion <- fileVersion "src/download.fs"

        let resource = StorageAuthorizationResources.downloadUriResource ownerId organizationId repositoryId parameters

        assertPathResource "src/download.fs" resource

    /// Verifies that content Block Upload Honors Explicit Authorized Scope.
    [<Test>]
    member _.ContentBlockUploadHonorsExplicitAuthorizedScope() =
        let parameters = Storage.GetContentBlockUploadUriParameters()
        parameters.ContentBlockAddress <- "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        parameters.AuthorizedScope <- "manifest/file.bin"

        let resource = StorageAuthorizationResources.contentBlockUploadResource ownerId organizationId repositoryId parameters

        assertPathResource "manifest/file.bin" resource

    /// Verifies that content Block Upload Falls Back To Content Block Object Key When Scope Is Blank.
    [<Test>]
    member _.ContentBlockUploadFallsBackToContentBlockObjectKeyWhenScopeIsBlank() =
        let parameters = Storage.GetContentBlockUploadUriParameters()
        parameters.ContentBlockAddress <- "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        parameters.AuthorizedScope <- "   "

        let resource = StorageAuthorizationResources.contentBlockUploadResource ownerId organizationId repositoryId parameters

        assertPathResource (StorageKeys.contentBlockObjectKey "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb") resource

    /// Verifies that content Block Download Uses Explicit Authorized Scope.
    [<Test>]
    member _.ContentBlockDownloadUsesExplicitAuthorizedScope() =
        let parameters = Storage.GetContentBlockDownloadUriParameters()
        parameters.ContentBlockAddress <- "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
        parameters.AuthorizedScope <- "manifest/download.bin"

        let result = StorageAuthorizationResources.tryContentBlockDownloadResource "unit-test-correlation" ownerId organizationId repositoryId parameters

        match result with
        | Ok resource -> assertPathResource "manifest/download.bin" resource
        | Error error -> Assert.Fail($"Expected explicit AuthorizedScope to create a path resource but received {error.Error}.")

    /// Verifies that content Block Download Rejects Missing Authorized Scope Before Path Resource Creation.
    [<Test>]
    member _.ContentBlockDownloadRejectsMissingAuthorizedScopeBeforePathResourceCreation() =
        assertMissingDownloadScope null
        assertMissingDownloadScope String.Empty
        assertMissingDownloadScope "   "

    /// Verifies that upload Session Storage Uses Authorized Scope.
    [<Test>]
    member _.UploadSessionStorageUsesAuthorizedScope() =
        let parameters = Storage.UploadSessionStorageParameters()
        parameters.AuthorizedScope <- "sessions/session-file.bin"

        let resource = StorageAuthorizationResources.uploadSessionResource ownerId organizationId repositoryId parameters

        assertPathResource "sessions/session-file.bin" resource

    /// Verifies that empty Upload File List Returns Empty Resource List.
    [<Test>]
    member _.EmptyUploadFileListReturnsEmptyResourceList() =
        let metadataParameters = Storage.GetUploadMetadataForFilesParameters()
        let uploadUriParameters = Storage.GetUploadUriParameters()

        let metadataResources = StorageAuthorizationResources.uploadMetadataResources ownerId organizationId repositoryId metadataParameters

        let uploadUriResources = StorageAuthorizationResources.uploadUriResources ownerId organizationId repositoryId uploadUriParameters

        Assert.That(metadataResources, Is.Empty)
        Assert.That(uploadUriResources, Is.Empty)
