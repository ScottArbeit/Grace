namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Types
open NUnit.Framework
open Spectre.Console
open System
open System.IO
open System.Text.Json

[<NonParallelizable>]
module AuthTests =
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

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
        |> fun value -> value.Trim()
        |> JsonDocument.Parse

    let private withEnv (name: string) (value: string option) (action: unit -> unit) =
        let original = Environment.GetEnvironmentVariable(name)

        match value with
        | Some v -> Environment.SetEnvironmentVariable(name, v)
        | None -> Environment.SetEnvironmentVariable(name, null)

        try
            action ()
        finally
            Environment.SetEnvironmentVariable(name, original)

    let private withClearedEnv names action =
        let rec loop remaining =
            match remaining with
            | [] -> action ()
            | name :: tail -> withEnv name None (fun () -> loop tail)

        loop names

    let private clearOidcEnv (action: unit -> unit) =
        withClearedEnv
            [
                Constants.EnvironmentVariables.GraceAuthOidcAuthority
                Constants.EnvironmentVariables.GraceAuthOidcAudience
                Constants.EnvironmentVariables.GraceAuthOidcCliClientId
                Constants.EnvironmentVariables.GraceAuthOidcCliRedirectPort
                Constants.EnvironmentVariables.GraceAuthOidcCliScopes
                Constants.EnvironmentVariables.GraceAuthOidcM2mClientId
                Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret
                Constants.EnvironmentVariables.GraceAuthOidcM2mScopes
                Constants.EnvironmentVariables.GraceTokenFile
                Constants.EnvironmentVariables.GraceServerUri
                "USERPROFILE"
                "HOME"
                "LOCALAPPDATA"
                "APPDATA"
            ]
            action

    let private withoutAuthEnv (action: unit -> unit) = clearOidcEnv (fun () -> withEnv Constants.EnvironmentVariables.GraceToken None action)

    let private assertAuthStatusJson (output: string) =
        use document = parseJsonOutput output

        document
            .RootElement
            .GetProperty("ReturnValue")
            .Clone()

    [<Test>]
    let ``tryGetAccessToken returns Error when auth is not configured`` () =
        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken None (fun () ->
                let result = Auth.tryGetAccessToken().GetAwaiter().GetResult()

                match result with
                | Ok _ -> Assert.Fail("Expected Error for missing auth configuration.")
                | Error _ -> ()))

    [<Test>]
    let ``tryGetAccessToken prefers GRACE_TOKEN env var`` () =
        let token = PersonalAccessToken.formatToken "user-1" (Guid.NewGuid()) (Array.zeroCreate 32)

        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                let result = Auth.tryGetAccessToken().GetAwaiter().GetResult()

                match result with
                | Ok (Some value) -> value |> should equal token
                | Ok None -> Assert.Fail("Expected GRACE_TOKEN to be returned.")
                | Error message -> Assert.Fail($"Unexpected error: {message}")))

    [<Test>]
    let ``tryGetAccessToken rejects invalid GRACE_TOKEN`` () =
        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some "not-a-pat") (fun () ->
                let result = Auth.tryGetAccessToken().GetAwaiter().GetResult()

                match result with
                | Ok _ -> Assert.Fail("Expected Error for invalid GRACE_TOKEN.")
                | Error _ -> ()))

    [<Test>]
    let ``auth status human output starts with authenticated banner and active source`` () =
        let token = PersonalAccessToken.formatToken "user-1" (Guid.NewGuid()) (Array.zeroCreate 32)

        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "auth"
                                                      "status" |]

                exitCode |> should equal 0
                standardError |> should equal String.Empty

                let normalized = standardOut.Replace("\r\n", "\n")

                normalized.StartsWith("Authenticated\n\n")
                |> should equal true

                let activeSourceIndex = normalized.IndexOf("Active source:")
                let tokenIndex = normalized.IndexOf("GRACE_TOKEN:")

                activeSourceIndex
                |> should be (greaterThanOrEqualTo 0)

                tokenIndex
                |> should be (greaterThan activeSourceIndex)

                normalized
                |> should contain "Environment (GRACE_TOKEN)"

                normalized |> should not' (contain token)))

    [<Test>]
    let ``auth status json emits structured authenticated return value`` () =
        let token = PersonalAccessToken.formatToken "user-1" (Guid.NewGuid()) (Array.zeroCreate 32)

        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "auth"
                                                      "status" |]

                exitCode |> should equal 0
                standardError |> should equal String.Empty
                standardOut |> should not' (contain token)

                let returnValue = assertAuthStatusJson standardOut

                returnValue
                    .GetProperty("Authenticated")
                    .GetBoolean()
                |> should equal true

                returnValue.GetProperty("Status").GetString()
                |> should equal "Authenticated"

                returnValue
                    .GetProperty("ActiveSource")
                    .GetString()
                |> should equal "Environment (GRACE_TOKEN)"

                returnValue
                    .GetProperty("Sources")
                    .GetProperty("GraceToken")
                    .GetProperty("Valid")
                    .GetBoolean()
                |> should equal true

                returnValue
                    .GetProperty("Diagnostics")
                    .GetArrayLength()
                |> should equal 0))

    [<Test>]
    let ``auth status json reports unauthenticated when no source is available`` () =
        withoutAuthEnv (fun () ->
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "auth"
                                                  "status" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            let returnValue = assertAuthStatusJson standardOut

            returnValue
                .GetProperty("Authenticated")
                .GetBoolean()
            |> should equal false

            returnValue.GetProperty("Status").GetString()
            |> should equal "Not authenticated"

            returnValue
                .GetProperty("ActiveSource")
                .GetString()
            |> should equal "None")

    [<Test>]
    let ``auth status json reports invalid GRACE_TOKEN without leaking raw value`` () =
        let token = "not-a-pat-secret-value"

        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "auth"
                                                      "status" |]

                exitCode |> should equal 0
                standardError |> should equal String.Empty
                standardOut |> should not' (contain token)

                let returnValue = assertAuthStatusJson standardOut

                returnValue
                    .GetProperty("Authenticated")
                    .GetBoolean()
                |> should equal false

                returnValue
                    .GetProperty("ActiveSource")
                    .GetString()
                |> should equal "Environment (GRACE_TOKEN invalid)"

                returnValue
                    .GetProperty("Sources")
                    .GetProperty("GraceToken")
                    .GetProperty("Valid")
                    .GetBoolean()
                |> should equal false

                returnValue
                    .GetProperty("Diagnostics")
                    .GetArrayLength()
                |> should be (greaterThan 0)))
