namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Services
open Grace.SDK
open Grace.Shared
open Grace.Shared.Dto
open Grace.Shared.Parameters
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Client.Configuration
open Grace.Shared.Parameters.Repository
open Grace.Shared.Validation.Errors.Repository
open NodaTime
open Spectre.Console
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.NamingConventionBinder
open System.CommandLine.Parsing
open System.Linq
open System.IO
open System.Threading
open System.Threading.Tasks
open Grace.Shared.Parameters.Directory
open Spectre.Console.Json

module Repository =

    type CommonParameters() = 
        inherit ParameterBase()
        member val public Name: RepositoryName = String.Empty with get, set
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName: OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName: OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName: RepositoryName = String.Empty with get, set

    module private Options =
        let ownerId = new Option<String>("--ownerId", IsRequired = false, Description = "The repository's owner ID <Guid>.", Arity = ArgumentArity.ZeroOrOne, getDefaultValue = (fun _ -> $"{Current().OwnerId}"))
        let ownerName = new Option<String>("--ownerName", IsRequired = false, Description = "The repository's owner name. [default: current owner]", Arity = ArgumentArity.ExactlyOne)
        let organizationId = new Option<String>("--organizationId", IsRequired = false, Description = "The repository's organization ID <Guid>.", Arity = ArgumentArity.ZeroOrOne, getDefaultValue = (fun _ -> $"{Current().OrganizationId}"))
        let organizationName = new Option<String>("--organizationName", IsRequired = false, Description = "The repository's organization name. [default: current organization]", Arity = ArgumentArity.ZeroOrOne)
        let repositoryId = new Option<String>([|"--repositoryId"; "-r"|], IsRequired = false, Description = "The repository's ID <Guid>.", Arity = ArgumentArity.ExactlyOne, getDefaultValue = (fun _ -> $"{Current().RepositoryId}"))
        let repositoryName = new Option<String>([|"--repositoryName"; "-n"|], IsRequired = false, Description = "The name of the repository. [default: current repository]", Arity = ArgumentArity.ExactlyOne)
        let requiredRepositoryName = new Option<String>([|"--repositoryName"; "-n"|], IsRequired = true, Description = "The name of the repository.", Arity = ArgumentArity.ExactlyOne)
        let visibility = (new Option<RepositoryVisibility>("--visibility", IsRequired = true, Description = "The visibility of the repository.", Arity = ArgumentArity.ExactlyOne))
                            .FromAmong (listCases<RepositoryVisibility>())
        let status = (new Option<String>("--status", IsRequired = true, Description = "The status of the repository.", Arity = ArgumentArity.ExactlyOne))
                            .FromAmong(listCases<RepositoryStatus>())
        let recordSaves = new Option<bool>("--recordSaves", IsRequired = true, Description = "True to record all saves; false to turn it off.", Arity = ArgumentArity.ExactlyOne)
        let defaultServerApiVersion = (new Option<String>("--defaultServerApiVersion", IsRequired = true, Description = "The default version of the server API that clients should use when accessing this repository.", Arity = ArgumentArity.ExactlyOne))
                                            .FromAmong(listCases<Constants.ServerApiVersions>())
        let saveDays = new Option<double>("--saveDays", IsRequired = true, Description = "How many days to keep saves. [default: 7.0]", Arity = ArgumentArity.ExactlyOne)
        let checkpointDays = new Option<double>("--checkpointDays", IsRequired = true, Description = "How many days to keep checkpoints. [default: 365.0]", Arity = ArgumentArity.ExactlyOne)
        let newName = new Option<String>("--newName", IsRequired = true, Description = "The new name for the repository.", Arity = ArgumentArity.ExactlyOne)
        let deleteReason = new Option<String>("--deleteReason", IsRequired = true, Description = "The reason for deleting the repository.", Arity = ArgumentArity.ExactlyOne)
        let graceConfig = new Option<String>("--graceConfig", IsRequired = false, Description = "The path of a Grace config file that you'd like to use instead of the default graceconfig.json.", Arity = ArgumentArity.ExactlyOne)
        let force = new Option<bool>("--force", IsRequired = false, Description = "Deletes repository even if there are links to other repositories.", Arity = ArgumentArity.ExactlyOne)
        let doNotSwitch = new Option<bool>("--doNotSwitch", IsRequired = false, Description = "Do not switch to the new repository as the current repository.", Arity = ArgumentArity.ZeroOrOne)
        let directory = new Option<String>("--directory", IsRequired = false, Description = "The directory to use when initializing the repository. [default: current directory]", Arity = ArgumentArity.ExactlyOne)
        let includeDeleted = new Option<bool>("--includeDeleted", IsRequired = false, Description = "True to include deleted branches; false to exclude them.", Arity = ArgumentArity.ZeroOrOne)

    let mustBeAValidGuid (parseResult: ParseResult) (parameters: CommonParameters) (option: Option) (value: string) (error: RepositoryError) =
        let mutable guid = Guid.Empty
        if parseResult.CommandResult.FindResultFor(option) <> null 
                && not <| String.IsNullOrEmpty(value) 
                && (Guid.TryParse(value, &guid) = false || guid = Guid.Empty)
                then 
            Error (GraceError.Create (RepositoryError.getErrorMessage error) (parameters.CorrelationId))
        else
            Ok (parseResult, parameters)

    let mustBeAValidGraceName (parseResult: ParseResult) (parameters: CommonParameters) (option: Option) (value: string) (error: RepositoryError) =
        if parseResult.CommandResult.FindResultFor(option) <> null && not <| Constants.GraceNameRegex.IsMatch(value) then 
            Error (GraceError.Create (RepositoryError.getErrorMessage error) (parameters.CorrelationId))
        else
            Ok (parseResult, parameters)

    let private CommonValidations (parseResult, parameters) =

        let ``OwnerId must be a Guid`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGuid parseResult parameters Options.ownerId parameters.OwnerId InvalidOwnerId

        let ``OwnerName must be a valid Grace name`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGraceName parseResult parameters Options.ownerName parameters.OwnerName InvalidOwnerName

        let ``OrganizationId must be a Guid`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGuid parseResult parameters Options.organizationId parameters.OrganizationId InvalidOrganizationId

        let ``OrganizationName must be a valid Grace name`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGraceName parseResult parameters Options.organizationName parameters.OrganizationName InvalidOrganizationName
            
        let ``RepositoryId must be a Guid`` (parseResult: ParseResult, parameters: CommonParameters) =
            mustBeAValidGuid parseResult parameters Options.repositoryId parameters.RepositoryId InvalidRepositoryId

        (parseResult, parameters)
            |>  ``OwnerId must be a Guid``
            >>= ``OwnerName must be a valid Grace name``
            >>= ``OrganizationId must be a Guid``
            >>= ``OrganizationName must be a valid Grace name``
            >>= ``RepositoryId must be a Guid``

    let ``RepositoryName must be a valid Grace name`` (parseResult: ParseResult, parameters: CommonParameters) =
        mustBeAValidGraceName parseResult parameters Options.repositoryName parameters.RepositoryName InvalidRepositoryName

    let ``Either RepositoryId or RepositoryName must be specified`` (parseResult: ParseResult, parameters: CommonParameters) =
        if parseResult.HasOption(Options.repositoryId) || parseResult.HasOption(Options.repositoryName) || parseResult.HasOption(Options.requiredRepositoryName) then
            Ok (parseResult, parameters)
        else
            Error (GraceError.Create (RepositoryError.getErrorMessage EitherRepositoryIdOrRepositoryNameRequired) (parameters.CorrelationId))

    /// Adjusts parameters to account for whether Id's or Name's were specified by the user, or should be taken from default values.
    let normalizeIdsAndNames<'T when 'T :> CommonParameters> (parseResult: ParseResult) (parameters: 'T) =
        // If the name was specified on the command line, but the id wasn't, then we should only send the name, and we set the id to String.Empty.
        if parseResult.CommandResult.FindResultFor(Options.ownerId).IsImplicit && not <| isNull(parseResult.CommandResult.FindResultFor(Options.ownerName)) && not <| parseResult.CommandResult.FindResultFor(Options.ownerName).IsImplicit then
            parameters.OwnerId <- String.Empty
        if parseResult.CommandResult.FindResultFor(Options.organizationId).IsImplicit && not <| isNull(parseResult.CommandResult.FindResultFor(Options.organizationName)) && not <| parseResult.CommandResult.FindResultFor(Options.organizationName).IsImplicit then
            parameters.OrganizationId <- String.Empty
        if parseResult.CommandResult.FindResultFor(Options.repositoryId).IsImplicit && not <| isNull(parseResult.CommandResult.FindResultFor(Options.repositoryName)) && not <| parseResult.CommandResult.FindResultFor(Options.repositoryName).IsImplicit then
            parameters.RepositoryId <- String.Empty
        parameters

    /// Populates OwnerId, OrganizationId, and RepositoryId based on the command parameters and the contents of graceconfig.json.
    let getIds<'T when 'T :> CommonParameters> (parameters: 'T) = 
        let ownerId = if not <| String.IsNullOrEmpty(parameters.OwnerId) || not <| String.IsNullOrEmpty(parameters.OwnerName)
                       then parameters.OwnerId 
                       else $"{Current().OwnerId}"
        let organizationId = if not <| String.IsNullOrEmpty(parameters.OrganizationId) || not <| String.IsNullOrEmpty(parameters.OrganizationName)
                             then parameters.OrganizationId
                             else $"{Current().OrganizationId}"
        let repositoryId = if not <| String.IsNullOrEmpty(parameters.RepositoryId) || not <| String.IsNullOrEmpty(parameters.RepositoryName)
                           then parameters.RepositoryId 
                           else $"{Current().RepositoryId}"
        (ownerId, organizationId, repositoryId)

    // Create subcommand.
    type CreateParameters() = 
        inherit CommonParameters()
    let private createHandler (parseResult: ParseResult) (createParameters: CreateParameters) =
        task {
            try
                //if parseResult |> verbose then printParseResult parseResult
                if verbose parseResult then printParseResult parseResult
                let validateIncomingParameters = CommonValidations (parseResult, createParameters) >>= ``Either RepositoryId or RepositoryName must be specified``
                match validateIncomingParameters with
                | Ok _ -> 
                    let optionResult = parseResult.FindResultFor(Options.repositoryId)
                    let repositoryId = if ((not <| isNull(optionResult)) && optionResult.IsImplicit) || isNull(optionResult) then Guid.NewGuid().ToString() else createParameters.RepositoryId
                    let ownerId = if not <| String.IsNullOrEmpty(createParameters.OwnerId) || not <| String.IsNullOrEmpty(createParameters.OwnerName)
                                  then createParameters.OwnerId
                                  else $"{Current().OwnerId}"
                    let organizationId = if not <| String.IsNullOrEmpty(createParameters.OrganizationId) || not <| String.IsNullOrEmpty(createParameters.OrganizationName)
                                         then createParameters.OrganizationId
                                         else $"{Current().OrganizationId}"
                    let enhancedParameters = Repository.CreateRepositoryParameters(RepositoryId = repositoryId, 
                        RepositoryName = createParameters.RepositoryName, 
                        OwnerId = ownerId, 
                        OwnerName = createParameters.OwnerName,
                        OrganizationId = organizationId,
                        OrganizationName = createParameters.OrganizationName,
                        CorrelationId = createParameters.CorrelationId)

                    if parseResult |> hasOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.Create(enhancedParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.Create(enhancedParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private Create =
        CommandHandler.Create(fun (parseResult: ParseResult) (createParameters: CreateParameters) ->
            task {                
                let! result = createHandler parseResult createParameters
                match result with
                | Ok returnValue ->
                    // Update the Grace configuration file with the newly-created repository.
                    if not <| parseResult.HasOption(Options.doNotSwitch) then
                        let newConfig = Current()
                        newConfig.RepositoryId <- Guid.Parse(returnValue.Properties[nameof(RepositoryId)])
                        newConfig.RepositoryName <- RepositoryName (returnValue.Properties[nameof(RepositoryName)])
                        newConfig.BranchId <- Guid.Parse(returnValue.Properties[nameof(BranchId)])
                        newConfig.BranchName <- BranchName (returnValue.Properties[nameof(BranchName)])
                        updateConfiguration newConfig
                | Error error ->
                    logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                return result |> renderOutput parseResult
            })

    // Init subcommand
    type InitParameters() =
        inherit CommonParameters()
        member val public Directory = String.Empty with get, set
        member val public GraceConfig = String.Empty with get, set

    /// Validates that the directory exists. Placed here so it can use InitParameters.
    let ``Directory must be a valid path`` (parseResult: ParseResult, parameters: InitParameters) =
        if parseResult.HasOption(Options.directory) && not <| Directory.Exists(parameters.Directory) then
            Error (GraceError.Create (RepositoryError.getErrorMessage InvalidDirectory) (parameters.CorrelationId))
        else
            Ok (parseResult, parameters)

    let private initHandler (parseResult: ParseResult) (parameters: InitParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let directoryIsValid = (parseResult, parameters) |> ``Directory must be a valid path``
                match directoryIsValid with
                | Ok _ ->
                    let validateIncomingParameters = (parseResult, parameters) |> CommonValidations 
                    match validateIncomingParameters with
                    | Ok _ -> 
                        let (ownerId, organizationId, repositoryId) = getIds parameters
                        let isEmptyParameters = Repository.IsEmptyParameters(
                            OwnerId = ownerId,
                            OwnerName = parameters.OwnerName,
                            OrganizationId = organizationId,
                            OrganizationName = parameters.OrganizationName,
                            RepositoryId = repositoryId, 
                            RepositoryName = parameters.RepositoryName, 
                            CorrelationId = parameters.CorrelationId)

                        let! repositoryIsEmpty = Repository.IsEmpty isEmptyParameters
                        match repositoryIsEmpty with
                        | Ok isEmpty ->
                            if isEmpty.ReturnValue = true then
                                if parseResult |> hasOutput then
                                    let! graceStatus = progress.Columns(progressColumns).StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading existing Grace index file.[/]")
                                            let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Computing new Grace index file.[/]", autoStart = false)
                                            let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Writing new Grace index file.[/]", autoStart = false)
                                            let t3 = progressContext.AddTask($"[{Color.DodgerBlue1}]Ensure files are in the object cache.[/]", autoStart = false)
                                            let t4 = progressContext.AddTask($"[{Color.DodgerBlue1}]Ensure object cache index is up-to-date.[/]", autoStart = false)
                                            let t5 = progressContext.AddTask($"[{Color.DodgerBlue1}]Ensure files are uploaded to object storage.[/]", autoStart = false)
                                            let t6 = progressContext.AddTask($"[{Color.DodgerBlue1}]Ensure directory versions are uploaded to Grace Server.[/]", autoStart = false)

                                            // Read the existing Grace status file.
                                            t0.Increment(0.0)
                                            let! previousGraceStatus = readGraceStatusFile()
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
                                            let plr = Parallel.ForEach(graceStatus.Index.Values, Constants.ParallelOptions, (fun ldv ->
                                                for fileVersion in ldv.Files do
                                                    fileVersions.TryAdd(fileVersion.RelativePath, fileVersion) |> ignore
                                            ))
                                            let incrementAmount = 100.0 / double fileVersions.Count

                                            // Loop through the files, and copy them to the object cache if they don't already exist.
                                            let plr = Parallel.ForEach(fileVersions, Constants.ParallelOptions, (fun kvp _ ->
                                                let fileVersion = kvp.Value
                                                let fullObjectPath = fileVersion.FullObjectPath
                                                if not <| File.Exists(fullObjectPath) then
                                                    Directory.CreateDirectory(Path.GetDirectoryName(fullObjectPath)) |> ignore // If the directory already exists, this will do nothing.
                                                    File.Copy(Path.Combine(Current().RootDirectory, fileVersion.RelativePath), fullObjectPath)
                                                t3.Increment(incrementAmount)))
                                            t3.Value <- 100.0

                                            // Ensure the object cache index is up-to-date.
                                            t4.StartTask()
                                            let! objectCache = readGraceObjectCacheFile()
                                            let incrementAmount = 100.0 / double graceStatus.Index.Count
                                            let plr = Parallel.ForEach(graceStatus.Index.Values, Constants.ParallelOptions, (fun ldv ->
                                                if not <| objectCache.Index.ContainsKey(ldv.DirectoryId) then
                                                    objectCache.Index.AddOrUpdate(ldv.DirectoryId, (fun _ -> ldv), (fun _ _ -> ldv)) |> ignore
                                                    t4.Increment(incrementAmount)
                                            ))
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
                                                do! Parallel.ForEachAsync(fileVersionGroups, Constants.ParallelOptions, (fun fileVersions ct ->
                                                    ValueTask(task {
                                                        let! graceResult = Storage.FilesExistInObjectStorage (fileVersions.Select(fun f -> f.Value.ToFileVersion).ToList()) (getCorrelationId parseResult)
                                                        match graceResult with
                                                        | Ok graceReturnValue ->
                                                            let uploadMetadata = graceReturnValue.ReturnValue
                                                            // Increment the counter for the files that we don't have to upload.
                                                            t5.Increment(incrementAmount * double (fileVersions.Count() - uploadMetadata.Count))

                                                            // Index all of the file versions by their SHA256 hash; we'll look up the files to upload with it.
                                                            let filesIndexedBySha256Hash = Dictionary<Sha256Hash, LocalFileVersion>(fileVersions.Select(fun kvp -> KeyValuePair(kvp.Value.Sha256Hash, kvp.Value)))

                                                            // Upload the files in this chunk to object storage.
                                                            do! Parallel.ForEachAsync(uploadMetadata, Constants.ParallelOptions, (fun upload ct ->
                                                                ValueTask(task {
                                                                    let fileVersion = filesIndexedBySha256Hash[upload.Sha256Hash].ToFileVersion
                                                                    let! result = Storage.SaveFileToObjectStorage fileVersion (upload.BlobUriWithSasToken) (getCorrelationId parseResult)
                    
                                                                    // Increment the counter for each file that we do upload.
                                                                    t5.Increment(incrementAmount)
                                                                    match result with
                                                                    | Ok result -> succeeded.Enqueue(result)
                                                                    | Error error -> errors.Enqueue(error)
                                                                })))

                                                        | Error error ->
                                                            AnsiConsole.Write((new Panel($"{error}"))
                                                                                  .BorderColor(Color.Red3))
                                                    })))

                                                // Print out any errors that occurred.
                                                if errors.Count = 0 then
                                                    ()
                                                else
                                                    AnsiConsole.MarkupLine($"{errors.Count} errors occurred.")
                                                    let mutable error = GraceError.Create String.Empty String.Empty
                                                    while not <| errors.IsEmpty do
                                                        if errors.TryDequeue(&error) then AnsiConsole.MarkupLine($"[{Colors.Error}]{error.Error.EscapeMarkup()}[/]")
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
                                            let segmentGroups = graceStatus.Index.Values
                                                                    .GroupBy(fun dv -> countSegments dv.RelativePath)
                                                                    .OrderByDescending(fun group -> group.Key)
                                                    
                                            for group in segmentGroups do
                                                let directoryVersionGroups = group.Chunk(chunkSize)
                                                do! Parallel.ForEachAsync(directoryVersionGroups, Constants.ParallelOptions, (fun directoryVersionGroup ct ->
                                                    ValueTask(task {
                                                        let param = SaveDirectoryVersionsParameters()
                                                        param.DirectoryVersions <- directoryVersionGroup.Select(fun dv -> dv.ToDirectoryVersion).ToList()
                                                        param.CorrelationId <- getCorrelationId parseResult
                                                        let! sdvResult = Directory.SaveDirectoryVersions param
                                                        match sdvResult with
                                                        | Ok result -> succeeded.Enqueue(result)
                                                        | Error error -> errors.Enqueue(error)
                                                        t6.Increment(incrementAmount * double directoryVersionGroup.Length)
                                                    })))
                                            t6.Value <- 100.0

                                            AnsiConsole.MarkupLine($"[{Colors.Important}]succeeded: {succeeded.Count}; errors: {errors.Count}.[/]")
                                            let mutable error = GraceError.Create String.Empty String.Empty
                                            while not <| errors.IsEmpty do
                                                errors.TryDequeue(&error) |> ignore
                                                if error.Error.Contains("TRetval") then
                                                    logToConsole $"********* {error.Error}"
                                                AnsiConsole.MarkupLine($"[{Colors.Error}]{error.Error.EscapeMarkup()}[/]")
                                            return graceStatus

                                    })

                                    let fileCount = graceStatus.Index.Values.Select(fun directoryVersion -> directoryVersion.Files.Count).Sum()
                                    let totalFileSize = graceStatus.Index.Values.Sum(fun directoryVersion -> directoryVersion.Files.Sum(fun f -> int64 f.Size))
                                    let rootDirectoryVersion = graceStatus.Index.Values.First(fun d -> d.RelativePath = Constants.RootDirectoryPath)
                                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of directories scanned: {graceStatus.Index.Count}.[/]")
                                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of files scanned: {fileCount}; total file size: {totalFileSize:N0}.[/]")
                                    AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Root SHA-256 hash: {rootDirectoryVersion.Sha256Hash.Substring(0, 8)}[/]"
                                    return Ok (GraceReturnValue.Create "Initialized repository." (parseResult |> getCorrelationId))
                                else
                                    // Do the whole thing with no output
                                    return Ok (GraceReturnValue.Create "Initialized repository." (parseResult |> getCorrelationId))
                            else
                                return Error (GraceError.Create (RepositoryError.getErrorMessage RepositoryIsAlreadyInitialized) (parseResult |> getCorrelationId))
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
            with ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private Init =
        CommandHandler.Create(fun (parseResult: ParseResult) (initParameters: InitParameters) ->
            task {                
                let! result = initHandler parseResult initParameters
                return result |> renderOutput parseResult
            })


    // Get subcommand
    type GetParameters() =
        inherit CommonParameters()
    let private getHandler (parseResult: ParseResult) (getParameters: GetParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations (parseResult, getParameters)
                match validateIncomingParameters with
                | Ok _ -> 
                    let parameters = Parameters.Repository.GetRepositoryParameters(OwnerId = getParameters.OwnerId, OwnerName = getParameters.OwnerName, 
                        OrganizationId = getParameters.OrganizationId, OrganizationName = getParameters.OrganizationName, 
                        RepositoryId = getParameters.RepositoryId, RepositoryName = getParameters.RepositoryName,
                        CorrelationId = getParameters.CorrelationId)
                    if parseResult |> hasOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")                                    
                                    let! result = Repository.Get(parameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.Get(parameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private Get =
        CommandHandler.Create(fun (parseResult: ParseResult) (getParameters: GetParameters) ->
            task {                
                let! result = getHandler parseResult (getParameters |> normalizeIdsAndNames parseResult)
                //return result |> renderOutput parseResult
                match result with
                | Ok graceReturnValue ->
                    let jsonText = JsonText(serialize graceReturnValue.ReturnValue)
                    AnsiConsole.Write(jsonText)
                    return Ok graceReturnValue |> renderOutput parseResult
                | Error graceError ->
                    return Error graceError |> renderOutput parseResult
            })

    // Get-Branches subcommand
    type GetBranchesParameters() = 
        inherit CommonParameters()
        member val public IncludeDeleted = false with get, set
    let private getBranchesHandler (parseResult: ParseResult) (parameters: GetBranchesParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations (parseResult, parameters)
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let getBranchesParameters = Repository.GetBranchesParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        IncludeDeleted = normalizedParameters.IncludeDeleted,
                        CorrelationId = normalizedParameters.CorrelationId)

                    if parseResult |> hasOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.GetBranches(getBranchesParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.GetBranches(getBranchesParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private GetBranches =
        CommandHandler.Create(fun (parseResult: ParseResult) (getBranchesParameters: GetBranchesParameters) ->
            task {                
                let! result = getBranchesHandler parseResult getBranchesParameters
                match result with
                | Ok returnValue ->
                    let intResult = result |> renderOutput parseResult
                    if parseResult |> hasOutput then
                        let table = Table(Border = TableBorder.DoubleEdge)
                        table.AddColumn(TableColumn(Markup($"[{Colors.Important}]Branch[/]")))
                             .AddColumn(TableColumn(Markup($"[{Colors.Important}]Updated at[/]")))
                             .AddColumn(TableColumn(Markup($"[{Colors.Important}]Branch Id[/]")))
                             .AddColumn(TableColumn(Markup($"[{Colors.Important}]Based on latest promotion[/]")))
                             .AddColumn(TableColumn(Markup($"[{Colors.Important}]Parent branch[/]"))) |> ignore
                        let allBranches = returnValue.ReturnValue
                        let parents = allBranches.Select(fun branch -> {|
                            branchId = branch.BranchId
                            branchName = if branch.ParentBranchId = Constants.DefaultParentBranchId then "root"
                                         else allBranches.Where(fun br -> br.BranchId = branch.ParentBranchId).Select(fun br -> br.BranchName).First()
                            latestPromotion = if branch.ParentBranchId = Constants.DefaultParentBranchId then branch.LatestPromotion
                                              else allBranches.Where(fun br -> br.BranchId = branch.ParentBranchId).Select(fun br -> br.LatestPromotion).First() |})

                        let branchesWithParentNames = allBranches.Join(parents, (fun branch -> branch.BranchId), (fun parent -> parent.branchId), 
                            (fun branch parent -> {| branchId = branch.BranchId; branchName = branch.BranchName; updatedAt = branch.UpdatedAt; parentBranchName = parent.branchName; basedOnLatestPromotion = (branch.BasedOn = parent.latestPromotion) |})).OrderBy(fun branch -> branch.parentBranchName, branch.branchName)

                        for br in branchesWithParentNames do
                            let updatedAt = match br.updatedAt with | Some t -> instantToLocalTime(t) | None -> String.Empty
                            table.AddRow(br.branchName, updatedAt, $"[{Colors.Deemphasized}]{br.branchId}[/]", 
                                (if br.basedOnLatestPromotion then $"[{Colors.Added}]Yes[/]" else $"[{Colors.Important}]No[/]"),
                                br.parentBranchName) |> ignore
                        AnsiConsole.Write(table)

                    return intResult
                | Error error ->
                    return result |> renderOutput parseResult
            })

    // Set-Visibility subcommand
    type SetVisibilityParameters() = 
        inherit CommonParameters()
        member val public Visibility: string = String.Empty with get, set
    let private setVisibilityHandler (parseResult: ParseResult) (parameters: SetVisibilityParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations (parseResult, parameters)
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let visibilityParameters = Repository.SetRepositoryVisibilityParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName, 
                        Visibility = normalizedParameters.Visibility,
                        CorrelationId = normalizedParameters.CorrelationId)

                    if parseResult |> hasOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.SetVisibility(visibilityParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.SetVisibility(visibilityParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetVisibility =
        CommandHandler.Create(fun (parseResult: ParseResult) (setNameParameters: SetVisibilityParameters) ->
            task {                
                let! result = setVisibilityHandler parseResult setNameParameters
                return result |> renderOutput parseResult
            })

    // Set-Status subcommand
    type StatusParameters() = 
        inherit CommonParameters()
        member val public Status: string = String.Empty with get, set
    let private setStatusHandler (parseResult: ParseResult) (parameters: StatusParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations (parseResult, parameters)
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let statusParameters = Repository.SetRepositoryStatusParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName, 
                        Status = normalizedParameters.Status,
                        CorrelationId = normalizedParameters.CorrelationId)

                    if parseResult |> hasOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.SetStatus(statusParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.SetStatus(statusParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetStatus =
        CommandHandler.Create(fun (parseResult: ParseResult) (setNameParameters: StatusParameters) ->
                task {                
                    let! result = setStatusHandler parseResult setNameParameters
                    return result |> renderOutput parseResult
                })

    // Set-RecordSaves subcommand
    type RecordSavesParameters() = 
        inherit CommonParameters()
        member val public RecordSaves: bool = false with get, set
    let private setRecordSavesHandler (parseResult: ParseResult) (parameters: RecordSavesParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations (parseResult, parameters)
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let recordSavesParameters = Repository.RecordSavesParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName, 
                        RecordSaves = normalizedParameters.RecordSaves,
                        CorrelationId = normalizedParameters.CorrelationId)

                    if parseResult |> hasOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.SetRecordSaves(recordSavesParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.SetRecordSaves(recordSavesParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetRecordSaves =
        CommandHandler.Create(fun (parseResult: ParseResult) (recordSavesParameters: RecordSavesParameters) ->
                    task {                
                        let! result = setRecordSavesHandler parseResult recordSavesParameters
                        return result |> renderOutput parseResult
                    })

    // Set-SaveDays subcommand
    type SaveDaysParameters() = 
        inherit CommonParameters()
        member val public SaveDays: double = Double.MinValue with get, set
    let private setSaveDaysHandler (parseResult: ParseResult) (parameters: SaveDaysParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations (parseResult, parameters)
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let setSaveDaysParameters = Repository.SetSaveDaysParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        CorrelationId = normalizedParameters.CorrelationId,
                        SaveDays = normalizedParameters.SaveDays)

                    if parseResult |> hasOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.SetSaveDays(setSaveDaysParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.SetSaveDays(setSaveDaysParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetSaveDays =
        CommandHandler.Create(fun (parseResult: ParseResult) (saveDaysParameters: SaveDaysParameters) ->
                    task {                
                        let! result = setSaveDaysHandler parseResult saveDaysParameters
                        return result |> renderOutput parseResult
                    })

    // Set-CheckpointDays subcommand
    type CheckpointDaysParameters() = 
        inherit CommonParameters()
        member val public CheckpointDays: double = Double.MinValue with get, set
    let private setCheckpointDaysHandler (parseResult: ParseResult) (parameters: CheckpointDaysParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations (parseResult, parameters)
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let checkpointDaysParameters = Repository.SetCheckpointDaysParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        CorrelationId = normalizedParameters.CorrelationId,
                        CheckpointDays = normalizedParameters.CheckpointDays)

                    if parseResult |> hasOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.SetCheckpointDays(checkpointDaysParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.SetCheckpointDays(checkpointDaysParameters)
                | Error error -> return Error error
            with ex ->
                return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetCheckpointDays =
        CommandHandler.Create(fun (parseResult: ParseResult) (checkpointDaysParameters: CheckpointDaysParameters) ->
                    task {                
                        let! result = setCheckpointDaysHandler parseResult checkpointDaysParameters
                        return result |> renderOutput parseResult
                    })
                    
    // Enable promotion type subcommands
    type EnablePromotionTypeCommand = EnablePromotionTypeParameters -> Task<GraceResult<string>>
    type EnablePromotionParameters() =
        inherit CommonParameters()
        member val public Enabled = false with get, set
    let private enablePromotionTypeHandler (parseResult: ParseResult) (parameters: EnablePromotionParameters) (command: EnablePromotionTypeCommand) (promotionType: PromotionType) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations (parseResult, parameters)
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let enablePromotionTypeParameters = EnablePromotionTypeParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        CorrelationId = normalizedParameters.CorrelationId,
                        Enabled = normalizedParameters.Enabled)

                    if parseResult |> hasOutput then
                        return! progress.Columns(progressColumns)
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
                return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }

    // Set-DefaultServerApiVersion subcommand
    type DefaultServerApiVersionParameters() = 
        inherit CommonParameters()
        member val public DefaultServerApiVersion = String.Empty with get, set
    let private setDefaultServerApiVersionHandler (parseResult: ParseResult) (parameters: DefaultServerApiVersionParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations (parseResult, parameters)
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let defaultServerApiVersionParameters = Repository.SetDefaultServerApiVersionParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        CorrelationId = normalizedParameters.CorrelationId,
                        DefaultServerApiVersion = normalizedParameters.DefaultServerApiVersion)

                    if parseResult |> hasOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.SetDefaultServerApiVersion(defaultServerApiVersionParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.SetDefaultServerApiVersion(defaultServerApiVersionParameters)
                | Error error -> return Error error
            with ex -> 
                return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetDefaultServerApiVersion =
        CommandHandler.Create(fun (parseResult: ParseResult) (defaultServerApiVersionParameters: DefaultServerApiVersionParameters) ->
                    task {                
                        let! result = setDefaultServerApiVersionHandler parseResult defaultServerApiVersionParameters
                        return result |> renderOutput parseResult
                    })

    // Rename subcommand    
    type SetNameParameters() = 
        inherit CommonParameters()
        member val public NewName: string = String.Empty with get, set
    let private setNameHandler (parseResult: ParseResult) (parameters: SetNameParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations (parseResult, parameters)
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let setNameParameters = Repository.SetRepositoryNameParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        CorrelationId = normalizedParameters.CorrelationId,
                        NewName = normalizedParameters.NewName)

                    if parseResult |> hasOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.SetName(setNameParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.SetName(setNameParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetName =
        CommandHandler.Create(fun (parseResult: ParseResult) (setNameParameters: SetNameParameters) ->
                task {                
                    let! result = setNameHandler parseResult setNameParameters
                    return result |> renderOutput parseResult
                })

    // Delete subcommand
    type DeleteParameters() =
        inherit CommonParameters()
        member val public Force: Boolean = false with get, set
        member val public DeleteReason: string = String.Empty with get, set
    let private deleteHandler (parseResult: ParseResult) (parameters: DeleteParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations (parseResult, parameters)
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let enhancedParameters = Repository.DeleteRepositoryParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        CorrelationId = normalizedParameters.CorrelationId,
                        Force = normalizedParameters.Force,
                        DeleteReason = normalizedParameters.DeleteReason)

                    if parseResult |> hasOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.Delete(enhancedParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.Delete(enhancedParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private Delete =
        CommandHandler.Create(fun (parseResult: ParseResult) (deleteParameters: DeleteParameters) ->
                task {                
                    let! result = deleteHandler parseResult deleteParameters
                    return result |> renderOutput parseResult
                })

    // Delete subcommand
    type UndeleteParameters() =
        inherit CommonParameters()
    let private undeleteHandler (parseResult: ParseResult) (parameters: UndeleteParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations (parseResult, parameters)
                match validateIncomingParameters with
                | Ok _ -> 
                    let normalizedParameters = parameters |> normalizeIdsAndNames parseResult
                    let undeleteParameters = Repository.UndeleteRepositoryParameters(
                        OwnerId = normalizedParameters.OwnerId,
                        OwnerName = normalizedParameters.OwnerName,
                        OrganizationId = normalizedParameters.OrganizationId,
                        OrganizationName = normalizedParameters.OrganizationName,
                        RepositoryId = normalizedParameters.RepositoryId, 
                        RepositoryName = normalizedParameters.RepositoryName,
                        CorrelationId = normalizedParameters.CorrelationId)

                    if parseResult |> hasOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Repository.Undelete(undeleteParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Repository.Undelete(undeleteParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private Undelete =
        CommandHandler.Create(fun (parseResult: ParseResult) (undeleteParameters: UndeleteParameters) ->
                task {                
                    let! result = undeleteHandler parseResult undeleteParameters
                    return result |> renderOutput parseResult
                })

    /// Builds the Repository subcommand.
    let Build = 
        let addCommonOptionsExceptForRepositoryInfo (command: Command) =
            command |> addOption Options.ownerName
                    |> addOption Options.ownerId
                    |> addOption Options.organizationName
                    |> addOption Options.organizationId
                    |> addOption Options.repositoryId

        let addCommonOptions (command: Command) =
            command |> addOption Options.repositoryName |> addCommonOptionsExceptForRepositoryInfo

        // Create main command and aliases, if any.
        let repositoryCommand = new Command("repository", Description = "Create, change, or delete repository-level information.")
        repositoryCommand.AddAlias("repo")

        // Add subcommands.
        let repositoryCreateCommand = new Command("create", Description = "Create a new repository.") |> addOption Options.requiredRepositoryName |> addCommonOptionsExceptForRepositoryInfo |> addOption Options.doNotSwitch
        repositoryCreateCommand.Handler <- Create
        repositoryCommand.AddCommand(repositoryCreateCommand)

        let repositoryGetCommand = new Command("get", Description = "Get information about a repository.") |> addCommonOptions
        repositoryGetCommand.Handler <- Get
        repositoryCommand.AddCommand(repositoryGetCommand)

        let repositoryInitCommand = new Command("init", Description = "Initializes a new repository with the contents of a directory.") |> addOption Options.directory |> addOption Options.graceConfig |> addCommonOptions
        repositoryInitCommand.Handler <- Init
        repositoryCommand.AddCommand(repositoryInitCommand)

        //let repositoryDownloadCommand = new Command("download", Description = "Downloads the current version of the repository.") |> addOption Options.requiredRepositoryName |> addOption Options.graceConfig |> addCommonOptionsExceptForRepositoryInfo
        //repositoryInitCommand.Handler <- Init
        //repositoryCommand.AddCommand(repositoryInitCommand)

        let getBranchesCommand = new Command("get-branches", Description = "Gets a list of branches in the repository.") |> addOption Options.includeDeleted |> addCommonOptions
        getBranchesCommand.Handler <- GetBranches
        repositoryCommand.AddCommand(getBranchesCommand)

        let setVisibilityCommand = new Command("set-visibility", Description = "Set the visibility of the repository.") |> addOption Options.visibility |> addCommonOptions
        setVisibilityCommand.Handler <- SetVisibility
        repositoryCommand.AddCommand(setVisibilityCommand)

        let setStatusCommand = new Command("set-status", Description = "Set the status of the repository.") |> addOption Options.status |> addCommonOptions
        setStatusCommand.Handler <- SetStatus
        repositoryCommand.AddCommand(setStatusCommand)

        let setRecordSavesCommand = new Command("set-recordsaves", Description = "Set whether the repository defaults to recording every save.") |> addOption Options.recordSaves |> addCommonOptions
        setRecordSavesCommand.Handler <- SetRecordSaves
        repositoryCommand.AddCommand(setRecordSavesCommand)

        let setDefaultServerApiVersionCommand = new Command("set-defaultserverapiversion", Description = "Sets the default server API version for clients to use when accessing this repository.") |> addOption Options.defaultServerApiVersion |> addCommonOptions
        setDefaultServerApiVersionCommand.Handler <- SetDefaultServerApiVersion
        repositoryCommand.AddCommand(setDefaultServerApiVersionCommand)

        let setSaveDaysCommand = new Command("set-savedays", Description = "Set how long to keep saves in the repository.") |> addOption Options.saveDays |> addCommonOptions
        setSaveDaysCommand.Handler <- SetSaveDays
        repositoryCommand.AddCommand(setSaveDaysCommand)
        
        let setCheckpointDaysCommand = new Command("set-checkpointdays", Description = "Set how long to keep checkpoints in the repository.") |> addOption Options.checkpointDays |> addCommonOptions
        setCheckpointDaysCommand.Handler <- SetCheckpointDays
        repositoryCommand.AddCommand(setCheckpointDaysCommand)

        let setNameCommand = new Command("set-name", Description = "Set the name of the repository.") |> addOption Options.newName |> addCommonOptions
        setNameCommand.Handler <- SetName
        repositoryCommand.AddCommand(setNameCommand)

        let deleteCommand = new Command("delete", Description = "Delete a repository.") |> addOption Options.deleteReason |> addOption Options.force |> addCommonOptions
        deleteCommand.Handler <- Delete
        repositoryCommand.AddCommand(deleteCommand)

        let undeleteCommand = new Command("undelete", Description = "Undeletes the repository.") |> addCommonOptions
        undeleteCommand.Handler <- Undelete
        repositoryCommand.AddCommand(undeleteCommand)

        repositoryCommand
