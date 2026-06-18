namespace Grace.CLI

open Grace.CLI.Command
open Grace.CLI.Common
open Grace.CLI.Services
open Grace.CLI.Text
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Converters
open Grace.Shared.Resources.Text
open Grace.Shared.Resources.Utilities
open Grace.Types.Common
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Microsoft.Extensions.Logging
open NodaTime
open NodaTime.Text
open Spectre.Console
open System
open System.Collections
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Help
open System.CommandLine.Parsing
open System.Diagnostics
open System.Globalization
open System.IO
open System.Linq
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.Extensions.Caching.Memory
open System.CommandLine.Help
open FSharpPlus.Control
open System.CommandLine.Invocation

module Configuration =

    type GraceCLIConfiguration = { GraceWatchStatus: GraceWatchStatus }

    let mutable private cliConfiguration = { GraceWatchStatus = GraceWatchStatus.Default }

    let CLIConfiguration () = cliConfiguration
    let updateConfiguration (config: GraceCLIConfiguration) = cliConfiguration <- config

module GraceCommand =

    exception private IntrospectionExit of int

    type OptionToUpdate = { optionAlias: string; display: string; displayOnCreate: string; createParentCommand: string }

    let private deleteGraceWatchIpcFileIfOwned () =
        if graceWatchStatusUpdateTime <> Instant.MinValue then
            try
                let ipcFileName = IpcFileName()

                if File.Exists(ipcFileName) then
                    let status =
                        File.ReadAllText(ipcFileName)
                        |> deserialize<GraceWatchStatus>

                    if status.UpdatedAt = graceWatchStatusUpdateTime then File.Delete(ipcFileName)
            with
            | _ -> ()

    /// Built-in aliases for Grace commands.
    let private aliases =
        let aliases = Dictionary<string, string seq>()
        aliases.Add("aliases", [ "alias"; "list" ])
        aliases.Add("branches", [ "repository"; "get-branches" ])
        aliases.Add("checkpoint", [ "branch"; "checkpoint" ])
        aliases.Add("checkpoints", [ "branch"; "get-checkpoints" ])
        aliases.Add("commit", [ "branch"; "commit" ])
        aliases.Add("commits", [ "branch"; "get-commits" ])
        aliases.Add("dir", [ "maint"; "list-contents" ])
        aliases.Add("login", [ "authenticate"; "login" ])
        aliases.Add("logout", [ "authenticate"; "logout" ])
        aliases.Add("ls", [ "maint"; "list-contents" ])
        aliases.Add("promote", [ "branch"; "promote" ])
        aliases.Add("promotions", [ "branch"; "get-promotions" ])
        aliases.Add("rebase", [ "branch"; "rebase" ])
        aliases.Add("refs", [ "branch"; "get-references" ])
        aliases.Add("save", [ "branch"; "save" ])
        aliases.Add("saves", [ "branch"; "get-saves" ])
        aliases.Add("sdir", [ "branch"; "list-contents" ])
        aliases.Add("sls", [ "branch"; "list-contents" ])
        aliases.Add("status", [ "branch"; "status" ])
        aliases.Add("switch", [ "branch"; "switch" ])
        aliases.Add("tag", [ "branch"; "tag" ])
        aliases.Add("tags", [ "branch"; "get-tags" ])
        //aliases.Add("", [""; ""])
        aliases

    let private getAliasListDto () =
        let items: LocalOutputDto.AliasListItemDto array =
            aliases
            |> Seq.map (fun alias ->
                let commandPath = alias.Value |> Seq.toArray
                let command = String.Join(" ", commandPath)

                let item: LocalOutputDto.AliasListItemDto = { Alias = $"grace {alias.Key}"; CommandPath = commandPath; Command = $"grace {command}" }

                item)
            |> Seq.sortBy (fun item -> item.Alias)
            |> Seq.toArray

        let dto: LocalOutputDto.AliasListDto = { Aliases = items; Count = items.Length }
        dto

    /// The character sequences that Grace will recognize as a request for help.
    let helpOptions = [| "-h"; "/h"; "--help"; "-?"; "/?" |]

    /// Prints the aliases for Grace commands.
    let printAliases () =
        let table = Table(Border = TableBorder.DoubleEdge)

        table.AddColumns(
            [|
                TableColumn($"[{Colors.Important}]Alias[/]")
                TableColumn($"[{Colors.Important}]Grace command[/]")
            |]
        )
        |> ignore

        aliases
        |> Seq.iter (fun alias ->
            table.AddRow($"grace {alias.Key}", $"grace {alias.Value.First()} {alias.Value.Last()}")
            |> ignore)

        AnsiConsole.Write(table)

    let private renderAliases (parseResult: ParseResult) =
        if parseResult |> json then
            let result =
                GraceReturnValue.Create (getAliasListDto ()) (getCorrelationId parseResult)
                |> Ok

            renderOutput parseResult result
        elif parseResult |> silent then
            0
        else
            printAliases ()
            0

    let internal tryGetTopLevelCommandFromArgs (args: string array) (isCaseInsensitive: bool) =
        if isNull args || args.Length = 0 then
            None
        else
            let comparison =
                if isCaseInsensitive then
                    StringComparison.InvariantCultureIgnoreCase
                else
                    StringComparison.InvariantCulture

            let isOptionWithValue (token: string) =
                token.Equals(OptionName.Output, comparison)
                || token.Equals("-o", comparison)
                || token.Equals(OptionName.CorrelationId, comparison)
                || token.Equals("-c", comparison)
                || token.Equals(OptionName.Source, comparison)
                || token.Equals(OptionName.Select, comparison)

            let rec loop index =
                if index >= args.Length then
                    None
                else
                    let token = args[index]

                    if token = "--" then
                        if index + 1 < args.Length then Some args[index + 1] else None
                    elif token.StartsWith("-", StringComparison.Ordinal) then
                        let nextIndex = if isOptionWithValue token then index + 2 else index + 1
                        loop nextIndex
                    else
                        Some token

            loop 0

    /// Gathers the available options for the current command and all its parents, which are applied hierarchically.
    [<TailCall>]
    let rec gatherAllOptions (command: Command) (allOptions: List<Option>) =
        allOptions.AddRange(command.Options)
        let parentCommand = command.Parents.OfType<Command>().FirstOrDefault()

        if not <| isNull parentCommand then
            gatherAllOptions parentCommand allOptions
        else
            allOptions

    let private commandIdentityFromCommandResult (commandResult: CommandResult) =
        let rec loop (current: SymbolResult) (names: string list) =
            match current with
            | :? CommandResult as currentCommand ->
                let nextNames =
                    if currentCommand.Command :? RootCommand then
                        names
                    else
                        currentCommand.Command.Name :: names

                if isNull currentCommand.Parent then
                    nextNames
                else
                    loop currentCommand.Parent nextNames
            | _ -> names

        let path = loop commandResult []

        match path with
        | [] -> CommandOutputContract.commandIdentity [] commandResult.Command.Name
        | _ ->
            let commandName = path |> List.last
            let groupPath = path |> List.take (path.Length - 1)
            CommandOutputContract.commandIdentity groupPath commandName

    let private introspectionRequestFromTokens (args: string array) =
        let tokens =
            if isNull args then
                Array.empty
            else
                args
                |> Array.takeWhile (fun token -> not (token.Equals("--", StringComparison.Ordinal)))

        let containsOption optionName =
            tokens
            |> Array.exists (fun token -> token.Equals(optionName, StringComparison.OrdinalIgnoreCase))

        let schema = containsOption OptionName.Schema
        let examples = containsOption OptionName.Examples

        match schema, examples with
        | false, false -> None
        | true, false -> Some(Ok CommandOutputContract.IntrospectionKind.Schema)
        | false, true -> Some(Ok CommandOutputContract.IntrospectionKind.Examples)
        | true, true -> Some(Error "--schema and --examples cannot be used together.")

    let private writeIntrospectionError parseResult message =
        let error = GraceError.Create message (getCorrelationId parseResult)
        Common.writeJsonErrorStdout error
        -1

    let private isIgnorableIntrospectionParseError (error: ParseError) =
        error.Message.StartsWith("Required argument missing for command:", StringComparison.Ordinal)

    let private hasBlockingIntrospectionParseErrors (parseResult: ParseResult) =
        parseResult.Errors
        |> Seq.exists (isIgnorableIntrospectionParseError >> not)

    let private writeIntrospectionParseError (parseResult: ParseResult) =
        let message =
            parseResult.Errors
            |> Seq.filter (isIgnorableIntrospectionParseError >> not)
            |> Seq.map (fun error -> error.Message)
            |> String.concat Environment.NewLine

        writeIntrospectionError parseResult message

    let private handleIntrospectionRequest (parseResult: ParseResult) kind =
        let identity = commandIdentityFromCommandResult parseResult.CommandResult

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            match entry.RouteDisposition with
            | CommandOutputContract.Routed ->
                let document = CommandOutputContract.introspectionDocument kind entry
                Common.writeJsonStdout document
                0
            | CommandOutputContract.SourceOnlyUnrouted reason ->
                writeIntrospectionError parseResult $"Command '{identity.CommandId}' is not routed for CLI introspection. {reason}"
        | None -> writeIntrospectionError parseResult $"Command '{identity.CommandId}' does not have CLI output contract metadata."

    let private isJsonOutputRequestedFromTokens (args: string array) =
        let tokens =
            if isNull args then
                Array.empty
            else
                args
                |> Array.takeWhile (fun token -> not (token.Equals("--", StringComparison.Ordinal)))

        let rec loop index =
            if index >= tokens.Length then
                false
            else
                let token = tokens[index]
                let outputEqualsPrefix = $"{OptionName.Output}="

                if
                    token.Equals(OptionName.Output, StringComparison.OrdinalIgnoreCase)
                    || token.Equals("-o", StringComparison.OrdinalIgnoreCase)
                then
                    if index + 1 < tokens.Length
                       && tokens[index + 1]
                           .Equals("Json", StringComparison.OrdinalIgnoreCase) then
                        true
                    elif index + 1 < tokens.Length
                         && tokens[index + 1]
                             .StartsWith("-", StringComparison.Ordinal) then
                        loop (index + 1)
                    else
                        loop (index + 2)
                elif token.StartsWith(outputEqualsPrefix, StringComparison.OrdinalIgnoreCase) then
                    if token
                        .Substring(outputEqualsPrefix.Length)
                           .Equals("Json", StringComparison.OrdinalIgnoreCase) then
                        true
                    else
                        loop (index + 1)
                elif token.StartsWith("-o=", StringComparison.OrdinalIgnoreCase) then
                    if token
                        .Substring("-o=".Length)
                           .Equals("Json", StringComparison.OrdinalIgnoreCase) then
                        true
                    else
                        loop (index + 1)
                elif
                    token.Equals(OptionName.Select, StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith($"{OptionName.Select}=", StringComparison.OrdinalIgnoreCase)
                then
                    true
                else
                    loop (index + 1)

        loop 0

    let private writeJsonParseError (parseResult: ParseResult) =
        let message =
            parseResult.Errors
            |> Seq.map (fun error -> error.Message)
            |> String.concat Environment.NewLine

        let error = GraceError.Create message (getCorrelationId parseResult)
        Common.writeJsonErrorStdout error
        -1

    let private writeJsonException (parseResult: ParseResult) (ex: exn) =
        let correlationId =
            if isNull parseResult then
                generateCorrelationId ()
            else
                getCorrelationId parseResult

        let error = GraceError.CreateWithException ex ex.Message correlationId
        Common.writeJsonErrorStdout error
        -1

    let private tryValidateSelectRequest (parseResult: ParseResult) =
        match Common.tryGetSelect parseResult with
        | None -> None
        | Some selectorText ->
            let correlationId = getCorrelationId parseResult

            match SelectProjection.tryParse correlationId selectorText with
            | Error error -> Some error
            | Ok _ ->
                let identity = commandIdentityFromCommandResult parseResult.CommandResult

                match CommandOutputContract.tryFind identity with
                | Some entry when entry.Features.Select = CommandOutputContract.ExistingBehavior -> None
                | Some _ -> Some(GraceError.Create $"Command '{identity.CommandId}' does not support --select in this release." correlationId)
                | None -> Some(GraceError.Create $"Command '{identity.CommandId}' does not have CLI output contract metadata for --select." correlationId)

    let private tryFindGraceConfigurationFileForJsonMode () =
        let rec loop (directory: DirectoryInfo) =
            if isNull directory then
                None
            else
                let candidate = Path.Combine(directory.FullName, Constants.GraceConfigDirectory, Constants.GraceConfigFileName)

                if File.Exists candidate then Some candidate else loop directory.Parent

        loop (DirectoryInfo(Environment.CurrentDirectory))

    let private tryGetJsonConfigurationError (parseResult: ParseResult) =
        if parseResult |> json then
            match tryFindGraceConfigurationFileForJsonMode () with
            | Some graceConfigurationFilePath ->
                try
                    use fileStream = new FileStream(graceConfigurationFilePath, FileMode.Open, FileAccess.Read, FileShare.Read)

                    JsonSerializer.Deserialize<GraceConfiguration>(fileStream, Constants.JsonSerializerOptions)
                    |> ignore

                    None
                with
                | ex -> Some(GraceError.CreateWithException ex ex.Message (getCorrelationId parseResult))
            | None -> None
        else
            None

    let private replaceDefaultValue (line: string) (defaultValueText: string) =
        let startIndex = line.IndexOf("[default:", StringComparison.OrdinalIgnoreCase)

        if startIndex >= 0 then
            let endIndex = line.IndexOf("]", startIndex)

            if endIndex > startIndex then
                $"{line.Substring(0, startIndex)}[default: {defaultValueText}]{line.Substring(endIndex + 1)}"
            else
                line
        else
            line

    let private rewriteHelpDefaults (helpText: string) (defaultsByAlias: IDictionary<string, string>) =
        let lines = helpText.Split(Environment.NewLine)
        let output = ResizeArray<string>(lines.Length)
        let mutable pendingAlias: string option = None
        let mutable i = 0

        while i < lines.Length do
            let line = lines[i]

            let matchedAlias =
                defaultsByAlias.Keys
                |> Seq.tryFind (fun alias -> line.Contains(alias))

            match matchedAlias with
            | Some alias -> pendingAlias <- Some alias
            | None -> ()

            match pendingAlias with
            | Some alias when line.Contains("[default:", StringComparison.OrdinalIgnoreCase) ->
                let startIndex = line.IndexOf("[default:", StringComparison.OrdinalIgnoreCase)
                let endIndex = line.IndexOf("]", startIndex)

                if endIndex > startIndex then
                    output.Add(replaceDefaultValue line defaultsByAlias[alias])
                    pendingAlias <- None
                    i <- i + 1
                else
                    let prefix = line.Substring(0, startIndex)
                    output.Add($"{prefix}[default: {defaultsByAlias[alias]}]")
                    pendingAlias <- None

                    let mutable j = i + 1
                    let mutable foundEnd = false

                    while j < lines.Length && not foundEnd do
                        let continuation = lines[j]
                        let continuationEndIndex = continuation.IndexOf("]")

                        if continuationEndIndex >= 0 then
                            let suffix =
                                if continuationEndIndex < continuation.Length - 1 then
                                    continuation.Substring(continuationEndIndex + 1)
                                else
                                    String.Empty

                            if not <| String.IsNullOrWhiteSpace(suffix) then output.Add(suffix)

                            foundEnd <- true

                        j <- j + 1

                    i <- j
            | Some alias when
                alias = OptionName.CorrelationId
                && line.Contains("CorrelationId")
                ->
                if
                    not
                    <| line.Contains("[default:", StringComparison.OrdinalIgnoreCase)
                then
                    output.Add($"{line} [default: {defaultsByAlias[alias]}]")
                else
                    output.Add(line)

                pendingAlias <- None
                i <- i + 1
            | _ ->
                output.Add(line)
                i <- i + 1

        String.Join(Environment.NewLine, output)

    type HelpSection = { Heading: string; CommandNames: string list }

    let private rootHelpSections =
        [
            { Heading = "Getting started"; CommandNames = [ "authenticate"; "connect"; "config" ] }
            {
                Heading = "Day-to-day development"
                CommandNames =
                    [
                        "branch"
                        "diff"
                        "directory-version"
                        "watch"
                    ]
            }
            {
                Heading = "Review and promotion"
                CommandNames =
                    [
                        "workitem"
                        "review"
                        "candidate"
                        "queue"
                        "promotion-set"
                        "approval"
                        "webhook"
                        "agent"
                    ]
            }
            {
                Heading = "Administration and authorization"
                CommandNames =
                    [
                        "owner"
                        "organization"
                        "repository"
                        "authorize"
                        "admin"
                    ]
            }
            {
                Heading = "Local utilities"
                CommandNames =
                    [
                        "doctor"
                        "history"
                        "maintenance"
                        "alias"
                    ]
            }
        ]

    let private repositoryHelpSections =
        [
            { Heading = "Create and initialize"; CommandNames = [ "create"; "init" ] }
            { Heading = "Inspect"; CommandNames = [ "get"; "get-branches" ] }
            {
                Heading = "Configuration"
                CommandNames =
                    [
                        "set-visibility"
                        "set-status"
                        "set-anonymous-access"
                        "set-allows-large-files"
                        "set-record-saves"
                        "set-default-server-api-version"
                        "set-save-days"
                        "set-checkpoint-days"
                        "set-diff-cache-days"
                        "set-directory-version-cache-days"
                        "set-logical-delete-days"
                        "set-name"
                        "set-description"
                        "set-conflict-resolution-policy"
                    ]
            }
            { Heading = "Lifecycle"; CommandNames = [ "delete"; "undelete" ] }
        ]

    let private branchHelpSections =
        [
            {
                Heading = "Create and contribute"
                CommandNames =
                    [
                        "create"
                        "commit"
                        "checkpoint"
                        "save"
                        "tag"
                        "create-external"
                    ]
            }
            { Heading = "Promotion workflow"; CommandNames = [ "promote"; "assign"; "rebase" ] }
            {
                Heading = "Inspect"
                CommandNames =
                    [
                        "status"
                        "annotate"
                        "list-contents"
                        "get-recursive-size"
                        "get"
                        "get-references"
                        "get-promotions"
                        "get-commits"
                        "get-checkpoints"
                        "get-saves"
                        "get-tags"
                        "get-externals"
                    ]
            }
            {
                Heading = "Settings"
                CommandNames =
                    [
                        "enable-assign"
                        "enable-promotion"
                        "enable-commit"
                        "enable-checkpoints"
                        "enable-save"
                        "enable-tag"
                        "enable-external"
                        "enable-auto-rebase"
                        "set-promotion-mode"
                        "set-name"
                        "update-parent-branch"
                    ]
            }
            { Heading = "Lifecycle"; CommandNames = [ "delete" ] }
        ]

    let private ownerHelpSections =
        [
            { Heading = "Create and inspect"; CommandNames = [ "create"; "get" ] }
            {
                Heading = "Settings"
                CommandNames =
                    [
                        "set-name"
                        "set-type"
                        "set-search-visibility"
                        "set-description"
                    ]
            }
            { Heading = "Lifecycle"; CommandNames = [ "delete"; "undelete" ] }
        ]

    let private organizationHelpSections =
        [
            { Heading = "Create and inspect"; CommandNames = [ "create"; "get" ] }
            {
                Heading = "Settings"
                CommandNames =
                    [
                        "set-name"
                        "set-type"
                        "set-search-visibility"
                        "set-description"
                    ]
            }
            { Heading = "Lifecycle"; CommandNames = [ "delete"; "undelete" ] }
        ]

    let private workItemHelpSections =
        [
            { Heading = "Create and update"; CommandNames = [ "create"; "show"; "status" ] }
            {
                Heading = "Link and attach"
                CommandNames =
                    [
                        "link"
                        "attach"
                        "attachments"
                        "links"
                    ]
            }
        ]

    let private groupedHelpSectionsByCommandName =
        let lookup = Dictionary<string, HelpSection list>(StringComparer.InvariantCultureIgnoreCase)
        lookup["repository"] <- repositoryHelpSections
        lookup["repo"] <- repositoryHelpSections
        lookup["branch"] <- branchHelpSections
        lookup["br"] <- branchHelpSections
        lookup["owner"] <- ownerHelpSections
        lookup["organization"] <- organizationHelpSections
        lookup["org"] <- organizationHelpSections
        lookup["workitem"] <- workItemHelpSections
        lookup["work"] <- workItemHelpSections
        lookup["work-item"] <- workItemHelpSections
        lookup["wi"] <- workItemHelpSections
        lookup

    let private formatDisplayName (command: Command) =
        let aliases =
            command.Aliases
            |> Seq.filter (fun alias ->
                not
                <| alias.Equals(command.Name, StringComparison.InvariantCultureIgnoreCase))
            |> Seq.distinctBy (fun alias -> alias.ToLowerInvariant())
            |> Seq.toArray

        if aliases.Length = 0 then
            command.Name
        else
            let aliasText = String.Join(", ", aliases)
            $"{command.Name} ({aliasText})"

    let private getGroupedCommands (command: Command) (sections: HelpSection list) =
        let lookup = Dictionary<string, Command>(StringComparer.InvariantCultureIgnoreCase)

        command.Subcommands
        |> Seq.cast<Command>
        |> Seq.iter (fun command -> lookup[command.Name] <- command)

        let mappedNames = HashSet<string>(StringComparer.InvariantCultureIgnoreCase)

        let sections =
            sections
            |> List.choose (fun section ->
                let commands =
                    section.CommandNames
                    |> List.choose (fun name ->
                        match lookup.TryGetValue(name) with
                        | true, command ->
                            mappedNames.Add(command.Name) |> ignore
                            Some command
                        | _ -> None)

                if commands.IsEmpty then None else Some(section.Heading, commands))

        let unmapped =
            command.Subcommands
            |> Seq.cast<Command>
            |> Seq.filter (fun command -> not <| mappedNames.Contains(command.Name))
            |> Seq.sortBy (fun command -> command.Name)
            |> Seq.toList

        sections, unmapped

    let private buildGroupedCommandLines (command: Command) (sections: HelpSection list) (indent: string) =
        let sections, unmapped = getGroupedCommands command sections
        let allCommands = (sections |> List.collect snd) @ unmapped

        let maxNameWidth =
            allCommands
            |> Seq.map formatDisplayName
            |> Seq.fold (fun current name -> max current name.Length) 0

        let lines = ResizeArray<string>()
        lines.Add($"{indent}Commands:")
        lines.Add(String.Empty)

        let writeSection heading commands =
            lines.Add($"{indent}  {heading}:")

            for command in commands do
                let name = formatDisplayName command
                let description = command.Description

                if String.IsNullOrWhiteSpace(description) then
                    lines.Add($"{indent}    {name}")
                else
                    lines.Add($"{indent}    {name.PadRight(maxNameWidth)}  {description}")

            lines.Add(String.Empty)

        for (heading, commands) in sections do
            writeSection heading commands

        if not unmapped.IsEmpty then writeSection "Other" unmapped

        lines.ToArray()

    let private stripAnsi (text: string) =
        if String.IsNullOrEmpty(text) then
            text
        else
            let withoutCsi = Regex.Replace(text, "\x1B\\[[0-9;?]*[A-Za-z]", String.Empty)
            Regex.Replace(withoutCsi, "\x1B\\][^\x07]*\x07", String.Empty)

    let private rewriteHelpCommands (helpText: string) (command: Command) (sections: HelpSection list) =
        if command.Subcommands.Count = 0 then
            helpText
        else
            let normalizedHelpText = helpText.Replace("\r\n", "\n")
            let lines = normalizedHelpText.Split('\n')

            let commandHeaderIndex =
                lines
                |> Array.tryFindIndex (fun line ->
                    let cleanedLine = stripAnsi line

                    cleanedLine
                        .Trim()
                        .Equals("Commands:", StringComparison.Ordinal))

            match commandHeaderIndex with
            | None -> helpText
            | Some startIndex ->
                let mutable endIndex = lines.Length
                let mutable i = startIndex + 1

                while i < lines.Length && endIndex = lines.Length do
                    let line = stripAnsi lines[i]

                    if
                        (not <| String.IsNullOrWhiteSpace(line))
                        && not (line.StartsWith(" "))
                    then
                        endIndex <- i
                    else
                        i <- i + 1

                let before = if startIndex > 0 then lines[0 .. startIndex - 1] else Array.empty

                let after = if endIndex < lines.Length then lines[endIndex..] else Array.empty

                let headerLine = stripAnsi lines[startIndex]

                let indentLength = headerLine.IndexOf("Commands:", StringComparison.Ordinal)

                let indent = if indentLength > 0 then headerLine.Substring(0, indentLength) else String.Empty

                let grouped = buildGroupedCommandLines command sections indent

                String.Join(Environment.NewLine, Array.concat [ before; grouped; after ])

    let private findTargetHelpCommandResult (commandResult: CommandResult) =
        let mutable current = commandResult
        let mutable searching = true

        while searching do
            match current.Children.OfType<CommandResult>()
                  |> Seq.tryLast
                with
            | Some childResult -> current <- childResult
            | None -> searching <- false

        current

    let rootCommand =
        // Create the root of the command tree.
        let rootCommand = new RootCommand("Grace Version Control System")

        // Turning this off means getting much more flexible in our input handling, and that's not happening for a while.
        rootCommand.TreatUnmatchedTokensAsErrors <- true

        // Create global options - these appear on every command in the system.
        rootCommand.Options.Add(Options.correlationId)
        rootCommand.Options.Add(Options.source)
        rootCommand.Options.Add(Options.output)
        rootCommand.Options.Add(Options.schema)
        rootCommand.Options.Add(Options.examples)
        rootCommand.Options.Add(Options.select)

        // Add subcommands.
        rootCommand.Subcommands.Add(Connect.Build)
        rootCommand.Subcommands.Add(Watch.Build)
        rootCommand.Subcommands.Add(Branch.Build)
        rootCommand.Subcommands.Add(DirectoryVersion.Build)
        rootCommand.Subcommands.Add(Diff.Build)
        rootCommand.Subcommands.Add(Repository.Build)
        rootCommand.Subcommands.Add(Organization.Build)
        rootCommand.Subcommands.Add(Owner.Build)
        rootCommand.Subcommands.Add(Config.Build)
        rootCommand.Subcommands.Add(History.Build)
        rootCommand.Subcommands.Add(Auth.Build)
        rootCommand.Subcommands.Add(Maintenance.Build)
        rootCommand.Subcommands.Add(Doctor.Build)
        rootCommand.Subcommands.Add(WorkItemCommand.Build)
        rootCommand.Subcommands.Add(ReviewCommand.Build)
        rootCommand.Subcommands.Add(CandidateCommand.Build)
        rootCommand.Subcommands.Add(QueueCommand.Build)
        rootCommand.Subcommands.Add(WebhookCommand.Build)
        rootCommand.Subcommands.Add(ApprovalCommand.Build)
        rootCommand.Subcommands.Add(PromotionSetCommand.Build)
        rootCommand.Subcommands.Add(AgentCommand.Build)
        rootCommand.Subcommands.Add(Admin.Build)
        rootCommand.Subcommands.Add(Access.Build)

        let Alias = Command("alias", "Display aliases for Grace commands.")
        let ListAliases = Command("list", "Display aliases for Grace commands.")
        ListAliases.SetAction(fun parseResult -> renderAliases parseResult)
        Alias.Subcommands.Add(ListAliases)
        rootCommand.Subcommands.Add(Alias)
        rootCommand

    ///// Converts tokens to the exact casing defined in their options, enabling case-insensitive parsing on Windows.
    //let caseInsensitiveMiddleware (rootCommand: Command, isCaseInsensitive) =
    //    if isCaseInsensitive then
    //        let parseResult = rootCommand.Parse()
    //        let commandOptions = rootCommand.Options

    //        // Collect all of the aliases for all of the options.
    //        let allAliases = commandOptions |> Seq.collect (fun option -> option.Aliases)

    //        /// Finds tokens that are either aliases for options, or pre-defined valid values for an option (i.e. completions), and converts them to the exact case found in the option definitions.
    //        let getCorrectTokenCase (tokens: string array) =
    //            /// The case-corrected list of tokens.
    //            let newTokens = List<string>(tokens.Length)

    //            // Loop through every token.
    //            for i = 0 to (tokens.Length - 1) do
    //                let token = tokens[i]
    //                // First, check if this is an alias for an option.
    //                match
    //                    allAliases
    //                    |> Seq.tryFind (fun alias -> alias.Equals(token, StringComparison.InvariantCultureIgnoreCase))
    //                with
    //                | Some alias ->
    //                    // We found an alias, so we'll use the alias from the option definition to get the case exactly right.
    //                    newTokens.Add(alias)
    //                | None ->
    //                    // This is either a value, or just an invalid option we didn't find.
    //                    // If it's a known value (i.e. completion) for an option, we want to use the completion value defined in the option to get the case right.
    //                    // If we don't recognize it, we'll just leave it as-is.
    //                    if i > 0 then
    //                        // Check if the previous token is an option. If it is, there's a chance it has completions.
    //                        let previousToken = tokens[i - 1]

    //                        match
    //                            commandOptions
    //                            |> Seq.tryFind (fun option -> option.Aliases.Contains(previousToken, StringComparer.InvariantCultureIgnoreCase))
    //                        with
    //                        | Some option ->
    //                            // We found an option for the previous token, so we'll check if it has completions.
    //                            let dlk = Completions.CompletionContext.Empty

    //                            let completions =
    //                                option.GetCompletions(parseResult.GetCompletionContext())
    //                                |> Seq.map (fun completion -> completion.InsertText)
    //                                |> Seq.toArray

    //                            // If we have completions, and this token is one of them, we'll use the actual completion value to get the case right.
    //                            if completions.Length > 0 then
    //                                newTokens.Add(
    //                                    completions.FirstOrDefault(
    //                                        (fun completion -> token.Equals(completion, StringComparison.InvariantCultureIgnoreCase)),
    //                                        token
    //                                    )
    //                                )
    //                            else
    //                                // There are no completions, so we'll just use the current token as-is.
    //                                newTokens.Add(token)
    //                        | None ->
    //                            // We didn't find an option for the previous token, so we'll just use the current token as-is.
    //                            newTokens.Add(token)
    //                    else
    //                        // This is the first token, so we'll just use it as-is. If this were a valid option, we would have matched it and returned `Some alias`.
    //                        newTokens.Add(token)

    //            newTokens.ToArray()

    //        // Get the text from all tokens on the command-line into an array.
    //        let tokens = parseResult.Tokens.Select(fun token -> token.Value).ToArray()

    //        // Convert the tokens to the correct case.
    //        let newTokens =
    //            match tokens.Count with
    //            | 0 ->
    //                // There are no tokens, so we'll just return an empty array.
    //                Array.empty<string>
    //            | 1 ->
    //                // This could be a command like `grace branch`, which is a valid request for help. I'll convert the token to lower-case to match Grace's command structure.
    //                [| tokens[0].ToLowerInvariant() |]
    //            | _ ->
    //                // We've already converted aliases to their noun-verb form, so we know the first two tokens should be converted to lower-case to match Grace's command structure.
    //                // The rest of the tokens are either options or values, and will be converted to their exact casing based on the option definitions.

    //                // In case someone typed `grace --some-option blah`, which is invalid, I want to leave it alone.
    //                if tokens[0].StartsWith("-") then
    //                    tokens
    //                else
    //                    // Convert the first two tokens to lower-case.
    //                    //Array.append [| tokens[0].ToLowerInvariant(); tokens[1].ToLowerInvariant() |] (getCorrectTokenCase tokens[2..])
    //                    Array.append [| tokens[0].ToLowerInvariant() |] (getCorrectTokenCase tokens[1..])

    //        // Replace the old ParseResult with one based on the updated tokens with exact casing.
    //        rootCommand.Parse(newTokens)
    //    else
    //        rootCommand.Parse()

    /// Checks if the command is a `grace watch` command.
    let isGraceWatch (parseResult: ParseResult) = if (parseResult.CommandResult.Command.Name = "watch") then true else false

    /// Checks if the command is a `grace doctor` command.
    let isGraceDoctor (parseResult: ParseResult) =
        if isNull parseResult then
            false
        else
            Seq.append [ parseResult.CommandResult.Command ] (parseResult.CommandResult.Command.Parents.OfType<Command>())
            |> Seq.exists (fun command -> command.Name.Equals("doctor", StringComparison.OrdinalIgnoreCase))

    /// Checks if the command is a foreground `grace watch` command.
    let isGraceWatchForeground (parseResult: ParseResult) =
        (parseResult |> isGraceWatch)
        && not (Command.Watch.isCheckRequested parseResult)

    /// Checks if the command is a `grace config` command.
    let isGraceConfig (parseResult: ParseResult) =
        if not <| isNull parseResult.CommandResult.Parent then
            if parseResult.CommandResult.Parent.GetValue("config") then true else false
        else
            false

    type FeedbackSection(action: HelpAction) =
        inherit SynchronousCommandLineAction()

        member _.HelpAction = action

        override _.Invoke(parseResult: ParseResult) =
            let result = action.Invoke(parseResult)
            AnsiConsole.WriteLine()
            AnsiConsole.WriteLine("More help and feedback:")

            AnsiConsole.WriteLine(
                "  For more help, or to give us feedback, please join us in Discussions, or create an issue in our repo, at https://github.com/scottarbeit/grace."
            )

            result

    /// This is the main entry point for Grace CLI.
    [<EntryPoint>]
    let main args =
        let startTime = getCurrentInstant ()

        // Create a MemoryCache instance.
        //let memoryCacheOptions = MemoryCacheOptions(TrackStatistics = false, TrackLinkedCacheEntries = false)
        //memoryCache <- new MemoryCache(memoryCacheOptions)

        /// True if the OS is case-insensitive (i.e. Windows), false otherwise.
        let isCaseInsensitive = Environment.OSVersion.Platform = PlatformID.Win32NT
        let argvOriginal = args |> Array.copy

        let normalizeArgsForHistory (args: string array) =
            if args.Length = 0 then
                Array.empty<string>
            else
                let normalizeOutputEqualsToken (arg: string) =
                    let outputEqualsPrefix = $"{OptionName.Output}="

                    if arg.StartsWith(outputEqualsPrefix, StringComparison.OrdinalIgnoreCase) then
                        $"{OptionName.Output}={arg.Substring(outputEqualsPrefix.Length)}"
                    else
                        arg

                let tryFindTopLevelCommandIndex (args: string array) =
                    let comparison =
                        if isCaseInsensitive then
                            StringComparison.InvariantCultureIgnoreCase
                        else
                            StringComparison.InvariantCulture

                    let isOptionWithValue (token: string) =
                        token.Equals(OptionName.Output, comparison)
                        || token.Equals("-o", comparison)
                        || token.Equals(OptionName.CorrelationId, comparison)
                        || token.Equals("-c", comparison)
                        || token.Equals(OptionName.Source, comparison)
                        || token.Equals(OptionName.Select, comparison)

                    let rec loop index =
                        if index >= args.Length then
                            None
                        else
                            let token = args[index]

                            if token = "--" then
                                if index + 1 < args.Length then Some(index + 1) else None
                            elif token.StartsWith("-", StringComparison.Ordinal) then
                                let nextIndex = if isOptionWithValue token then index + 2 else index + 1
                                loop nextIndex
                            else
                                Some index

                    loop 0

                let properCasedArgs =
                    args
                    |> Array.map (fun arg ->
                        let normalizedArg = normalizeOutputEqualsToken arg

                        if
                            isCaseInsensitive
                            && normalizedArg.StartsWith("--")
                        then
                            let equalsIndex = normalizedArg.IndexOf("=", StringComparison.Ordinal)

                            if equalsIndex > 0 then
                                let optionName =
                                    normalizedArg
                                        .Substring(0, equalsIndex)
                                        .ToLowerInvariant()

                                let optionValue = normalizedArg.Substring(equalsIndex)
                                $"{optionName}{optionValue}"
                            else
                                normalizedArg.ToLowerInvariant()
                        else
                            normalizedArg)

                match tryFindTopLevelCommandIndex args with
                | Some commandIndex ->
                    let aliasToken =
                        if isCaseInsensitive then
                            args[ commandIndex ].ToLowerInvariant()
                        else
                            args[commandIndex]

                    if aliases.ContainsKey(aliasToken) then
                        let newArgs = List<string>()

                        for token in properCasedArgs[0 .. commandIndex - 1] do
                            newArgs.Add(token)

                        for token in aliases[aliasToken] do
                            newArgs.Add(token)

                        for token in properCasedArgs[commandIndex + 1 ..] do
                            newArgs.Add(token)

                        newArgs.ToArray()
                    else
                        properCasedArgs
                | None -> properCasedArgs

        let mutable argvNormalized = normalizeArgsForHistory args

        (task {
            let mutable parseResult: ParseResult = null
            let mutable returnValue: int = 0
            let mutable parseSucceeded: bool = false
            let mutable isIntrospection: bool = false

            try
                try
                    Auth.configureSdkAuth ()
                    Services.configureSdkClientIdentity ()
                    Services.resetInvocationCorrelationId ()
                    Common.resetLifecycleWarningSuppression ()

                    //let commandLineConfiguration = ParserConfiguration rootCommand

                    let argvToParse = if args.Length = 0 then [| helpOptions[0] |] else argvNormalized

                    parseResult <- rootCommand.Parse(argvToParse)
                    parseSucceeded <- parseResult.Errors.Count = 0

                    match introspectionRequestFromTokens argvToParse with
                    | Some (Ok kind) ->
                        isIntrospection <- true
                        Services.parseResult <- parseResult

                        returnValue <-
                            if hasBlockingIntrospectionParseErrors parseResult then
                                writeIntrospectionParseError parseResult
                            else
                                handleIntrospectionRequest parseResult kind

                        raise (IntrospectionExit returnValue)
                    | Some (Error message) ->
                        isIntrospection <- true
                        Services.parseResult <- parseResult
                        returnValue <- writeIntrospectionError parseResult message
                        raise (IntrospectionExit returnValue)
                    | None ->
                        // Write the ParseResult to Services as global context for the CLI.
                        Services.parseResult <- parseResult

                        if parseResult.Errors.Count > 0
                           && isJsonOutputRequestedFromTokens argvToParse then
                            returnValue <- writeJsonParseError parseResult
                            raise (IntrospectionExit returnValue)

                        if parseResult.Errors.Count = 0 then
                            match tryValidateSelectRequest parseResult with
                            | Some error ->
                                Common.writeJsonErrorStdout error
                                returnValue <- -1
                                raise (IntrospectionExit returnValue)
                            | None -> ()

                        if not (parseResult |> isGraceDoctor) then
                            LocalStateDb.setVerbose (parseResult |> verbose)

                    let helpAction =
                        match parseResult.Action with
                        | :? HelpAction as action -> Some action
                        | :? FeedbackSection as feedback -> Some feedback.HelpAction
                        | _ -> None

                    let hasHelpToken =
                        argvNormalized
                        |> Array.takeWhile (fun arg -> not <| arg.Equals("--", StringComparison.Ordinal))
                        |> Array.exists (fun arg -> helpOptions.Contains(arg))

                    let isHelpRequest =
                        helpAction.IsSome
                        || args.Length = 0
                        || hasHelpToken

                    if isHelpRequest then
                        let helpCommandResult = findTargetHelpCommandResult parseResult.CommandResult
                        let helpCommand = helpCommandResult.Command

                        let isRootHelp =
                            obj.ReferenceEquals(helpCommand, rootCommand)
                            || helpCommand :? RootCommand

                        let groupedHelpSections =
                            if isRootHelp then
                                Some rootHelpSections
                            else
                                match groupedHelpSectionsByCommandName.TryGetValue(helpCommand.Name) with
                                | true, sections -> Some sections
                                | _ -> None

                        let groupedHelpCommand, groupedHelpSections =
                            match groupedHelpSections with
                            | Some sections -> helpCommand, Some sections
                            | None ->
                                match tryGetTopLevelCommandFromArgs argvNormalized isCaseInsensitive with
                                | Some commandName ->
                                    match groupedHelpSectionsByCommandName.TryGetValue(commandName) with
                                    | true, sections ->
                                        let comparison =
                                            if isCaseInsensitive then
                                                StringComparison.InvariantCultureIgnoreCase
                                            else
                                                StringComparison.InvariantCulture

                                        let command =
                                            rootCommand.Subcommands
                                            |> Seq.cast<Command>
                                            |> Seq.tryFind (fun command ->
                                                command.Name.Equals(commandName, comparison)
                                                || command.Aliases
                                                   |> Seq.exists (fun alias -> alias.Equals(commandName, comparison)))

                                        match command with
                                        | Some command -> command, Some sections
                                        | None -> helpCommand, None
                                    | _ -> helpCommand, None
                                | None -> helpCommand, None

                        if isRootHelp
                           && (parseResult.Tokens.Count = 0
                               || (parseResult.Tokens.Count = 1
                                   && helpOptions.Contains(parseResult.Tokens[0].Value))) then
                            // This is where we configure how help is displayed by Grace.
                            // We want to show the help text for the command, and then the feedback section at the end.
                            let graceFiglet = FigletText($"Grace")
                            graceFiglet.Justification <- Justify.Center
                            graceFiglet.Color <- Color.Green3_1
                            AnsiConsole.Write(graceFiglet)
                            let graceFiglet = FigletText($"Version Control System")
                            graceFiglet.Justification <- Justify.Center
                            graceFiglet.Color <- Color.DarkOrange
                            AnsiConsole.Write(graceFiglet)
                            AnsiConsole.WriteLine()

                            for i in 0 .. rootCommand.Options.Count - 1 do
                                match rootCommand.Options[i] with
                                | :? HelpOption as defaultHelpOption -> defaultHelpOption.Action <- FeedbackSection(defaultHelpOption.Action :?> HelpAction)
                                | _ -> ()

                        //helpAction.Builder.CustomizeLayout(fun layoutContext ->
                        //    HelpBuilder.Default
                        //        .GetLayout()
                        //        .Where(fun section ->
                        //            not
                        //            <| section.Method.Name.Contains("Synopsis", StringComparison.InvariantCultureIgnoreCase))
                        //        .Append(feedbackSection))

                        // We're passing a new List<Option> here, because we're going to be adding to it recursively in gatherAllOptions.
                        let allOptions = gatherAllOptions helpCommand (List<Option>())

                        // This section sets the display of the default value for these options in all commands in Grace CLI.
                        // Without setting the display values here, by default, we'd get something like
                        //   "[default: thing-we-said-in-the-Option-definition] [default:e4def31b-4547-4f6b-9324-56eba666b4b2]"
                        //   i.e. whatever the generated Guid value on create might be.
                        let optionsToUpdate =
                            [
                                {
                                    optionAlias = OptionName.CorrelationId
                                    display = "new NanoId"
                                    displayOnCreate = "new NanoId"
                                    createParentCommand = String.Empty
                                }
                                { optionAlias = OptionName.OwnerId; display = "current OwnerId"; displayOnCreate = "new Guid"; createParentCommand = "owner" }
                                {
                                    optionAlias = OptionName.OrganizationId
                                    display = "current OrganizationId"
                                    displayOnCreate = "new Guid"
                                    createParentCommand = "organization"
                                }
                                {
                                    optionAlias = OptionName.RepositoryId
                                    display = "current RepositoryId"
                                    displayOnCreate = "new Guid"
                                    createParentCommand = "repository"
                                }
                                {
                                    optionAlias = OptionName.BranchId
                                    display = "current BranchId"
                                    displayOnCreate = "new Guid"
                                    createParentCommand = "branch"
                                }
                            ]

                        let isCreate = helpCommand.Name.Equals("create", StringComparison.OrdinalIgnoreCase)

                        let parentCommands = helpCommand.Parents.OfType<Command>()

                        let defaultsByAlias =
                            optionsToUpdate
                            |> Seq.map (fun optionToUpdate ->
                                let useCreateDisplay =
                                    isCreate
                                    && not
                                       <| String.IsNullOrWhiteSpace(optionToUpdate.createParentCommand)
                                    && parentCommands.Any (fun parent ->
                                        parent.Name.Equals(optionToUpdate.createParentCommand, StringComparison.OrdinalIgnoreCase))

                                let defaultValueText =
                                    if useCreateDisplay then
                                        optionToUpdate.displayOnCreate
                                    else
                                        optionToUpdate.display

                                optionToUpdate.optionAlias, defaultValueText)
                            |> dict

                        use writer = new StringWriter()
                        let originalOut = Console.Out

                        let invokeResult =
                            try
                                Console.SetOut(writer)
                                parseResult.Invoke()
                            finally
                                Console.SetOut(originalOut)

                        let helpText = writer.ToString()
                        let rewrittenHelpText = rewriteHelpDefaults helpText defaultsByAlias

                        let finalHelpText =
                            match groupedHelpSections with
                            | Some sections -> rewriteHelpCommands rewrittenHelpText groupedHelpCommand sections
                            | None -> rewrittenHelpText

                        Console.Write(finalHelpText)
                        returnValue <- invokeResult
                    else if parseResult |> isGraceDoctor then
                        let invokedReturnValue = parseResult.Invoke()
                        returnValue <- invokedReturnValue
                    else if configurationFileExists () then
                        match tryGetJsonConfigurationError parseResult with
                        | Some error ->
                            Common.writeJsonErrorStdout error
                            returnValue <- -1
                        | None ->
                            //parseResult <- caseInsensitiveMiddleware (rootCommand, parseResult, isCaseInsensitive)

                            if (parseResult |> json)
                               && (parseResult |> isGraceWatch) then
                                let error =
                                    GraceError.Create
                                        "watch is a continuous foreground workflow; JSON mode requires a cancellation-aware automation host in this release."
                                        (getCorrelationId parseResult)

                                Common.writeJsonErrorStdout error
                                returnValue <- -1
                            else
                                if parseResult |> hasOutput then
                                    if parseResult |> verbose then
                                        AnsiConsole.Write(
                                            (new Rule($"[{Colors.Important}]Started: {formatInstantExtended startTime}.[/]"))
                                                .RightJustified()
                                        )

                                    //printParseResult parseResult
                                    else
                                        AnsiConsole.Write(new Rule())

                                // If this instance isn't foreground `grace watch`, we want to check if `grace watch` is running by trying to read the IPC file.
                                if not <| (parseResult |> isGraceWatchForeground) then
                                    let! graceWatchStatus = getGraceWatchStatus ()

                                    match graceWatchStatus with
                                    | Some status -> Configuration.updateConfiguration { GraceWatchStatus = status }
                                    | None -> ()

                                // Now we can invoke the command!
                                let! invokedReturnValue = parseResult.InvokeAsync()
                                returnValue <- invokedReturnValue

                                // Stuff to do after the command has been invoked:

                                // If this instance is foreground `grace watch`, we'll actually delete the IPC file in the finally clause below, but
                                //   we'll write the "we deleted the file" message to the console here, so it comes before the last Rule() is written.
                                if parseResult |> isGraceWatchForeground then
                                    logToAnsiConsole Colors.Important (getLocalizedString StringResourceName.InterprocessFileDeleted)

                                // If we're writing output, write the final Rule() to the console.
                                if parseResult |> hasOutput then
                                    let finishTime = getCurrentInstant ()

                                    let elapsed =
                                        (finishTime - startTime)
                                            .Plus(Duration.FromMilliseconds(110.0)) // Adding 110ms for .NET Runtime startup time.

                                    if parseResult |> verbose then
                                        AnsiConsole.Write(
                                            (new Rule(
                                                $"[{Colors.Important}]Elapsed: {elapsed.TotalSeconds:F3}s. Exit code: {returnValue}. Finished: {formatInstantExtended finishTime}[/]"
                                            ))
                                                .RightJustified()
                                        )
                                    else
                                        AnsiConsole.Write(
                                            (new Rule($"[{Colors.Important}]Elapsed: {elapsed.TotalSeconds:F3}s. Exit code: {returnValue}.[/]"))
                                                .RightJustified()
                                        )

                                    AnsiConsole.WriteLine()
                    else
                        // We don't have a config file, so write an error message and exit.
                        let comparison =
                            if isCaseInsensitive then
                                StringComparison.InvariantCultureIgnoreCase
                            else
                                StringComparison.InvariantCulture

                        let allowedCommands =
                            [
                                "config"
                                "history"
                                "authenticate"
                                "connect"
                                "alias"
                                "doctor"
                            ]

                        let isAllowed =
                            let command = parseResult.CommandResult.Command

                            Seq.append [ command ] (command.Parents.OfType<Command>())
                            |> Seq.exists (fun cmd ->
                                allowedCommands
                                |> List.exists (fun allowed -> cmd.Name.Equals(allowed, comparison)))
                            || (tryGetTopLevelCommandFromArgs argvNormalized isCaseInsensitive
                                |> Option.exists (fun topLevel ->
                                    allowedCommands
                                    |> List.exists (fun allowed -> topLevel.Equals(allowed, comparison))))

                        if isAllowed then
                            let! invokedReturnValue = parseResult.InvokeAsync()
                            returnValue <- invokedReturnValue
                            ()
                        else
                            let message = getLocalizedString StringResourceName.GraceConfigFileNotFound

                            if parseResult |> json then
                                let error = GraceError.Create message (getCorrelationId parseResult)
                                Common.writeJsonErrorStdout error
                            else
                                AnsiConsole.Write(new Rule())
                                AnsiConsole.MarkupLine($"[{Colors.Important}]{message}[/]")

                                let finishTime = getCurrentInstant ()

                                let elapsed =
                                    (finishTime - startTime)
                                        .Plus(Duration.FromMilliseconds(110.0)) // Adding 110ms for .NET Runtime startup time.

                                AnsiConsole.Write(
                                    (new Rule($"[{Colors.Important}]Elapsed: {elapsed.TotalSeconds:F3}s. Exit code: -1.[/]"))
                                        .RightJustified()
                                )

                            returnValue <- -1

                    return returnValue
                with
                | IntrospectionExit exitCode -> return exitCode
                | ex ->
                    if isJsonOutputRequestedFromTokens argvNormalized then
                        returnValue <- writeJsonException parseResult ex
                    else
                        AnsiConsole.WriteException ex
                        //logToAnsiConsole Colors.Error $"ex.Message: {ex.Message}"
                        //logToAnsiConsole Colors.Error $"{ex.StackTrace}"
                        returnValue <- -1

                    return returnValue
            finally
                let finishTime = getCurrentInstant ()

                let durationMs =
                    (finishTime - startTime).TotalMilliseconds
                    |> int64

                if
                    not isIntrospection
                    && not (parseResult |> isGraceDoctor)
                then
                    HistoryStorage.tryRecordInvocation
                        {
                            argvOriginal = argvOriginal
                            argvNormalized = argvNormalized
                            cwd = Environment.CurrentDirectory
                            exitCode = returnValue
                            durationMs = durationMs
                            parseSucceeded = parseSucceeded
                            timestampUtc = startTime
                            source = resolveInvocationSource parseResult
                        }

                // If this was foreground grace watch, delete the inter-process communication file.
                if not <| isNull (parseResult)
                   && parseResult |> isGraceWatchForeground then
                    deleteGraceWatchIpcFileIfOwned ()
        })
            .Result
