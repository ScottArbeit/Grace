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

/// Groups shared helpers for directory version server test helpers.
module DirectoryVersionServerTestHelpers =
    /// Captures directory version model values used by the test suite.
    type DirectoryVersionModel = Grace.Types.Common.DirectoryVersion

    /// Normalizes d directory size for hash for stable assertions.
    let normalizedDirectorySizeForHash (directoryVersion: DirectoryVersionModel) =
        if directoryVersion.Size = Constants.InitialDirectorySize then
            0L
        else
            directoryVersion.Size

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
                    (normalizedDirectorySizeForHash directoryVersion)
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
            Constants.InitialDirectorySize

    /// Builds create parameters for route calls.
    let createParameters (directoryVersion: DirectoryVersionModel) =
        let parameters = Parameters.DirectoryVersion.CreateParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- $"{directoryVersion.RepositoryId}"
        parameters.DirectoryVersionId <- $"{directoryVersion.DirectoryVersionId}"
        parameters.DirectoryVersion <- directoryVersion
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds get parameters for route calls.
    let getParameters (repositoryId: string) (directoryVersionId: DirectoryVersionId) =
        let parameters = Parameters.DirectoryVersion.GetParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.DirectoryVersionId <- $"{directoryVersionId}"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds get by SHA256 hash parameters for route calls.
    let getBySha256HashParameters (repositoryId: string) (directoryVersionId: DirectoryVersionId) (sha256Hash: Sha256Hash) =
        let parameters = Parameters.DirectoryVersion.GetBySha256HashParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.DirectoryVersionId <- $"{directoryVersionId}"
        parameters.Sha256Hash <- sha256Hash
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds get by BLAKE3 hash parameters for route calls.
    let getByBlake3HashParameters (repositoryId: string) (blake3Hash: Blake3Hash) =
        let parameters = Parameters.DirectoryVersion.GetByBlake3HashParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.Blake3Hash <- blake3Hash
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Builds save parameters for route calls.
    let saveParameters (repositoryId: string) (directoryVersions: DirectoryVersionModel seq) =
        let parameters = Parameters.DirectoryVersion.SaveDirectoryVersionsParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CorrelationId <- generateCorrelationId ()

        for directoryVersion in directoryVersions do
            parameters.DirectoryVersions.Add(directoryVersion)

        parameters

    /// Builds get by directory ids parameters for route calls.
    let getByDirectoryIdsParameters (repositoryId: string) (directoryVersionIds: DirectoryVersionId seq) =
        let parameters = Parameters.DirectoryVersion.GetByDirectoryIdsParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CorrelationId <- generateCorrelationId ()

        for directoryVersionId in directoryVersionIds do
            parameters.DirectoryIds.Add(directoryVersionId)

        parameters

    /// Builds get zip file parameters for route calls.
    let getZipFileParameters (repositoryId: string) (directoryVersionId: DirectoryVersionId) =
        let parameters = Parameters.DirectoryVersion.GetZipFileParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.DirectoryVersionId <- $"{directoryVersionId}"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Creates an isolated public repository and returns an observable branch that can publish saves.
    let createPublicRepositoryBranchAsync repositoryNamePrefix =
        task {
            let! repositoryId = BranchServerTestHelpers.createRepositoryAsync repositoryNamePrefix
            do! BranchServerTestHelpers.setRepositoryVisibilityAsync repositoryId "Public"
            let! branches = BranchServerTestHelpers.getRepositoryBranchesAsync repositoryId
            let parentBranch = branches |> Array.exactlyOne

            let! branchId =
                BranchServerTestHelpers.createBranchWithVisibilityAsync
                    Client
                    repositoryId
                    parentBranch
                    $"{repositoryNamePrefix}{Guid.NewGuid():N}"
                    "Public"
                    "RepositoryOwned"

            let! branchResponse = BranchServerTestHelpers.getBranchResponseAsync Client repositoryId branchId
            do! BranchServerTestHelpers.assertOk branchResponse
            let! branchReturnValue = deserializeContent<GraceReturnValue<Branch.BranchDto>> branchResponse
            return repositoryId, branchReturnValue.ReturnValue
        }

    /// Saves directory versions and publishes the supplied root through an observable reference.
    let saveDirectoryVersionsAndReferenceAsync
        (repositoryId: string)
        (branch: Branch.BranchDto)
        (root: DirectoryVersionModel)
        (directoryVersions: DirectoryVersionModel seq)
        =
        task {
            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId directoryVersions

            let! referenceResponse = BranchServerTestHelpers.saveReferenceResponseAsync repositoryId branch root.DirectoryVersionId root.Sha256Hash

            do! BranchServerTestHelpers.assertOk referenceResponse
        }

    /// Deletes the published branch through the hosted route so its references are marked deleted by the test server.
    let deleteBranchReferencesAsync (repositoryId: string) (branchId: BranchId) =
        task {
            let parameters = Parameters.Branch.DeleteBranchParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branchId}"
            parameters.Force <- true
            parameters.DeleteReason <- "Directory version reachability regression."
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/delete", createJsonContent parameters)
            do! BranchServerTestHelpers.assertOk response
        }

    /// Posts a directory get route through the supplied authenticated client.
    let getDirectoryVersionResponseAsync (client: HttpClient) repositoryId directoryVersionId =
        client.PostAsync("/directory/get", createJsonContent (getParameters repositoryId directoryVersionId))

    /// Posts a recursive directory get route through the supplied authenticated client.
    let getDirectoryVersionsRecursiveResponseAsync (client: HttpClient) repositoryId directoryVersionId =
        client.PostAsync("/directory/getDirectoryVersionsRecursive", createJsonContent (getParameters repositoryId directoryVersionId))

    /// Posts a SHA-256 directory hash lookup through the supplied authenticated client.
    let getBySha256HashResponseAsync (client: HttpClient) repositoryId directoryVersionId sha256Hash =
        client.PostAsync("/directory/getBySha256Hash", createJsonContent (getBySha256HashParameters repositoryId directoryVersionId sha256Hash))

    /// Posts a BLAKE3 directory hash lookup through the supplied authenticated client.
    let getByBlake3HashResponseAsync (client: HttpClient) repositoryId blake3Hash =
        client.PostAsync("/directory/getByBlake3Hash", createJsonContent (getByBlake3HashParameters repositoryId blake3Hash))

    /// Posts a batch directory id lookup through the supplied authenticated client.
    let getByDirectoryIdsResponseAsync (client: HttpClient) repositoryId directoryVersionIds =
        client.PostAsync("/directory/getByDirectoryIds", createJsonContent (getByDirectoryIdsParameters repositoryId directoryVersionIds))

    /// Posts a directory zip route through the supplied authenticated client.
    let getZipFileResponseAsync (client: HttpClient) repositoryId directoryVersionId =
        client.PostAsync("/directory/getZipFile", createJsonContent (getZipFileParameters repositoryId directoryVersionId))

    /// Builds a deterministic same SHA256 prefix directory pair for integration setup fixture for the server integration directory Version assertions.
    let createSameSha256PrefixDirectoryPair repositoryId pathPrefix =
        let candidates =
            [|
                for index in 0..512 -> createDirectoryVersion (Guid.NewGuid()) repositoryId (RelativePath $"/{pathPrefix}/{index}/") []
            |]

        candidates
        |> Array.groupBy (fun directoryVersion ->
            (string directoryVersion.Sha256Hash)
                .Substring(0, 2))
        |> Array.tryPick (fun (sharedPrefix, matches) -> if matches.Length >= 2 then Some(matches[0], matches[1], sharedPrefix) else None)
        |> function
            | Some pair -> pair
            | None -> failwith "Could not generate same-prefix SHA-256 directory versions for directory route tests."

    /// Asserts ok for integration responses.
    let assertOk (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("application/json"))
        }

    /// Asserts bad request grace error for integration responses.
    let assertBadRequestGraceError (expectedError: string) (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            let error = deserialize<GraceError> body
            Assert.That(error.Error, Is.EqualTo(expectedError))
            Assert.That(error.CorrelationId, Is.Not.Empty)
        }

    /// Builds a deterministic directory version for integration setup fixture for the server integration directory Version assertions.
    let createDirectoryVersionAsync (directoryVersion: DirectoryVersionModel) =
        task {
            let! response = Client.PostAsync("/directory/create", createJsonContent (createParameters directoryVersion))
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<string>> response
            Assert.That(returnValue.ReturnValue, Is.EqualTo("Directory version command succeeded."))
            Assert.That(returnValue.Properties.ContainsKey(nameof DirectoryVersionId), Is.True)
        }

    /// Gets directory version from the running test server.
    let getDirectoryVersionAsync (repositoryId: string) (directoryVersionId: DirectoryVersionId) =
        task {
            let! response = Client.PostAsync("/directory/get", createJsonContent (getParameters repositoryId directoryVersionId))
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<DirectoryVersionDto>> response
            return returnValue.ReturnValue
        }

    /// Asserts directory version DTO for integration responses.
    let assertDirectoryVersionDto (expected: DirectoryVersionModel) (actual: DirectoryVersionDto) =
        Assert.That(actual.DirectoryVersion.DirectoryVersionId, Is.EqualTo(expected.DirectoryVersionId))
        Assert.That(actual.DirectoryVersion.OwnerId, Is.EqualTo(expected.OwnerId))
        Assert.That(actual.DirectoryVersion.OrganizationId, Is.EqualTo(expected.OrganizationId))
        Assert.That(actual.DirectoryVersion.RepositoryId, Is.EqualTo(expected.RepositoryId))
        Assert.That(actual.DirectoryVersion.RelativePath, Is.EqualTo(expected.RelativePath))
        Assert.That(actual.DirectoryVersion.Sha256Hash, Is.EqualTo(expected.Sha256Hash))
        Assert.That(actual.DirectoryVersion.Blake3Hash, Is.EqualTo(expected.Blake3Hash))
        Assert.That(actual.DirectoryVersion.HashesValidated, Is.True)

    /// Asserts directory version for integration responses.
    let assertDirectoryVersion (expected: DirectoryVersionModel) (actual: DirectoryVersionModel) =
        Assert.That(actual.DirectoryVersionId, Is.EqualTo(expected.DirectoryVersionId))
        Assert.That(actual.OwnerId, Is.EqualTo(expected.OwnerId))
        Assert.That(actual.OrganizationId, Is.EqualTo(expected.OrganizationId))
        Assert.That(actual.RepositoryId, Is.EqualTo(expected.RepositoryId))
        Assert.That(actual.RelativePath, Is.EqualTo(expected.RelativePath))
        Assert.That(actual.Sha256Hash, Is.EqualTo(expected.Sha256Hash))
        Assert.That(actual.Blake3Hash, Is.EqualTo(expected.Blake3Hash))
        Assert.That(actual.HashesValidated, Is.True)

/// Covers directory version server scenarios.
[<NonParallelizable>]
type DirectoryVersionServer() =

    /// Verifies the create get get by SHA and recursive routes preserve directory DTO shape and identity scenario.
    [<Test>]
    member _.CreateGetGetByShaAndRecursiveRoutesPreserveDirectoryDtoShapeAndIdentity() =
        task {
            let! repositoryId, branch = DirectoryVersionServerTestHelpers.createPublicRepositoryBranchAsync "DirectoryPositiveReads"
            let childId = Guid.NewGuid()
            let rootId = Guid.NewGuid()

            let child = DirectoryVersionServerTestHelpers.createDirectoryVersion childId repositoryId "/src/" []

            let root = DirectoryVersionServerTestHelpers.createDirectoryVersion rootId repositoryId "/" [ child ]

            do! DirectoryVersionServerTestHelpers.saveDirectoryVersionsAndReferenceAsync repositoryId branch root [ child; root ]

            let! fetched = DirectoryVersionServerTestHelpers.getDirectoryVersionAsync repositoryId rootId
            DirectoryVersionServerTestHelpers.assertDirectoryVersionDto root fetched

            let getByShaParameters = DirectoryVersionServerTestHelpers.getBySha256HashParameters repositoryId rootId root.Sha256Hash

            let! getByShaResponse = Client.PostAsync("/directory/getBySha256Hash", createJsonContent getByShaParameters)
            do! DirectoryVersionServerTestHelpers.assertOk getByShaResponse
            let! getBySha = deserializeContent<GraceReturnValue<DirectoryVersionServerTestHelpers.DirectoryVersionModel>> getByShaResponse

            DirectoryVersionServerTestHelpers.assertDirectoryVersion root getBySha.ReturnValue

            let getByBlake3Parameters = DirectoryVersionServerTestHelpers.getByBlake3HashParameters repositoryId root.Blake3Hash

            let! getByBlake3Response = Client.PostAsync("/directory/getByBlake3Hash", createJsonContent getByBlake3Parameters)
            do! DirectoryVersionServerTestHelpers.assertOk getByBlake3Response
            let! getByBlake3 = deserializeContent<GraceReturnValue<DirectoryVersionServerTestHelpers.DirectoryVersionModel>> getByBlake3Response

            DirectoryVersionServerTestHelpers.assertDirectoryVersion root getByBlake3.ReturnValue

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

    /// Verifies the get by SHA returns default sentinel when no directory version matches scenario.
    [<Test>]
    member _.GetByShaReturnsDefaultSentinelWhenNoDirectoryVersionMatches() =
        task {
            let repositoryId = repositoryIds[0]
            let missingDirectoryId = Guid.NewGuid()
            let missingSha256Hash = Sha256Hash "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
            let getByShaParameters = DirectoryVersionServerTestHelpers.getBySha256HashParameters repositoryId missingDirectoryId missingSha256Hash

            let! getByShaResponse = Client.PostAsync("/directory/getBySha256Hash", createJsonContent getByShaParameters)
            do! DirectoryVersionServerTestHelpers.assertOk getByShaResponse
            let! getBySha = deserializeContent<GraceReturnValue<DirectoryVersionServerTestHelpers.DirectoryVersionModel>> getByShaResponse

            Assert.That(getBySha.ReturnValue.DirectoryVersionId, Is.EqualTo(DirectoryVersion.Default.DirectoryVersionId))
            Assert.That(getBySha.ReturnValue.Sha256Hash, Is.EqualTo(DirectoryVersion.Default.Sha256Hash))
            Assert.That(getBySha.ReturnValue.Blake3Hash, Is.EqualTo(DirectoryVersion.Default.Blake3Hash))
        }

    /// Verifies the get by SHA rejects malformed prefixes before lookup scenario.
    [<TestCase("not-a-sha")>]
    [<TestCase("f")>]
    member _.GetByShaRejectsMalformedPrefixesBeforeLookup(sha256Hash: string) =
        task {
            let repositoryId = repositoryIds[0]
            let getByShaParameters = DirectoryVersionServerTestHelpers.getBySha256HashParameters repositoryId (Guid.NewGuid()) (Sha256Hash sha256Hash)

            let! getByShaResponse = Client.PostAsync("/directory/getBySha256Hash", createJsonContent getByShaParameters)
            let! getByShaBody = getByShaResponse.Content.ReadAsStringAsync()

            Assert.That(getByShaResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), getByShaBody)
            Assert.That((deserialize<GraceError> getByShaBody).Error, Is.EqualTo(DirectoryVersionError.getErrorMessage DirectoryVersionError.InvalidSha256Hash))
        }

    /// Verifies the get by SHA rejects ambiguous prefix instead of returning default sentinel scenario.
    [<Test>]
    member _.GetByShaRejectsAmbiguousPrefixInsteadOfReturningDefaultSentinel() =
        task {
            let! repositoryId, branch = DirectoryVersionServerTestHelpers.createPublicRepositoryBranchAsync "DirectoryAmbiguousSha"

            /// Defines first behavior for the surrounding tests used by the server integration directory Version scenario.
            let first, second, sharedPrefix =
                DirectoryVersionServerTestHelpers.createSameSha256PrefixDirectoryPair repositoryId $"ambiguous-directory-sha/{Guid.NewGuid():N}"

            let root = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId "/" [ first; second ]
            do! DirectoryVersionServerTestHelpers.saveDirectoryVersionsAndReferenceAsync repositoryId branch root [ first; second; root ]

            let getByShaParameters = DirectoryVersionServerTestHelpers.getBySha256HashParameters repositoryId first.DirectoryVersionId (Sha256Hash sharedPrefix)

            let! getByShaResponse = Client.PostAsync("/directory/getBySha256Hash", createJsonContent getByShaParameters)
            let! getByShaBody = getByShaResponse.Content.ReadAsStringAsync()

            Assert.That(getByShaResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), getByShaBody)
            Assert.That((deserialize<GraceError> getByShaBody).Error, Does.Contain("ambiguous"))
        }

    /// Verifies the save directory versions creates missing directories and keeps missing get as grace error scenario.
    [<Test>]
    member _.SaveDirectoryVersionsCreatesMissingDirectoriesAndKeepsMissingGetAsGraceError() =
        task {
            let! repositoryId, branch = DirectoryVersionServerTestHelpers.createPublicRepositoryBranchAsync "DirectorySaveGet"
            let childId = Guid.NewGuid()
            let rootId = Guid.NewGuid()

            let child = DirectoryVersionServerTestHelpers.createDirectoryVersion childId repositoryId "/docs/" []

            let root = DirectoryVersionServerTestHelpers.createDirectoryVersion rootId repositoryId "/" [ child ]

            do! DirectoryVersionServerTestHelpers.saveDirectoryVersionsAndReferenceAsync repositoryId branch root [ child; root ]

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

    /// Verifies the save directory versions orders dot root after direct children when input is root first scenario.
    [<Test>]
    member _.SaveDirectoryVersionsOrdersDotRootAfterDirectChildrenWhenInputIsRootFirst() =
        task {
            let! repositoryId, branch = DirectoryVersionServerTestHelpers.createPublicRepositoryBranchAsync "DirectorySaveOrder"
            let childId = Guid.NewGuid()
            let rootId = Guid.NewGuid()

            let child = DirectoryVersionServerTestHelpers.createDirectoryVersion childId repositoryId "src" []

            let root = DirectoryVersionServerTestHelpers.createDirectoryVersion rootId repositoryId "." [ child ]
            let referenceRoot = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId "/" [ root ]

            let! saveResponse =
                Client.PostAsync(
                    "/directory/saveDirectoryVersions",
                    createJsonContent (DirectoryVersionServerTestHelpers.saveParameters repositoryId [ root; child; referenceRoot ])
                )

            do! DirectoryVersionServerTestHelpers.assertOk saveResponse
            let! saveReturnValue = deserializeContent<GraceReturnValue<string>> saveResponse
            Assert.That(saveReturnValue.ReturnValue, Is.EqualTo("Uploaded new directory versions."))

            let! referenceResponse =
                BranchServerTestHelpers.saveReferenceResponseAsync repositoryId branch referenceRoot.DirectoryVersionId referenceRoot.Sha256Hash

            do! BranchServerTestHelpers.assertOk referenceResponse

            let! fetchedRoot = DirectoryVersionServerTestHelpers.getDirectoryVersionAsync repositoryId rootId
            DirectoryVersionServerTestHelpers.assertDirectoryVersionDto root fetchedRoot
            Assert.That(fetchedRoot.DirectoryVersion.Directories, Has.Count.EqualTo(1))
            Assert.That(fetchedRoot.DirectoryVersion.Directories, Does.Contain(childId))

            let! fetchedChild = DirectoryVersionServerTestHelpers.getDirectoryVersionAsync repositoryId childId
            DirectoryVersionServerTestHelpers.assertDirectoryVersionDto child fetchedChild
        }

    /// Verifies hidden directory ids are treated as absent for direct, batch, recursive, and zip directory routes.
    [<Test>]
    member _.HiddenDirectoryVersionIdReadsBehaveAsAbsentForRepositoryReader() =
        task {
            let creatorUserId = $"{Guid.NewGuid()}"
            let observerUserId = $"{Guid.NewGuid()}"
            let! repositoryId = BranchServerTestHelpers.createRepositoryAsync "DirectoryHiddenIdReads"

            do! BranchServerTestHelpers.setRepositoryVisibilityAsync repositoryId "Public"
            do! BranchServerTestHelpers.grantRepositoryRoleAsync repositoryId creatorUserId "RepositoryContributor"
            do! BranchServerTestHelpers.grantRepositoryRoleAsync repositoryId observerUserId "RepositoryReader"

            use creatorClient = BranchServerTestHelpers.createClientWithUserId creatorUserId
            use observerClient = BranchServerTestHelpers.createClientWithUserId observerUserId

            let! observerBranches = BranchServerTestHelpers.getRepositoryBranchesWithClientAsync observerClient repositoryId
            let parentBranch = observerBranches |> Array.exactlyOne

            let! privateBranchId =
                BranchServerTestHelpers.createBranchWithVisibilityAsync
                    creatorClient
                    repositoryId
                    parentBranch
                    $"DirectoryHiddenId{Guid.NewGuid():N}"
                    "Private"
                    "ContributorOwned"

            let! privateBranchResponse = BranchServerTestHelpers.getBranchResponseAsync creatorClient repositoryId privateBranchId
            do! BranchServerTestHelpers.assertOk privateBranchResponse
            let! privateBranchReturnValue = deserializeContent<GraceReturnValue<Branch.BranchDto>> privateBranchResponse
            let privateBranch = privateBranchReturnValue.ReturnValue

            let hiddenChild = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId $"/hidden/{Guid.NewGuid():N}/" []

            let hiddenRoot = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId "/" [ hiddenChild ]

            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ hiddenChild; hiddenRoot ]

            let! hiddenReference =
                BranchServerTestHelpers.saveReferenceWithClientResponseAsync
                    creatorClient
                    repositoryId
                    privateBranch
                    hiddenRoot.DirectoryVersionId
                    hiddenRoot.Sha256Hash

            do! BranchServerTestHelpers.assertOk hiddenReference

            let expectedMissing = DirectoryVersionError.getErrorMessage DirectoryVersionError.DirectoryDoesNotExist

            let! getResponse = DirectoryVersionServerTestHelpers.getDirectoryVersionResponseAsync observerClient repositoryId hiddenRoot.DirectoryVersionId

            do! DirectoryVersionServerTestHelpers.assertBadRequestGraceError expectedMissing getResponse

            let! recursiveResponse =
                DirectoryVersionServerTestHelpers.getDirectoryVersionsRecursiveResponseAsync observerClient repositoryId hiddenRoot.DirectoryVersionId

            do! DirectoryVersionServerTestHelpers.assertBadRequestGraceError expectedMissing recursiveResponse

            let! batchResponse = DirectoryVersionServerTestHelpers.getByDirectoryIdsResponseAsync observerClient repositoryId [ hiddenRoot.DirectoryVersionId ]

            do! DirectoryVersionServerTestHelpers.assertBadRequestGraceError expectedMissing batchResponse

            let! zipResponse = DirectoryVersionServerTestHelpers.getZipFileResponseAsync observerClient repositoryId hiddenRoot.DirectoryVersionId

            do! DirectoryVersionServerTestHelpers.assertBadRequestGraceError expectedMissing zipResponse
        }

    /// Verifies deleted references do not authorize directory id, hash, recursive, or zip reads.
    [<Test>]
    member _.DeletedReferenceDirectoryReadsBehaveAsAbsentForRepositoryReader() =
        task {
            let! repositoryId, branch = DirectoryVersionServerTestHelpers.createPublicRepositoryBranchAsync "DirectoryDeletedReferenceReads"
            let child = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId $"/deleted/{Guid.NewGuid():N}/" []

            let root = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId "/" [ child ]

            do! DirectoryVersionServerTestHelpers.saveDirectoryVersionsAndReferenceAsync repositoryId branch root [ child; root ]

            let! activeGetResponse = DirectoryVersionServerTestHelpers.getDirectoryVersionResponseAsync Client repositoryId root.DirectoryVersionId
            do! DirectoryVersionServerTestHelpers.assertOk activeGetResponse

            let! activeHashResponse = DirectoryVersionServerTestHelpers.getBySha256HashResponseAsync Client repositoryId root.DirectoryVersionId root.Sha256Hash

            do! DirectoryVersionServerTestHelpers.assertOk activeHashResponse

            let! branchAfterSave = BranchServerTestHelpers.getBranchAsync repositoryId $"{branch.BranchId}"
            Assert.That(branchAfterSave.LatestSave.ReferenceId, Is.Not.EqualTo(ReferenceId.Empty))
            do! DirectoryVersionServerTestHelpers.deleteBranchReferencesAsync repositoryId branchAfterSave.BranchId

            let expectedMissing = DirectoryVersionError.getErrorMessage DirectoryVersionError.DirectoryDoesNotExist

            let! getResponse = DirectoryVersionServerTestHelpers.getDirectoryVersionResponseAsync Client repositoryId root.DirectoryVersionId

            do! DirectoryVersionServerTestHelpers.assertBadRequestGraceError expectedMissing getResponse

            let! recursiveResponse = DirectoryVersionServerTestHelpers.getDirectoryVersionsRecursiveResponseAsync Client repositoryId root.DirectoryVersionId

            do! DirectoryVersionServerTestHelpers.assertBadRequestGraceError expectedMissing recursiveResponse

            let! batchResponse = DirectoryVersionServerTestHelpers.getByDirectoryIdsResponseAsync Client repositoryId [ root.DirectoryVersionId ]

            do! DirectoryVersionServerTestHelpers.assertBadRequestGraceError expectedMissing batchResponse

            let! zipResponse = DirectoryVersionServerTestHelpers.getZipFileResponseAsync Client repositoryId root.DirectoryVersionId

            do! DirectoryVersionServerTestHelpers.assertBadRequestGraceError expectedMissing zipResponse

            let! deletedShaResponse = DirectoryVersionServerTestHelpers.getBySha256HashResponseAsync Client repositoryId root.DirectoryVersionId root.Sha256Hash

            do! DirectoryVersionServerTestHelpers.assertOk deletedShaResponse
            let! deletedSha = deserializeContent<GraceReturnValue<DirectoryVersionServerTestHelpers.DirectoryVersionModel>> deletedShaResponse
            Assert.That(deletedSha.ReturnValue.DirectoryVersionId, Is.EqualTo(DirectoryVersion.Default.DirectoryVersionId))

            let deletedBlakeLookup = $"{root.Blake3Hash}"

            let! deletedBlakeResponse = DirectoryVersionServerTestHelpers.getByBlake3HashResponseAsync Client repositoryId (Blake3Hash deletedBlakeLookup)

            do!
                DirectoryVersionServerTestHelpers.assertBadRequestGraceError
                    $"No DirectoryVersion matched the supplied BLAKE3 hash prefix '{deletedBlakeLookup}' in repository scope."
                    deletedBlakeResponse
        }

    /// Verifies hidden directory hashes are no-match-equivalent and hidden same-prefix roots do not create ambiguity.
    [<Test>]
    member _.DirectoryHashResolutionUsesVisibleCandidateSubsetOnly() =
        task {
            let creatorUserId = $"{Guid.NewGuid()}"
            let observerUserId = $"{Guid.NewGuid()}"
            let! repositoryId = BranchServerTestHelpers.createRepositoryAsync "DirectoryVisibleHashSubset"

            do! BranchServerTestHelpers.setRepositoryVisibilityAsync repositoryId "Public"
            do! BranchServerTestHelpers.grantRepositoryRoleAsync repositoryId creatorUserId "RepositoryContributor"
            do! BranchServerTestHelpers.grantRepositoryRoleAsync repositoryId observerUserId "RepositoryReader"

            use creatorClient = BranchServerTestHelpers.createClientWithUserId creatorUserId
            use observerClient = BranchServerTestHelpers.createClientWithUserId observerUserId

            let! observerBranches = BranchServerTestHelpers.getRepositoryBranchesWithClientAsync observerClient repositoryId
            let parentBranch = observerBranches |> Array.exactlyOne

            let! visibleBranchId =
                BranchServerTestHelpers.createBranchWithVisibilityAsync
                    creatorClient
                    repositoryId
                    parentBranch
                    $"DirectoryVisibleHash{Guid.NewGuid():N}"
                    "Public"
                    "RepositoryOwned"

            let! hiddenBranchId =
                BranchServerTestHelpers.createBranchWithVisibilityAsync
                    creatorClient
                    repositoryId
                    parentBranch
                    $"DirectoryHiddenHash{Guid.NewGuid():N}"
                    "Private"
                    "ContributorOwned"

            let! visibleBranchResponse = BranchServerTestHelpers.getBranchResponseAsync creatorClient repositoryId visibleBranchId
            do! BranchServerTestHelpers.assertOk visibleBranchResponse
            let! visibleBranchReturnValue = deserializeContent<GraceReturnValue<Branch.BranchDto>> visibleBranchResponse
            let visibleBranch = visibleBranchReturnValue.ReturnValue

            let! hiddenBranchResponse = BranchServerTestHelpers.getBranchResponseAsync creatorClient repositoryId hiddenBranchId
            do! BranchServerTestHelpers.assertOk hiddenBranchResponse
            let! hiddenBranchReturnValue = deserializeContent<GraceReturnValue<Branch.BranchDto>> hiddenBranchResponse
            let hiddenBranch = hiddenBranchReturnValue.ReturnValue

            let visibleRoot, hiddenRoot, sharedShaPrefix =
                DirectoryVersionServerTestHelpers.createSameSha256PrefixDirectoryPair repositoryId $"visible-hidden-sha/{Guid.NewGuid():N}"

            let visibleReferenceRoot = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId "/" [ visibleRoot ]

            let hiddenReferenceRoot = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId "/" [ hiddenRoot ]

            let _visibleBlakeChild, _visibleBlakeRoot, hiddenBlakeChild, hiddenBlakeRoot, _hiddenBlakePrefix =
                BranchServerTestHelpers.createSameBlake3PrefixRootPair repositoryId $"hidden-blake-only/{Guid.NewGuid():N}"

            do!
                BranchServerTestHelpers.saveDirectoryVersionsAsync
                    repositoryId
                    [
                        visibleRoot
                        hiddenRoot
                        visibleReferenceRoot
                        hiddenReferenceRoot
                        hiddenBlakeChild
                        hiddenBlakeRoot
                    ]

            let! visibleReference =
                BranchServerTestHelpers.saveReferenceWithClientResponseAsync
                    creatorClient
                    repositoryId
                    visibleBranch
                    visibleReferenceRoot.DirectoryVersionId
                    visibleReferenceRoot.Sha256Hash

            do! BranchServerTestHelpers.assertOk visibleReference

            let! hiddenReference =
                BranchServerTestHelpers.saveReferenceWithClientResponseAsync
                    creatorClient
                    repositoryId
                    hiddenBranch
                    hiddenReferenceRoot.DirectoryVersionId
                    hiddenReferenceRoot.Sha256Hash

            do! BranchServerTestHelpers.assertOk hiddenReference

            let! hiddenBlakeReference =
                BranchServerTestHelpers.saveReferenceWithClientResponseAsync
                    creatorClient
                    repositoryId
                    hiddenBranch
                    hiddenBlakeRoot.DirectoryVersionId
                    hiddenBlakeRoot.Sha256Hash

            do! BranchServerTestHelpers.assertOk hiddenBlakeReference

            let! hiddenOnlyShaResponse =
                DirectoryVersionServerTestHelpers.getBySha256HashResponseAsync observerClient repositoryId hiddenRoot.DirectoryVersionId hiddenRoot.Sha256Hash

            do! DirectoryVersionServerTestHelpers.assertOk hiddenOnlyShaResponse
            let! hiddenOnlySha = deserializeContent<GraceReturnValue<DirectoryVersionServerTestHelpers.DirectoryVersionModel>> hiddenOnlyShaResponse
            Assert.That(hiddenOnlySha.ReturnValue.DirectoryVersionId, Is.EqualTo(DirectoryVersion.Default.DirectoryVersionId))

            let hiddenBlakeLookup = $"{hiddenBlakeRoot.Blake3Hash}"

            let! hiddenOnlyBlakeResponse =
                DirectoryVersionServerTestHelpers.getByBlake3HashResponseAsync observerClient repositoryId (Blake3Hash hiddenBlakeLookup)

            do!
                DirectoryVersionServerTestHelpers.assertBadRequestGraceError
                    $"No DirectoryVersion matched the supplied BLAKE3 hash prefix '{hiddenBlakeLookup}' in repository scope."
                    hiddenOnlyBlakeResponse

            let! visibleSharedPrefixResponse =
                DirectoryVersionServerTestHelpers.getBySha256HashResponseAsync
                    observerClient
                    repositoryId
                    visibleRoot.DirectoryVersionId
                    (Sha256Hash sharedShaPrefix)

            do! DirectoryVersionServerTestHelpers.assertOk visibleSharedPrefixResponse
            let! visibleSharedPrefix = deserializeContent<GraceReturnValue<DirectoryVersionServerTestHelpers.DirectoryVersionModel>> visibleSharedPrefixResponse

            DirectoryVersionServerTestHelpers.assertDirectoryVersion visibleRoot visibleSharedPrefix.ReturnValue
        }
