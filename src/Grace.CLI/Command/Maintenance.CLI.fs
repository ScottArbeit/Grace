namespace Grace.CLI.Command

open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.DirectoryVersion
open Grace.Shared.Services
open Grace.Shared.Parameters.Storage
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Types
open Spectre.Console
open System
open System.Collections.Concurrent
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Linq
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open NodaTime
open MessagePack

module Maintenance =

    module private Options =
        let ownerId =
            new Option<OwnerId>(
                OptionName.OwnerId,
                Required = false,
                Description = "The repository's owner ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> Current().OwnerId)
            )

        let ownerName =
            new Option<String>(
                OptionName.OwnerName,
                Required = false,
                Description = "The repository's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationId =
            new Option<OrganizationId>(
                OptionName.OrganizationId,
                Required = false,
                Description = "The repository's organization ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> Current().OrganizationId)
            )

        let organizationName =
            new Option<String>(
                OptionName.OrganizationName,
                Required = false,
                Description = "The repository's organization name. [default: current organization]",
                Arity = ArgumentArity.ZeroOrOne
            )

        let repositoryId =
            new Option<RepositoryId>(
                OptionName.RepositoryId,
                [| "-r" |],
                Required = false,
                Description = "The repository's ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> Current().RepositoryId)
            )

        let repositoryName =
            new Option<String>(
                OptionName.RepositoryName,
                [| "-n" |],
                Required = false,
                Description = "The name of the repository. [default: current repository]",
                Arity = ArgumentArity.ExactlyOne
            )

        let listDirectories =
            new Option<bool>(
                OptionName.ListDirectories,
                Required = false,
                Description = "Show a list of directories in the Grace Index. [default: true]",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> true)
            )

        let listFiles =
            new Option<bool>(
                OptionName.ListFiles,
                Required = false,
                Description = $"Show a list of files in the Grace Index. Implies {OptionName.ListDirectories}. [default: true]",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> true)
            )

        let path =
            new Option<string>(
                "path",
                Required = false,
                Description = "The relative path to list. Wildcards ? and * are permitted. [default: *.*]",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> "*.*")
            )

    let private updateIndexHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                if parseResult |> hasOutput then
                    let! graceStatus =
                        progress
                            .Columns(progressColumns)
                            .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading existing Grace index file.[/]")

                                    let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Computing new Grace index file.[/]", autoStart = false)

                                    let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Writing new Grace index file.[/]", autoStart = false)

                                    let t3 = progressContext.AddTask($"[{Color.DodgerBlue1}]Ensure files are in the object cache.[/]", autoStart = false)

                                    let t4 = progressContext.AddTask($"[{Color.DodgerBlue1}]Ensure object cache index is up-to-date.[/]", autoStart = false)

                                    let t5 =
                                        progressContext.AddTask($"[{Color.DodgerBlue1}]Ensure files are uploaded to object storage.[/]", autoStart = false)

                                    let t6 =
                                        progressContext.AddTask(
                                            $"[{Color.DodgerBlue1}]Ensure directory versions are uploaded to Grace Server.[/]",
                                            autoStart = false
                                        )

                                    // Read the existing Grace status file.
                                    t0.Increment(0.0)
                                    let! previousGraceStatus = readGraceStatusFile ()
                                    t0.Increment(100.0)

                                    // Compute the new Grace status file, based on the contents of the working directory.
                                    t1.StartTask()

                                    let! graceStatus = createNewGraceStatusFile previousGraceStatus parseResult

                                    t1.Value <- 100.0

                                    // Write the new Grace status file to disk.
                                    t2.StartTask()
                                    do! writeGraceStatusFile graceStatus
                                    t2.Value <- 100.0

                                    // Ensure all files are in the object cache.
                                    t3.StartTask()
                                    let fileVersions = ConcurrentDictionary<RelativePath, LocalFileVersion>()

                                    // Loop through the local directory versions, and populate fileVersions with all of the files in the repo.
                                    let plr =
                                        Parallel.ForEach(
                                            graceStatus.Index.Values,
                                            Constants.ParallelOptions,
                                            (fun ldv ->
                                                for fileVersion in ldv.Files do
                                                    fileVersions.TryAdd(fileVersion.RelativePath, fileVersion) |> ignore)
                                        )

                                    let incrementAmount = 100.0 / double fileVersions.Count

                                    // Loop through the files, and copy them to the object cache if they don't already exist.
                                    let plr =
                                        Parallel.ForEach(
                                            fileVersions,
                                            Constants.ParallelOptions,
                                            (fun kvp _ ->
                                                let fileVersion = kvp.Value
                                                let fullObjectPath = fileVersion.FullObjectPath

                                                if not <| File.Exists(fullObjectPath) then
                                                    Directory.CreateDirectory(Path.GetDirectoryName(fullObjectPath)) |> ignore // If the directory already exists, this will do nothing.

                                                    File.Copy(Path.Combine(Current().RootDirectory, fileVersion.RelativePath), fullObjectPath)

                                                t3.Increment(incrementAmount))
                                        )

                                    t3.Value <- 100.0

                                    // Ensure the object cache index is up-to-date.
                                    t4.StartTask()
                                    let! objectCache = readGraceObjectCacheFile ()
                                    let incrementAmount = 100.0 / double graceStatus.Index.Count

                                    let plr =
                                        Parallel.ForEach(
                                            graceStatus.Index.Values,
                                            Constants.ParallelOptions,
                                            (fun localDirectoryVersion ->
                                                if not <| objectCache.Index.ContainsKey(localDirectoryVersion.DirectoryVersionId) then
                                                    objectCache.Index.AddOrUpdate(
                                                        localDirectoryVersion.DirectoryVersionId,
                                                        (fun _ -> localDirectoryVersion),
                                                        (fun _ _ -> localDirectoryVersion)
                                                    )
                                                    |> ignore

                                                t4.Increment(incrementAmount))
                                        )

                                    do! writeGraceObjectCacheFile objectCache
                                    t4.Value <- 100.0

                                    // Ensure all files are uploaded to object storage.
                                    t5.StartTask()
                                    let incrementAmount = 100.0 / double fileVersions.Count

                                    match Current().ObjectStorageProvider with
                                    | ObjectStorageProvider.Unknown -> ()
                                    | AzureBlobStorage ->
                                        if parseResult |> verbose then
                                            logToAnsiConsole Colors.Verbose "Uploading files to Azure Blob Storage."

                                        // Breaking the uploads into chunks allows us to interleave checking to see if files are already uploaded with actually uploading them when they haven't been.
                                        let chunkSize = 32
                                        let fileVersionGroups = fileVersions.Chunk(chunkSize)
                                        let succeeded = ConcurrentQueue<GraceReturnValue<string>>()
                                        let errors = ConcurrentQueue<GraceError>()

                                        // Loop through the groups of file versions, and upload files that aren't already in object storage.
                                        do!
                                            Parallel.ForEachAsync(
                                                fileVersionGroups,
                                                Constants.ParallelOptions,
                                                (fun fileVersions ct ->
                                                    ValueTask(
                                                        task {
                                                            let getUploadMetadataForFilesParameters =
                                                                GetUploadMetadataForFilesParameters(
                                                                    OwnerId = graceIds.OwnerIdString,
                                                                    OwnerName = graceIds.OwnerName,
                                                                    OrganizationId = graceIds.OrganizationIdString,
                                                                    OrganizationName = graceIds.OrganizationName,
                                                                    RepositoryId = graceIds.RepositoryIdString,
                                                                    RepositoryName = graceIds.RepositoryName,
                                                                    CorrelationId = getCorrelationId parseResult,
                                                                    FileVersions = fileVersions.Select(fun kvp -> kvp.Value.ToFileVersion).ToArray()
                                                                )

                                                            match! Storage.GetUploadMetadataForFiles getUploadMetadataForFilesParameters with
                                                            | Ok graceReturnValue ->
                                                                let uploadMetadata = graceReturnValue.ReturnValue
                                                                // Increment the counter for the files that we don't have to upload.
                                                                t5.Increment(incrementAmount * double (fileVersions.Count() - uploadMetadata.Count))

                                                                // Index all of the file versions by their SHA256 hash; we'll look up the files to upload with it.
                                                                let filesIndexedByRelativePath =
                                                                    Dictionary<RelativePath, LocalFileVersion>(
                                                                        fileVersions.Select(fun kvp -> KeyValuePair(kvp.Value.RelativePath, kvp.Value))
                                                                    )

                                                                // Upload the files in this chunk to object storage.
                                                                do!
                                                                    Parallel.ForEachAsync(
                                                                        uploadMetadata,
                                                                        Constants.ParallelOptions,
                                                                        (fun upload ct ->
                                                                            ValueTask(
                                                                                task {
                                                                                    let fileVersion =
                                                                                        filesIndexedByRelativePath[upload.RelativePath].ToFileVersion

                                                                                    let! result =
                                                                                        Storage.SaveFileToObjectStorage
                                                                                            (Current().RepositoryId)
                                                                                            fileVersion
                                                                                            (upload.BlobUriWithSasToken)
                                                                                            (getCorrelationId parseResult)

                                                                                    // Increment the counter for each file that we do upload.
                                                                                    t5.Increment(incrementAmount)

                                                                                    match result with
                                                                                    | Ok result -> succeeded.Enqueue(result)
                                                                                    | Error error -> errors.Enqueue(error)
                                                                                }
                                                                            ))
                                                                    )

                                                            | Error error -> AnsiConsole.Write((new Panel($"{error}")).BorderColor(Color.Red3))
                                                        }
                                                    ))
                                            )

                                        if errors |> Seq.isEmpty then
                                            if parseResult |> verbose then
                                                logToAnsiConsole Colors.Verbose "All files uploaded successfully."

                                            ()
                                        else
                                            AnsiConsole.MarkupLine($"{errors.Count} errors occurred while uploading files to object storage.")

                                            let mutable error = GraceError.Create String.Empty String.Empty

                                            while not <| errors.IsEmpty do
                                                if errors.TryDequeue(&error) then
                                                    AnsiConsole.MarkupLine($"[{Colors.Error}]{error.Error.EscapeMarkup()}[/]")
                                    | AWSS3 -> ()
                                    | GoogleCloudStorage -> ()

                                    t5.Value <- 100.0

                                    // Ensure all directory versions are uploaded to Grace Server.
                                    t6.StartTask()

                                    if parseResult |> verbose then
                                        logToAnsiConsole Colors.Verbose "Uploading new directory versions to the server."

                                    let chunkSize = 16
                                    let succeeded = ConcurrentQueue<GraceReturnValue<string>>()
                                    let errors = ConcurrentQueue<GraceError>()
                                    let incrementAmount = 100.0 / double graceStatus.Index.Count

                                    // We'll segment the uploads by the number of segments in the path,
                                    //   so we process the deepest paths first, and the new children exist before the parent is created.
                                    //   Within each segment group, we'll parallelize the processing for performance.
                                    let segmentGroups =
                                        graceStatus.Index.Values
                                            .GroupBy(fun dv -> countSegments dv.RelativePath)
                                            .OrderByDescending(fun group -> group.Key)

                                    for group in segmentGroups do
                                        let directoryVersionGroups = group.Chunk(chunkSize)
                                        let parallelOptions = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = 3)

                                        do!
                                            Parallel.ForEachAsync(
                                                directoryVersionGroups,
                                                parallelOptions,
                                                (fun (directoryVersionGroup: LocalDirectoryVersion array) (ct: CancellationToken) ->
                                                    ValueTask(
                                                        task {
                                                            let saveDirectoryVersionsParameters = SaveDirectoryVersionsParameters()
                                                            saveDirectoryVersionsParameters.OwnerId <- graceIds.OwnerIdString
                                                            saveDirectoryVersionsParameters.OwnerName <- graceIds.OwnerName
                                                            saveDirectoryVersionsParameters.OrganizationId <- graceIds.OrganizationIdString
                                                            saveDirectoryVersionsParameters.OrganizationName <- graceIds.OrganizationName
                                                            saveDirectoryVersionsParameters.RepositoryId <- graceIds.RepositoryIdString
                                                            saveDirectoryVersionsParameters.RepositoryName <- graceIds.RepositoryName
                                                            saveDirectoryVersionsParameters.CorrelationId <- getCorrelationId parseResult

                                                            saveDirectoryVersionsParameters.DirectoryVersions <-
                                                                directoryVersionGroup.Select(fun dv -> dv.ToDirectoryVersion).ToList()

                                                            match! DirectoryVersion.SaveDirectoryVersions saveDirectoryVersionsParameters with
                                                            | Ok result -> succeeded.Enqueue(result)
                                                            | Error error -> errors.Enqueue(error)

                                                            t6.Increment(incrementAmount * double directoryVersionGroup.Length)
                                                        }
                                                    ))
                                            )

                                    t6.Value <- 100.0

                                    AnsiConsole.MarkupLine($"[{Colors.Important}]succeeded: {succeeded.Count}; errors: {errors.Count}.[/]")

                                    let mutable error = GraceError.Create String.Empty String.Empty

                                    while not <| errors.IsEmpty do
                                        errors.TryDequeue(&error) |> ignore

                                        if error.Error.Contains("TRetval") then logToConsole $"********* {error.Error}"

                                        AnsiConsole.MarkupLine($"[{Colors.Error}]{error.Error.EscapeMarkup()}[/]")

                                    return graceStatus
                                })

                    let fileCount =
                        graceStatus.Index.Values
                            .Select(fun directoryVersion -> directoryVersion.Files.Count)
                            .Sum()

                    let totalFileSize = graceStatus.Index.Values.Sum(fun directoryVersion -> directoryVersion.Files.Sum(fun f -> int64 f.Size))

                    let rootDirectoryVersion = graceStatus.Index.Values.First(fun d -> d.RelativePath = Constants.RootDirectoryPath)

                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of directories scanned: {graceStatus.Index.Count}.[/]")

                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of files scanned: {fileCount}; total file size: {totalFileSize:N0}.[/]")

                    AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Root SHA-256 hash: {rootDirectoryVersion.Sha256Hash.Substring(0, 8)}[/]"
                    return 0
                else
                    let! previousGraceStatus = readGraceStatusFile ()
                    let! graceStatus = createNewGraceStatusFile previousGraceStatus parseResult
                    do! writeGraceStatusFile graceStatus

                    let fileVersions = ConcurrentDictionary<RelativePath, LocalFileVersion>()

                    let plr =
                        Parallel.ForEach(
                            graceStatus.Index.Values,
                            Constants.ParallelOptions,
                            (fun ldv ->
                                for fileVersion in ldv.Files do
                                    fileVersions.TryAdd(fileVersion.RelativePath, fileVersion) |> ignore)
                        )

                    let plr =
                        Parallel.ForEach(
                            fileVersions,
                            Constants.ParallelOptions,
                            (fun kvp _ ->
                                let fileVersion = kvp.Value
                                let fullObjectPath = fileVersion.FullObjectPath

                                if not <| File.Exists(fullObjectPath) then
                                    Directory.CreateDirectory(Path.GetDirectoryName(fullObjectPath)) |> ignore

                                    File.Copy(Path.Combine(Current().RootDirectory, fileVersion.RelativePath), fullObjectPath))
                        )

                    match Current().ObjectStorageProvider with
                    | ObjectStorageProvider.Unknown -> return -1
                    | AzureBlobStorage ->
                        let chunkSize = 32
                        let fileVersionGroups = fileVersions.Chunk(chunkSize)
                        let succeeded = ConcurrentQueue<GraceReturnValue<string>>()
                        let errors = ConcurrentQueue<GraceError>()

                        do!
                            Parallel.ForEachAsync(
                                fileVersionGroups,
                                Constants.ParallelOptions,
                                (fun fileVersions ct ->
                                    ValueTask(
                                        task {
                                            let getUploadMetadataForFilesParameters =
                                                GetUploadMetadataForFilesParameters(
                                                    OwnerId = graceIds.OwnerIdString,
                                                    OwnerName = graceIds.OwnerName,
                                                    OrganizationId = graceIds.OrganizationIdString,
                                                    OrganizationName = graceIds.OrganizationName,
                                                    RepositoryId = graceIds.RepositoryIdString,
                                                    RepositoryName = graceIds.RepositoryName,
                                                    CorrelationId = getCorrelationId parseResult,
                                                    FileVersions = (fileVersions |> Seq.map (fun kvp -> kvp.Value.ToFileVersion) |> Seq.toArray)
                                                )

                                            match! Storage.GetUploadMetadataForFiles getUploadMetadataForFilesParameters with
                                            | Ok graceReturnValue ->
                                                let uploadMetadata = graceReturnValue.ReturnValue

                                                let filesIndexedBySha256Hash =
                                                    Dictionary<Sha256Hash, LocalFileVersion>(
                                                        fileVersions.Select(fun kvp -> KeyValuePair(kvp.Value.Sha256Hash, kvp.Value))
                                                    )

                                                do!
                                                    Parallel.ForEachAsync(
                                                        uploadMetadata,
                                                        Constants.ParallelOptions,
                                                        (fun upload ct ->
                                                            ValueTask(
                                                                task {
                                                                    let fileVersion = filesIndexedBySha256Hash[upload.Sha256Hash].ToFileVersion

                                                                    let! result =
                                                                        Storage.SaveFileToObjectStorage
                                                                            (Current().RepositoryId)
                                                                            fileVersion
                                                                            (upload.BlobUriWithSasToken)
                                                                            (getCorrelationId parseResult)

                                                                    match result with
                                                                    | Ok result -> succeeded.Enqueue(result)
                                                                    | Error error -> errors.Enqueue(error)
                                                                }
                                                            ))
                                                    )
                                            | Error error -> AnsiConsole.MarkupLine($"[{Colors.Error}]{error}[/]")
                                        }
                                    ))
                            )

                        if errors |> Seq.isEmpty then
                            return 0
                        else
                            AnsiConsole.MarkupLine($"{errors.Count} errors occurred.")
                            let mutable error = GraceError.Create String.Empty String.Empty

                            while not <| errors.IsEmpty do
                                if errors.TryDequeue(&error) then
                                    AnsiConsole.MarkupLine($"[{Colors.Error}]{error.Error.EscapeMarkup()}[/]")

                            return -1

                    | AWSS3 -> return 0
                    | GoogleCloudStorage -> return 0

            with ex ->
                logToAnsiConsole Colors.Error $"Exception in UpdateIndex: {ExceptionResponse.Create ex}"
                return -1
        }

    type UpdateIndex() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task { return! updateIndexHandler parseResult }

    type Scan() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                if parseResult |> verbose then printParseResult parseResult

                if parseResult |> hasOutput then
                    let! (differences, newDirectoryVersions) =
                        progress
                            .Columns(progressColumns)
                            .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading Grace index file.[/]")

                                    let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Scanning working directory for changes.[/]", autoStart = false)

                                    let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Computing root directory SHA-256 value.[/]", autoStart = false)

                                    t0.Increment(0.0)
                                    let! previousGraceStatus = readGraceStatusFile ()
                                    t0.Increment(100.0)
                                    t1.StartTask()
                                    t1.Increment(0.0)
                                    let! differences = scanForDifferences previousGraceStatus
                                    t1.Increment(100.0)
                                    t2.StartTask()
                                    t2.Increment(0.0)

                                    let! (newGraceIndex, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions previousGraceStatus differences

                                    t2.Increment(100.0)
                                    return (differences, newDirectoryVersions)
                                })

                    AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Number of differences: {differences.Count}[/]"

                    for difference in differences do
                        let x = sprintf "%A" difference
                        AnsiConsole.MarkupLine $"[{Colors.Important}]{x}[/]"

                    AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Number of new DirectoryVersions: {newDirectoryVersions.Count}[/]"

                    for ldv in newDirectoryVersions do
                        AnsiConsole.MarkupLine
                            $"[{Colors.Important}]SHA-256: {ldv.Sha256Hash.Substring(0, 8)}; DirectoryId: {ldv.DirectoryVersionId}; RelativePath: {ldv.RelativePath}[/]"
                //AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Root SHA-256 hash: {rootDirectoryVersion.Sha256Hash.Substring(8)}[/]"
                else
                    let! previousGraceStatus = readGraceStatusFile ()
                    let! differences = scanForDifferences previousGraceStatus

                    let! (newGraceIndex, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions previousGraceStatus differences

                    AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Number of differences: {differences.Count}[/]"

                    for difference in differences do
                        let x = sprintf "%A" difference
                        AnsiConsole.MarkupLine $"[{Colors.Important}]{x}[/]"

                    AnsiConsole.MarkupLine $"[{Colors.Highlighted}]newDirectoryVersions.Count: {newDirectoryVersions.Count}[/]"

                    for ldv in newDirectoryVersions do
                        AnsiConsole.MarkupLine
                            $"[{Colors.Important}]SHA-256: {ldv.Sha256Hash.Substring(0, 8)}; DirectoryId: {ldv.DirectoryVersionId}; RelativePath: {ldv.RelativePath}[/]"

                return 0
            }

    type Stats() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                if parseResult |> verbose then printParseResult parseResult

                let! graceStatus = readGraceStatusFile ()
                let directoryCount = graceStatus.Index.Count

                let fileCount =
                    graceStatus.Index.Values
                        .Select(fun directoryVersion -> directoryVersion.Files.Count)
                        .Sum()

                let totalFileSize = graceStatus.Index.Values.Sum(fun directoryVersion -> directoryVersion.Files.Sum(fun f -> int64 f.Size))

                let rootDirectoryVersion = graceStatus.Index.Values.First(fun d -> d.RelativePath = Constants.RootDirectoryPath)

                AnsiConsole.MarkupLine($"[{Colors.Important}]All values taken from the local Grace status file.[/]")
                AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of directories: {directoryCount}.[/]")

                AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of files: {fileCount}; total file size: {totalFileSize:N0}.[/]")

                AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Root SHA-256 hash: {rootDirectoryVersion.Sha256Hash.Substring(0, 8)}[/]")

                return 0
            }

    type ListContents() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                if parseResult |> verbose then printParseResult parseResult

                let listDirectories = parseResult.GetValue(Options.listDirectories)
                let listFiles = parseResult.GetValue(Options.listFiles)

                let! graceStatus = readGraceStatusFile ()
                let directoryCount = graceStatus.Index.Count

                let fileCount =
                    graceStatus.Index.Values
                        .Select(fun directoryVersion -> directoryVersion.Files.Count)
                        .Sum()

                let totalFileSize = graceStatus.Index.Values.Sum(fun directoryVersion -> directoryVersion.Files.Sum(fun f -> int64 f.Size))

                let rootDirectoryVersion = graceStatus.Index.Values.First(fun d -> d.RelativePath = Constants.RootDirectoryPath)

                AnsiConsole.MarkupLine($"[{Colors.Important}]All values taken from the local Grace status file.[/]")
                AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of directories: {directoryCount}.[/]")

                AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of files: {fileCount}; total file size: {totalFileSize:N0}.[/]")

                AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Root SHA-256 hash: {rootDirectoryVersion.Sha256Hash.Substring(0, 8)}[/]")

                if listDirectories then
                    let longestRelativePath = getLongestRelativePath graceStatus.Index.Values
                    let additionalSpaces = String.replicate (longestRelativePath - 2) " "
                    let additionalImportantDashes = String.replicate (longestRelativePath + 3) "-"
                    let additionalDeemphasizedDashes = String.replicate 38 "-"

                    let sortedDirectoryVersions = graceStatus.Index.Values.OrderBy(fun dv -> dv.RelativePath)

                    sortedDirectoryVersions
                    |> Seq.iteri (fun i directoryVersion ->
                        AnsiConsole.WriteLine()

                        if i = 0 then
                            AnsiConsole.MarkupLine(
                                $"[{Colors.Important}]Last Write Time (UTC)        SHA-256            Size  Path{additionalSpaces}[/][{Colors.Deemphasized}] (DirectoryVersionId)[/]"
                            )

                            AnsiConsole.MarkupLine(
                                $"[{Colors.Important}]-----------------------------------------------------{additionalImportantDashes}[/][{Colors.Deemphasized}] {additionalDeemphasizedDashes}[/]"
                            )

                        let rightAlignedDirectoryVersionId =
                            (String.replicate (longestRelativePath - directoryVersion.RelativePath.Length) " ")
                            + $"({directoryVersion.DirectoryVersionId})"

                        AnsiConsole.MarkupLine(
                            $"[{Colors.Highlighted}]{formatDateTimeAligned directoryVersion.LastWriteTimeUtc}   {getShortSha256Hash directoryVersion.Sha256Hash}  {directoryVersion.Size, 13:N0}  /{directoryVersion.RelativePath}[/] [{Colors.Deemphasized}] {rightAlignedDirectoryVersionId}[/]"
                        )

                        if listFiles then
                            let sortedFiles = directoryVersion.Files.OrderBy(fun f -> f.RelativePath)

                            for file in sortedFiles do
                                AnsiConsole.MarkupLine(
                                    $"[{Colors.Verbose}]{formatDateTimeAligned file.LastWriteTimeUtc}   {getShortSha256Hash file.Sha256Hash}  {file.Size, 13:N0}  |- {file.FileInfo.Name}[/]"
                                ))

                return 0
            }

    let verify<'T> (label: string) =
        try
            let _ = messagePackSerializerOptions.Resolver.GetFormatterWithVerify<'T>()
            printfn "%s: formatter OK" label
        with ex ->
            printfn "%s: formatter MISSING\n%s" label (ex.ToString())

    type CheckIgnoreEntries() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                if parseResult |> verbose then printParseResult parseResult
                AnsiConsole.MarkupLine($"[{Colors.Important}]Directory ignore entries:[/]")

                for dir in Current().GraceDirectoryIgnoreEntries do
                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]  {dir}[/]")

                AnsiConsole.WriteLine()
                AnsiConsole.MarkupLine($"[{Colors.Important}]File ignore entries:[/]")

                for file in Current().GraceFileIgnoreEntries do
                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]  {file}[/]")

                return 0
            }

    type Test() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {

                // Probe the usual suspects
                verify<Instant> "NodaTime.Instant"
                verify<Uri> "System.Uri"
                verify<byte[]> "byte[]"
                verify<ReadOnlyMemory<byte>> "ReadOnlyMemory<byte>"
                verify<Guid> "Guid"

                // Your concrete types (adjust names if needed)
                verify<Grace.Types.Types.DirectoryVersion> "DirectoryVersion"
                verify<Grace.Types.Types.FileVersion> "FileVersion"
                verify<Grace.Types.Types.LocalDirectoryVersion> "LocalDirectoryVersion"
                verify<Grace.Types.Types.LocalFileVersion> "LocalFileVersion"

                // Also probe the generic containers which appear in your model
                verify<List<Grace.Types.Types.DirectoryVersion>> "List<DirectoryVersion>"
                verify<Grace.Types.Types.DirectoryVersion[]> "DirectoryVersion[]"
                return 0
            }

    let Build =
        let addCommonOptions (command: Command) =
            command
            |> addOption Options.ownerName
            |> addOption Options.ownerId
            |> addOption Options.organizationName
            |> addOption Options.organizationId
            |> addOption Options.repositoryId
            |> addOption Options.repositoryName

        let maintenanceCommand = new Command("maintenance", Description = "Performs various maintenance tasks.")

        maintenanceCommand.Aliases.Add("maint")

        let updateIndexCommand =
            new Command("update-index", Description = "Recreates the local Grace index file based on the current working directory contents.")
            |> addCommonOptions

        updateIndexCommand.Action <- new UpdateIndex()
        maintenanceCommand.Subcommands.Add(updateIndexCommand)

        let scanCommand =
            new Command("scan", Description = "Scans the working directory contents for changes.")
            |> addCommonOptions

        scanCommand.Action <- new Scan()
        maintenanceCommand.Subcommands.Add(scanCommand)

        let statsCommand =
            new Command("stats", Description = "Displays statistics about the current working directory.")
            |> addCommonOptions

        statsCommand.Action <- new Stats()
        maintenanceCommand.Subcommands.Add(statsCommand)

        let listContentsCommand =
            new Command("list-contents", Description = "List directories and files from the Grace Status file.")
            |> addCommonOptions
            |> addOption Options.listDirectories
            |> addOption Options.listFiles

        listContentsCommand.Action <- new ListContents()
        maintenanceCommand.Subcommands.Add(listContentsCommand)

        let checkIgnoreEntriesCommand =
            new Command("check-ignore-entries", Description = "Check the .graceignore entries for validity.")
            |> addCommonOptions

        checkIgnoreEntriesCommand.Action <- new CheckIgnoreEntries()
        maintenanceCommand.Subcommands.Add(checkIgnoreEntriesCommand)

        let testCommand = new Command("test", Description = "Just a test.") |> addCommonOptions
        testCommand.Action <- new Test()
        maintenanceCommand.Subcommands.Add(testCommand)

        maintenanceCommand
