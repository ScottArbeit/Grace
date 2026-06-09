namespace Grace.Server.Tests

open Azure.Storage.Blobs.Models
open Azure.Storage.Blobs.Specialized
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
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

module BranchServerTestHelpers =
    let private assertOk (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
        }

    let private assertBadRequestGraceError (expectedError: string) (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            let error = deserialize<GraceError> body
            Assert.That(error.Error, Is.EqualTo(expectedError))
            Assert.That(error.CorrelationId, Is.Not.Empty)
        }

    let getBranchParameters (repositoryId: string) (branchId: string) =
        let parameters = Parameters.Branch.GetBranchParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.BranchId <- branchId
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let getBranchAsync (repositoryId: string) (branchId: string) =
        task {
            let! response = Client.PostAsync("/branch/get", createJsonContent (getBranchParameters repositoryId branchId))
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<Branch.BranchDto>> response
            return returnValue.ReturnValue
        }

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

    let saveBranchAsync (repositoryId: string) (branch: Branch.BranchDto) =
        task {
            let parameters = Parameters.Branch.CreateReferenceParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
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

    let private sha256Hex (bytes: byte array) =
        SHA256.HashData(bytes)
        |> fun hash -> byteArrayToString (hash.AsSpan())

    let private gzipBytes (bytes: byte array) =
        use compressed = new MemoryStream()
        use gzipStream = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen = true)
        gzipStream.Write(bytes, 0, bytes.Length)
        gzipStream.Dispose()
        compressed.ToArray()

    let private uploadFileToObjectStorageAsync repositoryId (payload: byte array) (fileVersion: FileVersion) =
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

    let private saveBranchReferenceAsync repositoryId (branch: Branch.BranchDto) (directoryVersion: Grace.Types.Common.DirectoryVersion) =
        task {
            let parameters = Parameters.Branch.CreateReferenceParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.DirectoryVersionId <- directoryVersion.DirectoryVersionId
            parameters.Sha256Hash <- $"{directoryVersion.Sha256Hash}"
            parameters.Message <- "Annotate route test save"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/save", createJsonContent parameters)
            do! assertOk response
        }

    let createAnnotatableReferenceAsync repositoryId (parentBranch: Branch.BranchDto) =
        task {
            let! branch = createBranchAsync repositoryId parentBranch $"Annotate{Guid.NewGuid():N}"
            let relativePath = $"annotate/{Guid.NewGuid():N}/sample.fs"
            let content = $"let value = 42{Environment.NewLine}let other = value + 1{Environment.NewLine}"
            let contentBytes = Encoding.UTF8.GetBytes(content)
            let fileVersion = FileVersion.Create relativePath (sha256Hex contentBytes) String.Empty false (int64 contentBytes.Length)
            let tempRoot = Path.Combine(Path.GetTempPath(), "grace-annotate-tests", Guid.NewGuid().ToString("N"))
            let filePath = Path.Combine(tempRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))

            Directory.CreateDirectory(Path.GetDirectoryName(filePath))
            |> ignore

            do! File.WriteAllTextAsync(filePath, content)

            try
                do! uploadFileToObjectStorageAsync repositoryId contentBytes fileVersion

                let directoryVersion =
                    Grace.Types.Common.DirectoryVersion.Create
                        (Guid.NewGuid())
                        (Guid.Parse ownerId)
                        (Guid.Parse organizationId)
                        (Guid.Parse repositoryId)
                        "/"
                        (sha256Hex (Encoding.UTF8.GetBytes($"directory-{Guid.NewGuid():N}")))
                        (List<DirectoryVersionId>())
                        (List<FileVersion>([ fileVersion ]))
                        (int64 contentBytes.Length)

                do! saveDirectoryVersionAsync repositoryId directoryVersion
                do! saveBranchReferenceAsync repositoryId branch directoryVersion

                let! savedBranch = getBranchAsync repositoryId $"{branch.BranchId}"
                return savedBranch, fileVersion, savedBranch.LatestSave.ReferenceId
            finally
                if Directory.Exists(tempRoot) then Directory.Delete(tempRoot, true)
        }

    let createAnnotatableReferenceWithContentAsync repositoryId (branch: Branch.BranchDto) relativePath (content: string) =
        task {
            let contentBytes = Encoding.UTF8.GetBytes(content)
            let fileVersion = FileVersion.Create relativePath (sha256Hex contentBytes) String.Empty false (int64 contentBytes.Length)

            do! uploadFileToObjectStorageAsync repositoryId contentBytes fileVersion

            let directoryVersion =
                Grace.Types.Common.DirectoryVersion.Create
                    (Guid.NewGuid())
                    (Guid.Parse ownerId)
                    (Guid.Parse organizationId)
                    (Guid.Parse repositoryId)
                    "/"
                    (sha256Hex (Encoding.UTF8.GetBytes($"directory-{Guid.NewGuid():N}")))
                    (List<DirectoryVersionId>())
                    (List<FileVersion>([ fileVersion ]))
                    (int64 contentBytes.Length)

            do! saveDirectoryVersionAsync repositoryId directoryVersion
            do! saveBranchReferenceAsync repositoryId branch directoryVersion

            let! savedBranch = getBranchAsync repositoryId $"{branch.BranchId}"
            return savedBranch, fileVersion, savedBranch.LatestSave.ReferenceId
        }

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
            parameters.DirectoryVersionId <- branch.LatestSave.DirectoryId
            parameters.Sha256Hash <- $"{branch.LatestSave.Sha256Hash}"
            parameters.Message <- "Annotate route test promotion"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/promote", createJsonContent parameters)
            do! assertOk response
            return! getBranchAsync repositoryId $"{branch.BranchId}"
        }

    let rebaseBranchAsync repositoryId (branch: Branch.BranchDto) basedOnReferenceId =
        task {
            let parameters = Parameters.Branch.RebaseParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- $"{branch.BranchId}"
            parameters.BasedOn <- basedOnReferenceId
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/branch/rebase", createJsonContent parameters)
            do! assertOk response
            return! getBranchAsync repositoryId $"{branch.BranchId}"
        }

    let private createPersonalAccessTokenAsync () =
        task {
            let parameters = Parameters.Auth.CreatePersonalAccessTokenParameters()
            parameters.TokenName <- $"branch-sdk-{Guid.NewGuid():N}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = Client.PostAsync("/auth/token/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<PersonalAccessTokenCreated>> response
            return returnValue.ReturnValue.Token
        }

    let configureSdkForServerAsync () =
        task {
            let configuration = Current()
            configuration.ServerUri <- graceServerBaseAddress

            let! token = createPersonalAccessTokenAsync ()

            Grace.SDK.Auth.setTokenProvider (fun () -> task { return Some token })
        }

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

    let assertBranchMatches (expectedRepositoryId: string) (expectedBranchId: string) (expectedBranchName: string) (branch: Branch.BranchDto) =
        Assert.That(branch.RepositoryId, Is.EqualTo(Guid.Parse(expectedRepositoryId)))
        Assert.That(branch.BranchId, Is.EqualTo(Guid.Parse(expectedBranchId)))
        Assert.That($"{branch.BranchName}", Is.EqualTo(expectedBranchName))
        Assert.That(branch.OwnerId, Is.EqualTo(Guid.Parse(ownerId)))
        Assert.That(branch.OrganizationId, Is.EqualTo(Guid.Parse(organizationId)))
        Assert.That(branch.BasedOn.ReferenceId, Is.Not.EqualTo(Guid.Empty))

    let assertBranchReferenceShape (expectedRepositoryId: string) (expectedBranchId: string) (reference: Reference.ReferenceDto) =
        Assert.That(reference.RepositoryId, Is.EqualTo(Guid.Parse(expectedRepositoryId)))
        Assert.That(reference.BranchId, Is.EqualTo(Guid.Parse(expectedBranchId)))
        Assert.That(reference.ReferenceId, Is.Not.EqualTo(Guid.Empty))
        Assert.That(reference.DirectoryId, Is.Not.EqualTo(Guid.Empty))
        Assert.That($"{reference.Sha256Hash}", Is.Not.Empty)

    let assertMissingRepositoryAsync () =
        task {
            let parameters = getBranchParameters $"{Guid.NewGuid()}" repositoryDefaultBranchIds[0]
            let! response = Client.PostAsync("/branch/get", createJsonContent parameters)
            let expected = RepositoryError.getErrorMessage RepositoryError.RepositoryIdDoesNotExist
            do! assertBadRequestGraceError expected response
        }

    let assertMissingBranchAsync (repositoryId: string) =
        task {
            let parameters = getBranchParameters repositoryId $"{Guid.NewGuid()}"
            let! response = Client.PostAsync("/branch/get", createJsonContent parameters)
            let expected = BranchError.getErrorMessage BranchError.BranchIdDoesNotExist
            do! assertBadRequestGraceError expected response
        }

[<Parallelizable(ParallelScope.All)>]
type BranchServer() =

    [<Test>]
    member _.CreateGetListReferenceAndVersionRoutesRoundTripBranchIdentity() =
        task {
            let repositoryId = repositoryIds[0]
            let parentBranchId = repositoryDefaultBranchIds[0]
            let! parentBranch = BranchServerTestHelpers.getBranchAsync repositoryId parentBranchId
            let branchName = $"Branch{Guid.NewGuid():N}"

            let! createdBranch = BranchServerTestHelpers.createBranchAsync repositoryId parentBranch branchName
            BranchServerTestHelpers.assertBranchMatches repositoryId $"{createdBranch.BranchId}" branchName createdBranch
            Assert.That(createdBranch.ParentBranchId, Is.EqualTo(parentBranch.BranchId))
            Assert.That(createdBranch.BasedOn.ReferenceId, Is.EqualTo(parentBranch.BasedOn.ReferenceId))

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

    [<Test>]
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

            do! BranchServerTestHelpers.configureSdkForServerAsync ()

            try
                parameters.CorrelationId <- generateCorrelationId ()
                let! sdkResult = Grace.SDK.Branch.Annotate parameters

                match sdkResult with
                | Ok sdkReturnValue ->
                    Assert.That(sdkReturnValue.ReturnValue.TargetReferenceId, Is.EqualTo(parameters.TargetReferenceId))
                    Assert.That(sdkReturnValue.ReturnValue.Path, Is.EqualTo(fileVersion.RelativePath))
                | Error error -> Assert.Fail($"Expected SDK Branch.Annotate success, got {error.Error}.")
            finally
                Grace.SDK.Auth.clearTokenProvider ()
        }

    [<Test>]
    member _.AnnotateRouteReturnsGraceErrorForBadParameters() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! branch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let fileVersion = FileVersion.Create "annotate/bad-parameters.fs" String.Empty String.Empty false 1L
            let parameters = BranchServerTestHelpers.annotateParameters repositoryId branch fileVersion
            parameters.MaxReferences <- MaximumMaxReferences + 1

            let! response = Client.PostAsync("/branch/annotate", createJsonContent parameters)
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), responseBody)

            let error = deserialize<GraceError> responseBody
            Assert.That(error.Error, Does.Contain("MaxReferences"))
            Assert.That(error.CorrelationId, Is.Not.Empty)
        }

    [<Test>]
    member _.AnnotateRouteReturnsGraceErrorForNullPath() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! branch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let fileVersion = FileVersion.Create "annotate/null-path.fs" String.Empty String.Empty false 1L
            let parameters = BranchServerTestHelpers.annotateParameters repositoryId branch fileVersion
            parameters.Path <- null

            let! response = Client.PostAsync("/branch/annotate", createJsonContent parameters)
            let! responseBody = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), responseBody)

            let error = deserialize<GraceError> responseBody
            Assert.That(error.Error, Is.EqualTo("Annotation Path must be a relative file path."))
            Assert.That(error.CorrelationId, Is.Not.Empty)
        }

    [<Test>]
    member _.AnnotateRouteReturnsGraceErrorForNullReferenceTypes() =
        task {
            let repositoryId = repositoryIds[0]
            let branchId = repositoryDefaultBranchIds[0]
            let! branch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            let fileVersion = FileVersion.Create "annotate/null-reference-types.fs" String.Empty String.Empty false 1L
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
