namespace Grace.CLI

open Microsoft.Extensions
open FSharp.Collections
open Grace.SDK
open Grace.Shared.Client.Configuration
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Parameters.Directory
open Grace.Shared.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Storage
open NodaTime
open NodaTime.Text
open Spectre.Console
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Parsing
open System.Diagnostics
open System.Globalization
open System.IO
open System.IO.Compression
open System.IO.Enumeration
open System.IO.Pipelines
open System.Linq
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open System.Reactive.Linq
open MessagePack
open System.Threading
open Grace.Shared.Parameters
open Grace.Shared.Parameters.Storage

module Services =

    /// Utility method to write to the console using color.
    let logToAnsiConsole (color: string) message =
        AnsiConsole.MarkupLine $"[{color}]{getCurrentInstantExtended ()} {Environment.CurrentManagedThreadId:X2} {Markup.Escape(message)}[/]"

    /// A cache of paths that we've already decided to ignore or not.
    let private shouldIgnoreCache = ConcurrentDictionary<FilePath, bool>()

    // This section is "borrowed" from Common.CLI.fs, because Services.CLI.fs comes before Common.CLI.fs in the build order.
    let private isOutputFormat (outputFormat: string) (parseResult: ParseResult) =
        if
            parseResult
                .ToString()
                .IndexOf($"<{outputFormat}>", StringComparison.InvariantCultureIgnoreCase) > 0
        then
            true
        else if outputFormat = "Normal" then
            true
        else
            false

    /// GraceWatchStatus defines the schema for the inter-process communication (IPC) file that lets Grace know if `grace watch` is already running.
    ///
    /// It's written by `grace watch`. It holds everything required to allow other instances of Grace to skip checking current status.
    [<Struct>]
    type GraceWatchStatus =
        { UpdatedAt: Instant
          RootDirectoryId: DirectoryVersionId
          RootDirectorySha256Hash: Sha256Hash
          LastFileUploadInstant: Instant
          LastDirectoryVersionInstant: Instant
          DirectoryIds: HashSet<DirectoryVersionId> }

        static member Default =
            { UpdatedAt = Instant.MinValue
              RootDirectoryId = Guid.Empty
              RootDirectorySha256Hash = Sha256Hash String.Empty
              LastFileUploadInstant = Instant.MinValue
              LastDirectoryVersionInstant = Instant.MinValue
              DirectoryIds = HashSet<DirectoryVersionId>() }

    let mutable graceWatchStatusUpdateTime = Instant.MinValue

    // Extension methods for dealing with local file changes.
    type DirectoryVersion with
        /// Gets the full path for this file in the working directory.
        member this.FullName = Path.Combine(Current().RootDirectory, $"{this.RelativePath}")

    // Extension methods for dealing with local files.
    type LocalDirectoryVersion with
        /// Gets the full path for this file in the working directory.
        member this.FullName = Path.Combine(Current().RootDirectory, $"{this.RelativePath}")
        /// Gets a DirectoryInfo instance for the parent directory of this local file.
        member this.DirectoryInfo = DirectoryInfo(this.FullName)

    // Extension methods for dealing with local files.
    type LocalFileVersion with
        /// Gets the full path for this file in the working directory.
        member this.FullName = getNativeFilePath (Path.Combine(Current().RootDirectory, $"{this.RelativePath}"))

        /// Gets the full working directory path for this file.
        member this.FullRelativePath = FileInfo(this.FullName).DirectoryName
        /// Gets a FileInfo instance for this local file.
        member this.FileInfo = FileInfo(this.FullName)

        /// Gets the RelativeDirectory for this file.
        member this.RelativeDirectory = Path.GetRelativePath(Current().RootDirectory, this.FileInfo.DirectoryName)

        /// Gets the full name of the object file for this LocalFileVersion.
        member this.FullObjectPath = getNativeFilePath (Path.Combine(Current().ObjectDirectory, this.RelativePath, this.GetObjectFileName))

    /// Flag to determine if we should do case-insensitive file name processing on the current platform.
    let ignoreCase = runningOnWindows

    /// Returns true if fileToCheck matches this graceIgnoreEntry; otherwise returns false.
    let checkIgnoreLineAgainstFile (fileToCheck: FilePath) (graceIgnoreEntry: string) =
        let fileName = Path.GetFileName(fileToCheck)

        let ignoreEntryMatches = FileSystemName.MatchesSimpleExpression(graceIgnoreEntry, fileName, ignoreCase)

        ignoreEntryMatches

    /// Returns true if directory matches this graceIgnoreEntry; otherwise returns false.
    let checkIgnoreLineAgainstDirectory (directoryInfoToCheck: DirectoryInfo) (graceIgnoreEntry: string) =
        let expression = $"{graceIgnoreEntry}"

        let normalizedDirectoryPath =
            if Path.EndsInDirectorySeparator(directoryInfoToCheck.FullName) then
                normalizeFilePath directoryInfoToCheck.FullName
            else
                normalizeFilePath (directoryInfoToCheck.FullName + "/")

        if FileSystemName.MatchesSimpleExpression(expression, normalizedDirectoryPath, ignoreCase) then
            //if normalizedDirectoryPath.Contains(".git") then
            //AnsiConsole.MarkupLine(
            //    $"{getCurrentInstantExtended ()} [{Colors.Deleted}]In checkIgnoreLineAgainstDirectory(): normalizedDirectoryPath: {normalizedDirectoryPath}; expression: {expression}; matches: true. Should ignore.[/]"
            //)

            true
        else
            //if normalizedDirectoryPath.Contains(".git") then
            //AnsiConsole.MarkupLine(
            //    $"{getCurrentInstantExtended ()} [{Colors.Added}]In checkIgnoreLineAgainstDirectory(): normalizedDirectoryPath: {normalizedDirectoryPath}; expression: {expression}; matches: false. Should not ignore.[/]"
            //)

            false

    /// Returns true if filePath should be ignored by Grace, otherwise returns false.
    let shouldIgnoreFile (filePath: FilePath) =
        let mutable shouldIgnore = false
        let wasAlreadyCached = shouldIgnoreCache.TryGetValue(filePath, &shouldIgnore)
        //logToConsole $"In shouldIgnoreFile: filePath: {filePath}; wasAlreadyCached: {wasAlreadyCached}; shouldIgnore: {shouldIgnore}"
        if wasAlreadyCached then
            shouldIgnore
        else
            // Ignore it if:
            //   it's in the .grace directory, or
            //   it's the Grace Status file, or
            //   it's a Grace-owned temporary file, or
            //   it's a directory itself, or
            //   it matches something in graceignore.txt.
            let fileInfo = FileInfo(filePath)

            let shouldIgnoreThisFile =
                filePath.StartsWith(Current().GraceDirectory, StringComparison.InvariantCultureIgnoreCase) // it's in the /.grace directory
                || filePath.Equals(Current().GraceStatusFile, StringComparison.InvariantCultureIgnoreCase) // it's the Grace Status file
                || filePath.Equals(Current().GraceObjectCacheFile, StringComparison.InvariantCultureIgnoreCase) // it's the Grace Object Cache file
                || filePath.EndsWith(".gracetmp") // it's a Grace temporary file
                || Directory.Exists(filePath) // it's a directory
                //|| fileInfo.Attributes.HasFlag(FileAttributes.Temporary)                                          // it's temporary - why doesn't this work
                || Current().GraceDirectoryIgnoreEntries // one of the directories in the path matches a directory ignore line
                   |> Array.exists (fun graceIgnoreLine -> checkIgnoreLineAgainstDirectory fileInfo.Directory graceIgnoreLine)
                || Current().GraceDirectoryIgnoreEntries // the file name matches a directory ignore line (which is weird, but possible)
                   |> Array.exists (fun graceIgnoreLine -> checkIgnoreLineAgainstFile filePath graceIgnoreLine)
                || Current().GraceFileIgnoreEntries // the file name matches a file ignore line
                   |> Array.exists (fun graceIgnoreLine -> checkIgnoreLineAgainstFile filePath graceIgnoreLine)


            //logToAnsiConsole Colors.Verbose $"In shouldIgnoreFile: filePath: {filePath}; shouldIgnore: {shouldIgnoreThisFile}"
            shouldIgnoreCache.TryAdd(filePath, shouldIgnoreThisFile) |> ignore
            shouldIgnoreThisFile

    let private notString = "not "

    /// Returns true if directoryPath should be ignored by Grace, otherwise returns false.
    let shouldIgnoreDirectory (directoryPath: string) =
        let mutable shouldIgnore = false
        let wasAlreadyCached = shouldIgnoreCache.TryGetValue(directoryPath, &shouldIgnore)

        if wasAlreadyCached then
            shouldIgnore
        else
            let directoryInfo = DirectoryInfo(directoryPath)

            let shouldIgnoreDirectory =
                directoryInfo.FullName.StartsWith(Current().GraceDirectory)
                || (Current().GraceDirectoryIgnoreEntries
                    |> Array.exists (fun graceIgnoreLine -> checkIgnoreLineAgainstDirectory directoryInfo graceIgnoreLine))

            shouldIgnoreCache.TryAdd(directoryPath, shouldIgnoreDirectory) |> ignore
            //logToAnsiConsole Colors.Verbose $"In shouldIgnoreDirectory: directoryPath: {directoryPath}; shouldIgnore: {shouldIgnoreDirectory}"
            shouldIgnoreDirectory

    /// Returns true if directoryPath should not be ignored by Grace, otherwise returns false.
    let shouldNotIgnoreDirectory (directoryPath: string) = not <| shouldIgnoreDirectory directoryPath

    /// Creates a LocalFileVersion for the given FileInfo instance.
    let createLocalFileVersion (fileInfo: FileInfo) =
        task {
            if fileInfo.Exists then
                let relativePath = Path.GetRelativePath(Current().RootDirectory, fileInfo.FullName)

                use stream = fileInfo.Open(fileStreamOptionsRead)
                let! isBinary = isBinaryFile stream
                stream.Position <- 0
                let! shaValue = computeSha256ForFile stream relativePath
                //logToConsole $"fileRelativePath: {fileRelativePath}; Sha256Hash: {shaValue}."
                return
                    Some(
                        LocalFileVersion.Create
                            (RelativePath(normalizeFilePath relativePath))
                            (Sha256Hash shaValue)
                            isBinary
                            fileInfo.Length
                            (Instant.FromDateTimeUtc(fileInfo.LastWriteTimeUtc))
                            true
                            fileInfo.LastWriteTimeUtc
                    )
            else
                return None
        }

    /// Gets the LocalDirectoryVersion for the root directory of the repository from GraceStatus.
    let getRootDirectoryVersion (graceStatus: GraceStatus) =
        graceStatus.Index.Values.FirstOrDefault(
            (fun localDirectoryVersion -> localDirectoryVersion.RelativePath = Constants.RootDirectoryPath),
            LocalDirectoryVersion.Default
        )

    let localWriteTimes = ConcurrentDictionary<FileSystemEntryType * RelativePath, DateTime>()

    /// Gets a dictionary of local paths and their last write times.
    let rec getWorkingDirectoryWriteTimes (directoryInfo: DirectoryInfo) =
        if shouldNotIgnoreDirectory directoryInfo.FullName then
            // Add the current directory to the lookup dictionary
            let directoryFullPath = RelativePath(normalizeFilePath (Path.GetRelativePath(Current().RootDirectory, directoryInfo.FullName)))

            localWriteTimes.AddOrUpdate(
                (FileSystemEntryType.Directory, directoryFullPath),
                (fun _ -> directoryInfo.LastWriteTimeUtc),
                (fun _ _ -> directoryInfo.LastWriteTimeUtc)
            )
            |> ignore

            // Add each file to the lookup dictionary
            for f in directoryInfo.GetFiles().Where(fun f -> not <| shouldIgnoreFile f.FullName) do
                let fileFullPath = RelativePath(normalizeFilePath (Path.GetRelativePath(Current().RootDirectory, f.FullName)))

                localWriteTimes.AddOrUpdate((FileSystemEntryType.File, fileFullPath), (fun _ -> f.LastWriteTimeUtc), (fun _ _ -> f.LastWriteTimeUtc))
                |> ignore

            // Call recursively for each subdirectory
            let parallelLoopResult =
                Parallel.ForEach(directoryInfo.GetDirectories(), Constants.ParallelOptions, (fun d -> getWorkingDirectoryWriteTimes d |> ignore))

            if parallelLoopResult.IsCompleted then
                ()
            else
                printfn $"Failed while gathering local write times."

        localWriteTimes

    /// Serializes an object of type 'T to the specified file path using MessagePack.
    let serializeMessagePack<'T> (filePath: string) (data: 'T) =
        task {
            //let startInstant = getCurrentInstant ()
            use fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize = 65536, useAsync = true)
            do! MessagePackSerializer.SerializeAsync(fileStream, data, messagePackSerializerOptions)
        //let duration = $"{(getCurrentInstant () - startInstant).TotalSeconds:F3}"
        //logToAnsiConsole Colors.Important $"Serialized file {filePath}; compressed length: {fileStream.Length}; time elapsed: {duration}s."
        }
        :> Task

    /// Deserializes an object of type 'T from the specified file path using MessagePack.
    let deserializeMessagePack<'T> (filePath: string) =
        task {
            //let startInstant = getCurrentInstant ()
            use fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize = 65536, useAsync = true)
            let! returnValue = MessagePackSerializer.DeserializeAsync<'T>(fileStream, messagePackSerializerOptions)
            //let duration = $"{(getCurrentInstant () - startInstant).TotalSeconds:F3}"
            //logToAnsiConsole Colors.Important $"Deserialized file {filePath}; compressed length: {fileStream.Length}; time elapsed: {duration}s."
            return returnValue
        }

    /// Retrieves the Grace status file and returns it as a GraceStatus instance.
    let readGraceStatusFile () =
        task {
            if
                File.Exists(Current().GraceStatusFile)
                && FileInfo(Current().GraceStatusFile).Length > 0L
            then
                return! deserializeMessagePack<GraceStatus> (Current().GraceStatusFile)
            else
                return GraceStatus.Default
        }

    /// Writes the Grace status file to disk.
    let writeGraceStatusFile (graceStatus: GraceStatus) =
        task {
            Directory.CreateDirectory(Path.GetDirectoryName(Current().GraceStatusFile))
            |> ignore // No-op if the directory already exists

            do! serializeMessagePack (Current().GraceStatusFile) graceStatus
        }

    /// Retrieves the Grace object cache file and returns it as a GraceIndex instance.
    let readGraceObjectCacheFile () =
        task {
            if
                File.Exists(Current().GraceObjectCacheFile)
                && FileInfo(Current().GraceObjectCacheFile).Length > 0L
            then
                return! deserializeMessagePack<GraceObjectCache> (Current().GraceObjectCacheFile)
            else
                return GraceObjectCache.Default
        }

    /// Writes the Grace object cache file to disk.
    let writeGraceObjectCacheFile (graceObjectCache: GraceObjectCache) =
        task {
            Directory.CreateDirectory(Path.GetDirectoryName(Current().GraceObjectCacheFile))
            |> ignore // No-op if the directory already exists

            do! serializeMessagePack (Current().GraceObjectCacheFile) graceObjectCache
        }

    /// Compared the repository's working directory against the Grace index file and returns the differences.
    let scanForDifferences (previousGraceStatus: GraceStatus) =
        task {
            try
                let lookupCache = Dictionary<FileSystemEntryType * RelativePath, (DateTime * Sha256Hash)>()

                let differences = ConcurrentStack<FileSystemDifference>()

                // Create an indexed lookup table of path -> lastWriteTimeUtc from the Grace Status index.
                for kvp in previousGraceStatus.Index do
                    let directoryVersion = kvp.Value

                    lookupCache.TryAdd(
                        (FileSystemEntryType.Directory, directoryVersion.RelativePath),
                        (directoryVersion.LastWriteTimeUtc, directoryVersion.Sha256Hash)
                    )
                    |> ignore

                    for file in directoryVersion.Files do
                        lookupCache.TryAdd((FileSystemEntryType.File, file.RelativePath), (file.LastWriteTimeUtc, file.Sha256Hash))
                        |> ignore

                // Get an indexed lookup dictionary of path -> lastWriteTimeUtc from the working directory.
                let localWriteTimes = getWorkingDirectoryWriteTimes (DirectoryInfo(Current().RootDirectory))

                // Loop through the working directory list and compare it to the Grace Status index.
                for kvp in localWriteTimes do
                    let ((fileSystemEntryType, relativePath), lastWriteTimeUtc) = kvp.Deconstruct()
                    // Check for additions
                    if not <| lookupCache.ContainsKey((fileSystemEntryType, relativePath)) then
                        differences.Push(FileSystemDifference.Create Add fileSystemEntryType relativePath)

                    // Check for changes
                    if lookupCache.ContainsKey((fileSystemEntryType, relativePath)) then
                        let (knownLastWriteTimeUtc, existingSha256Hash) = lookupCache[(fileSystemEntryType, relativePath)]
                        // Has the LastWriteTimeUtc changed from the one in GraceStatus?
                        if lastWriteTimeUtc <> knownLastWriteTimeUtc then
                            match fileSystemEntryType with
                            | FileSystemEntryType.Directory ->
                                // If it's a directory, just add it to the differences list.
                                logToAnsiConsole
                                    Colors.Verbose
                                    $"scanForDifferences: Difference in directory: {relativePath}; lastWriteTimeUtc: {lastWriteTimeUtc}; knownLastWriteTimeUtc: {knownLastWriteTimeUtc}."

                                differences.Push(FileSystemDifference.Create Change fileSystemEntryType relativePath)
                            | FileSystemEntryType.File ->
                                // If this is a file, then check that the contents have actually changed.
                                let fileInfo = FileInfo(Path.Combine(Current().RootDirectory, relativePath))

                                match! createLocalFileVersion fileInfo with
                                | Some newFileVersion ->
                                    if newFileVersion.Sha256Hash <> existingSha256Hash then
                                        differences.Push(FileSystemDifference.Create Change fileSystemEntryType relativePath)
                                | None -> ()

                // Check for deletions
                for keyValuePair in lookupCache do
                    let (fileSystemEntryType, relativePath) = keyValuePair.Key
                    let (knownLastWriteTimeUtc, existingSha256Hash) = keyValuePair.Value

                    if not <| localWriteTimes.ContainsKey((fileSystemEntryType, relativePath)) then
                        differences.Push(FileSystemDifference.Create Delete fileSystemEntryType relativePath)

                return differences.ToList()
            with ex ->
                logToAnsiConsole Colors.Error $"{ExceptionResponse.Create ex}"
                return List<FileSystemDifference>()
        }

    //let processedThings = ConcurrentQueue<string>()
    let mutable newDirectoryVersionCount = 0
    let mutable existingDirectoryVersionCount = 0

    /// Gathers all of the LocalDirectoryVersions and LocalFileVersions for the requested directory and its subdirectories, and returns them along with the Sha256Hash of the requested directory.
    let rec collectDirectoriesAndFiles
        (relativeDirectoryPath: RelativePath)
        (previousDirectoryVersions: Dictionary<RelativePath, LocalDirectoryVersion>)
        (newGraceStatus: GraceStatus)
        (parseResult: ParseResult)
        =

        let getDirectoryContents (previousDirectoryVersions: Dictionary<RelativePath, LocalDirectoryVersion>) (directoryInfo: DirectoryInfo) =
            task {
                let files = ConcurrentQueue<LocalFileVersion>()
                let directories = ConcurrentQueue<LocalDirectoryVersion>()

                // Create LocalFileVersion instances for each file in this directory.
                do!
                    Parallel.ForEachAsync(
                        directoryInfo.GetFiles().Where(fun f -> not <| shouldIgnoreFile f.FullName),
                        Constants.ParallelOptions,
                        (fun fileInfo continuationToken ->
                            ValueTask(
                                task {
                                    match! createLocalFileVersion fileInfo with
                                    | Some fileVersion -> files.Enqueue(fileVersion)
                                    | None -> ()
                                }
                            ))
                    )

                // Create or reuse existing LocalDirectoryVersion instances for each subdirectory in this directory.
                let defaultDirectoryVersion = KeyValuePair(String.Empty, LocalDirectoryVersion.Default)

                do!
                    Parallel.ForEachAsync(
                        directoryInfo
                            .GetDirectories()
                            .Where(fun d -> shouldNotIgnoreDirectory d.FullName),
                        Constants.ParallelOptions,
                        (fun subdirectoryInfo continuationToken ->
                            ValueTask(
                                task {
                                    let subdirectoryRelativePath = Path.GetRelativePath(Current().RootDirectory, subdirectoryInfo.FullName)

                                    let! (subdirectoryVersions: List<LocalDirectoryVersion>, filesInSubdirectory: List<LocalFileVersion>, sha256Hash) =
                                        collectDirectoriesAndFiles subdirectoryRelativePath previousDirectoryVersions newGraceStatus parseResult

                                    // Check if we already have a LocalDirectoryVersion for this subdirectory.
                                    let existingSubdirectoryVersion =
                                        previousDirectoryVersions
                                            .FirstOrDefault(
                                                (fun existingDirectoryVersion -> existingDirectoryVersion.Key = normalizeFilePath subdirectoryRelativePath),
                                                defaultValue = defaultDirectoryVersion
                                            )
                                            .Value

                                    // Check if we already have this exact SHA-256 hash for this relative path; if so, keep the existing SubdirectoryVersion and its Guid.
                                    // If the DirectoryId is Guid.Empty (from LocalDirectoryVersion.Default), or the Sha256Hash doesn't match, create a new LocalDirectoryVersion reflecting the changes.
                                    if
                                        existingSubdirectoryVersion.DirectoryVersionId = Guid.Empty
                                        || existingSubdirectoryVersion.Sha256Hash <> sha256Hash
                                    then
                                        if parseResult |> isOutputFormat "Verbose" then
                                            logToAnsiConsole
                                                Colors.Verbose
                                                $"In collectDirectoriesAndFiles: Processing {subdirectoryRelativePath};  Creating new subdirectoryVersion."

                                        let directoryIds =
                                            subdirectoryVersions
                                                .OrderBy(fun d -> d.RelativePath)
                                                .Select(fun d -> d.DirectoryVersionId)
                                                .ToList()

                                        let subdirectoryVersion =
                                            LocalDirectoryVersion.Create
                                                (Guid.NewGuid())
                                                (Current().RepositoryId)
                                                (RelativePath(normalizeFilePath subdirectoryRelativePath))
                                                sha256Hash
                                                directoryIds
                                                filesInSubdirectory
                                                (getLocalDirectorySize filesInSubdirectory)
                                                subdirectoryInfo.LastWriteTimeUtc
                                        //processedThings.Enqueue($"New      {subdirectoryVersion.RelativePath}")
                                        newGraceStatus.Index.TryAdd(subdirectoryVersion.DirectoryVersionId, subdirectoryVersion)
                                        |> ignore

                                        Interlocked.Increment(&newDirectoryVersionCount) |> ignore

                                        if parseResult |> isOutputFormat "Verbose" then
                                            logToAnsiConsole
                                                Colors.Important
                                                $"Created new DirectoryVersion: RelativePath: {normalizeFilePath subdirectoryRelativePath}; existing SHA256Hash: {existingSubdirectoryVersion.Sha256Hash}; new SHA256Hash: {sha256Hash}."

                                        directories.Enqueue(subdirectoryVersion)
                                    else
                                        if parseResult |> isOutputFormat "Verbose" then
                                            logToAnsiConsole
                                                Colors.Verbose
                                                $"In collectDirectoriesAndFiles: Processing {subdirectoryRelativePath};  Existing subdirectoryVersion: {existingSubdirectoryVersion.DirectoryVersionId}."
                                        //processedThings.Enqueue($"Existing {existingSubdirectoryVersion.RelativePath}")
                                        newGraceStatus.Index.TryAdd(existingSubdirectoryVersion.DirectoryVersionId, existingSubdirectoryVersion)
                                        |> ignore

                                        Interlocked.Increment(&existingDirectoryVersionCount) |> ignore

                                        directories.Enqueue(existingSubdirectoryVersion)
                                }
                            ))
                    )

                return (directories.OrderBy(fun d -> d.RelativePath).ToList(), files.OrderBy(fun d -> d.RelativePath).ToList())
            }

        task {
            let directoryInfo = DirectoryInfo(Path.Combine(Current().RootDirectory, relativeDirectoryPath))

            let! (directories, files) = getDirectoryContents previousDirectoryVersions directoryInfo
            //for file in files do processedThings.Enqueue(file.RelativePath)
            if parseResult |> isOutputFormat "Verbose" then
                logToAnsiConsole
                    Colors.Verbose
                    $"In collectDirectoriesAndFiles: Processing {relativeDirectoryPath}: {files.Count} files, {directories.Count} directories."

            let sha256Hash = computeSha256ForDirectory relativeDirectoryPath directories files

            if parseResult |> isOutputFormat "Verbose" then
                logToAnsiConsole
                    Colors.Verbose
                    $"In collectDirectoriesAndFiles: newDirectoryVersionCount: {newDirectoryVersionCount}; existingDirectoryVersionCount: {existingDirectoryVersionCount}."

            return (directories, files, sha256Hash)
        }

    /// Creates the Grace index file by scanning the repository's working directory.
    let createNewGraceStatusFile (previousGraceStatus: GraceStatus) (parseResult: ParseResult) =
        task {
            // Start with a new GraceStatus instance.
            let newGraceStatus = GraceStatus.Default
            let rootDirectoryInfo = DirectoryInfo(Current().RootDirectory)

            // Get the previous GraceStatus index values into a Dictionary for faster lookup.
            let previousDirectoryVersions = Dictionary<RelativePath, LocalDirectoryVersion>()

            for kvp in previousGraceStatus.Index do
                if not <| previousDirectoryVersions.TryAdd(kvp.Value.RelativePath, kvp.Value) then
                    logToAnsiConsole Colors.Error $"createNewGraceStatusFile: Failed to add {kvp.Value.RelativePath} to previousDirectoryVersions."

            let! (subdirectoriesInRootDirectory, filesInRootDirectory, rootSha256Hash) =
                collectDirectoriesAndFiles Constants.RootDirectoryPath previousDirectoryVersions newGraceStatus parseResult

            //let getBySha256HashParameters = GetBySha256HashParameters(RepositoryId = $"{Current().RepositoryId}", Sha256Hash = rootSha256Hash)
            //let! directoryId = Directory.GetBySha256Hash(getBySha256HashParameters)

            // Check for existing root directory version so we don't update the Guid if it already exists.
            let rootDirectoryVersion =
                let previousRootDirectoryVersion =
                    previousDirectoryVersions
                        .FirstOrDefault(
                            (fun existingDirectoryVersion -> existingDirectoryVersion.Key = Constants.RootDirectoryPath),
                            defaultValue = KeyValuePair(String.Empty, LocalDirectoryVersion.Default)
                        )
                        .Value

                if previousRootDirectoryVersion.Sha256Hash = rootSha256Hash then
                    if parseResult |> isOutputFormat "Verbose" then
                        logToAnsiConsole
                            Colors.Verbose
                            $"In createNewGraceStatusFile: Using existing rootDirectoryVersion: {previousRootDirectoryVersion.DirectoryVersionId}."

                    previousRootDirectoryVersion
                else
                    if parseResult |> isOutputFormat "Verbose" then
                        logToAnsiConsole Colors.Verbose $"In createNewGraceStatusFile: Creating new rootDirectoryVersion."

                    let subdirectoryIds =
                        subdirectoriesInRootDirectory
                            .OrderBy(fun d -> d.RelativePath)
                            .Select(fun d -> d.DirectoryVersionId)
                            .ToList()

                    LocalDirectoryVersion.Create
                        (Guid.NewGuid())
                        (Current().RepositoryId)
                        (RelativePath(normalizeFilePath Constants.RootDirectoryPath))
                        (Sha256Hash rootSha256Hash)
                        subdirectoryIds
                        filesInRootDirectory
                        (getLocalDirectorySize filesInRootDirectory)
                        rootDirectoryInfo.LastWriteTimeUtc

            newGraceStatus.Index.TryAdd(Guid.Parse($"{rootDirectoryVersion.DirectoryVersionId}"), rootDirectoryVersion)
            |> ignore

            //let sb = StringBuilder(processedThings.Count)
            //for dir in processedThings.OrderBy(fun d -> d) do
            //    sb.AppendLine(dir) |> ignore
            //do! File.WriteAllTextAsync(@$"C:\Intel\ProcessedThings{sb.Length}.txt", sb.ToString())
            let rootDirectoryVersion = getRootDirectoryVersion newGraceStatus

            return { newGraceStatus with RootDirectoryId = rootDirectoryVersion.DirectoryVersionId; RootDirectorySha256Hash = rootDirectoryVersion.Sha256Hash }
        }

    /// Adds a LocalDirectoryVersion to the local object cache.
    let addDirectoryToObjectCache (localDirectoryVersion: LocalDirectoryVersion) =
        task {
            let! objectCache = readGraceObjectCacheFile ()

            if not <| objectCache.Index.ContainsKey(localDirectoryVersion.DirectoryVersionId) then
                let allFilesExist =
                    localDirectoryVersion.Files
                    |> Seq.forall (fun file -> File.Exists(Path.Combine(Current().ObjectDirectory, file.RelativeDirectory, file.GetObjectFileName)))

                if allFilesExist then
                    objectCache.Index.TryAdd(localDirectoryVersion.DirectoryVersionId, localDirectoryVersion)
                    |> ignore

                    do! File.WriteAllTextAsync(Current().GraceObjectCacheFile, serialize objectCache)
                    return Ok()
                else
                    return Error "Directory could not be added to object cache. All files do not exist in /objects directory."
            else
                return Ok()
        }

    /// Removes a directory from the local object cache.
    let removeDirectoryFromObjectCache (directoryId: DirectoryVersionId) =
        task {
            let! objectCache = readGraceObjectCacheFile ()

            if objectCache.Index.ContainsKey(directoryId) then
                let mutable ldv = LocalDirectoryVersion.Default
                objectCache.Index.TryRemove(directoryId, &ldv) |> ignore
                do! File.WriteAllTextAsync(Current().GraceObjectCacheFile, serialize objectCache)
        }

    /// Downloads files from object storage that aren't already present in the local object cache.
    let downloadFilesFromObjectStorage (files: IEnumerable<LocalFileVersion>) (correlationId: string) =
        task {
            match Current().ObjectStorageProvider with
            | ObjectStorageProvider.Unknown -> return Ok()
            | AzureBlobStorage ->
                let results =
                    files.ToArray()
                    |> Array.where (fun f -> not <| File.Exists(f.FullObjectPath))
                    |> Array.Parallel.map (fun f ->
                        (task { return! Storage.GetFileFromObjectStorage f.ToFileVersion correlationId })
                            .Result)

                let (results, errors) =
                    results
                    |> Array.partition (fun result ->
                        match result with
                        | Ok _ -> true
                        | Error _ -> false)

                if errors.Count() > 0 then
                    let sb = stringBuilderPool.Get()

                    try
                        sb.Append($"Some files could not be downloaded from object storage.{Environment.NewLine}")
                        |> ignore

                        errors
                        |> Seq.iter (fun e ->
                            match e with
                            | Ok _ -> ()
                            | Error e ->
                                sb.AppendLine($"{e.Error}{Environment.NewLine}{serialize e.Properties}")
                                |> ignore)

                        return Error(sb.ToString())
                    finally
                        stringBuilderPool.Return(sb) |> ignore
                else
                    return Ok()
            | AWSS3 -> return Ok()
            | GoogleCloudStorage -> return Ok()
        }

    /// Uploads all new or changed files from a directory to object storage.
    let uploadFilesToObjectStorage (parameters: GetUploadMetadataForFilesParameters) =
        task {
            match Current().ObjectStorageProvider with
            | ObjectStorageProvider.Unknown ->
                return Error(GraceError.Create (StorageError.getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
            | AzureBlobStorage ->
                //logToAnsiConsole Colors.Verbose $"Uploading {fileVersions.Count()} files to object storage."
                if parameters.FileVersions.Count() > 0 then
                    match! Storage.GetUploadMetadataForFiles parameters with
                    | Ok graceReturnValue ->
                        let filesToUpload = graceReturnValue.ReturnValue
                        //logToAnsiConsole Colors.Verbose $"In Services.uploadFilesToObjectStorage(): filesToUpload: {serialize filesToUpload}."
                        let errors = ConcurrentQueue<GraceError>()

                        do!
                            Parallel.ForEachAsync(
                                filesToUpload,
                                Constants.ParallelOptions,
                                (fun uploadMetadata ct ->
                                    ValueTask(
                                        task {
                                            let fileVersion =
                                                (parameters.FileVersions.First(fun fileVersion -> fileVersion.Sha256Hash = uploadMetadata.Sha256Hash))
                                            //logToAnsiConsole Colors.Verbose $"In Services.uploadFilesToObjectStorage(): Uploading {fileVersion.GetObjectFileName} to object storage."
                                            match!
                                                Storage.SaveFileToObjectStorage
                                                    (RepositoryId.Parse(parameters.RepositoryId))
                                                    fileVersion
                                                    (uploadMetadata.BlobUriWithSasToken)
                                                    parameters.CorrelationId
                                            with
                                            | Ok result -> () //logToAnsiConsole Colors.Verbose $"In Services.uploadFilesToObjectStorage(): Uploaded {fileVersion.GetObjectFileName} to object storage."
                                            | Error error ->
                                                logToAnsiConsole
                                                    Colors.Error
                                                    $"Error uploading {fileVersion.GetObjectFileName} to object storage: {error.Error}"

                                                errors.Enqueue(error)
                                        }
                                    ))
                            )

                        if errors |> Seq.isEmpty then
                            return Ok(GraceReturnValue.Create true parameters.CorrelationId)
                        else
                            // use Seq.fold to create a single error message from the ConcurrentQueue<GraceError>
                            let errorMessage = errors |> Seq.fold (fun acc error -> $"{acc}\n{error.Error}") ""

                            let graceError =
                                GraceError.Create (StorageError.getErrorMessage StorageError.FailedUploadingFilesToObjectStorage) parameters.CorrelationId

                            return Error graceError |> enhance "Errors" errorMessage
                    | Error error -> return Error error
                else
                    return Ok(GraceReturnValue.Create true parameters.CorrelationId)
            | AWSS3 -> return Error(GraceError.Create (StorageError.getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
            | GoogleCloudStorage -> return Error(GraceError.Create (StorageError.getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
        }

    /// Creates an updated LocalDirectoryVersion instance, with a new DirectoryId, based on changes to an existing one.
    ///
    /// If this is a new subdirectory, the LocalDirectoryVersion will have just been created with empty subdirectory and file lists.
    let processChangedDirectoryVersion (newGraceStatus: GraceStatus) (localDirectoryVersion: LocalDirectoryVersion) =
        // Get LocalDirectoryVersion instances for the DirectoryId's listed as subdirectories.
        //   They will already be in newGraceStatus; existing, unchanged DirectoryVersions, and updated ones if they (or their descendants) were changed.
        let subdirectoryVersions =
            if localDirectoryVersion.Directories.Count > 0 then
                localDirectoryVersion.Directories
                    .Select(fun directoryId ->
                        //logToConsole $"Checking for DirectoryId {directoryId} in relative path '{directoryVersion.RelativePath}'."
                        let localDirectoryVersion =
                            newGraceStatus.Index
                                .FirstOrDefault((fun kvp -> kvp.Key = directoryId), KeyValuePair(Guid.Empty, LocalDirectoryVersion.Default))
                                .Value

                        if localDirectoryVersion.DirectoryVersionId <> Guid.Empty then
                            Some localDirectoryVersion
                        else
                            None)
                    .Where(fun opt -> Option.isSome opt)
                    .Select(fun opt -> Option.get opt)
                    .ToList()
            else
                List<LocalDirectoryVersion>()

        // Get the new SHA-256 hash for the updated contents of this directory.
        let sha256Hash = computeSha256ForDirectory (localDirectoryVersion.RelativePath) subdirectoryVersions localDirectoryVersion.Files

        let directoryInfo = DirectoryInfo(Path.Combine(Current().RootDirectory, localDirectoryVersion.RelativePath))

        // Create a new LocalDirectoryVersion that contains the updated SHA-256 hash.
        let newDirectoryVersion =
            LocalDirectoryVersion.Create
                (Guid.NewGuid())
                localDirectoryVersion.RepositoryId
                localDirectoryVersion.RelativePath
                sha256Hash
                localDirectoryVersion.Directories
                localDirectoryVersion.Files
                (getLocalDirectorySize localDirectoryVersion.Files)
                directoryInfo.LastWriteTimeUtc

        newDirectoryVersion

    /// Determines if the given difference is for a directory, instead of a file.
    let isDirectoryChange (difference: FileSystemDifference) =
        match difference.FileSystemEntryType with
        | FileSystemEntryType.Directory -> true
        | FileSystemEntryType.File -> false

    /// Determines if the given difference is for a file, instead of a directory.
    let isFileChange difference = not <| isDirectoryChange difference

    /// Gets a list of new or updated LocalDirectoryVersions that reflect changes in the working directory.
    ///
    /// If an empty list of differences is passed in, returns the same GraceStatus that was passed in, and an empty list of LocalDirectoryVersions.
    let getNewGraceStatusAndDirectoryVersions (previousGraceStatus: GraceStatus) (differences: IEnumerable<FileSystemDifference>) =
        task {
            /// Holds DirectoryVersions that have already been changed, so we can make more changes to them if needed.
            let changedDirectoryVersions = ConcurrentDictionary<RelativePath, LocalDirectoryVersion>()

            let rootDirectoryVersion = getRootDirectoryVersion previousGraceStatus

            // Making a copy of the contents of previousGraceIndex for newGraceIndex.
            let mutable newGraceStatus = { previousGraceStatus with Index = GraceIndex(previousGraceStatus.Index) }

            // First, process the directory changes.
            for difference in differences.Where(fun d -> isDirectoryChange d) do
                match difference.DifferenceType with
                | Add
                | Change ->
                    // Get the existing LocalDirectoryVersion that matches the relative path.
                    let existingDirectoryVersion =
                        previousGraceStatus.Index
                            .FirstOrDefault(
                                (fun kvp -> kvp.Value.RelativePath = difference.RelativePath),
                                KeyValuePair(Guid.Empty, LocalDirectoryVersion.Default)
                            )
                            .Value

                    if existingDirectoryVersion.DirectoryVersionId <> Guid.Empty then
                        // This is an existing directory; so we need to update it.
                        let updatedDirectoryVersion = processChangedDirectoryVersion newGraceStatus existingDirectoryVersion

                        newGraceStatus.Index.AddOrUpdate(
                            updatedDirectoryVersion.DirectoryVersionId,
                            (fun _ -> updatedDirectoryVersion),
                            (fun _ _ -> updatedDirectoryVersion)
                        )
                        |> ignore
                    //logToAnsiConsole Colors.Verbose $"Updated directory {difference.RelativePath} in GraceIndex."
                    else
                        // This is a new directory relative path. We need to create a new LocalDirectoryVersion instance and add it to Grace Index.
                        let directoryInfo = DirectoryInfo(Path.Combine(Current().RootDirectory, difference.RelativePath))

                        let sha256Hash = computeSha256ForDirectory difference.RelativePath (List<LocalDirectoryVersion>()) (List<LocalFileVersion>())

                        let localDirectoryVersion =
                            LocalDirectoryVersion.Create
                                (Guid.NewGuid())
                                rootDirectoryVersion.RepositoryId
                                difference.RelativePath
                                sha256Hash
                                (List<DirectoryVersionId>())
                                (List<LocalFileVersion>())
                                Constants.InitialDirectorySize
                                directoryInfo.LastWriteTimeUtc

                        // Add the newly-created LocalDirectoryVersion to the GraceIndex.
                        newGraceStatus.Index.AddOrUpdate(
                            localDirectoryVersion.DirectoryVersionId,
                            (fun _ -> localDirectoryVersion),
                            (fun _ _ -> localDirectoryVersion)
                        )
                        |> ignore
                        //logToAnsiConsole Colors.Verbose $"Added/updated directory {difference.RelativePath} in GraceIndex."

                        // Add this DirectoryVersion for further processing.
                        changedDirectoryVersions.AddOrUpdate(difference.RelativePath, (fun _ -> localDirectoryVersion), (fun _ _ -> localDirectoryVersion))
                        |> ignore
                | Delete ->
                    let mutable directoryVersion = newGraceStatus.Index.Values.First(fun dv -> dv.RelativePath = difference.RelativePath)

                    newGraceStatus.Index.TryRemove(directoryVersion.DirectoryVersionId, &directoryVersion)
                    |> ignore

            // Next, process the individual file changes.
            for difference in differences.Where(fun d -> isFileChange d) do
                match difference.DifferenceType with
                | Add ->
                    //logToConsole $"Add file {relativePath}."
                    let fileInfo = FileInfo(Path.Combine(Current().RootDirectory, difference.RelativePath))

                    let relativeDirectoryPath = getLocalRelativeDirectory fileInfo.DirectoryName (Current().RootDirectory)

                    let directoryVersion =
                        // Check if we've already modified this DirectoryVersion, if so, use that one.
                        // Otherwise, take the not-yet-modified one from newGraceIndex.
                        //let changedDirectoryVersion =
                        //    changedDirectoryVersions.Values.FirstOrDefault((fun dv -> dv.RelativePath = relativeDirectoryPath), LocalDirectoryVersion.Default)

                        let mutable changedDirectoryVersion = LocalDirectoryVersion.Default

                        changedDirectoryVersions.TryGetValue(relativeDirectoryPath, &changedDirectoryVersion)
                        |> ignore

                        if changedDirectoryVersion.DirectoryVersionId <> Guid.Empty then
                            changedDirectoryVersion
                        else
                            newGraceStatus.Index.Values.First(fun dv -> dv.RelativePath = relativeDirectoryPath)

                    match! createLocalFileVersion fileInfo with
                    | Some fileVersion -> directoryVersion.Files.Add(fileVersion)
                    | None -> ()

                    let updatedWithNewSize = { directoryVersion with Size = directoryVersion.Files.Sum(fun file -> int64 (file.Size)) }

                    // Add this directoryVersion for further processing.
                    changedDirectoryVersions.AddOrUpdate(directoryVersion.RelativePath, (fun _ -> updatedWithNewSize), (fun _ _ -> updatedWithNewSize))
                    |> ignore
                | Change ->
                    //logToAnsiConsole Colors.Verbose $"Change file {difference.RelativePath}."
                    let fileInfo = FileInfo(Path.Combine(Current().RootDirectory, difference.RelativePath))

                    let relativeDirectoryPath = normalizeFilePath (getLocalRelativeDirectory fileInfo.DirectoryName (Current().RootDirectory))
                    //logToAnsiConsole Colors.Verbose $"{fileInfo.FullName}; {fileInfo.DirectoryName}; relativeDirectoryPath: {relativeDirectoryPath}"

                    let directoryVersion =
                        let alreadyChanged =
                            changedDirectoryVersions.Values.FirstOrDefault((fun dv -> dv.RelativePath = relativeDirectoryPath), LocalDirectoryVersion.Default)

                        if alreadyChanged.DirectoryVersionId <> Guid.Empty then
                            //logToAnsiConsole Colors.Verbose $"Already changed: alreadyChanged: {serialize alreadyChanged}"
                            alreadyChanged
                        else
                            let localDirectoryVersion = newGraceStatus.Index.Values.First(fun dv -> dv.RelativePath = relativeDirectoryPath)
                            //logToAnsiConsole Colors.Verbose $"Not already changed; localDirectoryVersion: {serialize localDirectoryVersion}."
                            localDirectoryVersion

                    //for dv in graceIndex.Values do
                    //    if dv.RelativePath.Contains("Grpc") then
                    //        logToConsole $"{dv.RelativePath} = {relativeDirectoryPath}: {dv.RelativePath = relativeDirectoryPath}"

                    //for file in directoryVersion.Files do
                    //    logToConsole $"{difference.DifferenceType}: {file.RelativePath} = {difference.RelativePath}: {file.RelativePath = difference.RelativePath}"

                    // Get a fileVersion for this file, including Sha256Hash, and see if it matches with the previous value.
                    // If it's changed, we have a new file version; if Sha256Hash matches, then the file was updated but then changed back to the same contents.
                    let existingFileIndex = directoryVersion.Files.FindIndex(fun file -> file.RelativePath = difference.RelativePath)
                    //logToAnsiConsole Colors.Verbose $"difference.RelativePath: {difference.RelativePath}; existingFileIndex: {existingFileIndex}."
                    let existingFileVersion = directoryVersion.Files[existingFileIndex]

                    match! createLocalFileVersion fileInfo with
                    | Some fileVersion ->
                        if fileVersion.Sha256Hash <> existingFileVersion.Sha256Hash then
                            // Remove old version of this file from the cache.
                            directoryVersion.Files.RemoveAt(existingFileIndex)

                            // Add new version of this file to the cache.
                            directoryVersion.Files.Add(fileVersion)

                            let updatedWithNewSize = { directoryVersion with Size = directoryVersion.Files.Sum(fun file -> int64 (file.Size)) }

                            // Add this directoryVersion for further processing.
                            changedDirectoryVersions.AddOrUpdate(directoryVersion.RelativePath, (fun _ -> updatedWithNewSize), (fun _ _ -> updatedWithNewSize))
                            |> ignore
                    | None -> ()
                | Delete ->
                    // Have to remove this file from the directory it's in. That directory might already have been deleted above.
                    //logToConsole $"Delete file {relativePath}."
                    let fileInfo = FileInfo(Path.Combine(Current().RootDirectory, difference.RelativePath))

                    let relativeDirectoryPath = getLocalRelativeDirectory fileInfo.DirectoryName (Current().RootDirectory)

                    // Search for this relativePath in the index.
                    let directoryVersion =
                        newGraceStatus.Index.Values
                            .Where(fun dv -> dv.RelativePath = relativeDirectoryPath)
                            .ToList()

                    match directoryVersion.Count with
                    | 0 -> () // Do nothing because the directory has already been removed above.
                    | 1 -> // We have to delete this file from the DirectoryVersion.
                        let directoryVersion = directoryVersion[0]

                        // Remove this file from the cache.
                        let index = directoryVersion.Files.FindIndex(fun file -> file.RelativePath = difference.RelativePath)

                        directoryVersion.Files.RemoveAt(index)

                        let updatedWithNewSize = { directoryVersion with Size = directoryVersion.Files.Sum(fun file -> int64 (file.Size)) }

                        // Add this directoryVersion for further processing.
                        changedDirectoryVersions.AddOrUpdate(directoryVersion.RelativePath, (fun _ -> updatedWithNewSize), (fun _ _ -> updatedWithNewSize))
                        |> ignore
                    | _ -> () // This branch should be flagged as an error.

            // Now, we have the list of changed DirectoryVersions. Let's process them!
            // We have to add DirectoryAppearance and FileAppearance to this party, at some point.
            // I'll process them in descending order of how many path segments they have.
            // I'll get the parent directory versions because I know the RelativePath.
            // I'll add those to the list of DirectoryVersions that need to be processed.
            // When that's done, the last one should be root.
            // And when that's done, I have a brand-new Grace Index, and some new DirectoryVersions to be saved into Object Cache, and sent to the server.

            let newDirectoryVersions = List<LocalDirectoryVersion>()

            // We'll process the changed directory versions one at a time, starting with the deepest directories.
            while not changedDirectoryVersions.IsEmpty do
                // Take the longest path on the list, by number of segments; i.e. deepest directory first, so we can work back up the tree.
                let relativePath =
                    changedDirectoryVersions.Keys
                        .OrderByDescending(fun relativePath -> countSegments relativePath)
                        .First()

                let mutable previousDirectoryVersion = LocalDirectoryVersion.Default

                if changedDirectoryVersions.TryRemove(relativePath, &previousDirectoryVersion) then // It should always succeed, but, you never know.
                    //logToConsole $"previousDirectoryVersion.RelativePath: {previousDirectoryVersion.RelativePath}; DirectoryId: {previousDirectoryVersion.DirectoryVersionId}; Sha256Hash: {previousDirectoryVersion.Sha256Hash.Substring(0, 8)}"

                    // Get the new DirectoryVersion, including new SHA-256 hash
                    let newDirectoryVersion = processChangedDirectoryVersion newGraceStatus previousDirectoryVersion

                    // Add it to the list that we'll return
                    newDirectoryVersions.Add(newDirectoryVersion)

                    // Remove the previous DirectoryVersion for this relative path, and replace it with the new one.
                    let mutable previous = LocalDirectoryVersion.Default

                    let foundPrevious = newGraceStatus.Index.TryRemove(previousDirectoryVersion.DirectoryVersionId, &previous)

                    let added = newGraceStatus.Index.TryAdd(newDirectoryVersion.DirectoryVersionId, newDirectoryVersion)

                    // Add the parent DirectoryVersion to changed list, since we have to process up the tree.
                    match getParentPath relativePath with
                    | Some path ->
                        // Find the parent directory version. Either we've already modified it - it'll be in changedDirectoryVersions -
                        //   or we can get it from newGraceIndex.
                        let dv =
                            let alreadyChanged =
                                changedDirectoryVersions.Values.FirstOrDefault((fun dv -> dv.RelativePath = path), LocalDirectoryVersion.Default)

                            if alreadyChanged.DirectoryVersionId <> Guid.Empty then
                                alreadyChanged
                            else
                                newGraceStatus.Index.Values.First(fun dv -> dv.RelativePath = path)

                        // If we found a previous version of a subdirectory and removed it from newGraceIndex,
                        //   remove it from the parent's subdirectory list as well.
                        if foundPrevious then
                            dv.Directories.Remove(previous.DirectoryVersionId) |> ignore

                        // Add the new directory version to the parent's subdirectory list.
                        dv.Directories.Add(newDirectoryVersion.DirectoryVersionId) |> ignore

                        // Store it in the list of changedDirectoryVersions.
                        changedDirectoryVersions.AddOrUpdate(path, (fun _ -> dv), (fun _ _ -> dv))
                        |> ignore
                    | None -> ()
                else
                    AnsiConsole.WriteLine($"Didn't find relative path {relativePath} in changedDirectoryVersions.")

            //for x in newDirectoryVersions do
            //    logToConsole $"{x.RelativePath}; {x.Sha256Hash.Substring(0, 8)}; Directories: {x.Directories.Count}; Files: {x.Files.Count}"
            //logToAnsiConsole Colors.Verbose $"newDirectoryVersions.Count: {newDirectoryVersions.Count}"
            if newDirectoryVersions.Count > 0 then
                let rootExists =
                    newGraceStatus.Index.Values
                        .FirstOrDefault((fun dv -> dv.RelativePath = Constants.RootDirectoryPath), LocalDirectoryVersion.Default)
                        .DirectoryVersionId
                    <> Guid.Empty

                if rootExists then
                    let newRootDirectoryVersion = getRootDirectoryVersion newGraceStatus

                    newGraceStatus <-
                        { newGraceStatus with
                            RootDirectoryId = newRootDirectoryVersion.DirectoryVersionId
                            RootDirectorySha256Hash = newRootDirectoryVersion.Sha256Hash }

                return (newGraceStatus, newDirectoryVersions)
            else
                return (previousGraceStatus, newDirectoryVersions)
        }

    /// Ensures that the provided directory versions are uploaded to Grace Server.
    /// This will add new directory versions, and ignore existing directory versions, as they are immutable.
    let uploadDirectoryVersions (localDirectoryVersions: List<LocalDirectoryVersion>) correlationId =
        let directoryVersions = localDirectoryVersions.Select(fun ldv -> ldv.ToDirectoryVersion).ToList()

        let parameters = SaveDirectoryVersionsParameters(CorrelationId = correlationId, DirectoryVersions = directoryVersions)

        DirectoryVersion.SaveDirectoryVersions parameters

    //let updateObjectCacheFromWorkingDirectory (graceIndex: GraceIndex) =
    //    task {
    //        Parallel.ForEachAsync(graceIndex.Values,
    //    }

    /// The full path of the inter-process communication file that grace watch uses to communicate with other invocations of Grace.
    let IpcFileName () = Path.Combine(Path.GetTempPath(), "Grace", Current().BranchName, Constants.IpcFileName)

    /// Updates the contents of the `grace watch` status inter-process communication file.
    let updateGraceWatchInterprocessFile (graceStatus: GraceStatus) =
        task {
            try
                let newGraceWatchStatus =
                    { UpdatedAt = getCurrentInstant ()
                      RootDirectoryId = graceStatus.RootDirectoryId
                      RootDirectorySha256Hash = graceStatus.RootDirectorySha256Hash
                      LastFileUploadInstant = graceStatus.LastSuccessfulFileUpload
                      LastDirectoryVersionInstant = graceStatus.LastSuccessfulDirectoryVersionUpload
                      DirectoryIds = HashSet<DirectoryVersionId>(graceStatus.Index.Keys) }
                //logToAnsiConsole Colors.Important $"In updateGraceWatchStatus. newGraceWatchStatus.UpdatedAt: {newGraceWatchStatus.UpdatedAt.ToString(InstantPattern.ExtendedIso.PatternText, CultureInfo.InvariantCulture)}."
                //logToAnsiConsole Colors.Highlighted $"{Markup.Escape(EnhancedStackTrace.Current().ToString())}"

                Directory.CreateDirectory(Path.GetDirectoryName(IpcFileName())) |> ignore

                use fileStream = new FileStream(IpcFileName(), FileMode.Create, FileAccess.Write, FileShare.None)

                do! serializeAsync fileStream newGraceWatchStatus
                graceWatchStatusUpdateTime <- newGraceWatchStatus.UpdatedAt
                logToAnsiConsole Colors.Important $"Wrote inter-process communication file."
            with ex ->
                logToAnsiConsole Colors.Error $"Exception in updateGraceWatchInterprocessFile."
                logToAnsiConsole Colors.Error $"ex.GetType: {ex.GetType().FullName}{Environment.NewLine}{Environment.NewLine}"
                logToAnsiConsole Colors.Error $"ex.Message: {ex.Message}{Environment.NewLine}{Environment.NewLine}{ex.StackTrace}"
        }

    /// Reads the `grace watch` status inter-process communication file.
    let getGraceWatchStatus () =
        task {
            try
                // If the file exists, `grace watch` is running.
                if File.Exists(IpcFileName()) then
                    //logToAnsiConsole Colors.Verbose $"File {IpcFileName} exists."
                    use fileStream = new FileStream(IpcFileName(), FileMode.Open, FileAccess.Read, FileShare.Read)

                    let! graceWatchStatus = deserializeAsync<GraceWatchStatus> fileStream

                    // `grace watch` updates the file at least every five minutes to indicate that it's still alive.
                    // When `grace watch` exits, the status file is deleted in a try...finally (Program.CLI.fs), so the only
                    //   circumstance where it would be on-disk without `grace watch` running is if the process were killed.
                    // Just to be safe, we're going to check that the file has been written in the last five minutes.
                    if graceWatchStatus.UpdatedAt > getCurrentInstant().Minus(Duration.FromMinutes(5.0)) then
                        return Some graceWatchStatus
                    else
                        return None // File is more than five minutes old, so something weird happened and we shouldn't trust the information.
                else
                    //logToAnsiConsole Colors.Verbose $"File {IpcFileName} does not exist."
                    return None // `grace watch` isn't running.
            with ex ->
                logToAnsiConsole Colors.Error $"Exception when reading inter-process communication file."
                logToAnsiConsole Colors.Error $"ex.GetType: {ex.GetType().FullName}."
                logToAnsiConsole Colors.Error $"ex.Message: {StringExtensions.EscapeMarkup(ex.Message)}."
                logToAnsiConsole Colors.Error $"{Environment.NewLine}{StringExtensions.EscapeMarkup(ex.StackTrace)}."
                return None
        }

    /// Checks if a file already exists in the object cache.
    let isFileInObjectCache (fileVersion: LocalFileVersion) =
        task {
            let objectFileName = fileVersion.GetObjectFileName

            let objectFilePath = Path.Combine(Current().ObjectDirectory, fileVersion.RelativeDirectory, objectFileName)

            return File.Exists(objectFilePath)
        }

    /// Checks if a file version is already found in the object cache index.
    let isFileVersionInObjectCacheIndex (objectCache: GraceObjectCache) (fileVersion: LocalFileVersion) =
        // Find the DirectoryVersions that match the relative directory.
        let directoryVersions = objectCache.Index.Values.Where(fun dv -> dv.RelativePath = fileVersion.RelativeDirectory)
        // Check the ones that match for the same RelativePath and Sha256Hash as the fileVersion.
        let matchingDirectoryVersions =
            directoryVersions.Where(fun dv ->
                dv.Files
                    .Where(fun fv ->
                        fv.RelativePath = fileVersion.RelativePath
                        && fv.Sha256Hash = fileVersion.Sha256Hash)
                    .Count() > 0)
        // If any of the DirectoryVersions in the object cache have the file, return true.
        if matchingDirectoryVersions.Count() > 0 then true else false

    /// Checks if a directory version is already found in the object cache index.
    let isDirectoryVersionInObjectCache (objectCache: GraceObjectCache) (directoryVersion: LocalDirectoryVersion) =
        objectCache.Index.ContainsKey(directoryVersion.DirectoryVersionId)

    /// Updates the Grace Status index with new directory versions after getting them from the server.
    let updateGraceStatusWithNewDirectoryVersionsFromServer (graceStatus: GraceStatus) (newDirectoryVersions: IEnumerable<DirectoryVersion>) =
        let newGraceIndex = GraceIndex(graceStatus.Index)
        let mutable dvForDeletions = LocalDirectoryVersion.Default

        // First, either add the new ones, or replace the existing ones.
        for newDirectoryVersion in newDirectoryVersions do
            let existingDirectoryVersion =
                newGraceIndex.Values.FirstOrDefault((fun dv -> dv.RelativePath = newDirectoryVersion.RelativePath), LocalDirectoryVersion.Default)

            if
                existingDirectoryVersion.DirectoryVersionId
                <> LocalDirectoryVersion.Default.DirectoryVersionId
            then
                // We already have an entry with the same RelativePath, so remove the old one and add the new one.
                newGraceIndex.TryRemove(existingDirectoryVersion.DirectoryVersionId, &dvForDeletions)
                |> ignore

                newGraceIndex.AddOrUpdate(
                    newDirectoryVersion.DirectoryVersionId,
                    (fun _ -> newDirectoryVersion.ToLocalDirectoryVersion DateTime.UtcNow),
                    (fun _ _ -> newDirectoryVersion.ToLocalDirectoryVersion DateTime.UtcNow)
                )
                |> ignore
            else
                // We didn't find the RelativePath, so it's a new DirectoryVersion.
                newGraceIndex.AddOrUpdate(
                    newDirectoryVersion.DirectoryVersionId,
                    (fun _ -> newDirectoryVersion.ToLocalDirectoryVersion DateTime.UtcNow),
                    (fun _ _ -> newDirectoryVersion.ToLocalDirectoryVersion DateTime.UtcNow)
                )
                |> ignore

        // Finally, delete any that don't exist anymore.
        // Get the list of the all of the subdirectories referenced in the DirectoryVersions left after replacing existing ones and adding new ones.
        let allSubdirectories =
            HashSet<DirectoryVersionId>(
                newGraceIndex.Values.Select(fun dv -> dv.Directories)
                |> Seq.collect (fun dvs -> dvs)
            )

        let allSubdirectoriesDistinct = allSubdirectories |> Seq.distinct
        // Now check every DirectoryVersion in newGraceIndex to see if it's in the list of subdirectories.
        //   If it's no longer referenced by anyone, that means we can delete it.
        for directoryVersion in newGraceIndex.Values do
            if
                not <| (directoryVersion.RelativePath = Constants.RootDirectoryPath)
                && not <| allSubdirectoriesDistinct.Contains(directoryVersion.DirectoryVersionId)
            then
                newGraceIndex.TryRemove(directoryVersion.DirectoryVersionId, &dvForDeletions)
                |> ignore

        let rootDirectoryVersion = newGraceIndex.Values.First(fun dv -> dv.RelativePath = Constants.RootDirectoryPath)

        let newGraceStatus =
            { Index = newGraceIndex
              RootDirectoryId = rootDirectoryVersion.DirectoryVersionId
              RootDirectorySha256Hash = rootDirectoryVersion.Sha256Hash
              LastSuccessfulDirectoryVersionUpload = graceStatus.LastSuccessfulDirectoryVersionUpload
              LastSuccessfulFileUpload = graceStatus.LastSuccessfulFileUpload }

        newGraceStatus

    /// Gets the file name used to indicate to `grace watch` that updates are in progress from another Grace command, and that it should ignore them.
    let updateInProgressFileName =
        let directory = Path.Combine(Path.GetTempPath(), "Grace", Current().BranchName)
        Directory.CreateDirectory(directory) |> ignore

        getNativeFilePath (Path.Combine(Path.GetTempPath(), "Grace", Current().BranchName, Constants.UpdateInProgressFileName))

    /// Updates the working directory to match the contents of new DirectoryVersions.
    ///
    /// In general, this means copying new and changed files into place, and removing deleted files and directories.
    let updateWorkingDirectory
        (previousGraceStatus: GraceStatus)
        (updatedGraceStatus: GraceStatus)
        (newDirectoryVersions: IEnumerable<DirectoryVersion>)
        (correlationId: CorrelationId)
        =
        task {
            // Loop through each new DirectoryVersion.
            for newDirectoryVersion in newDirectoryVersions do
                // Get the previous DirectoryVersion, so we can compare contents below.
                let previousDirectoryVersion =
                    previousGraceStatus.Index.Values.FirstOrDefault(
                        (fun dv -> dv.RelativePath = newDirectoryVersion.RelativePath),
                        LocalDirectoryVersion.Default
                    )
                // Ensure that the directory exists on disk.
                let directoryInfo = Directory.CreateDirectory(newDirectoryVersion.FullName)

                // Copy new and existing files into place.
                let newLocalFileVersions = newDirectoryVersion.Files.Select(fun file -> file.ToLocalFileVersion DateTime.UtcNow)

                for fileVersion in newLocalFileVersions do
                    let existingFileOnDisk = FileInfo(fileVersion.FullName)
                    let objectFile = FileInfo(fileVersion.FullObjectPath)

                    if not <| objectFile.Exists then
                        // This is an error. There _should_ be a file in the object cache for every file in each DirectoryVersion.
                        //   Anyway, we'll just download it from the server (again).
                        match! downloadFilesFromObjectStorage [| fileVersion |] correlationId with
                        | Ok _ ->
                            logToAnsiConsole Colors.Verbose $"Downloaded {fileVersion.FullObjectPath} from the object storage provider."

                            ()
                        | Error error ->
                            logToAnsiConsole
                                Colors.Error
                                $"An error occurred while downloading a file from the object storage provider. CorrelationId: {correlationId}."

                            logToAnsiConsole Colors.Error $"{error}"

                    if existingFileOnDisk.Exists then
                        // Need to compare existing file to new version from the object cache.
                        let findFileVersionFromPreviousGraceStatus = previousDirectoryVersion.Files.Where(fun f -> f.RelativePath = fileVersion.RelativePath)

                        if findFileVersionFromPreviousGraceStatus.Count() > 0 then
                            let fileVersionFromPreviousGraceStatus = findFileVersionFromPreviousGraceStatus.First()
                            // If the length is different, or the Sha256Hash is changing in the new version, we'll delete the
                            //   file in the working directory, and copy the version from the object cache to replace it.
                            if
                                existingFileOnDisk.Length <> fileVersion.Size
                                || fileVersionFromPreviousGraceStatus.Sha256Hash <> fileVersion.Sha256Hash
                            then
                                logToAnsiConsole
                                    Colors.Verbose
                                    $"Replacing {fileVersion.FullName}; previous length: {fileVersionFromPreviousGraceStatus.Size}; new length: {fileVersion.Size}."

                                existingFileOnDisk.Delete()
                                File.Copy(fileVersion.FullObjectPath, fileVersion.FullName)
                    else
                        // No existing file, so just copy it into place.
                        //logToAnsiConsole Colors.Verbose $"Copying file {fileVersion.FullName} from object cache; no existing file."
                        File.Copy(fileVersion.FullObjectPath, fileVersion.FullName)

                // Delete unnecessary directories.
                // Get DirectoryVersions for the subdirectories of the new DirectoryVersion.
                logToAnsiConsole
                    Colors.Verbose
                    $"Services.CLI.fs: updateWorkingDirectory(): {Markup.Escape(serialize (updatedGraceStatus.Index.Select(fun x -> x.Value.DirectoryVersionId)))}"

                logToAnsiConsole
                    Colors.Verbose
                    $"Services.CLI.fs: updateWorkingDirectory(): {Markup.Escape(serialize (newDirectoryVersions.Select(fun x -> x.DirectoryVersionId)))}"

                let subdirectoryVersions = newDirectoryVersion.Directories.Select(fun directoryId -> updatedGraceStatus.Index[directoryId])
                // Loop through the actual subdirectories on disk.
                for subdirectoryInfo in directoryInfo.EnumerateDirectories().ToArray() do
                    // If we don't have this subdirectory listed in new parent DirectoryVersion, and it's a directory that we shouldn't ignore,
                    //    that means that it was deleted, and we should delete it from the working directory.
                    let relativeSubdirectoryPath = Path.GetRelativePath(Current().RootDirectory, subdirectoryInfo.FullName)

                    if
                        not
                        <| (subdirectoryVersions
                            |> Seq.exists (fun subdirectoryVersion -> subdirectoryVersion.RelativePath = relativeSubdirectoryPath))
                        && shouldNotIgnoreDirectory subdirectoryInfo.FullName
                    then
                        //logToAnsiConsole Colors.Verbose $"Deleting directory {subdirectoryInfo.FullName}."
                        subdirectoryInfo.Delete(true)

                // Delete unnecessary files.
                // Loop through the actual files on disk.
                for fileInfo in directoryInfo.EnumerateFiles() do
                    // If we don't have this file in the new version of the directory, and it's a file that we shouldn't ignore,
                    //   that means that it was deleted, and we should delete it from the working directory.
                    // Ignored files get... ignored.
                    if
                        not
                        <| newLocalFileVersions.Any(fun fileVersion -> fileVersion.FullName = fileInfo.FullName)
                        && not <| shouldIgnoreFile fileInfo.FullName
                    then
                        //logToAnsiConsole Colors.Verbose $"Deleting file {fileInfo.FullName}."
                        fileInfo.Delete()
        }

    /// Creates a save reference with the given message.
    let createSaveReference rootDirectoryVersion message correlationId =
        task {
            //Activity.Current <- new Activity("createSaveReference")
            let createReferenceParameters =
                Parameters.Branch.CreateReferenceParameters(
                    OwnerId = $"{Current().OwnerId}",
                    OrganizationId = $"{Current().OrganizationId}",
                    RepositoryId = $"{Current().RepositoryId}",
                    BranchId = $"{Current().BranchId}",
                    CorrelationId = correlationId,
                    DirectoryVersionId = rootDirectoryVersion.DirectoryVersionId,
                    Sha256Hash = rootDirectoryVersion.Sha256Hash,
                    Message = message
                )

            let! result = Branch.Save createReferenceParameters
            //Activity.Current.SetTag("CorrelationId", correlationId) |> ignore
            match result with
            | Ok returnValue ->
                //logToAnsiConsole Colors.Verbose $"Created a save in branch {Current().BranchName}. Sha256Hash: {rootDirectoryVersion.Sha256Hash.Substring(0, 8)}. CorrelationId: {returnValue.CorrelationId}."
                //Activity.Current.AddTag("Created Save reference", "true")
                //                .SetStatus(ActivityStatusCode.Ok, returnValue.ReturnValue) |> ignore
                ()
            | Error error ->
                logToAnsiConsole
                    Colors.Error
                    $"An error occurred while creating a save that contains the current differences. CorrelationId: {error.CorrelationId}."
            //Activity.Current.AddTag("Created Save reference", "false")
            //                .AddTag("Server path", error.Properties["Path"])
            //                .SetStatus(ActivityStatusCode.Error, error.Error) |> ignore
            //Activity.Current.Dispose()
            return result
        }

    /// Generates a temporary file name within the ObjectDirectory, and returns the full file path.
    /// This file name will be used to copy modified files into before renaming them with their proper names and SHA256 values.
    let getTemporaryFilePath () =
        let tempDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Grace", Current().BranchName))

        Path.GetFullPath(Path.Combine(tempDirectory.FullName, $"{Path.GetRandomFileName()}.gracetmp"))

    /// Copies a file to the Object Directory, and returns a new FileVersion. The SHA-256 hash is computed and included in the object file name.
    let copyToObjectDirectory (filePath: FilePath) : Task<FileVersion option> =
        task {
            try
                if File.Exists(filePath) then
                    // First, capture the file by copying it to a temp name
                    let tempFilePath = getTemporaryFilePath ()
                    //logToConsole $"filePath: {filePath}; tempFilePath: {tempFilePath}"
                    let mutable iteration = 0

                    Constants.DefaultFileCopyRetryPolicy.Execute(fun () ->
                        //iteration <- iteration + 1
                        //logToAnsiConsole Colors.Deemphasized $"Attempt #{iteration} to copy file to object directory..."
                        File.Copy(sourceFileName = filePath, destFileName = tempFilePath, overwrite = true))

                    // Now that we've copied it, compute the SHA-256 hash.
                    let relativeFilePath = Path.GetRelativePath(Current().RootDirectory, filePath)

                    use tempFileStream = File.Open(tempFilePath, fileStreamOptionsRead)

                    let! isBinary = isBinaryFile tempFileStream
                    tempFileStream.Position <- 0
                    let! sha256Hash = computeSha256ForFile tempFileStream relativeFilePath
                    //logToConsole $"filePath: {filePath}; tempFilePath: {tempFilePath}; SHA256: {sha256Hash}"

                    // I'm going to rename the temp file below, using the SHA-256 hash, so I'll close the file and dispose the stream first.
                    do! tempFileStream.DisposeAsync()

                    // Get the new name for this version of the file, including the SHA-256 hash.
                    let relativeDirectoryPath = getLocalRelativeDirectory filePath (Current().RootDirectory)

                    let objectFileName = getObjectFileName filePath sha256Hash

                    let objectDirectoryPath = Path.Combine(Current().ObjectDirectory, relativeDirectoryPath)

                    let objectFilePath = Path.Combine(objectDirectoryPath, objectFileName)
                    //logToConsole $"relativeDirectoryPath: {relativeDirectoryPath}; objectFileName: {objectFileName}; objectFilePath: {objectFilePath}"

                    // If we don't already have this file, with this exact SHA256, make sure the directory exists,
                    //   and rename the temp file to the proper SHA256-enhanced name of the file.
                    if not (File.Exists(objectFilePath)) then
                        //logToConsole $"Before moving temp file to object storage..."
                        Directory.CreateDirectory(objectDirectoryPath) |> ignore // No-op if the directory already exists
                        File.Move(tempFilePath, objectFilePath)
                        //logToConsole $"After moving temp file to object storage..."
                        let objectFilePathInfo = FileInfo(objectFilePath)
                        //logToConsole $"After creating FileInfo; Exists: {objectFilePathInfo.Exists}; FullName = {objectFilePathInfo.FullName}..."
                        //logToConsole $"Finished copyToObjectDirectory for {filePath}; isBinary: {isBinary}; moved temp file to object directory."
                        let relativePath = Path.GetRelativePath(Current().RootDirectory, filePath)

                        return Some(FileVersion.Create (RelativePath relativePath) (Sha256Hash $"{sha256Hash}") ("") isBinary (objectFilePathInfo.Length))
                    else
                        // If we do already have this exact version of the file, just delete the temp file.
                        File.Delete(tempFilePath)
                        //logToConsole $"Finished copyToObjectDirectory for {filePath}; object file already exists; deleted temp file."
                        return None
                //return result
                else
                    logToAnsiConsole Colors.Error $"File {filePath} does not exist."
                    return None
            with ex ->
                logToAnsiConsole Colors.Error $"Exception in copyToObjectDirectory: {ExceptionResponse.Create ex}"
                return None
        }

    /// Copies new and updated files found in a list of FileSystemDifferences to the object directory.
    let copyUpdatedFilesToObjectCache (t: ProgressTask) (differences: List<FileSystemDifference>) =
        task {
            // Get the list of files that have been added or changed.
            let relativePathsOfUpdatedFiles =
                differences
                    .Select(fun difference ->
                        match difference.DifferenceType with
                        | Add ->
                            match difference.FileSystemEntryType with
                            | FileSystemEntryType.File -> Some difference.RelativePath
                            | FileSystemEntryType.Directory -> None
                        | Change ->
                            match difference.FileSystemEntryType with
                            | FileSystemEntryType.File -> Some difference.RelativePath
                            | FileSystemEntryType.Directory -> None
                        | Delete -> None)
                    .Where(fun relativePathOption -> relativePathOption.IsSome)
                    .Select(fun relativePath -> relativePath.Value)
            //logToAnsiConsole Colors.Verbose $"relativePathsOfUpdatedFiles: {serialize relativePathsOfUpdatedFiles}"

            // Create new LocalFileVersion instances for each updated file.
            let increment =
                if differences.Count > 0 then
                    (100.0 - t.Value) / float differences.Count
                else
                    0.0

            let newFileVersions = ConcurrentQueue<LocalFileVersion>()

            do!
                Parallel.ForEachAsync(
                    relativePathsOfUpdatedFiles,
                    Constants.ParallelOptions,
                    (fun relativePath continuationToken ->
                        ValueTask(
                            task {
                                //logToAnsiConsole Colors.Verbose $"In Services.CLI.copyToObjectDirectory: Copying {relativePath} to object storage."
                                match! copyToObjectDirectory (Path.Combine(Current().RootDirectory, relativePath)) with
                                | Some fileVersion -> newFileVersions.Enqueue(fileVersion.ToLocalFileVersion(DateTime.UtcNow))
                                //logToAnsiConsole Colors.Verbose $"Copied {fileVersion.RelativePath} to {fileVersion.GetObjectFileName} in object storage."
                                | None ->
                                    logToAnsiConsole Colors.Error $"Failed to copy {relativePath} to object storage."
                                    ()

                                t.Increment(increment)
                            }
                        ))
                )

            return newFileVersions
        }
