namespace Grace.CLI.Command

open FSharpPlus
open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Client.Theme
open Grace.Shared.Parameters.Branch
open Grace.Shared.Parameters.DirectoryVersion
open Grace.Shared.Services
open Grace.Shared.Resources
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors
open Grace.Types.Branch
open Grace.Types.DirectoryVersion
open Grace.Types.Reference
open Grace.Types.Types
open NodaTime
open NodaTime.TimeZones
open Spectre.Console
open Spectre.Console.Json
open Spectre.Console.Rendering
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Globalization
open System.IO
open System.IO.Enumeration
open System.Linq
open System.Threading
open System.Security.Cryptography
open System.Threading.Tasks
open System.Text
open System.Text.Json
open Grace.Shared.Parameters
open Grace.Shared.Client
open System.Text.RegularExpressions

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

        let branchId =
            new Option<BranchId>(
                OptionName.BranchId,
                [| "-i" |],
                Required = false,
                Description = "The branch's ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> if Current().BranchId = Guid.Empty then Guid.NewGuid() else Current().BranchId)
            )

        let branchName =
            new Option<String>(
                OptionName.BranchName,
                [| "-b" |],
                Required = false,
                Description = "The name of the branch. [default: current branch]",
                Arity = ArgumentArity.ExactlyOne
            )

        let branchNameRequired =
            new Option<String>(OptionName.BranchName, [| "-b" |], Required = true, Description = "The name of the branch.", Arity = ArgumentArity.ExactlyOne)

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
                Arity = ArgumentArity.ExactlyOne,
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

        let parentBranchId =
            new Option<Guid>(
                OptionName.ParentBranchId,
                [||],
                Required = false,
                Description = "The parent branch's ID <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let parentBranchName =
            new Option<String>(
                OptionName.ParentBranchName,
                [||],
                Required = false,
                Description = "The name of the parent branch. [default: current branch]",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> $"{Current().BranchName}")
            )

        let newName = new Option<String>(OptionName.NewName, Required = true, Description = "The new name of the branch.", Arity = ArgumentArity.ExactlyOne)

        let message =
            new Option<String>(
                OptionName.Message,
                [| "-m" |],
                Required = false,
                Description = "The text to store with this reference.",
                Arity = ArgumentArity.ExactlyOne
            )

        let messageRequired =
            new Option<String>(
                OptionName.Message,
                [| "-m" |],
                Required = true,
                Description = "The text to store with this reference.",
                Arity = ArgumentArity.ExactlyOne
            )

        let referenceType =
            (new Option<String>(OptionName.ReferenceType, Required = false, Description = "The type of reference.", Arity = ArgumentArity.ExactlyOne))
                .AcceptOnlyFromAmong(listCases<ReferenceType> ())

        let doNotSwitch =
            new Option<bool>(
                OptionName.DoNotSwitch,
                Required = false,
                Description = "Do not switch your current branch to the new branch after it is created. By default, the new branch becomes the current branch.",
                Arity = ArgumentArity.ZeroOrOne
            )

        let fullSha =
            new Option<bool>(
                OptionName.FullSha,
                Required = false,
                Description = "Show the full SHA-256 value in output.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let maxCount =
            new Option<int>(
                OptionName.MaxCount,
                Required = false,
                Description = "The maximum number of results to return.",
                Arity = ArgumentArity.ExactlyOne,
                DefaultValueFactory = (fun _ -> 30)
            )

        let referenceId =
            new Option<ReferenceId>(OptionName.ReferenceId, [||], Required = false, Description = "The reference ID <Guid>.", Arity = ArgumentArity.ExactlyOne)

        let sha256Hash =
            new Option<String>(
                OptionName.Sha256Hash,
                [||],
                Required = false,
                Description = "The full or partial SHA-256 hash value of the version.",
                Arity = ArgumentArity.ExactlyOne
            )

        let enabled =
            new Option<bool>(
                OptionName.Enabled,
                [||],
                Required = false,
                Description = "True to enable the feature; false to disable it.",
                DefaultValueFactory = (fun _ -> false)
            )

        let includeDeleted =
            new Option<bool>(OptionName.IncludeDeleted, [| "-d" |], Required = false, Description = "Include deleted branches in the result. [default: false]")

        let showEvents =
            new Option<bool>(OptionName.ShowEvents, [| "-e" |], Required = false, Description = "Include actor events in the result. [default: false]")

        let initialPermissions =
            new Option<ReferenceType array>(
                OptionName.InitialPermissions,
                Required = false,
                Description = "A list of reference types allowed in this branch.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> [| Commit; Checkpoint; Save; Tag; External |])
            )

        let reassignChildBranches =
            new Option<bool>(
                OptionName.ReassignChildBranches,
                [| "--reassign-child-branches" |],
                Required = false,
                Description = "Reassign child branches to a new parent when deleting a branch.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let newParentBranchId =
            new Option<String>(
                OptionName.NewParentBranchId,
                [| "--new-parent-branch-id" |],
                Required = false,
                Description = "The new parent branch's ID <Guid> for reassigning children.",
                Arity = ArgumentArity.ExactlyOne
            )

        let newParentBranchName =
            new Option<String>(
                OptionName.NewParentBranchName,
                [| "--new-parent-branch-name" |],
                Required = false,
                Description = "The name of the new parent branch for reassigning children.",
                Arity = ArgumentArity.ExactlyOne
            )

        let toBranchId =
            new Option<BranchId>(
                OptionName.ToBranchId,
                [| "-d" |],
                Required = false,
                Description = "The ID of the branch to switch to <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )

        let toBranchName =
            new Option<String>(
                OptionName.ToBranchName,
                [| "-c" |],
                Required = false,
                Description = "The name of the branch to switch to.",
                Arity = ArgumentArity.ExactlyOne
            )

        let forceRecompute =
            new Option<bool>(
                OptionName.ForceRecompute,
                Required = false,
                Description = "Force the re-computation of the recursive directory contents. [default: false]",
                Arity = ArgumentArity.ZeroOrOne
            )

        let directoryVersionId =
            new Option<DirectoryVersionId>(
                OptionName.DirectoryVersionId,
                [| "-v" |],
                Required = false,
                Description = "The directory version ID to assign to the promotion <Guid>.",
                Arity = ArgumentArity.ExactlyOne
            )
    //let listDirectories = new Option<bool>("--listDirectories", Required = false, Description = "Show directories when listing contents. [default: false]")
    //let listFiles = new Option<bool>("--listFiles", Required = false, Description = "Show files when listing contents. Implies --listDirectories. [default: false]")

    let mustBeAValidGuid (parseResult: ParseResult) (parameters: CommonParameters) (option: Option) (value: string) (error: BranchError) =
        let mutable guid = Guid.Empty

        if
            parseResult.GetResult(option) <> null
            && not <| String.IsNullOrEmpty(value)
            && (Guid.TryParse(value, &guid) = false || guid = Guid.Empty)
        then
            Error(GraceError.Create (getErrorMessage error) (getCorrelationId parseResult))
        else
            Ok(parseResult, parameters)

    let mustBeAValidGraceName (parseResult: ParseResult) (parameters: CommonParameters) (option: Option) (value: string) (error: BranchError) =
        if
            parseResult.GetResult(option) <> null
            && not <| Constants.GraceNameRegex.IsMatch(value)
        then
            Error(GraceError.Create (getErrorMessage error) (getCorrelationId parseResult))
        else
            Ok(parseResult, parameters)

    let oneOfTheseOptionsMustBeProvided (parseResult: ParseResult) (options: Option array) (error: BranchError) =
        match options |> Array.tryFind (fun opt -> not <| isNull (parseResult.GetResult(opt))) with
        | Some opt -> Ok(parseResult)
        | None -> Error(GraceError.Create (getErrorMessage error) (getCorrelationId parseResult))

    /// Adjusts parameters to account for whether Id's or Name's were specified by the user, or should be taken from default values.
    let normalizeIdsAndNames<'T when 'T :> CommonParameters> (parseResult: ParseResult) (parameters: 'T) =
        if
            parseResult.GetResult(Options.ownerId).Implicit
            && not <| isNull (parseResult.GetResult(Options.ownerName))
            && not <| parseResult.GetResult(Options.ownerName).Implicit
        then
            parameters.OwnerId <- String.Empty

        if
            parseResult.GetResult(Options.organizationId).Implicit
            && not <| isNull (parseResult.GetResult(Options.organizationName))
            && not <| parseResult.GetResult(Options.organizationName).Implicit
        then
            parameters.OrganizationId <- String.Empty

        if
            parseResult.GetResult(Options.repositoryId).Implicit
            && not <| isNull (parseResult.GetResult(Options.repositoryName))
            && not <| parseResult.GetResult(Options.repositoryName).Implicit
        then
            parameters.RepositoryId <- String.Empty

        parameters

    let private CommonValidations parseResult =
        let ``Grace index file must exist`` (parseResult: ParseResult) =
            if not <| File.Exists(Current().GraceStatusFile) then
                Error(GraceError.Create (getErrorMessage BranchError.IndexFileNotFound) (getCorrelationId parseResult))
            else
                Ok(parseResult)

        let ``Grace object cache file must exist`` (parseResult: ParseResult) =
            if not <| File.Exists(Current().GraceStatusFile) then
                Error(GraceError.Create (getErrorMessage BranchError.ObjectCacheFileNotFound) (getCorrelationId parseResult))
            else
                Ok(parseResult)

        let ``Message must not be empty`` (parseResult: ParseResult) =
            if
                parseResult.CommandResult.Command.Options.FirstOrDefault(fun option -> option.Name = OptionName.Message)
                <> null
            then
                let message = parseResult.GetValue<string>(OptionName.Message).Trim()

                if not <| String.IsNullOrEmpty(message) then
                    Ok(parseResult)
                else
                    Error(GraceError.Create (getErrorMessage BranchError.MessageIsRequired) (getCorrelationId parseResult))
            else
                Ok(parseResult)

        let ``Message must be less than 2048 characters`` (parseResult: ParseResult) =
            if
                parseResult.CommandResult.Command.Options.FirstOrDefault(fun option -> option.Name = OptionName.Message)
                <> null
            then
                let message = parseResult.GetValue<string>(OptionName.Message).Trim()

                if message.Length <= 2048 then
                    Ok(parseResult)
                else
                    Error(GraceError.Create (getErrorMessage BranchError.StringIsTooLong) (getCorrelationId parseResult))
            else
                Ok(parseResult)

        (parseResult)
        |> ``Grace index file must exist``
        >>= ``Grace object cache file must exist``
        >>= ``Message must not be empty``
        >>= ``Message must be less than 2048 characters``

    let private ``BranchName must not be empty`` (parseResult: ParseResult) =
        let graceIds = getNormalizedIdsAndNames parseResult

        if
            (parseResult.CommandResult.Command.Options.Contains(Options.branchNameRequired)
             || parseResult.CommandResult.Command.Options.Contains(Options.branchName))
            && not <| String.IsNullOrEmpty(graceIds.BranchName)
        then
            Ok parseResult
        else
            Error(GraceError.Create (getErrorMessage BranchError.BranchNameIsRequired) (getCorrelationId parseResult))

    let private valueOrEmpty (value: string) = if String.IsNullOrWhiteSpace(value) then String.Empty else value

    let private guidToString (value: Guid) = if value = Guid.Empty then String.Empty else $"{value}"

    let private fallbackString hasValue supplied fallbackValue = if hasValue then supplied |> valueOrEmpty else fallbackValue |> valueOrEmpty

    let private fallbackGuidString hasValue supplied fallbackValue = if hasValue then supplied |> valueOrEmpty else fallbackValue |> guidToString

    // Create subcommand.
    type Create() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let validateIncomingParameters = parseResult |> CommonValidations >>= ``BranchName must not be empty``

                    match validateIncomingParameters with
                    | Ok _ ->
                        // In a Create() command, if --branch-id is implicit, that's the current branch taken from graceconfig.json, and the
                        //   current branch, by default, is the parent branch of the new one. Therefore, we need to set BranchId to a new Guid.
                        let mutable graceIds = parseResult |> getNormalizedIdsAndNames

                        if parseResult.GetResult(Options.branchId).Implicit then
                            let branchId = Guid.NewGuid()
                            graceIds <- { graceIds with BranchId = branchId; BranchIdString = $"{branchId}" }

                        let parentBranchId = parseResult.GetValue(Options.parentBranchId)
                        let parentBranchName = parseResult.GetValue(Options.parentBranchName) |> valueOrEmpty

                        let parentBranchIdString =
                            match parentBranchId, parentBranchName with
                            | parentBranchId, parentBranchName when parentBranchId <> Guid.Empty -> parentBranchId.ToString()
                            | parentBranchId, parentBranchName when parentBranchName <> String.Empty -> String.Empty
                            | _ ->
                                if parseResult.GetResult(Options.branchId).Implicit then
                                    parseResult.GetValue(Options.branchId).ToString()
                                else
                                    String.Empty

                        let initialPermissions =
                            match parseResult.GetValue(Options.initialPermissions) with
                            | null -> Array.empty<ReferenceType>
                            | permissions -> permissions

                        let parameters =
                            CreateBranchParameters(
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                BranchId = graceIds.BranchIdString,
                                BranchName = graceIds.BranchName,
                                ParentBranchId = parentBranchIdString,
                                ParentBranchName = parentBranchName,
                                InitialPermissions = initialPermissions,
                                CorrelationId = graceIds.CorrelationId
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Branch.Create(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Branch.Create(parameters)

                        match result with
                        | Ok returnValue ->
                            if not <| parseResult.GetValue(Options.doNotSwitch) then
                                let newConfig = Current()
                                newConfig.BranchId <- Guid.Parse($"{returnValue.Properties[nameof BranchId]}")
                                newConfig.BranchName <- $"{returnValue.Properties[nameof BranchName]}"
                                updateConfiguration newConfig

                            return result |> renderOutput parseResult
                        | Error _ -> return result |> renderOutput parseResult
                    | Error error -> return GraceResult.Error error |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)

                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type GetRecursiveSize() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations
                    let correlationId = getCorrelationId parseResult

                    match validateIncomingParameters with
                    | Ok _ ->
                        let referenceId =
                            if isNull (parseResult.GetResult(Options.referenceId)) then
                                String.Empty
                            else
                                parseResult.GetValue(Options.referenceId).ToString()

                        let sha256Hash =
                            if isNull (parseResult.GetResult(Options.sha256Hash)) then
                                String.Empty
                            else
                                parseResult.GetValue(Options.sha256Hash)

                        let sdkParameters =
                            Parameters.Branch.ListContentsParameters(
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                BranchId = graceIds.BranchIdString,
                                BranchName = graceIds.BranchName,
                                Sha256Hash = sha256Hash,
                                ReferenceId = referenceId,
                                Pattern = String.Empty,
                                ShowDirectories = true,
                                ShowFiles = true,
                                ForceRecompute = false,
                                CorrelationId = correlationId
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Branch.GetRecursiveSize(sdkParameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Branch.GetRecursiveSize(sdkParameters)

                        match result with
                        | Ok returnValue -> AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Total file size: {returnValue.ReturnValue:N0}[/]"
                        | Error error -> AnsiConsole.MarkupLine $"[{Colors.Error}]{error}[/]"

                        return result |> renderOutput parseResult
                    | Error error -> return GraceResult.Error error |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)

                    return renderOutput parseResult (GraceResult.Error graceError)
            }

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
            //if parseResult.CommandResult.Command.Options.Contains(Options.listFiles) then
            let sortedFiles = directoryVersion.Files.OrderBy(fun f -> f.RelativePath)

            for file in sortedFiles do
                AnsiConsole.MarkupLine(
                    $"[{Colors.Verbose}]{formatInstantAligned file.CreatedAt}   {getShortSha256Hash file.Sha256Hash}  {file.Size, 13:N0}  |- {file.RelativePath.Split('/').LastOrDefault()}[/]"
                ))

    type ListContents() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations
                    let correlationId = getCorrelationId parseResult

                    match validateIncomingParameters with
                    | Ok _ ->
                        let referenceId =
                            if isNull (parseResult.GetResult(Options.referenceId)) then
                                String.Empty
                            else
                                parseResult.GetValue(Options.referenceId).ToString()

                        let sha256Hash =
                            if isNull (parseResult.GetResult(Options.sha256Hash)) then
                                String.Empty
                            else
                                parseResult.GetValue(Options.sha256Hash)

                        let forceRecompute = parseResult.GetValue(Options.forceRecompute)

                        let sdkParameters =
                            ListContentsParameters(
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                BranchId = graceIds.BranchIdString,
                                BranchName = graceIds.BranchName,
                                Sha256Hash = sha256Hash,
                                ReferenceId = referenceId,
                                Pattern = String.Empty,
                                ShowDirectories = true,
                                ShowFiles = true,
                                ForceRecompute = forceRecompute,
                                CorrelationId = correlationId
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Branch.ListContents(sdkParameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Branch.ListContents(sdkParameters)

                        match result with
                        | Ok returnValue ->
                            let! _ = readGraceStatusFile ()

                            let directoryVersions =
                                returnValue.ReturnValue
                                    .Select(fun directoryVersionDto -> directoryVersionDto.DirectoryVersion)
                                    .OrderBy(fun dv -> dv.RelativePath)

                            let directoryCount = directoryVersions.Count()

                            let fileCount = directoryVersions.Sum(fun directoryVersion -> directoryVersion.Files.Count)

                            let totalFileSize = directoryVersions.Sum(fun directoryVersion -> directoryVersion.Files.Sum(fun f -> f.Size))

                            let rootDirectoryVersion = directoryVersions.First(fun d -> d.RelativePath = Constants.RootDirectoryPath)

                            AnsiConsole.MarkupLine($"[{Colors.Important}]All values taken from the selected version of this branch from the server.[/]")
                            AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of directories: {directoryCount}.[/]")
                            AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of files: {fileCount}; total file size: {totalFileSize:N0}.[/]")
                            AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Root SHA-256 hash: {rootDirectoryVersion.Sha256Hash.Substring(0, 8)}[/]")

                            printContents parseResult directoryVersions
                            return result |> renderOutput parseResult
                        | Error _ -> return result |> renderOutput parseResult
                    | Error error -> return GraceResult.Error error |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)

                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type SetName() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let validateIncomingParameters = parseResult |> CommonValidations
                    let correlationId = getCorrelationId parseResult
                    let newName = parseResult.GetValue(Options.newName)

                    match validateIncomingParameters with
                    | Ok _ ->
                        let parameters =
                            Parameters.Branch.SetBranchNameParameters(
                                BranchId = graceIds.BranchIdString,
                                BranchName = graceIds.BranchName,
                                NewName = newName,
                                CorrelationId = correlationId
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Branch.SetName(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Branch.SetName(parameters)

                        return result |> renderOutput parseResult
                    | Error error -> return GraceResult.Error error |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)

                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type Assign() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let correlationId = getCorrelationId parseResult
                    let validateIncomingParameters = parseResult |> CommonValidations

                    let requiredInputs =
                        oneOfTheseOptionsMustBeProvided
                            parseResult
                            [| Options.directoryVersionId; Options.sha256Hash |]
                            BranchError.EitherDirectoryVersionIdOrSha256HashRequired

                    match validateIncomingParameters, requiredInputs with
                    | Ok _, Ok _ ->
                        let directoryVersionId =
                            if isNull (parseResult.GetResult(Options.directoryVersionId)) then
                                Guid.Empty
                            else
                                parseResult.GetValue(Options.directoryVersionId)

                        let sha256Hash =
                            if isNull (parseResult.GetResult(Options.sha256Hash)) then
                                String.Empty
                            else
                                parseResult.GetValue(Options.sha256Hash)

                        let parameters =
                            Parameters.Branch.AssignParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                BranchId = graceIds.BranchIdString,
                                BranchName = graceIds.BranchName,
                                DirectoryVersionId = directoryVersionId,
                                Sha256Hash = sha256Hash,
                                CorrelationId = correlationId
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Branch.Assign(parameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Branch.Assign(parameters)

                        return result |> renderOutput parseResult
                    | Error error, _
                    | _, Error error -> return GraceResult.Error error |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)

                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    let private validateAndCleanMessage (message: string) (correlationId: CorrelationId) : Result<string, GraceError> =

        // Helpers local to this function to keep things simple.
        let fail msg = Error(GraceError.Create msg correlationId)

        // Regexes: created per-call for simplicity;
        // you can hoist them to module-level if you want them compiled once.
        let disallowedControlChars = Regex("[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]", RegexOptions.Compiled)
        let bidiControls = Regex("[\u202A-\u202E\u2066-\u2069]", RegexOptions.Compiled)
        let nonWhitespace = Regex(@"\S", RegexOptions.Compiled)

        // 1. Normalize newlines to '\n'
        let step1 = message.Trim().Replace("\r\n", "\n").Replace("\r", "\n")

        // 2. Trim trailing whitespace per line
        let step2 =
            let lines = step1.Split('\n')
            let trimmedLines = lines |> Array.map (fun line -> line.TrimEnd())
            String.concat "\n" trimmedLines

        // 3. Remove leading / trailing *blank* lines (whitespace-only)
        let step3 =
            let lines = step2.Split('\n')

            let mutable start = 0
            let mutable finish = lines.Length - 1

            while start <= finish && String.IsNullOrWhiteSpace lines[start] do
                start <- start + 1

            while finish >= start && String.IsNullOrWhiteSpace lines[finish] do
                finish <- finish - 1

            if start > finish then "" else String.concat "\n" lines[start..finish]

        // 4. Unicode normalization to NFC
        let cleaned = step3.Normalize(NormalizationForm.FormC)

        // 5. Actual validations

        // 5a. Must contain at least one non-whitespace character
        if String.IsNullOrEmpty cleaned || not (nonWhitespace.IsMatch cleaned) then
            fail "Message must contain at least one non-whitespace character."
        // 5b. Disallow unwanted control characters
        elif disallowedControlChars.IsMatch cleaned then
            fail "Message contains disallowed control characters."
        // 5c. Disallow bidi control characters
        elif bidiControls.IsMatch cleaned then
            fail "Message contains disallowed Unicode bidi control characters."
        else
            Ok cleaned

    type CreateReferenceCommand = CreateReferenceParameters -> Task<GraceResult<String>>

    let private createReferenceHandler (parseResult: ParseResult) (message: string) (command: CreateReferenceCommand) (commandType: string) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = parseResult |> CommonValidations
                let referenceMessage = validateAndCleanMessage message (getCorrelationId parseResult)

                match (validateIncomingParameters, referenceMessage) with
                | Ok _, Ok referenceMessage ->
                    let graceIds = parseResult |> getNormalizedIdsAndNames

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
                                                        OwnerId = graceIds.OwnerIdString,
                                                        OwnerName = graceIds.OwnerName,
                                                        OrganizationId = graceIds.OrganizationIdString,
                                                        OrganizationName = graceIds.OrganizationName,
                                                        RepositoryId = graceIds.RepositoryIdString,
                                                        RepositoryName = graceIds.RepositoryName,
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
                                                saveParameters.OwnerId <- graceIds.OwnerIdString
                                                saveParameters.OwnerName <- graceIds.OwnerName
                                                saveParameters.OrganizationId <- graceIds.OrganizationIdString
                                                saveParameters.OrganizationName <- graceIds.OrganizationName
                                                saveParameters.RepositoryId <- graceIds.RepositoryIdString
                                                saveParameters.RepositoryName <- graceIds.RepositoryName
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
                                                BranchId = graceIds.BranchIdString,
                                                BranchName = graceIds.BranchName,
                                                OwnerId = graceIds.OwnerIdString,
                                                OwnerName = graceIds.OwnerName,
                                                OrganizationId = graceIds.OrganizationIdString,
                                                OrganizationName = graceIds.OrganizationName,
                                                RepositoryId = graceIds.RepositoryIdString,
                                                RepositoryName = graceIds.RepositoryName,
                                                DirectoryVersionId = rootDirectoryId,
                                                Sha256Hash = rootDirectorySha256Hash,
                                                Message = referenceMessage,
                                                CorrelationId = graceIds.CorrelationId
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
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                CorrelationId = getCorrelationId parseResult,
                                FileVersions =
                                    (newFileVersions
                                     |> Seq.map (fun localFileVersion -> localFileVersion.ToFileVersion)
                                     |> Seq.toArray)
                            )

                        let! uploadResult = uploadFilesToObjectStorage getUploadMetadataForFilesParameters
                        let saveParameters = SaveDirectoryVersionsParameters()
                        saveParameters.OwnerId <- graceIds.OwnerIdString
                        saveParameters.OwnerName <- graceIds.OwnerName
                        saveParameters.OrganizationId <- graceIds.OrganizationIdString
                        saveParameters.OrganizationName <- graceIds.OrganizationName
                        saveParameters.RepositoryId <- graceIds.RepositoryIdString
                        saveParameters.RepositoryName <- graceIds.RepositoryName
                        saveParameters.CorrelationId <- getCorrelationId parseResult
                        saveParameters.DirectoryVersions <- newDirectoryVersions.Select(fun dv -> dv.ToDirectoryVersion).ToList()

                        let! uploadDirectoryVersions = DirectoryVersion.SaveDirectoryVersions saveParameters
                        let rootDirectoryVersion = getRootDirectoryVersion previousGraceStatus

                        let sdkParameters =
                            Parameters.Branch.CreateReferenceParameters(
                                BranchId = graceIds.BranchIdString,
                                BranchName = graceIds.BranchName,
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                DirectoryVersionId = rootDirectoryVersion.DirectoryVersionId,
                                Sha256Hash = rootDirectoryVersion.Sha256Hash,
                                Message = referenceMessage,
                                CorrelationId = graceIds.CorrelationId
                            )

                        let! result = command sdkParameters
                        return result
                | Error error, _ -> return Error error
                | _, Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    let private promotionHandler (parseResult: ParseResult) (message: string) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = parseResult |> CommonValidations
                let sanitizedMessage = message.Trim()

                match validateIncomingParameters with
                | Ok _ ->
                    let graceIds = parseResult |> getNormalizedIdsAndNames

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
                                                BranchId = graceIds.BranchIdString,
                                                BranchName = graceIds.BranchName,
                                                OwnerId = graceIds.OwnerIdString,
                                                OwnerName = graceIds.OwnerName,
                                                OrganizationId = graceIds.OrganizationIdString,
                                                OrganizationName = graceIds.OrganizationName,
                                                RepositoryId = graceIds.RepositoryIdString,
                                                RepositoryName = graceIds.RepositoryName,
                                                CorrelationId = graceIds.CorrelationId
                                            )

                                        //logToAnsiConsole
                                        //    Colors.Verbose
                                        //    $"In promotionHandler: branchGetParameters:{Environment.NewLine}{serialize branchGetParameters}"

                                        let! branchResult = Branch.Get(branchGetParameters)

                                        match branchResult with
                                        | Ok branchReturnValue ->
                                            // If we succeeded, get the parent branch Dto. That will have its latest promotion.
                                            let! parentBranchResult = Branch.GetParentBranch(branchGetParameters)

                                            match parentBranchResult with
                                            | Ok parentBranchReturnValue ->
                                                // Yay, we have both Dto's.
                                                let branchDto = branchReturnValue.ReturnValue

                                                //logToAnsiConsole Colors.Verbose $"In promotionHandler: branchDto:{Environment.NewLine}{serialize branchDto}"

                                                let parentBranchDto = parentBranchReturnValue.ReturnValue

                                                let referenceIds = List<ReferenceId>()

                                                if branchDto.LatestCommit <> ReferenceDto.Default then
                                                    referenceIds.Add(branchDto.LatestCommit.ReferenceId)

                                                if branchDto.LatestPromotion <> ReferenceDto.Default then
                                                    referenceIds.Add(branchDto.LatestPromotion.ReferenceId)

                                                if referenceIds.Count > 0 then
                                                    let getReferencesByReferenceIdParameters =
                                                        Parameters.Repository.GetReferencesByReferenceIdParameters(
                                                            OwnerId = graceIds.OwnerIdString,
                                                            OwnerName = graceIds.OwnerName,
                                                            OrganizationId = graceIds.OrganizationIdString,
                                                            OrganizationName = graceIds.OrganizationName,
                                                            RepositoryId = graceIds.RepositoryIdString,
                                                            RepositoryName = graceIds.RepositoryName,
                                                            ReferenceIds = referenceIds,
                                                            CorrelationId = graceIds.CorrelationId
                                                        )

                                                    //logToAnsiConsole
                                                    //    Colors.Verbose
                                                    //    $"In promotionHandler: getReferencesByReferenceIdParameters:{Environment.NewLine}{serialize getReferencesByReferenceIdParameters}"

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
                                                                    OwnerId = graceIds.OwnerIdString,
                                                                    OwnerName = graceIds.OwnerName,
                                                                    OrganizationId = graceIds.OrganizationIdString,
                                                                    OrganizationName = graceIds.OrganizationName,
                                                                    RepositoryId = graceIds.RepositoryIdString,
                                                                    RepositoryName = graceIds.RepositoryName,
                                                                    DirectoryVersionId = latestPromotableReference.DirectoryId,
                                                                    Sha256Hash = latestPromotableReference.Sha256Hash,
                                                                    Message = sanitizedMessage,
                                                                    CorrelationId = graceIds.CorrelationId
                                                                )

                                                            let! promotionResult = Branch.Promote(promotionParameters)

                                                            match promotionResult with
                                                            | Ok returnValue ->
                                                                //logToAnsiConsole Colors.Verbose $"Succeeded doing promotion."

                                                                //logToAnsiConsole
                                                                //    Colors.Verbose
                                                                //    $"{serialize (returnValue.Properties.OrderBy(fun kvp -> kvp.Key))}"

                                                                let promotionReferenceId = returnValue.Properties["ReferenceId"].ToString()
                                                                //let promotionReferenceId = returnValue.Properties.Item(nameof ReferenceId) :?> string

                                                                let rebaseParameters =
                                                                    Parameters.Branch.RebaseParameters(
                                                                        BranchId = $"{branchDto.BranchId}",
                                                                        RepositoryId = $"{branchDto.RepositoryId}",
                                                                        OwnerId = graceIds.OwnerIdString,
                                                                        OwnerName = graceIds.OwnerName,
                                                                        OrganizationId = graceIds.OrganizationIdString,
                                                                        OrganizationName = graceIds.OrganizationName,
                                                                        BasedOn = Guid.Parse(promotionReferenceId)
                                                                    )

                                                                let! rebaseResult = Branch.Rebase(rebaseParameters)
                                                                t2.Value <- 100.0

                                                                match rebaseResult with
                                                                | Ok returnValue ->
                                                                    //logToAnsiConsole Colors.Verbose $"Succeeded doing rebase."

                                                                    return promotionResult
                                                                | Error error -> return Error error
                                                            | Error error ->
                                                                t2.Value <- 100.0
                                                                return Error error

                                                        else
                                                            return
                                                                Error(
                                                                    GraceError.Create
                                                                        (getErrorMessage BranchError.BranchIsNotBasedOnLatestPromotion)
                                                                        (parseResult |> getCorrelationId)
                                                                )
                                                    | Error error ->
                                                        t2.Value <- 100.0
                                                        return Error error
                                                else
                                                    return
                                                        Error(
                                                            GraceError.Create
                                                                (getErrorMessage BranchError.PromotionNotAvailableBecauseThereAreNoPromotableReferences)
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

    type Promote() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let message = parseResult.GetValue(Options.message) |> valueOrEmpty

                    let! result = promotionHandler parseResult message
                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type Commit() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let message = parseResult.GetValue(Options.messageRequired) |> valueOrEmpty
                    let command (parameters: CreateReferenceParameters) = task { return! Branch.Commit(parameters) }

                    let! result = createReferenceHandler parseResult message command (nameof(Commit).ToLowerInvariant())

                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type Checkpoint() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let message = parseResult.GetValue(Options.message) |> valueOrEmpty
                    let command (parameters: CreateReferenceParameters) = task { return! Branch.Checkpoint(parameters) }

                    let! result = createReferenceHandler parseResult message command (nameof(Checkpoint).ToLowerInvariant())

                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type Save() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let message = parseResult.GetValue(Options.message) |> valueOrEmpty
                    let command (parameters: CreateReferenceParameters) = task { return! Branch.Save(parameters) }

                    let! result = createReferenceHandler parseResult message command (nameof(Save).ToLowerInvariant())

                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type Tag() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let message = parseResult.GetValue(Options.messageRequired) |> valueOrEmpty
                    let command (parameters: CreateReferenceParameters) = task { return! Branch.Tag(parameters) }

                    let! result = createReferenceHandler parseResult message command (nameof(Tag).ToLowerInvariant())

                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type CreateExternal() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let message = parseResult.GetValue(Options.messageRequired) |> valueOrEmpty
                    let command (parameters: CreateReferenceParameters) = task { return! Branch.CreateExternal(parameters) }

                    let! result = createReferenceHandler parseResult message command ("External".ToLowerInvariant())

                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type EnableFeatureCommand = EnableFeatureParameters -> Task<GraceResult<string>>

    let private enableFeatureHandler (parseResult: ParseResult) (enabled: bool) (command: EnableFeatureCommand) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = parseResult |> CommonValidations

                match validateIncomingParameters with
                | Ok _ ->
                    let graceIds = parseResult |> getNormalizedIdsAndNames

                    let parameters =
                        Parameters.Branch.EnableFeatureParameters(
                            BranchId = graceIds.BranchIdString,
                            BranchName = graceIds.BranchName,
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            Enabled = enabled,
                            CorrelationId = graceIds.CorrelationId
                        )

                    let! result = command parameters

                    match result with
                    | Ok returnValue -> return Ok(GraceReturnValue.Create (returnValue.ReturnValue) graceIds.CorrelationId)
                    | Error error -> return Error error
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type EnableAssign() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let enabled = parseResult.GetValue(Options.enabled)
                    let command (parameters: EnableFeatureParameters) = task { return! Branch.EnableAssign(parameters) }

                    let! result = enableFeatureHandler parseResult enabled command
                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type EnablePromotion() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let enabled = parseResult.GetValue(Options.enabled)
                    let command (parameters: EnableFeatureParameters) = task { return! Branch.EnablePromotion(parameters) }

                    let! result = enableFeatureHandler parseResult enabled command
                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type EnableCommit() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let enabled = parseResult.GetValue(Options.enabled)
                    let command (parameters: EnableFeatureParameters) = task { return! Branch.EnableCommit(parameters) }

                    let! result = enableFeatureHandler parseResult enabled command
                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type EnableCheckpoint() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let enabled = parseResult.GetValue(Options.enabled)
                    let command (parameters: EnableFeatureParameters) = task { return! Branch.EnableCheckpoint(parameters) }

                    let! result = enableFeatureHandler parseResult enabled command
                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type EnableSave() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let enabled = parseResult.GetValue(Options.enabled)
                    let command (parameters: EnableFeatureParameters) = task { return! Branch.EnableSave(parameters) }

                    let! result = enableFeatureHandler parseResult enabled command
                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type EnableTag() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let enabled = parseResult.GetValue(Options.enabled)
                    let command (parameters: EnableFeatureParameters) = task { return! Branch.EnableTag(parameters) }

                    let! result = enableFeatureHandler parseResult enabled command
                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type EnableExternal() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let enabled = parseResult.GetValue(Options.enabled)
                    let command (parameters: EnableFeatureParameters) = task { return! Branch.EnableExternal(parameters) }

                    let! result = enableFeatureHandler parseResult enabled command
                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type EnableAutoRebase() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let enabled = parseResult.GetValue(Options.enabled)
                    let command (parameters: EnableFeatureParameters) = task { return! Branch.EnableAutoRebase(parameters) }

                    let! result = enableFeatureHandler parseResult enabled command
                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    // Get subcommand
    type Get() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    if parseResult |> verbose then printParseResult parseResult

                    let includeDeleted = parseResult.GetValue(Options.includeDeleted)
                    let showEvents = parseResult.GetValue(Options.showEvents)
                    let validateIncomingParameters = parseResult |> CommonValidations

                    match validateIncomingParameters with
                    | Ok _ ->
                        let graceIds = parseResult |> getNormalizedIdsAndNames

                        let branchParameters =
                            GetBranchParameters(
                                OwnerId = graceIds.OwnerIdString,
                                OwnerName = graceIds.OwnerName,
                                OrganizationId = graceIds.OrganizationIdString,
                                OrganizationName = graceIds.OrganizationName,
                                RepositoryId = graceIds.RepositoryIdString,
                                RepositoryName = graceIds.RepositoryName,
                                BranchId = graceIds.BranchIdString,
                                BranchName = graceIds.BranchName,
                                IncludeDeleted = includeDeleted,
                                CorrelationId = graceIds.CorrelationId
                            )

                        let! result =
                            if parseResult |> hasOutput then
                                progress
                                    .Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                        task {
                                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                            let! response = Branch.Get(branchParameters)
                                            t0.Increment(100.0)
                                            return response
                                        })
                            else
                                Branch.Get(branchParameters)

                        match result with
                        | Ok graceReturnValue ->
                            let jsonText = JsonText(serialize graceReturnValue.ReturnValue)
                            AnsiConsole.Write(jsonText)
                            AnsiConsole.WriteLine()

                            if showEvents then
                                let eventsParameters =
                                    GetBranchVersionParameters(
                                        OwnerId = graceIds.OwnerIdString,
                                        OwnerName = graceIds.OwnerName,
                                        OrganizationId = graceIds.OrganizationIdString,
                                        OrganizationName = graceIds.OrganizationName,
                                        RepositoryId = graceIds.RepositoryIdString,
                                        RepositoryName = graceIds.RepositoryName,
                                        BranchId = graceIds.BranchIdString,
                                        BranchName = graceIds.BranchName,
                                        IncludeDeleted = includeDeleted,
                                        CorrelationId = graceIds.CorrelationId
                                    )

                                let! eventsResult =
                                    if parseResult |> hasOutput then
                                        progress
                                            .Columns(progressColumns)
                                            .StartAsync(fun progressContext ->
                                                task {
                                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                                    let! response = Branch.GetEvents(eventsParameters)
                                                    t0.Increment(100.0)
                                                    return response
                                                })
                                    else
                                        Branch.GetEvents(eventsParameters)

                                match eventsResult with
                                | Ok eventsValue ->
                                    let sb = StringBuilder()

                                    for line in eventsValue.ReturnValue do
                                        sb.AppendLine($"{Markup.Escape(line)},") |> ignore
                                        AnsiConsole.MarkupLine $"[{Colors.Verbose}]{Markup.Escape(line)}[/]"

                                    if sb.Length > 0 then sb.Remove(sb.Length - 1, 1) |> ignore

                                    AnsiConsole.WriteLine()
                                    return 0
                                | Error graceError -> return renderOutput parseResult (GraceResult.Error graceError)
                            else
                                return 0
                        | Error graceError -> return renderOutput parseResult (GraceResult.Error graceError)
                    | Error graceError -> return renderOutput parseResult (GraceResult.Error graceError)
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)

                    return renderOutput parseResult (GraceResult.Error graceError)
            }


    type GetReferenceQuery = GetReferencesParameters -> Task<GraceResult<ReferenceDto array>>

    let getReferenceHandler (parseResult: ParseResult) (maxCount: int) (query: GetReferenceQuery) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult

                let validateIncomingParameters = parseResult |> CommonValidations
                let graceIds = parseResult |> getNormalizedIdsAndNames

                match validateIncomingParameters with
                | Ok _ ->
                    let getBranchParameters =
                        GetBranchParameters(
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            BranchId = graceIds.BranchIdString,
                            BranchName = graceIds.BranchName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    let getReferencesParameters =
                        GetReferencesParameters(
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            BranchId = graceIds.BranchIdString,
                            BranchName = graceIds.BranchName,
                            MaxCount = maxCount,
                            CorrelationId = graceIds.CorrelationId
                        )

                    let fetchReferences () =
                        task {
                            let! branchResult = Branch.Get(getBranchParameters)
                            let! referencesResult = query getReferencesParameters

                            match (branchResult, referencesResult) with
                            | (Ok branchValue, Ok referencesValue) ->
                                let graceReturnValue = GraceReturnValue.Create (branchValue.ReturnValue, referencesValue.ReturnValue) graceIds.CorrelationId

                                referencesValue.Properties
                                |> Seq.iter (fun kvp -> graceReturnValue.Properties.Add(kvp.Key, kvp.Value))

                                return Ok graceReturnValue
                            | (_, Error error)
                            | (Error error, _) -> return Error error
                        }

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! response = fetchReferences ()
                                        t0.Increment(100.0)
                                        return response
                                    })
                    else
                        return! fetchReferences ()
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
            table
                .AddColumns([| TableColumn($"[{Colors.Deemphasized}]ReferenceId[/]") |])
                .AddColumns([| TableColumn($"[{Colors.Deemphasized}]Root DirectoryVersionId[/]") |])
            |> ignore

        for row in sortedResults do
            //logToAnsiConsole Colors.Verbose $"{serialize row}"
            let sha256Hash =
                if parseResult.GetValue(Options.fullSha) then
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
                       $"[{Colors.Deemphasized}]{row.DirectoryId}[/]" |]
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

    let private renderReferencesOutput (parseResult: ParseResult) (label: string) (result: GraceResult<BranchDto * ReferenceDto array>) =
        match result with
        | Ok graceReturnValue ->
            let (branchDto, references) = graceReturnValue.ReturnValue
            let rendered = result |> renderOutput parseResult

            if parseResult |> hasOutput then
                let referenceTable = createReferenceTable parseResult references
                printReferenceTable referenceTable references branchDto.BranchName label

            rendered
        | Error _ -> result |> renderOutput parseResult

    type GetReferences() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let maxCount = parseResult.GetValue(Options.maxCount)
                    let query (parameters: GetReferencesParameters) = Branch.GetReferences parameters

                    let! result = getReferenceHandler parseResult maxCount query
                    return renderReferencesOutput parseResult "References" result
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type GetPromotions() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let maxCount = parseResult.GetValue(Options.maxCount)
                    let query (parameters: GetReferencesParameters) = task { return! Branch.GetPromotions(parameters) }

                    let! result = getReferenceHandler parseResult maxCount query
                    return renderReferencesOutput parseResult "Promotions" result
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type GetCommits() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let maxCount = parseResult.GetValue(Options.maxCount)
                    let query (parameters: GetReferencesParameters) = task { return! Branch.GetCommits(parameters) }

                    let! result = getReferenceHandler parseResult maxCount query
                    return renderReferencesOutput parseResult "Commits" result
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type GetCheckpoints() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let maxCount = parseResult.GetValue(Options.maxCount)

                    let query (parameters: GetReferencesParameters) =
                        task {
                            let! checkpointsResult = Branch.GetCheckpoints(parameters)

                            match checkpointsResult with
                            | Ok checkpointsValue ->
                                let! commitsResult = Branch.GetCommits(parameters)

                                match commitsResult with
                                | Ok commitsValue ->
                                    let combined =
                                        Seq.append checkpointsValue.ReturnValue commitsValue.ReturnValue
                                        |> Seq.sortByDescending (fun reference -> reference.CreatedAt)
                                        |> Seq.take maxCount
                                        |> Seq.toArray

                                    return Ok(GraceReturnValue.Create combined (getCorrelationId parseResult))
                                | Error error -> return Error error
                            | Error error -> return Error error
                        }

                    let! result = getReferenceHandler parseResult maxCount query
                    return renderReferencesOutput parseResult "Checkpoints" result
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type GetSaves() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let maxCount = parseResult.GetValue(Options.maxCount)
                    let query (parameters: GetReferencesParameters) = task { return! Branch.GetSaves(parameters) }

                    let! result = getReferenceHandler parseResult maxCount query
                    return renderReferencesOutput parseResult "Saves" result
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type GetTags() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let maxCount = parseResult.GetValue(Options.maxCount)
                    let query (parameters: GetReferencesParameters) = task { return! Branch.GetTags(parameters) }

                    let! result = getReferenceHandler parseResult maxCount query
                    return renderReferencesOutput parseResult "Tags" result
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type GetExternals() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let maxCount = parseResult.GetValue(Options.maxCount)
                    let query (parameters: GetReferencesParameters) = task { return! Branch.GetExternals(parameters) }

                    let! result = getReferenceHandler parseResult maxCount query
                    return renderReferencesOutput parseResult "Externals" result
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)
                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    type SwitchParameters() =
        member val ToBranchId: string = String.Empty with get, set
        member val ToBranchName: string = String.Empty with get, set
        member val Sha256Hash: string = String.Empty with get, set
        member val ReferenceId: string = String.Empty with get, set

    type Switch() =
        inherit AsynchronousCommandLineAction()

        let switchHandler (parseResult: ParseResult) (switchParameters: SwitchParameters) =
            task {
                try
                    let graceIds = getNormalizedIdsAndNames parseResult

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
                    let validateIncomingParameters (showOutput, parseResult: ParseResult, parameters: SwitchParameters) =
                        let ``Either ToBranchId or ToBranchName must be provided if no Sha256Hash or ReferenceId`` (parseResult: ParseResult) =
                            oneOfTheseOptionsMustBeProvided
                                parseResult
                                [| Options.toBranchId
                                   Options.toBranchName
                                   Options.sha256Hash
                                   Options.referenceId |]
                                BranchError.EitherToBranchIdOrToBranchNameIsRequired

                        match
                            parseResult
                            |> CommonValidations
                            >>= ``Either ToBranchId or ToBranchName must be provided if no Sha256Hash or ReferenceId``
                        with
                        | Ok result -> Ok(showOutput, parseResult, parameters) |> returnTask
                        | Error error -> Error error |> returnTask

                    // 0. Get the branchDto for the current branch.
                    let getCurrentBranch (t: ProgressTask) (showOutput, parseResult: ParseResult, parameters: SwitchParameters) =
                        task {
                            t |> startProgressTask showOutput

                            let getParameters =
                                GetBranchParameters(
                                    OwnerId = $"{Current().OwnerId}",
                                    OrganizationId = $"{Current().OrganizationId}",
                                    RepositoryId = $"{Current().RepositoryId}",
                                    BranchId = $"{Current().BranchId}",
                                    CorrelationId = getCorrelationId parseResult
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
                    let readGraceStatusFile (t: ProgressTask) (showOutput, parseResult: ParseResult, parameters: SwitchParameters, currentBranch: BranchDto) =
                        task {
                            t |> startProgressTask showOutput
                            let! existingGraceStaus = readGraceStatusFile ()
                            previousGraceStatus <- existingGraceStaus
                            newGraceStatus <- existingGraceStaus
                            t |> setProgressTaskValue showOutput 100.0
                            return Ok(showOutput, parseResult, parameters, currentBranch)
                        }

                    // 2. Scan the working directory for differences.
                    let scanForDifferences (t: ProgressTask) (showOutput, parseResult: ParseResult, parameters: SwitchParameters, currentBranch: BranchDto) =
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
                        (showOutput, parseResult: ParseResult, parameters: SwitchParameters, currentBranch: BranchDto, differences: List<FileSystemDifference>)
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
                            parameters: SwitchParameters,
                            currentBranch: BranchDto,
                            differences: List<FileSystemDifference>,
                            newDirectoryVersions: List<LocalDirectoryVersion>
                        ) =
                        task {
                            t |> startProgressTask showOutput

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
                                        OwnerId = graceIds.OwnerIdString,
                                        OwnerName = graceIds.OwnerName,
                                        OrganizationId = graceIds.OrganizationIdString,
                                        OrganizationName = graceIds.OrganizationName,
                                        RepositoryId = graceIds.RepositoryIdString,
                                        RepositoryName = graceIds.RepositoryName,
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
                            parameters: SwitchParameters,
                            currentBranch: BranchDto,
                            newDirectoryVersions: List<LocalDirectoryVersion>
                        ) =
                        task {
                            t |> startProgressTask showOutput

                            if currentBranch.SaveEnabled && newDirectoryVersions.Any() then
                                let saveParameters = SaveDirectoryVersionsParameters()
                                saveParameters.OwnerId <- graceIds.OwnerIdString
                                saveParameters.OwnerName <- graceIds.OwnerName
                                saveParameters.OrganizationId <- graceIds.OrganizationIdString
                                saveParameters.OrganizationName <- graceIds.OrganizationName
                                saveParameters.RepositoryId <- graceIds.RepositoryIdString
                                saveParameters.RepositoryName <- graceIds.RepositoryName
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
                        (showOutput, parseResult: ParseResult, parameters: SwitchParameters, branchDto: BranchDto, message: string)
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
                                logToAnsiConsole
                                    Colors.Verbose
                                    $"In createSaveReference: BranchName: {branchDto.BranchName}; Save not enabled; message: {message}"

                                t |> setProgressTaskValue showOutput 100.0
                                return Ok(showOutput, parseResult, parameters, branchDto)
                        }

                    /// 7. Get the branch and directory versions for the requested version we're switching to from the server.
                    let getVersionToSwitchTo (t: ProgressTask) (showOutput, parseResult: ParseResult, parameters: SwitchParameters, currentBranch: BranchDto) =
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
                                        OwnerId = graceIds.OwnerIdString,
                                        OwnerName = graceIds.OwnerName,
                                        OrganizationId = graceIds.OrganizationIdString,
                                        OrganizationName = graceIds.OrganizationName,
                                        RepositoryId = graceIds.RepositoryIdString,
                                        RepositoryName = graceIds.RepositoryName,
                                        BranchId = switchParameters.ToBranchId,
                                        BranchName = switchParameters.ToBranchName,
                                        Sha256Hash = switchParameters.Sha256Hash,
                                        ReferenceId = switchParameters.ReferenceId,
                                        CorrelationId = graceIds.CorrelationId
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
                                            OwnerId = graceIds.OwnerIdString,
                                            OwnerName = graceIds.OwnerName,
                                            OrganizationId = graceIds.OrganizationIdString,
                                            OrganizationName = graceIds.OrganizationName,
                                            RepositoryId = $"{newBranch.RepositoryId}",
                                            BranchId = $"{newBranch.BranchId}",
                                            ReferenceId = switchParameters.ReferenceId,
                                            Sha256Hash = switchParameters.Sha256Hash,
                                            CorrelationId = graceIds.CorrelationId
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
                                        OwnerId = graceIds.OwnerIdString,
                                        OwnerName = graceIds.OwnerName,
                                        OrganizationId = graceIds.OrganizationIdString,
                                        OrganizationName = graceIds.OrganizationName,
                                        RepositoryId = graceIds.RepositoryIdString,
                                        RepositoryName = graceIds.RepositoryName,
                                        BranchId = graceIds.BranchIdString,
                                        BranchName = graceIds.BranchName,
                                        ReferenceId = switchParameters.ReferenceId,
                                        CorrelationId = graceIds.CorrelationId
                                    )

                                match! Branch.GetReference(getReferenceParameters) with
                                | Ok returnValue ->
                                    // We have the reference, let's get the new branch and the DirectoryVersion from the reference.
                                    let reference = returnValue.ReturnValue

                                    let getNewBranchParameters =
                                        GetBranchParameters(
                                            OwnerId = graceIds.OwnerIdString,
                                            OwnerName = graceIds.OwnerName,
                                            OrganizationId = graceIds.OrganizationIdString,
                                            OrganizationName = graceIds.OrganizationName,
                                            RepositoryId = graceIds.RepositoryIdString,
                                            RepositoryName = graceIds.RepositoryName,
                                            BranchId = $"{reference.BranchId}",
                                            CorrelationId = graceIds.CorrelationId
                                        )

                                    let! getNewBranchResult = Branch.Get(getNewBranchParameters)

                                    let getVersionParameters =
                                        GetBranchVersionParameters(
                                            OwnerId = graceIds.OwnerIdString,
                                            OwnerName = graceIds.OwnerName,
                                            OrganizationId = graceIds.OrganizationIdString,
                                            OrganizationName = graceIds.OrganizationName,
                                            RepositoryId = graceIds.RepositoryIdString,
                                            RepositoryName = graceIds.RepositoryName,
                                            BranchId = $"{reference.BranchId}",
                                            ReferenceId = switchParameters.ReferenceId,
                                            CorrelationId = graceIds.CorrelationId
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
                                        OwnerId = graceIds.OwnerIdString,
                                        OwnerName = graceIds.OwnerName,
                                        OrganizationId = graceIds.OrganizationIdString,
                                        OrganizationName = graceIds.OrganizationName,
                                        RepositoryId = graceIds.RepositoryIdString,
                                        RepositoryName = graceIds.RepositoryName,
                                        BranchId = $"{currentBranch.BranchId}",
                                        Sha256Hash = switchParameters.Sha256Hash,
                                        CorrelationId = graceIds.CorrelationId
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
                                            (getErrorMessage BranchError.EitherToBranchIdOrToBranchNameIsRequired)
                                            (parseResult |> getCorrelationId)
                                    )
                        }

                    /// 8. Update object cache and working directory.
                    let updateWorkingDirectory
                        (t: ProgressTask)
                        (
                            showOutput,
                            parseResult: ParseResult,
                            parameters: SwitchParameters,
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
                                    OwnerId = graceIds.OwnerIdString,
                                    OwnerName = graceIds.OwnerName,
                                    OrganizationId = graceIds.OrganizationIdString,
                                    OrganizationName = graceIds.OrganizationName,
                                    RepositoryId = graceIds.RepositoryIdString,
                                    RepositoryName = graceIds.RepositoryName,
                                    DirectoryVersionId = $"{rootDirectoryId}",
                                    DirectoryIds = missingDirectoryIds,
                                    CorrelationId = graceIds.CorrelationId
                                )

                            match! DirectoryVersion.GetByDirectoryIds getByDirectoryIdParameters with
                            | Ok returnValue ->
                                // Create a new version of GraceStatus that includes the new DirectoryVersions.
                                let newDirectoryVersionDtos = returnValue.ReturnValue

                                if parseResult |> verbose then
                                    logToAnsiConsole Colors.Verbose $"In updateWorkingDirectory: newDirectoryVersions.Count: {newDirectoryVersionDtos.Count()}."

                                let graceStatusWithNewDirectoryVersionsFromServer =
                                    updateGraceStatusWithNewDirectoryVersionsFromServer newGraceStatus newDirectoryVersionDtos

                                let mutable isError = false

                                // Identify files that we don't already have in object cache and download them.
                                let getDownloadUriParameters =
                                    Storage.GetDownloadUriParameters(
                                        OwnerId = graceIds.OwnerIdString,
                                        OwnerName = graceIds.OwnerName,
                                        OrganizationId = graceIds.OrganizationIdString,
                                        OrganizationName = graceIds.OrganizationName,
                                        RepositoryId = graceIds.RepositoryIdString,
                                        RepositoryName = graceIds.RepositoryName,
                                        CorrelationId = graceIds.CorrelationId
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
                                                    newDirectoryVersionDtos
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

                    let writeNewGraceStatus (t: ProgressTask) (showOutput, parseResult: ParseResult, parameters: SwitchParameters, currentBranch: BranchDto) =
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
                                        let t0 =
                                            progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString GettingCurrentBranch}[/]", autoStart = false)

                                        let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString ReadingGraceStatus}[/]", autoStart = false)

                                        let t2 =
                                            progressContext.AddTask(
                                                $"[{Color.DodgerBlue1}]{UIString.getString ScanningWorkingDirectory}[/]",
                                                autoStart = false
                                            )

                                        let t3 =
                                            progressContext.AddTask(
                                                $"[{Color.DodgerBlue1}]{UIString.getString CreatingNewDirectoryVersions}[/]",
                                                autoStart = false
                                            )

                                        let t4 = progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString UploadingFiles}[/]", autoStart = false)

                                        let t5 =
                                            progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString SavingDirectoryVersions}[/]", autoStart = false)

                                        let t6 =
                                            progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString CreatingSaveReference}[/]", autoStart = false)

                                        let t7 =
                                            progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString GettingLatestVersion}[/]", autoStart = false)

                                        let t8 =
                                            progressContext.AddTask(
                                                $"[{Color.DodgerBlue1}]{UIString.getString UpdatingWorkingDirectory}[/]",
                                                autoStart = false
                                            )

                                        let t9 =
                                            progressContext.AddTask($"[{Color.DodgerBlue1}]{UIString.getString CreatingSaveReference}[/]", autoStart = false)

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

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                Directory.CreateDirectory(Path.GetDirectoryName(updateInProgressFileName))
                |> ignore

                try
                    if parseResult |> verbose then printParseResult parseResult

                    do! File.WriteAllTextAsync(updateInProgressFileName, "`grace switch` is in progress.")

                    let graceIds = parseResult |> getNormalizedIdsAndNames

                    let switchParameters = SwitchParameters()

                    let toBranchId = parseResult.GetValue(Options.toBranchId)
                    if toBranchId <> Guid.Empty then switchParameters.ToBranchId <- $"{toBranchId}"

                    let toBranchName = parseResult.GetValue(Options.toBranchName)
                    switchParameters.ToBranchName <- toBranchName

                    let referenceId = parseResult.GetValue(Options.referenceId)

                    if referenceId <> Guid.Empty then
                        switchParameters.ReferenceId <- $"{referenceId}"

                    let sha256Hash = parseResult.GetValue(Options.sha256Hash)
                    switchParameters.Sha256Hash <- sha256Hash

                    let! result = switchHandler parseResult switchParameters
                    return result
                finally
                    if File.Exists(updateInProgressFileName) then
                        File.Delete(updateInProgressFileName)
            }

    let rebaseHandler (parseResult: ParseResult) (graceStatus: GraceStatus) =
        task {
            // --------------------------------------------------------------------------------------------------------------------------------------
            // Algorithm:
            //
            // Get a diff between the promotion from the parent branch that the current branch is based on, and the latest promotion from the parent branch.
            //   These are the changes that we expect to apply to the current branch.
            //
            // Get a diff between the latest reference on this branch and the promotion that it's based on from the parent branch.
            //   This will be what's changed in the current branch since it was last rebased.
            //
            // If a file has changed in the first diff, but not in the second diff, cool, we can automatically copy them.
            // If a file has changed in the second diff, but not in the first diff, cool, we can keep those changes.
            // If a file has changed in both, we have a promotion conflict, so we'll call an LLM to suggest a resolution.
            //
            // Then we call Branch.Rebase() to actually record the update.
            // --------------------------------------------------------------------------------------------------------------------------------------

            let graceIds = parseResult |> getNormalizedIdsAndNames

            // First, get the current branchDto so we have the latest promotion that it's based on.
            let branchGetParameters =
                GetBranchParameters(
                    OwnerId = graceIds.OwnerIdString,
                    OwnerName = graceIds.OwnerName,
                    OrganizationId = graceIds.OrganizationIdString,
                    OrganizationName = graceIds.OrganizationName,
                    RepositoryId = graceIds.RepositoryIdString,
                    RepositoryName = graceIds.RepositoryName,
                    BranchId = graceIds.BranchIdString,
                    BranchName = graceIds.BranchName,
                    CorrelationId = graceIds.CorrelationId
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
                                OwnerId = $"{branchDto.OwnerId}",
                                OrganizationId = $"{branchDto.OrganizationId}",
                                RepositoryId = $"{branchDto.RepositoryId}",
                                BranchId = $"{branchDto.BranchId}",
                                MaxCount = 1,
                                CorrelationId = graceIds.CorrelationId
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
                                                OwnerId = graceIds.OwnerIdString,
                                                OwnerName = graceIds.OwnerName,
                                                OrganizationId = graceIds.OrganizationIdString,
                                                OrganizationName = graceIds.OrganizationName,
                                                RepositoryId = $"{branchDto.RepositoryId}",
                                                DirectoryVersionId1 = basedOn.DirectoryId,
                                                DirectoryVersionId2 = parentLatestPromotion.DirectoryId,
                                                CorrelationId = graceIds.CorrelationId
                                            )
                                        //logToAnsiConsole Colors.Verbose $"First diff: {Markup.Escape(serialize diffParameters)}"
                                        let! firstDiff = Diff.GetDiff(diffParameters)

                                        // Second diff: latest reference on current branch vs. parent promotion that current branch is based on.
                                        let diffParameters =
                                            Parameters.Diff.GetDiffParameters(
                                                OwnerId = graceIds.OwnerIdString,
                                                OwnerName = graceIds.OwnerName,
                                                OrganizationId = graceIds.OrganizationIdString,
                                                OrganizationName = graceIds.OrganizationName,
                                                RepositoryId = $"{branchDto.RepositoryId}",
                                                DirectoryVersionId1 = latestReference.DirectoryId,
                                                DirectoryVersionId2 = basedOn.DirectoryId,
                                                CorrelationId = graceIds.CorrelationId
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
                                                OwnerId = graceIds.OwnerIdString,
                                                OwnerName = graceIds.OwnerName,
                                                OrganizationId = graceIds.OrganizationIdString,
                                                OrganizationName = graceIds.OrganizationName,
                                                RepositoryId = $"{branchDto.RepositoryId}",
                                                DirectoryVersionId1 = latestReference.DirectoryId,
                                                DirectoryVersionId2 = parentLatestPromotion.DirectoryId,
                                                CorrelationId = graceIds.CorrelationId
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
                                        OwnerId = $"{branchDto.OwnerId}",
                                        OrganizationId = $"{branchDto.OrganizationId}",
                                        RepositoryId = $"{branchDto.RepositoryId}",
                                        DirectoryVersionId = $"{parentLatestPromotion.DirectoryId}",
                                        CorrelationId = graceIds.CorrelationId
                                    )

                                let getLatestReferenceDirectoryParameters =
                                    Parameters.DirectoryVersion.GetParameters(
                                        OwnerId = $"{branchDto.OwnerId}",
                                        OrganizationId = $"{branchDto.OrganizationId}",
                                        RepositoryId = $"{branchDto.RepositoryId}",
                                        DirectoryVersionId = $"{latestReference.DirectoryId}",
                                        CorrelationId = graceIds.CorrelationId
                                    )

                                // Get the directory versions for the parent promotion that we're rebasing on, and the latest reference.
                                let! d1 = DirectoryVersion.GetDirectoryVersionsRecursive(getParentLatestPromotionDirectoryParameters)

                                let! d2 = DirectoryVersion.GetDirectoryVersionsRecursive(getLatestReferenceDirectoryParameters)

                                let createFileVersionLookupDictionary (directoryVersionDtos: IEnumerable<DirectoryVersionDto>) =
                                    let lookup = Dictionary<RelativePath, LocalFileVersion>(StringComparer.OrdinalIgnoreCase)

                                    directoryVersionDtos
                                    |> Seq.map (fun dv -> dv.DirectoryVersion)
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
                                            OwnerId = graceIds.OwnerIdString,
                                            OwnerName = graceIds.OwnerName,
                                            OrganizationId = graceIds.OrganizationIdString,
                                            OrganizationName = graceIds.OrganizationName,
                                            RepositoryId = graceIds.RepositoryIdString,
                                            RepositoryName = graceIds.RepositoryName,
                                            CorrelationId = graceIds.CorrelationId
                                        )

                                    match! downloadFilesFromObjectStorage getDownloadUriParameters fileVersionsToDownload graceIds.CorrelationId with
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
                                                saveParameters.OwnerId <- graceIds.OwnerIdString
                                                saveParameters.OwnerName <- graceIds.OwnerName
                                                saveParameters.OrganizationId <- graceIds.OrganizationIdString
                                                saveParameters.OrganizationName <- graceIds.OrganizationName
                                                saveParameters.RepositoryId <- graceIds.RepositoryIdString
                                                saveParameters.RepositoryName <- graceIds.RepositoryName
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
                                                OwnerId = $"{branchDto.OwnerId}",
                                                OrganizationId = $"{branchDto.OrganizationId}",
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
                                                    OwnerId = $"{branchDto.OwnerId}",
                                                    OrganizationId = $"{branchDto.OrganizationId}",
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

    type Rebase() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                Directory.CreateDirectory(Path.GetDirectoryName(updateInProgressFileName))
                |> ignore

                try
                    if parseResult |> verbose then printParseResult parseResult

                    do! File.WriteAllTextAsync(updateInProgressFileName, "`grace rebase` is in progress.")

                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let! graceStatus = readGraceStatusFile ()

                    let! result = rebaseHandler parseResult graceStatus
                    return result
                finally
                    if File.Exists(updateInProgressFileName) then
                        File.Delete(updateInProgressFileName)
            }

    let private statusHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames
                let horizontalLineChar = "─"

                // Show repo and branch names.
                let getParameters =
                    GetBranchParameters(
                        OwnerId = graceIds.OwnerIdString,
                        OwnerName = graceIds.OwnerName,
                        OrganizationId = graceIds.OrganizationIdString,
                        OrganizationName = graceIds.OrganizationName,
                        RepositoryId = graceIds.RepositoryIdString,
                        RepositoryName = graceIds.RepositoryName,
                        BranchId = graceIds.BranchIdString,
                        BranchName = graceIds.BranchName,
                        CorrelationId = graceIds.CorrelationId
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

                    let permissions (branchDto: BranchDto) =
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

                    let ownerLabel = Utilities.getLocalizedString Text.StringResourceName.Owner
                    let organizationLabel = Utilities.getLocalizedString Text.StringResourceName.Organization
                    let repositoryLabel = Utilities.getLocalizedString Text.StringResourceName.Repository
                    let branchLabel = Utilities.getLocalizedString Text.StringResourceName.Branch

                    let headerLength = ownerLabel.Length + organizationLabel.Length + repositoryLabel.Length + 6

                    let column1 = TableColumn(String.replicate headerLength horizontalLineChar)

                    let getReferencesParameters =
                        Parameters.Branch.GetReferencesParameters(
                            BranchId = graceIds.BranchIdString,
                            BranchName = graceIds.BranchName,
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            MaxCount = 5,
                            CorrelationId = graceIds.CorrelationId
                        )

                    let! lastFiveReferences =
                        task {
                            match! Branch.GetReferences(getReferencesParameters) with
                            | Ok returnValue -> return returnValue.ReturnValue
                            | Error error -> return Array.Empty<ReferenceDto>()
                        }

                    let basedOnMessage =
                        if
                            branchDto.BasedOn.ReferenceId = parentBranchDto.LatestPromotion.ReferenceId
                            || branchDto.ParentBranchId = Constants.DefaultParentBranchId
                        then
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
                        let sortedReferences =
                            [| latestCommit; latestCheckpoint |]
                            |> Array.sortByDescending (fun r -> r.CreatedAt)

                        (createReferenceTable parseResult sortedReferences).Expand()

                    let outerTable = Table(Border = TableBorder.None, ShowHeaders = false).AddColumns(column1)

                    let branchTable = Table(ShowHeaders = false, Border = TableBorder.None, Expand = true)

                    branchTable
                        .AddColumn(column1)
                        .AddRow(basedOnMessage)
                        .AddEmptyRow()
                        .AddRow($"[{Colors.Important}] Most recent references:[/]")
                        .AddRow(Padder(referenceTable).Padding(1, 0, 0, 0))
                        .AddEmptyRow()
                    |> ignore

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
                                OwnerId = $"{branchDto.OwnerId}",
                                OrganizationId = $"{branchDto.OrganizationId}",
                                RepositoryId = $"{branchDto.RepositoryId}",
                                MaxCount = 5,
                                CorrelationId = graceIds.CorrelationId
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

                        | Error error -> logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
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

    type Status() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                let graceIds = parseResult |> getNormalizedIdsAndNames
                return! statusHandler parseResult
            }

    let private deleteHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let validateIncomingParameters = parseResult |> CommonValidations

                match validateIncomingParameters with
                | Ok _ ->
                    let reassignChildBranches = parseResult.GetValue(Options.reassignChildBranches)
                    let newParentBranchId = parseResult.GetValue(Options.newParentBranchId) |> valueOrEmpty
                    let newParentBranchName = parseResult.GetValue(Options.newParentBranchName) |> valueOrEmpty

                    let deleteParameters =
                        Parameters.Branch.DeleteBranchParameters(
                            BranchId = graceIds.BranchIdString,
                            BranchName = graceIds.BranchName,
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            ReassignChildBranches = reassignChildBranches,
                            NewParentBranchId = newParentBranchId,
                            NewParentBranchName = newParentBranchName,
                            CorrelationId = graceIds.CorrelationId
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

    type Delete() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let! result = deleteHandler parseResult
                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)

                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    let private updateParentBranchHandler (parseResult: ParseResult) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let graceIds = parseResult |> getNormalizedIdsAndNames

                let validateIncomingParameters = parseResult |> CommonValidations

                match validateIncomingParameters with
                | Ok _ ->
                    let newParentBranchId = parseResult.GetValue(Options.newParentBranchId) |> valueOrEmpty
                    let newParentBranchName = parseResult.GetValue(Options.newParentBranchName) |> valueOrEmpty

                    let updateParentBranchParameters =
                        Parameters.Branch.UpdateParentBranchParameters(
                            BranchId = graceIds.BranchIdString,
                            BranchName = graceIds.BranchName,
                            OwnerId = graceIds.OwnerIdString,
                            OwnerName = graceIds.OwnerName,
                            OrganizationId = graceIds.OrganizationIdString,
                            OrganizationName = graceIds.OrganizationName,
                            RepositoryId = graceIds.RepositoryIdString,
                            RepositoryName = graceIds.RepositoryName,
                            NewParentBranchId = newParentBranchId,
                            NewParentBranchName = newParentBranchName,
                            CorrelationId = graceIds.CorrelationId
                        )

                    if parseResult |> hasOutput then
                        return!
                            progress
                                .Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")

                                        let! result = Branch.UpdateParentBranch(updateParentBranchParameters)
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! Branch.UpdateParentBranch(updateParentBranchParameters)
                | Error error -> return Error error
            with ex ->
                return Error(GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId))
        }

    type UpdateParentBranch() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task {
                try
                    let graceIds = parseResult |> getNormalizedIdsAndNames
                    let! result = updateParentBranchHandler parseResult
                    return result |> renderOutput parseResult
                with ex ->
                    let graceError = GraceError.Create $"{ExceptionResponse.Create ex}" (parseResult |> getCorrelationId)

                    return renderOutput parseResult (GraceResult.Error graceError)
            }

    //type UndeleteParameters() =
    //    inherit CommonParameters()
    //let private undeleteHandler (parseResult: ParseResult) (undeleteParameters: UndeleteParameters) =
    //    task {
    //        try
    //            if parseResult |> verbose then printParseResult parseResult
    //            let validateIncomingParameters = parseResult |> CommonValidations
    //            match validateIncomingParameters with
    //            | Ok _ ->
    //                let parameters = Parameters.Owner.UndeleteParameters(OwnerId = undeletecontext.OwnerId, OwnerName = undeletecontext.OwnerName, CorrelationId = undeletecontext.CorrelationId)
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

        branchCommand.Aliases.Add("br")

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

        branchCreateCommand.Action <- new Create()
        branchCommand.Subcommands.Add(branchCreateCommand)

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

        switchCommand.Aliases.Add("download")
        switchCommand.Action <- new Switch()
        branchCommand.Subcommands.Add(switchCommand)

        let statusCommand =
            new Command("status", Description = "Displays status information about the current repository and branch.")
            |> addCommonOptions

        statusCommand.Action <- new Status()
        branchCommand.Subcommands.Add(statusCommand)

        let promoteCommand =
            new Command("promote", Description = "Promotes a commit into the parent branch.")
            |> addOption Options.message
            |> addCommonOptions

        promoteCommand.Action <- new Promote()
        branchCommand.Subcommands.Add(promoteCommand)

        let commitCommand =
            new Command("commit", Description = "Create a commit.")
            |> addOption Options.messageRequired
            |> addCommonOptions

        commitCommand.Action <- new Commit()
        branchCommand.Subcommands.Add(commitCommand)

        let checkpointCommand =
            new Command("checkpoint", Description = "Create a checkpoint.")
            |> addOption Options.message
            |> addCommonOptions

        checkpointCommand.Action <- new Checkpoint()
        branchCommand.Subcommands.Add(checkpointCommand)

        let saveCommand =
            new Command("save", Description = "Create a save.")
            |> addOption Options.message
            |> addCommonOptions

        saveCommand.Action <- new Save()
        branchCommand.Subcommands.Add(saveCommand)

        let tagCommand =
            new Command("tag", Description = "Create a tag.")
            |> addOption Options.messageRequired
            |> addCommonOptions

        tagCommand.Action <- new Tag()
        branchCommand.Subcommands.Add(tagCommand)

        let createExternalCommand =
            new Command("create-external", Description = "Create an external reference.")
            |> addOption Options.messageRequired
            |> addCommonOptions

        createExternalCommand.Action <- new CreateExternal()
        branchCommand.Subcommands.Add(createExternalCommand)

        let rebaseCommand =
            new Command("rebase", Description = "Rebase this branch on a promotion from the parent branch.")
            |> addCommonOptions

        rebaseCommand.Action <- new Rebase()
        branchCommand.Subcommands.Add(rebaseCommand)

        let listContentsCommand =
            new Command("list-contents", Description = "List directories and files in the current branch.")
            |> addOption Options.referenceId
            |> addOption Options.sha256Hash
            |> addOption Options.forceRecompute
            |> addCommonOptions

        listContentsCommand.Action <- new ListContents()
        branchCommand.Subcommands.Add(listContentsCommand)

        let getRecursiveSizeCommand =
            new Command("get-recursive-size", Description = "Get the recursive size of the current branch.")
            |> addOption Options.referenceId
            |> addOption Options.sha256Hash
            |> addCommonOptions

        getRecursiveSizeCommand.Action <- new GetRecursiveSize()
        branchCommand.Subcommands.Add(getRecursiveSizeCommand)

        let enableAssignCommand =
            new Command("enable-assign", Description = "Enable or disable assigning promotions on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enableAssignCommand.Action <- new EnableAssign()
        branchCommand.Subcommands.Add(enableAssignCommand)

        let enablePromotionCommand =
            new Command("enable-promotion", Description = "Enable or disable promotions on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enablePromotionCommand.Action <- new EnablePromotion()
        branchCommand.Subcommands.Add(enablePromotionCommand)

        let enableCommitCommand =
            new Command("enable-commit", Description = "Enable or disable commits on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enableCommitCommand.Action <- new EnableCommit()
        branchCommand.Subcommands.Add(enableCommitCommand)

        let enableCheckpointsCommand =
            new Command("enable-checkpoints", Description = "Enable or disable checkpoints on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enableCheckpointsCommand.Action <- new EnableCheckpoint()
        branchCommand.Subcommands.Add(enableCheckpointsCommand)

        let enableSaveCommand =
            new Command("enable-save", Description = "Enable or disable saves on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enableSaveCommand.Action <- new EnableSave()
        branchCommand.Subcommands.Add(enableSaveCommand)

        let enableTagCommand =
            new Command("enable-tag", Description = "Enable or disable tags on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enableTagCommand.Action <- new EnableTag()
        branchCommand.Subcommands.Add(enableTagCommand)

        let enableExternalCommand =
            new Command("enable-external", Description = "Enable or disable external references on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enableExternalCommand.Action <- new EnableExternal()
        branchCommand.Subcommands.Add(enableExternalCommand)

        let enableAutoRebaseCommand =
            new Command("enable-auto-rebase", Description = "Enable or disable auto-rebase on this branch.")
            |> addOption Options.enabled
            |> addCommonOptions

        enableAutoRebaseCommand.Action <- new EnableAutoRebase()
        branchCommand.Subcommands.Add(enableAutoRebaseCommand)

        let setNameCommand =
            new Command("set-name", Description = "Change the name of the branch.")
            |> addOption Options.newName
            |> addCommonOptions

        setNameCommand.Action <- new SetName()
        branchCommand.Subcommands.Add(setNameCommand)

        let getCommand =
            new Command("get", Description = "Gets details for the branch.")
            |> addOption Options.includeDeleted
            |> addOption Options.showEvents
            |> addCommonOptions

        getCommand.Action <- new Get()
        branchCommand.Subcommands.Add(getCommand)

        let getReferencesCommand =
            new Command("get-references", Description = "Retrieves a list of the most recent references from the branch.")
            |> addCommonOptions
            |> addOption Options.maxCount
            |> addOption Options.fullSha

        getReferencesCommand.Action <- new GetReferences()
        branchCommand.Subcommands.Add(getReferencesCommand)

        let getPromotionsCommand =
            new Command("get-promotions", Description = "Retrieves a list of the most recent promotions from the branch.")
            |> addCommonOptions
            |> addOption Options.maxCount
            |> addOption Options.fullSha

        getPromotionsCommand.Action <- new GetPromotions()
        branchCommand.Subcommands.Add(getPromotionsCommand)

        let getCommitsCommand =
            new Command("get-commits", Description = "Retrieves a list of the most recent commits from the branch.")
            |> addCommonOptions
            |> addOption Options.maxCount
            |> addOption Options.fullSha

        getCommitsCommand.Action <- new GetCommits()
        branchCommand.Subcommands.Add(getCommitsCommand)

        let getCheckpointsCommand =
            new Command("get-checkpoints", Description = "Retrieves a list of the most recent checkpoints from the branch.")
            |> addCommonOptions
            |> addOption Options.maxCount
            |> addOption Options.fullSha

        getCheckpointsCommand.Action <- new GetCheckpoints()
        branchCommand.Subcommands.Add(getCheckpointsCommand)

        let getSavesCommand =
            new Command("get-saves", Description = "Retrieves a list of the most recent saves from the branch.")
            |> addCommonOptions
            |> addOption Options.maxCount
            |> addOption Options.fullSha

        getSavesCommand.Action <- new GetSaves()
        branchCommand.Subcommands.Add(getSavesCommand)

        let getTagsCommand =
            new Command("get-tags", Description = "Retrieves a list of the most recent tags from the branch.")
            |> addCommonOptions
            |> addOption Options.maxCount
            |> addOption Options.fullSha

        getTagsCommand.Action <- new GetTags()
        branchCommand.Subcommands.Add(getTagsCommand)

        let getExternalsCommand =
            new Command("get-externals", Description = "Retrieves a list of the most recent external references from the branch.")
            |> addCommonOptions
            |> addOption Options.maxCount
            |> addOption Options.fullSha

        getExternalsCommand.Action <- new GetExternals()
        branchCommand.Subcommands.Add(getExternalsCommand)

        let deleteCommand =
            new Command("delete", Description = "Delete the branch.")
            |> addOption Options.reassignChildBranches
            |> addOption Options.newParentBranchId
            |> addOption Options.newParentBranchName
            |> addCommonOptions

        deleteCommand.Action <- new Delete()
        branchCommand.Subcommands.Add(deleteCommand)

        let updateParentBranchCommand =
            new Command("update-parent-branch", Description = "Update the parent branch of this branch.")
            |> addOption Options.newParentBranchId
            |> addOption Options.newParentBranchName
            |> addCommonOptions

        updateParentBranchCommand.Action <- new UpdateParentBranch()
        branchCommand.Subcommands.Add(updateParentBranchCommand)

        let assignCommand =
            new Command("assign", Description = "Assign a promotion to this branch.")
            |> addOption Options.directoryVersionId
            |> addOption Options.sha256Hash
            |> addOption Options.message
            |> addCommonOptions

        assignCommand.Action <- new Assign()
        branchCommand.Subcommands.Add(assignCommand)

        //let undeleteCommand = new Command("undelete", Description = "Undelete a deleted owner.") |> addCommonOptions
        //undeleteCommand.Action <- Undelete
        //branchCommand.Subcommands.Add(undeleteCommand)

        branchCommand
