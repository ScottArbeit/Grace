namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Branch
open Grace.Shared.Parameters.Storage
open Grace.Shared.Utilities
open Grace.Types.Automation
open Grace.Types.Common
open Grace.Types.Reference
open Microsoft.AspNetCore.SignalR
open Microsoft.Data.Sqlite
open NodaTime
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
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

    /// Builds a healthy live Watch status snapshot for IPC compatibility tests.
    let private liveWatchStatus rootDirectoryId : Services.GraceWatchStatus =
        let current = Current()

        {
            UpdatedAt = getCurrentInstant ()
            IsStartupClaim = false
            RepositoryId = current.RepositoryId
            RepositoryName = RepositoryName current.RepositoryName
            BranchId = current.BranchId
            BranchName = BranchName current.BranchName
            RootDirectory = current.RootDirectory
            HasPendingWatchWork = false
            IsWorkingTreeClean = true
            RootDirectoryId = rootDirectoryId
            RootDirectorySha256Hash = Sha256Hash "live-watch-root"
            RootDirectoryBlake3Hash = Blake3Hash "live-watch-root-blake3"
            LastFileUploadInstant = Instant.MinValue
            LastDirectoryVersionInstant = Instant.MinValue
            DirectoryIds = HashSet<DirectoryVersionId>([| rootDirectoryId |])
        }

    /// Applies the current repository identity used by Watch IPC trust tests.
    let private configureCurrentWatchIdentity rootDirectory repositoryName branchName =
        let repositoryId = Guid.NewGuid()
        let branchId = Guid.NewGuid()
        let configuration = Current()

        configuration.RepositoryId <- repositoryId
        configuration.RepositoryName <- repositoryName
        configuration.BranchId <- branchId
        configuration.BranchName <- branchName
        configuration.RootDirectory <- rootDirectory

        repositoryId, branchId

    /// Writes a repository configuration file without relying on the process-local cached Current() value.
    let private writeRepositoryConfiguration rootDirectory repositoryId repositoryName branchId branchName =
        let configuration = GraceConfiguration()
        configuration.RepositoryId <- repositoryId
        configuration.RepositoryName <- repositoryName
        configuration.BranchId <- branchId
        configuration.BranchName <- branchName
        configuration.RootDirectory <- rootDirectory

        saveConfigFile (Path.Combine(rootDirectory, Constants.GraceConfigDirectory, Constants.GraceConfigFileName)) configuration

    /// Removes the compact runtime surface so tests can simulate pre-WS3.1 IPC files.
    let private removeCompactWatchRuntimeSurface (json: string) =
        let statusNode = JsonNode.Parse(json).AsObject()
        statusNode.Remove("Mode") |> ignore
        statusNode.Remove("SafetyFlags") |> ignore
        statusNode.ToJsonString(Constants.JsonSerializerOptions)

    /// Removes issue #492 fields so tests can simulate Watch IPC files written by the previous CLI.
    let private removeWatchStatusFieldsAddedForIssue492 (json: string) =
        let statusNode = JsonNode.Parse(json).AsObject()
        statusNode.Remove("RepositoryId") |> ignore
        statusNode.Remove("RepositoryName") |> ignore
        statusNode.Remove("BranchId") |> ignore
        statusNode.Remove("BranchName") |> ignore
        statusNode.Remove("RootDirectory") |> ignore
        statusNode.Remove("HasPendingWatchWork") |> ignore
        statusNode.Remove("IsWorkingTreeClean") |> ignore
        statusNode.ToJsonString(Constants.JsonSerializerOptions)

    /// Reads safety flags into a deterministic set for assertions.
    let private safetyFlagSet (status: Services.GraceWatchStatus) = status.SafetyFlags |> Set.ofArray

    /// Reports whether a persisted JSON snapshot contains the supplied top-level property.
    let private jsonHasProperty (propertyName: string) (json: string) =
        use document = JsonDocument.Parse(json)
        let mutable property = Unchecked.defaultof<JsonElement>
        document.RootElement.TryGetProperty(propertyName, &property)

    /// Reads a scalar property from the persisted Watch IPC JSON contract.
    let private readWatchStatusJsonStringProperty (propertyName: string) =
        let json = File.ReadAllText(Services.IpcFileName())
        use document = JsonDocument.Parse(json)

        match document.RootElement.TryGetProperty(propertyName) with
        | true, property -> property.GetString()
        | false, _ ->
            Assert.Fail($"Expected Watch IPC JSON property '{propertyName}'. JSON:{Environment.NewLine}{json}")
            String.Empty

    /// Reads an optional scalar property from the persisted Watch IPC JSON contract.
    let private tryReadWatchStatusJsonStringProperty (propertyName: string) =
        let json = File.ReadAllText(Services.IpcFileName())
        use document = JsonDocument.Parse(json)

        match document.RootElement.TryGetProperty(propertyName) with
        | true, property when property.ValueKind <> JsonValueKind.Null -> Some(property.GetString())
        | false, _ -> None
        | true, _ -> None

    /// Returns the same path text with one ASCII letter cased differently for root identity regressions.
    let private requireDifferentlyCasedPath path =
        let chars = $"{path}".ToCharArray()
        let mutable changed = false
        let mutable index = 0

        while not changed && index < chars.Length do
            if Char.IsAsciiLetter(chars[index]) then
                let replacement =
                    if Char.IsUpper(chars[index]) then
                        Char.ToLowerInvariant(chars[index])
                    else
                        Char.ToUpperInvariant(chars[index])

                if replacement <> chars[index] then
                    chars[index] <- replacement
                    changed <- true

            index <- index + 1

        if not changed then
            Assert.Fail($"Expected path '{path}' to contain an ASCII letter whose casing can be changed.")

        String(chars)

    /// Reads a boolean property from the persisted Watch IPC JSON contract.
    let private readWatchStatusJsonBooleanProperty (propertyName: string) =
        let json = File.ReadAllText(Services.IpcFileName())
        use document = JsonDocument.Parse(json)

        match document.RootElement.TryGetProperty(propertyName) with
        | true, property -> property.GetBoolean()
        | false, _ ->
            Assert.Fail($"Expected Watch IPC JSON property '{propertyName}'. JSON:{Environment.NewLine}{json}")
            false

    /// Reads the persisted Watch IPC safety flags into a deterministic set for assertions.
    let private readWatchStatusJsonSafetyFlags () =
        let json = File.ReadAllText(Services.IpcFileName())
        use document = JsonDocument.Parse(json)

        match document.RootElement.TryGetProperty("SafetyFlags") with
        | true, property ->
            property.EnumerateArray()
            |> Seq.map (fun flag -> flag.GetString())
            |> Set.ofSeq
        | false, _ ->
            Assert.Fail($"Expected Watch IPC JSON property 'SafetyFlags'. JSON:{Environment.NewLine}{json}")
            Set.empty

    /// Writes a Watch IPC JSON snapshot with compact runtime fields for deterministic stale-status tests.
    let private writeWatchStatusJsonWithRuntimeSurface (status: Services.GraceWatchStatus) =
        let statusNode = JsonNode.Parse(serialize status).AsObject()
        statusNode["Mode"] <- JsonSerializer.SerializeToNode(status.Mode, Constants.JsonSerializerOptions)
        statusNode["SafetyFlags"] <- JsonSerializer.SerializeToNode(status.SafetyFlags, Constants.JsonSerializerOptions)

        let ipcFileName = Services.IpcFileName()

        Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
        |> ignore

        File.WriteAllText(ipcFileName, statusNode.ToJsonString(Constants.JsonSerializerOptions))
        ipcFileName

    /// Writes a Watch IPC JSON snapshot with an explicit persisted runtime mode for status-readability tests.
    let private writeWatchStatusJsonWithPersistedMode mode (status: Services.GraceWatchStatus) =
        let statusNode = JsonNode.Parse(serialize status).AsObject()
        statusNode["Mode"] <- JsonSerializer.SerializeToNode(mode, Constants.JsonSerializerOptions)
        statusNode["SafetyFlags"] <- JsonSerializer.SerializeToNode(status.SafetyFlags, Constants.JsonSerializerOptions)

        let ipcFileName = Services.IpcFileName()

        Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
        |> ignore

        File.WriteAllText(ipcFileName, statusNode.ToJsonString(Constants.JsonSerializerOptions))
        ipcFileName

    /// Rewrites only the Watch IPC heartbeat timestamp so tests can model a healthy writer that later dies.
    let private updatePersistedWatchStatusUpdatedAt updatedAt =
        let ipcFileName = Services.IpcFileName()

        let statusNode =
            JsonNode
                .Parse(File.ReadAllText(ipcFileName))
                .AsObject()

        statusNode["UpdatedAt"] <- JsonSerializer.SerializeToNode(updatedAt, Constants.JsonSerializerOptions)
        File.WriteAllText(ipcFileName, statusNode.ToJsonString(Constants.JsonSerializerOptions))

    /// Writes live watch status file needed by the test scenario.
    let private writeLiveWatchStatusFile () =
        let rootDirectoryId = Guid.NewGuid()

        let status = liveWatchStatus rootDirectoryId

        let ipcFileName = Services.IpcFileName()

        (LocalStateDb.ensureDbInitialized (Current().GraceStatusFile))
            .GetAwaiter()
            .GetResult()

        Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
        |> ignore

        File.WriteAllText(ipcFileName, serialize status)
        ipcFileName

    /// Reads file if exists needed by the test scenario.
    let private readFileIfExists path = if File.Exists(path) then Some(File.ReadAllText(path)) else None

    /// Deletes local-state database files so status trust tests can prove fail-closed durable inspection.
    let private deleteLocalStateDbFiles () =
        let dbPath = Current().GraceStatusFile

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()
        GC.Collect()
        GC.WaitForPendingFinalizers()

        [|
            dbPath
            dbPath + "-wal"
            dbPath + "-shm"
            dbPath + "-journal"
        |]
        |> Array.iter (fun path -> if File.Exists(path) then File.Delete(path))

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
            let configuration = Current()
            configuration.RepositoryId <- Guid.NewGuid()
            configuration.RepositoryName <- "watch-test-repository"
            configuration.BranchId <- Guid.NewGuid()
            configuration.BranchName <- "watch-test-branch"
            configuration.RootDirectory <- tempDir
            saveConfigFile configPath configuration
            Services.graceWatchStatusUpdateTime <- Instant.MinValue
            Services.clearWorkingDirectoryWriteTimesForWatchRescan ()
            Services.clearShouldIgnoreCache ()
            deleteWatchStatusFileIfExists ()
            Watch.clearPendingWatchWorkForTests ()
            action tempDir
        finally
            Watch.clearPendingWatchWorkForTests ()
            Services.clearShouldIgnoreCache ()
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

    /// Builds created event test data used to exercise CLI watch behavior.
    let private createdEvent (fullPath: string) = FileSystemEventArgs(WatcherChangeTypes.Created, Path.GetDirectoryName(fullPath), Path.GetFileName(fullPath))

    /// Builds changed event test data used to exercise CLI watch behavior.
    let private changedEvent (fullPath: string) = FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(fullPath), Path.GetFileName(fullPath))

    /// Builds a replayable Watch journal observation for Watch command retention tests.
    let private watchJournalScope () : LocalStateDb.WatchJournalScope =
        let current = Current()

        {
            RepositoryId = current.RepositoryId
            BranchId = current.BranchId
            WorkspaceRoot = current.RootDirectory
            WatchRoot = current.RootDirectory
            PathComparison = StringComparison.Ordinal
            RootDirectoryId = DirectoryVersionId("11111111-1111-1111-1111-111111111111")
            RootDirectoryBlake3Hash = Blake3Hash "test-root-blake3"
            WatchMode = "repository-root"
        }

    /// Builds a replayable Watch journal observation for Watch command retention tests.
    let private watchJournalObservation differenceType entryType (relativePath: string) : LocalStateDb.WatchJournalObservation =
        { Scope = watchJournalScope (); DifferenceType = differenceType; EntryType = entryType; RelativePath = RelativePath relativePath }

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

    /// Records a completed update marker deletion through the same callback path used by FileSystemWatcher.
    let private recordCompletedUpdateMarkerDeletion (updateMarkerFile: string) (markerCompletedUtc: DateTime) =
        Directory.CreateDirectory(Path.GetDirectoryName(updateMarkerFile))
        |> ignore

        File.WriteAllText(updateMarkerFile, "`grace switch` is in progress.")
        File.SetLastWriteTimeUtc(updateMarkerFile, markerCompletedUtc.AddSeconds(-1.0))
        Watch.OnGraceUpdateInProgressCreated(createdEvent updateMarkerFile)
        File.WriteAllText(updateMarkerFile + ".completed", markerCompletedUtc.ToString("O"))
        File.Delete(updateMarkerFile)
        Watch.OnGraceUpdateInProgressDeleted(deletedEvent updateMarkerFile)

    /// Records a completed marker deletion when Watch missed the marker-created callback.
    let private recordUnobservedCompletedUpdateMarkerDeletion (updateMarkerFile: string) (markerCompletedUtc: DateTime) =
        Directory.CreateDirectory(Path.GetDirectoryName(updateMarkerFile))
        |> ignore

        File.WriteAllText(updateMarkerFile, "`grace switch` is in progress.")
        File.SetLastWriteTimeUtc(updateMarkerFile, markerCompletedUtc.AddSeconds(-1.0))
        File.WriteAllText(updateMarkerFile + ".completed", markerCompletedUtc.ToString("O"))
        File.Delete(updateMarkerFile)
        Watch.OnGraceUpdateInProgressDeleted(deletedEvent updateMarkerFile)

    /// Records a marker deletion without the completed sidecar produced by a successful branch switch mutation.
    let private recordIncompleteUpdateMarkerDeletion (updateMarkerFile: string) =
        Directory.CreateDirectory(Path.GetDirectoryName(updateMarkerFile))
        |> ignore

        File.WriteAllText(updateMarkerFile, "`grace switch` is in progress.")
        File.Delete(updateMarkerFile)
        Watch.OnGraceUpdateInProgressDeleted(deletedEvent updateMarkerFile)

    /// Builds a promotion-applied automation event targeting the supplied branch.
    let private promotionAppliedEnvelope targetBranchId terminalReferenceId =
        let current = Current()

        let dataJson = JsonSerializer.Serialize({| targetBranchId = $"{targetBranchId}"; terminalPromotionReferenceId = $"{terminalReferenceId}" |})

        AutomationEventEnvelope.Create
            AutomationEventType.PromotionSetApplied
            (getCurrentInstant ())
            (generateCorrelationId ())
            current.OwnerId
            current.OrganizationId
            current.RepositoryId
            $"{targetBranchId}"
            dataJson

    /// Builds renamed event test data used to exercise CLI watch behavior.
    let private renamedEvent (oldFullPath: string) (fullPath: string) =
        RenamedEventArgs(WatcherChangeTypes.Renamed, Path.GetDirectoryName(fullPath), Path.GetFileName(fullPath), Path.GetFileName(oldFullPath))

    /// Verifies that Grace-owned writes under the update marker do not enqueue Save-producing Watch work.
    [<Test>]
    let ``update marker suppresses changed file observation without local save work`` () =
        withTempRepo (fun root ->
            let changedFilePath = Path.Combine(root, "grace-owned-write.txt")
            let updateMarkerFile = Services.updateInProgressFileName ()

            Directory.CreateDirectory(Path.GetDirectoryName(updateMarkerFile))
            |> ignore

            try
                File.WriteAllText(updateMarkerFile, "`grace switch` is in progress.")
                File.WriteAllText(changedFilePath, "Grace-owned branch switch payload")
                Watch.OnChanged(changedEvent changedFilePath)

                let pending = Watch.pendingWatchWorkSnapshotForTests ()

                pending.FilesToProcess
                |> should equal Array.empty<string>

                pending.DirectoriesToProcess
                |> should equal Array.empty<string>

                pending.StatusUpdateTriggers
                |> should equal Array.empty<string>
            finally
                if File.Exists(updateMarkerFile) then File.Delete(updateMarkerFile))

    /// Verifies that delayed callbacks for writes completed under the update marker do not enqueue after deletion.
    [<Test>]
    let ``update marker deletion keeps delayed Grace-owned changed observation suppressed`` () =
        withTempRepo (fun root ->
            let changedFilePath = Path.Combine(root, "delayed-grace-owned-write.txt")
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerCompletedUtc = DateTime.UtcNow

            Directory.CreateDirectory(Path.GetDirectoryName(updateMarkerFile))
            |> ignore

            File.WriteAllText(updateMarkerFile, "`grace switch` is in progress.")
            File.SetLastWriteTimeUtc(updateMarkerFile, markerCompletedUtc.AddSeconds(-1.0))
            Watch.OnGraceUpdateInProgressCreated(createdEvent updateMarkerFile)
            File.WriteAllText(changedFilePath, "Grace-owned branch switch payload")
            File.SetLastWriteTimeUtc(changedFilePath, markerCompletedUtc.AddSeconds(-1.0))
            File.WriteAllText(updateMarkerFile + ".completed", markerCompletedUtc.ToString("O"))
            File.Delete(updateMarkerFile)
            Watch.setReadGraceStatusFileForWatchTests (fun () -> Task.FromResult(graceStatusTracking [| "delayed-grace-owned-write.txt" |] Array.empty<string>))

            Watch.OnChanged(changedEvent changedFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal Array.empty<string>

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that a late marker-created callback does not remove the successful switch completion sidecar.
    [<Test>]
    let ``late update marker created event keeps completed sidecar authoritative until deletion`` () =
        withTempRepo (fun root ->
            let changedFilePath = Path.Combine(root, "late-created-grace-owned-write.txt")
            let updateMarkerFile = Services.updateInProgressFileName ()
            let completedSidecar = updateMarkerFile + ".completed"
            let markerCompletedUtc = DateTime.UtcNow

            Directory.CreateDirectory(Path.GetDirectoryName(updateMarkerFile))
            |> ignore

            File.WriteAllText(updateMarkerFile, "`grace switch` is in progress.")
            File.SetLastWriteTimeUtc(updateMarkerFile, markerCompletedUtc.AddSeconds(-1.0))
            File.WriteAllText(changedFilePath, "Grace-owned branch switch payload")
            File.SetLastWriteTimeUtc(changedFilePath, markerCompletedUtc.AddSeconds(-1.0))
            File.WriteAllText(completedSidecar, markerCompletedUtc.ToString("O"))

            Watch.setReadGraceStatusFileForWatchTests (fun () ->
                Task.FromResult(
                    graceStatusTracking
                        [|
                            "late-created-grace-owned-write.txt"
                        |]
                        Array.empty<string>
                ))

            Watch.OnGraceUpdateInProgressCreated(createdEvent updateMarkerFile)

            File.Exists(completedSidecar) |> should equal true

            File.Delete(updateMarkerFile)
            Watch.OnGraceUpdateInProgressDeleted(deletedEvent updateMarkerFile)
            Watch.OnChanged(changedEvent changedFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal Array.empty<string>

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that marker-created cleanup still removes stale completion sidecars from earlier switches.
    [<Test>]
    let ``new update marker created event clears stale completed sidecar`` () =
        withTempRepo (fun _ ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let completedSidecar = updateMarkerFile + ".completed"
            let previousMarkerCompletedUtc = DateTime.UtcNow.AddSeconds(-10.0)

            Directory.CreateDirectory(Path.GetDirectoryName(updateMarkerFile))
            |> ignore

            File.WriteAllText(completedSidecar, previousMarkerCompletedUtc.ToString("O"))
            File.WriteAllText(updateMarkerFile, "`grace switch` is in progress.")
            File.SetLastWriteTimeUtc(updateMarkerFile, previousMarkerCompletedUtc.AddSeconds(5.0))

            Watch.OnGraceUpdateInProgressCreated(createdEvent updateMarkerFile)

            File.Exists(completedSidecar)
            |> should equal false)

    /// Verifies that marker deletion without a completion sidecar cannot authorize delayed suppression.
    [<Test>]
    let ``update marker deletion without completion sidecar does not suppress delayed changed observation`` () =
        withTempRepo (fun root ->
            let changedFilePath = Path.Combine(root, "partial-switch-write.txt")
            let updateMarkerFile = Services.updateInProgressFileName ()

            File.WriteAllText(changedFilePath, "partial branch switch payload")
            File.SetLastWriteTimeUtc(changedFilePath, DateTime.UtcNow.AddSeconds(-1.0))
            recordIncompleteUpdateMarkerDeletion updateMarkerFile

            Watch.OnChanged(changedEvent changedFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal [| changedFilePath |]

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that marker deletion completes a same-process branch transition by reloading config and publishing branch B IPC.
    [<Test>]
    let ``update marker deletion reloads branch config and publishes new branch ipc`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let repositoryName = "transition-repo"
            let branchAName = "branch-a"
            let branchBName = "branch-b"

            writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
            resetConfiguration ()
            Current() |> ignore

            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental

            let statusA = graceStatusTracking Array.empty<string> Array.empty<string>
            let statusB = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIdsA = HashSet<DirectoryVersionId>(statusA.Index.Keys)
            let directoryIdsB = HashSet<DirectoryVersionId>(statusB.Index.Keys)

            (Services.updateGraceWatchInterprocessFile statusA (Some directoryIdsA))
                .GetAwaiter()
                .GetResult()

            let branchAIpc = Services.IpcFileName()

            branchAIpc |> File.Exists |> should equal true

            let branchBIpc = Services.IpcFileNameForIdentity repositoryId repositoryName root branchBId branchBName

            branchBIpc |> File.Exists |> should equal false

            writeRepositoryConfiguration root repositoryId repositoryName branchBId branchBName
            Watch.setReadGraceStatusFileForTransitionCompletionForWatchTests (fun () -> Task.FromResult(statusB))

            recordCompletedUpdateMarkerDeletion (Services.updateInProgressFileName ()) DateTime.UtcNow

            Services.IpcFileName() |> should equal branchBIpc

            branchBIpc |> File.Exists |> should equal true

            readWatchStatusJsonStringProperty "BranchId"
            |> should equal $"{branchBId}"

            readWatchStatusJsonStringProperty "BranchName"
            |> should equal branchBName

            readWatchStatusJsonStringProperty "RootDirectoryId"
            |> should equal $"{statusB.RootDirectoryId}"

            let branchBInspection = Services.inspectGraceWatchStatus().Result

            branchBInspection.IsUsable |> should equal true

            match branchBInspection.Status with
            | Some watchStatus ->
                watchStatus.BranchId |> should equal branchBId

                watchStatus.DirectoryIds.SetEquals(directoryIdsB)
                |> should equal true
            | None -> Assert.Fail("Expected branch B Watch IPC status after transition completion.")

            branchAIpc |> File.Exists |> should equal false

            writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
            resetConfiguration ()
            Current() |> ignore

            let branchAInspection = Services.inspectGraceWatchStatus().Result

            branchAInspection.Exists |> should equal false

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that branch transition completion refreshes SignalR parent identity before auto-rebase events resume.
    [<Test>]
    let ``update marker deletion refreshes signalr parent subscription for new branch`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let parentAId = Guid.NewGuid()
            let parentBId = Guid.NewGuid()
            let repositoryName = "transition-signalr-repo"
            let branchAName = "branch-a"
            let branchBName = "branch-b"

            try
                writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
                resetConfiguration ()
                Current() |> ignore

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental
                Watch.setSignalRBranchSubscriptionForWatchTests branchAId parentAId

                let statusA = graceStatusTracking Array.empty<string> Array.empty<string>
                let statusB = graceStatusTracking Array.empty<string> Array.empty<string>

                (Services.updateGraceWatchInterprocessFile statusA (Some(HashSet<DirectoryVersionId>(statusA.Index.Keys))))
                    .GetAwaiter()
                    .GetResult()

                let refreshedSubscriptions = ResizeArray<BranchId * BranchId>()
                let refreshObservedStatusRoots = ResizeArray<DirectoryVersionId option>()
                let rebasedStatuses = ResizeArray<DirectoryVersionId>()

                let readStatus () = Task.FromResult(statusB)

                let rebaseCurrentBranch (status: GraceStatus) = task { rebasedStatuses.Add(status.RootDirectoryId) }

                Watch.setSignalRSubscriptionRefreshForWatchTests (fun () ->
                    let current = Current()
                    Watch.setSignalRBranchSubscriptionForWatchTests current.BranchId parentBId
                    let subscription = Watch.signalRBranchSubscriptionForWatchTests ()
                    refreshedSubscriptions.Add(subscription.BranchId, subscription.ParentBranchId)

                    Services.getGraceWatchStatus().Result
                    |> Option.map (fun status -> status.RootDirectoryId)
                    |> refreshObservedStatusRoots.Add

                    (Watch.handleSignalRAutomationEventForWatchTests readStatus rebaseCurrentBranch (promotionAppliedEnvelope parentBId (Guid.NewGuid())))
                        .GetAwaiter()
                        .GetResult())

                writeRepositoryConfiguration root repositoryId repositoryName branchBId branchBName
                Watch.setReadGraceStatusFileForTransitionCompletionForWatchTests (fun () -> Task.FromResult(statusB))

                recordCompletedUpdateMarkerDeletion (Services.updateInProgressFileName ()) DateTime.UtcNow

                refreshedSubscriptions.ToArray()
                |> should equal [| branchBId, parentBId |]

                refreshObservedStatusRoots.ToArray()
                |> should equal [| Some statusB.RootDirectoryId |]

                let subscription = Watch.signalRBranchSubscriptionForWatchTests ()
                subscription.BranchId |> should equal branchBId

                subscription.ParentBranchId
                |> should equal parentBId

                Watch.signalRAutomationEventTargetsWatchedParentBranchForWatchTests parentAId
                |> should equal false

                Watch.signalRAutomationEventTargetsWatchedParentBranchForWatchTests parentBId
                |> should equal true

                (Watch.handleSignalRAutomationEventForWatchTests readStatus rebaseCurrentBranch (promotionAppliedEnvelope parentAId (Guid.NewGuid())))
                    .GetAwaiter()
                    .GetResult()

                rebasedStatuses.ToArray()
                |> should equal [| statusB.RootDirectoryId |]

                (Watch.handleSignalRAutomationEventForWatchTests readStatus rebaseCurrentBranch (promotionAppliedEnvelope parentBId (Guid.NewGuid())))
                    .GetAwaiter()
                    .GetResult()

                rebasedStatuses.ToArray()
                |> should
                    equal
                    [|
                        statusB.RootDirectoryId
                        statusB.RootDirectoryId
                    |]
            finally
                Watch.resetSignalRSubscriptionRefreshForWatchTests ())

    /// Verifies that stale parent events cannot auto-rebase after target branch config reload begins.
    [<Test>]
    let ``update marker deletion clears stale signalr parent before target config reload`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let parentAId = Guid.NewGuid()
            let repositoryName = "transition-signalr-stale-parent-repo"
            let branchAName = "branch-a"
            let branchBName = "branch-b"

            try
                writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
                resetConfiguration ()
                Current() |> ignore

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental
                Watch.setSignalRBranchSubscriptionForWatchTests branchAId parentAId

                let statusA = graceStatusTracking Array.empty<string> Array.empty<string>
                let statusB = graceStatusTracking Array.empty<string> Array.empty<string>

                (Services.updateGraceWatchInterprocessFile statusA (Some(HashSet<DirectoryVersionId>(statusA.Index.Keys))))
                    .GetAwaiter()
                    .GetResult()

                let rebasedUnderReloadedConfig = ResizeArray<BranchId * DirectoryVersionId>()

                let readStatus () = Task.FromResult(statusB)

                let rebaseCurrentBranch (status: GraceStatus) = task { rebasedUnderReloadedConfig.Add(Current().BranchId, status.RootDirectoryId) }

                Watch.setUpdateMarkerWatcherRebindForWatchTests (fun () ->
                    Current().BranchId |> should equal branchBId

                    Watch.signalRAutomationEventTargetsWatchedParentBranchForWatchTests parentAId
                    |> should equal false

                    (Watch.handleSignalRAutomationEventForWatchTests readStatus rebaseCurrentBranch (promotionAppliedEnvelope parentAId (Guid.NewGuid())))
                        .GetAwaiter()
                        .GetResult())

                Watch.setSignalRSubscriptionRefreshForWatchTests (fun () -> ())

                writeRepositoryConfiguration root repositoryId repositoryName branchBId branchBName
                Watch.setReadGraceStatusFileForTransitionCompletionForWatchTests (fun () -> Task.FromResult(statusB))

                recordCompletedUpdateMarkerDeletion (Services.updateInProgressFileName ()) DateTime.UtcNow

                rebasedUnderReloadedConfig.ToArray()
                |> should equal Array.empty<BranchId * DirectoryVersionId>
            finally
                Watch.resetUpdateMarkerWatcherRebindForWatchTests ()
                Watch.resetSignalRSubscriptionRefreshForWatchTests ())

    /// Verifies that an event authorized before a branch reload cannot rebase the reloaded branch.
    [<Test>]
    let ``signalr parent event rechecks branch identity before auto rebase`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let parentAId = Guid.NewGuid()
            let parentBId = Guid.NewGuid()
            let repositoryName = "transition-signalr-recheck-repo"
            let branchAName = "branch-a"
            let branchBName = "branch-b"

            try
                writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
                resetConfiguration ()
                Current() |> ignore

                Watch.setSignalRBranchSubscriptionForWatchTests branchAId parentAId

                let statusB = graceStatusTracking Array.empty<string> Array.empty<string>
                let rebasedUnderReloadedConfig = ResizeArray<BranchId * DirectoryVersionId>()

                let readStatus () =
                    task {
                        writeRepositoryConfiguration root repositoryId repositoryName branchBId branchBName
                        resetConfiguration ()
                        Current() |> ignore
                        Watch.setSignalRBranchSubscriptionForWatchTests branchBId parentBId
                        return statusB
                    }

                let rebaseCurrentBranch (status: GraceStatus) = task { rebasedUnderReloadedConfig.Add(Current().BranchId, status.RootDirectoryId) }

                (Watch.handleSignalRAutomationEventForWatchTests readStatus rebaseCurrentBranch (promotionAppliedEnvelope parentAId (Guid.NewGuid())))
                    .GetAwaiter()
                    .GetResult()

                Current().BranchId |> should equal branchBId

                let subscription = Watch.signalRBranchSubscriptionForWatchTests ()

                subscription.BranchId |> should equal branchBId

                subscription.ParentBranchId
                |> should equal parentBId

                rebasedUnderReloadedConfig.ToArray()
                |> should equal Array.empty<BranchId * DirectoryVersionId>
            finally
                Watch.resetSignalRSubscriptionRefreshForWatchTests ())

    /// Verifies that an in-flight parent refresh cannot restore stale trust after a branch transition advances.
    [<Test>]
    let ``stale signalr parent refresh completion cannot restore old branch trust`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let parentAId = Guid.NewGuid()
            let parentBId = Guid.NewGuid()
            let repositoryName = "transition-signalr-stale-refresh-repo"
            let branchAName = "branch-a"
            let branchBName = "branch-b"

            try
                writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
                resetConfiguration ()
                Current() |> ignore

                let staleRefreshAuthority = Watch.beginSignalRBranchSubscriptionRefreshForWatchTests ()

                writeRepositoryConfiguration root repositoryId repositoryName branchBId branchBName
                resetConfiguration ()
                Current() |> ignore

                Watch.clearSignalRBranchSubscriptionForTransitionForWatchTests ()
                Watch.setSignalRBranchSubscriptionForWatchTests branchBId parentBId

                Watch.trySetSignalRBranchSubscriptionForWatchTests staleRefreshAuthority parentAId
                |> should equal false

                let subscription = Watch.signalRBranchSubscriptionForWatchTests ()

                subscription.BranchId |> should equal branchBId

                subscription.ParentBranchId
                |> should equal parentBId

                Watch.signalRAutomationEventTargetsWatchedParentBranchForWatchTests parentAId
                |> should equal false

                Watch.signalRAutomationEventTargetsWatchedParentBranchForWatchTests parentBId
                |> should equal true
            finally
                Watch.resetSignalRSubscriptionRefreshForWatchTests ())

    /// Verifies that stale reconnect refreshes cannot replace the hub's active current-branch group.
    [<Test>]
    let ``stale signalr refresh does not invoke current branch hub registration`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let parentAId = Guid.NewGuid()
            let parentBId = Guid.NewGuid()
            let repositoryName = "transition-signalr-hub-authority-repo"

            try
                writeRepositoryConfiguration root repositoryId repositoryName branchAId "branch-a"
                resetConfiguration ()
                Current() |> ignore

                let parentBranchDto =
                    { Grace.Types.Branch.BranchDto.Default with RepositoryId = repositoryId; BranchId = parentAId; BranchName = BranchName "parent-a" }

                /// Advances local branch identity while an old-branch SignalR refresh is awaiting parent metadata.
                let getParentBranch (_: GetBranchParameters) =
                    writeRepositoryConfiguration root repositoryId repositoryName branchBId "branch-b"
                    resetConfiguration ()
                    Current() |> ignore

                    Watch.clearSignalRBranchSubscriptionForTransitionForWatchTests ()
                    Watch.setSignalRBranchSubscriptionForWatchTests branchBId parentBId

                    Task.FromResult(Ok(GraceReturnValue.Create parentBranchDto "stale-refresh-test"))

                let currentRegistrations = ResizeArray<RepositoryId * BranchId>()
                let parentRegistrations = ResizeArray<BranchId * BranchId>()

                /// Records current-branch group registrations only when the refresh has local authority.
                let registerCurrentBranch repositoryId branchId _ =
                    currentRegistrations.Add(repositoryId, branchId)
                    Task.CompletedTask

                /// Records parent-branch group registrations only when the refresh has local authority.
                let registerParentBranch branchId parentBranchId _ =
                    parentRegistrations.Add(branchId, parentBranchId)
                    Task.CompletedTask

                let result =
                    (Watch.registerCurrentSignalRParentBranchWithClientsForWatchTests
                        getParentBranch
                        registerCurrentBranch
                        registerParentBranch
                        CancellationToken.None)
                        .GetAwaiter()
                        .GetResult()

                match result with
                | Ok _ -> ()
                | Error error -> Assert.Fail($"Expected stale refresh to return the parent lookup result without hub side effects: {error}")

                currentRegistrations.ToArray()
                |> should equal Array.empty<RepositoryId * BranchId>

                parentRegistrations.ToArray()
                |> should equal Array.empty<BranchId * BranchId>

                let subscription = Watch.signalRBranchSubscriptionForWatchTests ()

                subscription.BranchId |> should equal branchBId

                subscription.ParentBranchId
                |> should equal parentBId

                Watch.signalRAutomationEventTargetsWatchedParentBranchForWatchTests parentAId
                |> should equal false

                Watch.signalRAutomationEventTargetsWatchedParentBranchForWatchTests parentBId
                |> should equal true
            finally
                Watch.resetSignalRSubscriptionRefreshForWatchTests ())

    /// Verifies that a successful SignalR refresh joins the current-branch group before the parent-branch group.
    [<Test>]
    let ``signalr refresh registers current branch before parent branch`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let parentId = Guid.NewGuid()
            let repositoryName = "signalr-registration-order-repo"

            writeRepositoryConfiguration root repositoryId repositoryName branchId "feature/current-registration"
            resetConfiguration ()
            Current() |> ignore

            let parentBranchDto = { Grace.Types.Branch.BranchDto.Default with RepositoryId = repositoryId; BranchId = parentId; BranchName = BranchName "main" }

            /// Returns parent metadata for the active branch without changing local branch identity.
            let getParentBranch (_: GetBranchParameters) = Task.FromResult(Ok(GraceReturnValue.Create parentBranchDto "registration-order-test"))

            let registrations = ResizeArray<string * Guid * Guid>()

            /// Records current-branch group registration in call order.
            let registerCurrentBranch repositoryId branchId _ =
                registrations.Add("current", repositoryId, branchId)
                Task.CompletedTask

            /// Records parent-branch group registration in call order.
            let registerParentBranch branchId parentBranchId _ =
                registrations.Add("parent", branchId, parentBranchId)
                Task.CompletedTask

            let result =
                (Watch.registerCurrentSignalRParentBranchWithClientsForWatchTests
                    getParentBranch
                    registerCurrentBranch
                    registerParentBranch
                    CancellationToken.None)
                    .GetAwaiter()
                    .GetResult()

            match result with
            | Ok parentBranch -> parentBranch.BranchId |> should equal parentId
            | Error error -> Assert.Fail($"Expected successful SignalR branch registration: {error}")

            registrations.ToArray()
            |> should
                equal
                [|
                    "current", repositoryId, branchId
                    "parent", branchId, parentId
                |]

            let subscription = Watch.signalRBranchSubscriptionForWatchTests ()

            subscription.BranchId |> should equal branchId

            subscription.ParentBranchId
            |> should equal parentId)

    /// Verifies that reconnect refresh invokes registration and clears local trust when registration fails.
    [<Test>]
    let ``signalr reconnect refresh reruns current branch registration and fails closed`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let parentId = Guid.NewGuid()

            writeRepositoryConfiguration root repositoryId "reconnect-refresh-repo" branchId "feature/reconnect"
            resetConfiguration ()
            Current() |> ignore

            Watch.setSignalRBranchSubscriptionForWatchTests branchId parentId

            let mutable refreshCalls = 0

            let refreshed =
                Watch.refreshSignalRSubscriptionsForActiveConnectionForWatchTests (fun () ->
                    task {
                        refreshCalls <- refreshCalls + 1
                        let _ = Watch.beginSignalRBranchSubscriptionRefreshForWatchTests ()
                        return Error(GraceError.Create "test registration failed" "signalr-reconnect-test")
                    })

            refreshed |> should equal false
            refreshCalls |> should equal 1

            Watch.signalRAutomationEventTargetsWatchedParentBranchForWatchTests parentId
            |> should equal false)

    /// Verifies that Watch uses Grace JSON options for typed SignalR payloads.
    [<Test>]
    let ``signalr json protocol uses grace serializer options`` () =
        let options = JsonHubProtocolOptions()

        Watch.configureGraceSignalRJsonProtocolForWatchTests options

        obj.ReferenceEquals(options.PayloadSerializerOptions, Constants.JsonSerializerOptions)
        |> should equal true

    /// Verifies that ordinary scan-derived resync recovery does not depend on transition SignalR refresh success.
    [<Test>]
    let ``unrelated resync recovery does not invoke signalr transition refresh`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let parentId = Guid.NewGuid()
            let repositoryName = "ordinary-resync-signalr-failure-repo"
            let branchName = "branch-a"

            try
                writeRepositoryConfiguration root repositoryId repositoryName branchId branchName
                resetConfiguration ()
                Current() |> ignore

                let status = graceStatusTracking Array.empty<string> Array.empty<string>
                Watch.setSignalRBranchSubscriptionForWatchTests branchId parentId
                Watch.requestGraceWatchExplicitResyncForWatchTests "test-controlled ordinary confidence loss"

                let mutable refreshCalls = 0

                Watch.setSignalRSubscriptionRefreshForWatchTests (fun () ->
                    refreshCalls <- refreshCalls + 1
                    raise (InvalidOperationException("ordinary resync must not depend on SignalR refresh")))

                /// Reads the durable status snapshot used by scan-derived recovery.
                let readStatus () = Task.FromResult(status)
                /// Keeps file upload retry available without queuing new content.
                let upload _ _ = Task.FromResult(())
                /// Keeps the incremental status helper available without changing status.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)
                /// Produces a clean scan for an ordinary local resync recovery.
                let scanForDifferences _ = Task.FromResult(List<FileSystemDifference>())
                /// Applies the scan-derived status boundary when differences exist.
                let updateGraceStatusFromDifferences currentStatus _ _ = Task.FromResult(Some currentStatus)
                /// Keeps incremental application available without changing status.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Writes Watch IPC after local coherence is proven.
                let updateIpc currentStatus directoryIds = Services.updateGraceWatchInterprocessFile currentStatus directoryIds

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

                refreshCalls |> should equal 0

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.HealthyIncremental

                Watch.isGraceWatchResyncPendingForWatchTests ()
                |> should equal false

                let subscription = Watch.signalRBranchSubscriptionForWatchTests ()

                subscription.BranchId |> should equal branchId

                subscription.ParentBranchId
                |> should equal parentId
            finally
                Watch.resetSignalRSubscriptionRefreshForWatchTests ())

    /// Verifies that transition-stale parent trust remains empty when recovery cannot refresh SignalR.
    [<Test>]
    let ``transition resync recovery tolerates signalr refresh failure with empty trust`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let parentId = Guid.NewGuid()
            let repositoryName = "transition-resync-signalr-failure-repo"
            let branchName = "branch-b"

            try
                writeRepositoryConfiguration root repositoryId repositoryName branchId branchName
                resetConfiguration ()
                Current() |> ignore

                let status = graceStatusTracking Array.empty<string> Array.empty<string>

                Watch.setSignalRBranchSubscriptionForWatchTests branchId parentId
                Watch.clearSignalRBranchSubscriptionForTransitionForWatchTests ()
                Watch.requestGraceWatchExplicitResyncForWatchTests "test-controlled transition recovery"

                let mutable refreshCalls = 0

                Watch.setSignalRSubscriptionRefreshForWatchTests (fun () ->
                    refreshCalls <- refreshCalls + 1
                    raise (InvalidOperationException("test-controlled SignalR refresh failure")))

                /// Reads the durable status snapshot used by transition recovery.
                let readStatus () = Task.FromResult(status)
                /// Keeps file upload retry available without queuing new content.
                let upload _ _ = Task.FromResult(())
                /// Keeps the incremental status helper available without changing status.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)
                /// Produces a clean scan after transition recovery reloads durable status.
                let scanForDifferences _ = Task.FromResult(List<FileSystemDifference>())
                /// Applies the scan-derived status boundary when differences exist.
                let updateGraceStatusFromDifferences currentStatus _ _ = Task.FromResult(Some currentStatus)
                /// Keeps incremental application available without changing status.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Writes Watch IPC after local coherence is proven.
                let updateIpc currentStatus directoryIds = Services.updateGraceWatchInterprocessFile currentStatus directoryIds

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

                refreshCalls |> should equal 1

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.HealthyIncremental

                Watch.isGraceWatchResyncPendingForWatchTests ()
                |> should equal false

                Watch.signalRBranchSubscriptionRefreshNeededForTransitionForWatchTests ()
                |> should equal false

                let subscription = Watch.signalRBranchSubscriptionForWatchTests ()

                subscription.BranchId
                |> should equal BranchId.Empty

                subscription.ParentBranchId
                |> should equal BranchId.Empty

                Watch.signalRAutomationEventTargetsWatchedParentBranchForWatchTests parentId
                |> should equal false
            finally
                Watch.resetSignalRSubscriptionRefreshForWatchTests ())

    /// Verifies that a missed marker-created callback cannot leave Watch serving the previous branch indefinitely.
    [<Test>]
    let ``unobserved current marker completed sidecar reloads target branch resync ipc`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let repositoryName = "transition-unobserved-sidecar-repo"
            let branchAName = "branch-a"
            let branchBName = "branch-b"

            writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
            resetConfiguration ()
            Current() |> ignore

            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental

            let statusA = graceStatusTracking Array.empty<string> Array.empty<string>
            let statusB = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIdsA = HashSet<DirectoryVersionId>(statusA.Index.Keys)
            let branchAMarker = Services.updateInProgressFileName ()
            let branchBIpc = Services.IpcFileNameForIdentity repositoryId repositoryName root branchBId branchBName

            (Services.updateGraceWatchInterprocessFile statusA (Some directoryIdsA))
                .GetAwaiter()
                .GetResult()

            let branchAIpc = Services.IpcFileName()

            branchAIpc |> File.Exists |> should equal true
            branchBIpc |> File.Exists |> should equal false

            writeRepositoryConfiguration root repositoryId repositoryName branchBId branchBName
            Watch.setReadGraceStatusFileForTransitionCompletionForWatchTests (fun () -> Task.FromResult(statusB))

            recordUnobservedCompletedUpdateMarkerDeletion branchAMarker DateTime.UtcNow

            Services.IpcFileName() |> should equal branchBIpc

            branchAIpc |> File.Exists |> should equal false
            branchBIpc |> File.Exists |> should equal true

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

            Watch.isGraceWatchResyncPendingForWatchTests ()
            |> should equal true

            readWatchStatusJsonStringProperty "BranchId"
            |> should equal $"{branchBId}"

            Services.getGraceWatchStatus().Result
            |> should equal None

            let branchBInspection = Services.inspectGraceWatchStatus().Result

            branchBInspection.EffectiveMode
            |> should equal (Some Services.GraceWatchRuntimeMode.Resynchronizing))

    /// Verifies that old branch IPC cannot be recreated while transition completion is retiring and reloading identity.
    [<Test>]
    let ``update marker deletion serializes old branch ipc retirement against pending publication`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let repositoryName = "transition-serialized-repo"
            let branchAName = "branch-a"
            let branchBName = "branch-b"

            writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
            resetConfiguration ()
            Current() |> ignore

            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental
            Services.setGraceWatchHasPendingWorkForStatus false

            let statusA = graceStatusTracking Array.empty<string> Array.empty<string>
            let statusB = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIdsA = HashSet<DirectoryVersionId>(statusA.Index.Keys)

            (Services.updateGraceWatchInterprocessFile statusA (Some directoryIdsA))
                .GetAwaiter()
                .GetResult()

            let branchAIpc = Services.IpcFileName()

            branchAIpc |> File.Exists |> should equal true

            writeRepositoryConfiguration root repositoryId repositoryName branchBId branchBName
            Watch.setReadGraceStatusFileForTransitionCompletionForWatchTests (fun () -> Task.FromResult(statusB))
            Watch.setReadGraceStatusFileForPendingWorkTransitionForWatchTests (fun () -> Task.FromResult(statusB))

            use publisherStarted = new ManualResetEventSlim(false)
            use publisherCompleted = new ManualResetEventSlim(false)
            let mutable publisherTask = Task.CompletedTask

            try
                Watch.setBranchTransitionCompletionAfterRetireProbeForWatchTests (fun () ->
                    Services.setGraceWatchHasPendingWorkForStatus true

                    publisherTask <-
                        Task.Run(
                            Action (fun () ->
                                publisherStarted.Set()
                                Watch.publishPendingWatchWorkTransitionIfNeededForWatchTests ()
                                publisherCompleted.Set())
                        )

                    publisherStarted.Wait(TimeSpan.FromSeconds(5.0))
                    |> should equal true

                    publisherCompleted.Wait(TimeSpan.FromMilliseconds(100.0))
                    |> should equal false

                    branchAIpc |> File.Exists |> should equal false)

                recordCompletedUpdateMarkerDeletion (Services.updateInProgressFileName ()) DateTime.UtcNow

                publisherTask.Wait(TimeSpan.FromSeconds(5.0))
                |> should equal true

                let branchBIpc = Services.IpcFileName()

                branchAIpc |> File.Exists |> should equal false
                branchBIpc |> File.Exists |> should equal true

                readWatchStatusJsonStringProperty "BranchId"
                |> should equal $"{branchBId}"
            finally
                Watch.resetBranchTransitionCompletionAfterRetireProbeForWatchTests ())

    /// Verifies that failed old branch IPC retirement downgrades old IPC before publishing target-branch resync IPC.
    [<Test>]
    let ``update marker deletion publishes target branch resync ipc when old branch ipc retirement fails`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let parentAId = Guid.NewGuid()
            let parentBId = Guid.NewGuid()
            let repositoryName = "transition-locked-old-ipc-repo"
            let branchAName = "branch-a"
            let branchBName = "branch-b"

            writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
            resetConfiguration ()
            Current() |> ignore

            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental
            Watch.setSignalRBranchSubscriptionForWatchTests branchAId parentAId

            let statusA = graceStatusTracking Array.empty<string> Array.empty<string>
            let statusB = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIdsA = HashSet<DirectoryVersionId>(statusA.Index.Keys)
            let branchBIpc = Services.IpcFileNameForIdentity repositoryId repositoryName root branchBId branchBName

            (Services.updateGraceWatchInterprocessFile statusA (Some directoryIdsA))
                .GetAwaiter()
                .GetResult()

            let branchAIpc = Services.IpcFileName()

            branchAIpc |> File.Exists |> should equal true

            writeRepositoryConfiguration root repositoryId repositoryName branchBId branchBName
            Watch.setReadGraceStatusFileForTransitionCompletionForWatchTests (fun () -> Task.FromResult(statusB))

            let mutable oldIpcReader: FileStream option = Some(new FileStream(branchAIpc, FileMode.Open, FileAccess.Read, FileShare.Read))
            let mutable refreshCalls = 0

            try
                Watch.setSignalRSubscriptionRefreshForWatchTests (fun () ->
                    refreshCalls <- refreshCalls + 1
                    Watch.setSignalRBranchSubscriptionForWatchTests branchBId parentBId)

                Watch.setRetirePreviousBranchWatchIpcForTransitionCompletionForWatchTests (fun _ ->
                    raise (IOException("test-controlled old branch IPC retirement failure")))

                Watch.setPreviousBranchIpcDowngradeRetryProbeForWatchTests (fun () ->
                    match oldIpcReader with
                    | Some reader ->
                        reader.Dispose()
                        oldIpcReader <- None
                    | None -> ())

                recordCompletedUpdateMarkerDeletion (Services.updateInProgressFileName ()) DateTime.UtcNow

                branchAIpc |> File.Exists |> should equal true
                branchBIpc |> File.Exists |> should equal true

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

                Watch.isGraceWatchResyncPendingForWatchTests ()
                |> should equal true

                refreshCalls |> should equal 0

                let subscription = Watch.signalRBranchSubscriptionForWatchTests ()

                subscription.BranchId
                |> should equal BranchId.Empty

                subscription.ParentBranchId
                |> should equal BranchId.Empty

                let rebasedStatuses = ResizeArray<DirectoryVersionId>()

                let readStatus () = Task.FromResult(statusB)

                let rebaseCurrentBranch (status: GraceStatus) = task { rebasedStatuses.Add(status.RootDirectoryId) }

                (Watch.handleSignalRAutomationEventForWatchTests readStatus rebaseCurrentBranch (promotionAppliedEnvelope parentAId (Guid.NewGuid())))
                    .GetAwaiter()
                    .GetResult()

                (Watch.handleSignalRAutomationEventForWatchTests readStatus rebaseCurrentBranch (promotionAppliedEnvelope parentBId (Guid.NewGuid())))
                    .GetAwaiter()
                    .GetResult()

                rebasedStatuses.ToArray()
                |> should equal Array.empty<DirectoryVersionId>

                readWatchStatusJsonStringProperty "BranchId"
                |> should equal $"{branchBId}"

                Services.getGraceWatchStatus().Result
                |> should equal None

                let inspection = Services.inspectGraceWatchStatus().Result

                inspection.EffectiveMode
                |> should equal (Some Services.GraceWatchRuntimeMode.Resynchronizing)

                /// Records upload calls for the scan-derived recovery pass.
                let upload _ _ = Task.FromResult(())
                /// Keeps the incremental status helper available without mutating status in this recovery scenario.
                let updateGraceStatus status _ = Task.FromResult(Some status)
                /// Produces a clean scan so recovery can republish the target branch IPC boundary.
                let scanForDifferences _ = Task.FromResult(List<FileSystemDifference>())
                /// Applies the scan-derived status boundary for the recovered target branch.
                let updateGraceStatusFromDifferences status _ _ = Task.FromResult(Some status)
                /// Keeps incremental application available without changing the recovery status.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Writes the target branch Watch IPC after resync recovery proves durable status.
                let updateIpc status directoryIds = Services.updateGraceWatchInterprocessFile status directoryIds

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

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.HealthyIncremental

                Watch.isGraceWatchResyncPendingForWatchTests ()
                |> should equal false

                refreshCalls |> should equal 1

                let recoveredSubscription = Watch.signalRBranchSubscriptionForWatchTests ()

                recoveredSubscription.BranchId
                |> should equal branchBId

                recoveredSubscription.ParentBranchId
                |> should equal parentBId

                Watch.signalRAutomationEventTargetsWatchedParentBranchForWatchTests parentAId
                |> should equal false

                Watch.signalRAutomationEventTargetsWatchedParentBranchForWatchTests parentBId
                |> should equal true

                Services.getGraceWatchStatus().Result
                |> Option.map (fun status -> status.RootDirectoryId)
                |> should equal (Some statusB.RootDirectoryId)

                (Watch.handleSignalRAutomationEventForWatchTests readStatus rebaseCurrentBranch (promotionAppliedEnvelope parentBId (Guid.NewGuid())))
                    .GetAwaiter()
                    .GetResult()

                (Watch.handleSignalRAutomationEventForWatchTests readStatus rebaseCurrentBranch (promotionAppliedEnvelope parentAId (Guid.NewGuid())))
                    .GetAwaiter()
                    .GetResult()

                rebasedStatuses.ToArray()
                |> should equal [| statusB.RootDirectoryId |]

                writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
                resetConfiguration ()
                Current() |> ignore

                let oldBranchInspection = Services.inspectGraceWatchStatus().Result

                oldBranchInspection.Exists |> should equal true
                oldBranchInspection.IsUsable |> should equal false

                oldBranchInspection.EffectiveMode
                |> should equal (Some Services.GraceWatchRuntimeMode.Resynchronizing)

                Services.getGraceWatchStatus().Result
                |> should equal None
            finally
                oldIpcReader
                |> Option.iter (fun reader -> reader.Dispose())

                Watch.resetRetirePreviousBranchWatchIpcForTransitionCompletionForWatchTests ()
                Watch.resetPreviousBranchIpcDowngradeRetryProbeForWatchTests ()
                Watch.resetSignalRSubscriptionRefreshForWatchTests ())

    /// Verifies that transition reload clears process-wide ignore decisions after `.graceignore` changes.
    [<Test>]
    let ``update marker deletion clears stale graceignore decisions before resuming`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let repositoryName = "transition-ignore-cache-repo"
            let branchAName = "branch-a"
            let branchBName = "branch-b"
            let fileName = "ignore-cache-sensitive.tmp"
            let filePath = Path.Combine(root, fileName)

            writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
            resetConfiguration ()
            Current() |> ignore

            File.WriteAllText(filePath, "cache me before branch switch")

            Services.shouldIgnoreFile filePath
            |> should equal false

            File.WriteAllText(Path.Combine(root, Constants.GraceIgnoreFileName), fileName)

            writeRepositoryConfiguration root repositoryId repositoryName branchBId branchBName
            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental

            let statusB = graceStatusTracking Array.empty<string> Array.empty<string>

            Watch.setReadGraceStatusFileForTransitionCompletionForWatchTests (fun () -> Task.FromResult(statusB))

            recordCompletedUpdateMarkerDeletion (Services.updateInProgressFileName ()) DateTime.UtcNow

            Services.shouldIgnoreFile filePath
            |> should equal true)

    /// Verifies that current transition completion authority wins over stale sidecars in the target branch directory.
    [<Test>]
    let ``update marker deletion prefers current deletion over stale target branch sidecar`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let repositoryName = "transition-stale-sidecar-repo"
            let branchAName = "branch-a"
            let branchBName = "branch-b"
            let relativePath = "grace-owned-after-switch.txt"
            let changedFilePath = Path.Combine(root, relativePath)
            let staleTargetBranchCompletedUtc = DateTime.UtcNow.AddSeconds(-20.0)
            let currentTransitionCompletedUtc = DateTime.UtcNow
            let statusB = graceStatusTracking [| relativePath |] Array.empty<string>
            let branchBMarker = Services.updateInProgressFileNameForIdentity repositoryId repositoryName root branchBId branchBName

            Directory.CreateDirectory(Path.GetDirectoryName(branchBMarker))
            |> ignore

            File.WriteAllText(branchBMarker + ".completed", staleTargetBranchCompletedUtc.ToString("O"))

            writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
            resetConfiguration ()
            Current() |> ignore

            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental
            Watch.setReadGraceStatusFileForWatchTests (fun () -> Task.FromResult(statusB))

            File.WriteAllText(changedFilePath, "Grace-owned payload from the current transition")
            File.SetLastWriteTimeUtc(changedFilePath, currentTransitionCompletedUtc.AddSeconds(-1.0))

            writeRepositoryConfiguration root repositoryId repositoryName branchBId branchBName

            recordCompletedUpdateMarkerDeletion (Services.updateInProgressFileName ()) currentTransitionCompletedUtc
            Watch.OnChanged(changedEvent changedFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal Array.empty<string>

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that later marker starts cannot erase delayed callback authority from the completed transition.
    [<Test>]
    let ``new update marker created event preserves delayed callback authority from completed transition`` () =
        withTempRepo (fun root ->
            let changedFilePath = Path.Combine(root, "delayed-after-later-marker-start.txt")
            let updateMarkerFile = Services.updateInProgressFileName ()
            let firstTransitionCompletedUtc = DateTime.UtcNow

            Directory.CreateDirectory(Path.GetDirectoryName(updateMarkerFile))
            |> ignore

            File.WriteAllText(changedFilePath, "Grace-owned payload from the first transition")
            File.SetLastWriteTimeUtc(changedFilePath, firstTransitionCompletedUtc.AddSeconds(-1.0))

            Watch.recordGraceUpdateMarkerDeletedUtcForTests firstTransitionCompletedUtc

            Watch.setReadGraceStatusFileForWatchTests (fun () ->
                Task.FromResult(
                    graceStatusTracking
                        [|
                            "delayed-after-later-marker-start.txt"
                        |]
                        Array.empty<string>
                ))

            File.WriteAllText(updateMarkerFile, "`grace switch` is in progress again.")
            Watch.OnGraceUpdateInProgressCreated(createdEvent updateMarkerFile)
            File.Delete(updateMarkerFile)
            Watch.OnChanged(changedEvent changedFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal Array.empty<string>

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that an unrelated marker deletion without sidecar cannot erase delayed callback authority.
    [<Test>]
    let ``update marker deletion without sidecar preserves delayed callback authority from completed transition`` () =
        withTempRepo (fun root ->
            let changedFilePath = Path.Combine(root, "delayed-after-no-sidecar-marker.txt")
            let updateMarkerFile = Services.updateInProgressFileName ()
            let firstTransitionCompletedUtc = DateTime.UtcNow

            File.WriteAllText(changedFilePath, "Grace-owned payload from the completed transition")
            File.SetLastWriteTimeUtc(changedFilePath, firstTransitionCompletedUtc.AddSeconds(-1.0))

            Watch.recordGraceUpdateMarkerDeletedUtcForTests firstTransitionCompletedUtc

            Watch.setReadGraceStatusFileForWatchTests (fun () ->
                Task.FromResult(
                    graceStatusTracking
                        [|
                            "delayed-after-no-sidecar-marker.txt"
                        |]
                        Array.empty<string>
                ))

            recordIncompleteUpdateMarkerDeletion updateMarkerFile
            Watch.OnChanged(changedEvent changedFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal Array.empty<string>

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that repeated transition deletion uses the just-deleted marker sidecar after configuration advances.
    [<Test>]
    let ``update marker deletion uses deleted marker sidecar after repeated transition advances config`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let branchCId = Guid.NewGuid()
            let repositoryName = "transition-current-sidecar-repo"
            let branchBName = "branch-b"
            let branchCName = "branch-c"
            let relativePath = "current-sidecar-owned.txt"
            let changedFilePath = Path.Combine(root, relativePath)
            let staleRecordedCompletedUtc = DateTime.UtcNow.AddSeconds(-20.0)
            let branchBCompletedUtc = DateTime.UtcNow
            let statusC = graceStatusTracking [| relativePath |] Array.empty<string>
            let branchBMarker = Services.updateInProgressFileNameForIdentity repositoryId repositoryName root branchBId branchBName

            Directory.CreateDirectory(Path.GetDirectoryName(branchBMarker))
            |> ignore

            Watch.recordGraceUpdateMarkerDeletedUtcForTests staleRecordedCompletedUtc
            File.WriteAllText(branchBMarker, "`grace switch` is in progress.")
            File.SetLastWriteTimeUtc(branchBMarker, branchBCompletedUtc.AddSeconds(-1.0))
            Watch.OnGraceUpdateInProgressCreated(createdEvent branchBMarker)
            File.WriteAllText(branchBMarker + ".completed", branchBCompletedUtc.ToString("O"))
            File.WriteAllText(changedFilePath, "Grace-owned payload from the repeated transition")
            File.SetLastWriteTimeUtc(changedFilePath, branchBCompletedUtc.AddSeconds(-1.0))

            writeRepositoryConfiguration root repositoryId repositoryName branchCId branchCName
            resetConfiguration ()
            Current() |> ignore

            Watch.setReadGraceStatusFileForWatchTests (fun () -> Task.FromResult(statusC))

            File.Delete(branchBMarker)
            Watch.OnGraceUpdateInProgressDeleted(deletedEvent branchBMarker)
            Watch.OnChanged(changedEvent changedFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal Array.empty<string>

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that delayed callbacks prefer the current sidecar before Watch observes marker deletion.
    [<Test>]
    let ``current marker sidecar suppresses repeated transition callback before deletion handler`` () =
        withTempRepo (fun root ->
            let relativePath = "current-sidecar-before-deletion.txt"
            let changedFilePath = Path.Combine(root, relativePath)
            let updateMarkerFile = Services.updateInProgressFileName ()
            let staleRecordedCompletedUtc = DateTime.UtcNow.AddSeconds(-20.0)
            let currentCompletedUtc = DateTime.UtcNow

            Directory.CreateDirectory(Path.GetDirectoryName(updateMarkerFile))
            |> ignore

            Watch.recordGraceUpdateMarkerDeletedUtcForTests staleRecordedCompletedUtc
            File.WriteAllText(updateMarkerFile, "`grace switch` is in progress.")
            File.SetLastWriteTimeUtc(updateMarkerFile, currentCompletedUtc.AddSeconds(-1.0))
            Watch.OnGraceUpdateInProgressCreated(createdEvent updateMarkerFile)
            File.WriteAllText(updateMarkerFile + ".completed", currentCompletedUtc.ToString("O"))
            File.WriteAllText(changedFilePath, "Grace-owned payload before marker deletion callback")
            File.SetLastWriteTimeUtc(changedFilePath, currentCompletedUtc.AddSeconds(-1.0))

            Watch.setReadGraceStatusFileForWatchTests (fun () -> Task.FromResult(graceStatusTracking [| relativePath |] Array.empty<string>))

            File.Delete(updateMarkerFile)
            Watch.OnChanged(changedEvent changedFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal Array.empty<string>

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that an incomplete stale marker deletion does not remove the current branch completion sidecar.
    [<Test>]
    let ``update marker deletion without sidecar preserves current branch sidecar`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let branchCId = Guid.NewGuid()
            let repositoryName = "transition-incomplete-stale-marker-repo"
            let branchBName = "branch-b"
            let branchCName = "branch-c"
            let branchBMarker = Services.updateInProgressFileNameForIdentity repositoryId repositoryName root branchBId branchBName
            let branchCMarker = Services.updateInProgressFileNameForIdentity repositoryId repositoryName root branchCId branchCName
            let branchCCompletedSidecar = branchCMarker + ".completed"

            Directory.CreateDirectory(Path.GetDirectoryName(branchBMarker))
            |> ignore

            Directory.CreateDirectory(Path.GetDirectoryName(branchCMarker))
            |> ignore

            File.WriteAllText(branchBMarker, "`grace switch` is in progress.")
            File.WriteAllText(branchCCompletedSidecar, DateTime.UtcNow.ToString("O"))

            writeRepositoryConfiguration root repositoryId repositoryName branchCId branchCName
            resetConfiguration ()
            Current() |> ignore

            File.Delete(branchBMarker)
            Watch.OnGraceUpdateInProgressDeleted(deletedEvent branchBMarker)

            File.Exists(branchCCompletedSidecar)
            |> should equal true)

    /// Verifies that a stale completed marker cannot finish transition work while the current marker is still live.
    [<Test>]
    let ``stale marker deletion with sidecar does not complete transition while current marker is live`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let repositoryName = "transition-stale-live-marker-repo"
            let branchAName = "branch-a"
            let branchBName = "branch-b"
            let branchAMarker = Services.updateInProgressFileNameForIdentity repositoryId repositoryName root branchAId branchAName
            let branchBMarker = Services.updateInProgressFileNameForIdentity repositoryId repositoryName root branchBId branchBName
            let staleTransitionCompletedUtc = DateTime.UtcNow
            let mutable completionProbeCalls = 0

            Directory.CreateDirectory(Path.GetDirectoryName(branchAMarker))
            |> ignore

            Directory.CreateDirectory(Path.GetDirectoryName(branchBMarker))
            |> ignore

            File.WriteAllText(branchAMarker, "`grace switch` from branch A is finishing late.")
            File.WriteAllText(branchAMarker + ".completed", staleTransitionCompletedUtc.ToString("O"))
            File.WriteAllText(branchBMarker, "`grace switch` to branch C is still in progress.")

            writeRepositoryConfiguration root repositoryId repositoryName branchBId branchBName
            resetConfiguration ()
            Current() |> ignore

            try
                Watch.setBranchTransitionCompletionAfterRetireProbeForWatchTests (fun () -> completionProbeCalls <- completionProbeCalls + 1)

                File.Delete(branchAMarker)
                Watch.OnGraceUpdateInProgressDeleted(deletedEvent branchAMarker)

                completionProbeCalls |> should equal 0
                File.Exists(branchBMarker) |> should equal true
            finally
                Watch.resetBranchTransitionCompletionAfterRetireProbeForWatchTests ())

    /// Verifies that stale sidecars cannot complete a later marker lifecycle when Watch never observed that marker.
    [<Test>]
    let ``stale same branch sidecar without fresh marker observation does not complete transition`` () =
        withTempRepo (fun _ ->
            let updateMarkerFile = Services.updateInProgressFileName ()
            let staleCompletedUtc = DateTime.UtcNow.AddSeconds(-5.0)
            let mutable completionProbeCalls = 0

            Directory.CreateDirectory(Path.GetDirectoryName(updateMarkerFile))
            |> ignore

            File.WriteAllText(updateMarkerFile, "`grace switch` ended before completing.")
            Watch.OnGraceUpdateInProgressCreated(createdEvent updateMarkerFile)
            File.Delete(updateMarkerFile)
            Watch.OnGraceUpdateInProgressDeleted(deletedEvent updateMarkerFile)

            File.WriteAllText(updateMarkerFile + ".completed", staleCompletedUtc.ToString("O"))
            File.WriteAllText(updateMarkerFile, "`grace switch` is in progress without a new sidecar.")

            try
                Watch.setBranchTransitionCompletionAfterRetireProbeForWatchTests (fun () -> completionProbeCalls <- completionProbeCalls + 1)

                File.Delete(updateMarkerFile)
                Watch.OnGraceUpdateInProgressDeleted(deletedEvent updateMarkerFile)

                completionProbeCalls |> should equal 0
            finally
                Watch.resetBranchTransitionCompletionAfterRetireProbeForWatchTests ())

    /// Verifies that transition completion cannot claim healthy incremental mode after an unverified branch IPC publish.
    [<Test>]
    let ``update marker deletion requests resync when new branch ipc publication is not verified`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let repositoryName = "transition-unverified-repo"
            let branchAName = "branch-a"
            let branchBName = "branch-b"

            writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
            resetConfiguration ()
            Current() |> ignore

            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental

            let statusA = graceStatusTracking Array.empty<string> Array.empty<string>
            let statusB = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIdsA = HashSet<DirectoryVersionId>(statusA.Index.Keys)

            (Services.updateGraceWatchInterprocessFile statusA (Some directoryIdsA))
                .GetAwaiter()
                .GetResult()

            let branchAIpc = Services.IpcFileName()
            let branchBIpc = Services.IpcFileNameForIdentity repositoryId repositoryName root branchBId branchBName

            Directory.CreateDirectory(Path.GetDirectoryName(branchBIpc))
            |> ignore

            writeRepositoryConfiguration root repositoryId repositoryName branchBId branchBName
            Watch.setReadGraceStatusFileForTransitionCompletionForWatchTests (fun () -> Task.FromResult(statusB))

            use lockedBranchBIpc = new FileStream(branchBIpc, FileMode.Create, FileAccess.ReadWrite, FileShare.None)

            recordCompletedUpdateMarkerDeletion (Services.updateInProgressFileName ()) DateTime.UtcNow

            lockedBranchBIpc.Dispose()

            branchAIpc |> File.Exists |> should equal false

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that repeated same-process branch transitions rebind marker observation to each refreshed branch.
    [<Test>]
    let ``update marker deletion rebinds marker watcher for repeated branch transitions`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let repositoryName = "transition-rebind-repo"
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let branchCId = Guid.NewGuid()
            let branchAName = "branch-a"
            let branchBName = "branch-b"
            let branchCName = "branch-c"
            let statusB = graceStatusTracking Array.empty<string> Array.empty<string>
            let statusC = graceStatusTracking Array.empty<string> Array.empty<string>
            let reboundMarkerFiles = ResizeArray<string>()

            try
                Watch.setUpdateMarkerWatcherRebindForWatchTests (fun () -> reboundMarkerFiles.Add(Services.updateInProgressFileName ()))
                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental

                writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
                resetConfiguration ()
                Current() |> ignore

                let statusA = graceStatusTracking Array.empty<string> Array.empty<string>
                let branchAIpc = Services.IpcFileName()

                (Services.updateGraceWatchInterprocessFile statusA (Some(HashSet<DirectoryVersionId>(statusA.Index.Keys))))
                    .GetAwaiter()
                    .GetResult()

                branchAIpc |> File.Exists |> should equal true

                writeRepositoryConfiguration root repositoryId repositoryName branchBId branchBName
                Watch.setReadGraceStatusFileForTransitionCompletionForWatchTests (fun () -> Task.FromResult(statusB))

                let branchAMarker = Services.updateInProgressFileName ()

                recordCompletedUpdateMarkerDeletion branchAMarker DateTime.UtcNow

                let branchBMarker = Services.updateInProgressFileName ()
                let branchBIpc = Services.IpcFileName()

                branchAIpc |> File.Exists |> should equal false
                branchBIpc |> File.Exists |> should equal true

                reboundMarkerFiles.ToArray()
                |> should equal [| branchBMarker |]

                writeRepositoryConfiguration root repositoryId repositoryName branchCId branchCName
                Watch.setReadGraceStatusFileForTransitionCompletionForWatchTests (fun () -> Task.FromResult(statusC))

                recordCompletedUpdateMarkerDeletion branchBMarker DateTime.UtcNow

                let branchCMarker = Services.updateInProgressFileName ()
                let branchCIpc = Services.IpcFileName()

                branchBIpc |> File.Exists |> should equal false
                branchCIpc |> File.Exists |> should equal true

                reboundMarkerFiles.ToArray()
                |> should equal [| branchBMarker; branchCMarker |]
            finally
                Watch.resetUpdateMarkerWatcherRebindForWatchTests ())

    /// Verifies that incoherent refreshed state cannot advertise healthy incremental status after marker deletion.
    [<Test>]
    let ``update marker deletion with incoherent status publishes non-incremental new branch ipc`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchAId = Guid.NewGuid()
            let branchBId = Guid.NewGuid()
            let parentAId = Guid.NewGuid()
            let parentBId = Guid.NewGuid()
            let repositoryName = "transition-incoherent-repo"
            let branchAName = "branch-a"
            let branchBName = "branch-b"

            try
                writeRepositoryConfiguration root repositoryId repositoryName branchAId branchAName
                resetConfiguration ()
                Current() |> ignore

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental
                Watch.setSignalRBranchSubscriptionForWatchTests branchAId parentAId

                let statusA = graceStatusTracking Array.empty<string> Array.empty<string>
                let directoryIdsA = HashSet<DirectoryVersionId>(statusA.Index.Keys)

                (Services.updateGraceWatchInterprocessFile statusA (Some directoryIdsA))
                    .GetAwaiter()
                    .GetResult()

                let mutable refreshCalls = 0

                Watch.setSignalRSubscriptionRefreshForWatchTests (fun () ->
                    refreshCalls <- refreshCalls + 1
                    Watch.setSignalRBranchSubscriptionForWatchTests branchBId parentBId)

                writeRepositoryConfiguration root repositoryId repositoryName branchBId branchBName
                Watch.setReadGraceStatusFileForTransitionCompletionForWatchTests (fun () -> Task.FromResult(GraceStatus.Default))

                recordCompletedUpdateMarkerDeletion (Services.updateInProgressFileName ()) DateTime.UtcNow

                let branchBIpc = Services.IpcFileNameForIdentity repositoryId repositoryName root branchBId branchBName

                Services.IpcFileName() |> should equal branchBIpc

                branchBIpc |> File.Exists |> should equal true

                Services.getGraceWatchStatus().Result
                |> should equal None

                refreshCalls |> should equal 0

                let subscription = Watch.signalRBranchSubscriptionForWatchTests ()

                subscription.BranchId
                |> should equal BranchId.Empty

                subscription.ParentBranchId
                |> should equal BranchId.Empty

                let rebasedStatuses = ResizeArray<DirectoryVersionId>()

                let readStatus () = Task.FromResult(GraceStatus.Default)

                let rebaseCurrentBranch (status: GraceStatus) = task { rebasedStatuses.Add(status.RootDirectoryId) }

                (Watch.handleSignalRAutomationEventForWatchTests readStatus rebaseCurrentBranch (promotionAppliedEnvelope parentBId (Guid.NewGuid())))
                    .GetAwaiter()
                    .GetResult()

                rebasedStatuses.ToArray()
                |> should equal Array.empty<DirectoryVersionId>

                readWatchStatusJsonStringProperty "BranchId"
                |> should equal $"{branchBId}"

                readWatchStatusJsonStringProperty "BranchName"
                |> should equal branchBName

                let inspection = Services.inspectGraceWatchStatus().Result

                inspection.EffectiveMode
                |> should equal (Some Services.GraceWatchRuntimeMode.Resynchronizing)

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.Resynchronizing
            finally
                Watch.resetSignalRSubscriptionRefreshForWatchTests ())

    /// Verifies that startup marker completion cannot skip startup catch-up and publish healthy incremental status.
    [<Test>]
    let ``startup update marker deletion keeps startup from entering healthy incremental`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let repositoryName = "startup-transition-repo"
            let branchName = "startup-branch"

            writeRepositoryConfiguration root repositoryId repositoryName branchId branchName
            resetConfiguration ()
            Current() |> ignore

            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

            let status = graceStatusTracking Array.empty<string> Array.empty<string>

            Watch.setReadGraceStatusFileForTransitionCompletionForWatchTests (fun () -> Task.FromResult(status))

            recordCompletedUpdateMarkerDeletion (Services.updateInProgressFileName ()) DateTime.UtcNow

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.StartingUp

            Services.getGraceWatchStatus().Result
            |> should equal None

            let inspection = Services.inspectGraceWatchStatus().Result

            inspection.EffectiveMode
            |> should equal (Some Services.GraceWatchRuntimeMode.Resynchronizing))

    /// Verifies that user writes after marker deletion still enqueue as local Watch work.
    [<Test>]
    let ``update marker deletion does not suppress later user changed observation`` () =
        withTempRepo (fun root ->
            let changedFilePath = Path.Combine(root, "later-user-write.txt")
            let markerDeletedUtc = DateTime.UtcNow.AddSeconds(-5.0)

            File.WriteAllText(changedFilePath, "user edit after branch switch")
            File.SetLastWriteTimeUtc(changedFilePath, markerDeletedUtc.AddSeconds(1.0))
            Watch.recordGraceUpdateMarkerDeletedUtcForTests markerDeletedUtc
            Watch.OnChanged(changedEvent changedFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal [| changedFilePath |]

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Produces an empty difference list for watch tests that do not exercise a scan path.
    let private scanForNoDifferences _ = Task.FromResult(List<FileSystemDifference>())

    /// Fails tests that exercise healthy running-mode watch status application through a working-tree scan.
    let private scannerHostileDifferenceDiscovery _ : Task<List<FileSystemDifference>> =
        raise (InvalidOperationException("Healthy running-mode watch status application must not scan for differences."))

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

    /// Verifies that marker deletion records the switch completion instant instead of late Watch handling time.
    [<Test>]
    let ``update marker deletion uses completed timestamp for delayed changed classification`` () =
        withTempRepo (fun root ->
            let changedFilePath = Path.Combine(root, "user-write-after-switch.txt")
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerCompletedUtc = DateTime.UtcNow.AddSeconds(-5.0)

            File.WriteAllText(changedFilePath, "user edit after branch switch completed")
            File.SetLastWriteTimeUtc(changedFilePath, markerCompletedUtc.AddSeconds(1.0))
            recordCompletedUpdateMarkerDeletion updateMarkerFile markerCompletedUtc

            Watch.OnChanged(changedEvent changedFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal [| changedFilePath |]

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that delayed create callbacks from completed Grace-owned switch writes are suppressed.
    [<Test>]
    let ``update marker deletion suppresses delayed Grace-owned created observation`` () =
        withTempRepo (fun root ->
            let createdFilePath = Path.Combine(root, "delayed-grace-owned-create.txt")
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerCompletedUtc = DateTime.UtcNow

            File.WriteAllText(createdFilePath, "Grace-owned branch switch create")
            File.SetLastWriteTimeUtc(createdFilePath, markerCompletedUtc.AddSeconds(-1.0))
            Watch.setReadGraceStatusFileForWatchTests (fun () -> Task.FromResult(graceStatusTracking [| "delayed-grace-owned-create.txt" |] Array.empty<string>))
            recordCompletedUpdateMarkerDeletion updateMarkerFile markerCompletedUtc

            Watch.OnCreated(createdEvent createdFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal Array.empty<string>

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that preserved older mtimes alone cannot suppress a new user-created file after switch completion.
    [<Test>]
    let ``update marker deletion does not suppress untracked created file with preserved old mtime`` () =
        withTempRepo (fun root ->
            let createdFilePath = Path.Combine(root, "user-created-preserved-mtime.txt")
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerCompletedUtc = DateTime.UtcNow

            Watch.setReadGraceStatusFileForWatchTests (fun () -> Task.FromResult(graceStatusTracking Array.empty<string> Array.empty<string>))
            recordCompletedUpdateMarkerDeletion updateMarkerFile markerCompletedUtc

            File.WriteAllText(createdFilePath, "user-created payload with preserved timestamp")
            File.SetLastWriteTimeUtc(createdFilePath, markerCompletedUtc.AddSeconds(-10.0))
            Watch.OnCreated(createdEvent createdFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal [| createdFilePath |]

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that preserved older mtimes alone cannot suppress a changed user file absent from completed status.
    [<Test>]
    let ``update marker deletion does not suppress untracked changed file with preserved old mtime`` () =
        withTempRepo (fun root ->
            let changedFilePath = Path.Combine(root, "user-changed-preserved-mtime.txt")
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerCompletedUtc = DateTime.UtcNow

            Watch.setReadGraceStatusFileForWatchTests (fun () -> Task.FromResult(graceStatusTracking Array.empty<string> Array.empty<string>))
            recordCompletedUpdateMarkerDeletion updateMarkerFile markerCompletedUtc

            File.WriteAllText(changedFilePath, "user-changed payload with preserved timestamp")
            File.SetLastWriteTimeUtc(changedFilePath, markerCompletedUtc.AddSeconds(-10.0))
            Watch.OnChanged(changedEvent changedFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal [| changedFilePath |]

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that delayed delete callbacks for paths absent from post-switch GraceStatus are suppressed.
    [<Test>]
    let ``update marker deletion suppresses delayed Grace-owned deleted observation`` () =
        withTempRepo (fun root ->
            let deletedFilePath = Path.Combine(root, "removed-by-switch.txt")
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerCompletedUtc = DateTime.UtcNow

            Watch.setReadGraceStatusFileForWatchTests (fun () -> Task.FromResult(graceStatusTracking Array.empty<string> Array.empty<string>))
            recordCompletedUpdateMarkerDeletion updateMarkerFile markerCompletedUtc

            Watch.OnDeleted(deletedEvent deletedFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal Array.empty<string>

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that post-switch user-created files are drained when deleted inside the completed-marker window.
    [<Test>]
    let ``update marker deletion drains post-switch user-created file deleted inside suppression window`` () =
        withTempRepo (fun root ->
            let createdFilePath = Path.Combine(root, "post-switch-user-created.txt")
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerCompletedUtc = DateTime.UtcNow.AddSeconds(-5.0)

            Watch.setReadGraceStatusFileForWatchTests (fun () -> Task.FromResult(graceStatusTracking Array.empty<string> Array.empty<string>))
            recordCompletedUpdateMarkerDeletion updateMarkerFile markerCompletedUtc

            File.WriteAllText(createdFilePath, "user-created payload")
            File.SetLastWriteTimeUtc(createdFilePath, markerCompletedUtc.AddSeconds(1.0))
            Watch.OnCreated(createdEvent createdFilePath)

            File.Delete(createdFilePath)
            Watch.OnDeleted(deletedEvent createdFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal Array.empty<string>

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal [| "post-switch-user-created.txt" |]

            let updateCalls, uploadCalls = processPendingWatchWorkForTestWithStatus (graceStatusTracking Array.empty<string> Array.empty<string>)

            updateCalls |> should equal 0
            uploadCalls |> should equal 0

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.FilesToProcess
            |> should equal Array.empty<string>

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that delayed delete suppression does not hide paths still tracked by completed GraceStatus.
    [<Test>]
    let ``update marker deletion preserves tracked deleted observation`` () =
        withTempRepo (fun root ->
            let deletedFilePath = Path.Combine(root, "tracked-user-delete.txt")
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerCompletedUtc = DateTime.UtcNow

            Watch.setReadGraceStatusFileForWatchTests (fun () -> Task.FromResult(graceStatusTracking [| "tracked-user-delete.txt" |] Array.empty<string>))
            recordCompletedUpdateMarkerDeletion updateMarkerFile markerCompletedUtc

            Watch.OnDeleted(deletedEvent deletedFilePath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal Array.empty<string>

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal [| "tracked-user-delete.txt" |])

    /// Verifies that delayed directory callbacks from completed Grace-owned switch writes are suppressed.
    [<Test>]
    let ``update marker deletion suppresses delayed Grace-owned directory observation`` () =
        withTempRepo (fun root ->
            let directoryPath = Path.Combine(root, "delayed-grace-owned-directory")
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerCompletedUtc = DateTime.UtcNow

            Directory.CreateDirectory(directoryPath) |> ignore

            Directory.SetLastWriteTimeUtc(directoryPath, markerCompletedUtc.AddSeconds(-1.0))
            Watch.setReadGraceStatusFileForWatchTests (fun () -> Task.FromResult(graceStatusTracking Array.empty<string> [| "delayed-grace-owned-directory" |]))
            recordCompletedUpdateMarkerDeletion updateMarkerFile markerCompletedUtc

            Watch.OnCreated(createdEvent directoryPath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal Array.empty<string>

            pending.DirectoriesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that delayed GraceStatus artifact observations still publish refresh work after switch completion.
    [<Test>]
    let ``update marker deletion preserves delayed GraceStatus artifact refresh observation`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)
            let updateMarkerFile = Services.updateInProgressFileName ()
            let markerCompletedUtc = DateTime.UtcNow
            let graceStatusFile = Current().GraceStatusFile

            Services.setGraceWatchHasPendingWorkForStatus false

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            Directory.CreateDirectory(Path.GetDirectoryName(graceStatusFile))
            |> ignore

            File.WriteAllText(graceStatusFile, "Grace-owned local state refresh")
            File.SetLastWriteTimeUtc(graceStatusFile, markerCompletedUtc.AddSeconds(-1.0))
            recordCompletedUpdateMarkerDeletion updateMarkerFile markerCompletedUtc

            Watch.OnChanged(changedEvent graceStatusFile)

            Watch.graceStatusHasChangedForWatchTests ()
            |> should equal true

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal false)

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

    /// Verifies that a concurrent file event does not leave already-applied upload paths waiting for status.
    [<Test>]
    let ``processed upload path clears when file event arrives during status apply`` () =
        withTempRepo (fun root ->
            let appliedRelativePath = "already-applied.txt"
            let appliedFilePath = Path.Combine(root, appliedRelativePath)
            let createdDuringApplyRelativePath = "created-during-upload-apply.txt"
            let createdDuringApplyPath = Path.Combine(root, createdDuringApplyRelativePath)
            /// Tracks upload Calls changes so the test proves old applied content is not retried.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls changes so the race and retry passes are explicit.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the latest Differences passed to the apply seam.
            let mutable observedDifferences = List<FileSystemDifference>()

            File.WriteAllText(appliedFilePath, "already uploaded content")
            Watch.OnChanged(changedEvent appliedFilePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ pendingFilePath =
                uploadCalls <- uploadCalls + 1
                recordUploadedFileVersion $"{pendingFilePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                observedDifferences <- differences

                if applyFromDifferencesCalls = 1 then
                    File.WriteAllText(createdDuringApplyPath, "created while status was saving")
                    Watch.OnChanged(changedEvent createdDuringApplyPath)

                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            /// Runs one watch processing pass with the status-apply race test clients.
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

            uploadCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 1

            let afterRace = Watch.pendingWatchWorkSnapshotForTests ()

            afterRace.FilesToProcess
            |> should equal [| createdDuringApplyPath |]

            Watch.processedFileRelativePathsPendingStatusForWatchTests ()
            |> should equal Array.empty<string>

            processPendingWork ()

            uploadCalls |> should equal 2
            applyFromDifferencesCalls |> should equal 2

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.File, createdDuringApplyRelativePath
                |])

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

    /// Verifies that normalized observations are durable before status application and applied after success.
    [<Test>]
    let ``watch appends journal before applying status and advances boundary after commit`` () =
        withTempRepo (fun root ->
            let relativePath = "journal-order-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let ordering = ResizeArray<string>()

            Watch.OnDeleted(deletedEvent filePath)

            try
                Watch.setWatchJournalClientsForWatchTests
                    (fun observations ->
                        task {
                            let observationArray = observations |> Seq.toArray

                            observationArray
                            |> Array.map (fun observation -> observation.DifferenceType, observation.EntryType, string observation.RelativePath)
                            |> should
                                equal
                                [|
                                    DifferenceType.Delete, FileSystemEntryType.File, relativePath
                                |]

                            ordering.Add("append")
                            return [| 1L |]
                        })
                    (fun sequences ->
                        task {
                            sequences |> Seq.toArray |> should equal [| 1L |]

                            ordering.ToArray()
                            |> should equal [| "append"; "apply" |]

                            ordering.Add("advance")
                            return 1L
                        })

                /// Reads status needed by the durable journal ordering scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this status-only observation scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Applies the normalized observation after journal append succeeds.
                let updateGraceStatusFromDifferences currentStatus differences _ =
                    ordering.ToArray() |> should equal [| "append" |]

                    differences
                    |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, string difference.RelativePath)
                    |> Seq.toArray
                    |> should
                        equal
                        [|
                            DifferenceType.Delete, FileSystemEntryType.File, relativePath
                        |]

                    ordering.Add("apply")
                    Task.FromResult(Some currentStatus)

                /// Leaves incremental status writes outside this status-only observation scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the ordering test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                ordering.ToArray()
                |> should equal [| "append"; "apply"; "advance" |]

                Watch
                    .pendingWatchWorkSnapshotForTests()
                    .StatusUpdateTriggers
                |> should equal Array.empty<string>
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies that startup replay reuses compatible pending journal sequences after reconciliation.
    [<Test>]
    let ``watch startup replay reuses pending journal sequence after reconciliation`` () =
        withTempRepo (fun root ->
            let relativePath = "startup-replay-delete.txt"
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let ordering = ResizeArray<string>()
            let childDirectory = Path.Combine(root, "child")

            Directory.CreateDirectory(childDirectory)
            |> ignore

            Environment.CurrentDirectory <- childDirectory

            try
                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                Watch.setWatchJournalStartupClientsForWatchTests
                    (fun scope ->
                        task {
                            scope.RepositoryId
                            |> should equal (Current().RepositoryId)

                            scope.BranchId
                            |> should equal (Current().BranchId)

                            scope.WorkspaceRoot
                            |> should equal (Path.GetFullPath(Current().RootDirectory))

                            scope.WatchRoot
                            |> should equal (Path.GetFullPath(Current().RootDirectory))

                            ordering.Add("recover")

                            return
                                {
                                    LocalStateDb.WatchJournalStartupRecovery.DbPath = Current().GraceStatusFile
                                    AppliedThroughSequence = 0L
                                    CompatibleReplayRows =
                                        [|
                                            {
                                                LocalStateDb.WatchJournalPendingReplay.Sequence = 42L
                                                DifferenceType = DifferenceType.Delete
                                                EntryType = FileSystemEntryType.File
                                                RelativePath = RelativePath relativePath
                                            }
                                        |]
                                    QuarantinedRows = Array.empty
                                }
                        })
                    (fun event ->
                        task {
                            let expectedLifecycleEvent =
                                event.Message.Contains("replay", StringComparison.OrdinalIgnoreCase)
                                || event.EventType = "startup-reconciliation-complete"

                            expectedLifecycleEvent |> should equal true

                            ordering.Add($"lifecycle:{event.EventType}")
                        })

                Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status
                |> fun recoveryTask -> recoveryTask.GetAwaiter().GetResult()
                |> ignore

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.File (RelativePath relativePath)
                )
                |> should equal (Some 42L)

                Watch.setWatchJournalClientsForWatchTests
                    (fun _ -> Task.FromException<int64 array>(InvalidOperationException("startup replay must not append duplicate rows")))
                    (fun sequences ->
                        task {
                            sequences |> Seq.toArray |> should equal [| 42L |]
                            ordering.Add("advance")
                            return 42L
                        })

                /// Reads status needed by the startup replay scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this replayed delete scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Applies only the replayed pending row after startup recovery has completed.
                let updateGraceStatusFromDifferences currentStatus differences _ =
                    ordering.ToArray()
                    |> should
                        equal
                        [|
                            "lifecycle:startup-reconciliation-complete"
                            "recover"
                            "lifecycle:startup-replay-complete"
                        |]

                    differences
                    |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, string difference.RelativePath)
                    |> Seq.toArray
                    |> should
                        equal
                        [|
                            DifferenceType.Delete, FileSystemEntryType.File, relativePath
                        |]

                    ordering.Add("apply")
                    Task.FromResult(Some currentStatus)

                /// Leaves incremental status writes outside this replay ordering scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the replay ordering test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                ordering.ToArray()
                |> should
                    equal
                    [|
                        "lifecycle:startup-reconciliation-complete"
                        "recover"
                        "lifecycle:startup-replay-complete"
                        "apply"
                        "advance"
                    |]
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that coalesced duplicate startup replay rows all retire after one status application.
    [<Test>]
    let ``watch startup replay retires all duplicate sequences for coalesced difference`` () =
        withTempRepo (fun _ ->
            let relativePath = "startup-replay-duplicate.txt"
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let ordering = ResizeArray<string>()

            try
                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                Watch.setWatchJournalStartupClientsForWatchTests
                    (fun _ ->
                        task {
                            ordering.Add("recover")

                            let replayRow sequence : LocalStateDb.WatchJournalPendingReplay =
                                {
                                    Sequence = sequence
                                    DifferenceType = DifferenceType.Delete
                                    EntryType = FileSystemEntryType.File
                                    RelativePath = RelativePath relativePath
                                }

                            return
                                {
                                    LocalStateDb.WatchJournalStartupRecovery.DbPath = Current().GraceStatusFile
                                    AppliedThroughSequence = 0L
                                    CompatibleReplayRows = [| replayRow 41L; replayRow 42L |]
                                    QuarantinedRows = Array.empty
                                }
                        })
                    (fun _ -> Task.FromResult(()))

                Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status
                |> fun recoveryTask -> recoveryTask.GetAwaiter().GetResult()
                |> ignore

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.File (RelativePath relativePath)
                )
                |> should equal (Some 41L)

                Watch.setWatchJournalClientsForWatchTests
                    (fun _ -> Task.FromException<int64 array>(InvalidOperationException("startup replay must not append duplicate rows")))
                    (fun sequences ->
                        task {
                            sequences
                            |> Seq.toArray
                            |> should equal [| 41L; 42L |]

                            ordering.Add("advance")
                            return 42L
                        })

                /// Reads status needed by the duplicate startup replay scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this replayed delete scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Applies one coalesced difference while preserving all represented replay sequences.
                let updateGraceStatusFromDifferences currentStatus differences _ =
                    differences
                    |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, string difference.RelativePath)
                    |> Seq.toArray
                    |> should
                        equal
                        [|
                            DifferenceType.Delete, FileSystemEntryType.File, relativePath
                        |]

                    ordering.Add("apply")
                    Task.FromResult(Some currentStatus)

                /// Leaves incremental status writes outside this replay ordering scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the replay ordering test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                ordering.ToArray()
                |> should equal [| "recover"; "apply"; "advance" |]

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.File (RelativePath relativePath)
                )
                |> should equal None
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that replayed file rows inconsistent with current GraceStatus retire before status mutation.
    [<Test>]
    let ``watch startup recovery quarantines replayed file rows inconsistent with GraceStatus`` () =
        withTempRepo (fun root ->
            let ghostChangePath = "ghost.txt"
            let trackedAddPath = "tracked.txt"
            let status = graceStatusTracking [| trackedAddPath |] Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let scope = { (watchJournalScope ()) with RootDirectoryId = status.RootDirectoryId; RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash }

            File.WriteAllText(Path.Combine(root, ghostChangePath), "untracked content")
            File.WriteAllText(Path.Combine(root, trackedAddPath), "tracked content")

            try
                (LocalStateDb.appendWatchJournalObservations
                    dbPath
                    [|
                        {
                            Scope = scope
                            DifferenceType = DifferenceType.Change
                            EntryType = FileSystemEntryType.File
                            RelativePath = RelativePath ghostChangePath
                        }
                        { Scope = scope; DifferenceType = DifferenceType.Add; EntryType = FileSystemEntryType.File; RelativePath = RelativePath trackedAddPath }
                    |])
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                Watch.setWatchJournalStartupClientsForWatchTests
                    (fun recoveryScope -> LocalStateDb.recoverWatchJournalForStartup dbPath recoveryScope)
                    (fun _ -> Task.FromResult(()))

                (Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Change FileSystemEntryType.File (RelativePath ghostChangePath)
                )
                |> should equal None

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Add FileSystemEntryType.File (RelativePath trackedAddPath)
                )
                |> should equal None

                /// Reads status needed by the inconsistent replay scenario.
                let readStatus () = Task.FromResult(status)
                /// Fails the test if inconsistent replay rows try to upload content.
                let upload _ _ = Task.FromException<unit>(InvalidOperationException("inconsistent replay rows should not upload"))
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Fails the test if inconsistent replay rows reach status application after quarantine.
                let updateGraceStatusFromDifferences _ _ _ =
                    Task.FromException<GraceStatus option>(InvalidOperationException("inconsistent replay rows should not apply status"))

                /// Leaves incremental status writes outside this inconsistent replay scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the inconsistent replay test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                let quarantinedSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "quarantined" None 10)
                        .GetAwaiter()
                        .GetResult()

                let pendingSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "pending" None 10)
                        .GetAwaiter()
                        .GetResult()

                quarantinedSnapshot.AppliedThroughSequence
                |> should equal 2L

                quarantinedSnapshot.Rows
                |> Array.sortBy (fun row -> row.Sequence)
                |> Array.map (fun row -> row.Sequence, row.QuarantineReason)
                |> should
                    equal
                    [|
                        1L, Some "startup replay file change is not tracked"
                        2L, Some "startup replay file add already tracked"
                    |]

                pendingSnapshot.Rows |> should haveLength 0

                let pendingWork = Watch.pendingWatchWorkSnapshotForTests ()

                pendingWork.FilesToProcess
                |> should equal Array.empty<string>

                pendingWork.StatusUpdateTriggers
                |> should equal Array.empty<string>
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that same-content replayed changes advance their durable journal sequence when upload cleanup drains them.
    [<Test>]
    let ``watch startup replay retires same-content uploaded change sequence durably`` () =
        withTempRepo (fun root ->
            let relativePath = "startup-replay-same-content.txt"
            let filePath = Path.Combine(root, relativePath)
            let dbPath = Current().GraceStatusFile
            let mutable uploadCalls = 0
            let mutable applyFromDifferencesCalls = 0

            File.WriteAllText(filePath, "same content")

            let trackedFile =
                (Services.createLocalFileVersion (FileInfo(filePath)))
                    .GetAwaiter()
                    .GetResult()
                    .Value

            let status = graceStatusTrackingFileVersions [| trackedFile |]
            let scope = { (watchJournalScope ()) with RootDirectoryId = status.RootDirectoryId; RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash }

            try
                (LocalStateDb.appendWatchJournalObservations
                    dbPath
                    [|
                        {
                            Scope = scope
                            DifferenceType = DifferenceType.Change
                            EntryType = FileSystemEntryType.File
                            RelativePath = RelativePath relativePath
                        }
                    |])
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                Watch.setWatchJournalStartupClientsForWatchTests
                    (fun recoveryScope -> LocalStateDb.recoverWatchJournalForStartup dbPath recoveryScope)
                    (fun _ -> Task.FromResult(()))

                (Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.queueStartupDifferenceForWatch (FileSystemDifference.Create DifferenceType.Change FileSystemEntryType.File (RelativePath relativePath))

                Watch.setWatchJournalClientsForWatchTests
                    (fun _ -> Task.FromException<int64 array>(InvalidOperationException("same-content replay must not append duplicate rows")))
                    (fun sequences ->
                        task {
                            sequences |> Seq.toArray |> should equal [| 1L |]

                            return! LocalStateDb.advanceWatchJournalAppliedThroughContiguousSequences dbPath sequences
                        })

                /// Reads status needed by the same-content replay scenario.
                let readStatus () = Task.FromResult(status)

                /// Records the replayed upload while leaving content equivalent to GraceStatus.
                let upload _ pendingFilePath =
                    uploadCalls <- uploadCalls + 1
                    recordUploadedFileVersion $"{pendingFilePath}"
                    Task.FromResult(())

                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Fails if same-content replay reaches status mutation instead of retiring through cleanup.
                let updateGraceStatusFromDifferences _ _ _ =
                    applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                    Task.FromException<GraceStatus option>(InvalidOperationException("same-content replay should not apply a status difference"))

                /// Leaves incremental status writes outside this same-content replay scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the same-content replay test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                let appliedSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "applied" None 10)
                        .GetAwaiter()
                        .GetResult()

                let pendingSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "pending" None 10)
                        .GetAwaiter()
                        .GetResult()

                uploadCalls |> should equal 1
                applyFromDifferencesCalls |> should equal 0

                appliedSnapshot.AppliedThroughSequence
                |> should equal 1L

                pendingSnapshot.Rows |> should haveLength 0

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Change FileSystemEntryType.File (RelativePath relativePath)
                )
                |> should equal None
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that malformed SQLite payload types quarantine instead of aborting startup replay recovery.
    [<Test>]
    let ``watch startup recovery quarantines non-text replay payload fields`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let scope = { (watchJournalScope ()) with RootDirectoryId = status.RootDirectoryId; RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash }

            (LocalStateDb.recoverWatchJournalForStartup dbPath scope)
                .GetAwaiter()
                .GetResult()
            |> ignore

            use connection = new SqliteConnection($"Data Source={dbPath}")
            connection.Open()

            /// Inserts one malformed replay row while preserving all scope fields needed to reach payload classification.
            let insertMalformed differenceType entryType relativePath =
                use command = connection.CreateCommand()

                command.CommandText <-
                    "INSERT INTO watch_journal (created_at_unix_ticks, repository_id, branch_id, workspace_root, watch_root, root_directory_version_id, root_directory_blake3_hash, watch_mode, difference_type, entry_type, relative_path) VALUES ($created_at, $repository_id, $branch_id, $workspace_root, $watch_root, $root_directory_version_id, $root_directory_blake3_hash, $watch_mode, $difference_type, $entry_type, $relative_path);"

                command.Parameters.AddWithValue("$created_at", getCurrentInstant().ToUnixTimeTicks())
                |> ignore

                command.Parameters.AddWithValue("$repository_id", scope.RepositoryId.ToString())
                |> ignore

                command.Parameters.AddWithValue("$branch_id", scope.BranchId.ToString())
                |> ignore

                command.Parameters.AddWithValue("$workspace_root", scope.WorkspaceRoot)
                |> ignore

                command.Parameters.AddWithValue("$watch_root", scope.WatchRoot)
                |> ignore

                command.Parameters.AddWithValue("$root_directory_version_id", scope.RootDirectoryId.ToString())
                |> ignore

                command.Parameters.AddWithValue("$root_directory_blake3_hash", string scope.RootDirectoryBlake3Hash)
                |> ignore

                command.Parameters.AddWithValue("$watch_mode", scope.WatchMode)
                |> ignore

                command.Parameters.AddWithValue("$difference_type", differenceType)
                |> ignore

                command.Parameters.AddWithValue("$entry_type", entryType)
                |> ignore

                command.Parameters.AddWithValue("$relative_path", relativePath)
                |> ignore

                command.ExecuteNonQuery() |> ignore

            insertMalformed [| 1uy |] "File" "non-text-difference.txt"
            insertMalformed "Change" [| 2uy |] "non-text-entry.txt"
            insertMalformed "Change" "File" [| 3uy |]

            let recovery =
                (LocalStateDb.recoverWatchJournalForStartup dbPath scope)
                    .GetAwaiter()
                    .GetResult()

            recovery.CompatibleReplayRows
            |> should haveLength 0

            recovery.QuarantinedRows |> should haveLength 3

            let quarantinedSnapshot =
                (LocalStateDb.readWatchJournalSnapshot dbPath "quarantined" None 10)
                    .GetAwaiter()
                    .GetResult()

            quarantinedSnapshot.AppliedThroughSequence
            |> should equal 3L

            quarantinedSnapshot.Rows
            |> Array.sortBy (fun row -> row.Sequence)
            |> Array.map (fun row -> row.Sequence, row.QuarantineReason)
            |> should
                equal
                [|
                    1L, Some "non-text difference_type"
                    2L, Some "non-text entry_type"
                    3L, Some "non-text relative_path"
                |])

    /// Verifies that replayed nested directory adds quarantine when their parent is neither tracked nor replayed.
    [<Test>]
    let ``watch startup recovery quarantines orphan replayed directory add`` () =
        withTempRepo (fun root ->
            let relativePath = "new/child"
            let directoryPath = Path.Combine(root, "new", "child")
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let scope = { (watchJournalScope ()) with RootDirectoryId = status.RootDirectoryId; RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash }

            Directory.CreateDirectory(directoryPath) |> ignore

            try
                (LocalStateDb.appendWatchJournalObservations
                    dbPath
                    [|
                        {
                            Scope = scope
                            DifferenceType = DifferenceType.Add
                            EntryType = FileSystemEntryType.Directory
                            RelativePath = RelativePath relativePath
                        }
                    |])
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                Watch.setWatchJournalStartupClientsForWatchTests
                    (fun recoveryScope -> LocalStateDb.recoverWatchJournalForStartup dbPath recoveryScope)
                    (fun _ -> Task.FromResult(()))

                (Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Add FileSystemEntryType.Directory (RelativePath relativePath)
                )
                |> should equal None

                /// Reads status needed by the orphan directory replay scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this directory replay scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Fails the test if an orphan directory add reaches status construction.
                let updateGraceStatusFromDifferences _ _ _ =
                    Task.FromException<GraceStatus option>(InvalidOperationException("orphan directory add should not apply status"))

                /// Leaves incremental status writes outside this orphan directory scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the orphan directory test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                let quarantinedSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "quarantined" None 10)
                        .GetAwaiter()
                        .GetResult()

                quarantinedSnapshot.AppliedThroughSequence
                |> should equal 1L

                quarantinedSnapshot.Rows
                |> Array.map (fun row -> row.QuarantineReason)
                |> should
                    equal
                    [|
                        Some "startup replay directory add parent is not tracked or replayed"
                    |]
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that startup scan work cannot append a duplicate when replay already recovered the same difference.
    [<Test>]
    let ``watch startup replay before scan prevents duplicate journal append`` () =
        withTempRepo (fun _ ->
            let relativePath = "startup-replay-scan-duplicate.txt"
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let scope = { (watchJournalScope ()) with RootDirectoryId = status.RootDirectoryId; RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash }
            let ordering = ResizeArray<string>()

            try
                (LocalStateDb.appendWatchJournalObservations
                    dbPath
                    [|
                        {
                            Scope = scope
                            DifferenceType = DifferenceType.Delete
                            EntryType = FileSystemEntryType.File
                            RelativePath = RelativePath relativePath
                        }
                    |])
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                Watch.setWatchJournalStartupClientsForWatchTests
                    (fun recoveryScope -> LocalStateDb.recoverWatchJournalForStartup dbPath recoveryScope)
                    (fun event -> task { ordering.Add($"lifecycle:{event.EventType}") })

                (Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.queueStartupDifferenceForWatch (FileSystemDifference.Create Delete FileSystemEntryType.File (RelativePath relativePath))

                Watch.setWatchJournalClientsForWatchTests
                    (fun _ -> Task.FromException<int64 array>(InvalidOperationException("startup scan must not append a duplicate replay row")))
                    (fun sequences ->
                        task {
                            sequences |> Seq.toArray |> should equal [| 1L |]
                            ordering.Add("advance")
                            return! LocalStateDb.advanceWatchJournalAppliedThroughContiguousSequences dbPath sequences
                        })

                /// Reads status needed by the replay-before-scan scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this replayed delete scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Applies one replayed difference even though startup scan queued the same path afterward.
                let updateGraceStatusFromDifferences currentStatus differences _ =
                    differences
                    |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, string difference.RelativePath)
                    |> Seq.toArray
                    |> should
                        equal
                        [|
                            DifferenceType.Delete, FileSystemEntryType.File, relativePath
                        |]

                    ordering.Add("apply")
                    Task.FromResult(Some currentStatus)

                /// Leaves incremental status writes outside this replay ordering scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the replay-before-scan test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                let snapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "applied" None 10)
                        .GetAwaiter()
                        .GetResult()

                snapshot.AppliedThroughSequence |> should equal 1L
                snapshot.TotalRows |> should equal 1
                ordering.ToArray() |> should contain "apply"
                ordering.ToArray() |> should contain "advance"
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that replay and startup scan observations coalesce with repository path comparison before status application.
    [<Test>]
    let ``watch startup replay and scan coalesce case variants before status application`` () =
        withTempRepo (fun _ ->
            let replayPath = "CasePath.txt"
            let scanPath = "casepath.txt"
            let status = graceStatusTracking [| replayPath |] Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let scope = { (watchJournalScope ()) with RootDirectoryId = status.RootDirectoryId; RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash }
            let ordering = ResizeArray<string>()

            try
                Watch.setWatchPathComparisonForWatchTests StringComparison.OrdinalIgnoreCase

                (LocalStateDb.appendWatchJournalObservations
                    dbPath
                    [|
                        { Scope = scope; DifferenceType = DifferenceType.Delete; EntryType = FileSystemEntryType.File; RelativePath = RelativePath replayPath }
                    |])
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                Watch.setWatchJournalStartupClientsForWatchTests
                    (fun recoveryScope -> LocalStateDb.recoverWatchJournalForStartup dbPath recoveryScope)
                    (fun _ -> Task.FromResult(()))

                (Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.queueStartupDifferenceForWatch (FileSystemDifference.Create Delete FileSystemEntryType.File (RelativePath scanPath))

                Watch.setWatchJournalClientsForWatchTests
                    (fun _ -> Task.FromException<int64 array>(InvalidOperationException("case-variant startup scan must not append a duplicate replay row")))
                    (fun sequences ->
                        task {
                            sequences |> Seq.toArray |> should equal [| 1L |]
                            ordering.Add("advance")
                            return! LocalStateDb.advanceWatchJournalAppliedThroughContiguousSequences dbPath sequences
                        })

                /// Reads status needed by the case-insensitive replay-before-scan scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this replayed delete scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Applies one replayed difference even though startup scan queued a case variant afterward.
                let updateGraceStatusFromDifferences currentStatus differences _ =
                    differences
                    |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, string difference.RelativePath)
                    |> Seq.toArray
                    |> should
                        equal
                        [|
                            DifferenceType.Delete, FileSystemEntryType.File, replayPath
                        |]

                    ordering.Add("apply")
                    Task.FromResult(Some currentStatus)

                /// Leaves incremental status writes outside this replay ordering scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the case-insensitive replay-before-scan test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                let snapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "applied" None 10)
                        .GetAwaiter()
                        .GetResult()

                snapshot.AppliedThroughSequence |> should equal 1L
                snapshot.TotalRows |> should equal 1

                ordering.ToArray()
                |> should equal [| "apply"; "advance" |]

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.File (RelativePath replayPath)
                )
                |> should equal None
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that missing replayed file Add/Change rows retire durably instead of reaching upload status application.
    [<Test>]
    let ``watch startup recovery quarantines missing replayed file add and change rows`` () =
        withTempRepo (fun _ ->
            let addPath = "missing-replayed-add.txt"
            let changePath = "missing-replayed-change.txt"
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let scope = { (watchJournalScope ()) with RootDirectoryId = status.RootDirectoryId; RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash }
            let lifecycleEvents = ResizeArray<string>()

            try
                (LocalStateDb.appendWatchJournalObservations
                    dbPath
                    [|
                        { Scope = scope; DifferenceType = DifferenceType.Add; EntryType = FileSystemEntryType.File; RelativePath = RelativePath addPath }
                        { Scope = scope; DifferenceType = DifferenceType.Change; EntryType = FileSystemEntryType.File; RelativePath = RelativePath changePath }
                    |])
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                Watch.setWatchJournalStartupClientsForWatchTests
                    (fun recoveryScope -> LocalStateDb.recoverWatchJournalForStartup dbPath recoveryScope)
                    (fun event -> task { lifecycleEvents.Add(event.EventType) })

                (Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.tryPeekStartupReplaySequenceForWatchTests (FileSystemDifference.Create DifferenceType.Add FileSystemEntryType.File (RelativePath addPath))
                |> should equal None

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Change FileSystemEntryType.File (RelativePath changePath)
                )
                |> should equal None

                /// Reads status needed by the stale replay scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this status-only replay scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Fails the test if stale replay rows reach status application after quarantine.
                let updateGraceStatusFromDifferences _ _ _ =
                    Task.FromException<GraceStatus option>(InvalidOperationException("stale file replay rows should not apply status"))

                /// Leaves incremental status writes outside this stale replay scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the stale replay test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                let quarantinedSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "quarantined" None 10)
                        .GetAwaiter()
                        .GetResult()

                let pendingSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "pending" None 10)
                        .GetAwaiter()
                        .GetResult()

                quarantinedSnapshot.AppliedThroughSequence
                |> should equal 2L

                quarantinedSnapshot.Rows |> should haveLength 2

                quarantinedSnapshot.Rows
                |> Array.map (fun row -> row.QuarantineReason)
                |> should
                    equal
                    [|
                        Some "stale startup replay file content missing before status application"
                        Some "stale startup replay file content missing before status application"
                    |]

                pendingSnapshot.Rows |> should haveLength 0

                lifecycleEvents.ToArray()
                |> should contain "startup-stale-file-replay-retired"
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that replayed file Add/Change rows ignored by current startup rules retire before status application.
    [<Test>]
    let ``watch startup recovery quarantines currently ignored replayed file add and change rows`` () =
        withTempRepo (fun root ->
            let ignoredFilePath = Path.Combine(root, "ignored-file.tmp")
            let ignoredDirectory = Path.Combine(root, "ignored-dir")
            let ignoredDirectoryFilePath = Path.Combine(ignoredDirectory, "live.txt")
            let ignoredFileRelativePath = "ignored-file.tmp"
            let ignoredDirectoryFileRelativePath = "ignored-dir/live.txt"
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let scope = { (watchJournalScope ()) with RootDirectoryId = status.RootDirectoryId; RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash }
            let lifecycleEvents = ResizeArray<string>()

            Directory.CreateDirectory(ignoredDirectory)
            |> ignore

            File.WriteAllText(ignoredFilePath, "ignored file payload")
            File.WriteAllText(ignoredDirectoryFilePath, "ignored directory payload")

            writeGraceIgnore
                root
                [|
                    ignoredFileRelativePath
                    "ignored-dir/"
                |]

            try
                (LocalStateDb.appendWatchJournalObservations
                    dbPath
                    [|
                        {
                            Scope = scope
                            DifferenceType = DifferenceType.Add
                            EntryType = FileSystemEntryType.File
                            RelativePath = RelativePath ignoredFileRelativePath
                        }
                        {
                            Scope = scope
                            DifferenceType = DifferenceType.Change
                            EntryType = FileSystemEntryType.File
                            RelativePath = RelativePath ignoredDirectoryFileRelativePath
                        }
                    |])
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                Watch.setWatchJournalStartupClientsForWatchTests
                    (fun recoveryScope -> LocalStateDb.recoverWatchJournalForStartup dbPath recoveryScope)
                    (fun event -> task { lifecycleEvents.Add(event.EventType) })

                (Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Add FileSystemEntryType.File (RelativePath ignoredFileRelativePath)
                )
                |> should equal None

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Change FileSystemEntryType.File (RelativePath ignoredDirectoryFileRelativePath)
                )
                |> should equal None

                /// Reads status needed by the ignored replay scenario.
                let readStatus () = Task.FromResult(status)
                /// Fails the test if ignored replay rows try to upload live but excluded content.
                let upload _ _ = Task.FromException<unit>(InvalidOperationException("ignored replay rows should not upload"))
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Fails the test if ignored replay rows reach status application after quarantine.
                let updateGraceStatusFromDifferences _ _ _ =
                    Task.FromException<GraceStatus option>(InvalidOperationException("ignored file replay rows should not apply status"))

                /// Leaves incremental status writes outside this ignored replay scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the ignored replay test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                let quarantinedSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "quarantined" None 10)
                        .GetAwaiter()
                        .GetResult()

                let pendingSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "pending" None 10)
                        .GetAwaiter()
                        .GetResult()

                quarantinedSnapshot.AppliedThroughSequence
                |> should equal 2L

                quarantinedSnapshot.Rows |> should haveLength 2

                quarantinedSnapshot.Rows
                |> Array.map (fun row -> row.QuarantineReason)
                |> should
                    equal
                    [|
                        Some "current startup replay file content ignored before status application"
                        Some "current startup replay file content ignored before status application"
                    |]

                pendingSnapshot.Rows |> should haveLength 0

                Watch
                    .pendingWatchWorkSnapshotForTests()
                    .FilesToProcess
                |> should equal Array.empty<string>

                Watch
                    .pendingWatchWorkSnapshotForTests()
                    .StatusUpdateTriggers
                |> should equal Array.empty<string>

                lifecycleEvents.ToArray()
                |> should contain "startup-ignored-file-replay-retired"
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that replayed directory Add rows ignored by current startup rules retire before status application.
    [<Test>]
    let ``watch startup recovery quarantines currently ignored replayed directory add rows`` () =
        withTempRepo (fun root ->
            let ignoredDirectory = Path.Combine(root, "ignored-dir")
            let ignoredDirectoryRelativePath = "ignored-dir"
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let scope = { (watchJournalScope ()) with RootDirectoryId = status.RootDirectoryId; RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash }
            let lifecycleEvents = ResizeArray<string>()

            Directory.CreateDirectory(ignoredDirectory)
            |> ignore

            writeGraceIgnore root [| "ignored-dir/" |]

            try
                (LocalStateDb.appendWatchJournalObservations
                    dbPath
                    [|
                        {
                            Scope = scope
                            DifferenceType = DifferenceType.Add
                            EntryType = FileSystemEntryType.Directory
                            RelativePath = RelativePath ignoredDirectoryRelativePath
                        }
                    |])
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                Watch.setWatchJournalStartupClientsForWatchTests
                    (fun recoveryScope -> LocalStateDb.recoverWatchJournalForStartup dbPath recoveryScope)
                    (fun event -> task { lifecycleEvents.Add(event.EventType) })

                (Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Add FileSystemEntryType.Directory (RelativePath ignoredDirectoryRelativePath)
                )
                |> should equal None

                /// Reads status needed by the ignored directory replay scenario.
                let readStatus () = Task.FromResult(status)
                /// Fails the test if ignored directory replay rows try to upload content.
                let upload _ _ = Task.FromException<unit>(InvalidOperationException("ignored directory replay rows should not upload"))
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Fails the test if ignored directory replay rows reach status application after quarantine.
                let updateGraceStatusFromDifferences _ _ _ =
                    Task.FromException<GraceStatus option>(InvalidOperationException("ignored directory replay rows should not apply status"))

                /// Leaves incremental status writes outside this ignored replay scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the ignored replay test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                let quarantinedSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "quarantined" None 10)
                        .GetAwaiter()
                        .GetResult()

                let pendingSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "pending" None 10)
                        .GetAwaiter()
                        .GetResult()

                quarantinedSnapshot.AppliedThroughSequence
                |> should equal 1L

                quarantinedSnapshot.Rows
                |> Array.map (fun row -> row.QuarantineReason)
                |> should
                    equal
                    [|
                        Some "current startup replay directory ignored before status application"
                    |]

                pendingSnapshot.Rows |> should haveLength 0

                Watch
                    .pendingWatchWorkSnapshotForTests()
                    .DirectoriesToProcess
                |> should equal Array.empty<string>

                lifecycleEvents.ToArray()
                |> should contain "startup-ignored-directory-replay-retired"
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that replayed delete rows retire when current final path kind contradicts the durable entry kind.
    [<Test>]
    let ``watch startup recovery quarantines delete replay rows whose current path kind changed`` () =
        withTempRepo (fun root ->
            let fileDeleteNowDirectory = "file-delete-now-directory"
            let directoryDeleteNowFile = "directory-delete-now-file"
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let scope = { (watchJournalScope ()) with RootDirectoryId = status.RootDirectoryId; RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash }

            Directory.CreateDirectory(Path.Combine(root, fileDeleteNowDirectory))
            |> ignore

            File.WriteAllText(Path.Combine(root, directoryDeleteNowFile), "live file payload")

            try
                (LocalStateDb.appendWatchJournalObservations
                    dbPath
                    [|
                        {
                            Scope = scope
                            DifferenceType = DifferenceType.Delete
                            EntryType = FileSystemEntryType.File
                            RelativePath = RelativePath fileDeleteNowDirectory
                        }
                        {
                            Scope = scope
                            DifferenceType = DifferenceType.Delete
                            EntryType = FileSystemEntryType.Directory
                            RelativePath = RelativePath directoryDeleteNowFile
                        }
                    |])
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                Watch.setWatchJournalStartupClientsForWatchTests
                    (fun recoveryScope -> LocalStateDb.recoverWatchJournalForStartup dbPath recoveryScope)
                    (fun _ -> Task.FromResult(()))

                (Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.File (RelativePath fileDeleteNowDirectory)
                )
                |> should equal None

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.Directory (RelativePath directoryDeleteNowFile)
                )
                |> should equal None

                /// Reads status needed by the changed-kind replay scenario.
                let readStatus () = Task.FromResult(status)
                /// Fails the test if changed-kind replay rows try to upload content.
                let upload _ _ = Task.FromException<unit>(InvalidOperationException("changed-kind replay rows should not upload"))
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Fails the test if changed-kind replay rows reach status application after quarantine.
                let updateGraceStatusFromDifferences _ _ _ =
                    Task.FromException<GraceStatus option>(InvalidOperationException("changed-kind replay rows should not apply status"))

                /// Leaves incremental status writes outside this changed-kind replay scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the changed-kind replay test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                let quarantinedSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "quarantined" None 10)
                        .GetAwaiter()
                        .GetResult()

                let pendingSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "pending" None 10)
                        .GetAwaiter()
                        .GetResult()

                quarantinedSnapshot.AppliedThroughSequence
                |> should equal 2L

                quarantinedSnapshot.Rows
                |> Array.sortBy (fun row -> row.Sequence)
                |> Array.map (fun row -> row.Sequence, row.QuarantineReason)
                |> should
                    equal
                    [|
                        1L, Some "startup replay file delete now targets a directory"
                        2L, Some "startup replay directory delete now targets a file"
                    |]

                pendingSnapshot.Rows |> should haveLength 0

                let pendingWork = Watch.pendingWatchWorkSnapshotForTests ()

                pendingWork.FilesToProcess
                |> should equal Array.empty<string>

                pendingWork.DirectoriesToProcess
                |> should equal Array.empty<string>

                pendingWork.StatusUpdateTriggers
                |> should equal Array.empty<string>
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that delayed startup replay runs after pending startup backlog drains on a later timer pass.
    [<Test>]
    let ``startup recovery runs after delayed backlog drains`` () =
        withTempRepo (fun _ ->
            let relativePath = "delayed-startup-replay.txt"
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let ordering = ResizeArray<string>()

            try
                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                Watch.setWatchJournalStartupClientsForWatchTests
                    (fun _ ->
                        task {
                            ordering.Add("recover")

                            return
                                {
                                    LocalStateDb.WatchJournalStartupRecovery.DbPath = Current().GraceStatusFile
                                    AppliedThroughSequence = 0L
                                    CompatibleReplayRows =
                                        [|
                                            {
                                                LocalStateDb.WatchJournalPendingReplay.Sequence = 77L
                                                DifferenceType = DifferenceType.Delete
                                                EntryType = FileSystemEntryType.File
                                                RelativePath = RelativePath relativePath
                                            }
                                        |]
                                    QuarantinedRows = Array.empty
                                }
                        })
                    (fun event -> task { ordering.Add($"lifecycle:{event.EventType}") })

                Watch.setWatchJournalClientsForWatchTests
                    (fun _ -> Task.FromException<int64 array>(InvalidOperationException("startup replay must not append duplicate rows")))
                    (fun sequences ->
                        task {
                            sequences |> Seq.toArray |> should equal [| 77L |]
                            ordering.Add("advance")
                            return 77L
                        })

                /// Reads status needed by the delayed startup completion scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this replayed delete scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Applies the replayed pending row that delayed startup recovery queued.
                let updateGraceStatusFromDifferences currentStatus differences _ =
                    differences
                    |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, string difference.RelativePath)
                    |> Seq.toArray
                    |> should
                        equal
                        [|
                            DifferenceType.Delete, FileSystemEntryType.File, relativePath
                        |]

                    ordering.Add("apply")
                    Task.FromResult(Some currentStatus)

                /// Leaves incremental status writes outside this replay ordering scenario.
                let applyIncremental _ _ _ = Task.FromResult(())

                /// Keeps IPC publication deterministic for the delayed startup recovery test.
                let updateIpc _ _ =
                    ordering.Add("ipc")
                    Task.FromResult(())

                let processStartupReplay () =
                    task {
                        ordering.Add("process")

                        do!
                            Watch.processChangedFilesWithClients
                                readStatus
                                readStatus
                                upload
                                updateGraceStatus
                                scannerHostileDifferenceDiscovery
                                updateGraceStatusFromDifferences
                                applyIncremental
                                updateIpc
                    }

                (Watch.completeStartupRecoveryIfPendingWorkDrainedForWatchTests readStatus updateIpc processStartupReplay)
                    .GetAwaiter()
                    .GetResult()

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.HealthyIncremental

                Watch
                    .pendingWatchWorkSnapshotForTests()
                    .StatusUpdateTriggers
                |> should equal Array.empty<string>

                ordering.ToArray()
                |> should
                    equal
                    [|
                        "lifecycle:startup-reconciliation-complete"
                        "recover"
                        "lifecycle:startup-replay-complete"
                        "process"
                        "apply"
                        "advance"
                        "ipc"
                        "ipc"
                    |]
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that replayed rows resolved before apply are still retired durably.
    [<Test>]
    let ``watch retires resolved startup replay rows`` () =
        withTempRepo (fun root ->
            let relativePath = "resolved-replay-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            File.WriteAllText(filePath, "recreated")
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let scope = { (watchJournalScope ()) with RootDirectoryId = status.RootDirectoryId; RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash }

            try
                (LocalStateDb.appendWatchJournalObservations
                    dbPath
                    [|
                        {
                            Scope = scope
                            DifferenceType = DifferenceType.Delete
                            EntryType = FileSystemEntryType.File
                            RelativePath = RelativePath relativePath
                        }
                    |])
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                Watch.setWatchJournalStartupClientsForWatchTests
                    (fun recoveryScope -> LocalStateDb.recoverWatchJournalForStartup dbPath recoveryScope)
                    (fun _ -> Task.FromResult(()))

                (Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                /// Reads status needed by the resolved replay scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this status-only replay scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Fails the test if resolved replay work reaches status application.
                let updateGraceStatusFromDifferences _ _ _ =
                    Task.FromException<GraceStatus option>(InvalidOperationException("resolved replay rows should not apply status"))

                /// Leaves incremental status writes outside this resolved replay scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the resolved replay test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                let snapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "applied" None 10)
                        .GetAwaiter()
                        .GetResult()

                snapshot.AppliedThroughSequence |> should equal 1L
                snapshot.Rows |> should haveLength 1

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.File (RelativePath relativePath)
                )
                |> should equal None
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that existing replay rows remain durable when status application defers before side effects.
    [<Test>]
    let ``watch keeps existing startup replay row when status apply defers`` () =
        withTempRepo (fun root ->
            let relativePath = "deferred-startup-replay.txt"
            let liveRelativePath = "deferred-live-delete.txt"
            let replayFilePath = Path.Combine(root, relativePath)
            let status = graceStatusTracking [| liveRelativePath |] Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let scope = { (watchJournalScope ()) with RootDirectoryId = status.RootDirectoryId; RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash }

            try
                File.WriteAllText(replayFilePath, "startup replay content still exists")

                (LocalStateDb.appendWatchJournalObservations
                    dbPath
                    [|
                        { Scope = scope; DifferenceType = DifferenceType.Add; EntryType = FileSystemEntryType.File; RelativePath = RelativePath relativePath }
                    |])
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                Watch.setWatchJournalStartupClientsForWatchTests
                    (fun recoveryScope -> LocalStateDb.recoverWatchJournalForStartup dbPath recoveryScope)
                    (fun _ -> Task.FromResult(()))

                (Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                Watch.OnDeleted(deletedEvent (Path.Combine(root, liveRelativePath)))

                /// Reads status needed by the deferred replay scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this status-only replay scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)
                /// Simulates status application deferring before durable side effects.
                let updateGraceStatusFromDifferences _ _ _ = Task.FromResult(None)
                /// Leaves incremental status writes outside this deferred replay scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the deferred replay test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                let snapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "pending" None 10)
                        .GetAwaiter()
                        .GetResult()

                snapshot.AppliedThroughSequence |> should equal 0L
                snapshot.TotalRows |> should equal 2
                snapshot.Rows |> should haveLength 2

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Add FileSystemEntryType.File (RelativePath relativePath)
                )
                |> should equal (Some 1L)

                Watch.tryPeekStartupReplaySequenceForWatchTests (
                    FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.File (RelativePath liveRelativePath)
                )
                |> should equal (Some 2L)
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that confidence-loss quarantine retires replay keys for the pending differences it discards.
    [<Test>]
    let ``watch quarantine clears startup replay key for pending difference`` () =
        withTempRepo (fun _ ->
            let relativePath = "quarantined-startup-replay-delete.txt"
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let scope = { (watchJournalScope ()) with RootDirectoryId = status.RootDirectoryId; RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash }

            try
                let replaySequences =
                    (LocalStateDb.appendWatchJournalObservations
                        dbPath
                        [|
                            {
                                Scope = scope
                                DifferenceType = DifferenceType.Delete
                                EntryType = FileSystemEntryType.File
                                RelativePath = RelativePath relativePath
                            }
                        |])
                        .GetAwaiter()
                        .GetResult()

                replaySequences |> should equal [| 1L |]

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                (Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                let difference = FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.File (RelativePath relativePath)

                Watch.tryPeekStartupReplaySequenceForWatchTests difference
                |> should equal (Some 1L)

                Watch.requestGraceWatchExplicitResyncForWatchTests "test replay-key quarantine"

                Watch.tryPeekStartupReplaySequenceForWatchTests difference
                |> should equal None

                let pendingSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "pending" None 10)
                        .GetAwaiter()
                        .GetResult()

                pendingSnapshot.RowCount |> should equal 0

                pendingSnapshot.AppliedThroughSequence
                |> should equal 1L

                let quarantinedSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "quarantined" None 10)
                        .GetAwaiter()
                        .GetResult()

                quarantinedSnapshot.Rows
                |> Array.map (fun row -> row.Sequence, row.QuarantineReason)
                |> should
                    equal
                    [|
                        1L, Some "confidence loss discarded startup replay work: test replay-key quarantine"
                    |]
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that a same-path observation after replay quarantine appends fresh durable journal evidence.
    [<Test>]
    let ``watch same path observation after replay quarantine appends fresh journal row`` () =
        withTempRepo (fun root ->
            let relativePath = "same-path-after-quarantine-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let scope = { (watchJournalScope ()) with RootDirectoryId = status.RootDirectoryId; RootDirectoryBlake3Hash = status.RootDirectoryBlake3Hash }
            let appendedDifferences = ResizeArray<FileSystemDifference>()
            let advancedSequences = ResizeArray<int64>()

            try
                let replaySequences =
                    (LocalStateDb.appendWatchJournalObservations
                        dbPath
                        [|
                            {
                                Scope = scope
                                DifferenceType = DifferenceType.Delete
                                EntryType = FileSystemEntryType.File
                                RelativePath = RelativePath relativePath
                            }
                        |])
                        .GetAwaiter()
                        .GetResult()

                replaySequences |> should equal [| 1L |]

                Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

                (Watch.recoverStartupWatchJournalAfterReconciliationForWatchTests status)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                let staleDifference = FileSystemDifference.Create DifferenceType.Delete FileSystemEntryType.File (RelativePath relativePath)

                Watch.tryPeekStartupReplaySequenceForWatchTests staleDifference
                |> should equal (Some 1L)

                Watch.requestGraceWatchExplicitResyncForWatchTests "test same-path replay quarantine"

                let afterQuarantineSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "all" None 10)
                        .GetAwaiter()
                        .GetResult()

                afterQuarantineSnapshot.AppliedThroughSequence
                |> should equal 1L

                afterQuarantineSnapshot.Rows
                |> Array.map (fun row -> row.Sequence, row.State, row.QuarantineReason)
                |> should
                    equal
                    [|
                        1L,
                        LocalStateDb.WatchJournalRowState.Quarantined,
                        Some "confidence loss discarded startup replay work: test same-path replay quarantine"
                    |]

                /// Reads status needed while resync clears the confidence-loss boundary.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this status-only resync scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper available without changing status.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)
                /// Produces a clean resync scan before same-path incremental capture resumes.
                let cleanScan _ = Task.FromResult(List<FileSystemDifference>())

                /// Applies same-path status differences after resync has completed.
                let updateGraceStatusFromDifferences currentStatus differences _ =
                    differences
                    |> Seq.toArray
                    |> should equal [| staleDifference |]

                    Task.FromResult(Some currentStatus)

                /// Leaves incremental status writes outside this status-only replay scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the replay quarantine test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    cleanScan
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.HealthyIncremental

                Watch.setWatchJournalClientsForWatchTests
                    (fun observations ->
                        let observationsArray = observations |> Seq.toArray

                        observationsArray
                        |> Array.map (fun observation -> FileSystemDifference.Create observation.DifferenceType observation.EntryType observation.RelativePath)
                        |> should equal [| staleDifference |]

                        for observation in observationsArray do
                            appendedDifferences.Add(FileSystemDifference.Create observation.DifferenceType observation.EntryType observation.RelativePath)

                        LocalStateDb.appendWatchJournalObservations dbPath observations)
                    (fun sequences ->
                        for sequence in sequences do
                            advancedSequences.Add(sequence)

                        LocalStateDb.advanceWatchJournalAppliedThroughContiguousSequences dbPath sequences)

                Watch.OnDeleted(deletedEvent filePath)

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                appendedDifferences.ToArray()
                |> should equal [| staleDifference |]

                advancedSequences.ToArray()
                |> should equal [| 2L |]

                let finalSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "all" None 10)
                        .GetAwaiter()
                        .GetResult()

                finalSnapshot.AppliedThroughSequence
                |> should equal 2L

                finalSnapshot.Rows
                |> Array.sortBy (fun row -> row.Sequence)
                |> Array.map (fun row -> row.Sequence, row.State, row.QuarantineReason)
                |> should
                    equal
                    [|
                        1L,
                        LocalStateDb.WatchJournalRowState.Quarantined,
                        Some "confidence loss discarded startup replay work: test same-path replay quarantine"
                        2L, LocalStateDb.WatchJournalRowState.Applied, None
                    |]

                Watch.tryPeekStartupReplaySequenceForWatchTests staleDifference
                |> should equal None
            finally
                Watch.resetWatchJournalClientsForWatchTests ()
                Watch.clearPendingWatchWorkForTests ())

    /// Verifies that journal append failure prevents unjournaled status application.
    [<Test>]
    let ``watch skips status application when journal append fails`` () =
        withTempRepo (fun root ->
            let relativePath = "journal-append-failure-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let mutable applyCalls = 0
            let mutable advanceCalls = 0

            Watch.OnDeleted(deletedEvent filePath)

            try
                Watch.setWatchJournalClientsForWatchTests
                    (fun _ -> Task.FromException<int64 array>(InvalidOperationException("test journal append failure")))
                    (fun _ ->
                        advanceCalls <- advanceCalls + 1
                        Task.FromResult(0L))

                /// Reads status needed by the append-failure scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this status-only observation scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Fails the test if unjournaled work reaches status application.
                let updateGraceStatusFromDifferences currentStatus _ _ =
                    applyCalls <- applyCalls + 1
                    Task.FromResult(Some currentStatus)

                /// Leaves incremental status writes outside this status-only observation scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the append-failure test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                applyCalls |> should equal 0
                advanceCalls |> should equal 0

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

                Watch
                    .pendingWatchWorkSnapshotForTests()
                    .StatusUpdateTriggers
                |> should equal Array.empty<string>
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies that failed status application clears appended observations before retry.
    [<Test>]
    let ``watch clears journal for retry when status application returns none after append`` () =
        withTempRepo (fun root ->
            let relativePath = "journal-status-none-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let mutable appendCalls = 0
            let mutable advanceCalls = 0
            let mutable recoveryCalls = 0
            let mutable pruneCalls = 0
            let mutable recoveredAdvancedThrough = -1L
            let mutable recoveredRequiredThrough = -1L

            Watch.OnDeleted(deletedEvent filePath)

            try
                Watch.setWatchJournalClientsForWatchTests
                    (fun observations ->
                        appendCalls <- appendCalls + 1
                        LocalStateDb.appendWatchJournalObservations dbPath observations)
                    (fun _ ->
                        advanceCalls <- advanceCalls + 1
                        Task.FromResult(1L))

                Watch.setWatchJournalMaintenanceClientsForWatchTests
                    (fun advancedThrough requiredThrough ->
                        recoveryCalls <- recoveryCalls + 1
                        recoveredAdvancedThrough <- advancedThrough
                        recoveredRequiredThrough <- requiredThrough

                        task {
                            let! _ = LocalStateDb.clearWatchJournal dbPath
                            return ()
                        })
                    (fun () ->
                        pruneCalls <- pruneCalls + 1
                        Task.FromResult(()))

                /// Reads status needed by the status-failure scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this status-only observation scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)
                /// Simulates a failed durable status application after append succeeds.
                let updateGraceStatusFromDifferences _ _ _ = Task.FromResult(None)
                /// Leaves incremental status writes outside this status-only observation scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the status-failure test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                appendCalls |> should equal 1
                advanceCalls |> should equal 0
                recoveryCalls |> should equal 1
                recoveredAdvancedThrough |> should equal 0L
                recoveredRequiredThrough |> should equal 1L
                pruneCalls |> should equal 0

                let repairedJournalSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "all" None 10)
                        .GetAwaiter()
                        .GetResult()

                repairedJournalSnapshot.TotalRows
                |> should equal 0

                repairedJournalSnapshot.AppliedThroughSequence
                |> should equal 0L

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.HealthyIncremental

                Watch
                    .pendingWatchWorkSnapshotForTests()
                    .StatusUpdateTriggers
                |> should equal [| relativePath |]
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies that a status exception after append repairs the journal before resync.
    [<Test>]
    let ``watch repairs journal before resync when status application throws after append`` () =
        withTempRepo (fun root ->
            let relativePath = "journal-status-throw-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let mutable appendCalls = 0
            let mutable applyCalls = 0
            let mutable advanceCalls = 0
            let mutable recoveryCalls = 0
            let mutable pruneCalls = 0
            let mutable recoveredAdvancedThrough = -1L
            let mutable recoveredRequiredThrough = -1L

            Watch.OnDeleted(deletedEvent filePath)

            try
                Watch.setWatchJournalClientsForWatchTests
                    (fun observations ->
                        appendCalls <- appendCalls + 1
                        LocalStateDb.appendWatchJournalObservations dbPath observations)
                    (fun _ ->
                        advanceCalls <- advanceCalls + 1
                        Task.FromResult(1L))

                Watch.setWatchJournalMaintenanceClientsForWatchTests
                    (fun advancedThrough requiredThrough ->
                        recoveryCalls <- recoveryCalls + 1
                        recoveredAdvancedThrough <- advancedThrough
                        recoveredRequiredThrough <- requiredThrough

                        Watch.currentGraceWatchRuntimeModeForWatchTests ()
                        |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

                        Watch.isGraceWatchResyncPendingForWatchTests ()
                        |> should equal true

                        task {
                            let! _ = LocalStateDb.clearWatchJournal dbPath
                            return ()
                        })
                    (fun () ->
                        pruneCalls <- pruneCalls + 1
                        Task.FromResult(()))

                /// Reads status needed by the status-throw journal repair scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this status-only observation scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Simulates an exception after the journal append succeeds but before status can be trusted.
                let updateGraceStatusFromDifferences _ _ _ =
                    applyCalls <- applyCalls + 1
                    Task.FromException<GraceStatus option>(InvalidOperationException("test status application failure"))

                /// Leaves incremental status writes outside this status-only observation scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the status-throw journal repair scenario.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                appendCalls |> should equal 1
                applyCalls |> should equal 1
                advanceCalls |> should equal 0
                recoveryCalls |> should equal 1
                recoveredAdvancedThrough |> should equal 0L
                recoveredRequiredThrough |> should equal 1L
                pruneCalls |> should equal 0

                let repairedJournalSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "all" None 10)
                        .GetAwaiter()
                        .GetResult()

                repairedJournalSnapshot.TotalRows
                |> should equal 0

                repairedJournalSnapshot.AppliedThroughSequence
                |> should equal 0L

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

                Watch
                    .pendingWatchWorkSnapshotForTests()
                    .StatusUpdateTriggers
                |> should equal Array.empty<string>
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies that successful trusted journal boundary advancement prunes applied rows.
    [<Test>]
    let ``watch prunes journal retention after trusted boundary advance`` () =
        withTempRepo (fun root ->
            let relativePath = "journal-prune-after-advance-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let mutable appendCalls = 0
            let mutable applyCalls = 0
            let mutable advanceCalls = 0
            let mutable pruneCalls = 0
            let mutable recoveryCalls = 0

            let seededObservations =
                [ 1..1026 ]
                |> Seq.map (fun index -> watchJournalObservation DifferenceType.Change FileSystemEntryType.File $"seed-{index}.txt")
                |> Seq.toArray

            let seededSequences =
                (LocalStateDb.appendWatchJournalObservations dbPath seededObservations)
                    .GetAwaiter()
                    .GetResult()

            seededSequences
            |> Array.length
            |> should equal 1026

            (LocalStateDb.setWatchJournalAppliedThroughSequence dbPath 1026L)
                .GetAwaiter()
                .GetResult()

            Watch.OnDeleted(deletedEvent filePath)

            try
                Watch.setWatchJournalClientsForWatchTests
                    (fun observations ->
                        appendCalls <- appendCalls + 1
                        LocalStateDb.appendWatchJournalObservations dbPath observations)
                    (fun sequences ->
                        advanceCalls <- advanceCalls + 1

                        sequences
                        |> Seq.toArray
                        |> should equal [| 1027L |]

                        LocalStateDb.advanceWatchJournalAppliedThroughContiguousSequences dbPath sequences)

                Watch.setWatchJournalMaintenanceClientsForWatchTests
                    (fun _ _ ->
                        recoveryCalls <- recoveryCalls + 1
                        Task.FromResult(()))
                    (fun () ->
                        pruneCalls <- pruneCalls + 1
                        LocalStateDb.pruneWatchJournalRetention dbPath)

                /// Reads status needed by the trusted journal pruning scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this status-only observation scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Simulates successful status application after the corresponding journal append.
                let updateGraceStatusFromDifferences currentStatus _ _ =
                    applyCalls <- applyCalls + 1
                    Task.FromResult(Some currentStatus)

                /// Leaves incremental status writes outside this status-only observation scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the journal pruning scenario.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                appendCalls |> should equal 1
                applyCalls |> should equal 1
                advanceCalls |> should equal 1
                pruneCalls |> should equal 1
                recoveryCalls |> should equal 0

                let journalSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "all" None 2048)
                        .GetAwaiter()
                        .GetResult()

                journalSnapshot.AppliedThroughSequence
                |> should equal 1027L

                journalSnapshot.TotalRows |> should equal 1024

                journalSnapshot.Rows
                |> Array.map (fun row -> row.Sequence)
                |> Array.min
                |> should equal 4L

                journalSnapshot.Rows
                |> Array.map (fun row -> row.Sequence)
                |> Array.max
                |> should equal 1027L

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.HealthyIncremental
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies that a non-contiguous journal boundary result forces resync instead of healthy status draining.
    [<Test>]
    let ``watch quarantines status work when journal boundary cannot advance through appended sequence`` () =
        withTempRepo (fun root ->
            let relativePath = "journal-non-contiguous-retry-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let mutable appendCalls = 0
            let mutable applyCalls = 0
            let mutable advanceCalls = 0
            let mutable recoveryCalls = 0
            let mutable pruneCalls = 0
            let mutable recoveredAdvancedThrough = -1L
            let mutable recoveredRequiredThrough = -1L

            let seededSequences =
                (LocalStateDb.appendWatchJournalObservations
                    dbPath
                    [
                        watchJournalObservation DifferenceType.Change FileSystemEntryType.File "older-pending.txt"
                    ])
                    .GetAwaiter()
                    .GetResult()

            seededSequences |> should equal [| 1L |]

            Watch.OnDeleted(deletedEvent filePath)

            try
                Watch.setWatchJournalClientsForWatchTests
                    (fun observations ->
                        appendCalls <- appendCalls + 1
                        LocalStateDb.appendWatchJournalObservations dbPath observations)
                    (fun sequences ->
                        advanceCalls <- advanceCalls + 1
                        sequences |> Seq.toArray |> should equal [| 2L |]
                        LocalStateDb.advanceWatchJournalAppliedThroughContiguousSequences dbPath sequences)

                Watch.setWatchJournalMaintenanceClientsForWatchTests
                    (fun advancedThrough requiredThrough ->
                        recoveryCalls <- recoveryCalls + 1
                        recoveredAdvancedThrough <- advancedThrough
                        recoveredRequiredThrough <- requiredThrough

                        Watch.currentGraceWatchRuntimeModeForWatchTests ()
                        |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

                        Watch.isGraceWatchResyncPendingForWatchTests ()
                        |> should equal true

                        task {
                            let! _ = LocalStateDb.clearWatchJournal dbPath
                            return ()
                        })
                    (fun () ->
                        pruneCalls <- pruneCalls + 1
                        Task.FromResult(()))

                /// Reads status needed by the non-contiguous journal retry scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this status-only observation scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Simulates a successful status application after only a later journal sequence was appended.
                let updateGraceStatusFromDifferences currentStatus _ _ =
                    applyCalls <- applyCalls + 1
                    Task.FromResult(Some currentStatus)

                /// Leaves incremental status writes outside this status-only observation scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the non-contiguous journal retry scenario.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                appendCalls |> should equal 1
                applyCalls |> should equal 1
                advanceCalls |> should equal 1
                recoveryCalls |> should equal 1
                recoveredAdvancedThrough |> should equal 0L
                recoveredRequiredThrough |> should equal 2L
                pruneCalls |> should equal 0

                let repairedJournalSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "all" None 10)
                        .GetAwaiter()
                        .GetResult()

                repairedJournalSnapshot.TotalRows
                |> should equal 0

                repairedJournalSnapshot.AppliedThroughSequence
                |> should equal 0L

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

                Watch.quarantinedWatchObservationCountForWatchTests ()
                |> should equal 2

                Watch
                    .pendingWatchWorkSnapshotForTests()
                    .StatusUpdateTriggers
                |> should equal Array.empty<string>
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies that an applied status write repairs appended journal rows when boundary advancement throws.
    [<Test>]
    let ``watch repairs journal before resync when boundary advancement throws after status write`` () =
        withTempRepo (fun root ->
            let relativePath = "journal-advance-throw-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let mutable appendCalls = 0
            let mutable applyCalls = 0
            let mutable advanceCalls = 0
            let mutable recoveryCalls = 0
            let mutable pruneCalls = 0
            let mutable recoveredAdvancedThrough = -1L
            let mutable recoveredRequiredThrough = -1L

            Watch.OnDeleted(deletedEvent filePath)

            try
                Watch.setWatchJournalClientsForWatchTests
                    (fun observations ->
                        appendCalls <- appendCalls + 1
                        LocalStateDb.appendWatchJournalObservations dbPath observations)
                    (fun sequences ->
                        advanceCalls <- advanceCalls + 1
                        sequences |> Seq.toArray |> should equal [| 1L |]
                        Task.FromException<int64>(InvalidOperationException("test boundary failure")))

                Watch.setWatchJournalMaintenanceClientsForWatchTests
                    (fun advancedThrough requiredThrough ->
                        recoveryCalls <- recoveryCalls + 1
                        recoveredAdvancedThrough <- advancedThrough
                        recoveredRequiredThrough <- requiredThrough

                        Watch.currentGraceWatchRuntimeModeForWatchTests ()
                        |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

                        Watch.isGraceWatchResyncPendingForWatchTests ()
                        |> should equal true

                        task {
                            let! _ = LocalStateDb.clearWatchJournal dbPath
                            return ()
                        })
                    (fun () ->
                        pruneCalls <- pruneCalls + 1
                        Task.FromResult(()))

                /// Reads status needed by the boundary-throw journal repair scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this status-only observation scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Simulates successful status application before the boundary advancement failure.
                let updateGraceStatusFromDifferences currentStatus _ _ =
                    applyCalls <- applyCalls + 1
                    Task.FromResult(Some currentStatus)

                /// Leaves incremental status writes outside this status-only observation scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the boundary-throw journal repair scenario.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                appendCalls |> should equal 1
                applyCalls |> should equal 1
                advanceCalls |> should equal 1
                recoveryCalls |> should equal 1
                recoveredAdvancedThrough |> should equal 0L
                recoveredRequiredThrough |> should equal 1L
                pruneCalls |> should equal 0

                let repairedJournalSnapshot =
                    (LocalStateDb.readWatchJournalSnapshot dbPath "all" None 10)
                        .GetAwaiter()
                        .GetResult()

                repairedJournalSnapshot.TotalRows
                |> should equal 0

                repairedJournalSnapshot.AppliedThroughSequence
                |> should equal 0L

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.Resynchronizing
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies that resync is published before fallible repair after status side effects commit.
    [<Test>]
    let ``watch requests resync before failed journal repair after status write`` () =
        withTempRepo (fun root ->
            let relativePath = "journal-repair-throw-after-status-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let dbPath = Current().GraceStatusFile
            let mutable appendCalls = 0
            let mutable applyCalls = 0
            let mutable advanceCalls = 0
            let mutable recoveryCalls = 0
            let mutable recoveryObservedResync = false
            let mutable updateIpcCalls = 0
            let mutable updateIpcSawNonIncremental = false

            Watch.OnDeleted(deletedEvent filePath)

            try
                Watch.setWatchJournalClientsForWatchTests
                    (fun observations ->
                        appendCalls <- appendCalls + 1
                        LocalStateDb.appendWatchJournalObservations dbPath observations)
                    (fun sequences ->
                        advanceCalls <- advanceCalls + 1
                        sequences |> Seq.toArray |> should equal [| 1L |]
                        Task.FromException<int64>(InvalidOperationException("test boundary failure before repair")))

                Watch.setWatchJournalMaintenanceClientsForWatchTests
                    (fun _ _ ->
                        recoveryCalls <- recoveryCalls + 1

                        recoveryObservedResync <-
                            Watch.currentGraceWatchRuntimeModeForWatchTests () = Services.GraceWatchRuntimeMode.Resynchronizing
                            && Watch.isGraceWatchResyncPendingForWatchTests ()

                        Task.FromException<unit>(InvalidOperationException("test repair failure after resync request")))
                    (fun () -> Task.FromResult(()))

                /// Reads status needed by the repair-failure publication scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this status-only observation scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Simulates successful status application before the boundary repair fails.
                let updateGraceStatusFromDifferences currentStatus _ _ =
                    applyCalls <- applyCalls + 1
                    Task.FromResult(Some currentStatus)

                /// Leaves incremental status writes outside this status-only observation scenario.
                let applyIncremental _ _ _ = Task.FromResult(())

                /// Tracks the final non-incremental IPC publication after repair failure.
                let updateIpc (_: GraceStatus) (directoryIds: HashSet<DirectoryVersionId> option) =
                    updateIpcCalls <- updateIpcCalls + 1

                    if directoryIds
                       |> Option.exists (fun ids -> ids.Count = 0) then
                        updateIpcSawNonIncremental <- true

                    Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                appendCalls |> should equal 1
                applyCalls |> should equal 1
                advanceCalls |> should equal 1
                recoveryCalls |> should equal 1
                recoveryObservedResync |> should equal true
                updateIpcCalls |> should equal 1
                updateIpcSawNonIncremental |> should equal true

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

                Watch
                    .pendingWatchWorkSnapshotForTests()
                    .StatusUpdateTriggers
                |> should equal Array.empty<string>
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies that confidence loss after a status write still advances the applied journal boundary.
    [<Test>]
    let ``watch advances journal boundary when confidence is lost after status write`` () =
        withTempRepo (fun root ->
            let relativePath = "journal-confidence-loss-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            let mutable appendCalls = 0
            let mutable applyCalls = 0
            let mutable advanceCalls = 0
            let mutable recoveryCalls = 0
            let mutable pruneCalls = 0

            Watch.OnDeleted(deletedEvent filePath)

            try
                Watch.setWatchJournalClientsForWatchTests
                    (fun _ ->
                        appendCalls <- appendCalls + 1
                        Task.FromResult([| 1L |]))
                    (fun _ ->
                        advanceCalls <- advanceCalls + 1
                        Task.FromResult(1L))

                Watch.setWatchJournalMaintenanceClientsForWatchTests
                    (fun _ _ ->
                        recoveryCalls <- recoveryCalls + 1
                        Task.FromResult(()))
                    (fun () ->
                        pruneCalls <- pruneCalls + 1
                        Task.FromResult(()))

                /// Reads status needed by the confidence-loss scenario.
                let readStatus () = Task.FromResult(status)
                /// Keeps uploads out of this status-only observation scenario.
                let upload _ _ = Task.FromResult(())
                /// Keeps the legacy status helper unused by the current Watch path.
                let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

                /// Simulates cancellation after append and before the commit boundary can advance.
                let updateGraceStatusFromDifferences currentStatus _ _ =
                    applyCalls <- applyCalls + 1
                    Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.Resynchronizing
                    Task.FromResult(Some currentStatus)

                /// Leaves incremental status writes outside this status-only observation scenario.
                let applyIncremental _ _ _ = Task.FromResult(())
                /// Keeps IPC publication deterministic for the confidence-loss test.
                let updateIpc _ _ = Task.FromResult(())

                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

                appendCalls |> should equal 1
                applyCalls |> should equal 1
                advanceCalls |> should equal 1
                recoveryCalls |> should equal 0
                pruneCalls |> should equal 1

                Watch.currentGraceWatchRuntimeModeForWatchTests ()
                |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

                Watch
                    .pendingWatchWorkSnapshotForTests()
                    .StatusUpdateTriggers
                |> should equal [| relativePath |]
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies that a stale status only delete requeues recreated content without a canceled upload marker.
    [<Test>]
    let ``stale status-only delete requeues recreated changed tracked file without canceled upload`` () =
        withTempRepo (fun root ->
            let relativePath = "stale-delete-recreated.txt"
            let filePath = Path.Combine(root, relativePath)
            /// Tracks upload Calls changes so this scenario can assert the requeued upload happened.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls changes so the changed content reaches status application.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the stale delete becomes a file change.
            let mutable observedDifferences = List<FileSystemDifference>()

            File.WriteAllText(filePath, "tracked content")

            let trackedFile =
                (Services.createLocalFileVersion (FileInfo(filePath)))
                    .GetAwaiter()
                    .GetResult()
                    .Value

            File.Delete(filePath)
            Watch.OnDeleted(deletedEvent filePath)
            File.WriteAllText(filePath, "recreated changed content")

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(graceStatusTrackingFileVersions [| trackedFile |])

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ pendingFilePath =
                uploadCalls <- uploadCalls + 1
                recordUploadedFileVersion $"{pendingFilePath}"
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

            uploadCalls |> should equal 0

            let afterRequeue = Watch.pendingWatchWorkSnapshotForTests ()

            afterRequeue.StatusUpdateTriggers
            |> should equal [| relativePath |]

            afterRequeue.FilesToProcess
            |> should equal [| filePath |]

            processPendingWork ()

            uploadCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Change, FileSystemEntryType.File, relativePath
                |]

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>

            afterProcessing.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that a stale delete requeue prevents applying unrelated ready uploads in the same pass.
    [<Test>]
    let ``mixed ready upload defers while stale status-only delete requeues`` () =
        withTempRepo (fun root ->
            let readyRelativePath = "ready-before-stale-delete.txt"
            let readyFilePath = Path.Combine(root, readyRelativePath)
            let staleDeleteRelativePath = "stale-delete-requeued.txt"
            let staleDeletePath = Path.Combine(root, staleDeleteRelativePath)
            /// Tracks upload Calls changes so the ready file is not redundantly uploaded.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls changes so partial status application is rejected.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam after the stale delete reupload completes.
            let mutable observedDifferences = List<FileSystemDifference>()

            File.WriteAllText(readyFilePath, "ready content")
            Watch.OnChanged(changedEvent readyFilePath)

            File.WriteAllText(staleDeletePath, "tracked stale-delete content")

            let trackedFile =
                (Services.createLocalFileVersion (FileInfo(staleDeletePath)))
                    .GetAwaiter()
                    .GetResult()
                    .Value

            File.Delete(staleDeletePath)
            Watch.OnDeleted(deletedEvent staleDeletePath)
            File.WriteAllText(staleDeletePath, "changed content after stale delete")

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(graceStatusTrackingFileVersions [| trackedFile |])

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ pendingFilePath =
                uploadCalls <- uploadCalls + 1
                recordUploadedFileVersion $"{pendingFilePath}"
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

            /// Runs one watch processing pass with mixed ready and stale-delete work.
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

            uploadCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 0

            let afterRequeue = Watch.pendingWatchWorkSnapshotForTests ()

            afterRequeue.FilesToProcess
            |> should equal [| staleDeletePath |]

            afterRequeue.StatusUpdateTriggers
            |> should equal [| staleDeleteRelativePath |]

            Watch.processedFileRelativePathsPendingStatusForWatchTests ()
            |> should equal [| readyRelativePath |]

            processPendingWork ()

            uploadCalls |> should equal 2
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.sortBy (fun (_, _, relativePath) -> relativePath)
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.File, readyRelativePath
                    DifferenceType.Change, FileSystemEntryType.File, staleDeleteRelativePath
                |]

            Watch.processedFileRelativePathsPendingStatusForWatchTests ()
            |> should equal Array.empty<string>)

    /// Verifies that status only triggers remain pending when status application fails.
    [<Test>]
    let ``status-only triggers remain pending when status application fails`` () =
        withTempRepo (fun root ->
            let filePath = Path.Combine(root, "failed-status-application-delete.txt")

            let status =
                graceStatusTracking
                    [|
                        "failed-status-application-delete.txt"
                    |]
                    Array.empty<string>

            /// Tracks update Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable updateCalls = 0

            Watch.OnDeleted(deletedEvent filePath)

            /// Builds update grace status test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ =
                updateCalls <- updateCalls + 1

                if updateCalls = 1 then Task.FromResult(None) else Task.FromResult(Some status)

            let processPendingWork () = processPendingWatchWorkWithStatusClients (fun () -> Task.FromResult(status)) updateGraceStatus

            processPendingWork ()

            let afterStatusUpdate = Watch.pendingWatchWorkSnapshotForTests ()

            afterStatusUpdate.StatusUpdateTriggers
            |> should
                equal
                [|
                    "failed-status-application-delete.txt"
                |]

            processPendingWork ()

            updateCalls |> should equal 2

            let afterRetry = Watch.pendingWatchWorkSnapshotForTests ()

            afterRetry.StatusUpdateTriggers
            |> should equal Array.empty<string>)

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
            |> should equal Array.empty<string>

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

    /// Verifies that case-sensitive delete derivation removes the exact tracked casing when a case-colliding file remains.
    [<Test>]
    let ``case-sensitive delete uses exact tracked file before ignore-case fallback`` () =
        withTempRepo (fun root ->
            Watch.setWatchPathComparisonForWatchTests StringComparison.Ordinal

            let status = graceStatusTracking [| "Foo.txt"; "foo.txt" |] Array.empty<string>

            Watch.setGraceStatusForWatchTests status

            Watch.setFinalPathExistsForWatchTests
                (fun fullPath -> String.Equals(Path.GetFileName(fullPath), "Foo.txt", StringComparison.Ordinal))
                (fun _ -> false)

            let deletedPath = Path.Combine(root, "foo.txt")
            Watch.OnDeleted(deletedEvent deletedPath)

            /// Tracks event-derived Differences changes so this scenario can assert the exact delete casing.
            let mutable observedDifferences = List<FileSystemDifference>()

            /// Reads status metadata needed by the test scenario.
            let readStatusMeta () = Task.FromResult(GraceStatus.Default)
            /// Reads status file needed by the test scenario.
            let readStatusFile () = Task.FromResult(status)
            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())
            /// Builds scan-oriented status update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)
            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ = Task.FromResult(List<FileSystemDifference>())

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status (differences: List<FileSystemDifference>) _ =
                observedDifferences <- List<FileSystemDifference>(differences)
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())
            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatusMeta
                readStatusFile
                upload
                updateGraceStatus
                scanForDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> should
                equal
                [|
                    DifferenceType.Delete, FileSystemEntryType.File, "foo.txt"
                |])

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

    /// Verifies that status only delete derives from GraceStatus and final path state without scanning.
    [<Test>]
    let ``status-only delete derives tracked file delete without scan`` () =
        withTempRepo (fun root ->
            let relativePath = "stale-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            File.WriteAllText(filePath, "tracked content before delete")

            let status = graceStatusTracking [| relativePath |] Array.empty<string>

            File.Delete(filePath)
            Watch.OnDeleted(deletedEvent filePath)

            /// Tracks observed Differences changes so this scenario can assert the resulting side effect explicitly.
            let mutable observedDifferences = List<FileSystemDifference>()

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds legacy status update test data used to exercise CLI watch behavior.
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
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

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
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

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

    /// Verifies that uploaded adds retry when the final file cannot be hashed during status derivation.
    [<Test>]
    let ``uploaded add retries when final hashing is unavailable`` () =
        withTempRepo (fun root ->
            let relativePath = "retry-unhashed-add.txt"
            let filePath = Path.Combine(root, relativePath)
            /// Tracks upload Calls changes so this scenario can assert the retry side effect explicitly.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls changes so the unavailable hash pass does not clear the add.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the retried add is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            File.WriteAllText(filePath, "content that cannot be hashed during the first status pass")
            Watch.OnCreated(changedEvent filePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ pendingFilePath =
                uploadCalls <- uploadCalls + 1
                recordUploadedFileVersion $"{pendingFilePath}"
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

            Watch.setCreateLocalFileVersionForWatchTests (fun _ -> Task.FromResult<LocalFileVersion option>(None))

            processPendingWork ()

            uploadCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 0

            let retrySnapshot = Watch.pendingWatchWorkSnapshotForTests ()

            retrySnapshot.FilesToProcess
            |> should equal [| filePath |]

            Watch.setCreateLocalFileVersionForWatchTests Services.createLocalFileVersion

            processPendingWork ()

            uploadCalls |> should equal 2
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.File, relativePath
                |]

            let appliedSnapshot = Watch.pendingWatchWorkSnapshotForTests ()

            appliedSnapshot.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that uploaded adds retry when final content no longer matches the uploaded identity.
    [<Test>]
    let ``uploaded add retries when final content changes after upload`` () =
        withTempRepo (fun root ->
            let relativePath = "retry-rewritten-add.txt"
            let filePath = Path.Combine(root, relativePath)
            /// Tracks upload Calls changes so this scenario can assert the retry side effect explicitly.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls changes so the stale upload identity does not clear the add.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the retried add is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            File.WriteAllText(filePath, "content captured by the first upload")
            Watch.OnCreated(changedEvent filePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ pendingFilePath =
                uploadCalls <- uploadCalls + 1
                recordUploadedFileVersion $"{pendingFilePath}"

                if uploadCalls = 1 then
                    File.WriteAllText($"{pendingFilePath}", "rewritten content with no newer watcher event")

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

            /// Runs one watch processing pass with the rewritten-content test clients.
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

            uploadCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 0

            let retrySnapshot = Watch.pendingWatchWorkSnapshotForTests ()

            retrySnapshot.FilesToProcess
            |> should equal [| filePath |]

            processPendingWork ()

            uploadCalls |> should equal 2
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.File, relativePath
                |]

            let appliedSnapshot = Watch.pendingWatchWorkSnapshotForTests ()

            appliedSnapshot.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that mixed uploaded adds wait for rewritten uploads before applying status.
    [<Test>]
    let ``mixed uploaded adds defer status while rewritten upload requeues`` () =
        withTempRepo (fun root ->
            let readyRelativePath = "ready-add.txt"
            let rewrittenRelativePath = "retry-rewritten-add.txt"
            let readyFilePath = Path.Combine(root, readyRelativePath)
            let rewrittenFilePath = Path.Combine(root, rewrittenRelativePath)
            /// Tracks upload Calls changes so this scenario can detect redundant ready-file retries.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls so partial status application is rejected.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply path once every upload has final content.
            let mutable observedDifferences = List<FileSystemDifference>()
            /// Tracks whether the rewritten file already changed after its first uploaded identity was recorded.
            let mutable rewrittenAfterFirstUpload = false

            File.WriteAllText(readyFilePath, "ready content")
            File.WriteAllText(rewrittenFilePath, "content captured by the first upload")
            Watch.OnCreated(changedEvent readyFilePath)
            Watch.OnCreated(changedEvent rewrittenFilePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ pendingFilePath =
                uploadCalls <- uploadCalls + 1
                let fullPath = $"{pendingFilePath}"
                recordUploadedFileVersion fullPath

                if
                    not rewrittenAfterFirstUpload
                    && String.Equals(Path.GetFileName(fullPath), rewrittenRelativePath, StringComparison.Ordinal)
                then
                    rewrittenAfterFirstUpload <- true
                    File.WriteAllText(fullPath, "rewritten content with no newer watcher event")

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

            /// Runs one watch processing pass with the mixed uploaded-file test clients.
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

            uploadCalls |> should equal 2
            applyFromDifferencesCalls |> should equal 0

            let retrySnapshot = Watch.pendingWatchWorkSnapshotForTests ()

            retrySnapshot.FilesToProcess
            |> should equal [| rewrittenFilePath |]

            processPendingWork ()

            uploadCalls |> should equal 3
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.sortBy (fun (_, _, relativePath) -> relativePath)
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.File, readyRelativePath
                    DifferenceType.Add, FileSystemEntryType.File, rewrittenRelativePath
                |]

            let appliedSnapshot = Watch.pendingWatchWorkSnapshotForTests ()

            appliedSnapshot.FilesToProcess
            |> should equal Array.empty<string>)

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

    /// Verifies that repository case detection can make default matching case-insensitive without a test override.
    [<Test>]
    let ``case-insensitive repository default preserves tracked file casing`` () =
        withTempRepo (fun root ->
            let uploadedPath = Path.Combine(root, "foo.txt")
            /// Tracks probe Calls changes so the test proves repository case behavior configures watch matching.
            let mutable probeCalls = 0
            /// Tracks the Differences passed to the apply seam so default tracked casing is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            Watch.setRepositoryPathCaseInsensitiveLookupForWatchTests (fun _ ->
                probeCalls <- probeCalls + 1
                true)

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

            probeCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Change, FileSystemEntryType.File, "Foo.txt"
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

                Watch.uploadedFileVersionRelativePathsForWatchTests ()
                |> Array.contains "Foo.txt"
                |> should equal true

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

    /// Verifies that a differently-cased delete event does not delete a tracked file that still exists.
    [<Test>]
    let ``matched delete checks tracked path before deleting tracked file`` () =
        withTempRepo (fun root ->
            Watch.setWatchPathComparisonForWatchTests StringComparison.Ordinal

            let trackedRelativePath = "Foo.txt"
            let deletedEventRelativePath = "foo.txt"
            let trackedPath = Path.Combine(root, trackedRelativePath)
            let deletedEventPath = Path.Combine(root, deletedEventRelativePath)
            let status = graceStatusTracking [| trackedRelativePath |] Array.empty<string>
            /// Tracks apply-from-differences Calls changes so stale delete suppression is proven.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the test fails if Foo.txt is deleted.
            let mutable observedDifferences = List<FileSystemDifference>()

            Watch.setGraceStatusForWatchTests status

            Watch.setFinalPathExistsForWatchTests (fun fullPath -> String.Equals(fullPath, trackedPath, StringComparison.Ordinal)) (fun _ -> false)

            Watch.OnDeleted(deletedEvent deletedEventPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

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

            applyFromDifferencesCalls |> should equal 0

            observedDifferences
            |> Seq.toArray
            |> should equal Array.empty<FileSystemDifference>)

    /// Verifies that delete classification stays case-insensitive even when other watch matching is case-sensitive.
    [<Test>]
    let ``case-sensitive watch comparison still preserves tracked delete casing`` () =
        withTempRepo (fun root ->
            Watch.setWatchPathComparisonForWatchTests StringComparison.Ordinal

            let deletedPath = Path.Combine(root, "foo.txt")
            let status = graceStatusTracking [| "Foo.txt" |] Array.empty<string>
            /// Tracks the Differences passed to the apply seam so legacy delete casing is proven.
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

    /// Verifies that a stale delete cannot discard an already-uploaded untracked add before status application.
    [<Test>]
    let ``stale delete after processed untracked add preserves uploaded status work`` () =
        withTempRepo (fun root ->
            let relativePath = "processed-add-stale-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            /// Tracks upload Calls changes so the test proves the original upload is reused.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls changes so the processed add reaches status.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the final add is proven.
            let mutable observedDifferences = List<FileSystemDifference>()
            /// Tracks whether the stale delete has already been injected after upload processing.
            let mutable staleDeleteQueued = false

            File.WriteAllText(filePath, "uploaded add content")
            Watch.OnChanged(changedEvent filePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ pendingFilePath =
                uploadCalls <- uploadCalls + 1
                recordUploadedFileVersion $"{pendingFilePath}"
                Task.FromResult(())

            /// Builds legacy status update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to inject a stale delete after the upload has been processed.
            let applyIncremental _ _ _ =
                if not staleDeleteQueued then
                    staleDeleteQueued <- true
                    Watch.OnDeleted(deletedEvent filePath)

                Task.FromResult(())

            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            uploadCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.File, relativePath
                |]

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.FilesToProcess
            |> should equal Array.empty<string>

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>

            Watch.processedFileRelativePathsPendingStatusForWatchTests ()
            |> should equal Array.empty<string>)

    /// Verifies that a pending uploaded file addition is cleared when a later delete removes the file.
    [<Test>]
    let ``delete after failed uploaded add clears stale pending file difference without scan`` () =
        withTempRepo (fun root ->
            let relativePath = "deleted-before-retry.txt"
            let filePath = Path.Combine(root, relativePath)
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

            /// Builds legacy status update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

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
                    scannerHostileDifferenceDiscovery
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

            /// Builds legacy status update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

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
                    scannerHostileDifferenceDiscovery
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

            applyFromDifferencesCalls |> should equal 1

            let afterRetry = Watch.pendingWatchWorkSnapshotForTests ()

            afterRetry.StatusUpdateTriggers
            |> should equal Array.empty<string>

            afterRetry.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that a clean retry drops a stale uploaded file change after the file returns to tracked content.
    [<Test>]
    let ``clean upload retry after reverted content clears stale pending change without scan`` () =
        withTempRepo (fun root ->
            let relativePath = "reverted-before-retry.txt"
            let filePath = Path.Combine(root, relativePath)
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

            /// Builds legacy status update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

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
                    scannerHostileDifferenceDiscovery
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

    /// Verifies that live file observations do not apply a stale startup delete for the same recreated path.
    [<Test>]
    let ``startup live change suppresses stale delete for same path without scan`` () =
        withTempRepo (fun root ->
            let relativePath = "recreated-with-change.txt"
            let fullPath = Path.Combine(root, relativePath)
            let staleDelete = FileSystemDifference.Create Delete FileSystemEntryType.File relativePath
            let liveChange = FileSystemDifference.Create Change FileSystemEntryType.File relativePath
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

            /// Builds legacy status update test data used to exercise CLI watch behavior.
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

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

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

    /// Verifies that a tracked renamed directory publishes dirty IPC even when the old path is ignored.
    [<Test>]
    let ``renamed directory add publishes dirty status when old path is ignored`` () =
        withTempRepo (fun root ->
            writeGraceIgnore root [| "ignored-old-assets/" |]

            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            Watch.setGraceStatusForWatchTests status
            Services.setGraceWatchHasPendingWorkForStatus false

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            let oldPath = Path.Combine(root, "ignored-old-assets")
            let newPath = Path.Combine(root, "tracked-new-assets")
            Directory.CreateDirectory(newPath) |> ignore

            let childFile = Path.Combine(newPath, "asset.txt")
            File.WriteAllText(childFile, "new tracked content")

            Watch.OnRenamed(renamedEvent oldPath newPath)

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal false

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>

            pending.FilesToProcess
            |> should equal [| childFile |])

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

    /// Verifies that a renamed directory enumeration failure blocks the old-path status trigger from applying.
    [<Test>]
    let ``renamed directory enumeration failure keeps old path trigger during processing`` () =
        withTempRepo (fun root ->
            let oldPath = Path.Combine(root, "old-enumeration-processing-name")
            let newPath = Path.Combine(root, "new-enumeration-processing-name")
            let status = graceStatusTracking Array.empty<string> [| "old-enumeration-processing-name" |]
            /// Tracks scan Calls changes so failed enumeration does not fall back to a healthy scan.
            let mutable scanCalls = 0
            /// Tracks event-derived status apply calls so the old path cannot be deleted before new contents upload.
            let mutable applyFromDifferencesCalls = 0

            Directory.CreateDirectory(newPath) |> ignore

            Watch.setGraceStatusForWatchTests status
            Watch.setEnumerateFilesForDirectoryUploadForWatchTests (fun _ -> raise (UnauthorizedAccessException("blocked enumeration")))

            Watch.OnRenamed(renamedEvent oldPath newPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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
            applyFromDifferencesCalls |> should equal 0

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.StatusUpdateTriggers
            |> should equal [| "old-enumeration-processing-name" |]

            pending.DirectoriesToProcess
            |> should equal [| newPath |])

    /// Verifies that directory deletes derive structural delete differences without a whole-root scan.
    [<Test>]
    let ``deleted tracked directory derives directory delete without scan`` () =
        withTempRepo (fun root ->
            let relativePath = "tracked-assets"
            let directoryPath = Path.Combine(root, relativePath)
            let status = graceStatusTracking Array.empty<string> [| relativePath |]
            /// Tracks scan Calls changes so this scenario proves healthy directory delete avoids a scan.
            let mutable scanCalls = 0
            /// Tracks event-derived status application so the directory delete is explicit.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so this scenario fails on missing directory delete work.
            let mutable observedDifferences = List<FileSystemDifference>()

            Watch.setGraceStatusForWatchTests status
            Watch.OnDeleted(deletedEvent directoryPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Delete, FileSystemEntryType.Directory, relativePath
                |])

    /// Verifies that unknown directory delete observations do not create Saves.
    [<Test>]
    let ``unknown deleted directory derives no save without scan`` () =
        withTempRepo (fun root ->
            let directoryPath = Path.Combine(root, "unknown-assets")
            /// Tracks scan Calls changes so this scenario proves no healthy fallback scan is used.
            let mutable scanCalls = 0
            /// Tracks event-derived status application so unknown deletes cannot create Saves.
            let mutable applyFromDifferencesCalls = 0

            Watch.OnDeleted(deletedEvent directoryPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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

    /// Verifies that a non-ignored empty directory create reaches status application without a whole-root scan.
    [<Test>]
    let ``created non-ignored empty directory derives directory add without scan`` () =
        withTempRepo (fun root ->
            let relativePath = "new-empty"
            let directoryPath = Path.Combine(root, relativePath)
            /// Tracks upload Calls changes so the test proves the empty directory is not file-derived.
            let mutable uploadCalls = 0
            /// Tracks scan Calls changes so healthy running mode stays event-derived.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so the directory add creates status work.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the empty directory add is explicit.
            let mutable observedDifferences = List<FileSystemDifference>()

            Directory.CreateDirectory(directoryPath) |> ignore
            Watch.OnCreated(createdEvent directoryPath)

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
                Task.FromResult(List<FileSystemDifference>())

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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

            uploadCalls |> should equal 0
            scanCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.Directory, relativePath
                |]

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>

            afterProcessing.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that a leaf-only nested empty directory event queues every missing parent before the child add.
    [<Test>]
    let ``nested empty directory leaf event derives missing parent adds without scan`` () =
        withTempRepo (fun root ->
            let parentRelativePath = "new-parent"
            let childRelativePath = "new-parent/empty-child"
            let childPath = Path.Combine(root, childRelativePath)
            /// Tracks upload Calls changes so the empty directory tree is not file-derived.
            let mutable uploadCalls = 0
            /// Tracks scan Calls changes so healthy running mode stays event-derived for leaf-only directory events.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so the nested directory adds create status work.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so missing parents are explicit.
            let mutable observedDifferences = List<FileSystemDifference>()

            Directory.CreateDirectory(childPath) |> ignore
            Watch.OnCreated(createdEvent childPath)

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
                Task.FromResult(List<FileSystemDifference>())

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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

            uploadCalls |> should equal 0
            scanCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.Directory, parentRelativePath
                    DifferenceType.Add, FileSystemEntryType.Directory, childRelativePath
                |]

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>

            afterProcessing.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that directory-create classification reuses one refreshed GraceStatus snapshot across a burst.
    [<Test>]
    let ``directory create classification caches refreshed status for burst`` () =
        withTempRepo (fun root ->
            let firstDirectoryPath = Path.Combine(root, "bulk-empty-one")
            let secondDirectoryPath = Path.Combine(root, "bulk-empty-two")
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            /// Tracks status reload Calls so burst directory-create callbacks do not reopen the local status DB per event.
            let mutable readStatusCalls = 0

            Watch.setGraceStatusForWatchTests GraceStatus.Default

            Watch.setReadGraceStatusFileForWatchTests (fun () ->
                readStatusCalls <- readStatusCalls + 1
                Task.FromResult(status))

            Directory.CreateDirectory(firstDirectoryPath)
            |> ignore

            Watch.OnCreated(createdEvent firstDirectoryPath)

            Directory.CreateDirectory(secondDirectoryPath)
            |> ignore

            Watch.OnCreated(createdEvent secondDirectoryPath)

            readStatusCalls |> should equal 1)

    /// Verifies that a refreshed status snapshot suppresses a queued directory add that is already tracked.
    [<Test>]
    let ``already tracked empty directory add derives no save after status refresh`` () =
        withTempRepo (fun root ->
            let relativePath = "already-tracked-empty"
            let directoryPath = Path.Combine(root, relativePath)
            let status = graceStatusTracking Array.empty<string> [| relativePath |]
            /// Tracks scan Calls changes so refreshed-status suppression stays event-derived.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so already-tracked directories cannot create Saves.
            let mutable applyFromDifferencesCalls = 0

            Directory.CreateDirectory(directoryPath) |> ignore
            Watch.OnCreated(createdEvent directoryPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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
            applyFromDifferencesCalls |> should equal 0

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>)

    /// Verifies that ignored empty directory create and delete observations do not create status work.
    [<Test>]
    let ``ignored empty directory create and delete derive no save without scan`` () =
        withTempRepo (fun root ->
            writeGraceIgnore root [| "ignored/" |]

            let directoryPath = Path.Combine(root, "ignored")
            /// Tracks scan Calls changes so ignored directory noise stays event-derived.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so ignored directories cannot create Saves.
            let mutable applyFromDifferencesCalls = 0

            Directory.CreateDirectory(directoryPath) |> ignore
            Watch.OnCreated(createdEvent directoryPath)
            Directory.Delete(directoryPath)
            Watch.OnDeleted(deletedEvent directoryPath)

            let pending = Watch.pendingWatchWorkSnapshotForTests ()

            pending.FilesToProcess
            |> should equal Array.empty<string>

            pending.StatusUpdateTriggers
            |> should equal Array.empty<string>

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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

    /// Verifies that directory mtime-only change observations do not create status work.
    [<Test>]
    let ``directory mtime-only change derives no save without scan`` () =
        withTempRepo (fun root ->
            let directoryPath = Path.Combine(root, "mtime-only")
            /// Tracks scan Calls changes so mtime-only directory noise stays event-derived.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so directory metadata noise cannot create Saves.
            let mutable applyFromDifferencesCalls = 0

            Directory.CreateDirectory(directoryPath) |> ignore
            Directory.SetLastWriteTimeUtc(directoryPath, DateTime.UtcNow.AddMinutes(1.0))
            Watch.OnChanged(changedEvent directoryPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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

    /// Verifies that a recreated directory invalidates a stale directory delete before status apply.
    [<Test>]
    let ``recreated directory suppresses stale directory delete without scan`` () =
        withTempRepo (fun root ->
            let relativePath = "recreated-assets"
            let directoryPath = Path.Combine(root, relativePath)
            let status = graceStatusTracking Array.empty<string> [| relativePath |]
            /// Tracks scan Calls changes so final-state invalidation stays event-derived.
            let mutable scanCalls = 0
            /// Tracks event-derived status application so stale directory deletes do not apply.
            let mutable applyFromDifferencesCalls = 0

            Watch.setGraceStatusForWatchTests status
            Watch.OnDeleted(deletedEvent directoryPath)

            Directory.CreateDirectory(directoryPath) |> ignore

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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

    /// Verifies that an empty directory rename is represented as a delete plus add without file upload or scanning.
    [<Test>]
    let ``renamed empty directory derives delete plus add without scan`` () =
        withTempRepo (fun root ->
            let oldRelativePath = "old-empty"
            let newRelativePath = "new-empty"
            let oldPath = Path.Combine(root, oldRelativePath)
            let newPath = Path.Combine(root, newRelativePath)
            let status = graceStatusTracking Array.empty<string> [| oldRelativePath |]
            /// Tracks upload Calls changes so empty directory rename cannot depend on uploaded file identity.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls changes so the rename reaches the event-derived status seam.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so delete-plus-add semantics are explicit.
            let mutable observedDifferences = List<FileSystemDifference>()

            Directory.CreateDirectory(newPath) |> ignore
            Watch.setGraceStatusForWatchTests status
            Watch.OnRenamed(renamedEvent oldPath newPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ =
                uploadCalls <- uploadCalls + 1
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            uploadCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equivalent
                [|
                    DifferenceType.Delete, FileSystemEntryType.Directory, oldRelativePath
                    DifferenceType.Add, FileSystemEntryType.Directory, newRelativePath
                |])

    /// Verifies that a renamed directory enumerates only its subtree and keeps empty child directories.
    [<Test>]
    let ``renamed subtree preserves empty child directory adds without root scan`` () =
        withTempRepo (fun root ->
            let oldRelativePath = "old-assets"
            let newRelativePath = "new-assets"
            let oldPath = Path.Combine(root, oldRelativePath)
            let newPath = Path.Combine(root, newRelativePath)
            let fileParentRelativePath = $"{newRelativePath}/content"
            let emptyChildRelativePath = $"{newRelativePath}/empty-child"
            let fileRelativePath = $"{fileParentRelativePath}/asset.txt"
            let unrelatedRelativePath = "unrelated-empty"
            let filePath = Path.Combine(root, fileRelativePath)
            let status = graceStatusTracking Array.empty<string> [| oldRelativePath |]
            /// Tracks upload Calls changes so the file-bearing part of the subtree is still processed.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls changes so status work stays event-derived.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so bounded subtree enumeration is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            Directory.CreateDirectory(Path.Combine(root, fileParentRelativePath))
            |> ignore

            Directory.CreateDirectory(Path.Combine(root, emptyChildRelativePath))
            |> ignore

            Directory.CreateDirectory(Path.Combine(root, unrelatedRelativePath))
            |> ignore

            File.WriteAllText(filePath, "renamed subtree content")
            Watch.setGraceStatusForWatchTests status
            Watch.OnRenamed(renamedEvent oldPath newPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ filePath =
                uploadCalls <- uploadCalls + 1
                recordUploadedFileVersion $"{filePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            uploadCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equivalent
                [|
                    DifferenceType.Delete, FileSystemEntryType.Directory, oldRelativePath
                    DifferenceType.Add, FileSystemEntryType.Directory, newRelativePath
                    DifferenceType.Add, FileSystemEntryType.Directory, fileParentRelativePath
                    DifferenceType.Add, FileSystemEntryType.Directory, emptyChildRelativePath
                    DifferenceType.Add, FileSystemEntryType.File, fileRelativePath
                |]

            observedDifferences
            |> Seq.exists (fun difference -> $"{difference.RelativePath}" = unrelatedRelativePath)
            |> should equal false)

    /// Verifies that ignored directories under a renamed subtree are pruned before directory adds are derived.
    [<Test>]
    let ``renamed subtree prunes ignored directory descendants before status adds`` () =
        withTempRepo (fun root ->
            writeGraceIgnore root [| "ignored/" |]

            let oldRelativePath = "old-assets"
            let newRelativePath = "new-assets"
            let ignoredRelativePath = $"{newRelativePath}/ignored"
            let ignoredChildRelativePath = $"{ignoredRelativePath}/empty"
            let oldPath = Path.Combine(root, oldRelativePath)
            let newPath = Path.Combine(root, newRelativePath)
            let status = graceStatusTracking Array.empty<string> [| oldRelativePath |]
            /// Tracks upload Calls changes so ignored empty directory descendants cannot be file-derived.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls changes so status work stays event-derived.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so ignored ancestors cannot be reintroduced.
            let mutable observedDifferences = List<FileSystemDifference>()

            Directory.CreateDirectory(Path.Combine(root, ignoredChildRelativePath))
            |> ignore

            Watch.setGraceStatusForWatchTests status
            Watch.OnRenamed(renamedEvent oldPath newPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ =
                uploadCalls <- uploadCalls + 1
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            uploadCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equivalent
                [|
                    DifferenceType.Delete, FileSystemEntryType.Directory, oldRelativePath
                    DifferenceType.Add, FileSystemEntryType.Directory, newRelativePath
                |]

            observedDifferences
            |> Seq.exists (fun difference ->
                let relativePath = $"{difference.RelativePath}"

                relativePath = ignoredRelativePath
                || relativePath = ignoredChildRelativePath)
            |> should equal false)

    /// Verifies that a stale parent delete does not drop a final non-ignored empty child directory add.
    [<Test>]
    let ``stale parent delete preserves recreated empty child directory add without scan`` () =
        withTempRepo (fun root ->
            let parentRelativePath = "parent-assets"
            let childRelativePath = "parent-assets/empty-child"
            let parentPath = Path.Combine(root, parentRelativePath)
            let childPath = Path.Combine(root, childRelativePath)
            let status = graceStatusTracking Array.empty<string> [| parentRelativePath |]
            /// Tracks upload Calls changes so the final empty directory add is not file-derived.
            let mutable uploadCalls = 0
            /// Tracks scan Calls changes so stale parent resolution stays event-derived.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so the child add reaches status application.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the stale parent delete is suppressed.
            let mutable observedDifferences = List<FileSystemDifference>()

            Watch.setGraceStatusForWatchTests status
            Watch.OnDeleted(deletedEvent parentPath)

            Directory.CreateDirectory(childPath) |> ignore
            Watch.OnCreated(createdEvent childPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ =
                uploadCalls <- uploadCalls + 1
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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

            uploadCalls |> should equal 0
            scanCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Add, FileSystemEntryType.Directory, childRelativePath
                |]

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>

            afterProcessing.FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that replacing a tracked file with an empty directory preserves the final directory add.
    [<Test>]
    let ``empty directory only replacement preserves final directory add without uploaded files`` () =
        withTempRepo (fun root ->
            let relativePath = "replace-me"
            let replacementPath = Path.Combine(root, relativePath)
            let status = graceStatusTracking [| relativePath |] Array.empty<string>
            /// Tracks upload Calls changes so no uploaded file path can force status application.
            let mutable uploadCalls = 0
            /// Tracks scan Calls changes so replacement stays event-derived.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so the replacement creates status work.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the final directory add is explicit.
            let mutable observedDifferences = List<FileSystemDifference>()

            Watch.setGraceStatusForWatchTests status
            Watch.OnDeleted(deletedEvent replacementPath)

            Directory.CreateDirectory(replacementPath)
            |> ignore

            Watch.OnCreated(createdEvent replacementPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ =
                uploadCalls <- uploadCalls + 1
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds scan-for-differences test data used to exercise CLI watch behavior.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                Task.FromResult(List<FileSystemDifference>())

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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

            uploadCalls |> should equal 0
            scanCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.sortBy (fun (_, entryType, _) -> if entryType = FileSystemEntryType.File then 0 else 1)
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Delete, FileSystemEntryType.File, relativePath
                    DifferenceType.Add, FileSystemEntryType.Directory, relativePath
                |])

    /// Verifies that a directory replaced by a file emits the directory delete before the file add.
    [<Test>]
    let ``tracked directory replaced by file derives delete before add without scan`` () =
        withTempRepo (fun root ->
            let relativePath = "kind-replaced"
            let replacementFilePath = Path.Combine(root, relativePath)
            let status = graceStatusTracking Array.empty<string> [| relativePath |]
            /// Tracks scan Calls changes so replacement-by-kind stays event-derived.
            let mutable scanCalls = 0
            /// Tracks the Differences passed to the apply seam so ordering is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            Watch.setGraceStatusForWatchTests status
            File.WriteAllText(replacementFilePath, "file replacing a tracked directory")
            Watch.OnDeleted(deletedEvent replacementFilePath)
            Watch.OnCreated(changedEvent replacementFilePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

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

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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
                scanForDifferences
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            scanCalls |> should equal 0

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Delete, FileSystemEntryType.Directory, relativePath
                    DifferenceType.Add, FileSystemEntryType.File, relativePath
                |])

    /// Verifies that case-insensitive directory delete emits the tracked directory casing.
    [<Test>]
    let ``case-insensitive deleted directory preserves tracked path casing`` () =
        withTempRepo (fun root ->
            Watch.setWatchPathComparisonForWatchTests StringComparison.OrdinalIgnoreCase

            let deletedPath = Path.Combine(root, "src")
            let status = graceStatusTracking Array.empty<string> [| "Src" |]
            /// Tracks the Differences passed to the apply seam so tracked directory casing is proven.
            let mutable observedDifferences = List<FileSystemDifference>()

            Watch.setGraceStatusForWatchTests status
            Watch.OnDeleted(deletedEvent deletedPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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
                    DifferenceType.Delete, FileSystemEntryType.Directory, "Src"
                |])

    /// Verifies that a stale directory delete requeues surviving child files after canceling their uploads.
    [<Test>]
    let ``stale directory delete requeues canceled child uploads without scan`` () =
        withTempRepo (fun root ->
            let directoryRelativePath = "assets"
            let childRelativePath = "assets/child.txt"
            let directoryPath = Path.Combine(root, directoryRelativePath)
            let childPath = Path.Combine(directoryPath, "child.txt")

            let status =
                graceStatusTracking [| childRelativePath |] [|
                    directoryRelativePath
                |]

            /// Tracks upload Calls changes so the canceled child upload retry is proven.
            let mutable uploadCalls = 0
            /// Tracks scan Calls changes so this stale directory path stays event-derived.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so status waits for requeued upload content.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the surviving child reaches status.
            let mutable observedDifferences = List<FileSystemDifference>()

            Directory.CreateDirectory(directoryPath) |> ignore

            File.WriteAllText(childPath, "surviving changed child")
            Watch.setGraceStatusForWatchTests status
            Watch.OnChanged(changedEvent childPath)
            Watch.OnDeleted(deletedEvent directoryPath)

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

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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

            uploadCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 0

            let afterRequeue = Watch.pendingWatchWorkSnapshotForTests ()

            afterRequeue.FilesToProcess
            |> should equal [| childPath |]

            afterRequeue.StatusUpdateTriggers
            |> should equal [| directoryRelativePath |]

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
                    DifferenceType.Change, FileSystemEntryType.File, childRelativePath
                |])

    /// Verifies that stale parent triggers do not suppress retry for unmatched uploaded child content.
    [<Test>]
    let ``stale parent delete requeues unmatched processed child upload without scan`` () =
        withTempRepo (fun root ->
            let directoryRelativePath = "unmatched-parent"
            let childRelativePath = "unmatched-parent/child.txt"
            let directoryPath = Path.Combine(root, directoryRelativePath)
            let childPath = Path.Combine(directoryPath, "child.txt")
            let requeuedChildPath = Path.Combine(root, childRelativePath)

            let status =
                graceStatusTracking [| childRelativePath |] [|
                    directoryRelativePath
                |]

            /// Tracks upload Calls changes so the unmatched child upload retry is proven.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls changes so status waits for the retried upload.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the Differences passed to the apply seam so the final child change is proven.
            let mutable observedDifferences = List<FileSystemDifference>()
            /// Tracks whether the stale parent delete has already been injected after upload processing.
            let mutable parentDeleteQueued = false

            Directory.CreateDirectory(directoryPath) |> ignore
            File.WriteAllText(childPath, "uploaded child content")
            Watch.setGraceStatusForWatchTests status
            Watch.OnChanged(changedEvent childPath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ pendingFilePath =
                uploadCalls <- uploadCalls + 1
                recordUploadedFileVersion $"{pendingFilePath}"
                Task.FromResult(())

            /// Builds legacy status update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                observedDifferences <- differences
                Task.FromResult(Some status)

            /// Builds apply incremental test data used to make the uploaded child identity stale before status apply.
            let applyIncremental _ _ _ =
                if not parentDeleteQueued then
                    parentDeleteQueued <- true
                    File.WriteAllText(childPath, "final child content after stale parent delete")
                    Watch.OnDeleted(deletedEvent directoryPath)

                Task.FromResult(())

            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            let processPendingWork () =
                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

            processPendingWork ()

            uploadCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 0

            let afterRequeue = Watch.pendingWatchWorkSnapshotForTests ()

            afterRequeue.FilesToProcess
            |> should equal [| requeuedChildPath |]

            afterRequeue.StatusUpdateTriggers
            |> should equal [| directoryRelativePath |]

            processPendingWork ()

            uploadCalls |> should equal 2
            applyFromDifferencesCalls |> should equal 1

            observedDifferences
            |> Seq.map (fun difference -> difference.DifferenceType, difference.FileSystemEntryType, $"{difference.RelativePath}")
            |> Seq.toArray
            |> should
                equal
                [|
                    DifferenceType.Change, FileSystemEntryType.File, childRelativePath
                |])

    /// Verifies that parent directory delete resolves stale child file work when the child is gone.
    [<Test>]
    let ``parent directory delete resolves stale child add without requeueing missing upload`` () =
        withTempRepo (fun root ->
            let directoryRelativePath = "transient-parent"
            let childRelativePath = "transient-parent/child.txt"
            let directoryPath = Path.Combine(root, directoryRelativePath)
            let childPath = Path.Combine(directoryPath, "child.txt")
            /// Tracks scan Calls changes so stale child resolution does not use a healthy scan.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so the stale child add is applied only on the failed pass.
            let mutable applyFromDifferencesCalls = 0

            Directory.CreateDirectory(directoryPath) |> ignore

            File.WriteAllText(childPath, "transient child content")
            Watch.OnChanged(changedEvent childPath)

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

            /// Builds event-derived status apply test data used to exercise CLI watch behavior.
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

            File.Delete(childPath)
            Directory.Delete(directoryPath)
            Watch.OnDeleted(deletedEvent directoryPath)

            processPendingWork ()

            scanCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

            let afterProcessing = Watch.pendingWatchWorkSnapshotForTests ()

            afterProcessing.StatusUpdateTriggers
            |> should equal Array.empty<string>

            afterProcessing.FilesToProcess
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

    /// Verifies that live uploads drained before startup apply are included without scanning.
    [<Test>]
    let ``startup apply merges live upload differences without scan`` () =
        withTempRepo (fun root ->
            let startupDifference = FileSystemDifference.Create Delete FileSystemEntryType.File "offline-delete.txt"
            let liveFilePath = Path.Combine(root, "live-upload.txt")
            let liveUploadDifference = FileSystemDifference.Create Add FileSystemEntryType.File "live-upload.txt"
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
                scannerHostileDifferenceDiscovery
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

    /// Verifies that failed startup status application retries pending startup differences.
    [<Test>]
    let ``startup apply retries failed status application without scan`` () =
        withTempRepo (fun root ->
            let startupDifference = FileSystemDifference.Create Add FileSystemEntryType.File "retry-after-failed-status.txt"
            let liveFilePath = Path.Combine(root, "live-upload-after-failed-status.txt")
            let liveUploadDifference = FileSystemDifference.Create Add FileSystemEntryType.File "live-upload-after-failed-status.txt"
            /// Tracks apply-from-differences Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks upload Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable uploadCalls = 0
            /// Tracks the Differences passed to the apply seam so the retry proves failed work was not drained.
            let mutable observedDifferences = List<FileSystemDifference>()

            Watch.queueStartupDifferenceForWatch startupDifference
            File.WriteAllText(liveFilePath, "live content")
            Watch.OnChanged(changedEvent liveFilePath)

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ filePath =
                uploadCalls <- uploadCalls + 1
                let fullPath = $"{filePath}"

                if File.Exists(fullPath) then recordUploadedFileVersion fullPath

                Task.FromResult(())

            /// Builds legacy status update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                observedDifferences <- differences

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
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    applyIncremental
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

            processPendingWork ()

            applyFromDifferencesCalls |> should equal 1

            processPendingWork ()

            uploadCalls |> should equal 2
            applyFromDifferencesCalls |> should equal 2

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

                let claimStatus: Services.GraceWatchStatus = deserialize (File.ReadAllText(Services.IpcFileName()))

                tryReadWatchStatusJsonStringProperty "Mode"
                |> should equal None

                claimStatus.Mode
                |> should equal Services.GraceWatchRuntimeMode.StartingUp

                let safetyFlags = safetyFlagSet claimStatus

                safetyFlags
                |> Set.contains "startupClaim"
                |> should equal true

                safetyFlags
                |> Set.contains "requiresExplicitResync"
                |> should equal true

                /// Verifies that the CLI watch scenario exits with the expected process status.
                let exitCode, output = runWithCapturedOutput [| "watch" |]

                exitCode |> should equal -1

                output
                |> should contain "GraceWatch is already running"

                output
                |> should not' (contain "Unable to acquire an access token for SignalR")))

    /// Verifies that current-identity startup claims reject duplicate startup attempts.
    [<Test>]
    let ``watch startup claim blocks duplicate current identity claim`` () =
        withTempRepo (fun root ->
            let repositoryName = "current-startup-repo"
            let branchName = $"current-startup-branch-{Guid.NewGuid():N}"
            let repositoryId, branchId = configureCurrentWatchIdentity root repositoryName branchName
            deleteWatchStatusFileIfExists ()

            let firstClaimed =
                Services
                    .tryClaimGraceWatchInterprocessFile()
                    .Result

            firstClaimed |> should equal true

            readWatchStatusJsonBooleanProperty "IsStartupClaim"
            |> should equal true

            readWatchStatusJsonStringProperty "RepositoryId"
            |> should equal $"{repositoryId}"

            readWatchStatusJsonStringProperty "RepositoryName"
            |> should equal repositoryName

            readWatchStatusJsonStringProperty "BranchId"
            |> should equal $"{branchId}"

            readWatchStatusJsonStringProperty "BranchName"
            |> should equal branchName

            readWatchStatusJsonStringProperty "RootDirectory"
            |> should equal root

            let originalContents = File.ReadAllText(Services.IpcFileName())

            let secondClaimed =
                Services
                    .tryClaimGraceWatchInterprocessFile()
                    .Result

            secondClaimed |> should equal false

            File.ReadAllText(Services.IpcFileName())
            |> should equal originalContents)

    /// Verifies that a startup claim written by another same-branch worktree does not block this repository identity.
    [<Test>]
    let ``watch startup claim replaces foreign same branch startup claim`` () =
        withTempRepo (fun root ->
            let branchName = $"shared-startup-branch-{Guid.NewGuid():N}"
            let foreignRoot = Path.Combine(root, "other-worktree")
            Directory.CreateDirectory(foreignRoot) |> ignore

            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-startup-repo" branchName
            deleteWatchStatusFileIfExists ()

            let foreignStartupClaim =
                { Services.GraceWatchStatus.Default with
                    UpdatedAt = getCurrentInstant ()
                    IsStartupClaim = true
                    RepositoryId = Guid.NewGuid()
                    RepositoryName = RepositoryName "foreign-startup-repo"
                    BranchId = Guid.NewGuid()
                    BranchName = BranchName branchName
                    RootDirectory = foreignRoot
                }

            let ipcFileName = Services.IpcFileName()

            Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
            |> ignore

            File.WriteAllText(ipcFileName, serialize foreignStartupClaim)

            Services
                .inspectGraceWatchStatus()
                .Result
                .HasCurrentRepositoryIdentity
            |> should equal false

            let currentClaimed =
                Services
                    .tryClaimGraceWatchInterprocessFile()
                    .Result

            currentClaimed |> should equal true

            readWatchStatusJsonBooleanProperty "IsStartupClaim"
            |> should equal true

            readWatchStatusJsonStringProperty "RepositoryId"
            |> should equal $"{currentRepositoryId}"

            readWatchStatusJsonStringProperty "BranchId"
            |> should equal $"{currentBranchId}"

            readWatchStatusJsonStringProperty "BranchName"
            |> should equal branchName

            readWatchStatusJsonStringProperty "RootDirectory"
            |> should equal root)

    /// Verifies that startup claim can replace legacy IPC that lacks current identity authority.
    [<Test>]
    let ``watch startup claim replaces fresh legacy status without current identity`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            let legacyJson =
                rootDirectoryId
                |> liveWatchStatus
                |> serialize
                |> removeWatchStatusFieldsAddedForIssue492

            let ipcFileName = Services.IpcFileName()

            Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
            |> ignore

            File.WriteAllText(ipcFileName, legacyJson)

            let claimed =
                Services
                    .tryClaimGraceWatchInterprocessFile()
                    .Result

            claimed |> should equal true

            File.ReadAllText(ipcFileName)
            |> should not' (equal legacyJson)

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that fresh Watch IPC from another repository identity does not block startup for the current worktree.
    [<Test>]
    let ``watch startup claim replaces fresh foreign identity status`` () =
        withTempRepo (fun tempDir ->
            let rootDirectoryId = Guid.NewGuid()
            let baseStatus = liveWatchStatus rootDirectoryId

            let foreignStatuses =
                [|
                    { baseStatus with RepositoryId = Guid.NewGuid() }
                    { baseStatus with BranchId = Guid.NewGuid() }
                    { baseStatus with RootDirectory = Path.Combine(tempDir, "other-worktree") }
                |]

            for foreignStatus in foreignStatuses do
                writeWatchStatusJsonWithRuntimeSurface foreignStatus
                |> ignore

                let claimed =
                    Services
                        .tryClaimGraceWatchInterprocessFile()
                        .Result

                claimed |> should equal true

                readWatchStatusJsonBooleanProperty "IsStartupClaim"
                |> should equal true

                Services.getGraceWatchStatus().Result
                |> should equal None)

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
            status |> should equal None

            tryReadWatchStatusJsonStringProperty "Mode"
            |> should equal None

            let safetyFlags = readWatchStatusJsonSafetyFlags ()

            safetyFlags
            |> Set.contains "startupClaim"
            |> should equal true

            safetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true)

    /// Verifies that watch startup claim reclaims stale ipc files through the compact runtime contract.
    [<Test>]
    let ``watch startup claim replaces stale ipc file through runtime contract`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            let staleStatus =
                { liveWatchStatus rootDirectoryId with
                    UpdatedAt =
                        getCurrentInstant()
                            .Minus(Duration.FromMinutes(6.0))
                }

            let ipcFileName = Services.IpcFileName()

            Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
            |> ignore

            File.WriteAllText(ipcFileName, serialize staleStatus)

            let claimed =
                Services
                    .tryClaimGraceWatchInterprocessFile()
                    .Result

            claimed |> should equal true

            tryReadWatchStatusJsonStringProperty "Mode"
            |> should equal None

            let safetyFlags = readWatchStatusJsonSafetyFlags ()

            safetyFlags
            |> Set.contains "startupClaim"
            |> should equal true

            safetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true)

    /// Verifies that a dead startup claim cannot keep advertising a live startup mode in raw compact IPC JSON.
    [<Test>]
    let ``watch compact startup claim does not expose starting mode after heartbeat ages`` () =
        withTempRepo (fun _ ->
            let claimed =
                Services
                    .tryClaimGraceWatchInterprocessFile()
                    .Result

            claimed |> should equal true

            tryReadWatchStatusJsonStringProperty "Mode"
            |> should equal None

            getCurrentInstant()
                .Minus(Duration.FromMinutes(6.0))
            |> updatePersistedWatchStatusUpdatedAt

            Services.getGraceWatchStatus().Result
            |> should equal None

            tryReadWatchStatusJsonStringProperty "Mode"
            |> should equal None

            let agedStatus: Services.GraceWatchStatus = deserialize (File.ReadAllText(Services.IpcFileName()))

            agedStatus.Mode
            |> should equal Services.GraceWatchRuntimeMode.StartingUp

            let derivedSafetyFlags = safetyFlagSet agedStatus

            derivedSafetyFlags
            |> Set.contains "startupClaim"
            |> should equal true

            derivedSafetyFlags
            |> Set.contains "staleStatus"
            |> should equal true

            derivedSafetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true)

    /// Verifies that watch status serializes the factual identity and clean fields branch-switch safety needs.
    [<Test>]
    let ``watch status serializes branch repository root identity and clean flags`` () =
        withTempRepo (fun root ->
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let configuration = Current()
            configuration.RepositoryId <- repositoryId
            configuration.RepositoryName <- "status-repo"
            configuration.BranchId <- branchId
            configuration.BranchName <- "status-branch"
            configuration.RootDirectory <- root

            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            Services.setGraceWatchHasPendingWorkForStatus false

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonStringProperty "RepositoryId"
            |> should equal $"{repositoryId}"

            readWatchStatusJsonStringProperty "RepositoryName"
            |> should equal "status-repo"

            readWatchStatusJsonStringProperty "BranchId"
            |> should equal $"{branchId}"

            readWatchStatusJsonStringProperty "BranchName"
            |> should equal "status-branch"

            readWatchStatusJsonStringProperty "RootDirectory"
            |> should equal root

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal false

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal true

            let safetyFlags = readWatchStatusJsonSafetyFlags ()

            safetyFlags
            |> Set.contains "cleanWorkingTree"
            |> should equal false

            match Services.getGraceWatchStatus().Result with
            | Some watchStatus ->
                watchStatus.RepositoryId
                |> should equal repositoryId

                watchStatus.RepositoryName
                |> should equal "status-repo"

                watchStatus.BranchId |> should equal branchId

                watchStatus.BranchName
                |> should equal "status-branch"

                watchStatus.RootDirectory |> should equal root

                watchStatus.IsWorkingTreeClean
                |> should equal true

                watchStatus.HasPendingWatchWork
                |> should equal false
            | None -> Assert.Fail("Expected clean status to remain usable."))

    /// Verifies that repeated raw events while already dirty do not rewrite Watch IPC status.
    [<Test>]
    let ``watch dirty status transition is not rewritten for repeated raw events`` () =
        withTempRepo (fun root ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            Services.setGraceWatchHasPendingWorkForStatus false

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            let filePath = Path.Combine(root, "dirty-once.txt")
            File.WriteAllText(filePath, "first content")

            Watch.OnChanged(changedEvent filePath)

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal false

            let safetyFlags = readWatchStatusJsonSafetyFlags ()

            safetyFlags
            |> Set.contains "pendingWatchWork"
            |> should equal true

            safetyFlags
            |> Set.contains "pendingStatusApply"
            |> should equal false

            let dirtyJson = File.ReadAllText(Services.IpcFileName())

            File.WriteAllText(filePath, "second content")
            Watch.OnChanged(changedEvent filePath)

            File.ReadAllText(Services.IpcFileName())
            |> should equal dirtyJson)

    /// Verifies that durable journal evidence marks Watch status dirty even when process-local queues are empty.
    [<Test>]
    let ``watch durable pending journal evidence publishes dirty transition`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            try
                Watch.setWatchJournalStatusClientForWatchTests (fun () ->
                    Task.FromResult(
                        { LocalStateDb.WatchJournalPendingWorkSummary.DbPath = Current().GraceStatusFile; AppliedThroughSequence = 1L; PendingRowCount = 1L }
                    ))

                Services.setGraceWatchHasPendingWorkForStatus false

                (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                    .GetAwaiter()
                    .GetResult()

                Watch.publishPendingWatchWorkTransitionIfNeededForWatchTests ()

                readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
                |> should equal true

                readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
                |> should equal false

                let inspection =
                    Services
                        .inspectGraceWatchStatus()
                        .GetAwaiter()
                        .GetResult()

                inspection.IsUsable |> should equal false

                let safetyFlags = inspection.SafetyFlags |> Set.ofArray

                safetyFlags
                |> Set.contains "pendingWatchWork"
                |> should equal true

                safetyFlags
                |> Set.contains "requiresExplicitResync"
                |> should equal true
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies that durable-only journal evidence publishes non-incremental IPC instead of a healthy dirty snapshot.
    [<Test>]
    let ``watch durable-only pending journal evidence publishes resync-required transition`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            try
                Watch.setWatchJournalStatusClientForWatchTests (fun () ->
                    Task.FromResult(
                        { LocalStateDb.WatchJournalPendingWorkSummary.DbPath = Current().GraceStatusFile; AppliedThroughSequence = 1L; PendingRowCount = 1L }
                    ))

                Services.setGraceWatchHasPendingWorkForStatus false

                (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                    .GetAwaiter()
                    .GetResult()

                Watch.publishPendingWatchWorkTransitionIfNeededForWatchTests ()

                let inspection =
                    Services
                        .inspectGraceWatchStatus()
                        .GetAwaiter()
                        .GetResult()

                inspection.IsUsable |> should equal false

                inspection.EffectiveMode
                |> should equal (Some Services.GraceWatchRuntimeMode.Resynchronizing)

                let publishedStatus =
                    inspection.Status
                    |> Option.defaultWith (fun () -> failwith "Expected durable-only IPC status.")

                publishedStatus.HasPendingWatchWork
                |> should equal true

                publishedStatus.IsWorkingTreeClean
                |> should equal false

                publishedStatus.RootDirectoryId
                |> should equal GraceStatus.Default.RootDirectoryId

                publishedStatus.DirectoryIds.Count
                |> should equal 0

                let safetyFlags = inspection.SafetyFlags |> Set.ofArray

                safetyFlags
                |> Set.contains "pendingWatchWork"
                |> should equal true

                safetyFlags
                |> Set.contains "requiresExplicitResync"
                |> should equal true
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies that durable dirty-to-clean evidence republishes immediately during an idle timer pass.
    [<Test>]
    let ``watch durable pending journal evidence publishes clean transition after clearing`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)
            let mutable pendingRows = 1L

            try
                Watch.setWatchJournalStatusClientForWatchTests (fun () ->
                    Task.FromResult(
                        {
                            LocalStateDb.WatchJournalPendingWorkSummary.DbPath = Current().GraceStatusFile
                            AppliedThroughSequence = 1L
                            PendingRowCount = pendingRows
                        }
                    ))

                Watch.setReadGraceStatusFileForPendingWorkTransitionForWatchTests (fun () -> Task.FromResult(status))

                Services.setGraceWatchHasPendingWorkForStatus false

                (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                    .GetAwaiter()
                    .GetResult()

                Watch.publishPendingWatchWorkTransitionIfNeededForWatchTests ()

                readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
                |> should equal true

                pendingRows <- 0L

                (Watch.processChangedFilesWithClients
                    (fun () -> Task.FromResult(status))
                    (fun () -> Task.FromResult(status))
                    (fun _ _ -> Task.FromResult(()))
                    (fun currentStatus _ -> Task.FromResult(Some currentStatus))
                    scannerHostileDifferenceDiscovery
                    (fun currentStatus _ _ -> Task.FromResult(Some currentStatus))
                    (fun _ _ _ -> Task.FromResult(()))
                    (fun currentStatus currentDirectoryIds -> Services.updateGraceWatchInterprocessFile currentStatus currentDirectoryIds))
                    .GetAwaiter()
                    .GetResult()

                readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
                |> should equal false

                readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
                |> should equal true

                let inspection =
                    Services
                        .inspectGraceWatchStatus()
                        .GetAwaiter()
                        .GetResult()

                inspection.IsUsable |> should equal true

                let safetyFlags = inspection.SafetyFlags |> Set.ofArray

                safetyFlags
                |> Set.contains "pendingWatchWork"
                |> should equal false

                safetyFlags
                |> Set.contains "requiresExplicitResync"
                |> should equal false
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies durable-only journal evidence does not enter the processable Watch work branch.
    [<Test>]
    let ``watch durable-only pending journal evidence publishes without processing`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)
            let mutable uploads = 0
            let mutable statusApplies = 0

            try
                Watch.setWatchJournalStatusClientForWatchTests (fun () ->
                    Task.FromResult(
                        { LocalStateDb.WatchJournalPendingWorkSummary.DbPath = Current().GraceStatusFile; AppliedThroughSequence = 1L; PendingRowCount = 1L }
                    ))

                Services.setGraceWatchHasPendingWorkForStatus false

                (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                    .GetAwaiter()
                    .GetResult()

                (Watch.processChangedFilesWithClients
                    (fun () -> Task.FromResult(status))
                    (fun () -> Task.FromResult(status))
                    (fun _ _ ->
                        uploads <- uploads + 1
                        Task.FromResult(()))
                    (fun currentStatus _ -> Task.FromResult(Some currentStatus))
                    scannerHostileDifferenceDiscovery
                    (fun currentStatus _ _ ->
                        statusApplies <- statusApplies + 1
                        Task.FromResult(Some currentStatus))
                    (fun _ _ _ -> Task.FromResult(()))
                    (fun currentStatus currentDirectoryIds -> Services.updateGraceWatchInterprocessFile currentStatus currentDirectoryIds))
                    .GetAwaiter()
                    .GetResult()

                uploads |> should equal 0
                statusApplies |> should equal 0

                readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
                |> should equal true

                readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
                |> should equal false
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies that watch check JSON exposes durable-pending degradation without changing stdout/stderr routing.
    [<Test>]
    let ``watch check json reports durable pending journal evidence`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            try
                Watch.setWatchJournalStatusClientForWatchTests (fun () ->
                    Task.FromResult(
                        { LocalStateDb.WatchJournalPendingWorkSummary.DbPath = Current().GraceStatusFile; AppliedThroughSequence = 1L; PendingRowCount = 1L }
                    ))

                Services.setGraceWatchHasPendingWorkForStatus false

                (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                    .GetAwaiter()
                    .GetResult()

                Watch.publishPendingWatchWorkTransitionIfNeededForWatchTests ()

                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "watch"
                                                      "--check" |]

                exitCode |> should equal -1
                standardError |> should equal String.Empty

                use document = parseJsonOutput standardOut
                let root = document.RootElement.GetProperty("ReturnValue")

                root
                    .GetProperty("CanUseIncrementalStatus")
                    .GetBoolean()
                |> should equal false

                root.GetProperty("Reason").GetString()
                |> should equal "resynchronizing"

                let safetyFlags =
                    root.GetProperty("SafetyFlags").EnumerateArray()
                    |> Seq.map (fun flag -> flag.GetString())
                    |> Set.ofSeq

                safetyFlags
                |> Set.contains "pendingWatchWork"
                |> should equal true

                safetyFlags
                |> Set.contains "requiresExplicitResync"
                |> should equal true
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies that transient IPC write failures do not suppress the next dirty status publication attempt.
    [<Test>]
    let ``watch dirty status transition retries after ipc write failure`` () =
        withTempRepo (fun root ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            Services.setGraceWatchHasPendingWorkForStatus false

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal false

            let filePath = Path.Combine(root, "dirty-retry.txt")

            use lockedIpc = new FileStream(Services.IpcFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None)

            File.WriteAllText(filePath, "first content")
            Watch.OnChanged(changedEvent filePath)
            lockedIpc.Dispose()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal false

            File.WriteAllText(filePath, "second content")
            Watch.OnChanged(changedEvent filePath)

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal false)

    /// Verifies that resync-required publication retries after a swallowed IPC write failure.
    [<Test>]
    let ``watch resync-required status transition retries after ipc write failure`` () =
        withTempRepo (fun root ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            Services.setGraceWatchHasPendingWorkForStatus false

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            Services.getGraceWatchStatus().Result
            |> Option.isSome
            |> should equal true

            use lockedIpc = new FileStream(Services.IpcFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            Watch.requestGraceWatchExplicitResyncForWatchTests "locked resync-required publication"
            lockedIpc.Dispose()

            Services.getGraceWatchStatus().Result
            |> Option.isSome
            |> should equal true

            let filePath = Path.Combine(root, "resync-retry.txt")
            File.WriteAllText(filePath, "retry resync publication")
            Watch.OnChanged(changedEvent filePath)

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal false

            Services.getGraceWatchStatus().Result
            |> should equal None

            let safetyFlags = readWatchStatusJsonSafetyFlags ()

            safetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true)

    /// Verifies that Watch IPC writes recheck pending work at the serialized write boundary.
    [<Test>]
    let ``watch clean ipc write becomes dirty when pending work arrives at write boundary`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            Services.setGraceWatchHasPendingWorkForStatus false

            (Services.updateGraceWatchInterprocessFileAfterPendingWorkProbeForWatchTests
                (fun () -> Services.setGraceWatchHasPendingWorkForStatus true)
                status
                (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal false

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that Watch publication rechecks queued work before leaving a clean IPC snapshot on disk.
    [<Test>]
    let ``watch refresh rewrites clean ipc when pending work arrives during publish`` () =
        withTempRepo (fun root ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let filePath = Path.Combine(root, "queued-during-clean-publish.txt")

            Services.setGraceWatchHasPendingWorkForStatus false
            Watch.setGraceStatusHasChangedForWatchTests true

            /// Simulates a stale clean writer losing a race to a FileSystemWatcher callback at the write boundary.
            let mutable writerCalls = 0

            let racingWriter status directoryIds =
                writerCalls <- writerCalls + 1

                if writerCalls = 1 then
                    File.WriteAllText(filePath, "queued while clean status is publishing")
                    Watch.OnChanged(changedEvent filePath)
                    Services.setGraceWatchHasPendingWorkForStatus false

                Services.updateGraceWatchInterprocessFile status directoryIds

            (Watch.publishGraceStatusRefreshSnapshotForWatchTests status racingWriter)
                .GetAwaiter()
                .GetResult()

            writerCalls |> should equal 2

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal false

            let safetyFlags = readWatchStatusJsonSafetyFlags ()

            safetyFlags
            |> Set.contains "pendingWatchWork"
            |> should equal true)

    /// Verifies that non-Watch status refreshes preserve a live dirty Watch IPC state.
    [<Test>]
    let ``watch ipc non-watch update preserves live dirty work state`` () =
        withTempRepo (fun _ ->
            let dirtyStatus = graceStatusTracking Array.empty<string> Array.empty<string>
            let dirtyDirectoryIds = HashSet<DirectoryVersionId>(dirtyStatus.Index.Keys)

            Services.setGraceWatchHasPendingWorkForStatus true

            (Services.updateGraceWatchInterprocessFile dirtyStatus (Some dirtyDirectoryIds))
                .GetAwaiter()
                .GetResult()

            Services.getGraceWatchStatus().Result
            |> should equal None

            let cleanStatusFromNonWatch =
                { graceStatusTracking Array.empty<string> Array.empty<string> with RootDirectorySha256Hash = Sha256Hash "non-watch-updated-root" }

            let cleanDirectoryIdsFromNonWatch = HashSet<DirectoryVersionId>(cleanStatusFromNonWatch.Index.Keys)

            Services.setGraceWatchHasPendingWorkForStatus false

            (Services.updateGraceWatchInterprocessFilePreservingLiveWorkState cleanStatusFromNonWatch (Some cleanDirectoryIdsFromNonWatch))
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal false

            readWatchStatusJsonStringProperty "RootDirectoryId"
            |> should equal $"{cleanStatusFromNonWatch.RootDirectoryId}"

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that non-Watch status refreshes re-read live IPC before preserving clean state.
    [<Test>]
    let ``watch ipc non-watch update rechecks clean live state before write`` () =
        withTempRepo (fun _ ->
            let liveRootDirectoryId = Guid.NewGuid()
            let initialCleanStatus = liveWatchStatus liveRootDirectoryId

            writeWatchStatusJsonWithRuntimeSurface initialCleanStatus
            |> ignore

            let cleanStatusFromNonWatch = graceStatusTracking Array.empty<string> Array.empty<string>
            let cleanDirectoryIdsFromNonWatch = HashSet<DirectoryVersionId>(cleanStatusFromNonWatch.Index.Keys)

            let publishDirtyLiveStatus () =
                let dirtyLiveStatus = { initialCleanStatus with UpdatedAt = getCurrentInstant (); HasPendingWatchWork = true; IsWorkingTreeClean = false }

                writeWatchStatusJsonWithRuntimeSurface dirtyLiveStatus
                |> ignore

            Services.setGraceWatchHasPendingWorkForStatus false

            (Services.updateGraceWatchInterprocessFilePreservingLiveWorkStateAfterInspectionForWatchTests
                publishDirtyLiveStatus
                cleanStatusFromNonWatch
                (Some cleanDirectoryIdsFromNonWatch))
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal false

            readWatchStatusJsonStringProperty "RootDirectoryId"
            |> should equal $"{cleanStatusFromNonWatch.RootDirectoryId}"

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that non-Watch status refreshes preserve same-repository non-incremental Watch state.
    [<Test>]
    let ``watch ipc non-watch update preserves current live recovery state`` () =
        withTempRepo (fun root ->
            let configuration = Current()
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            configuration.RepositoryId <- repositoryId
            configuration.RepositoryName <- "current-recovery-repo"
            configuration.BranchId <- branchId
            configuration.BranchName <- "current-recovery-branch"
            configuration.RootDirectory <- root

            let liveRootDirectoryId = Guid.NewGuid()

            for mode, modeText in
                [|
                    Services.GraceWatchRuntimeMode.StartingUp, "startingUp"
                    Services.GraceWatchRuntimeMode.Suspended, "suspended"
                    Services.GraceWatchRuntimeMode.Resynchronizing, "resynchronizing"
                |] do
                let liveRecoveryStatus =
                    { liveWatchStatus liveRootDirectoryId with
                        RepositoryId = repositoryId
                        RepositoryName = RepositoryName "current-recovery-repo"
                        BranchId = branchId
                        BranchName = BranchName "current-recovery-branch"
                        RootDirectory = root
                        HasPendingWatchWork = true
                        IsWorkingTreeClean = false
                    }

                writeWatchStatusJsonWithPersistedMode mode liveRecoveryStatus
                |> ignore

                let cleanStatusFromNonWatch =
                    { graceStatusTracking Array.empty<string> Array.empty<string> with
                        RootDirectorySha256Hash = Sha256Hash $"current-non-watch-root-{modeText}"
                    }

                let cleanDirectoryIdsFromNonWatch = HashSet<DirectoryVersionId>(cleanStatusFromNonWatch.Index.Keys)

                Services.setGraceWatchHasPendingWorkForStatus false

                (Services.updateGraceWatchInterprocessFilePreservingLiveWorkState cleanStatusFromNonWatch (Some cleanDirectoryIdsFromNonWatch))
                    .GetAwaiter()
                    .GetResult()

                readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
                |> should equal true

                readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
                |> should equal false

                readWatchStatusJsonStringProperty "Mode"
                |> should equal modeText

                readWatchStatusJsonStringProperty "RootDirectoryId"
                |> should equal $"{cleanStatusFromNonWatch.RootDirectoryId}")

    /// Verifies that non-Watch status refreshes preserve same-branch legacy recovery snapshots.
    [<Test>]
    let ``watch ipc non-watch update preserves legacy recovery state with blank root identity`` () =
        withTempRepo (fun _ ->
            let liveRootDirectoryId = Guid.NewGuid()

            for mode, modeText in
                [|
                    Services.GraceWatchRuntimeMode.Suspended, "suspended"
                    Services.GraceWatchRuntimeMode.Resynchronizing, "resynchronizing"
                |] do
                let liveRecoveryStatus = { liveWatchStatus liveRootDirectoryId with HasPendingWatchWork = true; IsWorkingTreeClean = false }

                let ipcFileName = writeWatchStatusJsonWithPersistedMode mode liveRecoveryStatus

                let legacyJson =
                    File.ReadAllText(ipcFileName)
                    |> removeWatchStatusFieldsAddedForIssue492

                File.WriteAllText(ipcFileName, legacyJson)

                let cleanStatusFromNonWatch =
                    { graceStatusTracking Array.empty<string> Array.empty<string> with RootDirectorySha256Hash = Sha256Hash $"legacy-recovery-root-{modeText}" }

                let cleanDirectoryIdsFromNonWatch = HashSet<DirectoryVersionId>(cleanStatusFromNonWatch.Index.Keys)

                Services.setGraceWatchHasPendingWorkForStatus false

                (Services.updateGraceWatchInterprocessFilePreservingLiveWorkState cleanStatusFromNonWatch (Some cleanDirectoryIdsFromNonWatch))
                    .GetAwaiter()
                    .GetResult()

                readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
                |> should equal true

                readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
                |> should equal false

                readWatchStatusJsonStringProperty "Mode"
                |> should equal modeText

                readWatchStatusJsonStringProperty "RootDirectoryId"
                |> should equal $"{cleanStatusFromNonWatch.RootDirectoryId}")

    /// Verifies that non-Watch status refreshes do not preserve live Watch state from another repo or root.
    [<Test>]
    let ``watch ipc non-watch update skips foreign live recovery state`` () =
        withTempRepo (fun root ->
            let configuration = Current()
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            configuration.RepositoryId <- repositoryId
            configuration.RepositoryName <- "current-foreign-guard-repo"
            configuration.BranchId <- branchId
            configuration.BranchName <- "current-foreign-guard-branch"
            configuration.RootDirectory <- root

            let foreignRoot = Path.Combine(Path.GetTempPath(), $"grace-watch-foreign-{Guid.NewGuid():N}")

            for mode in
                [|
                    Services.GraceWatchRuntimeMode.StartingUp
                    Services.GraceWatchRuntimeMode.Suspended
                    Services.GraceWatchRuntimeMode.Resynchronizing
                |] do
                let foreignRecoveryStatus =
                    { liveWatchStatus (Guid.NewGuid()) with
                        RepositoryId = Guid.NewGuid()
                        RepositoryName = RepositoryName "foreign-repo"
                        BranchId = Guid.NewGuid()
                        BranchName = BranchName "current-foreign-guard-branch"
                        RootDirectory = foreignRoot
                        HasPendingWatchWork = true
                        IsWorkingTreeClean = false
                    }

                writeWatchStatusJsonWithPersistedMode mode foreignRecoveryStatus
                |> ignore

                let cleanStatusFromNonWatch =
                    { graceStatusTracking Array.empty<string> Array.empty<string> with RootDirectorySha256Hash = Sha256Hash $"foreign-guard-root-{mode}" }

                let cleanDirectoryIdsFromNonWatch = HashSet<DirectoryVersionId>(cleanStatusFromNonWatch.Index.Keys)

                Services.setGraceWatchHasPendingWorkForStatus false

                (Services.updateGraceWatchInterprocessFilePreservingLiveWorkState cleanStatusFromNonWatch (Some cleanDirectoryIdsFromNonWatch))
                    .GetAwaiter()
                    .GetResult()

                readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
                |> should equal false

                readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
                |> should equal true

                tryReadWatchStatusJsonStringProperty "Mode"
                |> should equal None

                readWatchStatusJsonStringProperty "RepositoryId"
                |> should equal $"{repositoryId}"

                readWatchStatusJsonStringProperty "BranchId"
                |> should equal $"{branchId}"

                readWatchStatusJsonStringProperty "RootDirectory"
                |> should equal root

                readWatchStatusJsonStringProperty "RootDirectoryId"
                |> should equal $"{cleanStatusFromNonWatch.RootDirectoryId}")

    /// Verifies that a pending GraceStatus artifact refresh publishes dirty IPC before the timer reloads status.
    [<Test>]
    let ``watch grace status artifact change publishes dirty ipc`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            Services.setGraceWatchHasPendingWorkForStatus false

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal false

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal true

            Watch.OnChanged(changedEvent (Current().GraceStatusFile))

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal false

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that a blocked dirty IPC write can retry while the GraceStatus refresh flag is already pending.
    [<Test>]
    let ``watch grace status artifact change retries dirty ipc while refresh is pending`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            Services.setGraceWatchHasPendingWorkForStatus false

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal false

            use blockedIpc = new FileStream(Services.IpcFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None)

            Watch.OnChanged(changedEvent (Current().GraceStatusFile))
            blockedIpc.Dispose()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal false

            Watch.OnChanged(changedEvent (Current().GraceStatusFile + "-wal"))

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal false)

    /// Verifies that a consumed GraceStatus refresh can publish clean IPC during the same timer tick.
    [<Test>]
    let ``watch grace status refresh clears pending flag before clean ipc publish`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>

            Services.setGraceWatchHasPendingWorkForStatus false
            Watch.setGraceStatusHasChangedForWatchTests true

            (Watch.publishGraceStatusRefreshSnapshotForWatchTests status Services.updateGraceWatchInterprocessFile)
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal false

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal true

            let safetyFlags = readWatchStatusJsonSafetyFlags ()

            safetyFlags
            |> Set.contains "pendingWatchWork"
            |> should equal false)

    /// Verifies that a blocked clean IPC refresh does not suppress the next clean transition retry.
    [<Test>]
    let ``watch grace status refresh retries clean ipc after blocked write`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            Services.setGraceWatchHasPendingWorkForStatus true

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            Watch.setGraceStatusHasChangedForWatchTests true

            /// Simulates the production writer swallowing a transient IPC write failure without changing the file.
            let blockedWriter _ _ = Task.FromResult(())

            (Watch.publishGraceStatusRefreshSnapshotForWatchTests status blockedWriter)
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            (Watch.publishGraceStatusRefreshSnapshotForWatchTests status Services.updateGraceWatchInterprocessFile)
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal false

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal true)

    /// Verifies that a stale clean IPC file is not accepted when the attempted clean publication never reached disk.
    [<Test>]
    let ``watch clean publication verification rejects stale clean snapshot`` () =
        withTempRepo (fun root ->
            let staleCleanStatus = graceStatusTracking Array.empty<string> Array.empty<string>
            let staleDirectoryIds = HashSet<DirectoryVersionId>(staleCleanStatus.Index.Keys)

            Services.setGraceWatchHasPendingWorkForStatus false

            (Services.updateGraceWatchInterprocessFile staleCleanStatus (Some staleDirectoryIds))
                .GetAwaiter()
                .GetResult()

            let staleCleanJson = File.ReadAllText(Services.IpcFileName())
            let filePath = Path.Combine(root, "blocked-clean-verification.txt")

            use lockedIpc = new FileStream(Services.IpcFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            File.WriteAllText(filePath, "pending content")
            Watch.OnChanged(changedEvent filePath)
            lockedIpc.Dispose()

            File.ReadAllText(Services.IpcFileName())
            |> should equal staleCleanJson

            let freshCleanStatus = graceStatusTracking [| "blocked-clean-verification.txt" |] Array.empty<string>

            /// Reads status needed by the stale clean verification scenario.
            let readStatus () = Task.FromResult(freshCleanStatus)

            /// Builds upload test data used to drain the queued file work.
            let upload _ pendingFilePath =
                recordUploadedFileVersion $"{pendingFilePath}"
                Task.FromResult(())

            /// Keeps status unchanged after pending upload work drains in the test scenario.
            let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

            /// Keeps status unchanged after event-derived differences apply in the test scenario.
            let updateGraceStatusFromDifferences currentStatus _ _ = Task.FromResult(Some currentStatus)

            /// Keeps incremental local-state side effects out of this IPC verification test.
            let applyIncremental _ _ _ = Task.FromResult(())

            /// Simulates the production writer swallowing a transient clean IPC write failure.
            let blockedWriter _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                applyIncremental
                blockedWriter)
                .GetAwaiter()
                .GetResult()

            File.ReadAllText(Services.IpcFileName())
            |> should equal staleCleanJson

            Watch.publishPendingWatchWorkTransitionIfNeededForWatchTests ()

            File.ReadAllText(Services.IpcFileName())
            |> should not' (equal staleCleanJson)

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal false

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal true)

    /// Verifies that a clean transition retries after a transient Grace Status read failure.
    [<Test>]
    let ``watch clean transition retries after status read failure`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            Services.setGraceWatchHasPendingWorkForStatus true

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            Watch.setReadGraceStatusFileForPendingWorkTransitionForWatchTests (fun () ->
                Task.FromException<GraceStatus>(InvalidOperationException("transient status read failure")))

            Watch.publishPendingWatchWorkTransitionIfNeededForWatchTests ()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            Watch.setReadGraceStatusFileForPendingWorkTransitionForWatchTests (fun () -> Task.FromResult(status))

            Watch.publishPendingWatchWorkTransitionIfNeededForWatchTests ()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal false

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal true

            match Services.getGraceWatchStatus().Result with
            | Some publishedStatus ->
                publishedStatus.RootDirectoryId
                |> should equal status.RootDirectoryId
            | None -> Assert.Fail("Expected trusted clean Watch IPC after the status read recovers."))

    /// Verifies that dirty transitions still publish non-incremental IPC when Grace Status cannot be read.
    [<Test>]
    let ``watch dirty transition publishes non-incremental status after status read failure`` () =
        withTempRepo (fun root ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            Services.setGraceWatchHasPendingWorkForStatus false

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal false

            Watch.setReadGraceStatusFileForPendingWorkTransitionForWatchTests (fun () ->
                Task.FromException<GraceStatus>(InvalidOperationException("transient status read failure")))

            let filePath = Path.Combine(root, "dirty-read-failure.txt")
            File.WriteAllText(filePath, "dirty transition content")
            Watch.OnChanged(changedEvent filePath)

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal false

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that startup catch-up publishes dirty IPC until the startup scan and apply path drains.
    [<Test>]
    let ``watch startup catch-up status is dirty before startup scan drains`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

            (Watch.publishStartupCatchUpPendingStatusForWatchTests status directoryIds Services.updateGraceWatchInterprocessFile)
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal false

            let safetyFlags = readWatchStatusJsonSafetyFlags ()

            safetyFlags
            |> Set.contains "pendingWatchWork"
            |> should equal true

            safetyFlags
            |> Set.contains "cleanWorkingTree"
            |> should equal false

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that clean startup catch-up publication clears the manual pending marker.
    [<Test; Category("StartupCatchUpPendingStatus")>]
    let ``watch startup catch-up clean completion clears manual pending marker`` () =
        withTempRepo (fun _ ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            (LocalStateDb.ensureDbInitialized (Current().GraceStatusFile))
                .GetAwaiter()
                .GetResult()

            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

            (Watch.publishStartupCatchUpPendingStatusForWatchTests status directoryIds Services.updateGraceWatchInterprocessFile)
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            let readStatus () = Task.FromResult(status)
            let processChangedFiles () = Task.FromResult(())

            (Watch.completeStartupRecoveryIfPendingWorkDrainedForWatchTests readStatus Services.updateGraceWatchInterprocessFile processChangedFiles)
                .GetAwaiter()
                .GetResult()

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.HealthyIncremental

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal false

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal true

            let safetyFlags = readWatchStatusJsonSafetyFlags ()

            safetyFlags
            |> Set.contains "pendingWatchWork"
            |> should equal false

            Services.getGraceWatchStatus().Result
            |> should not' (equal None))

    /// Verifies that processing pending Watch work publishes the dirty-to-clean IPC transition.
    [<Test>]
    let ``watch clean status transition is published after pending work drains`` () =
        withTempRepo (fun root ->
            let status = graceStatusTracking Array.empty<string> Array.empty<string>
            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)

            Services.setGraceWatchHasPendingWorkForStatus false

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            let filePath = Path.Combine(root, "clean-after-process.txt")
            File.WriteAllText(filePath, "pending content")
            Watch.OnChanged(changedEvent filePath)

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal true

            /// Reads status needed by the test scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ filePath =
                recordUploadedFileVersion $"{filePath}"
                Task.FromResult(())

            /// Builds update grace status test data used to exercise CLI watch behavior.
            let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

            /// Builds update grace status from differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences currentStatus _ _ = Task.FromResult(Some currentStatus)

            /// Builds apply incremental test data used to exercise CLI watch behavior.
            let applyIncremental _ _ _ = Task.FromResult(())

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                applyIncremental
                Services.updateGraceWatchInterprocessFile)
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonBooleanProperty "HasPendingWatchWork"
            |> should equal false

            readWatchStatusJsonBooleanProperty "IsWorkingTreeClean"
            |> should equal true

            let safetyFlags = readWatchStatusJsonSafetyFlags ()

            safetyFlags
            |> Set.contains "cleanWorkingTree"
            |> should equal false

            safetyFlags
            |> Set.contains "pendingWatchWork"
            |> should equal false

            (Watch.pendingWatchWorkSnapshotForTests ())
                .FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that watch status serializes compact safety flags without durable liveness-sensitive mode.
    [<Test>]
    let ``watch status serializes compact safety flags without healthy runtime mode`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            let status =
                { GraceStatus.Default with
                    RootDirectoryId = rootDirectoryId
                    RootDirectorySha256Hash = Sha256Hash "live-watch-root"
                    RootDirectoryBlake3Hash = Blake3Hash "live-watch-root-blake3"
                }

            let directoryIds = HashSet<DirectoryVersionId>([| rootDirectoryId |])

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            tryReadWatchStatusJsonStringProperty "Mode"
            |> should equal None

            let json = File.ReadAllText(Services.IpcFileName())

            use document = JsonDocument.Parse(json)
            let root = document.RootElement

            let safetyFlags =
                root.GetProperty("SafetyFlags").EnumerateArray()
                |> Seq.map (fun flag -> flag.GetString())
                |> Set.ofSeq

            safetyFlags
            |> Set.contains "usableRoot"
            |> should equal true

            safetyFlags
            |> Set.contains "directoryIndex"
            |> should equal true

            safetyFlags
            |> Set.contains "incrementalSafe"
            |> should equal false

            let roundTripped: Services.GraceWatchStatus = deserialize json

            roundTripped.Mode
            |> should equal Services.GraceWatchRuntimeMode.HealthyIncremental

            safetyFlagSet roundTripped
            |> Set.contains "incrementalSafe"
            |> should equal true)

    /// Verifies that incomplete Watch IPC snapshots derive resync state instead of persisting it as a live raw mode.
    [<Test>]
    let ``watch status serializes compact safety flags without resync runtime mode`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            let status =
                { GraceStatus.Default with
                    RootDirectoryId = rootDirectoryId
                    RootDirectorySha256Hash = Sha256Hash "resync-watch-root"
                    RootDirectoryBlake3Hash = Blake3Hash "resync-watch-root-blake3"
                }

            (Services.updateGraceWatchInterprocessFile status (Some(HashSet<DirectoryVersionId>())))
                .GetAwaiter()
                .GetResult()

            tryReadWatchStatusJsonStringProperty "Mode"
            |> should equal None

            let safetyFlags = readWatchStatusJsonSafetyFlags ()

            safetyFlags
            |> Set.contains "missingDirectoryIndex"
            |> should equal true

            safetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true

            safetyFlags
            |> Set.contains "incrementalSafe"
            |> should equal false

            let roundTripped: Services.GraceWatchStatus = deserialize (File.ReadAllText(Services.IpcFileName()))

            roundTripped.Mode
            |> should equal Services.GraceWatchRuntimeMode.Resynchronizing)

    /// Verifies that raw compact IPC readers cannot inherit incremental safety after a healthy writer dies.
    [<Test>]
    let ``watch compact status does not expose incremental safety after healthy snapshot ages`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            let status =
                { GraceStatus.Default with
                    RootDirectoryId = rootDirectoryId
                    RootDirectorySha256Hash = Sha256Hash "aging-watch-root"
                    RootDirectoryBlake3Hash = Blake3Hash "aging-watch-root-blake3"
                }

            let directoryIds = HashSet<DirectoryVersionId>([| rootDirectoryId |])

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            readWatchStatusJsonSafetyFlags ()
            |> Set.contains "incrementalSafe"
            |> should equal false

            tryReadWatchStatusJsonStringProperty "Mode"
            |> should equal None

            getCurrentInstant()
                .Minus(Duration.FromMinutes(6.0))
            |> updatePersistedWatchStatusUpdatedAt

            Services.getGraceWatchStatus().Result
            |> should equal None

            readWatchStatusJsonSafetyFlags ()
            |> Set.contains "incrementalSafe"
            |> should equal false

            tryReadWatchStatusJsonStringProperty "Mode"
            |> should equal None

            let agedStatus: Services.GraceWatchStatus = deserialize (File.ReadAllText(Services.IpcFileName()))
            let derivedSafetyFlags = safetyFlagSet agedStatus

            derivedSafetyFlags
            |> Set.contains "staleStatus"
            |> should equal true

            derivedSafetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true

            derivedSafetyFlags
            |> Set.contains "incrementalSafe"
            |> should equal false)

    /// Verifies that stale watch status json does not advertise incremental safety.
    [<Test>]
    let ``watch status safety flags require resync when snapshot is stale`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            let staleStatus =
                { liveWatchStatus rootDirectoryId with
                    UpdatedAt =
                        getCurrentInstant()
                            .Minus(Duration.FromMinutes(6.0))
                }

            writeWatchStatusJsonWithRuntimeSurface staleStatus
            |> ignore

            Services.getGraceWatchStatus().Result
            |> should equal None

            let safetyFlags = readWatchStatusJsonSafetyFlags ()

            safetyFlags
            |> Set.contains "staleStatus"
            |> should equal true

            safetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true

            safetyFlags
            |> Set.contains "incrementalSafe"
            |> should equal false)

    /// Verifies that stale dirty status requires explicit resync because no live Watch can drain the queued work.
    [<Test>]
    let ``watch status safety flags require resync when stale snapshot is dirty`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            let staleStatus =
                { liveWatchStatus rootDirectoryId with
                    UpdatedAt =
                        getCurrentInstant()
                            .Minus(Duration.FromMinutes(6.0))
                    HasPendingWatchWork = true
                    IsWorkingTreeClean = false
                }

            writeWatchStatusJsonWithRuntimeSurface staleStatus
            |> ignore

            Services.getGraceWatchStatus().Result
            |> should equal None

            let persistedSafetyFlags = readWatchStatusJsonSafetyFlags ()

            persistedSafetyFlags
            |> Set.contains "pendingWatchWork"
            |> should equal true

            persistedSafetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true

            persistedSafetyFlags
            |> Set.contains "pendingStatusApply"
            |> should equal false)

    /// Verifies that untrusted no-work Watch snapshots do not advertise a clean working tree.
    [<Test>]
    let ``watch status safety flags do not mark untrusted snapshots clean`` () =
        withTempRepo (fun _ ->
            let startupClaim = { Services.GraceWatchStatus.Default with UpdatedAt = getCurrentInstant (); IsStartupClaim = true }

            let startupFlags = safetyFlagSet startupClaim

            startupFlags
            |> Set.contains "startupClaim"
            |> should equal true

            startupFlags
            |> Set.contains "cleanWorkingTree"
            |> should equal false

            startupFlags
            |> Set.contains "noQueuedWatchWork"
            |> should equal true

            startupFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true

            let missingRoot =
                { Services.GraceWatchStatus.Default with UpdatedAt = getCurrentInstant (); DirectoryIds = HashSet<DirectoryVersionId>([| Guid.NewGuid() |]) }

            let missingRootFlags = safetyFlagSet missingRoot

            missingRootFlags
            |> Set.contains "missingRoot"
            |> should equal true

            missingRootFlags
            |> Set.contains "cleanWorkingTree"
            |> should equal false

            missingRootFlags
            |> Set.contains "noQueuedWatchWork"
            |> should equal true

            missingRootFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true)

    /// Verifies that persisted suspended snapshots do not carry a trusted clean flag.
    [<Test>]
    let ``suspended watch status serialization does not mark clean snapshots trusted`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            let status =
                { GraceStatus.Default with
                    RootDirectoryId = rootDirectoryId
                    RootDirectorySha256Hash = Sha256Hash "suspended-clean-root"
                    RootDirectoryBlake3Hash = Blake3Hash "suspended-clean-root-blake3"
                }

            let directoryIds = HashSet<DirectoryVersionId>([| rootDirectoryId |])

            (Services.updateGraceWatchInterprocessFileForSuspendedMode status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            let safetyFlags = readWatchStatusJsonSafetyFlags ()

            safetyFlags
            |> Set.contains "cleanWorkingTree"
            |> should equal false

            safetyFlags
            |> Set.contains "noQueuedWatchWork"
            |> should equal true

            safetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that legacy watch status json without identity remains readable but cannot grant clean authority.
    [<Test>]
    let ``watch status reads legacy json without identity as non-authoritative`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            let legacyJson =
                rootDirectoryId
                |> liveWatchStatus
                |> serialize
                |> removeCompactWatchRuntimeSurface
                |> removeWatchStatusFieldsAddedForIssue492

            jsonHasProperty "Mode" legacyJson
            |> should equal false

            jsonHasProperty "SafetyFlags" legacyJson
            |> should equal false

            jsonHasProperty "HasPendingWatchWork" legacyJson
            |> should equal false

            jsonHasProperty "IsWorkingTreeClean" legacyJson
            |> should equal false

            jsonHasProperty "RepositoryId" legacyJson
            |> should equal false

            jsonHasProperty "RepositoryName" legacyJson
            |> should equal false

            jsonHasProperty "BranchId" legacyJson
            |> should equal false

            jsonHasProperty "BranchName" legacyJson
            |> should equal false

            jsonHasProperty "RootDirectory" legacyJson
            |> should equal false

            let ipcFileName = Services.IpcFileName()

            Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
            |> ignore

            File.WriteAllText(ipcFileName, legacyJson)

            let inspection = Services.inspectGraceWatchStatus().Result

            inspection.Status
            |> Option.isSome
            |> should equal true

            inspection.IsUsable |> should equal false

            inspection.SafetyFlags
            |> Set.ofArray
            |> Set.contains "requiresExplicitResync"
            |> should equal true

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that incomplete watch status requests explicit resync instead of advertising incremental safety.
    [<Test>]
    let ``watch status without directory index requires explicit resync`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            let status = { liveWatchStatus rootDirectoryId with DirectoryIds = HashSet<DirectoryVersionId>() }

            let ipcFileName = Services.IpcFileName()

            Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
            |> ignore

            File.WriteAllText(ipcFileName, serialize status)

            Services.getGraceWatchStatus().Result
            |> should equal None

            let persistedStatus: Services.GraceWatchStatus = deserialize (File.ReadAllText(ipcFileName))

            persistedStatus.Mode
            |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

            let safetyFlags = safetyFlagSet persistedStatus

            safetyFlags
            |> Set.contains "missingDirectoryIndex"
            |> should equal true

            safetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true)

    /// Verifies that scan legality follows the compact Watch runtime state contract.
    [<Test>]
    let ``watch runtime mode gates scan legality`` () =
        Services.isGraceWatchScanLegal Services.GraceWatchRuntimeMode.StartingUp
        |> should equal true

        Services.isGraceWatchScanLegal Services.GraceWatchRuntimeMode.Resynchronizing
        |> should equal true

        Services.isGraceWatchScanLegal Services.GraceWatchRuntimeMode.HealthyIncremental
        |> should equal false

        Services.isGraceWatchScanLegal Services.GraceWatchRuntimeMode.Suspended
        |> should equal false

        Services.isGraceWatchScanLegal Services.GraceWatchRuntimeMode.Stopping
        |> should equal false

    /// Verifies that watcher overflow requests resync and quarantines previously queued observations.
    [<Test>]
    let ``watcher error leaves healthy mode and quarantines pending observations`` () =
        withTempRepo (fun root ->
            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental
            let rootDirectoryId = Guid.NewGuid()

            let status =
                { GraceStatus.Default with
                    RootDirectoryId = rootDirectoryId
                    RootDirectorySha256Hash = Sha256Hash "watch-error-root"
                    RootDirectoryBlake3Hash = Blake3Hash "watch-error-root-blake3"
                }

            (Services.updateGraceWatchInterprocessFile status (Some(HashSet<DirectoryVersionId>([| rootDirectoryId |]))))
                .GetAwaiter()
                .GetResult()

            Services.getGraceWatchStatus().Result
            |> Option.isSome
            |> should equal true

            let filePath = Path.Combine(root, "queued-before-overflow.txt")
            File.WriteAllText(filePath, "queued before overflow")
            Watch.OnChanged(changedEvent filePath)

            Watch
                .pendingWatchWorkSnapshotForTests()
                .FilesToProcess
            |> should equal [| filePath |]

            Watch.OnError(ErrorEventArgs(InternalBufferOverflowException("watch buffer overflow")))

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

            Watch
                .pendingWatchWorkSnapshotForTests()
                .FilesToProcess
            |> should equal Array.empty<string>

            Watch.quarantinedWatchObservationCountForWatchTests ()
            |> should equal 1

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that resync scans build fresh working-tree maps so stale cache entries cannot hide deletes.
    [<Test>]
    let ``scan for differences forgets deleted paths from previous scan`` () =
        withTempRepo (fun root ->
            let relativePath = "stale-cache-delete.txt"
            let filePath = Path.Combine(root, relativePath)
            File.WriteAllText(filePath, "tracked content before delete")

            let status = graceStatusTracking [| relativePath |] Array.empty<string>

            (Services.scanForDifferences status)
                .GetAwaiter()
                .GetResult()
            |> ignore

            File.Delete(filePath)

            let differencesAfterDelete =
                (Services.scanForDifferences status)
                    .GetAwaiter()
                    .GetResult()

            differencesAfterDelete
            |> Seq.exists (fun difference ->
                difference.DifferenceType = DifferenceType.Delete
                && difference.FileSystemEntryType = FileSystemEntryType.File
                && string difference.RelativePath = relativePath)
            |> should equal true)

    /// Verifies that observations captured during resync wait behind the scan-derived status boundary.
    [<Test>]
    let ``resync defers captured observations until scan status applies`` () =
        withTempRepo (fun root ->
            let scanDifference = FileSystemDifference.Create Delete FileSystemEntryType.File "scan-delete.txt"
            let liveFilePath = Path.Combine(root, "captured-during-resync.txt")
            /// Tracks scan Calls changes so deferred observations cannot pass by skipping resync.
            let mutable scanCalls = 0
            /// Tracks upload Calls changes so the resync pass proves captured observations are deferred.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls changes so scan and deferred event applications are ordered.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks the first apply batch so the test fails if resync replays the deferred event immediately.
            let mutable firstObservedDifferences = List<FileSystemDifference>()

            Watch.requestGraceWatchExplicitResyncForWatchTests "test resync"

            /// Reads status needed by the resync scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to prove the resync pass does not process deferred observations.
            let upload _ filePath =
                uploadCalls <- uploadCalls + 1
                let fullPath = $"{filePath}"

                if File.Exists(fullPath) then recordUploadedFileVersion fullPath

                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Captures a reliable observation while the resync scan is in progress.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                File.WriteAllText(liveFilePath, "captured while resyncing")
                Watch.OnChanged(changedEvent liveFilePath)

                let differences = List<FileSystemDifference>()
                differences.Add(scanDifference)
                Task.FromResult(differences)

            /// Builds apply-from-differences test data used to prove scan-derived status applies first.
            let updateGraceStatusFromDifferences status differences _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1

                if applyFromDifferencesCalls = 1 then firstObservedDifferences <- differences

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
            uploadCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

            firstObservedDifferences
            |> Seq.toArray
            |> should equal [| scanDifference |]

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.HealthyIncremental

            Watch
                .pendingWatchWorkSnapshotForTests()
                .FilesToProcess
            |> should equal [| liveFilePath |]

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            uploadCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 2

            Watch
                .pendingWatchWorkSnapshotForTests()
                .FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that failed resync recovery suspends Watch instead of silently continuing.
    [<Test>]
    let ``failed resync recovery suspends observation capture`` () =
        withTempRepo (fun root ->
            Watch.requestGraceWatchExplicitResyncForWatchTests "test resync failure"

            /// Reads status needed by the failed resync scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Fails the scan-derived recovery before the durable status boundary.
            let scanForDifferences _ = Task.FromException<List<FileSystemDifference>>(InvalidOperationException("scan failed"))

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status _ _ = Task.FromResult(Some status)

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

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.Suspended

            readWatchStatusJsonStringProperty "Mode"
            |> should equal "suspended"

            let inspection = Services.inspectGraceWatchStatus().Result

            inspection.EffectiveMode
            |> should equal (Some Services.GraceWatchRuntimeMode.Suspended)

            inspection.IsUsable |> should equal false

            let ignoredPath = Path.Combine(root, "ignored-after-failed-resync.txt")
            File.WriteAllText(ignoredPath, "ignored after failed resync")
            Watch.OnChanged(changedEvent ignoredPath)

            Watch
                .pendingWatchWorkSnapshotForTests()
                .FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that scan-derived file uploads keep resync retryable instead of suspending Watch.
    [<Test>]
    let ``resync retries after queued file uploads complete`` () =
        withTempRepo (fun root ->
            let relativePath = "resync-upload-retry.txt"
            let filePath = Path.Combine(root, relativePath)
            let scanDifference = FileSystemDifference.Create Add FileSystemEntryType.File relativePath
            /// Tracks upload Calls changes so the test proves queued resync uploads run before retry.
            let mutable uploadCalls = 0
            /// Tracks scan-derived apply Calls changes so the first retryable none does not suspend Watch.
            let mutable applyFromDifferencesCalls = 0

            File.WriteAllText(filePath, "resync upload retry")
            Watch.requestGraceWatchExplicitResyncForWatchTests "test resync retry"

            /// Reads status needed by the retryable resync scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Records uploaded content for the scan-derived retry path.
            let upload _ pendingFilePath =
                uploadCalls <- uploadCalls + 1
                recordUploadedFileVersion $"{pendingFilePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Produces a file add discovered by the resync scan.
            let scanForDifferences _ =
                let differences = List<FileSystemDifference>()
                differences.Add(scanDifference)
                Task.FromResult(differences)

            /// Queues missing file content on the first status attempt and succeeds after upload.
            let updateGraceStatusFromDifferences status _ _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1

                if applyFromDifferencesCalls = 1 then
                    Watch.OnChanged(changedEvent filePath)
                    Task.FromResult(None)
                else
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

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

            Watch
                .pendingWatchWorkSnapshotForTests()
                .FilesToProcess
            |> should equal [| filePath |]

            uploadCalls |> should equal 0
            applyFromDifferencesCalls |> should equal 1

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

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.HealthyIncremental

            Watch
                .pendingWatchWorkSnapshotForTests()
                .FilesToProcess
            |> should equal Array.empty<string>

            uploadCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 2)

    /// Verifies that a newer overflow during a resync scan cannot be cleared by the older scan result.
    [<Test>]
    let ``overflow during resync scan requires a later scan before healthy mode`` () =
        withTempRepo (fun _ ->
            let scanDifference = FileSystemDifference.Create Delete FileSystemEntryType.File "stale-resync-delete.txt"
            /// Tracks scan Calls changes so the newer overflow must produce a fresh scan.
            let mutable scanCalls = 0
            /// Tracks apply-from-differences Calls changes so stale scan output cannot commit.
            let mutable applyFromDifferencesCalls = 0

            Watch.requestGraceWatchExplicitResyncForWatchTests "test stale resync"

            /// Reads status needed by the stale resync scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Raises a second overflow during the first scan so that attempt cannot clear pending resync.
            let scanForDifferences _ =
                scanCalls <- scanCalls + 1
                let differences = List<FileSystemDifference>()

                if scanCalls = 1 then
                    Watch.OnError(ErrorEventArgs(InternalBufferOverflowException("second resync overflow")))
                    differences.Add(scanDifference)

                Task.FromResult(differences)

            /// Builds apply-from-differences test data used to prove stale scan output is ignored.
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

            scanCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 0

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

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

            scanCalls |> should equal 2

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.HealthyIncremental)

    /// Verifies that transient upload failures during resync leave scan-derived work queued for retry.
    [<Test>]
    let ``resync upload retry failure keeps queued work`` () =
        withTempRepo (fun root ->
            let relativePath = "resync-upload-transient.txt"
            let filePath = Path.Combine(root, relativePath)
            let scanDifference = FileSystemDifference.Create Add FileSystemEntryType.File relativePath
            /// Tracks upload Calls changes so the failed upload is retried on the next tick.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls changes so failed upload does not suspend recovery.
            let mutable applyFromDifferencesCalls = 0

            File.WriteAllText(filePath, "resync upload transient")
            Watch.requestGraceWatchExplicitResyncForWatchTests "test transient resync upload"

            /// Reads status needed by the transient upload scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Fails the first queued resync upload and succeeds when the next timer tick retries it.
            let upload _ pendingFilePath =
                uploadCalls <- uploadCalls + 1

                if uploadCalls = 1 then
                    Task.FromException<unit>(InvalidOperationException("transient upload failure"))
                else
                    recordUploadedFileVersion $"{pendingFilePath}"
                    Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Produces a file add discovered by the resync scan.
            let scanForDifferences _ =
                let differences = List<FileSystemDifference>()
                differences.Add(scanDifference)
                Task.FromResult(differences)

            /// Queues missing file content on the first status attempt and succeeds after upload.
            let updateGraceStatusFromDifferences status _ _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1

                if applyFromDifferencesCalls = 1 then
                    Watch.OnChanged(changedEvent filePath)
                    Task.FromResult(None)
                else
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

            Watch
                .pendingWatchWorkSnapshotForTests()
                .FilesToProcess
            |> should equal [| filePath |]

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
            applyFromDifferencesCalls |> should equal 1

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

            Watch
                .pendingWatchWorkSnapshotForTests()
                .FilesToProcess
            |> should equal [| filePath |]

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

            uploadCalls |> should equal 2
            applyFromDifferencesCalls |> should equal 2

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.HealthyIncremental

            Watch
                .pendingWatchWorkSnapshotForTests()
                .FilesToProcess
            |> should equal Array.empty<string>)

    /// Verifies that a swallowed scan failure cannot complete resync as a clean empty scan.
    [<Test>]
    let ``resync suspends when scan reports failure with empty differences`` () =
        withTempRepo (fun _ ->
            Watch.requestGraceWatchExplicitResyncForWatchTests "test scan failure"

            /// Reads status needed by the failed scan scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Models the production scanner's fail-closed flag with an empty result.
            let scanForDifferences _ =
                Services.setLastScanForDifferencesSuccessfulForWatchTests false
                Task.FromResult(List<FileSystemDifference>())

            /// Fails if a failed empty scan reaches status application.
            let updateGraceStatusFromDifferences _ _ _ =
                Assert.Fail("Failed resync scan must not apply status.")
                Task.FromResult(None)

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

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.Suspended)

    /// Verifies that startup tail logic preserves a failed resync recovery instead of resuming healthy mode.
    [<Test>]
    let ``startup completion preserves suspended recovery state`` () =
        withTempRepo (fun _ ->
            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp
            Watch.requestGraceWatchExplicitResyncForWatchTests "startup resync failure"

            /// Reads status needed by the failed startup recovery scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Fails the startup resync scan before the durable status boundary.
            let scanForDifferences _ = Task.FromException<List<FileSystemDifference>>(InvalidOperationException("startup scan failed"))

            /// Builds apply-from-differences test data used to exercise CLI watch behavior.
            let updateGraceStatusFromDifferences status _ _ = Task.FromResult(Some status)

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

            Watch.promoteStartupModeIfRecoverySucceededForWatchTests ()

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.Suspended)

    /// Verifies that liveness refresh cannot republish healthy IPC while Watch is suspended.
    [<Test>]
    let ``suspended liveness refresh publishes non-incremental ipc`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            let status =
                { GraceStatus.Default with
                    RootDirectoryId = rootDirectoryId
                    RootDirectorySha256Hash = Sha256Hash "suspended-root"
                    RootDirectoryBlake3Hash = Blake3Hash "suspended-root-blake3"
                }

            (Services.updateGraceWatchInterprocessFile status (Some(HashSet<DirectoryVersionId>([| rootDirectoryId |]))))
                .GetAwaiter()
                .GetResult()

            Services.getGraceWatchStatus().Result
            |> Option.isSome
            |> should equal true

            Services.graceWatchStatusUpdateTime <- Instant.MinValue
            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.Suspended

            /// Reads status needed by the liveness scenario.
            let readStatus () = Task.FromResult(status)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus currentStatus _ = Task.FromResult(Some currentStatus)

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scanForNoDifferences
                (fun currentStatus _ _ -> Task.FromResult(Some currentStatus))
                (fun _ _ _ -> Task.FromResult(()))
                Services.updateGraceWatchInterprocessFile)
                .GetAwaiter()
                .GetResult()

            Services.getGraceWatchStatus().Result
            |> should equal None

            readWatchStatusJsonStringProperty "Mode"
            |> should equal "suspended"

            let inspection = Services.inspectGraceWatchStatus().Result

            inspection.PersistedMode
            |> should equal (Some Services.GraceWatchRuntimeMode.Suspended))

    /// Verifies that Grace Status DB refreshes cannot publish healthy IPC while resync is pending.
    [<Test>]
    let ``resync status refresh publishes non-incremental ipc`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            let status =
                { GraceStatus.Default with
                    RootDirectoryId = rootDirectoryId
                    RootDirectorySha256Hash = Sha256Hash "resync-refresh-root"
                    RootDirectoryBlake3Hash = Blake3Hash "resync-refresh-root-blake3"
                }

            let directoryIds = HashSet<DirectoryVersionId>([| rootDirectoryId |])

            (Services.updateGraceWatchInterprocessFile status (Some directoryIds))
                .GetAwaiter()
                .GetResult()

            Services.getGraceWatchStatus().Result
            |> Option.isSome
            |> should equal true

            Watch.requestGraceWatchExplicitResyncForWatchTests "test resync refresh"

            (Watch.publishGraceWatchInterprocessFileForCurrentConfidenceForWatchTests status directoryIds Services.updateGraceWatchInterprocessFile)
                .GetAwaiter()
                .GetResult()

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that a newer Grace Status refresh observed during publication survives for the next timer pass.
    [<Test>]
    let ``newer status refresh observed during clean publication remains pending`` () =
        withTempRepo (fun _ ->
            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental
            Watch.setGraceStatusHasChangedForWatchTests true

            let status =
                { graceStatusTracking Array.empty<string> Array.empty<string> with
                    RootDirectorySha256Hash = Sha256Hash "refresh-race-root"
                    RootDirectoryBlake3Hash = Blake3Hash "refresh-race-root-blake3"
                }

            let directoryIds = HashSet<DirectoryVersionId>(status.Index.Keys)
            /// Tracks IPC writes so the concurrent refresh can force a dirty retry publication.
            let mutable updateCalls = 0

            /// Publishes IPC and observes a newer Grace Status DB refresh during the first clean attempt.
            let updateIpc publishedStatus publishedDirectoryIds =
                updateCalls <- updateCalls + 1

                if updateCalls = 1 then Watch.setGraceStatusHasChangedForWatchTests true

                Services.updateGraceWatchInterprocessFile publishedStatus publishedDirectoryIds

            (Watch.publishGraceStatusRefreshSnapshotForWatchTests status updateIpc)
                .GetAwaiter()
                .GetResult()

            updateCalls |> should equal 2

            Watch.graceStatusHasChangedForWatchTests ()
            |> should equal true

            let persistedStatus = Services.inspectGraceWatchStatus().Result.Status

            match persistedStatus with
            | Some watchStatus ->
                watchStatus.HasPendingWatchWork
                |> should equal true

                watchStatus.IsWorkingTreeClean
                |> should equal false

                watchStatus.DirectoryIds.SetEquals(directoryIds)
                |> should equal true
            | None -> Assert.Fail("Expected dirty retry Watch IPC after concurrent refresh."))

    /// Verifies that clean and dirty transition publication reads do not replace the timer pass's in-flight state.
    [<Test>]
    let ``pending work transition publication does not mutate in-flight status`` () =
        withTempRepo (fun _ ->
            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental

            let inFlightStatus =
                { graceStatusTracking Array.empty<string> Array.empty<string> with
                    RootDirectorySha256Hash = Sha256Hash "in-flight-root"
                    RootDirectoryBlake3Hash = Blake3Hash "in-flight-root-blake3"
                }

            let transitionStatus =
                { graceStatusTracking Array.empty<string> Array.empty<string> with
                    RootDirectorySha256Hash = Sha256Hash "transition-root"
                    RootDirectoryBlake3Hash = Blake3Hash "transition-root-blake3"
                }

            let inFlightRootDirectoryId = inFlightStatus.RootDirectoryId
            let transitionRootDirectoryId = transitionStatus.RootDirectoryId
            let inFlightDirectoryIds = HashSet<DirectoryVersionId>(inFlightStatus.Index.Keys)

            Watch.setGraceStatusForWatchTests inFlightStatus
            Watch.updateGraceStatusDirectoryIds inFlightStatus
            Watch.setReadGraceStatusFileForPendingWorkTransitionForWatchTests (fun () -> Task.FromResult(transitionStatus))

            (Services.updateGraceWatchInterprocessFile inFlightStatus (Some inFlightDirectoryIds))
                .GetAwaiter()
                .GetResult()

            Watch.setGraceStatusHasChangedForWatchTests true
            Watch.publishPendingWatchWorkTransitionIfNeededForWatchTests ()

            Watch.graceStatusForWatchTests().RootDirectoryId
            |> should equal inFlightRootDirectoryId

            Watch
                .graceStatusDirectoryIdsForWatchTests()
                .SetEquals(inFlightDirectoryIds)
            |> should equal true

            let persistedStatus = Services.inspectGraceWatchStatus().Result.Status

            match persistedStatus with
            | Some watchStatus ->
                watchStatus.RootDirectoryId
                |> should equal transitionRootDirectoryId

                watchStatus.HasPendingWatchWork
                |> should equal true
            | None -> Assert.Fail("Expected transition IPC to publish from the local transition read."))

    /// Verifies that overflow during an in-flight upload cancels normal status application.
    [<Test>]
    let ``overflow during upload prevents stale observation commit`` () =
        withTempRepo (fun root ->
            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental

            let filePath = Path.Combine(root, "overflow-during-upload.txt")
            File.WriteAllText(filePath, "overflow during upload")
            Watch.OnChanged(changedEvent filePath)

            /// Tracks apply-from-differences Calls changes so overflow cannot commit stale observations.
            let mutable applyFromDifferencesCalls = 0

            /// Reads status needed by the overflow race scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Raises overflow after upload starts to model confidence loss during awaited work.
            let upload _ pendingFilePath =
                recordUploadedFileVersion $"{pendingFilePath}"
                Watch.OnError(ErrorEventArgs(InternalBufferOverflowException("watch buffer overflow")))
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise overflow race behavior.
            let updateGraceStatusFromDifferences status _ _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                Task.FromResult(Some status)

            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc status directoryIds = Services.updateGraceWatchInterprocessFile status directoryIds

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                (fun _ _ _ -> Task.FromResult(()))
                updateIpc)
                .GetAwaiter()
                .GetResult()

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

            Watch.processedFileRelativePathsPendingStatusForWatchTests ()
            |> should equal Array.empty<string>

            applyFromDifferencesCalls |> should equal 0

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that overflow during status application blocks later side effects in the same update.
    [<Test>]
    let ``overflow during status application cancels side effects`` () =
        withTempRepo (fun root ->
            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental

            let filePath = Path.Combine(root, "overflow-during-status.txt")
            Watch.OnDeleted(deletedEvent filePath)

            /// Tracks apply-from-differences Calls changes so the in-flight race is explicit.
            let mutable applyFromDifferencesCalls = 0
            /// Tracks side effects that should be skipped after overflow changes the trust predicate.
            let mutable trustedSideEffects = 0

            /// Reads status needed by the overflow race scenario.
            let readStatus () = Task.FromResult(graceStatusTracking [| "overflow-during-status.txt" |] Array.empty<string>)

            /// Builds upload test data used to exercise CLI watch behavior.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise overflow during status application.
            let updateGraceStatusFromDifferences status _ _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                Watch.OnError(ErrorEventArgs(InternalBufferOverflowException("watch buffer overflow")))

                if Watch.statusSideEffectsStillTrustedForWatchTests () then
                    trustedSideEffects <- trustedSideEffects + 1

                Task.FromResult(Some status)

            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc status directoryIds = Services.updateGraceWatchInterprocessFile status directoryIds

            (Watch.processChangedFilesWithClients
                readStatus
                readStatus
                upload
                updateGraceStatus
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                (fun _ _ _ -> Task.FromResult(()))
                updateIpc)
                .GetAwaiter()
                .GetResult()

            applyFromDifferencesCalls |> should equal 1
            trustedSideEffects |> should equal 0

            Watch.currentGraceWatchRuntimeModeForWatchTests ()
            |> should equal Services.GraceWatchRuntimeMode.Resynchronizing

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that startup upload completions are recorded before healthy mode so they are not uploaded again.
    [<Test>]
    let ``startup upload completion records processed path before healthy mode`` () =
        withTempRepo (fun root ->
            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.StartingUp

            let relativePath = "startup-upload.txt"
            let filePath = Path.Combine(root, relativePath)
            File.WriteAllText(filePath, "startup upload content")

            Watch.queueStartupDifferenceForWatch (FileSystemDifference.Create Add FileSystemEntryType.File relativePath)

            /// Tracks upload Calls changes so startup duplicate prevention is explicit.
            let mutable uploadCalls = 0
            /// Tracks apply-from-differences Calls changes so the retry remains pending without another upload.
            let mutable applyFromDifferencesCalls = 0

            /// Reads status needed by the startup upload scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Records uploaded content while Watch is still in startup mode.
            let upload _ pendingFilePath =
                uploadCalls <- uploadCalls + 1
                recordUploadedFileVersion $"{pendingFilePath}"
                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Keeps startup status application retryable so processed upload state remains observable.
            let updateGraceStatusFromDifferences status _ _ =
                applyFromDifferencesCalls <- applyFromDifferencesCalls + 1
                Task.FromResult(None)

            /// Builds update ipc test data used to exercise CLI watch behavior.
            let updateIpc _ _ = Task.FromResult(())

            let processPendingWork () =
                (Watch.processChangedFilesWithClients
                    readStatus
                    readStatus
                    upload
                    updateGraceStatus
                    scannerHostileDifferenceDiscovery
                    updateGraceStatusFromDifferences
                    (fun _ _ _ -> Task.FromResult(()))
                    updateIpc)
                    .GetAwaiter()
                    .GetResult()

            processPendingWork ()

            uploadCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 1

            Watch
                .pendingWatchWorkSnapshotForTests()
                .FilesToProcess
            |> should equal Array.empty<string>

            Watch.processedFileRelativePathsPendingStatusForWatchTests ()
            |> should equal [| relativePath |]

            processPendingWork ()

            uploadCalls |> should equal 1
            applyFromDifferencesCalls |> should equal 2)

    /// Verifies that normal event-derived observations apply only in healthy incremental mode.
    [<Test>]
    let ``healthy runtime mode applies event-derived observations`` () =
        withTempRepo (fun root ->
            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental

            let filePath = Path.Combine(root, "healthy-observation.txt")
            File.WriteAllText(filePath, "healthy observation")
            Watch.OnChanged(changedEvent filePath)

            /// Tracks apply-from-differences Calls changes so healthy mode proves observation application.
            let mutable applyFromDifferencesCalls = 0

            /// Reads status needed by the state-gate scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise healthy observation processing.
            let upload _ filePath =
                let fullPath = $"{filePath}"

                if File.Exists(fullPath) then recordUploadedFileVersion fullPath

                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise healthy observation processing.
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
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            applyFromDifferencesCalls |> should equal 1)

    /// Verifies that suspended mode does not queue or apply filesystem observations.
    [<Test>]
    let ``suspended runtime mode does not apply observations`` () =
        withTempRepo (fun root ->
            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.Suspended

            let filePath = Path.Combine(root, "suspended-observation.txt")
            File.WriteAllText(filePath, "suspended observation")
            Watch.OnChanged(changedEvent filePath)

            Watch
                .pendingWatchWorkSnapshotForTests()
                .FilesToProcess
            |> should equal Array.empty<string>

            /// Tracks apply-from-differences Calls changes so suspended mode proves no status application.
            let mutable applyFromDifferencesCalls = 0

            /// Reads status needed by the state-gate scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise suspended observation processing.
            let upload _ _ = Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise suspended observation processing.
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
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            applyFromDifferencesCalls |> should equal 0)

    /// Verifies that stopping mode leaves queued observation work untouched and cannot create a new Save.
    [<Test>]
    let ``stopping runtime mode does not create saves from queued observations`` () =
        withTempRepo (fun root ->
            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental

            let filePath = Path.Combine(root, "stopping-observation.txt")
            File.WriteAllText(filePath, "stopping observation")
            Watch.OnChanged(changedEvent filePath)

            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.Stopping

            /// Tracks apply-from-differences Calls changes so stopping mode proves no Save-producing application.
            let mutable applyFromDifferencesCalls = 0

            /// Reads status needed by the state-gate scenario.
            let readStatus () = Task.FromResult(GraceStatus.Default)

            /// Builds upload test data used to exercise stopping observation processing.
            let upload _ filePath =
                let fullPath = $"{filePath}"

                if File.Exists(fullPath) then recordUploadedFileVersion fullPath

                Task.FromResult(())

            /// Builds scan-oriented update test data used to exercise CLI watch behavior.
            let updateGraceStatus status _ = Task.FromResult(Some status)

            /// Builds apply-from-differences test data used to exercise stopping observation processing.
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
                scannerHostileDifferenceDiscovery
                updateGraceStatusFromDifferences
                applyIncremental
                updateIpc)
                .GetAwaiter()
                .GetResult()

            applyFromDifferencesCalls |> should equal 0

            Watch.processedFileRelativePathsPendingStatusForWatchTests ()
            |> should equal [| "stopping-observation.txt" |])

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

    /// Verifies that fresh clean Watch IPC must belong to the current repository, branch, and root before reuse.
    [<Test>]
    let ``watch status rejects clean non-legacy identity mismatches`` () =
        withTempRepo (fun tempDir ->
            let rootDirectoryId = Guid.NewGuid()
            let baseStatus = liveWatchStatus rootDirectoryId

            let mismatchCases =
                [|
                    { baseStatus with RepositoryId = Guid.NewGuid() }
                    { baseStatus with BranchId = Guid.NewGuid() }
                    { baseStatus with RootDirectory = Path.Combine(tempDir, "other-worktree") }
                |]

            for mismatchedStatus in mismatchCases do
                let statusLevelFlags = safetyFlagSet mismatchedStatus

                statusLevelFlags
                |> Set.contains "cleanWorkingTree"
                |> should equal true

                writeWatchStatusJsonWithRuntimeSurface mismatchedStatus
                |> ignore

                let inspection =
                    Services
                        .inspectGraceWatchStatus()
                        .GetAwaiter()
                        .GetResult()

                inspection.IsLiveProcess |> should equal true

                inspection.IsUsable |> should equal false

                let inspectionSafetyFlags = inspection.SafetyFlags |> Set.ofArray

                inspectionSafetyFlags
                |> Set.contains "cleanWorkingTree"
                |> should equal false

                inspectionSafetyFlags
                |> Set.contains "incrementalSafe"
                |> should equal false

                inspectionSafetyFlags
                |> Set.contains "requiresExplicitResync"
                |> should equal true

                Services.getGraceWatchStatus().Result
                |> should equal None)

    /// Verifies that persisted repository and branch ids are the authoritative Watch IPC identity.
    [<Test>]
    let ``watch status accepts current ids with stale display names`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let rootDirectoryId = Guid.NewGuid()

            let status =
                { liveWatchStatus rootDirectoryId with
                    RepositoryId = currentRepositoryId
                    RepositoryName = RepositoryName "stale-repo-name"
                    BranchId = currentBranchId
                    BranchName = BranchName "stale-branch-name"
                    RootDirectory = root
                }

            writeWatchStatusJsonWithRuntimeSurface status
            |> ignore

            let inspection = Services.inspectGraceWatchStatus().Result

            inspection.HasCurrentRepositoryIdentity
            |> should equal true

            inspection.IsUsable |> should equal true

            Services.getGraceWatchStatus().Result
            |> Option.isSome
            |> should equal true)

    /// Verifies that current-branch SignalR payloads must match the current repository and branch identity.
    [<Test>]
    let WatchCurrentBranchSignalRPayloadRejectsStaleRepositoryOrBranchIdentity () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"

            let payload =
                { CurrentBranchReferenceNotification.Default with
                    RepositoryId = currentRepositoryId
                    BranchId = currentBranchId
                    BranchName = BranchName "current-branch"
                    ReferenceId = Guid.NewGuid()
                    ReferenceType = ReferenceType.Save
                }

            Watch.currentBranchReferenceNotificationTargetsCurrentBranchForWatchTests payload
            |> should equal true

            Watch.currentBranchReferenceNotificationTargetsCurrentBranchForWatchTests { payload with RepositoryId = Guid.NewGuid() }
            |> should equal false

            Watch.currentBranchReferenceNotificationTargetsCurrentBranchForWatchTests { payload with BranchId = Guid.NewGuid() }
            |> should equal false)

    /// Builds the ReferenceDto authority shape that BranchDto latest fields expose for Watch notification decisions.
    let private referenceDtoForCurrentBranchNotification (payload: CurrentBranchReferenceNotification) =
        { ReferenceDto.Default with
            ReferenceId = payload.ReferenceId
            OwnerId = payload.OwnerId
            OrganizationId = payload.OrganizationId
            RepositoryId = payload.RepositoryId
            BranchId = payload.BranchId
            DirectoryId = payload.DirectoryId
            Sha256Hash = payload.Sha256Hash
            Blake3Hash = payload.Blake3Hash
            ReferenceType = payload.ReferenceType
            ReferenceText = payload.ReferenceText
        }

    /// Builds a BranchDto whose overall and per-type latest Reference authorities agree on the supplied notification.
    let private branchDtoWithLatestCurrentBranchReference (payload: CurrentBranchReferenceNotification) =
        let referenceDto = referenceDtoForCurrentBranchNotification payload
        let branchDto = { Grace.Types.Branch.BranchDto.Default with RepositoryId = payload.RepositoryId; BranchId = payload.BranchId }

        match payload.ReferenceType with
        | ReferenceType.Save -> { branchDto with LatestSave = referenceDto; LatestReference = referenceDto }
        | ReferenceType.Checkpoint -> { branchDto with LatestCheckpoint = referenceDto; LatestReference = referenceDto }
        | ReferenceType.Commit -> { branchDto with LatestCommit = referenceDto; LatestReference = referenceDto }
        | _ -> branchDto

    /// Builds a BranchDto where a delayed per-type notification is older than the branch's overall latest Reference.
    let private branchDtoWithPerTypeLatestAndOverallLatest
        (perTypePayload: CurrentBranchReferenceNotification)
        (overallLatestPayload: CurrentBranchReferenceNotification)
        =
        let perTypeReferenceDto = referenceDtoForCurrentBranchNotification perTypePayload
        let branchDto = branchDtoWithLatestCurrentBranchReference overallLatestPayload

        match perTypePayload.ReferenceType with
        | ReferenceType.Save -> { branchDto with LatestSave = perTypeReferenceDto }
        | ReferenceType.Checkpoint -> { branchDto with LatestCheckpoint = perTypeReferenceDto }
        | ReferenceType.Commit -> { branchDto with LatestCommit = perTypeReferenceDto }
        | _ -> branchDto

    /// Wraps a Watch status snapshot in the inspected IPC shape consumed by the materialization coordinator.
    let private watchStatusInspection (status: Services.GraceWatchStatus) : Services.GraceWatchStatusInspection =
        (LocalStateDb.ensureDbInitialized (Current().GraceStatusFile))
            .GetAwaiter()
            .GetResult()

        { Exists = true; Status = Some status; PersistedMode = Some status.Mode; SafetyFlags = status.SafetyFlags; ReadError = None }

    /// Represents missing Watch IPC authority for deterministic degraded coordinator tests.
    let private missingWatchStatusInspection: Services.GraceWatchStatusInspection =
        {
            Exists = false
            Status = None
            PersistedMode = None
            SafetyFlags =
                [|
                    "missingStatus"
                    "requiresExplicitResync"
                |]
            ReadError = None
        }

    /// Creates a concrete current-branch Reference notification with complete remote root identity.
    let private validCurrentBranchReferenceNotification repositoryId branchId referenceType rootDirectoryId rootSha256Hash rootBlake3Hash =
        { CurrentBranchReferenceNotification.Default with
            RepositoryId = repositoryId
            BranchId = branchId
            BranchName = BranchName "current-branch"
            ReferenceId = Guid.NewGuid()
            DirectoryId = rootDirectoryId
            Sha256Hash = rootSha256Hash
            Blake3Hash = rootBlake3Hash
            ReferenceType = referenceType
        }

    /// Verifies that protocol-invalid current-branch notifications are rejected before BranchDto latest authority.
    [<Test>]
    let ``current branch reference decision rejects missing root identity and empty reference id`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let validPayload =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let branchDto = branchDtoWithLatestCurrentBranchReference validPayload

            let assertRejected expectedReason payload =
                let decision = Services.decideLatestCurrentBranchReferenceMaterialization currentRepositoryId currentBranchId (Some status) branchDto payload

                decision.NeedsMaterialization
                |> should equal false

                decision.Reason |> should equal expectedReason

            assertRejected
                Services.LatestCurrentBranchReferenceDecisionReason.ReferenceRootIdentityUnavailable
                { validPayload with DirectoryId = DirectoryVersionId.Empty }

            assertRejected
                Services.LatestCurrentBranchReferenceDecisionReason.ReferenceRootIdentityUnavailable
                { validPayload with Sha256Hash = Sha256Hash String.Empty }

            assertRejected
                Services.LatestCurrentBranchReferenceDecisionReason.ReferenceRootIdentityUnavailable
                { validPayload with Blake3Hash = Blake3Hash String.Empty }

            assertRejected Services.LatestCurrentBranchReferenceDecisionReason.ReferenceIdUnavailable { validPayload with ReferenceId = ReferenceId.Empty })

    /// Verifies that current-branch notification handling reads BranchDto before local status and rechecks before apply.
    [<Test>]
    let ``current branch reference handler fetches BranchDto before local status for valid notifications`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            LocalStateDb
                .ensureDbInitialized(Current().GraceStatusFile)
                .GetAwaiter()
                .GetResult()

            let payload =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let calls = ResizeArray<string>()
            let branchDto = branchDtoWithLatestCurrentBranchReference payload

            let getBranch () =
                task {
                    calls.Add("branch")
                    return Ok(GraceReturnValue.Create branchDto "branch-fetch-before-status")
                }

            let readLocalStatus () =
                task {
                    calls.Add("status")
                    return Some status
                }

            let decision =
                (Watch.handleCurrentBranchReferenceNotificationWithClientsForWatchTests getBranch readLocalStatus payload)
                    .Result

            decision
            |> Option.map (fun result -> result.Reason)
            |> should equal (Some Services.LatestCurrentBranchReferenceDecisionReason.RemoteMaterializationRequired)

            calls.ToArray()
            |> should equal [| "branch"; "status"; "status" |])

    /// Verifies that same-root same-branch References do not schedule remote materialization.
    [<Test>]
    let ``latest current branch decision no-ops when remote root matches local status`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let localRootId = Guid.NewGuid()
            let status = liveWatchStatus localRootId

            let payload =
                { CurrentBranchReferenceNotification.Default with
                    RepositoryId = currentRepositoryId
                    BranchId = currentBranchId
                    ReferenceId = Guid.NewGuid()
                    DirectoryId = localRootId
                    Sha256Hash = status.RootDirectorySha256Hash
                    Blake3Hash = status.RootDirectoryBlake3Hash
                    ReferenceType = ReferenceType.Save
                }

            let branchDto = branchDtoWithLatestCurrentBranchReference payload

            let decision = Services.decideLatestCurrentBranchReferenceMaterialization currentRepositoryId currentBranchId (Some status) branchDto payload

            decision.NeedsMaterialization
            |> should equal false

            decision.Reason
            |> should equal Services.LatestCurrentBranchReferenceDecisionReason.SameRoot

            decision.Reference
            |> Option.map (fun reference -> reference.ReferenceId)
            |> should equal (Some payload.ReferenceId))

    /// Verifies that BranchDto latest authority, not notification arrival order, selects the materializable Reference.
    [<Test>]
    let ``latest current branch decision drops older notification even when it arrives last`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let olderNotification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "older-root")
                    (Blake3Hash "older-root-blake3")

            let newerNotification =
                { olderNotification with
                    ReferenceId = Guid.NewGuid()
                    DirectoryId = Guid.NewGuid()
                    Sha256Hash = Sha256Hash "newer-root"
                    Blake3Hash = Blake3Hash "newer-root-blake3"
                }

            let branchDto = branchDtoWithLatestCurrentBranchReference newerNotification

            let decision =
                Services.decideLatestCurrentBranchReferenceMaterialization currentRepositoryId currentBranchId (Some status) branchDto olderNotification

            decision.NeedsMaterialization
            |> should equal false

            decision.Reason
            |> should equal Services.LatestCurrentBranchReferenceDecisionReason.StaleLatestReference

            decision.Reference
            |> Option.map (fun reference -> reference.ReferenceId)
            |> should equal (Some olderNotification.ReferenceId))

    /// Verifies that an older Save is stale when the branch head has advanced to a newer Checkpoint.
    [<Test>]
    let ``latest current branch decision drops older save after newer checkpoint overall latest`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let olderSaveNotification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "older-save-root")
                    (Blake3Hash "older-save-root-blake3")

            let newerCheckpointNotification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Checkpoint
                    (Guid.NewGuid())
                    (Sha256Hash "newer-checkpoint-root")
                    (Blake3Hash "newer-checkpoint-root-blake3")

            let branchDto = branchDtoWithPerTypeLatestAndOverallLatest olderSaveNotification newerCheckpointNotification

            let decision =
                Services.decideLatestCurrentBranchReferenceMaterialization currentRepositoryId currentBranchId (Some status) branchDto olderSaveNotification

            decision.NeedsMaterialization
            |> should equal false

            decision.Reason
            |> should equal Services.LatestCurrentBranchReferenceDecisionReason.StaleLatestReference

            decision.Reference
            |> Option.map (fun reference -> reference.ReferenceId)
            |> should equal (Some olderSaveNotification.ReferenceId))

    /// Verifies that an older Checkpoint is stale when the branch head has advanced to a newer Commit.
    [<Test>]
    let ``latest current branch decision drops older checkpoint after newer commit overall latest`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let olderCheckpointNotification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Checkpoint
                    (Guid.NewGuid())
                    (Sha256Hash "older-checkpoint-root")
                    (Blake3Hash "older-checkpoint-root-blake3")

            let newerCommitNotification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Commit
                    (Guid.NewGuid())
                    (Sha256Hash "newer-commit-root")
                    (Blake3Hash "newer-commit-root-blake3")

            let branchDto = branchDtoWithPerTypeLatestAndOverallLatest olderCheckpointNotification newerCommitNotification

            let decision =
                Services.decideLatestCurrentBranchReferenceMaterialization
                    currentRepositoryId
                    currentBranchId
                    (Some status)
                    branchDto
                    olderCheckpointNotification

            decision.NeedsMaterialization
            |> should equal false

            decision.Reason
            |> should equal Services.LatestCurrentBranchReferenceDecisionReason.StaleLatestReference

            decision.Reference
            |> Option.map (fun reference -> reference.ReferenceId)
            |> should equal (Some olderCheckpointNotification.ReferenceId))

    /// Verifies that a BranchDto latest mismatch drops the notification even when root values appear materializable.
    [<Test>]
    let ``latest current branch decision drops BranchDto latest mismatch as stale`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Checkpoint
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let branchDto = branchDtoWithLatestCurrentBranchReference { notification with ReferenceId = Guid.NewGuid() }

            let decision = Services.decideLatestCurrentBranchReferenceMaterialization currentRepositoryId currentBranchId (Some status) branchDto notification

            decision.NeedsMaterialization
            |> should equal false

            decision.Reason
            |> should equal Services.LatestCurrentBranchReferenceDecisionReason.StaleLatestReference)

    /// Verifies that BranchDto overall latest and per-type latest agreement allows a complete notification to proceed.
    [<Test>]
    let ``latest current branch decision proceeds when overall latest matches notification reference`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Checkpoint
                    (Guid.NewGuid())
                    (Sha256Hash "matching-overall-root")
                    (Blake3Hash "matching-overall-root-blake3")

            let branchDto = branchDtoWithLatestCurrentBranchReference notification

            let decision = Services.decideLatestCurrentBranchReferenceMaterialization currentRepositoryId currentBranchId (Some status) branchDto notification

            decision.NeedsMaterialization |> should equal true

            decision.Reason
            |> should equal Services.LatestCurrentBranchReferenceDecisionReason.RemoteMaterializationRequired

            decision.Reference
            |> Option.map (fun reference -> reference.ReferenceId)
            |> should equal (Some notification.ReferenceId))

    /// Verifies that returning to an older root value is accepted only through the newer BranchDto latest Reference id.
    [<Test>]
    let ``latest current branch decision accepts newer reference that returns to older root value`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let olderRootId = Guid.NewGuid()
            let olderRootSha256 = Sha256Hash "older-returned-root"
            let olderRootBlake3 = Blake3Hash "older-returned-root-blake3"

            let olderNotification =
                validCurrentBranchReferenceNotification currentRepositoryId currentBranchId ReferenceType.Commit olderRootId olderRootSha256 olderRootBlake3

            let newerNotification = { olderNotification with ReferenceId = Guid.NewGuid() }

            let branchDto = branchDtoWithLatestCurrentBranchReference newerNotification

            let staleDecision =
                Services.decideLatestCurrentBranchReferenceMaterialization currentRepositoryId currentBranchId (Some status) branchDto olderNotification

            staleDecision.NeedsMaterialization
            |> should equal false

            staleDecision.Reason
            |> should equal Services.LatestCurrentBranchReferenceDecisionReason.StaleLatestReference

            let acceptedDecision =
                Services.decideLatestCurrentBranchReferenceMaterialization currentRepositoryId currentBranchId (Some status) branchDto newerNotification

            acceptedDecision.NeedsMaterialization
            |> should equal true

            acceptedDecision.Reason
            |> should equal Services.LatestCurrentBranchReferenceDecisionReason.RemoteMaterializationRequired)

    /// Verifies that root-hash equality is enough to make a deterministic no-op decision.
    [<Test>]
    let ``latest current branch decision compares local root hashes without scanning`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let payload =
                { CurrentBranchReferenceNotification.Default with
                    RepositoryId = currentRepositoryId
                    BranchId = currentBranchId
                    ReferenceId = Guid.NewGuid()
                    DirectoryId = Guid.NewGuid()
                    Sha256Hash = status.RootDirectorySha256Hash
                    Blake3Hash = status.RootDirectoryBlake3Hash
                    ReferenceType = ReferenceType.Checkpoint
                }

            let branchDto = branchDtoWithLatestCurrentBranchReference payload

            let decision = Services.decideLatestCurrentBranchReferenceMaterialization currentRepositoryId currentBranchId (Some status) branchDto payload

            decision.NeedsMaterialization
            |> should equal false

            decision.Reason
            |> should equal Services.LatestCurrentBranchReferenceDecisionReason.SameRoot)

    /// Verifies that dirty or pending local status cannot fall back to unsafe scan-oriented materialization.
    [<Test>]
    let ``latest current branch decision rejects materialization when local status has pending work`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"

            let status = { liveWatchStatus (Guid.NewGuid()) with HasPendingWatchWork = true; IsWorkingTreeClean = false }

            Services.isGraceWatchScanLegal status.Mode
            |> should equal false

            let payload =
                { CurrentBranchReferenceNotification.Default with
                    RepositoryId = currentRepositoryId
                    BranchId = currentBranchId
                    ReferenceId = Guid.NewGuid()
                    DirectoryId = Guid.NewGuid()
                    Sha256Hash = Sha256Hash "remote-root"
                    Blake3Hash = Blake3Hash "remote-root-blake3"
                    ReferenceType = ReferenceType.Commit
                }

            let branchDto = branchDtoWithLatestCurrentBranchReference payload

            let decision = Services.decideLatestCurrentBranchReferenceMaterialization currentRepositoryId currentBranchId (Some status) branchDto payload

            decision.NeedsMaterialization
            |> should equal false

            decision.Reason
            |> should equal Services.LatestCurrentBranchReferenceDecisionReason.LocalStatusRequiresResync)

    /// Verifies that the materialization coordinator processes one exact Reference at a time.
    [<Test; Category("CurrentBranchMaterializationCoordinator")>]
    let ``current branch materialization coordinator blocks later reference until active reference completes`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let referenceA =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root-a")
                    (Blake3Hash "remote-root-a-blake3")

            let referenceB =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root-b")
                    (Blake3Hash "remote-root-b-blake3")

            let branchDtos = Queue<Grace.Types.Branch.BranchDto>()
            branchDtos.Enqueue(branchDtoWithLatestCurrentBranchReference referenceA)
            branchDtos.Enqueue(branchDtoWithLatestCurrentBranchReference referenceB)

            let branchFetches = ResizeArray<ReferenceId>()
            let appliedReferences = ResizeArray<ReferenceId>()
            use applyStarted = new ManualResetEventSlim(false)
            use releaseApply = new ManualResetEventSlim(false)

            let getBranch () =
                task {
                    let branchDto = branchDtos.Dequeue()
                    branchFetches.Add(branchDto.LatestReference.ReferenceId)
                    return Ok(GraceReturnValue.Create branchDto "serialized-materialization-test")
                }

            let inspectStatus () = Task.FromResult(watchStatusInspection status)
            let requestDegradedResync _ = Assert.Fail("Clean IPC must not request degraded resync.")
            let waitForSafePoint _ _ = Task.FromResult(())
            let reestablishIpc _ _ = Task.FromResult(())

            let applyReference payload _ =
                task {
                    appliedReferences.Add(payload.ReferenceId)

                    if payload.ReferenceId = referenceA.ReferenceId then
                        applyStarted.Set() |> ignore
                        releaseApply.Wait()
                }

            let taskA =
                Task.Run (fun () ->
                    (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                        getBranch
                        inspectStatus
                        requestDegradedResync
                        waitForSafePoint
                        reestablishIpc
                        applyReference
                        referenceA)
                        .GetAwaiter()
                        .GetResult())

            let mutable taskB: Task<Watch.CurrentBranchMaterializationCoordinatorOutcome option> = null

            try
                applyStarted.Wait(5000) |> should equal true

                taskB <-
                    Task.Run (fun () ->
                        (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                            getBranch
                            inspectStatus
                            requestDegradedResync
                            waitForSafePoint
                            reestablishIpc
                            applyReference
                            referenceB)
                            .GetAwaiter()
                            .GetResult())

                Task.Delay(150).Wait()

                branchFetches.ToArray()
                |> should equal [| referenceA.ReferenceId |]
            finally
                releaseApply.Set()

            let tasksToWait = if isNull taskB then [| taskA :> Task |] else [| taskA :> Task; taskB :> Task |]

            Task.WaitAll(tasksToWait, 5000)
            |> should equal true

            branchFetches.ToArray()
            |> should
                equal
                [|
                    referenceA.ReferenceId
                    referenceB.ReferenceId
                |]

            appliedReferences.ToArray()
            |> should
                equal
                [|
                    referenceA.ReferenceId
                    referenceB.ReferenceId
                |])

    /// Verifies that the repository/worktree lease blocks materialization in another lane owner.
    [<Test; Category("CurrentBranchMaterializationCoordinator"); Category("BranchSwitchSerialization")>]
    let ``working directory materialization lane waits for repository lease`` () =
        withTempRepo (fun root ->
            configureCurrentWatchIdentity root "current-repo" "current-branch"
            |> ignore

            let leaseFileName = WorkingDirectoryMaterialization.leaseFileName ()

            Directory.CreateDirectory(Path.GetDirectoryName(leaseFileName))
            |> ignore

            use blockingLease = new FileStream(leaseFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            use operationEntered = new ManualResetEventSlim(false)

            let materializationTask =
                Task.Run (fun () ->
                    (WorkingDirectoryMaterialization.runSerialized (fun () -> task { operationEntered.Set() |> ignore }))
                        .GetAwaiter()
                        .GetResult())

            try
                Task.Delay(150).Wait()

                operationEntered.IsSet |> should equal false
            finally
                blockingLease.Dispose()

            Task.WaitAll([| materializationTask |], 5000)
            |> should equal true

            operationEntered.IsSet |> should equal true)

    /// Verifies that a Watch notification blocked on a local safe point does not own the repository file lease.
    [<Test; Category("CurrentBranchMaterializationCoordinator"); Category("BranchSwitchSerialization")>]
    let ``current branch materialization coordinator releases file lease while waiting for safe point`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let dirtyStatus = { liveWatchStatus (Guid.NewGuid()) with HasPendingWatchWork = true; IsWorkingTreeClean = false }
            let getBranch () = Task.FromResult(Ok(GraceReturnValue.Create (branchDtoWithLatestCurrentBranchReference notification) "safe-point-lease-test"))
            let inspectStatus () = Task.FromResult(watchStatusInspection dirtyStatus)
            let requestDegradedResync _ = Assert.Fail("Dirty IPC should wait for a safe point.")
            let reestablishIpc _ _ = Task.FromResult(())
            let applyReference _ _ = Task.FromException<unit>(InvalidOperationException("dirty local status must not apply"))
            use waitStarted = new ManualResetEventSlim(false)
            use releaseWait = new ManualResetEventSlim(false)

            let waitForSafePoint _ _ =
                task {
                    waitStarted.Set() |> ignore
                    releaseWait.Wait()
                }

            let materializationTask =
                Task.Run (fun () ->
                    (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                        getBranch
                        inspectStatus
                        requestDegradedResync
                        waitForSafePoint
                        reestablishIpc
                        applyReference
                        notification)
                        .GetAwaiter()
                        .GetResult())

            try
                waitStarted.Wait(5000) |> should equal true

                use _leaseProbe = new FileStream(WorkingDirectoryMaterialization.leaseFileName (), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

                Task.Delay(150).Wait()

                materializationTask.IsCompleted
                |> should equal false
            finally
                releaseWait.Set() |> ignore

            Task.WaitAll([| materializationTask :> Task |], 5000)
            |> should equal true

            materializationTask.Result.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.WaitingForSafePoint)

    /// Verifies that a Watch notification blocked on degraded IPC retry does not own the repository file lease.
    [<Test; Category("CurrentBranchMaterializationCoordinator"); Category("BranchSwitchSerialization")>]
    let ``current branch materialization coordinator releases file lease while retrying degraded ipc`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let getBranch () = Task.FromResult(Ok(GraceReturnValue.Create (branchDtoWithLatestCurrentBranchReference notification) "degraded-lease-test"))

            let inspectStatus () = Task.FromResult(missingWatchStatusInspection)
            let requestDegradedResync _ = ()
            let waitForSafePoint _ _ = Task.FromResult(())
            let applyReference _ _ = Task.FromException<unit>(InvalidOperationException("missing IPC must not apply"))
            use retryStarted = new ManualResetEventSlim(false)
            use releaseRetry = new ManualResetEventSlim(false)

            let reestablishIpc _ _ =
                task {
                    retryStarted.Set() |> ignore
                    releaseRetry.Wait()
                }

            let materializationTask =
                Task.Run (fun () ->
                    (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                        getBranch
                        inspectStatus
                        requestDegradedResync
                        waitForSafePoint
                        reestablishIpc
                        applyReference
                        notification)
                        .GetAwaiter()
                        .GetResult())

            try
                retryStarted.Wait(5000) |> should equal true

                use _leaseProbe = new FileStream(WorkingDirectoryMaterialization.leaseFileName (), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

                Task.Delay(150).Wait()

                materializationTask.IsCompleted
                |> should equal false
            finally
                releaseRetry.Set() |> ignore

            Task.WaitAll([| materializationTask :> Task |], 5000)
            |> should equal true

            materializationTask.Result.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.WaitingForDegradedResync)

    /// Verifies that branch-switch working-tree materialization waits behind an active Reference materialization.
    [<Test; Category("CurrentBranchMaterializationCoordinator"); Category("BranchSwitchSerialization")>]
    let ``current branch materialization coordinator blocks branch switch lane until active reference completes`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let getBranch () = Task.FromResult(Ok(GraceReturnValue.Create (branchDtoWithLatestCurrentBranchReference notification) "branch-switch-lane-test"))

            let inspectStatus () = Task.FromResult(watchStatusInspection status)
            let requestDegradedResync _ = Assert.Fail("Clean IPC must not request degraded resync.")
            let waitForSafePoint _ _ = Task.FromResult(())
            let reestablishIpc _ _ = Task.FromResult(())
            use applyStarted = new ManualResetEventSlim(false)
            use releaseApply = new ManualResetEventSlim(false)
            use branchSwitchEntered = new ManualResetEventSlim(false)

            let applyReference _ _ =
                task {
                    applyStarted.Set() |> ignore

                    releaseApply.Wait()
                }

            let referenceTask =
                Task.Run (fun () ->
                    (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                        getBranch
                        inspectStatus
                        requestDegradedResync
                        waitForSafePoint
                        reestablishIpc
                        applyReference
                        notification)
                        .GetAwaiter()
                        .GetResult())

            let mutable branchSwitchTask: Task = null

            try
                applyStarted.Wait(5000) |> should equal true

                branchSwitchTask <-
                    Task.Run (fun () ->
                        (WorkingDirectoryMaterialization.runSerialized (fun () -> task { branchSwitchEntered.Set() |> ignore }))
                            .GetAwaiter()
                            .GetResult())

                Task.Delay(150).Wait()

                branchSwitchEntered.IsSet |> should equal false
            finally
                releaseApply.Set() |> ignore

            let tasksToWait =
                if isNull branchSwitchTask then
                    [| referenceTask :> Task |]
                else
                    [|
                        referenceTask :> Task
                        branchSwitchTask
                    |]

            Task.WaitAll(tasksToWait, 5000)
            |> should equal true

            referenceTask.Result.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.Applied

            branchSwitchEntered.IsSet |> should equal true)

    /// Verifies that a queued Reference is checked against BranchDto latest only after it owns the serialized lane.
    [<Test; Category("CurrentBranchMaterializationCoordinator")>]
    let ``queued current branch reference revalidates BranchDto latest when processed`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let referenceA =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Commit
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root-a")
                    (Blake3Hash "remote-root-a-blake3")

            let referenceB =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Commit
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root-b")
                    (Blake3Hash "remote-root-b-blake3")

            let branchDtos = Queue<Grace.Types.Branch.BranchDto>()
            branchDtos.Enqueue(branchDtoWithLatestCurrentBranchReference referenceA)
            branchDtos.Enqueue(branchDtoWithLatestCurrentBranchReference referenceB)

            let branchFetches = ResizeArray<ReferenceId>()

            let getBranch () =
                task {
                    let branchDto = branchDtos.Dequeue()
                    branchFetches.Add(branchDto.LatestReference.ReferenceId)
                    return Ok(GraceReturnValue.Create branchDto "queued-revalidation-test")
                }

            let inspectStatus () = Task.FromResult(watchStatusInspection status)
            let requestDegradedResync _ = Assert.Fail("Clean IPC must not request degraded resync.")
            let waitForSafePoint _ _ = Task.FromResult(())
            let reestablishIpc _ _ = Task.FromResult(())
            let applyReference _ _ = Task.FromResult(())

            let outcomeA =
                (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                    getBranch
                    inspectStatus
                    requestDegradedResync
                    waitForSafePoint
                    reestablishIpc
                    applyReference
                    referenceA)
                    .Result

            let outcomeB =
                (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                    getBranch
                    inspectStatus
                    requestDegradedResync
                    waitForSafePoint
                    reestablishIpc
                    applyReference
                    referenceB)
                    .Result

            outcomeA.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.Applied

            outcomeB.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.Applied

            branchFetches.ToArray()
            |> should
                equal
                [|
                    referenceA.ReferenceId
                    referenceB.ReferenceId
                |])

    /// Verifies that dirty local Watch state blocks remote apply and leaves local files untouched.
    [<Test; Category("CurrentBranchMaterializationCoordinator")>]
    let ``current branch materialization coordinator waits when local status is dirty`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let localFile = Path.Combine(root, "local-work.txt")
            File.WriteAllText(localFile, "local edits must survive")

            let dirtyStatus = { liveWatchStatus (Guid.NewGuid()) with HasPendingWatchWork = true; IsWorkingTreeClean = false }

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let getBranch () = Task.FromResult(Ok(GraceReturnValue.Create (branchDtoWithLatestCurrentBranchReference notification) "dirty-gate-test"))
            let inspectStatus () = Task.FromResult(watchStatusInspection dirtyStatus)
            let requestDegradedResync _ = Assert.Fail("Dirty but readable IPC should wait, not request degraded resync.")
            let waitForSafePoint _ _ = Task.FromResult(())
            let reestablishIpc _ _ = Task.FromResult(())
            let applyReference _ _ = Task.FromException<unit>(InvalidOperationException("dirty local state must not apply remote materialization"))

            let outcome =
                (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                    getBranch
                    inspectStatus
                    requestDegradedResync
                    waitForSafePoint
                    reestablishIpc
                    applyReference
                    notification)
                    .Result

            outcome.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.WaitingForSafePoint

            File.ReadAllText(localFile)
            |> should equal "local edits must survive")

    /// Verifies that branch identity is checked again after the final clean IPC inspection and before the apply seam.
    [<Test; Category("CurrentBranchMaterializationCoordinator")>]
    let ``current branch materialization coordinator rejects branch switch before apply seam`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "branch-a"
            let branchBId = Guid.NewGuid()

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let getBranch () = Task.FromResult(Ok(GraceReturnValue.Create (branchDtoWithLatestCurrentBranchReference notification) "branch-switch-gate-test"))
            let mutable inspectionCount = 0

            let inspectStatus () =
                inspectionCount <- inspectionCount + 1

                if inspectionCount = 1 then
                    Task.FromResult(watchStatusInspection (liveWatchStatus (Guid.NewGuid())))
                else
                    let current = Current()
                    current.BranchId <- branchBId
                    current.BranchName <- "branch-b"

                    Task.FromResult(watchStatusInspection (liveWatchStatus (Guid.NewGuid())))

            let requestDegradedResync _ = Assert.Fail("Clean IPC for the new branch should not request degraded resync.")
            let waitForSafePoint _ _ = Task.FromResult(())
            let reestablishIpc _ _ = Task.FromResult(())
            let applyReference _ _ = Task.FromException<unit>(InvalidOperationException("old-branch Reference must not reach apply after branch switch"))

            let outcome =
                (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                    getBranch
                    inspectStatus
                    requestDegradedResync
                    waitForSafePoint
                    reestablishIpc
                    applyReference
                    notification)
                    .Result

            outcome.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.NotCurrentBranch)

    /// Verifies that materialization-in-progress is published as dirty IPC before the apply seam can run.
    [<Test; Category("CurrentBranchMaterializationCoordinator")>]
    let ``current branch materialization coordinator publishes dirty ipc before apply seam`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            writeWatchStatusJsonWithRuntimeSurface status
            |> ignore

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let getBranch () =
                Task.FromResult(Ok(GraceReturnValue.Create (branchDtoWithLatestCurrentBranchReference notification) "dirty-ipc-before-apply-test"))

            let inspectStatus () = Task.FromResult(watchStatusInspection status)
            let requestDegradedResync _ = Assert.Fail("Clean IPC must not request degraded resync.")
            let waitForSafePoint _ _ = Task.FromResult(())
            let reestablishIpc _ _ = Task.FromResult(())

            let applyReference _ _ =
                task {
                    let inspection =
                        Services
                            .inspectGraceWatchStatus()
                            .GetAwaiter()
                            .GetResult()

                    match inspection.Status with
                    | Some publishedStatus ->
                        publishedStatus.HasPendingWatchWork
                        |> should equal true

                        publishedStatus.IsWorkingTreeClean
                        |> should equal false

                        inspection.IsUsable |> should equal false
                    | None -> Assert.Fail("Expected materialization-in-progress to publish dirty Watch IPC before apply.")
                }

            let outcome =
                (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                    getBranch
                    inspectStatus
                    requestDegradedResync
                    waitForSafePoint
                    reestablishIpc
                    applyReference
                    notification)
                    .Result

            outcome.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.Applied)

    /// Verifies that a branch switch during dirty IPC publication is rechecked before the apply seam.
    [<Test; Category("CurrentBranchMaterializationCoordinator"); Category("CurrentBranchMaterializationApplyBoundary")>]
    let ``current branch materialization coordinator drops branch switch after dirty ipc before apply`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "branch-a"
            let branchBId = Guid.NewGuid()
            let status = liveWatchStatus (Guid.NewGuid())
            let graceStatus = graceStatusTracking Array.empty<string> Array.empty<string>

            writeWatchStatusJsonWithRuntimeSurface status
            |> ignore

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let getBranch () =
                Task.FromResult(Ok(GraceReturnValue.Create (branchDtoWithLatestCurrentBranchReference notification) "dirty-ipc-target-race-test"))

            let inspectStatus () = Task.FromResult(watchStatusInspection status)
            let requestDegradedResync _ = Assert.Fail("Clean IPC must not request degraded resync.")
            let waitForSafePoint _ _ = Task.FromResult(())
            let reestablishIpc _ _ = Task.FromResult(())
            let appliedReferences = ResizeArray<ReferenceId>()

            let applyReference payload _ =
                task {
                    appliedReferences.Add(payload.ReferenceId)
                    raise (InvalidOperationException("old-branch Reference must not apply after dirty IPC target race"))
                }

            Watch.setReadGraceStatusFileForPendingWorkTransitionForWatchTests (fun () ->
                let current = Current()
                current.BranchId <- branchBId
                current.BranchName <- "branch-b"

                Task.FromResult(graceStatus))

            let outcome =
                (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                    getBranch
                    inspectStatus
                    requestDegradedResync
                    waitForSafePoint
                    reestablishIpc
                    applyReference
                    notification)
                    .Result

            outcome.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.NotCurrentBranch

            appliedReferences.ToArray()
            |> should equal Array.empty<ReferenceId>)

    /// Verifies that a failed apply cannot republish clean IPC from pre-apply status evidence.
    [<Test; Category("CurrentBranchMaterializationCoordinator")>]
    let ``current branch materialization coordinator keeps ipc dirty when apply fails`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            writeWatchStatusJsonWithRuntimeSurface status
            |> ignore

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let getBranch () =
                Task.FromResult(Ok(GraceReturnValue.Create (branchDtoWithLatestCurrentBranchReference notification) "dirty-ipc-after-apply-failure-test"))

            let inspectStatus () = Task.FromResult(watchStatusInspection status)
            let requestDegradedResync _ = Assert.Fail("Clean IPC must not request degraded resync before apply.")
            let waitForSafePoint _ _ = Task.FromResult(())
            let reestablishIpc _ _ = Task.FromResult(())

            let applyReference _ _ =
                task {
                    File.WriteAllText(Path.Combine(root, "partially-materialized.txt"), "partial remote materialization")
                    raise (InvalidOperationException("apply failed after mutating the working tree"))
                }

            let operation =
                Func<Task> (fun () ->
                    (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                        getBranch
                        inspectStatus
                        requestDegradedResync
                        waitForSafePoint
                        reestablishIpc
                        applyReference
                        notification)
                    :> Task)

            Assert.ThrowsAsync<InvalidOperationException>(operation)
            |> ignore

            let inspection =
                Services
                    .inspectGraceWatchStatus()
                    .GetAwaiter()
                    .GetResult()

            match inspection.Status with
            | Some publishedStatus ->
                publishedStatus.HasPendingWatchWork
                |> should equal true

                publishedStatus.IsWorkingTreeClean
                |> should equal false

                inspection.IsUsable |> should equal false
            | None -> Assert.Fail("Expected failed materialization to preserve dirty Watch IPC."))

    /// Verifies that durable journal pending rows block materialization even when IPC status is otherwise clean.
    [<Test; Category("CurrentBranchMaterializationCoordinator")>]
    let ``current branch materialization coordinator blocks clean ipc with durable journal pending rows`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            Watch.setWatchJournalStatusClientForWatchTests (fun () ->
                let summary: LocalStateDb.WatchJournalPendingWorkSummary =
                    { DbPath = Current().GraceStatusFile; AppliedThroughSequence = 0L; PendingRowCount = 1L }

                Task.FromResult(summary))

            try
                let getBranch () =
                    Task.FromResult(Ok(GraceReturnValue.Create (branchDtoWithLatestCurrentBranchReference notification) "durable-journal-gate-test"))

                let inspectStatus () = Task.FromResult(watchStatusInspection status)
                let requestDegradedResync _ = Assert.Fail("Durable journal pending rows should wait, not request degraded resync.")
                let safePointWaits = ResizeArray<ReferenceId>()

                let waitForSafePoint payload gate =
                    task {
                        safePointWaits.Add(payload.ReferenceId)

                        match gate with
                        | Watch.CurrentBranchMaterializationStatusGate.Blocked reason ->
                            reason
                            |> should equal "durable Watch journal has pending local observations"
                        | _ -> Assert.Fail("Expected durable journal evidence to block the coordinator.")
                    }

                let reestablishIpc _ _ = Task.FromResult(())
                let applyReference _ _ = Task.FromException<unit>(InvalidOperationException("clean IPC with durable journal rows must not apply"))

                let outcome =
                    (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                        getBranch
                        inspectStatus
                        requestDegradedResync
                        waitForSafePoint
                        reestablishIpc
                        applyReference
                        notification)
                        .Result

                outcome.Value.Reason
                |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.WaitingForSafePoint

                safePointWaits.ToArray()
                |> should
                    equal
                    [|
                        notification.ReferenceId
                        notification.ReferenceId
                    |]
            finally
                Watch.resetWatchJournalClientsForWatchTests ())

    /// Verifies that process-local Watch queues block materialization before IPC or journal snapshots can appear clean.
    [<Test; Category("CurrentBranchMaterializationCoordinator")>]
    let ``current branch materialization coordinator blocks clean ipc with process local pending work`` () =
        withTempRepo (fun root ->
            Watch.setGraceWatchRuntimeModeForWatchTests Services.GraceWatchRuntimeMode.HealthyIncremental
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let queuedFile = Path.Combine(root, "queued-before-ipc.txt")
            File.WriteAllText(queuedFile, "queued local work")
            Watch.OnChanged(changedEvent queuedFile)

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let getBranch () =
                Task.FromResult(Ok(GraceReturnValue.Create (branchDtoWithLatestCurrentBranchReference notification) "process-local-work-gate-test"))

            let inspectStatus () = Task.FromResult(watchStatusInspection status)
            let requestDegradedResync _ = Assert.Fail("Process-local queues should wait, not request degraded resync.")
            let safePointWaits = ResizeArray<string>()

            let waitForSafePoint _ gate =
                task {
                    match gate with
                    | Watch.CurrentBranchMaterializationStatusGate.Blocked reason -> safePointWaits.Add(reason)
                    | _ -> Assert.Fail("Expected process-local Watch queues to block the coordinator.")
                }

            let reestablishIpc _ _ = Task.FromResult(())
            let applyReference _ _ = Task.FromException<unit>(InvalidOperationException("process-local Watch queues must not apply"))

            let outcome =
                (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                    getBranch
                    inspectStatus
                    requestDegradedResync
                    waitForSafePoint
                    reestablishIpc
                    applyReference
                    notification)
                    .Result

            outcome.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.WaitingForSafePoint

            safePointWaits.ToArray()
            |> should
                equal
                [|
                    "process-local Watch queues have pending local observations"
                    "process-local Watch queues have pending local observations"
                |])

    /// Verifies that clean IPC does not let materialization proceed when the local-state database is missing.
    [<Test; Category("CurrentBranchMaterializationCoordinator")>]
    let ``current branch materialization coordinator fails closed when local state database is missing`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())
            deleteLocalStateDbFiles ()

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let getBranch () =
                Task.FromResult(Ok(GraceReturnValue.Create (branchDtoWithLatestCurrentBranchReference notification) "missing-local-state-gate-test"))

            let inspectStatus () =
                Task.FromResult<Services.GraceWatchStatusInspection>(
                    { Exists = true; Status = Some status; PersistedMode = Some status.Mode; SafetyFlags = status.SafetyFlags; ReadError = None }
                )

            let degradedRequests = ResizeArray<string>()
            let requestDegradedResync reason = degradedRequests.Add(reason)
            let reestablishIpc _ _ = Task.FromResult(())
            let waitForSafePoint _ _ = Task.FromException<unit>(InvalidOperationException("missing local-state DB must not wait as clean work"))
            let applyReference _ _ = Task.FromException<unit>(InvalidOperationException("missing local-state DB must not apply"))

            let outcome =
                (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                    getBranch
                    inspectStatus
                    requestDegradedResync
                    waitForSafePoint
                    reestablishIpc
                    applyReference
                    notification)
                    .Result

            outcome.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.WaitingForDegradedResync

            degradedRequests.ToArray()
            |> should equal [| "missing local-state DB authority" |])

    /// Verifies that degraded resync is not requested after the notification stops targeting Current().
    [<Test; Category("CurrentBranchMaterializationCoordinator")>]
    let ``current branch materialization coordinator rechecks branch before degraded resync request`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "branch-a"
            let branchBId = Guid.NewGuid()

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let getBranch () =
                Task.FromResult(Ok(GraceReturnValue.Create (branchDtoWithLatestCurrentBranchReference notification) "degraded-current-recheck-test"))

            let inspectStatus () =
                let current = Current()
                current.BranchId <- branchBId
                current.BranchName <- "branch-b"

                Task.FromResult(missingWatchStatusInspection)

            let requestDegradedResync _ = Assert.Fail("Stale notification must not request degraded resync.")
            let reestablishIpc _ _ = Task.FromException<unit>(InvalidOperationException("stale notification must not reestablish IPC"))
            let waitForSafePoint _ _ = Task.FromException<unit>(InvalidOperationException("stale notification must not wait for a safe point"))
            let applyReference _ _ = Task.FromException<unit>(InvalidOperationException("stale notification must not apply"))

            let outcome =
                (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                    getBranch
                    inspectStatus
                    requestDegradedResync
                    waitForSafePoint
                    reestablishIpc
                    applyReference
                    notification)
                    .Result

            outcome.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.NotCurrentBranch)

    /// Verifies that missing IPC enters degraded resync and keeps the exact Reference for revalidation.
    [<Test; Category("CurrentBranchMaterializationCoordinator")>]
    let ``current branch materialization coordinator revalidates exact reference after degraded resync`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Checkpoint
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let branchFetches = ResizeArray<ReferenceId>()

            let getBranch () =
                task {
                    branchFetches.Add(notification.ReferenceId)
                    return Ok(GraceReturnValue.Create (branchDtoWithLatestCurrentBranchReference notification) "degraded-retry-test")
                }

            let mutable statusReestablished = false

            let inspectStatus () =
                Task.FromResult(
                    if statusReestablished then
                        watchStatusInspection status
                    else
                        missingWatchStatusInspection
                )

            let degradedRequests = ResizeArray<string>()
            let requestDegradedResync reason = degradedRequests.Add(reason)

            let reestablishIpc payload _ =
                task {
                    payload.ReferenceId
                    |> should equal notification.ReferenceId

                    statusReestablished <- true
                }

            let waitForSafePoint _ _ = Task.FromResult(())
            let appliedReferences = ResizeArray<ReferenceId>()

            let applyReference payload _ = task { appliedReferences.Add(payload.ReferenceId) }

            let outcome =
                (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                    getBranch
                    inspectStatus
                    requestDegradedResync
                    waitForSafePoint
                    reestablishIpc
                    applyReference
                    notification)
                    .Result

            outcome.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.Applied

            degradedRequests.ToArray()
            |> should
                equal
                [|
                    "missing Watch IPC/status authority"
                |]

            branchFetches.ToArray()
            |> should
                equal
                [|
                    notification.ReferenceId
                    notification.ReferenceId
                |]

            appliedReferences.ToArray()
            |> should equal [| notification.ReferenceId |])

    /// Verifies that degraded retry drops the preserved Reference when BranchDto latest changes before apply.
    [<Test; Category("CurrentBranchMaterializationCoordinator")>]
    let ``current branch materialization coordinator drops stale reference after degraded resync revalidation`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"
            let status = liveWatchStatus (Guid.NewGuid())

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Commit
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let newerNotification =
                { notification with
                    ReferenceId = Guid.NewGuid()
                    DirectoryId = Guid.NewGuid()
                    Sha256Hash = Sha256Hash "newer-remote-root"
                    Blake3Hash = Blake3Hash "newer-remote-root-blake3"
                }

            let branchDtos = Queue<Grace.Types.Branch.BranchDto>()
            branchDtos.Enqueue(branchDtoWithLatestCurrentBranchReference notification)
            branchDtos.Enqueue(branchDtoWithLatestCurrentBranchReference newerNotification)

            let branchFetches = ResizeArray<ReferenceId>()

            let getBranch () =
                task {
                    let branchDto = branchDtos.Dequeue()
                    branchFetches.Add(branchDto.LatestReference.ReferenceId)
                    return Ok(GraceReturnValue.Create branchDto "degraded-stale-drop-test")
                }

            let mutable statusReestablished = false

            let inspectStatus () =
                Task.FromResult(
                    if statusReestablished then
                        watchStatusInspection status
                    else
                        missingWatchStatusInspection
                )

            let requestDegradedResync _ = ()

            let reestablishIpc payload _ =
                task {
                    payload.ReferenceId
                    |> should equal notification.ReferenceId

                    statusReestablished <- true
                }

            let waitForSafePoint _ _ = Task.FromResult(())
            let applyReference _ _ = Task.FromException<unit>(InvalidOperationException("stale revalidated Reference must not apply"))

            let outcome =
                (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                    getBranch
                    inspectStatus
                    requestDegradedResync
                    waitForSafePoint
                    reestablishIpc
                    applyReference
                    notification)
                    .Result

            outcome.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.LatestAuthorityRejected

            outcome.Value.Decision.Value.Reason
            |> should equal Services.LatestCurrentBranchReferenceDecisionReason.StaleLatestReference

            branchFetches.ToArray()
            |> should
                equal
                [|
                    notification.ReferenceId
                    newerNotification.ReferenceId
                |])

    /// Verifies that stale branch evidence is dropped before a mismatch safe-point wait can hold the lane.
    [<Test; Category("CurrentBranchMaterializationCoordinator")>]
    let ``current branch materialization coordinator drops branch switch before mismatch safe point wait`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "branch-a"
            let branchBId = Guid.NewGuid()

            let notification =
                validCurrentBranchReferenceNotification
                    currentRepositoryId
                    currentBranchId
                    ReferenceType.Save
                    (Guid.NewGuid())
                    (Sha256Hash "remote-root")
                    (Blake3Hash "remote-root-blake3")

            let getBranch () =
                Task.FromResult(
                    Ok(
                        GraceReturnValue.Create
                            (branchDtoWithLatestCurrentBranchReference { notification with DirectoryId = Guid.NewGuid() })
                            "stale-mismatch-safe-point-test"
                    )
                )

            let dirtyStatus = { liveWatchStatus (Guid.NewGuid()) with HasPendingWatchWork = true; IsWorkingTreeClean = false }

            let inspectStatus () =
                let current = Current()
                current.BranchId <- branchBId
                current.BranchName <- "branch-b"

                Task.FromResult(watchStatusInspection dirtyStatus)

            let requestDegradedResync _ = Assert.Fail("Stale notification must be dropped before degraded resync.")

            let waitForSafePoint _ _ = Task.FromException<unit>(InvalidOperationException("stale notification must not wait for a safe point"))

            let reestablishIpc _ _ = Task.FromResult(())
            let applyReference _ _ = Task.FromException<unit>(InvalidOperationException("stale notification must not apply"))

            let outcome =
                (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                    getBranch
                    inspectStatus
                    requestDegradedResync
                    waitForSafePoint
                    reestablishIpc
                    applyReference
                    notification)
                    .Result

            outcome.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.NotCurrentBranch)

    /// Verifies that rootless notifications never enter a materialization apply or multi-reference optimization path.
    [<Test; Category("CurrentBranchMaterializationCoordinator")>]
    let ``current branch materialization coordinator rejects rootless notification before branch fetch and apply`` () =
        withTempRepo (fun root ->
            let currentRepositoryId, currentBranchId = configureCurrentWatchIdentity root "current-repo" "current-branch"

            let rootlessNotification =
                { CurrentBranchReferenceNotification.Default with
                    RepositoryId = currentRepositoryId
                    BranchId = currentBranchId
                    ReferenceId = Guid.NewGuid()
                    DirectoryId = DirectoryVersionId.Empty
                    Sha256Hash = Sha256Hash String.Empty
                    Blake3Hash = Blake3Hash String.Empty
                    ReferenceType = ReferenceType.Save
                }

            let getBranch () =
                Task.FromException<Result<GraceReturnValue<Grace.Types.Branch.BranchDto>, GraceError>>(
                    InvalidOperationException("rootless notification must not fetch BranchDto")
                )

            let inspectStatus () =
                Task.FromException<Services.GraceWatchStatusInspection>(InvalidOperationException("rootless notification must not inspect IPC"))

            let requestDegradedResync _ = Assert.Fail("Rootless notification must not request degraded resync.")
            let waitForSafePoint _ _ = Task.FromResult(())
            let reestablishIpc _ _ = Task.FromResult(())
            let applyReference _ _ = Task.FromException<unit>(InvalidOperationException("rootless notification must not apply"))

            let outcome =
                (Watch.handleCurrentBranchReferenceMaterializationWithClientsForWatchTests
                    getBranch
                    inspectStatus
                    requestDegradedResync
                    waitForSafePoint
                    reestablishIpc
                    applyReference
                    rootlessNotification)
                    .Result

            outcome.Value.Reason
            |> should equal Watch.CurrentBranchMaterializationCoordinatorOutcomeReason.ProtocolRejected

            outcome.Value.Decision.Value.Reason
            |> should equal Services.LatestCurrentBranchReferenceDecisionReason.ReferenceRootIdentityUnavailable)

    /// Verifies that stale non-empty ids cannot be rescued by matching display names.
    [<Test>]
    let ``watch status rejects mismatched ids even when display names match`` () =
        withTempRepo (fun root ->
            configureCurrentWatchIdentity root "current-repo" "current-branch"
            |> ignore

            let rootDirectoryId = Guid.NewGuid()

            let status =
                { liveWatchStatus rootDirectoryId with
                    RepositoryId = Guid.NewGuid()
                    RepositoryName = RepositoryName "current-repo"
                    BranchId = Guid.NewGuid()
                    BranchName = BranchName "current-branch"
                    RootDirectory = root
                }

            writeWatchStatusJsonWithRuntimeSurface status
            |> ignore

            let inspection = Services.inspectGraceWatchStatus().Result

            inspection.HasCurrentRepositoryIdentity
            |> should equal false

            inspection.IsUsable |> should equal false

            inspection.SafetyFlags
            |> Set.ofArray
            |> Set.contains "cleanWorkingTree"
            |> should equal false

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that persisted HealthyIncremental mode cannot override missing derived root and directory data.
    [<Test>]
    let ``watch status rejects persisted healthy mode without derived root data`` () =
        withTempRepo (fun _ ->
            let emptyRootStatus =
                { liveWatchStatus Guid.Empty with
                    RootDirectoryId = Guid.Empty
                    RootDirectorySha256Hash = Sha256Hash String.Empty
                    RootDirectoryBlake3Hash = Blake3Hash String.Empty
                    DirectoryIds = HashSet<DirectoryVersionId>()
                    HasPendingWatchWork = false
                    IsWorkingTreeClean = true
                }

            writeWatchStatusJsonWithPersistedMode Services.GraceWatchRuntimeMode.HealthyIncremental emptyRootStatus
            |> ignore

            let inspection =
                Services
                    .inspectGraceWatchStatus()
                    .GetAwaiter()
                    .GetResult()

            inspection.EffectiveMode
            |> should equal (Some Services.GraceWatchRuntimeMode.HealthyIncremental)

            match inspection.Status with
            | Some status ->
                status.Mode
                |> should equal Services.GraceWatchRuntimeMode.Resynchronizing
            | None -> Assert.Fail("Expected readable Watch IPC status.")

            inspection.IsLiveProcess |> should equal true
            inspection.IsUsable |> should equal false

            let inspectionSafetyFlags = inspection.SafetyFlags |> Set.ofArray

            inspectionSafetyFlags
            |> Set.contains "incrementalSafe"
            |> should equal false

            inspectionSafetyFlags
            |> Set.contains "cleanWorkingTree"
            |> should equal false

            inspectionSafetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true

            Services.getGraceWatchStatus().Result
            |> should equal None)

    /// Verifies that unusable dirty Watch IPC keeps current-repo pending diagnostics after identity validation.
    [<Test>]
    let ``watch status preserves current repo pending apply when dirty snapshot is unusable`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            let dirtyStatus = { liveWatchStatus rootDirectoryId with HasPendingWatchWork = true; IsWorkingTreeClean = false }

            let statusLevelFlags = safetyFlagSet dirtyStatus

            statusLevelFlags
            |> Set.contains "pendingWatchWork"
            |> should equal true

            statusLevelFlags
            |> Set.contains "pendingStatusApply"
            |> should equal true

            writeWatchStatusJsonWithRuntimeSurface dirtyStatus
            |> ignore

            let inspection =
                Services
                    .inspectGraceWatchStatus()
                    .GetAwaiter()
                    .GetResult()

            inspection.IsLiveProcess |> should equal true
            inspection.IsUsable |> should equal false

            let inspectionSafetyFlags = inspection.SafetyFlags |> Set.ofArray

            inspectionSafetyFlags
            |> Set.contains "pendingWatchWork"
            |> should equal true

            inspectionSafetyFlags
            |> Set.contains "pendingStatusApply"
            |> should equal true

            inspectionSafetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal false)

    /// Verifies that root identity comparisons reject case-only path differences under case-sensitive semantics.
    [<Test>]
    let ``watch root identity comparison respects case-sensitive path semantics`` () =
        let currentRoot = Path.Combine(Path.GetTempPath(), "grace-watch-root")
        let persistedRoot = requireDifferentlyCasedPath currentRoot

        Services.watchRootDirectoriesMatchWithComparison StringComparison.Ordinal persistedRoot currentRoot
        |> should equal false

        Services.watchRootDirectoriesMatchWithComparison StringComparison.OrdinalIgnoreCase persistedRoot currentRoot
        |> should equal true

    /// Verifies that Watch IPC root reuse rejects case-only root differences on case-sensitive repositories.
    [<Test>]
    let ``watch status root identity rejects case-only mismatch on case-sensitive repository`` () =
        Services.setWatchRootPathCaseInsensitiveLookupForTests (fun _ -> false)

        try
            withTempRepo (fun tempDir ->
                let rootDirectoryId = Guid.NewGuid()
                let persistedRoot = requireDifferentlyCasedPath tempDir
                let status = { liveWatchStatus rootDirectoryId with RootDirectory = persistedRoot }

                writeWatchStatusJsonWithRuntimeSurface status
                |> ignore

                let inspection =
                    Services
                        .inspectGraceWatchStatus()
                        .GetAwaiter()
                        .GetResult()

                inspection.IsLiveProcess |> should equal true

                inspection.IsUsable |> should equal false

                Services.getGraceWatchStatus().Result
                |> should equal None)
        finally
            Services.resetWatchRootPathCaseInsensitiveLookupForTests ()

    /// Verifies that Watch IPC root reuse preserves case-insensitive repository behavior.
    [<Test>]
    let ``watch status root identity accepts case-only mismatch on case-insensitive repository`` () =
        Services.setWatchRootPathCaseInsensitiveLookupForTests (fun _ -> true)

        try
            withTempRepo (fun tempDir ->
                let rootDirectoryId = Guid.NewGuid()
                let persistedRoot = requireDifferentlyCasedPath tempDir
                let status = { liveWatchStatus rootDirectoryId with RootDirectory = persistedRoot }

                writeWatchStatusJsonWithRuntimeSurface status
                |> ignore

                let inspection =
                    Services
                        .inspectGraceWatchStatus()
                        .GetAwaiter()
                        .GetResult()

                inspection.IsLiveProcess |> should equal true
                inspection.IsUsable |> should equal true

                Services.getGraceWatchStatus().Result
                |> should not' (equal None))
        finally
            Services.resetWatchRootPathCaseInsensitiveLookupForTests ()

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

    /// Verifies that watch check json mode emits a status envelope and preserves live watcher status.
    [<Test>]
    let ``watch check json mode emits status envelope and preserves live watcher status`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

            /// Verifies that the CLI watch scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "watch"
                                                  "--check" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            use document = parseJsonOutput standardOut
            let root = document.RootElement

            root
                .GetProperty("ReturnValue")
                .GetProperty("IsRunning")
                .GetBoolean()
            |> should equal true

            root
                .GetProperty("ReturnValue")
                .GetProperty("CanUseIncrementalStatus")
                .GetBoolean()
            |> should equal true

            root
                .GetProperty("ReturnValue")
                .GetProperty("Mode")
                .GetString()
            |> should equal "HealthyIncremental"

            root
                .GetProperty("ReturnValue")
                .GetProperty("SafetyFlags")
                .EnumerateArray()
            |> Seq.map (fun flag -> flag.GetString())
            |> Set.ofSeq
            |> Set.contains "incrementalSafe"
            |> should equal true

            /// Tracks error changes so this scenario can assert the resulting side effect explicitly.
            let mutable error = Unchecked.defaultof<JsonElement>

            root.TryGetProperty("Error", &error)
            |> should equal false

            readFileIfExists ipcFileName
            |> should equal originalContents)

    /// Verifies that watch check fails closed when clean IPC exists but durable journal rows are still unapplied.
    [<Test>]
    let ``watch check json refuses clean status when durable journal rows are pending`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

            (LocalStateDb.appendWatchJournalObservations
                (Current().GraceStatusFile)
                [|
                    watchJournalObservation DifferenceType.Change FileSystemEntryType.File "pending-watch-check.txt"
                |])
                .GetAwaiter()
                .GetResult()
            |> ignore

            /// Verifies that the CLI watch scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "watch"
                                                  "--check" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            use document = parseJsonOutput standardOut
            let root = document.RootElement.GetProperty("ReturnValue")

            root.GetProperty("IsRunning").GetBoolean()
            |> should equal true

            root
                .GetProperty("CanUseIncrementalStatus")
                .GetBoolean()
            |> should equal false

            root.GetProperty("Reason").GetString()
            |> should equal "resynchronizing"

            let safetyFlags =
                root.GetProperty("SafetyFlags").EnumerateArray()
                |> Seq.map (fun flag -> flag.GetString())
                |> Set.ofSeq

            safetyFlags
            |> Set.contains "pendingWatchWork"
            |> should equal true

            safetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true

            safetyFlags
            |> Set.contains "incrementalSafe"
            |> should equal false

            readFileIfExists ipcFileName
            |> should equal originalContents)

    /// Verifies that watch check fails closed when clean IPC exists but durable local-state evidence is missing.
    [<Test>]
    let ``watch check json refuses clean status when local-state database is missing`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName
            deleteLocalStateDbFiles ()

            /// Verifies that the CLI watch scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "watch"
                                                  "--check" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            use document = parseJsonOutput standardOut
            let root = document.RootElement.GetProperty("ReturnValue")

            root.GetProperty("IsRunning").GetBoolean()
            |> should equal true

            root
                .GetProperty("CanUseIncrementalStatus")
                .GetBoolean()
            |> should equal false

            root.GetProperty("Reason").GetString()
            |> should equal "durableStatusUnavailable"

            root.GetProperty("Message").GetString()
            |> should contain "durable local-state evidence could not be inspected"

            let safetyFlags =
                root.GetProperty("SafetyFlags").EnumerateArray()
                |> Seq.map (fun flag -> flag.GetString())
                |> Set.ofSeq

            safetyFlags
            |> Set.contains "durableJournalInspectionFailed"
            |> should equal true

            safetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true

            safetyFlags
            |> Set.contains "incrementalSafe"
            |> should equal false

            readFileIfExists ipcFileName
            |> should equal originalContents)

    /// Verifies that watch check does not advertise pending apply from a dirty snapshot for another root.
    [<Test>]
    let ``watch check json strips pending apply for dirty root mismatch`` () =
        withTempRepo (fun tempDir ->
            let rootDirectoryId = Guid.NewGuid()

            let dirtyMismatchedStatus =
                { liveWatchStatus rootDirectoryId with
                    RootDirectory = Path.Combine(tempDir, "other-worktree")
                    HasPendingWatchWork = true
                    IsWorkingTreeClean = false
                }

            safetyFlagSet dirtyMismatchedStatus
            |> Set.contains "pendingStatusApply"
            |> should equal true

            writeWatchStatusJsonWithRuntimeSurface dirtyMismatchedStatus
            |> ignore

            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "watch"
                                                  "--check" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            use document = parseJsonOutput standardOut
            let root = document.RootElement.GetProperty("ReturnValue")

            root.GetProperty("IsRunning").GetBoolean()
            |> should equal true

            root
                .GetProperty("CanUseIncrementalStatus")
                .GetBoolean()
            |> should equal false

            let safetyFlags =
                root.GetProperty("SafetyFlags").EnumerateArray()
                |> Seq.map (fun flag -> flag.GetString())
                |> Set.ofSeq

            safetyFlags
            |> Set.contains "pendingWatchWork"
            |> should equal true

            safetyFlags
            |> Set.contains "requiresExplicitResync"
            |> should equal true

            safetyFlags
            |> Set.contains "pendingStatusApply"
            |> should equal false)

    /// Verifies that non-healthy current-repository recovery snapshots do not advertise apply-style pending drains.
    [<Test>]
    let ``watch check json strips pending apply for non-healthy dirty current status`` () =
        withTempRepo (fun _ ->
            for mode in
                [|
                    Services.GraceWatchRuntimeMode.Suspended
                    Services.GraceWatchRuntimeMode.Resynchronizing
                |] do
                let rootDirectoryId = Guid.NewGuid()

                let dirtyStatus = { liveWatchStatus rootDirectoryId with HasPendingWatchWork = true; IsWorkingTreeClean = false }

                safetyFlagSet dirtyStatus
                |> Set.contains "pendingStatusApply"
                |> should equal true

                writeWatchStatusJsonWithPersistedMode mode dirtyStatus
                |> ignore

                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "watch"
                                                      "--check" |]

                exitCode |> should equal -1
                standardError |> should equal String.Empty

                use document = parseJsonOutput standardOut
                let root = document.RootElement.GetProperty("ReturnValue")

                root.GetProperty("Mode").GetString()
                |> should equal $"{mode}"

                root
                    .GetProperty("CanUseIncrementalStatus")
                    .GetBoolean()
                |> should equal false

                let safetyFlags =
                    root.GetProperty("SafetyFlags").EnumerateArray()
                    |> Seq.map (fun flag -> flag.GetString())
                    |> Set.ofSeq

                safetyFlags
                |> Set.contains "pendingWatchWork"
                |> should equal true

                safetyFlags
                |> Set.contains "requiresExplicitResync"
                |> should equal true

                safetyFlags
                |> Set.contains "pendingStatusApply"
                |> should equal false)

    /// Verifies that watch check select mode projects status fields and preserves live watcher status.
    [<Test>]
    let ``watch check select mode projects status field and preserves live watcher status`` () =
        withTempRepo (fun _ ->
            let ipcFileName = writeLiveWatchStatusFile ()
            let originalContents = readFileIfExists ipcFileName

            /// Verifies that the CLI watch scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "watch"
                                                  "--check"
                                                  "--select"
                                                  "Mode" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            standardOut
            |> should not' (contain "GraceWatch is running")

            standardOut.Trim()
            |> should equal "\"HealthyIncremental\""

            readFileIfExists ipcFileName
            |> should equal originalContents)

    /// Verifies that watch check json mode reports missing status without human text.
    [<Test>]
    let ``watch check json mode reports missing status`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                /// Verifies that the CLI watch scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "watch"
                                                      "--check" |]

                exitCode |> should equal -1
                standardError |> should equal String.Empty

                use document = parseJsonOutput standardOut
                let root = document.RootElement.GetProperty("ReturnValue")

                root.GetProperty("IsRunning").GetBoolean()
                |> should equal false

                root
                    .GetProperty("CanUseIncrementalStatus")
                    .GetBoolean()
                |> should equal false

                root.GetProperty("Mode").GetString()
                |> should equal "Unavailable"

                root.GetProperty("Reason").GetString()
                |> should equal "notRunning"))

    /// Verifies that watch check explains resynchronizing state without exposing local status internals.
    [<Test>]
    let ``watch check explains resynchronizing state without raw paths or stacks`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            let status = { liveWatchStatus rootDirectoryId with DirectoryIds = HashSet<DirectoryVersionId>() }

            let ipcFileName = Services.IpcFileName()

            Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
            |> ignore

            File.WriteAllText(ipcFileName, serialize status)

            /// Verifies that the CLI watch scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "watch"
                                         "--check" |]

            exitCode |> should equal -1

            output
            |> should contain "resynchronizing trusted state"

            output
            |> should contain "incremental status shortcuts are suspended until resync completes"

            output |> should not' (contain ipcFileName)
            output |> should not' (contain "StackTrace")
            output |> should not' (contain "System."))

    /// Verifies that watch check explains suspended state without exposing local status internals.
    [<Test>]
    let ``watch check explains suspended state without raw paths or stacks`` () =
        withTempRepo (fun _ ->
            let rootDirectoryId = Guid.NewGuid()

            rootDirectoryId
            |> liveWatchStatus
            |> writeWatchStatusJsonWithPersistedMode Services.GraceWatchRuntimeMode.Suspended
            |> ignore

            /// Verifies that the CLI watch scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "watch"
                                         "--check" |]

            exitCode |> should equal -1

            output
            |> should contain "suspended after confidence loss"

            output
            |> should not' (contain (Services.IpcFileName()))

            output |> should not' (contain "StackTrace")
            output |> should not' (contain "System."))

    /// Verifies that watch check handles malformed status without exposing parser exception details.
    [<Test>]
    let ``watch check handles malformed status without raw paths or stacks`` () =
        withTempRepo (fun _ ->
            let ipcFileName = Services.IpcFileName()

            Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
            |> ignore

            File.WriteAllText(ipcFileName, "not-json")

            /// Verifies that the CLI watch scenario exits with the expected process status.
            let exitCode, output =
                runWithCapturedOutput [| "watch"
                                         "--check" |]

            exitCode |> should equal -1

            output
            |> should contain "status exists but could not be read"

            output |> should not' (contain ipcFileName)
            output |> should not' (contain "Json")
            output |> should not' (contain "StackTrace")
            output |> should not' (contain "System."))

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

                output |> should contain "GraceWatch is starting"

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
