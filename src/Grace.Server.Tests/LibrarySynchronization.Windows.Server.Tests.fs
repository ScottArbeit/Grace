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
        let mutable afterNextBootstrap: (unit -> Task) option = None
        let mutable beforeNextChangesGet: (unit -> unit) option = None
        let mutable beforeNextSubmit: (unit -> Task) option = None
        let mutable singleItemBootstrapPages = false
        let mutable singleChangePages = false
        let mutable injectGap = 0
        let mutable replacementCursor: string option = None
        let mutable lastChangesStatus = 0
        let requestedCursors = ConcurrentQueue<string>()

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

                if singleItemBootstrapPages
                   && (path = "/libraries/bootstrap/start"
                       || path = "/libraries/bootstrap/continue") then
                    let! json = forward.Content.ReadAsStringAsync()

                    if path = "/libraries/bootstrap/start" then
                        let request = deserialize<Parameters.Library.StartLibraryBootstrapParameters> json
                        request.PageSize <- 1
                        forward.Content <- createJsonContent request
                    else
                        let request = deserialize<Parameters.Library.ContinueLibraryBootstrapParameters> json
                        request.PageSize <- 1
                        forward.Content <- createJsonContent request

                if path = "/libraries/changes/submit" then
                    Interlocked.Increment(&submitRequestCount)
                    |> ignore

                    let callback =
                        lock manifestCallbackLock (fun () ->
                            let next = beforeNextSubmit in
                            beforeNextSubmit <- None
                            next)

                    match callback with
                    | Some action -> do! action ()
                    | None -> ()

                let mutable requestedCursor = ""

                if path = "/libraries/changes/get" then
                    let callback =
                        lock manifestCallbackLock (fun () ->
                            let next = beforeNextChangesGet in
                            beforeNextChangesGet <- None
                            next)

                    callback |> Option.iter (fun action -> action ())
                    let! requestJson = forward.Content.ReadAsStringAsync()
                    let request = deserialize<Parameters.Library.GetLibraryChangesParameters> requestJson
                    requestedCursor <- request.AfterCursor
                    requestedCursors.Enqueue request.AfterCursor

                    if singleChangePages then
                        request.PageSize <- 1
                        forward.Content <- createJsonContent request

                    let replacement =
                        lock manifestCallbackLock (fun () ->
                            let next = replacementCursor in
                            replacementCursor <- None
                            next)

                    replacement
                    |> Option.iter (fun cursor -> request.AfterCursor <- cursor)

                    forward.Content <- createJsonContent request

                use! response = client.SendAsync(forward)
                requestTrace.Enqueue($"{DateTime.UtcNow:O} response {int response.StatusCode} {tracePath}")
                let! receivedBytes = response.Content.ReadAsByteArrayAsync()
                let mutable bytes = receivedBytes

                if response.IsSuccessStatusCode
                   && path = "/libraries/bootstrap/start" then
                    let callback =
                        lock manifestCallbackLock (fun () ->
                            let next = afterNextBootstrap in
                            afterNextBootstrap <- None
                            next)

                    match callback with
                    | Some action -> do! action ()
                    | None -> ()

                if path = "/libraries/changes/get" then
                    Volatile.Write(&lastChangesStatus, int response.StatusCode)

                if response.IsSuccessStatusCode
                   && path = "/libraries/changes/get"
                   && Interlocked.CompareExchange(&injectGap, 0, 1) = 1 then
                    let envelope = deserialize<GraceReturnValue<LibraryChangePageDto>> (System.Text.Encoding.UTF8.GetString(bytes))
                    let empty = { envelope.ReturnValue with Changes = [||]; LastCursor = requestedCursor; HasMore = true }
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
        /// Accepts a real remote edit after an immutable baseline was selected and before its first page reaches the joining copy.
        member _.AfterNextBootstrap(action) = lock manifestCallbackLock (fun () -> afterNextBootstrap <- Some action)
        /// Observes the installed baseline immediately before the joining copy asks for later accepted changes.
        member _.BeforeNextChangesGet(action) = lock manifestCallbackLock (fun () -> beforeNextChangesGet <- Some action)
        /// Runs one competing real mutation after local intent/request persistence and before submission reaches the server.
        member _.BeforeNextSubmit(action) = lock manifestCallbackLock (fun () -> beforeNextSubmit <- Some action)
        /// Exercises the actual server's immutable continuation route with one item per page.
        member _.UseSingleItemBootstrapPages() = singleItemBootstrapPages <- true

        /// Makes every change page hold one change, so a pull of several changes spans several pages.
        member _.UseSingleChangePages() = singleChangePages <- true
        /// Simulates one empty visibility page in place of the real response; the real changes stay on the server.
        member _.InjectEmptyVisibilityPage() = Interlocked.Exchange(&injectGap, 1) |> ignore
        /// Sends the next change request with this cursor instead of the copy's own, so the real server judges it.
        member _.ReplaceNextChangesCursor(cursor: string) = lock manifestCallbackLock (fun () -> replacementCursor <- Some cursor)
        /// Exposes the cursors the copy requested changes after, in order, without interpreting them.
        member _.RequestedCursors = requestedCursors.ToArray()
        /// Returns the HTTP status of the most recent change request the real server answered.
        member _.LastChangesStatus = Volatile.Read(&lastChangesStatus)
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

    /// Reads the repository's current Library catalog through the public route.
    let private catalogAsync (repositoryId: Guid) =
        task {
            let catalog = Parameters.Library.GetLibraryCatalogParameters()
            catalog.OwnerId <- ownerId
            catalog.OrganizationId <- organizationId
            catalog.RepositoryId <- repositoryId.ToString("D")
            catalog.CorrelationId <- generateCorrelationId ()
            use! response = Client.PostAsync("/libraries/catalog/get", createJsonContent catalog)
            return! requireReturnValueAsync<LibraryCatalogDto> response
        }

    /// Adds or removes one Library as an administrator would, against the catalog's current version.
    let private changeLibraryAsync (repositoryId: Guid) add (library: string) =
        task {
            let! current = catalogAsync repositoryId

            /// Scopes one catalog request to this repository.
            let scope (parameters: Parameters.Library.LibraryParameters) =
                parameters.OwnerId <- ownerId
                parameters.OrganizationId <- organizationId
                parameters.RepositoryId <- repositoryId.ToString("D")
                parameters.CorrelationId <- generateCorrelationId ()

            let route, content =
                if add then
                    let request = Parameters.Library.AddLibraryParameters()
                    scope request
                    request.ExpectedVersion <- current.Version
                    request.LibraryPath <- library
                    request.OperationId <- Guid.NewGuid()
                    "/libraries/add", createJsonContent request
                else
                    let request = Parameters.Library.RemoveLibraryParameters()
                    scope request
                    request.ExpectedVersion <- current.Version
                    request.LibraryPath <- library
                    request.OperationId <- Guid.NewGuid()
                    "/libraries/remove", createJsonContent request

            use! response = Client.PostAsync(route, content)
            let! result = requireReturnValueAsync<LibraryCatalogChangeResultDto> response
            Assert.That(result.Outcome, Is.EqualTo(OutcomeKind.Accepted), serialize result)
            return result.LibraryCatalog
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
    let private occupyDirectoryAsync (repositoryId: Guid) parent name =
        task {
            let create = Parameters.Library.SubmitLibraryChangeParameters()
            create.OwnerId <- ownerId
            create.OrganizationId <- organizationId
            create.RepositoryId <- repositoryId.ToString("D")
            create.OperationId <- Guid.NewGuid()
            create.ChangeKind <- ChangeKind.CreateDirectory
            create.ItemKind <- ItemKind.Directory
            create.DestinationParent <- Some parent
            create.DestinationName <- name
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

    /// Runs one real CLI process from the selected working copy, with extra environment variables, and preserves bounded
    /// failure output.
    let private runGraceWithEnvironmentAsync (environment: (string * string) list) token workingDirectory serverUri arguments =
        task {
            let cliAssembly =
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Grace.CLI", "bin", "Release", "net11.0", "grace.dll"))

            let startInfo = ProcessStartInfo("dotnet")
            startInfo.WorkingDirectory <- workingDirectory
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.UseShellExecute <- false

            token
            |> Option.iter (fun value ->
                startInfo.Environment[
                    Constants.EnvironmentVariables.GraceToken
                ] <- value)

            startInfo.Environment[
                Constants.EnvironmentVariables.GraceServerUri
            ] <- serverUri

            for name, value in environment do
                startInfo.Environment[ name ] <- value

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

    /// Runs one real CLI process from the selected working copy and preserves bounded failure output.
    let private runGraceWithTokenAsync token workingDirectory serverUri arguments = runGraceWithEnvironmentAsync [] token workingDirectory serverUri arguments

    /// Runs the fixture-authenticated CLI without changing the parent test process authentication.
    let private runGraceAsync workingDirectory serverUri arguments = runGraceWithTokenAsync None workingDirectory serverUri arguments

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

    /// Pauses through a fresh CLI and verifies restart preserves every repository and operation field except pause.
    let private pauseRetainingAsync root repositoryId (proxy: AuthenticatedProxy) =
        task {
            let before =
                Grace.CLI.LibraryLocalState.readRepository (localDb root) repositoryId
                |> Option.get

            let operations = Grace.CLI.LibraryLocalState.readOperations (localDb root) repositoryId
            let uploads, submits = proxy.ManifestUploadCount, proxy.SubmitRequestCount
            let! _ = requireGraceSuccessAsync root proxy.BaseAddress (syncCommand "pause")
            let! status = requireGraceSuccessAsync root proxy.BaseAddress (syncCommand "status")
            Assert.That(status, Does.Contain("\"Paused\": true"))
            Assert.That(Grace.CLI.LibraryLocalState.readRepository (localDb root) repositoryId, Is.EqualTo(Some { before with Paused = true }))

            Assert.That(
                Grace.CLI.LibraryLocalState.readOperations (localDb root) repositoryId,
                Is.EqualTo<Grace.CLI.LibraryLocalState.PendingOperation>(operations)
            )

            Assert.That(proxy.ManifestUploadCount, Is.EqualTo(uploads))
            Assert.That(proxy.SubmitRequestCount, Is.EqualTo(submits))
        }

    /// Orders a real file rename or deletion ahead of a saved edit in the other working copy.
    let private changeNamespaceAsync (repositoryId: Guid) (item: LibraryItemDto) deleted =
        task {
            let request = Parameters.Library.SubmitLibraryChangeParameters()
            request.OwnerId <- ownerId
            request.OrganizationId <- organizationId
            request.RepositoryId <- repositoryId.ToString("D")
            request.OperationId <- Guid.NewGuid()
            request.ItemId <- Nullable item.ItemId
            request.ItemKind <- ItemKind.File
            request.NamespacePrecondition <- Some { ItemId = item.ItemId; ExpectedNamespaceVersion = item.Namespace.Value.NamespaceVersion }
            request.CorrelationId <- generateCorrelationId ()

            if deleted then
                request.ChangeKind <- ChangeKind.Delete

                request.ContentPrecondition <-
                    Some
                        {
                            ItemId = item.ItemId
                            ExpectedContentVersionId = item.Content.Value.ContentVersionId
                            ExpectedContentRevision = item.ContentRevision.Value
                        }
            else
                request.ChangeKind <- ChangeKind.Rename
                request.DestinationName <- "renamed.txt"

            use! response = Client.PostAsync("/libraries/changes/submit", createJsonContent request)
            let! receipt = requireReturnValueAsync<LibraryOperationReceiptDto> response
            Assert.That(receipt.Change.IsSome, Is.True, serialize receipt)
            return receipt.Change.Value
        }

    /// Creates two enabled empty copies before any positive or excluded file is observed.
    let private enableCopiesAsync label =
        task {
            let root = Path.Combine(Path.GetTempPath(), $"grace-library-{label}-{Guid.NewGuid():N}")
            Console.WriteLine($"Library working copies: {root}")
            let copyA, copyB = Path.Combine(root, "A"), Path.Combine(root, "B")
            Directory.CreateDirectory(copyA) |> ignore
            Directory.CreateDirectory(copyB) |> ignore
            let! repositoryId = createRepositoryAsync ()
            do! addLibraryAsync repositoryId
            let proxy = new AuthenticatedProxy(graceServerBaseAddress, testUserId)
            configureWorkingCopy copyA repositoryId proxy.BaseAddress
            configureWorkingCopy copyB repositoryId proxy.BaseAddress
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "enable")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "enable")
            return copyA, copyB, repositoryId, proxy
        }

    /// Retains exact local state when resume meets a changed remote catalog or a cursor the server didn't issue.
    [<TestCase("removedLibrary"); TestCase("unrecognizedCursor")>]
    let ``resume stops active with retained state when remote participation boundary changes`` scenario =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, _, repositoryId, createdProxy = enableCopiesAsync ("pause-" + scenario)
            use proxy = createdProxy

            // An added Library is followed automatically, so the change this copy can't follow is a removal.
            if scenario = "removedLibrary" then
                let! _ = changeLibraryAsync repositoryId true "OtherLibrary"
                ()

            File.WriteAllText(Path.Combine(copyA, "Library", "retained.txt"), "retained original")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            do! pauseRetainingAsync copyA repositoryId proxy

            let before =
                Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId
                |> Option.get

            let operations = Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId

            if scenario = "removedLibrary" then
                let! _ = changeLibraryAsync repositoryId false "OtherLibrary"
                ()
            else
                proxy.ReplaceNextChangesCursor("fixture-unrecognized-cursor")

            let! blocked = runGraceAsync copyA proxy.BaseAddress (syncCommand "resume")
            Assert.That(blocked.ExitCode, Is.Not.Zero)

            if scenario = "unrecognizedCursor" then
                Assert.That(proxy.LastChangesStatus, Is.EqualTo(400))

            Assert.That(
                Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId,
                Is.EqualTo(Some { before with Paused = false; State = "blocked" })
            )

            Assert.That(
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId,
                Is.EqualTo<Grace.CLI.LibraryLocalState.PendingOperation>(operations)
            )

            Assert.That(File.ReadAllText(Path.Combine(copyA, "Library", "retained.txt")), Is.EqualTo("retained original"))
        }

    /// Connects a fresh working copy to the repository's main branch with saves enabled, as Watch requires, and returns
    /// the branch, its initial root directory version and the token that Watch and the copy's CLI calls use.
    let private connectWatchedCopyAsync (repositoryId: Guid) (copyA: string) =
        task {
            Directory.CreateDirectory(copyA) |> ignore
            // Watch requires an actual Reference boundary; the Library-only fixture starts with no branch content.
            let branchParameters = BranchServerTestHelpers.getBranchParameters (string repositoryId) ""
            branchParameters.BranchName <- "main"
            use! branchResponse = Client.PostAsync("/branch/get", createJsonContent branchParameters)
            let! branch = requireReturnValueAsync<Grace.Types.Branch.BranchDto> branchResponse
            let enableSave = Parameters.Branch.EnableFeatureParameters()
            enableSave.OwnerId <- ownerId
            enableSave.OrganizationId <- organizationId
            enableSave.RepositoryId <- string repositoryId
            enableSave.BranchId <- string branch.BranchId
            enableSave.Enabled <- true
            enableSave.CorrelationId <- generateCorrelationId ()
            use! saveEnabled = Client.PostAsync("/branch/enableSave", createJsonContent enableSave)
            let! _ = requireReturnValueAsync<string> saveEnabled
            let initialRoot = BranchServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) (string repositoryId) (RelativePath ".") []
            do! BranchServerTestHelpers.saveDirectoryVersionsAsync (string repositoryId) [ initialRoot ]
            use! saved = BranchServerTestHelpers.saveReferenceResponseAsync (string repositoryId) branch initialRoot.DirectoryVersionId initialRoot.Sha256Hash
            let! savedBody = saved.Content.ReadAsStringAsync()
            Assert.That(saved.IsSuccessStatusCode, Is.True, savedBody)
            let! seededBranch = BranchServerTestHelpers.getBranchAsync (string repositoryId) (string branch.BranchId)
            Assert.That(seededBranch.LatestSave.DirectoryId, Is.EqualTo(initialRoot.DirectoryVersionId))
            let tokenParameters = Parameters.Auth.CreatePersonalAccessTokenParameters()
            tokenParameters.TokenName <- $"library-watch-{Guid.NewGuid():N}"
            tokenParameters.CorrelationId <- generateCorrelationId ()
            use! tokenResponse = Client.PostAsync("/authenticate/token/create", createJsonContent tokenParameters)
            let! token = requireReturnValueAsync<Grace.Types.PersonalAccessToken.PersonalAccessTokenCreated> tokenResponse

            let! connected =
                runGraceWithTokenAsync
                    (Some token.Token)
                    copyA
                    graceServerBaseAddress
                    [|
                        "connect"
                        "--owner-id"
                        ownerId
                        "--organization-id"
                        organizationId
                        "--repository-id"
                        string repositoryId
                        "--server-address"
                        graceServerBaseAddress
                        "--reference-id"
                        string seededBranch.LatestSave.ReferenceId
                    |]

            Assert.That(connected.ExitCode, Is.Zero, connected.StandardOutput + connected.StandardError)

            Directory.CreateDirectory(Path.Combine(copyA, "Library"))
            |> ignore

            return branch, initialRoot, token.Token
        }

    /// Starts `grace watch` in a working copy with real credentials and collects its output lines.
    let private startWatch (copyA: string) (token: string) =
        let startInfo = ProcessStartInfo("dotnet")
        startInfo.WorkingDirectory <- copyA
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true

        startInfo.Environment[
            Constants.EnvironmentVariables.GraceServerUri
        ] <- graceServerBaseAddress

        startInfo.Environment[
            Constants.EnvironmentVariables.GraceToken
        ] <- token

        startInfo.ArgumentList.Add(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Grace.CLI", "bin", "Release", "net11.0", "grace.dll"))
        )

        startInfo.ArgumentList.Add("watch")
        startInfo.ArgumentList.Add("--output")
        startInfo.ArgumentList.Add("Verbose")
        let watch = new Process(StartInfo = startInfo)
        let output = ConcurrentQueue<string>()
        watch.OutputDataReceived.Add(fun line -> if not (isNull line.Data) then output.Enqueue(line.Data))
        watch.ErrorDataReceived.Add(fun line -> if not (isNull line.Data) then output.Enqueue(line.Data))
        Assert.That(watch.Start(), Is.True)
        watch.BeginOutputReadLine()
        watch.BeginErrorReadLine()
        watch, output

    /// Waits for an observable condition while Watch keeps running, with Watch's output on failure.
    let private waitWhileWatchRuns (watch: Process) (output: ConcurrentQueue<string>) condition =
        task {
            let timer = Stopwatch.StartNew()

            while not (condition ())
                  && not watch.HasExited
                  && timer.Elapsed < TimeSpan.FromSeconds(45.0) do
                do! Task.Delay(100)

            Assert.That(watch.HasExited, Is.False, String.Join(Environment.NewLine, output))
            Assert.That(condition (), Is.True, String.Join(Environment.NewLine, output))
        }

    /// Exercises a running Watch process with real authentication while CLI pause/resume races its periodic work.
    [<Test>]
    let ``live Watch and CLI pause retain local saves while another copy continues`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! _, copyB, repositoryId, createdProxy = enableCopiesAsync "live-pause"
            use proxy = createdProxy
            let copyA = Path.Combine(Path.GetDirectoryName(copyB), "Watched")
            let! branch, initialRoot, token = connectWatchedCopyAsync repositoryId copyA

            /// Uses the same real credentials for Watch and every CLI call from its directly configured copy.
            let requireWatched arguments =
                task {
                    let! result = runGraceWithTokenAsync (Some token) copyA graceServerBaseAddress arguments
                    Assert.That(result.ExitCode, Is.Zero, result.StandardOutput + result.StandardError)
                    return result.StandardOutput
                }

            let! _ = requireWatched (syncCommand "enable")
            let pathA = Path.Combine(copyA, "Library", "local.txt")
            let remoteA = Path.Combine(copyA, "Library", "remote.txt")
            let watch, output = startWatch copyA token
            use _watchProcess = watch

            /// Waits for an observable runtime condition with bounded process diagnostics on failure.
            let waitUntil condition = waitWhileWatchRuns watch output condition

            try
                do!
                    waitUntil (fun () ->
                        output
                        |> Seq.exists (fun line -> line.Contains("Starting timer.")))

                File.WriteAllText(pathA, "positive Watch capture")

                do!
                    waitUntil (fun () ->
                        Grace.CLI.LibraryLocalState.readItems (localDb copyA) repositoryId
                        |> Array.exists (fun item ->
                            item.Namespace
                            |> Option.exists (fun ns -> ns.Name = "local.txt")))

                let! paused = requireWatched (syncCommand "pause")
                Assert.That(paused, Does.Contain("\"Paused\": true"))

                let! human =
                    requireGraceSuccessAsync
                        copyA
                        proxy.BaseAddress
                        [|
                            "library"
                            "sync"
                            "pause"
                            "--output"
                            "Normal"
                        |]

                Assert.That(human, Does.Contain("Enabled=True, Paused=True, State="))
                let frozen = Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                let before = Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId
                File.WriteAllText(pathA, "intermediate paused save")
                File.WriteAllText(pathA, "latest paused save")
                File.WriteAllText(Path.Combine(copyB, "Library", "remote.txt"), "other copy continues")
                let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

                let! _ =
                    requireGraceSuccessAsync
                        copyB
                        proxy.BaseAddress
                        [|
                            "library"
                            "rename"
                            "Library/local.txt"
                            "remote-renamed.txt"
                            "--output"
                            "Json"
                        |]

                let verbs = [| "run"; "enable" |]
                let mutable index = 0

                while index < verbs.Length do
                    let! blocked = runGraceWithTokenAsync (Some token) copyA graceServerBaseAddress (syncCommand verbs[index])
                    Assert.That(blocked.ExitCode, Is.Not.Zero)
                    Assert.That(blocked.StandardOutput, Does.Contain("sync resume"))
                    index <- index + 1

                let! rename =
                    runGraceAsync
                        copyA
                        proxy.BaseAddress
                        [|
                            "library"
                            "rename"
                            "Library/local.txt"
                            "renamed.txt"
                            "--output"
                            "Json"
                        |]

                Assert.That(rename.ExitCode, Is.Not.Zero)
                Assert.That(rename.StandardOutput, Does.Contain("sync resume"))
                do! Task.Delay(6500)
                Assert.That(watch.HasExited, Is.False, String.Join(Environment.NewLine, output))
                Assert.That(File.Exists(remoteA), Is.False)
                Assert.That(Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId, Is.EqualTo(before))

                Assert.That(
                    Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId,
                    Is.EqualTo<Grace.CLI.LibraryLocalState.PendingOperation>(frozen)
                )

                let! _ = requireWatched (syncCommand "resume")
                let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
                Assert.That(File.ReadAllText(Path.Combine(copyB, "Library", "remote-renamed.txt")), Is.EqualTo("latest paused save"))
                Assert.That(File.ReadAllText(Path.Combine(copyA, "Library", "remote-renamed.txt")), Is.EqualTo("latest paused save"))
                Assert.That(File.Exists(pathA), Is.False)
                Assert.That(File.ReadAllText(remoteA), Is.EqualTo("other copy continues"))
                let! _ = requireWatched (syncCommand "pause")
                let completedWrite = File.GetLastWriteTimeUtc(remoteA)
                let! _ = requireWatched (syncCommand "resume")
                Assert.That(File.GetLastWriteTimeUtc(remoteA), Is.EqualTo(completedWrite))
                Assert.That(watch.HasExited, Is.False, String.Join(Environment.NewLine, output))
                let! afterBranch = BranchServerTestHelpers.getBranchAsync (string repositoryId) (string branch.BranchId)

                Assert.That(
                    afterBranch.LatestSave.DirectoryId,
                    Is.EqualTo(initialRoot.DirectoryVersionId),
                    "Library content entered version-control Save history."
                )
            finally
                if not watch.HasExited then watch.Kill(true)
                watch.WaitForExit()
                output |> Seq.iter TestContext.Progress.WriteLine
        }

    /// A Library failure never stops Watch: after the Watched copy edits a file another copy has deleted, which the
    /// server rejects every time until the edit-after-delete rule is built, Watch keeps saving version-controlled
    /// work, and status says what failed, where, and what to do next.
    [<Test>]
    let ``Watch keeps saving version-controlled work after a Library change is rejected`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! _, copyB, repositoryId, createdProxy = enableCopiesAsync "watch-survives"
            use proxy = createdProxy
            let copyA = Path.Combine(Path.GetDirectoryName(copyB), "Watched")
            let! branch, initialRoot, token = connectWatchedCopyAsync repositoryId copyA

            /// Runs one CLI command in the Watched copy with the credentials Watch uses.
            let runWatched arguments = runGraceWithTokenAsync (Some token) copyA graceServerBaseAddress arguments

            let! enabled = runWatched (syncCommand "enable")
            Assert.That(enabled.ExitCode, Is.Zero, enabled.StandardOutput + enabled.StandardError)

            // Both copies have the file, then the other copy's delete reaches the server.
            File.WriteAllText(Path.Combine(copyB, "Library", "same.txt"), "created by the other copy")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! pulled = runWatched (syncCommand "run")
            Assert.That(pulled.ExitCode, Is.Zero, pulled.StandardOutput + pulled.StandardError)

            let original =
                Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId
                |> Array.exactlyOne

            let! _ = changeNamespaceAsync repositoryId original true

            // The Watched copy edits the file before it has pulled the delete.
            let sameA = Path.Combine(copyA, "Library", "same.txt")
            File.WriteAllText(sameA, "edited by the Watched copy")

            let watch, output = startWatch copyA token
            use _watchProcess = watch

            try
                // Watch's first Library sync is rejected because the file is gone. Watch records it and keeps running.
                do!
                    waitWhileWatchRuns watch output (fun () ->
                        (Grace.CLI.LibraryLocalState.readHealth (localDb copyA) repositoryId)
                            .LastFailure
                        |> Option.exists (fun failure -> failure.Path = Some "Library/same.txt"))

                do!
                    waitWhileWatchRuns watch output (fun () ->
                        output
                        |> Seq.exists (fun line -> line.Contains("Library sync failed")))

                // Version-controlled work saved after the failure still produces a save reference.
                File.WriteAllText(Path.Combine(copyA, "notes.txt"), "version-controlled work after the Library failure")
                let timer = Stopwatch.StartNew()
                let mutable saved = false

                while not saved
                      && not watch.HasExited
                      && timer.Elapsed < TimeSpan.FromSeconds(60.0) do
                    let! current = BranchServerTestHelpers.getBranchAsync (string repositoryId) (string branch.BranchId)

                    saved <-
                        current.LatestSave.DirectoryId
                        <> initialRoot.DirectoryVersionId

                    if not saved then do! Task.Delay(250)

                Assert.That(watch.HasExited, Is.False, String.Join(Environment.NewLine, output))

                Assert.That(
                    saved,
                    Is.True,
                    "Watch didn't save version-controlled work after the Library failure."
                    + Environment.NewLine
                    + String.Join(Environment.NewLine, output)
                )

                // Status says the copy is blocked, why, on which path, and what to do next.
                let! status = runWatched (syncCommand "status")
                Assert.That(status.ExitCode, Is.Zero, status.StandardOutput + status.StandardError)
                Assert.That(status.StandardOutput, Does.Contain("\"State\": \"blocked\""))
                Assert.That(status.StandardOutput, Does.Contain(RejectionReason.ItemTombstoned))
                Assert.That(status.StandardOutput, Does.Contain("Library/same.txt"))
                Assert.That(status.StandardOutput, Does.Contain("The server rejected this change. Your saved file is kept."))

                // The Watched copy keeps its edit.
                Assert.That(File.ReadAllText(sameA), Is.EqualTo("edited by the Watched copy"))
                Assert.That(watch.HasExited, Is.False, String.Join(Environment.NewLine, output))
            finally
                if not watch.HasExited then watch.Kill(true)
                watch.WaitForExit()
                output |> Seq.iter TestContext.Progress.WriteLine
        }

    /// A failed sync run is shown in status with its path and next step, and a later successful run clears it.
    [<Test>]
    let ``failed sync run is shown in status until a later run succeeds`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, _, repositoryId, createdProxy = enableCopiesAsync "status-recovery"
            use proxy = createdProxy
            File.WriteAllText(Path.Combine(copyA, "Library", "status.txt"), "saved while the server is unreachable")
            proxy.BeforeNextSubmit(fun () -> Task.FromException(InvalidOperationException("Injected submit failure.")))

            let! failed = runGraceAsync copyA proxy.BaseAddress (syncCommand "run")
            Assert.That(failed.ExitCode, Is.Not.Zero, failed.StandardOutput + failed.StandardError)

            let! blocked = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "status")
            Assert.That(blocked, Does.Contain("\"State\": \"blocked\""))

            let failure =
                (Grace.CLI.LibraryLocalState.readHealth (localDb copyA) repositoryId)
                    .LastFailure

            Assert.That(failure.IsSome, Is.True, blocked)
            Assert.That(failure.Value.Path, Is.EqualTo(Some "Library/status.txt"))
            Assert.That(failure.Value.NextStep, Is.EqualTo(LibrarySynchronization.retryNextStep))

            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! current = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "status")
            Assert.That(current, Does.Contain("\"State\": \"current\""))
            let health = Grace.CLI.LibraryLocalState.readHealth (localDb copyA) repositoryId
            Assert.That(health.LastFailure, Is.EqualTo(None))
            Assert.That(health.LastSuccessAtUnixTicks.IsSome, Is.True)
        }

    /// Fills the repository locator shared by every Library request. It lives outside the test's task because a generic
    /// function inside one stops the task from compiling to a state machine.
    let private scopedTo (repositoryId: Guid) (parameters: #Parameters.Library.LibraryParameters) =
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId.ToString("D")
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// A contributor with no Library role can read the catalog, which Watch needs on every copy, and still can't
    /// call a Library content route.
    [<Test>]
    let ``repository contributor without a Library role reads the catalog but not Library content`` () =
        task {
            let! repositoryId = createRepositoryAsync ()
            do! addLibraryAsync repositoryId
            let contributor = $"{Guid.NewGuid()}"
            let grant = Parameters.Access.GrantRoleParameters()
            grant.OwnerId <- ownerId
            grant.OrganizationId <- organizationId
            grant.RepositoryId <- repositoryId.ToString("D")
            grant.PrincipalType <- "User"
            grant.PrincipalId <- contributor
            grant.ScopeKind <- "repo"
            grant.RoleId <- "RepositoryContributor"
            grant.Source <- "test"
            grant.CorrelationId <- generateCorrelationId ()
            use! grantResponse = Client.PostAsync("/authorize/grant-role", createJsonContent grant)
            let! grantBody = grantResponse.Content.ReadAsStringAsync()
            Assert.That(grantResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), grantBody)

            use contributorClient = new HttpClient()
            contributorClient.BaseAddress <- Client.BaseAddress
            contributorClient.DefaultRequestHeaders.Add("x-grace-user-id", contributor)

            use! catalogResponse =
                contributorClient.PostAsync(
                    "/libraries/catalog/get",
                    createJsonContent (scopedTo repositoryId (Parameters.Library.GetLibraryCatalogParameters()))
                )

            let! catalog = requireReturnValueAsync<LibraryCatalogDto> catalogResponse
            Assert.That(catalog.Libraries, Is.EqualTo([| "Library" |]))

            use! changesResponse =
                contributorClient.PostAsync(
                    "/libraries/changes/get",
                    createJsonContent (scopedTo repositoryId (Parameters.Library.GetLibraryChangesParameters()))
                )

            Assert.That(changesResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            let prepare = scopedTo repositoryId (Parameters.Library.PrepareLibraryContentParameters())
            prepare.OperationId <- Guid.NewGuid()
            prepare.Blake3Hash <- String('a', 64)
            prepare.Sha256Hash <- String('b', 64)
            prepare.Size <- 1L
            use! prepareResponse = contributorClient.PostAsync("/libraries/content/prepare", createJsonContent prepare)
            Assert.That(prepareResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            // A caller with no role in the repository is denied the catalog. A nonexistent repository fails ID validation
            // with 400 before any route's permission check runs; that applies to every repository route, not just this one.
            use outsiderClient = new HttpClient()
            outsiderClient.BaseAddress <- Client.BaseAddress
            outsiderClient.DefaultRequestHeaders.Add("x-grace-user-id", $"{Guid.NewGuid()}")

            use! outsiderResponse =
                outsiderClient.PostAsync("/libraries/catalog/get", createJsonContent (scopedTo repositoryId (Parameters.Library.GetLibraryCatalogParameters())))

            let! outsiderBody = outsiderResponse.Content.ReadAsStringAsync()
            Assert.That(outsiderResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), outsiderBody)
            Assert.That(outsiderBody, Does.Not.Contain("Library"), "The denial revealed catalog content.")
        }

    /// Proves committed object discovery, frozen-object upload and missing-reference blocking through actual CLI processes and the hosted server.
    [<TestCase("complete"); TestCase("missing"); TestCase("corrupt")>]
    let ``committed saved object resumes without caller identity and uploads frozen content`` scenario =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, proxy = enableCopiesAsync ("saved-object-" + scenario)
            use proxy = proxy
            let source = Path.Combine(copyA, "Library", "object.txt")
            let frozenBytes = Array.init 196609 (fun index -> byte (index % 251))
            File.WriteAllBytes(source, frozenBytes)
            let configuration = GraceConfiguration()
            configuration.RootDirectory <- copyA
            configuration.ObjectDirectory <- Path.Combine(copyA, ".grace", "objects")
            configuration.GraceStatusFile <- localDb copyA
            configuration.RepositoryId <- repositoryId
            let before = Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId

            /// Interrupts after the actual insert, discarding the caller's operation identity.
            let interruptCapture () =
                LibrarySynchronization.captureSavedWith (fun _ -> raise (OperationCanceledException())) configuration
                |> ignore

            Assert.That(Action interruptCapture, Throws.TypeOf<OperationCanceledException>())

            let pending =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                |> Array.exactlyOne

            let locator = LibraryFilesystem.objectPath configuration pending.SourceObject.Value
            let timestamp = File.GetLastWriteTimeUtc locator
            do! pauseRetainingAsync copyA repositoryId proxy
            Assert.That(LibrarySynchronization.captureSaved configuration, Is.False)

            Assert.That(
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId,
                Is.EqualTo<Grace.CLI.LibraryLocalState.PendingOperation>([| pending |])
            )

            Assert.That(File.GetLastWriteTimeUtc locator, Is.EqualTo(timestamp))
            File.WriteAllText(source, "distinct working bytes after committed capture")

            if scenario <> "complete" then
                if scenario = "missing" then
                    File.Delete(locator)
                else
                    File.WriteAllText(locator, "corrupt partial object")

                let! blocked = runGraceAsync copyA proxy.BaseAddress (syncCommand "resume")
                Assert.That(blocked.ExitCode, Is.Not.Zero)

                Assert.That(
                    (Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId)
                        .Value
                        .Paused,
                    Is.False
                )

                Assert.That(proxy.ManifestUploadCount, Is.Zero)
                Assert.That(proxy.SubmitRequestCount, Is.Zero)

                Assert.That(
                    (Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId)
                        .Value
                        .AppliedCursor,
                    Is.EqualTo(before.Value.AppliedCursor)
                )

                let retained =
                    Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                    |> Array.find (fun operation -> operation.OperationId = pending.OperationId)

                Assert.That(retained, Is.EqualTo(pending))
                Assert.That(File.ReadAllText(source), Is.EqualTo("distinct working bytes after committed capture"))
                // Restore the exact test backup explicitly; production never repairs from changed working bytes.
                File.WriteAllBytes(locator, frozenBytes)

            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "resume")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            let completed =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                |> Array.find (fun operation -> operation.OperationId = pending.OperationId)

            Assert.That(completed.Terminal, Is.True)
            Assert.That(completed.SourceObject, Is.EqualTo(pending.SourceObject))
            Assert.That(completed.Accepted.Value.Item.Content.Value.Size, Is.EqualTo(int64 frozenBytes.Length))
            Assert.That(completed.Accepted.Value.Item.Content.Value.Sha256Hash, Is.EqualTo(pending.SourceObject.Value.Content.Sha256Hash))
            Assert.That(File.ReadAllBytes(locator), Is.EqualTo<byte>(frozenBytes))
            Assert.That(proxy.ManifestUploadCount, Is.EqualTo(2))
            Assert.That(proxy.SubmitRequestCount, Is.EqualTo(2))
            Assert.That(File.ReadAllText(Path.Combine(copyB, "Library", "object.txt")), Is.EqualTo("distinct working bytes after committed capture"))
            let finalWrite = File.GetLastWriteTimeUtc(source)
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            Assert.That(proxy.ManifestUploadCount, Is.EqualTo(2))
            Assert.That(proxy.SubmitRequestCount, Is.EqualTo(2))
            Assert.That(File.GetLastWriteTimeUtc(source), Is.EqualTo(finalWrite))
        }

    /// Invokes the actual explicit command through real server acceptance, ordered predecessors, saved edits and receipt replay in two Windows copies.
    [<TestCase("normal");
      TestCase("nested");
      TestCase("human");
      TestCase("occupied");
      TestCase("canceled");
      TestCase("lost");
      TestCase("compatible-edit");
      TestCase("saved-source");
      TestCase("saved-target");
      TestCase("competing-rename");
      TestCase("deleted");
      TestCase("lost-rejection")>]
    let ``explicit rename command converges or retires its exact namespace intent`` scenario =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, proxy = enableCopiesAsync ("rename-" + scenario)
            use proxy = proxy
            let parent = if scenario = "nested" then "Library/nested" else "Library"

            Directory.CreateDirectory(Path.Combine(copyA, parent))
            |> ignore

            let pathA = Path.Combine(copyA, parent, "ordinary.txt")
            let pathB = Path.Combine(copyB, parent, "ordinary.txt")
            let targetA = Path.Combine(copyA, parent, "new.txt")
            File.WriteAllText(pathA, "original nonempty bytes")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            let original =
                Grace.CLI.LibraryLocalState.readItems (localDb copyA) repositoryId
                |> Array.find (fun item -> item.ItemKind = ItemKind.File)

            let beforeState =
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId)
                    .Value

            let catalog =
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId)
                    .Value
                    .Catalog
                    .Version

            let beforeUploads = proxy.ManifestUploadCount

            let rejected =
                scenario = "competing-rename"
                || scenario = "deleted"
                || scenario = "lost-rejection"
                || scenario = "occupied"

            if scenario = "lost" || scenario = "lost-rejection" then
                proxy.DropNextAcceptedSubmitResponse()

            if rejected then
                proxy.BeforeNextSubmit (fun () ->
                    task {
                        if scenario = "occupied" then
                            let! _ = occupyDirectoryAsync repositoryId original.Namespace.Value.Parent "new.txt"
                            ()
                        else
                            let! _ = changeNamespaceAsync repositoryId original (scenario = "deleted")
                            ()

                        return ()
                    }
                    :> Task)

            if scenario = "compatible-edit" then
                proxy.BeforeNextSubmit (fun () ->
                    task {
                        File.WriteAllText(pathB, "compatible accepted content")
                        let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
                        return ()
                    }
                    :> Task)

            if scenario = "saved-source"
               || scenario = "saved-target" then
                proxy.AfterNextContentRead(fun () -> File.WriteAllText((if scenario = "saved-source" then pathA else targetA), "saved during rename"))

            let command =
                [|
                    "library"
                    "rename"
                    parent + "/ordinary.txt"
                    "new.txt"
                    "--output"
                    (if scenario = "human" then "Normal" else "Json")
                |]

            let! first =
                if scenario = "canceled" then
                    task {
                        use cancellation = new CancellationTokenSource()
                        proxy.AfterNextContentRead(fun () -> cancellation.Cancel())
                        let previousDirectory = Directory.GetCurrentDirectory()
                        let previousUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)

                        try
                            Directory.SetCurrentDirectory(copyA)
                            resetConfiguration ()
                            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, proxy.BaseAddress)

                            let! result =
                                Grace.CLI.Command.LibrarySynchronization.rename
                                    (Current())
                                    (generateCorrelationId ())
                                    (parent + "/ordinary.txt")
                                    "new.txt"
                                    cancellation.Token

                            let exitCode = if result.Outcome = LibrarySynchronization.RenameOutcome.Completed then 0 else 1

                            return
                                { ExitCode = exitCode; StandardOutput = serialize (Grace.CLI.Command.LibraryCommand.renameOutput result); StandardError = "" }
                        finally
                            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, previousUri)
                            Directory.SetCurrentDirectory(previousDirectory)
                            resetConfiguration ()
                    }
                else
                    runGraceAsync copyA proxy.BaseAddress command

            /// Reads the exact generated operation after each external CLI process has exited.
            let intent () =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                |> Array.find (fun operation -> operation.Rename)

            let selected = intent ()
            let frozen = selected.RequestJson.Value
            let request = deserialize<Parameters.Library.SubmitLibraryChangeParameters> frozen
            Assert.That(request.ChangeKind, Is.EqualTo(ChangeKind.Rename))
            Assert.That(request.NamespacePrecondition.Value.ExpectedNamespaceVersion, Is.EqualTo(original.Namespace.Value.NamespaceVersion))

            Assert.That(
                request.ContentPrecondition.IsNone
                && not request.UploadSessionId.HasValue
                && request.DestinationParent.IsNone,
                Is.True
            )

            Assert.That(
                selected.SourceObject.IsNone
                && not selected.Uploaded,
                Is.True
            )

            if scenario = "lost" || scenario = "lost-rejection" then
                Assert.That(first.ExitCode, Is.Not.Zero)
                Assert.That(first.StandardOutput, Does.Contain("ambiguous"))
                Assert.That(File.Exists(pathA), Is.True)
                Assert.That(File.Exists(targetA), Is.False)
            elif scenario = "saved-source"
                 || scenario = "saved-target"
                 || scenario = "canceled" then
                Assert.That(first.ExitCode, Is.Not.Zero)
                Assert.That(first.StandardOutput, Does.Contain("acceptedButObstructed"))

                if scenario = "canceled" then
                    Assert.That(File.Exists(pathA), Is.True)
                    Assert.That(File.Exists(targetA), Is.False)

                    Assert.That(
                        (Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId)
                            .Value
                            .AppliedCursor,
                        Is.EqualTo(beforeState.AppliedCursor)
                    )
            elif rejected then
                Assert.That(first.StandardOutput, Does.Contain("rejected"))
            else
                Assert.That(first.ExitCode, Is.Zero, first.StandardOutput + first.StandardError)

            let! resumed = runGraceAsync copyA proxy.BaseAddress command
            Assert.That((intent ()).OperationId, Is.EqualTo(selected.OperationId))
            Assert.That((intent ()).RequestJson, Is.EqualTo(Some frozen))

            if rejected then
                Assert.That(resumed.ExitCode, Is.Not.Zero)
                Assert.That(resumed.StandardOutput, Does.Contain("rejected"))
                Assert.That(File.ReadAllText(pathA), Is.EqualTo("original nonempty bytes"))
                Assert.That(File.Exists(targetA), Is.False)
                Assert.That(Grace.CLI.LibraryLocalState.readItems (localDb copyA) repositoryId, Is.EqualTo<LibraryItemDto>([| original |]))

                Assert.That(
                    (Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId)
                        .Value
                        .AppliedCursor,
                    Is.EqualTo(beforeState.AppliedCursor)
                )

                Assert.That(
                    (intent ()).Terminal
                    && (intent ()).Accepted.IsNone
                    && not (intent ()).EchoPending,
                    Is.True
                )

                Assert.That((intent ()).Receipt.Value.ReasonCode.IsSome, Is.True)
                File.WriteAllText(Path.Combine(copyB, "Library", "unrelated.txt"), "later unrelated content")
            else
                Assert.That(resumed.ExitCode, Is.Zero, resumed.StandardOutput + resumed.StandardError)
                Assert.That(resumed.StandardOutput, Does.Contain("completed"))

            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")

            if rejected then
                Assert.That(File.ReadAllText(Path.Combine(copyA, "Library", "unrelated.txt")), Is.EqualTo("later unrelated content"))
                Assert.That(File.Exists(targetA), Is.False)
                Assert.That(Directory.Exists(targetA), Is.EqualTo((scenario = "occupied")))
            else
                let expected =
                    if scenario = "compatible-edit" then
                        "compatible accepted content"
                    elif scenario = "saved-source"
                         || scenario = "saved-target" then
                        "saved during rename"
                    else
                        "original nonempty bytes"

                let targetB = Path.Combine(copyB, parent, "new.txt")
                Assert.That(File.Exists(pathA) || File.Exists(pathB), Is.False)
                Assert.That(File.ReadAllText(targetA), Is.EqualTo(expected))
                Assert.That(File.ReadAllText(targetB), Is.EqualTo(expected))

                let a =
                    Grace.CLI.LibraryLocalState.readItems (localDb copyA) repositoryId
                    |> Array.find (fun item -> item.ItemId = original.ItemId)

                let b =
                    Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId
                    |> Array.find (fun item -> item.ItemId = original.ItemId)

                Assert.That(a, Is.EqualTo(b))

                if scenario = "normal" || scenario = "lost" then
                    Assert.That(a.ContentRevision, Is.EqualTo(original.ContentRevision))
                    Assert.That(proxy.ManifestUploadCount, Is.EqualTo(beforeUploads))

                let writes =
                    [|
                        File.GetLastWriteTimeUtc(targetA)
                        File.GetLastWriteTimeUtc(targetB)
                    |]

                let submissions, uploads = proxy.SubmitRequestCount, proxy.ManifestUploadCount
                let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress command
                let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

                Assert.That(
                    [|
                        File.GetLastWriteTimeUtc(targetA)
                        File.GetLastWriteTimeUtc(targetB)
                    |],
                    Is.EqualTo<DateTime>(writes)
                )

                Assert.That(proxy.SubmitRequestCount, Is.EqualTo(submissions))
                Assert.That(proxy.ManifestUploadCount, Is.EqualTo(uploads))

            Assert.That(
                countWduCompletions copyA
                + countWduCompletions copyB,
                Is.Zero
            )
        }

    /// Keeps a destination created before preparation as an obstruction, then resumes the same genuine acceptance after deliberate resolution.
    [<TestCase(true); TestCase(false)>]
    let ``accepted unprepared rename preserves new destination without capturing a stuck create`` cancelBeforePreparation =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, proxy =
                enableCopiesAsync (
                    "rename-unprepared-"
                    + string cancelBeforePreparation
                )

            use proxy = proxy
            let sourceA = Path.Combine(copyA, "Library", "ordinary.txt")
            let targetA = Path.Combine(copyA, "Library", "new.txt")
            let targetB = Path.Combine(copyB, "Library", "new.txt")
            let originalBytes = "original accepted nonempty bytes"
            let obstructionBytes = "distinct locally created obstruction bytes"
            File.WriteAllText(sourceA, originalBytes)
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let originalItems = Grace.CLI.LibraryLocalState.readItems (localDb copyA) repositoryId
            let originalState = Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId
            let uploads = proxy.ManifestUploadCount
            let submissions = proxy.SubmitRequestCount

            let command =
                [|
                    "library"
                    "rename"
                    "Library/ordinary.txt"
                    "new.txt"
                    "--output"
                    "Json"
                |]

            let! first =
                if cancelBeforePreparation then
                    task {
                        use cancellation = new CancellationTokenSource()
                        proxy.BeforeNextChangesGet(fun () -> cancellation.Cancel())
                        let previousDirectory = Directory.GetCurrentDirectory()
                        let previousUri = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)

                        try
                            Directory.SetCurrentDirectory(copyA)
                            resetConfiguration ()
                            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, proxy.BaseAddress)

                            let! result =
                                Grace.CLI.Command.LibrarySynchronization.rename
                                    (Current())
                                    (generateCorrelationId ())
                                    "Library/ordinary.txt"
                                    "new.txt"
                                    cancellation.Token

                            let exitCode = if result.Outcome = LibrarySynchronization.RenameOutcome.Completed then 0 else 1

                            return
                                { ExitCode = exitCode; StandardOutput = serialize (Grace.CLI.Command.LibraryCommand.renameOutput result); StandardError = "" }
                        finally
                            Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri, previousUri)
                            Directory.SetCurrentDirectory(previousDirectory)
                            resetConfiguration ()
                    }
                else
                    proxy.BeforeNextChangesGet(fun () -> File.WriteAllText(targetA, obstructionBytes))
                    runGraceAsync copyA proxy.BaseAddress command

            /// Reloads the selected durable intent after each command releases its working-root lease.
            let intent () =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                |> Array.find (fun operation -> operation.Rename)

            let accepted = intent ()
            Assert.That(first.ExitCode, Is.Not.Zero)
            Assert.That(first.StandardOutput, Does.Contain("acceptedButObstructed"))

            Assert.That(
                accepted.Accepted.IsSome
                && accepted.Receipt.IsSome
                && not accepted.Prepared,
                Is.True
            )

            Assert.That(accepted.RequestJson.IsSome, Is.True)

            if cancelBeforePreparation then
                Assert.That(File.Exists(targetA), Is.False)
                File.WriteAllText(targetA, obstructionBytes)

            let! obstructed = runGraceAsync copyA proxy.BaseAddress command
            Assert.That(obstructed.ExitCode, Is.Not.Zero)
            Assert.That(obstructed.StandardOutput, Does.Contain("acceptedButObstructed"))
            Assert.That(File.ReadAllText(sourceA), Is.EqualTo(originalBytes))
            Assert.That(File.ReadAllText(targetA), Is.EqualTo(obstructionBytes))
            Assert.That(Grace.CLI.LibraryLocalState.readItems (localDb copyA) repositoryId, Is.EqualTo<LibraryItemDto>(originalItems))

            Assert.That(
                Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId,
                Is.EqualTo(Some { originalState.Value with State = "blocked" })
            )

            Assert.That(intent (), Is.EqualTo(accepted))

            let pending =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                |> Array.filter (fun operation -> not operation.Terminal)

            Assert.That(pending, Is.EqualTo<Grace.CLI.LibraryLocalState.PendingOperation>([| accepted |]))
            Assert.That(proxy.ManifestUploadCount, Is.EqualTo(uploads))
            Assert.That(proxy.SubmitRequestCount, Is.EqualTo(submissions + 1))

            let preserved = Path.Combine(copyA, "preserved-obstruction.txt")
            File.Move(targetA, preserved)
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress command
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let completed = intent ()
            Assert.That(completed.Terminal, Is.True)
            Assert.That(completed.OperationId, Is.EqualTo(accepted.OperationId))
            Assert.That(completed.RequestJson, Is.EqualTo(accepted.RequestJson))
            Assert.That(completed.Receipt, Is.EqualTo(accepted.Receipt))
            Assert.That(File.ReadAllText(preserved), Is.EqualTo(obstructionBytes))
            Assert.That(File.ReadAllText(targetA), Is.EqualTo(originalBytes))
            Assert.That(File.ReadAllText(targetB), Is.EqualTo(originalBytes))

            Assert.That(
                File.Exists(sourceA)
                || File.Exists(Path.Combine(copyB, "Library", "ordinary.txt")),
                Is.False
            )

            let itemA =
                Grace.CLI.LibraryLocalState.readItems (localDb copyA) repositoryId
                |> Array.exactlyOne

            let itemB =
                Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId
                |> Array.exactlyOne

            Assert.That(itemA, Is.EqualTo(itemB))
            Assert.That(itemA.ItemId, Is.EqualTo(originalItems[0].ItemId))
            Assert.That(itemA.ContentRevision, Is.EqualTo(originalItems[0].ContentRevision))

            let writes =
                [|
                    File.GetLastWriteTimeUtc(targetA)
                    File.GetLastWriteTimeUtc(targetB)
                |]

            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress command
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            Assert.That(
                [|
                    File.GetLastWriteTimeUtc(targetA)
                    File.GetLastWriteTimeUtc(targetB)
                |],
                Is.EqualTo<DateTime>(writes)
            )

            Assert.That(proxy.ManifestUploadCount, Is.EqualTo(uploads))
            Assert.That(proxy.SubmitRequestCount, Is.EqualTo(submissions + 1))
        }

    /// Joins an already populated Library through real commands, installs retained revisions, then catches up and restarts without publication.
    [<Test>]
    let ``populated A and new B install selected baseline then later accepted edit without duplicate publication on restart`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let root = Path.Combine(Path.GetTempPath(), $"grace-library-populated-{Guid.NewGuid():N}")
            let copyA, copyB = Path.Combine(root, "A"), Path.Combine(root, "B")
            Directory.CreateDirectory(copyA) |> ignore
            Directory.CreateDirectory(copyB) |> ignore
            let! repositoryId = createRepositoryAsync ()
            do! addLibraryAsync repositoryId
            use proxy = new AuthenticatedProxy(graceServerBaseAddress, testUserId)
            configureWorkingCopy copyA repositoryId proxy.BaseAddress
            configureWorkingCopy copyB repositoryId proxy.BaseAddress
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "enable")
            let nestedA = Directory.CreateDirectory(Path.Combine(copyA, "Library", "one", "two"))
            let pathA = Path.Combine(nestedA.FullName, "selected.txt")
            let pathB = Path.Combine(copyB, "Library", "one", "two", "selected.txt")
            File.WriteAllText(pathA, "selected retained baseline bytes")
            File.WriteAllBytes(Path.Combine(copyA, "Library", "other.bin"), [| 0uy; 255uy; 7uy |])
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let selected = Grace.CLI.LibraryLocalState.readItems (localDb copyA) repositoryId

            let selectedCursor =
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId)
                    .Value
                    .AppliedCursor

            Assert.That(selected.Length, Is.EqualTo(4))
            let mutable installedSelectionObserved = false
            let mutable submissionsAfterEdit = 0
            let mutable uploadsAfterEdit = 0
            proxy.UseSingleItemBootstrapPages()

            proxy.AfterNextBootstrap (fun () ->
                task {
                    File.WriteAllText(pathA, "later accepted bytes after baseline selection")
                    let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
                    submissionsAfterEdit <- proxy.SubmitRequestCount
                    uploadsAfterEdit <- proxy.ManifestUploadCount

                    proxy.BeforeNextChangesGet (fun () ->
                        Assert.That(File.ReadAllText(pathB), Is.EqualTo("selected retained baseline bytes"))

                        let installed =
                            Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId
                            |> Array.sortBy (fun item -> item.ItemId)

                        Assert.That(installed, Is.EqualTo(box (selected |> Array.sortBy (fun item -> item.ItemId))))

                        let state =
                            (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                                .Value

                        Assert.That(state.AppliedCursor, Is.EqualTo(selectedCursor))
                        Assert.That(state.Baseline.Value.Applied, Is.True)

                        Assert.That(
                            Grace.CLI.LibraryLocalState.readOperations (localDb copyB) repositoryId
                            |> Array.forall (fun op ->
                                op.Direction = "baseline"
                                && op.Terminal
                                && op.Accepted.IsNone),
                            Is.True
                        )

                        installedSelectionObserved <- true)
                })

            let! enabled = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "enable")
            Assert.That(installedSelectionObserved, Is.True)
            Assert.That(enabled, Does.Contain("current"))
            Assert.That(File.ReadAllText(pathB), Is.EqualTo("later accepted bytes after baseline selection"))
            Assert.That(File.ReadAllBytes(Path.Combine(copyB, "Library", "other.bin")), Is.EqualTo(box [| 0uy; 255uy; 7uy |]))
            Assert.That(proxy.SubmitRequestCount, Is.EqualTo(submissionsAfterEdit))
            Assert.That(proxy.ManifestUploadCount, Is.EqualTo(uploadsAfterEdit))
            let beforeA, beforeB = File.GetLastWriteTimeUtc(pathA), File.GetLastWriteTimeUtc(pathB)
            let otherA = File.GetLastWriteTimeUtc(Path.Combine(copyA, "Library", "other.bin"))
            let otherB = File.GetLastWriteTimeUtc(Path.Combine(copyB, "Library", "other.bin"))
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            do! pauseRetainingAsync copyB repositoryId proxy
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "resume")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "enable")
            Assert.That(File.GetLastWriteTimeUtc(pathA), Is.EqualTo(beforeA))
            Assert.That(File.GetLastWriteTimeUtc(pathB), Is.EqualTo(beforeB))
            Assert.That(File.GetLastWriteTimeUtc(Path.Combine(copyA, "Library", "other.bin")), Is.EqualTo(otherA))
            Assert.That(File.GetLastWriteTimeUtc(Path.Combine(copyB, "Library", "other.bin")), Is.EqualTo(otherB))
            Assert.That(proxy.SubmitRequestCount, Is.EqualTo(submissionsAfterEdit))
            Assert.That(proxy.ManifestUploadCount, Is.EqualTo(uploadsAfterEdit))

            let stateA =
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId)
                    .Value

            let stateB =
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value

            Assert.That(stateB.AppliedCursor, Is.EqualTo(stateA.AppliedCursor))
            Assert.That(stateB.Baseline.IsNone, Is.True)
            Assert.That(stateB.State, Is.EqualTo("current"))
            TestContext.Progress.WriteLine($"Populated Library copies: {root}")
        }

    /// Excludes empty observations without losing presence, the original edit base, or a previously captured positive request.
    [<Test>]
    let ``nonempty boundary preserves zero files and frozen positive work across restart`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync "nonempty"
            use proxy = createdProxy
            let pathA, pathB = Path.Combine(copyA, "Library", "source.txt"), Path.Combine(copyB, "Library", "source.txt")
            let zero = Path.Combine(copyA, "Library", "zero.txt")
            File.WriteAllBytes(zero, [||])
            File.WriteAllText(pathA, "original positive")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            Assert.That(File.Exists(zero), Is.True)
            Assert.That(FileInfo(zero).Length, Is.Zero)
            Assert.That(File.Exists(Path.Combine(copyB, "Library", "zero.txt")), Is.False)

            Assert.That(
                Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId
                |> Array.length,
                Is.EqualTo(1)
            )

            Assert.That(
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                |> Array.length,
                Is.EqualTo(1)
            )

            Assert.That(proxy.ManifestUploadCount, Is.EqualTo(1))

            let original =
                Grace.CLI.LibraryLocalState.readItems (localDb copyA) repositoryId
                |> Array.exactlyOne

            let before = Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId
            let submitted = proxy.SubmitRequestCount
            File.WriteAllBytes(pathA, [||])
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            Assert.That(File.Exists(pathA), Is.True)
            Assert.That(FileInfo(pathA).Length, Is.Zero)

            Assert.That(
                Grace.CLI.LibraryLocalState.readItems (localDb copyA) repositoryId
                |> Array.exactlyOne,
                Is.EqualTo(original)
            )

            Assert.That(
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId)
                    .Value
                    .AppliedCursor,
                Is.EqualTo(before.Value.AppliedCursor)
            )

            Assert.That(proxy.SubmitRequestCount, Is.EqualTo(submitted))
            File.WriteAllText(pathA, "positive after zero")
            proxy.AfterNextManifestUpload(fun () -> File.WriteAllBytes(pathA, [||]))
            proxy.DropNextAcceptedSubmitResponse()
            let! lost = runGraceAsync copyA proxy.BaseAddress (syncCommand "run")
            Assert.That(lost.ExitCode, Is.Not.EqualTo(0))

            let pending =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                |> Array.find (fun operation -> not operation.Terminal)

            let frozen = deserialize<Parameters.Library.SubmitLibraryChangeParameters> pending.RequestJson.Value
            Assert.That(frozen.ContentPrecondition.Value.ExpectedContentRevision, Is.EqualTo(original.ContentRevision.Value))
            let! blocked = runGraceAsync copyA proxy.BaseAddress (syncCommand "run")
            Assert.That(blocked.ExitCode, Is.Not.EqualTo(0))
            Assert.That(blocked.StandardOutput, Does.Contain("excluded empty"))
            Assert.That(FileInfo(pathA).Length, Is.Zero)

            let retained =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                |> Array.find (fun operation -> not operation.Terminal)

            Assert.That(retained.OperationId, Is.EqualTo(pending.OperationId))
            Assert.That(retained.SourceObject, Is.EqualTo(pending.SourceObject))
            Assert.That(retained.RequestJson, Is.EqualTo(pending.RequestJson))
            Assert.That(retained.Accepted.IsSome, Is.True)

            Assert.That(
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId)
                    .Value
                    .AppliedCursor,
                Is.EqualTo(before.Value.AppliedCursor)
            )

            Assert.That(proxy.ManifestUploadCount, Is.EqualTo(2))
            File.WriteAllText(pathA, "positive after zero")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            Assert.That(File.ReadAllText(pathB), Is.EqualTo("positive after zero"))
            Assert.That(File.Exists(zero), Is.True)

            Assert.That(
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                |> Array.filter (fun operation -> not operation.Terminal)
                |> Array.length,
                Is.Zero
            )

            Assert.That(proxy.ManifestUploadCount, Is.EqualTo(2))

            let completed =
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId)
                    .Value

            File.WriteAllText(pathA, "publication source")
            proxy.AfterNextManifestUpload(fun () -> File.WriteAllText(pathA, "distinct later positive"))

            localSql
                copyA
                "CREATE TRIGGER fail_zero_checkpoint BEFORE UPDATE OF applied_cursor ON library_repository_state WHEN NEW.applied_cursor <> OLD.applied_cursor BEGIN SELECT RAISE(ABORT, 'zero publication checkpoint'); END;"
            |> ignore

            let! published = runGraceAsync copyA proxy.BaseAddress (syncCommand "run")
            Assert.That(published.ExitCode, Is.Not.EqualTo(0))
            Assert.That(published.StandardOutput, Does.Contain("zero publication checkpoint"))
            Assert.That(File.ReadAllText(pathA), Is.EqualTo("publication source"))

            let prepared =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                |> Array.filter (fun operation -> not operation.Terminal)

            Assert.That(prepared.Length, Is.EqualTo(2))

            Assert.That(
                prepared[0].Prepared
                && prepared[0].Accepted.IsSome
                && prepared[0].RequestJson.IsSome,
                Is.True
            )

            Assert.That(
                File.ReadAllBytes(Path.Combine(copyA, ".grace", "objects", prepared[1].SourceObject.Value.ObjectPath)),
                Is.EqualTo<byte>(System.Text.Encoding.UTF8.GetBytes("distinct later positive"))
            )

            Assert.That(
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId)
                    .Value
                    .AppliedCursor,
                Is.EqualTo(completed.AppliedCursor)
            )

            File.WriteAllBytes(pathA, [||])

            localSql copyA "DROP TRIGGER fail_zero_checkpoint;"
            |> ignore

            let! zeroAfterPublication = runGraceAsync copyA proxy.BaseAddress (syncCommand "run")
            Assert.That(zeroAfterPublication.ExitCode, Is.Not.EqualTo(0))
            Assert.That(File.Exists(pathA), Is.True)
            Assert.That(FileInfo(pathA).Length, Is.Zero)

            let afterZero =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                |> Array.filter (fun operation -> not operation.Terminal)

            Assert.That(afterZero.Length, Is.EqualTo(2))
            Assert.That(afterZero[0], Is.EqualTo(prepared[0]))
            Assert.That(afterZero[1].OperationId, Is.EqualTo(prepared[1].OperationId))
            Assert.That(afterZero[1].SourceObject, Is.EqualTo(prepared[1].SourceObject))

            Assert.That(
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyA) repositoryId)
                    .Value
                    .AppliedCursor,
                Is.EqualTo(completed.AppliedCursor)
            )

            File.WriteAllText(pathA, "publication source")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            let conflict =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                |> Array.find (fun operation -> operation.OperationId = prepared[1].OperationId)

            Assert.That(conflict.Accepted.Value.Conflict.IsSome, Is.True)
            Assert.That(File.ReadAllText(pathB), Is.EqualTo("publication source"))

            Assert.That(
                File.ReadAllText(Path.Combine(copyB, "Library", conflict.Accepted.Value.Item.Namespace.Value.Name)),
                Is.EqualTo("distinct later positive")
            )
        }

    /// Keeps empty files at actual incoming mutation sources and destinations intact until an explicit local recovery.
    [<TestCase("update"); TestCase("rename-source"); TestCase("rename-target"); TestCase("delete")>]
    let ``incoming changes preserve excluded zero files`` mutation =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync mutation
            use proxy = createdProxy
            let pathA, pathB = Path.Combine(copyA, "Library", "source.txt"), Path.Combine(copyB, "Library", "source.txt")
            let renamed = Path.Combine(copyB, "Library", "renamed.txt")
            File.WriteAllText(pathA, "original positive")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            let before =
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value

            let original =
                Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId
                |> Array.exactlyOne

            let protectedPath = if mutation = "rename-target" then renamed else pathB
            File.WriteAllBytes(protectedPath, [||])

            if mutation = "update" then
                File.WriteAllText(pathA, "incoming positive")
                let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
                ()
            else
                let! _ = changeNamespaceAsync repositoryId original (mutation = "delete")
                ()

            do! pauseRetainingAsync copyB repositoryId proxy
            let! failed = runGraceAsync copyB proxy.BaseAddress (syncCommand "resume")
            Assert.That(failed.ExitCode, Is.Not.EqualTo(0))

            Assert.That(
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value
                    .Paused,
                Is.False
            )

            Assert.That(File.Exists(protectedPath), Is.True)
            Assert.That(FileInfo(protectedPath).Length, Is.Zero)

            Assert.That(
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value
                    .AppliedCursor,
                Is.EqualTo(before.AppliedCursor)
            )

            Assert.That(
                Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId
                |> Array.exactlyOne,
                Is.EqualTo(original)
            )

            Assert.That(
                Grace.CLI.LibraryLocalState.readOperations (localDb copyB) repositoryId
                |> Array.filter (fun operation -> operation.Direction = "local")
                |> Array.length,
                Is.Zero
            )

            if mutation = "rename-target" then
                File.Delete(renamed)
            else
                File.WriteAllText(pathB, "original positive")

            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            if mutation = "delete" then
                Assert.That(File.Exists(pathB), Is.False)
            elif mutation.StartsWith("rename") then
                Assert.That(File.Exists(pathB), Is.False)
                Assert.That(File.ReadAllText(renamed), Is.EqualTo("original positive"))
            else
                Assert.That(File.ReadAllText(pathB), Is.EqualTo("incoming positive"))
        }

    /// Interrupts a real rename between publication and removal, then preserves distinct saves at either path with their original base.
    [<TestCase(false); TestCase(true)>]
    let ``accepted saved edit and later partial rename save survive process restart`` saveAtDestination =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync "rename-saved"
            use proxy = createdProxy
            let pathA, pathB = Path.Combine(copyA, "Library", "source.txt"), Path.Combine(copyB, "Library", "source.txt")
            let renamed = Path.Combine(copyB, "Library", "renamed.txt")
            File.WriteAllText(pathA, "X")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            let before =
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value

            let original =
                Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId
                |> Array.exactlyOne

            let! rename = changeNamespaceAsync repositoryId original false
            File.WriteAllText(pathB, "Z1")
            let mutable held: FileStream option = None
            proxy.AfterNextContentRead(fun () -> held <- Some(new FileStream(pathB, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            let! interrupted = runGraceAsync copyB proxy.BaseAddress (syncCommand "run")

            held
            |> Option.iter (fun stream -> stream.Dispose())

            Assert.That(interrupted.ExitCode, Is.Not.EqualTo(0))
            Assert.That(File.ReadAllText(renamed), Is.EqualTo("X"))
            Assert.That(File.ReadAllText(pathB), Is.EqualTo("Z1"))

            Assert.That(
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value
                    .AppliedCursor,
                Is.EqualTo(before.AppliedCursor)
            )

            let first =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyB) repositoryId
                |> Array.find (fun operation ->
                    operation.Direction = "local"
                    && not operation.Terminal)

            Assert.That(first.Accepted.IsSome, Is.True)
            Assert.That(first.Accepted.Value.Item.ItemId, Is.EqualTo(original.ItemId))
            let configuration = GraceConfiguration()
            configuration.RootDirectory <- copyB
            configuration.ObjectDirectory <- Path.Combine(copyB, ".grace", "objects")
            configuration.GraceStatusFile <- localDb copyB
            configuration.RepositoryId <- repositoryId
            let operationsBeforeCapture = Grace.CLI.LibraryLocalState.readOperations (localDb copyB) repositoryId
            let uploadsBeforeCapture = proxy.ManifestUploadCount
            do! Grace.CLI.Command.LibrarySynchronization.classifyWatchObservations configuration [| renamed |] CancellationToken.None

            let scope =
                Grace.CLI.Command.WorkingDirectoryUpdateCoordination.Scope.create repositoryId copyB
                |> Result.defaultWith invalidOp

            do!
                task {
                    use! captureLease = Grace.CLI.Command.WorkingDirectoryUpdateCoordination.Lease.acquire scope CancellationToken.None
                    Assert.That(Grace.CLI.Command.LibrarySynchronization.captureSaved configuration, Is.False)

                    Assert.That(
                        Grace.CLI.LibraryLocalState.readOperations (localDb copyB) repositoryId,
                        Is.EqualTo<Grace.CLI.LibraryLocalState.PendingOperation>(operationsBeforeCapture)
                    )

                    File.WriteAllText(Path.Combine(copyB, "Library", "capture-control.txt"), "positive untracked capture")
                    Assert.That(Grace.CLI.Command.LibrarySynchronization.captureSaved configuration, Is.True)
                }

            Assert.That(proxy.ManifestUploadCount, Is.EqualTo(uploadsBeforeCapture))
            do! pauseRetainingAsync copyB repositoryId proxy
            File.WriteAllText((if saveAtDestination then renamed else pathB), "Z2")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "resume")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")

            let local =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyB) repositoryId
                |> Array.filter (fun operation ->
                    operation.Direction = "local"
                    && operation.SourcePath
                       <> "Library/capture-control.txt")

            Assert.That(local.Length, Is.EqualTo(2))

            let control =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyB) repositoryId
                |> Array.find (fun operation -> operation.SourcePath = "Library/capture-control.txt")

            Assert.That(
                control.Terminal
                && control.Uploaded
                && control.Accepted.IsSome,
                Is.True
            )

            Assert.That(control.Accepted.Value.ChangeKind, Is.EqualTo(ChangeKind.CreateFile))
            Assert.That(proxy.ManifestUploadCount, Is.EqualTo(uploadsBeforeCapture + 2))
            Assert.That(File.ReadAllText(Path.Combine(copyA, "Library", "capture-control.txt")), Is.EqualTo("positive untracked capture"))
            Assert.That(local[0].RequestJson, Is.EqualTo(first.RequestJson))
            let second = local[1]
            let request = deserialize<Parameters.Library.SubmitLibraryChangeParameters> second.RequestJson.Value
            Assert.That(request.ChangeKind, Is.EqualTo(ChangeKind.UpdateContent))
            Assert.That(request.ItemId.Value, Is.EqualTo(original.ItemId))
            Assert.That(request.ContentPrecondition.Value.ExpectedContentRevision, Is.EqualTo(original.ContentRevision.Value))
            Assert.That(second.Accepted.Value.Conflict.IsSome, Is.True)
            let conflictName = second.Accepted.Value.Item.Namespace.Value.Name
            Assert.That(File.ReadAllText(renamed), Is.EqualTo("Z1"))
            Assert.That(File.ReadAllText(Path.Combine(copyB, "Library", conflictName)), Is.EqualTo("Z2"))
            Assert.That(File.ReadAllText(Path.Combine(copyA, "Library", conflictName)), Is.EqualTo("Z2"))
            Assert.That(File.Exists(pathB), Is.False)

            Assert.That(
                local
                |> Array.forall (fun operation -> operation.Terminal),
                Is.True
            )

            Assert.That(
                (Grace.CLI.LibraryLocalState.readOperations (localDb copyB) repositoryId
                 |> Array.find (fun operation -> operation.OperationId = rename.OperationId)
                 |> fun operation -> operation.Terminal),
                Is.True
            )
        }

    /// Preserves rejected saved input after the real server has tombstoned its original item.
    [<Test>]
    let ``deletion first retains ItemTombstoned saved edit without resurrection`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync "delete-saved"
            use proxy = createdProxy
            let pathA, pathB = Path.Combine(copyA, "Library", "source.txt"), Path.Combine(copyB, "Library", "source.txt")
            File.WriteAllText(pathA, "X")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            let before =
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value

            let original =
                Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId
                |> Array.exactlyOne

            let! _ = changeNamespaceAsync repositoryId original true
            File.WriteAllText(pathB, "saved after server deletion")
            let! rejected = runGraceAsync copyB proxy.BaseAddress (syncCommand "run")
            Assert.That(rejected.ExitCode, Is.Not.EqualTo(0))
            Assert.That(rejected.StandardOutput, Does.Contain(RejectionReason.ItemTombstoned))

            let pending =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyB) repositoryId
                |> Array.find (fun operation -> operation.Direction = "local")

            let frozen = deserialize<Parameters.Library.SubmitLibraryChangeParameters> pending.RequestJson.Value
            use! receiptResponse = Client.PostAsync("/libraries/changes/submit", createJsonContent frozen)
            let! receipt = requireReturnValueAsync<LibraryOperationReceiptDto> receiptResponse
            Assert.That(receipt.ReasonCode, Is.EqualTo(Some RejectionReason.ItemTombstoned))
            Assert.That(receipt.Change.IsNone, Is.True)
            Assert.That(receipt.OperationId, Is.EqualTo(pending.OperationId))

            do! pauseRetainingAsync copyB repositoryId proxy
            let! retried = runGraceAsync copyB proxy.BaseAddress (syncCommand "resume")

            Assert.That(
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value
                    .Paused,
                Is.False
            )

            Assert.That(retried.ExitCode, Is.Not.EqualTo(0))
            Assert.That(retried.StandardOutput, Does.Contain(RejectionReason.ItemTombstoned))

            Assert.That(
                Grace.CLI.LibraryLocalState.readOperations (localDb copyB) repositoryId
                |> Array.find (fun operation -> operation.Direction = "local"),
                Is.EqualTo(pending)
            )

            Assert.That(pending.Terminal, Is.False)
            Assert.That(pending.Accepted.IsNone, Is.True)
            Assert.That(File.ReadAllText(pathB), Is.EqualTo("saved after server deletion"))

            Assert.That(
                Directory
                    .GetFiles(
                        Path.Combine(copyB, "Library")
                    )
                    .Length,
                Is.EqualTo(1)
            )

            Assert.That(
                (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                    .Value
                    .AppliedCursor,
                Is.EqualTo(before.AppliedCursor)
            )

            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            Assert.That(File.Exists(pathA), Is.False)

            Assert.That(
                (Grace.CLI.LibraryLocalState.readItems (localDb copyA) repositoryId
                 |> Array.exactlyOne)
                    .Tombstone
                    .IsSome,
                Is.True
            )
        }

    /// Proves an empty page and a later interruption between two item completions don't skip accepted changes, because
    /// every pull restarts from the applied cursor.
    [<Test>]
    let ``empty visibility page and partial page restart preserve every change`` () =
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

            proxy.UseSingleChangePages()
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
            Assert.That(proxy.RequestedCursors |> Array.last, Is.EqualTo(original.AppliedCursor))

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
            // The interrupted run asked for its second page from the cursor of the change it had just applied.
            Assert.That(proxy.RequestedCursors |> Array.last, Is.EqualTo(partial.AppliedCursor))

            localSql copyB "DROP TRIGGER fail_second_library_completion;"
            |> ignore

            let requestsBeforeResume = proxy.RequestedCursors.Length
            let! output = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            let resumed =
                proxy.RequestedCursors
                |> Array.skip requestsBeforeResume

            // The resumed run starts at the partial cursor and moves forward one page at a time, never asking for a page twice.
            Assert.That(resumed |> Array.head, Is.EqualTo(partial.AppliedCursor))
            Assert.That(resumed |> Array.distinct, Is.EqualTo(resumed))
            Assert.That(resumed.Length, Is.EqualTo(2), "One request for each of the two remaining one-change pages.")
            use status = System.Text.Json.JsonDocument.Parse(output)

            Assert.That(
                status
                    .RootElement
                    .GetProperty("ReturnValue")
                    .GetProperty("State")
                    .GetString(),
                Is.EqualTo("current")
            )

            Assert.That(
                proxy.RequestedCursors
                |> Array.contains partial.AppliedCursor,
                Is.True
            )

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
            do! pauseRetainingAsync copyA repositoryId proxy
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (command "resume")
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
                Assert.That(Convert.ToInt32(command.ExecuteScalar()), Is.EqualTo(4))
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
            do! pauseRetainingAsync copyA repositoryId proxy
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (command "resume")
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

                occupiedName <- Grace.Shared.Validation.Library.conflictName "saved.txt" saved.OperationId 0

                occupyDirectoryAsync repositoryId saved.Placement.Parent occupiedName
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

    /// Reads one copy's file in a Library, or None while it isn't there yet.
    let private libraryFile (copy: string) (relative: string) =
        let path = Path.Combine(copy, relative.Replace('/', Path.DirectorySeparatorChar))
        if File.Exists(path) then Some(File.ReadAllText(path)) else None

    /// Connects another working copy to the same main-branch save as a copy from connectWatchedCopyAsync, with the same
    /// token, so a second Watch can run against the same repository.
    let private connectAnotherWatchedCopyAsync (repositoryId: Guid) (branch: Grace.Types.Branch.BranchDto) token (copy: string) =
        task {
            Directory.CreateDirectory(copy) |> ignore
            let! current = BranchServerTestHelpers.getBranchAsync (string repositoryId) (string branch.BranchId)

            let! connected =
                runGraceWithTokenAsync
                    (Some token)
                    copy
                    graceServerBaseAddress
                    [|
                        "connect"
                        "--owner-id"
                        ownerId
                        "--organization-id"
                        organizationId
                        "--repository-id"
                        string repositoryId
                        "--server-address"
                        graceServerBaseAddress
                        "--reference-id"
                        string current.LatestSave.ReferenceId
                    |]

            Assert.That(connected.ExitCode, Is.Zero, connected.StandardOutput + connected.StandardError)

            Directory.CreateDirectory(Path.Combine(copy, "Library"))
            |> ignore
        }

    /// #1081: copies A and B both run Watch. An administrator runs `grace library add`. Both copies create the empty
    /// folder within seconds, with no restart and no extra command, and a file A then saves in the new Library reaches B.
    [<Test>]
    let ``a Library added while Watch runs on two copies appears on both and carries files`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! repositoryId = createRepositoryAsync ()
            do! addLibraryAsync repositoryId
            let root = Path.Combine(Path.GetTempPath(), $"grace-library-added-watch-{Guid.NewGuid():N}")
            Console.WriteLine($"Library working copies: {root}")
            let copyA, copyB = Path.Combine(root, "A"), Path.Combine(root, "B")
            let! branch, initialRoot, token = connectWatchedCopyAsync repositoryId copyA
            do! connectAnotherWatchedCopyAsync repositoryId branch token copyB

            /// Runs one CLI command in a copy with the credentials Watch uses.
            let runIn copy arguments =
                task {
                    let! result = runGraceWithTokenAsync (Some token) copy graceServerBaseAddress arguments
                    Assert.That(result.ExitCode, Is.Zero, result.StandardOutput + result.StandardError)
                    return result.StandardOutput
                }

            let! _ = runIn copyA (syncCommand "enable")
            let! _ = runIn copyB (syncCommand "enable")
            let watchA, outputA = startWatch copyA token
            use _watchA = watchA
            let watchB, outputB = startWatch copyB token
            use _watchB = watchB

            /// Describes both Watch processes for a failure message.
            let diagnostics () =
                String.Join(Environment.NewLine, Seq.append (Seq.map (fun line -> "A: " + line) outputA) (Seq.map (fun line -> "B: " + line) outputB))

            /// Waits for a condition while both Watch processes keep running, and returns how long it took.
            let within (limit: TimeSpan) condition =
                task {
                    let timer = Stopwatch.StartNew()

                    while not (condition ())
                          && not watchA.HasExited
                          && not watchB.HasExited
                          && timer.Elapsed < limit do
                        do! Task.Delay(100)

                    Assert.That(watchA.HasExited || watchB.HasExited, Is.False, diagnostics ())
                    Assert.That(condition (), Is.True, $"Not met within {limit.TotalSeconds} s.{Environment.NewLine}{diagnostics ()}")
                    return timer.Elapsed
                }

            try
                let! _ =
                    within (TimeSpan.FromSeconds 45.0) (fun () ->
                        [ outputA; outputB ]
                        |> List.forall (Seq.exists (fun line -> line.Contains("Starting timer."))))

                let! _ =
                    runIn
                        copyA
                        [|
                            "library"
                            "add"
                            "Second"
                            "--output"
                            "Json"
                        |]

                // Watch starts a sync run at least every 5 seconds; allow three intervals.
                let! folders =
                    within (TimeSpan.FromSeconds 15.0) (fun () ->
                        Directory.Exists(Path.Combine(copyA, "Second"))
                        && Directory.Exists(Path.Combine(copyB, "Second")))

                File.WriteAllText(Path.Combine(copyA, "Second", "hello.txt"), "saved in the added Library")

                // A's Watch captures and submits it, and B's Watch pulls it.
                let! arrived = within (TimeSpan.FromSeconds 20.0) (fun () -> libraryFile copyB "Second/hello.txt" = Some "saved in the added Library")

                TestContext.Progress.WriteLine(
                    $"Both folders appeared after {folders.TotalSeconds:n1} s; the file reached B after {arrived.TotalSeconds:n1} s."
                )

                // B's file appears during its Library sync, before B's Watch processes version-control changes for that
                // tick. Let both Watches run several more ticks first. LatestSave only moves forward, so if either one
                // saved anything since the seed, including Library content, it no longer points at the seed.
                do! Task.Delay(TimeSpan.FromSeconds 10.0)
                Assert.That(watchA.HasExited || watchB.HasExited, Is.False, diagnostics ())
                let! afterBranch = BranchServerTestHelpers.getBranchAsync (string repositoryId) (string branch.BranchId)

                Assert.That(
                    afterBranch.LatestSave.DirectoryId,
                    Is.EqualTo(initialRoot.DirectoryVersionId),
                    "Library content entered version-control Save history."
                )
            finally
                for watch in [ watchA; watchB ] do
                    if not watch.HasExited then watch.Kill(true)
                    watch.WaitForExit()

                TestContext.Progress.WriteLine(diagnostics ())
        }

    /// Submits one Library change as an administrator would and returns its accepted change.
    let private submitAcceptedAsync (repositoryId: Guid) (request: Parameters.Library.SubmitLibraryChangeParameters) =
        task {
            request.OwnerId <- ownerId
            request.OrganizationId <- organizationId
            request.RepositoryId <- repositoryId.ToString("D")
            request.OperationId <- Guid.NewGuid()
            request.CorrelationId <- generateCorrelationId ()
            use! response = Client.PostAsync("/libraries/changes/submit", createJsonContent request)
            let! receipt = requireReturnValueAsync<LibraryOperationReceiptDto> response
            Assert.That(receipt.Change.IsSome, Is.True, serialize receipt)
            return receipt.Change.Value
        }

    /// #1081 review: while a copy is away, a Library is added, a directory is created and deleted in it, and the Library
    /// is removed again. The copy's catalog only gained a version, but the journal still holds that create. The copy stops
    /// with the removal next step before writing anything, even where an ordinary folder of that name exists. When a
    /// Library is then added at the deleted item's own path, the old create and delete flatten to that new Library's
    /// path; they still belong to the removed root, so replay stops before deleting the new folder.
    [<TestCase(false); TestCase(true)>]
    let ``a change in a Library removed while a copy was away stops without writing outside the Libraries`` readded =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! _, copyB, repositoryId, createdProxy = enableCopiesAsync "removed-away"
            use proxy = createdProxy

            let ordinary =
                Directory
                    .CreateDirectory(
                        Path.Combine(copyB, "Gone")
                    )
                    .FullName

            let! _ = changeLibraryAsync repositoryId true "Gone"
            let parent = { Kind = "root"; LibraryPath = Some "Gone"; ItemId = None }

            let create = Parameters.Library.SubmitLibraryChangeParameters()
            create.ChangeKind <- ChangeKind.CreateDirectory
            create.ItemKind <- ItemKind.Directory
            create.DestinationParent <- Some parent
            create.DestinationName <- "temporary"
            let! created = submitAcceptedAsync repositoryId create

            let delete = Parameters.Library.SubmitLibraryChangeParameters()
            delete.ChangeKind <- ChangeKind.Delete
            delete.ItemKind <- ItemKind.Directory
            delete.ItemId <- Nullable created.Item.ItemId
            delete.NamespacePrecondition <- Some { ItemId = created.Item.ItemId; ExpectedNamespaceVersion = created.Item.Namespace.Value.NamespaceVersion }
            let! _ = submitAcceptedAsync repositoryId delete
            let! _ = changeLibraryAsync repositoryId false "Gone"

            if readded then
                let! _ = changeLibraryAsync repositoryId true "Gone/temporary"
                ()

            let before =
                Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId
                |> Option.get

            let! stopped = runGraceAsync copyB proxy.BaseAddress (syncCommand "run")
            let! status = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "status")

            let after =
                Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId
                |> Option.get

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(stopped.ExitCode, Is.Not.Zero, stopped.StandardOutput)
                    Assert.That(status, Does.Contain("A Library was removed from this repository or moved"))

                    if readded then
                        Assert.That(Directory.Exists(Path.Combine(ordinary, "temporary")), Is.True, "Replay deleted the Library added later at that path.")
                    else
                        Assert.That(Directory.Exists(Path.Combine(ordinary, "temporary")), Is.False, "Sync wrote outside every Library.")

                    Assert.That(after.AppliedCursor, Is.EqualTo(before.AppliedCursor)))
            )
        }

    /// #1081: a copy that wasn't running while two Libraries were added and filled creates both folders and downloads
    /// their files on its next run, and finishes current.
    [<Test>]
    let ``a copy that was away while two Libraries were added catches up`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync "added-away"
            use proxy = createdProxy
            let! _ = changeLibraryAsync repositoryId true "Second"
            let! _ = changeLibraryAsync repositoryId true "Third"
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")

            Directory.CreateDirectory(Path.Combine(copyA, "Third", "nested"))
            |> ignore

            File.WriteAllText(Path.Combine(copyA, "Second", "second.txt"), "in Second")
            File.WriteAllText(Path.Combine(copyA, "Third", "nested", "third.txt"), "in Third")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")

            let! status = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! latest = catalogAsync repositoryId

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(libraryFile copyB "Second/second.txt", Is.EqualTo(Some "in Second"))
                    Assert.That(libraryFile copyB "Third/nested/third.txt", Is.EqualTo(Some "in Third"))
                    Assert.That(status, Does.Contain("\"State\": \"current\""))

                    Assert.That(
                        (Grace.CLI.LibraryLocalState.readRepository (localDb copyB) repositoryId)
                            .Value
                            .Catalog,
                        Is.EqualTo(latest)
                    ))
            )
        }

    /// #1081, PR #1082's captured-save case: a save is captured and its request frozen, an administrator adds another
    /// Library before the request reaches the server, and the accepted response is lost. The save keeps its operation,
    /// the next run resubmits the same request and gets its receipt, and the file lands on the other copy.
    [<Test>]
    let ``a save captured before a Library is added lands after it`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync "added-captured"
            use proxy = createdProxy
            File.WriteAllText(Path.Combine(copyA, "Library", "captured.txt"), "captured before the addition")

            proxy.BeforeNextSubmit (fun () ->
                task {
                    let! _ = changeLibraryAsync repositoryId true "Second"
                    ()
                })

            proxy.DropNextAcceptedSubmitResponse()
            let! interrupted = runGraceAsync copyA proxy.BaseAddress (syncCommand "run")
            Assert.That(interrupted.ExitCode, Is.Not.Zero, interrupted.StandardOutput)
            Assert.That(proxy.DroppedAcceptedSubmitCount, Is.EqualTo(1))

            let captured =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                |> Array.filter (fun operation -> operation.Direction = "local")

            Assert.That(captured.Length, Is.EqualTo(1))

            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            let finished =
                Grace.CLI.LibraryLocalState.readOperations (localDb copyA) repositoryId
                |> Array.filter (fun operation -> operation.Direction = "local")

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(
                        finished
                        |> Array.map (fun operation -> operation.OperationId),
                        Is.EqualTo(
                            captured
                            |> Array.map (fun operation -> operation.OperationId)
                        )
                    )

                    Assert.That(
                        finished
                        |> Array.forall (fun operation -> operation.Terminal),
                        Is.True
                    )

                    Assert.That(proxy.SubmitRequestCount, Is.EqualTo(2), "The save is submitted once and resubmitted once, under one operation.")
                    Assert.That(Directory.Exists(Path.Combine(copyA, "Second")), Is.True)
                    Assert.That(libraryFile copyB "Library/captured.txt", Is.EqualTo(Some "captured before the addition")))
            )
        }

    /// #1081: when an added Library's folder already has the user's own files on a copy, that copy waits, says which
    /// folder and what to do, and never touches the files. The other copy keeps syncing. Once the folder is empty, the
    /// waiting copy picks the Library up.
    [<Test>]
    let ``an added Library whose folder already has files on a copy waits there and keeps the files`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync "added-occupied"
            use proxy = createdProxy

            let mine =
                Path.Combine(
                    Directory
                        .CreateDirectory(
                            Path.Combine(copyB, "Second")
                        )
                        .FullName,
                    "mine.txt"
                )

            File.WriteAllText(mine, "the user's own file")
            let! _ = changeLibraryAsync repositoryId true "Second"
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            File.WriteAllText(Path.Combine(copyA, "Second", "shared.txt"), "shared in Second")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")

            let! waiting = runGraceAsync copyB proxy.BaseAddress (syncCommand "run")
            let! status = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "status")

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(waiting.ExitCode, Is.Not.Zero)
                    Assert.That(status, Does.Contain("\"LastErrorPath\": \"Second\""))
                    Assert.That(status, Does.Contain("Move the files out of \\u0027Second\\u0027"))
                    Assert.That(File.ReadAllText(mine), Is.EqualTo("the user's own file"))
                    Assert.That(libraryFile copyB "Second/shared.txt", Is.EqualTo(None)))
            )

            File.Delete(mine)
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            Assert.That(libraryFile copyB "Second/shared.txt", Is.EqualTo(Some "shared in Second"))
            Assert.That(libraryFile copyA "Second/mine.txt", Is.EqualTo(None), "The user's own file must never sync.")
        }

    /// #1081: a repository that already has two Libraries can enable sync, and enable creates both folders.
    [<Test>]
    let ``enable works in a repository with two Libraries`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let root = Path.Combine(Path.GetTempPath(), $"grace-library-two-{Guid.NewGuid():N}")

            let copy =
                Directory
                    .CreateDirectory(
                        Path.Combine(root, "A")
                    )
                    .FullName

            let! repositoryId = createRepositoryAsync ()
            do! addLibraryAsync repositoryId
            let! _ = changeLibraryAsync repositoryId true "Second"
            use proxy = new AuthenticatedProxy(graceServerBaseAddress, testUserId)
            configureWorkingCopy copy repositoryId proxy.BaseAddress
            let! enabled = requireGraceSuccessAsync copy proxy.BaseAddress (syncCommand "enable")

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(enabled, Does.Contain("\"Enabled\": true"))
                    Assert.That(Directory.Exists(Path.Combine(copy, "Library")), Is.True)
                    Assert.That(Directory.Exists(Path.Combine(copy, "Second")), Is.True))
            )
        }

    /// Runs one sync command in a copy with an editor's save landing in a Library file just after sync's last check of
    /// it and just before sync replaces, moves aside or deletes it (#1122). The name limits the save to that file.
    let private runWithSaveBeforeDisplaceAsync copy serverUri (text: string) (name: string) =
        runGraceWithEnvironmentAsync
            [
                "GRACE_TESTING", "1"
                Grace.CLI.Command.LibraryFilesystem.SaveBeforeDisplaceVariable, text
                Grace.CLI.Command.LibraryFilesystem.SaveBeforeDisplaceNameVariable, name
            ]
            None
            copy
            serverUri
            (syncCommand "run")

    /// Lists a copy's Library files other than the named ones.
    let private otherLibraryFiles copy (names: string list) =
        Directory.GetFiles(Path.Combine(copy, "Library"))
        |> Array.filter (fun path -> not (names |> List.contains (Path.GetFileName path)))

    /// #1122: A edits a synced file, and B saves its own version at the instant B's sync run replaces the file with A's,
    /// after sync's last check. B ends with A's version at the path and its own save next to it under the conflict-copy
    /// name, and the next runs carry that copy to A.
    [<Test>]
    let ``a save that lands as an incoming edit replaces the file is kept as a conflict copy`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, _, createdProxy = enableCopiesAsync "displace-edit"
            use proxy = createdProxy
            let pathA, pathB = Path.Combine(copyA, "Library", "plan.txt"), Path.Combine(copyB, "Library", "plan.txt")
            File.WriteAllText(pathA, "first version")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            File.WriteAllText(pathA, "edited on A")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")

            let! applied = runWithSaveBeforeDisplaceAsync copyB proxy.BaseAddress "saved on B" "plan.txt"
            Assert.That(applied.ExitCode, Is.Zero, applied.StandardOutput + applied.StandardError)
            Assert.That(File.ReadAllText(pathB), Is.EqualTo("edited on A"))
            let kept = otherLibraryFiles copyB [ "plan.txt" ]
            Assert.That(kept.Length, Is.EqualTo(1), "B's save wasn't kept next to the file.")
            Assert.That(File.ReadAllText(kept[0]), Is.EqualTo("saved on B"))

            Assert.That(
                Path.GetFileName(kept[0]),
                Does
                    .StartWith("plan.conflict-")
                    .And.EndWith(".txt")
            )

            // The kept copy is an ordinary Library file, so it syncs.
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            Assert.That(File.ReadAllText(Path.Combine(copyA, "Library", Path.GetFileName(kept[0]))), Is.EqualTo("saved on B"))
            Assert.That(File.ReadAllText(pathA), Is.EqualTo("edited on A"))
        }

    /// #1122: A deletes a synced file, and B saves it at the instant B's sync run deletes it, after sync's last check.
    /// B's save stays at its path, and sync stops with the existing message instead of deleting it.
    [<Test>]
    let ``a save that lands as an incoming delete removes the file is put back`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync "displace-delete"
            use proxy = createdProxy
            let pathB = Path.Combine(copyB, "Library", "plan.txt")
            File.WriteAllText(Path.Combine(copyA, "Library", "plan.txt"), "first version")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            let original =
                Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId
                |> Array.exactlyOne

            let! _ = changeNamespaceAsync repositoryId original true

            let! stopped = runWithSaveBeforeDisplaceAsync copyB proxy.BaseAddress "saved on B" "plan.txt"
            Assert.That(stopped.ExitCode, Is.Not.Zero, stopped.StandardOutput + stopped.StandardError)
            Assert.That(File.ReadAllText(pathB), Is.EqualTo("saved on B"))
            Assert.That(otherLibraryFiles copyB [ "plan.txt" ], Is.Empty)
            let! status = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "status")
            Assert.That(status, Does.Contain("would remove locally changed bytes"))
        }

    /// #1122: A renames a synced file, and B saves the old path at the instant B's sync run removes it, after sync's last
    /// check. The renamed file arrives, and B's save stays at the old path.
    [<Test>]
    let ``a save to a renamed file's old path that lands as sync removes it is put back`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync "displace-rename"
            use proxy = createdProxy
            let pathB = Path.Combine(copyB, "Library", "source.txt")
            File.WriteAllText(Path.Combine(copyA, "Library", "source.txt"), "first version")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            let original =
                Grace.CLI.LibraryLocalState.readItems (localDb copyB) repositoryId
                |> Array.exactlyOne

            let! _ = changeNamespaceAsync repositoryId original false

            let! stopped = runWithSaveBeforeDisplaceAsync copyB proxy.BaseAddress "saved on B" "source.txt"
            Assert.That(stopped.ExitCode, Is.Not.Zero, stopped.StandardOutput + stopped.StandardError)
            Assert.That(File.ReadAllText(Path.Combine(copyB, "Library", "renamed.txt")), Is.EqualTo("first version"))
            Assert.That(File.ReadAllText(pathB), Is.EqualTo("saved on B"))
            Assert.That(otherLibraryFiles copyB [ "source.txt"; "renamed.txt" ], Is.Empty)
        }

    /// Orders a copy's Library files, as Library-relative path and text, the same way for every copy.
    let private sortContents (contents: (string * string) array) =
        contents
        |> Array.sortWith (fun (left, _) (right, _) -> String.CompareOrdinal(left, right))

    /// Lists every file under a copy's Library folder as its Library-relative path and text.
    let private libraryContents copy =
        let library = Path.Combine(copy, "Library")

        Directory.GetFiles(library, "*", SearchOption.AllDirectories)
        |> Array.map (fun path ->
            Path
                .GetRelativePath(library, path)
                .Replace('\\', '/'),
            File.ReadAllText path)
        |> sortContents

    /// Requires every copy to hold exactly these Library files, with no extra folder, and to be current with nothing
    /// pending.
    let private requireConvergedAsync serverUri (copies: string array) (expected: (string * string) array) (folders: string array) =
        task {
            let mutable index = 0

            while index < copies.Length do
                let copy = copies[index]
                let library = Path.Combine(copy, "Library")

                let actualFolders =
                    Directory.GetDirectories(library, "*", SearchOption.AllDirectories)
                    |> Array.map (fun path ->
                        Path
                            .GetRelativePath(library, path)
                            .Replace('\\', '/'))
                    |> Array.sort

                Assert.That(libraryContents copy, Is.EqualTo<(string * string) array>(sortContents expected), copy)
                Assert.That(actualFolders, Is.EqualTo<string array>(Array.sort folders), copy)
                let! status = requireGraceSuccessAsync copy serverUri (syncCommand "status")
                Assert.That(status, Does.Contain("\"State\": \"current\""), status)
                Assert.That(status, Does.Contain("\"PendingOperationCount\": 0"), status)
                index <- index + 1
        }

    /// Returns the operation of a copy's own create at one repository-relative Library path.
    let private localCreateId copy repositoryId (path: string) =
        let create =
            Grace.CLI.LibraryLocalState.readOperations (localDb copy) repositoryId
            |> Array.find (fun operation ->
                operation.Direction = "local"
                && operation.SourcePath = path)

        create.OperationId

    /// Names the conflict copy a create's own operation gives a taken name.
    let private conflictCopyName name operationId = Grace.Shared.Validation.Library.conflictName name operationId 0

    /// Creates a file over HTTP, as another copy would, and returns its accepted item.
    let private createFileOverHttpAsync (repositoryId: Guid) name (text: string) =
        task {
            let operation = Guid.NewGuid()
            let! prepared = LibraryHttp.prepare (repositoryId.ToString("D")) operation (Text.Encoding.UTF8.GetBytes text)
            let create = Parameters.Library.SubmitLibraryChangeParameters()
            create.OwnerId <- ownerId
            create.OrganizationId <- organizationId
            create.RepositoryId <- repositoryId.ToString("D")
            create.OperationId <- operation
            create.ChangeKind <- ChangeKind.CreateFile
            create.ItemKind <- ItemKind.File
            create.UploadSessionId <- Nullable prepared.UploadSessionId
            create.DestinationParent <- Some { Kind = "root"; LibraryPath = Some "Library"; ItemId = None }
            create.DestinationName <- name
            create.CorrelationId <- generateCorrelationId ()
            use! response = Client.PostAsync("/libraries/changes/submit", createJsonContent create)
            let! receipt = requireReturnValueAsync<LibraryOperationReceiptDto> response
            Assert.That(receipt.Outcome, Is.EqualTo(OutcomeKind.Accepted), serialize receipt)
            return receipt.Change.Value.Item
        }

    /// #1127: A and B create notes.txt before either syncs, and every run succeeds. With different bytes, both copies
    /// end with A's version at the name and B's beside it, under the conflict-copy name of B's create. With the same
    /// bytes, both copies end with one file and one item.
    [<TestCase(false); TestCase(true)>]
    let ``two copies that create the same file name both keep their version`` identical =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync (if identical then "same-name-same" else "same-name-different")
            use proxy = createdProxy
            let textA = "created on A"
            let textB = if identical then textA else "created on B"
            File.WriteAllText(Path.Combine(copyA, "Library", "notes.txt"), textA)
            File.WriteAllText(Path.Combine(copyB, "Library", "notes.txt"), textB)
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let createB = localCreateId copyB repositoryId "Library/notes.txt"

            let expected =
                if identical then
                    [| "notes.txt", textA |]
                else
                    [|
                        "notes.txt", textA
                        conflictCopyName "notes.txt" createB, textB
                    |]

            do! requireConvergedAsync proxy.BaseAddress [| copyA; copyB |] expected [||]

            if identical then
                let liveItems copy =
                    Grace.CLI.LibraryLocalState.readItems (localDb copy) repositoryId
                    |> Array.filter (fun item -> item.Tombstone.IsNone)
                    |> Array.map (fun item -> item.ItemId)

                Assert.That(liveItems copyA, Has.Length.EqualTo(1))
                Assert.That(liveItems copyB, Is.EqualTo<Guid array>(liveItems copyA))
        }

    /// #1127: A's create of the same name lands after B has frozen its own create and just before B's request reaches
    /// the server. B still keeps both versions, exactly as if A had synced first.
    [<Test>]
    let ``a same-name create that lands while the other copy submits keeps both versions`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync "same-name-race"
            use proxy = createdProxy
            File.WriteAllText(Path.Combine(copyA, "Library", "notes.txt"), "created on A")
            File.WriteAllText(Path.Combine(copyB, "Library", "notes.txt"), "created on B")

            proxy.BeforeNextSubmit (fun () ->
                task {
                    let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
                    ()
                }
                :> Task)

            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let createB = localCreateId copyB repositoryId "Library/notes.txt"

            do!
                requireConvergedAsync
                    proxy.BaseAddress
                    [| copyA; copyB |]
                    [|
                        "notes.txt", "created on A"
                        conflictCopyName "notes.txt" createB, "created on B"
                    |]
                    [||]
        }

    /// #1127: B's create fails to reach the server once, and meanwhile another copy takes the name and gives it up. The
    /// name is free again, so B's create lands there.
    [<Test>]
    let ``a create waiting to submit takes a name that was used and freed meanwhile`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync "same-name-freed"
            use proxy = createdProxy
            File.WriteAllText(Path.Combine(copyB, "Library", "notes.txt"), "created on B")
            let freed = ref None

            proxy.BeforeNextSubmit (fun () ->
                task {
                    let! held = createFileOverHttpAsync repositoryId "notes.txt" "held briefly elsewhere"
                    let! deleted = changeNamespaceAsync repositoryId held true
                    freed.Value <- Some deleted
                    return raise (InvalidOperationException("Injected submit failure."))
                }
                :> Task)

            let! failed = runGraceAsync copyB proxy.BaseAddress (syncCommand "run")
            Assert.That(failed.ExitCode, Is.Not.Zero, failed.StandardOutput + failed.StandardError)

            // The name really was taken and given up while B's create waited.
            Assert.That(
                freed.Value
                |> Option.exists (fun deleted -> deleted.Item.Tombstone.IsSome),
                Is.True,
                "The other copy's create and delete didn't commit."
            )

            let createB = localCreateId copyB repositoryId "Library/notes.txt"
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            do! requireConvergedAsync proxy.BaseAddress [| copyA; copyB |] [| "notes.txt", "created on B" |] [||]

            // The file at the name is B's own frozen create, not a new one.
            let liveAtName copy =
                Grace.CLI.LibraryLocalState.readItems (localDb copy) repositoryId
                |> Array.filter (fun item -> item.Tombstone.IsNone)
                |> Array.map (fun item -> item.ItemId)

            let createdByB = Grace.Actors.LibraryDecision.deterministicGuid repositoryId createB "item"
            Assert.That(liveAtName copyA, Is.EqualTo<Guid array>([| createdByB |]))
            Assert.That(liveAtName copyB, Is.EqualTo<Guid array>([| createdByB |]))
        }

    /// #1127: A and B both create Photos. A adds a.txt, B adds b.txt, and both add cover.txt with different bytes. Every
    /// copy ends with one Photos folder holding all of them and one conflict copy of cover.txt.
    [<Test>]
    let ``two copies that create the same folder end with one folder holding both sets of files`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync "same-name-folder"
            use proxy = createdProxy
            let photosA, photosB = Path.Combine(copyA, "Library", "Photos"), Path.Combine(copyB, "Library", "Photos")
            Directory.CreateDirectory(photosA) |> ignore
            Directory.CreateDirectory(photosB) |> ignore
            File.WriteAllText(Path.Combine(photosA, "a.txt"), "only on A")
            File.WriteAllText(Path.Combine(photosA, "cover.txt"), "cover from A")
            File.WriteAllText(Path.Combine(photosB, "b.txt"), "only on B")
            File.WriteAllText(Path.Combine(photosB, "cover.txt"), "cover from B")

            // A copy captures a folder's files once the folder has synced, so each copy needs two runs.
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let coverB = localCreateId copyB repositoryId "Library/Photos/cover.txt"

            do!
                requireConvergedAsync
                    proxy.BaseAddress
                    [| copyA; copyB |]
                    [|
                        "Photos/a.txt", "only on A"
                        "Photos/b.txt", "only on B"
                        "Photos/cover.txt", "cover from A"
                        "Photos/" + conflictCopyName "cover.txt" coverB, "cover from B"
                    |]
                    [| "Photos" |]
        }

    /// #1127, and the case PR #1123 couldn't verify: A edits plan.txt, and B and C each keep a late save as sync replaces
    /// it (#1122). Both kept files get the same conflict-copy name, from A's edit. The second to sync keeps both again,
    /// under its own create's conflict-copy name, and all three copies end with all four versions.
    [<Test>]
    let ``two copies that keep a late save for the same incoming edit both keep it`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync "same-name-late-saves"
            use proxy = createdProxy
            let copyC = Path.Combine(Path.GetDirectoryName(copyB), "C")
            Directory.CreateDirectory(copyC) |> ignore
            configureWorkingCopy copyC repositoryId proxy.BaseAddress
            let! _ = requireGraceSuccessAsync copyC proxy.BaseAddress (syncCommand "enable")
            File.WriteAllText(Path.Combine(copyA, "Library", "plan.txt"), "first version")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyC proxy.BaseAddress (syncCommand "run")
            File.WriteAllText(Path.Combine(copyA, "Library", "plan.txt"), "edited on A")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")

            let! savedB = runWithSaveBeforeDisplaceAsync copyB proxy.BaseAddress "saved on B" "plan.txt"
            Assert.That(savedB.ExitCode, Is.Zero, savedB.StandardOutput + savedB.StandardError)

            let keptB =
                Path.GetFileName(
                    (otherLibraryFiles copyB [ "plan.txt" ]
                     |> Array.exactlyOne)
                )

            // C keeps its save under the same name, because both are named from A's edit, so C's create of it meets B's.
            let! savedC = runWithSaveBeforeDisplaceAsync copyC proxy.BaseAddress "saved on C" "plan.txt"
            Assert.That(savedC.ExitCode, Is.Zero, savedC.StandardOutput + savedC.StandardError)

            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyC proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let createC = localCreateId copyC repositoryId ("Library/" + keptB)

            do!
                requireConvergedAsync
                    proxy.BaseAddress
                    [| copyA; copyB; copyC |]
                    [|
                        "plan.txt", "edited on A"
                        keptB, "saved on B"
                        conflictCopyName keptB createC, "saved on C"
                    |]
                    [||]
        }

    /// #1127: the proxy loses the receipt for B's conflict copy. B's next run resubmits the same request, gets the same
    /// receipt, and every copy ends with exactly one conflict copy.
    [<Test>]
    let ``a lost conflict-copy receipt leaves exactly one conflict copy`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync "same-name-lost-receipt"
            use proxy = createdProxy
            File.WriteAllText(Path.Combine(copyA, "Library", "notes.txt"), "created on A")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            File.WriteAllText(Path.Combine(copyB, "Library", "notes.txt"), "created on B")
            proxy.DropNextAcceptedSubmitResponse()
            let! lost = runGraceAsync copyB proxy.BaseAddress (syncCommand "run")
            Assert.That(lost.ExitCode, Is.Not.Zero, lost.StandardOutput + lost.StandardError)
            Assert.That(proxy.DroppedAcceptedSubmitCount, Is.EqualTo(1))
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let createB = localCreateId copyB repositoryId "Library/notes.txt"

            do!
                requireConvergedAsync
                    proxy.BaseAddress
                    [| copyA; copyB |]
                    [|
                        "notes.txt", "created on A"
                        conflictCopyName "notes.txt" createB, "created on B"
                    |]
                    [||]
        }

    /// The composed (NFC) and decomposed spellings of 'café', written as escapes so an editor can't normalize either.
    let private composedCafe, decomposedCafe = "café", "café"

    /// #1133: B creates a folder or a file whose name is decomposed, as names from macOS often are. Every copy ends with
    /// it under the composed name the server stores, and nothing is left under the decomposed one.
    [<TestCase(true); TestCase(false)>]
    let ``a decomposed Library name syncs under its composed spelling`` folder =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, _, createdProxy = enableCopiesAsync (if folder then "nfd-folder" else "nfd-file")
            use proxy = createdProxy
            let libraryB = Path.Combine(copyB, "Library")

            if folder then
                Directory.CreateDirectory(Path.Combine(libraryB, decomposedCafe))
                |> ignore

                File.WriteAllText(Path.Combine(libraryB, decomposedCafe, "cover.txt"), "cover from B")
            else
                File.WriteAllText(Path.Combine(libraryB, decomposedCafe + ".txt"), "notes from B")

            // A copy captures a folder's files once the folder has synced, so B needs two runs.
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")

            if folder then
                do!
                    requireConvergedAsync
                        proxy.BaseAddress
                        [| copyA; copyB |]
                        [|
                            composedCafe + "/cover.txt", "cover from B"
                        |]
                        [| composedCafe |]
            else
                do!
                    requireConvergedAsync
                        proxy.BaseAddress
                        [| copyA; copyB |]
                        [|
                            composedCafe + ".txt", "notes from B"
                        |]
                        [||]
        }

    /// #1133: A creates 'café' and B creates its decomposed spelling, each with a file inside, before either syncs. B's
    /// folder takes the composed name, the server merges the two folders, and every copy ends with one folder holding
    /// both files.
    [<Test>]
    let ``a composed folder and a decomposed folder made on two copies end as one folder`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, _, createdProxy = enableCopiesAsync "nfd-two-copies"
            use proxy = createdProxy
            let folderA = Path.Combine(copyA, "Library", composedCafe)
            let folderB = Path.Combine(copyB, "Library", decomposedCafe)
            Directory.CreateDirectory(folderA) |> ignore
            Directory.CreateDirectory(folderB) |> ignore
            File.WriteAllText(Path.Combine(folderA, "a.txt"), "only on A")
            File.WriteAllText(Path.Combine(folderB, "b.txt"), "only on B")

            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")

            do!
                requireConvergedAsync
                    proxy.BaseAddress
                    [| copyA; copyB |]
                    [|
                        composedCafe + "/a.txt", "only on A"
                        composedCafe + "/b.txt", "only on B"
                    |]
                    [| composedCafe |]
        }

    /// #1133: a copy that holds both spellings side by side can't tell which one the person means, so sync stops with a
    /// message naming both and renames or loses nothing. Once the person keeps one, sync carries on.
    [<Test>]
    let ``a copy holding both spellings of one name stops and changes neither`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, repositoryId, createdProxy = enableCopiesAsync "nfd-both-spellings"
            use proxy = createdProxy
            let composedA = Path.Combine(copyA, "Library", composedCafe)
            Directory.CreateDirectory(composedA) |> ignore
            File.WriteAllText(Path.Combine(composedA, "a.txt"), "only on A")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            let composedB = Path.Combine(copyB, "Library", composedCafe)
            let decomposedB = Path.Combine(copyB, "Library", decomposedCafe)
            Directory.CreateDirectory(decomposedB) |> ignore
            File.WriteAllText(Path.Combine(decomposedB, "b.txt"), "only on B")

            let! stopped = runGraceAsync copyB proxy.BaseAddress (syncCommand "run")
            Assert.That(stopped.ExitCode, Is.Not.Zero, stopped.StandardOutput + stopped.StandardError)

            let failure =
                (Grace.CLI.LibraryLocalState.readHealth (localDb copyB) repositoryId)
                    .LastFailure

            Assert.That(failure.IsSome, Is.True, stopped.StandardOutput + stopped.StandardError)
            let message = failure.Value.Message
            Assert.That(message.Contains($"'{composedCafe}'", StringComparison.Ordinal), Is.True, message)
            Assert.That(message.Contains("'cafe' + U+0301", StringComparison.Ordinal), Is.True, message)
            Assert.That(failure.Value.Path, Is.EqualTo(Some $"Library/{decomposedCafe}"))
            Assert.That(failure.Value.NextStep, Is.EqualTo(LibrarySynchronization.twoSpellingsNextStep))

            // Nothing was renamed, moved, or lost.
            Assert.That(
                libraryContents copyB,
                Is.EqualTo<(string * string) array>(
                    sortContents [| composedCafe + "/a.txt", "only on A"
                                    decomposedCafe + "/b.txt", "only on B" |]
                )
            )

            // The person keeps the composed folder, and sync carries on.
            File.Move(Path.Combine(decomposedB, "b.txt"), Path.Combine(composedB, "b.txt"))
            Directory.Delete(decomposedB)
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")

            do!
                requireConvergedAsync
                    proxy.BaseAddress
                    [| copyA; copyB |]
                    [|
                        composedCafe + "/a.txt", "only on A"
                        composedCafe + "/b.txt", "only on B"
                    |]
                    [| composedCafe |]
        }

    /// #1133: B's saved edit inside 'café' is waiting to submit when the folder's name turns decomposed on disk, as when a
    /// tool from macOS rewrites it. Capture gives the folder its composed name back before applying anything, so the
    /// edit's recorded path still names the file, the edit lands, and nothing is captured twice.
    [<Test>]
    let ``a pending edit inside a folder whose name turns decomposed still lands`` () =
        task {
            if not (OperatingSystem.IsWindows()) then
                Assert.Ignore("Windows filesystem contract.")

            let! copyA, copyB, _, createdProxy = enableCopiesAsync "nfd-pending-edit"
            use proxy = createdProxy
            let composedA = Path.Combine(copyA, "Library", composedCafe)
            Directory.CreateDirectory(composedA) |> ignore
            File.WriteAllText(Path.Combine(composedA, "cover.txt"), "first version")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")

            let composedB = Path.Combine(copyB, "Library", composedCafe)
            File.WriteAllText(Path.Combine(composedB, "cover.txt"), "edited on B")

            proxy.BeforeNextSubmit (fun () ->
                Directory.Move(composedB, Path.Combine(copyB, "Library", decomposedCafe))
                Task.CompletedTask)

            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
            let! _ = requireGraceSuccessAsync copyA proxy.BaseAddress (syncCommand "run")

            do!
                requireConvergedAsync
                    proxy.BaseAddress
                    [| copyA; copyB |]
                    [|
                        composedCafe + "/cover.txt", "edited on B"
                    |]
                    [| composedCafe |]
        }
