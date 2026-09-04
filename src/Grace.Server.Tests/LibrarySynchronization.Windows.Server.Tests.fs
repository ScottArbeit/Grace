namespace Grace.Server.Tests

open Grace.CLI.Command
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Library
open Microsoft.Data.Sqlite
open NUnit.Framework
open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks

/// Runs the Windows two-working-copy Library tracer against the shared Aspire server.
[<NonParallelizable>]
module LibrarySynchronizationWindowsServerTests =

    /// Captures one external Grace CLI process result without hiding its diagnostics.
    type private ProcessResult = { ExitCode: int; StandardOutput: string; StandardError: string }

    /// Proxies Aspire requests with the shared test principal and one injectable post-acceptance response loss.
    type private AuthenticatedProxy(serverBaseAddress: string, principalId: string) =
        let listener = new HttpListener()
        let client = new HttpClient()
        let cancellation = new CancellationTokenSource()
        let mutable dropNextAcceptedSubmit = 0
        let mutable droppedAcceptedSubmitCount = 0
        let mutable submitRequestCount = 0
        let mutable manifestUploadCount = 0
        let manifestCallbackLock = obj ()
        let mutable afterNextManifestUpload: (unit -> unit) option = None

        /// Maps short HttpListener-safe aliases back to the unchanged signed Library read grants.
        let readGrantTokens = ConcurrentDictionary<string, string>(StringComparer.Ordinal)

        let port =
            use probe = new TcpListener(IPAddress.Loopback, 0)
            probe.Start()
            (probe.LocalEndpoint :?> IPEndPoint).Port

        let prefix = $"http://127.0.0.1:{port}/"

        /// Copies unrestricted HTTP headers while allowing content headers to choose their proper collection.
        let copyRequestHeaders (source: HttpListenerRequest) (target: HttpRequestMessage) =
            for name in source.Headers.AllKeys do
                if
                    not (String.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
                    && not (String.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                then
                    let values = source.Headers.GetValues(name)

                    if
                        not (target.Headers.TryAddWithoutValidation(name, values))
                        && not (isNull target.Content)
                    then
                        target.Content.Headers.TryAddWithoutValidation(name, values)
                        |> ignore

            target.Headers.Remove("x-grace-user-id") |> ignore

            target.Headers.TryAddWithoutValidation("x-grace-user-id", principalId)
            |> ignore

        /// Forwards one request and optionally replaces the first accepted submit response with a transport failure.
        let forwardAsync (context: HttpListenerContext) =
            task {
                try
                    let requestedRelative = context.Request.RawUrl.TrimStart('/')

                    let relative =
                        if
                            context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase)
                            && context.Request.Url.AbsolutePath.StartsWith("/libraries/content/", StringComparison.OrdinalIgnoreCase)
                        then
                            let alias = context.Request.Url.AbsolutePath.Substring("/libraries/content/".Length)

                            match readGrantTokens.TryRemove(alias) with
                            | true, token -> $"libraries/content/{Uri.EscapeDataString token}"
                            | false, _ -> requestedRelative
                        else
                            requestedRelative

                    let targetUri = Uri(Uri(serverBaseAddress.TrimEnd('/') + "/"), relative)
                    TestContext.Progress.WriteLine($"Library tracer proxy forwarding {context.Request.HttpMethod} /{relative}.")
                    use forward = new HttpRequestMessage(HttpMethod(context.Request.HttpMethod), targetUri)

                    if context.Request.HasEntityBody then
                        use buffer = new MemoryStream()
                        do! context.Request.InputStream.CopyToAsync(buffer)
                        forward.Content <- new ByteArrayContent(buffer.ToArray())

                    copyRequestHeaders context.Request forward

                    if context.Request.Url.AbsolutePath.Equals("/libraries/changes/submit", StringComparison.OrdinalIgnoreCase) then
                        Interlocked.Increment(&submitRequestCount) |> ignore

                    use! response = client.SendAsync(forward, HttpCompletionOption.ResponseContentRead, cancellation.Token)
                    let! originalResponseBytes = response.Content.ReadAsByteArrayAsync(cancellation.Token)

                    if
                        response.IsSuccessStatusCode
                        && context.Request.Url.AbsolutePath.Equals("/storage/finalizeManifestUpload", StringComparison.OrdinalIgnoreCase)
                    then
                        Interlocked.Increment(&manifestUploadCount) |> ignore

                        let callback =
                            lock manifestCallbackLock (fun () ->
                                let callback = afterNextManifestUpload
                                afterNextManifestUpload <- None
                                callback)

                        callback |> Option.iter (fun action -> action ())

                    let responseBytes =
                        if
                            response.IsSuccessStatusCode
                            && context.Request.Url.AbsolutePath.Equals("/libraries/content/read", StringComparison.OrdinalIgnoreCase)
                        then
                            let envelope =
                                originalResponseBytes
                                |> Text.Encoding.UTF8.GetString
                                |> deserialize<GraceReturnValue<LibraryContentReadGrantDto>>

                            let alias = Guid.NewGuid().ToString("N")
                            readGrantTokens[alias] <- envelope.ReturnValue.GrantId

                            { envelope.ReturnValue with GrantId = alias; DownloadPath = $"/libraries/content/{alias}" }
                            |> fun grant -> GraceReturnValue.Create grant envelope.CorrelationId
                            |> serialize
                            |> Text.Encoding.UTF8.GetBytes
                        else
                            originalResponseBytes

                    let shouldDrop =
                        response.IsSuccessStatusCode
                        && context.Request.Url.AbsolutePath.Equals("/libraries/changes/submit", StringComparison.OrdinalIgnoreCase)
                        && Interlocked.CompareExchange(&dropNextAcceptedSubmit, 0, 1) = 1

                    if shouldDrop then
                        Interlocked.Increment(&droppedAcceptedSubmitCount)
                        |> ignore

                        TestContext.Progress.WriteLine("Library tracer proxy replaced one accepted submit response with 502.")
                        context.Response.StatusCode <- int HttpStatusCode.BadGateway
                        context.Response.ContentLength64 <- 0L
                        context.Response.Close()
                    else
                        context.Response.StatusCode <- int response.StatusCode

                        for header in response.Headers do
                            if
                                not (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                                && not (header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                                && not (header.Key.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
                            then
                                context.Response.Headers[ header.Key ] <- String.Join(",", header.Value)

                        for header in response.Content.Headers do
                            if not (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) then
                                context.Response.Headers[ header.Key ] <- String.Join(",", header.Value)

                        context.Response.ContentLength64 <- int64 responseBytes.Length
                        do! context.Response.OutputStream.WriteAsync(responseBytes, cancellation.Token)
                        context.Response.Close()
                        TestContext.Progress.WriteLine($"Library tracer proxy returned {(int response.StatusCode)} for /{relative}.")
                with
                | :? OperationCanceledException -> ()
                | :? ObjectDisposedException -> ()
                | ex ->
                    TestContext.Progress.WriteLine($"Library tracer proxy failed: {ex}")

                    try
                        context.Response.StatusCode <- int HttpStatusCode.BadGateway
                        let bytes = Text.Encoding.UTF8.GetBytes(ex.Message)
                        context.Response.ContentLength64 <- int64 bytes.Length
                        do! context.Response.OutputStream.WriteAsync(bytes)
                        context.Response.Close()
                    with
                    | _ -> context.Response.Abort()
            }

        /// Accepts forwarded requests until the fixture disposes its bounded loopback proxy.
        let rec acceptLoopAsync () =
            task {
                try
                    let! context = listener.GetContextAsync()
                    do! forwardAsync context
                    return! acceptLoopAsync ()
                with
                | :? HttpListenerException when cancellation.IsCancellationRequested -> ()
                | :? ObjectDisposedException when cancellation.IsCancellationRequested -> ()
                | ex ->
                    TestContext.Progress.WriteLine($"Library tracer proxy accept loop recovered: {ex}")
                    return! acceptLoopAsync ()
            }

        do
            listener.Prefixes.Add(prefix)
            listener.Start()

            Task.Run(Func<Task>(fun () -> acceptLoopAsync () :> Task))
            |> ignore

        /// Gets the loopback URI used by isolated CLI processes.
        member _.BaseAddress = prefix.TrimEnd('/')

        /// Drops only the next successfully accepted Library submit response after the server returns it.
        member _.DropNextAcceptedSubmitResponse() =
            Interlocked.Exchange(&dropNextAcceptedSubmit, 1)
            |> ignore

        /// Reports accepted submit responses deliberately hidden from the publishing CLI process.
        member _.DroppedAcceptedSubmitCount = Volatile.Read(&droppedAcceptedSubmitCount)

        /// Mutates local test state after the next successful manifest upload but before the CLI receives that response.
        member _.AfterNextManifestUpload(action) = lock manifestCallbackLock (fun () -> afterNextManifestUpload <- Some action)

        /// Reports Library submit requests forwarded to the accepted server route.
        member _.SubmitRequestCount = Volatile.Read(&submitRequestCount)

        /// Reports successful manifest uploads observed at the post-upload pre-submit boundary.
        member _.ManifestUploadCount = Volatile.Read(&manifestUploadCount)

        interface IDisposable with
            member _.Dispose() =
                cancellation.Cancel()
                listener.Close()
                client.Dispose()
                cancellation.Dispose()

    /// Requires one HTTP response to carry the expected typed Grace envelope.
    let private requireReturnValueAsync<'T> (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)

            return
                (deserialize<GraceReturnValue<'T>> body)
                    .ReturnValue
        }

    /// Creates an isolated repository and grants the shared Aspire principal repository administration.
    let private createRepositoryAsync () =
        task {
            let repositoryId = Guid.NewGuid()
            let create = Parameters.Repository.CreateRepositoryParameters()
            create.OwnerId <- ownerId
            create.OrganizationId <- organizationId
            create.RepositoryId <- repositoryId.ToString("D")
            create.RepositoryName <- $"LibraryTracer{repositoryId:N}"
            create.CorrelationId <- generateCorrelationId ()
            use! createResponse = Client.PostAsync("/repository/create", createJsonContent create)
            let! _ = requireReturnValueAsync<string> createResponse

            let grant = Parameters.Access.GrantRoleParameters()
            grant.OwnerId <- ownerId
            grant.OrganizationId <- organizationId
            grant.RepositoryId <- repositoryId.ToString("D")
            grant.PrincipalType <- "User"
            grant.PrincipalId <- testUserId
            grant.ScopeKind <- "repo"
            grant.RoleId <- "RepositoryAdmin"
            grant.Source <- "test"
            grant.CorrelationId <- generateCorrelationId ()
            use! grantResponse = Client.PostAsync("/authorize/grant-role", createJsonContent grant)
            let! grantBody = grantResponse.Content.ReadAsStringAsync()
            Assert.That(grantResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), grantBody)
            return repositoryId
        }

    /// Adds the initially empty Library root using the unchanged public catalog contract.
    let private addLibraryAsync (repositoryId: Guid) =
        task {
            let catalog = Parameters.Library.GetLibraryCatalogParameters()
            catalog.OwnerId <- ownerId
            catalog.OrganizationId <- organizationId
            catalog.RepositoryId <- repositoryId.ToString("D")
            catalog.CorrelationId <- generateCorrelationId ()
            use! catalogResponse = Client.PostAsync("/libraries/catalog/get", createJsonContent catalog)
            let! current = requireReturnValueAsync<LibraryCatalogDto> catalogResponse

            let add = Parameters.Library.AddLibraryParameters()
            add.OwnerId <- ownerId
            add.OrganizationId <- organizationId
            add.RepositoryId <- repositoryId.ToString("D")
            add.ExpectedVersion <- current.Version
            add.LibraryPath <- "Library"
            add.OperationId <- Guid.NewGuid()
            add.CorrelationId <- generateCorrelationId ()
            use! addResponse = Client.PostAsync("/libraries/add", createJsonContent add)
            let! _ = requireReturnValueAsync<LibraryCatalogChangeResultDto> addResponse
            ()
        }

    /// Writes the minimal repository configuration consumed by a fresh Grace CLI process.
    let private configureWorkingCopy root repositoryId serverUri =
        let graceDirectory = Directory.CreateDirectory(Path.Combine(root, Constants.GraceConfigDirectory))

        Directory.CreateDirectory(Path.Combine(root, "Library"))
        |> ignore

        let configuration = GraceConfiguration()
        configuration.OwnerId <- Guid.Parse ownerId
        configuration.OrganizationId <- Guid.Parse organizationId
        configuration.RepositoryId <- repositoryId
        configuration.ServerUri <- serverUri
        configuration.ObjectStorageProvider <- ObjectStorageProvider.AzureBlobStorage
        saveConfigFile (Path.Combine(graceDirectory.FullName, Constants.GraceConfigFileName)) configuration

    /// Runs one real CLI process from the selected working copy and preserves bounded failure output.
    let private runGraceAsync workingDirectory serverUri arguments =
        task {
            let cliAssembly =
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Grace.CLI", "bin", "Release", "net10.0", "grace.dll"))

            let startInfo = ProcessStartInfo("dotnet")
            startInfo.WorkingDirectory <- workingDirectory
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.UseShellExecute <- false

            startInfo.Environment[
                Constants.EnvironmentVariables.GraceServerUri
            ] <- serverUri

            startInfo.ArgumentList.Add(cliAssembly)

            for argument in arguments do
                startInfo.ArgumentList.Add(argument)

            use cliProcess = new Process(StartInfo = startInfo)

            if not (cliProcess.Start()) then invalidOp "Grace CLI process did not start."

            let standardOutput = cliProcess.StandardOutput.ReadToEndAsync()
            let standardError = cliProcess.StandardError.ReadToEndAsync()
            use timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2.0))

            try
                do! cliProcess.WaitForExitAsync(timeout.Token)
            with
            | :? OperationCanceledException ->
                cliProcess.Kill(entireProcessTree = true)
                invalidOp $"Grace CLI timed out: {String.Join(' ', arguments)}"

            let! output = standardOutput
            let! error = standardError
            return { ExitCode = cliProcess.ExitCode; StandardOutput = output; StandardError = error }
        }

    /// Starts one real CLI process with a filesystem-publication marker used to prove kill-and-restart recovery.
    let private startGracePausedAfterFilesystemPublication workingDirectory serverUri arguments markerPath =
        let cliAssembly =
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Grace.CLI", "bin", "Release", "net10.0", "grace.dll"))

        let startInfo = ProcessStartInfo("dotnet")
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false

        startInfo.Environment[
            Constants.EnvironmentVariables.GraceServerUri
        ] <- serverUri

        startInfo.Environment[
            "GRACE_TEST_LIBRARY_FILESYSTEM_PUBLISHED_MARKER"
        ] <- markerPath

        startInfo.ArgumentList.Add(cliAssembly)

        for argument in arguments do
            startInfo.ArgumentList.Add(argument)

        let cliProcess = new Process(StartInfo = startInfo)

        if not (cliProcess.Start()) then
            cliProcess.Dispose()
            invalidOp "Grace CLI process did not start."

        cliProcess

    /// Waits for one externally visible crash marker without hiding a prematurely exited CLI process.
    let private waitForFilesystemPublicationMarkerAsync (cliProcess: Process) markerPath =
        task {
            use timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1.0))

            while not (File.Exists(markerPath)) do
                if cliProcess.HasExited then
                    let! output = cliProcess.StandardOutput.ReadToEndAsync()
                    let! error = cliProcess.StandardError.ReadToEndAsync()

                    invalidOp
                        $"Grace CLI exited before filesystem publication. stdout:{Environment.NewLine}{output}{Environment.NewLine}stderr:{Environment.NewLine}{error}"

                do! Task.Delay(25, timeout.Token)
        }

    /// Requires one successful CLI command and returns its JSON output for state assertions.
    let private requireGraceSuccessAsync workingDirectory serverUri arguments =
        task {
            let! result = runGraceAsync workingDirectory serverUri arguments

            Assert.That(
                result.ExitCode,
                Is.EqualTo(0),
                $"stdout:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}stderr:{Environment.NewLine}{result.StandardError}"
            )

            return result.StandardOutput
        }

    /// Counts durable WDU completions to prove Library synchronization did not create a fourth caller.
    let private countWduCompletions root =
        let path = Path.Combine(root, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)
        use connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT COUNT(*) FROM working_directory_update_completions;"
        Convert.ToInt32(command.ExecuteScalar())

    /// Reads the sole remote Library operation state from an isolated tracer working copy.
    let private readRemoteOperationState root =
        let path = Path.Combine(root, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)
        use connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- "SELECT operation_state FROM library_operations WHERE direction = 'remote';"
        command.ExecuteScalar() :?> string

    /// Reads causal local operation identities for one complete-byte BLAKE3 identity.
    let private readLocalOperationIdsForBlake3 root blake3 =
        let path = Path.Combine(root, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)
        use connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <-
            "SELECT operation_id FROM library_operations WHERE direction = 'local' AND expected_blake3 = $blake3 ORDER BY created_at, operation_id;"

        command.Parameters.AddWithValue("$blake3", blake3) |> ignore
        use reader = command.ExecuteReader()
        let operationIds = ResizeArray<Guid>()

        while reader.Read() do
            operationIds.Add(Guid.Parse(reader.GetString(0)))

        operationIds.ToArray()

    /// Proves accepted response replay, bidirectional exact bytes, and restart-current behavior for two Windows copies.
    [<Test>]
    let ``two Windows copies converge exact Library bytes and restart current`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Issue #1039 executes only on Windows 11.")

            let root = Path.Combine(Path.GetTempPath(), $"grace-library-two-copy-{Guid.NewGuid():N}")
            let copyA = Path.Combine(root, "A")
            let copyB = Path.Combine(root, "B")
            Directory.CreateDirectory(copyA) |> ignore
            Directory.CreateDirectory(copyB) |> ignore

            try
                let! repositoryId = createRepositoryAsync ()
                do! addLibraryAsync repositoryId
                use proxy = new AuthenticatedProxy(graceServerBaseAddress, testUserId)
                configureWorkingCopy copyA repositoryId proxy.BaseAddress
                configureWorkingCopy copyB repositoryId proxy.BaseAddress

                let command verb =
                    [|
                        "library"
                        "sync"
                        verb
                        "--output"
                        "Json"
                    |]

                let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (command "enable")
                let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (command "enable")

                let pathA = Path.Combine(copyA, "Library", "ordinary.txt")
                let pathB = Path.Combine(copyB, "Library", "ordinary.txt")

                let preparedBytes =
                    [|
                        0uy
                        1uy
                        2uy
                        3uy
                        127uy
                        128uy
                        254uy
                        255uy
                    |]

                let firstBytes =
                    [|
                        8uy
                        13uy
                        21uy
                        34uy
                        55uy
                        89uy
                        144uy
                        233uy
                    |]

                File.WriteAllBytes(pathA, preparedBytes)
                let submitCountBeforeMutation = proxy.SubmitRequestCount
                let manifestCountBeforeMutation = proxy.ManifestUploadCount
                proxy.AfterNextManifestUpload(fun () -> File.WriteAllBytes(pathA, firstBytes))
                let! rejectedMutation = runGraceAsync copyA proxy.BaseAddress (command "run")

                Assert.That(
                    rejectedMutation.ExitCode,
                    Is.Not.EqualTo(0),
                    rejectedMutation.StandardOutput
                    + rejectedMutation.StandardError
                )

                Assert.That(proxy.ManifestUploadCount, Is.EqualTo(manifestCountBeforeMutation + 1))
                Assert.That(proxy.SubmitRequestCount, Is.EqualTo(submitCountBeforeMutation))
                Assert.That(File.ReadAllBytes(pathA).AsSpan().SequenceEqual(firstBytes), Is.True)

                proxy.DropNextAcceptedSubmitResponse()
                let! lostResponse = runGraceAsync copyA proxy.BaseAddress (command "run")

                Assert.That(
                    lostResponse.ExitCode,
                    Is.Not.EqualTo(0),
                    lostResponse.StandardOutput
                    + lostResponse.StandardError
                )

                Assert.That(
                    proxy.DroppedAcceptedSubmitCount,
                    Is.EqualTo(1),
                    lostResponse.StandardOutput
                    + lostResponse.StandardError
                )

                let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (command "run")

                let crashMarker = Path.Combine(root, "copy-b-filesystem-published.marker")

                use interruptedCopyB = startGracePausedAfterFilesystemPublication copyB proxy.BaseAddress (command "run") crashMarker

                do! waitForFilesystemPublicationMarkerAsync interruptedCopyB crashMarker

                Assert.That(
                    File
                        .ReadAllBytes(pathB)
                        .AsSpan()
                        .SequenceEqual(firstBytes),
                    Is.True
                )

                let publishedWriteB = File.GetLastWriteTimeUtc(pathB)
                interruptedCopyB.Kill(entireProcessTree = true)
                do! interruptedCopyB.WaitForExitAsync()
                Assert.That(readRemoteOperationState copyB, Is.EqualTo("pendingFilesystem"))

                let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (command "run")
                Assert.That(File.GetLastWriteTimeUtc(pathB), Is.EqualTo(publishedWriteB))

                Assert.That(
                    File
                        .ReadAllBytes(pathB)
                        .AsSpan()
                        .SequenceEqual(firstBytes),
                    Is.True
                )

                let secondBytes =
                    [|
                        255uy
                        21uy
                        34uy
                        55uy
                        89uy
                        144uy
                        0uy
                    |]

                File.WriteAllBytes(pathB, secondBytes)
                let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (command "run")
                let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (command "run")

                Assert.That(
                    File
                        .ReadAllBytes(pathA)
                        .AsSpan()
                        .SequenceEqual(secondBytes),
                    Is.True
                )

                let firstBlake3 = ContentAddress.computeBlake3Hex firstBytes
                let firstOperationIds = readLocalOperationIdsForBlake3 copyA firstBlake3
                Assert.That(firstOperationIds, Has.Length.EqualTo(1))
                let submitCountBeforeReturnToFirstBytes = proxy.SubmitRequestCount
                File.WriteAllBytes(pathA, firstBytes)
                let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (command "run")
                let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (command "run")
                let returnedOperationIds = readLocalOperationIdsForBlake3 copyA firstBlake3
                Assert.That(returnedOperationIds, Has.Length.EqualTo(2))
                Assert.That(returnedOperationIds[0], Is.Not.EqualTo(returnedOperationIds[1]))
                Assert.That(proxy.SubmitRequestCount, Is.EqualTo(submitCountBeforeReturnToFirstBytes + 1))
                Assert.That(File.ReadAllBytes(pathB).AsSpan().SequenceEqual(firstBytes), Is.True)

                let writeA = File.GetLastWriteTimeUtc(pathA)
                let writeB = File.GetLastWriteTimeUtc(pathB)
                let! restartedA = requireGraceSuccessAsync copyA proxy.BaseAddress (command "run")
                let! restartedB = requireGraceSuccessAsync copyB proxy.BaseAddress (command "run")
                Assert.That(restartedA, Does.Contain("current"))
                Assert.That(restartedB, Does.Contain("current"))
                Assert.That(File.GetLastWriteTimeUtc(pathA), Is.EqualTo(writeA))
                Assert.That(File.GetLastWriteTimeUtc(pathB), Is.EqualTo(writeB))
                Assert.That(countWduCompletions copyA, Is.EqualTo(0))
                Assert.That(countWduCompletions copyB, Is.EqualTo(0))
            finally
                SqliteConnection.ClearAllPools()

                if Directory.Exists(root) then Directory.Delete(root, recursive = true)
        }
