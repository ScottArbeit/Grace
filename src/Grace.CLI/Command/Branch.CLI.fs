namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Client.Theme
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Reference
open Grace.Shared.Parameters.Branch
open Grace.Shared.Parameters.DirectoryVersion
open Grace.Shared.Services
open Grace.Shared.Resources
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors.Branch
open NodaTime
open NodaTime.TimeZones
open Spectre.Console
open Spectre.Console.Json
open Spectre.Console.Rendering
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.NamingConventionBinder
open System.CommandLine.Parsing
open System.Globalization
open System.IO
open System.IO.Enumeration
open System.Linq
open System.Security.Cryptography
open System.Threading.Tasks
open System.Text
open System.Text.Json
open Grace.Shared.Parameters

module Branch =
    open Grace.Shared.Validation.Common.Input

    type CommonParameters() =
        inherit ParameterBase()
        member val public BranchId: string = String.Empty with get, set
        member val public BranchName: string = String.Empty with get, set
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public OrganizationName: string = String.Empty with get, set
        member val public RepositoryId: string = String.Empty with get, set
        member val public RepositoryName: string = String.Empty with get, set

    module private Options =
        let validateGuid (validate: OptionResult) =
            let mutable guid = Guid.Empty

            if Guid.TryParse(validate.GetValueOrDefault<String>(), &guid) = false then
                validate.ErrorMessage <- BranchError.getErrorMessage InvalidBranchId

        let branchId =
            new Option<String>(
                [| "--branchId"; "-i" |],
                IsRequired = false,
                Description = "The branch's ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                getDefaultValue =
                    (fun _ ->
                        if Current().BranchId = Guid.Empty then
                            $"{Guid.NewGuid()}"
                        else
                            $"{Current().BranchId}")
            )

        let branchName =
            new Option<String>(
                [| "--branchName"; "-b" |],
                IsRequired = false,
                Description = "The name of the branch. [default: current branch]",
                Arity = ArgumentArity.ExactlyOne
            )

        let branchNameRequired =
            new Option<String>([| "--branchName"; "-b" |], IsRequired = true, Description = "The name of the branch.", Arity = ArgumentArity.ExactlyOne)

        let ownerId =
            new Option<String>(
                "--ownerId",
                IsRequired = false,
                Description = "The repository's owner ID <Guid>.",
                Arity = ArgumentArity.ZeroOrOne,
                getDefaultValue =
                    (fun _ ->
                        if Current().OwnerId = Guid.Empty then
                            $"{Guid.NewGuid()}"
                        else
                            $"{Current().OwnerId}")
            )

        let ownerName =
            new Option<String>(
                "--ownerName",
                IsRequired = false,
                Description = "The repository's owner name. [default: current owner]",
                Arity = ArgumentArity.ExactlyOne
            )

        let organizationId =
            new Option<String>(
                "--organizationId",
                IsRequired = false,
                Description = "The repository's organization ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                getDefaultValue =
                    (fun _ ->
                        if Current().OrganizationId = Guid.Empty then
                            $"{Guid.NewGuid()}"
                        else
                            $"{Current().OrganizationId}")
            )

        let organizationName =
            new Option<String>(
                "--organizationName",
                IsRequired = false,
                Description = "The repository's organization name. [default: current organization]",
                Arity = ArgumentArity.ZeroOrOne
            )

        let repositoryId =
            new Option<String>(
                [| "--repositoryId"; "-r" |],
                IsRequired = false,
                Description = "The repository's ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                getDefaultValue =
                    (fun _ ->
                        if Current().RepositoryId = Guid.Empty then
                            $"{Guid.NewGuid()}"
                        else
                            $"{Current().RepositoryId}")
            )

        let repositoryName =
            new Option<String>(
                [| "--repositoryName"; "-n" |],
                IsRequired = false,
                Description = "The name of the repository. [default: current repository]",
                Arity = ArgumentArity.ExactlyOne
            )

        let parentBranchId =
            new Option<String>([| "--parentBranchId" |], IsRequired = false, Description = "The parent branch's ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let parentBranchName =
            new Option<String>(
                [| "--parentBranchName" |],
                IsRequired = false,
                Description = "The name of the parent branch. [default: current branch]",
                Arity = ArgumentArity.ExactlyOne,
                getDefaultValue = (fun _ -> $"{Current().BranchName}")
            )

        let newName = new Option<String>("--newName", IsRequired = true, Description = "The new name of the branch.", Arity = ArgumentArity.ExactlyOne)

        let message =
            new Option<String>(
                [| "--message"; "-m" |],
                IsRequired = false,
                Description = "The text to store with this reference.",
                Arity = ArgumentArity.ExactlyOne
            )

        let messageRequired =
            new Option<String>(
                [| "--message"; "-m" |],
                IsRequired = true,
                Description = "The text to store with this reference.",
                Arity = ArgumentArity.ExactlyOne
            )

        let referenceType =
            (new Option<String>("--referenceType", IsRequired = false, Description = "The type of reference.", Arity = ArgumentArity.ExactlyOne))
                .FromAmong(listCases<ReferenceType> ())

        let doNotSwitch =
            new Option<bool>(
                "--doNotSwitch",
                IsRequired = false,
                Description = "Do not switch to the new branch as the current branch.",
                Arity = ArgumentArity.ZeroOrOne
            )

        let fullSha = new Option<bool>("--fullSha", IsRequired = false, Description = "Show the full SHA-256 value in output.", Arity = ArgumentArity.ZeroOrOne)

        let maxCount =
            new Option<int>("--maxCount", IsRequired = false, Description = "The maximum number of results to return.", Arity = ArgumentArity.ExactlyOne)

        maxCount.SetDefaultValue(30)

        let referenceId =
            new Option<String>([| "--referenceId" |], IsRequired = false, Description = "The reference ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let sha256Hash =
            new Option<String>(
                [| "--sha256Hash" |],
                IsRequired = false,
                Description = "The full or partial SHA-256 hash value of the version.",
                Arity = ArgumentArity.ExactlyOne
            )

        let enabled =
            new Option<bool>("--enabled", IsRequired = false, Description = "True to enable the feature; false to disable it.", Arity = ArgumentArity.ZeroOrOne)

        let includeDeleted =
            new Option<bool>([| "--include-deleted"; "-d" |], IsRequired = false, Description = "Include deleted branches in the result. [default: false]")

        let showEvents = new Option<bool>([| "--show-events"; "-e" |], IsRequired = false, Description = "Include actor events in the result. [default: false]")

        let initialPermissions =
            new Option<ReferenceType array>(
                "--initialPermissions",
                IsRequired = false,
                Description = "A list of reference types allowed in this branch.",
                Arity = ArgumentArity.ZeroOrOne,
                getDefaultValue = (fun _ -> [| Commit; Checkpoint; Save; Tag; External |])
            )

        let toBranchId =
            new Option<String>(
                [| "--toBranchId"; "-d" |],
                IsRequired = false,
                Description = "The ID of the branch to switch to <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let toBranchName =
            new Option<String>(
                [| "--toBranchName"; "-c" |],
                IsRequired = false,
                Description = "The name of the branch to switch to.",
                Arity = ArgumentArity.ExactlyOne
            )

        let forceRecompute =
            new Option<bool>(
                "--forceRecompute",
                IsRequired = false,
                Description = "Force the re-computation of the recursive directory contents. [default: false]",
                Arity = ArgumentArity.ZeroOrOne
            )

        let directoryVersionId =
            new Option<String>(
                [| "--directoryVersionId"; "-v" |],
                IsRequired = false,
                Description = "The directory version ID to assign to the promotion <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )
    //let listDirectories = new Option<bool>("--listDirectories", IsRequired = false, Description = "Show directories when listing contents. [default: false]")
    //let listFiles = new Option<bool>("--listFiles", IsRequired = false, Description = "Show files when listing contents. Implies --listDirectories. [default: false]")

    let mustBeAValidGuid (parseResult: ParseResult) (parameters: CommonParameters) (option: Option) (value: string) (error: BranchError) =
        let mutable guid = Guid.Empty

        if
            parseResult.CommandResult.FindResultFor(option) <> null
            && not <| String.IsNullOrEmpty(value)
            && (Guid.TryParse(value, &guid) = false || guid = Guid.Empty)
        then
            Error(GraceError.Create (BranchError.getErrorMessage error) (parameters.CorrelationId))
        else
            Ok(parseResult, parameters)

    let mustBeAValidGraceName (parseResult: ParseResult) (parameters: CommonParameters) (option: Option) (value: string) (error: BranchError) =
        if
            parseResult.CommandResult.FindResultFor(option) <> null
            && not <| Constants.GraceNameRegex.IsMatch(value)
        then
            Error(GraceError.Create (BranchError.getErrorMessage error) (parameters.CorrelationId))
        else
            Ok(parseResult, parameters)

    let oneOfTheseOptionsMustBeProvided (parseResult: ParseResult) (parameters: CommonParameters) (options: Option array) (error: BranchError) =
        match
            options
            |> Array.tryFind (fun opt -> not <| isNull (parseResult.CommandResult.FindResultFor(opt)))
        with
        | Some opt -> Ok(parseResult, parameters)
        | None -> Error(GraceError.Create (BranchError.getErrorMessage error) (parameters.CorrelationId))

    let private CommonValidations parseResult commonParameters =
        let ``BranchId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGuid parseResult commonParameters Options.branchId commonParameters.BranchId InvalidBranchId

        let ``BranchName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGraceName parseResult commonParameters Options.branchName commonParameters.BranchName InvalidBranchName

        let ``OwnerId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGuid parseResult commonParameters Options.ownerId commonParameters.OwnerId InvalidOwnerId

        let ``OwnerName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGraceName parseResult commonParameters Options.ownerName commonParameters.OwnerName InvalidOwnerName

        let ``OrganizationId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGuid parseResult commonParameters Options.organizationId commonParameters.OrganizationId InvalidOrganizationId

        let ``OrganizationName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGraceName parseResult commonParameters Options.organizationName commonParameters.OrganizationName InvalidOrganizationName

        let ``RepositoryId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGuid parseResult commonParameters Options.repositoryId commonParameters.RepositoryId InvalidRepositoryId

        let ``RepositoryName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGraceName parseResult commonParameters Options.repositoryName commonParameters.RepositoryName InvalidRepositoryName

        let ``Grace index file must exist`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if not <| File.Exists(Current().GraceStatusFile) then
                Error(GraceError.Create (BranchError.getErrorMessage IndexFileNotFound) commonParameters.CorrelationId)
            else
                Ok(parseResult, commonParameters)

        let ``Grace object cache file must exist`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if not <| File.Exists(Current().GraceStatusFile) then
                Error(GraceError.Create (BranchError.getErrorMessage ObjectCacheFileNotFound) commonParameters.CorrelationId)
            else
                Ok(parseResult, commonParameters)

        (parseResult, commonParameters)
        |> ``BranchId must be a Guid``
        >>= ``BranchName must be a valid Grace name``
        >>= ``OwnerId must be a Guid``
        >>= ``OwnerName must be a valid Grace name``
        >>= ``OrganizationId must be a Guid``
        >>= ``OrganizationName must be a valid Grace name``
        >>= ``RepositoryId must be a Guid``
        >>= ``RepositoryName must be a valid Grace name``
        >>= ``Grace index file must exist``
        >>= ``Grace object cache file must exist``

    let private ``BranchName must not be empty`` (parseResult: ParseResult, commonParameters: CommonParameters) =
        if
            (parseResult.HasOption(Options.branchNameRequired)
             || parseResult.HasOption(Options.branchName))
            && not <| String.IsNullOrEmpty(commonParameters.BranchName)
        then
            Ok(parseResult, commonParameters)
        else
            Error(GraceError.Create (BranchError.getErrorMessage BranchNameIsRequired) (commonParameters.CorrelationId))

    /// Adjusts parameters to account for whether Id's or Name's were specified by the user, or should be taken from default values.
    let normalizeIdsAndNames<'T when 'T :> CommonParameters> (parseResult: ParseResult) (parameters: 'T) =
        // If the name was specified on the command line, but the id wasn't, then we should only send the name, and we set the id to String.Empty.
        if
            parseResult.CommandResult.FindResultFor(Options.ownerId).IsImplicit
            && not <| isNull (parseResult.CommandResult.FindResultFor(Options.ownerName))
            && not <| parseResult.CommandResult.FindResultFor(Options.ownerName).IsImplicit
        then
            parameters.OwnerId <- String.Empty

        if
            parseResult.CommandResult.FindResultFor(Options.organizationId).IsImplicit
            && not
               <| isNull (parseResult.CommandResult.FindResultFor(Options.organizationName))
            && not
               <| parseResult.CommandResult.FindResultFor(Options.organizationName).IsImplicit
        then
            parameters.OrganizationId <- String.Empty

        if
            parseResult.CommandResult.FindResultFor(Options.repositoryId).IsImplicit
            && not <| isNull (parseResult.CommandResult.FindResultFor(Options.repositoryName))
            && not
               <| parseResult.CommandResult.FindResultFor(Options.repositoryName).IsImplicit
        then
            parameters.RepositoryId <- String.Empty

        if
            parseResult.CommandResult.FindResultFor(Options.branchId).IsImplicit
            && not <| isNull (parseResult.CommandResult.FindResultFor(Options.branchName))
            && not <| parseResult.CommandResult.FindResultFor(Options.branchName).IsImplicit
        then
            parameters.BranchId <- String.Empty

        parameters

    /// Populates OwnerId, OrganizationId, RepositoryId, and BranchId based on the command parameters and the contents of graceconfig.json.
    let getIds<'T when 'T :> CommonParameters> (parameters: 'T) =
        let ownerId =
            if
                not <| String.IsNullOrEmpty(parameters.OwnerId)
                || not <| String.IsNullOrEmpty(parameters.OwnerName)
            then
                parameters.OwnerId
            else
                $"{Current().OwnerId}"

        let organizationId =
            if
                not <| String.IsNullOrEmpty(parameters.OrganizationId)
                || not <| String.IsNullOrEmpty(parameters.OrganizationName)
            then
                parameters.OrganizationId
            else
                $"{Current().OrganizationId}"

        let repositoryId =
            if
                not <| String.IsNullOrEmpty(parameters.RepositoryId)
                || not <| String.IsNullOrEmpty(parameters.RepositoryName)
            then
                parameters.RepositoryId
            else
                $"{Current().RepositoryId}"

        let branchId =
            if
                not <| String.IsNullOrEmpty(parameters.BranchId)
                || not <| String.IsNullOrEmpty(parameters.BranchName)
            then
                parameters.BranchId
            else
                $"{Current().BranchId}"

        (ownerId, organizationId, repositoryId, branchId)

    // Create subcommand.
    type CreateParameters() =
        inherit CommonParameters()
        member val public ParentBranchId = String.Empty with get, set
        member val public ParentBranchName = String.Empty with get, set
        member val public InitialPermissions = Array.Empty<ReferenceType>() with get, set

    let private createHandler (parseResult: ParseResult) (createParameters: CreateParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters =
                    CommonValidations parseResult createParameters
                    >>= ``BranchName must not be empty``

                match validateIncomingParameters with
                | Ok _ ->
                    let branchId =
                        if parseResult.FindResultFor(Options.branchId).IsImplicit then
                            Guid.NewGuid().ToString()
                        else
                            createParameters.BranchId

                    let parameters =
                        CreateBranchParameters(
                            RepositoryId = createParameters.RepositoryId,
                            RepositoryName = createParameters.RepositoryName,
                            OwnerId = createParameters.OwnerId,
                            OwnerName = createParameters.OwnerName,
                            OrganizationId = createParameters.OrganizationId,
                            OrganizationName = createParameters.OrganizationName,
                            BranchId = branchId,
                            BranchName = createParameters.BranchName,
                            ParentBranchId = createParameters.ParentBranchId,
                            ParentBranchName = createParameters.ParentBranchName,
                            InitialPermissions = createParameters.InitialPermissions,
                            CorrelationId = createParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Branch.Create(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Branch.Create(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private Create =
        CommandHandler.Create(fun (parseResult: ParseResult) (createParameters: CreateParameters) ->
            task {
                let! result = createHandler parseResult createParameters

                match result with
                | Ok returnValue ->
                    // Update the Grace configuration file with the newly-created branch.
                    if not <| parseResult.HasOption(Options.doNotSwitch) then
                        let newConfig = Current()
                        newConfig.BranchId <- Guid.Parse(returnValue.Properties[nameof (BranchId)])
                        newConfig.BranchName <- BranchName(returnValue.Properties[nameof (BranchName)])
                        updateConfiguration newConfig
                | Error _ -> ()

                return result |> renderOutput parseResult
            })

    type GetRecursiveSizeParameters() =
        inherit CommonParameters()
        member val public Sha256Hash: Sha256Hash = String.Empty with get, set
        member val public ReferenceId = String.Empty with get, set
        member val public Pattern = String.Empty with get, set
        member val public ShowDirectories = true with get, set
        member val public ShowFiles = true with get, set

    let getRecursiveSizeHandler (parseResult: ParseResult) (getRecursiveSizeParameters: GetRecursiveSizeParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations parseResult getRecursiveSizeParameters

                match validateIncomingParameters with
                | Ok _ ->
                    let sdkParameters =
                        Parameters.Branch.ListContentsParameters(
                            RepositoryId = getRecursiveSizeParameters.RepositoryId,
                            RepositoryName = getRecursiveSizeParameters.RepositoryName,
                            OwnerId = getRecursiveSizeParameters.OwnerId,
                            OwnerName = getRecursiveSizeParameters.OwnerName,
                            OrganizationId = getRecursiveSizeParameters.OrganizationId,
                            OrganizationName = getRecursiveSizeParameters.OrganizationName,
                            BranchId = getRecursiveSizeParameters.BranchId,
                            BranchName = getRecursiveSizeParameters.BranchName,
                            Sha256Hash = getRecursiveSizeParameters.Sha256Hash,
                            ReferenceId = getRecursiveSizeParameters.ReferenceId,
                            Pattern = getRecursiveSizeParameters.Pattern,
                            ShowDirectories = getRecursiveSizeParameters.ShowDirectories,
                            ShowFiles = getRecursiveSizeParameters.ShowFiles,
                            CorrelationId = getRecursiveSizeParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Branch.GetRecursiveSize(sdkParameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Branch.GetRecursiveSize(sdkParameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private GetRecursiveSize =
        CommandHandler.Create(fun (parseResult: ParseResult) (getRecursiveSizeParameters: GetRecursiveSizeParameters) ->
            task {
                let! result = getRecursiveSizeHandler parseResult getRecursiveSizeParameters

                match result with
                | Ok returnValue -> AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Total file size: {returnValue.ReturnValue:N0}[/]"
                | Error error -> AnsiConsole.MarkupLine $"[{Colors.Error}]{error}[/]"

                return result |> renderOutput parseResult
            })

    type ListContentsParameters() =
        inherit CommonParameters()
        member val public Sha256Hash: Sha256Hash = String.Empty with get, set
        member val public ReferenceId = String.Empty with get, set
        member val public Pattern = String.Empty with get, set
        member val public ShowDirectories = true with get, set
        member val public ShowFiles = true with get, set
        member val public ForceRecompute = false with get, set

    let printContents (parseResult: ParseResult) (directoryVersions: IEnumerable<DirectoryVersion>) =
        let longestRelativePath =
            getLongestRelativePath (
                directoryVersions
                |> Seq.map (fun directoryVersion -> directoryVersion.ToLocalDirectoryVersion(DateTime.UtcNow))
            )
        //logToAnsiConsole Colors.Verbose $"In printContents: getLongestRelativePath: {longestRelativePath}"
        let additionalSpaces = String.replicate (longestRelativePath - 2) " "
        let additionalImportantDashes = String.replicate (longestRelativePath + 3) "-"
        let additionalDeemphasizedDashes = String.replicate (38) "-"

        directoryVersions
        |> Seq.iteri (fun i directoryVersion ->
            AnsiConsole.WriteLine()

            if i = 0 then
                AnsiConsole.MarkupLine(
                    $"[{Colors.Important}]Created At                   SHA-256            Size  Path{additionalSpaces}[/][{Colors.Deemphasized}] (DirectoryVersionId)[/]"
                )

                AnsiConsole.MarkupLine(
                    $"[{Colors.Important}]-----------------------------------------------------{additionalImportantDashes}[/][{Colors.Deemphasized}] {additionalDeemphasizedDashes}[/]"
                )
            //logToAnsiConsole Colors.Verbose $"In printContents: directoryVersion.RelativePath: {directoryVersion.RelativePath}"
            let rightAlignedDirectoryVersionId =
                (String.replicate (longestRelativePath - directoryVersion.RelativePath.Length) " ")
                + $"({directoryVersion.DirectoryVersionId})"

            AnsiConsole.MarkupLine(
                $"[{Colors.Highlighted}]{formatInstantAligned directoryVersion.CreatedAt}   {getShortSha256Hash directoryVersion.Sha256Hash}  {directoryVersion.Size, 13:N0}  /{directoryVersion.RelativePath}[/] [{Colors.Deemphasized}] {rightAlignedDirectoryVersionId}[/]"
            )
            //if parseResult.HasOption(Options.listFiles) then
            let sortedFiles = directoryVersion.Files.OrderBy(fun f -> f.RelativePath)

            for file in sortedFiles do
                AnsiConsole.MarkupLine(
                    $"[{Colors.Verbose}]{formatInstantAligned file.CreatedAt}   {getShortSha256Hash file.Sha256Hash}  {file.Size, 13:N0}  |- {file.RelativePath.Split('/').LastOrDefault()}[/]"
                ))

    let private listContentsHandler (parseResult: ParseResult) (listContentsParameters: ListContentsParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations parseResult listContentsParameters

                match validateIncomingParameters with
                | Ok _ ->
                    let sdkParameters =
                        Parameters.Branch.ListContentsParameters(
                            RepositoryId = listContentsParameters.RepositoryId,
                            RepositoryName = listContentsParameters.RepositoryName,
                            OwnerId = listContentsParameters.OwnerId,
                            OwnerName = listContentsParameters.OwnerName,
                            OrganizationId = listContentsParameters.OrganizationId,
                            OrganizationName = listContentsParameters.OrganizationName,
                            BranchId = listContentsParameters.BranchId,
                            BranchName = listContentsParameters.BranchName,
                            Sha256Hash = listContentsParameters.Sha256Hash,
                            ReferenceId = listContentsParameters.ReferenceId,
                            Pattern = listContentsParameters.Pattern,
                            ShowDirectories = listContentsParameters.ShowDirectories,
                            ShowFiles = listContentsParameters.ShowFiles,
                            ForceRecompute = listContentsParameters.ForceRecompute,
                            CorrelationId = listContentsParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Branch.ListContents(sdkParameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Branch.ListContents(sdkParameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private ListContents =
        CommandHandler.Create(fun (parseResult: ParseResult) (listFileParameters: ListContentsParameters) ->
            task {
                let! result = listContentsHandler parseResult (listFileParameters |> normalizeIdsAndNames parseResult)

                match result with
                | Ok returnValue ->
                    let! graceStatus = readGraceStatusFile ()

                    let directoryVersions = returnValue.ReturnValue |> Seq.sortBy (fun dv -> dv.RelativePath)

                    let directoryCount = directoryVersions.Count()

                    let fileCount =
                        directoryVersions
                            .Select(fun directoryVersion -> directoryVersion.Files.Count)
                            .Sum()

                    let totalFileSize = directoryVersions.Sum(fun directoryVersion -> directoryVersion.Files.Sum(fun f -> f.Size))

                    let rootDirectoryVersion = directoryVersions.First(fun d -> d.RelativePath = Constants.RootDirectoryPath)

                    AnsiConsole.MarkupLine($"[{Colors.Important}]All values taken from the selected version of this branch from the server.[/]")

                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of directories: {directoryCount}.[/]")

                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of files: {fileCount}; total file size: {totalFileSize:N0}.[/]")

                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Root SHA-256 hash: {rootDirectoryVersion.Sha256Hash.Substring(0, 8)}[/]")

                    printContents parseResult directoryVersions
                    return result |> renderOutput parseResult
                | Error error -> return result |> renderOutput parseResult
            })

    type SetNameParameters() =
        inherit CommonParameters()
        member val public NewName = String.Empty with get, set

    let private setNameHandler (parseResult: ParseResult) (setNameParameters: SetNameParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations parseResult setNameParameters

                match validateIncomingParameters with
                | Ok _ ->
                    let parameters =
                        Parameters.Branch.SetBranchNameParameters(
                            BranchId = setNameParameters.BranchId,
                            BranchName = setNameParameters.BranchName,
                            NewName = setNameParameters.NewName,
                            CorrelationId = setNameParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Branch.SetName(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Branch.SetName(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private SetName =
        CommandHandler.Create(fun (parseResult: ParseResult) (setNameParameters: SetNameParameters) ->
            task {
                let! result = setNameHandler parseResult (setNameParameters |> normalizeIdsAndNames parseResult)
                return result |> renderOutput parseResult
            })

    type AssignParameters() =
        inherit CommonParameters()
        member val public DirectoryVersionId: DirectoryVersionId = Guid.Empty with get, set
        member val public Sha256Hash: Sha256Hash = String.Empty with get, set

    let assignHandler (parseResult: ParseResult) (assignParameters: AssignParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations parseResult assignParameters

                let localValidations =
                    oneOfTheseOptionsMustBeProvided
                        parseResult
                        assignParameters
                        [| Options.directoryVersionId; Options.sha256Hash |]
                        EitherDirectoryVersionIdOrSha256HashRequired

                match validateIncomingParameters with
                | Ok _ ->
                    let parameters =
                        Parameters.Branch.AssignParameters(
                            OwnerId = assignParameters.OwnerId,
                            OwnerName = assignParameters.OwnerName,
                            OrganizationId = assignParameters.OrganizationId,
                            OrganizationName = assignParameters.OrganizationName,
                            RepositoryId = assignParameters.RepositoryId,
                            RepositoryName = assignParameters.RepositoryName,
                            BranchId = assignParameters.BranchId,
                            BranchName = assignParameters.BranchName,
                            DirectoryVersionId = assignParameters.DirectoryVersionId,
                            Sha256Hash = assignParameters.Sha256Hash,
                            CorrelationId = assignParameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Branch.Assign(parameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Branch.Assign(parameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private Assign =
        CommandHandler.Create(fun (parseResult: ParseResult) (assignParameters: AssignParameters) ->
            task {
                let! result = assignHandler parseResult (assignParameters |> normalizeIdsAndNames parseResult)
                return result |> renderOutput parseResult
            })

    type CreateReferenceCommand = CreateReferenceParameters -> Task<GraceResult<String>>

    type CreateRefParameters() =
        inherit CommonParameters()
        member val public DirectoryVersion = DirectoryVersion.Default with get, set
        member val public Message = String.Empty with get, set

    let createReferenceHandler (parseResult: ParseResult) (parameters: CreateRefParameters) (command: CreateReferenceCommand) (commandType: string) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations parseResult parameters
                let repositoryId = RepositoryId.Parse(parameters.RepositoryId)

                match validateIncomingParameters with
                | Ok _ ->
                    //let sha256Bytes = SHA256.HashData(Encoding.ASCII.GetBytes(rnd.NextInt64().ToString("x8")))
                    //let sha256Hash = Seq.fold (fun (sb: StringBuilder) currentByte ->
                    //    sb.Append(sprintf $"{currentByte:X2}")) (StringBuilder(sha256Bytes.Length)) sha256Bytes
                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading Grace status file.[/]")

                                        let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Scanning working directory for changes.[/]", autoStart = false)

                                        let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Creating new directory verions.[/]", autoStart = false)

                                        let t3 =
                                            progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading changed files to object storage.[/]", autoStart = false)

                                        let t4 = progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading new directory versions.[/]", autoStart = false)

                                        let t5 = progressContext.AddTask($"[{Color.DodgerBlue1}]Creating new {commandType}.[/]", autoStart = false)

                                        //let mutable rootDirectoryId = DirectoryId.Empty
                                        //let mutable rootDirectorySha256Hash = Sha256Hash String.Empty
                                        let rootDirectoryVersion = ref (DirectoryVersionId.Empty, Sha256Hash String.Empty)

                                        match! getGraceWatchStatus () with
                                        | Some graceWatchStatus ->
                                            t0.Value <- 100.0
                                            t1.Value <- 100.0
                                            t2.Value <- 100.0
                                            t3.Value <- 100.0
                                            t4.Value <- 100.0

                                            rootDirectoryVersion.Value <- (graceWatchStatus.RootDirectoryId, graceWatchStatus.RootDirectorySha256Hash)
                                        | None ->
                                            t0.StartTask() // Read Grace status file.
                                            let! previousGraceStatus = readGraceStatusFile ()
                                            let mutable newGraceStatus = previousGraceStatus
                                            t0.Value <- 100.0

                                            t1.StartTask() // Scan for differences.
                                            let! differences = scanForDifferences previousGraceStatus
                                            //logToAnsiConsole Colors.Verbose $"differences: {serialize differences}"
                                            let! newFileVersions = copyUpdatedFilesToObjectCache t1 differences
                                            //logToAnsiConsole Colors.Verbose $"newFileVersions: {serialize newFileVersions}"
                                            t1.Value <- 100.0

                                            t2.StartTask() // Create new directory versions.

                                            let! (updatedGraceStatus, newDirectoryVersions) =
                                                getNewGraceStatusAndDirectoryVersions previousGraceStatus differences

                                            newGraceStatus <- updatedGraceStatus

                                            rootDirectoryVersion.Value <- (newGraceStatus.RootDirectoryId, newGraceStatus.RootDirectorySha256Hash)

                                            t2.Value <- 100.0

                                            t3.StartTask() // Upload to object storage.

                                            let updatedRelativePaths =
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

                                            // let newFileVersions = updatedRelativePaths.Select(fun relativePath ->
                                            //     newDirectoryVersions.First(fun dv -> dv.Files.Exists(fun file -> file.RelativePath = relativePath)).Files.First(fun file -> file.RelativePath = relativePath))

                                            let mutable lastFileUploadInstant = newGraceStatus.LastSuccessfulFileUpload

                                            if newFileVersions.Count() > 0 then
                                                let getUploadMetadataForFilesParameters =
                                                    Storage.GetUploadMetadataForFilesParameters(
                                                        OwnerId = parameters.OwnerId,
                                                        OwnerName = parameters.OwnerName,
                                                        OrganizationId = parameters.OrganizationId,
                                                        OrganizationName = parameters.OrganizationName,
                                                        RepositoryId = parameters.RepositoryId,
                                                        RepositoryName = parameters.RepositoryName,
                                                        CorrelationId = getCorrelationId parseResult,
                                                        FileVersions =
                                                            (newFileVersions
                                                             |> Seq.map (fun localFileVersion -> localFileVersion.ToFileVersion)
                                                             |> Seq.toArray)
                                                    )

                                                match! uploadFilesToObjectStorage getUploadMetadataForFilesParameters with
                                                | Ok returnValue -> () //logToAnsiConsole Colors.Verbose $"Uploaded all files to object storage."
                                                | Error error -> logToAnsiConsole Colors.Error $"Error uploading files to object storage: {error.Error}"

                                                lastFileUploadInstant <- getCurrentInstant ()

                                            t3.Value <- 100.0

                                            t4.StartTask() // Upload directory versions.

                                            let mutable lastDirectoryVersionUpload = newGraceStatus.LastSuccessfulDirectoryVersionUpload

                                            if newDirectoryVersions.Count > 0 then
                                                let saveParameters = SaveDirectoryVersionsParameters()
                                                saveParameters.OwnerId <- parameters.OwnerId
                                                saveParameters.OwnerName <- parameters.OwnerName
                                                saveParameters.OrganizationId <- parameters.OrganizationId
                                                saveParameters.OrganizationName <- parameters.OrganizationName
                                                saveParameters.RepositoryId <- parameters.RepositoryId
                                                saveParameters.RepositoryName <- parameters.RepositoryName
                                                saveParameters.DirectoryVersionId <- $"{newGraceStatus.RootDirectoryId}"

                                                saveParameters.DirectoryVersions <- newDirectoryVersions.Select(fun dv -> dv.ToDirectoryVersion).ToList()

                                                saveParameters.CorrelationId <- getCorrelationId parseResult

                                                let! uploadDirectoryVersions = DirectoryVersion.SaveDirectoryVersions saveParameters

                                                lastDirectoryVersionUpload <- getCurrentInstant ()

                                            t4.Value <- 100.0

                                            newGraceStatus <-
                                                { newGraceStatus with
                                                    LastSuccessfulFileUpload = lastFileUploadInstant
                                                    LastSuccessfulDirectoryVersionUpload = lastDirectoryVersionUpload }

                                            do! writeGraceStatusFile newGraceStatus

                                        t5.StartTask() // Create new reference.

                                        let (rootDirectoryId, rootDirectorySha256Hash) = rootDirectoryVersion.Value

                                        let sdkParameters =
                                            Parameters.Branch.CreateReferenceParameters(
                                                BranchId = parameters.BranchId,
                                                BranchName = parameters.BranchName,
                                                OwnerId = parameters.OwnerId,
                                                OwnerName = parameters.OwnerName,
                                                OrganizationId = parameters.OrganizationId,
                                                OrganizationName = parameters.OrganizationName,
                                                RepositoryId = parameters.RepositoryId,
                                                RepositoryName = parameters.RepositoryName,
                                                DirectoryVersionId = rootDirectoryId,
                                                Sha256Hash = rootDirectorySha256Hash,
                                                Message = parameters.Message,
                                                CorrelationId = parameters.CorrelationId
                                            )

                                        let! result = command sdkParameters
                                        t5.Value <- 100.0

                                        return result
                                    })
                    else
                        let! previousGraceStatus = readGraceStatusFile ()
                        let! differences = scanForDifferences previousGraceStatus

                        let! (newGraceIndex, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions previousGraceStatus differences

                        let updatedRelativePaths =
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

                        let newFileVersions =
                            updatedRelativePaths.Select(fun relativePath ->
                                newDirectoryVersions
                                    .First(fun dv -> dv.Files.Exists(fun file -> file.RelativePath = relativePath))
                                    .Files.First(fun file -> file.RelativePath = relativePath))

                        let getUploadMetadataForFilesParameters =
                            Storage.GetUploadMetadataForFilesParameters(
                                OwnerId = parameters.OwnerId,
                                OwnerName = parameters.OwnerName,
                                OrganizationId = parameters.OrganizationId,
                                OrganizationName = parameters.OrganizationName,
                                RepositoryId = parameters.RepositoryId,
                                RepositoryName = parameters.RepositoryName,
                                CorrelationId = getCorrelationId parseResult,
                                FileVersions =
                                    (newFileVersions
                                     |> Seq.map (fun localFileVersion -> localFileVersion.ToFileVersion)
                                     |> Seq.toArray)
                            )

                        let! uploadResult = uploadFilesToObjectStorage getUploadMetadataForFilesParameters
                        let saveParameters = SaveDirectoryVersionsParameters()
                        saveParameters.OwnerId <- parameters.OwnerId
                        saveParameters.OwnerName <- parameters.OwnerName
                        saveParameters.OrganizationId <- parameters.OrganizationId
                        saveParameters.OrganizationName <- parameters.OrganizationName
                        saveParameters.RepositoryId <- parameters.RepositoryId
                        saveParameters.RepositoryName <- parameters.RepositoryName
                        saveParameters.CorrelationId <- getCorrelationId parseResult
                        saveParameters.DirectoryVersions <- newDirectoryVersions.Select(fun dv -> dv.ToDirectoryVersion).ToList()

                        let! uploadDirectoryVersions = DirectoryVersion.SaveDirectoryVersions saveParameters
                        let rootDirectoryVersion = getRootDirectoryVersion previousGraceStatus

                        let sdkParameters =
                            Parameters.Branch.CreateReferenceParameters(
                                BranchId = parameters.BranchId,
                                BranchName = parameters.BranchName,
                                OwnerId = parameters.OwnerId,
                                OwnerName = parameters.OwnerName,
                                OrganizationId = parameters.OrganizationId,
                                OrganizationName = parameters.OrganizationName,
                                RepositoryId = parameters.RepositoryId,
                                RepositoryName = parameters.RepositoryName,
                                DirectoryVersionId = rootDirectoryVersion.DirectoryVersionId,
                                Sha256Hash = rootDirectoryVersion.Sha256Hash,
                                Message = parameters.Message,
                                CorrelationId = parameters.CorrelationId
                            )

                        let! result = command sdkParameters
                        return result
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let promotionHandler (parseResult: ParseResult) (parameters: CreateRefParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations parseResult parameters

                match validateIncomingParameters with
                | Ok _ ->
                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading Grace status file.[/]")

                                        let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Checking if the promotion is valid.[/]", autoStart = false)

                                        let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]", autoStart = false)

                                        // Read Grace status file.
                                        let! graceStatus = readGraceStatusFile ()
                                        let rootDirectoryId = graceStatus.RootDirectoryId
                                        let rootDirectorySha256Hash = graceStatus.RootDirectorySha256Hash
                                        t0.Value <- 100.0

                                        // Check if the promotion is valid; i.e. it's allowed by the ReferenceTypes enabled in the repository.
                                        t1.StartTask()
                                        // For single-step promotion, the current branch's latest commit will become the parent branch's next promotion.
                                        // If our current state is not the latest commit, print a warning message.

                                        // Get the Dto for the current branch. That will have its latest commit.
                                        let branchGetParameters =
                                            GetBranchParameters(
                                                BranchId = parameters.BranchId,
                                                BranchName = parameters.BranchName,
                                                OwnerId = parameters.OwnerId,
                                                OwnerName = parameters.OwnerName,
                                                OrganizationId = parameters.OrganizationId,
                                                OrganizationName = parameters.OrganizationName,
                                                RepositoryId = parameters.RepositoryId,
                                                RepositoryName = parameters.RepositoryName,
                                                CorrelationId = parameters.CorrelationId
                                            )

                                        let! branchResult = Branch.Get(branchGetParameters)

                                        match branchResult with
                                        | Ok branchReturnValue ->
                                            // If we succeeded, get the parent branch Dto. That will have its latest promotion.
                                            let! parentBranchResult = Branch.GetParentBranch(branchGetParameters)

                                            match parentBranchResult with
                                            | Ok parentBranchReturnValue ->
                                                // Yay, we have both Dto's.
                                                let branchDto = branchReturnValue.ReturnValue
                                                let parentBranchDto = parentBranchReturnValue.ReturnValue

                                                // Get the references for the latest commit and/or promotion on the current branch.
                                                //let getReferenceParameters =
                                                //    Parameters.Branch.GetReferenceParameters(BranchId = parameters.BranchId, BranchName = parameters.BranchName,
                                                //        OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                                //        OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                                //        RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                                                //        ReferenceId = $"{branchDto.LatestCommit}", CorrelationId = parameters.CorrelationId)
                                                //let! referenceResult = Branch.GetReference(getReferenceParameters)

                                                let referenceIds = List<ReferenceId>()

                                                if branchDto.LatestCommit <> ReferenceDto.Default then
                                                    referenceIds.Add(branchDto.LatestCommit.ReferenceId)

                                                if branchDto.LatestPromotion <> ReferenceDto.Default then
                                                    referenceIds.Add(branchDto.LatestPromotion.ReferenceId)

                                                if referenceIds.Count > 0 then
                                                    let getReferencesByReferenceIdParameters =
                                                        Parameters.Repository.GetReferencesByReferenceIdParameters(
                                                            OwnerId = parameters.OwnerId,
                                                            OwnerName = parameters.OwnerName,
                                                            OrganizationId = parameters.OrganizationId,
                                                            OrganizationName = parameters.OrganizationName,
                                                            RepositoryId = parameters.RepositoryId,
                                                            RepositoryName = parameters.RepositoryName,
                                                            ReferenceIds = referenceIds,
                                                            CorrelationId = parameters.CorrelationId
                                                        )

                                                    match! Repository.GetReferencesByReferenceId(getReferencesByReferenceIdParameters) with
                                                    | Ok returnValue ->
                                                        let references = returnValue.ReturnValue

                                                        let latestPromotableReference =
                                                            references.OrderByDescending(fun reference -> reference.CreatedAt).First()
                                                        // If the current branch's latest reference is not the latest commit - i.e. they've done more work in the branch
                                                        //   after the commit they're expecting to promote - print a warning.
                                                        //match getReferencesByReferenceIdResult with
                                                        //| Ok returnValue ->
                                                        //    let references = returnValue.ReturnValue
                                                        //    if referenceDto.DirectoryId <> graceStatus.RootDirectoryId then
                                                        //        logToAnsiConsole Colors.Important $"Note: the branch has been updated since the latest commit."
                                                        //| Error error -> () // I don't really care if this call fails, it's just a warning message.
                                                        t1.Value <- 100.0

                                                        // If the current branch is based on the parent's latest promotion, then we can proceed with the promotion.
                                                        if branchDto.BasedOn.ReferenceId = parentBranchDto.LatestPromotion.ReferenceId then
                                                            t2.StartTask()

                                                            let promotionParameters =
                                                                Parameters.Branch.CreateReferenceParameters(
                                                                    BranchId = $"{parentBranchDto.BranchId}",
                                                                    OwnerId = parameters.OwnerId,
                                                                    OwnerName = parameters.OwnerName,
                                                                    OrganizationId = parameters.OrganizationId,
                                                                    OrganizationName = parameters.OrganizationName,
                                                                    RepositoryId = parameters.RepositoryId,
                                                                    RepositoryName = parameters.RepositoryName,
                                                                    DirectoryVersionId = latestPromotableReference.DirectoryId,
                                                                    Sha256Hash = latestPromotableReference.Sha256Hash,
                                                                    Message = parameters.Message,
                                                                    CorrelationId = parameters.CorrelationId
                                                                )

                                                            let! promotionResult = Branch.Promote(promotionParameters)

                                                            match promotionResult with
                                                            | Ok returnValue ->
                                                                logToAnsiConsole Colors.Verbose $"Succeeded doing promotion."

                                                                let promotionReferenceId = returnValue.Properties["ReferenceId"]

                                                                let rebaseParameters =
                                                                    Parameters.Branch.RebaseParameters(
                                                                        BranchId = $"{branchDto.BranchId}",
                                                                        RepositoryId = $"{branchDto.RepositoryId}",
                                                                        OwnerId = parameters.OwnerId,
                                                                        OwnerName = parameters.OwnerName,
                                                                        OrganizationId = parameters.OrganizationId,
                                                                        OrganizationName = parameters.OrganizationName,
                                                                        BasedOn = (Guid.Parse(promotionReferenceId))
                                                                    )

                                                                let! rebaseResult = Branch.Rebase(rebaseParameters)
                                                                t2.Value <- 100.0

                                                                match rebaseResult with
                                                                | Ok returnValue ->
                                                                    logToAnsiConsole Colors.Verbose $"Succeeded doing rebase."

                                                                    return promotionResult
                                                                | Error error -> return Error error
                                                            | Error error ->
                                                                t2.Value <- 100.0
                                                                return Error error

                                                        else
                                                            return
                                                                Error(
                                                                    GraceError.Create
                                                                        (BranchError.getErrorMessage BranchIsNotBasedOnLatestPromotion)
                                                                        (parseResult |> getCorrelationId)
                                                                )
                                                    | Error error ->
                                                        t2.Value <- 100.0
                                                        return Error error
                                                else
                                                    return
                                                        Error(
                                                            GraceError.Create
                                                                (BranchError.getErrorMessage PromotionNotAvailableBecauseThereAreNoPromotableReferences)
                                                                (parseResult |> getCorrelationId)
                                                        )
                                            | Error error ->
                                                t1.Value <- 100.0
                                                return Error error
                                        | Error error ->
                                            t1.Value <- 100.0
                                            return Error error
                                    })
                    else
                        // Same result, with no output.
                        return Error(GraceError.Create "Need to implement the else clause." (parseResult |> getCorrelationId))
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private Promote =
        CommandHandler.Create(fun (parseResult: ParseResult) (createReferencesParameters: CreateRefParameters) ->
            task {
                try
                    let! result = promotionHandler parseResult (createReferencesParameters |> normalizeIdsAndNames parseResult)

                    return result |> renderOutput parseResult
                with ex ->
                    logToAnsiConsole Colors.Error (Markup.Escape($"{ExceptionResponse.Create ex}"))
                    return -1
            })

    let private Commit =
        CommandHandler.Create(fun (parseResult: ParseResult) (createReferencesParameters: CreateRefParameters) ->
            task {
                let command (parameters: CreateReferenceParameters) = task { return! Branch.Commit(parameters) }

                let! result =
                    createReferenceHandler
                        parseResult
                        (createReferencesParameters |> normalizeIdsAndNames parseResult)
                        command
                        (nameof(Commit).ToLowerInvariant())

                return result |> renderOutput parseResult
            })

    let private Checkpoint =
        CommandHandler.Create(fun (parseResult: ParseResult) (createReferencesParameters: CreateRefParameters) ->
            task {
                let command (parameters: CreateReferenceParameters) = task { return! Branch.Checkpoint(parameters) }

                let! result =
                    createReferenceHandler
                        parseResult
                        (createReferencesParameters |> normalizeIdsAndNames parseResult)
                        command
                        (nameof(Checkpoint).ToLowerInvariant())

                return result |> renderOutput parseResult
            })

    let private Save =
        CommandHandler.Create(fun (parseResult: ParseResult) (createReferencesParameters: CreateRefParameters) ->
            task {
                let command (parameters: CreateReferenceParameters) = task { return! Branch.Save(parameters) }

                let! result =
                    createReferenceHandler
                        parseResult
                        (createReferencesParameters |> normalizeIdsAndNames parseResult)
                        command
                        (nameof(Save).ToLowerInvariant())

                return result |> renderOutput parseResult
            })

    let private Tag =
        CommandHandler.Create(fun (parseResult: ParseResult) (createReferencesParameters: CreateRefParameters) ->
            task {
                let command (parameters: CreateReferenceParameters) = task { return! Branch.Tag(parameters) }

                let! result =
                    createReferenceHandler
                        parseResult
                        (createReferencesParameters |> normalizeIdsAndNames parseResult)
                        command
                        (nameof(Tag).ToLowerInvariant())

                return result |> renderOutput parseResult
            })

    let private CreateExternal =
        CommandHandler.Create(fun (parseResult: ParseResult) (createReferencesParameters: CreateRefParameters) ->
            task {
                let command (parameters: CreateReferenceParameters) = task { return! Branch.CreateExternal(parameters) }

                let! result =
                    createReferenceHandler parseResult (createReferencesParameters |> normalizeIdsAndNames parseResult) command ("External".ToLowerInvariant())

                return result |> renderOutput parseResult
            })

    type EnableFeatureCommand = EnableFeatureParameters -> Task<GraceResult<string>>

    type EnableFeatureParams() =
        inherit CommonParameters()
        member val public Enabled = false with get, set

    let enableFeatureHandler (parseResult: ParseResult) (parameters: EnableFeatureParams) (command: EnableFeatureCommand) (commandType: string) =
        task {
            let sdkParameters =
                Parameters.Branch.EnableFeatureParameters(
                    BranchId = parameters.BranchId,
                    BranchName = parameters.BranchName,
                    OwnerId = parameters.OwnerId,
                    OwnerName = parameters.OwnerName,
                    OrganizationId = parameters.OrganizationId,
                    OrganizationName = parameters.OrganizationName,
                    RepositoryId = parameters.RepositoryId,
                    RepositoryName = parameters.RepositoryName,
                    Enabled = parameters.Enabled,
                    CorrelationId = parameters.CorrelationId
                )

            let! enableFeatureResult = command sdkParameters

            match enableFeatureResult with
            | Ok returnValue -> return Ok(GraceReturnValue.Create (returnValue.ReturnValue) (getCorrelationId parseResult))
            | Error error -> return Error error
        }

    let private EnableAssign =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableFeaturesParams: EnableFeatureParams) ->
            task {
                let command (parameters: EnableFeatureParameters) = task { return! Branch.EnableAssign(parameters) }

                let! result = enableFeatureHandler parseResult (enableFeaturesParams |> normalizeIdsAndNames parseResult) command "assign"

                return result |> renderOutput parseResult
            })

    let private EnablePromotion =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableFeaturesParams: EnableFeatureParams) ->
            task {
                let command (parameters: EnableFeatureParameters) = task { return! Branch.EnablePromotion(parameters) }

                let! result = enableFeatureHandler parseResult (enableFeaturesParams |> normalizeIdsAndNames parseResult) command "promotion"

                return result |> renderOutput parseResult
            })

    let private EnableCommit =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableFeaturesParams: EnableFeatureParams) ->
            task {
                let command (parameters: EnableFeatureParameters) = task { return! Branch.EnableCommit(parameters) }

                let! result = enableFeatureHandler parseResult (enableFeaturesParams |> normalizeIdsAndNames parseResult) command "commit"

                return result |> renderOutput parseResult
            })

    let private EnableCheckpoint =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableFeaturesParams: EnableFeatureParams) ->
            task {
                let command (parameters: EnableFeatureParameters) = task { return! Branch.EnableCheckpoint(parameters) }

                let! result = enableFeatureHandler parseResult (enableFeaturesParams |> normalizeIdsAndNames parseResult) command "checkpoint"

                return result |> renderOutput parseResult
            })

    let private EnableSave =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableFeaturesParams: EnableFeatureParams) ->
            task {
                let command (parameters: EnableFeatureParameters) = task { return! Branch.EnableSave(parameters) }

                let! result = enableFeatureHandler parseResult (enableFeaturesParams |> normalizeIdsAndNames parseResult) command "save"

                return result |> renderOutput parseResult
            })

    let private EnableTag =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableFeaturesParams: EnableFeatureParams) ->
            task {
                let command (parameters: EnableFeatureParameters) = task { return! Branch.EnableTag(parameters) }

                let! result = enableFeatureHandler parseResult (enableFeaturesParams |> normalizeIdsAndNames parseResult) command "tag"

                return result |> renderOutput parseResult
            })

    let private EnableExternal =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableFeaturesParams: EnableFeatureParams) ->
            task {
                let command (parameters: EnableFeatureParameters) = task { return! Branch.EnableExternal(parameters) }

                let! result = enableFeatureHandler parseResult (enableFeaturesParams |> normalizeIdsAndNames parseResult) command "external"

                return result |> renderOutput parseResult
            })

    let private EnableAutoRebase =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableFeaturesParams: EnableFeatureParams) ->
            task {
                let command (parameters: EnableFeatureParameters) = task { return! Branch.EnableAutoRebase(parameters) }

                let! result = enableFeatureHandler parseResult (enableFeaturesParams |> normalizeIdsAndNames parseResult) command "auto-rebase"

                return result |> renderOutput parseResult
            })

    // Get subcommand
    type GetParameters() =
        inherit CommonParameters()
        member val public IncludeDeleted: bool = false with get, set
        member val public ShowEvents: bool = false with get, set

    let private getHandler (parseResult: ParseResult) (parameters: GetParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations parseResult parameters

                match validateIncomingParameters with
                | Ok _ ->
                    let sdkParameters =
                        GetBranchParameters(
                            OwnerId = parameters.OwnerId,
                            OwnerName = parameters.OwnerName,
                            OrganizationId = parameters.OrganizationId,
                            OrganizationName = parameters.OrganizationName,
                            RepositoryId = parameters.RepositoryId,
                            RepositoryName = parameters.RepositoryName,
                            BranchId = parameters.BranchId,
                            BranchName = parameters.BranchName,
                            IncludeDeleted = parameters.IncludeDeleted,
                            CorrelationId = parameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Branch.Get(sdkParameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Branch.Get(sdkParameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private getEventsHandler (parseResult: ParseResult) (parameters: GetParameters) =
        task {
            try
                let validateIncomingParameters = CommonValidations parseResult parameters

                match validateIncomingParameters with
                | Ok _ ->
                    let sdkParameters =
                        GetBranchVersionParameters(
                            OwnerId = parameters.OwnerId,
                            OwnerName = parameters.OwnerName,
                            OrganizationId = parameters.OrganizationId,
                            OrganizationName = parameters.OrganizationName,
                            RepositoryId = parameters.RepositoryId,
                            RepositoryName = parameters.RepositoryName,
                            BranchId = parameters.BranchId,
                            BranchName = parameters.BranchName,
                            IncludeDeleted = parameters.IncludeDeleted,
                            CorrelationId = parameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Branch.GetEvents(sdkParameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Branch.GetEvents(sdkParameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
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
                    AnsiConsole.WriteLine()

                    if getParameters.ShowEvents then
                        let! eventsResult = getEventsHandler parseResult (getParameters |> normalizeIdsAndNames parseResult)

                        match eventsResult with
                        | Ok graceReturnValue ->
                            let sb = new StringBuilder()

                            for line in graceReturnValue.ReturnValue do
                                sb.AppendLine($"{Markup.Escape(line)},") |> ignore
                                AnsiConsole.MarkupLine $"[{Colors.Verbose}]{Markup.Escape(line)}[/]"

                            sb.Remove(sb.Length - 1, 1) |> ignore
                            //AnsiConsole.Write(sb.ToString())
                            AnsiConsole.WriteLine()
                            return 0
                        | Error graceError -> return Error graceError |> renderOutput parseResult
                    else
                        return 0
                | Error graceError -> return Error graceError |> renderOutput parseResult
            })


    type GetReferenceQuery = GetReferencesParameters -> Task<GraceResult<ReferenceDto array>>

    type GetRefParameters() =
        inherit CommonParameters()
        member val public MaxCount = 50 with get, set

    /// Executes the query to retrieve a set of references.
    let getReferenceHandler (parseResult: ParseResult) (parameters: GetRefParameters) (query: GetReferenceQuery) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations parseResult parameters

                match validateIncomingParameters with
                | Ok _ ->
                    let getBranchParameters =
                        GetBranchParameters(
                            BranchId = parameters.BranchId,
                            BranchName = parameters.BranchName,
                            OwnerId = parameters.OwnerId,
                            OwnerName = parameters.OwnerName,
                            OrganizationId = parameters.OrganizationId,
                            OrganizationName = parameters.OrganizationName,
                            RepositoryId = parameters.RepositoryId,
                            RepositoryName = parameters.RepositoryName,
                            CorrelationId = parameters.CorrelationId
                        )

                    let getReferencesParameters =
                        Parameters.Branch.GetReferencesParameters(
                            BranchId = parameters.BranchId,
                            BranchName = parameters.BranchName,
                            OwnerId = parameters.OwnerId,
                            OwnerName = parameters.OwnerName,
                            OrganizationId = parameters.OrganizationId,
                            OrganizationName = parameters.OrganizationName,
                            RepositoryId = parameters.RepositoryId,
                            RepositoryName = parameters.RepositoryName,
                            MaxCount = parameters.MaxCount,
                            CorrelationId = parameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let branchDtoResult = Branch.Get(getBranchParameters)
                                        let getReferencesResult = query getReferencesParameters
                                        Task.WaitAll([| branchDtoResult :> Task; getReferencesResult :> Task |])
                                        t0.Increment(100.0)

                                        match (branchDtoResult.Result, getReferencesResult.Result) with
                                        | (Ok branchDto, Ok references) ->
                                            let graceReturnValue =
                                                GraceReturnValue.Create (branchDto.ReturnValue, references.ReturnValue.ToArray()) (parameters.CorrelationId)

                                            references.Properties
                                            |> Seq.iter (fun kvp -> graceReturnValue.Properties.Add(kvp.Key, kvp.Value))

                                            return Ok graceReturnValue
                                        | (Error error, _) -> return Error error
                                        | (_, Error error) -> return Error error
                                    })
                    else
                        let branchDtoResult = Branch.Get(getBranchParameters)
                        let getReferencesResult = query getReferencesParameters
                        Task.WaitAll([| branchDtoResult :> Task; getReferencesResult :> Task |])

                        match (branchDtoResult.Result, getReferencesResult.Result) with
                        | (Ok branchDto, Ok references) ->
                            return Ok(GraceReturnValue.Create (branchDto.ReturnValue, references.ReturnValue.ToArray()) (parameters.CorrelationId))
                        | (Error error, _) -> return Error error
                        | (_, Error error) -> return Error error
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private createReferenceTable (parseResult: ParseResult) (references: ReferenceDto array) =
        let sortedResults = references |> Array.sortByDescending (fun row -> row.CreatedAt)

        let table = Table(Border = TableBorder.DoubleEdge, ShowHeaders = true)

        table.AddColumns(
            [| TableColumn($"[{Colors.Important}]Type[/]")
               TableColumn($"[{Colors.Important}]Message[/]")
               TableColumn($"[{Colors.Important}]SHA-256[/]")
               TableColumn($"[{Colors.Important}]When[/]", Alignment = Justify.Right)
               TableColumn($"[{Colors.Important}][/]") |]
        )
        |> ignore

        if parseResult |> verbose then
            table.AddColumns([| TableColumn($"[{Colors.Deemphasized}]ReferenceId[/]") |])
                    .AddColumns([| TableColumn($"[{Colors.Deemphasized}]Root DirectoryVersionId[/]") |])
            |> ignore

        for row in sortedResults do
            //logToAnsiConsole Colors.Verbose $"{serialize row}"
            let sha256Hash =
                if parseResult.HasOption(Options.fullSha) then
                    $"{row.Sha256Hash}"
                else
                    $"{getShortSha256Hash row.Sha256Hash}"

            let localCreatedAtTime = row.CreatedAt.ToDateTimeUtc().ToLocalTime()

            let referenceTime = $"""{localCreatedAtTime.ToString("g", CultureInfo.CurrentUICulture)}"""

            if parseResult |> verbose then
                table.AddRow(
                    [| $"{getDiscriminatedUnionCaseName (row.ReferenceType)}"
                       $"{row.ReferenceText}"
                       sha256Hash
                       ago row.CreatedAt
                       $"[{Colors.Deemphasized}]{referenceTime}[/]"
                       $"[{Colors.Deemphasized}]{row.ReferenceId}[/]"
                       $"[{Colors.Deemphasized}]{row.DirectoryId}[/]"
                    |]
                )
            else
                table.AddRow(
                    [| $"{getDiscriminatedUnionCaseName (row.ReferenceType)}"
                       $"{row.ReferenceText}"
                       sha256Hash
                       ago row.CreatedAt
                       $"[{Colors.Deemphasized}]{referenceTime}[/]" |]
                )
            |> ignore

        table

    let printReferenceTable (table: Table) (references: ReferenceDto array) branchName referenceName =
        AnsiConsole.MarkupLine($"[{Colors.Important}]{referenceName} in branch {branchName}:[/]")
        AnsiConsole.Write(table)
        AnsiConsole.MarkupLine($"[{Colors.Important}]Returned {references.Length} rows.[/]")

    let private GetReferences =
        CommandHandler.Create(fun (parseResult: ParseResult) (getReferencesParameters: GetRefParameters) ->
            task {
                let query (parameters: GetReferencesParameters) = task { return! Branch.GetReferences(parameters) }

                let! result = getReferenceHandler parseResult (getReferencesParameters |> normalizeIdsAndNames parseResult) query

                match result with
                | Ok graceReturnValue ->
                    let (branchDto, references) = graceReturnValue.ReturnValue

                    if parseResult |> hasOutput then
                        let referenceTable = createReferenceTable parseResult references
                        printReferenceTable referenceTable references branchDto.BranchName "References"

                    return result |> renderOutput parseResult
                | Error error -> return result |> renderOutput parseResult
            })

    let private GetPromotions =
        CommandHandler.Create(fun (parseResult: ParseResult) (getReferencesParameters: GetRefParameters) ->
            task {
                let query (parameters: GetReferencesParameters) = task { return! Branch.GetPromotions(parameters) }

                let! result = getReferenceHandler parseResult (getReferencesParameters |> normalizeIdsAndNames parseResult) query

                match result with
                | Ok graceReturnValue ->
                    let (branchDto, references) = graceReturnValue.ReturnValue
                    let intReturn = result |> renderOutput parseResult

                    if parseResult |> hasOutput then
                        let referenceTable = createReferenceTable parseResult references
                        printReferenceTable referenceTable references branchDto.BranchName "Promotions"

                    return intReturn
                | Error error -> return result |> renderOutput parseResult
            })

    let private GetCommits =
        CommandHandler.Create(fun (parseResult: ParseResult) (getReferencesParameters: GetRefParameters) ->
            task {
                let query (parameters: GetReferencesParameters) = task { return! Branch.GetCommits(parameters) }

                let! result = getReferenceHandler parseResult (getReferencesParameters |> normalizeIdsAndNames parseResult) query

                match result with
                | Ok graceReturnValue ->
                    let (branchDto, references) = graceReturnValue.ReturnValue
                    let intReturn = result |> renderOutput parseResult

                    if parseResult |> hasOutput then
                        let referenceTable = createReferenceTable parseResult references
                        printReferenceTable referenceTable references branchDto.BranchName "Commits"

                    return intReturn
                | Error error -> return result |> renderOutput parseResult
            })

    let private GetCheckpoints =
        CommandHandler.Create(fun (parseResult: ParseResult) (getReferencesParameters: GetRefParameters) ->
            task {
                let query (parameters: GetReferencesParameters) =
                    task {
                        let commitResult = Branch.GetCommits(parameters)
                        let checkpointResult = Branch.GetCheckpoints(parameters)
                        Task.WaitAll(checkpointResult, commitResult)

                        match checkpointResult.Result, commitResult.Result with
                        | Ok checkpoints, Ok commits ->
                            // Combining the results of both queries
                            let allReferences =
                                checkpoints.ReturnValue
                                    .Concat(commits.ReturnValue)
                                    .OrderByDescending(fun ref -> ref.CreatedAt)
                                    .Take(getReferencesParameters.MaxCount)
                                    .ToArray()

                            return Ok(GraceReturnValue.Create allReferences (getCorrelationId parseResult))
                        | Error error, _ -> return Error error
                        | _, Error error -> return Error error
                    }

                let! result = getReferenceHandler parseResult (getReferencesParameters |> normalizeIdsAndNames parseResult) query

                match result with
                | Ok graceReturnValue ->
                    let (branchDto, references) = graceReturnValue.ReturnValue
                    let intReturn = result |> renderOutput parseResult

                    if parseResult |> hasOutput then
                        let referenceTable = createReferenceTable parseResult references
                        printReferenceTable referenceTable references branchDto.BranchName "Checkpoints"

                    return intReturn
                | Error error -> return result |> renderOutput parseResult
            })

    let private GetSaves =
        CommandHandler.Create(fun (parseResult: ParseResult) (getReferencesParameters: GetRefParameters) ->
            task {
                let query (parameters: GetReferencesParameters) = task { return! Branch.GetSaves(parameters) }

                let! result = getReferenceHandler parseResult (getReferencesParameters |> normalizeIdsAndNames parseResult) query

                match result with
                | Ok graceReturnValue ->
                    let (branchDto, references) = graceReturnValue.ReturnValue
                    let intReturn = result |> renderOutput parseResult

                    if parseResult |> hasOutput then
                        let referenceTable = createReferenceTable parseResult references
                        printReferenceTable referenceTable references branchDto.BranchName "Saves"

                    return intReturn
                | Error error -> return result |> renderOutput parseResult
            })

    let private GetTags =
        CommandHandler.Create(fun (parseResult: ParseResult) (getReferencesParameters: GetRefParameters) ->
            task {
                let query (parameters: GetReferencesParameters) = task { return! Branch.GetTags(parameters) }

                let! result = getReferenceHandler parseResult (getReferencesParameters |> normalizeIdsAndNames parseResult) query

                match result with
                | Ok graceReturnValue ->
                    let (branchDto, references) = graceReturnValue.ReturnValue
                    let intReturn = result |> renderOutput parseResult

                    if parseResult |> hasOutput then
                        let referenceTable = createReferenceTable parseResult references
                        printReferenceTable referenceTable references branchDto.BranchName "Tags"

                    return intReturn
                | Error error -> return result |> renderOutput parseResult
            })

    let private GetExternals =
        CommandHandler.Create(fun (parseResult: ParseResult) (getReferencesParameters: GetRefParameters) ->
            task {
                let query (parameters: GetReferencesParameters) = task { return! Branch.GetExternals(parameters) }

                let! result = getReferenceHandler parseResult (getReferencesParameters |> normalizeIdsAndNames parseResult) query

                match result with
                | Ok graceReturnValue ->
                    let (branchDto, references) = graceReturnValue.ReturnValue
                    let intReturn = result |> renderOutput parseResult

                    if parseResult |> hasOutput then
                        let referenceTable = createReferenceTable parseResult references
                        printReferenceTable referenceTable references branchDto.BranchName "Externals"

                    return intReturn
                | Error error -> return result |> renderOutput parseResult
            })

    type SwitchParameters() =
        inherit CommonParameters()
        member val public ToBranchId = String.Empty with get, set
        member val public ToBranchName = String.Empty with get, set
        member val public ReferenceId = String.Empty with get, set
        member val public Sha256Hash = Sha256Hash String.Empty with get, set

    let private switchHandler parseResult (switchParameters: SwitchParameters) =
        task {
            try
                /// The GraceStatus at the beginning of running this command.
                let mutable previousGraceStatus = GraceStatus.Default
                /// The GraceStatus after the current version is saved.
                let mutable newGraceStatus = GraceStatus.Default
                /// The DirectoryId of the root directory version.
                let mutable rootDirectoryId = DirectoryVersionId.Empty
                /// The SHA-256 hash of the root directory version.
                let mutable rootDirectorySha256Hash = Sha256Hash String.Empty
                /// The set of DirectoryIds in the working directory after the current version is saved.
                let mutable directoryIdsInNewGraceStatus: HashSet<DirectoryVersionId> = null

                let showOutput = parseResult |> hasOutput

                if parseResult |> verbose then printParseResult parseResult

                // Validate the incoming parameters.
                let validateIncomingParameters (showOutput, parseResult: ParseResult, parameters: CommonParameters) =
                    let ``Either ToBranchId or ToBranchName must be provided if no Sha256Hash or ReferenceId``
                        (parseResult: ParseResult, commonParameters: CommonParameters)
                        =
                        oneOfTheseOptionsMustBeProvided
                            parseResult
                            commonParameters
                            [| Options.toBranchId
                               Options.toBranchName
                               Options.sha256Hash
                               Options.referenceId |]
                            EitherToBranchIdOrToBranchNameIsRequired

                    match
                        CommonValidations parseResult parameters
                        >>= ``Either ToBranchId or ToBranchName must be provided if no Sha256Hash or ReferenceId``
                    with
                    | Ok result -> Ok(showOutput, parseResult, parameters) |> returnTask
                    | Error error -> Error error |> returnTask

                // 0. Get the branchDto for the current branch.
                let getCurrentBranch (t: ProgressTask) (showOutput, parseResult: ParseResult, parameters: CommonParameters) =
                    task {
                        t |> startProgressTask showOutput

                        let getParameters =
                            GetBranchParameters(
                                OwnerId = $"{Current().OwnerId}",
                                OrganizationId = $"{Current().OrganizationId}",
                                RepositoryId = $"{Current().RepositoryId}",
                                BranchId = $"{Current().BranchId}",
                                CorrelationId = parameters.CorrelationId
                            )

                        match! Branch.Get(getParameters) with
                        | Ok returnValue ->
                            t |> setProgressTaskValue showOutput 100.0
                            let branchDto = returnValue.ReturnValue
                            return Ok(showOutput, parseResult, parameters, branchDto)
                        | Error error ->
                            t |> setProgressTaskValue showOutput 50.0
                            return Error error
                    }

                // 1. Read the Grace status file.
                let readGraceStatusFile (t: ProgressTask) (showOutput, parseResult: ParseResult, parameters: CommonParameters, currentBranch: BranchDto) =
                    task {
                        t |> startProgressTask showOutput
                        let! existingGraceStaus = readGraceStatusFile ()
                        previousGraceStatus <- existingGraceStaus
                        newGraceStatus <- existingGraceStaus
                        t |> setProgressTaskValue showOutput 100.0
                        return Ok(showOutput, parseResult, parameters, currentBranch)
                    }

                // 2. Scan the working directory for differences.
                let scanForDifferences (t: ProgressTask) (showOutput, parseResult: ParseResult, parameters: CommonParameters, currentBranch: BranchDto) =
                    task {
                        t |> startProgressTask showOutput

                        let! differences =
                            if currentBranch.SaveEnabled then
                                scanForDifferences newGraceStatus
                            else
                                List<FileSystemDifference>() |> returnTask

                        t |> setProgressTaskValue showOutput 100.0
                        return Ok(showOutput, parseResult, parameters, currentBranch, differences)
                    }

                // 3. Create new directory versions.
                let getNewGraceStatusAndDirectoryVersions
                    (t: ProgressTask)
                    (showOutput, parseResult: ParseResult, parameters: CommonParameters, currentBranch: BranchDto, differences: List<FileSystemDifference>)
                    =
                    task {
                        t |> startProgressTask showOutput
                        let mutable newDirectoryVersions = List<LocalDirectoryVersion>()

                        if currentBranch.SaveEnabled then
                            let! (updatedGraceStatus, newVersions) = getNewGraceStatusAndDirectoryVersions previousGraceStatus differences
                            newGraceStatus <- updatedGraceStatus
                            newDirectoryVersions <- newVersions

                        rootDirectoryId <- newGraceStatus.RootDirectoryId
                        rootDirectorySha256Hash <- newGraceStatus.RootDirectorySha256Hash
                        directoryIdsInNewGraceStatus <- newGraceStatus.Index.Keys.ToHashSet()
                        t |> setProgressTaskValue showOutput 100.0
                        return Ok(showOutput, parseResult, parameters, currentBranch, differences, newDirectoryVersions)
                    }

                // 4. Upload changed files to object storage.
                let uploadChangedFilesToObjectStorage
                    (t: ProgressTask)
                    (
                        showOutput,
                        parseResult: ParseResult,
                        parameters: CommonParameters,
                        currentBranch: BranchDto,
                        differences: List<FileSystemDifference>,
                        newDirectoryVersions: List<LocalDirectoryVersion>
                    ) =
                    task {
                        t |> startProgressTask showOutput
                        let repositoryId = RepositoryId.Parse(parameters.RepositoryId)

                        if currentBranch.SaveEnabled && newDirectoryVersions.Any() then
                            let updatedRelativePaths =
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

                            let newFileVersions =
                                updatedRelativePaths.Select(fun relativePath ->
                                    newDirectoryVersions
                                        .First(fun dv -> dv.Files.Exists(fun file -> file.RelativePath = relativePath))
                                        .Files.First(fun file -> file.RelativePath = relativePath))

                            logToAnsiConsole
                                Colors.Verbose
                                $"Uploading {newFileVersions.Count()} file(s) from {newDirectoryVersions.Count} new directory version(s) to object storage."

                            let getUploadMetadataForFilesParameters =
                                Storage.GetUploadMetadataForFilesParameters(
                                    OwnerId = parameters.OwnerId,
                                    OwnerName = parameters.OwnerName,
                                    OrganizationId = parameters.OrganizationId,
                                    OrganizationName = parameters.OrganizationName,
                                    RepositoryId = parameters.RepositoryId,
                                    RepositoryName = parameters.RepositoryName,
                                    CorrelationId = getCorrelationId parseResult,
                                    FileVersions =
                                        (newFileVersions
                                         |> Seq.map (fun localFileVersion -> localFileVersion.ToFileVersion)
                                         |> Seq.toArray)
                                )

                            match! uploadFilesToObjectStorage getUploadMetadataForFilesParameters with
                            | Ok returnValue ->
                                t |> setProgressTaskValue showOutput 100.0
                                return Ok(showOutput, parseResult, parameters, currentBranch, newDirectoryVersions)
                            | Error error ->
                                t |> setProgressTaskValue showOutput 50.0
                                return Error error
                        else
                            t |> setProgressTaskValue showOutput 100.0
                            return Ok(showOutput, parseResult, parameters, currentBranch, newDirectoryVersions)
                    }

                // 5. Upload new directory versions.
                let uploadNewDirectoryVersions
                    (t: ProgressTask)
                    (
                        showOutput,
                        parseResult: ParseResult,
                        parameters: CommonParameters,
                        currentBranch: BranchDto,
                        newDirectoryVersions: List<LocalDirectoryVersion>
                    ) =
                    task {
                        t |> startProgressTask showOutput

                        if currentBranch.SaveEnabled && newDirectoryVersions.Any() then
                            let saveParameters = SaveDirectoryVersionsParameters()
                            saveParameters.OwnerId <- parameters.OwnerId
                            saveParameters.OwnerName <- parameters.OwnerName
                            saveParameters.OrganizationId <- parameters.OrganizationId
                            saveParameters.OrganizationName <- parameters.OrganizationName
                            saveParameters.RepositoryId <- parameters.RepositoryId
                            saveParameters.RepositoryName <- parameters.RepositoryName
                            saveParameters.CorrelationId <- getCorrelationId parseResult
                            saveParameters.DirectoryVersions <- newDirectoryVersions.Select(fun dv -> dv.ToDirectoryVersion).ToList()

                            let! uploadDirectoryVersions = DirectoryVersion.SaveDirectoryVersions saveParameters

                            match! DirectoryVersion.SaveDirectoryVersions saveParameters with
                            | Ok returnValue ->
                                t |> setProgressTaskValue showOutput 100.0

                                return Ok(showOutput, parseResult, parameters, currentBranch, $"Save created prior to branch switch.")
                            | Error error ->
                                t |> setProgressTaskValue showOutput 50.0
                                return Error error
                        else
                            t |> setProgressTaskValue showOutput 100.0

                            return Ok(showOutput, parseResult, parameters, currentBranch, $"Save created prior to branch switch.")
                    }

                // 6. Create a before save reference.
                let createSaveReference
                    (t: ProgressTask)
                    (showOutput, parseResult: ParseResult, parameters: CommonParameters, branchDto: BranchDto, message: string)
                    =
                    task {
                        t |> startProgressTask showOutput

                        if branchDto.SaveEnabled then
                            logToAnsiConsole Colors.Verbose $"In createSaveReference: BranchName: {branchDto.BranchName}; SaveEnabled; message: {message}"

                            match! createSaveReference newGraceStatus.Index[rootDirectoryId] message (getCorrelationId parseResult) with
                            | Ok returnValue ->
                                logToAnsiConsole
                                    Colors.Verbose
                                    $"In createSaveReference: BranchName: {branchDto.BranchName}; SaveEnabled; message: {message}; returnValue: {returnValue}."

                                t |> setProgressTaskValue showOutput 100.0
                                return Ok(showOutput, parseResult, parameters, branchDto)
                            | Error error ->
                                logToAnsiConsole
                                    Colors.Verbose
                                    $"In createSaveReference: BranchName: {branchDto.BranchName}; SaveEnabled; message: {message}; error: {error}."

                                t |> setProgressTaskValue showOutput 50.0
                                return Error error
                        else
                            logToAnsiConsole Colors.Verbose $"In createSaveReference: BranchName: {branchDto.BranchName}; Save not enabled; message: {message}"
                            t |> setProgressTaskValue showOutput 100.0
                            return Ok(showOutput, parseResult, parameters, branchDto)
                    }

                /// 7. Get the branch and directory versions for the requested version we're switching to from the server.
                let getVersionToSwitchTo (t: ProgressTask) (showOutput, parseResult: ParseResult, parameters: CommonParameters, currentBranch: BranchDto) =
                    task {
                        t |> startProgressTask showOutput

                        // The version to switch to may be specified by a branch name or id, or by a ReferenceId or Sha256Hash.
                        // If ToBranchId or ToBranchName is specified, that takes precedence. We return the new branch and DirectoryId's from its latest version.
                        // If no ToBranch* is provided, we first check for ReferenceId. If that's provided, we return the branch and DirectoryId's from the reference.
                        // If no ReferenceId is provided, we check for Sha256Hash. If that's provided, we return the current branch, and the DirectoryId's from the version with that hash.
                        // If none of those four parameters is provided, we return an error.

                        // First, see if ToBranchId or ToBranchName is provided.
                        if
                            not <| String.IsNullOrEmpty(switchParameters.ToBranchId)
                            || not <| String.IsNullOrEmpty(switchParameters.ToBranchName)
                        then
                            let getNewBranchParameters =
                                GetBranchParameters(
                                    OwnerId = parameters.OwnerId,
                                    OwnerName = parameters.OwnerName,
                                    OrganizationId = parameters.OrganizationId,
                                    OrganizationName = parameters.OrganizationName,
                                    RepositoryId = parameters.RepositoryId,
                                    RepositoryName = parameters.RepositoryName,
                                    BranchId = switchParameters.ToBranchId,
                                    BranchName = switchParameters.ToBranchName,
                                    Sha256Hash = switchParameters.Sha256Hash,
                                    ReferenceId = switchParameters.ReferenceId,
                                    CorrelationId = parameters.CorrelationId
                                )

                            if parseResult |> verbose then
                                logToAnsiConsole Colors.Verbose $"In getVersionToSwitchTo: getNewBranchParameters: {serialize getNewBranchParameters}."

                            match! Branch.Get(getNewBranchParameters) with
                            | Ok returnValue ->
                                let newBranch = returnValue.ReturnValue

                                if parseResult |> verbose then
                                    logToAnsiConsole Colors.Verbose $"In getVersionToSwitchTo: New branch: {serialize newBranch}."

                                let getBranchVersionParameters =
                                    GetBranchVersionParameters(
                                        OwnerId = parameters.OwnerId,
                                        OwnerName = parameters.OwnerName,
                                        OrganizationId = parameters.OrganizationId,
                                        OrganizationName = parameters.OrganizationName,
                                        RepositoryId = $"{newBranch.RepositoryId}",
                                        BranchId = $"{newBranch.BranchId}",
                                        ReferenceId = switchParameters.ReferenceId,
                                        Sha256Hash = switchParameters.Sha256Hash,
                                        CorrelationId = parameters.CorrelationId
                                    )

                                if parseResult |> verbose then
                                    logToAnsiConsole
                                        Colors.Verbose
                                        $"In getVersionToSwitchTo: getBranchVersionParameters: {serialize getBranchVersionParameters}."

                                match! Branch.GetVersion getBranchVersionParameters with
                                | Ok returnValue ->
                                    let directoryIds = returnValue.ReturnValue

                                    if parseResult |> verbose then
                                        logToAnsiConsole
                                            Colors.Verbose
                                            $"Retrieved {directoryIds.Count()} directory version(s) for branch {newBranch.BranchName}."

                                    //logToAnsiConsole Colors.Verbose $"DirectoryIds: {serialize directoryIds}."
                                    t |> setProgressTaskValue showOutput 100.0

                                    return Ok(showOutput, parseResult, parameters, currentBranch, newBranch, directoryIds)
                                | Error error -> return Error error
                            | Error error -> return Error error

                        // Next, see if ReferenceId is provided.
                        elif not <| String.IsNullOrEmpty(switchParameters.ReferenceId) then
                            let getReferenceParameters =
                                GetReferenceParameters(
                                    OwnerId = parameters.OwnerId,
                                    OwnerName = parameters.OwnerName,
                                    OrganizationId = parameters.OrganizationId,
                                    OrganizationName = parameters.OrganizationName,
                                    RepositoryId = parameters.RepositoryId,
                                    RepositoryName = parameters.RepositoryName,
                                    BranchId = switchParameters.BranchId,
                                    BranchName = switchParameters.BranchName,
                                    ReferenceId = switchParameters.ReferenceId,
                                    CorrelationId = parameters.CorrelationId
                                )

                            match! Branch.GetReference(getReferenceParameters) with
                            | Ok returnValue ->
                                // We have the reference, let's get the new branch and the DirectoryVersion from the reference.
                                let reference = returnValue.ReturnValue

                                let getNewBranchParameters =
                                    GetBranchParameters(
                                        OwnerId = parameters.OwnerId,
                                        OwnerName = parameters.OwnerName,
                                        OrganizationId = parameters.OrganizationId,
                                        OrganizationName = parameters.OrganizationName,
                                        RepositoryId = parameters.RepositoryId,
                                        RepositoryName = parameters.RepositoryName,
                                        BranchId = $"{reference.BranchId}",
                                        CorrelationId = parameters.CorrelationId
                                    )

                                let! getNewBranchResult = Branch.Get(getNewBranchParameters)

                                let getVersionParameters =
                                    GetBranchVersionParameters(
                                        OwnerId = parameters.OwnerId,
                                        OwnerName = parameters.OwnerName,
                                        OrganizationId = parameters.OrganizationId,
                                        OrganizationName = parameters.OrganizationName,
                                        RepositoryId = parameters.RepositoryId,
                                        RepositoryName = parameters.RepositoryName,
                                        BranchId = $"{reference.BranchId}",
                                        ReferenceId = switchParameters.ReferenceId,
                                        CorrelationId = parameters.CorrelationId
                                    )

                                let! getVersionResult = Branch.GetVersion getVersionParameters

                                match (getNewBranchResult, getVersionResult) with
                                | (Ok branchReturnValue, Ok versionReturnValue) ->
                                    let newBranch = branchReturnValue.ReturnValue
                                    let directoryIds = versionReturnValue.ReturnValue

                                    return Ok(showOutput, parseResult, parameters, currentBranch, newBranch, directoryIds)
                                | (Error error, _) -> return Error error
                                | (_, Error error) -> return Error error
                            | Error error -> return Error error

                        // Next, see if Sha256Hash is provided. If so, we return the current branch and the DirectoryVersion with that Sha256Hash.
                        elif not <| String.IsNullOrEmpty(switchParameters.Sha256Hash) then
                            let getVersionParameters =
                                GetBranchVersionParameters(
                                    OwnerId = parameters.OwnerId,
                                    OwnerName = parameters.OwnerName,
                                    OrganizationId = parameters.OrganizationId,
                                    OrganizationName = parameters.OrganizationName,
                                    RepositoryId = parameters.RepositoryId,
                                    RepositoryName = parameters.RepositoryName,
                                    BranchId = $"{currentBranch.BranchId}",
                                    Sha256Hash = switchParameters.Sha256Hash,
                                    CorrelationId = parameters.CorrelationId
                                )

                            match! Branch.GetVersion getVersionParameters with
                            | Ok returnValue ->
                                let directoryIds = returnValue.ReturnValue

                                return Ok(showOutput, parseResult, parameters, currentBranch, currentBranch, directoryIds)
                            | Error error -> return Error error
                        else
                            return
                                Error(
                                    GraceError.Create
                                        (BranchError.getErrorMessage BranchError.EitherToBranchIdOrToBranchNameIsRequired)
                                        (parseResult |> getCorrelationId)
                                )
                    }

                /// 8. Update object cache and working directory.
                let updateWorkingDirectory
                    (t: ProgressTask)
                    (
                        showOutput,
                        parseResult: ParseResult,
                        parameters: CommonParameters,
                        currentBranch: BranchDto,
                        newBranch: BranchDto,
                        directoryIds: IEnumerable<DirectoryVersionId>
                    ) =
                    task {
                        t |> startProgressTask showOutput

                        let missingDirectoryIds =
                            directoryIds
                                .Where(fun directoryId -> not <| directoryIdsInNewGraceStatus.Contains(directoryId))
                                .ToList()

                        if parseResult |> verbose then
                            logToAnsiConsole Colors.Verbose $"In updateWorkingDirectory: missingDirectoryIds.Count: {missingDirectoryIds.Count()}."

                        // Get missing directory versions from server.
                        let getByDirectoryIdParameters =
                            GetByDirectoryIdsParameters(
                                OwnerId = parameters.OwnerId,
                                OwnerName = parameters.OwnerName,
                                OrganizationId = parameters.OrganizationId,
                                OrganizationName = parameters.OrganizationName,
                                RepositoryId = parameters.RepositoryId,
                                RepositoryName = parameters.RepositoryName,
                                DirectoryVersionId = $"{rootDirectoryId}",
                                DirectoryIds = missingDirectoryIds,
                                CorrelationId = parameters.CorrelationId
                            )

                        match! DirectoryVersion.GetByDirectoryIds getByDirectoryIdParameters with
                        | Ok returnValue ->
                            // Create a new version of GraceStatus that includes the new DirectoryVersions.
                            let newDirectoryVersions = returnValue.ReturnValue

                            if parseResult |> verbose then
                                logToAnsiConsole Colors.Verbose $"In updateWorkingDirectory: newDirectoryVersions.Count: {newDirectoryVersions.Count()}."

                            let graceStatusWithNewDirectoryVersionsFromServer =
                                updateGraceStatusWithNewDirectoryVersionsFromServer newGraceStatus newDirectoryVersions

                            let mutable isError = false

                            // Identify files that we don't already have in object cache and download them.
                            let getDownloadUriParameters =
                                Storage.GetDownloadUriParameters(
                                    OwnerId = parameters.OwnerId,
                                    OwnerName = parameters.OwnerName,
                                    OrganizationId = parameters.OrganizationId,
                                    OrganizationName = parameters.OrganizationName,
                                    RepositoryId = parameters.RepositoryId,
                                    RepositoryName = parameters.RepositoryName,
                                    CorrelationId = parameters.CorrelationId
                                )

                            for directoryVersion in graceStatusWithNewDirectoryVersionsFromServer.Index.Values do
                                match! (downloadFilesFromObjectStorage getDownloadUriParameters directoryVersion.Files (getCorrelationId parseResult)) with
                                | Ok _ ->
                                    try
                                        //logToAnsiConsole Colors.Verbose $"Succeeded downloading files from object storage for {directoryVersion.RelativePath}."

                                        // Write the UpdatesInProgress file to let grace watch know to ignore these changes.
                                        // This file is deleted in the finally clause.
                                        do! File.WriteAllTextAsync(updateInProgressFileName, "This file won't exist for long.")

                                        // Update working directory based on new GraceStatus.Index
                                        do!
                                            updateWorkingDirectory
                                                newGraceStatus
                                                graceStatusWithNewDirectoryVersionsFromServer
                                                newDirectoryVersions
                                                (getCorrelationId parseResult)
                                        //logToAnsiConsole Colors.Verbose $"Succeeded calling updateWorkingDirectory."

                                        // Save the new Grace Status.
                                        do! writeGraceStatusFile graceStatusWithNewDirectoryVersionsFromServer

                                        // Update graceconfig.json.
                                        let configuration = Current()
                                        configuration.BranchId <- newBranch.BranchId
                                        configuration.BranchName <- newBranch.BranchName
                                        updateConfiguration configuration
                                        t |> setProgressTaskValue showOutput 100.0
                                    finally
                                        // Delete the UpdatesInProgress file.
                                        File.Delete(updateInProgressFileName)

                                | Error error ->
                                    logToAnsiConsole Colors.Verbose $"Failed downloading files from object storage for {directoryVersion.RelativePath}."

                                    logToAnsiConsole Colors.Error $"{error}"
                                    isError <- true

                            if not <| isError then
                                logToAnsiConsole Colors.Verbose $"About to exit updateWorkingDirectory."
                                return Ok(showOutput, parseResult, parameters, newBranch, $"Save created after branch switch.")
                            else
                                return Error(GraceError.Create $"Failed downloading files from object storage." (parseResult |> getCorrelationId))
                        | Error error ->
                            logToAnsiConsole Colors.Verbose $"Failed calling Directory.GetByDirectoryIds."
                            logToAnsiConsole Colors.Error $"{error}"
                            return Error(GraceError.Create $"{error}" (parseResult |> getCorrelationId))
                    }

                let writeNewGraceStatus (t: ProgressTask) (showOutput, parseResult: ParseResult, parameters: CommonParameters, currentBranch: BranchDto) =
                    task {
                        t |> startProgressTask showOutput
                        do! writeGraceStatusFile newGraceStatus
                        let! objectCache = readGraceObjectCacheFile ()

                        let plr =
                            Parallel.ForEach(
                                newGraceStatus.Index.Values,
                                Constants.ParallelOptions,
                                (fun localDirectoryVersion ->
                                    if not <| objectCache.Index.ContainsKey(localDirectoryVersion.DirectoryVersionId) then
                                        objectCache.Index.AddOrUpdate(
                                            localDirectoryVersion.DirectoryVersionId,
                                            (fun _ -> localDirectoryVersion),
                                            (fun _ _ -> localDirectoryVersion)
                                        )
                                        |> ignore)
                            )

                        do! writeGraceObjectCacheFile objectCache

                        t |> setProgressTaskValue showOutput 100.0
                        return Ok(showOutput, parseResult, parameters, currentBranch)
                    }

                let generateResult (progressTasks: ProgressTask array) =
                    task {
                        let! result =
                            (showOutput, parseResult, switchParameters) |> validateIncomingParameters
                            >>=! getCurrentBranch progressTasks[0]
                            >>=! readGraceStatusFile progressTasks[1]
                            >>=! scanForDifferences progressTasks[2]
                            >>=! getNewGraceStatusAndDirectoryVersions progressTasks[3]
                            >>=! uploadChangedFilesToObjectStorage progressTasks[4]
                            >>=! uploadNewDirectoryVersions progressTasks[5]
                            >>=! createSaveReference progressTasks[6]
                            >>=! getVersionToSwitchTo progressTasks[7]
                            >>=! updateWorkingDirectory progressTasks[8]
                            >>=! createSaveReference progressTasks[9]
                            >>=! writeNewGraceStatus progressTasks[10]

                        match result with
                        | Ok _ -> return 0
                        | Error error ->
                            if parseResult |> verbose then
                                AnsiConsole.MarkupLine($"[{Colors.Error}]{error}[/]")
                            else
                                AnsiConsole.MarkupLine($"[{Colors.Error}]{error.Error}[/]")

                            return -1
                    }

                if showOutput then
                    return!
                        progress
                            .Columns(progressColumns)
                            .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString GettingCurrentBranch}[/]", autoStart = false)

                                    let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString ReadingGraceStatus}[/]", autoStart = false)

                                    let t2 =
                                        progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString ScanningWorkingDirectory}[/]", autoStart = false)

                                    let t3 =
                                        progressContext.AddTask(
                                            $"[{Color.DodgerBlue1}]{UIString.getString CreatingNewDirectoryVersions}[/]",
                                            autoStart = false
                                        )

                                    let t4 = progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString UploadingFiles}[/]", autoStart = false)

                                    let t5 =
                                        progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString SavingDirectoryVersions}[/]", autoStart = false)

                                    let t6 = progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString CreatingSaveReference}[/]", autoStart = false)

                                    let t7 = progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString GettingLatestVersion}[/]", autoStart = false)

                                    let t8 =
                                        progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString UpdatingWorkingDirectory}[/]", autoStart = false)

                                    let t9 = progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString CreatingSaveReference}[/]", autoStart = false)

                                    let t10 =
                                        progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString WritingGraceStatusFile}[/]", autoStart = false)

                                    return! generateResult [| t0; t1; t2; t3; t4; t5; t6; t7; t8; t9; t10 |]
                                })
                else
                    // If we're not showing output, we don't need to create the progress tasks.
                    return!
                        generateResult
                            [| emptyTask
                               emptyTask
                               emptyTask
                               emptyTask
                               emptyTask
                               emptyTask
                               emptyTask
                               emptyTask
                               emptyTask
                               emptyTask
                               emptyTask |]
            with ex ->
                logToConsole $"{ExceptionResponse.Create ex}"
                logToAnsiConsole Colors.Error (Markup.Escape($"{ExceptionResponse.Create ex}"))
                logToAnsiConsole Colors.Important $"CorrelationId: {(parseResult |> getCorrelationId)}"
                return -1
        }

    let private Switch =
        CommandHandler.Create(fun (parseResult: ParseResult) (switchParameters: SwitchParameters) ->
            task {
                try
                    // Write the UpdatesInProgress file to let grace watch know to ignore these changes.
                    do! File.WriteAllTextAsync(updateInProgressFileName, "`grace switch` is in progress.")
                    let! result = switchHandler parseResult switchParameters
                    return result
                finally
                    File.Delete(updateInProgressFileName)
            })

    type RebaseParameters() =
        inherit CommonParameters()
        member val public BasedOn: ReferenceId = ReferenceId.Empty with get, set

    let rebaseHandler (parseResult: ParseResult) (parameters: RebaseParameters) (graceStatus: GraceStatus) =
        task {
            // --------------------------------------------------------------------------------------------------------------------------------------
            // Get a diff between the promotion from the parent branch that the current branch is based on, and the latest promotion from the parent branch.
            //   These are the changes that we expect to apply to the current branch.

            // Get a diff between the latest reference on this branch and the promotion that it's based on from the parent branch.
            //   This will be what's changed in the current branch since it was last rebased.

            // If a file has changed in the first diff, but not in the second diff, cool, we can automatically copy them.
            // If a file has changed in the second diff, but not in the first diff, cool, we can keep those changes.
            // If a file has changed in both, we have to check the two diffs at the line-level to see if there are any conflicts.

            // Then we call Branch.Rebase() to actually record the update.
            // --------------------------------------------------------------------------------------------------------------------------------------

            // First, get the current branchDto so we have the latest promotion that it's based on.
            let branchGetParameters =
                GetBranchParameters(
                    OwnerId = parameters.OwnerId,
                    OwnerName = parameters.OwnerName,
                    OrganizationId = parameters.OrganizationId,
                    OrganizationName = parameters.OrganizationName,
                    RepositoryId = parameters.RepositoryId,
                    RepositoryName = parameters.RepositoryName,
                    BranchId = parameters.BranchId,
                    BranchName = parameters.BranchName,
                    CorrelationId = parameters.CorrelationId
                )

            match! Branch.Get(branchGetParameters) with
            | Ok returnValue ->
                let branchDto = returnValue.ReturnValue

                // Now, get the parent branch information so we have its latest promotion.
                match! Branch.GetParentBranch(branchGetParameters) with
                | Ok returnValue ->
                    let parentBranchDto = returnValue.ReturnValue

                    if branchDto.BasedOn.ReferenceId = parentBranchDto.LatestPromotion.ReferenceId then
                        AnsiConsole.MarkupLine("The current branch is already based on the latest promotion in the parent branch.")

                        AnsiConsole.MarkupLine("Run `grace status` to see more.")
                        return 0
                    else
                        // Now, get ReferenceDtos for current.BasedOn and parent.LatestPromotion so we have their DirectoryId's.
                        let latestCommit = branchDto.LatestCommit
                        let parentLatestPromotion = parentBranchDto.LatestPromotion
                        let basedOn = branchDto.BasedOn

                        // Get the latest reference from the current branch.
                        let getReferencesParameters =
                            Parameters.Branch.GetReferencesParameters(
                                OwnerId = parameters.OwnerId,
                                OwnerName = parameters.OwnerName,
                                OrganizationId = parameters.OrganizationId,
                                OrganizationName = parameters.OrganizationName,
                                RepositoryId = $"{branchDto.RepositoryId}",
                                BranchId = $"{branchDto.BranchId}",
                                MaxCount = 1,
                                CorrelationId = parameters.CorrelationId
                            )
                        //logToAnsiConsole Colors.Verbose $"getReferencesParameters: {getReferencesParameters |> serialize)}"
                        match! Branch.GetReferences(getReferencesParameters) with
                        | Ok returnValue ->
                            let latestReference =
                                if returnValue.ReturnValue.Count() > 0 then
                                    returnValue.ReturnValue.First()
                                else
                                    ReferenceDto.Default
                            //logToAnsiConsole Colors.Verbose $"latestReference: {serialize latestReference}"
                            // Now we have all of the references we need, so we have DirectoryId's to do diffs with.

                            let! (diffs, errors) =
                                task {
                                    if basedOn.DirectoryId <> DirectoryVersionId.Empty then
                                        // First diff: parent promotion that current branch is based on vs. parent's latest promotion.
                                        let diffParameters =
                                            Parameters.Diff.GetDiffParameters(
                                                OwnerId = parameters.OwnerId,
                                                OwnerName = parameters.OwnerName,
                                                OrganizationId = parameters.OrganizationId,
                                                OrganizationName = parameters.OrganizationName,
                                                RepositoryId = $"{branchDto.RepositoryId}",
                                                DirectoryVersionId1 = basedOn.DirectoryId,
                                                DirectoryVersionId2 = parentLatestPromotion.DirectoryId,
                                                CorrelationId = parameters.CorrelationId
                                            )
                                        //logToAnsiConsole Colors.Verbose $"First diff: {Markup.Escape(serialize diffParameters)}"
                                        let! firstDiff = Diff.GetDiff(diffParameters)

                                        // Second diff: latest reference on current branch vs. parent promotion that current branch is based on.
                                        let diffParameters =
                                            Parameters.Diff.GetDiffParameters(
                                                OwnerId = parameters.OwnerId,
                                                OwnerName = parameters.OwnerName,
                                                OrganizationId = parameters.OrganizationId,
                                                OrganizationName = parameters.OrganizationName,
                                                RepositoryId = $"{branchDto.RepositoryId}",
                                                DirectoryVersionId1 = latestReference.DirectoryId,
                                                DirectoryVersionId2 = basedOn.DirectoryId,
                                                CorrelationId = parameters.CorrelationId
                                            )
                                        //logToAnsiConsole Colors.Verbose $"Second diff: {Markup.Escape(serialize diffParameters)}"
                                        let! secondDiff = Diff.GetDiff(diffParameters)

                                        let returnValue = Result.partition [ firstDiff; secondDiff ]
                                        return returnValue
                                    else
                                        // This should only happen when first creating a repository, when main has no promotions.
                                        // Only one diff possible: latest reference on current branch vs. parent's latest promotion.
                                        let diffParameters =
                                            Parameters.Diff.GetDiffParameters(
                                                OwnerId = parameters.OwnerId,
                                                OwnerName = parameters.OwnerName,
                                                OrganizationId = parameters.OrganizationId,
                                                OrganizationName = parameters.OrganizationName,
                                                RepositoryId = $"{branchDto.RepositoryId}",
                                                DirectoryVersionId1 = latestReference.DirectoryId,
                                                DirectoryVersionId2 = parentLatestPromotion.DirectoryId,
                                                CorrelationId = parameters.CorrelationId
                                            )
                                        //logToAnsiConsole Colors.Verbose $"Initial diff: {Markup.Escape(serialize diffParameters)}"
                                        let! diff = Diff.GetDiff(diffParameters)
                                        let returnValue = Result.partition [ diff ]
                                        return returnValue
                                }

                            // So, right now, if repo just created, and BasedOn is empty, we'll have a single diff.
                            // That fails a few lines below here.
                            // Have to decide what to do in this case.

                            if errors.Count() = 0 then
                                // Yay! We have our two diffs.
                                let diff1 = diffs[0].ReturnValue
                                let diff2 = diffs[1].ReturnValue

                                let filesToDownload = List<FileSystemDifference>()

                                // Identify which files have been changed in the first diff, but not in the second diff.
                                //   We can just download and copy these files into place in the working directory.
                                for fileDifference in diff1.Differences do
                                    if
                                        not
                                        <| diff2.Differences.Any(fun d -> d.RelativePath = fileDifference.RelativePath)
                                    then
                                        // Copy different file version into place - similar to how we do it for switch
                                        filesToDownload.Add(fileDifference)

                                let getParentLatestPromotionDirectoryParameters =
                                    Parameters.DirectoryVersion.GetParameters(
                                        RepositoryId = $"{branchDto.RepositoryId}",
                                        DirectoryVersionId = $"{parentLatestPromotion.DirectoryId}",
                                        CorrelationId = parameters.CorrelationId
                                    )

                                let getLatestReferenceDirectoryParameters =
                                    Parameters.DirectoryVersion.GetParameters(
                                        RepositoryId = $"{branchDto.RepositoryId}",
                                        DirectoryVersionId = $"{latestReference.DirectoryId}",
                                        CorrelationId = parameters.CorrelationId
                                    )

                                // Get the directory versions for the parent promotion that we're rebasing on, and the latest reference.
                                let! d1 = DirectoryVersion.GetDirectoryVersionsRecursive(getParentLatestPromotionDirectoryParameters)

                                let! d2 = DirectoryVersion.GetDirectoryVersionsRecursive(getLatestReferenceDirectoryParameters)

                                let createFileVersionLookupDictionary (directoryVersions: IEnumerable<DirectoryVersion>) =
                                    let lookup = Dictionary<RelativePath, LocalFileVersion>(StringComparer.OrdinalIgnoreCase)

                                    directoryVersions
                                    |> Seq.map (fun dv -> dv.ToLocalDirectoryVersion(dv.CreatedAt.ToDateTimeUtc()))
                                    |> Seq.map (fun dv -> dv.Files)
                                    |> Seq.concat
                                    |> Seq.iter (fun file -> lookup.Add(file.RelativePath, file))

                                    //lookup.GetAlternateLookup()
                                    lookup

                                let (directories, errors) = Result.partition [ d1; d2 ]

                                if errors.Count() = 0 then
                                    let parentLatestPromotionDirectoryVersions = directories[0].ReturnValue
                                    let latestReferenceDirectoryVersions = directories[1].ReturnValue

                                    let parentLatestPromotionLookup = createFileVersionLookupDictionary parentLatestPromotionDirectoryVersions

                                    let latestReferenceLookup = createFileVersionLookupDictionary latestReferenceDirectoryVersions

                                    // Get the specific FileVersions for those files from the contents of the parent's latest promotion.
                                    let fileVersionsToDownload =
                                        filesToDownload
                                        |> Seq.where (fun fileToDownload -> parentLatestPromotionLookup.ContainsKey($"{fileToDownload.RelativePath}"))
                                        |> Seq.map (fun fileToDownload -> parentLatestPromotionLookup[$"{fileToDownload.RelativePath}"])
                                    //logToAnsiConsole Colors.Verbose $"fileVersionsToDownload: {fileVersionsToDownload.Count()}"
                                    //for f in fileVersionsToDownload do
                                    //    logToAnsiConsole Colors.Verbose  $"relativePath: {f.RelativePath}"

                                    // Download those FileVersions from object storage, and copy them into the working directory.
                                    let getDownloadUriParameters =
                                        Storage.GetDownloadUriParameters(
                                            OwnerId = parameters.OwnerId,
                                            OwnerName = parameters.OwnerName,
                                            OrganizationId = parameters.OrganizationId,
                                            OrganizationName = parameters.OrganizationName,
                                            RepositoryId = parameters.RepositoryId,
                                            RepositoryName = parameters.RepositoryName,
                                            CorrelationId = parameters.CorrelationId
                                        )

                                    match! downloadFilesFromObjectStorage getDownloadUriParameters fileVersionsToDownload parameters.CorrelationId with
                                    | Ok _ ->
                                        //logToAnsiConsole Colors.Verbose $"Succeeded in downloadFilesFromObjectStorage."
                                        fileVersionsToDownload
                                        |> Seq.iter (fun file ->
                                            logToAnsiConsole Colors.Verbose $"Copying {file.RelativePath} from {file.FullObjectPath} to {file.FullName}."
                                            // Delete the existing file in the working directory.
                                            File.Delete(file.FullName)
                                            // Copy the version from the object cache to the working directory.
                                            File.Copy(file.FullObjectPath, file.FullName))

                                        logToAnsiConsole Colors.Verbose $"Copied files into place."
                                    | Error error -> AnsiConsole.WriteLine($"[{Colors.Error}]{Markup.Escape(error)}[/]")

                                    // If a file has changed in the second diff, but not in the first diff, cool, we can keep those changes, nothing to be done.

                                    // If a file has changed in both, we have to check the two diffs at the line-level to see if there are any conflicts.
                                    let mutable potentialPromotionConflicts = false

                                    for diff1Difference in diff1.Differences do
                                        let diff2DifferenceQuery =
                                            diff2.Differences.Where(fun d ->
                                                d.RelativePath = diff1Difference.RelativePath
                                                && d.FileSystemEntryType = FileSystemEntryType.File
                                                && d.DifferenceType = DifferenceType.Change)

                                        if diff2DifferenceQuery.Count() = 1 then
                                            // We have a file that's changed in both diffs.
                                            let diff2Difference = diff2DifferenceQuery.First()

                                            // Check the Sha256Hash values; if they're identical, ignore the file.
                                            //let fileVersion1 = parentLatestPromotionLookup[$"{diff1Difference.RelativePath}"]
                                            let fileVersion1 =
                                                parentLatestPromotionLookup.FirstOrDefault(fun kvp -> kvp.Key = $"{diff1Difference.RelativePath}")
                                            //let fileVersion2 = latestReferenceLookup[$"{diff2Difference.RelativePath}"]
                                            let fileVersion2 = latestReferenceLookup.FirstOrDefault(fun kvp -> kvp.Key = $"{diff2Difference.RelativePath}")
                                            //if (not <| isNull(fileVersion1) && not <| isNull(fileVersion2)) && (fileVersion1.Value.Sha256Hash <> fileVersion2.Value.Sha256Hash) then
                                            if (fileVersion1.Value.Sha256Hash <> fileVersion2.Value.Sha256Hash) then
                                                // Compare them at a line level; if there are no overlapping lines, we can just modify the working-directory version.
                                                // ...
                                                // For now, we're just going to show a message.
                                                AnsiConsole.MarkupLine(
                                                    $"[{Colors.Important}]Potential promotion conflict: file {diff1Difference.RelativePath} has been changed in both the latest promotion, and in the current branch.[/]"
                                                )

                                                AnsiConsole.MarkupLine(
                                                    $"[{Colors.Important}]fileVersion1.Sha256Hash: {fileVersion1.Value.Sha256Hash}; fileVersion1.LastWriteTimeUTC: {fileVersion1.Value.LastWriteTimeUtc}.[/]"
                                                )

                                                AnsiConsole.MarkupLine(
                                                    $"[{Colors.Important}]fileVersion2.Sha256Hash: {fileVersion2.Value.Sha256Hash}; fileVersion2.LastWriteTimeUTC: {fileVersion2.Value.LastWriteTimeUtc}.[/]"
                                                )

                                                potentialPromotionConflicts <- true

                                    /// Create new directory versions and updates Grace Status with them.
                                    let getNewGraceStatusAndDirectoryVersions
                                        (showOutput, graceStatus, currentBranch: BranchDto, differences: IEnumerable<FileSystemDifference>)
                                        =
                                        task {
                                            if differences.Count() > 0 then
                                                let! (updatedGraceStatus, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions graceStatus differences

                                                return Ok(updatedGraceStatus, newDirectoryVersions)
                                            else
                                                return Ok(graceStatus, List<LocalDirectoryVersion>())
                                        }

                                    /// Upload new DirectoryVersion records to the server.
                                    let uploadNewDirectoryVersions (currentBranch: BranchDto) (newDirectoryVersions: List<LocalDirectoryVersion>) =
                                        task {
                                            if currentBranch.SaveEnabled && newDirectoryVersions.Any() then
                                                let saveParameters = SaveDirectoryVersionsParameters()
                                                saveParameters.OwnerId <- parameters.OwnerId
                                                saveParameters.OwnerName <- parameters.OwnerName
                                                saveParameters.OrganizationId <- parameters.OrganizationId
                                                saveParameters.OrganizationName <- parameters.OrganizationName
                                                saveParameters.RepositoryId <- parameters.RepositoryId
                                                saveParameters.RepositoryName <- parameters.RepositoryName
                                                saveParameters.CorrelationId <- getCorrelationId parseResult
                                                saveParameters.DirectoryVersions <- newDirectoryVersions.Select(fun dv -> dv.ToDirectoryVersion).ToList()

                                                match! DirectoryVersion.SaveDirectoryVersions saveParameters with
                                                | Ok returnValue -> return Ok()
                                                | Error error -> return Error error
                                            else
                                                return Ok()
                                        }

                                    if not <| potentialPromotionConflicts then
                                        // Yay! No promotion conflicts.
                                        let mutable newGraceStatus = graceStatus

                                        // Update the GraceStatus file with the new file versions (and therefore new LocalDirectoryVersion's) we just put in place.
                                        // filesToDownload is, conveniently, the list of files we're changing in the rebase.
                                        match! getNewGraceStatusAndDirectoryVersions (parseResult |> hasOutput, graceStatus, branchDto, filesToDownload) with
                                        | Ok(updatedGraceStatus, newDirectoryVersions) ->
                                            // Ensure that previous DirectoryVersions for a given path are deleted from GraceStatus.
                                            newDirectoryVersions
                                            |> Seq.iter (fun localDirectoryVersion ->
                                                let directoryVersionsWithSameRelativePath =
                                                    updatedGraceStatus.Index.Values.Where(fun dv -> dv.RelativePath = localDirectoryVersion.RelativePath)

                                                if directoryVersionsWithSameRelativePath.Count() > 1 then
                                                    // Delete all but the most recent DirectoryVersion for this path.
                                                    directoryVersionsWithSameRelativePath
                                                    |> Seq.where (fun dv -> dv.DirectoryVersionId <> localDirectoryVersion.DirectoryVersionId)
                                                    |> Seq.iter (fun dv ->
                                                        let mutable localDirectoryVersion = LocalDirectoryVersion.Default

                                                        updatedGraceStatus.Index.Remove(dv.DirectoryVersionId, &localDirectoryVersion)
                                                        |> ignore))

                                            let! result = uploadNewDirectoryVersions branchDto newDirectoryVersions
                                            do! writeGraceStatusFile updatedGraceStatus
                                            do! updateGraceWatchInterprocessFile updatedGraceStatus
                                            newGraceStatus <- updatedGraceStatus

                                        | Error error -> logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))

                                        // Create a save reference to mark the state of the branch after rebase.
                                        let rootDirectoryVersion = getRootDirectoryVersion newGraceStatus

                                        let saveReferenceParameters =
                                            Parameters.Branch.CreateReferenceParameters(
                                                BranchId = $"{branchDto.BranchId}",
                                                RepositoryId = $"{branchDto.RepositoryId}",
                                                OwnerId = parameters.OwnerId,
                                                OwnerName = parameters.OwnerName,
                                                OrganizationId = parameters.OrganizationId,
                                                OrganizationName = parameters.OrganizationName,
                                                Sha256Hash = rootDirectoryVersion.Sha256Hash,
                                                DirectoryVersionId = rootDirectoryVersion.DirectoryVersionId,
                                                Message =
                                                    $"Save after rebase from {parentBranchDto.BranchName}; {getShortSha256Hash parentLatestPromotion.Sha256Hash} - {parentLatestPromotion.ReferenceText}."
                                            )

                                        match! Branch.Save(saveReferenceParameters) with
                                        | Ok returnValue ->
                                            // Add a rebase event to the branch.
                                            let rebaseParameters =
                                                Parameters.Branch.RebaseParameters(
                                                    BranchId = $"{branchDto.BranchId}",
                                                    RepositoryId = $"{branchDto.RepositoryId}",
                                                    OwnerId = parameters.OwnerId,
                                                    OwnerName = parameters.OwnerName,
                                                    OrganizationId = parameters.OrganizationId,
                                                    OrganizationName = parameters.OrganizationName,
                                                    BasedOn = parentLatestPromotion.ReferenceId
                                                )

                                            match! Branch.Rebase(rebaseParameters) with
                                            | Ok returnValue ->
                                                AnsiConsole.MarkupLine($"[{Colors.Important}]Rebase succeeded.[/]")

                                                AnsiConsole.MarkupLine($"[{Colors.Verbose}]({serialize returnValue})[/]")

                                                return 0
                                            | Error error ->
                                                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                                                return -1
                                        | Error error ->
                                            logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                                            return -1
                                    else
                                        AnsiConsole.MarkupLine($"[{Colors.Highlighted}]A potential promotion conflict was detected. Rebase not successful.[/]")

                                        return -1
                                else
                                    logToAnsiConsole Colors.Error (Markup.Escape($"{errors.First()}"))
                                    return -1
                            else
                                logToAnsiConsole Colors.Error (Markup.Escape($"{errors.First()}"))
                                return -1
                        | Error error ->
                            logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                            return -1
                | Error error ->
                    logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                    return -1
            | Error error ->
                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                return -1
        }

    let private Rebase =
        CommandHandler.Create(fun (parseResult: ParseResult) (rebaseParameters: RebaseParameters) ->
            task {
                try
                    Directory.CreateDirectory(Path.GetDirectoryName(updateInProgressFileName))
                    |> ignore

                    do! File.WriteAllTextAsync(updateInProgressFileName, "`grace rebase` is in progress.")
                    let! graceStatus = readGraceStatusFile ()

                    let! result = rebaseHandler parseResult (rebaseParameters |> normalizeIdsAndNames parseResult) graceStatus

                    return result
                finally
                    File.Delete(updateInProgressFileName)
            })

    type StatusParameters() =
        inherit CommonParameters()

    let private newStatusHandler parseResult (parameters: StatusParameters) =
        task {
            try
                return 0
            with ex ->
                logToAnsiConsole Colors.Error (Markup.Escape($"{ex.Message}"))
                return -1
        }

    let private statusHandler parseResult (parameters: StatusParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let horizontalLineChar = "─"

                // Show repo and branch names.
                let getParameters =
                    GetBranchParameters(
                        OwnerId = parameters.OwnerId,
                        OwnerName = parameters.OwnerName,
                        OrganizationId = parameters.OrganizationId,
                        OrganizationName = parameters.OrganizationName,
                        RepositoryId = parameters.RepositoryId,
                        RepositoryName = parameters.RepositoryName,
                        BranchId = parameters.BranchId,
                        BranchName = parameters.BranchName,
                        CorrelationId = parameters.CorrelationId
                    )

                let! branchResult = Branch.Get(getParameters)
                let! parentBranchResult = Branch.GetParentBranch(getParameters)

                match branchResult, parentBranchResult with
                | Ok branchReturnValue, Ok parentBranchReturnValue ->
                    let branchDto = branchReturnValue.ReturnValue
                    let parentBranchDto = parentBranchReturnValue.ReturnValue

                    // Now that I have the current and parent branch, I can get the details for the latest promotion, latest commit, latest checkpoint, and latest save.
                    let latestSave = branchDto.LatestSave
                    let latestCheckpoint = branchDto.LatestCheckpoint
                    let latestCommit = branchDto.LatestCommit
                    let latestParentBranchPromotion = parentBranchDto.LatestPromotion
                    let basedOn = branchDto.BasedOn

                    let longestAgoLength =
                        [ latestSave
                          latestCheckpoint
                          latestCommit
                          latestParentBranchPromotion
                          basedOn ]
                        |> Seq.map (fun b -> (ago b.CreatedAt).Length)
                        |> Seq.max

                    let aligned (s: string) =
                        let space = " "
                        $"{String.replicate (longestAgoLength - s.Length) space}{s}"

                    let permissions (branchDto: Dto.Branch.BranchDto) =
                        let sb = stringBuilderPool.Get()

                        try
                            if branchDto.PromotionEnabled then sb.Append("Promotion/") |> ignore

                            if branchDto.CommitEnabled then sb.Append("Commit/") |> ignore

                            if branchDto.CheckpointEnabled then sb.Append("Checkpoint/") |> ignore

                            if branchDto.SaveEnabled then sb.Append("Save/") |> ignore

                            if branchDto.TagEnabled then sb.Append("Tag/") |> ignore

                            if branchDto.ExternalEnabled then sb.Append("External/") |> ignore

                            if sb.Length > 0 && sb[sb.Length - 1] = '/' then
                                sb.Remove(sb.Length - 1, 1) |> ignore

                            sb.ToString()
                        finally
                            if not <| isNull sb then stringBuilderPool.Return(sb)

                    let ownerLabel = getLocalizedString Text.StringResourceName.Owner
                    let organizationLabel = getLocalizedString Text.StringResourceName.Organization
                    let repositoryLabel = getLocalizedString Text.StringResourceName.Repository
                    let branchLabel = getLocalizedString Text.StringResourceName.Branch

                    let headerLength = ownerLabel.Length + organizationLabel.Length + repositoryLabel.Length + 6

                    let column1 = TableColumn(String.replicate headerLength horizontalLineChar)

                    let getReferencesParameters =
                        Parameters.Branch.GetReferencesParameters(
                            BranchId = parameters.BranchId,
                            BranchName = parameters.BranchName,
                            OwnerId = parameters.OwnerId,
                            OwnerName = parameters.OwnerName,
                            OrganizationId = parameters.OrganizationId,
                            OrganizationName = parameters.OrganizationName,
                            RepositoryId = parameters.RepositoryId,
                            RepositoryName = parameters.RepositoryName,
                            MaxCount = 5,
                            CorrelationId = parameters.CorrelationId
                        )

                    let! lastFiveReferences =
                        task {
                            match! Branch.GetReferences(getReferencesParameters) with
                            | Ok returnValue -> return returnValue.ReturnValue
                            | Error error -> return Array.Empty<ReferenceDto>()
                        }

                    let basedOnMessage =
                        if branchDto.BasedOn.ReferenceId = parentBranchDto.LatestPromotion.ReferenceId || branchDto.ParentBranchId = Constants.DefaultParentBranchId then
                                $"[{Colors.Added}]Based on latest promotion.[/]"
                            else
                                $"[{Colors.Important}]Not based on latest promotion.[/]"

                    let referenceTable = (createReferenceTable parseResult lastFiveReferences).Expand()

                    let ownerOrgRepoHeader =
                        if parseResult |> verbose then
                            $"[{Colors.Important}]{ownerLabel}:[/] {Current().OwnerName} [{Colors.Deemphasized}]│ {Current().OwnerId}[/] │ [{Colors.Important}]{organizationLabel}:[/] {Current().OrganizationName} [{Colors.Deemphasized}]│ {Current().OrganizationId}[/] │ [{Colors.Important}]{repositoryLabel}:[/] {Current().RepositoryName} [{Colors.Deemphasized}]│ {Current().RepositoryId}[/]"
                        else
                            $"[{Colors.Important}]{ownerLabel}:[/] {Current().OwnerName} │ [{Colors.Important}]{organizationLabel}:[/] {Current().OrganizationName} │ [{Colors.Important}]{repositoryLabel}:[/] {Current().RepositoryName}"

                    let branchHeader =
                        if parseResult |> verbose then
                            $"[{Colors.Important}]{branchLabel}:[/] {branchDto.BranchName} [{Colors.Deemphasized}]─ Allows {permissions branchDto} ─ {branchDto.BranchId}[/]"
                        else
                            $"[{Colors.Important}]{branchLabel}:[/] {branchDto.BranchName} [{Colors.Deemphasized}]─ Allows {permissions branchDto}[/]"

                    let parentBranchHeader =
                        if parseResult |> verbose then
                            $"[{Colors.Important}]Parent branch:[/] {parentBranchDto.BranchName} [{Colors.Deemphasized}]─ Allows {permissions parentBranchDto} ─ {parentBranchDto.BranchId}[/]"
                        else
                            $"[{Colors.Important}]Parent branch:[/] {parentBranchDto.BranchName} [{Colors.Deemphasized}]─ Allows {permissions parentBranchDto}[/]"

                    let commitReferenceTable =
                        let sortedReferences = [| latestCommit; latestCheckpoint |] |> Array.sortByDescending (fun r -> r.CreatedAt)
                        (createReferenceTable parseResult sortedReferences).Expand()

                    let outerTable = Table(Border = TableBorder.None, ShowHeaders = false).AddColumns(column1)

                    let branchTable = Table(ShowHeaders = false, Border = TableBorder.None, Expand = true)
                    branchTable
                        .AddColumn(column1)
                        .AddRow(basedOnMessage)
                        .AddEmptyRow()
                        .AddRow($"[{Colors.Important}] Most recent references:[/]")
                        .AddRow(Padder(referenceTable).Padding(1, 0, 0, 0))
                        .AddEmptyRow() |> ignore

                    if branchDto.CheckpointEnabled || branchDto.CommitEnabled then
                        branchTable
                            .AddRow($"[{Colors.Important}] Most recent checkpoint and commit:[/]")
                            .AddRow(Padder(commitReferenceTable).Padding(1, 0, 0, 0))
                        |> ignore

                    let branchPanel = Panel(branchTable, Expand = true)
                    branchPanel.Header <- PanelHeader(branchHeader, Justify.Left)
                    branchPanel.Border <- BoxBorder.Double
                    outerTable.AddRow(branchPanel).AddEmptyRow() |> ignore

                    if branchDto.ParentBranchId <> Constants.DefaultParentBranchId then
                        let getParentBranchReferencesParameters = 
                            GetReferencesParameters(
                                BranchId = $"{branchDto.ParentBranchId}",
                                OwnerId = parameters.OwnerId,
                                OwnerName = parameters.OwnerName,
                                OrganizationId = parameters.OrganizationId,
                                OrganizationName = parameters.OrganizationName,
                                RepositoryId = parameters.RepositoryId,
                                RepositoryName = parameters.RepositoryName,
                                MaxCount = 5,
                                CorrelationId = parameters.CorrelationId
                            )

                        match! Branch.GetReferences(getParentBranchReferencesParameters) with
                        | Ok returnValue ->
                            let parentBranchReferences = returnValue.ReturnValue

                            let parentBranchReferencesTable = (createReferenceTable parseResult parentBranchReferences).Expand()

                            let parentBranchTable = Table(ShowHeaders = false, Border = TableBorder.None, Expand = true)
                            parentBranchTable
                                .AddColumn(column1)
                                .AddEmptyRow()
                                .AddRow($"[{Colors.Important}] Most recent references:[/]")
                                .AddRow(Padder(parentBranchReferencesTable).Padding(1, 0, 0, 0))
                            |> ignore

                            let parentBranchPanel = Panel(parentBranchTable, Expand = true)
                            parentBranchPanel.Header <- PanelHeader(parentBranchHeader, Justify.Left)
                            parentBranchPanel.Border <- BoxBorder.Double
                            outerTable.AddRow(parentBranchPanel) |> ignore

                        | Error error ->
                            logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                    else
                        outerTable.AddRow($"[{Colors.Important}]Parent branch[/]: None") |> ignore

                    outerTable.AddEmptyRow().AddRow(ownerOrgRepoHeader) |> ignore

                    AnsiConsole.Write(outerTable)

                    return 0
                | Error error, _
                | _, Error error ->
                    logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                    return -1
            with ex ->
                logToAnsiConsole Colors.Error (Markup.Escape($"{ExceptionResponse.Create ex}"))
                return -1
        }

    let private Status =
        CommandHandler.Create(fun (parseResult: ParseResult) (statusParameters: StatusParameters) ->
            task {
                let! result = statusHandler parseResult (statusParameters |> normalizeIdsAndNames parseResult)
                return result
            })

    type DeleteParameters() =
        inherit CommonParameters()

    let private deleteHandler (parseResult: ParseResult) (parameters: DeleteParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = CommonValidations parseResult parameters

                match validateIncomingParameters with
                | Ok _ ->
                    let parameters = parameters |> normalizeIdsAndNames parseResult

                    let deleteParameters =
                        Parameters.Branch.DeleteBranchParameters(
                            BranchId = parameters.BranchId,
                            BranchName = parameters.BranchName,
                            OwnerId = parameters.OwnerId,
                            OwnerName = parameters.OwnerName,
                            OrganizationId = parameters.OrganizationId,
                            OrganizationName = parameters.OrganizationName,
                            RepositoryId = parameters.RepositoryId,
                            RepositoryName = parameters.RepositoryName,
                            CorrelationId = parameters.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Branch.Delete(deleteParameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Branch.Delete(deleteParameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private Delete =
        CommandHandler.Create(fun (parseResult: ParseResult) (deleteParameters: DeleteParameters) ->
            task {
                let! result = deleteHandler parseResult deleteParameters
                return result |> renderOutput parseResult
            })

    //type UndeleteParameters() =
    //    inherit CommonParameters()
    //let private undeleteHandler (parseResult: ParseResult) (undeleteParameters: UndeleteParameters) =
    //    task {
    //        try
    //            if parseResult |> verbose then printParseResult parseResult
    //            let validateIncomingParameters = CommonValidations parseResult undeleteParameters
    //            match validateIncomingParameters with
    //            | Ok _ ->
    //                let parameters = Parameters.Owner.UndeleteParameters(OwnerId = undeleteParameters.OwnerId, OwnerName = undeleteParameters.OwnerName, CorrelationId = undeleteParameters.CorrelationId)
    //                if parseResult |> showOutput then
    //                    return! progress.Columns(progressColumns)
    //                            .StartAsync(fun progressContext ->
    //                            task {
    //                                let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
    //                                let! result = Owner.Undelete(parameters)
    //                                t0.Increment(100.0)
    //                                return result
    //                            })
    //                else
    //                    return! Owner.Undelete(parameters)
    //            | Error error -> return Error error
    //        with
    //            | ex -> return Error (GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
    //    }
    //let private Undelete =
    //    CommandHandler.Create(fun (parseResult: ParseResult) (undeleteParameters: UndeleteParameters) ->
    //        task {
    //            let! result = undeleteHandler parseResult undeleteParameters
    //            return result |> renderOutput parseResult
    //        })

    let Build =
        let addCommonOptions (command: Command) =
            command
            |> addOption Options.ownerName
            |> addOption Options.ownerId
            |> addOption Options.organizationName
            |> addOption Options.organizationId
            |> addOption Options.repositoryName
            |> addOption Options.repositoryId
            |> addOption Options.branchName
            |> addOption Options.branchId

        // Create main command and aliases, if any.`
        let branchCommand = new Command("branch", Description = "Create, change, or delete branch-level information.")

        branchCommand.AddAlias("br")

        // Add subcommands.
        let branchCreateCommand =
            new Command("create", Description = "Create a new branch.")
            |> addOption Options.branchNameRequired
            |> addOption Options.branchId
            |> addOption Options.parentBranchName
            |> addOption Options.parentBranchId
            |> addOption Options.ownerName
            |> addOption Options.ownerId
            |> addOption Options.organizationName
            |> addOption Options.organizationId
            |> addOption Options.repositoryName
            |> addOption Options.repositoryId
            |> addOption Options.initialPermissions
            |> addOption Options.doNotSwitch

        branchCreateCommand.Handler <- Create
        branchCommand.AddCommand(branchCreateCommand)

        let switchCommand =
            new Command(
                "switch",
                Description =
                    "Switches your current branch to another branch, or to a specific reference or Sha256Hash. If a Sha256Hash is provided, the current branch will be set to the version with that hash."
            )
            |> addOption Options.toBranchId
            |> addOption Options.toBranchName
            |> addOption Options.sha256Hash
            |> addOption Options.referenceId
            |> addCommonOptions

        switchCommand.AddAlias("download")
        switchCommand.Handler <- Switch
        branchCommand.AddCommand(switchCommand)

        let statusCommand =
            new Command("status", Description = "Displays status information about the current repository and branch.")
            |> addCommonOptions

        statusCommand.Handler <- Status
        branchCommand.AddCommand(statusCommand)

        let promoteCommand =
            new Command("promote", Description = "Promotes a commit into the parent branch.")
            |> addOption Options.message
            |> addCommonOptions

        promoteCommand.Handler <- Promote
        branchCommand.AddCommand(promoteCommand)

        let commitCommand =
            new Command("commit", Description = "Create a commit.")
            |> addOption Options.messageRequired
            |> addCommonOptions

        commitCommand.Handler <- Commit
        branchCommand.AddCommand(commitCommand)

        let checkpointCommand =
            new Command("checkpoint", Description = "Create a checkpoint.")
            |> addOption Options.message
            |> addCommonOptions

        checkpointCommand.Handler <- Checkpoint
        branchCommand.AddCommand(checkpointCommand)

        let saveCommand =
            new Command("save", Description = "Create a save.")
            |> addOption Options.message
            |> addCommonOptions

        saveCommand.Handler <- Save
        branchCommand.AddCommand(saveCommand)

        let tagCommand =
            new Command("tag", Description = "Create a tag.")
            |> addOption Options.messageRequired
            |> addCommonOptions

        tagCommand.Handler <- Tag
        branchCommand.AddCommand(tagCommand)

        let createExternalCommand =
            new Command("create-external", Description = "Create an external reference.")
            |> addOption Options.messageRequired
            |> addCommonOptions

        createExternalCommand.Handler <- CreateExternal
        branchCommand.AddCommand(createExternalCommand)

        let rebaseCommand =
            new Command("rebase", Description = "Rebase this branch on a promotion from the parent branch.")
            |> addCommonOptions

        rebaseCommand.Handler <- Rebase
        branchCommand.AddCommand(rebaseCommand)

        let listContentsCommand =
            new Command("list-contents", Description = "List directories and files in the current branch.")
            |> addOption Options.referenceId
            |> addOption Options.sha256Hash
            |> addOption Options.forceRecompute
            |> addCommonOptions

        listContentsCommand.Handler <- ListContents
        branchCommand.AddCommand(listContentsCommand)

        let getRecursiveSizeCommand =
            new Command("get-recursive-size", Description = "Get the recursive size of the current branch.")
            |> addOption Options.referenceId
            |> addOption Options.sha256Hash
            |> addCommonOptions

        getRecursiveSizeCommand.Handler <- GetRecursiveSize
        branchCommand.AddCommand(getRecursiveSizeCommand)

        let enableAssignCommand =
            new Command("enable-assign", Description = "Enable or disable assigning promotions on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enableAssignCommand.Handler <- EnableAssign
        branchCommand.AddCommand(enableAssignCommand)

        let enablePromotionCommand =
            new Command("enable-promotion", Description = "Enable or disable promotions on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enablePromotionCommand.Handler <- EnablePromotion
        branchCommand.AddCommand(enablePromotionCommand)

        let enableCommitCommand =
            new Command("enable-commit", Description = "Enable or disable commits on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enableCommitCommand.Handler <- EnableCommit
        branchCommand.AddCommand(enableCommitCommand)

        let enableCheckpointsCommand =
            new Command("enable-checkpoints", Description = "Enable or disable checkpoints on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enableCheckpointsCommand.Handler <- EnableCheckpoint
        branchCommand.AddCommand(enableCheckpointsCommand)

        let enableSaveCommand =
            new Command("enable-save", Description = "Enable or disable saves on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enableSaveCommand.Handler <- EnableSave
        branchCommand.AddCommand(enableSaveCommand)

        let enableTagCommand =
            new Command("enable-tag", Description = "Enable or disable tags on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enableTagCommand.Handler <- EnableTag
        branchCommand.AddCommand(enableTagCommand)

        let enableExternalCommand =
            new Command("enable-external", Description = "Enable or disable external references on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enableExternalCommand.Handler <- EnableExternal
        branchCommand.AddCommand(enableExternalCommand)

        let enableAutoRebaseCommand =
            new Command("enable-auto-rebase", Description = "Enable or disable auto-rebase on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enableAutoRebaseCommand.Handler <- EnableAutoRebase
        branchCommand.AddCommand(enableAutoRebaseCommand)

        let setNameCommand =
            new Command("set-name", Description = "Change the name of the branch.")
            |> addOption Options.newName
            |> addCommonOptions

        setNameCommand.Handler <- SetName
        branchCommand.AddCommand(setNameCommand)

        let getCommand =
            new Command("get", Description = "Gets details for the branch.")
            |> addOption Options.includeDeleted
            |> addOption Options.showEvents
            |> addCommonOptions

        getCommand.Handler <- Get
        branchCommand.AddCommand(getCommand)

        let getReferencesCommand =
            new Command("get-references", Description = "Retrieves a list of the most recent references from the branch.")
            |> addCommonOptions
            |> addOption Options.maxCount
            |> addOption Options.fullSha

        getReferencesCommand.Handler <- GetReferences
        branchCommand.AddCommand(getReferencesCommand)

        let getPromotionsCommand =
            new Command("get-promotions", Description = "Retrieves a list of the most recent promotions from the branch.")
            |> addCommonOptions
            |> addOption Options.maxCount
            |> addOption Options.fullSha

        getPromotionsCommand.Handler <- GetPromotions
        branchCommand.AddCommand(getPromotionsCommand)

        let getCommitsCommand =
            new Command("get-commits", Description = "Retrieves a list of the most recent commits from the branch.")
            |> addCommonOptions
            |> addOption Options.maxCount
            |> addOption Options.fullSha

        getCommitsCommand.Handler <- GetCommits
        branchCommand.AddCommand(getCommitsCommand)

        let getCheckpointsCommand =
            new Command("get-checkpoints", Description = "Retrieves a list of the most recent checkpoints from the branch.")
            |> addCommonOptions
            |> addOption Options.maxCount
            |> addOption Options.fullSha

        getCheckpointsCommand.Handler <- GetCheckpoints
        branchCommand.AddCommand(getCheckpointsCommand)

        let getSavesCommand =
            new Command("get-saves", Description = "Retrieves a list of the most recent saves from the branch.")
            |> addCommonOptions
            |> addOption Options.maxCount
            |> addOption Options.fullSha

        getSavesCommand.Handler <- GetSaves
        branchCommand.AddCommand(getSavesCommand)

        let getTagsCommand =
            new Command("get-tags", Description = "Retrieves a list of the most recent tags from the branch.")
            |> addCommonOptions
            |> addOption Options.maxCount
            |> addOption Options.fullSha

        getTagsCommand.Handler <- GetTags
        branchCommand.AddCommand(getTagsCommand)

        let getExternalsCommand =
            new Command("get-externals", Description = "Retrieves a list of the most recent external references from the branch.")
            |> addCommonOptions
            |> addOption Options.maxCount
            |> addOption Options.fullSha

        getExternalsCommand.Handler <- GetExternals
        branchCommand.AddCommand(getExternalsCommand)

        let deleteCommand = new Command("delete", Description = "Delete the branch.") |> addCommonOptions

        deleteCommand.Handler <- Delete
        branchCommand.AddCommand(deleteCommand)

        let assignCommand =
            new Command("assign", Description = "Assign a promotion to this branch.")
            |> addOption Options.directoryVersionId
            |> addOption Options.sha256Hash
            |> addOption Options.message
            |> addCommonOptions

        assignCommand.Handler <- Assign
        branchCommand.AddCommand(assignCommand)

        //let undeleteCommand = new Command("undelete", Description = "Undelete a deleted owner.") |> addCommonOptions
        //undeleteCommand.Handler <- Undelete
        //branchCommand.AddCommand(undeleteCommand)

        branchCommand
