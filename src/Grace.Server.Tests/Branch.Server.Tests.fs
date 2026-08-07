namespace Grace.Server.Tests

open Azure.Storage.Blobs.Models
open Azure.Storage.Blobs.Specialized
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors
open Grace.Types
open Grace.Types.Annotation
open Grace.Types.Common
open Grace.Types.PersonalAccessToken
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

/// Groups shared helpers for branch server test helpers.
module BranchServerTestHelpers =
    /// Asserts ok for integration responses.
    let private assertOk (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
        }

    /// Asserts bad request grace error for integration responses.
    let private assertBadRequestGraceError (expectedError: string) (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            let error = deserialize<GraceError> body
            Assert.That(error.Error, Is.EqualTo(expectedError))
            Assert.That(error.CorrelationId, Is.Not.Empty)
        }

    /// Builds get branch parameters for route calls.
    let getBranchParameters (repositoryId: string) (branchId: string) =
        let parameters = Parameters.Branch.GetBranchParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.BranchId <- branchId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Gets branch from the running test server.
    let getBranchAsync (repositoryId: string) (branchId: string) =
        task {
            let! response = Client.PostAsync("/branch/get", createJsonContent (getBranchParameters repositoryId branchId))
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<Branch.BranchDto>> response
            return returnValue.ReturnValue
        }

    /// Waits for for branch to become observable in the test host.
    let waitForBranchAsync (repositoryId: string) (branchId: string) =
        task {
            let timeoutAt = DateTime.UtcNow.AddSeconds(15.0)
            let mutable branch = None
            let mutable lastBody = String.Empty
            let mutable lastStatus = HttpStatusCode.OK

            while branch.IsNone && DateTime.UtcNow < timeoutAt do
                let! response = Client.PostAsync("/branch/get", createJsonContent (getBranchParameters repositoryId branchId))
                let! body = response.Content.ReadAsStringAsync()
                lastBody <- body
                lastStatus <- response.StatusCode

                if response.StatusCode = HttpStatusCode.OK then
                    let returnValue = deserialize<GraceReturnValue<Branch.BranchDto>> body
                    branch <- Some returnValue.ReturnValue
                else
                    do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            match branch with
            | Some branch -> return branch
            | None ->
                Assert.Fail($"Timed out waiting for branch {branchId} in repository {repositoryId}. Last status: {lastStatus}; body: {lastBody}")
                return Unchecked.defaultof<Branch.BranchDto>
        }

    /// Builds a deterministic branch for integration setup fixture for the server integration branch assertions.
    let createBranchAsync (repositoryId: string) (parentBranch: Branch.BranchDto) (branchName: string) =
        task {
            let branchId = $"{Guid.NewGuid()}"
            let parameters = Parameters.Branch.CreateBranchParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- branchId
            parameters.BranchName <- branchName
            parameters.ParentBranchId <- $"{parentBranch.BranchId}"
            parameters.ParentBranchName <- $"{parentBranch.BranchName}"
            parameters.ReferenceId <- Guid.NewGuid()
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/create", createJsonContent parameters)
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<string>> response

            Assert.That(returnValue.Properties.ContainsKey(nameof BranchId), Is.True)

            let returnedBranchProperty = returnValue.Properties[nameof BranchId]

            let returnedBranchId = Grace.Server.Tests.Common.requireGuidProperty (nameof BranchId) returnedBranchProperty

            Assert.That(returnedBranchId, Is.EqualTo(Guid.Parse(branchId)))

            return! waitForBranchAsync repositoryId branchId
        }

    /// Gets repository branches from the running test server.
    let getRepositoryBranchesAsync (repositoryId: string) =
        task {
            let parameters = Parameters.Repository.GetBranchesParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.MaxCount <- 100
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/repository/getBranches", createJsonContent parameters)
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<Branch.BranchDto array>> response
            return returnValue.ReturnValue
        }

    /// Gets selected repository branches through the persisted-identity query route.
    let getRepositoryBranchesByIdAsync (repositoryId: string) (branchIds: BranchId array) =
        task {
            let parameters = Parameters.Repository.GetBranchesByBranchIdParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchIds <- branchIds
            parameters.MaxCount <- branchIds.Length
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/repository/getBranchesByBranchId", createJsonContent parameters)
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<Branch.BranchDto array>> response
            return returnValue.ReturnValue
        }

    /// Saves branch through the branch test routes.
    let saveBranchAsync (repositoryId: string) (branch: Branch.BranchDto) =
        task {
            let parameters = Parameters.Branch.CreateReferenceParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.ReferenceId <- Guid.NewGuid()
            parameters.DirectoryVersionId <- branch.BasedOn.DirectoryId
            parameters.Sha256Hash <- $"{branch.BasedOn.Sha256Hash}"
            parameters.Message <- "Hosted branch lifecycle route proof save"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/save", createJsonContent parameters)
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<string>> response

            Assert.That(returnValue.Properties.ContainsKey(nameof BranchId), Is.True)
            Assert.That(returnValue.Properties.ContainsKey(nameof RepositoryId), Is.True)

            return returnValue
        }

    /// Gets branch references from the running test server.
    let getBranchReferencesAsync (repositoryId: string) (branchId: string) =
        task {
            let parameters = Parameters.Branch.GetReferencesParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- branchId
            parameters.MaxCount <- 10
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/getReferences", createJsonContent parameters)
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<Reference.ReferenceDto array>> response
            return returnValue.ReturnValue
        }

    /// Gets branch version from the running test server.
    let getBranchVersionAsync (repositoryId: string) (branchId: string) =
        task {
            let parameters = Parameters.Branch.GetBranchVersionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- branchId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/getVersion", createJsonContent parameters)
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<Guid array>> response
            return returnValue.ReturnValue
        }

    /// Defines sHA256 hex behavior for the surrounding tests used by the server integration branch scenario.
    let sha256Hex (bytes: byte array) =
        SHA256.HashData(bytes)
        |> fun hash -> byteArrayToString (hash.AsSpan())

    /// Defines bLAKE3 hex behavior for the surrounding tests used by the server integration branch scenario.
    let blake3Hex (bytes: byte array) = ContentAddress.computeBlake3Hex bytes

    /// Builds a deterministic root directory version for integration setup fixture for the server integration branch assertions.
    let createRootDirectoryVersion (repositoryId: string) (fileVersion: FileVersion) =
        let entries =
            [|
                DirectoryVersionPreimageEntry.File fileVersion.RelativePath fileVersion.Size fileVersion.Blake3Hash fileVersion.Sha256Hash
            |]

        let sha256Hash = computeSha256ForDirectoryEntries (RelativePath "/") entries
        let blake3Hash = computeBlake3ForDirectory (RelativePath "/") entries

        Grace.Types.Common.DirectoryVersion.CreateWithHashes
            (Guid.NewGuid())
            (Guid.Parse ownerId)
            (Guid.Parse organizationId)
            (Guid.Parse repositoryId)
            "/"
            sha256Hash
            blake3Hash
            (List<DirectoryVersionId>())
            (List<FileVersion>([ fileVersion ]))
            fileVersion.Size

    /// Builds a deterministic directory version with file for integration setup fixture for the server integration branch assertions.
    let createDirectoryVersionWithFile (repositoryId: string) (relativePath: RelativePath) (fileVersion: FileVersion) =
        let entries =
            [|
                DirectoryVersionPreimageEntry.File fileVersion.RelativePath fileVersion.Size fileVersion.Blake3Hash fileVersion.Sha256Hash
            |]

        let sha256Hash = computeSha256ForDirectoryEntries relativePath entries
        let blake3Hash = computeBlake3ForDirectory relativePath entries

        Grace.Types.Common.DirectoryVersion.CreateWithHashes
            (Guid.NewGuid())
            (Guid.Parse ownerId)
            (Guid.Parse organizationId)
            (Guid.Parse repositoryId)
            relativePath
            sha256Hash
            blake3Hash
            (List<DirectoryVersionId>())
            (List<FileVersion>([ fileVersion ]))
            fileVersion.Size

    /// Normalizes d directory size for hash for stable assertions.
    let private normalizedDirectorySizeForHash (directoryVersion: Grace.Types.Common.DirectoryVersion) =
        if directoryVersion.Size = Constants.InitialDirectorySize then
            0L
        else
            directoryVersion.Size

    let createDirectoryVersion
        (directoryVersionId: DirectoryVersionId)
        (repositoryId: string)
        (relativePath: RelativePath)
        (childDirectoryVersions: Grace.Types.Common.DirectoryVersion seq)
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

        Grace.Types.Common.DirectoryVersion.CreateWithHashes
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

    /// Defines gzip bytes behavior for the surrounding tests used by the server integration branch scenario.
    let private gzipBytes (bytes: byte array) =
        use compressed = new MemoryStream()
        use gzipStream = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen = true)
        gzipStream.Write(bytes, 0, bytes.Length)
        gzipStream.Dispose()
        compressed.ToArray()

    /// Uploads file to object storage through storage test infrastructure.
    let uploadFileToObjectStorageAsync repositoryId (payload: byte array) (fileVersion: FileVersion) =
        task {
            let parameters = Parameters.Storage.GetUploadMetadataForFilesParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.FileVersions <- [| fileVersion |]
            parameters.CorrelationId <- generateCorrelationId ()

            let! uploadResponse = Client.PostAsync("/storage/getUploadMetadataForFiles", createJsonContent parameters)
            do! assertOk uploadResponse
            let! uploadMetadata = deserializeContent<GraceReturnValue<List<Parameters.Storage.UploadMetadata>>> uploadResponse
            let metadata = uploadMetadata.ReturnValue |> Seq.exactlyOne

            use payloadStream = new MemoryStream(gzipBytes payload, writable = false)
            let blockBlobClient = BlockBlobClient(metadata.BlobUriWithSasToken)

            let uploadOptions = BlobUploadOptions()
            uploadOptions.HttpHeaders <- BlobHttpHeaders(ContentEncoding = "gzip")

            let! response = blockBlobClient.UploadAsync(payloadStream, uploadOptions)
            Assert.That(response.GetRawResponse().Status, Is.EqualTo(int HttpStatusCode.Created))
        }

    /// Saves directory version through the branch test routes.
    let private saveDirectoryVersionAsync repositoryId (directoryVersion: Grace.Types.Common.DirectoryVersion) =
        task {
            let parameters = Parameters.DirectoryVersion.SaveDirectoryVersionsParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.CorrelationId <- generateCorrelationId ()
            parameters.DirectoryVersions.Add(directoryVersion)

            let! response = Client.PostAsync("/directory/saveDirectoryVersions", createJsonContent parameters)
            do! assertOk response
        }

    /// Saves directory versions through the branch test routes.
    let saveDirectoryVersionsAsync repositoryId (directoryVersions: Grace.Types.Common.DirectoryVersion seq) =
        task {
            let parameters = Parameters.DirectoryVersion.SaveDirectoryVersionsParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.CorrelationId <- generateCorrelationId ()

            for directoryVersion in directoryVersions do
                parameters.DirectoryVersions.Add(directoryVersion)

            let! response = Client.PostAsync("/directory/saveDirectoryVersions", createJsonContent parameters)
            do! assertOk response
        }

    /// Saves reference response through the branch test routes.
    let saveReferenceResponseAsync repositoryId (branch: Branch.BranchDto) directoryVersionId sha256Hash =
        task {
            let parameters = Parameters.Branch.CreateReferenceParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.ReferenceId <- Guid.NewGuid()
            parameters.DirectoryVersionId <- directoryVersionId
            parameters.Sha256Hash <- sha256Hash
            parameters.Message <- "Root hash hydration route proof save"
            parameters.CorrelationId <- generateCorrelationId ()

            return! Client.PostAsync("/branch/save", createJsonContent parameters)
        }

    /// Defines assign reference response behavior for the surrounding tests used by the server integration branch scenario.
    let assignReferenceResponseAsync repositoryId (branch: Branch.BranchDto) directoryVersionId sha256Hash =
        task {
            let parameters = Parameters.Branch.AssignParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.ReferenceId <- Guid.NewGuid()
            parameters.DirectoryVersionId <- directoryVersionId
            parameters.Sha256Hash <- sha256Hash
            parameters.Message <- "Root hash hydration route proof assign"
            parameters.CorrelationId <- generateCorrelationId ()

            return! Client.PostAsync("/branch/assign", createJsonContent parameters)
        }

    /// Saves reference by BLAKE3 response through the branch test routes.
    let saveReferenceByBlake3ResponseAsync repositoryId (branch: Branch.BranchDto) directoryVersionId blake3Hash =
        task {
            let parameters = Parameters.Branch.CreateReferenceParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.ReferenceId <- Guid.NewGuid()
            parameters.DirectoryVersionId <- directoryVersionId
            parameters.Blake3Hash <- blake3Hash
            parameters.Message <- "BLAKE3 root locator route proof save"
            parameters.CorrelationId <- generateCorrelationId ()

            return! Client.PostAsync("/branch/save", createJsonContent parameters)
        }

    /// Saves reference by SHA and BLAKE3 response through the branch test routes.
    let saveReferenceByShaAndBlake3ResponseAsync repositoryId (branch: Branch.BranchDto) sha256Hash blake3Hash =
        task {
            let parameters = Parameters.Branch.CreateReferenceParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.ReferenceId <- Guid.NewGuid()
            parameters.Sha256Hash <- sha256Hash
            parameters.Blake3Hash <- blake3Hash
            parameters.Message <- "Mixed hash locator route proof save"
            parameters.CorrelationId <- generateCorrelationId ()

            return! Client.PostAsync("/branch/save", createJsonContent parameters)
        }

    /// Builds a deterministic reference by BLAKE3 response for integration setup fixture for the server integration branch assertions.
    let createReferenceByBlake3ResponseAsync (endpoint: string) repositoryId (branch: Branch.BranchDto) blake3Hash =
        task {
            let parameters = Parameters.Branch.CreateReferenceParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.ReferenceId <- Guid.NewGuid()
            parameters.Blake3Hash <- blake3Hash
            parameters.Message <- "Ambiguous BLAKE3 root locator route proof"
            parameters.CorrelationId <- generateCorrelationId ()

            return! Client.PostAsync(endpoint, createJsonContent parameters)
        }

    /// Builds a Commit request with a caller-owned reference identity for ambiguous BLAKE3 root validation.
    let commitReferenceByBlake3ResponseAsync repositoryId (branch: Branch.BranchDto) blake3Hash =
        task {
            let parameters = Parameters.Branch.CommitReferenceParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.ReferenceId <- Guid.NewGuid()
            parameters.Blake3Hash <- blake3Hash
            parameters.Message <- "Ambiguous BLAKE3 root locator route proof"
            parameters.CorrelationId <- generateCorrelationId ()

            return! Client.PostAsync("/branch/commit", createJsonContent parameters)
        }

    /// Defines assign reference by BLAKE3 response behavior for the surrounding tests used by the server integration branch scenario.
    let assignReferenceByBlake3ResponseAsync repositoryId (branch: Branch.BranchDto) directoryVersionId blake3Hash =
        task {
            let parameters = Parameters.Branch.AssignParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.ReferenceId <- Guid.NewGuid()
            parameters.DirectoryVersionId <- directoryVersionId
            parameters.Blake3Hash <- blake3Hash
            parameters.Message <- "BLAKE3 root locator route proof assign"
            parameters.CorrelationId <- generateCorrelationId ()

            return! Client.PostAsync("/branch/assign", createJsonContent parameters)
        }

    /// Lists contents by SHA and BLAKE3 hash response from the running test server.
    let listContentsByShaAndBlake3HashResponseAsync repositoryId (branch: Branch.BranchDto) sha256Hash blake3Hash =
        task {
            let parameters = Parameters.Branch.ListContentsParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.Sha256Hash <- sha256Hash
            parameters.Blake3Hash <- blake3Hash
            parameters.CorrelationId <- generateCorrelationId ()

            return! Client.PostAsync("/branch/listContents", createJsonContent parameters)
        }

    /// Gets version by SHA and BLAKE3 hash response from the running test server.
    let getVersionByShaAndBlake3HashResponseAsync repositoryId (branch: Branch.BranchDto) sha256Hash blake3Hash =
        task {
            let parameters = Parameters.Branch.GetBranchVersionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.Sha256Hash <- sha256Hash
            parameters.Blake3Hash <- blake3Hash
            parameters.CorrelationId <- generateCorrelationId ()

            return! Client.PostAsync("/branch/getVersion", createJsonContent parameters)
        }

    /// Gets recursive size by SHA256 hash response from the running test server.
    let getRecursiveSizeBySha256HashResponseAsync repositoryId (branch: Branch.BranchDto) sha256Hash =
        task {
            let parameters = Parameters.Branch.ListContentsParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.Sha256Hash <- sha256Hash
            parameters.CorrelationId <- generateCorrelationId ()

            return! Client.PostAsync("/branch/getRecursiveSize", createJsonContent parameters)
        }

    /// Gets recursive size by BLAKE3 hash response from the running test server.
    let getRecursiveSizeByBlake3HashResponseAsync repositoryId (branch: Branch.BranchDto) blake3Hash =
        task {
            let parameters = Parameters.Branch.ListContentsParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.Blake3Hash <- blake3Hash
            parameters.CorrelationId <- generateCorrelationId ()

            return! Client.PostAsync("/branch/getRecursiveSize", createJsonContent parameters)
        }

    /// Gets recursive size by SHA and BLAKE3 hash response from the running test server.
    let getRecursiveSizeByShaAndBlake3HashResponseAsync repositoryId (branch: Branch.BranchDto) sha256Hash blake3Hash =
        task {
            let parameters = Parameters.Branch.ListContentsParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.Sha256Hash <- sha256Hash
            parameters.Blake3Hash <- blake3Hash
            parameters.CorrelationId <- generateCorrelationId ()

            return! Client.PostAsync("/branch/getRecursiveSize", createJsonContent parameters)
        }

    /// Enables assign for branch route tests.
    let enableAssignAsync repositoryId (branch: Branch.BranchDto) =
        task {
            let parameters = Parameters.Branch.EnableFeatureParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.Enabled <- true
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/enableAssign", createJsonContent parameters)
            do! assertOk response
        }

    /// Enables commit for branch route tests.
    let enableCommitAsync repositoryId (branch: Branch.BranchDto) =
        task {
            let parameters = Parameters.Branch.EnableFeatureParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.Enabled <- true
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/enableCommit", createJsonContent parameters)
            do! assertOk response
        }

    /// Enables promotion for branch route tests.
    let enablePromotionAsync repositoryId (branch: Branch.BranchDto) =
        task {
            let parameters = Parameters.Branch.EnableFeatureParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.Enabled <- true
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/enablePromotion", createJsonContent parameters)
            do! assertOk response
        }

    /// Saves branch reference through the branch test routes.
    let private saveBranchReferenceAsync repositoryId (branch: Branch.BranchDto) (directoryVersion: Grace.Types.Common.DirectoryVersion) =
        task {
            let parameters = Parameters.Branch.CreateReferenceParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.ReferenceId <- Guid.NewGuid()
            parameters.DirectoryVersionId <- directoryVersion.DirectoryVersionId
            parameters.Sha256Hash <- $"{directoryVersion.Sha256Hash}"
            parameters.Message <- "Annotate route test save"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/save", createJsonContent parameters)
            do! assertOk response
        }

    /// Builds a deterministic annotatable reference for integration setup fixture for the server integration branch assertions.
    let createAnnotatableReferenceAsync repositoryId (parentBranch: Branch.BranchDto) =
        task {
            let! branch = createBranchAsync repositoryId parentBranch $"Annotate{Guid.NewGuid():N}"
            let relativePath = $"annotate/{Guid.NewGuid():N}/sample.fs"
            let content = $"let value = 42{Environment.NewLine}let other = value + 1{Environment.NewLine}"
            let contentBytes = Encoding.UTF8.GetBytes(content)

            let fileVersion =
                FileVersion.CreateWithHashes relativePath (sha256Hex contentBytes) (blake3Hex contentBytes) String.Empty false (int64 contentBytes.Length)

            let tempRoot = Path.Combine(Path.GetTempPath(), "grace-annotate-tests", Guid.NewGuid().ToString("N"))
            let filePath = Path.Combine(tempRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))

            Directory.CreateDirectory(Path.GetDirectoryName(filePath))
            |> ignore

            do! File.WriteAllTextAsync(filePath, content)

            try
                do! uploadFileToObjectStorageAsync repositoryId contentBytes fileVersion

                let directoryVersion = createRootDirectoryVersion repositoryId fileVersion

                do! saveDirectoryVersionAsync repositoryId directoryVersion
                do! saveBranchReferenceAsync repositoryId branch directoryVersion

                let! savedBranch = getBranchAsync repositoryId $"{branch.BranchId}"
                return savedBranch, fileVersion, savedBranch.LatestSave.ReferenceId
            finally
                if Directory.Exists(tempRoot) then Directory.Delete(tempRoot, true)
        }

    /// Builds a deterministic dot root with child directory versions for integration setup fixture for the server integration branch assertions.
    let createDotRootWithChildDirectoryVersions repositoryId childRelativePath =
        let child = createDirectoryVersion (Guid.NewGuid()) repositoryId childRelativePath []
        let root = createDirectoryVersion (Guid.NewGuid()) repositoryId Constants.RootDirectoryPath [ child ]
        child, root

    /// Builds a deterministic slash root directory version for integration setup fixture for the server integration branch assertions.
    let createSlashRootDirectoryVersion repositoryId = createDirectoryVersion (Guid.NewGuid()) repositoryId (RelativePath "/") []

    /// Builds a deterministic dot root with child SHA prefix collision for integration setup fixture for the server integration branch assertions.
    let createDotRootWithChildShaPrefixCollision repositoryId childBasePath excludedRootHashes =
        let mutable collision = None
        let mutable attempt = 0
        let prefixLength = 3

        while collision.IsNone && attempt < 32768 do
            /// Defines child behavior for the surrounding tests used by the server integration branch scenario.
            let child, root = createDotRootWithChildDirectoryVersions repositoryId $"{childBasePath}/{attempt}"

            let rootPrefix =
                (string root.Sha256Hash)
                    .Substring(0, prefixLength)

            let excludedRootMatches =
                excludedRootHashes
                |> Seq.exists (fun excludedHash ->
                    (string excludedHash)
                        .StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))

            if not excludedRootMatches
               && (string child.Sha256Hash)
                   .StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) then
                child.CreatedAt <- getCurrentInstant ()
                collision <- Some(child, root, rootPrefix)

            attempt <- attempt + 1

        match collision with
        | Some collision -> collision
        | None ->
            Assert.Fail("Could not generate a root/child SHA prefix collision for the assign regression test.")
            Unchecked.defaultof<Grace.Types.Common.DirectoryVersion * Grace.Types.Common.DirectoryVersion * string>

    /// Builds a deterministic dot root with child BLAKE3 prefix collision for integration setup fixture for the server integration branch assertions.
    let createDotRootWithChildBlake3PrefixCollision repositoryId childBasePath excludedRootHashes =
        let mutable collision = None
        let mutable attempt = 0
        let prefixLength = 3

        while collision.IsNone && attempt < 32768 do
            /// Defines child behavior for the surrounding tests used by the server integration branch scenario.
            let child, root = createDotRootWithChildDirectoryVersions repositoryId $"{childBasePath}/{attempt}"

            let rootPrefix =
                (string root.Blake3Hash)
                    .Substring(0, prefixLength)

            let excludedRootMatches =
                excludedRootHashes
                |> Seq.exists (fun excludedHash ->
                    (string excludedHash)
                        .StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))

            if not excludedRootMatches
               && (string child.Blake3Hash)
                   .StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) then
                child.CreatedAt <- getCurrentInstant ()
                collision <- Some(child, root, rootPrefix)

            attempt <- attempt + 1

        match collision with
        | Some collision -> collision
        | None ->
            Assert.Fail("Could not generate a root/child BLAKE3 prefix collision for the assign regression test.")
            Unchecked.defaultof<Grace.Types.Common.DirectoryVersion * Grace.Types.Common.DirectoryVersion * string>

    /// Builds a deterministic same BLAKE3 prefix root pair for integration setup fixture for the server integration branch assertions.
    let createSameBlake3PrefixRootPair repositoryId pathPrefix =
        let candidates =
            [|
                for index in 0..512 ->
                    /// Defines child behavior for the surrounding tests used by the server integration branch scenario.
                    let child, root = createDotRootWithChildDirectoryVersions repositoryId $"{pathPrefix}/{index}"
                    child, root
            |]

        candidates
        |> Array.groupBy (fun (_, root) -> (string root.Blake3Hash).Substring(0, 2))
        |> Array.tryPick (fun (sharedPrefix, matches) ->
            if matches.Length >= 2 then
                /// Defines first child behavior for the surrounding tests used by the server integration branch scenario.
                let firstChild, firstRoot = matches[0]
                /// Defines second child behavior for the surrounding tests used by the server integration branch scenario.
                let secondChild, secondRoot = matches[1]
                Some(firstChild, firstRoot, secondChild, secondRoot, sharedPrefix)
            else
                None)
        |> function
            | Some pair -> pair
            | None ->
                Assert.Fail("Could not generate same-prefix BLAKE3 root DirectoryVersions for branch route tests.")
                Unchecked.defaultof<Grace.Types.Common.DirectoryVersion * Grace.Types.Common.DirectoryVersion * Grace.Types.Common.DirectoryVersion * Grace.Types.Common.DirectoryVersion * string>

    /// Defines shortest unique prefix behavior for the surrounding tests used by the server integration branch scenario.
    let shortestUniquePrefix (selected: Sha256Hash) (others: Sha256Hash seq) =
        let selectedHash = string selected
        let otherHashes = others |> Seq.map string |> Seq.toArray
        let mutable prefixLength = 2

        while prefixLength < selectedHash.Length
              && (otherHashes
                  |> Array.exists (fun otherHash -> otherHash.StartsWith(selectedHash.Substring(0, prefixLength), StringComparison.OrdinalIgnoreCase))) do
            prefixLength <- prefixLength + 1

        selectedHash.Substring(0, prefixLength)

    /// Defines shortest unique BLAKE3 prefix behavior for the surrounding tests used by the server integration branch scenario.
    let shortestUniqueBlake3Prefix (selected: Blake3Hash) (others: Blake3Hash seq) =
        let selectedHash = string selected
        let otherHashes = others |> Seq.map string |> Seq.toArray
        let mutable prefixLength = 2

        while prefixLength < selectedHash.Length
              && (otherHashes
                  |> Array.exists (fun otherHash -> otherHash.StartsWith(selectedHash.Substring(0, prefixLength), StringComparison.OrdinalIgnoreCase))) do
            prefixLength <- prefixLength + 1

        selectedHash.Substring(0, prefixLength)

    /// Builds a deterministic annotatable reference with content for integration setup fixture for the server integration branch assertions.
    let createAnnotatableReferenceWithContentAsync repositoryId (branch: Branch.BranchDto) relativePath (content: string) =
        task {
            let contentBytes = Encoding.UTF8.GetBytes(content)

            let fileVersion =
                FileVersion.CreateWithHashes relativePath (sha256Hex contentBytes) (blake3Hex contentBytes) String.Empty false (int64 contentBytes.Length)

            do! uploadFileToObjectStorageAsync repositoryId contentBytes fileVersion

            let directoryVersion = createRootDirectoryVersion repositoryId fileVersion

            do! saveDirectoryVersionAsync repositoryId directoryVersion
            do! saveBranchReferenceAsync repositoryId branch directoryVersion

            let! savedBranch = getBranchAsync repositoryId $"{branch.BranchId}"
            return savedBranch, fileVersion, savedBranch.LatestSave.ReferenceId
        }

    /// Defines promote latest save behavior for the surrounding tests used by the server integration branch scenario.
    let promoteLatestSaveAsync repositoryId (branch: Branch.BranchDto) =
        task {
            let enableParameters = Parameters.Branch.EnableFeatureParameters()
            enableParameters.OwnerId <- ownerId
            enableParameters.OrganizationId <- organizationId
            enableParameters.RepositoryId <- repositoryId
            enableParameters.BranchId <- $"{branch.BranchId}"
            enableParameters.Enabled <- true
            enableParameters.CorrelationId <- generateCorrelationId ()

            let! enableResponse = Client.PostAsync("/branch/enablePromotion", createJsonContent enableParameters)
            do! assertOk enableResponse

            let parameters = Parameters.Branch.CreateReferenceParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.ReferenceId <- Guid.NewGuid()
            parameters.DirectoryVersionId <- branch.LatestSave.DirectoryId
            parameters.Sha256Hash <- $"{branch.LatestSave.Sha256Hash}"
            parameters.Message <- "Annotate route test promotion"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/promote", createJsonContent parameters)
            do! assertOk response
            return! getBranchAsync repositoryId $"{branch.BranchId}"
        }

    /// Defines rebase branch behavior for the surrounding tests used by the server integration branch scenario.
    let rebaseBranchAsync repositoryId (branch: Branch.BranchDto) basedOnReferenceId =
        task {
            let parameters = Parameters.Branch.RebaseParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.ReferenceId <- Guid.NewGuid()
            parameters.BasedOn <- basedOnReferenceId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/rebase", createJsonContent parameters)
            do! assertOk response
            return! getBranchAsync repositoryId $"{branch.BranchId}"
        }

    /// Builds a deterministic personal access token for integration setup fixture for the server integration branch assertions.
    let private createPersonalAccessTokenAsync () =
        task {
            let parameters = Parameters.Auth.CreatePersonalAccessTokenParameters()
            parameters.TokenName <- $"branch-sdk-{Guid.NewGuid():N}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/authenticate/token/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<PersonalAccessTokenCreated>> response
            return returnValue.ReturnValue.Token
        }

    /// Runs an SDK assertion against a real temporary repository configuration and restores process-global configuration state.
    let withExplicitSdkConfigurationForServerAsync (testBody: unit -> Task<'T>) =
        task {
            let root = Path.Combine(Path.GetTempPath(), $"grace-branch-sdk-{Guid.NewGuid():N}")
            let configurationDirectory = Path.Combine(root, Constants.GraceConfigDirectory)
            let configurationPath = Path.Combine(configurationDirectory, Constants.GraceConfigFileName)
            let previousDirectory = Environment.CurrentDirectory

            try
                Directory.CreateDirectory(configurationDirectory)
                |> ignore

                let configuration = GraceConfiguration()
                configuration.ServerUri <- graceServerBaseAddress
                saveConfigFile configurationPath configuration
                Environment.CurrentDirectory <- root
                resetConfiguration ()

                let loadedConfiguration = Current()
                Assert.That(loadedConfiguration.ServerUri, Is.EqualTo(graceServerBaseAddress))

                return! testBody ()
            finally
                Grace.SDK.Auth.clearTokenProvider ()
                Environment.CurrentDirectory <- previousDirectory
                resetConfiguration ()

                if Directory.Exists(root) then
                    try
                        Directory.Delete(root, true)
                    with
                    | _ -> ()
        }

    /// Defines configure SDK for server behavior for the surrounding tests used by the server integration branch scenario.
    let configureSdkForServerAsync () =
        task {
            let configuration = Current()
            configuration.ServerUri <- graceServerBaseAddress

            let! token = createPersonalAccessTokenAsync ()

            Grace.SDK.Auth.setTokenProvider (fun () -> task { return Some token })
        }

    /// Gets first annotatable file from the running test server.
    let getFirstAnnotatableFileAsync (repositoryId: string) (branch: Branch.BranchDto) =
        task {
            let parameters = Parameters.Branch.ListContentsParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.ReferenceId <- $"{branch.BasedOn.ReferenceId}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/listContents", createJsonContent parameters)
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<DirectoryVersion.DirectoryVersionDto array>> response

            let fileVersion =
                returnValue.ReturnValue
                |> Array.collect (fun directoryVersionDto ->
                    directoryVersionDto.DirectoryVersion.Files
                    |> Seq.toArray)
                |> Array.tryFind (fun fileVersion -> not fileVersion.IsBinary && fileVersion.Size > 0L)

            match fileVersion with
            | Some fileVersion -> return fileVersion
            | None ->
                Assert.Fail($"Repository {repositoryId} branch {branch.BranchId} did not expose a non-empty text file for annotate tests.")
                return Unchecked.defaultof<FileVersion>
        }

    /// Builds annotate parameters for route calls.
    let annotateParameters (repositoryId: string) (branch: Branch.BranchDto) (fileVersion: FileVersion) =
        let parameters = Parameters.Branch.AnnotateParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.BranchId <- $"{branch.BranchId}"
        parameters.TargetReferenceId <- branch.BasedOn.ReferenceId
        parameters.Path <- fileVersion.RelativePath
        parameters.StartLine <- 1
        parameters.EndLine <- 1

        parameters.ReferenceTypes <-
            [|
                ReferenceType.Commit
                ReferenceType.Save
                ReferenceType.Promotion
            |]

        parameters.MaxReferences <- 10
        parameters.IncludeLineText <- true
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Asserts branch matches for integration responses.
    let assertBranchMatches (expectedRepositoryId: string) (expectedBranchId: string) (expectedBranchName: string) (branch: Branch.BranchDto) =
        Assert.That(branch.RepositoryId, Is.EqualTo(Guid.Parse(expectedRepositoryId)))
        Assert.That(branch.BranchId, Is.EqualTo(Guid.Parse(expectedBranchId)))
        Assert.That($"{branch.BranchName}", Is.EqualTo(expectedBranchName))
        Assert.That(branch.OwnerId, Is.EqualTo(Guid.Parse(ownerId)))
        Assert.That(branch.OrganizationId, Is.EqualTo(Guid.Parse(organizationId)))
        Assert.That(branch.BasedOn.ReferenceId, Is.Not.EqualTo(Guid.Empty))

    /// Asserts branch reference shape for integration responses.
    let assertBranchReferenceShape (expectedRepositoryId: string) (expectedBranchId: string) (reference: Reference.ReferenceDto) =
        Assert.That(reference.RepositoryId, Is.EqualTo(Guid.Parse(expectedRepositoryId)))
        Assert.That(reference.BranchId, Is.EqualTo(Guid.Parse(expectedBranchId)))
        Assert.That(reference.ReferenceId, Is.Not.EqualTo(Guid.Empty))
        Assert.That(reference.DirectoryId, Is.Not.EqualTo(Guid.Empty))
        Assert.That($"{reference.Sha256Hash}", Is.Not.Empty)

    /// Asserts missing repository for integration responses.
    let assertMissingRepositoryAsync () =
        task {
            let parameters = getBranchParameters $"{Guid.NewGuid()}" repositoryDefaultBranchIds[0]
            let! response = Client.PostAsync("/branch/get", createJsonContent parameters)
            let expected = RepositoryError.getErrorMessage RepositoryError.RepositoryIdDoesNotExist
            do! assertBadRequestGraceError expected response
        }

    /// Asserts missing branch for integration responses.
    let assertMissingBranchAsync (repositoryId: string) =
        task {
            let parameters = getBranchParameters repositoryId $"{Guid.NewGuid()}"
            let! response = Client.PostAsync("/branch/get", createJsonContent parameters)
            let expected = BranchError.getErrorMessage BranchError.BranchIdDoesNotExist
            do! assertBadRequestGraceError expected response
        }

/// Covers branch server scenarios.
[<Parallelizable(ParallelScope.All)>]
type BranchServer() =

    /// Verifies branch-by-id resolves initial main through the strict normalized public projection.
    [<Test>]
    member _.GetBranchesByBranchIdReturnsNormalizedInitialMain() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = Guid.Parse(repositoryDefaultBranchIds[0])
            let! branches = BranchServerTestHelpers.getRepositoryBranchesByIdAsync repositoryId [| branchId |]

            Assert.That(branches, Has.Length.EqualTo(1))
            Assert.That(branches[0].BranchId, Is.EqualTo(branchId))

            [|
                branches[0].BasedOn
                branches[0].LatestReference
            |]
            |> Array.iter (fun reference ->
                Assert.That(reference.ReferenceId, Is.Not.EqualTo(ReferenceId.Empty))
                Assert.That(string reference.Sha256Hash, Is.Not.Empty)
                Assert.That(string reference.Blake3Hash, Is.Not.Empty))

            Assert.That(Branch.BranchDto.IsValidPublicProjection(branches[0]), Is.True, serialize branches[0])
        }

    /// Verifies the root branch parent route returns Grace's controlled no-parent error without querying a default parent actor.
    [<Test>]
    member _.GetParentBranchReturnsControlledErrorForRootBranch() =
        task {
            let repositoryId = repositoryIds[0]
            let parameters = BranchServerTestHelpers.getBranchParameters repositoryId repositoryDefaultBranchIds[0]

            let! response = Client.PostAsync("/branch/getParentBranch", createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)

            let error = deserialize<GraceError> body
            let expected = BranchError.getErrorMessage BranchError.ParentBranchDoesNotExist
            Assert.That(error.Error, Is.EqualTo(expected))
            Assert.That(error.CorrelationId, Is.Not.Empty)
        }

    /// Verifies branch Reference lookup rejects the default ReferenceId through the normal validation envelope.
    [<Test>]
    member _.GetReferenceRejectsEmptyReferenceId() =
        task {
            let repositoryId = repositoryIds[0]
            let parameters = Parameters.Branch.GetReferenceParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- repositoryDefaultBranchIds[0]
            parameters.ReferenceId <- $"{ReferenceId.Empty}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/getReference", createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)

            let error = deserialize<GraceError> body
            let expected = BranchError.getErrorMessage BranchError.InvalidReferenceId
            Assert.That(error.Error, Is.EqualTo(expected))
            Assert.That(error.CorrelationId, Is.Not.Empty)
        }

    /// Verifies the create get list reference and version routes round trip branch identity scenario.
    [<Test>]
    member _.CreateGetListReferenceAndVersionRoutesRoundTripBranchIdentity() =
        task {
            let repositoryId = repositoryIds[0]
            let parentBranchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId parentBranchId

            [|
                parentBranch.BasedOn
                parentBranch.LatestReference
            |]
            |> Array.iter (fun reference ->
                Assert.That(reference.ReferenceId, Is.Not.EqualTo(Guid.Empty))
                Assert.That(string reference.Sha256Hash, Is.Not.Empty)
                Assert.That(string reference.Blake3Hash, Is.Not.Empty))

            let branchName = $"Branch{Guid.NewGuid():N}"

            let! createdBranch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch branchName
            BranchServerTestHelpers.assertBranchMatches repositoryId $"{createdBranch.BranchId}" branchName createdBranch
            Assert.That(createdBranch.ParentBranchId, Is.EqualTo(parentBranch.BranchId))
            Assert.That(createdBranch.BasedOn.ReferenceId, Is.EqualTo(parentBranch.BasedOn.ReferenceId))

            [|
                createdBranch.BasedOn
                createdBranch.LatestReference
            |]
            |> Array.iter (fun reference ->
                Assert.That(reference.ReferenceId, Is.Not.EqualTo(Guid.Empty))
                Assert.That(string reference.Sha256Hash, Is.Not.Empty)
                Assert.That(string reference.Blake3Hash, Is.Not.Empty))

            [|
                createdBranch.LatestPromotion
                createdBranch.LatestCommit
                createdBranch.LatestCheckpoint
                createdBranch.LatestSave
            |]
            |> Array.iter (fun reference -> Assert.That(reference, Is.EqualTo(Reference.ReferenceDto.Default)))

            let! fetchedBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{createdBranch.BranchId}"
            BranchServerTestHelpers.assertBranchMatches repositoryId $"{createdBranch.BranchId}" branchName fetchedBranch

            let! listedBranches = BranchServerTestHelpers.getRepositoryBranchesAsync repositoryId

            Assert.That(
                listedBranches
                |> Array.exists (fun branch -> branch.BranchId = createdBranch.BranchId),
                Is.True
            )

            let! _saveResult = BranchServerTestHelpers.saveBranchAsync repositoryId createdBranch
            let! savedBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{createdBranch.BranchId}"
            Assert.That(savedBranch.LatestSave.ReferenceId, Is.Not.EqualTo(Guid.Empty))
            Assert.That(savedBranch.LatestSave.BranchId, Is.EqualTo(createdBranch.BranchId))

            let! references = BranchServerTestHelpers.getBranchReferencesAsync repositoryId $"{createdBranch.BranchId}"
            Assert.That(references, Is.Not.Empty)

            references
            |> Array.iter (BranchServerTestHelpers.assertBranchReferenceShape repositoryId $"{createdBranch.BranchId}")

            let! versionDirectoryIds = BranchServerTestHelpers.getBranchVersionAsync repositoryId $"{createdBranch.BranchId}"
            Assert.That(versionDirectoryIds, Is.Not.Empty)

            versionDirectoryIds
            |> Array.iter (fun directoryId -> Assert.That(directoryId, Is.Not.EqualTo(Guid.Empty)))

            do! BranchServerTestHelpers.assertMissingRepositoryAsync ()
            do! BranchServerTestHelpers.assertMissingBranchAsync repositoryId
        }

    /// Verifies the save with directory version ID and SHA prefix hydrates full root hashes scenario.
    [<Test>]
    member _.SaveWithDirectoryVersionIdAndShaPrefixHydratesFullRootHashes() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"RootPrefix{Guid.NewGuid():N}"
            /// Defines child behavior for the surrounding tests used by the server integration branch scenario.
            let child, root = BranchServerTestHelpers.createDotRootWithChildDirectoryVersions repositoryId $"prefix/{Guid.NewGuid():N}"

            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ child; root ]

            let rootShaPrefix = (string root.Sha256Hash).Substring(0, 8)
            let! response = BranchServerTestHelpers.saveReferenceResponseAsync repositoryId branch root.DirectoryVersionId (Sha256Hash rootShaPrefix)
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseBody)

            let! savedBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{branch.BranchId}"
            Assert.That(savedBranch.LatestSave.DirectoryId, Is.EqualTo(root.DirectoryVersionId))
            Assert.That(savedBranch.LatestSave.Sha256Hash, Is.EqualTo(root.Sha256Hash))
            Assert.That(savedBranch.LatestSave.Blake3Hash, Is.EqualTo(root.Blake3Hash))
        }

    /// Verifies the save with SHA only slash root hydrates full root hashes scenario.
    [<Test>]
    member _.SaveWithShaOnlySlashRootHydratesFullRootHashes() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"SlashRoot{Guid.NewGuid():N}"

            let root = BranchServerTestHelpers.createSlashRootDirectoryVersion repositoryId

            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ root ]

            let! response = BranchServerTestHelpers.saveReferenceResponseAsync repositoryId branch DirectoryVersionId.Empty root.Sha256Hash
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseBody)

            let! savedBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{branch.BranchId}"
            Assert.That(savedBranch.LatestSave.DirectoryId, Is.EqualTo(root.DirectoryVersionId))
            Assert.That(savedBranch.LatestSave.Sha256Hash, Is.EqualTo(root.Sha256Hash))
            Assert.That(savedBranch.LatestSave.Blake3Hash, Is.EqualTo(root.Blake3Hash))
        }

    /// Verifies the save with BLAKE3 full and unique prefix hydrates full root hashes scenario.
    [<Test>]
    member _.SaveWithBlake3FullAndUniquePrefixHydratesFullRootHashes() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! fullHashBranch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"Blake3Full{Guid.NewGuid():N}"
            let! prefixBranch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"Blake3Prefix{Guid.NewGuid():N}"
            /// Defines child behavior for the surrounding tests used by the server integration branch scenario.
            let child, root = BranchServerTestHelpers.createDotRootWithChildDirectoryVersions repositoryId $"blake3-prefix/{Guid.NewGuid():N}"

            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ child; root ]

            let! fullHashResponse =
                BranchServerTestHelpers.saveReferenceByBlake3ResponseAsync repositoryId fullHashBranch DirectoryVersionId.Empty root.Blake3Hash

            let! fullHashBody = fullHashResponse.Content.ReadAsStringAsync()
            Assert.That(fullHashResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), fullHashBody)

            let rootBlake3Prefix =
                BranchServerTestHelpers.shortestUniqueBlake3Prefix
                    root.Blake3Hash
                    [
                        child.Blake3Hash
                        parentBranch.BasedOn.Blake3Hash
                    ]

            let stableRootBlake3Prefix =
                if rootBlake3Prefix.Length < 16 then
                    (string root.Blake3Hash).Substring(0, 16)
                else
                    rootBlake3Prefix

            let! prefixResponse =
                BranchServerTestHelpers.saveReferenceByBlake3ResponseAsync
                    repositoryId
                    prefixBranch
                    DirectoryVersionId.Empty
                    (Blake3Hash stableRootBlake3Prefix)

            let! prefixBody = prefixResponse.Content.ReadAsStringAsync()
            Assert.That(prefixResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), prefixBody)

            let! fullHashSavedBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{fullHashBranch.BranchId}"
            Assert.That(fullHashSavedBranch.LatestSave.DirectoryId, Is.EqualTo(root.DirectoryVersionId))
            Assert.That(fullHashSavedBranch.LatestSave.Sha256Hash, Is.EqualTo(root.Sha256Hash))
            Assert.That(fullHashSavedBranch.LatestSave.Blake3Hash, Is.EqualTo(root.Blake3Hash))

            let! prefixSavedBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{prefixBranch.BranchId}"
            Assert.That(prefixSavedBranch.LatestSave.DirectoryId, Is.EqualTo(root.DirectoryVersionId))
            Assert.That(prefixSavedBranch.LatestSave.Sha256Hash, Is.EqualTo(root.Sha256Hash))
            Assert.That(prefixSavedBranch.LatestSave.Blake3Hash, Is.EqualTo(root.Blake3Hash))
        }

    /// Verifies the save with mismatched SHA and BLAKE3 hash only locators fails before mutation scenario.
    [<Test>]
    member _.SaveWithMismatchedShaAndBlake3HashOnlyLocatorsFailsBeforeMutation() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"MixedHashMismatch{Guid.NewGuid():N}"

            /// Defines first child behavior for the surrounding tests used by the server integration branch scenario.
            let firstChild, firstRoot, secondChild, secondRoot, _sharedPrefix =
                BranchServerTestHelpers.createSameBlake3PrefixRootPair repositoryId $"mixed-hash-mismatch/{Guid.NewGuid():N}"

            do!
                BranchServerTestHelpers.saveDirectoryVersionsAsync
                    repositoryId
                    [
                        firstChild
                        firstRoot
                        secondChild
                        secondRoot
                    ]

            let! response = BranchServerTestHelpers.saveReferenceByShaAndBlake3ResponseAsync repositoryId branch firstRoot.Sha256Hash secondRoot.Blake3Hash

            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), responseBody)
            Assert.That(responseBody, Does.Contain("Reference root DirectoryVersion does not exist."))

            let! afterBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{branch.BranchId}"
            Assert.That(afterBranch.LatestSave.ReferenceId, Is.EqualTo(branch.LatestSave.ReferenceId))
            Assert.That(afterBranch.LatestSave.DirectoryId, Is.EqualTo(branch.LatestSave.DirectoryId))
        }

    /// Verifies the assign with SHA only root prefix ignores newer child directory match scenario.
    [<Test>]
    member _.AssignWithShaOnlyRootPrefixIgnoresNewerChildDirectoryMatch() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"AssignRootPrefix{Guid.NewGuid():N}"

            /// Defines child behavior for the surrounding tests used by the server integration branch scenario.
            let child, root, sharedPrefix =
                BranchServerTestHelpers.createDotRootWithChildShaPrefixCollision
                    repositoryId
                    $"assign-prefix/{Guid.NewGuid():N}"
                    [ parentBranch.BasedOn.Sha256Hash ]

            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ root; child ]
            do! BranchServerTestHelpers.enableAssignAsync repositoryId branch

            let! response = BranchServerTestHelpers.assignReferenceResponseAsync repositoryId branch DirectoryVersionId.Empty (Sha256Hash sharedPrefix)
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseBody)

            let! assignedBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{branch.BranchId}"
            Assert.That(assignedBranch.LatestPromotion.DirectoryId, Is.EqualTo(root.DirectoryVersionId))
            Assert.That(assignedBranch.LatestPromotion.Sha256Hash, Is.EqualTo(root.Sha256Hash))
            Assert.That(assignedBranch.LatestPromotion.Blake3Hash, Is.EqualTo(root.Blake3Hash))
        }

    /// Verifies the assign with BLAKE3 only root prefix ignores newer child directory match scenario.
    [<Test>]
    member _.AssignWithBlake3OnlyRootPrefixIgnoresNewerChildDirectoryMatch() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"AssignBlake3Prefix{Guid.NewGuid():N}"

            /// Defines child behavior for the surrounding tests used by the server integration branch scenario.
            let child, root, sharedPrefix =
                BranchServerTestHelpers.createDotRootWithChildBlake3PrefixCollision
                    repositoryId
                    $"assign-blake3-prefix/{Guid.NewGuid():N}"
                    [ parentBranch.BasedOn.Blake3Hash ]

            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ root; child ]
            do! BranchServerTestHelpers.enableAssignAsync repositoryId branch

            let! response = BranchServerTestHelpers.assignReferenceByBlake3ResponseAsync repositoryId branch DirectoryVersionId.Empty (Blake3Hash sharedPrefix)
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseBody)

            let! assignedBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{branch.BranchId}"
            Assert.That(assignedBranch.LatestPromotion.DirectoryId, Is.EqualTo(root.DirectoryVersionId))
            Assert.That(assignedBranch.LatestPromotion.Sha256Hash, Is.EqualTo(root.Sha256Hash))
            Assert.That(assignedBranch.LatestPromotion.Blake3Hash, Is.EqualTo(root.Blake3Hash))
        }

    /// Verifies the assign with directory version ID and mismatched BLAKE3 fails before mutation scenario.
    [<Test>]
    member _.AssignWithDirectoryVersionIdAndMismatchedBlake3FailsBeforeMutation() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"AssignMismatchedBlake3{Guid.NewGuid():N}"
            /// Defines child behavior for the surrounding tests used by the server integration branch scenario.
            let child, root = BranchServerTestHelpers.createDotRootWithChildDirectoryVersions repositoryId $"assign-mismatch/{Guid.NewGuid():N}"

            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ child; root ]
            do! BranchServerTestHelpers.enableAssignAsync repositoryId branch

            let beforeBranch = branch

            let mismatchedBlake3 =
                if (string root.Blake3Hash)
                    .StartsWith("0", StringComparison.Ordinal) then
                    Blake3Hash "1fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
                else
                    Blake3Hash "0fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"

            let! response = BranchServerTestHelpers.assignReferenceByBlake3ResponseAsync repositoryId branch root.DirectoryVersionId mismatchedBlake3

            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), responseBody)

            let! afterBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{branch.BranchId}"
            Assert.That(afterBranch.LatestPromotion.ReferenceId, Is.EqualTo(beforeBranch.LatestPromotion.ReferenceId))
            Assert.That(afterBranch.LatestPromotion.DirectoryId, Is.EqualTo(beforeBranch.LatestPromotion.DirectoryId))
        }

    /// Verifies the assign with malformed BLAKE3 locator returns validation error before mutation scenario.
    [<Test>]
    member _.AssignWithMalformedBlake3LocatorReturnsValidationErrorBeforeMutation() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"AssignMalformedBlake3{Guid.NewGuid():N}"

            do! BranchServerTestHelpers.enableAssignAsync repositoryId branch

            let! response =
                BranchServerTestHelpers.assignReferenceByBlake3ResponseAsync repositoryId branch DirectoryVersionId.Empty (Blake3Hash "not-a-blake3")

            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), responseBody)
            Assert.That((deserialize<GraceError> responseBody).Error, Is.EqualTo(BranchError.getErrorMessage BranchError.InvalidBlake3Hash))

            let! afterBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{branch.BranchId}"
            Assert.That(afterBranch.LatestPromotion.ReferenceId, Is.EqualTo(branch.LatestPromotion.ReferenceId))
            Assert.That(afterBranch.LatestPromotion.DirectoryId, Is.EqualTo(branch.LatestPromotion.DirectoryId))
        }

    /// Verifies the assign with malformed SHA locator returns validation error before lookup and mutation scenario.
    [<Test>]
    member _.AssignWithMalformedShaLocatorReturnsValidationErrorBeforeLookupAndMutation() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"AssignMalformedSha{Guid.NewGuid():N}"
            /// Defines child behavior for the surrounding tests used by the server integration branch scenario.
            let child, root = BranchServerTestHelpers.createDotRootWithChildDirectoryVersions repositoryId $"assign-malformed-sha/{Guid.NewGuid():N}"

            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ child; root ]
            do! BranchServerTestHelpers.enableAssignAsync repositoryId branch

            let! response = BranchServerTestHelpers.assignReferenceResponseAsync repositoryId branch root.DirectoryVersionId (Sha256Hash "not-a-sha")

            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), responseBody)
            Assert.That((deserialize<GraceError> responseBody).Error, Is.EqualTo(BranchError.getErrorMessage BranchError.InvalidSha256Hash))

            let! afterBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{branch.BranchId}"
            Assert.That(afterBranch.LatestPromotion.ReferenceId, Is.EqualTo(branch.LatestPromotion.ReferenceId))
            Assert.That(afterBranch.LatestPromotion.DirectoryId, Is.EqualTo(branch.LatestPromotion.DirectoryId))
        }

    /// Verifies the save with malformed zero and ambiguous BLAKE3 locators fails before mutation scenario.
    [<Test>]
    member _.SaveWithMalformedZeroAndAmbiguousBlake3LocatorsFailsBeforeMutation() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! malformedBranch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"MalformedBlake3{Guid.NewGuid():N}"
            let! zeroBranch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"ZeroBlake3{Guid.NewGuid():N}"
            let! ambiguousBranch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"AmbiguousBlake3{Guid.NewGuid():N}"

            /// Defines first child behavior for the surrounding tests used by the server integration branch scenario.
            let firstChild, firstRoot, secondChild, secondRoot, sharedPrefix =
                BranchServerTestHelpers.createSameBlake3PrefixRootPair repositoryId $"ambiguous-branch-blake3/{Guid.NewGuid():N}"

            do!
                BranchServerTestHelpers.saveDirectoryVersionsAsync
                    repositoryId
                    [
                        firstChild
                        firstRoot
                        secondChild
                        secondRoot
                    ]

            /// Asserts latest save unchanged for integration responses.
            let assertLatestSaveUnchanged repositoryId (beforeBranch: Branch.BranchDto) =
                task {
                    let! afterBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{beforeBranch.BranchId}"
                    Assert.That(afterBranch.LatestSave.ReferenceId, Is.EqualTo(beforeBranch.LatestSave.ReferenceId))
                    Assert.That(afterBranch.LatestSave.DirectoryId, Is.EqualTo(beforeBranch.LatestSave.DirectoryId))
                }

            let! malformedResponse =
                BranchServerTestHelpers.saveReferenceByBlake3ResponseAsync repositoryId malformedBranch DirectoryVersionId.Empty (Blake3Hash "not-a-blake3")

            let! malformedBody = malformedResponse.Content.ReadAsStringAsync()
            Assert.That(malformedResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), malformedBody)
            Assert.That((deserialize<GraceError> malformedBody).Error, Is.EqualTo(BranchError.getErrorMessage BranchError.InvalidBlake3Hash))
            do! assertLatestSaveUnchanged repositoryId malformedBranch

            let! zeroResponse =
                BranchServerTestHelpers.saveReferenceByBlake3ResponseAsync
                    repositoryId
                    zeroBranch
                    DirectoryVersionId.Empty
                    (Blake3Hash "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")

            let! zeroBody = zeroResponse.Content.ReadAsStringAsync()
            Assert.That(zeroResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), zeroBody)
            do! assertLatestSaveUnchanged repositoryId zeroBranch

            let! ambiguousResponse =
                BranchServerTestHelpers.saveReferenceByBlake3ResponseAsync repositoryId ambiguousBranch DirectoryVersionId.Empty (Blake3Hash sharedPrefix)

            let! ambiguousBody = ambiguousResponse.Content.ReadAsStringAsync()
            Assert.That(ambiguousResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), ambiguousBody)
            Assert.That((deserialize<GraceError> ambiguousBody).Error, Does.Contain("ambiguous"))
            do! assertLatestSaveUnchanged repositoryId ambiguousBranch
        }

    /// Verifies the reference creation routes reject ambiguous BLAKE3 root prefix before mutation scenario.
    [<Test>]
    member _.ReferenceCreationRoutesRejectAmbiguousBlake3RootPrefixBeforeMutation() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! saveBranch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"AmbiguousSave{Guid.NewGuid():N}"
            let! commitBranch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"AmbiguousCommit{Guid.NewGuid():N}"
            let! promoteBranch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"AmbiguousPromote{Guid.NewGuid():N}"
            let! assignBranch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"AmbiguousAssign{Guid.NewGuid():N}"

            /// Defines first child behavior for the surrounding tests used by the server integration branch scenario.
            let firstChild, firstRoot, secondChild, secondRoot, sharedPrefix =
                BranchServerTestHelpers.createSameBlake3PrefixRootPair repositoryId $"ambiguous-reference-create/{Guid.NewGuid():N}"

            do!
                BranchServerTestHelpers.saveDirectoryVersionsAsync
                    repositoryId
                    [
                        firstChild
                        firstRoot
                        secondChild
                        secondRoot
                    ]

            do! BranchServerTestHelpers.enableCommitAsync repositoryId commitBranch
            do! BranchServerTestHelpers.enablePromotionAsync repositoryId promoteBranch
            do! BranchServerTestHelpers.enableAssignAsync repositoryId assignBranch

            /// Asserts ambiguous response for integration responses.
            let assertAmbiguousResponse (response: HttpResponseMessage) =
                task {
                    let! body = response.Content.ReadAsStringAsync()
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
                    Assert.That((deserialize<GraceError> body).Error, Does.Contain("ambiguous"))
                }

            let! saveResponse = BranchServerTestHelpers.createReferenceByBlake3ResponseAsync "/branch/save" repositoryId saveBranch (Blake3Hash sharedPrefix)

            do! assertAmbiguousResponse saveResponse

            let! commitResponse = BranchServerTestHelpers.commitReferenceByBlake3ResponseAsync repositoryId commitBranch (Blake3Hash sharedPrefix)

            do! assertAmbiguousResponse commitResponse

            let! promoteResponse =
                BranchServerTestHelpers.createReferenceByBlake3ResponseAsync "/branch/promote" repositoryId promoteBranch (Blake3Hash sharedPrefix)

            do! assertAmbiguousResponse promoteResponse

            let! assignResponse =
                BranchServerTestHelpers.assignReferenceByBlake3ResponseAsync repositoryId assignBranch DirectoryVersionId.Empty (Blake3Hash sharedPrefix)

            do! assertAmbiguousResponse assignResponse

            let! afterSaveBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{saveBranch.BranchId}"
            Assert.That(afterSaveBranch.LatestSave.ReferenceId, Is.EqualTo(saveBranch.LatestSave.ReferenceId))
            Assert.That(afterSaveBranch.LatestSave.DirectoryId, Is.EqualTo(saveBranch.LatestSave.DirectoryId))

            let! afterCommitBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{commitBranch.BranchId}"
            Assert.That(afterCommitBranch.LatestCommit.ReferenceId, Is.EqualTo(commitBranch.LatestCommit.ReferenceId))
            Assert.That(afterCommitBranch.LatestCommit.DirectoryId, Is.EqualTo(commitBranch.LatestCommit.DirectoryId))

            let! afterPromoteBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{promoteBranch.BranchId}"
            Assert.That(afterPromoteBranch.LatestPromotion.ReferenceId, Is.EqualTo(promoteBranch.LatestPromotion.ReferenceId))
            Assert.That(afterPromoteBranch.LatestPromotion.DirectoryId, Is.EqualTo(promoteBranch.LatestPromotion.DirectoryId))

            let! afterAssignBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{assignBranch.BranchId}"
            Assert.That(afterAssignBranch.LatestPromotion.ReferenceId, Is.EqualTo(assignBranch.LatestPromotion.ReferenceId))
            Assert.That(afterAssignBranch.LatestPromotion.DirectoryId, Is.EqualTo(assignBranch.LatestPromotion.DirectoryId))
        }

    /// Verifies the save with ambiguous BLAKE3 prefix and matching SHA creates reference for paired root scenario.
    [<Test>]
    member _.SaveWithAmbiguousBlake3PrefixAndMatchingShaCreatesReferenceForPairedRoot() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"PairedHashSave{Guid.NewGuid():N}"

            /// Defines first child behavior for the surrounding tests used by the server integration branch scenario.
            let firstChild, firstRoot, secondChild, secondRoot, sharedPrefix =
                BranchServerTestHelpers.createSameBlake3PrefixRootPair repositoryId $"paired-hash-save/{Guid.NewGuid():N}"

            do!
                BranchServerTestHelpers.saveDirectoryVersionsAsync
                    repositoryId
                    [
                        firstChild
                        firstRoot
                        secondChild
                        secondRoot
                    ]

            let! response = BranchServerTestHelpers.saveReferenceByShaAndBlake3ResponseAsync repositoryId branch firstRoot.Sha256Hash (Blake3Hash sharedPrefix)

            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)

            let! afterBranch = BranchServerTestHelpers.getBranchAsync repositoryId $"{branch.BranchId}"
            Assert.That(afterBranch.LatestSave.DirectoryId, Is.EqualTo(firstRoot.DirectoryVersionId))
            Assert.That(afterBranch.LatestSave.Sha256Hash, Is.EqualTo(firstRoot.Sha256Hash))
            Assert.That(afterBranch.LatestSave.Blake3Hash, Is.EqualTo(firstRoot.Blake3Hash))
        }

    /// Verifies the branch hash queries reject ambiguous BLAKE3 prefix instead of returning empty success scenario.
    [<Test>]
    member _.BranchHashQueriesRejectAmbiguousBlake3PrefixInsteadOfReturningEmptySuccess() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"AmbiguousBranchHashQuery{Guid.NewGuid():N}"

            /// Defines first child behavior for the surrounding tests used by the server integration branch scenario.
            let firstChild, firstRoot, secondChild, secondRoot, sharedPrefix =
                BranchServerTestHelpers.createSameBlake3PrefixRootPair repositoryId $"ambiguous-branch-query/{Guid.NewGuid():N}"

            do!
                BranchServerTestHelpers.saveDirectoryVersionsAsync
                    repositoryId
                    [
                        firstChild
                        firstRoot
                        secondChild
                        secondRoot
                    ]

            /// Asserts ambiguous for integration responses.
            let assertAmbiguous (response: HttpResponseMessage) =
                task {
                    let! body = response.Content.ReadAsStringAsync()
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
                    Assert.That((deserialize<GraceError> body).Error, Does.Contain("ambiguous"))
                }

            let! getVersionResponse =
                BranchServerTestHelpers.getVersionByShaAndBlake3HashResponseAsync repositoryId branch (Sha256Hash String.Empty) (Blake3Hash sharedPrefix)

            do! assertAmbiguous getVersionResponse

            let! listContentsResponse =
                BranchServerTestHelpers.listContentsByShaAndBlake3HashResponseAsync repositoryId branch (Sha256Hash String.Empty) (Blake3Hash sharedPrefix)

            do! assertAmbiguous listContentsResponse

            let! recursiveSizeResponse = BranchServerTestHelpers.getRecursiveSizeByBlake3HashResponseAsync repositoryId branch (Blake3Hash sharedPrefix)

            do! assertAmbiguous recursiveSizeResponse
        }

    /// Verifies the save with SHA only child directory prefix does not create root reference scenario.
    [<Test>]
    member _.SaveWithShaOnlyChildDirectoryPrefixDoesNotCreateRootReference() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"ChildPrefix{Guid.NewGuid():N}"
            /// Defines child behavior for the surrounding tests used by the server integration branch scenario.
            let child, root = BranchServerTestHelpers.createDotRootWithChildDirectoryVersions repositoryId $"child-prefix/{Guid.NewGuid():N}"

            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ child; root ]

            let childOnlyPrefix =
                let shortestPrefix =
                    BranchServerTestHelpers.shortestUniquePrefix
                        child.Sha256Hash
                        [
                            root.Sha256Hash
                            parentBranch.BasedOn.Sha256Hash
                        ]

                (string child.Sha256Hash)
                    .Substring(0, Math.Max(16, shortestPrefix.Length))

            let! response = BranchServerTestHelpers.saveReferenceResponseAsync repositoryId branch DirectoryVersionId.Empty (Sha256Hash childOnlyPrefix)
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), responseBody)
            Assert.That(responseBody, Does.Contain("Reference root DirectoryVersion does not exist."))
        }

    /// Verifies the save with BLAKE3 child directory prefix does not create root reference scenario.
    [<Test>]
    member _.SaveWithBlake3ChildDirectoryPrefixDoesNotCreateRootReference() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"Blake3ChildPrefix{Guid.NewGuid():N}"
            /// Defines child behavior for the surrounding tests used by the server integration branch scenario.
            let child, root = BranchServerTestHelpers.createDotRootWithChildDirectoryVersions repositoryId $"blake3-child-prefix/{Guid.NewGuid():N}"

            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ child; root ]

            let childOnlyPrefix =
                BranchServerTestHelpers.shortestUniqueBlake3Prefix
                    child.Blake3Hash
                    [
                        root.Blake3Hash
                        parentBranch.BasedOn.Blake3Hash
                    ]

            let stableChildOnlyPrefix =
                if childOnlyPrefix.Length < 16 then
                    (string child.Blake3Hash).Substring(0, 16)
                else
                    childOnlyPrefix

            let! response =
                BranchServerTestHelpers.saveReferenceByBlake3ResponseAsync repositoryId branch DirectoryVersionId.Empty (Blake3Hash stableChildOnlyPrefix)

            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), responseBody)
            Assert.That(responseBody, Does.Contain("Reference root DirectoryVersion does not exist."))
        }

    /// Verifies the get recursive size with child directory BLAKE3 matches SHA lookup scenario.
    [<Test>]
    member _.GetRecursiveSizeWithChildDirectoryBlake3MatchesShaLookup() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let payload = Encoding.UTF8.GetBytes($"recursive-size-child-{Guid.NewGuid():N}")
            let childPath = RelativePath $"recursive-size/{Guid.NewGuid():N}"
            let filePath = RelativePath $"{childPath}/sample.txt"

            let fileVersion =
                FileVersion.CreateWithHashes
                    filePath
                    (BranchServerTestHelpers.sha256Hex payload)
                    (BranchServerTestHelpers.blake3Hex payload)
                    String.Empty
                    false
                    (int64 payload.Length)

            let child = BranchServerTestHelpers.createDirectoryVersionWithFile repositoryId childPath fileVersion
            let root = BranchServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId Constants.RootDirectoryPath [ child ]

            do! BranchServerTestHelpers.uploadFileToObjectStorageAsync repositoryId payload fileVersion
            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ child; root ]

            let! shaResponse = BranchServerTestHelpers.getRecursiveSizeBySha256HashResponseAsync repositoryId parentBranch child.Sha256Hash
            let! shaBody = shaResponse.Content.ReadAsStringAsync()
            Assert.That(shaResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), shaBody)

            let shaSize =
                (deserialize<GraceReturnValue<int64>> shaBody)
                    .ReturnValue

            let! blake3Response = BranchServerTestHelpers.getRecursiveSizeByBlake3HashResponseAsync repositoryId parentBranch child.Blake3Hash
            let! blake3Body = blake3Response.Content.ReadAsStringAsync()
            Assert.That(blake3Response.StatusCode, Is.EqualTo(HttpStatusCode.OK), blake3Body)

            let blake3Size =
                (deserialize<GraceReturnValue<int64>> blake3Body)
                    .ReturnValue

            Assert.That(shaSize, Is.EqualTo(fileVersion.Size))
            Assert.That(blake3Size, Is.EqualTo(shaSize))
            Assert.That(blake3Size, Is.Not.EqualTo(Constants.InitialDirectorySize))
        }

    /// Verifies the hash read queries reject inconsistent SHA and BLAKE3 root evidence scenario.
    [<Test>]
    member _.HashReadQueriesRejectInconsistentShaAndBlake3RootEvidence() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId

            /// Defines first child behavior for the surrounding tests used by the server integration branch scenario.
            let firstChild, firstRoot, secondChild, secondRoot, _sharedPrefix =
                BranchServerTestHelpers.createSameBlake3PrefixRootPair repositoryId $"read-mixed-hash-mismatch/{Guid.NewGuid():N}"

            do!
                BranchServerTestHelpers.saveDirectoryVersionsAsync
                    repositoryId
                    [
                        firstChild
                        firstRoot
                        secondChild
                        secondRoot
                    ]

            let! listContentsResponse =
                BranchServerTestHelpers.listContentsByShaAndBlake3HashResponseAsync repositoryId parentBranch firstRoot.Sha256Hash secondRoot.Blake3Hash

            let! listContentsBody = listContentsResponse.Content.ReadAsStringAsync()
            Assert.That(listContentsResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), listContentsBody)

            let listContents =
                (deserialize<GraceReturnValue<DirectoryVersion.DirectoryVersionDto array>> listContentsBody)
                    .ReturnValue

            Assert.That(listContents, Is.Empty)

            let! recursiveSizeResponse =
                BranchServerTestHelpers.getRecursiveSizeByShaAndBlake3HashResponseAsync repositoryId parentBranch firstRoot.Sha256Hash secondRoot.Blake3Hash

            let! recursiveSizeBody = recursiveSizeResponse.Content.ReadAsStringAsync()
            Assert.That(recursiveSizeResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), recursiveSizeBody)

            let recursiveSize =
                (deserialize<GraceReturnValue<int64>> recursiveSizeBody)
                    .ReturnValue

            Assert.That(recursiveSize, Is.EqualTo(Constants.InitialDirectorySize))

            let! getVersionResponse =
                BranchServerTestHelpers.getVersionByShaAndBlake3HashResponseAsync repositoryId parentBranch firstRoot.Sha256Hash secondRoot.Blake3Hash

            let! getVersionBody = getVersionResponse.Content.ReadAsStringAsync()
            Assert.That(getVersionResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), getVersionBody)

            let directoryIds =
                (deserialize<GraceReturnValue<Guid array>> getVersionBody)
                    .ReturnValue

            Assert.That(directoryIds, Is.Empty)
        }

    /// Verifies the hash read queries use SHA to disambiguate shared BLAKE3 root prefix scenario.
    [<Test>]
    member _.HashReadQueriesUseShaToDisambiguateSharedBlake3RootPrefix() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId

            /// Defines first child behavior for the surrounding tests used by the server integration branch scenario.
            let firstChild, firstRoot, secondChild, secondRoot, sharedPrefix =
                BranchServerTestHelpers.createSameBlake3PrefixRootPair repositoryId $"read-paired-hash/{Guid.NewGuid():N}"

            do!
                BranchServerTestHelpers.saveDirectoryVersionsAsync
                    repositoryId
                    [
                        firstChild
                        firstRoot
                        secondChild
                        secondRoot
                    ]

            let! listContentsResponse =
                BranchServerTestHelpers.listContentsByShaAndBlake3HashResponseAsync repositoryId parentBranch firstRoot.Sha256Hash (Blake3Hash sharedPrefix)

            let! listContentsBody = listContentsResponse.Content.ReadAsStringAsync()
            Assert.That(listContentsResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), listContentsBody)

            let listContents =
                (deserialize<GraceReturnValue<DirectoryVersion.DirectoryVersionDto array>> listContentsBody)
                    .ReturnValue

            Assert.That(
                listContents
                |> Array.exists (fun directoryVersionDto -> directoryVersionDto.DirectoryVersion.DirectoryVersionId = firstRoot.DirectoryVersionId),
                Is.True
            )

            let! getVersionResponse =
                BranchServerTestHelpers.getVersionByShaAndBlake3HashResponseAsync repositoryId parentBranch firstRoot.Sha256Hash (Blake3Hash sharedPrefix)

            let! getVersionBody = getVersionResponse.Content.ReadAsStringAsync()
            Assert.That(getVersionResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), getVersionBody)

            let directoryIds =
                (deserialize<GraceReturnValue<Guid array>> getVersionBody)
                    .ReturnValue

            Assert.That(directoryIds, Does.Contain(firstRoot.DirectoryVersionId))
        }

    /// Verifies the save with child directory version ID and SHA prefix does not create root reference scenario.
    [<Test>]
    member _.SaveWithChildDirectoryVersionIdAndShaPrefixDoesNotCreateRootReference() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"ChildIdPrefix{Guid.NewGuid():N}"
            /// Defines child behavior for the surrounding tests used by the server integration branch scenario.
            let child, root = BranchServerTestHelpers.createDotRootWithChildDirectoryVersions repositoryId $"child-id-prefix/{Guid.NewGuid():N}"

            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ child; root ]

            let childShaPrefix = (string child.Sha256Hash).Substring(0, 8)
            let! response = BranchServerTestHelpers.saveReferenceResponseAsync repositoryId branch child.DirectoryVersionId (Sha256Hash childShaPrefix)
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), responseBody)
            Assert.That(responseBody, Does.Contain("Reference root DirectoryVersion must use the repository root path."))
        }

    /// Verifies the annotate route and SDK return envelope for server known reference scenario.
    [<Test; NonParallelizable>]
    member _.AnnotateRouteAndSdkReturnEnvelopeForServerKnownReference() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch, fileVersion, targetReferenceId = BranchServerTestHelpers.createAnnotatableReferenceAsync repositoryId parentBranch
            let parameters = BranchServerTestHelpers.annotateParameters repositoryId branch fileVersion
            parameters.TargetReferenceId <- targetReferenceId

            let! response = Client.PostAsync("/branch/annotate", createJsonContent parameters)
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseBody)

            let returnValue = deserialize<GraceReturnValue<BranchAnnotationDto>> responseBody
            Assert.That(returnValue.ReturnValue.TargetReferenceId, Is.EqualTo(parameters.TargetReferenceId))
            Assert.That(returnValue.ReturnValue.Path, Is.EqualTo(fileVersion.RelativePath))
            Assert.That(returnValue.ReturnValue.IncludeLineText, Is.True)
            Assert.That(returnValue.ReturnValue.Lines, Is.Not.Empty)
            Assert.That(returnValue.Properties[ "Path" ].ToString(), Is.EqualTo("/branch/annotate"))

            do!
                BranchServerTestHelpers.withExplicitSdkConfigurationForServerAsync (fun () ->
                    task {
                        do! BranchServerTestHelpers.configureSdkForServerAsync ()
                        parameters.CorrelationId <- generateCorrelationId ()
                        let! sdkResult = Grace.SDK.Branch.Annotate parameters

                        match sdkResult with
                        | Ok sdkReturnValue ->
                            Assert.That(sdkReturnValue.ReturnValue.TargetReferenceId, Is.EqualTo(parameters.TargetReferenceId))
                            Assert.That(sdkReturnValue.ReturnValue.Path, Is.EqualTo(fileVersion.RelativePath))
                        | Error error -> Assert.Fail($"Expected SDK Branch.Annotate success, got {error.Error}.")
                    })
        }

    /// Verifies the authenticated missing-cursor route matches exact roots, baselines unknown roots, and projects generic failures.
    [<Test; NonParallelizable>]
    member _.ResolveReferenceEventBoundaryRoutePreservesLocalRootWithoutMaterialization() =
        task {
            let repositoryId = repositoryIds[0]
            let parentBranchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId parentBranchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"ResolveBoundary{Guid.NewGuid():N}"
            let eligibleRoot = BranchServerTestHelpers.createSlashRootDirectoryVersion repositoryId
            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ eligibleRoot ]
            let! saveResponse = BranchServerTestHelpers.saveReferenceResponseAsync repositoryId branch eligibleRoot.DirectoryVersionId eligibleRoot.Sha256Hash
            let! saveBody = saveResponse.Content.ReadAsStringAsync()
            Assert.That(saveResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), saveBody)

            do!
                BranchServerTestHelpers.withExplicitSdkConfigurationForServerAsync (fun () ->
                    task {
                        do! BranchServerTestHelpers.configureSdkForServerAsync ()

                        let parameters = Parameters.Branch.ResolveReferenceEventBoundaryParameters()
                        parameters.OwnerId <- ownerId
                        parameters.OrganizationId <- organizationId
                        parameters.RepositoryId <- repositoryId
                        parameters.BranchId <- $"{branch.BranchId}"
                        parameters.DirectoryVersionId <- eligibleRoot.DirectoryVersionId
                        parameters.Sha256Hash <- eligibleRoot.Sha256Hash
                        parameters.Blake3Hash <- eligibleRoot.Blake3Hash
                        parameters.CorrelationId <- generateCorrelationId ()

                        let! exactResult = Grace.SDK.Branch.ResolveReferenceEventBoundary parameters

                        let exactBoundary =
                            match exactResult with
                            | Ok returnValue -> returnValue.ReturnValue
                            | Error error ->
                                Assert.Fail($"Expected exact missing-cursor boundary success, got {error.Error}.")
                                Unchecked.defaultof<Reference.ReferenceMaterializationBoundaryDto>

                        Assert.That(exactBoundary.RepositoryId, Is.EqualTo(Guid.Parse(repositoryId)))
                        Assert.That(exactBoundary.BranchId, Is.EqualTo(branch.BranchId))
                        Assert.That(exactBoundary.DirectoryId, Is.EqualTo(eligibleRoot.DirectoryVersionId))
                        Assert.That(exactBoundary.Sha256Hash, Is.EqualTo(eligibleRoot.Sha256Hash))
                        Assert.That(exactBoundary.Blake3Hash, Is.EqualTo(eligibleRoot.Blake3Hash))
                        Assert.That(exactBoundary.EventCursor, Is.EqualTo("branch-event-v1:1"))

                        parameters.DirectoryVersionId <- branch.BasedOn.DirectoryId
                        parameters.Sha256Hash <- branch.BasedOn.Sha256Hash
                        parameters.Blake3Hash <- branch.BasedOn.Blake3Hash
                        parameters.CorrelationId <- generateCorrelationId ()

                        let! parentBaseResult = Grace.SDK.Branch.ResolveReferenceEventBoundary parameters

                        let parentBaseBoundary =
                            match parentBaseResult with
                            | Ok returnValue -> returnValue.ReturnValue
                            | Error error ->
                                Assert.Fail($"Expected parent-base conservative boundary success, got {error.Error}.")
                                Unchecked.defaultof<Reference.ReferenceMaterializationBoundaryDto>

                        Assert.That(parentBaseBoundary.EventCursor, Is.EqualTo("branch-event-v1:1"))

                        let replayParameters = Parameters.Branch.ReplayReferenceEventsParameters()
                        replayParameters.OwnerId <- ownerId
                        replayParameters.OrganizationId <- organizationId
                        replayParameters.RepositoryId <- repositoryId
                        replayParameters.BranchId <- $"{branch.BranchId}"
                        replayParameters.CursorRepositoryId <- repositoryId
                        replayParameters.CursorBranchId <- $"{branch.BranchId}"
                        replayParameters.EventCursor <- parentBaseBoundary.EventCursor
                        replayParameters.CorrelationId <- generateCorrelationId ()

                        let! parentBaseReplayResult = Grace.SDK.Branch.ReplayReferenceEvents replayParameters

                        match parentBaseReplayResult with
                        | Ok returnValue ->
                            Assert.That(returnValue.ReturnValue.Events, Is.Empty)
                            Assert.That(returnValue.ReturnValue.ScannedThroughCursor, Is.EqualTo("branch-event-v1:1"))
                        | Error error -> Assert.Fail($"Expected parent-base replay closure success, got {error.Error}.")

                        parameters.DirectoryVersionId <- Guid.NewGuid()
                        parameters.Sha256Hash <- Sha256Hash "unmatched-local-sha"
                        parameters.Blake3Hash <- Blake3Hash "unmatched-local-blake3"
                        parameters.CorrelationId <- generateCorrelationId ()

                        let! baselineResult = Grace.SDK.Branch.ResolveReferenceEventBoundary parameters

                        let unmatchedBoundary =
                            match baselineResult with
                            | Ok returnValue -> returnValue.ReturnValue
                            | Error error ->
                                Assert.Fail($"Expected conservative missing-cursor baseline success, got {error.Error}.")
                                Unchecked.defaultof<Reference.ReferenceMaterializationBoundaryDto>

                        Assert.That(unmatchedBoundary.DirectoryId, Is.EqualTo(parameters.DirectoryVersionId))
                        Assert.That(unmatchedBoundary.Sha256Hash, Is.EqualTo(parameters.Sha256Hash))
                        Assert.That(unmatchedBoundary.Blake3Hash, Is.EqualTo(parameters.Blake3Hash))
                        Assert.That(unmatchedBoundary.EventCursor, Is.EqualTo("branch-event-v1:1"))

                        let futureRoot = BranchServerTestHelpers.createSlashRootDirectoryVersion repositoryId
                        do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ futureRoot ]

                        let! futureSave =
                            BranchServerTestHelpers.saveReferenceResponseAsync repositoryId branch futureRoot.DirectoryVersionId futureRoot.Sha256Hash

                        let! futureSaveBody = futureSave.Content.ReadAsStringAsync()
                        Assert.That(futureSave.StatusCode, Is.EqualTo(HttpStatusCode.OK), futureSaveBody)

                        replayParameters.EventCursor <- unmatchedBoundary.EventCursor
                        replayParameters.CorrelationId <- generateCorrelationId ()
                        let! unmatchedReplayResult = Grace.SDK.Branch.ReplayReferenceEvents replayParameters

                        match unmatchedReplayResult with
                        | Ok returnValue ->
                            Assert.That(returnValue.ReturnValue.Events, Has.Length.EqualTo(1))

                            Assert.That(
                                returnValue.ReturnValue.Events[0]
                                    .Reference
                                    .DirectoryId,
                                Is.EqualTo(futureRoot.DirectoryVersionId)
                            )

                            Assert.That(returnValue.ReturnValue.Events[0].EventCursor, Is.EqualTo("branch-event-v1:2"))
                            Assert.That(returnValue.ReturnValue.ScannedThroughCursor, Is.EqualTo("branch-event-v1:2"))
                        | Error error -> Assert.Fail($"Expected unmatched-boundary future replay success, got {error.Error}.")

                        parameters.DirectoryVersionId <- DirectoryVersionId.Empty
                        parameters.CorrelationId <- generateCorrelationId ()
                        let! invalidResponse = Client.PostAsync("/branch/resolveReferenceEventBoundary", createJsonContent parameters)
                        let! invalidBody = invalidResponse.Content.ReadAsStringAsync()
                        Assert.That(invalidResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), invalidBody)
                        Assert.That(invalidBody, Does.Contain("cannot establish a Watch event boundary for this branch"))
                        Assert.That(invalidBody, Does.Not.Contain("System."))
                    })
        }

    /// A reset local database recovers through production Watch, materializes one later event, and durably acknowledges its cursor.
    [<Test; NonParallelizable>]
    member _.ProductionWatchMissingBoundaryRecoveryReplaysExactlyOneLaterReference() =
        task {
            let repositoryId = repositoryIds[0]
            let parentBranchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId parentBranchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"WatchRecovery{Guid.NewGuid():N}"
            let recoveredRoot = BranchServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId Constants.RootDirectoryPath []
            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ recoveredRoot ]

            let! recoveredSave =
                BranchServerTestHelpers.saveReferenceResponseAsync repositoryId branch recoveredRoot.DirectoryVersionId recoveredRoot.Sha256Hash

            let! recoveredSaveBody = recoveredSave.Content.ReadAsStringAsync()
            Assert.That(recoveredSave.StatusCode, Is.EqualTo(HttpStatusCode.OK), recoveredSaveBody)

            let relativePath = RelativePath $"watch-runtime-{Guid.NewGuid():N}.txt"
            let payload = Encoding.UTF8.GetBytes("production missing-boundary replay")

            let fileVersion =
                FileVersion.CreateWithHashes
                    relativePath
                    (BranchServerTestHelpers.sha256Hex payload)
                    (BranchServerTestHelpers.blake3Hex payload)
                    String.Empty
                    false
                    (int64 payload.Length)

            do! BranchServerTestHelpers.uploadFileToObjectStorageAsync repositoryId payload fileVersion
            let laterRoot = BranchServerTestHelpers.createDirectoryVersionWithFile repositoryId Constants.RootDirectoryPath fileVersion
            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ laterRoot ]
            let! laterSave = BranchServerTestHelpers.saveReferenceResponseAsync repositoryId branch laterRoot.DirectoryVersionId laterRoot.Sha256Hash
            let! laterSaveBody = laterSave.Content.ReadAsStringAsync()
            Assert.That(laterSave.StatusCode, Is.EqualTo(HttpStatusCode.OK), laterSaveBody)

            do!
                BranchServerTestHelpers.withExplicitSdkConfigurationForServerAsync (fun () ->
                    task {
                        do! BranchServerTestHelpers.configureSdkForServerAsync ()
                        let configuration = Current()
                        configuration.OwnerId <- Guid.Parse(ownerId)
                        configuration.OrganizationId <- Guid.Parse(organizationId)
                        configuration.RepositoryId <- Guid.Parse(repositoryId)
                        configuration.RepositoryName <- "watch-runtime-repository"
                        configuration.BranchId <- branch.BranchId
                        configuration.BranchName <- string branch.BranchName

                        saveConfigFile (Path.Combine(configuration.RootDirectory, Constants.GraceConfigDirectory, Constants.GraceConfigFileName)) configuration

                        let localRoot = recoveredRoot.ToLocalDirectoryVersion(DateTime.UtcNow)
                        let index = GraceIndex()

                        index.TryAdd(localRoot.DirectoryVersionId, localRoot)
                        |> ignore

                        let initialStatus =
                            { GraceStatus.Default with
                                Index = index
                                RootDirectoryId = localRoot.DirectoryVersionId
                                RootDirectorySha256Hash = localRoot.Sha256Hash
                                RootDirectoryBlake3Hash = localRoot.Blake3Hash
                            }

                        let objectCachePath = Grace.CLI.Services.getLocalObjectCachePathForFileVersion fileVersion

                        Directory.CreateDirectory(Path.GetDirectoryName(objectCachePath))
                        |> ignore

                        File.WriteAllBytes(objectCachePath, payload)

                        Grace.CLI.Command.Watch.clearPendingWatchWorkForTests ()
                        Assert.That(File.Exists(configuration.GraceStatusFile), Is.False)
                        do! Grace.CLI.LocalStateDb.ensureDbInitialized configuration.GraceStatusFile
                        do! Grace.CLI.Services.writeGraceStatusFile initialStatus
                        do! Grace.CLI.Services.updateGraceWatchInterprocessFile initialStatus (Some(HashSet<DirectoryVersionId>(index.Keys)))

                        let ipcPath = Grace.CLI.Services.IpcFileName()
                        let retainedStatus = deserialize<Grace.CLI.Services.GraceWatchStatus> (File.ReadAllText(ipcPath))

                        File.WriteAllText(
                            ipcPath,
                            serialize { retainedStatus with UpdatedAt = retainedStatus.UpdatedAt.Minus(NodaTime.Duration.FromMinutes(10.0)) }
                        )

                        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                        GC.Collect()
                        GC.WaitForPendingFinalizers()

                        [|
                            configuration.GraceStatusFile
                            configuration.GraceStatusFile + "-wal"
                            configuration.GraceStatusFile + "-shm"
                            configuration.GraceStatusFile + "-journal"
                        |]
                        |> Array.iter (fun path -> if File.Exists(path) then File.Delete(path))

                        try
                            Assert.That(File.Exists(configuration.GraceStatusFile), Is.False)
                            Assert.That(File.Exists(ipcPath), Is.True)

                            let! claim = Grace.CLI.Services.claimGraceWatchInterprocessFile ()
                            Assert.That(claim.Claimed, Is.True)
                            Assert.That(claim.RetainedStatus.IsSome, Is.True)
                            Grace.CLI.Command.Watch.setGraceWatchRuntimeModeForWatchTests Grace.CLI.Services.GraceWatchRuntimeMode.StartingUp

                            let! resetStatus = Grace.CLI.Services.readGraceStatusFile ()
                            Assert.That(resetStatus.RootDirectoryId, Is.EqualTo(DirectoryVersionId.Empty))
                            let materializedPath = Path.Combine(configuration.RootDirectory, string relativePath)

                            use cancelled = new System.Threading.CancellationTokenSource()
                            cancelled.Cancel()

                            Assert.ThrowsAsync<OperationCanceledException>(
                                Func<Task> (fun () ->
                                    Grace.CLI.Command.Watch.establishMissingCurrentBranchReferenceBoundaryBeforeStartupScanForHostedTests
                                        claim.RetainedStatus
                                        resetStatus
                                        cancelled.Token
                                    :> Task)
                            )
                            |> ignore

                            let! afterCancellation =
                                Grace.CLI.LocalStateDb.readRemoteReferenceBoundary
                                    configuration.GraceStatusFile
                                    configuration.RepositoryId
                                    configuration.BranchId

                            Assert.That(afterCancellation, Is.EqualTo(None))
                            Assert.That(File.Exists(materializedPath), Is.False)

                            let configPath = Path.Combine(configuration.RootDirectory, Constants.GraceConfigDirectory, Constants.GraceConfigFileName)
                            File.Delete(configPath)

                            try
                                Assert.ThrowsAsync<InvalidOperationException>(
                                    Func<Task> (fun () ->
                                        Grace.CLI.Command.Watch.establishMissingCurrentBranchReferenceBoundaryBeforeStartupScanForHostedTests
                                            claim.RetainedStatus
                                            resetStatus
                                            System.Threading.CancellationToken.None
                                        :> Task)
                                )
                                |> ignore
                            finally
                                saveConfigFile configPath configuration

                            let! afterFailure =
                                Grace.CLI.LocalStateDb.readRemoteReferenceBoundary
                                    configuration.GraceStatusFile
                                    configuration.RepositoryId
                                    configuration.BranchId

                            Assert.That(afterFailure, Is.EqualTo(None))
                            Assert.That(File.Exists(materializedPath), Is.False)

                            let! _ =
                                Grace.CLI.Command.Watch.establishMissingCurrentBranchReferenceBoundaryBeforeStartupScanForHostedTests
                                    claim.RetainedStatus
                                    resetStatus
                                    System.Threading.CancellationToken.None

                            do!
                                Grace.CLI.Command.Watch.completeStartupRecoveryIfPendingWorkDrainedWithCatchUpForWatchTests
                                    Grace.CLI.Services.readGraceStatusFile
                                    Grace.CLI.Services.updateGraceWatchInterprocessFile
                                    (fun () -> task { Assert.Fail("Startup processing must remain idle before boundary recovery completes.") })
                                    (fun () -> Grace.CLI.Command.Watch.replayCurrentBranchReferenceEventsForHostedTests System.Threading.CancellationToken.None)

                            Assert.That(File.Exists(materializedPath), Is.True)
                            Assert.That(Convert.ToHexString(File.ReadAllBytes(materializedPath)), Is.EqualTo(Convert.ToHexString(payload)))

                            let! durableStatus = Grace.CLI.Services.readGraceStatusFile ()
                            Assert.That(durableStatus.RootDirectoryId, Is.EqualTo(laterRoot.DirectoryVersionId))
                            Assert.That(durableStatus.RootDirectorySha256Hash, Is.EqualTo(laterRoot.Sha256Hash))
                            Assert.That(durableStatus.RootDirectoryBlake3Hash, Is.EqualTo(laterRoot.Blake3Hash))

                            let! boundary =
                                Grace.CLI.LocalStateDb.readRemoteReferenceBoundary
                                    configuration.GraceStatusFile
                                    configuration.RepositoryId
                                    configuration.BranchId

                            Assert.That(boundary.IsSome, Is.True)
                            Assert.That(boundary.Value.DirectoryId, Is.EqualTo(laterRoot.DirectoryVersionId))
                            Assert.That(boundary.Value.EventCursor, Is.EqualTo("branch-event-v1:2"))
                        finally
                            Grace.CLI.Command.Watch.clearPendingWatchWorkForTests ()
                            if File.Exists(ipcPath) then File.Delete(ipcPath)
                    })
        }

    /// A real DB reset with an unmatched retained root preserves local files and starts at the current server tail.
    [<Test; NonParallelizable>]
    member _.ProductionWatchMissingBoundaryRecoveryBaselinesUnmatchedRootWithoutMutation() =
        task {
            let repositoryId = repositoryIds[0]
            let parentBranchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId parentBranchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"WatchBaseline{Guid.NewGuid():N}"
            let serverRoot = BranchServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId Constants.RootDirectoryPath []
            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ serverRoot ]
            let! serverSave = BranchServerTestHelpers.saveReferenceResponseAsync repositoryId branch serverRoot.DirectoryVersionId serverRoot.Sha256Hash
            let! serverSaveBody = serverSave.Content.ReadAsStringAsync()
            Assert.That(serverSave.StatusCode, Is.EqualTo(HttpStatusCode.OK), serverSaveBody)

            do!
                BranchServerTestHelpers.withExplicitSdkConfigurationForServerAsync (fun () ->
                    task {
                        do! BranchServerTestHelpers.configureSdkForServerAsync ()
                        let configuration = Current()
                        configuration.OwnerId <- Guid.Parse(ownerId)
                        configuration.OrganizationId <- Guid.Parse(organizationId)
                        configuration.RepositoryId <- Guid.Parse(repositoryId)
                        configuration.RepositoryName <- "watch-baseline-repository"
                        configuration.BranchId <- branch.BranchId
                        configuration.BranchName <- string branch.BranchName

                        saveConfigFile (Path.Combine(configuration.RootDirectory, Constants.GraceConfigDirectory, Constants.GraceConfigFileName)) configuration

                        let localRoot =
                            LocalDirectoryVersion.CreateWithHashes
                                (Guid.NewGuid())
                                configuration.OwnerId
                                configuration.OrganizationId
                                configuration.RepositoryId
                                Constants.RootDirectoryPath
                                (Sha256Hash "unmatched-local-root-sha256")
                                (Blake3Hash "unmatched-local-root-blake3")
                                (List<DirectoryVersionId>())
                                (List<LocalFileVersion>())
                                Constants.InitialDirectorySize
                                DateTime.UtcNow

                        let index = GraceIndex()

                        index.TryAdd(localRoot.DirectoryVersionId, localRoot)
                        |> ignore

                        let initialStatus =
                            { GraceStatus.Default with
                                Index = index
                                RootDirectoryId = localRoot.DirectoryVersionId
                                RootDirectorySha256Hash = localRoot.Sha256Hash
                                RootDirectoryBlake3Hash = localRoot.Blake3Hash
                            }

                        let retainedFilePath = Path.Combine(configuration.RootDirectory, $"retained-{Guid.NewGuid():N}.txt")
                        let retainedBytes = Encoding.UTF8.GetBytes("unmatched local content wins")
                        File.WriteAllBytes(retainedFilePath, retainedBytes)

                        Grace.CLI.Command.Watch.clearPendingWatchWorkForTests ()
                        do! Grace.CLI.LocalStateDb.ensureDbInitialized configuration.GraceStatusFile
                        do! Grace.CLI.Services.writeGraceStatusFile initialStatus
                        do! Grace.CLI.Services.updateGraceWatchInterprocessFile initialStatus (Some(HashSet<DirectoryVersionId>(index.Keys)))

                        let ipcPath = Grace.CLI.Services.IpcFileName()
                        let retainedStatus = deserialize<Grace.CLI.Services.GraceWatchStatus> (File.ReadAllText(ipcPath))

                        File.WriteAllText(
                            ipcPath,
                            serialize { retainedStatus with UpdatedAt = retainedStatus.UpdatedAt.Minus(NodaTime.Duration.FromMinutes(10.0)) }
                        )

                        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
                        GC.Collect()
                        GC.WaitForPendingFinalizers()

                        [|
                            configuration.GraceStatusFile
                            configuration.GraceStatusFile + "-wal"
                            configuration.GraceStatusFile + "-shm"
                            configuration.GraceStatusFile + "-journal"
                        |]
                        |> Array.iter (fun path -> if File.Exists(path) then File.Delete(path))

                        try
                            let! claim = Grace.CLI.Services.claimGraceWatchInterprocessFile ()
                            Assert.That(claim.Claimed, Is.True)
                            Assert.That(claim.RetainedStatus.IsSome, Is.True)
                            Grace.CLI.Command.Watch.setGraceWatchRuntimeModeForWatchTests Grace.CLI.Services.GraceWatchRuntimeMode.StartingUp

                            let! resetStatus = Grace.CLI.Services.readGraceStatusFile ()

                            let! recoveredStatus =
                                Grace.CLI.Command.Watch.establishMissingCurrentBranchReferenceBoundaryBeforeStartupScanForHostedTests
                                    claim.RetainedStatus
                                    resetStatus
                                    System.Threading.CancellationToken.None

                            Assert.That(Convert.ToHexString(File.ReadAllBytes(retainedFilePath)), Is.EqualTo(Convert.ToHexString(retainedBytes)))
                            Assert.That(recoveredStatus.RootDirectoryId, Is.EqualTo(localRoot.DirectoryVersionId))

                            let! boundary =
                                Grace.CLI.LocalStateDb.readRemoteReferenceBoundary
                                    configuration.GraceStatusFile
                                    configuration.RepositoryId
                                    configuration.BranchId

                            Assert.That(boundary.IsSome, Is.True)
                            Assert.That(boundary.Value.DirectoryId, Is.EqualTo(localRoot.DirectoryVersionId))
                            Assert.That(boundary.Value.Sha256Hash, Is.EqualTo(localRoot.Sha256Hash))
                            Assert.That(boundary.Value.Blake3Hash, Is.EqualTo(localRoot.Blake3Hash))
                            Assert.That(boundary.Value.EventCursor, Is.EqualTo("branch-event-v1:1"))

                            let mutable startupProcessingCount = 0

                            do!
                                Grace.CLI.Command.Watch.completeStartupRecoveryIfPendingWorkDrainedWithCatchUpForWatchTests
                                    Grace.CLI.Services.readGraceStatusFile
                                    Grace.CLI.Services.updateGraceWatchInterprocessFile
                                    (fun () ->
                                        task {
                                            startupProcessingCount <- startupProcessingCount + 1
                                            Assert.Fail("Unmatched recovery must establish its tail boundary before startup processing.")
                                        })
                                    (fun () -> Grace.CLI.Command.Watch.replayCurrentBranchReferenceEventsForHostedTests System.Threading.CancellationToken.None)

                            Assert.That(startupProcessingCount, Is.EqualTo(0))
                            Assert.That(Convert.ToHexString(File.ReadAllBytes(retainedFilePath)), Is.EqualTo(Convert.ToHexString(retainedBytes)))

                            let! branchAfterRecovery = BranchServerTestHelpers.getBranchAsync repositoryId $"{branch.BranchId}"
                            Assert.That(branchAfterRecovery.LatestReference.DirectoryId, Is.EqualTo(serverRoot.DirectoryVersionId))
                        finally
                            Grace.CLI.Command.Watch.clearPendingWatchWorkForTests ()
                            if File.Exists(ipcPath) then File.Delete(ipcPath)
                    })
        }

    /// Verifies authenticated SDK replay preserves branch scope, event order, cursor closure, and generic cursor failures through the hosted route.
    [<Test; NonParallelizable>]
    member _.ReplayReferenceEventsRouteAndSdkPreserveOrderedBranchScopedCursorContract() =
        task {
            let repositoryId = repositoryIds[0]
            let parentBranchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId parentBranchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"ReplayRoute{Guid.NewGuid():N}"

            do!
                BranchServerTestHelpers.withExplicitSdkConfigurationForServerAsync (fun () ->
                    task {
                        do! BranchServerTestHelpers.configureSdkForServerAsync ()

                        let replayParameters = Parameters.Branch.ReplayReferenceEventsParameters()
                        replayParameters.OwnerId <- ownerId
                        replayParameters.OrganizationId <- organizationId
                        replayParameters.RepositoryId <- repositoryId
                        replayParameters.BranchId <- $"{branch.BranchId}"
                        replayParameters.CursorRepositoryId <- repositoryId
                        replayParameters.CursorBranchId <- $"{branch.BranchId}"
                        replayParameters.EventCursor <- "branch-event-v1:0"
                        replayParameters.CorrelationId <- generateCorrelationId ()

                        let! baselineResult = Grace.SDK.Branch.ReplayReferenceEvents replayParameters

                        let baseline =
                            match baselineResult with
                            | Ok returnValue -> returnValue.ReturnValue
                            | Error error ->
                                Assert.Fail($"Expected authenticated SDK replay baseline success, got {error.Error}.")
                                Unchecked.defaultof<Reference.ReferenceReplayDto>

                        Assert.That(baseline.RepositoryId, Is.EqualTo(Guid.Parse(repositoryId)))
                        Assert.That(baseline.BranchId, Is.EqualTo(branch.BranchId))
                        Assert.That(baseline.Events, Is.Empty)
                        Assert.That(baseline.ScannedThroughCursor, Does.StartWith("branch-event-v1:"))

                        let saveReferenceId = Guid.NewGuid()
                        let saveParameters = Parameters.Branch.CreateReferenceParameters()
                        saveParameters.OwnerId <- ownerId
                        saveParameters.OrganizationId <- organizationId
                        saveParameters.RepositoryId <- repositoryId
                        saveParameters.BranchId <- $"{branch.BranchId}"
                        saveParameters.ReferenceId <- saveReferenceId
                        saveParameters.DirectoryVersionId <- branch.BasedOn.DirectoryId
                        saveParameters.Sha256Hash <- branch.BasedOn.Sha256Hash
                        saveParameters.Blake3Hash <- branch.BasedOn.Blake3Hash
                        saveParameters.Message <- "Hosted replay route ordered save"
                        saveParameters.CorrelationId <- generateCorrelationId ()

                        let! saveResponse = Client.PostAsync("/branch/save", createJsonContent saveParameters)
                        let! saveBody = saveResponse.Content.ReadAsStringAsync()
                        Assert.That(saveResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), saveBody)

                        let! saveRetryResponse = Client.PostAsync("/branch/save", createJsonContent saveParameters)
                        let! saveRetryBody = saveRetryResponse.Content.ReadAsStringAsync()
                        Assert.That(saveRetryResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), saveRetryBody)

                        let commitReferenceId = Guid.NewGuid()
                        let commitParameters = Parameters.Branch.CreateReferenceParameters()
                        commitParameters.OwnerId <- ownerId
                        commitParameters.OrganizationId <- organizationId
                        commitParameters.RepositoryId <- repositoryId
                        commitParameters.BranchId <- $"{branch.BranchId}"
                        commitParameters.ReferenceId <- commitReferenceId
                        commitParameters.DirectoryVersionId <- branch.BasedOn.DirectoryId
                        commitParameters.Sha256Hash <- branch.BasedOn.Sha256Hash
                        commitParameters.Blake3Hash <- branch.BasedOn.Blake3Hash
                        commitParameters.Message <- "Hosted replay route ordered commit"
                        commitParameters.CorrelationId <- generateCorrelationId ()

                        let! commitResponse = Client.PostAsync("/branch/commit", createJsonContent commitParameters)
                        let! commitBody = commitResponse.Content.ReadAsStringAsync()
                        Assert.That(commitResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), commitBody)

                        let checkpointReferenceId = Guid.NewGuid()
                        let checkpointParameters = Parameters.Branch.CreateReferenceParameters()
                        checkpointParameters.OwnerId <- ownerId
                        checkpointParameters.OrganizationId <- organizationId
                        checkpointParameters.RepositoryId <- repositoryId
                        checkpointParameters.BranchId <- $"{branch.BranchId}"
                        checkpointParameters.ReferenceId <- checkpointReferenceId
                        checkpointParameters.DirectoryVersionId <- branch.BasedOn.DirectoryId
                        checkpointParameters.Sha256Hash <- branch.BasedOn.Sha256Hash
                        checkpointParameters.Blake3Hash <- branch.BasedOn.Blake3Hash
                        checkpointParameters.Message <- "Hosted replay route ordered checkpoint"
                        checkpointParameters.CorrelationId <- generateCorrelationId ()

                        let! checkpointResponse = Client.PostAsync("/branch/checkpoint", createJsonContent checkpointParameters)
                        let! checkpointBody = checkpointResponse.Content.ReadAsStringAsync()
                        Assert.That(checkpointResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), checkpointBody)

                        replayParameters.EventCursor <- baseline.ScannedThroughCursor
                        replayParameters.CorrelationId <- generateCorrelationId ()
                        let! replayResult = Grace.SDK.Branch.ReplayReferenceEvents replayParameters

                        let replay =
                            match replayResult with
                            | Ok returnValue -> returnValue.ReturnValue
                            | Error error ->
                                Assert.Fail($"Expected authenticated SDK replay success, got {error.Error}.")
                                Unchecked.defaultof<Reference.ReferenceReplayDto>

                        Assert.That(replay.RepositoryId, Is.EqualTo(Guid.Parse(repositoryId)))
                        Assert.That(replay.BranchId, Is.EqualTo(branch.BranchId))
                        Assert.That(replay.Events, Has.Length.EqualTo(3))
                        Assert.That(replay.Events[0].Reference.ReferenceId, Is.EqualTo(saveReferenceId))
                        Assert.That(replay.Events[0].Reference.ReferenceType, Is.EqualTo(ReferenceType.Save))
                        Assert.That(replay.Events[1].Reference.ReferenceId, Is.EqualTo(commitReferenceId))
                        Assert.That(replay.Events[1].Reference.ReferenceType, Is.EqualTo(ReferenceType.Commit))
                        Assert.That(replay.Events[2].Reference.ReferenceId, Is.EqualTo(checkpointReferenceId))
                        Assert.That(replay.Events[2].Reference.ReferenceType, Is.EqualTo(ReferenceType.Checkpoint))
                        Assert.That(replay.Events[0].EventCursor, Is.Not.EqualTo(replay.Events[1].EventCursor))
                        Assert.That(replay.Events[1].EventCursor, Is.Not.EqualTo(replay.Events[2].EventCursor))
                        Assert.That(replay.ScannedThroughCursor, Is.EqualTo(replay.Events[2].EventCursor))

                        replayParameters.EventCursor <- "not-a-cursor"
                        replayParameters.CorrelationId <- generateCorrelationId ()
                        let! malformedResult = Grace.SDK.Branch.ReplayReferenceEvents replayParameters

                        match malformedResult with
                        | Ok _ -> Assert.Fail("Expected malformed replay cursor rejection through the SDK.")
                        | Error error ->
                            let projectedError = deserialize<GraceError> error.Error

                            Assert.That(
                                projectedError.Error,
                                Is.EqualTo("The supplied Watch replay cursor does not identify a valid interval for this branch.")
                            )

                        replayParameters.EventCursor <- baseline.ScannedThroughCursor
                        replayParameters.CursorRepositoryId <- $"{Guid.NewGuid()}"
                        replayParameters.CorrelationId <- generateCorrelationId ()
                        let! crossScopeResult = Grace.SDK.Branch.ReplayReferenceEvents replayParameters

                        match crossScopeResult with
                        | Ok _ -> Assert.Fail("Expected cross-repository replay cursor rejection through the SDK.")
                        | Error error ->
                            let projectedError = deserialize<GraceError> error.Error

                            Assert.That(
                                projectedError.Error,
                                Is.EqualTo("The supplied Watch replay cursor does not identify a valid interval for this branch.")
                            )
                    })
        }

    /// Verifies the annotate route returns grace error for bad parameters scenario.
    [<Test>]
    member _.AnnotateRouteReturnsGraceErrorForBadParameters() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! branch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let fileVersion = FileVersion.CreateWithHashes "annotate/bad-parameters.fs" String.Empty "blake3" String.Empty false 1L
            let parameters = BranchServerTestHelpers.annotateParameters repositoryId branch fileVersion
            parameters.MaxReferences <- MaximumMaxReferences + 1

            let! response = Client.PostAsync("/branch/annotate", createJsonContent parameters)
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), responseBody)

            let error = deserialize<GraceError> responseBody
            Assert.That(error.Error, Does.Contain("MaxReferences"))
            Assert.That(error.CorrelationId, Is.Not.Empty)
        }

    /// Verifies the annotate route returns grace error for null path scenario.
    [<Test>]
    member _.AnnotateRouteReturnsGraceErrorForNullPath() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! branch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let fileVersion = FileVersion.CreateWithHashes "annotate/null-path.fs" String.Empty "blake3" String.Empty false 1L
            let parameters = BranchServerTestHelpers.annotateParameters repositoryId branch fileVersion
            parameters.Path <- null

            let! response = Client.PostAsync("/branch/annotate", createJsonContent parameters)
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), responseBody)

            let error = deserialize<GraceError> responseBody
            Assert.That(error.Error, Is.EqualTo("Annotation Path must be a relative file path."))
            Assert.That(error.CorrelationId, Is.Not.Empty)
        }

    /// Verifies the annotate route returns grace error for null body scenario.
    [<Test>]
    member _.AnnotateRouteReturnsGraceErrorForNullBody() =
        task {
            use content = new StringContent("null", Encoding.UTF8, "application/json")

            let! response = Client.PostAsync("/branch/annotate", content)
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), responseBody)

            let error = deserialize<GraceError> responseBody
            Assert.That(error.Error, Is.EqualTo("Annotate parameters must not be null."))
            Assert.That(error.CorrelationId, Is.Not.Empty)
        }

    /// Verifies the annotate route returns grace error for null reference types scenario.
    [<Test>]
    member _.AnnotateRouteReturnsGraceErrorForNullReferenceTypes() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! branch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let fileVersion = FileVersion.CreateWithHashes "annotate/null-reference-types.fs" String.Empty "blake3" String.Empty false 1L
            let parameters = BranchServerTestHelpers.annotateParameters repositoryId branch fileVersion

            let json =
                $"""
{{
  "OwnerId": "{parameters.OwnerId}",
  "OrganizationId": "{parameters.OrganizationId}",
  "RepositoryId": "{parameters.RepositoryId}",
  "BranchId": "{parameters.BranchId}",
  "TargetReferenceId": "{parameters.TargetReferenceId}",
  "Path": "{parameters.Path}",
  "StartLine": {parameters.StartLine},
  "EndLine": {parameters.EndLine},
  "ReferenceTypes": null,
  "MaxReferences": {parameters.MaxReferences},
  "IncludeLineText": {parameters
                          .IncludeLineText
                          .ToString()
                          .ToLowerInvariant()},
  "CorrelationId": "{parameters.CorrelationId}"
}}
"""

            use content = new StringContent(json, Encoding.UTF8, "application/json")
            let! response = Client.PostAsync("/branch/annotate", content)
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), responseBody)

            let error = deserialize<GraceError> responseBody
            Assert.That(error.Error, Is.EqualTo("ReferenceTypes must not be null."))
            Assert.That(error.CorrelationId, Is.Not.Empty)
        }

    /// Verifies the annotate older target reference includes local ancestors before newest references scenario.
    [<Test>]
    member _.AnnotateOlderTargetReferenceIncludesLocalAncestorsBeforeNewestReferences() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! branch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"AnnotateWindow{Guid.NewGuid():N}"
            let relativePath = $"annotate/{Guid.NewGuid():N}/window.fs"
            let originalContent = $"let value = 1{Environment.NewLine}"

            let! branch, firstFileVersion, firstReferenceId =
                BranchServerTestHelpers.createAnnotatableReferenceWithContentAsync repositoryId branch relativePath originalContent

            let! branch, secondFileVersion, secondReferenceId =
                BranchServerTestHelpers.createAnnotatableReferenceWithContentAsync repositoryId branch relativePath originalContent

            let! _branch, _thirdFileVersion, _thirdReferenceId =
                BranchServerTestHelpers.createAnnotatableReferenceWithContentAsync repositoryId branch relativePath $"let value = 2{Environment.NewLine}"

            let parameters = BranchServerTestHelpers.annotateParameters repositoryId branch secondFileVersion
            parameters.TargetReferenceId <- secondReferenceId
            parameters.MaxReferences <- 2

            let! response = Client.PostAsync("/branch/annotate", createJsonContent parameters)
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseBody)

            let returnValue = deserialize<GraceReturnValue<BranchAnnotationDto>> responseBody
            let annotation = returnValue.ReturnValue

            Assert.That(
                annotation.SourceReferences
                |> Array.exists (fun sourceReference -> sourceReference.ReferenceId = firstReferenceId),
                Is.True
            )

            let firstReferenceSourceId = $"{firstReferenceId}"

            Assert.That(
                annotation.SourceRows
                |> Array.exists (fun sourceRow -> sourceRow.SourceReferenceId = firstReferenceSourceId),
                Is.True
            )
        }

    /// Verifies the annotate child rebase fetches parent history before based on promotion scenario.
    [<Test>]
    member _.AnnotateChildRebaseFetchesParentHistoryBeforeBasedOnPromotion() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let! parentWorkBranch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch $"AnnotateParent{Guid.NewGuid():N}"
            let relativePath = $"annotate/{Guid.NewGuid():N}/parent-history.fs"
            let content = $"let inherited = 319{Environment.NewLine}"

            let! parentWithSave, fileVersion, parentSaveReferenceId =
                BranchServerTestHelpers.createAnnotatableReferenceWithContentAsync repositoryId parentWorkBranch relativePath content

            let! parentWithPromotion = BranchServerTestHelpers.promoteLatestSaveAsync repositoryId parentWithSave
            let! childBranch = BranchServerTestHelpers.createBranchAsync repositoryId parentWithPromotion $"AnnotateParentHistory{Guid.NewGuid():N}"
            let! childBranch = BranchServerTestHelpers.rebaseBranchAsync repositoryId childBranch parentWithPromotion.LatestPromotion.ReferenceId
            let! childReferences = BranchServerTestHelpers.getBranchReferencesAsync repositoryId $"{childBranch.BranchId}"

            let childRebaseReference =
                childReferences
                |> Array.filter (fun referenceDto -> referenceDto.ReferenceType = ReferenceType.Rebase)
                |> Array.maxBy (fun referenceDto -> referenceDto.CreatedAt)

            let parameters = BranchServerTestHelpers.annotateParameters repositoryId childBranch fileVersion
            parameters.TargetReferenceId <- childRebaseReference.ReferenceId

            parameters.ReferenceTypes <-
                [|
                    ReferenceType.Save
                    ReferenceType.Promotion
                    ReferenceType.Rebase
                |]

            parameters.MaxReferences <- 10

            let! response = Client.PostAsync("/branch/annotate", createJsonContent parameters)
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), responseBody)

            let returnValue = deserialize<GraceReturnValue<BranchAnnotationDto>> responseBody
            let annotation = returnValue.ReturnValue
            let parentSaveSourceId = $"{parentSaveReferenceId}"

            Assert.That(
                annotation.SourceReferences
                |> Array.exists (fun sourceReference -> sourceReference.ReferenceId = parentSaveReferenceId),
                Is.True
            )

            Assert.That(
                annotation.SourceRows
                |> Array.exists (fun sourceRow -> sourceRow.SourceReferenceId = parentSaveSourceId),
                Is.True
            )
        }
