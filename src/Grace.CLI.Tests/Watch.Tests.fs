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

[<NonParallelizable>]
module WatchTests =
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

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

    let private withEnv (name: string) (value: string option) (action: unit -> unit) =
        let original = Environment.GetEnvironmentVariable(name)

        match value with
        | Some v -> Environment.SetEnvironmentVariable(name, v)
        | None -> Environment.SetEnvironmentVariable(name, null)

        try
            action ()
        finally
            Environment.SetEnvironmentVariable(name, original)

    let private withClearedEnvVars (names: string list) (action: unit -> unit) =
        let rec run remaining =
            match remaining with
            | [] -> action ()
            | head :: tail -> withEnv head None (fun () -> run tail)

        run names

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

    let private parseJsonOutput (output: string) =
        output.StartsWith("{", StringComparison.Ordinal)
        |> should equal true

        JsonDocument.Parse(output)

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

    let private readFileIfExists path = if File.Exists(path) then Some(File.ReadAllText(path)) else None

    let private deleteWatchStatusFileIfExists () =
        let ipcFileName = Services.IpcFileName()

        if File.Exists(ipcFileName) then File.Delete(ipcFileName)

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

    let private deletedEvent (fullPath: string) = FileSystemEventArgs(WatcherChangeTypes.Deleted, Path.GetDirectoryName(fullPath), Path.GetFileName(fullPath))

    let private renamedEvent (oldFullPath: string) (fullPath: string) =
        RenamedEventArgs(WatcherChangeTypes.Renamed, Path.GetDirectoryName(fullPath), Path.GetFileName(fullPath), Path.GetFileName(oldFullPath))

    let private processPendingWatchWorkForTest () =
        let status = GraceStatus.Default
        let mutable updateCalls = 0
        let mutable uploadCalls = 0

        let readStatus () = Task.FromResult(status)

        let upload _ _ =
            uploadCalls <- uploadCalls + 1
            Task.FromResult(())

        let updateGraceStatus status _ =
            updateCalls <- updateCalls + 1
            Task.FromResult(Some status)

        let applyIncremental _ _ _ = Task.FromResult(())
        let updateIpc _ _ = Task.FromResult(())

        let processTask = Watch.processChangedFilesWithClients readStatus readStatus upload updateGraceStatus applyIncremental updateIpc

        processTask.GetAwaiter().GetResult()

        updateCalls, uploadCalls

    let private processPendingWatchWorkWithStatusClients readStatusFile updateGraceStatus =
        let status = GraceStatus.Default
        let readStatusMeta () = Task.FromResult(status)
        let upload _ _ = Task.FromResult(())
        let applyIncremental _ _ _ = Task.FromResult(())
        let updateIpc _ _ = Task.FromResult(())

        Watch.processChangedFilesWithClients readStatusMeta readStatusFile upload updateGraceStatus applyIncremental updateIpc
        |> fun processTask -> processTask.GetAwaiter().GetResult()

    let private writeGraceIgnore root (entries: string array) =
        File.WriteAllText(Path.Combine(root, Constants.GraceIgnoreFileName), String.Join(Environment.NewLine, entries))
        resetConfiguration ()

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

    [<Test>]
    let ``resolveSignalRAccessTokenResult returns token when present`` () =
        let result = Watch.resolveSignalRAccessTokenResult (Ok(Some "token-value"))

        match result with
        | Ok token -> token |> should equal "token-value"
        | Error error -> Assert.Fail($"Expected token result, got error: {error}")

    [<Test>]
    let ``resolveSignalRAccessTokenResult errors when token is missing`` () =
        let result = Watch.resolveSignalRAccessTokenResult (Ok None)

        match result with
        | Ok token -> Assert.Fail($"Expected missing token error, got token: {token}")
        | Error error ->
            error
            |> should contain "No access token is available."

    [<Test>]
    let ``resolveSignalRAccessTokenResult includes underlying auth error`` () =
        let result = Watch.resolveSignalRAccessTokenResult (Error "test error")

        match result with
        | Ok token -> Assert.Fail($"Expected auth error, got token: {token}")
        | Error error ->
            error
            |> should contain "Unable to acquire an access token for SignalR notifications:"

            error |> should contain "test error"

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

            let updateCalls, uploadCalls = processPendingWatchWorkForTest ()

            updateCalls |> should equal 1
            uploadCalls |> should equal 0

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>

            afterProcessing.FilesToProcess
            |> should equal Array.empty<string>)

    [<Test>]
    let ``status-only triggers remain pending when status update returns none`` () =
        withTempRepo (fun root ->
            let filePath = Path.Combine(root, "retry-delete.txt")
            let mutable updateCalls = 0

            Watch.OnDeleted(deletedEvent filePath)

            let updateGraceStatus _ _ =
                updateCalls <- updateCalls + 1
                Task.FromResult(None)

            processPendingWatchWorkWithStatusClients (fun () -> Task.FromResult(GraceStatus.Default)) updateGraceStatus

            updateCalls |> should equal 1

            let afterFailure = Watch.pendingWatchWorkSnapshotForTests ()

            afterFailure.StatusUpdateTriggers
            |> should equal [| "retry-delete.txt" |]

            let successCalls, uploadCalls = processPendingWatchWorkForTest ()

            successCalls |> should equal 1
            uploadCalls |> should equal 0

            let afterSuccess = Watch.pendingWatchWorkSnapshotForTests ()

            afterSuccess.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    [<Test>]
    let ``status-only triggers added during status update remain pending for next pass`` () =
        withTempRepo (fun root ->
            let beforeUpdatePath = Path.Combine(root, "before-update-delete.txt")
            let duringUpdatePath = Path.Combine(root, "during-update-delete.txt")
            let mutable updateCalls = 0

            Watch.OnDeleted(deletedEvent beforeUpdatePath)

            let updateGraceStatus status _ =
                updateCalls <- updateCalls + 1

                if updateCalls = 1 then Watch.OnDeleted(deletedEvent duringUpdatePath)

                Task.FromResult(Some status)

            processPendingWatchWorkWithStatusClients (fun () -> Task.FromResult(GraceStatus.Default)) updateGraceStatus

            updateCalls |> should equal 1

            let afterFirstPass = Watch.pendingWatchWorkSnapshotForTests ()

            afterFirstPass.StatusUpdateTriggers
            |> should equal [| "during-update-delete.txt" |]

            let successCalls, uploadCalls = processPendingWatchWorkForTest ()

            successCalls |> should equal 1
            uploadCalls |> should equal 0

            let afterSecondPass = Watch.pendingWatchWorkSnapshotForTests ()

            afterSecondPass.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    [<Test>]
    let ``status-only triggers remain pending when status file read fails`` () =
        withTempRepo (fun root ->
            let filePath = Path.Combine(root, "read-failure-delete.txt")
            let mutable updateCalls = 0

            Watch.OnDeleted(deletedEvent filePath)

            let readStatusFile () = Task.FromException<GraceStatus>(InvalidOperationException("transient status read failure"))

            let updateGraceStatus status _ =
                updateCalls <- updateCalls + 1
                Task.FromResult(Some status)

            processPendingWatchWorkWithStatusClients readStatusFile updateGraceStatus

            updateCalls |> should equal 0

            let afterFailure = Watch.pendingWatchWorkSnapshotForTests ()

            afterFailure.StatusUpdateTriggers
            |> should equal [| "read-failure-delete.txt" |])

    [<Test>]
    let ``rename-old status-only trigger remains pending when status update fails`` () =
        withTempRepo (fun root ->
            let oldPath = Path.Combine(root, "old-status-only-name.txt")
            let ignoredNewPath = Path.Combine(root, "new-status-only-name.gracetmp")

            Watch.OnRenamed(renamedEvent oldPath ignoredNewPath)

            let updateGraceStatus _ _ = Task.FromException<GraceStatus option>(InvalidOperationException("transient status update failure"))

            processPendingWatchWorkWithStatusClients (fun () -> Task.FromResult(GraceStatus.Default)) updateGraceStatus

            let afterFailure = Watch.pendingWatchWorkSnapshotForTests ()

            afterFailure.StatusUpdateTriggers
            |> should equal [| "old-status-only-name.txt" |]

            afterFailure.FilesToProcess
            |> should equal Array.empty<string>)

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

            let mutable observedDifferences = List<FileSystemDifference>()

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

    [<Test>]
    let ``duplicate deleted file events drain as one status update trigger`` () =
        withTempRepo (fun root ->
            let filePath = Path.Combine(root, "duplicate-delete.txt")

            Watch.OnDeleted(deletedEvent filePath)
            Watch.OnDeleted(deletedEvent filePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "duplicate-delete.txt" |]

            let updateCalls, uploadCalls = processPendingWatchWorkForTest ()

            updateCalls |> should equal 1
            uploadCalls |> should equal 0

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>)

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

            let updateCalls, uploadCalls = processPendingWatchWorkForTest ()

            uploadCalls |> should equal 2
            updateCalls |> should equal 1)

    [<Test>]
    let ``startup deleted file difference queues status-only work without upload work`` () =
        withTempRepo (fun _ ->
            Watch.queueStartupDifferenceForWatch (FileSystemDifference.Create Delete FileSystemEntryType.File "offline-delete.txt")

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "offline-delete.txt" |]

            pending.FilesToProcess
            |> should equal Array.empty<string>

            let updateCalls, uploadCalls = processPendingWatchWorkForTest ()

            updateCalls |> should equal 1
            uploadCalls |> should equal 0)

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

    [<Test>]
    let ``watch exits with auth guidance when no token is configured`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                let exitCode, output = runWithCapturedOutput [| "watch" |]

                if exitCode <> -1 then
                    Assert.Fail(
                        $"Expected watch to exit with -1 when auth is missing. Actual: {exitCode}.{Environment.NewLine}Output:{Environment.NewLine}{output}"
                    )

                output
                |> should contain "Unable to acquire an access token for SignalR"

                output
                |> should contain "Authentication is not configured."))

    [<Test>]
    let ``watch exits nonzero when live watcher status already exists`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                let ipcFileName = writeLiveWatchStatusFile ()
                let originalContents = readFileIfExists ipcFileName
                let exitCode, output = runWithCapturedOutput [| "watch" |]

                exitCode |> should equal -1

                output
                |> should contain "GraceWatch is already running"

                output
                |> should not' (contain "Unable to acquire an access token for SignalR")

                readFileIfExists ipcFileName
                |> should equal originalContents))

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

                let exitCode, output = runWithCapturedOutput [| "watch" |]

                exitCode |> should equal -1

                output
                |> should contain "GraceWatch is already running"

                output
                |> should not' (contain "Unable to acquire an access token for SignalR")))

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

    [<Test>]
    let ``watch check exits zero when live watcher status exists`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

            let exitCode, output =
                runWithCapturedOutput [| "watch"
                                         "--check" |]

            exitCode |> should equal 0

            output |> should contain "GraceWatch is running"

            readFileIfExists ipcFileName
            |> should equal originalContents)

    [<Test>]
    let ``watch check through main exits zero and preserves live watcher status`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "watch"
                                                  "--check" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            standardOut
            |> should contain "GraceWatch is running"

            readFileIfExists ipcFileName
            |> should equal originalContents)

    [<Test>]
    let ``watch check json mode emits error envelope and preserves live watcher status`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

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

            let mutable returnValue = Unchecked.defaultof<JsonElement>

            root.TryGetProperty("ReturnValue", &returnValue)
            |> should equal false

            readFileIfExists ipcFileName
            |> should equal originalContents)

    [<Test>]
    let ``watch check select mode emits error envelope and preserves live watcher status`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

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

            let mutable returnValue = Unchecked.defaultof<JsonElement>

            root.TryGetProperty("ReturnValue", &returnValue)
            |> should equal false

            readFileIfExists ipcFileName
            |> should equal originalContents)

    [<Test>]
    let ``watch check exits nonzero when live watcher status is missing`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
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

    [<Test>]
    let ``watch cached file changes upload cached object for save enrichment`` () =
        let filePath = FilePath @"C:\repo\dir\cached-file.txt"

        let cachedFileVersion =
            FileVersion.Create "dir/cached-file.txt" (Sha256Hash "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") String.Empty true 12L

        let mutable uploadedFileVersions = Array.empty<FileVersion>

        let copyFileToObjectCache _ = Task.FromResult<FileVersion option> None

        let getCachedFileVersion _ = Task.FromResult(Some cachedFileVersion)

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

    [<Test>]
    let ``watch json auth failure emits one clean error envelope`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
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

                let mutable returnValue = Unchecked.defaultof<JsonElement>

                root.TryGetProperty("ReturnValue", &returnValue)
                |> should equal false))

    [<Test>]
    let ``watch json error in separate process does not delete live watch ipc file`` () =
        withTempRepo (fun root ->
            clearWatchAuthEnv (fun () ->
                let ipcFileName = writeLiveWatchStatusFile ()

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
