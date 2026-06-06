namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open Spectre.Console
open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json

[<NonParallelizable>]
module WatchTests =
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
            let parseResult = GraceCommand.rootCommand.Parse(args)
            let exitCode = parseResult.InvokeAsync().Result
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

    let private runGraceProcessWithCapturedStdoutAndStderr workingDirectory (args: string array) =
        let cliDllPath = Path.Combine(AppContext.BaseDirectory, "grace.dll")

        File.Exists(cliDllPath) |> should equal true

        let startInfo = ProcessStartInfo()
        startInfo.FileName <- "dotnet"
        startInfo.WorkingDirectory <- workingDirectory
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.ArgumentList.Add(cliDllPath)

        for arg in args do
            startInfo.ArgumentList.Add(arg)

        use proc = new Process()
        proc.StartInfo <- startInfo

        if not (proc.Start()) then failwith "Failed to start grace process."

        let standardOut = proc.StandardOutput.ReadToEnd()
        let standardError = proc.StandardError.ReadToEnd()

        if not (proc.WaitForExit(30000)) then
            try
                proc.Kill(true)
            with
            | _ -> ()

            Assert.Fail("Timed out waiting for grace process to exit.")

        proc.ExitCode, standardOut, standardError

    let private withEnv (name: string) (value: string option) (action: unit -> unit) =
        let original = Environment.GetEnvironmentVariable(name)

        match value with
        | Some v -> Environment.SetEnvironmentVariable(name, v)
        | None -> Environment.SetEnvironmentVariable(name, null)

        try
            action ()
        finally
            Environment.SetEnvironmentVariable(name, original)

    let private withClearedEnvVars (names: string list) (action: unit -> unit) =
        let rec run remaining =
            match remaining with
            | [] -> action ()
            | head :: tail -> withEnv head None (fun () -> run tail)

        run names

    let private clearWatchAuthEnv (action: unit -> unit) =
        withClearedEnvVars
            [
                Constants.EnvironmentVariables.GraceToken
                Constants.EnvironmentVariables.GraceTokenFile
                Constants.EnvironmentVariables.GraceAuthOidcAuthority
                Constants.EnvironmentVariables.GraceAuthOidcAudience
                Constants.EnvironmentVariables.GraceAuthOidcCliClientId
                Constants.EnvironmentVariables.GraceAuthOidcCliRedirectPort
                Constants.EnvironmentVariables.GraceAuthOidcCliScopes
                Constants.EnvironmentVariables.GraceAuthOidcM2mClientId
                Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret
                Constants.EnvironmentVariables.GraceAuthOidcM2mScopes
                Constants.EnvironmentVariables.GraceServerUri
            ]
            action

    let private parseJsonOutput (output: string) =
        output.StartsWith("{", StringComparison.Ordinal)
        |> should equal true

        JsonDocument.Parse(output)

    let private writeLiveWatchStatusFile () =
        let status: Services.GraceWatchStatus =
            {
                UpdatedAt = getCurrentInstant ()
                RootDirectoryId = Guid.NewGuid()
                RootDirectorySha256Hash = Sha256Hash "live-watch-root"
                LastFileUploadInstant = Instant.MinValue
                LastDirectoryVersionInstant = Instant.MinValue
                DirectoryIds = HashSet<DirectoryVersionId>()
            }

        let ipcFileName = Services.IpcFileName()

        Directory.CreateDirectory(Path.GetDirectoryName(ipcFileName))
        |> ignore

        File.WriteAllText(ipcFileName, serialize status)
        ipcFileName

    let private withTempRepo (action: string -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"grace-watch-tests-{Guid.NewGuid():N}")
        let graceDir = Path.Combine(tempDir, Constants.GraceConfigDirectory)
        let configPath = Path.Combine(graceDir, Constants.GraceConfigFileName)
        Directory.CreateDirectory(graceDir) |> ignore
        File.WriteAllText(configPath, "{}")

        let originalDir = Environment.CurrentDirectory

        try
            Environment.CurrentDirectory <- tempDir
            resetConfiguration ()
            action tempDir
        finally
            resetConfiguration ()
            Environment.CurrentDirectory <- originalDir

            if Directory.Exists(tempDir) then
                try
                    Directory.Delete(tempDir, true)
                with
                | _ -> ()

    [<Test>]
    let ``resolveSignalRAccessTokenResult returns token when present`` () =
        let result = Watch.resolveSignalRAccessTokenResult (Ok(Some "token-value"))

        match result with
        | Ok token -> token |> should equal "token-value"
        | Error error -> Assert.Fail($"Expected token result, got error: {error}")

    [<Test>]
    let ``resolveSignalRAccessTokenResult errors when token is missing`` () =
        let result = Watch.resolveSignalRAccessTokenResult (Ok None)

        match result with
        | Ok token -> Assert.Fail($"Expected missing token error, got token: {token}")
        | Error error ->
            error
            |> should contain "No access token is available."

    [<Test>]
    let ``resolveSignalRAccessTokenResult includes underlying auth error`` () =
        let result = Watch.resolveSignalRAccessTokenResult (Error "test error")

        match result with
        | Ok token -> Assert.Fail($"Expected auth error, got token: {token}")
        | Error error ->
            error
            |> should contain "Unable to acquire an access token for SignalR notifications:"

            error |> should contain "test error"

    [<Test>]
    let ``watch exits with auth guidance when no token is configured`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                let exitCode, output = runWithCapturedOutput [| "watch" |]

                if exitCode <> -1 then
                    Assert.Fail(
                        $"Expected watch to exit with -1 when auth is missing. Actual: {exitCode}.{Environment.NewLine}Output:{Environment.NewLine}{output}"
                    )

                output
                |> should contain "Unable to acquire an access token for SignalR"

                output
                |> should contain "Authentication is not configured."))

    [<Test>]
    let ``watch json auth failure emits one clean error envelope`` () =
        withTempRepo (fun _ ->
            clearWatchAuthEnv (fun () ->
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "watch" |]

                exitCode |> should equal -1
                standardError |> should equal String.Empty

                standardOut
                |> should not' (contain "SignalR Hub connection state")

                standardOut |> should not' (contain "Elapsed:")

                use document = parseJsonOutput standardOut
                let root = document.RootElement

                root.GetProperty("Error").GetString()
                |> should contain "watch is a continuous foreground workflow"

                let mutable returnValue = Unchecked.defaultof<JsonElement>

                root.TryGetProperty("ReturnValue", &returnValue)
                |> should equal false))

    [<Test>]
    let ``watch json error in separate process does not delete live watch ipc file`` () =
        withTempRepo (fun root ->
            clearWatchAuthEnv (fun () ->
                let ipcFileName = writeLiveWatchStatusFile ()

                let exitCode, standardOut, standardError = runGraceProcessWithCapturedStdoutAndStderr root [| "--output"; "Json"; "watch" |]

                Assert.That(exitCode, Is.EqualTo(-1).Or.EqualTo(255))
                standardError |> should equal String.Empty

                use document = parseJsonOutput standardOut

                document
                    .RootElement
                    .GetProperty("Error")
                    .GetString()
                |> should contain "watch is a continuous foreground workflow"

                File.Exists(ipcFileName) |> should equal true))
