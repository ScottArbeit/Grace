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
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open Grace.Types.Common
open NUnit.Framework
open Spectre.Console
open System
open System.IO
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
            |> should equal "PartialManualSuccess"

            rootElement
                .GetProperty("Schema")
                .GetProperty("Status")
                .GetString()
            |> should equal "registry-placeholder"

            Directory.Exists(Path.Combine(root, ".grace"))
            |> should equal false)

    [<Test>]
    let ``examples emit registry-derived json and ignore output mode`` () =
        withTempDir (fun _ ->
            let exitCode, output =
                runWithCapturedOutput [| "--output"
                                         "Json"
                                         "workitem"
                                         "show"
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
            |> should equal "workitem.show"

            let examples = rootElement.GetProperty("Examples")
            examples.GetArrayLength() |> should equal 2

            examples[0].GetProperty("Document").GetProperty(
                "ReturnValue"
            )
                .ValueKind
            |> should equal JsonValueKind.Object)

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
    let ``catch all exception in json mode emits one error document on stdout`` () =
        withTempDir (fun root ->
            writeInvalidConfig root

            let exitCode, standardOut, _ =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "access"
                                                  "list-roles" |]

            exitCode |> should equal -1

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
