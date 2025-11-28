namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Common.Validations
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Parameters
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.DirectoryVersion
open Grace.Shared.Parameters.Repository
open Grace.Shared.Parameters.Storage
open Grace.Shared.Validation.Errors
open NodaTime
open Spectre.Console
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Linq
open System.IO
open System.Threading
open System.Threading.Tasks
open Spectre.Console.Json

module Repository =

    module private Options =
        let ownerId =
            new Option<OwnerId>(
                OptionName.OwnerId,
                Required = false,
                Description = "The repository's owner ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> if Current().OwnerId = Guid.Empty then Guid.NewGuid() else Current().OwnerId)
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
                DefaultValueFactory =
                    (fun _ ->
                        if Current().OrganizationId = Guid.Empty then
                            Guid.NewGuid()
                        else
                            Current().OrganizationId)
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
                DefaultValueFactory =
                    (fun _ ->
                        if Current().RepositoryId = Guid.Empty then
                            Guid.NewGuid()
                        else
                            Current().RepositoryId)
            )

        let repositoryName =
            new Option<String>(
                OptionName.RepositoryName,
                [| "-n" |],
                Required = false,
                Description = "The name of the repository. [default: current repository]",
                Arity = ArgumentArity.ExactlyOne
            )

        let requiredRepositoryName =
            new Option<String>(
                OptionName.RepositoryName,
                [| "-n" |],
                Required = true,
                Description = "The name of the repository.",
                Arity = ArgumentArity.ExactlyOne
            )

        let description =
            new Option<String>(OptionName.Description, Required = false, Description = "The description of the repository.", Arity = ArgumentArity.ExactlyOne)

        let visibility =
            (new Option<RepositoryType>(
                OptionName.Visibility,
                Required = true,
                Description = "The visibility of the repository.",
                Arity = ArgumentArity.ExactlyOne
            ))
                .AcceptOnlyFromAmong(listCases<RepositoryType> ())

        let status =
            (new Option<String>(OptionName.Status, Required = true, Description = "The status of the repository.", Arity = ArgumentArity.ExactlyOne))
                .AcceptOnlyFromAmong(listCases<RepositoryStatus> ())

        let recordSaves =
            new Option<bool>(
                OptionName.RecordSaves,
                Required = true,
                Description = "True to record all saves; false to turn it off.",
                Arity = ArgumentArity.ExactlyOne
            )

        let defaultServerApiVersion =
            (new Option<String>(
                OptionName.DefaultServerApiVersion,
                Required = true,
                Description = "The default version of the server API that clients should use when accessing this repository.",
                Arity = ArgumentArity.ExactlyOne
            ))
                .AcceptOnlyFromAmong(listCases<Constants.ServerApiVersions> ())

        let saveDays =
            new Option<single>(
                OptionName.SaveDays,
                Required = true,
                Description = "How many days to keep saves. [default: 7.0]",
                Arity = ArgumentArity.ExactlyOne
            )

        let checkpointDays =
            new Option<single>(
                OptionName.CheckpointDays,
                Required = true,
                Description = "How many days to keep checkpoints. [default: 365.0]",
                Arity = ArgumentArity.ExactlyOne
            )

        let diffCacheDays =
            new Option<single>(
                OptionName.DiffCacheDays,
                Required = true,
                Description = "How many days to keep diff results cached in the database. [default: 3.0]",
                Arity = ArgumentArity.ExactlyOne
            )

        let directoryVersionCacheDays =
            new Option<single>(
                OptionName.DirectoryVersionCacheDays,
                Required = true,
                Description = "How many days to keep recursive directory version contents cached. [default: 3.0]",
                Arity = ArgumentArity.ExactlyOne
            )

        let logicalDeleteDays =
            new Option<single>(
                OptionName.LogicalDeleteDays,
                Required = true,
                Description = "How many days to keep deleted branches before permanently deleting them. [default: 30.0]",
                Arity = ArgumentArity.ExactlyOne
            )

        let newName =
            new Option<String>(OptionName.NewName, Required = true, Description = "The new name for the repository.", Arity = ArgumentArity.ExactlyOne)

        let deleteReason =
            new Option<String>(
                OptionName.DeleteReason,
                Required = true,
                Description = "The reason for deleting the repository.",
                Arity = ArgumentArity.ExactlyOne
            )

        let graceConfig =
            new Option<String>(
                OptionName.GraceConfig,
                Required = false,
                Description = "The path of a Grace config file that you'd like to use instead of the default graceconfig.json.",
                Arity = ArgumentArity.ExactlyOne
            )

        let force =
            new Option<bool>(
                OptionName.Force,
                Required = false,
                Description = "Deletes repository even if there are links to other repositories.",
                Arity = ArgumentArity.ExactlyOne
            )

        let doNotSwitch =
            new Option<bool>(
                OptionName.DoNotSwitch,
                Required = false,
                Description =
                    "Do not switch your current repository to the new repository after it is created. By default, the new repository becomes the current repository.",
                Arity = ArgumentArity.ZeroOrOne
            )

        let directory =
            new Option<String>(
                OptionName.Directory,
                Required = false,
                Description = "The directory to use when initializing the repository. [default: current directory]",
                Arity = ArgumentArity.ExactlyOne
            )

        let includeDeleted =
            new Option<bool>(
                OptionName.IncludeDeleted,
                [| "-d" |],
                Required = false,
                Description = "Include deleted branches in the result.",
                DefaultValueFactory = (fun _ -> false)
            )

        let anonymousAccess =
            new Option<bool>(
                OptionName.AnonymousAccess,
                Required = true,
                Description = "Enable or disable anonymous access for the repository.",
                Arity = ArgumentArity.ExactlyOne
            )

        let allowsLargeFiles =
            new Option<bool>(
                OptionName.AllowsLargeFiles,
                Required = true,
                Description = "Enable or disable large file support for the repository.",
                Arity = ArgumentArity.ExactlyOne
            )

        let policyType =
            new Option<String>(
                "--policy-type",
                Required = true,
                Description = "The conflict resolution policy type: NoConflicts or ConflictsAllowedWithConfidence.",
                Arity = ArgumentArity.ExactlyOne
            )

        let confidenceThreshold =
            new Option<float>(
                "--confidence-threshold",
                Required = false,
                Description = "The confidence threshold for auto-accepting conflict resolutions (0.0 to 1.0). Required when policy is ConflictsAllowedWithConfidence.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> 0.0)
            )

    // Create subcommand.
    type Create() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    // In a Create() command, if --repository-id is implicit, that's actually the old RepositoryId taken from graceconfig.json,
                    //   and we need to set RepositoryId to a new Guid.
                    let mutable graceIds = parseResult |> getNormalizedIdsAndNames

                    if parseResult.GetResult(Options.ownerId).Implicit then
                        let repositoryId = Guid.NewGuid()
                        graceIds <- { graceIds with RepositoryId = repositoryId; RepositoryIdString = $"{repositoryId}" }

                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let repositoryIdOption = parseResult.GetResult(Options.repositoryId)

                        let repositoryId =
                            if isNull repositoryIdOption || repositoryIdOption.Implicit then
                                Guid.NewGuid().ToString()
                            else
                                graceIds.RepositoryIdString

                        let ownerId = if graceIds.HasOwner then graceIds.OwnerIdString else $"{Current().OwnerId}"

                        let organizationId =
                            if graceIds.HasOrganization then
                                graceIds.OrganizationIdString
                            else
                                $"{Current().OrganizationId}"

                        let parameters =
                            Repository.CreateRepositoryParameters(
                                RepositoryId = repositoryId,
                                RepositoryName = graceIds.RepositoryName,
                                OwnerId = ownerId,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = organizationId,
                                OrganizationName = graceIds.OrganizationName,
                                CorrelationId = getCorrelationId parseResult
                            )

                        if parseResult |> hasOutput then
                            let! result =
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! result = Repository.Create(parameters)
                                            t0.Increment(100.0)
                                            return result
                                        })

                            match result with
                            | Ok returnValue ->
                                if not <| parseResult.GetValue(Options.doNotSwitch) then
                                    let newConfig = Current()
                                    newConfig.RepositoryId <- Guid.Parse($"{returnValue.Properties[nameof RepositoryId]}")
                                    newConfig.RepositoryName <- $"{returnValue.Properties[nameof RepositoryName]}"
                                    newConfig.BranchId <- Guid.Parse($"{returnValue.Properties[nameof BranchId]}")
                                    newConfig.BranchName <- $"{returnValue.Properties[nameof BranchName]}"
                                    newConfig.DefaultBranchName <- "main"
                                    newConfig.ObjectStorageProvider <- ObjectStorageProvider.AzureBlobStorage
                                    updateConfiguration newConfig

                                return result |> renderOutput parseResult
                            | Error error ->
                                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                                return result |> renderOutput parseResult
                        else
                            let! result = Repository.Create(parameters)

                            match result with
                            | Ok returnValue ->
                                if not <| parseResult.GetValue(Options.doNotSwitch) then
                                    let newConfig = Current()
                                    newConfig.RepositoryId <- Guid.Parse($"{returnValue.Properties[nameof RepositoryId]}")
                                    newConfig.RepositoryName <- $"{returnValue.Properties[nameof RepositoryName]}"
                                    newConfig.BranchId <- Guid.Parse($"{returnValue.Properties[nameof BranchId]}")
                                    newConfig.BranchName <- $"{returnValue.Properties[nameof BranchName]}"
                                    newConfig.DefaultBranchName <- "main"
                                    newConfig.ObjectStorageProvider <- ObjectStorageProvider.AzureBlobStorage
                                    updateConfiguration newConfig

                                return result |> renderOutput parseResult
                            | Error error ->
                                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                                return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    // Init subcommand
    /// Validates that the directory exists. Placed here so it can use InitParameters.
    let ``Directory must be a valid path`` (parseResult: ParseResult) =
        if
            parseResult.CommandResult.Command.Options.Contains(Options.directory)
            && not <| Directory.Exists(parseResult.GetValue(Options.directory))
        then
            Error(GraceError.Create (RepositoryError.getErrorMessage InvalidDirectory) (getCorrelationId parseResult))
        else
            Ok parseResult

    let private initHandler (parseResult: ParseResult) (parameters: InitParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let directoryIsValid = parseResult |> ``Directory must be a valid path``

                match directoryIsValid with
                | Ok _ ->
                    let validateIncomingParameters = parseResult |> CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let graceIds = parseResult |> getNormalizedIdsAndNames

                        let isEmptyParameters =
                            Repository.IsEmptyParameters(
                                OwnerId = parameters.OwnerId,
                                OwnerName = parameters.OwnerName,
                                OrganizationId = parameters.OrganizationId,
                                OrganizationName = parameters.OrganizationName,
                                RepositoryId = parameters.RepositoryId,
                                RepositoryName = parameters.RepositoryName,
                                CorrelationId = parameters.CorrelationId
                            )

                        let! repositoryIsEmpty = Repository.IsEmpty isEmptyParameters

                        match repositoryIsEmpty with
                        | Ok isEmpty ->
                            if isEmpty.ReturnValue = true then
                                let repositoryId = RepositoryId.Parse(parameters.RepositoryId)

                                if parseResult |> hasOutput then
                                    let! graceStatus =
                                        progress
                                            .Columns(progressColumns)
                                            .StartAsync(fun progressContext ->
                                                task {
                                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading existing Grace index file.[/]")

                                                    let t1 =
                                                        progressContext.AddTask($"[{Color.DodgerBlue1}]Computing new Grace index file.[/]", autoStart = false)

                                                    let t2 =
                                                        progressContext.AddTask($"[{Color.DodgerBlue1}]Writing new Grace index file.[/]", autoStart = false)

                                                    let t3 =
                                                        progressContext.AddTask(
                                                            $"[{Color.DodgerBlue1}]Ensure files are in the object cache.[/]",
                                                            autoStart = false
                                                        )

                                                    let t4 =
                                                        progressContext.AddTask(
                                                            $"[{Color.DodgerBlue1}]Ensure object cache index is up-to-date.[/]",
                                                            autoStart = false
                                                        )

                                                    let t5 =
                                                        progressContext.AddTask(
                                                            $"[{Color.DodgerBlue1}]Ensure files are uploaded to object storage.[/]",
                                                            autoStart = false
                                                        )

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
                                                            (fun ldv ->
                                                                if not <| objectCache.Index.ContainsKey(ldv.DirectoryVersionId) then
                                                                    objectCache.Index.AddOrUpdate(ldv.DirectoryVersionId, (fun _ -> ldv), (fun _ _ -> ldv))
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
                                                        // Breaking the uploads into chunks allows us to interleave checking to see if files are already uploaded with actually uploading them when they don't.
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
                                                                                    OwnerId = parameters.OwnerId,
                                                                                    OwnerName = parameters.OwnerName,
                                                                                    OrganizationId = parameters.OrganizationId,
                                                                                    OrganizationName = parameters.OrganizationName,
                                                                                    RepositoryId = parameters.RepositoryId,
                                                                                    RepositoryName = parameters.RepositoryName,
                                                                                    CorrelationId = getCorrelationId parseResult,
                                                                                    FileVersions =
                                                                                        (fileVersions
                                                                                         |> Seq.map (fun kvp -> kvp.Value.ToFileVersion)
                                                                                         |> Seq.toArray)
                                                                                )

                                                                            let! graceResult =
                                                                                Storage.GetUploadMetadataForFiles getUploadMetadataForFilesParameters

                                                                            match graceResult with
                                                                            | Ok graceReturnValue ->
                                                                                let uploadMetadata = graceReturnValue.ReturnValue
                                                                                // Increment the counter for the files that we don't have to upload.
                                                                                t5.Increment(
                                                                                    incrementAmount * double (fileVersions.Count() - uploadMetadata.Count)
                                                                                )

                                                                                // Index all of the file versions by their SHA256 hash; we'll look up the files to upload with it.
                                                                                let filesIndexedBySha256Hash =
                                                                                    Dictionary<Sha256Hash, LocalFileVersion>(
                                                                                        fileVersions.Select(fun kvp ->
                                                                                            KeyValuePair(kvp.Value.Sha256Hash, kvp.Value))
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
                                                                                                        filesIndexedBySha256Hash[upload.Sha256Hash]
                                                                                                            .ToFileVersion

                                                                                                    let! result =
                                                                                                        Storage.SaveFileToObjectStorage
                                                                                                            repositoryId
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

                                                                            | Error error ->
                                                                                AnsiConsole.Write((new Panel($"{error}")).BorderColor(Color.Red3))
                                                                        }
                                                                    ))
                                                            )

                                                        // Print out any errors that occurred.
                                                        if errors |> Seq.isEmpty then
                                                            ()
                                                        else
                                                            AnsiConsole.MarkupLine($"{errors.Count} errors occurred.")

                                                            let mutable error = GraceError.Create String.Empty String.Empty

                                                            while not <| errors.IsEmpty do
                                                                if errors.TryDequeue(&error) then
                                                                    AnsiConsole.MarkupLine($"[{Colors.Error}]{error.Error.EscapeMarkup()}[/]")

                                                            ()
                                                    | AWSS3 -> ()
                                                    | GoogleCloudStorage -> ()

                                                    t5.Value <- 100.0

                                                    // Ensure all directory versions are uploaded to Grace Server.
                                                    t6.StartTask()
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

                                                        do!
                                                            Parallel.ForEachAsync(
                                                                directoryVersionGroups,
                                                                Constants.ParallelOptions,
                                                                (fun directoryVersionGroup ct ->
                                                                    ValueTask(
                                                                        task {
                                                                            let saveParameters = SaveDirectoryVersionsParameters()
                                                                            saveParameters.OwnerId <- parameters.OwnerId
                                                                            saveParameters.OwnerName <- parameters.OwnerName
                                                                            saveParameters.OrganizationId <- parameters.OrganizationId
                                                                            saveParameters.OrganizationName <- parameters.OrganizationName
                                                                            saveParameters.RepositoryId <- parameters.RepositoryId
                                                                            saveParameters.RepositoryName <- parameters.RepositoryName
                                                                            saveParameters.CorrelationId <- getCorrelationId parseResult

                                                                            saveParameters.DirectoryVersions <-
                                                                                directoryVersionGroup.Select(fun dv -> dv.ToDirectoryVersion).ToList()

                                                                            let! sdvResult = DirectoryVersion.SaveDirectoryVersions saveParameters

                                                                            match sdvResult with
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

                                    AnsiConsole.MarkupLine(
                                        $"[{Colors.Highlighted}]Number of files scanned: {fileCount}; total file size: {totalFileSize:N0}.[/]"
                                    )

                                    AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Root SHA-256 hash: {rootDirectoryVersion.Sha256Hash.Substring(0, 8)}[/]"

                                    return Ok(GraceReturnValue.Create "Initialized repository." (parseResult |> getCorrelationId))
                                else
                                    // Do the whole thing with no output
                                    return Ok(GraceReturnValue.Create "Initialized repository." (parseResult |> getCorrelationId))
                            else
                                return
                                    Error(GraceError.Create (RepositoryError.getErrorMessage RepositoryIsAlreadyInitialized) (parseResult |> getCorrelationId))
                        | Error error -> return Error error
                    // Take functionality from grace maint update... most of it is already there.
                    // We need to double-check that we have the correct owner/organization/repository because we're
                    //   going to be uploading files to object storage placed in containers named after the owner/organization/repository.
                    // Test on small, medium, and large repositories.
                    // Test on repositories with multiple branches - should fail.
                    // Test on repositories with only initial branch and no references - should succeed.
                    // Test on repositories with only initial branch and references - should fail.
                    // Test on repositories with multiple branches and references - should fail.
                    | Error error -> return Error error
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type Init() =
        inherit AsynchronousCommandLineAction()

        override this.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let graceIds = parseResult |> getNormalizedIdsAndNames

                        // Build the InitParameters object from the parsed values so we can reuse existing handler logic.
                        let initParameters = InitParameters()
                        initParameters.OwnerId <- graceIds.OwnerIdString
                        initParameters.OwnerName <- graceIds.OwnerName
                        initParameters.OrganizationId <- graceIds.OrganizationIdString
                        initParameters.OrganizationName <- graceIds.OrganizationName
                        initParameters.RepositoryId <- graceIds.RepositoryIdString
                        initParameters.RepositoryName <- graceIds.RepositoryName
                        initParameters.GraceConfig <- parseResult.GetValue(Options.graceConfig)
                        initParameters.CorrelationId <- getCorrelationId parseResult

                        let! result = initHandler parseResult initParameters
                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }


    /// Repository.Get subcommand definition
    type Get() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Repository.GetRepositoryParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                            let! response = Repository.Get(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.Get(parameters)

                        match result with
                        | Ok graceReturnValue ->
                            let jsonText = JsonText(serialize graceReturnValue.ReturnValue)
                            AnsiConsole.Write(jsonText)
                            AnsiConsole.WriteLine()
                            return Ok graceReturnValue |> renderOutput parseResult
                        | Error graceError -> return Error graceError |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.GetBranches subcommand definition
    type GetBranches() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.GetBranchesParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                IncludeDeleted = parseResult.GetValue(Options.includeDeleted),
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Repository.GetBranches(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.GetBranches(parameters)

                        match result with
                        | Ok returnValue ->
                            let rendered = Ok returnValue |> renderOutput parseResult

                            if parseResult |> hasOutput then
                                let table = Table(Border = TableBorder.DoubleEdge)

                                table.AddColumns(
                                    [| TableColumn($"[{Colors.Important}]Branch name[/]")
                                       TableColumn($"[{Colors.Important}]Branch Id[/]")
                                       TableColumn($"[{Colors.Important}]SHA-256 hash[/]")
                                       TableColumn($"[{Colors.Important}]Based on latest promotion[/]")
                                       TableColumn($"[{Colors.Important}]Parent branch[/]")
                                       TableColumn($"[{Colors.Important}]When[/]", Alignment = Justify.Right)
                                       TableColumn($"[{Colors.Important}]Updated at[/]") |]
                                )
                                |> ignore

                                let allBranches = returnValue.ReturnValue

                                // Get the parent branch names and latest promotions for all branches
                                let parents =
                                    allBranches.Select(fun branch ->
                                        {| BranchId = branch.BranchId
                                           BranchName =
                                            if branch.ParentBranchId = Constants.DefaultParentBranchId then
                                                "root"
                                            else
                                                allBranches
                                                    .Where(fun br -> br.BranchId = branch.ParentBranchId)
                                                    .Select(fun br -> br.BranchName)
                                                    .First()
                                           LatestPromotion =
                                            if branch.ParentBranchId = Constants.DefaultParentBranchId then
                                                branch.LatestPromotion
                                            else
                                                allBranches
                                                    .Where(fun br -> br.BranchId = branch.ParentBranchId)
                                                    .Select(fun br -> br.LatestPromotion)
                                                    .First() |})

                                let branchesWithParentNames =
                                    allBranches
                                        .Join(
                                            parents,
                                            (fun branch -> branch.BranchId),
                                            (fun parent -> parent.BranchId),
                                            (fun branch parent ->
                                                {| BranchId = branch.BranchId
                                                   BranchName = branch.BranchName
                                                   Sha256Hash = branch.LatestReference.Sha256Hash
                                                   UpdatedAt = branch.UpdatedAt
                                                   Ago = ago branch.CreatedAt
                                                   ParentBranchName = parent.BranchName
                                                   BasedOnLatestPromotion = (branch.BasedOn.ReferenceId = parent.LatestPromotion.ReferenceId) |})
                                        )
                                        .OrderBy(fun branch -> branch.UpdatedAt)

                                for br in branchesWithParentNames do
                                    let updatedAt =
                                        match br.UpdatedAt with
                                        | Some t -> instantToLocalTime (t)
                                        | None -> String.Empty

                                    table.AddRow(
                                        br.BranchName,
                                        $"[{Colors.Deemphasized}]{br.BranchId}[/]",
                                        br.Sha256Hash |> getShortSha256Hash,
                                        (if br.BasedOnLatestPromotion then
                                             $"[{Colors.Added}]Yes[/]"
                                         else
                                             $"[{Colors.Important}]No[/]"),
                                        br.ParentBranchName,
                                        br.Ago,
                                        $"[{Colors.Deemphasized}]{updatedAt}[/]"
                                    )
                                    |> ignore

                                AnsiConsole.Write(table)

                            return rendered
                        | Error _ -> return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.SetVisibility subcommand definition
    type SetVisibility() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let visibilityParameters =
                            Repository.SetRepositoryVisibilityParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                Visibility = (parseResult.GetValue(Options.visibility)).ToString(),
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Repository.SetVisibility(visibilityParameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.SetVisibility(visibilityParameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.SetStatus subcommand definition
    type SetStatus() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.SetRepositoryStatusParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                Status = parseResult.GetValue(Options.status),
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Repository.SetStatus(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.SetStatus(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.SetRecordSaves subcommand definition
    type SetRecordSaves() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.RecordSavesParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                RecordSaves = parseResult.GetValue(Options.recordSaves),
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Repository.SetRecordSaves(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.SetRecordSaves(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.SetSaveDays subcommand definition
    type SetSaveDays() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.SetSaveDaysParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = getCorrelationId parseResult,
                                SaveDays = parseResult.GetValue(Options.saveDays)
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Repository.SetSaveDays(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.SetSaveDays(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.SetCheckpointDays subcommand definition
    type SetCheckpointDays() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.SetCheckpointDaysParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = getCorrelationId parseResult,
                                CheckpointDays = parseResult.GetValue(Options.checkpointDays)
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Repository.SetCheckpointDays(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.SetCheckpointDays(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.SetDiffCacheDays subcommand definition
    type SetDiffCacheDays() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.SetDiffCacheDaysParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = getCorrelationId parseResult,
                                DiffCacheDays = parseResult.GetValue(Options.diffCacheDays)
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Repository.SetDiffCacheDays(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.SetDiffCacheDays(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.SetDirectoryVersionCacheDays subcommand definition
    type SetDirectoryVersionCacheDays() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.SetDirectoryVersionCacheDaysParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = getCorrelationId parseResult,
                                DirectoryVersionCacheDays = parseResult.GetValue(Options.directoryVersionCacheDays)
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Repository.SetDirectoryVersionCacheDays(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.SetDirectoryVersionCacheDays(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.SetLogicalDeleteDays subcommand definition
    type SetLogicalDeleteDays() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.SetLogicalDeleteDaysParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = getCorrelationId parseResult,
                                LogicalDeleteDays = parseResult.GetValue(Options.logicalDeleteDays)
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                            let! response = Repository.SetLogicalDeleteDays(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.SetLogicalDeleteDays(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    // Enable promotion type subcommands
    type EnablePromotionTypeCommand = EnablePromotionTypeParameters -> Task<GraceResult<string>>

    type EnablePromotionParameters() =
        member val public Enabled = false with get, set

    let private enablePromotionTypeHandler
        (parseResult: ParseResult)
        (parameters: EnablePromotionParameters)
        (command: EnablePromotionTypeCommand)
        (promotionType: PromotionType)
        =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations parseResult

                match validateIncomingParameters with
                | Ok _ ->
                    let graceIds = parseResult |> getNormalizedIdsAndNames

                    let enablePromotionTypeParameters =
                        EnablePromotionTypeParameters(
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            CorrelationId = graceIds.CorrelationId,
                            Enabled = parameters.Enabled
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = command enablePromotionTypeParameters
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! command enablePromotionTypeParameters
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    // Set-DefaultServerApiVersion subcommand
    /// Repository.SetDefaultServerApiVersion subcommand definition
    type SetDefaultServerApiVersion() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.SetDefaultServerApiVersionParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = getCorrelationId parseResult,
                                DefaultServerApiVersion = parseResult.GetValue(Options.defaultServerApiVersion)
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Repository.SetDefaultServerApiVersion(parameters)

                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.SetDefaultServerApiVersion(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.SetName subcommand definition
    type SetName() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.SetRepositoryNameParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = getCorrelationId parseResult,
                                NewName = parseResult.GetValue(Options.newName)
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Repository.SetName(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.SetName(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.SetDescription subcommand definition
    type SetDescription() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.SetRepositoryDescriptionParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = getCorrelationId parseResult,
                                Description = parseResult.GetValue(Options.description)
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Repository.SetDescription(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.SetDescription(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.SetConflictResolutionPolicy subcommand definition
    type SetConflictResolutionPolicy() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.SetConflictResolutionPolicyParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = getCorrelationId parseResult,
                                PolicyType = parseResult.GetValue(Options.policyType),
                                ConfidenceThreshold = parseResult.GetValue(Options.confidenceThreshold)
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Grace.SDK.Repository.SetConflictResolutionPolicy(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Grace.SDK.Repository.SetConflictResolutionPolicy(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.Delete subcommand definition
    type Delete() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.DeleteRepositoryParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = getCorrelationId parseResult,
                                Force = parseResult.GetValue(Options.force),
                                DeleteReason = parseResult.GetValue(Options.deleteReason)
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Repository.Delete(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.Delete(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.Undelete subcommand definition
    type Undelete() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.UndeleteRepositoryParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Repository.Undelete(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.Undelete(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.SetAnonymousAccess subcommand definition
    type SetAnonymousAccess() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.SetAnonymousAccessParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                AnonymousAccess = parseResult.GetValue(Options.anonymousAccess),
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Setting anonymous access.[/]")
                                            let! response = Repository.SetAnonymousAccess(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.SetAnonymousAccess(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Repository.SetAllowsLargeFiles subcommand definition
    type SetAllowsLargeFiles() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> Grace.CLI.Common.Validations.CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Repository.SetAllowsLargeFilesParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                AllowsLargeFiles = parseResult.GetValue(Options.allowsLargeFiles),
                                CorrelationId = getCorrelationId parseResult
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Setting allows large files.[/]")
                                            let! response = Repository.SetAllowsLargeFiles(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Repository.SetAllowsLargeFiles(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return Error error |> renderOutput parseResult

                with ex ->
                    return
                        renderOutput
                            parseResult
                            (GraceResult.Error(GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)))
            }

    /// Builds the Repository subcommand.
    let Build =
        let addCommonOptionsExceptForRepositoryInfo (command: Command) =
            command
            |> addOption Options.ownerName
            |> addOption Options.ownerId
            |> addOption Options.organizationName
            |> addOption Options.organizationId
            |> addOption Options.repositoryId

        let addCommonOptions (command: Command) =
            command
            |> addOption Options.repositoryName
            |> addCommonOptionsExceptForRepositoryInfo

        // Create main command and aliases, if any.
        let repositoryCommand = new Command("repository", Description = "Creates, changes, and deletes repository-level information.")

        repositoryCommand.Aliases.Add("repo")

        // Add subcommands.
        let repositoryCreateCommand =
            new Command("create", Description = "Creates a new repository.")
            |> addOption Options.requiredRepositoryName
            |> addCommonOptionsExceptForRepositoryInfo
            |> addOption Options.doNotSwitch

        repositoryCreateCommand.Action <- new Create()
        repositoryCommand.Subcommands.Add(repositoryCreateCommand)

        let repositoryGetCommand =
            new Command("get", Description = "Gets information about a repository.")
            |> addCommonOptions

        repositoryGetCommand.Action <- new Get()
        repositoryCommand.Subcommands.Add(repositoryGetCommand)

        let repositoryInitCommand =
            new Command("init", Description = "Initializes a new repository with the contents of a directory.")
            |> addOption Options.directory
            |> addOption Options.graceConfig
            |> addCommonOptions

        repositoryInitCommand.Action <- new Init()
        repositoryCommand.Subcommands.Add(repositoryInitCommand)

        //let repositoryDownloadCommand = new Command("download", Description = "Downloads the current version of the repository.") |> addOption Options.requiredRepositoryName |> addOption Options.graceConfig |> addCommonOptionsExceptForRepositoryInfo
        //repositoryInitCommand.Action <- Init
        //repositoryCommand.Subcommands.Add(repositoryInitCommand)

        let getBranchesCommand =
            new Command("get-branches", Description = "Gets a list of branches in the repository.")
            |> addOption Options.includeDeleted
            |> addCommonOptions

        getBranchesCommand.Action <- new GetBranches()
        repositoryCommand.Subcommands.Add(getBranchesCommand)

        let setVisibilityCommand =
            new Command("set-visibility", Description = "Sets the visibility of the repository.")
            |> addOption Options.visibility
            |> addCommonOptions

        setVisibilityCommand.Action <- new SetVisibility()
        repositoryCommand.Subcommands.Add(setVisibilityCommand)

        let setStatusCommand =
            new Command("set-status", Description = "Sets the status of the repository.")
            |> addOption Options.status
            |> addCommonOptions

        setStatusCommand.Action <- new SetStatus()
        repositoryCommand.Subcommands.Add(setStatusCommand)

        let setAnonymousAccessCommand =
            new Command("set-anonymous-access", Description = "Sets the anonymous access status of the repository.")
            |> addOption Options.anonymousAccess
            |> addCommonOptions

        setAnonymousAccessCommand.Action <- new SetAnonymousAccess()
        repositoryCommand.Subcommands.Add(setAnonymousAccessCommand)

        let setAllowsLargeFilesCommand =
            new Command("set-allows-large-files", Description = "Sets the large files status of the repository.")
            |> addOption Options.allowsLargeFiles
            |> addCommonOptions

        setAllowsLargeFilesCommand.Action <- new SetAllowsLargeFiles()
        repositoryCommand.Subcommands.Add(setAllowsLargeFilesCommand)

        let setRecordSavesCommand =
            new Command("set-record-saves", Description = "Sets whether the repository defaults to recording every save.")
            |> addOption Options.recordSaves
            |> addCommonOptions

        setRecordSavesCommand.Action <- new SetRecordSaves()
        repositoryCommand.Subcommands.Add(setRecordSavesCommand)

        let setDefaultServerApiVersionCommand =
            new Command(
                "set-default-server-api-version",
                Description = "Sets the default server API version for clients to use when accessing this repository."
            )
            |> addOption Options.defaultServerApiVersion
            |> addCommonOptions

        setDefaultServerApiVersionCommand.Action <- new SetDefaultServerApiVersion()
        repositoryCommand.Subcommands.Add(setDefaultServerApiVersionCommand)

        let setSaveDaysCommand =
            new Command("set-save-days", Description = "Sets the number of days to keep saves in the repository.")
            |> addOption Options.saveDays
            |> addCommonOptions

        setSaveDaysCommand.Action <- new SetSaveDays()
        repositoryCommand.Subcommands.Add(setSaveDaysCommand)

        let setCheckpointDaysCommand =
            new Command("set-checkpoint-days", Description = "Sets the number of days to keep checkpoints in the repository.")
            |> addOption Options.checkpointDays
            |> addCommonOptions

        setCheckpointDaysCommand.Action <- new SetCheckpointDays()
        repositoryCommand.Subcommands.Add(setCheckpointDaysCommand)

        let setDiffCacheDaysCommand =
            new Command("set-diff-cache-days", Description = "Sets the number of days to keep diff results cached in the repository.")
            |> addOption Options.diffCacheDays
            |> addCommonOptions

        setDiffCacheDaysCommand.Action <- new SetDiffCacheDays()
        repositoryCommand.Subcommands.Add(setDiffCacheDaysCommand)

        let setDirectoryVersionCacheDaysCommand =
            new Command(
                "set-directory-version-cache-days",
                Description = "Sets how long to keep recursive directory version contents cached in the repository."
            )
            |> addOption Options.directoryVersionCacheDays
            |> addCommonOptions

        setDirectoryVersionCacheDaysCommand.Action <- new SetDirectoryVersionCacheDays()
        repositoryCommand.Subcommands.Add(setDirectoryVersionCacheDaysCommand)

        let setLogicalDeleteDaysCommand =
            new Command(
                "set-logical-delete-days",
                Description = "Sets the number of days to keep deleted branches in the repository before permanently deleting them."
            )
            |> addOption Options.logicalDeleteDays
            |> addCommonOptions

        setLogicalDeleteDaysCommand.Action <- new SetLogicalDeleteDays()
        repositoryCommand.Subcommands.Add(setLogicalDeleteDaysCommand)

        let setNameCommand =
            new Command("set-name", Description = "Sets the name of the repository.")
            |> addOption Options.newName
            |> addCommonOptions

        setNameCommand.Action <- new SetName()
        repositoryCommand.Subcommands.Add(setNameCommand)

        let setDescriptionCommand =
            new Command("set-description", Description = "Sets the description of the repository.")
            |> addOption Options.description
            |> addCommonOptions

        setDescriptionCommand.Action <- new SetDescription()
        repositoryCommand.Subcommands.Add(setDescriptionCommand)

        let setConflictResolutionPolicyCommand =
            new Command("set-conflict-resolution-policy", Description = "Sets the conflict resolution policy for the repository.")
            |> addOption Options.policyType
            |> addOption Options.confidenceThreshold
            |> addCommonOptions

        setConflictResolutionPolicyCommand.Action <- new SetConflictResolutionPolicy()
        repositoryCommand.Subcommands.Add(setConflictResolutionPolicyCommand)

        let deleteCommand =
            new Command("delete", Description = "Deletes a repository.")
            |> addOption Options.deleteReason
            |> addOption Options.force
            |> addCommonOptions

        deleteCommand.Action <- new Delete()
        repositoryCommand.Subcommands.Add(deleteCommand)

        let undeleteCommand =
            new Command("undelete", Description = "Undeletes the repository.")
            |> addCommonOptions

        undeleteCommand.Action <- new Undelete()
        repositoryCommand.Subcommands.Add(undeleteCommand)

        repositoryCommand
