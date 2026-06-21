namespace Grace.Server.Tests

open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Azure.Storage.Blobs.Specialized
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.ContentBlockMetadata
open Grace.Types.UploadSession
open Grace.Types.Common
open NUnit.Framework
open System
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Net.Http.Headers

module private StoragePlacementTestHelpers =
    let private fragmentParameter (name: string) (uri: Uri) =
        uri
            .Fragment
            .TrimStart('#')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun part ->
            let pieces = part.Split('=', 2)

            if pieces.Length = 2
               && pieces[0]
                   .Equals(name, StringComparison.OrdinalIgnoreCase) then
                Some(Uri.UnescapeDataString pieces[1])
            else
                None)

    let contentBlockPlacementFromUri (blobUriWithSasToken: Uri) eTag =
        let pathSegments =
            blobUriWithSasToken
                .AbsolutePath
                .Trim('/')
                .Split([| '/' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map Uri.UnescapeDataString

        let isPathStyleAzurite =
            blobUriWithSasToken.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(blobUriWithSasToken.Host)
               |> fst

        let accountName =
            if isPathStyleAzurite && pathSegments.Length >= 3 then
                pathSegments[0]
            else if (fragmentParameter "graceStorageAccount" blobUriWithSasToken)
                .IsSome then
                (fragmentParameter "graceStorageAccount" blobUriWithSasToken)
                    .Value
            else
                let host = blobUriWithSasToken.Host
                let firstDot = host.IndexOf('.')

                if firstDot > 0 then host.Substring(0, firstDot) else host

        let containerIndex = if isPathStyleAzurite then 1 else 0

        {
            StorageAccountName = accountName
            StorageContainerName = StorageContainerName pathSegments[containerIndex]
            ObjectKey = String.Join("/", pathSegments |> Array.skip (containerIndex + 1))
            ETag = eTag
        }

[<NonParallelizable>]
type StorageWholeFileCompatibility() =

    let computeSha256Hash (bytes: byte array) =
        let hash = SHA256.HashData(bytes)
        byteArrayToString (hash.AsSpan())

    let tryGetJsonProperty (name: string) (element: JsonElement) =
        element.EnumerateObject()
        |> Seq.tryFind (fun property -> property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun property -> property.Value)

    let requireJsonProperty (name: string) (element: JsonElement) =
        match tryGetJsonProperty name element with
        | Some value -> value
        | None ->
            Assert.Fail($"Expected JSON property '{name}'.")
            Unchecked.defaultof<JsonElement>

    let assertJsonContent (response: HttpResponseMessage) =
        Assert.That(response.Content.Headers.ContentType, Is.Not.Null)
        Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("application/json"))

    let assertRawStringContent (response: HttpResponseMessage) = Assert.That(response.Content.Headers.ContentType, Is.Null)

    let assertWholeFileContentReference (metadata: JsonElement) =
        let contentReference = requireJsonProperty "ContentReference" metadata
        let referenceType = requireJsonProperty "ReferenceType" contentReference

        match referenceType.ValueKind with
        | JsonValueKind.Number -> Assert.That(referenceType.GetInt32(), Is.EqualTo(int FileContentReferenceType.WholeFileContent))
        | JsonValueKind.String -> Assert.That(referenceType.GetString(), Is.EqualTo("WholeFileContent").IgnoreCase)
        | _ -> Assert.Fail($"Unexpected ContentReference.ReferenceType JSON kind: {referenceType.ValueKind}.")

        match tryGetJsonProperty "Manifest" contentReference with
        | Some manifest -> Assert.That(manifest.ValueKind, Is.EqualTo(JsonValueKind.Null))
        | None -> ()

    let getUploadMetadataJson (content: string) =
        use document = JsonDocument.Parse(content)
        let root = document.RootElement.Clone()
        let returnValue = requireJsonProperty "ReturnValue" root
        returnValue.EnumerateArray() |> Seq.exactlyOne

    let createUploadParameters repositoryId fileVersion =
        let parameters = Parameters.Storage.GetUploadMetadataForFilesParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.FileVersions <- [| fileVersion |]
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let createDownloadParameters repositoryId fileVersion =
        let parameters = Parameters.Storage.GetDownloadUriParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.FileVersion <- fileVersion
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    [<Test>]
    member _.SmallWholeFileContentRejectsMissingBlake3HashBeforePublishingUploadMetadata() =
        task {
            let repositoryId = repositoryIds[0]
            let relativeDirectory = $"wholefile-dual-hash-required/{Guid.NewGuid():N}"
            let relativePath = $"{relativeDirectory}/small.bin"
            let payload = Encoding.UTF8.GetBytes($"Grace whole-file missing BLAKE3 {Guid.NewGuid():N}")
            let sha256Hash = computeSha256Hash payload
            let fileVersion = FileVersion.Create relativePath sha256Hash String.Empty true (int64 payload.Length)

            let! uploadResponse = Client.PostAsync("/storage/getUploadMetadataForFiles", createJsonContent (createUploadParameters repositoryId fileVersion))
            let! uploadContent = uploadResponse.Content.ReadAsStringAsync()

            Assert.That(uploadResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), uploadContent)
            let error = deserialize<GraceError> uploadContent
            Assert.That(error.Error, Is.EqualTo("The Blake3Hash value is required."))
        }

    [<Test>]
    member _.SmallWholeFileContentPopulatesContentReferenceWithFullBlake3Metadata() =
        task {
            let repositoryId = repositoryIds[0]
            let relativeDirectory = $"wholefile-compatibility/{Guid.NewGuid():N}"
            let relativePath = $"{relativeDirectory}/small.bin"
            let payload = Encoding.UTF8.GetBytes($"Grace whole-file compatibility {Guid.NewGuid():N}")
            let sha256Hash = computeSha256Hash payload
            let blake3Hash = Blake3Hash "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adcd1e8c76d9a8885f16a39f"

            let fileVersion =
                FileVersion.CreateWithHashes (RelativePath relativePath) (Sha256Hash sha256Hash) blake3Hash String.Empty true (int64 payload.Length)

            let! uploadResponse = Client.PostAsync("/storage/getUploadMetadataForFiles", createJsonContent (createUploadParameters repositoryId fileVersion))
            let! uploadContent = uploadResponse.Content.ReadAsStringAsync()
            Assert.That(uploadResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), uploadContent)

            let metadata = getUploadMetadataJson uploadContent

            Assert.That(
                (requireJsonProperty "RelativePath" metadata)
                    .GetString(),
                Is.EqualTo(relativePath)
            )

            Assert.That(
                (requireJsonProperty "Sha256Hash" metadata)
                    .GetString(),
                Is.EqualTo(sha256Hash)
            )

            Assert.That(
                (requireJsonProperty "Blake3Hash" metadata)
                    .GetString(),
                Is.EqualTo($"{blake3Hash}")
            )

            assertWholeFileContentReference metadata

            let blobUri =
                Uri(
                    (requireJsonProperty "BlobUriWithSasToken" metadata)
                        .GetString()
                )

            Assert.That(blobUri.IsAbsoluteUri, Is.True)

            let! downloadResponse = Client.PostAsync("/storage/getDownloadUri", createJsonContent (createDownloadParameters repositoryId fileVersion))
            let! downloadUri = downloadResponse.Content.ReadAsStringAsync()
            Assert.That(downloadResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), downloadUri)
            Assert.That(downloadUri, Does.Contain(StorageKeys.wholeFileContentObjectKey fileVersion))
        }

    [<Test>]
    member _.SmallWholeFileMetadataUsesBlake3SpecificBlobKeysWhenPresent() =
        task {
            let repositoryId = repositoryIds[0]
            let relativeDirectory = $"wholefile-dual-hash/{Guid.NewGuid():N}"
            let relativePath = $"{relativeDirectory}/small.bin"
            let sharedSha256Hash = "805331a98813206270e35564769e8bb59eea02aeb7b27c7d6c63e625e1857243"

            let firstFileVersion =
                FileVersion.CreateWithHashes
                    (RelativePath relativePath)
                    (Sha256Hash sharedSha256Hash)
                    (Blake3Hash "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adcd1e8c76d9a8885f16a39f")
                    String.Empty
                    true
                    128L

            let secondFileVersion =
                FileVersion.CreateWithHashes
                    (RelativePath relativePath)
                    (Sha256Hash sharedSha256Hash)
                    (Blake3Hash "9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d")
                    String.Empty
                    true
                    128L

            let! firstUploadResponse =
                Client.PostAsync("/storage/getUploadMetadataForFiles", createJsonContent (createUploadParameters repositoryId firstFileVersion))

            let! firstUploadContent = firstUploadResponse.Content.ReadAsStringAsync()
            Assert.That(firstUploadResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), firstUploadContent)

            let! secondUploadResponse =
                Client.PostAsync("/storage/getUploadMetadataForFiles", createJsonContent (createUploadParameters repositoryId secondFileVersion))

            let! secondUploadContent = secondUploadResponse.Content.ReadAsStringAsync()
            Assert.That(secondUploadResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), secondUploadContent)

            let firstMetadata = getUploadMetadataJson firstUploadContent
            let secondMetadata = getUploadMetadataJson secondUploadContent

            let firstUploadUri =
                (requireJsonProperty "BlobUriWithSasToken" firstMetadata)
                    .GetString()

            let secondUploadUri =
                (requireJsonProperty "BlobUriWithSasToken" secondMetadata)
                    .GetString()

            Assert.That(firstUploadUri, Does.Contain($"small_{sharedSha256Hash}_{firstFileVersion.Blake3Hash}.bin"))
            Assert.That(secondUploadUri, Does.Contain($"small_{sharedSha256Hash}_{secondFileVersion.Blake3Hash}.bin"))
            Assert.That(firstUploadUri, Is.Not.EqualTo(secondUploadUri))

            Assert.That(
                (requireJsonProperty "Blake3Hash" firstMetadata)
                    .GetString(),
                Is.EqualTo(firstFileVersion.Blake3Hash)
            )

            Assert.That(
                (requireJsonProperty "Blake3Hash" secondMetadata)
                    .GetString(),
                Is.EqualTo(secondFileVersion.Blake3Hash)
            )

            let! firstDownloadResponse = Client.PostAsync("/storage/getDownloadUri", createJsonContent (createDownloadParameters repositoryId firstFileVersion))
            let! firstDownloadUri = firstDownloadResponse.Content.ReadAsStringAsync()
            Assert.That(firstDownloadResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), firstDownloadUri)
            Assert.That(firstDownloadUri, Does.Contain($"small_{sharedSha256Hash}_{firstFileVersion.Blake3Hash}.bin"))

            let! secondDownloadResponse =
                Client.PostAsync("/storage/getDownloadUri", createJsonContent (createDownloadParameters repositoryId secondFileVersion))

            let! secondDownloadUri = secondDownloadResponse.Content.ReadAsStringAsync()
            Assert.That(secondDownloadResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), secondDownloadUri)
            Assert.That(secondDownloadUri, Does.Contain($"small_{sharedSha256Hash}_{secondFileVersion.Blake3Hash}.bin"))
            Assert.That(firstDownloadUri, Is.Not.EqualTo(secondDownloadUri))
        }

    [<Test>]
    member _.SmallWholeFileDownloadUsesCurrentBlake3KeyWhenBlobHasNotBeenUploadedYet() =
        task {
            let repositoryId = repositoryIds[0]
            let relativeDirectory = $"wholefile-current-key/{Guid.NewGuid():N}"
            let relativePath = $"{relativeDirectory}/small.txt"
            let payload = Encoding.UTF8.GetBytes($"Grace current whole-file key {Guid.NewGuid():N}")
            let sha256Hash = computeSha256Hash payload

            let fileVersion =
                FileVersion.CreateWithHashes
                    (RelativePath relativePath)
                    (Sha256Hash sha256Hash)
                    (Blake3Hash "9a35d91b2f631be9025de753139b88f7b1e71385c412bc3986ff2f38f230841d")
                    String.Empty
                    false
                    (int64 payload.Length)

            let currentBlobName = StorageKeys.wholeFileContentObjectKey fileVersion
            let shaOnlyBlobName = StorageKeys.legacyWholeFileContentObjectKey fileVersion
            Assert.That(currentBlobName, Is.Not.EqualTo(shaOnlyBlobName))

            let! downloadResponse = Client.PostAsync("/storage/getDownloadUri", createJsonContent (createDownloadParameters repositoryId fileVersion))
            let! downloadUriText = downloadResponse.Content.ReadAsStringAsync()
            Assert.That(downloadResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), downloadUriText)
            Assert.That(downloadUriText, Does.Contain(currentBlobName))
            Assert.That(downloadUriText, Does.Not.Contain(shaOnlyBlobName))
        }

[<NonParallelizable>]
type StorageContentBlockSasRoutes() =

    let createClientWithUserId (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    let createUnauthenticatedClient () =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client

    let createMalformedJsonContent () = new StringContent("{", Encoding.UTF8, "application/json")

    let grantRoleAsync (client: HttpClient) scopeKind ownerId organizationId repositoryId branchId principalId roleId =
        task {
            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- branchId
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- principalId
            parameters.ScopeKind <- scopeKind
            parameters.RoleId <- roleId
            parameters.Source <- "test"
            parameters.CorrelationId <- generateCorrelationId ()

            return! client.PostAsync("/authorize/grant-role", createJsonContent parameters)
        }

    let setContentBlockParameters (parameters: Parameters.Storage.StorageParameters) repositoryId =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CorrelationId <- generateCorrelationId ()

    let createContentBlockUploadParameters repositoryId =
        let parameters = Parameters.Storage.GetContentBlockUploadUriParameters()
        setContentBlockParameters parameters repositoryId
        parameters.ContentBlockAddress <- ContentAddress.computeBlake3Hex (Guid.NewGuid().ToByteArray())
        parameters

    let createContentBlockDownloadParameters repositoryId =
        let parameters = Parameters.Storage.GetContentBlockDownloadUriParameters()
        setContentBlockParameters parameters repositoryId
        parameters.ContentBlockAddress <- ContentAddress.computeBlake3Hex (Guid.NewGuid().ToByteArray())
        parameters

    let malformedContentBlockAddress () = ContentBlockAddress $"content-block-contract-{Guid.NewGuid():N}"

    let assertUnauthorized (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), body)
        }

    let assertBadRequestForMalformedContentBlockAddress (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Contain("ContentBlockAddress"))
            Assert.That(body, Does.Contain("64-character hexadecimal BLAKE3"))
        }

    let assertSuccessSasForContentBlock (response: HttpResponseMessage) (contentBlockAddress: ContentBlockAddress) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            Assert.That(body, Does.Contain("cas/content/"))
            Assert.That(body, Does.Contain(contentBlockAddress))
        }

    let assertBadRequestContains (expected: string) (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Contain(expected))
        }

    [<Test>]
    member _.ContentBlockUploadUriRequiresPathWriteAndFailsClosedWithoutUploadSessionIntent() =
        task {
            let repositoryId = repositoryIds[0]
            let pathWriter = $"{Guid.NewGuid()}"

            let! grantWriter = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" pathWriter "RepositoryContributor"
            Assert.That(grantWriter.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use unauthClient = createUnauthenticatedClient ()
            use writerClient = createClientWithUserId pathWriter
            use unprivilegedClient = createClientWithUserId $"{Guid.NewGuid()}"

            let parameters = createContentBlockUploadParameters repositoryId

            let! unauthUpload = unauthClient.PostAsync("/storage/getContentBlockUploadUri", createJsonContent parameters)
            Assert.That(unauthUpload.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedUpload = unprivilegedClient.PostAsync("/storage/getContentBlockUploadUri", createJsonContent parameters)
            Assert.That(deniedUpload.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedUpload = writerClient.PostAsync("/storage/getContentBlockUploadUri", createJsonContent parameters)
            do! assertBadRequestContains "UploadSessionId is required" allowedUpload
        }

    [<Test>]
    member _.ContentBlockUploadUriChecksPrincipalBeforeBodyValidationAndReturnsBadRequestAfterAuthentication() =
        task {
            let repositoryId = repositoryIds[0]
            let pathWriter = $"{Guid.NewGuid()}"

            let! grantWriter = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" pathWriter "RepositoryContributor"
            Assert.That(grantWriter.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use unauthClient = createUnauthenticatedClient ()
            use writerClient = createClientWithUserId pathWriter
            let parameters = createContentBlockUploadParameters repositoryId

            parameters.ContentBlockAddress <- malformedContentBlockAddress ()

            let! unauthInvalidUpload = unauthClient.PostAsync("/storage/getContentBlockUploadUri", createJsonContent parameters)
            do! assertUnauthorized unauthInvalidUpload

            use malformedJson = createMalformedJsonContent ()
            let! unauthMalformedUpload = unauthClient.PostAsync("/storage/getContentBlockUploadUri", malformedJson)
            do! assertUnauthorized unauthMalformedUpload

            let! malformedUpload = writerClient.PostAsync("/storage/getContentBlockUploadUri", createJsonContent parameters)
            do! assertBadRequestForMalformedContentBlockAddress malformedUpload

            parameters.ContentBlockAddress <- ContentBlockAddress String.Empty

            let! emptyUpload = writerClient.PostAsync("/storage/getContentBlockUploadUri", createJsonContent parameters)
            do! assertBadRequestForMalformedContentBlockAddress emptyUpload
        }

    [<Test>]
    member _.ConfirmContentBlockUploadValidatesAddressBeforePlacementKeyDerivation() =
        task {
            let repositoryId = repositoryIds[0]
            let pathWriter = $"{Guid.NewGuid()}"

            let! grantWriter = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" pathWriter "RepositoryContributor"
            Assert.That(grantWriter.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use writerClient = createClientWithUserId pathWriter
            let parameters = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setContentBlockParameters parameters repositoryId
            parameters.UploadSessionId <- Guid.NewGuid()
            parameters.AuthorizedScope <- "/"
            parameters.OperationId <- "confirm-malformed-address"
            parameters.ContentBlockAddress <- malformedContentBlockAddress ()
            parameters.Payload <- Array.empty

            parameters.StoragePlacement <-
                {
                    StorageAccountName = "wrong-account"
                    StorageContainerName = StorageContainerName "wrong-container"
                    ObjectKey = "wrong/object/key"
                    ETag = None
                }

            let! response = writerClient.PostAsync("/storage/confirmContentBlockUpload", createJsonContent parameters)
            do! assertBadRequestForMalformedContentBlockAddress response
        }

    [<Test>]
    member _.ContentBlockDownloadUriRequiresPathReadAndFailsClosedWithoutFinalizedManifestReference() =
        task {
            let repositoryId = repositoryIds[0]
            let pathReader = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" pathReader "RepositoryReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use unauthClient = createUnauthenticatedClient ()
            use readerClient = createClientWithUserId pathReader
            use unprivilegedClient = createClientWithUserId $"{Guid.NewGuid()}"

            let parameters = createContentBlockDownloadParameters repositoryId

            let! unauthDownload = unauthClient.PostAsync("/storage/getContentBlockDownloadUri", createJsonContent parameters)
            Assert.That(unauthDownload.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedDownload = unprivilegedClient.PostAsync("/storage/getContentBlockDownloadUri", createJsonContent parameters)
            Assert.That(deniedDownload.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedDownload = readerClient.PostAsync("/storage/getContentBlockDownloadUri", createJsonContent parameters)
            do! assertBadRequestContains "ManifestAddress" allowedDownload
        }

    [<Test>]
    member _.ContentBlockDownloadUriChecksPrincipalBeforeBodyValidationAndReturnsBadRequestAfterAuthentication() =
        task {
            let repositoryId = repositoryIds[0]
            let pathReader = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" pathReader "RepositoryReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use unauthClient = createUnauthenticatedClient ()
            use readerClient = createClientWithUserId pathReader
            let parameters = createContentBlockDownloadParameters repositoryId

            parameters.ContentBlockAddress <- malformedContentBlockAddress ()

            let! unauthInvalidDownload = unauthClient.PostAsync("/storage/getContentBlockDownloadUri", createJsonContent parameters)
            do! assertUnauthorized unauthInvalidDownload

            use malformedJson = createMalformedJsonContent ()
            let! unauthMalformedDownload = unauthClient.PostAsync("/storage/getContentBlockDownloadUri", malformedJson)
            do! assertUnauthorized unauthMalformedDownload

            let! malformedDownload = readerClient.PostAsync("/storage/getContentBlockDownloadUri", createJsonContent parameters)
            do! assertBadRequestForMalformedContentBlockAddress malformedDownload

            parameters.ContentBlockAddress <- ContentBlockAddress String.Empty

            let! emptyDownload = readerClient.PostAsync("/storage/getContentBlockDownloadUri", createJsonContent parameters)
            do! assertBadRequestForMalformedContentBlockAddress emptyDownload
        }

    [<Test>]
    member _.ContentBlockDownloadUriReturnsBadRequestForMalformedManifestAddress() =
        task {
            let repositoryId = repositoryIds[0]
            let pathReader = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" pathReader "RepositoryReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use readerClient = createClientWithUserId pathReader
            let parameters = createContentBlockDownloadParameters repositoryId
            parameters.AuthorizedScope <- "/malformed/manifest.bin"
            parameters.ManifestAddress <- ManifestAddress "manifest-malformed-non-empty"

            let! response = readerClient.PostAsync("/storage/getContentBlockDownloadUri", createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Contain("ManifestAddress"))
            Assert.That(body, Does.Not.Contain("InternalServerError"))
            Assert.That(body, Does.Not.Contain("Error in /storage/getContentBlockDownloadUri"))
        }

[<NonParallelizable>]
type StorageContentBlockDiscoveryRoutes() =

    let maxDiscoveryKeyChunkAddresses = 256

    let createClientWithUserId (userId: string) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    let createUnauthenticatedClient () =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client

    let grantRoleAsync (client: HttpClient) scopeKind ownerId organizationId repositoryId branchId principalId roleId =
        task {
            let parameters = Parameters.Access.GrantRoleParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- branchId
            parameters.PrincipalType <- "User"
            parameters.PrincipalId <- principalId
            parameters.ScopeKind <- scopeKind
            parameters.RoleId <- roleId
            parameters.Source <- "test"
            parameters.CorrelationId <- generateCorrelationId ()

            return! client.PostAsync("/authorize/grant-role", createJsonContent parameters)
        }

    let createDiscoveryJson repositoryId (keyChunkAddresses: string array) =
        let encodedAddresses =
            keyChunkAddresses
            |> Array.map JsonSerializer.Serialize
            |> String.concat ","

        String.Concat(
            "{",
            "\"OwnerId\":",
            JsonSerializer.Serialize ownerId,
            ",\"OrganizationId\":",
            JsonSerializer.Serialize organizationId,
            ",\"RepositoryId\":",
            JsonSerializer.Serialize repositoryId,
            ",\"CorrelationId\":",
            JsonSerializer.Serialize(generateCorrelationId ()),
            ",\"KeyChunkAddresses\":[",
            encodedAddresses,
            "]}"
        )

    let createJsonContentFromString (json: string) =
        let content = new StringContent(json, Encoding.UTF8)
        content.Headers.ContentType <- MediaTypeHeaderValue("application/json")
        content

    let tryGetJsonProperty (name: string) (element: JsonElement) =
        element.EnumerateObject()
        |> Seq.tryFind (fun property -> property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun property -> property.Value)

    let requireJsonProperty (name: string) (element: JsonElement) =
        match tryGetJsonProperty name element with
        | Some value -> value
        | None ->
            Assert.Fail($"Expected JSON property '{name}'.")
            Unchecked.defaultof<JsonElement>

    let postDiscoveryAsync (client: HttpClient) repositoryId keyChunkAddresses =
        client.PostAsync("/storage/discoverContentBlocks", createJsonContentFromString (createDiscoveryJson repositoryId keyChunkAddresses))

    [<Test>]
    member _.DiscoveryRequiresRepositoryReadAndReturnsSafeEmptyNonAuthoritativeResults() =
        task {
            let repositoryId = repositoryIds[0]
            let repoReader = $"{Guid.NewGuid()}"

            let requestedChunkAddresses =
                [|
                    $"chunk-{Guid.NewGuid():N}"
                    $"chunk-{Guid.NewGuid():N}"
                |]

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" repoReader "RepositoryReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use unauthClient = createUnauthenticatedClient ()
            use readerClient = createClientWithUserId repoReader
            use unprivilegedClient = createClientWithUserId $"{Guid.NewGuid()}"

            let! unauthDiscovery = postDiscoveryAsync unauthClient repositoryId requestedChunkAddresses
            Assert.That(unauthDiscovery.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            let! deniedDiscovery = postDiscoveryAsync unprivilegedClient repositoryId requestedChunkAddresses
            Assert.That(deniedDiscovery.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let! allowedDiscovery = postDiscoveryAsync readerClient repositoryId requestedChunkAddresses
            let! body = allowedDiscovery.Content.ReadAsStringAsync()
            Assert.That(allowedDiscovery.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)

            for requestedChunkAddress in requestedChunkAddresses do
                Assert.That(body, Does.Not.Contain(requestedChunkAddress), "Discovery must not echo per-chunk existence probes.")

            use document = JsonDocument.Parse(body)
            let returnValue = requireJsonProperty "ReturnValue" document.RootElement
            let policy = requireJsonProperty "Policy" returnValue
            let candidates = requireJsonProperty "CandidateContentBlocks" returnValue

            Assert.That(
                (requireJsonProperty "MaxKeyChunkAddresses" policy)
                    .GetInt32(),
                Is.EqualTo(maxDiscoveryKeyChunkAddresses)
            )

            Assert.That(
                (requireJsonProperty "PositiveCandidatesEnabled" policy)
                    .GetBoolean(),
                Is.True
            )

            Assert.That(
                (requireJsonProperty "EmptyResponseMeansAbsent" policy)
                    .GetBoolean(),
                Is.False
            )

            Assert.That(
                (requireJsonProperty "IsAuthoritative" policy)
                    .GetBoolean(),
                Is.False
            )

            Assert.That(
                (requireJsonProperty "RequestedKeyChunkCount" returnValue)
                    .GetInt32(),
                Is.EqualTo(requestedChunkAddresses.Length)
            )

            Assert.That(
                (requireJsonProperty "AcceptedKeyChunkCount" returnValue)
                    .GetInt32(),
                Is.EqualTo(requestedChunkAddresses.Length)
            )

            Assert.That(
                (requireJsonProperty "IsPartial" returnValue)
                    .GetBoolean(),
                Is.True
            )

            Assert.That(candidates.GetArrayLength(), Is.EqualTo(0))
        }

    [<Test>]
    member _.DiscoveryRejectsRequestsAboveMaxKeyChunkLimit() =
        task {
            let repositoryId = repositoryIds[0]
            let repoReader = $"{Guid.NewGuid()}"
            let requestedChunkAddresses = Array.init (maxDiscoveryKeyChunkAddresses + 1) (fun index -> $"chunk-{index}-{Guid.NewGuid():N}")

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" repoReader "RepositoryReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use readerClient = createClientWithUserId repoReader
            let! response = postDiscoveryAsync readerClient repositoryId requestedChunkAddresses
            let! body = response.Content.ReadAsStringAsync()

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Contain("KeyChunkAddresses"))
            Assert.That(body, Does.Contain($"{maxDiscoveryKeyChunkAddresses}"))
        }

[<NonParallelizable>]
type StorageManifestUploadSessionRoutes() =

    let maxDiscoveryKeyChunkAddresses = 256

    let pseudoRandomBytes length =
        let bytes = Array.zeroCreate<byte> length
        let mutable state = 0x78123456u

        for index in 0 .. length - 1 do
            state <- state ^^^ (state <<< 13)
            state <- state ^^^ (state >>> 17)
            state <- state ^^^ (state <<< 5)
            bytes[index] <- byte (state &&& 0xffu)

        bytes

    let encodeBlockAt physicalOffset bytes =
        match ContentBlockFormat.encode [ { PhysicalOffset = physicalOffset; Bytes = bytes } ] with
        | Ok block -> block
        | Error error ->
            Assert.Fail($"Expected test ContentBlock to encode, got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    let encodeBlock bytes = encodeBlockAt 0L bytes

    let manifestForStoragePool storagePoolId (bytes: byte array) (block: ContentBlockFormat.EncodedContentBlock) =
        let contentBlock = ContentBlock.Create(block.Address, 0L, int64 bytes.Length)

        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                ChunkingSuiteId RabinChunking.SuiteName,
                FileContentHash(ContentAddress.computeBlake3Hex bytes),
                int64 bytes.Length,
                storagePoolId,
                [ contentBlock ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let manifestFor bytes block = manifestForStoragePool (StoragePoolId Constants.DefaultStoragePoolId) bytes block

    let setStorageParameters (parameters: Parameters.Storage.StorageParameters) repositoryId correlationId =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CorrelationId <- correlationId

    let createClientWithClaims (claims: string list) =
        let client = new HttpClient()
        client.BaseAddress <- Client.BaseAddress
        client.DefaultRequestHeaders.Add("x-grace-user-id", testUserId)

        if not (List.isEmpty claims) then
            client.DefaultRequestHeaders.Add("x-grace-claims", String.Join(";", claims))

        client

    let upsertPathPermissionAsync (client: HttpClient) repositoryId path claimPermissions =
        task {
            let parameters = Parameters.Access.UpsertPathPermissionParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.Path <- path
            parameters.CorrelationId <- generateCorrelationId ()

            for (claim, permission) in claimPermissions do
                let claimPermission = Parameters.Access.ClaimPermissionParameters()
                claimPermission.Claim <- claim
                claimPermission.DirectoryPermission <- permission
                parameters.ClaimPermissions.Add(claimPermission)

            let! response = client.PostAsync("/authorize/upsert-path-permission", createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
        }

    let tryGetJsonProperty (name: string) (element: JsonElement) =
        element.EnumerateObject()
        |> Seq.tryFind (fun property -> property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun property -> property.Value)

    let requireJsonProperty (name: string) (element: JsonElement) =
        match tryGetJsonProperty name element with
        | Some value -> value
        | None ->
            Assert.Fail($"Expected JSON property '{name}'.")
            Unchecked.defaultof<JsonElement>

    let assertJsonContent (response: HttpResponseMessage) =
        Assert.That(response.Content.Headers.ContentType, Is.Not.Null)
        Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("application/json"))

    let assertRawStringContent (response: HttpResponseMessage) = Assert.That(response.Content.Headers.ContentType, Is.Null)

    let createDiscoveryJson repositoryId (keyChunkAddresses: string array) =
        let encodedAddresses =
            keyChunkAddresses
            |> Array.map JsonSerializer.Serialize
            |> String.concat ","

        String.Concat(
            "{",
            "\"OwnerId\":",
            JsonSerializer.Serialize ownerId,
            ",\"OrganizationId\":",
            JsonSerializer.Serialize organizationId,
            ",\"RepositoryId\":",
            JsonSerializer.Serialize repositoryId,
            ",\"CorrelationId\":",
            JsonSerializer.Serialize(generateCorrelationId ()),
            ",\"KeyChunkAddresses\":[",
            encodedAddresses,
            "]}"
        )

    let createJsonContentFromString (json: string) =
        let content = new StringContent(json, Encoding.UTF8)
        content.Headers.ContentType <- MediaTypeHeaderValue("application/json")
        content

    let postDiscoveryAsync (client: HttpClient) repositoryId keyChunkAddresses =
        client.PostAsync("/storage/discoverContentBlocks", createJsonContentFromString (createDiscoveryJson repositoryId keyChunkAddresses))

    let queryParameter (name: string) (uri: Uri) =
        uri
            .Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun part ->
            let pieces = part.Split('=', 2)

            if pieces.Length = 2
               && pieces[0]
                   .Equals(name, StringComparison.OrdinalIgnoreCase) then
                Some(Uri.UnescapeDataString pieces[1])
            else
                None)
        |> Option.defaultValue String.Empty

    let sanitizedSasShape (uri: Uri) =
        let permissions = queryParameter "sp" uri
        let resource = queryParameter "sr" uri
        let serviceVersion = queryParameter "sv" uri
        $"host={uri.Host}; path={uri.AbsolutePath}; sp={permissions}; sr={resource}; sv={serviceVersion}"

    let putContentBlockWithSas (payload: byte array) (uploadUri: Uri) =
        task {
            let blockBlobClient = BlockBlobClient(uploadUri)
            use payloadStream = new MemoryStream(payload, writable = false)
            let options = BlobUploadOptions()
            options.Conditions <- BlobRequestConditions(IfNoneMatch = Azure.ETag.All)
            let! response = blockBlobClient.UploadAsync(payloadStream, options)
            return response.Value.ETag.ToString()
        }

    let uploadContentBlockWithSas (payload: byte array) (uploadUri: Uri) =
        task {
            try
                return! putContentBlockWithSas payload uploadUri
            with
            | :? Azure.RequestFailedException as ex ->
                Assert.Fail($"Expected SAS upload to succeed for {sanitizedSasShape uploadUri}, got {ex.Status} {ex.ErrorCode}: {ex.Message}")
                return Unchecked.defaultof<string>
        }

    let downloadContentBlockWithSas (downloadUri: Uri) =
        task {
            let blobClient = BlobClient(downloadUri)
            let! response = blobClient.DownloadContentAsync()
            return response.Value.Content.ToArray()
        }

    let finalContentBlockClientFromUploadUri (uploadUri: Uri) (contentBlockAddress: ContentBlockAddress) =
        let pathSegments =
            uploadUri
                .AbsolutePath
                .Trim('/')
                .Split([| '/' |], StringSplitOptions.RemoveEmptyEntries)

        let isPathStyleAzurite =
            uploadUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uploadUri.Host) |> fst

        let accountName =
            if isPathStyleAzurite && pathSegments.Length > 0 then
                pathSegments[0]
            else
                uploadUri.Host.Substring(0, uploadUri.Host.IndexOf('.'))

        let blobEndpoint =
            if isPathStyleAzurite then
                $"{uploadUri.Scheme}://{uploadUri.Authority}/{accountName}"
            else
                $"{uploadUri.Scheme}://{uploadUri.Authority}"

        let connectionString =
            $"DefaultEndpointsProtocol={uploadUri.Scheme};AccountName={accountName};AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint={blobEndpoint};"

        let containerName = if isPathStyleAzurite then pathSegments[1] else pathSegments[0]

        let objectKey =
            let prefix = Constants.DefaultCasStoragePrefix.Trim().Trim('/')
            let contentObjectKey = StorageKeys.contentBlockObjectKey contentBlockAddress

            if String.IsNullOrWhiteSpace prefix then
                contentObjectKey
            else
                $"{prefix}/{contentObjectKey}"

        let containerClient = BlobContainerClient(connectionString, containerName)
        containerClient.GetBlockBlobClient(objectKey)

    let storageConnectionStringFromUploadUri (uploadUri: Uri) =
        let pathSegments =
            uploadUri
                .AbsolutePath
                .Trim('/')
                .Split([| '/' |], StringSplitOptions.RemoveEmptyEntries)

        let isPathStyleAzurite =
            uploadUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uploadUri.Host) |> fst

        let accountName =
            if isPathStyleAzurite && pathSegments.Length > 0 then
                pathSegments[0]
            else
                uploadUri.Host.Substring(0, uploadUri.Host.IndexOf('.'))

        let blobEndpoint =
            if isPathStyleAzurite then
                $"{uploadUri.Scheme}://{uploadUri.Authority}/{accountName}"
            else
                $"{uploadUri.Scheme}://{uploadUri.Authority}"

        let connectionString =
            $"DefaultEndpointsProtocol={uploadUri.Scheme};AccountName={accountName};AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint={blobEndpoint};"

        connectionString

    let contentBlockClientFromPlacementViaUploadUri (uploadUri: Uri) (placement: ContentBlockStoragePlacement) =
        let connectionString = storageConnectionStringFromUploadUri uploadUri
        let containerClient = BlobContainerClient(connectionString, placement.StorageContainerName)
        containerClient.GetBlockBlobClient(placement.ObjectKey)

    let uploadFinalContentBlockByAddress (uploadUri: Uri) (contentBlockAddress: ContentBlockAddress) (payload: byte array) =
        task {
            let blockBlobClient = finalContentBlockClientFromUploadUri uploadUri contentBlockAddress
            let! _ = blockBlobClient.DeleteIfExistsAsync()
            use payloadStream = new MemoryStream(payload, writable = false)
            let! _ = blockBlobClient.UploadAsync(payloadStream)
            return ()
        }

    let finalContentBlockExistsByAddress (uploadUri: Uri) (contentBlockAddress: ContentBlockAddress) =
        task {
            let blockBlobClient = finalContentBlockClientFromUploadUri uploadUri contentBlockAddress
            let! exists = blockBlobClient.ExistsAsync()
            return exists.Value
        }

    let contentBlockExistsAtPlacement (uploadUri: Uri) (placement: ContentBlockStoragePlacement) =
        task {
            let blockBlobClient = contentBlockClientFromPlacementViaUploadUri uploadUri placement
            let! exists = blockBlobClient.ExistsAsync()
            return exists.Value
        }

    let reuseHint index =
        {
            StoragePoolId = StoragePoolId $"storage-pool-{Guid.NewGuid():N}"
            ContentBlockAddress = ContentBlockAddress $"content-block-{index}-{Guid.NewGuid():N}"
            OrdinalStart = 0
            OrdinalCount = Parameters.Storage.MinimumAcceptedReuseRunLength
            MetadataVersion = 1L
        }

    let postUploadSessionDecision (route: string) parameters =
        task {
            let! response = Client.PostAsync(route, createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            return deserialize<GraceReturnValue<UploadSessionDecision>> body
        }

    let postUploadSessionBadRequest (route: string) parameters =
        task {
            let! response = Client.PostAsync(route, createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            return body
        }

    [<Test>]
    member _.LargeManifestUploadCanUploadBlobConfirmFinalizeAndDownloadContentBlock() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 0uy
            Guid.NewGuid().ToByteArray().CopyTo(payload, 1)
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-no-global-dedupe-v1"
            start.OperationId <- "start"

            let! startResult = postUploadSessionDecision "/storage/startManifestUploadSession" start
            Assert.That(startResult.ReturnValue.Session.UploadSessionId, Is.EqualTo(sessionId))
            let manifest = manifestForStoragePool startResult.ReturnValue.Session.StoragePoolId payload block

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters register repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- "/"
            register.OperationId <- "register-0"
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length

            let! _ = postUploadSessionDecision "/storage/registerContentBlockUpload" register

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.UploadSessionId <- sessionId
            uploadUriParameters.ContentBlockAddress <- block.Address
            uploadUriParameters.AuthorizedScope <- "/"

            let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = uploadUriResponse.Content.ReadAsStringAsync()
            Assert.That(uploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), uploadUriBody)
            assertRawStringContent uploadUriResponse

            let uploadUri = Uri uploadUriBody
            let sasPermissions = queryParameter "sp" uploadUri
            Assert.That(sasPermissions, Does.Contain("c"))
            Assert.That(sasPermissions, Does.Contain("w"))

            Assert.That(
                uploadUri.AbsolutePath,
                Does.Contain($"/staging/repositories/{repositoryId}/upload-sessions/{sessionId:N}/content-blocks/{block.Address}")
            )

            Assert.That(uploadUri.AbsolutePath, Does.Not.Contain($"/cas/content/{block.Address}"))
            Assert.That(uploadUri.Fragment, Does.Contain("graceStorageAccount="))

            let! uploadETag = uploadContentBlockWithSas block.Payload uploadUri

            let storagePlacement = StoragePlacementTestHelpers.contentBlockPlacementFromUri uploadUri (Some uploadETag)

            Assert.That(storagePlacement.ETag, Is.Not.EqualTo(None))

            try
                let! _ = putContentBlockWithSas block.Payload uploadUri
                Assert.Fail("Expected conditional CAS retry to fail instead of overwriting existing content.")
            with
            | :? Azure.RequestFailedException as ex -> Assert.That(ex.Status, Is.EqualTo(int HttpStatusCode.Conflict))

            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirm repositoryId correlationId
            confirm.UploadSessionId <- sessionId
            confirm.AuthorizedScope <- "/"
            confirm.OperationId <- "confirm-0"
            confirm.ContentBlockAddress <- block.Address
            confirm.Payload <- block.Payload
            confirm.StoragePlacement <- storagePlacement

            let! confirmResult = postUploadSessionDecision "/storage/confirmContentBlockUpload" confirm

            let confirmedPlacement =
                confirmResult.ReturnValue.Session.ConfirmedBlockUploads[0]
                    .StoragePlacement

            let finalize = Parameters.Storage.FinalizeManifestUploadParameters()
            setStorageParameters finalize repositoryId correlationId
            finalize.UploadSessionId <- sessionId
            finalize.AuthorizedScope <- "/"
            finalize.OperationId <- "finalize"
            finalize.Manifest <- manifest

            let! finalizeResult = postUploadSessionDecision "/storage/finalizeManifestUpload" finalize
            Assert.That(finalizeResult.ReturnValue.Session.FinalizedManifestAddress, Is.EqualTo(Some manifest.ManifestAddress))
            Assert.That(finalizeResult.ReturnValue.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))

            let downloadUriParameters = Parameters.Storage.GetContentBlockDownloadUriParameters()
            setStorageParameters downloadUriParameters repositoryId correlationId
            downloadUriParameters.AuthorizedScope <- "/"
            downloadUriParameters.ContentBlockAddress <- block.Address
            downloadUriParameters.StoragePoolId <- manifest.StoragePoolId
            downloadUriParameters.ManifestAddress <- manifest.ManifestAddress

            let! downloadUriResponse = Client.PostAsync("/storage/getContentBlockDownloadUri", createJsonContent downloadUriParameters)
            let! downloadUriBody = downloadUriResponse.Content.ReadAsStringAsync()
            Assert.That(downloadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), downloadUriBody)
            assertRawStringContent downloadUriResponse

            let! downloadedPayload = downloadContentBlockWithSas (Uri downloadUriBody)
            let downloadPlacement = StoragePlacementTestHelpers.contentBlockPlacementFromUri (Uri downloadUriBody) None

            Assert.That(downloadPlacement.ObjectKey, Is.EqualTo(confirmedPlacement.ObjectKey))
            Assert.That(downloadPlacement.StorageAccountName, Is.EqualTo(confirmedPlacement.StorageAccountName))
            Assert.That(downloadPlacement.StorageContainerName, Is.EqualTo(confirmedPlacement.StorageContainerName))

            Assert.That(Convert.ToHexString(downloadedPayload), Is.EqualTo(Convert.ToHexString(block.Payload)))

            match
                ManifestValidation.validate
                    RabinChunking.SuiteName
                    manifest
                    [
                        ManifestValidation.createBlockPayload block.Address downloadedPayload
                    ]
                with
            | Ok reconstructedBytes -> Assert.That(Convert.ToHexString(reconstructedBytes), Is.EqualTo(Convert.ToHexString(payload)))
            | Error error -> Assert.Fail($"Expected downloaded ContentBlock to reconstruct the manifest bytes, got {error}.")
        }

    [<Test>]
    member _.ConfirmContentBlockUploadReplaysAfterStagingCleanupForSameOperationId() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 1uy
            Guid.NewGuid().ToByteArray().CopyTo(payload, 1)
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-no-global-dedupe-v1"
            start.OperationId <- "start-confirm-replay"

            let! _ = postUploadSessionDecision "/storage/startManifestUploadSession" start

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters register repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- "/"
            register.OperationId <- "register-confirm-replay"
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length

            let! _ = postUploadSessionDecision "/storage/registerContentBlockUpload" register

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.UploadSessionId <- sessionId
            uploadUriParameters.ContentBlockAddress <- block.Address
            uploadUriParameters.AuthorizedScope <- "/"

            let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = uploadUriResponse.Content.ReadAsStringAsync()
            Assert.That(uploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), uploadUriBody)

            let uploadUri = Uri uploadUriBody
            let! uploadETag = uploadContentBlockWithSas block.Payload uploadUri

            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirm repositoryId correlationId
            confirm.UploadSessionId <- sessionId
            confirm.AuthorizedScope <- "/"
            confirm.OperationId <- "confirm-replayed-after-staging-delete"
            confirm.ContentBlockAddress <- block.Address
            confirm.Payload <- block.Payload
            confirm.StoragePlacement <- StoragePlacementTestHelpers.contentBlockPlacementFromUri uploadUri (Some uploadETag)

            let! firstConfirm = postUploadSessionDecision "/storage/confirmContentBlockUpload" confirm
            Assert.That(firstConfirm.ReturnValue.WasIdempotentReplay, Is.False)

            let! replayConfirm = postUploadSessionDecision "/storage/confirmContentBlockUpload" confirm
            Assert.That(replayConfirm.ReturnValue.WasIdempotentReplay, Is.True)
            Assert.That(replayConfirm.ReturnValue.Session.ConfirmedBlockUploads.Length, Is.EqualTo(1))

            Assert.That(
                replayConfirm.ReturnValue.Session.ConfirmedBlockUploads[0]
                    .ContentBlockAddress,
                Is.EqualTo(block.Address)
            )
        }

    [<Test>]
    member _.ConfirmContentBlockUploadDeletesStagingWhenActorRejectsAfterConfirmRace() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let acceptedPayload = pseudoRandomBytes 220000
            acceptedPayload[0] <- 9uy
            let acceptedBlock = encodeBlock acceptedPayload
            let manifest = manifestFor acceptedPayload acceptedBlock
            let racingPayload = pseudoRandomBytes (8 * 1024 * 1024)
            racingPayload[0] <- 10uy

            Guid
                .NewGuid()
                .ToByteArray()
                .CopyTo(racingPayload, 1)

            let racingBlock = encodeBlock racingPayload

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-no-global-dedupe-v1"
            start.OperationId <- "start-actor-race"

            let! startResult = postUploadSessionDecision "/storage/startManifestUploadSession" start
            let manifest = manifestForStoragePool startResult.ReturnValue.Session.StoragePoolId acceptedPayload acceptedBlock

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters register repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- "/"
            register.OperationId <- "register-accepted"
            register.ContentBlockAddress <- acceptedBlock.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 acceptedPayload.Length
            register.ExpectedPayloadLength <- int64 acceptedBlock.Payload.Length

            let! _ = postUploadSessionDecision "/storage/registerContentBlockUpload" register

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.UploadSessionId <- sessionId
            uploadUriParameters.ContentBlockAddress <- acceptedBlock.Address
            uploadUriParameters.AuthorizedScope <- "/"

            let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = uploadUriResponse.Content.ReadAsStringAsync()
            Assert.That(uploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), uploadUriBody)

            let acceptedUploadUri = Uri uploadUriBody
            let! acceptedUploadETag = uploadContentBlockWithSas acceptedBlock.Payload acceptedUploadUri

            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirm repositoryId correlationId
            confirm.UploadSessionId <- sessionId
            confirm.AuthorizedScope <- "/"
            confirm.OperationId <- "confirm-accepted"
            confirm.ContentBlockAddress <- acceptedBlock.Address
            confirm.Payload <- acceptedBlock.Payload
            confirm.StoragePlacement <- StoragePlacementTestHelpers.contentBlockPlacementFromUri acceptedUploadUri (Some acceptedUploadETag)

            let! _ = postUploadSessionDecision "/storage/confirmContentBlockUpload" confirm

            let registerRacing = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters registerRacing repositoryId correlationId
            registerRacing.UploadSessionId <- sessionId
            registerRacing.AuthorizedScope <- "/"
            registerRacing.OperationId <- "register-racing"
            registerRacing.ContentBlockAddress <- racingBlock.Address
            registerRacing.LogicalOffset <- int64 acceptedPayload.Length
            registerRacing.LogicalLength <- int64 racingPayload.Length
            registerRacing.ExpectedPayloadLength <- int64 racingBlock.Payload.Length

            let! _ = postUploadSessionDecision "/storage/registerContentBlockUpload" registerRacing

            let racingUploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters racingUploadUriParameters repositoryId correlationId
            racingUploadUriParameters.UploadSessionId <- sessionId
            racingUploadUriParameters.ContentBlockAddress <- racingBlock.Address
            racingUploadUriParameters.AuthorizedScope <- "/"

            let! racingUploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent racingUploadUriParameters)
            let! racingUploadUriBody = racingUploadUriResponse.Content.ReadAsStringAsync()
            Assert.That(racingUploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), racingUploadUriBody)

            let racingUploadUri = Uri racingUploadUriBody
            let! racingUploadETag = uploadContentBlockWithSas racingBlock.Payload racingUploadUri
            let racingStagingPlacement = StoragePlacementTestHelpers.contentBlockPlacementFromUri racingUploadUri (Some racingUploadETag)

            let racingConfirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters racingConfirm repositoryId correlationId
            racingConfirm.UploadSessionId <- sessionId
            racingConfirm.AuthorizedScope <- "/"
            racingConfirm.OperationId <- "confirm-racing"
            racingConfirm.ContentBlockAddress <- racingBlock.Address
            racingConfirm.Payload <- racingBlock.Payload
            racingConfirm.StoragePlacement <- racingStagingPlacement

            use racingConfirmContent = createJsonContent racingConfirm
            let racingConfirmTask = Client.PostAsync("/storage/confirmContentBlockUpload", racingConfirmContent)

            let finalize = Parameters.Storage.FinalizeManifestUploadParameters()
            setStorageParameters finalize repositoryId correlationId
            finalize.UploadSessionId <- sessionId
            finalize.AuthorizedScope <- "/"
            finalize.OperationId <- "finalize-during-confirm-race"
            finalize.Manifest <- manifest

            let! finalizeResult = postUploadSessionDecision "/storage/finalizeManifestUpload" finalize
            Assert.That(finalizeResult.ReturnValue.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))

            let! racingResponse = racingConfirmTask
            let! racingBody = racingResponse.Content.ReadAsStringAsync()
            Assert.That(racingResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), racingBody)
            Assert.That(racingBody, Does.Contain("UploadSession must be active"))

            let! stagingExists = contentBlockExistsAtPlacement racingUploadUri racingStagingPlacement
            Assert.That(stagingExists, Is.False)
        }

    [<Test>]
    member _.ConfirmContentBlockUploadRejectsTerminalSessionBeforeFinalCasMaterialization() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let firstPayload = pseudoRandomBytes 220000
            firstPayload[0] <- 2uy
            let secondPayload = pseudoRandomBytes 220000
            secondPayload[0] <- 3uy

            Guid
                .NewGuid()
                .ToByteArray()
                .CopyTo(secondPayload, 1)

            let firstBlock = encodeBlock firstPayload
            let secondBlock = encodeBlock secondPayload
            let manifest = manifestFor firstPayload firstBlock

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-no-global-dedupe-v1"
            start.OperationId <- "start-terminal-confirm"

            let! startResult = postUploadSessionDecision "/storage/startManifestUploadSession" start
            let manifest = manifestForStoragePool startResult.ReturnValue.Session.StoragePoolId firstPayload firstBlock

            let registerBlock index (block: ContentBlockFormat.EncodedContentBlock) logicalLength =
                task {
                    let register = Parameters.Storage.RegisterContentBlockUploadParameters()
                    setStorageParameters register repositoryId correlationId
                    register.UploadSessionId <- sessionId
                    register.AuthorizedScope <- "/"
                    register.OperationId <- $"register-terminal-confirm-{index}"
                    register.ContentBlockAddress <- block.Address
                    register.LogicalOffset <- 0L
                    register.LogicalLength <- int64 logicalLength
                    register.ExpectedPayloadLength <- int64 block.Payload.Length

                    let! _ = postUploadSessionDecision "/storage/registerContentBlockUpload" register
                    return ()
                }

            do! registerBlock 0 firstBlock firstPayload.Length
            do! registerBlock 1 secondBlock secondPayload.Length

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.UploadSessionId <- sessionId
            uploadUriParameters.ContentBlockAddress <- firstBlock.Address
            uploadUriParameters.AuthorizedScope <- "/"

            let! firstUploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! firstUploadUriBody = firstUploadUriResponse.Content.ReadAsStringAsync()
            Assert.That(firstUploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), firstUploadUriBody)

            uploadUriParameters.ContentBlockAddress <- secondBlock.Address
            let! secondUploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! secondUploadUriBody = secondUploadUriResponse.Content.ReadAsStringAsync()
            Assert.That(secondUploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), secondUploadUriBody)

            let firstUploadUri = Uri firstUploadUriBody
            let secondUploadUri = Uri secondUploadUriBody
            let! firstUploadETag = uploadContentBlockWithSas firstBlock.Payload firstUploadUri
            let! secondUploadETag = uploadContentBlockWithSas secondBlock.Payload secondUploadUri

            let confirmFirst = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirmFirst repositoryId correlationId
            confirmFirst.UploadSessionId <- sessionId
            confirmFirst.AuthorizedScope <- "/"
            confirmFirst.OperationId <- "confirm-terminal-first"
            confirmFirst.ContentBlockAddress <- firstBlock.Address
            confirmFirst.Payload <- firstBlock.Payload
            confirmFirst.StoragePlacement <- StoragePlacementTestHelpers.contentBlockPlacementFromUri firstUploadUri (Some firstUploadETag)

            let! _ = postUploadSessionDecision "/storage/confirmContentBlockUpload" confirmFirst

            let finalize = Parameters.Storage.FinalizeManifestUploadParameters()
            setStorageParameters finalize repositoryId correlationId
            finalize.UploadSessionId <- sessionId
            finalize.AuthorizedScope <- "/"
            finalize.OperationId <- "finalize-terminal-confirm"
            finalize.Manifest <- manifest

            let! finalizeResult = postUploadSessionDecision "/storage/finalizeManifestUpload" finalize
            Assert.That(finalizeResult.ReturnValue.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))

            let confirmSecond = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirmSecond repositoryId correlationId
            confirmSecond.UploadSessionId <- sessionId
            confirmSecond.AuthorizedScope <- "/"
            confirmSecond.OperationId <- "confirm-terminal-second"
            confirmSecond.ContentBlockAddress <- secondBlock.Address
            confirmSecond.Payload <- secondBlock.Payload
            confirmSecond.StoragePlacement <- StoragePlacementTestHelpers.contentBlockPlacementFromUri secondUploadUri (Some secondUploadETag)

            let! body = postUploadSessionBadRequest "/storage/confirmContentBlockUpload" confirmSecond
            Assert.That(body, Does.Contain("UploadSession must be active"))
            Assert.That(body, Does.Contain("RetentionPending"))

            let! finalSecondExists = finalContentBlockExistsByAddress secondUploadUri secondBlock.Address
            Assert.That(finalSecondExists, Is.False)
        }

    [<Test>]
    member _.ConfirmContentBlockUploadRejectsPayloadLengthIntentMismatchBeforeFinalCasMaterialization() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 4uy
            Guid.NewGuid().ToByteArray().CopyTo(payload, 1)
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-no-global-dedupe-v1"
            start.OperationId <- "start-intent-mismatch"

            let! _ = postUploadSessionDecision "/storage/startManifestUploadSession" start

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters register repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- "/"
            register.OperationId <- "register-intent-mismatch"
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length + 1L

            let! _ = postUploadSessionDecision "/storage/registerContentBlockUpload" register

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.UploadSessionId <- sessionId
            uploadUriParameters.ContentBlockAddress <- block.Address
            uploadUriParameters.AuthorizedScope <- "/"

            let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = uploadUriResponse.Content.ReadAsStringAsync()
            Assert.That(uploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), uploadUriBody)

            let uploadUri = Uri uploadUriBody
            let! uploadETag = uploadContentBlockWithSas block.Payload uploadUri

            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirm repositoryId correlationId
            confirm.UploadSessionId <- sessionId
            confirm.AuthorizedScope <- "/"
            confirm.OperationId <- "confirm-intent-mismatch"
            confirm.ContentBlockAddress <- block.Address
            confirm.Payload <- block.Payload
            confirm.StoragePlacement <- StoragePlacementTestHelpers.contentBlockPlacementFromUri uploadUri (Some uploadETag)

            let! body = postUploadSessionBadRequest "/storage/confirmContentBlockUpload" confirm
            Assert.That(body, Does.Contain("payload length mismatch"))

            let! finalBlockExists = finalContentBlockExistsByAddress uploadUri block.Address
            Assert.That(finalBlockExists, Is.False)
        }

    [<Test>]
    member _.ContentBlockUploadUriScopesStagingKeyByRepository() =
        task {
            let firstRepositoryId = repositoryIds[0]
            let secondRepositoryId = repositoryIds[1]
            let firstCorrelationId = generateCorrelationId ()
            let secondCorrelationId = generateCorrelationId ()
            let firstSessionId = Guid.NewGuid()
            let secondSessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 4uy
            Guid.NewGuid().ToByteArray().CopyTo(payload, 1)
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            let startSession repositoryId correlationId sessionId =
                task {
                    let start = Parameters.Storage.StartManifestUploadSessionParameters()
                    setStorageParameters start repositoryId correlationId
                    start.UploadSessionId <- sessionId
                    start.AuthorizedScope <- "/"
                    start.FileContentHash <- manifest.FileContentHash
                    start.ExpectedSize <- manifest.Size
                    start.ChunkingSuiteId <- manifest.ChunkingSuiteId
                    start.SamplingPolicySnapshot <- "sdk-no-global-dedupe-v1"
                    start.OperationId <- $"start-staging-scope-{repositoryId}"

                    let! _ = postUploadSessionDecision "/storage/startManifestUploadSession" start
                    return ()
                }

            let registerBlock repositoryId correlationId sessionId =
                task {
                    let register = Parameters.Storage.RegisterContentBlockUploadParameters()
                    setStorageParameters register repositoryId correlationId
                    register.UploadSessionId <- sessionId
                    register.AuthorizedScope <- "/"
                    register.OperationId <- $"register-staging-scope-{repositoryId}"
                    register.ContentBlockAddress <- block.Address
                    register.LogicalOffset <- 0L
                    register.LogicalLength <- int64 payload.Length
                    register.ExpectedPayloadLength <- int64 block.Payload.Length

                    let! _ = postUploadSessionDecision "/storage/registerContentBlockUpload" register
                    return ()
                }

            let getUploadUri repositoryId correlationId sessionId =
                task {
                    let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
                    setStorageParameters uploadUriParameters repositoryId correlationId
                    uploadUriParameters.UploadSessionId <- sessionId
                    uploadUriParameters.ContentBlockAddress <- block.Address
                    uploadUriParameters.AuthorizedScope <- "/"

                    let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
                    let! uploadUriBody = uploadUriResponse.Content.ReadAsStringAsync()
                    Assert.That(uploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), uploadUriBody)
                    return Uri uploadUriBody
                }

            do! startSession firstRepositoryId firstCorrelationId firstSessionId
            do! startSession secondRepositoryId secondCorrelationId secondSessionId
            do! registerBlock firstRepositoryId firstCorrelationId firstSessionId
            do! registerBlock secondRepositoryId secondCorrelationId secondSessionId

            let! firstUploadUri = getUploadUri firstRepositoryId firstCorrelationId firstSessionId
            let! secondUploadUri = getUploadUri secondRepositoryId secondCorrelationId secondSessionId

            Assert.That(firstUploadUri.AbsolutePath, Does.Contain($"/staging/repositories/{firstRepositoryId}/"))
            Assert.That(secondUploadUri.AbsolutePath, Does.Contain($"/staging/repositories/{secondRepositoryId}/"))
            Assert.That(firstUploadUri.AbsolutePath, Is.Not.EqualTo(secondUploadUri.AbsolutePath))
        }

    [<Test>]
    member _.ConfirmContentBlockUploadRejectsFinalCasEncodingWithDifferentPhysicalOffsets() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 5uy
            Guid.NewGuid().ToByteArray().CopyTo(payload, 1)
            let stagedBlock = encodeBlockAt 0L payload
            let existingFinalBlock = encodeBlockAt 4096L payload
            let manifest = manifestFor payload stagedBlock

            Assert.That(existingFinalBlock.Address, Is.EqualTo(stagedBlock.Address))
            Assert.That(Convert.ToHexString(existingFinalBlock.Payload), Is.Not.EqualTo(Convert.ToHexString(stagedBlock.Payload)))

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-no-global-dedupe-v1"
            start.OperationId <- "start-equivalent-final"

            let! _ = postUploadSessionDecision "/storage/startManifestUploadSession" start

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters register repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- "/"
            register.OperationId <- "register-equivalent-final"
            register.ContentBlockAddress <- stagedBlock.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 stagedBlock.Payload.Length

            let! _ = postUploadSessionDecision "/storage/registerContentBlockUpload" register

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.UploadSessionId <- sessionId
            uploadUriParameters.ContentBlockAddress <- stagedBlock.Address
            uploadUriParameters.AuthorizedScope <- "/"

            let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = uploadUriResponse.Content.ReadAsStringAsync()
            Assert.That(uploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), uploadUriBody)

            let uploadUri = Uri uploadUriBody
            let! uploadETag = uploadContentBlockWithSas stagedBlock.Payload uploadUri
            do! uploadFinalContentBlockByAddress uploadUri stagedBlock.Address existingFinalBlock.Payload

            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirm repositoryId correlationId
            confirm.UploadSessionId <- sessionId
            confirm.AuthorizedScope <- "/"
            confirm.OperationId <- "confirm-equivalent-final"
            confirm.ContentBlockAddress <- stagedBlock.Address
            confirm.Payload <- stagedBlock.Payload
            confirm.StoragePlacement <- StoragePlacementTestHelpers.contentBlockPlacementFromUri uploadUri (Some uploadETag)

            let! body = postUploadSessionBadRequest "/storage/confirmContentBlockUpload" confirm
            Assert.That(body, Does.Contain("does not match the staged validated payload"))
        }

    [<Test>]
    member _.ConfirmContentBlockUploadRejectsInvalidFinalCasBlobInsteadOfTreatingItAsSuccess() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 0uy
            Guid.NewGuid().ToByteArray().CopyTo(payload, 1)
            let block = encodeBlock payload
            let manifest = manifestFor payload block
            let invalidFinalPayload = pseudoRandomBytes block.Payload.Length
            invalidFinalPayload[0] <- invalidFinalPayload[0] ^^^ 0xffuy

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-no-global-dedupe-v1"
            start.OperationId <- "start-poisoned-final"

            let! _ = postUploadSessionDecision "/storage/startManifestUploadSession" start

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters register repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- "/"
            register.OperationId <- "register-poisoned-final"
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length

            let! _ = postUploadSessionDecision "/storage/registerContentBlockUpload" register

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.UploadSessionId <- sessionId
            uploadUriParameters.ContentBlockAddress <- block.Address
            uploadUriParameters.AuthorizedScope <- "/"

            let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = uploadUriResponse.Content.ReadAsStringAsync()
            Assert.That(uploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), uploadUriBody)

            let uploadUri = Uri uploadUriBody

            Assert.That(
                uploadUri.AbsolutePath,
                Does.Contain($"/staging/repositories/{repositoryId}/upload-sessions/{sessionId:N}/content-blocks/{block.Address}")
            )

            Assert.That(uploadUri.AbsolutePath, Does.Not.Contain($"/cas/content/{block.Address}"))

            let! uploadETag = uploadContentBlockWithSas block.Payload uploadUri
            do! uploadFinalContentBlockByAddress uploadUri block.Address invalidFinalPayload

            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirm repositoryId correlationId
            confirm.UploadSessionId <- sessionId
            confirm.AuthorizedScope <- "/"
            confirm.OperationId <- "confirm-poisoned-final"
            confirm.ContentBlockAddress <- block.Address
            confirm.Payload <- block.Payload
            confirm.StoragePlacement <- StoragePlacementTestHelpers.contentBlockPlacementFromUri uploadUri (Some uploadETag)

            let! body = postUploadSessionBadRequest "/storage/confirmContentBlockUpload" confirm
            Assert.That(body, Does.Contain("Existing final ContentBlock"))
            Assert.That(body, Does.Contain("invalid"))
        }

    [<Test>]
    member _.ContentBlockUploadUriRejectsRetentionPendingSessionWithRetainedIntent() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 7uy
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-no-global-dedupe-v1"
            start.OperationId <- "start"

            let! startResult = postUploadSessionDecision "/storage/startManifestUploadSession" start
            let manifest = manifestForStoragePool startResult.ReturnValue.Session.StoragePoolId payload block

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters register repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- "/"
            register.OperationId <- "register-0"
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length

            let! _ = postUploadSessionDecision "/storage/registerContentBlockUpload" register

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.UploadSessionId <- sessionId
            uploadUriParameters.ContentBlockAddress <- block.Address
            uploadUriParameters.AuthorizedScope <- "/"

            let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = uploadUriResponse.Content.ReadAsStringAsync()
            Assert.That(uploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), uploadUriBody)

            let uploadUri = Uri uploadUriBody
            let! uploadETag = uploadContentBlockWithSas block.Payload uploadUri

            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirm repositoryId correlationId
            confirm.UploadSessionId <- sessionId
            confirm.AuthorizedScope <- "/"
            confirm.OperationId <- "confirm-0"
            confirm.ContentBlockAddress <- block.Address
            confirm.Payload <- block.Payload
            confirm.StoragePlacement <- StoragePlacementTestHelpers.contentBlockPlacementFromUri uploadUri (Some uploadETag)

            let! _ = postUploadSessionDecision "/storage/confirmContentBlockUpload" confirm

            let finalize = Parameters.Storage.FinalizeManifestUploadParameters()
            setStorageParameters finalize repositoryId correlationId
            finalize.UploadSessionId <- sessionId
            finalize.AuthorizedScope <- "/"
            finalize.OperationId <- "finalize"
            finalize.Manifest <- manifest

            let! finalizeResult = postUploadSessionDecision "/storage/finalizeManifestUpload" finalize
            Assert.That(finalizeResult.ReturnValue.Session.LifecycleState, Is.EqualTo(UploadSessionLifecycleState.RetentionPending))
            Assert.That(finalizeResult.ReturnValue.Session.BlockUploadIntents, Is.Not.Empty)

            let! deniedResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! deniedBody = deniedResponse.Content.ReadAsStringAsync()

            Assert.That(deniedResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), deniedBody)
            assertJsonContent deniedResponse
            Assert.That(deniedBody, Does.Contain("UploadSession must be active"))
            Assert.That(deniedBody, Does.Contain("RetentionPending"))
            Assert.That(deniedBody, Does.Not.Contain("cas/content/"))
        }

    [<Test>]
    member _.ContentBlockDownloadUriRejectsManifestFinalizedForDifferentAuthorizedScope() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let pathA = $"/download/path-a-{Guid.NewGuid():N}.bin"
            let pathB = $"/download/path-b-{Guid.NewGuid():N}.bin"
            let readerClaim = $"download-scope-reader-{Guid.NewGuid():N}"
            let payload = pseudoRandomBytes 220000
            payload[0] <- 8uy
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            do! upsertPathPermissionAsync Client repositoryId pathA [ (readerClaim, "Read") ]

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- pathB
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-no-global-dedupe-v1"
            start.OperationId <- "start"

            let! startResult = postUploadSessionDecision "/storage/startManifestUploadSession" start
            let manifest = manifestForStoragePool startResult.ReturnValue.Session.StoragePoolId payload block

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters register repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- pathB
            register.OperationId <- "register-0"
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length

            let! _ = postUploadSessionDecision "/storage/registerContentBlockUpload" register

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.UploadSessionId <- sessionId
            uploadUriParameters.ContentBlockAddress <- block.Address
            uploadUriParameters.AuthorizedScope <- pathB

            let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = uploadUriResponse.Content.ReadAsStringAsync()
            Assert.That(uploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), uploadUriBody)
            let! uploadETag = uploadContentBlockWithSas block.Payload (Uri uploadUriBody)

            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirm repositoryId correlationId
            confirm.UploadSessionId <- sessionId
            confirm.AuthorizedScope <- pathB
            confirm.OperationId <- "confirm-0"
            confirm.ContentBlockAddress <- block.Address
            confirm.Payload <- block.Payload
            confirm.StoragePlacement <- StoragePlacementTestHelpers.contentBlockPlacementFromUri (Uri uploadUriBody) (Some uploadETag)

            let! _ = postUploadSessionDecision "/storage/confirmContentBlockUpload" confirm

            let finalize = Parameters.Storage.FinalizeManifestUploadParameters()
            setStorageParameters finalize repositoryId correlationId
            finalize.UploadSessionId <- sessionId
            finalize.AuthorizedScope <- pathB
            finalize.OperationId <- "finalize"
            finalize.Manifest <- manifest

            finalize.BlockPayloads <-
                [|
                    { Address = block.Address; Payload = block.Payload }
                |]

            let! finalizeResult = postUploadSessionDecision "/storage/finalizeManifestUpload" finalize
            Assert.That(finalizeResult.ReturnValue.Session.FinalizedManifestAddress, Is.EqualTo(Some manifest.ManifestAddress))
            Assert.That(finalizeResult.ReturnValue.Session.AuthorizedScope, Is.EqualTo(pathB))

            let downloadUriParameters = Parameters.Storage.GetContentBlockDownloadUriParameters()
            setStorageParameters downloadUriParameters repositoryId correlationId
            downloadUriParameters.AuthorizedScope <- pathA
            downloadUriParameters.ContentBlockAddress <- block.Address
            downloadUriParameters.StoragePoolId <- manifest.StoragePoolId
            downloadUriParameters.ManifestAddress <- manifest.ManifestAddress

            use pathAReader = createClientWithClaims [ readerClaim ]
            let! downloadUriResponse = pathAReader.PostAsync("/storage/getContentBlockDownloadUri", createJsonContent downloadUriParameters)
            let! downloadUriBody = downloadUriResponse.Content.ReadAsStringAsync()
            Assert.That(downloadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), downloadUriBody)
            assertJsonContent downloadUriResponse
            Assert.That(downloadUriBody, Does.Contain("authorized scope"))
            Assert.That(downloadUriBody, Does.Not.Contain("cas/content/"))
        }

    [<Test>]
    member _.StorageRoutesLockRawAndJsonResponseContracts() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let contentBlockAddress = ContentBlockAddress(ContentAddress.computeBlake3Hex (Encoding.UTF8.GetBytes $"content-block-contract-{Guid.NewGuid():N}"))

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.ContentBlockAddress <- contentBlockAddress
            uploadUriParameters.AuthorizedScope <- "/"

            let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = uploadUriResponse.Content.ReadAsStringAsync()
            Assert.That(uploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), uploadUriBody)
            assertJsonContent uploadUriResponse
            Assert.That(uploadUriBody, Does.Contain("UploadSessionId is required"))

            let downloadUriParameters = Parameters.Storage.GetContentBlockDownloadUriParameters()
            setStorageParameters downloadUriParameters repositoryId correlationId
            downloadUriParameters.ContentBlockAddress <- contentBlockAddress

            let! downloadUriResponse = Client.PostAsync("/storage/getContentBlockDownloadUri", createJsonContent downloadUriParameters)
            let! downloadUriBody = downloadUriResponse.Content.ReadAsStringAsync()
            Assert.That(downloadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), downloadUriBody)
            assertJsonContent downloadUriResponse
            Assert.That(downloadUriBody, Does.Contain("ManifestAddress"))

            let! discoveryResponse =
                postDiscoveryAsync
                    Client
                    repositoryId
                    [|
                        $"chunk-contract-{Guid.NewGuid():N}"
                    |]

            let! discoveryBody = discoveryResponse.Content.ReadAsStringAsync()
            Assert.That(discoveryResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), discoveryBody)
            assertJsonContent discoveryResponse

            use discoveryDocument = JsonDocument.Parse(discoveryBody)
            let discoveryRoot = discoveryDocument.RootElement
            let returnValue = requireJsonProperty "ReturnValue" discoveryRoot
            let policy = requireJsonProperty "Policy" returnValue

            Assert.That(
                (requireJsonProperty "IsAuthoritative" policy)
                    .GetBoolean(),
                Is.False
            )

            Assert.That(
                (requireJsonProperty "EmptyResponseMeansAbsent" policy)
                    .GetBoolean(),
                Is.False
            )

            let oversizedKeyChunks = Array.init (maxDiscoveryKeyChunkAddresses + 1) (fun index -> $"chunk-contract-{index}-{Guid.NewGuid():N}")

            let! badDiscoveryResponse = postDiscoveryAsync Client repositoryId oversizedKeyChunks
            let! badDiscoveryBody = badDiscoveryResponse.Content.ReadAsStringAsync()
            Assert.That(badDiscoveryResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), badDiscoveryBody)
            assertJsonContent badDiscoveryResponse

            use errorDocument = JsonDocument.Parse(badDiscoveryBody)

            Assert.That(
                (requireJsonProperty "Error" errorDocument.RootElement)
                    .GetString(),
                Does.Contain("KeyChunkAddresses")
            )

            Assert.That(
                (requireJsonProperty "CorrelationId" errorDocument.RootElement)
                    .GetString(),
                Is.Not.Empty
            )
        }

    [<Test>]
    member _.UploadSessionMutationsRejectAuthorizedScopeMismatch() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 2uy
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/allowed/file.bin"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-no-global-dedupe-v1"
            start.OperationId <- "start"

            let! _ = postUploadSessionDecision "/storage/startManifestUploadSession" start

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters register repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- "/other/file.bin"
            register.OperationId <- "register-0"
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length

            let! body = postUploadSessionBadRequest "/storage/registerContentBlockUpload" register
            Assert.That(body, Does.Contain("AuthorizedScope must match"))
        }

    [<Test>]
    member _.ClaimReuseRangesRejectsAuthorizedScopeMismatchBeforeMetadataLookup() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 3uy
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/allowed/file.bin"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-dedupe-discovery-claim-test"
            start.OperationId <- "start"

            let! _ = postUploadSessionDecision "/storage/startManifestUploadSession" start

            let claim = Parameters.Storage.ClaimReuseRangesParameters()
            setStorageParameters claim repositoryId correlationId
            claim.UploadSessionId <- sessionId
            claim.AuthorizedScope <- "/other/file.bin"
            claim.OperationId <- "claim"
            claim.DiscoveryOperationId <- "discovery"
            claim.Hints <- [| reuseHint 0 |]

            let! body = postUploadSessionBadRequest "/storage/claimReuseRanges" claim
            Assert.That(body, Does.Contain("AuthorizedScope must match"))
            Assert.That(body, Does.Not.Contain("Authoritative ContentBlockMetadata is absent"))
        }

    [<Test>]
    member _.ClaimReuseRangesRejectsDiscoveryOperationMismatchBeforeMetadataLookup() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 4uy
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-dedupe-discovery-claim-test"
            start.OperationId <- "start"

            let! _ = postUploadSessionDecision "/storage/startManifestUploadSession" start

            let issue = Parameters.Storage.IssueDedupeDiscoveryParameters()
            setStorageParameters issue repositoryId correlationId
            issue.UploadSessionId <- sessionId
            issue.AuthorizedScope <- "/"
            issue.OperationId <- "discovery-active"

            issue.ExpiresAt <-
                getCurrentInstant()
                    .Plus(NodaTime.Duration.FromMinutes 5L)

            issue.MinimumReuseRunLength <- Parameters.Storage.MinimumAcceptedReuseRunLength
            issue.Hints <- Array.empty

            let! _ = postUploadSessionDecision "/storage/issueDedupeDiscovery" issue

            let claim = Parameters.Storage.ClaimReuseRangesParameters()
            setStorageParameters claim repositoryId correlationId
            claim.UploadSessionId <- sessionId
            claim.AuthorizedScope <- "/"
            claim.OperationId <- "claim"
            claim.DiscoveryOperationId <- "discovery-stale"
            claim.Hints <- [| reuseHint 1 |]

            let! body = postUploadSessionBadRequest "/storage/claimReuseRanges" claim
            Assert.That(body, Does.Contain("DiscoveryOperationId does not match"))
            Assert.That(body, Does.Not.Contain("Authoritative ContentBlockMetadata is absent"))
        }

    [<Test>]
    member _.IssueDedupeDiscoveryRejectsOversizedHintArraysBeforeDispatch() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()

            let issue = Parameters.Storage.IssueDedupeDiscoveryParameters()
            setStorageParameters issue repositoryId correlationId
            issue.UploadSessionId <- Guid.NewGuid()
            issue.AuthorizedScope <- "/"
            issue.OperationId <- "discovery"

            issue.ExpiresAt <-
                getCurrentInstant()
                    .Plus(NodaTime.Duration.FromMinutes 5L)

            issue.MinimumReuseRunLength <- Parameters.Storage.MinimumAcceptedReuseRunLength
            issue.Hints <- Array.init (Parameters.Storage.MaxReuseRangeClaims + 1) reuseHint

            let! body = postUploadSessionBadRequest "/storage/issueDedupeDiscovery" issue
            Assert.That(body, Does.Contain("IssueDedupeDiscovery Hints"))
            Assert.That(body, Does.Contain($"{Parameters.Storage.MaxReuseRangeClaims}"))
        }

    [<Test>]
    member _.IssueDedupeDiscoveryRejectsMissingSessionBeforeDedupeIndexLookup() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()

            let issue = Parameters.Storage.IssueDedupeDiscoveryParameters()
            setStorageParameters issue repositoryId correlationId
            issue.UploadSessionId <- Guid.NewGuid()
            issue.AuthorizedScope <- "/"
            issue.OperationId <- "discovery"

            issue.ExpiresAt <-
                getCurrentInstant()
                    .Plus(NodaTime.Duration.FromMinutes 5L)

            issue.MinimumReuseRunLength <- Parameters.Storage.MinimumAcceptedReuseRunLength
            issue.Hints <- [| reuseHint 0 |]

            let! body = postUploadSessionBadRequest "/storage/issueDedupeDiscovery" issue
            Assert.That(body, Does.Contain("UploadSession must be started"))
            Assert.That(body, Does.Not.Contain("server discovery candidates"))
        }

    [<Test>]
    member _.IssueDedupeDiscoveryRejectsHintsNotBackedByServerDiscovery() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 5uy
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-dedupe-discovery-claim-test"
            start.OperationId <- "start"

            let! _ = postUploadSessionDecision "/storage/startManifestUploadSession" start

            let issue = Parameters.Storage.IssueDedupeDiscoveryParameters()
            setStorageParameters issue repositoryId correlationId
            issue.UploadSessionId <- sessionId
            issue.AuthorizedScope <- "/"
            issue.OperationId <- "discovery"

            issue.ExpiresAt <-
                getCurrentInstant()
                    .Plus(NodaTime.Duration.FromMinutes 5L)

            issue.MinimumReuseRunLength <- Parameters.Storage.MinimumAcceptedReuseRunLength
            issue.Hints <- [| reuseHint 0 |]

            let! body = postUploadSessionBadRequest "/storage/issueDedupeDiscovery" issue
            Assert.That(body, Does.Contain("server discovery candidates"))
            Assert.That(body, Does.Not.Contain("Dedupe discovery issued"))
        }

    [<Test>]
    member _.IssueDedupeDiscoveryNormalizesNullHintArrayToEmptySnapshot() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 6uy
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-dedupe-discovery-claim-test"
            start.OperationId <- "start"

            let! _ = postUploadSessionDecision "/storage/startManifestUploadSession" start

            let issue = Parameters.Storage.IssueDedupeDiscoveryParameters()
            setStorageParameters issue repositoryId correlationId
            issue.UploadSessionId <- sessionId
            issue.AuthorizedScope <- "/"
            issue.OperationId <- "discovery"

            issue.ExpiresAt <-
                getCurrentInstant()
                    .Plus(NodaTime.Duration.FromMinutes 5L)

            issue.MinimumReuseRunLength <- Parameters.Storage.MinimumAcceptedReuseRunLength
            issue.Hints <- null

            let! result = postUploadSessionDecision "/storage/issueDedupeDiscovery" issue
            Assert.That(result.ReturnValue.Session.DedupeDiscovery, Is.Not.EqualTo(None))
            Assert.That(result.ReturnValue.Session.DedupeDiscovery.Value.Hints, Is.Empty)
        }

    [<Test>]
    member _.ClaimReuseRangesTreatsNullHintArrayAsEmptyBeforeMetadataLookup() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 7uy
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-dedupe-discovery-claim-test"
            start.OperationId <- "start"

            let! _ = postUploadSessionDecision "/storage/startManifestUploadSession" start

            let issue = Parameters.Storage.IssueDedupeDiscoveryParameters()
            setStorageParameters issue repositoryId correlationId
            issue.UploadSessionId <- sessionId
            issue.AuthorizedScope <- "/"
            issue.OperationId <- "discovery-active"

            issue.ExpiresAt <-
                getCurrentInstant()
                    .Plus(NodaTime.Duration.FromMinutes 5L)

            issue.MinimumReuseRunLength <- Parameters.Storage.MinimumAcceptedReuseRunLength
            issue.Hints <- Array.empty

            let! _ = postUploadSessionDecision "/storage/issueDedupeDiscovery" issue

            let claim = Parameters.Storage.ClaimReuseRangesParameters()
            setStorageParameters claim repositoryId correlationId
            claim.UploadSessionId <- sessionId
            claim.AuthorizedScope <- "/"
            claim.OperationId <- "claim"
            claim.DiscoveryOperationId <- "discovery-active"
            claim.Hints <- null

            let! body = postUploadSessionBadRequest "/storage/claimReuseRanges" claim
            Assert.That(body, Does.Contain("At least one reuse range claim is required"))
            Assert.That(body, Does.Not.Contain("Authoritative ContentBlockMetadata is absent"))
        }

    [<Test>]
    member _.ClaimReuseRangesRejectsWrongStoragePoolBeforeMetadataLookup() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 9uy
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-dedupe-discovery-claim-test"
            start.OperationId <- "start"

            let! _ = postUploadSessionDecision "/storage/startManifestUploadSession" start

            let issue = Parameters.Storage.IssueDedupeDiscoveryParameters()
            setStorageParameters issue repositoryId correlationId
            issue.UploadSessionId <- sessionId
            issue.AuthorizedScope <- "/"
            issue.OperationId <- "discovery-active"

            issue.ExpiresAt <-
                getCurrentInstant()
                    .Plus(NodaTime.Duration.FromMinutes 5L)

            issue.MinimumReuseRunLength <- Parameters.Storage.MinimumAcceptedReuseRunLength
            issue.Hints <- Array.empty

            let! _ = postUploadSessionDecision "/storage/issueDedupeDiscovery" issue

            let claim = Parameters.Storage.ClaimReuseRangesParameters()
            setStorageParameters claim repositoryId correlationId
            claim.UploadSessionId <- sessionId
            claim.AuthorizedScope <- "/"
            claim.OperationId <- "claim"
            claim.DiscoveryOperationId <- "discovery-active"
            claim.Hints <- [| reuseHint 2 |]

            let! body = postUploadSessionBadRequest "/storage/claimReuseRanges" claim
            Assert.That(body, Does.Contain("upload session repository storage pool"))
            Assert.That(body, Does.Not.Contain("Authoritative ContentBlockMetadata is absent"))
        }

    [<Test>]
    member _.ClaimReuseRangesRejectsOversizedHintArraysBeforeMetadataLookup() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()

            let claim = Parameters.Storage.ClaimReuseRangesParameters()
            setStorageParameters claim repositoryId correlationId
            claim.UploadSessionId <- Guid.NewGuid()
            claim.AuthorizedScope <- "/"
            claim.OperationId <- "claim"
            claim.DiscoveryOperationId <- "discovery"
            claim.Hints <- Array.init (Parameters.Storage.MaxReuseRangeClaims + 1) reuseHint

            let! body = postUploadSessionBadRequest "/storage/claimReuseRanges" claim
            Assert.That(body, Does.Contain("ClaimReuseRanges Hints"))
            Assert.That(body, Does.Contain($"{Parameters.Storage.MaxReuseRangeClaims}"))
            Assert.That(body, Does.Not.Contain("Authoritative ContentBlockMetadata is absent"))
        }

    [<Test>]
    member _.ConfirmContentBlockUploadReturnsBadRequestWhenStagedBlockBlobIsMissing() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 1uy
            let block = encodeBlock payload
            let manifest = manifestFor payload block

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-no-global-dedupe-v1"
            start.OperationId <- "start"

            let! _ = postUploadSessionDecision "/storage/startManifestUploadSession" start

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters register repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- "/"
            register.OperationId <- "register-0"
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length

            let! _ = postUploadSessionDecision "/storage/registerContentBlockUpload" register

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.UploadSessionId <- sessionId
            uploadUriParameters.ContentBlockAddress <- block.Address
            uploadUriParameters.AuthorizedScope <- "/"

            let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = uploadUriResponse.Content.ReadAsStringAsync()
            Assert.That(uploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), uploadUriBody)

            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirm repositoryId correlationId
            confirm.UploadSessionId <- sessionId
            confirm.AuthorizedScope <- "/"
            confirm.OperationId <- "confirm-0"
            confirm.ContentBlockAddress <- block.Address
            confirm.Payload <- block.Payload

            confirm.StoragePlacement <- StoragePlacementTestHelpers.contentBlockPlacementFromUri (Uri uploadUriBody) (Some "etag-missing-block")

            let! body = postUploadSessionBadRequest "/storage/confirmContentBlockUpload" confirm
            Assert.That(body, Does.Contain("could not be read from object storage"))
        }

    [<Test>]
    member _.ConfirmContentBlockUploadReturnsBadRequestWhenStagedBlockBlobIsCorrupt() =
        task {
            let repositoryId = repositoryIds[0]
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = pseudoRandomBytes 220000
            payload[0] <- 2uy
            let block = encodeBlock payload
            let manifest = manifestFor payload block
            let corruptPayload = Array.copy block.Payload
            corruptPayload[0] <- corruptPayload[0] ^^^ 0xffuy

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- "/"
            start.FileContentHash <- manifest.FileContentHash
            start.ExpectedSize <- manifest.Size
            start.ChunkingSuiteId <- manifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "sdk-no-global-dedupe-v1"
            start.OperationId <- "start"

            let! _ = postUploadSessionDecision "/storage/startManifestUploadSession" start

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters register repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- "/"
            register.OperationId <- "register-0"
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length

            let! _ = postUploadSessionDecision "/storage/registerContentBlockUpload" register

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.UploadSessionId <- sessionId
            uploadUriParameters.ContentBlockAddress <- block.Address
            uploadUriParameters.AuthorizedScope <- "/"

            let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = uploadUriResponse.Content.ReadAsStringAsync()
            Assert.That(uploadUriResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), uploadUriBody)

            let uploadUri = Uri uploadUriBody
            let! uploadETag = uploadContentBlockWithSas corruptPayload uploadUri
            let stagingPlacement = StoragePlacementTestHelpers.contentBlockPlacementFromUri uploadUri (Some uploadETag)

            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirm repositoryId correlationId
            confirm.UploadSessionId <- sessionId
            confirm.AuthorizedScope <- "/"
            confirm.OperationId <- "confirm-0"
            confirm.ContentBlockAddress <- block.Address
            confirm.Payload <- block.Payload
            confirm.StoragePlacement <- stagingPlacement

            let! body = postUploadSessionBadRequest "/storage/confirmContentBlockUpload" confirm
            Assert.That(body, Does.Contain("Payload must match the staged uploaded bytes"))

            let! stagingExists = contentBlockExistsAtPlacement uploadUri stagingPlacement
            Assert.That(stagingExists, Is.False)
        }
