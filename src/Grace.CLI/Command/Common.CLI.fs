namespace Grace.CLI

open FSharpPlus
open Grace.SDK
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.Shared
open Grace.Shared.Validation.Errors
open Grace.Shared.Client.Configuration
open Grace.Shared.Resources.Text
open Grace.Types.Common
open Grace.Types.Webhooks
open Grace.Shared.Utilities
open Spectre.Console
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Parsing
open System.Globalization
open System.Linq
open System.Text.Json
open System.Threading.Tasks
open Spectre.Console.Rendering
open Spectre.Console.Json
open System.Text.RegularExpressions

module Common =

    let mutable private renderedLifecycleWarnings = HashSet<string>(StringComparer.OrdinalIgnoreCase)

    let resetLifecycleWarningSuppression () = renderedLifecycleWarnings <- HashSet<string>(StringComparer.OrdinalIgnoreCase)

    type ParameterBase() =
        member val public CorrelationId: string = String.Empty with get, set
        member val public Json: bool = false with get, set
        member val public OutputFormat: string = String.Empty with get, set

    module LocalOutputDto =
        type AliasListItemDto = { Alias: string; CommandPath: string array; Command: string }

        type AliasListDto = { Aliases: AliasListItemDto array; Count: int }

        type HistoryEntryDto =
            {
                Id: Guid
                TimestampUtc: NodaTime.Instant
                CommandLine: string
                Cwd: string
                RepoRoot: string option
                RepoName: string option
                RepoBranch: string option
                GraceVersion: string
                ExitCode: int
                DurationMs: int64
                ParseSucceeded: bool
                Source: string option
                RedactionCount: int
            }

        type HistoryEntriesDto = { Entries: HistoryEntryDto array; Count: int; CorruptCount: int }

        type HistoryRecordingDto = { Enabled: bool }

        type HistoryDeleteDto = { Deleted: bool; Removed: int }

        type RepositoryInitDto =
            {
                Message: string
                DirectoryCount: int option
                FileCount: int option
                TotalFileSize: int64 option
                RootSha256Hash: string option
            }

        type ConnectDto =
            {
                OwnerId: Guid
                OwnerName: string
                OrganizationId: Guid
                OrganizationName: string
                RepositoryId: Guid
                RepositoryName: string
                BranchId: Guid
                BranchName: string
                DefaultBranchName: string
                RetrievedDefaultBranch: bool
            }

        type ReviewReportExportDto = { CandidateId: string; Format: string; OutputFile: string; BytesWritten: int64 }

        type MaintenanceStatsDto = { DirectoryCount: int; FileCount: int; TotalFileSize: int64; RootSha256Hash: string option }

        type MaintenanceListContentsFileDto =
            {
                RelativePath: string
                FileName: string
                Sha256Hash: string
                Blake3Hash: string
                Size: int64
                LastWriteTimeUtc: DateTime
            }

        type MaintenanceListContentsDirectoryDto =
            {
                RelativePath: string
                DirectoryVersionId: Guid
                Sha256Hash: string
                Blake3Hash: string
                Size: int64
                LastWriteTimeUtc: DateTime
                Files: MaintenanceListContentsFileDto array
            }

        type MaintenanceListContentsDto = { Summary: MaintenanceStatsDto; Directories: MaintenanceListContentsDirectoryDto array }

        type MaintenanceIgnoreEntriesDto = { DirectoryEntries: string array; FileEntries: string array }

        type MaintenanceScanDifferenceDto = { DifferenceType: string; FileSystemEntryType: string; RelativePath: string }

        type MaintenanceScanDirectoryVersionDto = { DirectoryVersionId: Guid; RelativePath: string; Sha256Hash: string; Blake3Hash: string }

        type MaintenanceScanDto =
            {
                DifferenceCount: int
                Differences: MaintenanceScanDifferenceDto array
                NewDirectoryVersionCount: int
                NewDirectoryVersions: MaintenanceScanDirectoryVersionDto array
            }

        type DoctorCheckDto = { Id: string; Category: string; Title: string; Description: string; DefaultEnabled: bool; SupportsOffline: bool }

        type DoctorCheckResultDto = { Id: string; Category: string; Title: string; Status: string; Severity: string; Summary: string }

        type DoctorSummaryDto = { Total: int; Ok: int; Warning: int; Failed: int; Skipped: int }

        type DoctorReportDto =
            {
                ReportVersion: string
                Status: string
                ExitCode: int
                Full: bool
                Offline: bool
                Strict: bool
                ListOnly: bool
                RequestedChecks: string array
                Catalog: DoctorCheckDto array
                Checks: DoctorCheckResultDto array
                Summary: DoctorSummaryDto
            }

        type WatchResultDto = { Started: bool; Completed: bool; Message: string; RootDirectory: string; ServerUri: string; RepositoryId: Guid; BranchId: Guid }

    /// The output format for the command.
    type OutputFormat =
        | Normal
        | Json
        | Minimal
        | Silent
        | Verbose

    /// Adds an option (i.e. parameter) to a command, so you can do cool stuff like `|> addOption Options.someOption |> addOption Options.anotherOption`.
    let addOption (option: Option) (command: Command) =
        command.Options.Add(option)
        command

    let public Language = CultureInfo.CurrentCulture.TwoLetterISOLanguageName

    /// Gets the "... ago" text.
    let ago = ago Language

    module Options =
        let correlationId =
            new Option<String>(
                OptionName.CorrelationId,
                [| "-c" |],
                Required = false,
                Description = "CorrelationId for end-to-end tracking <String>.",
                Arity = ArgumentArity.ExactlyOne,
                Recursive = true,
                DefaultValueFactory = (fun _ -> CorrelationId.Empty)
            )

        let source =
            new Option<string>(
                OptionName.Source,
                Required = false,
                Description = "Optional invocation source metadata for history attribution.",
                Arity = ArgumentArity.ExactlyOne,
                Recursive = true
            )

        let output =
            (new Option<String>(
                OptionName.Output,
                [| "-o" |],
                Required = false,
                Description = "The style of output.",
                Arity = ArgumentArity.ExactlyOne,
                Recursive = true,
                DefaultValueFactory = (fun _ -> "Normal")
            ))
                .AcceptOnlyFromAmong(listCases<OutputFormat> ())

        let schema =
            new Option<bool>(
                OptionName.Schema,
                Required = false,
                Description = "Emit the machine-readable JSON contract schema for the selected command.",
                Arity = ArgumentArity.Zero,
                Recursive = true,
                DefaultValueFactory = (fun _ -> false)
            )

        let examples =
            new Option<bool>(
                OptionName.Examples,
                Required = false,
                Description = "Emit representative machine-readable JSON examples for the selected command.",
                Arity = ArgumentArity.Zero,
                Recursive = true,
                DefaultValueFactory = (fun _ -> false)
            )

        let select =
            new Option<string>(
                OptionName.Select,
                Required = false,
                Description = "Emit only the selected dot-separated ReturnValue property path as JSON.",
                Arity = ArgumentArity.ExactlyOne,
                Recursive = true
            )

    module HashOptions =
        [<Literal>]
        let MinimumVersionHashPrefixLength = 2

        [<Literal>]
        let FullVersionHashLength = 64

        type VersionHashAlgorithm =
            | Blake3
            | Sha256Compatibility

        type VersionHashLookupMode =
            | NoVersionHashLookup
            | Blake3VersionHashLookup of blake3HashPrefix: string
            | Sha256CompatibilityVersionHashLookup of sha256HashPrefix: string
            | PairedVersionHashLookup of sha256HashPrefix: string * blake3HashPrefix: string

        type VersionHashDisplayMode = { FullHashes: bool; ShowSha256: bool; UsedDeprecatedFullSha: bool }

        [<Literal>]
        let MissingVersionHashText = "unavailable"

        let private algorithmName algorithm =
            match algorithm with
            | Blake3 -> "BLAKE3"
            | Sha256Compatibility -> "SHA-256"

        let private shortHash (value: string) =
            if String.IsNullOrWhiteSpace value then MissingVersionHashText
            elif value.Length <= 8 then value
            else value.Substring(0, 8)

        let formatVersionHash (hashDisplayMode: VersionHashDisplayMode) (value: string) =
            if String.IsNullOrWhiteSpace value then MissingVersionHashText
            elif hashDisplayMode.FullHashes then value
            else shortHash value

        let formatVersionHashPair (hashDisplayMode: VersionHashDisplayMode) (blake3Hash: Blake3Hash) (sha256Hash: Sha256Hash) =
            let blake3Display = formatVersionHash hashDisplayMode $"{blake3Hash}"

            if hashDisplayMode.ShowSha256 then
                let sha256Display = formatVersionHash hashDisplayMode $"{sha256Hash}"
                $"{blake3Display} (SHA-256 {sha256Display})"
            else
                blake3Display

        let private isHex value =
            value
            |> Seq.forall (fun c ->
                Char.IsDigit c
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F'))

        let normalizeVersionHashPrefix algorithm optionName (value: string) =
            let trimmed = if isNull value then String.Empty else value.Trim()

            if String.IsNullOrWhiteSpace trimmed then
                Error $"{optionName} requires a non-empty {algorithmName algorithm} version hash prefix."
            elif trimmed.Length < MinimumVersionHashPrefixLength then
                Error $"{optionName} must be at least {MinimumVersionHashPrefixLength} hex characters."
            elif trimmed.Length > FullVersionHashLength then
                Error $"{optionName} must be at most {FullVersionHashLength} hex characters."
            elif not <| isHex trimmed then
                Error $"{optionName} must contain only hexadecimal characters."
            else
                Ok(trimmed.ToLowerInvariant())

        let addVersionHashValidator algorithm optionName (option: Option<string>) =
            option.Validators.Add (fun optionResult ->
                let value = optionResult.GetValueOrDefault<string>()

                match normalizeVersionHashPrefix algorithm optionName value with
                | Ok _ -> ()
                | Error message -> optionResult.AddError(message))

            option

        let sha256HashOption description =
            new Option<string>(OptionName.Sha256Hash, [||], Required = false, Description = description, Arity = ArgumentArity.ExactlyOne)
            |> addVersionHashValidator Sha256Compatibility OptionName.Sha256Hash

        let blake3HashOption description =
            new Option<string>(OptionName.Blake3Hash, [||], Required = false, Description = description, Arity = ArgumentArity.ExactlyOne)
            |> addVersionHashValidator Blake3 OptionName.Blake3Hash

        let private explicitOptionValue (optionName: string) algorithm (parseResult: ParseResult) =
            let result = parseResult.GetResult(optionName)

            if isNull result then
                None
            else
                match
                    parseResult.GetValue<string>(optionName)
                    |> normalizeVersionHashPrefix algorithm optionName
                    with
                | Ok normalized -> Some normalized
                | Error _ -> None

        let bindVersionHashLookupMode (parseResult: ParseResult) =
            let sha256Hash = explicitOptionValue OptionName.Sha256Hash Sha256Compatibility parseResult
            let blake3Hash = explicitOptionValue OptionName.Blake3Hash Blake3 parseResult

            match sha256Hash, blake3Hash with
            | Some sha256Hash, Some blake3Hash -> PairedVersionHashLookup(sha256Hash, blake3Hash)
            | Some sha256Hash, None -> Sha256CompatibilityVersionHashLookup sha256Hash
            | None, Some blake3Hash -> Blake3VersionHashLookup blake3Hash
            | None, None -> NoVersionHashLookup

        let getSha256CompatibilityHashPrefix (parseResult: ParseResult) =
            explicitOptionValue OptionName.Sha256Hash Sha256Compatibility parseResult
            |> Option.defaultValue String.Empty

        let getBlake3HashPrefix (parseResult: ParseResult) =
            explicitOptionValue OptionName.Blake3Hash Blake3 parseResult
            |> Option.defaultValue String.Empty

        let bindVersionHashDisplayMode (parseResult: ParseResult) =
            let hasOption (optionName: string) = not <| isNull (parseResult.GetResult(optionName))

            let fullHashes =
                hasOption OptionName.FullHashes
                && parseResult.GetValue<bool>(OptionName.FullHashes)

            let showSha256 =
                hasOption OptionName.ShowSha256
                && parseResult.GetValue<bool>(OptionName.ShowSha256)

            let deprecatedFullSha =
                hasOption OptionName.FullSha
                && parseResult.GetValue<bool>(OptionName.FullSha)

            { FullHashes = fullHashes || deprecatedFullSha; ShowSha256 = showSha256 || deprecatedFullSha; UsedDeprecatedFullSha = deprecatedFullSha }

        let fullHashes =
            new Option<bool>(
                OptionName.FullHashes,
                Required = false,
                Description = "Show full version hash values in human output.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let showSha256 =
            new Option<bool>(
                OptionName.ShowSha256,
                Required = false,
                Description = "Include SHA-256 compatibility hash values in human output.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

        let deprecatedFullSha =
            new Option<bool>(
                OptionName.FullSha,
                Required = false,
                Description = "Deprecated compatibility option. Use --full-hashes with --show-sha256 instead.",
                Arity = ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (fun _ -> false)
            )

    /// Gets the correlationId value from the command's ParseResult.
    let getCorrelationId (parseResult: ParseResult) = Services.resolveCorrelationId parseResult

    [<Literal>]
    let SourceEnvironmentVariableName = "GRACE_SOURCE"

    let private normalizeSource (value: string) = if String.IsNullOrWhiteSpace(value) then None else Some(value.Trim())

    let private tryGetExplicitSourceFromParseResult (parseResult: ParseResult) =
        if isNull parseResult then
            None
        else
            let result = parseResult.GetResult(OptionName.Source)

            if isNull result then
                None
            else
                try
                    let optionResult = result :?> OptionResult

                    if optionResult.Implicit then
                        None
                    else
                        parseResult.GetValue<string>(OptionName.Source)
                        |> normalizeSource
                with
                | :? InvalidOperationException -> None

    let resolveInvocationSource (parseResult: ParseResult) =
        match tryGetExplicitSourceFromParseResult parseResult with
        | Some source -> Some source
        | None ->
            Environment.GetEnvironmentVariable(SourceEnvironmentVariableName)
            |> normalizeSource

    module Validations =
        /// Checks that a given name option is a valid Grace name. If the option is not present, it does not return an error.
        let mustBeAValidGraceName<'T when 'T :> IErrorDiscriminatedUnion> (parseResult: ParseResult) (optionName: string) (error: 'T) =
            let result = parseResult.GetResult(optionName)
            let value = parseResult.GetValue<string>(optionName)

            if result <> null
               && not <| Constants.GraceNameRegex.IsMatch(value) then
                Error(GraceError.Create (getErrorMessage error) (parseResult |> getCorrelationId))
            else
                Ok(parseResult)

        let ``Option must be present`` (optionName: string) (error: IErrorDiscriminatedUnion) (parseResult: ParseResult) =
            let result = parseResult.GetResult(optionName)

            if isNull result then
                Error(GraceError.Create (getErrorMessage error) (parseResult |> getCorrelationId))
            else
                Ok(parseResult)

        let ``OwnerName must be a valid Grace name`` (parseResult: ParseResult) =
            mustBeAValidGraceName parseResult OptionName.OwnerName OwnerError.InvalidOwnerName

        let ``OrganizationName must be a valid Grace name`` (parseResult: ParseResult) =
            mustBeAValidGraceName parseResult OptionName.OrganizationName OrganizationError.InvalidOrganizationName

        let ``RepositoryName must be a valid Grace name`` (parseResult: ParseResult) =
            mustBeAValidGraceName parseResult OptionName.RepositoryName RepositoryError.InvalidRepositoryName

        let ``BranchName must be a valid Grace name`` (parseResult: ParseResult) =
            mustBeAValidGraceName parseResult OptionName.BranchName BranchError.InvalidBranchName

        let ``NewName must be a valid Grace name`` (parseResult: ParseResult) =
            mustBeAValidGraceName parseResult OptionName.NewName RepositoryError.InvalidNewName

        let ``Either OwnerId or OwnerName must be provided`` (parseResult: ParseResult) =
            // Get the command that was invoked.
            let command = parseResult.CommandResult.Command

            // Only perform this validation if the command has an OwnerId option.
            if command.Options.Any(fun option -> option.Name = OptionName.OwnerId) then
                let ownerIdResult = parseResult.GetResult(OptionName.OwnerId) :?> OptionResult
                let ownerId = parseResult.GetValue<Guid>(OptionName.OwnerId)
                let ownerName = parseResult.GetValue<string>(OptionName.OwnerName)

                let isOk =
                    ownerIdResult.Implicit
                    || ownerId <> Guid.Empty
                    || not <| String.IsNullOrWhiteSpace(ownerName)

                if isOk then
                    Ok(parseResult)
                else
                    Error(GraceError.Create (getErrorMessage OwnerError.EitherOwnerIdOrOwnerNameRequired) (parseResult |> getCorrelationId))
            else
                Ok(parseResult)

        let ``Either OrganizationId or OrganizationName must be provided`` (parseResult: ParseResult) =
            // Get the command that was invoked.
            let command = parseResult.CommandResult.Command
            // Only perform this validation if the command has an OrganizationId option.
            if command.Options.Any(fun option -> option.Name = OptionName.OrganizationId) then
                let organizationId = parseResult.GetValue<Guid>(OptionName.OrganizationId)
                let organizationName = parseResult.GetValue<string>(OptionName.OrganizationName)

                if
                    organizationId = Guid.Empty
                    && String.IsNullOrWhiteSpace(organizationName)
                then
                    Error(
                        GraceError.Create (getErrorMessage OrganizationError.EitherOrganizationIdOrOrganizationNameRequired) (parseResult |> getCorrelationId)
                    )
                else
                    Ok(parseResult)
            else
                Ok(parseResult)


        let ``Either RepositoryId or RepositoryName must be provided`` (parseResult: ParseResult) =
            // Get the command that was invoked.
            let command = parseResult.CommandResult.Command
            // Only perform this validation if the command has a RepositoryId option.
            if command.Options.Any(fun option -> option.Name = OptionName.RepositoryId) then
                let repositoryId = parseResult.GetValue<Guid>(OptionName.RepositoryId)
                let repositoryName = parseResult.GetValue<string>(OptionName.RepositoryName)

                if
                    repositoryId = Guid.Empty
                    && String.IsNullOrWhiteSpace(repositoryName)
                then
                    Error(GraceError.Create (getErrorMessage RepositoryError.EitherRepositoryIdOrRepositoryNameRequired) (parseResult |> getCorrelationId))
                else
                    Ok(parseResult)
            else
                Ok(parseResult)

        let ``Either BranchId or BranchName must be provided`` (parseResult: ParseResult) =
            // Get the command that was invoked.
            let command = parseResult.CommandResult.Command
            // Only perform this validation if the command has a BranchId option.
            if command.Options.Any(fun option -> option.Name = OptionName.BranchId) then
                let branchId = parseResult.GetValue<Guid>(OptionName.BranchId)
                let branchName = parseResult.GetValue<string>(OptionName.BranchName)

                if
                    branchId = Guid.Empty
                    && String.IsNullOrWhiteSpace(branchName)
                then
                    Error(GraceError.Create (getErrorMessage BranchError.EitherBranchIdOrBranchNameRequired) (parseResult |> getCorrelationId))
                else
                    Ok(parseResult)
            else
                Ok(parseResult)

        let CommonValidations (parseResult: ParseResult) =
            parseResult
            |> ``OwnerName must be a valid Grace name``
            >>= ``OrganizationName must be a valid Grace name``
            >>= ``RepositoryName must be a valid Grace name``
            >>= ``BranchName must be a valid Grace name``
            >>= ``NewName must be a valid Grace name``

    //>>= ``Either OwnerId or OwnerName must be provided``
    //>>= ``Either OrganizationId or OrganizationName must be provided``
    //>>= ``Either RepositoryId or RepositoryName must be provided``
    //>>= ``Either BranchId or BranchName must be provided``

    /// Checks if the output format from the command line is a specific format.
    let isOutputFormat (outputFormat: OutputFormat) (parseResult: ParseResult) =
        try
            let outputOption = parseResult.GetValue(Options.output)

            match outputOption with
            | null ->
                // The command didn't have an output option set, which means it defaults to Normal.
                if outputFormat = OutputFormat.Normal then true else false
            | _ ->
                // The command had an output option set, so we check if it matches the expected output format.
                let formatFromCommand = parseResult.GetValue<string>(Options.output)

                if outputFormat = discriminatedUnionFromString<OutputFormat>(
                    formatFromCommand
                )
                    .Value then
                    true
                else
                    false
        with
        | ex ->
            logToAnsiConsole Colors.Error $"Exception in isOutputFormat: {ExceptionResponse.Create ex}"
            false

    /// Checks if the output format from the command line is Json.
    let tryGetSelect (parseResult: ParseResult) =
        if isNull parseResult then
            None
        else
            let result = parseResult.GetResult(OptionName.Select)

            if isNull result then
                None
            else
                try
                    parseResult.GetValue<string>(Options.select)
                    |> Option.ofObj
                with
                | :? InvalidOperationException -> None

    let hasSelect parseResult = tryGetSelect parseResult |> Option.isSome

    /// Checks if the command should emit machine-readable JSON.
    let json parseResult =
        (parseResult |> isOutputFormat Json)
        || (parseResult |> hasSelect)

    /// Checks if the output format from the command line is Minimal.
    let minimal parseResult = parseResult |> isOutputFormat Minimal

    /// Checks if the output format from the command line is Normal.
    let normal parseResult = parseResult |> isOutputFormat Normal

    /// Checks if the output format from the command line is Silent.
    let silent parseResult = parseResult |> isOutputFormat Silent

    /// Checks if the output format from the command line is Verbose.
    let verbose parseResult = parseResult |> isOutputFormat Verbose

    /// Checks if the output format from the command line is either Normal or Verbose; i.e. it has output.
    let hasOutput parseResult = parseResult |> normal || parseResult |> verbose

    let startProgressTask showOutput (t: ProgressTask) = if showOutput then t.StartTask()

    let setProgressTaskValue showOutput (value: float) (t: ProgressTask) = if showOutput then t.Value <- value

    let incrementProgressTaskValue showOutput (value: float) (t: ProgressTask) = if showOutput then t.Increment(value)

    let emptyTask = ProgressTask(0, "Empty progress task", 0.0, autoStart = false)

    /// Rewrites "[" to "[[" and "]" to "]]".
    let escapeBrackets s = s.ToString().Replace("[", "[[").Replace("]", "]]")

    let private resolvedValueOptionNames =
        [
            OptionName.OwnerId
            OptionName.OwnerName
            OptionName.OrganizationId
            OptionName.OrganizationName
            OptionName.RepositoryId
            OptionName.RepositoryName
            OptionName.BranchId
            OptionName.BranchName
        ]

    let private shouldShowResolvedValues (parseResult: ParseResult) =
        resolvedValueOptionNames
        |> List.exists (isOptionPresent parseResult)

    let private tryBuildResolvedValuesText (parseResult: ParseResult) =
        if
            isNull parseResult
            || not (configurationFileExists ())
            || not (shouldShowResolvedValues parseResult)
        then
            None
        else
            let graceIds = Services.getNormalizedIdsAndNames parseResult
            let sb = stringBuilderPool.Get()

            try
                let appendLine label value = sb.AppendLine($"{label}: {value}") |> ignore

                let appendName label (value: string) = if not <| String.IsNullOrWhiteSpace(value) then appendLine label value

                if graceIds.HasOwner then
                    appendLine "OwnerId" graceIds.OwnerId
                    appendName "OwnerName" graceIds.OwnerName

                if graceIds.HasOrganization then
                    appendLine "OrganizationId" graceIds.OrganizationId
                    appendName "OrganizationName" graceIds.OrganizationName

                if graceIds.HasRepository then
                    appendLine "RepositoryId" graceIds.RepositoryId
                    appendName "RepositoryName" graceIds.RepositoryName

                if graceIds.HasBranch then
                    appendLine "BranchId" graceIds.BranchId
                    appendName "BranchName" graceIds.BranchName

                if sb.Length > 0 then Some(sb.ToString()) else None
            finally
                stringBuilderPool.Return sb

    /// Prints the ParseResult with markup.
    let printParseResult (parseResult: ParseResult) =
        if not <| isNull parseResult then
            let sb = stringBuilderPool.Get()

            try
                // Gather all options from the root command and the invoked command.
                let optionList =
                    parseResult.RootCommandResult.Command.Options
                    |> Seq.append parseResult.CommandResult.Command.Options
                    |> Seq.sortBy (fun option -> option.Name)
                    |> Seq.toIReadOnlyList

                let tryGetValue (option: Option) =
                    let result = parseResult.GetResult(option.Name)

                    if isNull result then
                        None
                    else
                        try
                            let value = parseResult.GetValue(option.Name)
                            if isNull value then None else Some value
                        with
                        | :? InvalidOperationException -> None

                for option in optionList do
                    match tryGetValue option with
                    | Some value ->
                        if option.ValueType.IsArray then
                            sb.AppendLine($"{option.Name}: {serialize value}")
                            |> ignore
                        else
                            sb.AppendLine($"{option.Name}: {value}") |> ignore
                    | None -> ()

                AnsiConsole.MarkupLine($"[{Colors.Verbose}]{escapeBrackets (parseResult.ToString())}[/]")
                AnsiConsole.WriteLine()
                AnsiConsole.MarkupLine($"[{Colors.Verbose}]Parameter values:[/]")
                AnsiConsole.MarkupLine($"[{Colors.Verbose}]{escapeBrackets (sb.ToString())}[/]")
                AnsiConsole.WriteLine()

                match tryBuildResolvedValuesText parseResult with
                | Some resolvedValues ->
                    AnsiConsole.MarkupLine($"[{Colors.Verbose}]Resolved values:[/]")
                    AnsiConsole.MarkupLine($"[{Colors.Verbose}]{escapeBrackets resolvedValues}[/]")
                    AnsiConsole.WriteLine()
                | None -> ()
            finally
                stringBuilderPool.Return sb

    /// Prints AnsiConsole markup to the console.
    let writeMarkup (markup: IRenderable) =
        AnsiConsole.Write(markup)
        AnsiConsole.WriteLine()

    let private redactedValue = "[redacted]"

    let private redactScopedOutboundUrl (url: ScopedOutboundUrl) = { url with Url = redactedValue }

    let private redactWebhookRule (rule: WebhookRule) = { rule with Url = redactScopedOutboundUrl rule.Url; SigningSecretVersion = redactedValue }

    let private redactApprovalPolicy (policy: ApprovalPolicy) =
        { policy with
            NotificationUrl =
                policy.NotificationUrl
                |> Option.map redactScopedOutboundUrl
        }

    let private redactJsonReturnValue<'T> (returnValue: 'T) =
        match box returnValue with
        | :? WebhookRule as rule -> box (redactWebhookRule rule)
        | :? (WebhookRule array) as rules -> box (rules |> Array.map redactWebhookRule)
        | :? (IEnumerable<WebhookRule>) as rules -> box (rules |> Seq.map redactWebhookRule |> Seq.toArray)
        | :? ApprovalPolicy as policy -> box (redactApprovalPolicy policy)
        | :? (ApprovalPolicy array) as policies -> box (policies |> Array.map redactApprovalPolicy)
        | :? (IEnumerable<ApprovalPolicy>) as policies ->
            box (
                policies
                |> Seq.map redactApprovalPolicy
                |> Seq.toArray
            )
        | _ -> box returnValue

    let writeJsonStdout value = Console.Out.WriteLine(serialize value)

    let writeJsonErrorStdout (error: GraceError) = writeJsonStdout error

    let private renderJsonReturnValue (graceReturnValue: GraceReturnValue<'T>) =
        let output =
            {|
                ReturnValue = redactJsonReturnValue graceReturnValue.ReturnValue
                EventTime = graceReturnValue.EventTime
                CorrelationId = graceReturnValue.CorrelationId
                Properties = graceReturnValue.Properties.Select(fun kvp -> {| Key = kvp.Key; Value = kvp.Value |})
            |}

        serialize output

    let writeJsonReturnValueStdout (graceReturnValue: GraceReturnValue<'T>) = Console.Out.WriteLine(renderJsonReturnValue graceReturnValue)

    let private tryRenderJsonSelection (parseResult: ParseResult) (graceReturnValue: GraceReturnValue<'T>) =
        match tryGetSelect parseResult with
        | None -> None
        | Some selectorText ->
            let correlationId = graceReturnValue.CorrelationId

            match SelectProjection.tryParse correlationId selectorText with
            | Error error -> Some(Error error)
            | Ok selector ->
                let returnValue = redactJsonReturnValue graceReturnValue.ReturnValue

                match SelectProjection.project correlationId selector returnValue with
                | Error error -> Some(Error error)
                | Ok selected -> Some(Ok(SelectProjection.renderSelectedJson selected))

    let private tryGetProperty (properties: Dictionary<string, obj>) key =
        if isNull properties then
            None
        else
            match properties.TryGetValue key with
            | true, value when
                not (isNull value)
                && not <| String.IsNullOrWhiteSpace($"{value}")
                ->
                Some($"{value}")
            | _ -> None

    let private isTrueProperty (properties: Dictionary<string, obj>) key =
        match tryGetProperty properties key with
        | Some value -> value.Equals("true", StringComparison.OrdinalIgnoreCase)
        | None -> false

    let private sanitizeLifecyclePropertiesForHumanOutput (properties: Dictionary<string, obj>) =
        let sanitized = Dictionary<string, obj>()
        let updateUrlIsHttps = isTrueProperty properties ClientIdentity.LifecycleUpdateUrlIsHttpsPropertyKey

        if not (isNull properties) then
            properties
            |> Seq.iter (fun kvp ->
                if
                    kvp.Key.Equals(ClientIdentity.LifecycleUpdateUrlPropertyKey, StringComparison.Ordinal)
                    && not updateUrlIsHttps
                then
                    ()
                else
                    sanitized[kvp.Key] <- kvp.Value)

        sanitized

    let private appendIfSome (lines: ResizeArray<string>) label value =
        match value with
        | Some value -> lines.Add($"{label}: {value}")
        | None -> ()

    let private tryBuildLifecycleWarning (properties: Dictionary<string, obj>) =
        match tryGetProperty properties ClientIdentity.LifecycleStatusPropertyKey with
        | None -> None
        | Some status ->
            let recommendedVersion = tryGetProperty properties ClientIdentity.LifecycleRecommendedVersionPropertyKey
            let minimumVersion = tryGetProperty properties ClientIdentity.LifecycleMinimumVersionPropertyKey
            let unsupportedAfter = tryGetProperty properties ClientIdentity.LifecycleUnsupportedAfterPropertyKey
            let updateUrl = tryGetProperty properties ClientIdentity.LifecycleUpdateUrlPropertyKey
            let updateUrlIsHttps = isTrueProperty properties ClientIdentity.LifecycleUpdateUrlIsHttpsPropertyKey
            let unsupportedAfterIsMalformed = isTrueProperty properties ClientIdentity.LifecycleUnsupportedAfterIsMalformedPropertyKey
            let normalizedStatus = status.Trim().ToLowerInvariant()

            let title =
                match normalizedStatus with
                | "unsupported" -> "This Grace client version is no longer supported."
                | "deprecated" -> "This Grace client version is deprecated."
                | _ -> $"Grace reported client lifecycle status '{status}'."

            let lines = ResizeArray<string>()
            lines.Add(title)

            match recommendedVersion, minimumVersion with
            | Some recommendedVersion, _ -> lines.Add($"Update to Grace CLI/SDK version {recommendedVersion} or newer.")
            | None, Some minimumVersion -> lines.Add($"Update to Grace CLI/SDK version {minimumVersion} or newer.")
            | None, None -> lines.Add("Update Grace CLI/SDK before continuing with production workflows.")

            if normalizedStatus = "unsupported" then
                lines.Add("The server rejected this request because the client version is unsupported.")

            appendIfSome lines "Minimum supported version" minimumVersion
            appendIfSome lines "Recommended version" recommendedVersion

            match unsupportedAfter with
            | Some value when unsupportedAfterIsMalformed -> lines.Add($"Unsupported-after value from server could not be parsed: {value}.")
            | Some value -> lines.Add($"Unsupported after: {value}.")
            | None -> ()

            match updateUrl, updateUrlIsHttps with
            | Some value, true -> lines.Add($"Update URL: {value}")
            | Some _, false -> lines.Add("Update URL from server was not HTTPS and was not displayed.")
            | None, _ -> ()

            Some(normalizedStatus, String.Join(Environment.NewLine, lines))

    let private renderLifecycleWarningOnce parseResult outputFormat properties =
        match outputFormat with
        | Json
        | Silent -> ()
        | _ when parseResult |> hasSelect -> ()
        | _ ->
            match tryBuildLifecycleWarning properties with
            | Some (status, warningText) ->
                let warningKey = $"{status}:{warningText}"

                if renderedLifecycleWarnings.Add warningKey then
                    AnsiConsole.MarkupLine($"[{Colors.Important}]{Markup.Escape(warningText)}[/]")
            | None -> ()

    /// Prints output to the console, depending on the output format.
    let renderOutput (parseResult: ParseResult) (result: GraceResult<'T>) =
        let outputFormat =
            discriminatedUnionFromString<OutputFormat>(
                parseResult.GetValue(Options.output)
            )
                .Value

        match result with
        | Ok graceReturnValue ->
            renderLifecycleWarningOnce parseResult outputFormat graceReturnValue.Properties

            match tryRenderJsonSelection parseResult graceReturnValue with
            | Some (Ok json) ->
                Console.Out.WriteLine(json)
                0
            | Some (Error error) ->
                writeJsonErrorStdout error
                -1
            | None ->
                match outputFormat with
                | Json -> writeJsonReturnValueStdout graceReturnValue
                | Minimal -> () //AnsiConsole.MarkupLine($"""[{Colors.Highlighted}]{Markup.Escape($"{graceReturnValue.ReturnValue}")}[/]""")
                | Silent -> ()
                | Verbose ->
                    AnsiConsole.WriteLine()

                    AnsiConsole.MarkupLine($"""[{Colors.Verbose}]EventTime: {formatInstantExtended graceReturnValue.EventTime}[/]""")

                    AnsiConsole.MarkupLine($"""[{Colors.Verbose}]CorrelationId: "{graceReturnValue.CorrelationId}"[/]""")

                    let properties = sanitizeLifecyclePropertiesForHumanOutput graceReturnValue.Properties

                    AnsiConsole.MarkupLine($"""[{Colors.Verbose}]Properties: {Markup.Escape(serialize properties)}[/]""")

                    AnsiConsole.WriteLine()
                | Normal -> () // Return unit because in the Normal case, we expect to print output within each command.

                0
        | Error error ->
            renderLifecycleWarningOnce parseResult outputFormat error.Properties

            let json =
                if error.Error.Contains("Stack trace") then
                    Uri.UnescapeDataString(error.Error)
                else
                    Uri.UnescapeDataString(serialize error)

            let errorText =
                if error.Error.Contains("Stack trace") then
                    try
                        let exceptionResponse = deserialize<ExceptionResponse> error.Error
                        Uri.UnescapeDataString($"{exceptionResponse}")
                    with
                    | ex -> Uri.UnescapeDataString(error.Error)
                else
                    Uri.UnescapeDataString(error.Error)

            match outputFormat with
            | _ when parseResult |> hasSelect -> writeJsonErrorStdout error
            | Json -> writeJsonErrorStdout error
            | Minimal -> AnsiConsole.MarkupLine($"[{Colors.Error}]{Markup.Escape(errorText)}[/]")
            | Silent -> ()
            | Verbose ->
                AnsiConsole.MarkupLine($"[{Colors.Error}]{Markup.Escape(errorText)}[/]")
                AnsiConsole.WriteLine()

                let sanitizedError = { error with Properties = sanitizeLifecyclePropertiesForHumanOutput error.Properties }

                let verboseJson =
                    if error.Error.Contains("Stack trace") then
                        json
                    else
                        Uri.UnescapeDataString(serialize sanitizedError)

                AnsiConsole.MarkupLine($"[{Colors.Verbose}]{Markup.Escape(verboseJson)}[/]")
                AnsiConsole.WriteLine()
            | Normal -> AnsiConsole.MarkupLine($"[{Colors.Error}]{Markup.Escape(errorText)}[/]")

            -1

    let progressBarColumn = new ProgressBarColumn()
    progressBarColumn.FinishedStyle <- new Style(foreground = Color.Green)

    let percentageColumn = new PercentageColumn()
    percentageColumn.Style <- new Style(foreground = Color.Yellow)
    percentageColumn.CompletedStyle <- new Style(foreground = Color.Yellow)

    let spinnerColumn = new SpinnerColumn(Spinner.Known.Dots)

    let progressColumns: ProgressColumn [] =
        [|
            new TaskDescriptionColumn(Alignment = Justify.Right)
            progressBarColumn
            percentageColumn
            spinnerColumn
        |]

    let progress = AnsiConsole.Progress(AutoRefresh = true, AutoClear = false, HideCompleted = false)
