namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.CommandOutputContract
open Grace.Shared
open Grace.Types.Common
open NUnit.Framework
open System
open System.CommandLine
open System.IO
open System.Text.RegularExpressions
open System.Text.Json

/// Groups command output contract registry coverage for the CLI test project.
[<TestFixture>]
[<NonParallelizable>]
module CommandOutputContractRegistryTests =

    /// Normalizes command identifiers so command-output coverage can compare documentation and parser surfaces.
    let private commandId (identity: CommandIdentity) = identity.CommandId

    let private placeholderGuid = "11111111-1111-1111-1111-111111111111"

    let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let private machineReadableDocPath = Path.Combine(repoRoot, "docs", "Machine-readable CLI output.md")

    /// Builds an option placeholder token used when auditing command help output.
    let private optionPlaceholder optionName =
        match optionName with
        | "--after" -> "+15m"
        | "--allows-large-files"
        | "--anonymous-access"
        | "--record-saves" -> "true"
        | "--branch-name"
        | "--display-name"
        | "--name"
        | "--new-name"
        | "--organization-name"
        | "--owner-name"
        | "--repository-name" -> "example"
        | "--candidate" -> "candidate-1"
        | "--checkpoint-days"
        | "--diff-cache-days"
        | "--directory-version-cache-days"
        | "--logical-delete-days"
        | "--save-days" -> "30"
        | "--blake3-hash"
        | "--blake3-hash-1"
        | "--blake3-hash-2"
        | "--sha256-hash"
        | "--sha256-hash-1"
        | "--sha256-hash-2" -> "abcd1234"
        | "--claim" -> "user:user-1"
        | "--conflict-resolution-policy" -> "NoConflicts"
        | "--default-server-api-version" -> "Latest"
        | "--delete-reason"
        | "--description"
        | "--message"
        | "--reason"
        | "--required-responder"
        | "--text"
        | "--title" -> "example text"
        | "--dir-perm" -> "Read"
        | "--event" -> "promotion-set.applied"
        | "--fire-at" -> "2026-01-01T00:00:00Z"
        | "--format" -> "json"
        | "--gate" -> "build"
        | "--operation" -> "RepositoryRead"
        | "--organization-type"
        | "--owner-type"
        | "--visibility" -> "Public"
        | "--output-file" -> "output.json"
        | "--path" -> "src"
        | "--principal-id" -> "user-1"
        | "--principal-type" -> "User"
        | "--promotion-mode" -> "IndividualOnly"
        | "--resource"
        | "--scope" -> "repository"
        | "--role" -> "RepositoryReader"
        | "--search-visibility" -> "Visible"
        | "--set" -> "Active"
        | "--status" -> "Active"
        | "--type" -> "summary"
        | "--url" -> "https://example.test/webhook"
        | name when name.EndsWith("-file", StringComparison.OrdinalIgnoreCase) -> "input.txt"
        | name when name.EndsWith("-id", StringComparison.OrdinalIgnoreCase) -> placeholderGuid
        | name when name.EndsWith("-type", StringComparison.OrdinalIgnoreCase) -> "Maintenance"
        | _ -> "example"

    /// Builds an argument placeholder token used when auditing command help output.
    let private argumentPlaceholder argumentName =
        match argumentName with
        | "action" -> "read"
        | "resource" -> "repo"
        | "query" -> "example"
        | name when name.Contains("number", StringComparison.OrdinalIgnoreCase) -> "123"
        | name when name.Contains("work", StringComparison.OrdinalIgnoreCase) -> "123"
        | name when name.Contains("file", StringComparison.OrdinalIgnoreCase) -> "input.txt"
        | name when name.Contains("id", StringComparison.OrdinalIgnoreCase) -> placeholderGuid
        | _ -> "example"

    /// Finds command used by the test scenario.
    let private findCommand (commandPath: string list) =
        /// Tracks current changes so this scenario can assert the resulting side effect explicitly.
        let mutable current: Command = GraceCommand.rootCommand :> Command

        for commandName in commandPath do
            current <-
                current.Subcommands
                |> Seq.find (fun subcommand ->
                    subcommand.Name.Equals(commandName, StringComparison.Ordinal)
                    || subcommand.Aliases.Contains(commandName))

        current

    /// Builds audit placeholder args test data used to exercise CLI command Output Contract behavior.
    let private auditPlaceholderArgs (entry: CommandContractEntry) =
        let command = findCommand entry.Identity.CommandPath

        [|
            for option in command.Options do
                if option.Required then
                    yield option.Name
                    yield optionPlaceholder option.Name

            for argument in command.Arguments do
                yield argumentPlaceholder argument.Name
        |]

    /// Builds audit parse args test data used to exercise CLI command Output Contract behavior.
    let private auditParseArgs (entry: CommandContractEntry) =
        let commandPath = entry.Identity.CommandPath |> List.toArray

        [|
            yield "--output"
            yield "Json"
            yield! commandPath
            yield! auditPlaceholderArgs entry
        |]

    /// Counts by for test assertions.
    let private countBy behavior =
        CommandOutputContract.entries
        |> List.filter (fun entry -> entry.CurrentJsonBehavior = behavior)
        |> List.length

    /// Asserts that set equal matches the expected contract.
    let private assertSetEqual expected actual =
        let expectedSet = Set.ofSeq expected
        let actualSet = Set.ofSeq actual

        if expectedSet <> actualSet then
            let missing =
                Set.difference expectedSet actualSet
                |> String.concat ", "

            let extra =
                Set.difference actualSet expectedSet
                |> String.concat ", "

            Assert.Fail($"Set mismatch. Missing: {missing}. Extra: {extra}.")

    /// Captures stdout produced by the action.
    let private captureStdout action =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            let exitCode = action ()
            exitCode, writer.ToString()
        finally
            Console.SetOut(originalOut)

    /// Parses json document for test assertions.
    let private parseJsonDocument (output: string) =
        output
            .TrimStart()
            .StartsWith("{", StringComparison.Ordinal)
        |> should equal true

        JsonDocument.Parse(output)

    /// Asserts that one json object stdout matches the expected contract.
    let private assertOneJsonObjectStdout (output: string) =
        let ansiCsiPrefix = string (char 0x1B) + "["

        output.Contains(ansiCsiPrefix, StringComparison.Ordinal)
        |> should equal false

        output |> should not' (contain "[red]")
        output |> should not' (contain "Elapsed:")

        output
        |> should not' (contain "Exception in isOutputFormat")

        use document = parseJsonDocument output

        document.RootElement.ValueKind
        |> should equal JsonValueKind.Object

    /// Counts entries for test assertions.
    let private countEntries predicate =
        CommandOutputContract.entries
        |> List.filter predicate
        |> List.length

    /// Builds command ids for test data used to exercise CLI command Output Contract behavior.
    let private commandIdsFor predicate =
        CommandOutputContract.entries
        |> List.filter predicate
        |> List.map (fun entry -> entry.Identity.CommandId)

    /// Builds markdown bullet ids after test data used to exercise CLI command Output Contract behavior.
    let private markdownBulletIdsAfter (anchor: string) (markdown: string) =
        let pattern =
            Regex.Escape(anchor)
            + @"\r?\n\r?\n(?<items>(?:- `[^`]+`\r?\n)+)"

        let markdownMatch = Regex.Match(markdown, pattern)

        markdownMatch.Success |> should equal true

        markdownMatch
            .Groups[ "items" ]
            .Value.Split([| "\r\n"; "\n" |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun line ->
            let itemMatch = Regex.Match(line.Trim(), @"^- `(?<id>[^`]+)`$")

            itemMatch.Success |> should equal true

            itemMatch.Groups["id"].Value)
        |> Array.toList

    /// Verifies that registry contains accepted inventory totals.
    [<Test>]
    let ``registry contains accepted inventory totals`` () =
        CommandOutputContract.entries.Length
        |> should equal 208

        CommandOutputContract.routedEntries.Length
        |> should equal 199

        CommandOutputContract.sourceOnlyEntries.Length
        |> should equal 9

        countBy CommonRenderOutputEnvelope
        |> should equal 187

        countBy ImmediateJsonErrorOnly |> should equal 0

        countBy ConditionalCheckStatusEnvelope
        |> should equal 1

        countBy HumanProgressOnlySuccess
        |> should equal 10

        countBy PartialManualSuccess |> should equal 0
        countBy ManualJsonUnenveloped |> should equal 0
        countBy HumanProcOnly |> should equal 1
        countBy HumanOnly |> should equal 0
        countBy UnroutedSourceOnly |> should equal 9

    /// Verifies that final inventory dispositions cover every command exactly once.
    [<Test>]
    let ``final inventory dispositions cover every command exactly once`` () =
        let jsonReady =
            countEntries (fun entry ->
                match entry.RouteDisposition, entry.EnvelopeContract with
                | Routed, ExistingGraceResultEnvelope _ -> true
                | _ -> false)

        let intentionallyHumanOnly =
            countEntries (fun entry ->
                match entry.RouteDisposition, entry.EnvelopeContract with
                | Routed, JsonModeErrorOnly _ -> true
                | _ -> false)

        let conditionalStatus =
            countEntries (fun entry ->
                match entry.RouteDisposition, entry.EnvelopeContract with
                | Routed, ConditionalGraceResultEnvelope _ -> true
                | _ -> false)

        let deferredV2 =
            countEntries (fun entry ->
                match entry.RouteDisposition, entry.EnvelopeContract with
                | Routed, MigrationRequiredToGraceResultEnvelope _ -> true
                | _ -> false)

        let sourceOnly =
            countEntries (fun entry ->
                match entry.RouteDisposition, entry.EnvelopeContract with
                | SourceOnlyUnrouted _, SourceOnlyUnsupported _ -> true
                | _ -> false)

        let deleted = 0

        jsonReady |> should equal 187
        intentionallyHumanOnly |> should equal 0
        conditionalStatus |> should equal 1
        deferredV2 |> should equal 11
        sourceOnly |> should equal 9
        deleted |> should equal 0

        jsonReady
        + intentionallyHumanOnly
        + conditionalStatus
        + deferredV2
        + sourceOnly
        + deleted
        |> should equal CommandOutputContract.entries.Length

    /// Verifies that every live leaf command has one registry entry.
    [<Test>]
    let ``every live leaf command has one registry entry`` () =
        let discovered =
            CommandOutputContract.discoverLeafCommands GraceCommand.rootCommand
            |> List.map commandId

        let registered =
            CommandOutputContract.routedEntries
            |> List.map (fun entry -> entry.Identity.CommandId)

        let sourceOnlyIds =
            CommandOutputContract.sourceOnlyEntries
            |> List.map (fun entry -> entry.Identity.CommandId)

        discovered.Length
        |> should equal CommandOutputContract.routedEntries.Length

        for sourceOnlyId in sourceOnlyIds do
            discovered |> should not' (contain sourceOnlyId)

        discovered.Length
        |> should equal (discovered |> List.distinct |> List.length)

        registered.Length
        |> should equal (registered |> List.distinct |> List.length)

        assertSetEqual discovered registered

    /// Verifies that source only entries are explicit and unsupported.
    [<Test>]
    let ``source-only entries are explicit and unsupported`` () =
        let sourceOnlyIds =
            CommandOutputContract.sourceOnlyEntries
            |> List.map (fun entry -> entry.Identity.CommandId)

        sourceOnlyIds
        |> should
            equal
            [
                "reference.assign"
                "reference.checkpoint"
                "reference.commit"
                "reference.create-external"
                "reference.delete"
                "reference.get"
                "reference.promote"
                "reference.save"
                "reference.tag"
            ]

        for entry in CommandOutputContract.sourceOnlyEntries do
            entry.CurrentJsonBehavior
            |> should equal UnroutedSourceOnly

            match entry.RouteDisposition, entry.EnvelopeContract with
            | SourceOnlyUnrouted _, SourceOnlyUnsupported _ -> ()
            | other -> Assert.Fail($"Expected source-only unsupported disposition for {entry.Identity.CommandId}, got {other}.")

            entry.Features.JsonMode
            |> should equal UnsupportedUntilRouted

            entry.Features.Schema
            |> should equal UnsupportedUntilRouted

            entry.Features.Examples
            |> should equal UnsupportedUntilRouted

            entry.Features.Select
            |> should equal UnsupportedUntilRouted

    /// Verifies that routed backlog categories are represented.
    [<Test>]
    let ``routed backlog categories are represented`` () =
        let behaviors =
            CommandOutputContract.routedEntries
            |> List.map (fun entry -> entry.CurrentJsonBehavior)
            |> Set.ofList

        behaviors.Contains HumanOnly |> should equal false

        behaviors.Contains HumanProgressOnlySuccess
        |> should equal true

        behaviors.Contains ImmediateJsonErrorOnly
        |> should equal false

        behaviors.Contains ConditionalCheckStatusEnvelope
        |> should equal true

        behaviors.Contains ManualJsonUnenveloped
        |> should equal false

        behaviors.Contains PartialManualSuccess
        |> should equal false

        behaviors.Contains HumanProcOnly
        |> should equal true

    /// Verifies that registry metadata is consumable without invoking handlers.
    [<Test>]
    let ``registry metadata is consumable without invoking handlers`` () =
        let identity = CommandOutputContract.commandIdentity [] "connect"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            entry.Identity.CommandId |> should equal "connect"

            entry.CurrentJsonBehavior
            |> should equal CommonRenderOutputEnvelope

            entry.EnvelopeContract
            |> should equal (ExistingGraceResultEnvelope RequiresCliDto)

            entry.Features.JsonMode
            |> should equal ExistingBehavior

            entry.Features.Schema
            |> should equal FutureInertIntrospection

            entry.Features.Examples
            |> should equal FutureInertIntrospection

            entry.Features.Select
            |> should equal ExistingBehavior
        | None -> Assert.Fail("connect should have a registry entry.")

    /// Verifies that branch annotate registry entry is mutating because implicit save can update local state.
    [<Test>]
    let ``branch annotate registry entry is mutating because implicit save can update local state`` () =
        let identity = CommandOutputContract.commandIdentity [ "branch" ] "annotate"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            entry.Mutating |> should equal true

            entry.Category
            |> should equal MutatingStateTransition

            entry.ExecutionScope
            |> should equal CompositeLocalAndServer
        | None -> Assert.Fail("branch annotate should have a registry entry.")

    /// Verifies that diff blake3 json mode is centrally rendered instead of human progress only.
    [<Test>]
    let ``diff blake3 json mode is centrally rendered instead of human-progress only`` () =
        let identity = CommandOutputContract.commandIdentity [ "diff" ] "blake3"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            entry.CurrentJsonBehavior
            |> should equal CommonRenderOutputEnvelope

            entry.EnvelopeContract
            |> should equal (ExistingGraceResultEnvelope ReuseExistingApiOrSdkDto)

            entry.Features.JsonMode
            |> should equal ExistingBehavior
        | None -> Assert.Fail("diff blake3 should have a registry entry.")

    /// Verifies that current common renderer entries keep the grace envelope model.
    [<Test>]
    let ``current common renderer entries keep the Grace envelope model`` () =
        let commonEntries =
            CommandOutputContract.entries
            |> List.filter (fun entry -> entry.CurrentJsonBehavior = CommonRenderOutputEnvelope)

        commonEntries.Length |> should equal 187

        for entry in commonEntries do
            match entry.EnvelopeContract with
            | ExistingGraceResultEnvelope (ReuseExistingApiOrSdkDto
            | RequiresCliDto) -> ()
            | other -> Assert.Fail($"Expected existing Grace result envelope metadata for {entry.Identity.CommandId}, got {other}.")

            entry.Features.Select
            |> should equal ExistingBehavior

    /// Verifies that every common renderer entry has recursive json option and central envelope behavior.
    [<Test>]
    let ``every common renderer entry has recursive json option and central envelope behavior`` () =
        let commonEntries =
            CommandOutputContract.entries
            |> List.filter (fun entry -> entry.CurrentJsonBehavior = CommonRenderOutputEnvelope)

        commonEntries.Length |> should equal 187

        let parserInvalidEntries =
            commonEntries
            |> List.choose (fun entry ->
                let parseResult = GraceCommand.rootCommand.Parse(auditParseArgs entry)

                if parseResult.Errors.Count = 0 then
                    None
                else
                    let errors =
                        parseResult.Errors
                        |> Seq.map (fun error -> error.Message)
                        |> String.concat "; "

                    Some $"{entry.Identity.CommandId}: {errors}")

        if not parserInvalidEntries.IsEmpty then
            parserInvalidEntries
            |> String.concat Environment.NewLine
            |> fun errors -> Assert.Fail($"Expected parser-valid audit paths for common renderer entries:{Environment.NewLine}{errors}")

        for entry in commonEntries do
            let parseResult = GraceCommand.rootCommand.Parse(auditParseArgs entry)

            parseResult.Errors.Count |> should equal 0

            Common.json parseResult |> should equal true

            match entry.EnvelopeContract with
            | ExistingGraceResultEnvelope (ReuseExistingApiOrSdkDto
            | RequiresCliDto) -> ()
            | other -> Assert.Fail($"Expected central Grace result envelope metadata for {entry.Identity.CommandId}, got {other}.")

            let successValue = GraceReturnValue.Create $"success:{entry.Identity.CommandId}" $"corr-success-{entry.Identity.CommandId}"

            /// Verifies the successful command path returns the expected process status.
            let successExitCode, successOutput = captureStdout (fun () -> Common.renderOutput parseResult (Ok successValue))

            successExitCode |> should equal 0
            assertOneJsonObjectStdout successOutput

            use successDocument = parseJsonDocument successOutput
            let successRoot = successDocument.RootElement

            successRoot.GetProperty("ReturnValue").GetString()
            |> should equal $"success:{entry.Identity.CommandId}"

            successRoot
                .GetProperty("CorrelationId")
                .GetString()
            |> should equal $"corr-success-{entry.Identity.CommandId}"

            let error = GraceError.Create $"error:{entry.Identity.CommandId}" $"corr-error-{entry.Identity.CommandId}"

            /// Verifies the failing command path returns the expected process status.
            let errorExitCode, errorOutput = captureStdout (fun () -> Common.renderOutput parseResult (Error error))

            errorExitCode |> should equal -1
            assertOneJsonObjectStdout errorOutput

            use errorDocument = parseJsonDocument errorOutput
            let errorRoot = errorDocument.RootElement

            errorRoot.GetProperty("Error").GetString()
            |> should equal $"error:{entry.Identity.CommandId}"

            errorRoot.GetProperty("CorrelationId").GetString()
            |> should equal $"corr-error-{entry.Identity.CommandId}"

    /// Verifies that representative local dto envelopes use shared serializer contract.
    [<Test>]
    let ``representative local dto envelopes use shared serializer contract`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "--output"
                    "Json"
                    "maintenance"
                    "stats"
                |]
            )

        parseResult.Errors.Count |> should equal 0

        let dto: Common.LocalOutputDto.MaintenanceStatsDto =
            { DirectoryCount = 3; FileCount = 5; TotalFileSize = 89L; RootSha256Hash = Some "0123456789abcdef"; RootBlake3Hash = Some "af1349b9f5f9a1a6" }

        /// Verifies that the CLI command Output Contract scenario exits with the expected process status.
        let exitCode, output =
            captureStdout (fun () ->
                GraceReturnValue.Create dto "corr-local-dto"
                |> Ok
                |> Common.renderOutput parseResult)

        exitCode |> should equal 0
        assertOneJsonObjectStdout output

        use document = parseJsonDocument output
        let root = document.RootElement
        let returnValue = root.GetProperty("ReturnValue")

        returnValue
            .GetProperty("DirectoryCount")
            .GetInt32()
        |> should equal 3

        returnValue.GetProperty("FileCount").GetInt32()
        |> should equal 5

        returnValue
            .GetProperty("TotalFileSize")
            .GetInt64()
        |> should equal 89L

        returnValue
            .GetProperty("RootSha256Hash")
            .GetString()
        |> should equal "0123456789abcdef"

        returnValue
            .GetProperty("RootBlake3Hash")
            .GetString()
        |> should equal "af1349b9f5f9a1a6"

        /// Tracks camel Case Return Value changes so this scenario can assert the resulting side effect explicitly.
        let mutable camelCaseReturnValue = Unchecked.defaultof<JsonElement>

        root.TryGetProperty("returnValue", &camelCaseReturnValue)
        |> should equal false

        Constants.JsonSerializerOptions.PropertyNamingPolicy
        |> should equal null

    /// Verifies that maintenance list contents local dto serializes explicit dual hash fields.
    [<Test>]
    let ``maintenance list contents local dto serializes explicit dual hash fields`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "--output"
                    "Json"
                    "maintenance"
                    "list-contents"
                |]
            )

        parseResult.Errors.Count |> should equal 0

        let dto: Common.LocalOutputDto.MaintenanceListContentsDto =
            {
                Summary = { DirectoryCount = 1; FileCount = 1; TotalFileSize = 12L; RootSha256Hash = Some "root-sha256"; RootBlake3Hash = Some "root-blake3" }
                Directories =
                    [|
                        {
                            RelativePath = "."
                            DirectoryVersionId = Guid.Parse placeholderGuid
                            Sha256Hash = "directory-sha256"
                            Blake3Hash = "directory-blake3"
                            Size = 12L
                            LastWriteTimeUtc = DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
                            Files =
                                [|
                                    {
                                        RelativePath = "README.md"
                                        FileName = "README.md"
                                        Sha256Hash = "file-sha256"
                                        Blake3Hash = "file-blake3"
                                        Size = 12L
                                        LastWriteTimeUtc = DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
                                    }
                                |]
                        }
                    |]
            }

        /// Verifies that the CLI command Output Contract scenario exits with the expected process status.
        let exitCode, output =
            captureStdout (fun () ->
                GraceReturnValue.Create dto "corr-local-list-contents-dto"
                |> Ok
                |> Common.renderOutput parseResult)

        exitCode |> should equal 0
        assertOneJsonObjectStdout output

        use document = parseJsonDocument output

        let summary =
            document
                .RootElement
                .GetProperty("ReturnValue")
                .GetProperty("Summary")

        summary.GetProperty("RootSha256Hash").GetString()
        |> should equal "root-sha256"

        summary.GetProperty("RootBlake3Hash").GetString()
        |> should equal "root-blake3"

        let directory =
            document
                .RootElement
                .GetProperty("ReturnValue")
                .GetProperty("Directories")[0]

        directory.GetProperty("Sha256Hash").GetString()
        |> should equal "directory-sha256"

        directory.GetProperty("Blake3Hash").GetString()
        |> should equal "directory-blake3"

        let file = directory.GetProperty("Files")[0]

        file.GetProperty("Sha256Hash").GetString()
        |> should equal "file-sha256"

        file.GetProperty("Blake3Hash").GetString()
        |> should equal "file-blake3"

    /// Verifies that watch json mode is registered as conditional check status output.
    [<Test>]
    let ``watch json mode is registered as conditional check status output`` () =
        let identity = CommandOutputContract.commandIdentity [] "watch"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            entry.CurrentJsonBehavior
            |> should equal ConditionalCheckStatusEnvelope

            match entry.EnvelopeContract with
            | ConditionalGraceResultEnvelope (RequiresCliDto, condition) -> condition |> should contain "watch --check"
            | other -> Assert.Fail($"Expected watch to be registered as ConditionalGraceResultEnvelope, got {other}.")

            entry.Features.JsonMode
            |> should equal ExistingBehavior

            entry.Features.Select
            |> should equal ExistingBehavior

            entry.ReturnValueContract.Status
            |> should equal SchemaReady

            let schemaDocument = CommandOutputContract.introspectionDocument Schema entry

            match schemaDocument.Schema with
            | Some schema ->
                schema.Status |> should equal "schema-ready"

                schema.Envelope
                |> should contain "status envelope for status checks"

                schema.Envelope
                |> should contain "unavailable modes with nonzero exit codes"

                schema.Envelope
                |> should not' (contain "GraceError on unsupported or failed modes")

                schema.ReturnValueContract
                |> should equal "WatchStatusDto"

                use successSchema = JsonDocument.Parse(Grace.Shared.Utilities.serialize schema.SuccessSchema)

                let watchStatusSchema =
                    successSchema
                        .RootElement
                        .GetProperty("properties")
                        .GetProperty("ReturnValue")

                let requiredFields =
                    watchStatusSchema
                        .GetProperty("required")
                        .EnumerateArray()
                    |> Seq.map (fun field -> field.GetString())
                    |> Set.ofSeq

                requiredFields
                |> should not' (contain "UpdatedAt")

                requiredFields
                |> should not' (contain "RootDirectoryId")
            | None -> Assert.Fail("watch schema introspection should include the conditional status schema document.")
        | None -> Assert.Fail("watch should have a registry entry.")

    /// Verifies that schema ready registry entries describe success and error envelopes.
    [<Test>]
    let ``schema ready registry entries describe success and error envelopes`` () =
        let identity = CommandOutputContract.commandIdentity [ "authenticate" ] "logout"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            let document = CommandOutputContract.introspectionDocument Schema entry

            document.Kind |> should equal "schema"

            document.Command.Id
            |> should equal "authenticate.logout"

            match document.Schema with
            | Some schema ->
                schema.Status |> should equal "schema-ready"

                schema.ReturnValueContract
                |> should equal "string"

                use successSchema = JsonDocument.Parse(Grace.Shared.Utilities.serialize schema.SuccessSchema)
                let successRoot = successSchema.RootElement

                successRoot.GetProperty("title").GetString()
                |> should equal "GraceReturnValue<string>"

                let properties = successRoot.GetProperty("properties")

                properties
                    .GetProperty("ReturnValue")
                    .GetProperty("type")
                    .GetString()
                |> should equal "string"

                properties.GetProperty("ReturnValue").ValueKind
                |> should equal JsonValueKind.Object

                properties
                    .GetProperty("EventTime")
                    .GetProperty("type")
                    .GetString()
                |> should equal "string"

                properties
                    .GetProperty("CorrelationId")
                    .GetProperty("type")
                    .GetString()
                |> should equal "string"

                properties
                    .GetProperty("Properties")
                    .GetProperty("type")
                    .GetString()
                |> should equal "array"

                use errorSchema = JsonDocument.Parse(Grace.Shared.Utilities.serialize schema.ErrorSchema)

                errorSchema
                    .RootElement
                    .GetProperty("title")
                    .GetString()
                |> should equal "GraceError"

                errorSchema
                    .RootElement
                    .GetProperty("properties")
                    .GetProperty("Error")
                    .GetProperty("type")
                    .GetString()
                |> should equal "string"
            | None -> Assert.Fail("Schema introspection should include a schema document.")
        | None -> Assert.Fail("authenticate.logout should have a registry entry.")

    /// Verifies that maintenance registry entries expose schema ready local dto metadata.
    [<Test>]
    let ``maintenance registry entries expose schema ready local dto metadata`` () =
        let cases =
            [
                CommandOutputContract.commandIdentity [ "maintenance" ] "check-ignore-entries", "MaintenanceIgnoreEntriesDto"
                CommandOutputContract.commandIdentity [ "maintenance" ] "clear-journal", "MaintenanceClearJournalDto"
                CommandOutputContract.commandIdentity [ "maintenance" ] "list-contents", "MaintenanceListContentsDto"
                CommandOutputContract.commandIdentity [ "maintenance" ] "scan", "MaintenanceScanDto"
                CommandOutputContract.commandIdentity [ "maintenance" ] "show-journal", "MaintenanceShowJournalDto"
                CommandOutputContract.commandIdentity [ "maintenance" ] "stats", "MaintenanceStatsDto"
                CommandOutputContract.commandIdentity [ "maintenance" ] "update-index", "MaintenanceStatsDto"
            ]

        for identity, expectedContract in cases do
            match CommandOutputContract.tryFind identity with
            | Some entry ->
                entry.ReturnValueContract.Status
                |> should equal SchemaReady

                let schemaDocument = CommandOutputContract.introspectionDocument Schema entry

                match schemaDocument.Schema with
                | Some schema ->
                    schema.Status |> should equal "schema-ready"

                    schema.ReturnValueContract
                    |> should equal expectedContract

                    use successSchema = JsonDocument.Parse(Grace.Shared.Utilities.serialize schema.SuccessSchema)

                    let returnValueSchema =
                        successSchema
                            .RootElement
                            .GetProperty("properties")
                            .GetProperty("ReturnValue")

                    returnValueSchema.GetProperty("title").GetString()
                    |> should equal expectedContract
                | None -> Assert.Fail($"Expected schema document for {identity.CommandId}.")

                let examplesDocument = CommandOutputContract.introspectionDocument Examples entry

                examplesDocument.Examples[0].Name
                |> should equal "success-envelope-shape"
            | None -> Assert.Fail($"{identity.CommandId} should have a registry entry.")

    /// Verifies that doctor registry entry exposes schema ready local dto metadata.
    [<Test>]
    let ``doctor registry entry exposes schema ready local dto metadata`` () =
        let identity = CommandOutputContract.commandIdentity [] "doctor"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            entry.ReturnValueContract.Status
            |> should equal SchemaReady

            entry.Features.Schema
            |> should equal ExistingBehavior

            entry.Features.Examples
            |> should equal ExistingBehavior

            entry.Features.Select
            |> should equal ExistingBehavior

            let schemaDocument = CommandOutputContract.introspectionDocument Schema entry

            match schemaDocument.Schema with
            | Some schema ->
                schema.Status |> should equal "schema-ready"

                schema.ReturnValueContract
                |> should equal "DoctorReportDto"

                use successSchema = JsonDocument.Parse(Grace.Shared.Utilities.serialize schema.SuccessSchema)

                let returnValueSchema =
                    successSchema
                        .RootElement
                        .GetProperty("properties")
                        .GetProperty("ReturnValue")

                returnValueSchema.GetProperty("title").GetString()
                |> should equal "DoctorReportDto"
            | None -> Assert.Fail("Expected schema document for doctor.")

            let examplesDocument = CommandOutputContract.introspectionDocument Examples entry

            examplesDocument.Examples[0].Name
            |> should equal "success-envelope-shape"
        | None -> Assert.Fail("doctor should have a registry entry.")

    /// Verifies that metadata incomplete registry entries are explicit.
    [<Test>]
    let ``metadata incomplete registry entries are explicit`` () =
        let identity = CommandOutputContract.commandIdentity [ "repository" ] "init"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            entry.ReturnValueContract.Status
            |> should equal MetadataIncomplete

            let schemaDocument = CommandOutputContract.introspectionDocument Schema entry

            match schemaDocument.Schema with
            | Some schema ->
                schema.Status
                |> should equal "metadata-incomplete"

                schema.Notes
                |> should
                    contain
                    "The registry has envelope metadata for this command, but command-specific ReturnValue schema/example metadata has not been declared yet."
            | None -> Assert.Fail("Metadata-incomplete schema introspection should include a schema document.")

            let examplesDocument = CommandOutputContract.introspectionDocument Examples entry
            examplesDocument.Examples.Length |> should equal 2

            examplesDocument.Examples[0].Name
            |> should equal "metadata-incomplete"
        | None -> Assert.Fail("repository.init should have a registry entry.")

    /// Verifies that dto and union contracts are not schema ready until their full emitted shapes are declared.
    [<Test>]
    let ``dto and union contracts are not schema ready until their full emitted shapes are declared`` () =
        let cases =
            [
                CommandOutputContract.commandIdentity [ "repository" ] "get", "RepositoryDto metadata is incomplete"
                CommandOutputContract.commandIdentity [ "workitem" ] "show", "WorkItemDto metadata is incomplete"
                CommandOutputContract.commandIdentity [ "authorize" ] "check", "PermissionCheckResult metadata is incomplete"
            ]

        for identity, expectedNote in cases do
            match CommandOutputContract.tryFind identity with
            | Some entry ->
                entry.ReturnValueContract.Status
                |> should equal MetadataIncomplete

                let schemaDocument = CommandOutputContract.introspectionDocument Schema entry

                match schemaDocument.Schema with
                | Some schema ->
                    schema.Status
                    |> should equal "metadata-incomplete"

                    schema.Notes
                    |> List.exists (fun note -> note.Contains(expectedNote, StringComparison.Ordinal))
                    |> should equal true
                | None -> Assert.Fail($"Expected schema document for {identity.CommandId}.")

                let examplesDocument = CommandOutputContract.introspectionDocument Examples entry

                examplesDocument.Examples[0].Name
                |> should equal "metadata-incomplete"
            | None -> Assert.Fail($"{identity.CommandId} should have a registry entry.")

    /// Verifies that examples for schema ready commands parse as grace envelopes.
    [<Test>]
    let ``examples for schema ready commands parse as Grace envelopes`` () =
        let identity = CommandOutputContract.commandIdentity [ "authenticate" ] "logout"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            let document = CommandOutputContract.introspectionDocument Examples entry

            document.Examples.Length |> should equal 2

            document.Examples[0].Name
            |> should equal "success-envelope-shape"

            document.Examples[1].Name
            |> should equal "error-envelope-shape"

            use success = JsonDocument.Parse(Grace.Shared.Utilities.serialize document.Examples[0].Document)
            let successRoot = success.RootElement

            successRoot.GetProperty("ReturnValue").GetString()
            |> should equal "Signed out."

            let successProperties = successRoot.GetProperty("Properties")

            successProperties.ValueKind
            |> should equal JsonValueKind.Array

            successProperties[0]
                .GetProperty("Key")
                .GetString()
            |> should equal "cli.contractVersion"

            use error = JsonDocument.Parse(Grace.Shared.Utilities.serialize document.Examples[1].Document)
            let errorRoot = error.RootElement

            errorRoot.GetProperty("Error").GetString()
            |> should equal "error message"

            errorRoot.GetProperty("CorrelationId").GetString()
            |> should equal "correlation-id"
        | None -> Assert.Fail("authenticate.logout should have a registry entry.")

    /// Verifies that maintenance show journal examples include every required journal row property.
    [<Test>]
    let ``maintenance show journal example includes quarantine reason`` () =
        let identity = CommandOutputContract.commandIdentity [ "maintenance" ] "show-journal"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            let document = CommandOutputContract.introspectionDocument Examples entry
            use success = JsonDocument.Parse(Grace.Shared.Utilities.serialize document.Examples[0].Document)

            let row =
                success
                    .RootElement
                    .GetProperty("ReturnValue")
                    .GetProperty("Rows")[0]

            row.GetProperty("QuarantineReason").ValueKind
            |> should equal JsonValueKind.Null
        | None -> Assert.Fail("maintenance.show-journal should have a registry entry.")

    /// Verifies that all registry schema and example documents serialize as json.
    [<Test>]
    let ``all registry schema and example documents serialize as json`` () =
        for entry in CommandOutputContract.entries do
            for kind in [ Schema; Examples ] do
                let document = CommandOutputContract.introspectionDocument kind entry
                let json = Grace.Shared.Utilities.serialize document
                use parsed = JsonDocument.Parse(json)

                parsed.RootElement.ValueKind
                |> should equal JsonValueKind.Object

                parsed
                    .RootElement
                    .GetProperty("Command")
                    .GetProperty("Id")
                    .GetString()
                |> should equal entry.Identity.CommandId

    /// Verifies that machine readable cli docs keep final inventory evidence current.
    [<Test>]
    let ``machine readable cli docs keep final inventory evidence current`` () =
        let markdown = File.ReadAllText(machineReadableDocPath)

        let docsTrackedEntries = CommandOutputContract.entries

        /// Counts docs tracked for test assertions.
        let countDocsTracked predicate =
            docsTrackedEntries
            |> List.filter predicate
            |> List.length

        /// Builds command ids for docs tracked test data used to exercise CLI command Output Contract behavior.
        let commandIdsForDocsTracked predicate =
            docsTrackedEntries
            |> List.filter predicate
            |> List.map (fun entry -> entry.Identity.CommandId)

        let jsonReady =
            countDocsTracked (fun entry ->
                match entry.RouteDisposition, entry.EnvelopeContract with
                | Routed, ExistingGraceResultEnvelope _ -> true
                | _ -> false)

        let intentionallyHumanOnly =
            countDocsTracked (fun entry ->
                match entry.RouteDisposition, entry.EnvelopeContract with
                | Routed, JsonModeErrorOnly _ -> true
                | _ -> false)

        let conditionalStatus =
            countDocsTracked (fun entry ->
                match entry.RouteDisposition, entry.EnvelopeContract with
                | Routed, ConditionalGraceResultEnvelope _ -> true
                | _ -> false)

        let deferredV2 =
            commandIdsForDocsTracked (fun entry ->
                match entry.RouteDisposition, entry.EnvelopeContract with
                | Routed, MigrationRequiredToGraceResultEnvelope _ -> true
                | _ -> false)

        let sourceOnly =
            commandIdsForDocsTracked (fun entry ->
                match entry.RouteDisposition, entry.EnvelopeContract with
                | SourceOnlyUnrouted _, SourceOnlyUnsupported _ -> true
                | _ -> false)

        let deleted = 0

        [
            $"Total leaf commands: `{docsTrackedEntries.Length}`"
            $"JSON-ready routed commands: `{jsonReady}`"
            $"Conditionally JSON-ready routed commands: `{conditionalStatus}`"
            $"Intentionally human-only commands: `{intentionallyHumanOnly}`"
            $"Deferred routed commands with explicit V2 scope: `{deferredV2.Length}`"
            $"Source-only/unrouted commands: `{sourceOnly.Length}`"
            $"Deleted commands: `{deleted}`"
            "Predicate, wildcard, function, rename, computed-field, metadata, and streaming projections"
        ]
        |> List.iter (fun expected ->
            markdown.Contains(expected, StringComparison.Ordinal)
            |> should equal true)

        let sourceOnlyInDocs =
            markdownBulletIdsAfter "The source-only/unrouted commands are defined in source but are not attached to `GraceCommand.rootCommand` in V1:" markdown

        let deferredV2InDocs = markdownBulletIdsAfter "Current deferrals are:" markdown

        assertSetEqual sourceOnly sourceOnlyInDocs
        assertSetEqual deferredV2 deferredV2InDocs

    /// Verifies that machine readable cli docs json snippets parse.
    [<Test>]
    let ``machine readable cli docs json snippets parse`` () =
        let markdown = File.ReadAllText(machineReadableDocPath)
        let matches = Regex.Matches(markdown, "```json\r?\n(?<json>.*?)\r?\n```", RegexOptions.Singleline)

        matches.Count
        |> should be (greaterThanOrEqualTo 3)

        for markdownMatch in matches |> Seq.cast<Match> do
            let json = markdownMatch.Groups["json"].Value
            use parsed = JsonDocument.Parse(json)

            parsed.RootElement.ValueKind
            |> should not' (equal JsonValueKind.Undefined)
