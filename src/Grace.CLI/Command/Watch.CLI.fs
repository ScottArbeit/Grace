namespace Grace.Cli.Command

open Grace.Cli.Common
open Grace.Cli.Services
open Grace.SDK
open Grace.SDK.Common
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Branch
open Grace.Shared.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http.Connections
open Microsoft.AspNetCore.SignalR.Client
open NodaTime
open Spectre.Console
open Spectre.Console.Cli
open System
open System.Buffers
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.NamingConventionBinder
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
open Branch


module Watch =

    /// Holds a list of the created or changed files that we need to process, as determined by the FileSystemWatcher.
    ///
    /// NOTE: We're using ConcurrentDictionary because it's safe for multithreading, and it doesn't allow us to insert the same key twice. The value is meaningless.
    let private filesToProcess = ConcurrentDictionary<string, string>()

    type WatchParameters() = 
        inherit ParameterBase()
        member val public RepositoryPath: string = String.Empty with get, set
        member val public NamedSections: string[] = Array.empty with get, set

    let mutable private graceStatus = GraceStatus.Default
    let mutable graceStatusMemoryStream: MemoryStream = null
    let mutable graceStatusHasChanged = false
    let private rnd = Random()
    
    /// Generates a temporary file name within the ObjectDirectory, and returns the full file path.
    /// This file name will be used to copy modified files into before renaming them with their proper names and SHA256 values.
    let getTemporaryFilePath =
        Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("TEMP"), $"{Path.GetRandomFileName()}.gracetmp"))

    let copyToObjectDirectory (filePath: FilePath) : Task<FileVersion option> =
        task {
            try
                if File.Exists($"{filePath}") then
                    let filePath = $"{filePath}"
                    // First, capture the file by copying it to a temp name
                    let tempFilePath = getTemporaryFilePath
                    //logToConsole $"filePath: {filePath}; tempFilePath: {tempFilePath}"
                    let mutable iteration = 0
                    Constants.DefaultFileCopyRetryPolicy.Execute(fun () -> 
                        //iteration <- iteration + 1
                        //logToAnsiConsole Colors.Deemphasized $"Attempt #{iteration} to copy file to object directory..."
                        File.Copy(sourceFileName = filePath, destFileName = tempFilePath, overwrite = true))

                    // Now that we've copied it, compute the SHA-256 hash.
                    let relativeFilePath = Path.GetRelativePath(Current().RootDirectory, filePath)
                    use fileStream = File.Open(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read)
                    let! sha256Hash = computeSha256ForFile fileStream relativeFilePath
                    //logToConsole $"filePath: {filePath}; tempFilePath: {tempFilePath}; SHA256: {sha256Hash}"
                    fileStream.Dispose()

                    // Get the new name for this version of the file, including the SHA-256 hash.
                    let originalFile = FileInfo(filePath)
                    let relativeDirectoryPath = getLocalRelativeDirectory filePath (Current().RootDirectory)
                    // We're putting the SHA-256 hash just before the file extension; this allows us to open these versions using default file associations.
                    // FileInfo.Extension includes the '.'.
                    let objectFileName = $"{originalFile.Name.Replace(originalFile.Extension, String.Empty)}_{sha256Hash}{originalFile.Extension}"
                    let objectDirectoryPath = Path.Combine(Current().ObjectDirectory, relativeDirectoryPath)
                    let objectFilePath = Path.Combine(objectDirectoryPath, objectFileName)
                    //logToConsole $"relativeDirectoryPath: {relativeDirectoryPath}; objectFileName: {objectFileName}; objectFilePath: {objectFilePath}"

                    // If we don't already have this file, with this exact SHA256, make sure the directory exists, 
                    //   and rename the temp file to the proper SHA256-enhanced name of the file.
                    if not (File.Exists(objectFilePath)) then
                        //logToConsole $"Before moving temp file to object storage..."
                        Directory.CreateDirectory(objectDirectoryPath) |> ignore    // No-op if the directory already exists
                        File.Move(tempFilePath, objectFilePath)
                        //logToConsole $"After moving temp file to object storage..."
                        let objectFilePathInfo = FileInfo(objectFilePath)
                        //logToConsole $"After creating FileInfo; Exists: {objectFilePathInfo.Exists}; FullName = {objectFilePathInfo.FullName}..."
                        use objectFileStream = objectFilePathInfo.Open(FileMode.Open, FileAccess.Read)
                        //logToConsole $"After creating stream; .Length = {objectFileStream.Length}..."
                        let! isBinary = isBinaryFile objectFileStream
                        //logToConsole $"Finished copyToObjectDirectory for {filePath}; isBinary: {isBinary}; moved temp file to object directory."
                        let relativePath = Path.GetRelativePath(Current().RootDirectory, filePath)
                        return Some (FileVersion.Create (Current().RepositoryId) (RelativePath relativePath) (Sha256Hash $"{sha256Hash}") ("") isBinary (uint64 (objectFilePathInfo.Length)))
                    else
                    //   If we do already have this exact version of the file, just delete the temp file.
                        File.Delete(tempFilePath)
                        //logToConsole $"Finished copyToObjectDirectory for {filePath}; deleted temp file."
                        return None
                    //return result
                else
                    return None
            with ex ->
                logToAnsiConsole Colors.Error $"Exception: {ex.Message}"
                logToAnsiConsole Colors.Error $"Stack trace: {ex.StackTrace}"
                return None
        }

    let fileDeleted filePath =
        logToConsole $"In Delete: filePath: {filePath}"
      
    let updateInProgress() = 
        File.Exists(getUpdateInProgressFileName())
    let updateNotInProgress() = not <| updateInProgress()

    let OnCreated (args: FileSystemEventArgs) =
        // Ignore directory creation; need to think about this more... should we capture new empty directories?
        if updateNotInProgress() && not <| Directory.Exists(args.FullPath) then
            let shouldIgnore = shouldIgnoreFile args.FullPath
            //logToConsole $"Should ignore {args.FullPath}: {shouldIgnore}."

            if not <| shouldIgnore then
                logToAnsiConsole Colors.Added $"I saw that {args.FullPath} was created."
                filesToProcess.TryAdd(args.FullPath, String.Empty) |> ignore

    let OnChanged (args: FileSystemEventArgs) =
        if updateNotInProgress() && not <| Directory.Exists(args.FullPath) then            
            let shouldIgnore = shouldIgnoreFile args.FullPath
            //logToConsole $"Should ignore {args.FullPath}: {shouldIgnore}."

            if not <| shouldIgnore then
                logToAnsiConsole Colors.Changed $"I saw that {args.FullPath} changed."
                filesToProcess.TryAdd(args.FullPath, String.Empty) |> ignore

            if not <| graceStatusHasChanged && args.FullPath = Current().GraceStatusFile then
                logToAnsiConsole Colors.Highlighted $"Grace Status file has been updated."
                graceStatusHasChanged <- true

    let OnDeleted (args: FileSystemEventArgs) =
        if updateNotInProgress() && not <| Directory.Exists(args.FullPath) then
            let shouldIgnore = shouldIgnoreFile args.FullPath
            //logToConsole $"Should ignore {args.FullPath}: {shouldIgnore}."

            if not <| shouldIgnore then
                logToAnsiConsole Colors.Deleted $"I saw that {args.FullPath} was deleted."
                logToAnsiConsole Colors.Deleted $"Delete processing is not yet implemented."

    let OnRenamed (args: RenamedEventArgs) =
        if updateNotInProgress() then
            let shouldIgnoreOldFile = shouldIgnoreFile args.OldFullPath
            let shouldIgnoreNewFile = shouldIgnoreFile args.FullPath

            if not <| shouldIgnoreOldFile then
                logToAnsiConsole Colors.Changed $"I saw that {args.OldFullPath} was renamed to {args.FullPath}."
                //logToConsole $"Should ignore {args.OldFullPath}: {shouldIgnoreOldFile}. Should ignore {args.FullPath}: {shouldIgnoreNewFile}."
                logToAnsiConsole Colors.Changed $"Delete processing is not yet implemented."
                
            if not <| shouldIgnoreNewFile then
                logToAnsiConsole Colors.Changed $"I saw that {args.OldFullPath} was renamed to {args.FullPath}."
                //logToConsole $"Should ignore {args.OldFullPath}: {shouldIgnoreOldFile}. Should ignore {args.FullPath}: {shouldIgnoreNewFile}."
                filesToProcess.TryAdd(args.FullPath, String.Empty) |> ignore

    let OnError (args: ErrorEventArgs) =
        let correlationId = Guid.NewGuid().ToString()
        logToAnsiConsole Colors.Error $"I saw that the FileSystemWatcher threw an exception: {args.GetException().Message}. grace watch should be restarted."

    let createFileSystemWatcher rootDirectory =
        let fileSystemWatcher = new FileSystemWatcher(rootDirectory)
        fileSystemWatcher.InternalBufferSize <- (64 * 1024)   // Default is 4K, choosing maximum of 64K for safety.
        fileSystemWatcher.IncludeSubdirectories <- true
        fileSystemWatcher.NotifyFilter <- NotifyFilters.DirectoryName ||| NotifyFilters.FileName ||| NotifyFilters.LastWrite ||| NotifyFilters.Security
        fileSystemWatcher

    let printDifferences (differences: List<FileSystemDifference>) =
        if differences.Count > 0 then logToAnsiConsole Colors.Verbose $"Differences detected since last save/checkpoint/commit:"
        for difference in differences.OrderBy(fun diff -> diff.RelativePath) do
            logToAnsiConsole Colors.Verbose $"{discriminatedUnionCaseNameToString difference.DifferenceType} {discriminatedUnionCaseNameToString difference.FileSystemEntryType} {difference.RelativePath}"

    let updateObjectCacheFile (newDirectoryVersions: List<LocalDirectoryVersion>) =
        task {
            let! objectCache = readGraceObjectCacheFile()
            for directoryVersion in newDirectoryVersions.OrderByDescending(fun dv -> countSegments dv.RelativePath) do
                objectCache.Index.AddOrUpdate(directoryVersion.DirectoryId, (fun _ -> directoryVersion), (fun _ _ -> directoryVersion)) |> ignore
            do! writeGraceObjectCacheFile objectCache
        }

    let updateGraceStatus graceStatus correlationId =
        task {
            // Get the list of differences between what's in the working directory, and what Grace Index knows about.
            let! differences = scanForDifferences graceStatus
            printDifferences differences
        
            // Get an updated Grace Index, and any new DirectoryVersions that were needed to build it.
            let! (newGraceStatus, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions graceStatus differences

            // Log the changes.
            for dv in newDirectoryVersions do
                logToAnsiConsole Colors.Verbose $"new Sha256Hash: {dv.Sha256Hash.Substring(0, 8)}; DirectoryId: {dv.DirectoryId.ToString().Substring(0, 9)}...; RelativePath: {dv.RelativePath}"

            // Upload the new directory versions.
            let! result = uploadDirectoryVersions newDirectoryVersions correlationId
            match result with
            | Ok returnValue ->
                do! updateObjectCacheFile newDirectoryVersions
                let fileDifferences = differences.Where(fun diff -> diff.FileSystemEntryType = FileSystemEntryType.File).ToList()
                let message =
                    if fileDifferences.Count = 0 then
                        String.Empty
                    else 
                        let sb = StringBuilder()
                        for fileDifference in fileDifferences do
                            //sb.AppendLine($"{(discriminatedUnionCaseNameToString fileDifference.DifferenceType)}: {fileDifference.RelativePath}") |> ignore
                            match fileDifference.DifferenceType with
                            | Change -> sb.AppendLine($"{fileDifference.RelativePath}") |> ignore
                            | Add -> sb.AppendLine($"Add {fileDifference.RelativePath}") |> ignore
                            | Delete -> sb.AppendLine($"Delete {fileDifference.RelativePath}") |> ignore
                        let saveMessage = sb.ToString()
                        saveMessage.Remove(saveMessage.LastIndexOf(Environment.NewLine), Environment.NewLine.Length)
                if fileDifferences.Count > 0 then
                    let! saveReferenceResult = createSaveReference (getRootDirectoryVersion newGraceStatus) message correlationId
                    match saveReferenceResult with
                    | Ok returnValue ->
                        let newGraceStatusWithUpdatedTime = {newGraceStatus with LastSuccessfulDirectoryVersionUpload = getCurrentInstant()}
                        // Write the new Grace Status file to disk.
                        do! writeGraceStatusFile newGraceStatusWithUpdatedTime
                        return Some newGraceStatusWithUpdatedTime
                    | Error error -> 
                        logToAnsiConsole Colors.Error $"{Markup.Escape(error.Error)}"
                        return None
                else
                    return None
            | Error error ->
                logToAnsiConsole Colors.Error $"{Markup.Escape(error.Error)}"
                return None
        }

    let fileChanged fullPath correlationId = 
        task {
            //logToConsole $"*In fileChanged for {fullPath}."
            let! fileVersionOption = copyToObjectDirectory fullPath
            match fileVersionOption with
                | Some fileVersion ->
                    let! result = uploadToServerAsync fileVersion correlationId
                    match result with
                        | Ok correlationId -> logToAnsiConsole Colors.Verbose $"File {fileVersion.GetObjectFileName} has been uploaded to storage."
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
            do! gzStream.DisposeAsync()
            do! graceStatusMemoryStream.DisposeAsync()
            graceStatusMemoryStream <- null
        }

    /// Compresses the GraceStatus information into a gzipped memory stream.
    let storeGraceStatusInMemoryStream () =
        task {
            logToAnsiConsole Colors.Verbose $"Storing Grace Status in compressed memory stream."
            graceStatusMemoryStream <- new MemoryStream()
            use gzStream = new GZipStream(graceStatusMemoryStream, CompressionLevel.SmallestSize, leaveOpen = true)
            do! JsonSerializer.SerializeAsync(gzStream, graceStatus, Constants.JsonSerializerOptions)
            do! gzStream.FlushAsync()
            do! graceStatusMemoryStream.FlushAsync()
            logToAnsiConsole Colors.Verbose $"Stored Grace Status in compressed memory stream."
            do! gzStream.DisposeAsync()
            graceStatus <- GraceStatus.Default
        }

    /// Processes any changed files since the last timer tick.
    let processChangedFiles() =
        task {
            // First, check if there's anything to process.
            if not <| filesToProcess.IsEmpty then
                let correlationId = Guid.NewGuid().ToString()
                let! graceStatusFromDisk = readGraceStatusFile()
                graceStatus <- graceStatusFromDisk
                
                let mutable lastFileUploadInstant = graceStatus.LastSuccessfulFileUpload
                let mutable meaninglessValue = String.Empty

                // Loop through no more than 50 files. In the incredibly rare event that more than 50 files have changed,
                //   we'll get 50-per-timer-tick, and clear the queue quickly without overwhelming the system.
                for fileName in filesToProcess.Keys.Take(50) do
                    if filesToProcess.TryRemove(fileName, &meaninglessValue) then   
                        logToAnsiConsole Colors.Verbose $"Processing {fileName}. filesToProcess.Count: {filesToProcess.Count}."
                        do! fileChanged (FilePath fileName) correlationId
                        lastFileUploadInstant <- getCurrentInstant()
                
                graceStatus <- {graceStatus with LastSuccessfulFileUpload = lastFileUploadInstant}

                // If we've drained all of the files that changed (and we'll almost always have done so), update all the things:
                //   GraceStatus, directory versions, etc.
                if filesToProcess.IsEmpty then
                    match! (updateGraceStatus graceStatus correlationId) with
                    | Some newGraceStatus -> 
                        graceStatus <- newGraceStatus
                    | None -> ()    // Something went wrong, don't update the in-memory Grace Status.

                graceStatusHasChanged <- false  // We *just* changed it ourselves, so we don't have to re-process it in the timer loop.
                do! updateGraceWatchStatus graceStatus
                graceStatus <- GraceStatus.Default
                
            // Refresh the file every (just under) 5 minutes to indicate that `grace watch` is still alive.
            elif graceWatchStatusUpdateTime < getCurrentInstant().Minus(Duration.FromMinutes(4.8)) then
                let! graceStatusFromDisk = readGraceStatusFile()
                graceStatus <- graceStatusFromDisk
                do! updateGraceWatchStatus graceStatus
                graceStatus <- GraceStatus.Default
        }

    let OnWatch =
        CommandHandler.Create(fun (parseResult: ParseResult) (cancellationToken: CancellationToken) (parameters: WatchParameters) ->
            task {
                try
                    // Create the FileSystemWatcher, but don't enable it yet.
                    use fileSystemWatcher = createFileSystemWatcher (Current().RootDirectory)
                    use created = Observable.FromEventPattern<FileSystemEventArgs>(fileSystemWatcher, "Created").Select(fun e -> e.EventArgs).Subscribe(OnCreated)
                    use changed = Observable.FromEventPattern<FileSystemEventArgs>(fileSystemWatcher, "Changed").Select(fun e -> e.EventArgs).Subscribe(OnChanged)
                    use deleted = Observable.FromEventPattern<FileSystemEventArgs>(fileSystemWatcher, "Deleted").Select(fun e -> e.EventArgs).Subscribe(OnDeleted)
                    use renamed = Observable.FromEventPattern<RenamedEventArgs>(fileSystemWatcher, "Renamed")   .Select(fun e -> e.EventArgs).Subscribe(OnRenamed)
                    use errored = Observable.FromEventPattern<ErrorEventArgs>(fileSystemWatcher, "Error")       .Select(fun e -> e.EventArgs).Subscribe(OnError)    // I want all of the errors.
                
                    // Load the Grace Index file.
                    let! status = readGraceStatusFile()
                    graceStatus <- status

                    // Create the inter-process communication file.
                    do! updateGraceWatchStatus graceStatus

                    // Enable the FileSystemWatcher.
                    fileSystemWatcher.EnableRaisingEvents <- true

                    let timerTimeSpan = TimeSpan.FromSeconds(3.0)
                    logToAnsiConsole Colors.Verbose $"The change processor timer will tick every {timerTimeSpan.TotalSeconds:F1} seconds."

                    // Open a SignalR connection to the server.
                    let signalRUrl = Uri($"{Current().ServerUri}/notification")
                    use signalRConnection = HubConnectionBuilder()
                                                .WithAutomaticReconnect()
                                                .WithUrl(signalRUrl, HttpTransportType.ServerSentEvents)
                                                .Build()

                    use serverToClient = signalRConnection.On<string>("ServerToClientMessage", fun message -> logToAnsiConsole Colors.Important $"From Grace Server: {message}")
                    use notifyOnMerge = signalRConnection.On<BranchId, ReferenceId>("NotifyOnMerge", fun (parentBranchId: BranchId) (referenceId: ReferenceId) ->
                        (task {
                            logToAnsiConsole Colors.Highlighted $"Parent branch {parentBranchId} has a new merge; referenceId: {referenceId}."
                            let! graceStatus = readGraceStatusFile()
                            let rebaseParameters = RebaseParameters(OwnerId = $"{Current().OwnerId}", OrganizationId = $"{Current().OrganizationId}",
                                RepositoryId = $"{Current().RepositoryId}", BranchId = $"{Current().BranchId}", BasedOn = referenceId, CorrelationId = (parseResult |> getCorrelationId))
                            let! x = Branch.rebaseHandler parseResult rebaseParameters graceStatus
                            ()
                        }) :> Task)
                    use notifyOnSave = signalRConnection.On<BranchId, ReferenceId>("NotifyOnSave", fun parentBranchId referenceId ->
                        (task {
                            logToAnsiConsole Colors.Highlighted $"A branch with parent branch {parentBranchId} has a new save; referenceId: {referenceId}."
                        }) :> Task)
                    use notifyOnCheckpoint = signalRConnection.On<BranchId, ReferenceId>("NotifyOnCheckpoint", fun parentBranchId referenceId ->
                        (task {
                            logToAnsiConsole Colors.Highlighted $"A branch with parent branch {parentBranchId} has a new checkpoint; referenceId: {referenceId}."
                        }) :> Task)
                    use notifyOnCommit = signalRConnection.On<BranchId, ReferenceId>("NotifyOnCommit", fun parentBranchId referenceId ->
                        (task {
                            logToAnsiConsole Colors.Highlighted $"A branch with parent branch {parentBranchId} has a new commit; reference: {referenceId}."
                        }) :> Task)
                    do! signalRConnection.StartAsync(cancellationToken)
                    
                    // Get the parent BranchId so we can tell SignalR what to notify us about.
                    let branchGetParameters = GetParameters(OwnerId = $"{Current().OwnerId}", OrganizationId = $"{Current().OrganizationId}",
                                            RepositoryId = $"{Current().RepositoryId}", BranchId = $"{Current().BranchId}")
                    match! Branch.GetParentBranch branchGetParameters with
                    | Ok returnValue ->
                        let parentBranchDto = returnValue.ReturnValue
                        do! signalRConnection.InvokeAsync("RegisterParentBranch", Current().BranchId, parentBranchDto.BranchId, cancellationToken)
                        logToAnsiConsole Colors.Highlighted $"Connected to SignalR Hub. Listening for changes in parent branch {parentBranchDto.BranchName} ({parentBranchDto.BranchId}); connectionId: {signalRConnection.ConnectionId}."
                    | Error error ->
                        logToAnsiConsole Colors.Error $"Failed to retrieve branch metadata. Cannot connect to SignalR Hub."
                        logToAnsiConsole Colors.Error $"{Markup.Escape(error.ToString())}"

                    // Check for changes that occurred while not running.
                    logToAnsiConsole Colors.Verbose $"Scanning for differences."
                    let! differences = scanForDifferences graceStatus
                    for difference in differences do
                        match difference.FileSystemEntryType with
                        | Directory -> ()
                        | File -> filesToProcess.TryAdd(difference.RelativePath, String.Empty) |> ignore

                    // Process any changes that occurred while not running.
                    graceStatus <- GraceStatus.Default
                    do! processChangedFiles()

                    // Create a timer to process the file changes detected by the FileSystemWatcher.
                    // This timer is the reason that there's a delay in stopping `grace watch`.
                    logToAnsiConsole Colors.Verbose $"Starting timer."
                    use periodicTimer = new PeriodicTimer(timerTimeSpan)
                    let! tick = periodicTimer.WaitForNextTickAsync()
                    let mutable previousGC = getCurrentInstant()
                    let mutable ticked = true

                    while ticked && not (cancellationToken.IsCancellationRequested) do
                        // Grace Status may have changed from branch switch, or other commands.
                        if graceStatusHasChanged then
                            let! updatedGraceStatus = readGraceStatusFile()
                            graceStatus <- updatedGraceStatus
                            do! updateGraceWatchStatus graceStatus
                            graceStatusHasChanged <- false
                        do! processChangedFiles()
                        let! tick = periodicTimer.WaitForNextTickAsync()
                        ticked <- tick

                        // About once a minute, do a full GC to be kind with our memory usage.
                        if previousGC < getCurrentInstant().Minus(Duration.FromMinutes(1.0)) then
                            let memoryBeforeGC = Process.GetCurrentProcess().WorkingSet64
                            GC.Collect(2, GCCollectionMode.Forced, blocking = true, compacting = true)
                            //logToAnsiConsole Colors.Verbose $"Memory before GC: {memoryBeforeGC:N0}; after: {Process.GetCurrentProcess().WorkingSet64:N0}."
                            previousGC <- getCurrentInstant()
                with ex ->
                    //let exceptionMarkup = Markup.Escape($"{createExceptionResponse ex}").Replace("\\\\", @"\").Replace("\r\n", Environment.NewLine)
                    //logToAnsiConsole Colors.Error $"{exceptionMarkup}"
                    let exceptionSettings = ExceptionSettings()
                    // Need to fill in some styles here.
                    AnsiConsole.WriteException(ex, exceptionSettings)
            } :> Task
        )

    let Build =
        // Create main command and aliases, if any.
        let watchCommand = new Command("watch", Description = "Watches your repo for changes, and uploads new versions of your files.")
        watchCommand.AddAlias("w")
        watchCommand.Handler <- OnWatch
        watchCommand
