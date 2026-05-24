namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Net.Http.Headers

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
    member _.SmallWholeFileContentPopulatesContentReferenceWithoutChangingEndpointMetadata() =
        task {
            let repositoryId = repositoryIds[0]
            let relativeDirectory = $"wholefile-compatibility/{Guid.NewGuid():N}"
            let relativePath = $"{relativeDirectory}/small.bin"
            let payload = Encoding.UTF8.GetBytes($"Grace whole-file compatibility {Guid.NewGuid():N}")
            let sha256Hash = computeSha256Hash payload
            let fileVersion = FileVersion.Create relativePath sha256Hash String.Empty true (int64 payload.Length)

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
            Assert.That(downloadUri, Does.Contain(fileVersion.GetObjectFileName))
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

            return! client.PostAsync("/access/grantRole", createJsonContent parameters)
        }

    let setContentBlockParameters (parameters: Parameters.Storage.StorageParameters) repositoryId =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.CorrelationId <- generateCorrelationId ()

    let createContentBlockUploadParameters repositoryId =
        let parameters = Parameters.Storage.GetContentBlockUploadUriParameters()
        setContentBlockParameters parameters repositoryId
        parameters.ContentBlockAddress <- $"content-block-{Guid.NewGuid():N}"
        parameters

    let createContentBlockDownloadParameters repositoryId =
        let parameters = Parameters.Storage.GetContentBlockDownloadUriParameters()
        setContentBlockParameters parameters repositoryId
        parameters.ContentBlockAddress <- $"content-block-{Guid.NewGuid():N}"
        parameters

    let assertSuccessSasForContentBlock (response: HttpResponseMessage) (contentBlockAddress: ContentBlockAddress) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            Assert.That(body, Does.Contain("cas/content-blocks"))
            Assert.That(body, Does.Contain(contentBlockAddress))
        }

    [<Test>]
    member _.ContentBlockUploadUriRequiresPathWriteAndDoesNotProbeBlockExistence() =
        task {
            let repositoryId = repositoryIds[0]
            let pathWriter = $"{Guid.NewGuid()}"

            let! grantWriter = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" pathWriter "RepoContributor"
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
            do! assertSuccessSasForContentBlock allowedUpload parameters.ContentBlockAddress
        }

    [<Test>]
    member _.ContentBlockDownloadUriRequiresPathReadAndDoesNotProbeBlockExistence() =
        task {
            let repositoryId = repositoryIds[0]
            let pathReader = $"{Guid.NewGuid()}"

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" pathReader "RepoReader"
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
            do! assertSuccessSasForContentBlock allowedDownload parameters.ContentBlockAddress
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

            return! client.PostAsync("/access/grantRole", createJsonContent parameters)
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

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" repoReader "RepoReader"
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

            let! grantReader = grantRoleAsync Client "repo" ownerId organizationId repositoryId "" repoReader "RepoReader"
            Assert.That(grantReader.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use readerClient = createClientWithUserId repoReader
            let! response = postDiscoveryAsync readerClient repositoryId requestedChunkAddresses
            let! body = response.Content.ReadAsStringAsync()

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Contain("KeyChunkAddresses"))
            Assert.That(body, Does.Contain($"{maxDiscoveryKeyChunkAddresses}"))
        }

[<Parallelizable(ParallelScope.All)>]
type StorageContentBlockSdkContract() =

    let getStorageParameterType typeName =
        typeof<Parameters.Storage.StorageParameters>.Assembly.GetType ($"Grace.Shared.Parameters.Storage+{typeName}", throwOnError = false)

    let assertContentBlockParameterShape typeName =
        let parameterType = getStorageParameterType typeName
        Assert.That(parameterType, Is.Not.Null, $"{typeName} should be defined in Grace.Shared.Parameters.Storage.")
        Assert.That(parameterType.IsSubclassOf(typeof<Parameters.Storage.StorageParameters>), Is.True)
        Assert.That(parameterType.GetProperty("RelativePath"), Is.Null)
        Assert.That(parameterType.GetProperty("ContentBlockAddress"), Is.Not.Null)

    let assertSdkMethod methodName parameterTypeName =
        let parameterType = getStorageParameterType parameterTypeName
        Assert.That(parameterType, Is.Not.Null)

        let storageType = typeof<Grace.SDK.AgentSession>.Assembly.GetType ("Grace.SDK.Storage", throwOnError = false)
        Assert.That(storageType, Is.Not.Null, "Grace.SDK.Storage should be available as an SDK module.")

        let methodInfo: MethodInfo = storageType.GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Static)

        Assert.That(methodInfo, Is.Not.Null, $"Grace.SDK.Storage.{methodName} should be a public SDK method.")
        let parameters: ParameterInfo array = methodInfo.GetParameters()
        Assert.That(parameters, Has.Length.EqualTo(1))
        Assert.That(parameters[0].ParameterType, Is.EqualTo(parameterType))

    [<Test>]
    member _.ContentBlockSasParametersExposeAddressWithoutCallerSuppliedPath() =
        assertContentBlockParameterShape "GetContentBlockUploadUriParameters"
        assertContentBlockParameterShape "GetContentBlockDownloadUriParameters"

    [<Test>]
    member _.ContentBlockSasSdkMethodsMatchSharedParameterContracts() =
        assertSdkMethod "GetContentBlockUploadUri" "GetContentBlockUploadUriParameters"
        assertSdkMethod "GetContentBlockDownloadUri" "GetContentBlockDownloadUriParameters"

    [<Test>]
    member _.ContentBlockDiscoverySdkMethodMatchesSharedParameterContract() =
        let parameterType = getStorageParameterType "DiscoverContentBlocksParameters"
        Assert.That(parameterType, Is.Not.Null)
        Assert.That(parameterType.IsSubclassOf(typeof<Parameters.Storage.StorageParameters>), Is.True)
        Assert.That(parameterType.GetProperty("KeyChunkAddresses"), Is.Not.Null)
        Assert.That(parameterType.GetProperty("ContentBlockAddress"), Is.Null)
        assertSdkMethod "DiscoverContentBlocks" "DiscoverContentBlocksParameters"
