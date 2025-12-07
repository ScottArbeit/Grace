namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.SDK
open Grace.SDK.Common
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Branch
open Grace.Shared.Services
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http.Connections
open Microsoft.AspNetCore.SignalR.Client
open NodaTime
open Spectre.Console
open System
open System.Buffers
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.ComponentModel
open System.Diagnostics
open System.Globalization
open System.IO
open System.IO.Compression
open System.IO.Enumeration
open System.Linq
open System.Net.Http
open System.Reactive.Linq
open System.Security.Cryptography
open System.Text.Json
open System.Threading.Tasks
open System.Threading
open System.Collections.Concurrent
open Spectre.Console
open System.Text
open Grace.Shared.Parameters.Storage

module Watch =

    /// Holds a list of the created or changed files that we need to process, as determined by the FileSystemWatcher.
    ///
    /// Note: We're using ConcurrentDictionary because it's safe for multithreading, doesn't allow us to insert the same key twice, and for its algorithms. We're not using the values of the ConcurrentDictionary here, only the keys.
    let private filesToProcess = ConcurrentDictionary<string, unit>()

    /// Holds a list of the created or changed directories that we need to process, as determined by the FileSystemWatcher.
    ///
    /// Note: We're using ConcurrentDictionary because it's safe for multithreading, doesn't allow us to insert the same key twice, and for its algorithms. We're not using the values of the ConcurrentDictionary here, only the keys.
    let private directoriesToProcess = ConcurrentDictionary<string, unit>()

    type WatchParameters() =
        inherit ParameterBase()
        member val public RepositoryPath: string = String.Empty with get, set
        member val public NamedSections: string[] = Array.empty with get, set

    let mutable private graceStatus = GraceStatus.Default
    let mutable graceStatusMemoryStream: MemoryStream = null
    let mutable graceStatusHasChanged = false

    let fileDeleted filePath = logToConsole $"In Delete: filePath: {filePath}"

    let isNotDirectory path = not <| Directory.Exists(path)
    let updateInProgress () = File.Exists(updateInProgressFileName)
    let updateNotInProgress () = not <| updateInProgress ()

    let OnCreated (args: FileSystemEventArgs) =
        // Ignore directory creation; need to think about this more... should we capture new empty directories?
        if updateNotInProgress () && isNotDirectory args.FullPath then
            let shouldIgnore = shouldIgnoreFile args.FullPath
            //logToAnsiConsole Colors.Verbose $"Should ignore {args.FullPath}: {shouldIgnore}."

            if not <| shouldIgnore then
                logToAnsiConsole Colors.Added $"I saw that {args.FullPath} was created."
                filesToProcess.TryAdd(args.FullPath, ()) |> ignore

    let OnChanged (args: FileSystemEventArgs) =
        if updateNotInProgress () && isNotDirectory args.FullPath then
            let shouldIgnore = shouldIgnoreFile args.FullPath
            //logToAnsiConsole Colors.Verbose $"Should ignore {args.FullPath}: {shouldIgnore}."

            if not <| shouldIgnore then
                logToAnsiConsole Colors.Changed $"I saw that {args.FullPath} changed."
                filesToProcess.TryAdd(args.FullPath, ()) |> ignore

            // Special handling for the Grace status file; if that is the changed file, we'll set this flag so we reload it in OnWatch() in the main loop
            if (args.FullPath = Current().GraceStatusFile) && (not <| graceStatusHasChanged) then
                //logToAnsiConsole Colors.Important $"Setting graceStatusHasChanged to true in OnChanged(). Current value: {graceStatusHasChanged}."
                graceStatusHasChanged <- true
                logToAnsiConsole Colors.Important $"Grace Status file has been updated."

    let OnDeleted (args: FileSystemEventArgs) =
        if updateNotInProgress () && isNotDirectory args.FullPath then
            let shouldIgnore = shouldIgnoreFile args.FullPath
            //logToAnsiConsole Colors.Verbose $"Should ignore {args.FullPath}: {shouldIgnore}."

            if not <| shouldIgnore then
                logToAnsiConsole Colors.Deleted $"I saw that {args.FullPath} was deleted."
                logToAnsiConsole Colors.Deleted $"Delete processing is not yet implemented."

    let OnRenamed (args: RenamedEventArgs) =
        if updateNotInProgress () then
            let shouldIgnoreOldFile = shouldIgnoreFile args.OldFullPath
            let shouldIgnoreNewFile = shouldIgnoreFile args.FullPath

            if not <| shouldIgnoreOldFile then
                logToAnsiConsole Colors.Changed $"I saw that {args.OldFullPath} was renamed to {args.FullPath}."
                //logToAnsiConsole Colors.Verbose $"Should ignore {args.OldFullPath}: {shouldIgnoreOldFile}. Should ignore {args.FullPath}: {shouldIgnoreNewFile}."
                logToAnsiConsole Colors.Changed $"Delete processing is not yet implemented."

            if not <| shouldIgnoreNewFile then
                logToAnsiConsole Colors.Changed $"I saw that {args.OldFullPath} was renamed to {args.FullPath}."
                //logToAnsiConsole Colors.Verbose $"Should ignore {args.OldFullPath}: {shouldIgnoreOldFile}. Should ignore {args.FullPath}: {shouldIgnoreNewFile}."
                filesToProcess.TryAdd(args.FullPath, ()) |> ignore

    let OnError (args: ErrorEventArgs) =
        let correlationId = generateCorrelationId ()

        logToAnsiConsole Colors.Error $"I saw that the FileSystemWatcher threw an exception: {args.GetException().Message}. grace watch should be restarted."

    let OnGraceUpdateInProgressCreated (args: FileSystemEventArgs) =
        if args.FullPath = updateInProgressFileName then
            if updateInProgress () then
                logToAnsiConsole Colors.Important $"Update is in progress from another Grace instance."
            else
                logToAnsiConsole Colors.Important $"{updateInProgressFileName} should already exist, but it doesn't."

    let OnGraceUpdateInProgressDeleted (args: FileSystemEventArgs) =
        if args.FullPath = updateInProgressFileName then
            if updateNotInProgress () then
                logToAnsiConsole Colors.Important $"Update has finished in another Grace instance."
            else
                logToAnsiConsole Colors.Important $"{updateInProgressFileName} should have been deleted, but it hasn't yet."

    /// Creates a FileSystemWatcher for the given path.
    let createFileSystemWatcher path =
        let fileSystemWatcher = new FileSystemWatcher(path)
        fileSystemWatcher.InternalBufferSize <- (64 * 1024) // Default is 4K, choosing maximum of 64K for safety.
        fileSystemWatcher.IncludeSubdirectories <- true

        fileSystemWatcher.NotifyFilter <-
            NotifyFilters.DirectoryName
            ||| NotifyFilters.FileName
            ||| NotifyFilters.LastWrite
            ||| NotifyFilters.Security

        fileSystemWatcher

    let printDifferences (differences: List<FileSystemDifference>) =
        if differences.Count > 0 then
            logToAnsiConsole Colors.Verbose $"Differences detected since last save/checkpoint/commit:"

        for difference in differences.OrderBy(fun diff -> diff.RelativePath) do
            logToAnsiConsole
                Colors.Verbose
                $"{getDiscriminatedUnionCaseName difference.DifferenceType} for {getDiscriminatedUnionCaseName difference.FileSystemEntryType} {difference.RelativePath}"

    /// Update the Grace Object Cache file with the new DirectoryVersions.
    let updateObjectCacheFile (newDirectoryVersions: List<LocalDirectoryVersion>) =
        task {
            let! objectCache = readGraceObjectCacheFile ()

            for directoryVersion in newDirectoryVersions.OrderByDescending(fun dv -> countSegments dv.RelativePath) do
                objectCache.Index.AddOrUpdate(directoryVersion.DirectoryVersionId, (fun _ -> directoryVersion), (fun _ _ -> directoryVersion))
                |> ignore

            do! writeGraceObjectCacheFile objectCache
        }

    /// Updates the Grace Status file's Index with updates detected from the file system.
    let updateGraceStatus graceStatus correlationId =
        task {
            // Get the list of differences between what's in the working directory, and what Grace Index knows about.
            let! differences = scanForDifferences graceStatus
            printDifferences differences

            // Get an updated Grace Index, and any new DirectoryVersions that were needed to build it.
            let! (newGraceStatus, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions graceStatus differences

            // Log the changes.
            for dv in newDirectoryVersions do
                logToAnsiConsole
                    Colors.Verbose
                    $"new Sha256Hash: {dv.Sha256Hash.Substring(0, 8)}; DirectoryId: {dv.DirectoryVersionId.ToString().Substring(0, 9)}...; RelativePath: {dv.RelativePath}"

            // Upload the new directory versions.
            let! result = uploadDirectoryVersions newDirectoryVersions correlationId

            match result with
            | Ok returnValue ->
                do! updateObjectCacheFile newDirectoryVersions

                let fileDifferences = differences.Where(fun diff -> diff.FileSystemEntryType = FileSystemEntryType.File).ToList()

                let message =
                    if fileDifferences |> Seq.isEmpty then
                        String.Empty
                    else
                        let sb = stringBuilderPool.Get()

                        try
                            for fileDifference in fileDifferences do
                                //sb.AppendLine($"{(getDiscriminatedUnionCaseNameToString fileDifference.DifferenceType)}: {fileDifference.RelativePath}") |> ignore
                                match fileDifference.DifferenceType with
                                | Change -> sb.AppendLine($"{fileDifference.RelativePath}") |> ignore
                                | Add -> sb.AppendLine($"Add {fileDifference.RelativePath}") |> ignore
                                | Delete -> sb.AppendLine($"Delete {fileDifference.RelativePath}") |> ignore

                            let saveMessage = sb.ToString()
                            saveMessage.Remove(saveMessage.LastIndexOf(Environment.NewLine), Environment.NewLine.Length)
                        finally
                            stringBuilderPool.Return(sb)

                // If there are changes either to files or just to directories, create a save reference.
                if (differences.Count > 0) then
                    match! createSaveReference (getRootDirectoryVersion newGraceStatus) message correlationId with
                    | Ok returnValue ->
                        let newGraceStatusWithUpdatedTime = { newGraceStatus with LastSuccessfulDirectoryVersionUpload = getCurrentInstant () }
                        // Write the new Grace Status file to disk.
                        do! writeGraceStatusFile newGraceStatusWithUpdatedTime
                        //logToAnsiConsole Colors.Important $"Setting graceStatusHasChanged to false in updateGraceStatus(). Current value: {graceStatusHasChanged}."
                        graceStatusHasChanged <- false // We *just* changed it ourselves, so we don't have to re-process it in the timer loop.
                        return Some newGraceStatusWithUpdatedTime
                    | Error error ->
                        logToAnsiConsole Colors.Error $"{Markup.Escape(error.Error)}"
                        return None
                else
                    // There were no changes to process, so just return the existing GraceStatus.
                    //logToAnsiConsole Colors.Verbose "No fileDifferences or newDirectoryVersions to process; not updating GraceStatus."
                    return Some graceStatus
            | Error error ->
                logToAnsiConsole Colors.Error $"{Markup.Escape(error.Error)}"
                return None
        }

    /// Copies a file from the working directory to the object directory, with its SHA-256 hash, and then uploads it to storage.
    let copyFileToObjectDirectoryAndUploadToStorage (getUploadMetadataForFilesParameters: GetUploadMetadataForFilesParameters) fullPath =
        task {
            //logToConsole $"*In fileChanged for {fullPath}."
            match! copyToObjectDirectory fullPath with
            | Some fileVersion ->
                getUploadMetadataForFilesParameters.FileVersions <- [| fileVersion |]

                match! uploadFilesToObjectStorage getUploadMetadataForFilesParameters with
                | Ok returnValue -> logToAnsiConsole Colors.Verbose $"File {fileVersion.GetObjectFileName} has been uploaded to storage."
                | Error error -> logToAnsiConsole Colors.Error $"**Failed to upload {fileVersion.GetObjectFileName} to storage."
            | None -> ()
        }

    /// Decompresses the GraceStatus information from the memory stream.
    let retrieveGraceStatusFromMemoryStream () =
        task {
            logToAnsiConsole Colors.Verbose $"Retrieving Grace Status from compressed memory stream."
            graceStatusMemoryStream.Position <- 0
            use gzStream = new GZipStream(graceStatusMemoryStream, CompressionMode.Decompress)

            let! retrievedGraceStatus = JsonSerializer.DeserializeAsync<GraceStatus>(gzStream, Constants.JsonSerializerOptions)
            graceStatus <- retrievedGraceStatus
            logToAnsiConsole Colors.Verbose $"Retrieved Grace Status from compressed memory stream."

            do! gzStream.DisposeAsync() // Dispose the GZipStream first, before disposing the MemoryStream.
            do! graceStatusMemoryStream.DisposeAsync()
            graceStatusMemoryStream <- null
        }

    /// Compresses the GraceStatus information into a gzipped memory stream.
    let storeGraceStatusInMemoryStream () =
        task {
            logToAnsiConsole Colors.Verbose $"Storing Grace Status in compressed memory stream."
            graceStatusMemoryStream <- new MemoryStream()

            use gzStream = new GZipStream(graceStatusMemoryStream, CompressionLevel.SmallestSize, leaveOpen = true)

            do! serializeAsync gzStream graceStatus
            do! gzStream.FlushAsync()
            do! graceStatusMemoryStream.FlushAsync()
            logToAnsiConsole Colors.Verbose $"Stored Grace Status in compressed memory stream."
            do! gzStream.DisposeAsync()
            graceStatus <- GraceStatus.Default
        }

    /// Processes any changed files since the last timer tick.
    let processChangedFiles () =
        task {
            // First, check if there's anything to process.
            if not (filesToProcess.IsEmpty && directoriesToProcess.IsEmpty) then
                try
                    let correlationId = generateCorrelationId ()
                    let! graceStatusFromDisk = readGraceStatusFile ()
                    graceStatus <- graceStatusFromDisk

                    let mutable lastFileUploadInstant = graceStatus.LastSuccessfulFileUpload

                    /// This is just a way to throw away the unit value from the ConcurrentDictionary.
                    let mutable unitValue = ()

                    // Loop through no more than 50 files. Copy them to the objects directory, and upload them to storage.
                    //   In the incredibly rare event that more than 50 files have changed, we'll get 50-per-timer-tick,
                    //   and clear the queue quickly without overwhelming the system.
                    let getUploadMetadataForFilesParameters =
                        GetUploadMetadataForFilesParameters(
                            OwnerId = $"{Current().OwnerId}",
                            OrganizationId = $"{Current().OrganizationId}",
                            RepositoryId = $"{Current().RepositoryId}",
                            CorrelationId = correlationId
                        )

                    for fileName in filesToProcess.Keys.Take(50) do
                        if filesToProcess.TryRemove(fileName, &unitValue) then
                            logToAnsiConsole Colors.Verbose $"Processing {fileName}. filesToProcess.Count: {filesToProcess.Count}."
                            do! copyFileToObjectDirectoryAndUploadToStorage getUploadMetadataForFilesParameters (FilePath fileName)
                            lastFileUploadInstant <- getCurrentInstant ()

                    graceStatus <- { graceStatus with LastSuccessfulFileUpload = lastFileUploadInstant }

                    // If we've drained all of the files that changed (and we'll almost always have done so), update all the things:
                    //   GraceStatus, directory versions, etc.
                    if filesToProcess.IsEmpty then
                        match! (updateGraceStatus graceStatus correlationId) with
                        | Some newGraceStatus -> graceStatus <- newGraceStatus
                        | None ->
                            logToAnsiConsole Colors.Important $"Grace Status file was not updated."
                            () // Something went wrong, don't update the in-memory Grace Status.

                    do! updateGraceWatchInterprocessFile graceStatus

                    // Reset the in-memory Grace Status to empty to minimize memory usage.
                    graceStatus <- GraceStatus.Default
                    GC.Collect(2, GCCollectionMode.Forced, blocking = true, compacting = true)
                with ex ->
                    logToAnsiConsole
                        Colors.Error
                        $"Error in processChangedFiles: Message: {ex.Message}{Environment.NewLine}{Environment.NewLine}{ex.StackTrace}"
            // Refresh the file every (just under) 5 minutes to indicate that `grace watch` is still alive.
            elif graceWatchStatusUpdateTime < getCurrentInstant().Minus(Duration.FromMinutes(4.8)) then
                let! graceStatusFromDisk = readGraceStatusFile ()
                do! updateGraceWatchInterprocessFile graceStatusFromDisk
                GC.Collect(2, GCCollectionMode.Forced, blocking = true, compacting = true)
        }

    type Watch() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) =
            task {
                try
                    // Create the FileSystemWatcher, but don't enable it yet.
                    use rootDirectoryFileSystemWatcher = createFileSystemWatcher (Current().RootDirectory)

                    use created =
                        Observable
                            .FromEventPattern<FileSystemEventArgs>(rootDirectoryFileSystemWatcher, "Created")
                            .Select(fun e -> e.EventArgs)
                            .Subscribe(OnCreated)

                    use changed =
                        Observable
                            .FromEventPattern<FileSystemEventArgs>(rootDirectoryFileSystemWatcher, "Changed")
                            .Select(fun e -> e.EventArgs)
                            .Subscribe(OnChanged)

                    use deleted =
                        Observable
                            .FromEventPattern<FileSystemEventArgs>(rootDirectoryFileSystemWatcher, "Deleted")
                            .Select(fun e -> e.EventArgs)
                            .Subscribe(OnDeleted)

                    use renamed =
                        Observable
                            .FromEventPattern<RenamedEventArgs>(rootDirectoryFileSystemWatcher, "Renamed")
                            .Select(fun e -> e.EventArgs)
                            .Subscribe(OnRenamed)

                    use errored =
                        Observable.FromEventPattern<ErrorEventArgs>(rootDirectoryFileSystemWatcher, "Error").Select(fun e -> e.EventArgs).Subscribe(OnError) // I want all of the errors.

                    Directory.CreateDirectory(Path.GetDirectoryName(updateInProgressFileName))
                    |> ignore

                    use updateInProgressFileSystemWatcher = createFileSystemWatcher (Path.GetDirectoryName(updateInProgressFileName))

                    use updateInProgressChanged =
                        Observable
                            .FromEventPattern<FileSystemEventArgs>(updateInProgressFileSystemWatcher, "Created")
                            .Select(fun e -> e.EventArgs)
                            .Subscribe(OnGraceUpdateInProgressCreated)

                    use updateInProgressDeleted =
                        Observable
                            .FromEventPattern<FileSystemEventArgs>(updateInProgressFileSystemWatcher, "Deleted")
                            .Select(fun e -> e.EventArgs)
                            .Subscribe(OnGraceUpdateInProgressDeleted)

                    // Load the Grace Index file.
                    let! status = readGraceStatusFile ()
                    graceStatus <- status

                    // Create the inter-process communication file.
                    do! updateGraceWatchInterprocessFile graceStatus

                    // Enable the FileSystemWatcher.
                    rootDirectoryFileSystemWatcher.EnableRaisingEvents <- true
                    updateInProgressFileSystemWatcher.EnableRaisingEvents <- true

                    let timerTimeSpan = TimeSpan.FromSeconds(1.0)

                    logToAnsiConsole Colors.Verbose $"The change processor timer will tick every {timerTimeSpan.TotalSeconds:F1} seconds."

                    // Open a SignalR connection to the server.
                    let signalRUrl = Uri($"{Current().ServerUri}/notifications")
                    logToConsole $"signalRUrl: {signalRUrl}."

                    use signalRConnection = HubConnectionBuilder().WithAutomaticReconnect().WithUrl(signalRUrl, HttpTransportType.ServerSentEvents).Build()

                    use notifyRepository =
                        signalRConnection.On<RepositoryId, ReferenceId>(
                            "NotifyRepository",
                            fun repositoryId referenceId ->
                                (task { logToAnsiConsole Colors.Highlighted $"ReferenceId {referenceId} was created in repository {repositoryId}." }) :> Task
                        )

                    use serverToClient =
                        signalRConnection.On<string>(
                            "ServerToClientMessage",
                            (fun message -> logToAnsiConsole Colors.Important $"From Grace Server: {message}")
                        )

                    use notifyOnPromotion =
                        signalRConnection.On<BranchId, BranchName, ReferenceId>(
                            "NotifyOnPromotion",
                            fun parentBranchId parentBranchName referenceId ->
                                (task {
                                    logToAnsiConsole Colors.Highlighted $"Parent branch {parentBranchName} has a new promotion; referenceId: {referenceId}."

                                    let! graceStatus = readGraceStatusFile ()

                                    let rebaseParameters =
                                        RebaseParameters(
                                            OwnerId = $"{Current().OwnerId}",
                                            OrganizationId = $"{Current().OrganizationId}",
                                            RepositoryId = $"{Current().RepositoryId}",
                                            BranchId = $"{Current().BranchId}",
                                            BasedOn = referenceId,
                                            CorrelationId = (parseResult |> getCorrelationId)
                                        )

                                    let! x = Branch.rebaseHandler parseResult graceStatus
                                    ()
                                })
                                :> Task
                        )

                    use notifyOnSave =
                        signalRConnection.On<BranchName, BranchName, BranchId, ReferenceId>(
                            "NotifyOnSave",
                            fun branchName parentBranchName parentBranchId referenceId ->
                                (task {
                                    logToAnsiConsole
                                        Colors.Highlighted
                                        $"Branch {branchName} with parent branch {parentBranchName} has a new save; referenceId: {referenceId}."
                                })
                                :> Task
                        )

                    use notifyOnCheckpoint =
                        signalRConnection.On<BranchName, BranchName, BranchId, ReferenceId>(
                            "NotifyOnCheckpoint",
                            fun branchName parentBranchName parentBranchId referenceId ->
                                (task {
                                    logToAnsiConsole
                                        Colors.Highlighted
                                        $"Branch {branchName} with parent branch {parentBranchName} has a new checkpoint; referenceId: {referenceId}."
                                })
                                :> Task
                        )

                    use notifyOnCommit =
                        signalRConnection.On<BranchName, BranchName, BranchId, ReferenceId>(
                            "NotifyOnCommit",
                            fun branchName parentBranchName parentBranchId referenceId ->
                                (task {
                                    logToAnsiConsole
                                        Colors.Highlighted
                                        $"Branch {branchName} with parent branch {parentBranchName} has a new commit; referenceId: {referenceId}."
                                })
                                :> Task
                        )

                    signalRConnection.add_Closed (fun ex -> task { logToAnsiConsole Colors.Error $"SignalR connection closed: {ex.Message}." })

                    signalRConnection.add_Reconnecting (fun ex -> task { logToAnsiConsole Colors.Important $"SignalR connection reconnecting: {ex.Message}." })

                    signalRConnection.add_Reconnected (fun connectionId ->
                        task { logToAnsiConsole Colors.Important $"SignalR connection reconnected: {connectionId}." })

                    do! signalRConnection.StartAsync(cancellationToken)

                    // Get the parent BranchId so we can tell SignalR what to notify us about.
                    let branchGetParameters =
                        GetBranchParameters(
                            OwnerId = $"{Current().OwnerId}",
                            OrganizationId = $"{Current().OrganizationId}",
                            RepositoryId = $"{Current().RepositoryId}",
                            BranchId = $"{Current().BranchId}"
                        )

                    match! Branch.GetParentBranch branchGetParameters with
                    | Ok returnValue ->
                        let parentBranchDto = returnValue.ReturnValue

                        do! signalRConnection.InvokeAsync("RegisterParentBranch", Current().BranchId, parentBranchDto.BranchId, cancellationToken)

                        logToAnsiConsole
                            Colors.Highlighted
                            $"Connected to SignalR Hub. Listening for changes in parent branch {parentBranchDto.BranchName} ({parentBranchDto.BranchId}); connectionId: {signalRConnection.ConnectionId}."
                    | Error error ->
                        logToAnsiConsole Colors.Error $"Failed to retrieve branch metadata. Cannot connect to SignalR Hub."

                        logToAnsiConsole Colors.Error $"{Markup.Escape(error.ToString())}"

                    // Check for changes that occurred while not running.
                    logToAnsiConsole Colors.Verbose $"Scanning for differences."
                    let! differences = scanForDifferences graceStatus // <--- This always finds the directories with updated write times, but we never update GraceStatus below..

                    if differences |> Seq.isEmpty then
                        logToAnsiConsole Colors.Verbose $"Already up-to-date."
                    else
                        logToAnsiConsole Colors.Verbose $"Found {differences.Count} differences."

                    for difference in differences do
                        match difference.FileSystemEntryType with
                        | Directory -> directoriesToProcess.TryAdd(difference.RelativePath, ()) |> ignore
                        | File -> filesToProcess.TryAdd(difference.RelativePath, ()) |> ignore

                    // Process any changes that occurred while not running.
                    graceStatus <- GraceStatus.Default
                    do! processChangedFiles ()

                    // Create a timer to process the file changes detected by the FileSystemWatcher.
                    // This timer is the reason that there's a delay in stopping `grace watch`.
                    logToAnsiConsole Colors.Verbose $"Starting timer."
                    use periodicTimer = new PeriodicTimer(timerTimeSpan)
                    let! tick = periodicTimer.WaitForNextTickAsync()
                    let mutable previousGC = getCurrentInstant ()
                    let mutable ticked = true

                    while ticked && not (cancellationToken.IsCancellationRequested) do
                        // Grace Status may have changed from branch switch, or other commands.
                        if graceStatusHasChanged then
                            let! updatedGraceStatus = readGraceStatusFile ()
                            graceStatus <- updatedGraceStatus
                            do! updateGraceWatchInterprocessFile graceStatus
                            //logToAnsiConsole Colors.Important $"Setting graceStatusHasChanged to false in OnWatch(). Current value: {graceStatusHasChanged}."
                            graceStatusHasChanged <- false

                        do! processChangedFiles ()
                        let! tick = periodicTimer.WaitForNextTickAsync()
                        ticked <- tick

                        // About once a minute, do a full GC to be kind with our memory usage. This is for looks, not for function.
                        //
                        // In .NET, when a computer has lots of available memory, and there's no memory pressure signal from the OS, GC doesn't happen much, if at all.
                        //   With no memory pressure, `grace watch` wouldn't bother releasing its unused heap after handling events like saves and auto-rebases.
                        //   Seeing that kind of memory usage could lead to uninformed people saying things like, "OMG, `grace watch` takes up so much memory!"
                        //   Actually, `grace watch` only grabs a lot of memory at the moment of processing events. As soon as we're done, we want to release that
                        //   memory back to the OS, that means forcing a full GC.
                        //
                        // Because of DATAS (see https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/datas), it may take more than one GC.Collect()
                        //   call to fully compact the heap (and that's OK). If we weren't being so aggressive about memory usage, we would just let DATAS compute
                        //   a close-to-optimal heap size on its own over time.
                        if previousGC < getCurrentInstant().Minus(Duration.FromMinutes(1.0)) then
                            //let memoryBeforeGC = Process.GetCurrentProcess().WorkingSet64
                            GC.Collect(2, GCCollectionMode.Forced, blocking = true, compacting = true)
                            //logToAnsiConsole Colors.Verbose $"Memory before GC: {memoryBeforeGC:N0}; after: {Process.GetCurrentProcess().WorkingSet64:N0}."
                            previousGC <- getCurrentInstant ()

                    return 0
                with ex ->
                    //let exceptionMarkup = Markup.Escape($"{ExceptionResponse.Create ex}").Replace("\\\\", @"\").Replace("\r\n", Environment.NewLine)
                    //logToAnsiConsole Colors.Error $"{exceptionMarkup}"
                    let exceptionSettings = ExceptionSettings()
                    // Need to fill in some exception styles here.
                    exceptionSettings.Format <- ExceptionFormats.Default
                    AnsiConsole.WriteException(ex, exceptionSettings)
                    return -1
            }

    let Build =
        // Create main command and aliases, if any.
        let watchCommand = new Command("watch", Description = "Watches your repo for changes, and uploads new versions of your files.")

        watchCommand.Aliases.Add("w")
        watchCommand.Action <- Watch()
        watchCommand
