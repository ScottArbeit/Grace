namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Storage
open Grace.Shared.Utilities
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading.Tasks

/// Groups watch coverage for the CLI test project.
[<NonParallelizable>]
module WatchTests =
    /// Sets ansi console output needed by the test scenario.
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    /// Runs with captured output for test scenarios.
    let private runWithCapturedOutput (args: string array) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            let parseResult = GraceCommand.rootCommand.Parse(args)
            let exitCode = parseResult.InvokeAsync().Result
            exitCode, writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    /// Runs with captured stdout and stderr for test scenarios.
    let private runWithCapturedStdoutAndStderr (args: string array) =
        use standardOutWriter = new StringWriter()
        use standardErrorWriter = new StringWriter()
        let originalOut = Console.Out
        let originalError = Console.Error

        try
            Console.SetOut(standardOutWriter)
            Console.SetError(standardErrorWriter)
            setAnsiConsoleOutput standardOutWriter
            let exitCode = GraceCommand.main args
            exitCode, standardOutWriter.ToString(), standardErrorWriter.ToString()
        finally
            Console.SetOut(originalOut)
            Console.SetError(originalError)
            setAnsiConsoleOutput originalOut

    /// Runs grace process with captured stdout and stderr for test scenarios.
    let private runGraceProcessWithCapturedStdoutAndStderr workingDirectory (args: string array) =
        let cliDllPath = Path.Combine(AppContext.BaseDirectory, "grace.dll")

        File.Exists(cliDllPath) |> should equal true

        let startInfo = ProcessStartInfo()
        startInfo.FileName <- "dotnet"
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.ArgumentList.Add(cliDllPath)

        for arg in args do
            startInfo.ArgumentList.Add(arg)

        use proc = new Process()
        proc.StartInfo <- startInfo

        if not (proc.Start()) then failwith "Failed to start grace process."

        let standardOut = proc.StandardOutput.ReadToEnd()
        let standardError = proc.StandardError.ReadToEnd()

        if not (proc.WaitForExit(30000)) then
            try
                proc.Kill(true)
            with
            | _ -> ()

            Assert.Fail("Timed out waiting for grace process to exit.")

        proc.ExitCode, standardOut, standardError

    /// Runs the supplied action with env applied.
    let private withEnv (name: string) (value: string option) (action: unit -> unit) =
        let original = Environment.GetEnvironmentVariable(name)

        match value with
        | Some v -> Environment.SetEnvironmentVariable(name, v)
        | None -> Environment.SetEnvironmentVariable(name, null)

        try
            action ()
        finally
            Environment.SetEnvironmentVariable(name, original)

    /// Runs the supplied action with cleared env vars applied.
    let private withClearedEnvVars (names: string list) (action: unit -> unit) =
        /// Builds run test data used to exercise CLI watch behavior.
        let rec run remaining =
            match remaining with
            | [] -> action ()
            | head :: tail -> withEnv head None (fun () -> run tail)

        run names

    /// Clears watch auth env for isolated test execution.
    let private clearWatchAuthEnv (action: unit -> unit) =
        withClearedEnvVars
            [
                Constants.EnvironmentVariables.GraceToken
                Constants.EnvironmentVariables.GraceTokenFile
                Constants.EnvironmentVariables.GraceAuthOidcAuthority
                Constants.EnvironmentVariables.GraceAuthOidcAudience
                Constants.EnvironmentVariables.GraceAuthOidcCliClientId
                Constants.EnvironmentVariables.GraceAuthOidcCliRedirectPort
                Constants.EnvironmentVariables.GraceAuthOidcCliScopes
                Constants.EnvironmentVariables.GraceAuthOidcM2mClientId
                Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret
                Constants.EnvironmentVariables.GraceAuthOidcM2mScopes
                Constants.EnvironmentVariables.GraceServerUri
            ]
            action

    /// Parses json output for test assertions.
    let private parseJsonOutput (output: string) =
        output.StartsWith("{", StringComparison.Ordinal)
        |> should equal true

        JsonDocument.Parse(output)

    /// Writes live watch status file needed by the test scenario.
    let private writeLiveWatchStatusFile () =
        let rootDirectoryId = Guid.NewGuid()

        let status: Services.GraceWatchStatus =
            {
                UpdatedAt = getCurrentInstant ()
                IsStartupClaim = false
                RootDirectoryId = rootDirectoryId
                RootDirectorySha256Hash = Sha256Hash "live-watch-root"
                RootDirectoryBlake3Hash = Blake3Hash "live-watch-root-blake3"
                LastFileUploadInstant = Instant.MinValue
                LastDirectoryVersionInstant = Instant.MinValue
                DirectoryIds = HashSet<DirectoryVersionId>([| rootDirectoryId |])
            }

        let ipcFileName = Services.IpcFileName()

        Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
        |> ignore

        File.WriteAllText(ipcFileName, serialize status)
        ipcFileName

    /// Reads file if exists needed by the test scenario.
    let private readFileIfExists path = if File.Exists(path) then Some(File.ReadAllText(path)) else None

    /// Builds delete watch status file if exists test data used to exercise CLI watch behavior.
    let private deleteWatchStatusFileIfExists () =
        let ipcFileName = Services.IpcFileName()

        if File.Exists(ipcFileName) then File.Delete(ipcFileName)

    /// Runs the supplied action with temp repo applied.
    let private withTempRepo (action: string -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"grace-watch-tests-{Guid.NewGuid():N}")
        let graceDir = Path.Combine(tempDir, Constants.GraceConfigDirectory)
        let configPath = Path.Combine(graceDir, Constants.GraceConfigFileName)
        Directory.CreateDirectory(graceDir) |> ignore
        File.WriteAllText(configPath, "{}")

        let originalDir = Environment.CurrentDirectory
        let originalParseResult = Services.parseResult

        try
            Environment.CurrentDirectory <- tempDir
            Services.parseResult <- GraceCommand.rootCommand.Parse(Array.empty<string>)
            resetConfiguration ()
            Services.graceWatchStatusUpdateTime <- Instant.MinValue
            Services.clearWorkingDirectoryWriteTimesForWatchRescan ()
            deleteWatchStatusFileIfExists ()
            Watch.clearPendingWatchWorkForTests ()
            action tempDir
        finally
            Watch.clearPendingWatchWorkForTests ()
            Services.clearWorkingDirectoryWriteTimesForWatchRescan ()
            deleteWatchStatusFileIfExists ()
            Services.graceWatchStatusUpdateTime <- Instant.MinValue
            resetConfiguration ()
            Services.parseResult <- originalParseResult
            Environment.CurrentDirectory <- originalDir

            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, true)
                with
                | _ -> ()

    /// Builds deleted event test data used to exercise CLI watch behavior.
    let private deletedEvent (fullPath: string) = FileSystemEventArgs(WatcherChangeTypes.Deleted, Path.GetDirectoryName(fullPath), Path.GetFileName(fullPath))

    /// Builds changed event test data used to exercise CLI watch behavior.
    let private changedEvent (fullPath: string) = FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(fullPath), Path.GetFileName(fullPath))

    /// Builds renamed event test data used to exercise CLI watch behavior.
    let private renamedEvent (oldFullPath: string) (fullPath: string) =
        RenamedEventArgs(WatcherChangeTypes.Renamed, Path.GetDirectoryName(fullPath), Path.GetFileName(fullPath), Path.GetFileName(oldFullPath))

    /// Produces an empty difference list for watch tests that do not exercise a scan path.
    let private scanForNoDifferences _ = Task.FromResult(List<FileSystemDifference>())

    /// Records the uploaded identity that the real watch upload path would cache for status application.
    let private recordUploadedFileVersion fullPath =
        match (Services.createLocalFileVersion (FileInfo(fullPath)))
            .GetAwaiter()
            .GetResult()
            with
        | Some localFileVersion -> Watch.recordUploadedFileVersionForWatchTests localFileVersion.ToFileVersion
        | None -> Assert.Fail($"Expected a local file version for {fullPath}.")

    /// Builds process pending watch work for test test data used to exercise CLI watch behavior.
    let private processPendingWatchWorkForTest () =
        let status = GraceStatus.Default
        /// Tracks update Calls changes so this scenario can assert the resulting side effect explicitly.
        let mutable updateCalls = 0
        /// Tracks upload Calls changes so this scenario can assert the resulting side effect explicitly.
        let mutable uploadCalls = 0

        /// Reads status needed by the test scenario.
        let readStatus () = Task.FromResult(status)

        /// Builds upload test data used to exercise CLI watch behavior.
        let upload _ filePath =
            uploadCalls <- uploadCalls + 1
            let fullPath = $"{filePath}"

            if File.Exists(fullPath) then recordUploadedFileVersion fullPath

            Task.FromResult(())

        /// Builds update grace status test data used to exercise CLI watch behavior.
        let updateGraceStatus status _ =
            updateCalls <- updateCalls + 1
            Task.FromResult(Some status)

        /// Builds update grace status from differences test data used to exercise CLI watch behavior.
        let updateGraceStatusFromDifferences status _ _ = updateGraceStatus status CorrelationId.Empty

        /// Builds apply incremental test data used to exercise CLI watch behavior.
        let applyIncremental _ _ _ = Task.FromResult(())
        /// Builds update ipc test data used to exercise CLI watch behavior.
        let updateIpc _ _ = Task.FromResult(())

        let processTask =
            Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForNoDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc

        processTask.GetAwaiter().GetResult()

        updateCalls, uploadCalls

    /// Builds process pending watch work with an explicit GraceStatus for test data used to exercise CLI watch behavior.
    let private processPendingWatchWorkForTestWithStatus status =
        /// Tracks update Calls changes so this scenario can assert the resulting side effect explicitly.
        let mutable updateCalls = 0
        /// Tracks upload Calls changes so this scenario can assert the resulting side effect explicitly.
        let mutable uploadCalls = 0
        /// Reads status needed by the test scenario.
        let readStatus () = Task.FromResult(status)

        /// Builds upload test data used to exercise CLI watch behavior.
        let upload _ filePath =
            uploadCalls <- uploadCalls + 1
            let fullPath = $"{filePath}"

            if File.Exists(fullPath) then recordUploadedFileVersion fullPath

            Task.FromResult(())

        /// Builds update grace status test data used to exercise CLI watch behavior.
        let updateGraceStatus status _ =
            updateCalls <- updateCalls + 1
            Task.FromResult(Some status)

        /// Builds update grace status from differences test data used to exercise CLI watch behavior.
        let updateGraceStatusFromDifferences status _ _ = updateGraceStatus status CorrelationId.Empty
        /// Builds apply incremental test data used to exercise CLI watch behavior.
        let applyIncremental _ _ _ = Task.FromResult(())
        /// Builds update ipc test data used to exercise CLI watch behavior.
        let updateIpc _ _ = Task.FromResult(())

        (Watch.processChangedFilesWithClients
            readStatus
            readStatus
            upload
            updateGraceStatus
            scanForNoDifferences
            updateGraceStatusFromDifferences
            applyIncremental
            updateIpc)
            .GetAwaiter()
            .GetResult()

        updateCalls, uploadCalls

    /// Builds process pending watch work with status clients test data used to exercise CLI watch behavior.
    let private processPendingWatchWorkWithStatusClients readStatusFile updateGraceStatus =
        let status = GraceStatus.Default
        /// Reads status meta needed by the test scenario.
        let readStatusMeta () = Task.FromResult(status)

        /// Builds upload test data used to exercise CLI watch behavior.
        let upload _ filePath =
            let fullPath = $"{filePath}"

            if File.Exists(fullPath) then recordUploadedFileVersion fullPath

            Task.FromResult(())

        /// Builds update grace status from differences test data used to exercise CLI watch behavior.
        let updateGraceStatusFromDifferences status _ _ = updateGraceStatus status CorrelationId.Empty
        /// Builds apply incremental test data used to exercise CLI watch behavior.
        let applyIncremental _ _ _ = Task.FromResult(())
        /// Builds update ipc test data used to exercise CLI watch behavior.
        let updateIpc _ _ = Task.FromResult(())

        Watch.processChangedFilesWithClients
            readStatusMeta
            readStatusFile
            upload
            updateGraceStatus
            scanForNoDifferences
            updateGraceStatusFromDifferences
            applyIncremental
            updateIpc
        |> fun processTask -> processTask.GetAwaiter().GetResult()

    /// Writes grace ignore needed by the test scenario.
    let private writeGraceIgnore root (entries: string array) =
        File.WriteAllText(Path.Combine(root, Constants.GraceIgnoreFileName), String.Join(Environment.NewLine, entries))
        resetConfiguration ()

    /// Builds local file version test data used to exercise CLI watch behavior.
    let private localFileVersion relativePath =
        LocalFileVersion.CreateWithHashes
            relativePath
            (Sha256Hash $"sha-{relativePath}")
            (Blake3Hash $"blake3-{relativePath}")
            false
            1L
            (getCurrentInstant ())
            true
            DateTime.UtcNow

    /// Builds local directory version test data used to exercise CLI watch behavior.
    let private localDirectoryVersion relativePath directories files =
        LocalDirectoryVersion.CreateWithHashes
            (Guid.NewGuid())
            OwnerId.Empty
            OrganizationId.Empty
            RepositoryId.Empty
            relativePath
            (Sha256Hash $"sha-{relativePath}")
            (Blake3Hash $"blake3-{relativePath}")
            directories
            files
            1L
            DateTime.UtcNow

    /// Builds grace status tracking test data used to exercise CLI watch behavior.
    let private graceStatusTracking trackedFiles trackedDirectories =
        let rootDirectoryId = Guid.NewGuid()
        let index = GraceIndex()

        let childDirectories =
            trackedDirectories
            |> Array.map (fun relativePath -> localDirectoryVersion relativePath (List<DirectoryVersionId>()) (List<LocalFileVersion>()))

        let root =
            LocalDirectoryVersion.CreateWithHashes
                rootDirectoryId
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                (Sha256Hash "root-sha")
                (Blake3Hash "root-blake3")
                (List<DirectoryVersionId>(
                    childDirectories
                    |> Array.map (fun directory -> directory.DirectoryVersionId)
                ))
                (List<LocalFileVersion>(trackedFiles |> Array.map localFileVersion))
                1L
                DateTime.UtcNow

        index.TryAdd(rootDirectoryId, root) |> ignore

        for directory in childDirectories do
            index.TryAdd(directory.DirectoryVersionId, directory)
            |> ignore

        { GraceStatus.Default with
            Index = index
            RootDirectoryId = rootDirectoryId
            RootDirectorySha256Hash = root.Sha256Hash
            RootDirectoryBlake3Hash = root.Blake3Hash
        }

    /// Builds GraceStatus around explicit file versions so tests can model content-equivalent uploads.
    let private graceStatusTrackingFileVersions (trackedFiles: LocalFileVersion array) =
        let rootDirectoryId = Guid.NewGuid()
        let index = GraceIndex()

        let root =
            LocalDirectoryVersion.CreateWithHashes
                rootDirectoryId
                OwnerId.Empty
                OrganizationId.Empty
                RepositoryId.Empty
                Constants.RootDirectoryPath
                (Sha256Hash "root-sha")
                (Blake3Hash "root-blake3")
                (List<DirectoryVersionId>())
                (List<LocalFileVersion>(trackedFiles))
                1L
                DateTime.UtcNow

        index.TryAdd(rootDirectoryId, root) |> ignore

        { GraceStatus.Default with
            Index = index
            RootDirectoryId = rootDirectoryId
            RootDirectorySha256Hash = root.Sha256Hash
            RootDirectoryBlake3Hash = root.Blake3Hash
        }

    /// Verifies that resolve signal r access token result returns token when present.
    [<Test>]
    let ``resolveSignalRAccessTokenResult returns token when present`` () =
        let result = Watch.resolveSignalRAccessTokenResult (Ok(Some "token-value"))

        match result with
        | Ok token -> token |> should equal "token-value"
        | Error error -> Assert.Fail($"Expected token result, got error: {error}")

    /// Verifies that resolve signal r access token result errors when token is missing.
    [<Test>]
    let ``resolveSignalRAccessTokenResult errors when token is missing`` () =
        let result = Watch.resolveSignalRAccessTokenResult (Ok None)

        match result with
        | Ok token -> Assert.Fail($"Expected missing token error, got token: {token}")
        | Error error ->
            error
            |> should contain "No access token is available."

    /// Verifies that resolve signal r access token result includes underlying auth error.
    [<Test>]
    let ``resolveSignalRAccessTokenResult includes underlying auth error`` () =
        let result = Watch.resolveSignalRAccessTokenResult (Error "test error")

        match result with
        | Ok token -> Assert.Fail($"Expected auth error, got token: {token}")
        | Error error ->
            error
            |> should contain "Unable to acquire an access token for SignalR notifications:"

            error |> should contain "test error"

    /// Verifies that deleted file queues status update work without upload work.
    [<Test>]
    let ``deleted file queues status update work without upload work`` () =
        withTempRepo (fun root ->
            let filePath = Path.Combine(root, "deleted.txt")

            Watch.OnDeleted(deletedEvent filePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "deleted.txt" |]

            pending.FilesToProcess
            |> should equal Array.empty<string>

            /// Builds update calls test data used to exercise CLI watch behavior.
            let updateCalls, uploadCalls = processPendingWatchWorkForTestWithStatus (graceStatusTracking [| "deleted.txt" |] Array.empty<string>)

            updateCalls |> should equal 1
            uploadCalls |> should equal 0

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>

            afterProcessing.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that failed upload remains queued and blocks status only rescan until upload succeeds.
    [<Test>]
    let ``failed upload remains queued and blocks status-only rescan until upload succeeds`` () =
        withTempRepo (fun root ->
            let changedFilePath = Path.Combine(root, "changed.txt")
            let deletedFilePath = Path.Combine(root, "deleted-while-upload-pending.txt")
            /// Tracks upload Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable uploadCalls = 0
            /// Tracks update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable updateCalls = 0

            File.WriteAllText(changedFilePath, "changed payload")
            Watch.OnChanged(changedEvent changedFilePath)
            Watch.OnDeleted(deletedEvent deletedFilePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds failing upload test data used to exercise CLI watch behavior.
            let failingUpload _ _ =
                uploadCalls <- uploadCalls + 1
                Task.FromException<unit>(InvalidOperationException("transient upload failure"))

            /// Builds update grace status test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                updateCalls <- updateCalls + 1
                Task.FromResult(Some status)

            /// Builds update grace status from differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status _ _ = updateGraceStatus status CorrelationId.Empty

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                failingUpload
                updateGraceStatus
                scanForNoDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            uploadCalls |> should equal 1
            updateCalls |> should equal 0

            let afterFailure = Watch.pendingWatchWorkSnapshotForTests ()

            afterFailure.FilesToProcess
            |> should equal [| changedFilePath |]

            afterFailure.StatusUpdateTriggers
            |> should equal [| "deleted-while-upload-pending.txt" |]

            /// Builds success calls test data used to exercise CLI watch behavior.
            let successCalls, successfulUploadCalls =
                processPendingWatchWorkForTestWithStatus (graceStatusTracking [| "deleted-while-upload-pending.txt" |] Array.empty<string>)

            successCalls |> should equal 1
            successfulUploadCalls |> should equal 1

            let afterSuccess = Watch.pendingWatchWorkSnapshotForTests ()

            afterSuccess.FilesToProcess
            |> should equal Array.empty<string>

            afterSuccess.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that same path change during upload remains queued for a newer upload.
    [<Test>]
    let ``same-path change during upload remains queued for a newer upload`` () =
        withTempRepo (fun root ->
            let changedFilePath = Path.Combine(root, "changed-during-upload.txt")
            /// Tracks upload Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable uploadCalls = 0
            /// Tracks update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable updateCalls = 0

            File.WriteAllText(changedFilePath, "first payload")
            Watch.OnChanged(changedEvent changedFilePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ filePath =
                uploadCalls <- uploadCalls + 1

                if uploadCalls = 1 then
                    File.WriteAllText(changedFilePath, "second payload")
                    Watch.OnChanged(changedEvent changedFilePath)
                else
                    recordUploadedFileVersion $"{filePath}"

                Task.FromResult(())

            /// Builds update grace status test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                updateCalls <- updateCalls + 1
                Task.FromResult(Some status)

            /// Builds update grace status from differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status _ _ = updateGraceStatus status CorrelationId.Empty

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForNoDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            uploadCalls |> should equal 1
            updateCalls |> should equal 0

            let afterFirstUpload = Watch.pendingWatchWorkSnapshotForTests ()

            afterFirstUpload.FilesToProcess
            |> should equal [| changedFilePath |]

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForNoDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            uploadCalls |> should equal 2
            updateCalls |> should equal 1

            let afterSecondUpload = Watch.pendingWatchWorkSnapshotForTests ()

            afterSecondUpload.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that deleted file cancels same path pending upload work before status rescan.
    [<Test>]
    let ``deleted file cancels same-path pending upload work before status rescan`` () =
        withTempRepo (fun root ->
            let deletedFilePath = Path.Combine(root, "queued-then-deleted.txt")

            File.WriteAllText(deletedFilePath, "payload before delete")
            Watch.OnChanged(changedEvent deletedFilePath)
            File.Delete(deletedFilePath)
            Watch.OnDeleted(deletedEvent deletedFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal [| "queued-then-deleted.txt" |]

            /// Builds update calls test data used to exercise CLI watch behavior.
            let updateCalls, uploadCalls = processPendingWatchWorkForTestWithStatus (graceStatusTracking [| "queued-then-deleted.txt" |] Array.empty<string>)

            updateCalls |> should equal 1
            uploadCalls |> should equal 0)

    /// Verifies that ignored delete uses filesystem casing when cancelling pending upload work.
    [<Test>]
    let ``ignored delete uses filesystem casing when cancelling pending upload work`` () =
        withTempRepo (fun root ->
            Watch.setWatchPathComparisonForWatchTests StringComparison.Ordinal

            let queuedDirectory = Path.Combine(root, "src", "Foo")

            Directory.CreateDirectory(queuedDirectory)
            |> ignore

            let queuedFilePath = Path.Combine(queuedDirectory, "pending.txt")
            File.WriteAllText(queuedFilePath, "queued payload")
            Watch.OnChanged(changedEvent queuedFilePath)
            writeGraceIgnore root [| "src/foo/" |]

            let ignoredDeletedDirectory = Path.Combine(root, "src", "foo")
            Watch.OnDeleted(deletedEvent ignoredDeletedDirectory)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal [| queuedFilePath |]

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that status only triggers remain pending when status update returns none.
    [<Test>]
    let ``status-only triggers remain pending when status update returns none`` () =
        withTempRepo (fun root ->
            let filePath = Path.Combine(root, "retry-delete.txt")
            let status = graceStatusTracking [| "retry-delete.txt" |] Array.empty<string>
            /// Tracks update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable updateCalls = 0

            Watch.OnDeleted(deletedEvent filePath)

            /// Builds update grace status test data used to exercise CLI watch behavior.
            let updateGraceStatus _ _ =
                updateCalls <- updateCalls + 1
                Task.FromResult(None)

            processPendingWatchWorkWithStatusClients (fun () -> Task.FromResult(status)) updateGraceStatus

            updateCalls |> should equal 1

            let afterFailure = Watch.pendingWatchWorkSnapshotForTests ()

            afterFailure.StatusUpdateTriggers
            |> should equal [| "retry-delete.txt" |]

            /// Builds success calls test data used to exercise CLI watch behavior.
            let successCalls, uploadCalls = processPendingWatchWorkForTestWithStatus status

            successCalls |> should equal 1
            uploadCalls |> should equal 0

            let afterSuccess = Watch.pendingWatchWorkSnapshotForTests ()

            afterSuccess.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that status only triggers remain pending when scan failure is swallowed as unchanged status.
    [<Test>]
    let ``status-only triggers remain pending when scan failure is swallowed as unchanged status`` () =
        withTempRepo (fun root ->
            let filePath = Path.Combine(root, "swallowed-scan-failure-delete.txt")

            let status =
                graceStatusTracking
                    [|
                        "swallowed-scan-failure-delete.txt"
                    |]
                    Array.empty<string>

            /// Tracks update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable updateCalls = 0

            Watch.OnDeleted(deletedEvent filePath)

            /// Builds update grace status test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                updateCalls <- updateCalls + 1
                Services.setLastScanForDifferencesSuccessfulForWatchTests false
                Task.FromResult(Some status)

            processPendingWatchWorkWithStatusClients (fun () -> Task.FromResult(status)) updateGraceStatus

            updateCalls |> should equal 1

            let afterStatusUpdate = Watch.pendingWatchWorkSnapshotForTests ()

            afterStatusUpdate.StatusUpdateTriggers
            |> should equal Array.empty<string>

            Services.setLastScanForDifferencesSuccessfulForWatchTests true)

    /// Verifies that status only triggers added during status update remain pending for next pass.
    [<Test>]
    let ``status-only triggers added during status update remain pending for next pass`` () =
        withTempRepo (fun root ->
            let beforeUpdatePath = Path.Combine(root, "before-update-delete.txt")
            let duringUpdatePath = Path.Combine(root, "during-update-delete.txt")

            let status =
                graceStatusTracking
                    [|
                        "before-update-delete.txt"
                        "during-update-delete.txt"
                    |]
                    Array.empty<string>

            /// Tracks update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable updateCalls = 0

            Watch.OnDeleted(deletedEvent beforeUpdatePath)

            /// Builds update grace status test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                updateCalls <- updateCalls + 1

                if updateCalls = 1 then Watch.OnDeleted(deletedEvent duringUpdatePath)

                Task.FromResult(Some status)

            processPendingWatchWorkWithStatusClients (fun () -> Task.FromResult(status)) updateGraceStatus

            updateCalls |> should equal 1

            let afterFirstPass = Watch.pendingWatchWorkSnapshotForTests ()

            afterFirstPass.StatusUpdateTriggers
            |> should equal [| "during-update-delete.txt" |]

            /// Builds success calls test data used to exercise CLI watch behavior.
            let successCalls, uploadCalls = processPendingWatchWorkForTestWithStatus status

            successCalls |> should equal 1
            uploadCalls |> should equal 0

            let afterSecondPass = Watch.pendingWatchWorkSnapshotForTests ()

            afterSecondPass.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that same status only trigger added during status update remains pending for next pass.
    [<Test>]
    let ``same status-only trigger added during status update remains pending for next pass`` () =
        withTempRepo (fun root ->
            let deletedPath = Path.Combine(root, "same-path-delete.txt")
            let status = graceStatusTracking [| "same-path-delete.txt" |] Array.empty<string>
            /// Tracks update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable updateCalls = 0

            Watch.OnDeleted(deletedEvent deletedPath)

            /// Builds update grace status test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                updateCalls <- updateCalls + 1

                if updateCalls = 1 then Watch.OnDeleted(deletedEvent deletedPath)

                Task.FromResult(Some status)

            processPendingWatchWorkWithStatusClients (fun () -> Task.FromResult(status)) updateGraceStatus

            updateCalls |> should equal 1

            let afterFirstPass = Watch.pendingWatchWorkSnapshotForTests ()

            afterFirstPass.StatusUpdateTriggers
            |> should equal [| "same-path-delete.txt" |]

            /// Builds success calls test data used to exercise CLI watch behavior.
            let successCalls, uploadCalls = processPendingWatchWorkForTestWithStatus status

            successCalls |> should equal 1
            uploadCalls |> should equal 0

            let afterSecondPass = Watch.pendingWatchWorkSnapshotForTests ()

            afterSecondPass.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that status only triggers remain pending when status file read fails.
    [<Test>]
    let ``status-only triggers remain pending when status file read fails`` () =
        withTempRepo (fun root ->
            let filePath = Path.Combine(root, "read-failure-delete.txt")
            /// Tracks update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable updateCalls = 0

            Watch.OnDeleted(deletedEvent filePath)

            /// Reads status file needed by the test scenario.
            let readStatusFile () = Task.FromException<GraceStatus>(InvalidOperationException("transient status read failure"))

            /// Builds update grace status test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                updateCalls <- updateCalls + 1
                Task.FromResult(Some status)

            processPendingWatchWorkWithStatusClients readStatusFile updateGraceStatus

            updateCalls |> should equal 0

            let afterFailure = Watch.pendingWatchWorkSnapshotForTests ()

            afterFailure.StatusUpdateTriggers
            |> should equal [| "read-failure-delete.txt" |])

    /// Verifies that rename old status only trigger remains pending when status update fails.
    [<Test>]
    let ``rename-old status-only trigger remains pending when status update fails`` () =
        withTempRepo (fun root ->
            let oldPath = Path.Combine(root, "old-status-only-name.txt")
            let ignoredNewPath = Path.Combine(root, "new-status-only-name.gracetmp")
            let status = graceStatusTracking [| "old-status-only-name.txt" |] Array.empty<string>

            Watch.OnRenamed(renamedEvent oldPath ignoredNewPath)

            /// Builds update grace status test data used to exercise CLI watch behavior.
            let updateGraceStatus _ _ = Task.FromException<GraceStatus option>(InvalidOperationException("transient status update failure"))

            processPendingWatchWorkWithStatusClients (fun () -> Task.FromResult(status)) updateGraceStatus

            let afterFailure = Watch.pendingWatchWorkSnapshotForTests ()

            afterFailure.StatusUpdateTriggers
            |> should equal [| "old-status-only-name.txt" |]

            afterFailure.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that ignored and outside delete paths do not queue status update work.
    [<Test>]
    let ``ignored and outside delete paths do not queue status update work`` () =
        withTempRepo (fun root ->
            let graceArtifactPath = Path.Combine(root, Constants.GraceConfigDirectory, "grace-local.db")
            let outsidePath = Path.Combine(Path.GetTempPath(), $"grace-watch-outside-{Guid.NewGuid():N}.txt")

            Watch.OnDeleted(deletedEvent graceArtifactPath)
            Watch.OnDeleted(deletedEvent outsidePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>

            pending.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that unknown deleted file matching file ignore does not queue status update work.
    [<Test>]
    let ``unknown deleted file matching file ignore does not queue status update work`` () =
        withTempRepo (fun root ->
            writeGraceIgnore root [| "*.log" |]

            let filePath = Path.Combine(root, "ignored.log")

            Watch.OnDeleted(deletedEvent filePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>

            pending.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that unknown deleted file under ignored parent directory does not queue status update work.
    [<Test>]
    let ``unknown deleted file under ignored parent directory does not queue status update work`` () =
        withTempRepo (fun root ->
            writeGraceIgnore root [| "logs/" |]

            let logsDirectory = Path.Combine(root, "logs")
            Directory.CreateDirectory(logsDirectory) |> ignore
            let filePath = Path.Combine(logsDirectory, "ignored.txt")

            Watch.OnDeleted(deletedEvent filePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>

            pending.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that deleted tracked file under ignored parent directory queues status update work.
    [<Test>]
    let ``deleted tracked file under ignored parent directory queues status update work`` () =
        withTempRepo (fun root ->
            writeGraceIgnore root [| "logs/" |]

            let logsDirectory = Path.Combine(root, "logs")
            Directory.CreateDirectory(logsDirectory) |> ignore
            let filePath = Path.Combine(logsDirectory, "tracked.txt")
            File.WriteAllText(filePath, "tracked log content")
            Watch.setGraceStatusForWatchTests (graceStatusTracking [| "logs/tracked.txt" |] Array.empty<string>)
            File.Delete(filePath)

            Watch.OnDeleted(deletedEvent filePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "logs/tracked.txt" |]

            pending.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that dirty status reload keeps tracked ignored looking delete from being suppressed.
    [<Test>]
    let ``dirty status reload keeps tracked ignored-looking delete from being suppressed`` () =
        withTempRepo (fun root ->
            writeGraceIgnore root [| "*.log" |]

            let filePath = Path.Combine(root, "important.log")
            File.WriteAllText(filePath, "tracked log file")

            Watch.setGraceStatusForWatchTests (graceStatusTracking [| "other.txt" |] Array.empty<string>)
            Watch.setGraceStatusHasChangedForWatchTests true

            (Services.writeGraceStatusFile (graceStatusTracking [| "important.log" |] Array.empty<string>))
                .GetAwaiter()
                .GetResult()

            File.Delete(filePath)
            Watch.OnDeleted(deletedEvent filePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "important.log" |]

            pending.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that status reload failure keeps ignored looking delete queued for retry.
    [<Test>]
    let ``status reload failure keeps ignored-looking delete queued for retry`` () =
        withTempRepo (fun root ->
            writeGraceIgnore root [| "*.log" |]

            let filePath = Path.Combine(root, "important.log")

            Watch.setGraceStatusHasChangedForWatchTests true

            Watch.setReadGraceStatusFileForWatchTests (fun () -> Task.FromException<GraceStatus>(InvalidOperationException("transient status reload failure")))

            Watch.OnDeleted(deletedEvent filePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "important.log" |]

            pending.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that status only delete rescan clears stale working tree scan cache.
    [<Test>]
    let ``status-only delete rescan clears stale working tree scan cache`` () =
        withTempRepo (fun root ->
            let relativePath = "stale-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            File.WriteAllText(filePath, "tracked content before delete")

            let status = graceStatusTracking [| relativePath |] Array.empty<string>

            (Services.scanForDifferences status)
                .GetAwaiter()
                .GetResult()
            |> ignore

            File.Delete(filePath)
            Watch.OnDeleted(deletedEvent filePath)

            /// Tracks observed Differences changes so this scenario can assert the resulting side effect explicitly.
            let mutable observedDifferences = List<FileSystemDifference>()

            /// Builds update grace status test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                task {
                    let! differences = Services.scanForDifferences status
                    observedDifferences <- differences
                    return Some status
                }

            processPendingWatchWorkWithStatusClients (fun () -> Task.FromResult(status)) updateGraceStatus

            observedDifferences
            |> Seq.exists (fun difference ->
                difference.DifferenceType = DifferenceType.Delete
                && difference.FileSystemEntryType = FileSystemEntryType.File
                && difference.RelativePath = RelativePath relativePath)
            |> should equal true)

    /// Verifies that deleted directory queues status update when rename cached directory ignore.
    [<Test>]
    let ``deleted directory queues status update when rename cached directory ignore`` () =
        withTempRepo (fun root ->
            let oldPath = Path.Combine(root, "old-directory-name")
            let directoryPath = Path.Combine(root, "cached-directory")
            Directory.CreateDirectory(directoryPath) |> ignore

            Watch.OnRenamed(renamedEvent oldPath directoryPath)
            Watch.clearPendingWatchWorkForTests ()
            Directory.Delete(directoryPath)

            Watch.OnDeleted(deletedEvent directoryPath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "cached-directory" |]

            pending.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that deleted directory named like file ignore queues status update work.
    [<Test>]
    let ``deleted directory named like file ignore queues status update work`` () =
        withTempRepo (fun root ->
            writeGraceIgnore root [| "*.tmp" |]

            let directoryPath = Path.Combine(root, "archive.tmp")
            Directory.CreateDirectory(directoryPath) |> ignore
            Watch.setGraceStatusForWatchTests (graceStatusTracking Array.empty<string> [| "archive.tmp" |])
            Directory.Delete(directoryPath)

            Watch.OnDeleted(deletedEvent directoryPath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "archive.tmp" |]

            pending.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that deleted tracked directory ending gracetmp queues status update work.
    [<Test>]
    let ``deleted tracked directory ending gracetmp queues status update work`` () =
        withTempRepo (fun root ->
            let directoryPath = Path.Combine(root, "assets.gracetmp")
            Directory.CreateDirectory(directoryPath) |> ignore
            Watch.setGraceStatusForWatchTests (graceStatusTracking Array.empty<string> [| "assets.gracetmp" |])
            Directory.Delete(directoryPath)

            Watch.OnDeleted(deletedEvent directoryPath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "assets.gracetmp" |]

            pending.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that deleted tracked directory matching directory ignore queues status update work.
    [<Test>]
    let ``deleted tracked directory matching directory ignore queues status update work`` () =
        withTempRepo (fun root ->
            writeGraceIgnore root [| "archive.tmp/" |]

            let directoryPath = Path.Combine(root, "archive.tmp")
            Directory.CreateDirectory(directoryPath) |> ignore
            Watch.setGraceStatusForWatchTests (graceStatusTracking Array.empty<string> [| "archive.tmp" |])
            Directory.Delete(directoryPath)

            Watch.OnDeleted(deletedEvent directoryPath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "archive.tmp" |]

            pending.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that unknown deleted directory matching directory ignore does not queue status update work.
    [<Test>]
    let ``unknown deleted directory matching directory ignore does not queue status update work`` () =
        withTempRepo (fun root ->
            writeGraceIgnore root [| "archive.tmp/" |]

            let directoryPath = Path.Combine(root, "archive.tmp")
            Directory.CreateDirectory(directoryPath) |> ignore
            Directory.Delete(directoryPath)

            Watch.OnDeleted(deletedEvent directoryPath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>

            pending.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that deleted tracked file matching directory only ignore queues status update work.
    [<Test>]
    let ``deleted tracked file matching directory-only ignore queues status update work`` () =
        withTempRepo (fun root ->
            writeGraceIgnore root [| "archive.tmp/" |]

            let filePath = Path.Combine(root, "archive.tmp")
            File.WriteAllText(filePath, "tracked file with directory-only ignored name")
            Watch.setGraceStatusForWatchTests (graceStatusTracking [| "archive.tmp" |] Array.empty<string>)
            File.Delete(filePath)

            Watch.OnDeleted(deletedEvent filePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "archive.tmp" |]

            pending.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that duplicate deleted file events drain as one status update trigger.
    [<Test>]
    let ``duplicate deleted file events drain as one status update trigger`` () =
        withTempRepo (fun root ->
            let filePath = Path.Combine(root, "duplicate-delete.txt")

            Watch.OnDeleted(deletedEvent filePath)
            Watch.OnDeleted(deletedEvent filePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "duplicate-delete.txt" |]

            /// Builds update calls test data used to exercise CLI watch behavior.
            let updateCalls, uploadCalls = processPendingWatchWorkForTestWithStatus (graceStatusTracking [| "duplicate-delete.txt" |] Array.empty<string>)

            updateCalls |> should equal 1
            uploadCalls |> should equal 0

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that healthy file add change and delete derive status differences without scanning.
    [<Test>]
    let ``healthy file events derive status differences without scan`` () =
        withTempRepo (fun root ->
            let addedPath = Path.Combine(root, "added.txt")
            let changedPath = Path.Combine(root, "changed.txt")
            let deletedPath = Path.Combine(root, "deleted.txt")
            /// Tracks scan Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable scanCalls = 0
            /// Tracks scan-oriented update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable scanOrientedUpdateCalls = 0
            /// Tracks apply-from-differences Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so this scenario can assert event-derived status work.
            let mutable observedDifferences = List<FileSystemDifference>()

            File.WriteAllText(addedPath, "new file content")
            File.WriteAllText(changedPath, "changed file content")

            Watch.OnCreated(changedEvent addedPath)
            Watch.OnChanged(changedEvent changedPath)
            Watch.OnDeleted(deletedEvent deletedPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(graceStatusTracking [| "changed.txt"; "deleted.txt" |] Array.empty<string>)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ filePath =
                recordUploadedFileVersion $"{filePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                scanOrientedUpdateCalls <- scanOrientedUpdateCalls + 1
                Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            scanCalls |> should equal 0
            scanOrientedUpdateCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.sortBy (fun (_, _, relativePath) -> relativePath)
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.File, "added.txt"
                    DifferenceType.Change, FileSystemEntryType.File, "changed.txt"
                    DifferenceType.Delete, FileSystemEntryType.File, "deleted.txt"
                |])

    /// Verifies that a file addition under new directories includes the directory additions needed to link the file.
    [<Test>]
    let ``new file under untracked directories derives parent directory additions`` () =
        withTempRepo (fun root ->
            let nestedDirectory = Path.Combine(root, "new-parent", "nested")

            Directory.CreateDirectory(nestedDirectory)
            |> ignore

            let addedPath = Path.Combine(nestedDirectory, "added.txt")
            /// Tracks the Differences passed to the apply seam so parent directory additions are proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            File.WriteAllText(addedPath, "new nested file content")
            Watch.OnCreated(changedEvent addedPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(graceStatusTracking Array.empty<string> Array.empty<string>)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ filePath =
                recordUploadedFileVersion $"{filePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForNoDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.Directory, "new-parent"
                    DifferenceType.Add, FileSystemEntryType.Directory, "new-parent/nested"
                    DifferenceType.Add, FileSystemEntryType.File, "new-parent/nested/added.txt"
                |])

    /// Verifies that uploaded paths are retained across 50-file batches until status application drains the full queue.
    [<Test>]
    let ``uploaded file paths survive across watch batches before status apply`` () =
        withTempRepo (fun root ->
            let fileCount = 55
            /// Tracks upload Calls changes so this scenario can assert the batched side effect explicitly.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls changes so this scenario can assert status applies once after drain.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so all uploaded files are represented.
            let mutable observedDifferences = List<FileSystemDifference>()

            for index in 1..fileCount do
                let filePath = Path.Combine(root, $"batch-{index:D2}.txt")
                File.WriteAllText(filePath, $"batch payload {index}")
                Watch.OnChanged(changedEvent filePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ filePath =
                uploadCalls <- uploadCalls + 1
                recordUploadedFileVersion $"{filePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            let processPendingWork () =
                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scanForNoDifferences
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

            processPendingWork ()

            uploadCalls |> should equal 50
            applyFromDifferencesCalls |> should equal 0

            processPendingWork ()

            uploadCalls |> should equal fileCount
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.filter (fun difference ->
                difference.DifferenceType = DifferenceType.Add
                && difference.FileSystemEntryType = FileSystemEntryType.File)
            |> Seq.map (fun difference -> $"{difference.RelativePath}")
            |> Seq.sort
            |> Seq.toArray
            |> should
                equal
                [|
                    for index in 1..fileCount -> $"batch-{index:D2}.txt"
                |])

    /// Verifies that case-sensitive watch comparison does not collapse distinct tracked and uploaded file paths.
    [<Test>]
    let ``case-sensitive tracked file matching preserves distinct uploaded path`` () =
        withTempRepo (fun root ->
            Watch.setWatchPathComparisonForWatchTests StringComparison.Ordinal

            let uploadedPath = Path.Combine(root, "foo.txt")
            /// Tracks the Differences passed to the apply seam so case-sensitive matching is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            File.WriteAllText(uploadedPath, "uploaded lowercase content")
            Watch.OnChanged(changedEvent uploadedPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(graceStatusTracking [| "Foo.txt" |] Array.empty<string>)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ filePath =
                recordUploadedFileVersion $"{filePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForNoDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.File, "foo.txt"
                |])

    /// Verifies that case-insensitive tracked file matching emits changes with the tracked path casing.
    [<Test>]
    let ``case-insensitive changed file preserves tracked path casing`` () =
        withTempRepo (fun root ->
            Watch.setWatchPathComparisonForWatchTests StringComparison.OrdinalIgnoreCase

            let uploadedPath = Path.Combine(root, "foo.txt")
            /// Tracks the Differences passed to the apply seam so tracked casing is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            File.WriteAllText(uploadedPath, "uploaded lowercase content")
            Watch.OnChanged(changedEvent uploadedPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(graceStatusTracking [| "Foo.txt" |] Array.empty<string>)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ filePath =
                recordUploadedFileVersion $"{filePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForNoDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Change, FileSystemEntryType.File, "Foo.txt"
                |])

    /// Verifies that case-insensitive deleted file matching emits deletes with the tracked path casing.
    [<Test>]
    let ``case-insensitive deleted file preserves tracked path casing`` () =
        withTempRepo (fun root ->
            Watch.setWatchPathComparisonForWatchTests StringComparison.OrdinalIgnoreCase

            let deletedPath = Path.Combine(root, "foo.txt")
            let status = graceStatusTracking [| "Foo.txt" |] Array.empty<string>
            /// Tracks the Differences passed to the apply seam so tracked delete casing is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            Watch.setGraceStatusForWatchTests status
            Watch.OnDeleted(deletedEvent deletedPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForNoDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Delete, FileSystemEntryType.File, "Foo.txt"
                |])

    /// Verifies that case-insensitive file adds under tracked directories preserve the tracked parent casing.
    [<Test>]
    let ``case-insensitive added file preserves tracked parent directory casing`` () =
        withTempRepo (fun root ->
            Watch.setWatchPathComparisonForWatchTests StringComparison.OrdinalIgnoreCase

            let uploadedDirectory = Path.Combine(root, "src")
            let uploadedPath = Path.Combine(uploadedDirectory, "new.txt")
            let status = graceStatusTracking Array.empty<string> [| "Src" |]
            /// Tracks the Differences passed to the apply seam so tracked parent casing is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            Directory.CreateDirectory(uploadedDirectory)
            |> ignore

            File.WriteAllText(uploadedPath, "new file under differently-cased tracked parent")
            Watch.OnCreated(changedEvent uploadedPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ filePath =
                recordUploadedFileVersion $"{filePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForNoDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.File, "Src/new.txt"
                |])

    /// Verifies that nested file adds preserve the tracked ancestor directory casing for all derived add paths.
    [<Test>]
    let ``case-insensitive nested added file preserves tracked ancestor directory casing`` () =
        withTempRepo (fun root ->
            Watch.setWatchPathComparisonForWatchTests StringComparison.OrdinalIgnoreCase

            let uploadedDirectory = Path.Combine(root, "src", "newdir")
            let uploadedPath = Path.Combine(uploadedDirectory, "a.txt")
            let status = graceStatusTracking Array.empty<string> [| "Src" |]
            /// Tracks the Differences passed to the apply seam so nested tracked ancestor casing is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            Directory.CreateDirectory(uploadedDirectory)
            |> ignore

            File.WriteAllText(uploadedPath, "new file under a nested differently-cased tracked ancestor")
            Watch.OnCreated(changedEvent uploadedPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ filePath =
                recordUploadedFileVersion $"{filePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForNoDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.Directory, "Src/newdir"
                    DifferenceType.Add, FileSystemEntryType.File, "Src/newdir/a.txt"
                |])

    /// Verifies that stale deletes requeue canceled same-path file upload work before status-only triggers drain.
    [<Test>]
    let ``stale delete after same-path change requeues canceled upload work`` () =
        withTempRepo (fun root ->
            let relativePath = "same-path-stale-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            /// Tracks upload Calls changes so the canceled upload is proven to retry.
            let mutable uploadCalls = 0
            /// Tracks scan Calls changes so this scenario proves the upload retry path avoids a healthy scan.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so final status work is applied once after reupload.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the final file change is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            Watch.setGraceStatusForWatchTests status
            File.WriteAllText(filePath, "updated content that should still upload")
            Watch.OnChanged(changedEvent filePath)
            Watch.OnDeleted(deletedEvent filePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ pendingFilePath =
                uploadCalls <- uploadCalls + 1
                recordUploadedFileVersion $"{pendingFilePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            let processPendingWork () =
                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scanForDifferences
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

            processPendingWork ()

            let afterRequeue = Watch.pendingWatchWorkSnapshotForTests ()

            afterRequeue.FilesToProcess
            |> should equal [| filePath |]

            afterRequeue.StatusUpdateTriggers
            |> should equal [| relativePath |]

            processPendingWork ()

            uploadCalls |> should equal 1
            scanCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Change, FileSystemEntryType.File, relativePath
                |])

    /// Verifies that stale deletes requeue canceled same-path upload work for files not already tracked in GraceStatus.
    [<Test>]
    let ``stale delete after untracked add requeues canceled upload work`` () =
        withTempRepo (fun root ->
            let relativePath = "same-path-stale-add.txt"
            let filePath = Path.Combine(root, relativePath)
            /// Tracks upload Calls changes so the untracked add retry is proven.
            let mutable uploadCalls = 0
            /// Tracks scan Calls changes so this scenario proves the upload retry path avoids a healthy scan.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so final status work is applied once after reupload.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the final file add is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            File.WriteAllText(filePath, "new content that should still upload")
            Watch.OnChanged(changedEvent filePath)
            Watch.OnDeleted(deletedEvent filePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ pendingFilePath =
                uploadCalls <- uploadCalls + 1
                recordUploadedFileVersion $"{pendingFilePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            let processPendingWork () =
                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scanForDifferences
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

            processPendingWork ()

            let afterRequeue = Watch.pendingWatchWorkSnapshotForTests ()

            afterRequeue.FilesToProcess
            |> should equal [| filePath |]

            afterRequeue.StatusUpdateTriggers
            |> should equal [| relativePath |]

            processPendingWork ()

            uploadCalls |> should equal 1
            scanCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.File, relativePath
                |])

    /// Verifies that a pending uploaded file addition is rescanned and cleared when a later delete removes the file.
    [<Test>]
    let ``delete after failed uploaded add rescans and clears stale pending file difference`` () =
        withTempRepo (fun root ->
            let relativePath = "deleted-before-retry.txt"
            let filePath = Path.Combine(root, relativePath)
            /// Tracks scan Calls changes so this scenario can assert stale pending work is rescanned.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so only the failed add attempt reached status apply.
            let mutable applyFromDifferencesCalls = 0

            File.WriteAllText(filePath, "transient uploaded content")
            Watch.OnChanged(changedEvent filePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ pendingFilePath =
                recordUploadedFileVersion $"{pendingFilePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status _ _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                Task.FromResult(None)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            let processPendingWork () =
                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scanForDifferences
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

            processPendingWork ()

            applyFromDifferencesCalls |> should equal 1

            File.Delete(filePath)
            Watch.OnDeleted(deletedEvent filePath)

            processPendingWork ()

            scanCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 1

            let afterRetry = Watch.pendingWatchWorkSnapshotForTests ()

            afterRetry.StatusUpdateTriggers
            |> should equal Array.empty<string>

            afterRetry.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that stale parent directory adds are cleared when their child file add disappears before retry.
    [<Test>]
    let ``delete after failed nested uploaded add clears stale parent directory difference`` () =
        withTempRepo (fun root ->
            let relativePath = "new-parent/deleted-before-retry.txt"
            let parentPath = Path.Combine(root, "new-parent")
            let filePath = Path.Combine(root, relativePath)
            /// Tracks scan Calls changes so this scenario proves stale parent cleanup uses the retry rescan.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so the stale parent add is not applied on retry.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the first failed parent-plus-file add is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            Directory.CreateDirectory(parentPath) |> ignore

            File.WriteAllText(filePath, "transient nested upload content")
            Watch.OnChanged(changedEvent filePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ pendingFilePath =
                recordUploadedFileVersion $"{pendingFilePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                observedDifferences <- differences
                Task.FromResult(None)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            let processPendingWork () =
                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scanForDifferences
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

            processPendingWork ()

            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.Directory, "new-parent"
                    DifferenceType.Add, FileSystemEntryType.File, relativePath
                |]

            File.Delete(filePath)
            Directory.Delete(parentPath)
            Watch.OnDeleted(deletedEvent filePath)

            processPendingWork ()

            scanCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 1

            let afterRetry = Watch.pendingWatchWorkSnapshotForTests ()

            afterRetry.StatusUpdateTriggers
            |> should equal Array.empty<string>

            afterRetry.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that a clean retry drops a stale uploaded file change after the file returns to tracked content.
    [<Test>]
    let ``clean rescan after reverted upload clears stale pending change`` () =
        withTempRepo (fun root ->
            let relativePath = "reverted-before-retry.txt"
            let filePath = Path.Combine(root, relativePath)
            /// Tracks scan Calls changes so this scenario can assert the clean retry path was used.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so stale changes are not re-applied.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the first failed change is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            File.WriteAllText(filePath, "tracked content")

            let trackedFile =
                (Services.createLocalFileVersion (FileInfo(filePath)))
                    .GetAwaiter()
                    .GetResult()
                    .Value

            File.WriteAllText(filePath, "changed content")
            Watch.OnChanged(changedEvent filePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(graceStatusTrackingFileVersions [| trackedFile |])

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ pendingFilePath =
                recordUploadedFileVersion $"{pendingFilePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                observedDifferences <- differences
                Task.FromResult(None)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            let processPendingWork () =
                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scanForDifferences
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

            processPendingWork ()

            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Change, FileSystemEntryType.File, relativePath
                |]

            File.WriteAllText(filePath, "tracked content")
            Watch.OnChanged(changedEvent filePath)

            processPendingWork ()
            processPendingWork ()

            scanCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 1

            let afterRetry = Watch.pendingWatchWorkSnapshotForTests ()

            afterRetry.StatusUpdateTriggers
            |> should equal Array.empty<string>

            afterRetry.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that replacing a tracked file with a directory preserves the file delete half of the replacement.
    [<Test>]
    let ``tracked file replaced by directory still derives file delete`` () =
        withTempRepo (fun root ->
            let relativePath = "replaced-path"
            let replacementDirectory = Path.Combine(root, relativePath)
            /// Tracks the Differences passed to the apply seam so the replaced file delete is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            Directory.CreateDirectory(replacementDirectory)
            |> ignore

            Watch.OnDeleted(deletedEvent replacementDirectory)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(graceStatusTracking [| relativePath |] Array.empty<string>)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForNoDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Delete, FileSystemEntryType.File, relativePath
                |])

    /// Verifies that content-equivalent changed observations do not apply a Save-producing difference.
    [<Test>]
    let ``content-equivalent changed file derives no status difference`` () =
        withTempRepo (fun root ->
            let changedPath = Path.Combine(root, "equivalent.txt")
            /// Tracks scan Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so this scenario can assert the no-save behavior.
            let mutable applyFromDifferencesCalls = 0

            File.WriteAllText(changedPath, "same content")

            let trackedFile =
                (Services.createLocalFileVersion (FileInfo(changedPath)))
                    .GetAwaiter()
                    .GetResult()
                    .Value

            Watch.OnChanged(changedEvent changedPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(graceStatusTrackingFileVersions [| trackedFile |])

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ filePath =
                recordUploadedFileVersion $"{filePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status _ _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            scanCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 0)

    /// Verifies that recreated paths invalidate stale startup deletes before status apply.
    [<Test>]
    let ``recreated path invalidates stale startup delete before apply`` () =
        withTempRepo (fun root ->
            let relativePath = "recreated.txt"
            let fullPath = Path.Combine(root, relativePath)
            /// Tracks scan-oriented update Calls changes so this scenario can assert the no-scan-update behavior.
            let mutable scanOrientedUpdateCalls = 0
            /// Tracks apply-from-differences Calls changes so this scenario can assert no stale delete was applied.
            let mutable applyFromDifferencesCalls = 0

            Watch.queueStartupDifferenceForWatch (FileSystemDifference.Create Delete FileSystemEntryType.File relativePath)
            File.WriteAllText(fullPath, "recreated equivalent content")

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(graceStatusTracking [| relativePath |] Array.empty<string>)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                scanOrientedUpdateCalls <- scanOrientedUpdateCalls + 1
                Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1

                differences
                |> Seq.exists (fun difference -> difference.DifferenceType = DifferenceType.Delete)
                |> should equal false

                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForNoDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            scanOrientedUpdateCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 0

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that startup rescan changes do not apply a stale delete for the same recreated path.
    [<Test>]
    let ``startup rescan change suppresses stale delete for same path`` () =
        withTempRepo (fun root ->
            let relativePath = "recreated-with-change.txt"
            let fullPath = Path.Combine(root, relativePath)
            let staleDelete = FileSystemDifference.Create Delete FileSystemEntryType.File relativePath
            let liveChange = FileSystemDifference.Create Change FileSystemEntryType.File relativePath
            /// Tracks scan Calls changes so this scenario can assert the startup fallback still runs when required.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so this scenario can assert the applied action set.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the test fails on Delete plus Change.
            let mutable observedDifferences = List<FileSystemDifference>()

            Watch.queueStartupDifferenceForWatch staleDelete
            File.WriteAllText(fullPath, "recreated changed content")
            Watch.OnChanged(changedEvent fullPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(graceStatusTracking [| relativePath |] Array.empty<string>)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ filePath =
                recordUploadedFileVersion $"{filePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>([ liveChange ]))

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            scanCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.toArray
            |> should equal [| liveChange |])

    /// Verifies that renamed file queues old path status trigger and new path upload work.
    [<Test>]
    let ``renamed file queues old path status trigger and new path upload work`` () =
        withTempRepo (fun root ->
            let oldPath = Path.Combine(root, "old-name.txt")
            let newPath = Path.Combine(root, "new-name.txt")
            File.WriteAllText(newPath, "new rename target")

            Watch.OnRenamed(renamedEvent oldPath newPath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "old-name.txt" |]

            pending.FilesToProcess
            |> should equal [| newPath |])

    /// Verifies that renamed file cancels old path pending upload before queuing new path.
    [<Test>]
    let ``renamed file cancels old path pending upload before queuing new path`` () =
        withTempRepo (fun root ->
            let oldPath = Path.Combine(root, "queued-old-name.txt")
            let newPath = Path.Combine(root, "queued-new-name.txt")

            File.WriteAllText(oldPath, "payload before rename")
            Watch.OnChanged(changedEvent oldPath)
            File.Move(oldPath, newPath)

            Watch.OnRenamed(renamedEvent oldPath newPath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "queued-old-name.txt" |]

            pending.FilesToProcess
            |> should equal [| newPath |])

    /// Verifies that renamed directory queues old path status trigger and new contents upload work.
    [<Test>]
    let ``renamed directory queues old path status trigger and new contents upload work`` () =
        withTempRepo (fun root ->
            writeGraceIgnore root [| "*.tmp" |]

            let oldPath = Path.Combine(root, "old-assets")
            let newPath = Path.Combine(root, "new-assets")
            let nestedPath = Path.Combine(newPath, "nested")
            Directory.CreateDirectory(nestedPath) |> ignore

            let rootFile = Path.Combine(newPath, "asset.txt")
            let nestedFile = Path.Combine(nestedPath, "nested.txt")
            let ignoredFile = Path.Combine(nestedPath, "ignored.tmp")
            File.WriteAllText(rootFile, "root asset")
            File.WriteAllText(nestedFile, "nested asset")
            File.WriteAllText(ignoredFile, "ignored asset")

            Watch.OnRenamed(renamedEvent oldPath newPath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "old-assets" |]

            pending.FilesToProcess
            |> should equal ([| rootFile; nestedFile |] |> Array.sort)

            /// Builds update calls test data used to exercise CLI watch behavior.
            let updateCalls, uploadCalls = processPendingWatchWorkForTest ()

            uploadCalls |> should equal 2
            updateCalls |> should equal 1)

    /// Verifies that renamed directory enumeration failure keeps old path trigger for retry.
    [<Test>]
    let ``renamed directory enumeration failure keeps old path trigger for retry`` () =
        withTempRepo (fun root ->
            let oldPath = Path.Combine(root, "old-enumeration-name")
            let newPath = Path.Combine(root, "new-enumeration-name")
            Directory.CreateDirectory(newPath) |> ignore

            Watch.setEnumerateFilesForDirectoryUploadForWatchTests (fun _ -> raise (UnauthorizedAccessException("blocked enumeration")))

            Assert.DoesNotThrow(Action(fun () -> Watch.OnRenamed(renamedEvent oldPath newPath)))

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "old-enumeration-name" |]

            pending.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that file event during status only update keeps trigger pending for upload first retry.
    [<Test>]
    let ``file event during status-only update keeps trigger pending for upload-first retry`` () =
        withTempRepo (fun root ->
            let deletedFilePath = Path.Combine(root, "delete-before-race.txt")
            let createdFilePath = Path.Combine(root, "created-during-status-update.txt")
            let status = graceStatusTracking [| "delete-before-race.txt" |] Array.empty<string>
            /// Tracks update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable updateCalls = 0

            Watch.OnDeleted(deletedEvent deletedFilePath)

            /// Builds update grace status test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                updateCalls <- updateCalls + 1

                if updateCalls = 1 then
                    File.WriteAllText(createdFilePath, "created during status-only rescan")
                    Watch.OnChanged(changedEvent createdFilePath)

                Task.FromResult(Some status)

            processPendingWatchWorkWithStatusClients (fun () -> Task.FromResult(status)) updateGraceStatus

            updateCalls |> should equal 1

            let afterRace = Watch.pendingWatchWorkSnapshotForTests ()

            afterRace.StatusUpdateTriggers
            |> should equal [| "delete-before-race.txt" |]

            afterRace.FilesToProcess
            |> should equal [| createdFilePath |])

    /// Verifies that startup deleted file difference queues status only work without upload work.
    [<Test>]
    let ``startup deleted file difference queues status-only work without upload work`` () =
        withTempRepo (fun _ ->
            Watch.queueStartupDifferenceForWatch (FileSystemDifference.Create Delete FileSystemEntryType.File "offline-delete.txt")

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "offline-delete.txt" |]

            pending.FilesToProcess
            |> should equal Array.empty<string>

            /// Builds update calls test data used to exercise CLI watch behavior.
            let updateCalls, uploadCalls = processPendingWatchWorkForTest ()

            updateCalls |> should equal 1
            uploadCalls |> should equal 0)

    /// Verifies that startup-derived file differences use the shared apply seam without rescanning.
    [<Test>]
    let ``startup file difference applies pre-derived differences without scan-oriented update`` () =
        withTempRepo (fun _ ->
            let startupDifference = FileSystemDifference.Create Add FileSystemEntryType.File "offline-add.txt"
            /// Tracks scan-oriented update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable scanOrientedUpdateCalls = 0
            /// Tracks apply-from-differences Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks upload Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable uploadCalls = 0
            /// Tracks the Differences passed to the apply seam so the test fails if startup scan findings are discarded.
            let mutable observedDifferences = List<FileSystemDifference>()

            Watch.queueStartupDifferenceForWatch startupDifference

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ filePath =
                uploadCalls <- uploadCalls + 1
                let fullPath = $"{filePath}"

                if File.Exists(fullPath) then recordUploadedFileVersion fullPath

                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                scanOrientedUpdateCalls <- scanOrientedUpdateCalls + 1
                Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForNoDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            uploadCalls |> should equal 1
            scanOrientedUpdateCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.toArray
            |> should equal [| startupDifference |]

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.FilesToProcess
            |> should equal Array.empty<string>

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that live uploads drained before startup apply are included by a fresh status scan.
    [<Test>]
    let ``startup apply rescans after live upload drains before applying differences`` () =
        withTempRepo (fun root ->
            let startupDifference = FileSystemDifference.Create Delete FileSystemEntryType.File "offline-delete.txt"
            let liveFilePath = Path.Combine(root, "live-upload.txt")
            let liveUploadDifference = FileSystemDifference.Create Add FileSystemEntryType.File "live-upload.txt"
            /// Tracks scan Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable scanCalls = 0
            /// Tracks scan-oriented update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable scanOrientedUpdateCalls = 0
            /// Tracks apply-from-differences Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks upload Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable uploadCalls = 0
            /// Tracks the Differences passed to the apply seam so the test fails if live uploads are omitted.
            let mutable observedDifferences = List<FileSystemDifference>()

            Watch.queueStartupDifferenceForWatch startupDifference
            File.WriteAllText(liveFilePath, "live content")
            Watch.OnChanged(changedEvent liveFilePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ =
                uploadCalls <- uploadCalls + 1
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                scanOrientedUpdateCalls <- scanOrientedUpdateCalls + 1
                Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>([ liveUploadDifference ]))

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            uploadCalls |> should equal 1
            scanCalls |> should equal 1
            scanOrientedUpdateCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.toArray
            |> should
                equal
                [|
                    startupDifference
                    liveUploadDifference
                |]

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.FilesToProcess
            |> should equal Array.empty<string>

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that failed startup rescan retries before applying pending startup differences.
    [<Test>]
    let ``startup apply retries failed rescan before applying pending differences`` () =
        withTempRepo (fun root ->
            let startupDifference = FileSystemDifference.Create Add FileSystemEntryType.File "retry-after-failed-rescan.txt"
            let liveFilePath = Path.Combine(root, "live-upload-after-failed-rescan.txt")
            let liveUploadDifference = FileSystemDifference.Create Add FileSystemEntryType.File "live-upload-after-failed-rescan.txt"
            /// Tracks scan Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks upload Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable uploadCalls = 0
            /// Tracks the Differences passed to the apply seam so the test fails if retry skips the failed rescan result.
            let mutable observedDifferences = List<FileSystemDifference>()

            Watch.queueStartupDifferenceForWatch startupDifference
            File.WriteAllText(liveFilePath, "live content")
            Watch.OnChanged(changedEvent liveFilePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ =
                uploadCalls <- uploadCalls + 1
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Services.setLastScanForDifferencesSuccessfulForWatchTests (scanCalls > 1)

                Task.FromResult(List<FileSystemDifference>([ liveUploadDifference ]))

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            let processPendingWork () =
                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scanForDifferences
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

            processPendingWork ()

            scanCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 0

            processPendingWork ()

            uploadCalls |> should equal 2
            scanCalls |> should equal 2
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.toArray
            |> should
                equal
                [|
                    startupDifference
                    liveUploadDifference
                |]

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.FilesToProcess
            |> should equal Array.empty<string>

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that failed startup-derived status application remains retryable without rescanning.
    [<Test>]
    let ``startup pre-derived differences retry apply seam after transient status failure`` () =
        withTempRepo (fun _ ->
            let startupDifference = FileSystemDifference.Create Add FileSystemEntryType.File "retry-add.txt"
            /// Tracks scan-oriented update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable scanOrientedUpdateCalls = 0
            /// Tracks apply-from-differences Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks upload Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable uploadCalls = 0

            Watch.queueStartupDifferenceForWatch startupDifference

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ =
                uploadCalls <- uploadCalls + 1
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                scanOrientedUpdateCalls <- scanOrientedUpdateCalls + 1
                Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status _ _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1

                if applyFromDifferencesCalls = 1 then
                    Task.FromResult(None)
                else
                    Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            let processPendingWork () =
                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scanForNoDifferences
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

            processPendingWork ()
            processPendingWork ()

            uploadCalls |> should equal 1
            scanOrientedUpdateCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 2

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.FilesToProcess
            |> should equal Array.empty<string>

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that already-applied startup differences are dropped when concurrent file work blocks status commit.
    [<Test>]
    let ``startup applied differences are dropped when commit gate loses file-event race`` () =
        withTempRepo (fun root ->
            let startupDifference = FileSystemDifference.Create Delete FileSystemEntryType.File "race-delete.txt"
            let createdDuringApplyPath = Path.Combine(root, "created-during-apply.txt")
            /// Tracks scan-oriented update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable scanOrientedUpdateCalls = 0
            /// Tracks apply-from-differences Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks upload Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable uploadCalls = 0

            Watch.queueStartupDifferenceForWatch startupDifference

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ =
                uploadCalls <- uploadCalls + 1
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                scanOrientedUpdateCalls <- scanOrientedUpdateCalls + 1
                Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1

                if applyFromDifferencesCalls = 1 then
                    differences
                    |> Seq.toArray
                    |> should equal [| startupDifference |]

                    File.WriteAllText(createdDuringApplyPath, "created during apply")
                    Watch.OnChanged(changedEvent createdDuringApplyPath)
                else
                    differences
                    |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
                    |> Seq.toArray
                    |> should
                        equal
                        [|
                            DifferenceType.Add, FileSystemEntryType.File, "created-during-apply.txt"
                        |]

                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            let processPendingWork () =
                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scanForNoDifferences
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

            processPendingWork ()

            let afterRace = Watch.pendingWatchWorkSnapshotForTests ()

            afterRace.FilesToProcess
            |> should equal [| createdDuringApplyPath |]

            processPendingWork ()

            uploadCalls |> should equal 1
            scanOrientedUpdateCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1)

    /// Verifies that startup apply does not drain unrelated status-only work queued before the same pass.
    [<Test>]
    let ``startup apply preserves unrelated status-only trigger for later scan`` () =
        withTempRepo (fun root ->
            let startupDifference = FileSystemDifference.Create Add FileSystemEntryType.File "startup-add.txt"
            let unrelatedDeletedPath = Path.Combine(root, "unrelated-delete.txt")
            /// Tracks scan-oriented update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable scanOrientedUpdateCalls = 0
            /// Tracks apply-from-differences Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable applyFromDifferencesCalls = 0

            Watch.queueStartupDifferenceForWatch startupDifference
            Watch.OnDeleted(deletedEvent unrelatedDeletedPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                scanOrientedUpdateCalls <- scanOrientedUpdateCalls + 1
                Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status _ _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForNoDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            scanOrientedUpdateCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.StatusUpdateTriggers
            |> should equal [| "unrelated-delete.txt" |])

    /// Verifies that renamed tracked file matching directory only ignore queues old path status trigger.
    [<Test>]
    let ``renamed tracked file matching directory-only ignore queues old path status trigger`` () =
        withTempRepo (fun root ->
            writeGraceIgnore root [| "archive.tmp/" |]

            let oldPath = Path.Combine(root, "archive.tmp")
            let newPath = Path.Combine(root, "renamed.txt")
            File.WriteAllText(newPath, "new rename target")
            Watch.setGraceStatusForWatchTests (graceStatusTracking [| "archive.tmp" |] Array.empty<string>)

            Watch.OnRenamed(renamedEvent oldPath newPath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "archive.tmp" |]

            pending.FilesToProcess
            |> should equal [| newPath |])

    /// Verifies that watch exits with auth guidance when no token is configured.
    [<Test>]
    let ``watch exits with auth guidance when no token is configured`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                /// Verifies that the CLI watch scenario exits with the expected process status.
                let exitCode, output = runWithCapturedOutput [| "watch" |]

                if exitCode <> -1 then
                    Assert.Fail(
                        $"Expected watch to exit with -1 when auth is missing. Actual: {exitCode}.{Environment.NewLine}Output:{Environment.NewLine}{output}"
                    )

                output
                |> should contain "Unable to acquire an access token for SignalR"

                output
                |> should contain "Authentication is not configured."))

    /// Verifies that watch exits nonzero when live watcher status already exists.
    [<Test>]
    let ``watch exits nonzero when live watcher status already exists`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                let ipcFileName = writeLiveWatchStatusFile ()
                let originalContents = readFileIfExists ipcFileName
                /// Verifies that the CLI watch scenario exits with the expected process status.
                let exitCode, output = runWithCapturedOutput [| "watch" |]

                exitCode |> should equal -1

                output
                |> should contain "GraceWatch is already running"

                output
                |> should not' (contain "Unable to acquire an access token for SignalR")

                readFileIfExists ipcFileName
                |> should equal originalContents))

    /// Verifies that watch startup claim is atomic for simultaneous ordinary starts.
    [<Test>]
    let ``watch startup claim is atomic for simultaneous ordinary starts`` () =
        withTempRepo (fun _ ->
            let claimTasks =
                Array.init 8 (fun _ ->
                    Task.Run (fun () ->
                        Services
                            .tryClaimGraceWatchInterprocessFile()
                            .Result))

            claimTasks
            |> Array.map (fun claimTask -> claimTask :> Task)
            |> Task.WaitAll

            let successfulClaims =
                claimTasks
                |> Array.filter (fun claimTask -> claimTask.Result)

            successfulClaims.Length |> should equal 1

            let status = Services.getGraceWatchStatus().Result
            status |> should equal None)

    /// Verifies that watch startup claim blocks second ordinary start without usable status.
    [<Test>]
    let ``watch startup claim blocks second ordinary start without usable status`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                let claimed =
                    Services
                        .tryClaimGraceWatchInterprocessFile()
                        .Result

                claimed |> should equal true

                let status = Services.getGraceWatchStatus().Result
                status |> should equal None

                /// Verifies that the CLI watch scenario exits with the expected process status.
                let exitCode, output = runWithCapturedOutput [| "watch" |]

                exitCode |> should equal -1

                output
                |> should contain "GraceWatch is already running"

                output
                |> should not' (contain "Unable to acquire an access token for SignalR")))

    /// Verifies that watch startup claim replaces malformed stale ipc file.
    [<Test>]
    let ``watch startup claim replaces malformed stale ipc file`` () =
        withTempRepo (fun _ ->
            let ipcFileName = Services.IpcFileName()

            Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
            |> ignore

            File.WriteAllText(ipcFileName, "not-json")

            let claimed =
                Services
                    .tryClaimGraceWatchInterprocessFile()
                    .Result

            claimed |> should equal true

            let status = Services.getGraceWatchStatus().Result
            status |> should equal None)

    /// Verifies that watch status preserves root blake3 from grace status index.
    [<Test>]
    let ``watch status preserves root Blake3 from GraceStatus index`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()
            let rootSha256Hash = Sha256Hash "watch-root-sha"
            let rootBlake3Hash = Blake3Hash "watch-root-blake3"

            let rootDirectory =
                LocalDirectoryVersion.CreateWithHashes
                    rootDirectoryId
                    OwnerId.Empty
                    OrganizationId.Empty
                    RepositoryId.Empty
                    Constants.RootDirectoryPath
                    rootSha256Hash
                    rootBlake3Hash
                    (List<DirectoryVersionId>())
                    (List<LocalFileVersion>())
                    0L
                    DateTime.UtcNow

            let index = GraceIndex()

            index.TryAdd(rootDirectoryId, rootDirectory)
            |> ignore

            let graceStatus = { GraceStatus.Default with Index = index; RootDirectoryId = rootDirectoryId; RootDirectorySha256Hash = rootSha256Hash }

            (Services.updateGraceWatchInterprocessFile graceStatus None)
                .GetAwaiter()
                .GetResult()

            match Services.getGraceWatchStatus().Result with
            | Some status ->
                status.RootDirectoryId
                |> should equal rootDirectoryId

                status.RootDirectorySha256Hash
                |> should equal rootSha256Hash

                status.RootDirectoryBlake3Hash
                |> should equal rootBlake3Hash
            | None -> Assert.Fail("Expected usable watch status with root BLAKE3."))

    /// Verifies that watch status preserves root blake3 from grace status metadata when index is empty.
    [<Test>]
    let ``watch status preserves root Blake3 from GraceStatus metadata when index is empty`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()
            let rootSha256Hash = Sha256Hash "watch-root-sha"
            let rootBlake3Hash = Blake3Hash "watch-root-blake3"
            let directoryIds = HashSet<DirectoryVersionId>([| rootDirectoryId |])

            let graceStatus =
                { GraceStatus.Default with
                    RootDirectoryId = rootDirectoryId
                    RootDirectorySha256Hash = rootSha256Hash
                    RootDirectoryBlake3Hash = rootBlake3Hash
                }

            (Services.updateGraceWatchInterprocessFile graceStatus (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            match Services.getGraceWatchStatus().Result with
            | Some status ->
                status.RootDirectoryId
                |> should equal rootDirectoryId

                status.RootDirectorySha256Hash
                |> should equal rootSha256Hash

                status.RootDirectoryBlake3Hash
                |> should equal rootBlake3Hash
            | None -> Assert.Fail("Expected usable watch status with fallback root BLAKE3."))

    /// Verifies that watch check exits zero when live watcher status exists.
    [<Test>]
    let ``watch check exits zero when live watcher status exists`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

            /// Verifies that the CLI watch scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "watch"
                                         "--check" |]

            exitCode |> should equal 0

            output |> should contain "GraceWatch is running"

            readFileIfExists ipcFileName
            |> should equal originalContents)

    /// Verifies that watch check through main exits zero and preserves live watcher status.
    [<Test>]
    let ``watch check through main exits zero and preserves live watcher status`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

            /// Verifies that the CLI watch scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "watch"
                                                  "--check" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            standardOut
            |> should contain "GraceWatch is running"

            readFileIfExists ipcFileName
            |> should equal originalContents)

    /// Verifies that watch check json mode emits error envelope and preserves live watcher status.
    [<Test>]
    let ``watch check json mode emits error envelope and preserves live watcher status`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

            /// Verifies that the CLI watch scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "watch"
                                                  "--check" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            standardOut
            |> should not' (contain "GraceWatch is running")

            use document = parseJsonOutput standardOut
            let root = document.RootElement

            root.GetProperty("Error").GetString()
            |> should contain "watch is a continuous foreground workflow"

            /// Tracks return Value changes so this scenario can assert the resulting side effect explicitly.
            let mutable returnValue = Unchecked.defaultof<JsonElement>

            root.TryGetProperty("ReturnValue", &returnValue)
            |> should equal false

            readFileIfExists ipcFileName
            |> should equal originalContents)

    /// Verifies that watch check select mode emits error envelope and preserves live watcher status.
    [<Test>]
    let ``watch check select mode emits error envelope and preserves live watcher status`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

            /// Verifies that the CLI watch scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "watch"
                                                  "--check"
                                                  "--select"
                                                  "RootDirectoryId" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            standardOut
            |> should not' (contain "GraceWatch is running")

            use document = parseJsonOutput standardOut
            let root = document.RootElement

            root.GetProperty("Error").GetString()
            |> should contain "does not support --select in this release"

            /// Tracks return Value changes so this scenario can assert the resulting side effect explicitly.
            let mutable returnValue = Unchecked.defaultof<JsonElement>

            root.TryGetProperty("ReturnValue", &returnValue)
            |> should equal false

            readFileIfExists ipcFileName
            |> should equal originalContents)

    /// Verifies that watch check exits nonzero when live watcher status is missing.
    [<Test>]
    let ``watch check exits nonzero when live watcher status is missing`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                /// Verifies that the CLI watch scenario exits with the expected process status.
                let exitCode, output =
                    runWithCapturedOutput [| "watch"
                                             "--check" |]

                exitCode |> should equal -1

                output
                |> should contain "GraceWatch is not running"

                output
                |> should not' (contain "Unable to acquire an access token for SignalR")

                Services.IpcFileName()
                |> File.Exists
                |> should equal false))

    /// Verifies that watch check exits nonzero and preserves startup claim.
    [<Test>]
    let ``watch check exits nonzero and preserves startup claim`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                let claimed =
                    Services
                        .tryClaimGraceWatchInterprocessFile()
                        .Result

                claimed |> should equal true

                let ipcFileName = Services.IpcFileName()
                let originalContents = readFileIfExists ipcFileName

                /// Verifies that the CLI watch scenario exits with the expected process status.
                let exitCode, output =
                    runWithCapturedOutput [| "watch"
                                             "--check" |]

                exitCode |> should equal -1

                output
                |> should contain "GraceWatch is not running"

                output
                |> should not' (contain "Unable to acquire an access token for SignalR")

                readFileIfExists ipcFileName
                |> should equal originalContents))

    /// Verifies that watch cached file changes upload cached object for save enrichment.
    [<Test>]
    let ``watch cached file changes upload cached object for save enrichment`` () =
        let filePath = FilePath @"C:\repo\dir\cached-file.txt"

        let cachedFileVersion =
            FileVersion.Create "dir/cached-file.txt" (Sha256Hash "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") String.Empty true 12L

        /// Tracks uploaded File Versions changes so this scenario can assert the resulting side effect explicitly.
        let mutable uploadedFileVersions = Array.empty<FileVersion>

        /// Builds copy file to object cache test data used to exercise CLI watch behavior.
        let copyFileToObjectCache _ = Task.FromResult<FileVersion option> None

        /// Gets cached file version needed by the test scenario.
        let getCachedFileVersion _ = Task.FromResult(Some cachedFileVersion)

        /// Builds upload file versions test data used to exercise CLI watch behavior.
        let uploadFileVersions (parameters: GetUploadMetadataForFilesParameters) =
            uploadedFileVersions <- parameters.FileVersions
            Task.FromResult(Ok(GraceReturnValue.Create parameters.FileVersions parameters.CorrelationId))

        let parameters =
            GetUploadMetadataForFilesParameters(
                OwnerId = $"{OwnerId.Empty}",
                OrganizationId = $"{OrganizationId.Empty}",
                RepositoryId = $"{RepositoryId.Empty}",
                CorrelationId = "watch-cache-hit-test"
            )

        (Watch.copyFileToObjectDirectoryAndUploadToStorageWithClients copyFileToObjectCache getCachedFileVersion uploadFileVersions parameters filePath)
            .GetAwaiter()
            .GetResult()

        uploadedFileVersions
        |> should equal [| cachedFileVersion |]

        parameters.FileVersions
        |> should equal [| cachedFileVersion |]

    /// Verifies that object cache copies preserve scanner blake3 identity.
    [<Test>]
    let ``object-cache copies preserve scanner Blake3 identity`` () =
        withTempRepo (fun root ->
            let nestedDirectory = Path.Combine(root, "dir")

            Directory.CreateDirectory(nestedDirectory)
            |> ignore

            let filePath = Path.Combine(nestedDirectory, "whole-file.txt")
            File.WriteAllText(filePath, "whole-file watch upload payload")

            let localFileVersion =
                match (Services.createLocalFileVersion (FileInfo filePath))
                    .Result
                    with
                | Some fileVersion -> fileVersion
                | None -> failwith "Expected scanner file version."

            let copiedFileVersion =
                match (Services.copyToObjectDirectory (FilePath filePath))
                    .Result
                    with
                | Some fileVersion -> fileVersion
                | None -> failwith "Expected object-cache copy to create the object."

            String.IsNullOrWhiteSpace(string copiedFileVersion.Blake3Hash)
            |> should equal false

            copiedFileVersion.Blake3Hash
            |> should equal localFileVersion.Blake3Hash)

    /// Verifies that object cache copy returns file version when object already exists.
    [<Test>]
    let ``object-cache copy returns file version when object already exists`` () =
        withTempRepo (fun root ->
            let nestedDirectory = Path.Combine(root, "dir")

            Directory.CreateDirectory(nestedDirectory)
            |> ignore

            let filePath = Path.Combine(nestedDirectory, "cached-file.txt")
            File.WriteAllText(filePath, "already cached object payload")

            let firstCopy =
                match (Services.copyToObjectDirectory (FilePath filePath))
                    .Result
                    with
                | Some fileVersion -> fileVersion
                | None -> failwith "Expected the first object-cache copy to create the object."

            let secondCopy =
                match (Services.copyToObjectDirectory (FilePath filePath))
                    .Result
                    with
                | Some fileVersion -> fileVersion
                | None -> failwith "Expected the cache-hit copy to return the existing object version."

            secondCopy.Sha256Hash
            |> should equal firstCopy.Sha256Hash

            secondCopy.Blake3Hash
            |> should equal firstCopy.Blake3Hash

            secondCopy.RelativePath
            |> should equal firstCopy.RelativePath)

    /// Verifies that watch json auth failure emits one clean error envelope.
    [<Test>]
    let ``watch json auth failure emits one clean error envelope`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                /// Verifies that the CLI watch scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "watch" |]

                exitCode |> should equal -1
                standardError |> should equal String.Empty

                standardOut
                |> should not' (contain "SignalR Hub connection state")

                standardOut |> should not' (contain "Elapsed:")

                use document = parseJsonOutput standardOut
                let root = document.RootElement

                root.GetProperty("Error").GetString()
                |> should contain "watch is a continuous foreground workflow"

                /// Tracks return Value changes so this scenario can assert the resulting side effect explicitly.
                let mutable returnValue = Unchecked.defaultof<JsonElement>

                root.TryGetProperty("ReturnValue", &returnValue)
                |> should equal false))

    /// Verifies that watch json error in separate process does not delete live watch ipc file.
    [<Test>]
    let ``watch json error in separate process does not delete live watch ipc file`` () =
        withTempRepo (fun root ->
            clearWatchAuthEnv (fun () ->
                let ipcFileName = writeLiveWatchStatusFile ()

                /// Verifies that the CLI watch scenario exits with the expected process status.
                let exitCode, standardOut, standardError = runGraceProcessWithCapturedStdoutAndStderr root [| "--output"; "Json"; "watch" |]

                Assert.That(exitCode, Is.EqualTo(-1).Or.EqualTo(255))
                standardError |> should equal String.Empty

                use document = parseJsonOutput standardOut

                document
                    .RootElement
                    .GetProperty("Error")
                    .GetString()
                |> should contain "watch is a continuous foreground workflow"

                File.Exists(ipcFileName) |> should equal true))
