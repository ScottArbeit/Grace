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
open System.Text.Json

/// Groups command output contract registry coverage for the CLI test project.
[<TestFixture>]
[<NonParallelizable>]
module CommandOutputContractRegistryTests =

    /// Normalizes command identifiers so command-output coverage can compare documentation and parser surfaces.
    let private commandId (identity: CommandIdentity) = identity.CommandId

    let private placeholderGuid = "11111111-1111-1111-1111-111111111111"

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
        | "--expected-version" -> placeholderGuid
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
        let mutable current: Command = GraceCommand.fullRootCommand.Value :> Command

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

    /// Lists the dotted IDs of commands whose JSON output matches the predicate.
    let private commandIdsWith predicate =
        CommandOutputContract.entries
        |> List.filter (fun entry -> predicate entry.JsonOutput)
        |> List.map (fun entry -> entry.Identity.CommandId)

    /// Verifies that every live leaf command declares exactly one output contract and nothing else is registered.
    [<Test>]
    let ``every live leaf command has exactly one output contract`` () =
        let discovered =
            CommandOutputContract.discoverLeafCommands GraceCommand.fullRootCommand.Value
            |> List.map commandId

        let registered =
            CommandOutputContract.entries
            |> List.map (fun entry -> entry.Identity.CommandId)

        /// Names IDs that appear more than once, such as a command declared with a result type and as migration-required.
        let duplicatesIn ids =
            ids
            |> List.countBy id
            |> List.filter (fun (_, count) -> count > 1)
            |> List.map fst

        let duplicateLeafCommands = duplicatesIn discovered
        let duplicateContracts = duplicatesIn registered

        if not duplicateLeafCommands.IsEmpty then
            Assert.Fail($"""Leaf commands discovered more than once: {String.Join(", ", duplicateLeafCommands)}.""")

        if not duplicateContracts.IsEmpty then
            Assert.Fail($"""Commands with more than one output contract: {String.Join(", ", duplicateContracts)}.""")

        assertSetEqual discovered registered

    /// Verifies that commands shipping without a stable JSON success envelope are named explicitly.
    [<Test>]
    let ``migration-required commands are an explicit list`` () =
        commandIdsWith (fun jsonOutput -> jsonOutput = MigrationRequired)
        |> List.sort
        |> should
            equal
            [
                "branch.rebase"
                "branch.status"
                "branch.switch"
                "diff.checkpoint"
                "diff.commit"
                "diff.directoryid"
                "diff.promotion"
                "diff.save"
                "diff.sha"
                "diff.tag"
                "history.run"
            ]

        for entry in
            CommandOutputContract.entries
            |> List.filter (fun entry -> entry.JsonOutput = MigrationRequired) do
            entry.SupportsSelect |> should equal false

            entry.ReturnValueContract.Status
            |> should equal MetadataIncomplete

            let schemaDocument = CommandOutputContract.introspectionDocument Schema entry

            schemaDocument.Support.JsonOutput
            |> should equal "MigrationRequired"

            schemaDocument.Support.Select
            |> should equal false

            match schemaDocument.Schema with
            | Some schema ->
                schema.Status
                |> should equal "metadata-incomplete"

                schema.Envelope
                |> should contain "not yet a stable GraceReturnValue<T>"
            | None -> Assert.Fail($"Expected schema metadata for {entry.Identity.CommandId}.")

            let examplesDocument = CommandOutputContract.introspectionDocument Examples entry

            examplesDocument.Examples[0].Name
            |> should equal "metadata-incomplete"

    /// Verifies that watch is the only command whose JSON envelope depends on how it is invoked.
    [<Test>]
    let ``watch is the only conditional JSON command`` () =
        commandIdsWith (fun jsonOutput ->
            match jsonOutput with
            | Conditional _ -> true
            | _ -> false)
        |> should equal [ "watch" ]

    /// Verifies that registry metadata is consumable without invoking handlers.
    [<Test>]
    let ``registry metadata is consumable without invoking handlers`` () =
        let identity = CommandOutputContract.commandIdentity [] "connect"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            entry.Identity.CommandId |> should equal "connect"
            entry.JsonOutput |> should equal Supported
            entry.SupportsSelect |> should equal true

            entry.ReturnValueContract.Status
            |> should equal SchemaReady

            let document = CommandOutputContract.introspectionDocument Schema entry

            document.Support.JsonOutput
            |> should equal "Supported"

            document.Support.Select |> should equal true
        | None -> Assert.Fail("connect should have a registry entry.")

    /// Verifies that the work item status change has only its explicit registry identity.
    [<Test>]
    let ``workitem set-status registry entry is the only status change identity`` () =
        let identity = CommandOutputContract.commandIdentity [ "workitem" ] "set-status"
        let oldIdentity = CommandOutputContract.commandIdentity [ "workitem" ] "status"

        CommandOutputContract.tryFind oldIdentity
        |> should equal None

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            entry.JsonOutput |> should equal Supported
            entry.SupportsSelect |> should equal true

            entry.ReturnValueContract.Name
            |> should equal "string"
        | None -> Assert.Fail("workitem.set-status should have a registry entry.")

    /// Verifies that attachment creation has one canonical registry identity and no removed attach rows.
    [<Test>]
    let ``workitem attachments add is the only attachment creation registry identity`` () =
        let identity = CommandOutputContract.commandIdentity [ "workitem"; "attachments" ] "add"

        for oldCommand in [ "summary"; "prompt"; "notes" ] do
            CommandOutputContract.commandIdentity [ "workitem"; "attach" ] oldCommand
            |> CommandOutputContract.tryFind
            |> should equal None

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            entry.JsonOutput |> should equal Supported

            entry.ReturnValueContract.Name
            |> should equal "AttachmentResult"
        | None -> Assert.Fail("workitem.attachments.add should have a registry entry.")

    /// Verifies that recoverable attachment deletion replaces bulk type-unlink registry identities.
    [<Test>]
    let ``workitem attachment deletion registry is specific and recoverable`` () =
        for oldCommand in [ "summary"; "prompt"; "notes" ] do
            CommandOutputContract.commandIdentity [ "workitem"; "links"; "remove" ] oldCommand
            |> CommandOutputContract.tryFind
            |> should equal None

        let deleteEntry =
            CommandOutputContract.commandIdentity [ "workitem"; "attachments" ] "delete"
            |> CommandOutputContract.tryFind

        let undeleteEntry =
            CommandOutputContract.commandIdentity [ "workitem"; "attachments" ] "undelete"
            |> CommandOutputContract.tryFind

        deleteEntry.IsSome |> should equal true
        undeleteEntry.IsSome |> should equal true

        deleteEntry.Value.ReturnValueContract.Name
        |> should equal "ArtifactDeletionResult"

        undeleteEntry.Value.ReturnValueContract.Name
        |> should equal "string"

    /// Verifies that diff blake3 json mode is centrally rendered instead of human progress only.
    [<Test>]
    let ``diff blake3 json mode is centrally rendered instead of human-progress only`` () =
        let identity = CommandOutputContract.commandIdentity [ "diff" ] "blake3"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            entry.JsonOutput |> should equal Supported

            entry.ReturnValueContract.Status
            |> should equal SchemaReady
        | None -> Assert.Fail("diff blake3 should have a registry entry.")

    /// Verifies that every supported entry has a parser-valid path with the recursive json option and central envelope behavior.
    [<Test>]
    let ``every supported entry has recursive json option and central envelope behavior`` () =
        let supportedEntries =
            CommandOutputContract.entries
            |> List.filter (fun entry -> entry.JsonOutput = Supported)

        supportedEntries |> should not' (be Empty)

        let parserInvalidEntries =
            supportedEntries
            |> List.choose (fun entry ->
                let parseResult = GraceCommand.fullRootCommand.Value.Parse(auditParseArgs entry)

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
            |> fun errors -> Assert.Fail($"Expected parser-valid audit paths for supported entries:{Environment.NewLine}{errors}")

        for entry in supportedEntries do
            let parseResult = GraceCommand.fullRootCommand.Value.Parse(auditParseArgs entry)

            parseResult.Errors.Count |> should equal 0

            Common.json parseResult |> should equal true

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
            GraceCommand.fullRootCommand.Value.Parse(
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
            GraceCommand.fullRootCommand.Value.Parse(
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
            match entry.JsonOutput with
            | Conditional condition -> condition |> should contain "watch --check"
            | other -> Assert.Fail($"Expected watch to be registered as Conditional JSON output, got {other}.")

            entry.SupportsSelect |> should equal true

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
                |> should equal "WatchCheckStatusDto"

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

            entry.JsonOutput |> should equal Supported

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

    /// Verifies that every success-envelope command exposes type-derived nested metadata.
    [<Test>]
    let ``every success envelope has schema ready ReturnValue metadata`` () =
        let eligibleEntries =
            CommandOutputContract.entries
            |> List.filter (fun entry -> entry.JsonOutput <> MigrationRequired)

        eligibleEntries |> should not' (be Empty)

        for entry in eligibleEntries do
            entry.ReturnValueContract.Status
            |> should equal SchemaReady

            entry.SupportsSelect |> should equal true

            let schemaDocument = CommandOutputContract.introspectionDocument Schema entry

            match schemaDocument.Schema with
            | Some schema -> schema.Status |> should equal "schema-ready"
            | None -> Assert.Fail($"Expected schema metadata for {entry.Identity.CommandId}.")

            let examplesDocument = CommandOutputContract.introspectionDocument Examples entry
            examplesDocument.Examples.Length |> should equal 2

            examplesDocument.Examples[0].Name
            |> should equal "success-envelope-shape"

    /// Requires every synchronization entrypoint to expose pause independently of participation and progress.
    [<TestCase("enable"); TestCase("run"); TestCase("pause"); TestCase("resume"); TestCase("status")>]
    let ``library synchronization schema exposes independent pause setting`` verb =
        let entry =
            CommandOutputContract.commandIdentity [ "library"; "sync" ] verb
            |> CommandOutputContract.tryFind
            |> Option.get

        let document = CommandOutputContract.introspectionDocument Schema entry
        use success = JsonDocument.Parse(Grace.Shared.Utilities.serialize document.Schema.Value.SuccessSchema)

        let properties =
            success
                .RootElement
                .GetProperty("properties")
                .GetProperty("ReturnValue")
                .GetProperty("properties")

        Assert.That(
            properties
                .GetProperty("Paused")
                .GetProperty("type")
                .GetString(),
            Is.EqualTo("boolean")
        )

        Assert.That(
            properties
                .GetProperty("Enabled")
                .GetProperty("type")
                .GetString(),
            Is.EqualTo("boolean")
        )

        Assert.That(
            properties
                .GetProperty("State")
                .GetProperty("type")
                .GetString(),
            Is.EqualTo("string")
        )

        let epochSchema =
            properties
                .GetProperty("CursorEpoch")
                .GetProperty("anyOf")
                .EnumerateArray()
            |> Seq.find (fun schema -> schema.GetProperty("type").GetString() = "string")

        Assert.That(epochSchema.GetProperty("format").GetString(), Is.EqualTo("uuid"))

    /// Keeps rename introspection tied to the retained operation result emitted by the actual command.
    [<Test>]
    let ``library rename schema describes the retained operation outcome`` () =
        let entry =
            CommandOutputContract.commandIdentity [ "library" ] "rename"
            |> CommandOutputContract.tryFind
            |> Option.get

        let document = CommandOutputContract.introspectionDocument Schema entry
        let schema = document.Schema |> Option.get
        use success = JsonDocument.Parse(Grace.Shared.Utilities.serialize schema.SuccessSchema)

        let result =
            success
                .RootElement
                .GetProperty("properties")
                .GetProperty("ReturnValue")

        result.GetProperty("title").GetString()
        |> should equal "RenameOutput"

        let properties = result.GetProperty("properties")

        properties.EnumerateObject()
        |> Seq.map (fun property -> property.Name)
        |> Seq.toList
        |> should
            equal
            [
                "OperationId"
                "SourcePath"
                "TargetPath"
                "Outcome"
                "ReasonCode"
                "Diagnostic"
            ]

        properties
            .GetProperty("OperationId")
            .GetProperty("format")
            .GetString()
        |> should equal "uuid"

        properties
            .GetProperty("Outcome")
            .GetProperty("type")
            .GetString()
        |> should equal "string"

    /// Verifies that refs keeps its current tuple and nested DTO serialization shape.
    [<Test>]
    let ``refs schema and example retain the BranchDto ReferenceDto tuple`` () =
        let identity = CommandOutputContract.commandIdentity [ "branch" ] "get-references"

        let entry =
            CommandOutputContract.tryFind identity
            |> Option.get

        let schemaDocument = CommandOutputContract.introspectionDocument Schema entry
        let schema = schemaDocument.Schema |> Option.get
        use successSchema = JsonDocument.Parse(Grace.Shared.Utilities.serialize schema.SuccessSchema)

        let returnValueSchema =
            successSchema
                .RootElement
                .GetProperty("properties")
                .GetProperty("ReturnValue")

        returnValueSchema.GetProperty("type").GetString()
        |> should equal "array"

        let tupleItems = returnValueSchema.GetProperty("prefixItems")
        tupleItems.GetArrayLength() |> should equal 2

        let mutable branchIdSchema = Unchecked.defaultof<JsonElement>

        tupleItems[0]
            .GetProperty("properties")
            .TryGetProperty("BranchId", &branchIdSchema)
        |> should equal true

        tupleItems[ 1 ].GetProperty("type").GetString()
        |> should equal "array"

        let mutable referenceIdSchema = Unchecked.defaultof<JsonElement>

        tupleItems[1]
            .GetProperty("items")
            .GetProperty("properties")
            .TryGetProperty("ReferenceId", &referenceIdSchema)
        |> should equal true

        let examplesDocument = CommandOutputContract.introspectionDocument Examples entry
        use successExample = JsonDocument.Parse(Grace.Shared.Utilities.serialize examplesDocument.Examples[0].Document)
        let returnValue = successExample.RootElement.GetProperty("ReturnValue")

        returnValue.ValueKind
        |> should equal JsonValueKind.Array

        returnValue.GetArrayLength() |> should equal 2

        returnValue[0].GetProperty("BranchId").ValueKind
        |> should equal JsonValueKind.String

        returnValue[1].ValueKind
        |> should equal JsonValueKind.Array

    /// Verifies representative record, collection, scalar, union, and option result shapes.
    [<Test>]
    let ``derived contracts cover representative serializer shapes`` () =
        let returnValueSchema groupPath commandName =
            let identity = CommandOutputContract.commandIdentity groupPath commandName

            let entry =
                CommandOutputContract.tryFind identity
                |> Option.get

            let schema =
                (CommandOutputContract.introspectionDocument Schema entry)
                    .Schema
                |> Option.get

            let document = JsonDocument.Parse(Grace.Shared.Utilities.serialize schema.SuccessSchema)

            document,
            document
                .RootElement
                .GetProperty("properties")
                .GetProperty("ReturnValue")

        let recordDocument, recordSchema = returnValueSchema [ "repository" ] "get"
        use _recordDocument = recordDocument
        let mutable repositoryIdSchema = Unchecked.defaultof<JsonElement>

        recordSchema
            .GetProperty("properties")
            .TryGetProperty("RepositoryId", &repositoryIdSchema)
        |> should equal true

        let collectionDocument, collectionSchema = returnValueSchema [ "webhook" ] "list"
        use _collectionDocument = collectionDocument

        collectionSchema.GetProperty("type").GetString()
        |> should equal "array"

        let listDocument, listSchema = returnValueSchema [ "authenticate"; "token" ] "list"
        use _listDocument = listDocument

        listSchema.GetProperty("type").GetString()
        |> should equal "array"

        let scalarDocument, scalarSchema = returnValueSchema [ "branch" ] "get-recursive-size"
        use _scalarDocument = scalarDocument

        let scalarTypes =
            scalarSchema.GetProperty("type").EnumerateArray()
            |> Seq.map (fun typeName -> typeName.GetString())
            |> Set.ofSeq

        scalarTypes |> should contain "integer"

        let diffEntry =
            CommandOutputContract.tryFind (CommandOutputContract.commandIdentity [ "diff" ] "blake3")
            |> Option.get

        let diffExample =
            (CommandOutputContract.introspectionDocument Examples diffEntry)
                .Examples[0]

        use diffDocument = JsonDocument.Parse(Grace.Shared.Utilities.serialize diffExample.Document)

        diffDocument
            .RootElement
            .GetProperty(
                "ReturnValue"
            )
            .ValueKind
        |> should equal JsonValueKind.Object

        let unionDocument, unionSchema = returnValueSchema [ "authorize" ] "check"
        use _unionDocument = unionDocument

        unionSchema.GetProperty("anyOf").GetArrayLength()
        |> should equal 2

        let optionDocument, optionSchema = returnValueSchema [ "review" ] "open"
        use _optionDocument = optionDocument

        optionSchema.GetProperty("anyOf").EnumerateArray()
        |> Seq.exists (fun branch ->
            let mutable typeSchema = Unchecked.defaultof<JsonElement>

            branch.TryGetProperty("type", &typeSchema)
            && typeSchema.GetString() = "null")
        |> should equal true

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
            |> should equal "example"

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

    /// Verifies that maintenance show journal schemas include every journal row property.
    [<Test>]
    let ``maintenance show journal schema includes quarantine reason`` () =
        let identity = CommandOutputContract.commandIdentity [ "maintenance" ] "show-journal"

        match CommandOutputContract.tryFind identity with
        | Some entry ->
            let document = CommandOutputContract.introspectionDocument Schema entry
            let schema = document.Schema |> Option.get
            use success = JsonDocument.Parse(Grace.Shared.Utilities.serialize schema.SuccessSchema)

            let rowSchema =
                success
                    .RootElement
                    .GetProperty("properties")
                    .GetProperty("ReturnValue")
                    .GetProperty("properties")
                    .GetProperty("Rows")
                    .GetProperty("items")
                    .GetProperty("properties")

            let mutable quarantineReasonSchema = Unchecked.defaultof<JsonElement>

            rowSchema.TryGetProperty("QuarantineReason", &quarantineReasonSchema)
            |> should equal true
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
