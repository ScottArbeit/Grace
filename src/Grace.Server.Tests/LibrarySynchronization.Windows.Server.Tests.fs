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
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Builder
open System.Linq
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

    /// Forwards real SDK requests through Kestrel with the fixture principal and bounded failure injection.
    type private AuthenticatedProxy(serverBaseAddress: string, principalId: string) =
        let client = new HttpClient()
        let requestTrace = ConcurrentQueue<string>()
        let mutable dropNextAcceptedSubmit = 0
        let mutable droppedAcceptedSubmitCount = 0
        let mutable submitRequestCount = 0
        let mutable manifestUploadCount = 0
        let manifestCallbackLock = obj ()
        let mutable afterNextManifestUpload: (unit -> unit) option = None
        let mutable afterNextContentRead: (unit -> unit) option = None
        let mutable gapPage: byte array option = None
        let mutable injectGap = 0
        let pageTokens = ConcurrentQueue<string>()
        let gapToken = "fixture-opaque-visibility-gap"

        let port =
            use probe = new TcpListener(IPAddress.Loopback, 0)
            probe.Start()
            (probe.LocalEndpoint :?> IPEndPoint).Port

        let prefix = $"http://127.0.0.1:{port}"
        let builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder()
        do builder.WebHost.UseUrls(prefix) |> ignore
        let app = builder.Build()

        /// Keeps signed read paths unchanged while replacing only test authentication and selected responses.
        let forwardAsync (context: Microsoft.AspNetCore.Http.HttpContext) =
            task {
                let relative =
                    context.Request.Path.ToString()
                    + context.Request.QueryString.ToString()

                let tracePath =
                    if context.Request.Method = "GET" then
                        "/libraries/content/<signed>"
                    else
                        context.Request.Path.Value

                requestTrace.Enqueue($"{DateTime.UtcNow:O} start {context.Request.Method} {tracePath}")
                use forward = new HttpRequestMessage(HttpMethod(context.Request.Method), Uri(Uri(serverBaseAddress), relative))

                if context.Request.Method <> "GET"
                   && context.Request.Method <> "HEAD" then
                    use buffer = new MemoryStream()
                    do! context.Request.Body.CopyToAsync(buffer)
                    forward.Content <- new ByteArrayContent(buffer.ToArray())

                for header in context.Request.Headers do
                    if
                        not (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                        && not (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        && not (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    then
                        if
                            not (forward.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                            && not (isNull forward.Content)
                        then
                            forward.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())
                            |> ignore

                forward.Headers.Remove("x-grace-user-id")
                |> ignore

                forward.Headers.TryAddWithoutValidation("x-grace-user-id", principalId)
                |> ignore

                let path = context.Request.Path.Value

                if path = "/libraries/changes/submit" then
                    Interlocked.Increment(&submitRequestCount)
                    |> ignore

                let mutable requestedCursor = ""
                let mutable replayGap = false

                if path = "/libraries/changes/get" then
                    let! requestJson = forward.Content.ReadAsStringAsync()
                    let request = deserialize<Parameters.Library.GetLibraryChangesParameters> requestJson
                    requestedCursor <- request.AfterCursor
                    pageTokens.Enqueue(if isNull request.PageToken then "<none>" else request.PageToken)
                    replayGap <- request.PageToken = gapToken
                    if replayGap then request.PageToken <- null
                    if Volatile.Read(&injectGap) = 1 then request.PageSize <- 2
                    forward.Content <- createJsonContent request

                use! response = client.SendAsync(forward)
                requestTrace.Enqueue($"{DateTime.UtcNow:O} response {int response.StatusCode} {tracePath}")
                let! receivedBytes = response.Content.ReadAsByteArrayAsync()
                let mutable bytes = receivedBytes

                if response.IsSuccessStatusCode
                   && path = "/libraries/changes/get" then
                    if replayGap then
                        bytes <- gapPage.Value
                        gapPage <- None
                    elif Interlocked.CompareExchange(&injectGap, 0, 1) = 1 then
                        let envelope = deserialize<GraceReturnValue<LibraryChangePageDto>> (System.Text.Encoding.UTF8.GetString(bytes))
                        Assert.That(envelope.ReturnValue.Changes.Length, Is.EqualTo(2))
                        gapPage <- Some bytes
                        let empty = { envelope.ReturnValue with Changes = [||]; LastCursor = requestedCursor; HasMore = true; NextPageToken = Some gapToken }
                        bytes <- System.Text.Encoding.UTF8.GetBytes(serialize { envelope with ReturnValue = empty })

                if response.IsSuccessStatusCode
                   && path = "/libraries/content/read" then
                    let callback =
                        lock manifestCallbackLock (fun () ->
                            let next = afterNextContentRead
                            afterNextContentRead <- None
                            next)

                    callback |> Option.iter (fun action -> action ())

                if response.IsSuccessStatusCode
                   && path = "/storage/finalizeManifestUpload" then
                    Interlocked.Increment(&manifestUploadCount)
                    |> ignore

                    let callback =
                        lock manifestCallbackLock (fun () ->
                            let next = afterNextManifestUpload in
                            afterNextManifestUpload <- None
                            next)

                    callback |> Option.iter (fun action -> action ())

                if response.IsSuccessStatusCode
                   && path = "/libraries/changes/submit"
                   && Interlocked.CompareExchange(&dropNextAcceptedSubmit, 0, 1) = 1 then
                    Interlocked.Increment(&droppedAcceptedSubmitCount)
                    |> ignore

                    context.Response.StatusCode <- 502
                else
                    context.Response.StatusCode <- int response.StatusCode

                    for header in response.Headers do
                        if not (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) then
                            context.Response.Headers[ header.Key ] <- Microsoft.Extensions.Primitives.StringValues(header.Value |> Seq.toArray)

                    for header in response.Content.Headers do
                        if not (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) then
                            context.Response.Headers[ header.Key ] <- Microsoft.Extensions.Primitives.StringValues(header.Value |> Seq.toArray)

                    context.Response.ContentLength <- Nullable(int64 bytes.Length)
                    do! context.Response.Body.WriteAsync(bytes)

                TestContext.Progress.WriteLine($"Library proxy {context.Request.Method} response {context.Response.StatusCode}")
            }

        do
            app.Run(Microsoft.AspNetCore.Http.RequestDelegate(fun context -> forwardAsync context :> Task))
            app.StartAsync().GetAwaiter().GetResult()

        /// Supplies the isolated CLI process endpoint.
        member _.BaseAddress = prefix

        /// Drops one real accepted response without changing the server operation.
        member _.DropNextAcceptedSubmitResponse() =
            Interlocked.Exchange(&dropNextAcceptedSubmit, 1)
            |> ignore

        /// Counts the deliberately lost accepted response.
        member _.DroppedAcceptedSubmitCount = Volatile.Read(&droppedAcceptedSubmitCount)
        /// Captures a saved edit while its earlier immutable source is uploading.
        member _.AfterNextManifestUpload(action) = lock manifestCallbackLock (fun () -> afterNextManifestUpload <- Some action)
        /// Saves a local edit after the server has prepared a real exact-revision content read.
        member _.AfterNextContentRead(action) = lock manifestCallbackLock (fun () -> afterNextContentRead <- Some action)
        /// Simulates one empty visibility page while retaining its real two-change response behind an opaque fixture continuation.
        member _.InjectEmptyVisibilityPage() = Interlocked.Exchange(&injectGap, 1) |> ignore
        /// Exposes received continuation values for restart assertions, without interpreting server cursors.
        member _.PageTokens = pageTokens.ToArray()
        /// Counts actual submissions, including idempotent retries.
        member _.SubmitRequestCount = Volatile.Read(&submitRequestCount)
        /// Counts finalized source uploads.
        member _.ManifestUploadCount = Volatile.Read(&manifestUploadCount)

        interface IDisposable with
            member _.Dispose() =
                requestTrace.ToArray()
                |> Array.iter Console.WriteLine

                app.StopAsync().GetAwaiter().GetResult()

                app
                    .DisposeAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()

                client.Dispose()

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

    /// Occupies one conflict candidate through the public namespace contract before the stale edit submits.
    let private occupyDirectoryAsync (repositoryId: Guid) catalogVersion parent name =
        task {
            let slotRequest = Parameters.Library.GetLibraryNamespaceSlotParameters()
            slotRequest.OwnerId <- ownerId
            slotRequest.OrganizationId <- organizationId
            slotRequest.RepositoryId <- repositoryId.ToString("D")
            slotRequest.Parent <- Some parent
            slotRequest.Name <- name
            slotRequest.CorrelationId <- generateCorrelationId ()
            use! slotResponse = Client.PostAsync("/libraries/namespace/get-slot", createJsonContent slotRequest)
            let! slot = requireReturnValueAsync<LibraryNamespaceSlotDto> slotResponse
            let create = Parameters.Library.SubmitLibraryChangeParameters()
            create.OwnerId <- ownerId
            create.OrganizationId <- organizationId
            create.RepositoryId <- repositoryId.ToString("D")
            create.OperationId <- Guid.NewGuid()
            create.LibraryCatalogVersion <- catalogVersion
            create.ChangeKind <- ChangeKind.CreateDirectory
            create.ItemKind <- ItemKind.Directory
            create.CreationSlotExpectation <- Some { Parent = parent; Name = name; ExpectedSlotVersion = slot.SlotVersion; ExpectedState = "vacant" }
            create.CorrelationId <- generateCorrelationId ()
            use! response = Client.PostAsync("/libraries/changes/submit", createJsonContent create)
            let! _ = requireReturnValueAsync<LibraryOperationReceiptDto> response
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

    /// Executes a fixture-only SQLite assertion or failure trigger against one disposable copy.
    let private localSql root sql =
        let path = Path.Combine(root, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)
        use connection = new SqliteConnection($"Data Source={path};Pooling=False")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteScalar()

    /// Supplies the public local synchronization verb with machine-readable output.
    let private syncCommand verb =
        [|
            "library"
            "sync"
            verb
            "--output"
            "Json"
        |]

    /// Locates the existing shared local database for assertions against a disposable copy.
    let private localDb root = Path.Combine(root, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)

    /// Proves empty-page restart and a subsequent interruption between two item completions do not skip accepted changes.
    [<Test>]
    let ``empty visibility continuation and partial page restart preserve every change`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let root = Path.Combine(Path.GetTempPath(), $"grace-library-page-{Guid.NewGuid():N}")
            Console.WriteLine($"Page-restart working copies: {root}")
            let copyA, copyB = Path.Combine(root, "A"), Path.Combine(root, "B")
            let! repositoryId = createRepositoryAsync ()
            do! addLibraryAsync repositoryId
            use proxy = new AuthenticatedProxy(graceServerBaseAddress, testUserId)
            configureWorkingCopy copyA repositoryId proxy.BaseAddress
            configureWorkingCopy copyB repositoryId proxy.BaseAddress
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "enable")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "enable")

            for name in [ "one"; "two"; "three" ] do
                File.WriteAllText(Path.Combine(copyA, "Library", name), $"exact {name} bytes")

            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")

            let original =
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value

            proxy.InjectEmptyVisibilityPage()
            let! emptyOutput = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            use emptyStatus = System.Text.Json.JsonDocument.Parse(emptyOutput)

            Assert.That(
                emptyStatus
                    .RootElement
                    .GetProperty("ReturnValue")
                    .GetProperty("State")
                    .GetString(),
                Is.EqualTo("catchingUp")
            )

            let gap =
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value

            Assert.That(gap.AppliedCursor, Is.EqualTo(original.AppliedCursor))
            Assert.That(gap.NextPageToken, Is.EqualTo(Some "fixture-opaque-visibility-gap"))

            Assert.That(
                (Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId)
                    .Length,
                Is.Zero
            )

            localSql
                copyB
                "CREATE TRIGGER fail_second_library_completion BEFORE UPDATE OF applied_cursor ON library_repository_state WHEN (SELECT COUNT(*) FROM library_items)>1 BEGIN SELECT RAISE(ABORT,'second item completion interrupted'); END;"
            |> ignore

            let! interrupted = runGraceAsync copyB proxy.BaseAddress (syncCommand "run")
            Assert.That(interrupted.ExitCode, Is.Not.EqualTo(0))

            let partial =
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value

            let completed = Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId
            Assert.That(completed.Length, Is.EqualTo(1))
            Assert.That(partial.AppliedCursor, Is.EqualTo(completed[0].LastChangeCursor))
            Assert.That(partial.NextPageToken, Is.EqualTo(None))

            Assert.That(
                proxy.PageTokens
                |> Array.contains "fixture-opaque-visibility-gap",
                Is.True
            )

            localSql copyB "DROP TRIGGER fail_second_library_completion;"
            |> ignore

            let! output = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            use status = System.Text.Json.JsonDocument.Parse(output)

            Assert.That(
                status
                    .RootElement
                    .GetProperty("ReturnValue")
                    .GetProperty("State")
                    .GetString(),
                Is.EqualTo("current")
            )

            Assert.That(proxy.PageTokens |> Array.last, Is.EqualTo("<none>"))

            for name in [ "one"; "two"; "three" ] do
                Assert.That(File.ReadAllText(Path.Combine(copyB, "Library", name)), Is.EqualTo($"exact {name} bytes"))

            Assert.That(
                (Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId)
                    .Length,
                Is.EqualTo(3)
            )

            let operations = Grace.CLI.LibraryLocalState.readOperations (localDb copyB) repositoryId
            Assert.That(operations.Length, Is.EqualTo(3))

            Assert.That(
                operations
                |> Array.forall (fun operation -> operation.Terminal),
                Is.True
            )

            Assert.That(countWduCompletions copyB, Is.Zero)
        }

    /// Proves an incoming directory rename recovers after its real Windows move without inventing child edits or revisions.
    [<Test>]
    let ``directory move survives SQLite interruption with unchanged child revision`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let root = Path.Combine(Path.GetTempPath(), $"grace-library-directory-{Guid.NewGuid():N}")
            Console.WriteLine($"Directory-restart working copies: {root}")
            let copyA, copyB = Path.Combine(root, "A"), Path.Combine(root, "B")
            let! repositoryId = createRepositoryAsync ()
            do! addLibraryAsync repositoryId
            use proxy = new AuthenticatedProxy(graceServerBaseAddress, testUserId)
            configureWorkingCopy copyA repositoryId proxy.BaseAddress
            configureWorkingCopy copyB repositoryId proxy.BaseAddress
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "enable")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "enable")

            Directory.CreateDirectory(Path.Combine(copyA, "Library", "before"))
            |> ignore

            File.WriteAllText(Path.Combine(copyA, "Library", "before", "child.txt"), "child original bytes")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let items = Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId

            let directory =
                items
                |> Array.find (fun item -> item.ItemKind = ItemKind.Directory)

            let child =
                items
                |> Array.find (fun item -> item.ItemKind = ItemKind.File)

            let catalog =
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value
                    .Catalog

            let rename = Parameters.Library.SubmitLibraryChangeParameters()
            rename.OwnerId <- ownerId
            rename.OrganizationId <- organizationId
            rename.RepositoryId <- repositoryId.ToString("D")
            rename.OperationId <- Guid.NewGuid()
            rename.LibraryCatalogVersion <- catalog.Version
            rename.ChangeKind <- ChangeKind.Rename
            rename.ItemKind <- ItemKind.Directory
            rename.ItemId <- Nullable directory.ItemId
            rename.NamespacePrecondition <- Some { ItemId = directory.ItemId; ExpectedNamespaceVersion = directory.Namespace.Value.NamespaceVersion }
            rename.DestinationName <- "after"
            rename.CorrelationId <- generateCorrelationId ()
            use! response = Client.PostAsync("/libraries/changes/submit", createJsonContent rename)
            let! _ = requireReturnValueAsync<LibraryOperationReceiptDto> response

            localSql
                copyB
                "CREATE TRIGGER fail_directory_completion BEFORE UPDATE OF applied_cursor ON library_repository_state BEGIN SELECT RAISE(ABORT,'directory completion interrupted'); END;"
            |> ignore

            let! interrupted = runGraceAsync copyB proxy.BaseAddress (syncCommand "run")
            Assert.That(interrupted.ExitCode, Is.Not.EqualTo(0))
            let moved = Path.Combine(copyB, "Library", "after", "child.txt")
            Assert.That(Directory.Exists(Path.Combine(copyB, "Library", "before")), Is.False)
            Assert.That(File.ReadAllText(moved), Is.EqualTo("child original bytes"))
            let written = File.GetLastWriteTimeUtc(moved)

            localSql copyB "DROP TRIGGER fail_directory_completion;"
            |> ignore

            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")

            let afterChild =
                Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId
                |> Array.find (fun item -> item.ItemId = child.ItemId)

            Assert.That(afterChild.ContentRevision, Is.EqualTo(child.ContentRevision))
            Assert.That(File.GetLastWriteTimeUtc(moved), Is.EqualTo(written))
            Assert.That(Convert.ToInt32(localSql copyB "SELECT COUNT(*) FROM library_operations WHERE direction='local';"), Is.Zero)
            File.WriteAllText(moved, "edited through renamed parent")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            Assert.That(File.ReadAllText(Path.Combine(copyA, "Library", "after", "child.txt")), Is.EqualTo("edited through renamed parent"))
            Assert.That(countWduCompletions copyB, Is.Zero)
        }

    /// Preserves a save made after remote read preparation and retries it against its original materialized content revision.
    [<Test>]
    let ``save during remote preparation survives restart as a real stale edit`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let root = Path.Combine(Path.GetTempPath(), $"grace-library-read-save-{Guid.NewGuid():N}")
            Console.WriteLine($"Remote-read save working copies: {root}")
            let copyA, copyB = Path.Combine(root, "A"), Path.Combine(root, "B")
            let! repositoryId = createRepositoryAsync ()
            do! addLibraryAsync repositoryId
            use proxy = new AuthenticatedProxy(graceServerBaseAddress, testUserId)
            configureWorkingCopy copyA repositoryId proxy.BaseAddress
            configureWorkingCopy copyB repositoryId proxy.BaseAddress
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "enable")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "enable")
            let pathA, pathB = Path.Combine(copyA, "Library", "saved.txt"), Path.Combine(copyB, "Library", "saved.txt")
            File.WriteAllText(pathA, "original materialized source")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            let before =
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value

            let original = (Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId).[0]
            File.WriteAllText(pathA, "remote accepted source")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            proxy.AfterNextContentRead(fun () -> File.WriteAllText(pathB, "save after remote preparation"))
            let! interrupted = runGraceAsync copyB proxy.BaseAddress (syncCommand "run")
            Assert.That(interrupted.ExitCode, Is.Not.EqualTo(0))
            Assert.That(File.ReadAllText(pathB), Is.EqualTo("save after remote preparation"))

            Assert.That(
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value
                    .AppliedCursor,
                Is.EqualTo(before.AppliedCursor)
            )

            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")

            for copy in [ copyA; copyB ] do
                Assert.That(File.ReadAllText(Path.Combine(copy, "Library", "saved.txt")), Is.EqualTo("remote accepted source"))

                let conflicts =
                    Directory.GetFiles(Path.Combine(copy, "Library"))
                    |> Array.filter (fun path -> Path.GetFileName(path) <> "saved.txt")

                Assert.That(conflicts.Length, Is.EqualTo(1))
                Assert.That(File.ReadAllText(conflicts[0]), Is.EqualTo("save after remote preparation"))

            let saved =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyB) repositoryId
                |> Array.find (fun operation -> operation.Direction = "local")

            let request = deserialize<Parameters.Library.SubmitLibraryChangeParameters> saved.RequestJson.Value
            Assert.That(request.ContentPrecondition.Value.ExpectedContentRevision, Is.EqualTo(original.ContentRevision.Value))
            Assert.That(saved.Terminal, Is.True)
        }

    /// Proves real create, upload, accepted replay, pull, completion, edit and fresh-process restart across two Windows copies.
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
            TestContext.Progress.WriteLine($"Library tracer working copies: {root}")
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

            let firstBytes =
                [|
                    0uy
                    1uy
                    2uy
                    127uy
                    128uy
                    254uy
                    255uy
                |]

            File.WriteAllBytes(pathA, firstBytes)
            proxy.DropNextAcceptedSubmitResponse()
            let! lost = runGraceAsync copyA proxy.BaseAddress (command "run")
            Assert.That(lost.ExitCode, Is.Not.EqualTo(0), lost.StandardOutput + lost.StandardError)
            Assert.That(proxy.DroppedAcceptedSubmitCount, Is.EqualTo(1))
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (command "run")
            let beforeCursor = localSql copyB "SELECT applied_cursor FROM library_repository_state;" :?> string

            localSql
                copyB
                "CREATE TRIGGER fail_library_completion BEFORE UPDATE OF applied_cursor ON library_repository_state BEGIN SELECT RAISE(ABORT,'injected SQLite completion failure'); END;"
            |> ignore

            let! incompleteB = runGraceAsync copyB proxy.BaseAddress (command "run")

            Assert.That(
                incompleteB.ExitCode,
                Is.Not.EqualTo(0),
                incompleteB.StandardOutput
                + incompleteB.StandardError
            )

            Assert.That(
                File
                    .ReadAllBytes(pathB)
                    .AsSpan()
                    .SequenceEqual(firstBytes),
                Is.True
            )

            let publishedAt = File.GetLastWriteTimeUtc(pathB)
            Assert.That(localSql copyB "SELECT applied_cursor FROM library_repository_state;" :?> string, Is.EqualTo(beforeCursor))
            Assert.That(Convert.ToInt32(localSql copyB "SELECT COUNT(*) FROM library_items;"), Is.Zero)
            Assert.That(Convert.ToInt32(localSql copyB "SELECT COUNT(*) FROM library_operations WHERE terminal=1;"), Is.Zero)

            localSql copyB "DROP TRIGGER fail_library_completion;"
            |> ignore

            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (command "run")
            Assert.That(File.GetLastWriteTimeUtc(pathB), Is.EqualTo(publishedAt))

            Assert.That(
                File
                    .ReadAllBytes(pathB)
                    .AsSpan()
                    .SequenceEqual(firstBytes),
                Is.True
            )

            let editedBytes = [| 255uy; 0uy; 67uy; 19uy; 202uy |]
            File.WriteAllBytes(pathB, editedBytes)
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (command "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (command "run")

            Assert.That(
                File
                    .ReadAllBytes(pathA)
                    .AsSpan()
                    .SequenceEqual(editedBytes),
                Is.True
            )

            let writeA = File.GetLastWriteTimeUtc(pathA)
            let writeB = File.GetLastWriteTimeUtc(pathB)
            let submits = proxy.SubmitRequestCount
            let! restartedA = requireGraceSuccessAsync copyA proxy.BaseAddress (command "run")
            let! restartedB = requireGraceSuccessAsync copyB proxy.BaseAddress (command "run")
            use statusA = System.Text.Json.JsonDocument.Parse(restartedA)
            use statusB = System.Text.Json.JsonDocument.Parse(restartedB)

            Assert.That(
                statusA
                    .RootElement
                    .GetProperty("ReturnValue")
                    .GetProperty("State")
                    .GetString(),
                Is.EqualTo("current")
            )

            Assert.That(
                statusB
                    .RootElement
                    .GetProperty("ReturnValue")
                    .GetProperty("State")
                    .GetString(),
                Is.EqualTo("current")
            )

            Assert.That(File.GetLastWriteTimeUtc(pathA), Is.EqualTo(writeA))
            Assert.That(File.GetLastWriteTimeUtc(pathB), Is.EqualTo(writeB))
            Assert.That(proxy.SubmitRequestCount, Is.EqualTo(submits))
            Assert.That(countWduCompletions copyA, Is.Zero)
            Assert.That(countWduCompletions copyB, Is.Zero)

            for copy in [ copyA; copyB ] do
                use connection =
                    new SqliteConnection($"Data Source={Path.Combine(copy, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)};Mode=ReadOnly")

                connection.Open()
                use command = connection.CreateCommand()
                command.CommandText <- "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'library_%';"
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(3))
                command.CommandText <- "SELECT COUNT(*) FROM library_operations WHERE terminal=0;"
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.Zero)
                command.CommandText <- "SELECT COUNT(*) FROM library_operations WHERE direction='local';"
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(1))
        }

    /// Preserves an initial-create successor across upload/response loss, then proves stale edits and repeated bytes retain their real bases.
    [<Test>]
    let ``saved create successor and stale edits preserve exact causal content`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let root = Path.Combine(Path.GetTempPath(), $"grace-library-saved-edit-{Guid.NewGuid():N}")
            Console.WriteLine($"Saved-edit working copies: {root}")
            let copyA = Path.Combine(root, "A")
            let copyB = Path.Combine(root, "B")
            Directory.CreateDirectory(copyA) |> ignore
            Directory.CreateDirectory(copyB) |> ignore
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
            let pathA = Path.Combine(copyA, "Library", "saved.txt")
            let pathB = Path.Combine(copyB, "Library", "saved.txt")
            File.WriteAllText(pathA, "first create source")
            proxy.AfterNextManifestUpload(fun () -> File.WriteAllText(pathA, "second save during create upload"))
            proxy.DropNextAcceptedSubmitResponse()
            let! lost = runGraceAsync copyA proxy.BaseAddress (command "run")
            Assert.That(lost.ExitCode, Is.Not.EqualTo(0))
            Assert.That(File.ReadAllText(pathA), Is.EqualTo("second save during create upload"))
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (command "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (command "run")
            Assert.That(File.ReadAllText(pathA), Is.EqualTo("second save during create upload"))
            Assert.That(File.ReadAllText(pathB), Is.EqualTo("second save during create upload"))
            let dbA = Path.Combine(copyA, Constants.GraceConfigDirectory, Constants.GraceLocalStateDbFileName)

            let created =
                Grace.CLI.LibraryLocalState.readOperations dbA repositoryId
                |> Array.filter (fun operation -> operation.Direction = "local")

            Assert.That(created.Length, Is.EqualTo(2))
            Assert.That(created[1].OriginatingCreateId, Is.EqualTo(Some created[0].OperationId))
            let successor = deserialize<Parameters.Library.SubmitLibraryChangeParameters> created[1].RequestJson.Value
            Assert.That(successor.ChangeKind, Is.EqualTo(ChangeKind.UpdateContent))
            Assert.That(successor.ItemId.Value, Is.EqualTo(created[0].Accepted.Value.Item.ItemId))

            Assert.That(
                successor.ContentPrecondition.Value.ExpectedContentRevision,
                Is.EqualTo(
                    created[0]
                        .Accepted
                        .Value
                        .Item
                        .ContentRevision
                        .Value
                )
            )

            File.WriteAllText(pathA, "Alice saved against her materialized base")
            File.WriteAllText(pathB, "Bob accepted before Alice uploaded")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (command "run")
            let mutable occupiedName = ""

            proxy.AfterNextManifestUpload (fun () ->
                let saved =
                    Grace.CLI.LibraryLocalState.readOperations dbA repositoryId
                    |> Array.find (fun operation ->
                        operation.Direction = "local"
                        && not operation.Terminal)

                occupiedName <- Grace.Actors.LibraryDecision.conflictName "saved.txt" saved.OperationId 0

                occupyDirectoryAsync repositoryId saved.ExpectedCatalogVersion saved.Parent occupiedName
                |> fun work -> work.GetAwaiter().GetResult())

            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (command "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (command "run")
            Assert.That(File.ReadAllText(pathA), Is.EqualTo("Bob accepted before Alice uploaded"))
            Assert.That(File.ReadAllText(pathB), Is.EqualTo("Bob accepted before Alice uploaded"))

            let conflictsA =
                Directory.GetFiles(Path.Combine(copyA, "Library"))
                |> Array.filter (fun path -> path <> pathA)

            let conflictsB =
                Directory.GetFiles(Path.Combine(copyB, "Library"))
                |> Array.filter (fun path -> path <> pathB)

            Assert.That(conflictsA.Length, Is.EqualTo(1))
            Assert.That(conflictsB.Length, Is.EqualTo(1))
            Assert.That(Directory.Exists(Path.Combine(copyA, "Library", occupiedName)), Is.True)
            Assert.That(Directory.Exists(Path.Combine(copyB, "Library", occupiedName)), Is.True)
            Assert.That(Path.GetFileName(conflictsA[0]), Is.Not.EqualTo(occupiedName))
            Assert.That(Path.GetFileName(conflictsA[0]), Is.EqualTo(Path.GetFileName(conflictsB[0])))
            Assert.That(File.ReadAllText(conflictsA[0]), Is.EqualTo("Alice saved against her materialized base"))
            Assert.That(File.ReadAllText(conflictsB[0]), Is.EqualTo("Alice saved against her materialized base"))

            let revisions = ResizeArray<string>()

            for text in [ "X"; "Y"; "X" ] do
                File.WriteAllText(pathA, text)
                let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (command "run")
                let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (command "run")
                Assert.That(File.ReadAllText(pathB), Is.EqualTo(text))

                let item =
                    Grace.CLI.LibraryLocalState.readItems dbA repositoryId
                    |> Array.find (fun item -> item.Namespace.Value.Name = "saved.txt")

                revisions.Add(item.ContentRevision.Value)

            Assert.That(revisions |> Seq.distinct |> Seq.length, Is.EqualTo(3))
            let all = Grace.CLI.LibraryLocalState.readOperations dbA repositoryId

            Assert.That(
                all
                |> Array.filter (fun operation -> not operation.Terminal)
                |> Array.length,
                Is.Zero
            )

            Assert.That(
                all
                |> Array.map (fun operation -> operation.OperationId)
                |> Array.distinct
                |> Array.length,
                Is.EqualTo(all.Length)
            )
        }
