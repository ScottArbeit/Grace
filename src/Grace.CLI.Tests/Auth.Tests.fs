namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Types
open NodaTime
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

    let private isMissingOrNull (root: JsonElement) (name: string) =
        match root.TryGetProperty(name) with
        | false, _ -> true
        | true, property -> property.ValueKind = JsonValueKind.Null

    let private assertSerializedAuthStatusJson (status: Auth.AuthStatusOutput) =
        use document =
            JsonSerializer.Serialize(status, Constants.JsonSerializerOptions)
            |> parseJsonOutput

        document.RootElement.Clone()

    let private defaultStatusContext now : Auth.AuthStatusContext =
        {
            GraceTokenPresent = false
            GraceTokenValid = false
            GraceTokenError = None
            GraceTokenFilePresent = false
            GraceTokenFileError = None
            M2mConfigured = false
            InteractiveConfigured = false
            InteractiveTokenPresent = false
            InteractiveExpiresAt = None
            InteractiveSubject = None
            SecureStoreAvailable = true
            SecureStoreError = None
            ConfigError = None
            Now = now
        }

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
    let ``auth status human output writes authenticated banner before active source`` () =
        let token = PersonalAccessToken.formatToken "user-1" (Guid.NewGuid()) (Array.zeroCreate 32)

        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "auth"
                                                      "status" |]

                exitCode |> should equal 0
                standardError |> should equal String.Empty

                let normalized = standardOut.Replace("\r\n", "\n")

                let authenticatedIndex = normalized.IndexOf("Authenticated")
                let activeSourceIndex = normalized.IndexOf("Active source:")
                let tokenIndex = normalized.IndexOf("GRACE_TOKEN:")

                authenticatedIndex
                |> should be (greaterThanOrEqualTo 0)

                activeSourceIndex
                |> should be (greaterThan authenticatedIndex)

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
                |> should equal 0

                isMissingOrNull returnValue "AccessTokenExpiresAt"
                |> should equal true

                isMissingOrNull returnValue "Subject"
                |> should equal true))

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

    [<Test>]
    let ``auth status json reports configured M2M as unverified and unauthenticated`` () =
        let m2mSecret = "m2m-client-secret-value"

        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken None (fun () ->
                withEnv Constants.EnvironmentVariables.GraceAuthOidcAuthority (Some "https://tenant.example.com/") (fun () ->
                    withEnv Constants.EnvironmentVariables.GraceAuthOidcAudience (Some "https://api.example.com") (fun () ->
                        withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientId (Some "m2m-client-id") (fun () ->
                            withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret (Some m2mSecret) (fun () ->
                                let exitCode, standardOut, standardError =
                                    runWithCapturedStdoutAndStderr [| "--output"
                                                                      "Json"
                                                                      "auth"
                                                                      "status" |]

                                exitCode |> should equal 0
                                standardError |> should equal String.Empty
                                standardOut |> should not' (contain m2mSecret)

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
                                |> should equal "M2M (client credentials)"

                                returnValue
                                    .GetProperty("Sources")
                                    .GetProperty("M2m")
                                    .GetProperty("Configured")
                                    .GetBoolean()
                                |> should equal true

                                isMissingOrNull returnValue "AccessTokenExpiresAt"
                                |> should equal true

                                isMissingOrNull returnValue "Subject"
                                |> should equal true))))))

    [<Test>]
    let ``auth status json keeps stale interactive cache nested when GRACE_TOKEN is active`` () =
        let expiresAt = Instant.FromUtc(2024, 1, 1, 0, 0)
        let subject = "cached-user-1"

        let status =
            Auth.buildAuthStatus
                { defaultStatusContext (expiresAt.Plus(Duration.FromDays(30.0))) with
                    GraceTokenPresent = true
                    GraceTokenValid = true
                    InteractiveConfigured = true
                    InteractiveTokenPresent = true
                    InteractiveExpiresAt = Some expiresAt
                    InteractiveSubject = Some subject
                }

        let returnValue = assertSerializedAuthStatusJson status

        returnValue
            .GetProperty("Authenticated")
            .GetBoolean()
        |> should equal true

        returnValue
            .GetProperty("ActiveSource")
            .GetString()
        |> should equal "Environment (GRACE_TOKEN)"

        isMissingOrNull returnValue "AccessTokenExpiresAt"
        |> should equal true

        isMissingOrNull returnValue "Subject"
        |> should equal true

        let interactive =
            returnValue
                .GetProperty("Sources")
                .GetProperty("Interactive")

        interactive
            .GetProperty("TokenPresent")
            .GetBoolean()
        |> should equal true

        interactive
            .GetProperty("AccessTokenExpiresAt")
            .GetString()
        |> should equal "2024-01-01T00:00:00Z"

        interactive.GetProperty("Subject").GetString()
        |> should equal subject

    [<Test>]
    let ``auth status json reports expired interactive cache as unauthenticated`` () =
        let expiresAt = Instant.FromUtc(2024, 1, 1, 0, 0)

        let status =
            Auth.buildAuthStatus
                { defaultStatusContext (expiresAt.Plus(Duration.FromDays(30.0))) with
                    InteractiveConfigured = true
                    InteractiveTokenPresent = true
                    InteractiveExpiresAt = Some expiresAt
                    InteractiveSubject = Some "expired-user"
                }

        let returnValue = assertSerializedAuthStatusJson status

        returnValue
            .GetProperty("Authenticated")
            .GetBoolean()
        |> should equal false

        returnValue.GetProperty("Status").GetString()
        |> should equal "Not authenticated"

        returnValue
            .GetProperty("ActiveSource")
            .GetString()
        |> should equal "Interactive (cached token expired)"

        isMissingOrNull returnValue "AccessTokenExpiresAt"
        |> should equal true

        isMissingOrNull returnValue "Subject"
        |> should equal true

        returnValue
            .GetProperty("Sources")
            .GetProperty("Interactive")
            .GetProperty("AccessTokenExpiresAt")
            .GetString()
        |> should equal "2024-01-01T00:00:00Z"

    [<Test>]
    let ``auth status json promotes fresh interactive cache details when interactive is active`` () =
        let now = Instant.FromUtc(2024, 1, 1, 0, 0)
        let expiresAt = now.Plus(Duration.FromHours(2.0))
        let subject = "fresh-user"

        let status =
            Auth.buildAuthStatus
                { defaultStatusContext now with
                    InteractiveConfigured = true
                    InteractiveTokenPresent = true
                    InteractiveExpiresAt = Some expiresAt
                    InteractiveSubject = Some subject
                }

        let returnValue = assertSerializedAuthStatusJson status

        returnValue
            .GetProperty("Authenticated")
            .GetBoolean()
        |> should equal true

        returnValue
            .GetProperty("ActiveSource")
            .GetString()
        |> should equal "Interactive (cached token)"

        returnValue
            .GetProperty("AccessTokenExpiresAt")
            .GetString()
        |> should equal "2024-01-01T02:00:00Z"

        returnValue.GetProperty("Subject").GetString()
        |> should equal subject
