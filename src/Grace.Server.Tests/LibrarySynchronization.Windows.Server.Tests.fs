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
                let mutable replayGap = false

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
                   && path = "/libraries/bootstrap/start" then
                    let callback =
                        lock manifestCallbackLock (fun () ->
                            let next = afterNextBootstrap in
                            afterNextBootstrap <- None
                            next)

                    match callback with
                    | Some action -> do! action ()
                    | None -> ()

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
        /// Accepts a real remote edit after an immutable baseline was selected and before its first page reaches the joining copy.
        member _.AfterNextBootstrap(action) = lock manifestCallbackLock (fun () -> afterNextBootstrap <- Some action)
        /// Observes the installed baseline immediately before the joining copy asks for later accepted changes.
        member _.BeforeNextChangesGet(action) = lock manifestCallbackLock (fun () -> beforeNextChangesGet <- Some action)
        /// Runs one competing real mutation after local intent/request persistence and before submission reaches the server.
        member _.BeforeNextSubmit(action) = lock manifestCallbackLock (fun () -> beforeNextSubmit <- Some action)
        /// Exercises the actual server's immutable continuation route with one item per page.
        member _.UseSingleItemBootstrapPages() = singleItemBootstrapPages <- true
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

    /// Orders a real file rename or deletion ahead of a saved edit in the other working copy.
    let private changeNamespaceAsync (repositoryId: Guid) catalogVersion (item: LibraryItemDto) deleted =
        task {
            let request = Parameters.Library.SubmitLibraryChangeParameters()
            request.OwnerId <- ownerId
            request.OrganizationId <- organizationId
            request.RepositoryId <- repositoryId.ToString("D")
            request.OperationId <- Guid.NewGuid()
            request.LibraryCatalogVersion <- catalogVersion
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
                            let! _ = occupyDirectoryAsync repositoryId catalog original.Namespace.Value.Parent "new.txt"
                            ()
                        else
                            let! _ = changeNamespaceAsync repositoryId catalog original (scenario = "deleted")
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

                            let exitCode = if result.Outcome = "completed" then 0 else 1
                            return { ExitCode = exitCode; StandardOutput = serialize result; StandardError = "" }
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
                && request.CreationSlotExpectation.IsNone,
                Is.True
            )

            Assert.That(
                selected.SourceBytes.IsNone
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

                            let exitCode = if result.Outcome = "completed" then 0 else 1
                            return { ExitCode = exitCode; StandardOutput = serialize result; StandardError = "" }
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
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
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
            Assert.That(retained.SourceBytes, Is.EqualTo(pending.SourceBytes))
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

            Assert.That(prepared[1].SourceBytes.Value, Is.EqualTo<byte>(System.Text.Encoding.UTF8.GetBytes("distinct later positive")))

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
            Assert.That(afterZero[1].SourceBytes, Is.EqualTo(prepared[1].SourceBytes))

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
                let! _ = changeNamespaceAsync repositoryId before.Catalog.Version original (mutation = "delete")
                ()

            let! failed = runGraceAsync copyB proxy.BaseAddress (syncCommand "run")
            Assert.That(failed.ExitCode, Is.Not.EqualTo(0))
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

            let! rename = changeNamespaceAsync repositoryId before.Catalog.Version original false
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
            File.WriteAllText((if saveAtDestination then renamed else pathB), "Z2")
            let! _ = requireGraceSuccessAsync copyB proxy.BaseAddress (syncCommand "run")
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

            let! _ = changeNamespaceAsync repositoryId before.Catalog.Version original true
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

            let! retried = runGraceAsync copyB proxy.BaseAddress (syncCommand "run")
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
