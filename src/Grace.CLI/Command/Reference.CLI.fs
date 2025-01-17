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
open Grace.Shared.Parameters.Directory
open Grace.Shared.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors.Branch
open NodaTime
open NodaTime.TimeZones
open Spectre.Console
open Spectre.Console.Json
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
open Grace.Shared.Parameters.Storage

module Reference =
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
                getDefaultValue = (fun _ -> $"{Current().BranchId}")
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
                getDefaultValue = (fun _ -> $"{Current().OwnerId}")
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
                getDefaultValue = (fun _ -> $"{Current().OrganizationId}")
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
                getDefaultValue = (fun _ -> $"{Current().RepositoryId}")
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

        let directoryVersionId =
            new Option<String>(
                [| "--directoryVersionId"; "-v" |],
                IsRequired = false,
                Description = "The directory version ID to assign to the promotion <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

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

                match validateIncomingParameters with
                | Ok _ ->
                    //let sha256Bytes = SHA256.HashData(Encoding.ASCII.GetBytes(rnd.NextInt64().ToString("x8")))
                    //let sha256Hash = Seq.fold (fun (sb: StringBuilder) currentByte ->
                    //    sb.Append(sprintf $"{currentByte:X2}")) (StringBuilder(sha256Bytes.Length)) sha256Bytes
                    let repositoryId = RepositoryId.Parse(parameters.RepositoryId)

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
                                                    GetUploadMetadataForFilesParameters(
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
                                                saveParameters.DirectoryId <- $"{newGraceStatus.RootDirectoryId}"

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
                            GetUploadMetadataForFilesParameters(
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
        let referenceCommand = new Command("reference", Description = "Create or delete references.")

        referenceCommand.AddAlias("ref")

        let promoteCommand =
            new Command("promote", Description = "Promotes a commit into the parent branch.")
            |> addOption Options.message
            |> addCommonOptions

        promoteCommand.Handler <- Promote
        referenceCommand.AddCommand(promoteCommand)

        let commitCommand =
            new Command("commit", Description = "Create a commit.")
            |> addOption Options.messageRequired
            |> addCommonOptions

        commitCommand.Handler <- Commit
        referenceCommand.AddCommand(commitCommand)

        let checkpointCommand =
            new Command("checkpoint", Description = "Create a checkpoint.")
            |> addOption Options.message
            |> addCommonOptions

        checkpointCommand.Handler <- Checkpoint
        referenceCommand.AddCommand(checkpointCommand)

        let saveCommand =
            new Command("save", Description = "Create a save.")
            |> addOption Options.message
            |> addCommonOptions

        saveCommand.Handler <- Save
        referenceCommand.AddCommand(saveCommand)

        let tagCommand =
            new Command("tag", Description = "Create a tag.")
            |> addOption Options.messageRequired
            |> addCommonOptions

        tagCommand.Handler <- Tag
        referenceCommand.AddCommand(tagCommand)

        let createExternalCommand =
            new Command("create-external", Description = "Create an external reference.")
            |> addOption Options.messageRequired
            |> addCommonOptions

        createExternalCommand.Handler <- CreateExternal
        referenceCommand.AddCommand(createExternalCommand)

        let getCommand =
            new Command("get", Description = "Gets details for the branch.")
            |> addOption Options.includeDeleted
            |> addOption Options.showEvents
            |> addCommonOptions

        getCommand.Handler <- Get
        referenceCommand.AddCommand(getCommand)

        let deleteCommand = new Command("delete", Description = "Delete the branch.") |> addCommonOptions

        deleteCommand.Handler <- Delete
        referenceCommand.AddCommand(deleteCommand)

        let assignCommand =
            new Command("assign", Description = "Assign a promotion to this branch.")
            |> addOption Options.directoryVersionId
            |> addOption Options.sha256Hash
            |> addOption Options.message
            |> addCommonOptions

        assignCommand.Handler <- Assign
        referenceCommand.AddCommand(assignCommand)

        //let undeleteCommand = new Command("undelete", Description = "Undelete a deleted owner.") |> addCommonOptions
        //undeleteCommand.Handler <- Undelete
        //branchCommand.AddCommand(undeleteCommand)

        referenceCommand
