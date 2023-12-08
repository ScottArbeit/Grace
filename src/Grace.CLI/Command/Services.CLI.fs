namespace Grace.CLI

open Microsoft.Extensions
open FSharp.Collections
open Grace.SDK
open Grace.Shared.Client.Configuration
open Grace.Shared
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
open System.CommandLine.Parsing
open System.Diagnostics
open System.Globalization
open System.IO
open System.IO.Compression
open System.IO.Enumeration
open System.Linq
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks

module Services =

    /// Utility method to write to the console using color.
    let logToAnsiConsole color message = AnsiConsole.MarkupLine $"[{color}]{getCurrentInstantExtended()} {Environment.CurrentManagedThreadId:X2} {Markup.Escape(message)}[/]"

    /// A cache of paths that we've already decided to ignore or not.
    let private shouldIgnoreCache = ConcurrentDictionary<FilePath, bool>()

    /// The format of the data that is written to the inter-process communication file.
    [<Struct>]
    type GraceWatchStatus = 
        {
            UpdatedAt: Instant
            RootDirectoryId: DirectoryId
            RootDirectorySha256Hash: Sha256Hash
            LastFileUploadInstant: Instant
            LastDirectoryVersionInstant: Instant
            DirectoryIds: HashSet<DirectoryId>
        }
        static member Default = {
            UpdatedAt = Instant.MinValue
            RootDirectoryId = Guid.Empty 
            RootDirectorySha256Hash = Sha256Hash String.Empty
            LastFileUploadInstant = Instant.MinValue
            LastDirectoryVersionInstant = Instant.MinValue
            DirectoryIds = HashSet<DirectoryId>()
        }

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
            //AnsiConsole.MarkupLine($"{GetCurrentInstantString()} [{Colors.Deleted}]normalizedDirectoryPath: {normalizedDirectoryPath}; expression: {expression}; matches: true. Should ignore.[/]")
            true
        else
            //AnsiConsole.MarkupLine($"{GetCurrentInstantString()} [{Colors.Added}]normalizedDirectoryPath: {normalizedDirectoryPath}; expression: {expression}; matches: false. Should not ignore.[/]")
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
            //let b1 = filePath.StartsWith(Current().GraceDirectory)           // it's in the /.grace directory
            //let b2 = filePath.EndsWith(".gracetmp")                           // it's a Grace temporary file
            //let b3 = Directory.Exists(filePath)                               // it's a directory
            //let b4 = fileInfo.Attributes.HasFlag(FileAttributes.Temporary)    // it's temporary
            //printfn $"{GetCurrentInstantString()}: b1: {b1}; b2: {b2}; b3: {b3}; b4: {b4}"
            let shouldIgnoreThisFile = 
                filePath.StartsWith(Current().GraceDirectory)              // it's in the /.grace directory
                || filePath.Equals(Current().GraceStatusFile, StringComparison.InvariantCultureIgnoreCase)
                || filePath.Equals(Current().GraceObjectCacheFile, StringComparison.InvariantCultureIgnoreCase)
                || filePath.EndsWith(".gracetmp")                           // it's a Grace temporary file
                || Directory.Exists(filePath)                               // it's a directory
                //|| fileInfo.Attributes.HasFlag(FileAttributes.Temporary)    // it's temporary - why doesn't this work
                || Current().GraceDirectoryIgnoreEntries                    // one of the directories in the path matches a directory ignore line
                    |> Array.exists(fun graceIgnoreLine -> checkIgnoreLineAgainstDirectory fileInfo.Directory graceIgnoreLine)
                || Current().GraceDirectoryIgnoreEntries                    
                    |> Array.exists(fun graceIgnoreLine -> checkIgnoreLineAgainstFile filePath graceIgnoreLine)
                || Current().GraceFileIgnoreEntries                         // the file name matches a file ignore line
                    |> Array.exists(fun graceIgnoreLine -> checkIgnoreLineAgainstFile filePath graceIgnoreLine)
            //logToConsole $"In shouldIgnoreFile: filePath: {filePath}; shouldIgnore: {shouldIgnoreThisFile}"
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
                    |> Array.exists(fun graceIgnoreLine -> checkIgnoreLineAgainstDirectory directoryInfo graceIgnoreLine))
            shouldIgnoreCache.TryAdd(directoryPath, shouldIgnoreDirectory) |> ignore
            //logToConsole $"Should {if shouldIgnoreDirectory then String.Empty else notString}ignore directory {directoryPath}."
            shouldIgnoreDirectory

    //let shouldIgnore (fullPath: string) =
    //    if Directory.Exists(fullPath) then
    //        shouldIgnoreDirectory fullPath
    //    else
    //        shouldIgnoreFile fullPath

    let createLocalFileVersion (fileInfo: FileInfo) =
        task {
            if fileInfo.Exists then
                let relativePath = Path.GetRelativePath(Current().RootDirectory, fileInfo.FullName)
                use stream = fileInfo.Open(FileMode.Open, FileAccess.Read, FileShare.Read)
                let! isBinary = isBinaryFile stream
                stream.Position <- 0
                let! shaValue = computeSha256ForFile stream relativePath         
                //logToConsole $"fileRelativePath: {fileRelativePath}; Sha256Hash: {shaValue}."
                return Some (LocalFileVersion.Create (Current().RepositoryId) (RelativePath (normalizeFilePath relativePath)) (Sha256Hash shaValue) 
                    isBinary (uint64 fileInfo.Length) (getCurrentInstant()) true fileInfo.LastWriteTimeUtc)
            else
                return None
        }

    let getRootDirectoryVersion (graceStatus: GraceStatus) =
        graceStatus.Index.Values.FirstOrDefault((fun localDirectoryVersion -> localDirectoryVersion.RelativePath = Constants.RootDirectoryPath), LocalDirectoryVersion.Default)

    type LocalWriteTimes = ConcurrentDictionary<FileSystemEntryType * RelativePath, DateTime>
    let localWriteTimes = LocalWriteTimes()

    /// Gets a dictionary of local paths and their last write times.
    let rec getWorkingDirectoryWriteTimes (directoryInfo: DirectoryInfo) =
        if not <| shouldIgnoreDirectory directoryInfo.FullName then
            // Add the current directory to the lookup dictionary
            let directoryFullPath = RelativePath (normalizeFilePath (Path.GetRelativePath(Current().RootDirectory, directoryInfo.FullName)))
            localWriteTimes.AddOrUpdate((FileSystemEntryType.Directory, directoryFullPath), (fun _ -> directoryInfo.LastWriteTimeUtc), (fun _ _ -> directoryInfo.LastWriteTimeUtc)) |> ignore

            // Add each file to the lookup dictionary
            for f in directoryInfo.GetFiles().Where(fun f -> not <| shouldIgnoreFile f.FullName) do
                let fileFullPath = RelativePath (normalizeFilePath (Path.GetRelativePath(Current().RootDirectory, f.FullName)))
                localWriteTimes.AddOrUpdate((FileSystemEntryType.File, fileFullPath), (fun _ -> f.LastWriteTimeUtc), (fun _ _ -> f.LastWriteTimeUtc)) |> ignore
            
            // Call recursively for each subdirectory
            let parallelLoopResult = Parallel.ForEach(directoryInfo.GetDirectories(), Constants.ParallelOptions, (fun d -> getWorkingDirectoryWriteTimes d |> ignore))
            if parallelLoopResult.IsCompleted then () else printfn $"Failed while gathering local write times."

        localWriteTimes

    /// Retrieves the Grace status file and returns it as a GraceStatus instance.
    let readGraceStatusFile() = 
        task {
            if File.Exists(Current().GraceStatusFile) then
                use fileStream = Constants.DefaultRetryPolicy.Execute(fun _ -> File.Open(Current().GraceStatusFile, FileMode.Open, FileAccess.Read))
                use gzStream = new GZipStream(fileStream, CompressionMode.Decompress, leaveOpen = false)
                let! graceStatus = deserializeAsync<GraceStatus> gzStream
                //logToAnsiConsole Colors.Important $"Read Grace Status file from disk."
                return graceStatus
            else
                return GraceStatus.Default
        }

    /// Writes the Grace status file to disk.
    let writeGraceStatusFile (graceStatus: GraceStatus) =
        (task {
            use fileStream = Constants.DefaultRetryPolicy.Execute(fun _ -> File.Open(Current().GraceStatusFile, FileMode.Create, FileAccess.Write, FileShare.None))
            use gzStream = new GZipStream(fileStream, CompressionLevel.SmallestSize, leaveOpen = false)
            do! serializeAsync gzStream graceStatus 
            //logToAnsiConsole Colors.Important $"Wrote new Grace Status file to disk."
        }) :> Task

    /// Retrieves the Grace object cache file and returns it as a GraceIndex instance.
    let readGraceObjectCacheFile() = 
        task {
            if File.Exists(Current().GraceObjectCacheFile) then
                use fileStream = Constants.DefaultRetryPolicy.Execute(fun _ -> File.Open(Current().GraceStatusFile, FileMode.Open, FileAccess.Read))
                use gzStream = new GZipStream(fileStream, CompressionMode.Decompress, leaveOpen = false)
                return! deserializeAsync<GraceObjectCache> gzStream

            else
                return GraceObjectCache.Default
        }

    /// Writes the Grace object cache file to disk.
    let writeGraceObjectCacheFile (graceObjectCache: GraceObjectCache) =
        (task {
            use fileStream = Constants.DefaultRetryPolicy.Execute(fun _ -> File.Open(Current().GraceObjectCacheFile, FileMode.Create, FileAccess.Write, FileShare.None))
            use gzStream = new GZipStream(fileStream, CompressionLevel.SmallestSize, leaveOpen = false)
            do! serializeAsync gzStream graceObjectCache
        }) :> Task

    /// Compared the repository's working directory against the Grace index file and returns the differences.
    let scanForDifferences (previousGraceStatus: GraceStatus) =
        task {
            try
                let lookupCache = Dictionary<FileSystemEntryType * RelativePath, (DateTime * Sha256Hash)>()
                let differences = ConcurrentStack<FileSystemDifference>()
            
                // Create an indexed lookup table of path -> lastWriteTimeUtc from the Grace Status index.
                for kvp in previousGraceStatus.Index do
                    let directoryVersion = kvp.Value
                    lookupCache.TryAdd((FileSystemEntryType.Directory, directoryVersion.RelativePath), (directoryVersion.LastWriteTimeUtc, directoryVersion.Sha256Hash)) |> ignore
                    for file in directoryVersion.Files do
                        lookupCache.TryAdd((FileSystemEntryType.File, file.RelativePath), (file.LastWriteTimeUtc, file.Sha256Hash)) |> ignore

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
                                logToAnsiConsole Colors.Verbose $"scanForDifferences: Difference in directory: {relativePath}; lastWriteTimeUtc: {lastWriteTimeUtc}; knownLastWriteTimeUtc: {knownLastWriteTimeUtc}."
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
                logToAnsiConsole Colors.Error $"{createExceptionResponse ex}"
                return List<FileSystemDifference>()
        }

    //let processedThings = ConcurrentQueue<string>()

    /// Computes the SHA-256 value for a given local directory.
    let rec processDirectoryContents (relativeDirectoryPath: RelativePath) (previousDirectoryVersions: Dictionary<RelativePath, LocalDirectoryVersion>) (newGraceStatus: GraceStatus) =
        let getDirectoryContents (previousDirectoryVersions: Dictionary<RelativePath, LocalDirectoryVersion>) (directoryInfo: DirectoryInfo) =
            task {
                let files = ConcurrentQueue<LocalFileVersion>()
                let directories = ConcurrentQueue<LocalDirectoryVersion>()

                // Create LocalFileVersion instances for each file in this directory.
                do! Parallel.ForEachAsync(directoryInfo.GetFiles().Where(fun f -> not <| shouldIgnoreFile f.FullName), Constants.ParallelOptions, (fun fileInfo continuationToken ->
                    ValueTask(task {
                        match! createLocalFileVersion fileInfo with
                        | Some fileVersion -> files.Enqueue(fileVersion)
                        | None -> ()
                    })
                ))

                // Create or reuse existing LocalDirectoryVersion instances for each subdirectory in this directory.
                do! Parallel.ForEachAsync(directoryInfo.GetDirectories().Where(fun d -> not <| shouldIgnoreDirectory d.FullName), Constants.ParallelOptions, (fun subdirectoryInfo continuationToken ->
                    ValueTask(task {
                        let subdirectoryRelativePath = Path.GetRelativePath(Current().RootDirectory, subdirectoryInfo.FullName)
                        let! (subdirectoryVersions: List<LocalDirectoryVersion>, filesInSubdirectory: List<LocalFileVersion>, sha256Hash) = processDirectoryContents subdirectoryRelativePath previousDirectoryVersions newGraceStatus
                        
                        // Check if we already have a LocalDirectoryVersion for this subdirectory.
                        let existingSubdirectoryVersion = previousDirectoryVersions.FirstOrDefault((fun existingDirectoryVersion -> existingDirectoryVersion.Key = normalizeFilePath subdirectoryRelativePath), 
                            defaultValue = KeyValuePair(String.Empty, LocalDirectoryVersion.Default)).Value
                        
                        // Check if we already have this exact SHA-256 hash for this relative path; if so, keep the existing SubdirectoryVersion and its Guid.     
                        // If the DirectoryId is Guid.Empty (from LocalDirectoryVersion.Default), or the Sha256Hash doesn't match, create a new LocalDirectoryVersion reflecting the changes.
                        if existingSubdirectoryVersion.DirectoryId = Guid.Empty || existingSubdirectoryVersion.Sha256Hash <> sha256Hash then
                            let directoryIds = subdirectoryVersions.OrderBy(fun d -> d.RelativePath).Select(fun d -> d.DirectoryId).ToList()
                            let subdirectoryVersion = LocalDirectoryVersion.Create (Guid.NewGuid()) (Current().RepositoryId) (RelativePath (normalizeFilePath subdirectoryRelativePath))
                                                        sha256Hash directoryIds filesInSubdirectory (getLocalDirectorySize filesInSubdirectory) subdirectoryInfo.LastWriteTimeUtc
                            //processedThings.Enqueue($"New      {subdirectoryVersion.RelativePath}")
                            newGraceStatus.Index.TryAdd(subdirectoryVersion.DirectoryId, subdirectoryVersion) |> ignore
                            directories.Enqueue(subdirectoryVersion)
                        else
                            //processedThings.Enqueue($"Existing {existingSubdirectoryVersion.RelativePath}")
                            newGraceStatus.Index.TryAdd(existingSubdirectoryVersion.DirectoryId, existingSubdirectoryVersion) |> ignore
                            directories.Enqueue(existingSubdirectoryVersion)
                    })
                ))

                return (directories.OrderBy(fun d -> d.RelativePath).ToList(), files.OrderBy(fun d -> d.RelativePath).ToList())
            }

        task {
            let directoryInfo = DirectoryInfo(Path.Combine(Current().RootDirectory, relativeDirectoryPath))
            let! (directories, files) = getDirectoryContents previousDirectoryVersions directoryInfo
            //for file in files do processedThings.Enqueue(file.RelativePath)
            let sha256Hash = computeSha256ForDirectory relativeDirectoryPath directories files
            return (directories, files, sha256Hash)
        }

    /// Creates the Grace index file by scanning the repository's working directory.
    let createNewGraceStatusFile (previousGraceStatus: GraceStatus) = 
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
                processDirectoryContents Constants.RootDirectoryPath previousDirectoryVersions newGraceStatus

            //let getBySha256HashParameters = GetBySha256HashParameters(RepositoryId = $"{Current().RepositoryId}", Sha256Hash = rootSha256Hash)
            //let! directoryId = Directory.GetBySha256Hash(getBySha256HashParameters)

            let subdirectoryIds = subdirectoriesInRootDirectory.OrderBy(fun d -> d.RelativePath).Select(fun d -> d.DirectoryId).ToList()
            let rootDirectoryVersion = 
                LocalDirectoryVersion.Create (Guid.NewGuid())
                    (Current().RepositoryId) (RelativePath (normalizeFilePath Constants.RootDirectoryPath)) (Sha256Hash rootSha256Hash) 
                    subdirectoryIds filesInRootDirectory (getLocalDirectorySize filesInRootDirectory) rootDirectoryInfo.LastWriteTimeUtc
            newGraceStatus.Index.TryAdd(Guid.Parse($"{rootDirectoryVersion.DirectoryId}"), rootDirectoryVersion) |> ignore

            //let sb = StringBuilder(processedThings.Count)
            //for dir in processedThings.OrderBy(fun d -> d) do
            //    sb.AppendLine(dir) |> ignore
            //do! File.WriteAllTextAsync(@$"C:\Intel\ProcessedThings{sb.Length}.txt", sb.ToString())
            let rootDirectoryVersion = getRootDirectoryVersion newGraceStatus

            return {newGraceStatus with RootDirectoryId = rootDirectoryVersion.DirectoryId; RootDirectorySha256Hash = rootDirectoryVersion.Sha256Hash}
        }

    /// Adds a LocalDirectoryVersion to the local object cache.
    let addDirectoryToObjectCache (localDirectoryVersion: LocalDirectoryVersion) =
        task {
            let! objectCache = readGraceObjectCacheFile()
            if not <| objectCache.Index.ContainsKey(localDirectoryVersion.DirectoryId) then
                let allFilesExist = localDirectoryVersion.Files |> Seq.forall(fun file -> File.Exists(Path.Combine(Current().ObjectDirectory, file.RelativeDirectory, file.GetObjectFileName)))
                if allFilesExist then
                    objectCache.Index.TryAdd(localDirectoryVersion.DirectoryId, localDirectoryVersion) |> ignore
                    do! File.WriteAllTextAsync(Current().GraceObjectCacheFile, serialize objectCache)
                    return Ok ()
                else
                    return Error "Directory could not be added to object cache. All files do not exist in /objects directory."
            else
                return Ok ()
        }
        
    /// Removes a directory from the local object cache.
    let removeDirectoryFromObjectCache (directoryId: DirectoryId) =
        task {
            let! objectCache = readGraceObjectCacheFile()
            if objectCache.Index.ContainsKey(directoryId) then
                let mutable ldv = LocalDirectoryVersion.Default
                objectCache.Index.TryRemove(directoryId, &ldv) |> ignore
                do! File.WriteAllTextAsync(Current().GraceObjectCacheFile, serialize objectCache)
        }
    
    /// Downloads files from object storage that aren't already present in the local object cache.
    let downloadFilesFromObjectStorage (files: IEnumerable<LocalFileVersion>) (correlationId: string) =
        task {
            match Current().ObjectStorageProvider with
            | ObjectStorageProvider.Unknown ->
                return Ok ()
            | AzureBlobStorage -> 
                let anyErrors = files.ToArray()
                                |> Array.where (fun f -> not <| File.Exists(f.FullObjectPath))
                                |> Array.Parallel.map (fun f -> 
                                    (task {
                                        return! Storage.GetFileFromObjectStorage f.ToFileVersion correlationId
                                    }).Result)
                                |> Array.exists (fun result -> match result with | Ok _ -> false | Error _ -> true)
                if anyErrors then
                    return Error "Some files could not be downloaded from object storage."
                else
                    return Ok ()
            | AWSS3 -> 
                return Ok ()
            | GoogleCloudStorage ->
                return Ok ()
        }

    /// Uploads all new or changed files from a directory to object storage.
    let uploadFilesToObjectStorage (fileVersions: IEnumerable<LocalFileVersion>) (correlationId: string) =
        task {
            match Current().ObjectStorageProvider with
            | ObjectStorageProvider.Unknown -> return Error (GraceError.Create (StorageError.getErrorMessage StorageError.NotImplemented) correlationId)
            | AzureBlobStorage -> 
                if fileVersions.Count() > 0 then
                    let! graceResult = Storage.FilesExistInObjectStorage (fileVersions.Select(fun f -> f.ToFileVersion).ToList()) correlationId
                    match graceResult with
                    | Ok graceReturnValue ->
                        let filesToUpload = graceReturnValue.ReturnValue
                        let errors = ConcurrentQueue<GraceError>()
                        do! Parallel.ForEachAsync(filesToUpload, Constants.ParallelOptions, (fun uploadMetadata ct ->
                            ValueTask(task {
                                let fileVersion = (fileVersions.First(fun f -> f.Sha256Hash = uploadMetadata.Sha256Hash)).ToFileVersion
                                let! x = Storage.SaveFileToObjectStorage fileVersion (uploadMetadata.BlobUriWithSasToken) correlationId
                                match x with
                                | Ok result -> ()
                                | Error error -> errors.Enqueue(error)
                            })))

                        if errors.Count = 0 then
                            return Ok (GraceReturnValue.Create true correlationId)
                        else
                            return Error (GraceError.Create (StorageError.getErrorMessage StorageError.FailedUploadingFilesToObjectStorage) correlationId)
                    | Error error -> return Error error
                else
                    return Ok (GraceReturnValue.Create true correlationId)
            | AWSS3 -> return Error (GraceError.Create (StorageError.getErrorMessage StorageError.NotImplemented) correlationId)
            | GoogleCloudStorage -> return Error (GraceError.Create (StorageError.getErrorMessage StorageError.NotImplemented) correlationId)
        }

    /// Uploads a new or changed file to object storage.
    let uploadToServerAsync (fileVersion: FileVersion) correlationId =
        task {
            match! Storage.GetUploadUri fileVersion correlationId with
            | Ok returnValue ->
                let! result = Storage.SaveFileToObjectStorage fileVersion (Uri(returnValue.ReturnValue)) correlationId
                match result with
                | Ok message -> 
                    //printfn $"{message}"
                    return Ok message
                | Error error ->
                    printfn $"{error}"
                    return Error error
            | Error error -> return Error error
        }

    /// Creates a new LocalDirectoryVersion instance based on changes to an existing one.
    ///
    /// If this is a new subdirectory, the LocalDirectoryVersion will have just been created with empty subdirectory and file lists.
    let processChangedDirectoryVersion (directoryVersion: LocalDirectoryVersion) (newGraceIndex: GraceStatus) =
        // Get LocalDirectoryVersion information for the subdirectories of this directory; they should already have been updated if there were any changed deeper in the directory tree.
        let subdirectoryVersions =
            if directoryVersion.Directories.Count > 0 then
                directoryVersion.Directories
                    .Select(fun directoryId -> 
                        //logToConsole $"Checking for DirectoryId {directoryId} in relative path '{directoryVersion.RelativePath}'."
                        let ldv = newGraceIndex.Index.FirstOrDefault((fun kvp -> kvp.Key = directoryId), KeyValuePair(Guid.Empty, LocalDirectoryVersion.Default)).Value
                        if ldv.DirectoryId <> Guid.Empty then
                            Some ldv
                        else 
                            None)
                    .Where(fun opt -> Option.isSome opt)
                    .Select(fun opt -> Option.get opt)
                    .ToList()
            else
                List<LocalDirectoryVersion>()

        // Get the new SHA-256 hash for the updated contents of this directory.
        let sha256Hash = computeSha256ForDirectory (directoryVersion.RelativePath) subdirectoryVersions directoryVersion.Files

        // Create a new LocalDirectoryVersion that contains the updated SHA-256 hash.
        let directoryInfo = DirectoryInfo(Path.Combine(Current().RootDirectory, directoryVersion.RelativePath))
        let newDirectoryVersion = LocalDirectoryVersion.Create (Guid.NewGuid()) directoryVersion.RepositoryId 
                                    directoryVersion.RelativePath sha256Hash directoryVersion.Directories directoryVersion.Files 
                                    (getLocalDirectorySize directoryVersion.Files) directoryInfo.LastWriteTimeUtc
        newDirectoryVersion

    /// Determines if the given difference is for a directory, instead of a file.
    let isDirectoryChange (difference: FileSystemDifference) =
        match difference.FileSystemEntryType with | FileSystemEntryType.Directory -> true | FileSystemEntryType.File -> false

    /// Determines if the given difference is for a file, instead of a directory.
    let isFileChange difference = not (isDirectoryChange difference)

    /// Gets a list of new or updated LocalDirectoryVersions that reflect changes in the working directory.
    let getNewGraceStatusAndDirectoryVersions (previousGraceStatus: GraceStatus) (differences: List<FileSystemDifference>) =
        task {
            let changedDirectoryVersions = ConcurrentDictionary<RelativePath, LocalDirectoryVersion>()
            let rootDirectoryVersion = getRootDirectoryVersion previousGraceStatus
            
            // Making a copy of the contents of previousGraceIndex for newGraceIndex.
            let mutable newGraceStatus = {previousGraceStatus with Index = GraceIndex(previousGraceStatus.Index)}

            // First, process the directory changes.
            for difference in differences.Where(fun d -> isDirectoryChange d) do
                match difference.DifferenceType with
                | Add  
                | Change -> 
                    // Need to add an entry to changedDirectoryVersions.
                    let directoryInfo = DirectoryInfo(Path.Combine(Current().RootDirectory, difference.RelativePath))
                    let sha256Hash = computeSha256ForDirectory difference.RelativePath (List<LocalDirectoryVersion>()) (List<LocalFileVersion>())
                    let localDirectoryVersion = LocalDirectoryVersion.Create (Guid.NewGuid()) rootDirectoryVersion.RepositoryId difference.RelativePath sha256Hash 
                                                    (List<DirectoryId>()) (List<LocalFileVersion>()) Constants.InitialDirectorySize directoryInfo.LastWriteTimeUtc
                    
                    // Add the newly-created LocalDirectoryVersion to the GraceIndex.
                    newGraceStatus.Index.AddOrUpdate(localDirectoryVersion.DirectoryId, (fun _ -> localDirectoryVersion), (fun _ _ -> localDirectoryVersion)) |> ignore
                    logToAnsiConsole Colors.Verbose $"Added/updated directory {difference.RelativePath} in GraceIndex."

                    // Add this DirectoryVersion for further processing.
                    changedDirectoryVersions.AddOrUpdate(difference.RelativePath, (fun _ -> localDirectoryVersion), (fun _ _ -> localDirectoryVersion)) |> ignore
                //| Change -> 
                    // Do nothing because the file changes below will catch these.
                //    () 
                | Delete ->
                    let mutable directoryVersion = newGraceStatus.Index.Values.First(fun dv -> dv.RelativePath = difference.RelativePath)
                    newGraceStatus.Index.TryRemove(directoryVersion.DirectoryId, &directoryVersion) |> ignore

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
                        let changedDirectoryVersion = changedDirectoryVersions.Values.FirstOrDefault((fun dv -> dv.RelativePath = relativeDirectoryPath), LocalDirectoryVersion.Default)
                        if changedDirectoryVersion.DirectoryId <> Guid.Empty then
                            changedDirectoryVersion
                        else
                            newGraceStatus.Index.Values.First(fun dv -> dv.RelativePath = relativeDirectoryPath)   
                    match! createLocalFileVersion fileInfo with
                    | Some fileVersion -> directoryVersion.Files.Add(fileVersion)
                    | None -> ()

                    let updatedWithNewSize = {directoryVersion with Size = uint64 (directoryVersion.Files.Sum(fun file -> int64 (file.Size)))}

                    // Add this directoryVersion for further processing.
                    changedDirectoryVersions.AddOrUpdate(directoryVersion.RelativePath, (fun _ -> updatedWithNewSize), (fun _ _ -> updatedWithNewSize)) |> ignore
                | Change -> 
                    logToConsole $"Change file {difference.RelativePath}."
                    let fileInfo = FileInfo(Path.Combine(Current().RootDirectory, difference.RelativePath))
                    let relativeDirectoryPath = normalizeFilePath (getLocalRelativeDirectory fileInfo.DirectoryName (Current().RootDirectory))
                    logToConsole $"{fileInfo.FullName}; {fileInfo.DirectoryName}; relativeDirectoryPath: {relativeDirectoryPath}"

                    let directoryVersion = 
                        let alreadyChanged = changedDirectoryVersions.Values.FirstOrDefault((fun dv -> dv.RelativePath = relativeDirectoryPath), LocalDirectoryVersion.Default)
                        if alreadyChanged.DirectoryId <> Guid.Empty then
                            alreadyChanged
                        else
                            newGraceStatus.Index.Values.First(fun dv -> dv.RelativePath = relativeDirectoryPath)

                    //logToConsole (sprintf "%A" directoryVersion)

                    //for dv in graceIndex.Values do
                    //    if dv.RelativePath.Contains("Grpc") then
                    //        logToConsole $"{dv.RelativePath} = {relativeDirectoryPath}: {dv.RelativePath = relativeDirectoryPath}"
                    
                    //for file in directoryVersion.Files do
                    //    logToConsole $"{difference.DifferenceType}: {file.RelativePath} = {difference.RelativePath}: {file.RelativePath = difference.RelativePath}"

                    // Get a fileVersion for this file, including Sha256Hash, and see if it matches with the previous value.
                    // If it's changed, we have a new file version; if Sha256Hash matches, then the file was updated but then changed back to the same contents.
                    let existingFileIndex = directoryVersion.Files.FindIndex(fun file -> file.RelativePath = difference.RelativePath)
                    let existingFileVersion = directoryVersion.Files[existingFileIndex]
                    match! createLocalFileVersion fileInfo with
                    | Some fileVersion ->
                        if fileVersion.Sha256Hash <> existingFileVersion.Sha256Hash then
                            // Remove old version of this file from the cache.
                            directoryVersion.Files.RemoveAt(existingFileIndex)

                            // Add new version of this file to the cache.
                            directoryVersion.Files.Add(fileVersion)
                            let updatedWithNewSize = {directoryVersion with Size = uint64 (directoryVersion.Files.Sum(fun file -> int64 (file.Size)))}

                            // Add this directoryVersion for further processing.
                            changedDirectoryVersions.AddOrUpdate(directoryVersion.RelativePath, (fun _ -> updatedWithNewSize), (fun _ _ -> updatedWithNewSize)) |> ignore
                    | None -> ()
                | Delete ->
                    // Have to remove this file from the directory it's in. That directory might already have been deleted above.
                    //logToConsole $"Delete file {relativePath}."
                    let fileInfo = FileInfo(Path.Combine(Current().RootDirectory, difference.RelativePath))
                    let relativeDirectoryPath = getLocalRelativeDirectory fileInfo.DirectoryName (Current().RootDirectory)

                    // Search for this relativePath in the index.
                    let directoryVersion = newGraceStatus.Index.Values.Where(fun dv -> dv.RelativePath = relativeDirectoryPath).ToList()
                    match directoryVersion.Count with
                    | 0 -> ()   // Do nothing because the directory has already been removed above.
                    | 1 ->      // We have to delete this file from the DirectoryVersion.
                        let directoryVersion = directoryVersion[0]
                        
                        // Remove this file from the cache.
                        let index = directoryVersion.Files.FindIndex(fun file -> file.RelativePath = difference.RelativePath)
                        directoryVersion.Files.RemoveAt(index)
                        let updatedWithNewSize = {directoryVersion with Size = uint64 (directoryVersion.Files.Sum(fun file -> int64 (file.Size)))}
                        
                        // Add this directoryVersion for further processing.
                        changedDirectoryVersions.AddOrUpdate(directoryVersion.RelativePath, (fun _ -> updatedWithNewSize), (fun _ _ -> updatedWithNewSize)) |> ignore
                    | _ -> ()   // This branch should be flagged as an error.

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
                let relativePath = changedDirectoryVersions.Keys.OrderByDescending(fun relativePath -> countSegments relativePath).First()
                let mutable previousDirectoryVersion = LocalDirectoryVersion.Default
                if changedDirectoryVersions.TryRemove(relativePath, &previousDirectoryVersion) then     // It should always succeed, but, you never know.
                    //logToConsole $"previousDirectoryVersion.RelativePath: {previousDirectoryVersion.RelativePath}; DirectoryId: {previousDirectoryVersion.DirectoryId}; Sha256Hash: {previousDirectoryVersion.Sha256Hash.Substring(0, 8)}"

                    // Get the new DirectoryVersion, including new SHA-256 hash
                    let newDirectoryVersion = processChangedDirectoryVersion previousDirectoryVersion newGraceStatus

                    // Add it to the list that we'll return
                    newDirectoryVersions.Add(newDirectoryVersion)

                    // Remove the previous DirectoryVersion for this relative path, and replace it with the new one.
                    let mutable previous = LocalDirectoryVersion.Default
                    let foundPrevious = newGraceStatus.Index.TryRemove(previousDirectoryVersion.DirectoryId, &previous)
                    let added = newGraceStatus.Index.TryAdd(newDirectoryVersion.DirectoryId, newDirectoryVersion)
                    
                    // Add the parent DirectoryVersion to changed list, since we have to process up the tree.
                    match getParentPath relativePath with
                    | Some path -> 
                        // Find the parent directory version. Either we've already modified it - it'll be in changedDirectoryVersions - 
                        //   or we can get it from newGraceIndex.
                        let dv = 
                            let alreadyChanged = changedDirectoryVersions.Values.FirstOrDefault((fun dv -> dv.RelativePath = path), LocalDirectoryVersion.Default)
                            if alreadyChanged.DirectoryId <> Guid.Empty then
                                alreadyChanged
                            else
                                newGraceStatus.Index.Values.First(fun dv -> dv.RelativePath = path)
                        
                        // If we found a previous version of a subdirectory and removed it from newGraceIndex, 
                        //   remove it from the parent's subdirectory list as well.
                        if foundPrevious then
                            dv.Directories.Remove(previous.DirectoryId) |> ignore

                        // Add the new directory version to the parent's subdirectory list.
                        dv.Directories.Add(newDirectoryVersion.DirectoryId) |> ignore

                        // Store it in the list of changedDirectoryVersions.
                        changedDirectoryVersions.AddOrUpdate(path, (fun _ -> dv), (fun _ _ -> dv)) |> ignore
                    | None -> ()
                else
                    AnsiConsole.WriteLine($"Didn't find relative path {relativePath} in changedDirectoryVersions.")

            //for x in newDirectoryVersions do
            //    logToConsole $"{x.RelativePath}; {x.Sha256Hash.Substring(0, 8)}; Directories: {x.Directories.Count}; Files: {x.Files.Count}"
            //logToAnsiConsole Colors.Verbose $"newDirectoryVersions.Count: {newDirectoryVersions.Count}"
            let rootExists = newGraceStatus.Index.Values.FirstOrDefault((fun dv -> dv.RelativePath = Constants.RootDirectoryPath), LocalDirectoryVersion.Default).DirectoryId <> Guid.Empty
            if rootExists then
                let newRootDirectoryVersion = getRootDirectoryVersion newGraceStatus
                newGraceStatus <- {newGraceStatus with RootDirectoryId = newRootDirectoryVersion.DirectoryId; RootDirectorySha256Hash = newRootDirectoryVersion.Sha256Hash}
            return (newGraceStatus, newDirectoryVersions)
        }

    /// Ensures that the provided directory versions are uploaded to Grace Server.
    /// This will add new directory versions, and ignore existing directory versions, as they are immutable.
    let uploadDirectoryVersions (localDirectoryVersions: List<LocalDirectoryVersion>) correlationId =
        let directoryVersions = localDirectoryVersions.Select(fun ldv -> ldv.ToDirectoryVersion).ToList()
        let parameters = SaveDirectoryVersionsParameters(CorrelationId = correlationId, DirectoryVersions = directoryVersions)
        Directory.SaveDirectoryVersions parameters

    //let updateObjectCacheFromWorkingDirectory (graceIndex: GraceIndex) =
    //    task {
    //        Parallel.ForEachAsync(graceIndex.Values, 
    //    }

    /// The full path of the inter-process communication file that grace watch uses to communicate with other invocations of Grace.
    let IpcFileName() = Path.Combine(Path.GetTempPath(), Constants.IpcFileName)

    /// Updates the contents of the `grace watch` status inter-process communication file.
    let updateGraceWatchInterprocessFile (graceStatus: GraceStatus) =
        task {
            try
                let newGraceWatchStatus = { 
                    UpdatedAt = getCurrentInstant()
                    RootDirectoryId = graceStatus.RootDirectoryId
                    RootDirectorySha256Hash = graceStatus.RootDirectorySha256Hash
                    LastFileUploadInstant = graceStatus.LastSuccessfulFileUpload
                    LastDirectoryVersionInstant = graceStatus.LastSuccessfulDirectoryVersionUpload
                    DirectoryIds = HashSet<DirectoryId>(graceStatus.Index.Keys)
                }
                //logToAnsiConsole Colors.Important $"In updateGraceWatchStatus. newGraceWatchStatus.UpdatedAt: {newGraceWatchStatus.UpdatedAt.ToString(InstantPattern.ExtendedIso.PatternText, CultureInfo.InvariantCulture)}."
                //logToAnsiConsole Colors.Highlighted $"{Markup.Escape(EnhancedStackTrace.Current().ToString())}"

                use fileStream = new FileStream(IpcFileName(), FileMode.Create, FileAccess.Write, FileShare.None)
                do! serializeAsync fileStream newGraceWatchStatus
                graceWatchStatusUpdateTime <- newGraceWatchStatus.UpdatedAt
                logToAnsiConsole Colors.Important $"Wrote inter-process communication file."
            with ex -> 
                logToAnsiConsole Colors.Error $"Exception in updateGraceWatchStatus."
                logToAnsiConsole Colors.Error $"ex.GetType: {ex.GetType().FullName}"
                logToAnsiConsole Colors.Error $"ex.Message: {ex.Message}"
                logToAnsiConsole Colors.Error $"{Environment.NewLine}{ex.StackTrace}"
        }

    /// Reads the `grace watch` status inter-process communication file.
    let getGraceWatchStatus() =
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
        let matchingDirectoryVersions = directoryVersions.Where(fun dv -> dv.Files.Where(fun fv -> fv.RelativePath = fileVersion.RelativePath && fv.Sha256Hash = fileVersion.Sha256Hash).Count() > 0)
        // If any of the DirectoryVersions in the object cache have the file, return true.
        if matchingDirectoryVersions.Count() > 0 then true else false

    /// Checks if a directory version is already found in the object cache index.
    let isDirectoryVersionInObjectCache (objectCache: GraceObjectCache) (directoryVersion: LocalDirectoryVersion) =
        objectCache.Index.ContainsKey(directoryVersion.DirectoryId)

    /// Updates the Grace Status index with new directory versions after getting them from the server.
    let updateGraceStatusWithNewDirectoryVersions (graceStatus: GraceStatus) (newDirectoryVersions: IEnumerable<DirectoryVersion>) =
        let newGraceIndex = GraceIndex(graceStatus.Index)
        let mutable dvForDeletions = LocalDirectoryVersion.Default

        // First, either add the new ones, or replace the existing ones.
        for newDirectoryVersion in newDirectoryVersions do
            let existingDirectoryVersion = newGraceIndex.Values.FirstOrDefault((fun dv -> dv.RelativePath = newDirectoryVersion.RelativePath), LocalDirectoryVersion.Default)
            if existingDirectoryVersion.DirectoryId <> LocalDirectoryVersion.Default.DirectoryId then
                // We already have an entry with the same RelativePath, so remove the old one and add the new one.
                newGraceIndex.TryRemove(existingDirectoryVersion.DirectoryId, &dvForDeletions) |> ignore
                newGraceIndex.AddOrUpdate(newDirectoryVersion.DirectoryId, (fun _ -> newDirectoryVersion.ToLocalDirectoryVersion DateTime.UtcNow), (fun _ _ -> newDirectoryVersion.ToLocalDirectoryVersion DateTime.UtcNow)) |> ignore
            else
                // We didn't find the RelativePath, so it's a new DirectoryVersion.
                newGraceIndex.AddOrUpdate(newDirectoryVersion.DirectoryId, (fun _ -> newDirectoryVersion.ToLocalDirectoryVersion DateTime.UtcNow), (fun _ _ -> newDirectoryVersion.ToLocalDirectoryVersion DateTime.UtcNow)) |> ignore

        // Finally, delete any that don't exist anymore.
        // Get the list of the all of the subdirectories referenced in the DirectoryVersions left after replacing existing ones and adding new ones.
        let allSubdirectories = HashSet<DirectoryId>(newGraceIndex.Values.Select(fun dv -> dv.Directories) |> Seq.collect (fun dvs -> dvs))
        let allSubdirectoriesDistinct = allSubdirectories |> Seq.distinct
        // Now check every DirectoryVersion in newGraceIndex to see if it's in the list of subdirectories.
        //   If it's no longer referenced by anyone, that means we can delete it.
        for directoryVersion in newGraceIndex.Values do
            if not <| (directoryVersion.RelativePath = Constants.RootDirectoryPath) && not <| allSubdirectoriesDistinct.Contains(directoryVersion.DirectoryId) then
                newGraceIndex.TryRemove(directoryVersion.DirectoryId, &dvForDeletions) |> ignore

        let rootDirectoryVersion = newGraceIndex.Values.First(fun dv -> dv.RelativePath = Constants.RootDirectoryPath)
        let newGraceStatus = {
            Index = newGraceIndex
            RootDirectoryId = rootDirectoryVersion.DirectoryId
            RootDirectorySha256Hash = rootDirectoryVersion.Sha256Hash
            LastSuccessfulDirectoryVersionUpload = graceStatus.LastSuccessfulDirectoryVersionUpload
            LastSuccessfulFileUpload = graceStatus.LastSuccessfulFileUpload
        }
        newGraceStatus

    /// Gets the file name used to indicate to `grace watch` that updates are in progress from another Grace command, and that it should ignore them.
    let getUpdateInProgressFileName() = getNativeFilePath (Path.Combine(Environment.GetEnvironmentVariable("temp"), $"grace-UpdatesInProgress.txt"))

    /// Updates the working directory to match the contents of new DirectoryVersions.
    ///
    /// In general, this means copying new and changed files into place, and removing deleted files and directories.
    let updateWorkingDirectory (previousGraceStatus: GraceStatus) (updatedGraceStatus: GraceStatus) (newDirectoryVersions: List<DirectoryVersion>) =
        // Loop through each new DirectoryVersion.
        for newDirectoryVersion in newDirectoryVersions do
            // Get the previous DirectoryVersion, so we can compare contents below.
            let previousDirectoryVersion = previousGraceStatus.Index.Values.FirstOrDefault((fun dv -> dv.RelativePath = newDirectoryVersion.RelativePath), LocalDirectoryVersion.Default)
            // Ensure that the directory exists on disk.
            let directoryInfo = Directory.CreateDirectory(newDirectoryVersion.FullName)
            
            // Copy new and existing files into place.
            let newLocalFileVersions = newDirectoryVersion.Files.Select(fun file -> file.ToLocalFileVersion DateTime.UtcNow)
            for fileVersion in newLocalFileVersions do
                let existingFileOnDisk = FileInfo(fileVersion.FullName)
                if existingFileOnDisk.Exists then
                    // Need to compare existing file to new version from the object cache.
                    let findFileVersionFromPreviousGraceStatus = previousDirectoryVersion.Files.Where(fun f -> f.RelativePath = fileVersion.RelativePath)
                    if findFileVersionFromPreviousGraceStatus.Count() > 0 then
                        let fileVersionFromPreviousGraceStatus = findFileVersionFromPreviousGraceStatus.First()
                        // If the length is different, or the Sha256Hash is changing in the new version, we'll delete the
                        //   file in the working directory, and copy the version from the object cache to replace it.
                        if uint64 existingFileOnDisk.Length <> fileVersion.Size || 
                                fileVersionFromPreviousGraceStatus.Sha256Hash <> fileVersion.Sha256Hash then
                            //logToAnsiConsole Colors.Verbose $"Replacing {fileVersion.FullName}; previous length: {fileVersionFromPreviousGraceStatus.Size}; new length: {fileVersion.Size}."
                            existingFileOnDisk.Delete()
                            File.Copy(fileVersion.FullObjectPath, fileVersion.FullName)
                else
                    // No existing file, so just copy it into place.
                    //logToAnsiConsole Colors.Verbose $"Copying file {fileVersion.FullName} from object cache; no existing file."
                    File.Copy(fileVersion.FullObjectPath, fileVersion.FullName)

            // Delete unnecessary directories.
            // Get DirectoryVersions for the subdirectories of the new DirectoryVersion.
            logToAnsiConsole Colors.Verbose $"Services.CLI.fs: updateWorkingDirectory(): {Markup.Escape(serialize (updatedGraceStatus.Index.Select(fun x -> x.Value.DirectoryId)))}"
            logToAnsiConsole Colors.Verbose $"Services.CLI.fs: updateWorkingDirectory(): {Markup.Escape(serialize (newDirectoryVersions.Select(fun x -> x.DirectoryId)))}"
            let subdirectoryVersions = newDirectoryVersion.Directories.Select(fun directoryId -> updatedGraceStatus.Index[directoryId])
            // Loop through the actual subdirectories on disk.
            for subdirectoryInfo in directoryInfo.EnumerateDirectories().ToArray() do 
                // If we don't have this subdirectory listed in new parent DirectoryVersion, and it's a directory that we shouldn't ignore,
                //    that means that it was deleted, and we should delete it from the working directory.
                let relativeSubdirectoryPath = Path.GetRelativePath(Current().RootDirectory, subdirectoryInfo.FullName)
                if not <| subdirectoryVersions.Any(fun subdirectoryVersion -> subdirectoryVersion.RelativePath = relativeSubdirectoryPath) &&
                        not <| shouldIgnoreDirectory subdirectoryInfo.FullName then
                    //logToAnsiConsole Colors.Verbose $"Deleting directory {subdirectoryInfo.FullName}."
                    subdirectoryInfo.Delete(true)
                    ()

            // Delete unnecessary files.
            // Loop through the actual files on disk.
            for fileInfo in directoryInfo.EnumerateFiles() do
                // If we don't have this file in the new version of the directory, and it's a file that we shouldn't ignore,
                //   that means that it was deleted, and we should delete it from the working directory.
                // Ignored files get... ignored.
                if not <| newLocalFileVersions.Any(fun fileVersion -> fileVersion.FullName = fileInfo.FullName) &&
                        not <| shouldIgnoreFile fileInfo.FullName then
                    //logToAnsiConsole Colors.Verbose $"Deleting file {fileInfo.FullName}."
                    fileInfo.Delete()
                    ()

    /// Creates a save reference with the given message.
    let createSaveReference rootDirectoryVersion message correlationId =
        task {
            //Activity.Current <- new Activity("createSaveReference")
            let createReferenceParameters = Parameters.Branch.CreateReferenceParameters(
                                   OwnerId = $"{Current().OwnerId}",
                                   OrganizationId = $"{Current().OrganizationId}",
                                   RepositoryId = $"{Current().RepositoryId}",
                                   BranchId = $"{Current().BranchId}",
                                   CorrelationId = correlationId,
                                   DirectoryId = rootDirectoryVersion.DirectoryId,
                                   Sha256Hash = rootDirectoryVersion.Sha256Hash,
                                   Message = message)
                                   
            let! result = Branch.Save createReferenceParameters
            //Activity.Current.SetTag("CorrelationId", correlationId) |> ignore
            match result with
            | Ok returnValue -> 
                //logToAnsiConsole Colors.Verbose $"Created a save in branch {Current().BranchName}. Sha256Hash: {rootDirectoryVersion.Sha256Hash.Substring(0, 8)}. CorrelationId: {returnValue.CorrelationId}."
                //Activity.Current.AddTag("Created Save reference", "true")
                //                .SetStatus(ActivityStatusCode.Ok, returnValue.ReturnValue) |> ignore  
                ()
            | Error error -> 
                logToAnsiConsole Colors.Error $"An error occurred while creating a save that contains the current differences. CorrelationId: {error.CorrelationId}."
                //Activity.Current.AddTag("Created Save reference", "false")
                //                .AddTag("Server path", error.Properties["Path"])
                //                .SetStatus(ActivityStatusCode.Error, error.Error) |> ignore
            //Activity.Current.Dispose()
            return result
        }
