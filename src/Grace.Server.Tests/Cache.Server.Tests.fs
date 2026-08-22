namespace Grace.Server.Tests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Parameters.Cache
open Grace.Shared.Utilities
open Grace.Types.Common
open NUnit.Framework

/// Exercises the supported development topology through real Cache, Server, Actor, and Blob boundaries.
[<NonParallelizable>]
[<TestFixture>]
type CacheServerIntegrationTests() =

    /// Encodes public-key coordinates using the contract's unpadded base64url alphabet.
    let encode (bytes: byte array) =
        Convert
            .ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    /// Projects a generated P-256 key into the public JWK preparation contract.
    let publicJwk (key: ECDsa) =
        let parameters = key.ExportParameters(false)
        { Kty = "EC"; Crv = "P-256"; X = encode parameters.Q.X; Y = encode parameters.Q.Y }

    /// Creates a valid P-256 public JWK for preparation-only integration cases.
    let createPublicJwk () =
        use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
        publicJwk key

    /// Creates a Server client for the caller whose access is bound into the permit.
    let createCallerClient (userId: string) =
        let client = new HttpClient(BaseAddress = Client.BaseAddress)
        client.DefaultRequestHeaders.Add("x-grace-user-id", userId)
        client

    /// Grants or revokes the repository reader role through the running Server authorization boundary.
    let changeReaderRole grant userId repositoryId =
        task {
            let! response =
                if grant then
                    let parameters = Parameters.Access.GrantRoleParameters()
                    parameters.OwnerId <- ownerId
                    parameters.OrganizationId <- organizationId
                    parameters.RepositoryId <- repositoryId
                    parameters.PrincipalType <- "User"
                    parameters.PrincipalId <- userId
                    parameters.ScopeKind <- "repo"
                    parameters.RoleId <- "RepositoryReader"
                    parameters.Source <- "cache-integration-test"
                    parameters.CorrelationId <- generateCorrelationId ()
                    Client.PostAsync("/authorize/grant-role", createJsonContent parameters)
                else
                    let parameters = Parameters.Access.RevokeRoleParameters()
                    parameters.OwnerId <- ownerId
                    parameters.OrganizationId <- organizationId
                    parameters.RepositoryId <- repositoryId
                    parameters.PrincipalType <- "User"
                    parameters.PrincipalId <- userId
                    parameters.ScopeKind <- "repo"
                    parameters.RoleId <- "RepositoryReader"
                    parameters.CorrelationId <- generateCorrelationId ()
                    Client.PostAsync("/authorize/revoke-role", createJsonContent parameters)

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), response.Content.ReadAsStringAsync().Result)
        }

    /// Reserves a local TCP port for the disposable Cache process.
    let reservePort () =
        use listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        (listener.LocalEndpoint :?> IPEndPoint).Port

    /// Starts one disposable Cache process pointed at the Aspire-hosted Grace Server.
    let startCache root port =
        let assembly = typeof<Grace.Cache.Program>.Assembly.Location
        let info = ProcessStartInfo("dotnet", $"\"{assembly}\"")
        info.UseShellExecute <- false
        info.CreateNoWindow <- true
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.Environment[ "Cache__DatabasePath" ] <- Path.Combine(root, "cache.db")
        info.Environment[ "Cache__ManagedRoot" ] <- Path.Combine(root, "managed")
        info.Environment[ "GRACE_SERVER_URI" ] <- graceServerBaseAddress
        info.Environment[ "ASPNETCORE_URLS" ] <- $"http://127.0.0.1:{port}"
        Process.Start(info)

    /// Waits until the Cache public-key route is reachable.
    let waitForCache (client: HttpClient) =
        task {
            let mutable ready = false
            let mutable attempts = 0

            while not ready && attempts < 100 do
                attempts <- attempts + 1

                try
                    use! response = client.GetAsync("/fill-public-key")
                    ready <- response.IsSuccessStatusCode
                with
                | :? HttpRequestException -> do! Task.Delay(100)

            if not ready then
                failwith "Grace Cache did not become ready for the cross-service tracer."
        }

    /// Proves stale preparation cannot issue a source, then runs one truthful miss-to-hit through both HTTP services.
    [<Test>]
    member _.``stale access fails before source and fresh permit fills through Cache``() =
        task {
            let repositoryId = repositoryIds[0]
            let directoryVersion = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId Constants.RootDirectoryPath []

            do! DirectoryVersionServerTestHelpers.createDirectoryVersionAsync directoryVersion

            let callerId = string (Guid.NewGuid())
            do! changeReaderRole true callerId repositoryId

            let root = Path.Combine(Path.GetTempPath(), "grace-cache-server-tests", Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(root) |> ignore
            let port = reservePort ()
            use cache = startCache root port
            use cacheClient = new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{port}"))
            use caller = createCallerClient callerId

            try
                do! waitForCache cacheClient
                let artifactPath = $"/repositories/{repositoryId}/directory-version-zips/{directoryVersion.DirectoryVersionId}"
                use! miss = cacheClient.GetAsync(artifactPath)
                Assert.That(miss.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))

                let! publicKey = cacheClient.GetFromJsonAsync<CachePublicJwk>("/fill-public-key", Constants.JsonSerializerOptions)

                let prepare () =
                    task {
                        let parameters = PrepareDirectoryVersionZipParameters()
                        parameters.RepositoryId <- repositoryId
                        parameters.DirectoryVersionId <- string directoryVersion.DirectoryVersionId
                        parameters.CachePublicKey <- publicKey
                        use! response = caller.PostAsync("/cache/prepareDirectoryVersionZip", createJsonContent parameters)
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), response.Content.ReadAsStringAsync().Result)
                        let! result = deserializeContent<GraceReturnValue<DirectoryVersionZipPreparation>> response
                        return result.ReturnValue
                    }

                let! stalePreparation = prepare ()
                do! changeReaderRole false callerId repositoryId
                use! staleFill = cacheClient.PostAsJsonAsync(artifactPath + "/fill", {| Permit = stalePreparation.Permit |}, Constants.JsonSerializerOptions)
                Assert.That(staleFill.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))
                use! stillMissing = cacheClient.GetAsync(artifactPath)
                Assert.That(stillMissing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))

                do! changeReaderRole true callerId repositoryId
                let! freshPreparation = prepare ()
                use! fill = cacheClient.PostAsJsonAsync(artifactPath + "/fill", {| Permit = freshPreparation.Permit |}, Constants.JsonSerializerOptions)
                Assert.That(fill.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), fill.Content.ReadAsStringAsync().Result)
                use! hit = cacheClient.GetAsync(artifactPath)
                Assert.That(hit.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(hit.Content.Headers.ContentType.MediaType, Is.EqualTo("application/zip"))

                use! hitBypassesRedemption = cacheClient.PostAsJsonAsync(artifactPath + "/fill", {| Permit = "invalid-on-hit" |})
                Assert.That(hitBypassesRedemption.StatusCode, Is.EqualTo(HttpStatusCode.NoContent))

                let direct = Parameters.DirectoryVersion.GetZipFileParameters()
                direct.OwnerId <- ownerId
                direct.OrganizationId <- organizationId
                direct.RepositoryId <- repositoryId
                direct.DirectoryVersionId <- string directoryVersion.DirectoryVersionId
                direct.CorrelationId <- generateCorrelationId ()
                use! directResponse = Client.PostAsync("/directory/getZipFile", createJsonContent direct)
                Assert.That(directResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            finally
                if not cache.HasExited then cache.Kill(true)

                cache.WaitForExit()
                Directory.Delete(root, true)
        }

    /// Proves an existing ZIP without its exact descriptor metadata has no compatibility preparation path.
    [<Test>]
    member _.``preparation rejects an existing ZIP without descriptor metadata``() =
        task {
            let repositoryId = repositoryIds[1]

            let directoryVersion = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId Constants.RootDirectoryPath []

            do! DirectoryVersionServerTestHelpers.createDirectoryVersionAsync directoryVersion

            let state =
                HostState
                |> Option.defaultWith (fun () -> failwith "Aspire host state is unavailable.")

            let! container = AspireTestHost.getAzureStorageContainerClientAsync state repositoryId
            do! container.CreateIfNotExistsAsync() :> Task
            let blob = container.GetBlobClient($"{Constants.GraceZipFilesFolderName}/{directoryVersion.DirectoryVersionId}.zip")
            use bytes = new MemoryStream([| 1uy; 2uy; 3uy |], false)
            do! blob.UploadAsync(bytes) :> Task

            let parameters = PrepareDirectoryVersionZipParameters()
            parameters.RepositoryId <- repositoryId
            parameters.DirectoryVersionId <- string directoryVersion.DirectoryVersionId
            parameters.CachePublicKey <- createPublicJwk ()
            use! response = Client.PostAsync("/cache/prepareDirectoryVersionZip", createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Contain("descriptor metadata is unavailable"))
            Assert.That(body, Does.Not.Contain("sig="))
            Assert.That(body, Does.Not.Contain("SharedAccessSignature"))
        }

    /// Proves a repository-owned non-root DirectoryVersion cannot prepare a permit through the shared artifact lookup.
    [<Test>]
    member _.``preparation rejects a repository child directory version``() =
        task {
            let repositoryId = repositoryIds[2]
            let child = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId "/cache-child/" []
            do! DirectoryVersionServerTestHelpers.createDirectoryVersionAsync child

            let parameters = PrepareDirectoryVersionZipParameters()
            parameters.RepositoryId <- repositoryId
            parameters.DirectoryVersionId <- string child.DirectoryVersionId
            parameters.CachePublicKey <- createPublicJwk ()
            use! response = Client.PostAsync("/cache/prepareDirectoryVersionZip", createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), body)
            Assert.That(body, Does.Not.Contain("\"permit\""))
            Assert.That(body, Does.Not.Contain("sourceUri"))
        }

    /// Proves access revoked after descriptor preparation still prevents the prepared SAS from leaving Server.
    [<Test>]
    member _.``redemption rechecks access after descriptor preparation before releasing source``() =
        task {
            let repositoryId = repositoryIds[2]

            let rootDirectory = DirectoryVersionServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId Constants.RootDirectoryPath []

            do! DirectoryVersionServerTestHelpers.createDirectoryVersionAsync rootDirectory
            let callerId = string (Guid.NewGuid())
            do! changeReaderRole true callerId repositoryId
            use caller = createCallerClient callerId
            use key = ECDsa.Create(ECCurve.NamedCurves.nistP256)
            let preparationParameters = PrepareDirectoryVersionZipParameters()
            preparationParameters.RepositoryId <- repositoryId
            preparationParameters.DirectoryVersionId <- string rootDirectory.DirectoryVersionId
            preparationParameters.CachePublicKey <- publicJwk key
            use! preparationResponse = caller.PostAsync("/cache/prepareDirectoryVersionZip", createJsonContent preparationParameters)
            Assert.That(preparationResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            let! preparationEnvelope = deserializeContent<GraceReturnValue<DirectoryVersionZipPreparation>> preparationResponse
            let preparation = preparationEnvelope.ReturnValue

            let signature =
                key.SignData(Encoding.UTF8.GetBytes(preparation.Permit), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                |> encode

            let redemption = RedeemDirectoryVersionZipFillParameters()
            redemption.Permit <- preparation.Permit
            redemption.Signature <- signature
            let gatePort, listener = AspireTestHost.getDescriptionClearPreAppendTestGate ()
            let cacheRoot = Path.Combine(Path.GetTempPath(), "grace-cache-late-revoke-tests", Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(cacheRoot) |> ignore
            let cachePort = reservePort ()
            use cache = startCache cacheRoot cachePort
            use cacheClient = new HttpClient(BaseAddress = Uri($"http://127.0.0.1:{cachePort}"))

            try
                do! waitForCache cacheClient
                let artifactPath = $"/repositories/{repositoryId}/directory-version-zips/{rootDirectory.DirectoryVersionId}"
                use! initialMiss = cacheClient.GetAsync(artifactPath)
                Assert.That(initialMiss.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
                use request = new HttpRequestMessage(HttpMethod.Post, "/cache/redeemDirectoryVersionZipFill")
                request.Headers.Add("X-Grace-Test-Cache-Redemption-Gate-Port", string gatePort)
                request.Content <- createJsonContent redemption
                let responseTask = caller.SendAsync(request)
                use timeout = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(20.0))
                use! serverGate = listener.AcceptTcpClientAsync(timeout.Token)
                use stream = serverGate.GetStream()
                use reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true)
                use writer = new StreamWriter(stream, Encoding.UTF8, 1024, true)
                let! ready = reader.ReadLineAsync(timeout.Token)
                Assert.That(ready, Is.EqualTo("cache-descriptor-ready"))
                do! changeReaderRole false callerId repositoryId
                do! writer.WriteLineAsync("release".AsMemory(), timeout.Token)
                do! writer.FlushAsync(timeout.Token)
                use! response = responseTask
                let! body = response.Content.ReadAsStringAsync()
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), body)
                Assert.That(body, Does.Not.Contain("sourceUri"))
                Assert.That(body, Does.Not.Contain("sig="))
                Assert.That(body, Does.Not.Contain("SharedAccessSignature"))
                use! finalMiss = cacheClient.GetAsync(artifactPath)
                Assert.That(finalMiss.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
            finally
                AspireTestHost.releaseDescriptionClearPreAppendTestGate listener

                if not cache.HasExited then cache.Kill(true)

                cache.WaitForExit()
                Directory.Delete(cacheRoot, true)
        }
