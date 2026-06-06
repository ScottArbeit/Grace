namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System

[<TestFixture>]
[<NonParallelizable>]
module CommandParsingTests =
    let private withEnvironmentVariable (name: string) (value: string option) (action: unit -> unit) =
        let original = Environment.GetEnvironmentVariable(name)

        try
            Environment.SetEnvironmentVariable(name, value |> Option.toObj)
            action ()
        finally
            Environment.SetEnvironmentVariable(name, original)

    [<Test>]
    let ``resolveInvocationSource prefers explicit source over environment`` () =
        withEnvironmentVariable Common.SourceEnvironmentVariableName (Some "env-source") (fun () ->
            let parseResult =
                GraceCommand.rootCommand.Parse(
                    [|
                        "--source"
                        "explicit-source"
                        "history"
                        "show"
                    |]
                )

            parseResult.Errors.Count |> should equal 0

            Common.resolveInvocationSource parseResult
            |> should equal (Some "explicit-source"))

    [<Test>]
    let ``resolveInvocationSource falls back to environment`` () =
        withEnvironmentVariable Common.SourceEnvironmentVariableName (Some "env-source") (fun () ->
            let parseResult = GraceCommand.rootCommand.Parse([| "history"; "show" |])
            parseResult.Errors.Count |> should equal 0

            Common.resolveInvocationSource parseResult
            |> should equal (Some "env-source"))

    [<Test>]
    let ``history show accepts source filter option`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "history"
                    "show"
                    "--source"
                    "codex"
                |]
            )

        parseResult.Errors.Count |> should equal 0

    [<Test>]
    let ``history search accepts source filter option`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse [| "history"
                                              "search"
                                              "workitem"
                                              "--source"
                                              "codex" |]

        parseResult.Errors.Count |> should equal 0


namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open Grace.Types.Common
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json

[<NonParallelizable>]
module HelpDoesNotReadConfigTests =
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    let private runWithCapturedOutput (args: string array) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            let exitCode = GraceCommand.main args
            exitCode, writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    let private runWithCapturedStdoutAndStderr (args: string array) =
        use standardOutWriter = new StringWriter()
        use standardErrorWriter = new StringWriter()
        let originalOut = Console.Out
        let originalError = Console.Error

        try
            Console.SetOut(standardOutWriter)
            Console.SetError(standardErrorWriter)
            setAnsiConsoleOutput standardOutWriter
            let exitCode = GraceCommand.main args
            exitCode, standardOutWriter.ToString(), standardErrorWriter.ToString()
        finally
            Console.SetOut(originalOut)
            Console.SetError(originalError)
            setAnsiConsoleOutput originalOut

    let private parseJsonOutput (output: string) =
        output
            .TrimStart()
            .StartsWith("{", StringComparison.Ordinal)
        |> should equal true

        JsonDocument.Parse(output)

    let private assertJsonErrorOutput (standardOut: string) =
        use document = parseJsonOutput standardOut
        let rootElement = document.RootElement
        let error = rootElement.GetProperty("Error").GetString()

        error |> should not' (equal String.Empty)

        standardOut
        |> should not' (contain "Exception in isOutputFormat")

        standardOut
        |> should not' (contain "graceconfig.json is not found")

        standardOut |> should not' (contain "Elapsed:")

        error

    let private assertGraceEnvelope (output: string) =
        use document = parseJsonOutput output
        let rootElement = document.RootElement

        rootElement.GetProperty("ReturnValue").ValueKind
        |> should equal JsonValueKind.Object

        rootElement.GetProperty("EventTime").ValueKind
        |> should equal JsonValueKind.String

        rootElement.GetProperty("CorrelationId").ValueKind
        |> should equal JsonValueKind.String

        rootElement.GetProperty("Properties").ValueKind
        |> should equal JsonValueKind.Array

        rootElement.Clone()

    let private assertGraceErrorEnvelopeOnCleanStreams (standardOut: string) (standardError: string) =
        standardError |> should equal String.Empty
        let error = assertJsonErrorOutput standardOut

        standardOut
            .TrimStart()
            .StartsWith("{", StringComparison.Ordinal)
        |> should equal true

        error

    let private withFileBackup (path: string) (action: unit -> unit) =
        let directory = Path.GetDirectoryName(path)

        if not (String.IsNullOrWhiteSpace(directory)) then
            Directory.CreateDirectory(directory) |> ignore

        let backupPath = Path.Combine(Path.GetTempPath(), $"grace-cli-test-backup-{Guid.NewGuid():N}")
        let existed = File.Exists(path)

        if existed then File.Copy(path, backupPath, true)

        try
            action ()
        finally
            if existed then
                File.Copy(backupPath, path, true)
                File.Delete(backupPath)
            elif File.Exists(path) then
                File.Delete(path)

    let private captureStdoutAndStderr (action: unit -> unit) =
        use standardOutWriter = new StringWriter()
        use standardErrorWriter = new StringWriter()
        let originalOut = Console.Out
        let originalError = Console.Error

        try
            Console.SetOut(standardOutWriter)
            Console.SetError(standardErrorWriter)
            setAnsiConsoleOutput standardOutWriter
            action ()
            standardOutWriter.ToString(), standardErrorWriter.ToString()
        finally
            Console.SetOut(originalOut)
            Console.SetError(originalError)
            setAnsiConsoleOutput originalOut

    let private captureOutput (action: unit -> unit) =
        let standardOut, _ = captureStdoutAndStderr action
        standardOut


    let private withTempDir (action: string -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"grace-cli-tests-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDir) |> ignore
        let originalDir = Environment.CurrentDirectory

        try
            Environment.CurrentDirectory <- tempDir
            resetConfiguration ()
            action tempDir
        finally
            Environment.CurrentDirectory <- originalDir

            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, true)
                with
                | _ -> ()

    let private writeInvalidConfig (root: string) =
        let graceDir = Path.Combine(root, ".grace")
        Directory.CreateDirectory(graceDir) |> ignore
        File.WriteAllText(Path.Combine(graceDir, "graceconfig.json"), "not json")

    let private writeValidConfig (root: string) (ownerId: Guid) (orgId: Guid) (repoId: Guid) (branchId: Guid) =
        let graceDir = Path.Combine(root, ".grace")
        Directory.CreateDirectory(graceDir) |> ignore
        let config = GraceConfiguration()
        config.OwnerId <- ownerId
        config.OrganizationId <- orgId
        config.RepositoryId <- repoId
        config.BranchId <- branchId
        let json = serialize config
        File.WriteAllText(Path.Combine(graceDir, "graceconfig.json"), json)

    let private writeValidConfigWithDeterministicIds (root: string) =
        writeValidConfig
            root
            (Guid.Parse("11111111-1111-1111-1111-111111111111"))
            (Guid.Parse("22222222-2222-2222-2222-222222222222"))
            (Guid.Parse("33333333-3333-3333-3333-333333333333"))
            (Guid.Parse("44444444-4444-4444-4444-444444444444"))

    let private addLifecycleHeaders
        (response: HttpResponseMessage)
        (status: string)
        (unsupportedAfter: string)
        (minimumVersion: string)
        (recommendedVersion: string option)
        (updateUrl: string)
        =
        response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleStatusHeaderKey, status)
        |> ignore

        response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleUnsupportedAfterHeaderKey, unsupportedAfter)
        |> ignore

        response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleMinimumVersionHeaderKey, minimumVersion)
        |> ignore

        match recommendedVersion with
        | Some value ->
            response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleRecommendedVersionHeaderKey, value)
            |> ignore
        | None -> ()

        response.Headers.TryAddWithoutValidation(Constants.SdkLifecycleUpdateUrlHeaderKey, updateUrl)
        |> ignore

    let private lifecycleProperties status =
        use response = new HttpResponseMessage(HttpStatusCode.OK)

        addLifecycleHeaders response status "2026-12-01" "0.1.0" (Some "0.2.0") "https://github.com/ScottArbeit/Grace/releases"

        match ClientIdentity.parseLifecycleDiagnostics response with
        | Some diagnostics -> ClientIdentity.lifecycleDiagnosticsToProperties diagnostics
        | None -> Dictionary<string, obj>()

    [<Test>]
    let ``help works with invalid config`` () =
        withTempDir (fun root ->
            writeInvalidConfig root

            let exitCode, _ =
                runWithCapturedOutput [| "access"
                                         "grant-role"
                                         "-h" |]

            exitCode |> should equal 0)

    [<Test>]
    let ``help works without config`` () =
        withTempDir (fun _ ->
            let exitCode, _ =
                runWithCapturedOutput [| "access"
                                         "grant-role"
                                         "-h" |]

            exitCode |> should equal 0)

    [<Test>]
    let ``help shows symbolic defaults`` () =
        withTempDir (fun _ ->
            let exitCode, output =
                runWithCapturedOutput [| "access"
                                         "grant-role"
                                         "-h" |]

            exitCode |> should equal 0

            output
            |> should contain "[default: current OwnerId]"

            output
            |> should contain "[default: current OrganizationId]"

            output
            |> should contain "[default: current RepositoryId]"

            output
            |> should contain "[default: current BranchId]"

            output |> should contain "[default: new NanoId]")

    [<Test>]
    let ``create help rewrites empty guid defaults`` () =
        withTempDir (fun _ ->
            let exitCode, output =
                runWithCapturedOutput [| "repository"
                                         "create"
                                         "-h" |]

            exitCode |> should equal 0

            output
            |> should contain "[default: current OwnerId]"

            output
            |> should contain "[default: current OrganizationId]"

            output |> should contain "[default: new Guid]"

            output
            |> should not' (contain "00000000-0000-0000-0000-0000000000000"))

    [<Test>]
    let ``schema emits registry-derived json without requiring config`` () =
        withTempDir (fun root ->
            let exitCode, output =
                runWithCapturedOutput [| "repository"
                                         "init"
                                         "--schema" |]

            exitCode |> should equal 0

            use document = JsonDocument.Parse(output)
            let rootElement = document.RootElement

            rootElement.GetProperty("Kind").GetString()
            |> should equal "schema"

            rootElement
                .GetProperty("ContractVersion")
                .GetString()
            |> should equal "cli-json-v1"

            rootElement
                .GetProperty("Command")
                .GetProperty("Id")
                .GetString()
            |> should equal "repository.init"

            rootElement
                .GetProperty("Registry")
                .GetProperty("CurrentJsonBehavior")
                .GetString()
            |> should equal "CommonRenderOutputEnvelope"

            rootElement
                .GetProperty("Schema")
                .GetProperty("Status")
                .GetString()
            |> should equal "metadata-incomplete"

            rootElement.GetProperty("Schema").GetProperty(
                "SuccessSchema"
            )
                .GetProperty(
                "properties"
            )
                .GetProperty(
                "ReturnValue"
            )
                .ValueKind
            |> should equal JsonValueKind.Object

            Directory.Exists(Path.Combine(root, ".grace"))
            |> should equal false)

    [<Test>]
    let ``examples emit registry-derived json and ignore output mode`` () =
        withTempDir (fun _ ->
            let exitCode, output =
                runWithCapturedOutput [| "--output"
                                         "Json"
                                         "auth"
                                         "logout"
                                         "--examples" |]

            exitCode |> should equal 0

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Kind").GetString()
            |> should equal "examples"

            rootElement
                .GetProperty("Command")
                .GetProperty("Id")
                .GetString()
            |> should equal "auth.logout"

            let examples = rootElement.GetProperty("Examples")
            examples.GetArrayLength() |> should equal 2

            examples[0]
                .GetProperty("Document")
                .GetProperty("ReturnValue")
                .GetString()
            |> should equal "Signed out.")

    [<Test>]
    let ``examples for missing dto metadata emit explicit unsupported document`` () =
        withTempDir (fun _ ->
            let exitCode, output =
                runWithCapturedOutput [| "workitem"
                                         "show"
                                         "--examples" |]

            exitCode |> should equal 0

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Kind").GetString()
            |> should equal "examples"

            let examples = rootElement.GetProperty("Examples")

            examples[ 0 ].GetProperty("Name").GetString()
            |> should equal "metadata-incomplete"

            examples[0]
                .GetProperty("Document")
                .GetProperty("Status")
                .GetString()
            |> should equal "metadata-incomplete"

            examples[0]
                .GetProperty("Document")
                .GetProperty("CommandId")
                .GetString()
            |> should equal "workitem.show")

    [<Test>]
    let ``nested command schema resolves full command id`` () =
        withTempDir (fun _ ->
            let exitCode, output =
                runWithCapturedOutput [| "workitem"
                                         "attach"
                                         "summary"
                                         "--schema" |]

            exitCode |> should equal 0

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Kind").GetString()
            |> should equal "schema"

            rootElement
                .GetProperty("Command")
                .GetProperty("Id")
                .GetString()
            |> should equal "workitem.attach.summary")

    [<Test>]
    let ``root schema emits json parse error envelope`` () =
        withTempDir (fun _ ->
            let exitCode, output = runWithCapturedOutput [| "--schema" |]

            exitCode |> should equal -1

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should equal "Required command was not provided."

            rootElement
                .GetProperty("CorrelationId")
                .GetString()
            |> should not' (equal String.Empty))

    [<Test>]
    let ``schema and examples together emit json error envelope`` () =
        withTempDir (fun _ ->
            let exitCode, output =
                runWithCapturedOutput [| "repository"
                                         "init"
                                         "--schema"
                                         "--examples" |]

            exitCode |> should equal -1

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should equal "--schema and --examples cannot be used together.")

    [<Test>]
    let ``schema with unknown option emits json parse error envelope`` () =
        withTempDir (fun _ ->
            let exitCode, output =
                runWithCapturedOutput [| "repository"
                                         "init"
                                         "--schema"
                                         "--definitely-not-an-option" |]

            exitCode |> should equal -1

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should contain "Unrecognized command or argument")

    [<Test>]
    let ``schema with invalid output emits json parse error envelope`` () =
        withTempDir (fun _ ->
            let exitCode, output =
                runWithCapturedOutput [| "--output"
                                         "Bogus"
                                         "repository"
                                         "init"
                                         "--schema" |]

            exitCode |> should equal -1

            use document = parseJsonOutput output
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should contain "Bogus")

    [<Test>]
    let ``renderOutput json success writes one stdout document and no stderr`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--output"; "Json" |])
        let returnValue = GraceReturnValue.Create {| Value = "ok" |} "corr-json-success"

        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> should equal 0)

        standardError |> should equal String.Empty

        use document = parseJsonOutput standardOut
        let rootElement = document.RootElement

        rootElement
            .GetProperty("ReturnValue")
            .GetProperty("Value")
            .GetString()
        |> should equal "ok"

        rootElement
            .GetProperty("CorrelationId")
            .GetString()
        |> should equal "corr-json-success"

        standardOut |> should not' (contain "Elapsed:")

    [<Test>]
    let ``alias list json emits Grace envelope`` () =
        let exitCode, output =
            runWithCapturedOutput [| "--output"
                                     "Json"
                                     "alias"
                                     "list" |]

        exitCode |> should equal 0

        let rootElement = assertGraceEnvelope output
        let returnValue = rootElement.GetProperty("ReturnValue")

        returnValue.GetProperty("Count").GetInt32()
        |> should be (greaterThan 0)

        returnValue.GetProperty("Aliases").ValueKind
        |> should equal JsonValueKind.Array

        output |> should not' (contain "Grace command")

    [<Test>]
    let ``history show json emits Grace envelope`` () =
        let exitCode, output =
            runWithCapturedOutput [| "--output"
                                     "Json"
                                     "history"
                                     "show"
                                     "--limit"
                                     "0" |]

        exitCode |> should equal 0

        let rootElement = assertGraceEnvelope output
        let returnValue = rootElement.GetProperty("ReturnValue")

        returnValue.GetProperty("Entries").ValueKind
        |> should equal JsonValueKind.Array

        returnValue.GetProperty("Count").GetInt32()
        |> should be (greaterThanOrEqualTo 0)

    [<Test>]
    let ``history search json emits Grace envelope`` () =
        let exitCode, output =
            runWithCapturedOutput [| "--output"
                                     "Json"
                                     "history"
                                     "search"
                                     "workitem"
                                     "--limit"
                                     "0" |]

        exitCode |> should equal 0

        let rootElement = assertGraceEnvelope output

        rootElement.GetProperty("ReturnValue").GetProperty(
            "Entries"
        )
            .ValueKind
        |> should equal JsonValueKind.Array

    [<Test>]
    let ``history validation error json emits Grace error envelope`` () =
        let exitCode, output =
            runWithCapturedOutput [| "--output"
                                     "Json"
                                     "history"
                                     "show"
                                     "--limit"
                                     "-1" |]

        exitCode |> should equal -1

        use document = parseJsonOutput output

        document
            .RootElement
            .GetProperty("Error")
            .GetString()
        |> should equal "Limit must be positive."

    [<Test>]
    let ``history on off and delete json emit Grace envelopes`` () =
        let userConfigPath = UserConfiguration.getUserConfigurationPath ()
        let historyPath = HistoryStorage.getHistoryFilePath ()

        withFileBackup userConfigPath (fun () ->
            withFileBackup historyPath (fun () ->
                let onExitCode, onOutput =
                    runWithCapturedOutput [| "--output"
                                             "Json"
                                             "history"
                                             "on" |]

                onExitCode |> should equal 0
                assertGraceEnvelope onOutput |> ignore

                let offExitCode, offOutput =
                    runWithCapturedOutput [| "--output"
                                             "Json"
                                             "history"
                                             "off" |]

                offExitCode |> should equal 0
                assertGraceEnvelope offOutput |> ignore

                File.WriteAllText(historyPath, String.Empty)

                let deleteExitCode, deleteOutput =
                    runWithCapturedOutput [| "--output"
                                             "Json"
                                             "history"
                                             "delete" |]

                deleteExitCode |> should equal 0
                assertGraceEnvelope deleteOutput |> ignore))

    [<Test>]
    let ``connect json validation error emits clean Grace envelope`` () =
        withTempDir (fun root ->
            writeValidConfigWithDeterministicIds root

            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "connect"
                                                  "owner/repository"
                                                  "--owner-name"
                                                  "owner" |]

            exitCode |> should equal -1

            assertGraceErrorEnvelopeOnCleanStreams standardOut standardError
            |> should contain "Repository shortcut must be in the form")

    [<Test>]
    let ``directory version get zip file json validation error emits clean Grace envelope`` () =
        withTempDir (fun _ ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "directory-version"
                                                  "get-zip-file"
                                                  "--directory-version-id"
                                                  $"{Guid.Empty}" |]

            exitCode |> should equal -1

            assertGraceErrorEnvelopeOnCleanStreams standardOut standardError
            |> should contain "graceconfig.json")

    [<Test>]
    let ``repository init json invalid directory emits clean Grace envelope`` () =
        withTempDir (fun root ->
            writeValidConfigWithDeterministicIds root
            let missingDirectory = Path.Combine(root, "missing")

            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "repository"
                                                  "init"
                                                  "--directory"
                                                  missingDirectory |]

            exitCode |> should equal -1

            assertGraceErrorEnvelopeOnCleanStreams standardOut standardError
            |> should contain "directory")

    [<Test>]
    let ``repository init no output DTO renders absent observability as null`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "--output"
                    "Json"
                    "repository"
                    "init"
                    "--no-output"
                |]
            )

        let output: Common.LocalOutputDto.RepositoryInitDto =
            { Message = "Initialized repository."; DirectoryCount = None; FileCount = None; TotalFileSize = None; RootSha256Hash = None }

        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok(GraceReturnValue.Create output "corr-repository-init"))
                |> should equal 0)

        standardError |> should equal String.Empty

        let rootElement = assertGraceEnvelope standardOut
        let returnValue = rootElement.GetProperty("ReturnValue")

        returnValue.GetProperty("Message").GetString()
        |> should equal "Initialized repository."

        returnValue
            .GetProperty(
                "DirectoryCount"
            )
            .ValueKind
        |> should equal JsonValueKind.Null

        returnValue.GetProperty("FileCount").ValueKind
        |> should equal JsonValueKind.Null

        returnValue.GetProperty("TotalFileSize").ValueKind
        |> should equal JsonValueKind.Null

        returnValue
            .GetProperty(
                "RootSha256Hash"
            )
            .ValueKind
        |> should equal JsonValueKind.Null

    [<Test>]
    let ``review report show json validation error emits clean Grace envelope`` () =
        withTempDir (fun root ->
            writeValidConfigWithDeterministicIds root

            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "review"
                                                  "report"
                                                  "show"
                                                  "--candidate"
                                                  "not-a-guid" |]

            exitCode |> should equal -1

            assertGraceErrorEnvelopeOnCleanStreams standardOut standardError
            |> should contain "CandidateId must be a valid non-empty Guid")

    [<Test>]
    let ``review report export json validation error emits clean Grace envelope`` () =
        withTempDir (fun root ->
            writeValidConfigWithDeterministicIds root

            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "review"
                                                  "report"
                                                  "export"
                                                  "--candidate"
                                                  "not-a-guid"
                                                  "--format"
                                                  "markdown"
                                                  "--output-file"
                                                  "report.md" |]

            exitCode |> should equal -1

            assertGraceErrorEnvelopeOnCleanStreams standardOut standardError
            |> should contain "CandidateId must be a valid non-empty Guid")

    [<Test>]
    let ``workitem attachments download json validation error emits clean Grace envelope`` () =
        withTempDir (fun root ->
            writeValidConfigWithDeterministicIds root

            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "workitem"
                                                  "attachments"
                                                  "download"
                                                  "not-a-work-item"
                                                  "--artifact-id"
                                                  $"{Guid.Empty}"
                                                  "--output-file"
                                                  "attachment.bin" |]

            exitCode |> should equal -1

            assertGraceErrorEnvelopeOnCleanStreams standardOut standardError
            |> should contain "work item ID is invalid")

    [<Test>]
    let ``select option makes command json-oriented`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "auth"
                    "logout"
                    "--select"
                    "Value"
                |]
            )

        parseResult.Errors.Count |> should equal 0

        Common.json parseResult |> should equal true

        Common.tryGetSelect parseResult
        |> should equal (Some "Value")

    [<Test>]
    let ``renderOutput select writes selected scalar json only`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--select"; "Value" |])
        let returnValue = GraceReturnValue.Create {| Value = "ok"; Other = "hidden" |} "corr-select-success"

        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> should equal 0)

        standardError |> should equal String.Empty
        standardOut.Trim() |> should equal "\"ok\""

    [<Test>]
    let ``renderOutput select success suppresses lifecycle warnings`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--select"; "Value" |])
        let returnValue = GraceReturnValue.Create {| Value = "ok" |} "corr-select-lifecycle-success"

        returnValue.enhance (lifecycleProperties "deprecated")
        |> ignore

        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> should equal 0)

        standardError |> should equal String.Empty
        standardOut.Trim() |> should equal "\"ok\""

        standardOut
        |> should not' (contain "This Grace client version is deprecated.")

    [<Test>]
    let ``renderOutput select writes selected object and collection json`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--select"; "Container.Items" |])

        let returnValue =
            GraceReturnValue.Create
                {|
                    Container =
                        {|
                            Items =
                                [|
                                    {| Name = "one" |}
                                    {| Name = "two" |}
                                |]
                        |}
                |}
                "corr-select-collection"

        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> should equal 0)

        standardError |> should equal String.Empty

        use document = JsonDocument.Parse(standardOut)
        let rootElement = document.RootElement

        rootElement.ValueKind
        |> should equal JsonValueKind.Array

        rootElement.GetArrayLength() |> should equal 2

        rootElement[ 1 ].GetProperty("Name").GetString()
        |> should equal "two"

    [<Test>]
    let ``renderOutput select writes null json`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--select"; "Value" |])
        let returnValue = GraceReturnValue.Create {| Value = (null: string) |} "corr-select-null"

        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> should equal 0)

        standardError |> should equal String.Empty
        standardOut.Trim() |> should equal "null"

    [<Test>]
    let ``renderOutput select missing property emits json error`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--select"; "Missing" |])
        let returnValue = GraceReturnValue.Create {| Value = "ok" |} "corr-select-missing"

        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> should equal -1)

        standardError |> should equal String.Empty

        use document = parseJsonOutput standardOut
        let rootElement = document.RootElement

        rootElement.GetProperty("Error").GetString()
        |> should contain "was not found in ReturnValue"

        rootElement
            .GetProperty("CorrelationId")
            .GetString()
        |> should equal "corr-select-missing"

    [<Test>]
    let ``renderOutput select scalar traversal emits json error`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--select"; "Value.Name" |])
        let returnValue = GraceReturnValue.Create {| Value = "ok" |} "corr-select-scalar"

        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Ok returnValue)
                |> should equal -1)

        standardError |> should equal String.Empty

        assertJsonErrorOutput standardOut
        |> should contain "cannot read 'Name'"

    [<Test>]
    let ``renderOutput select leaves error envelope unprojected`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--select"; "Value" |])
        let error = GraceError.Create "original failure" "corr-select-error"

        error.enhance (lifecycleProperties "unsupported")
        |> ignore

        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Error error)
                |> should equal -1)

        standardError |> should equal String.Empty

        use document = parseJsonOutput standardOut
        let rootElement = document.RootElement

        rootElement.GetProperty("Error").GetString()
        |> should equal "original failure"

        standardOut
        |> should not' (contain "This Grace client version is no longer supported.")

        let mutable returnValue = Unchecked.defaultof<JsonElement>

        rootElement.TryGetProperty("ReturnValue", &returnValue)
        |> should equal false

    [<Test>]
    let ``invalid select grammar does not invoke command`` () =
        withTempDir (fun root ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "connect"
                                                  "--select"
                                                  "Value[0]" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut
            |> should contain "predicates, wildcards, functions, and expressions are not supported"

            File.Exists(Path.Combine(root, ".grace", "graceconfig.json"))
            |> should equal false)

    [<Test>]
    let ``valid select on unsupported command is json error without invoking command`` () =
        withTempDir (fun root ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "history"
                                                  "run"
                                                  "--select"
                                                  "Value" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut
            |> should contain "does not support --select in this release"

            File.Exists(Path.Combine(root, ".grace", "graceconfig.json"))
            |> should equal false)

    [<Test>]
    let ``renderOutput json error writes GraceError envelope with shared field names`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "--output"; "Json" |])
        let error = GraceError.Create "central writer error" "corr-json-error"

        let standardOut, standardError =
            captureStdoutAndStderr (fun () ->
                Common.renderOutput parseResult (Error error)
                |> should equal -1)

        standardError |> should equal String.Empty

        use document = parseJsonOutput standardOut
        let rootElement = document.RootElement

        rootElement.GetProperty("Error").GetString()
        |> should equal "central writer error"

        rootElement
            .GetProperty("CorrelationId")
            .GetString()
        |> should equal "corr-json-error"

        let mutable camelCaseCorrelationId = Unchecked.defaultof<JsonElement>

        rootElement.TryGetProperty("correlationId", &camelCaseCorrelationId)
        |> should equal false

        Constants.JsonSerializerOptions.PropertyNamingPolicy
        |> should equal null

    [<Test>]
    let ``missing config in json mode emits one error document on stdout`` () =
        withTempDir (fun _ ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "branch"
                                                  "get" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            use document = parseJsonOutput standardOut
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should contain "graceconfig.json"

            standardOut |> should not' (contain "Elapsed:"))

    [<Test>]
    let ``missing config in json equals mode emits one error document on stdout`` () =
        withTempDir (fun _ ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output=Json"
                                                  "branch"
                                                  "get" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut
            |> should contain "graceconfig.json")

    [<Test>]
    let ``missing config in mixed-case json equals mode emits one error document on stdout`` () =
        withTempDir (fun _ ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--OUTPUT=Json"
                                                  "branch"
                                                  "get" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut
            |> should contain "graceconfig.json")

    [<Test>]
    let ``earlier non-json output token does not mask later json intent`` () =
        withTempDir (fun _ ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Normal"
                                                  "--output=Json"
                                                  "branch"
                                                  "get" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut |> ignore)

    [<Test>]
    let ``malformed split output option does not mask later long json intent`` () =
        withTempDir (fun _ ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "--output"
                                                  "Json"
                                                  "branch"
                                                  "get" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut |> ignore)

    [<Test>]
    let ``malformed split output option does not mask later short json intent`` () =
        withTempDir (fun _ ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "-o"
                                                  "-o"
                                                  "Json"
                                                  "branch"
                                                  "get" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut |> ignore)

    [<Test>]
    let ``parse error in json mode emits one error document on stdout`` () =
        withTempDir (fun _ ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "repository"
                                                  "init"
                                                  "--definitely-not-an-option" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            use document = parseJsonOutput standardOut
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should contain "Unrecognized command or argument")

    [<Test>]
    let ``parse error in json equals mode emits one error document on stdout`` () =
        withTempDir (fun _ ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output=Json"
                                                  "repository"
                                                  "init"
                                                  "--definitely-not-an-option" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut
            |> should contain "Unrecognized command or argument")

    [<Test>]
    let ``short equals json output spelling emits json error envelope`` () =
        withTempDir (fun _ ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "-o=Json"
                                                  "repository"
                                                  "init"
                                                  "--definitely-not-an-option" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut |> ignore)

    [<Test>]
    let ``lowercase json value is rejected with json error envelope`` () =
        withTempDir (fun _ ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output=json"
                                                  "repository"
                                                  "init" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            assertJsonErrorOutput standardOut
            |> should contain "json")

    [<Test>]
    let ``catch all exception in json mode emits one error document on stdout`` () =
        withTempDir (fun root ->
            writeInvalidConfig root

            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "access"
                                                  "list-roles" |]

            exitCode |> should equal -1
            standardError |> should equal String.Empty

            use document = parseJsonOutput standardOut
            let rootElement = document.RootElement

            rootElement.GetProperty("Error").GetString()
            |> should not' (equal String.Empty)

            rootElement
                .GetProperty("CorrelationId")
                .GetString()
            |> should not' (equal String.Empty)

            standardOut |> should not' (contain "Exception:"))

    [<Test>]
    let ``missing config in human mode remains human oriented`` () =
        withTempDir (fun _ ->
            let exitCode, standardOut, _ =
                runWithCapturedStdoutAndStderr [| "branch"
                                                  "get" |]

            exitCode |> should equal -1

            standardOut
                .TrimStart()
                .StartsWith("{", StringComparison.Ordinal)
            |> should equal false

            standardOut |> should contain "graceconfig.json")

    [<Test>]
    let ``verbose parse result shows resolved ids`` () =
        withTempDir (fun root ->
            let ownerId = Guid.NewGuid()
            let orgId = Guid.NewGuid()
            let repoId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            writeValidConfig root ownerId orgId repoId branchId

            let parseResult = GraceCommand.rootCommand.Parse([| "access"; "grant-role" |])

            let output = captureOutput (fun () -> Common.printParseResult parseResult)

            output |> should contain "Resolved values:"
            output |> should contain $"{ownerId}"
            output |> should contain $"{orgId}"
            output |> should contain $"{repoId}"
            output |> should contain $"{branchId}")

    [<Test>]
    let ``getNormalizedIdsAndNames falls back to config ids`` () =
        withTempDir (fun root ->
            let ownerId = Guid.NewGuid()
            let orgId = Guid.NewGuid()
            let repoId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            writeValidConfig root ownerId orgId repoId branchId

            let parseResult = GraceCommand.rootCommand.Parse([| "access"; "grant-role" |])
            let graceIds = Services.getNormalizedIdsAndNames parseResult

            graceIds.OwnerId |> should equal ownerId
            graceIds.OrganizationId |> should equal orgId
            graceIds.RepositoryId |> should equal repoId
            graceIds.BranchId |> should equal branchId)


namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.Shared.Client
open NUnit.Framework
open Spectre.Console
open System
open System.IO

[<NonParallelizable>]
module RootHelpGroupingTests =
    type private GroupedHelpExpectation = { Args: string array; Headings: string list }

    let private groupedHelpExpectations =
        [
            {
                Args = [| "repo"; "-h" |]
                Headings =
                    [
                        "Create and initialize:"
                        "Inspect:"
                        "Configuration:"
                        "Lifecycle:"
                    ]
            }
            {
                Args = [| "branch"; "-h" |]
                Headings =
                    [
                        "Create and contribute:"
                        "Promotion workflow:"
                        "Inspect:"
                        "Settings:"
                        "Lifecycle:"
                    ]
            }
            {
                Args = [| "owner"; "-h" |]
                Headings =
                    [
                        "Create and inspect:"
                        "Settings:"
                        "Lifecycle:"
                    ]
            }
            {
                Args = [| "org"; "-h" |]
                Headings =
                    [
                        "Create and inspect:"
                        "Settings:"
                        "Lifecycle:"
                    ]
            }
        ]

    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    let private runWithCapturedOutput (args: string array) =
        use writer = new StringWriter()
        let originalOut = Console.Out

        try
            Console.SetOut(writer)
            setAnsiConsoleOutput writer
            let exitCode = GraceCommand.main args
            exitCode, writer.ToString()
        finally
            Console.SetOut(originalOut)
            setAnsiConsoleOutput originalOut

    let private withFileBackup (path: string) (action: unit -> unit) =
        let backupPath = path + ".testbackup"
        let hadExisting = File.Exists(path)

        if hadExisting then File.Copy(path, backupPath, true)

        try
            action ()
        finally
            if hadExisting then
                File.Copy(backupPath, path, true)
                File.Delete(backupPath)
            elif File.Exists(path) then
                File.Delete(path)

    let private withGraceUserFileBackups (action: unit -> unit) =
        let configPath = UserConfiguration.getUserConfigurationPath ()
        let historyPath = HistoryStorage.getHistoryFilePath ()
        let lockPath = HistoryStorage.getHistoryLockPath ()

        withFileBackup configPath (fun () -> withFileBackup historyPath (fun () -> withFileBackup lockPath action))

    let private sliceBetween (text: string) (startText: string) (endText: string) =
        let startIndex = text.IndexOf(startText, StringComparison.Ordinal)
        let endIndex = text.IndexOf(endText, StringComparison.Ordinal)

        if startIndex >= 0 && endIndex > startIndex then
            text.Substring(startIndex, endIndex - startIndex)
        else
            text

    [<Test>]
    let ``root help groups commands`` () =
        withGraceUserFileBackups (fun () ->
            let exitCode, output = runWithCapturedOutput [||]
            exitCode |> should equal 0

            output |> should contain "Getting started:"
            output |> should contain "Day-to-day development:"
            output |> should contain "Review and promotion:"

            output
            |> should contain "Administration and access:"

            output |> should contain "Local utilities:"

            let gettingStarted = sliceBetween output "Getting started:" "Day-to-day development:"

            gettingStarted |> should contain "auth"
            gettingStarted |> should contain "connect"
            gettingStarted |> should contain "config"
            gettingStarted |> should not' (contain "branch")

            let dayToDay = sliceBetween output "Day-to-day development:" "Review and promotion:"

            dayToDay |> should contain "branch"
            dayToDay |> should contain "diff"
            dayToDay |> should contain "directory-version"
            dayToDay |> should contain "watch"
            dayToDay |> should not' (contain "work")

            let reviewAndPromotion = sliceBetween output "Review and promotion:" "Administration and access:"

            reviewAndPromotion |> should contain "workitem"
            reviewAndPromotion |> should contain "review"
            reviewAndPromotion |> should contain "candidate"
            reviewAndPromotion |> should contain "queue"
            reviewAndPromotion |> should contain "agent"
            reviewAndPromotion |> should contain "approval"
            reviewAndPromotion |> should contain "webhook"

            reviewAndPromotion
            |> should contain "promotion-set")

    [<Test>]
    let ``subcommand help is not grouped`` () =
        withGraceUserFileBackups (fun () ->
            let exitCode, output =
                runWithCapturedOutput [| "branch"
                                         "-h" |]

            exitCode |> should equal 0
            output |> should not' (contain "Getting started:")

            output
            |> should not' (contain "Day-to-day development:")

            output
            |> should not' (contain "Review and promotion:")

            output
            |> should not' (contain "Administration and access:")

            output |> should not' (contain "Local utilities:"))

    [<Test>]
    let ``selected command helps are grouped`` () =
        withGraceUserFileBackups (fun () ->
            for expectation in groupedHelpExpectations do
                let exitCode, output = runWithCapturedOutput expectation.Args
                exitCode |> should equal 0

                for heading in expectation.Headings do
                    output |> should contain heading)


namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.Types.Common
open NUnit.Framework
open System

[<NonParallelizable>]
module ClientIdentityTests =

    [<Test>]
    let ``configureSdkClientIdentity stamps CLI client type with assembly file version`` () =
        Grace.SDK.ClientIdentity.clear ()

        try
            Services.configureSdkClientIdentity ()

            match Grace.SDK.ClientIdentity.tryGetConfiguredClientType () with
            | Some (ClientType.CLI version) ->
                String.IsNullOrWhiteSpace version
                |> should equal false

                version
                |> should equal (Services.getCliAssemblyFileVersion ())
            | other -> Assert.Fail($"Expected CLI client identity, got {other}.")
        finally
            Grace.SDK.ClientIdentity.clear ()

    [<Test>]
    let ``configured SDK client identity adds Grace client headers`` () =
        Grace.SDK.ClientIdentity.clear ()

        try
            Grace.SDK.ClientIdentity.configure (ClientType.CLI "1.2.3")

            use httpClient = Grace.SDK.ClientIdentity.getHttpClient "corr-client"

            httpClient.DefaultRequestHeaders.Contains(Grace.Shared.Constants.ClientTypeHeaderKey)
            |> should equal true

            httpClient.DefaultRequestHeaders.GetValues(Grace.Shared.Constants.ClientTypeHeaderKey)
            |> Seq.exactlyOne
            |> should equal "CLI"

            httpClient.DefaultRequestHeaders.GetValues(Grace.Shared.Constants.ClientVersionHeaderKey)
            |> Seq.exactlyOne
            |> should equal "1.2.3"
        finally
            Grace.SDK.ClientIdentity.clear ()
