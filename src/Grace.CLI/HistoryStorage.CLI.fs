namespace Grace.CLI

open Grace.CLI.Text
open Grace.Shared
open Grace.Shared.Client
open Grace.Shared.Utilities
open NodaTime
open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading

/// Groups the history storage command parser, handlers, and output helpers.
module HistoryStorage =

    [<Literal>]
    let Placeholder = "__REDACTED__"

    let private jsonlOptions =
        let options = JsonSerializerOptions(Constants.JsonSerializerOptions)
        options.WriteIndented <- false
        options

    /// Defines structured data exchanged by CLI helpers.
    type Redaction = { kind: string; name: string; argIndex: int; originalLength: int option; placeholder: string }

    /// Models history entry values passed between the parser and history storage handlers.
    type HistoryEntry =
        {
            id: Guid
            timestampUtc: Instant
            argvOriginal: string array
            argvNormalized: string array
            commandLine: string
            cwd: string
            repoRoot: string option
            repoName: string option
            repoBranch: string option
            graceVersion: string
            exitCode: int
            durationMs: int64
            parseSucceeded: bool
            redactions: Redaction list
            source: string option
        }

    /// Defines structured data exchanged by CLI helpers.
    type ReadResult = { Entries: HistoryEntry list; CorruptCount: int }

    /// Models record input values passed between the parser and history storage handlers.
    type RecordInput =
        {
            argvOriginal: string array
            argvNormalized: string array
            cwd: string
            exitCode: int
            durationMs: int64
            parseSucceeded: bool
            timestampUtc: Instant
            source: string option
        }

    let private lockBackoffMs =
        [|
            25
            50
            100
            150
            200
            250
            300
            400
            500
            750
        |]

    /// Reads history file path from ParseResult, local configuration, or Grace ids.
    let getHistoryFilePath () =
        let userGraceDir = UserConfiguration.getUserGraceDirectory ()
        Path.Combine(userGraceDir, "history.jsonl")

    /// Reads history lock path from ParseResult, local configuration, or Grace ids.
    let getHistoryLockPath () =
        let userGraceDir = UserConfiguration.getUserGraceDirectory ()
        Path.Combine(userGraceDir, "history.lock")

    /// Ensures required command context is present.
    let private ensureHistoryDirectory () =
        UserConfiguration.ensureUserGraceDirectory ()
        |> ignore

    /// Reads grace version from ParseResult, local configuration, or Grace ids.
    let private getGraceVersion () =
        try
            BuildInfo.current().InformationalVersion
        with
        | _ -> Constants.CurrentConfigurationVersion

    /// Tries to map acquire lock and returns a GraceError instead of throwing on unsupported input.
    let private tryAcquireLock () =
        ensureHistoryDirectory ()
        let lockPath = getHistoryLockPath ()
        let mutable acquired: FileStream option = None

        for attempt in 0 .. lockBackoffMs.Length - 1 do
            if acquired.IsNone then
                try
                    let stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

                    acquired <- Some stream
                with
                | :? IOException -> Thread.Sleep(lockBackoffMs[attempt])

        acquired

    /// Shapes local command-history with history lock data for persistence or display.
    let private withHistoryLock (onLocked: unit -> 'T) (onFailure: unit -> 'T) =
        match tryAcquireLock () with
        | Some stream ->
            try
                onLocked ()
            finally
                stream.Dispose()
        | None -> onFailure ()

    /// Shapes local command-history quote arg data for persistence or display.
    let private quoteArg (arg: string) =
        if String.IsNullOrEmpty(arg) then
            "\"\""
        elif arg.IndexOfAny([| ' '; '\t'; '"' |]) >= 0 then
            "\"" + arg.Replace("\"", "\\\"") + "\""
        else
            arg

    /// Builds command objects or parameters for execution.
    let buildCommandLine (argv: string array) = argv |> Array.map quoteArg |> String.concat " "

    /// Tries to map find repo root and returns a GraceError instead of throwing on unsupported input.
    let tryFindRepoRoot (startDirectory: string) =
        try
            let mutable current = DirectoryInfo(startDirectory)
            let mutable found: string option = None

            while (not <| isNull current) && found.IsNone do
                let configPath = Path.Combine(current.FullName, Constants.GraceConfigDirectory, Constants.GraceConfigFileName)

                if File.Exists(configPath) then
                    found <- Some current.FullName
                else
                    current <- current.Parent

            found
        with
        | _ -> None

    /// Tries to map parse duration and returns a GraceError instead of throwing on unsupported input.
    let tryParseDuration (value: string) =
        if String.IsNullOrWhiteSpace(value) then
            Error "Duration cannot be empty."
        else
            let trimmed = value.Trim()
            let suffix = trimmed[trimmed.Length - 1]
            let numberPart = trimmed.Substring(0, trimmed.Length - 1)

            match Double.TryParse(numberPart) with
            | true, amount ->
                match suffix with
                | 's' -> Ok(Duration.FromSeconds(amount))
                | 'm' -> Ok(Duration.FromMinutes(amount))
                | 'h' -> Ok(Duration.FromHours(amount))
                | 'd' -> Ok(Duration.FromDays(amount))
                | _ -> Error "Duration must end with s, m, h, or d."
            | _ -> Error "Duration must be a number followed by s, m, h, or d."

    /// Tries to map get repo name and returns a GraceError instead of throwing on unsupported input.
    let private tryGetRepoName (repoRoot: string option) =
        match repoRoot with
        | Some root when not <| String.IsNullOrWhiteSpace(root) ->
            try
                let name = DirectoryInfo(root).Name
                if String.IsNullOrWhiteSpace(name) then None else Some name
            with
            | _ -> None
        | _ -> None

    /// Evaluates has git metadata against parsed options and command state.
    let private hasGitMetadata (repoRoot: string) =
        let gitPath = Path.Combine(repoRoot, ".git")
        Directory.Exists(gitPath) || File.Exists(gitPath)

    /// Tries to map get git branch and returns a GraceError instead of throwing on unsupported input.
    let private tryGetGitBranch (repoRoot: string option) =
        match repoRoot with
        | Some root when
            not <| String.IsNullOrWhiteSpace(root)
            && hasGitMetadata root
            ->
            try
                let startInfo = ProcessStartInfo()
                startInfo.FileName <- "git"
                startInfo.Arguments <- "rev-parse --abbrev-ref HEAD"
                startInfo.WorkingDirectory <- root
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                startInfo.UseShellExecute <- false
                startInfo.CreateNoWindow <- true

                use proc = new Process()
                proc.StartInfo <- startInfo

                if proc.Start() then
                    if proc.WaitForExit(2000) then
                        let output = proc.StandardOutput.ReadToEnd().Trim()

                        if
                            proc.ExitCode = 0
                            && not <| String.IsNullOrWhiteSpace(output)
                            && not (output.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                        then
                            Some output
                        else
                            None
                    else
                        try
                            proc.Kill(true)
                        with
                        | _ -> ()

                        None
                else
                    None
            with
            | _ -> None
        | _ -> None

    /// Builds command objects or parameters for execution.
    let private buildSensitiveOptionSet (historyConfig: UserConfiguration.HistoryConfiguration) =
        let names = HashSet<string>(StringComparer.InvariantCultureIgnoreCase)

        for name in (UserConfiguration.defaultRedactOptionNames ()) do
            names.Add(name) |> ignore

        if not <| isNull historyConfig.RedactOptionNames then
            for name in historyConfig.RedactOptionNames do
                if not <| String.IsNullOrWhiteSpace(name) then names.Add(name.Trim()) |> ignore

        names

    /// Builds command objects or parameters for execution.
    let private buildRegexes (patterns: string array) =
        let regexes = ResizeArray<Regex>()

        if not <| isNull patterns then
            for pattern in patterns do
                if not <| String.IsNullOrWhiteSpace(pattern) then
                    try
                        regexes.Add(
                            Regex(
                                pattern,
                                RegexOptions.Compiled
                                ||| RegexOptions.CultureInvariant,
                                TimeSpan.FromSeconds(1.0)
                            )
                        )
                    with
                    | _ -> ()

        regexes |> Seq.toList

    /// Shapes local command-history redact options data for persistence or display.
    let private redactOptions (args: string array) (sensitiveOptions: HashSet<string>) =
        let redactions = ResizeArray<Redaction>()
        let redacted = Array.copy args

        let mutable i = 0

        while i < redacted.Length do
            let current = redacted[i]

            if
                not <| String.IsNullOrWhiteSpace(current)
                && current.StartsWith("--")
            then
                let optionPart = current.Substring(2)
                let equalsIndex = optionPart.IndexOf('=')

                if equalsIndex >= 0 then
                    let optionName = optionPart.Substring(0, equalsIndex)
                    let optionValue = optionPart.Substring(equalsIndex + 1)

                    if sensitiveOptions.Contains(optionName) then
                        redacted[i] <- $"--{optionName}={Placeholder}"

                        redactions.Add(
                            { kind = "OptionValue"; name = optionName; argIndex = i; originalLength = Some optionValue.Length; placeholder = Placeholder }
                        )
                else
                    let optionName = optionPart

                    if sensitiveOptions.Contains(optionName) then
                        if i + 1 < redacted.Length then
                            let optionValue = redacted[i + 1]
                            redacted[i + 1] <- Placeholder

                            redactions.Add(
                                {
                                    kind = "OptionValue"
                                    name = optionName
                                    argIndex = i + 1
                                    originalLength = Some optionValue.Length
                                    placeholder = Placeholder
                                }
                            )

            i <- i + 1

        redacted, redactions |> Seq.toList

    /// Shapes local command-history apply regex redactions data for persistence or display.
    let private applyRegexRedactions (args: string array) (regexes: Regex list) =
        let redactions = ResizeArray<Redaction>()
        let redacted = Array.copy args

        for i in 0 .. redacted.Length - 1 do
            let mutable updated = redacted[i]

            for regex in regexes do
                if not <| String.IsNullOrWhiteSpace(updated) then
                    let matches = regex.Matches(updated)

                    if matches.Count > 0 then
                        for m in matches do
                            let prefix = if m.Groups.Count > 1 then m.Groups[1].Value else String.Empty

                            let sensitiveLength =
                                if m.Groups.Count > 1 then
                                    Math.Max(0, m.Value.Length - prefix.Length)
                                else
                                    m.Value.Length

                            redactions.Add(
                                { kind = "RegexMatch"; name = regex.ToString(); argIndex = i; originalLength = Some sensitiveLength; placeholder = Placeholder }
                            )

                        updated <- regex.Replace(updated, (fun (m: Match) -> if m.Groups.Count > 1 then m.Groups[1].Value + Placeholder else Placeholder))

            redacted[i] <- updated

        redacted, redactions |> Seq.toList

    /// Shapes local command-history redact arguments data for persistence or display.
    let redactArguments (args: string array) (historyConfig: UserConfiguration.HistoryConfiguration) =
        if isNull args then
            Array.empty, List.empty
        else
            let sensitiveOptions = buildSensitiveOptionSet historyConfig
            let regexes = buildRegexes historyConfig.RedactRegexes

            let redactedAfterOptions, optionRedactions = redactOptions args sensitiveOptions
            let fullyRedacted, regexRedactions = applyRegexRedactions redactedAfterOptions regexes
            fullyRedacted, (optionRedactions @ regexRedactions)

    /// Reads history entries data needed by the CLI workflow.
    let readHistoryEntries () =
        ensureHistoryDirectory ()
        let path = getHistoryFilePath ()

        if not <| File.Exists(path) then
            { Entries = List.empty; CorruptCount = 0 }
        else
            let mutable attempts = 0
            let mutable success = false
            let mutable result = { Entries = List.empty; CorruptCount = 0 }

            while attempts < lockBackoffMs.Length && not success do
                try
                    let entries = ResizeArray<HistoryEntry>()
                    let mutable corrupt = 0

                    use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    use reader = new StreamReader(stream, Encoding.UTF8)
                    let mutable line = reader.ReadLine()

                    while not <| isNull line do
                        if not <| String.IsNullOrWhiteSpace(line) then
                            try
                                let entry = JsonSerializer.Deserialize<HistoryEntry>(line, Constants.JsonSerializerOptions)

                                if obj.ReferenceEquals(entry, null) then
                                    corrupt <- corrupt + 1
                                else
                                    entries.Add(entry)
                            with
                            | _ -> corrupt <- corrupt + 1

                        line <- reader.ReadLine()

                    result <- { Entries = entries |> Seq.toList; CorruptCount = corrupt }
                    success <- true
                with
                | :? IOException ->
                    Thread.Sleep(lockBackoffMs[attempts])
                    attempts <- attempts + 1

            result

    /// Writes history entries data through the CLI output contract.
    let private writeHistoryEntries (entries: HistoryEntry list) =
        ensureHistoryDirectory ()
        let historyPath = getHistoryFilePath ()
        let tempPath = historyPath + ".tmp"
        let backupPath = historyPath + ".bak"

        /// Tries to map delete file and returns a GraceError instead of throwing on unsupported input.
        let tryDeleteFile (path: string) =
            let mutable attempts = 0
            let mutable deleted = false

            while attempts < lockBackoffMs.Length && not deleted do
                try
                    if File.Exists(path) then File.Delete(path)
                    deleted <- true
                with
                | :? IOException
                | :? UnauthorizedAccessException ->
                    Thread.Sleep(lockBackoffMs[attempts])
                    attempts <- attempts + 1

        do
            use stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None)
            use writer = new StreamWriter(stream, Encoding.UTF8)

            for entry in entries do
                let json = JsonSerializer.Serialize(entry, jsonlOptions)
                writer.WriteLine(json)

            writer.Flush()
            stream.Flush(true)

        let mutable attempts = 0
        let mutable replaced = false

        while attempts < lockBackoffMs.Length && not replaced do
            try
                if File.Exists(historyPath) then
                    try
                        File.Replace(tempPath, historyPath, backupPath, true)
                        tryDeleteFile backupPath
                    with
                    | :? IOException -> File.Move(tempPath, historyPath, true)
                else
                    File.Move(tempPath, historyPath)

                replaced <- true
            with
            | :? IOException
            | :? UnauthorizedAccessException ->
                Thread.Sleep(lockBackoffMs[attempts])
                attempts <- attempts + 1

        if not replaced then tryDeleteFile tempPath

    /// Shapes local command-history prune if needed data for persistence or display.
    let private pruneIfNeeded (historyConfig: UserConfiguration.HistoryConfiguration) =
        let historyPath = getHistoryFilePath ()
        let fileInfo = FileInfo(historyPath)

        let readResult = readHistoryEntries ()
        let entries = readResult.Entries

        let retentionCutoff =
            if historyConfig.RetentionDays > 0 then
                Some(
                    getCurrentInstant()
                        .Minus(Duration.FromDays(float historyConfig.RetentionDays))
                )
            else
                None

        let retained =
            match retentionCutoff with
            | Some cutoff ->
                entries
                |> List.filter (fun entry -> entry.timestampUtc >= cutoff)
            | None -> entries

        let trimmed =
            if historyConfig.MaxEntries > 0
               && retained.Length > historyConfig.MaxEntries then
                retained
                |> List.sortByDescending (fun entry -> entry.timestampUtc)
                |> List.truncate historyConfig.MaxEntries
            else
                retained

        let trimmedOrdered =
            trimmed
            |> List.sortBy (fun entry -> entry.timestampUtc)

        let exceedsSize =
            if historyConfig.MaxFileBytes > 0L then
                fileInfo.Exists
                && fileInfo.Length > historyConfig.MaxFileBytes
            else
                false

        let exceedsCount =
            historyConfig.MaxEntries > 0
            && entries.Length > historyConfig.MaxEntries

        let exceedsRetention = retained.Length <> entries.Length

        if exceedsSize || exceedsCount || exceedsRetention then
            writeHistoryEntries trimmedOrdered

        readResult

    /// Records append history entry information in the local command-history store.
    let private appendHistoryEntry (entry: HistoryEntry) (historyConfig: UserConfiguration.HistoryConfiguration) =
        ensureHistoryDirectory ()
        let historyPath = getHistoryFilePath ()
        let json = JsonSerializer.Serialize(entry, jsonlOptions)

        do
            use stream = new FileStream(historyPath, FileMode.Append, FileAccess.Write, FileShare.Read)
            use writer = new StreamWriter(stream, Encoding.UTF8)
            writer.WriteLine(json)
            writer.Flush()
            stream.Flush(true)

        pruneIfNeeded historyConfig |> ignore

    /// Tries to map get top level command and returns a GraceError instead of throwing on unsupported input.
    let private tryGetTopLevelCommand (tokens: string array) =
        if isNull tokens || tokens.Length = 0 then
            None
        else
            let comparison =
                if runningOnWindows then
                    StringComparison.InvariantCultureIgnoreCase
                else
                    StringComparison.InvariantCulture

            /// Evaluates is option with value against parsed options and command state.
            let isOptionWithValue (token: string) =
                token.Equals(OptionName.Output, comparison)
                || token.Equals("-o", comparison)
                || token.Equals(OptionName.CorrelationId, comparison)
                || token.Equals("-c", comparison)
                || token.Equals(OptionName.Source, comparison)

            /// Shapes local command-history rec data for persistence or display.
            let rec loop index =
                if index >= tokens.Length then
                    None
                else
                    let token = tokens[index]

                    if token = "--" then
                        if index + 1 < tokens.Length then Some tokens[index + 1] else None
                    elif token.StartsWith("-", StringComparison.Ordinal) then
                        let nextIndex = if isOptionWithValue token then index + 2 else index + 1
                        loop nextIndex
                    else
                        Some token

            loop 0

    /// Normalizes Grace ids for source option by keeping explicit scope values and clearing implicit child scopes.
    let private normalizeSourceOption (value: string option) =
        value
        |> Option.bind (fun source -> if String.IsNullOrWhiteSpace(source) then None else Some(source.Trim()))

    /// Records should record information in the local command-history store.
    let shouldRecord (input: RecordInput) (historyConfig: UserConfiguration.HistoryConfiguration) =
        if not historyConfig.Enabled then
            false
        else
            let tokens = if isNull input.argvNormalized then Array.empty else input.argvNormalized

            let commandName =
                tryGetTopLevelCommand tokens
                |> Option.defaultValue String.Empty

            let isHistory = commandName.Equals("history", StringComparison.InvariantCultureIgnoreCase)

            if isHistory
               && not historyConfig.RecordHistoryCommands then
                false
            else
                true

    /// Records record invocation information in the local command-history store.
    let recordInvocation (input: RecordInput) =
        let loadResult = UserConfiguration.loadUserConfiguration ()

        if not
           <| shouldRecord input loadResult.Configuration.History then
            None
        else
            let redactedNormalized, redactions = redactArguments input.argvNormalized loadResult.Configuration.History

            let redactedOriginal, _ = redactArguments input.argvOriginal loadResult.Configuration.History

            let entry =
                let repoRoot = tryFindRepoRoot input.cwd
                let repoName = tryGetRepoName repoRoot
                let repoBranch = tryGetGitBranch repoRoot

                {
                    id = Guid.NewGuid()
                    timestampUtc = input.timestampUtc
                    argvOriginal = redactedOriginal
                    argvNormalized = redactedNormalized
                    commandLine = buildCommandLine redactedNormalized
                    cwd = input.cwd
                    repoRoot = repoRoot
                    repoName = repoName
                    repoBranch = repoBranch
                    graceVersion = getGraceVersion ()
                    exitCode = input.exitCode
                    durationMs = input.durationMs
                    parseSucceeded = input.parseSucceeded
                    redactions = redactions
                    source = normalizeSourceOption input.source
                }

            Some(entry, loadResult.Configuration.History)

    /// Tries to map record invocation and returns a GraceError instead of throwing on unsupported input.
    let tryRecordInvocation (input: RecordInput) =
        match recordInvocation input with
        | None -> ()
        | Some (entry, historyConfig) ->
            /// Shapes local command-history on failure data for persistence or display.
            let onFailure () = Console.Error.WriteLine("Grace history: failed to acquire history lock; skipping history recording.")

            withHistoryLock
                (fun () ->
                    appendHistoryEntry entry historyConfig
                    ())
                onFailure

    /// Clears inherited history values so explicitly scoped access commands do not target child resources accidentally.
    let clearHistory () =
        /// Shapes local command-history on failure data for persistence or display.
        let onFailure () = Error "Grace history: failed to acquire history lock."

        withHistoryLock
            (fun () ->
                ensureHistoryDirectory ()
                let path = getHistoryFilePath ()

                let removedCount =
                    if File.Exists(path) then
                        File.ReadLines(path)
                        |> Seq.filter (fun line -> not <| String.IsNullOrWhiteSpace(line))
                        |> Seq.length
                    else
                        0

                File.WriteAllText(path, String.Empty)
                Ok removedCount)
            onFailure

    /// Evaluates is destructive against parsed options and command state.
    let isDestructive (commandLine: string) (historyConfig: UserConfiguration.HistoryConfiguration) =
        let patterns = historyConfig.DestructiveTokenRegexes
        let regexes = buildRegexes patterns

        regexes
        |> List.exists (fun regex -> regex.IsMatch(commandLine))
