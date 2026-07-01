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
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Groups auth coverage for the CLI test project.
[<NonParallelizable>]
module AuthTests =
    /// Sets ansi console output needed by the test scenario.
    let private setAnsiConsoleOutput (writer: TextWriter) =
        let settings = AnsiConsoleSettings()
        settings.Out <- AnsiConsoleOutput(writer)
        AnsiConsole.Console <- AnsiConsole.Create(settings)

    /// Runs with captured stdout and stderr for test scenarios.
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

    /// Parses json output for test assertions.
    let private parseJsonOutput (output: string) =
        output
        |> (fun value -> value.Trim())
        |> JsonDocument.Parse

    /// Runs the supplied action with env applied.
    let private withEnv (name: string) (value: string option) (action: unit -> unit) =
        let original = Environment.GetEnvironmentVariable(name)

        match value with
        | Some v -> Environment.SetEnvironmentVariable(name, v)
        | None -> Environment.SetEnvironmentVariable(name, null)

        try
            action ()
        finally
            Environment.SetEnvironmentVariable(name, original)

    /// Runs the supplied action with cleared env applied.
    let private withClearedEnv names action =
        /// Runs the authentication loop until the callback scenario completes or fails.
        let rec loop remaining =
            match remaining with
            | [] -> action ()
            | name :: tail -> withEnv name None (fun () -> loop tail)

        loop names

    /// Clears oidc env for isolated test execution.
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

    /// Runs the scenario with authentication environment variables removed from the process context.
    let private withoutAuthEnv (action: unit -> unit) = clearOidcEnv (fun () -> withEnv Constants.EnvironmentVariables.GraceToken None action)

    /// Runs the supplied action with single http response applied.
    let private withSingleHttpResponse (statusCode: int) (reasonPhrase: string) (body: string) (action: unit -> unit) =
        use listener = new TcpListener(IPAddress.Loopback, 0)
        use cancellation = new CancellationTokenSource()
        listener.Start()

        /// Writes response needed by the test scenario.
        let writeResponse (client: TcpClient) =
            task {
                use client = client
                use stream = client.GetStream()
                let buffer = Array.zeroCreate<byte> 8192
                let! _ = stream.ReadAsync(buffer, 0, buffer.Length)
                let bodyBytes = Encoding.UTF8.GetBytes(body)

                let responseHeaders = $"HTTP/1.1 {statusCode} {reasonPhrase}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n"

                let headerBytes = Encoding.ASCII.GetBytes(responseHeaders)
                do! stream.WriteAsync(headerBytes, 0, headerBytes.Length)

                if bodyBytes.Length > 0 then
                    do! stream.WriteAsync(bodyBytes, 0, bodyBytes.Length)
            }

        /// Hosts the loopback callback endpoint used by browser-based authentication scenarios.
        let rec serve () =
            task {
                if not cancellation.IsCancellationRequested then
                    try
                        let! client =
                            listener
                                .AcceptTcpClientAsync(cancellation.Token)
                                .AsTask()

                        do! writeResponse client
                        return! serve ()
                    with
                    | :? OperationCanceledException -> return ()
                    | :? ObjectDisposedException -> return ()
            }

        let serverTask = Task.Run(Func<Task>(fun () -> serve ()))

        let port = (listener.LocalEndpoint :?> IPEndPoint).Port

        try
            withEnv Constants.EnvironmentVariables.GraceServerUri (Some $"http://127.0.0.1:{port}") action
        finally
            cancellation.Cancel()
            listener.Stop()

            if not (serverTask.Wait(TimeSpan.FromSeconds(5.0))) then
                Assert.Fail("Timed out waiting for the test HTTP responder to stop.")

    /// Asserts that auth status json matches the expected contract.
    let private assertAuthStatusJson (output: string) =
        use document = parseJsonOutput output

        document
            .RootElement
            .GetProperty("ReturnValue")
            .Clone()

    /// Determines whether missing or null for test assertions.
    let private isMissingOrNull (root: JsonElement) (name: string) =
        match root.TryGetProperty(name) with
        | false, _ -> true
        | true, property -> property.ValueKind = JsonValueKind.Null

    /// Asserts that boolean property matches the expected contract.
    let private assertBooleanProperty (root: JsonElement) (name: string) (expected: bool) =
        /// Tracks property changes so this scenario can assert the resulting side effect explicitly.
        let mutable property = Unchecked.defaultof<JsonElement>

        root.TryGetProperty(name, &property)
        |> should equal true

        (property.ValueKind = JsonValueKind.True
         || property.ValueKind = JsonValueKind.False)
        |> should equal true

        property.GetBoolean() |> should equal expected

    /// Asserts that serialized auth status json matches the expected contract.
    let private assertSerializedAuthStatusJson (status: Auth.AuthStatusOutput) =
        use document =
            JsonSerializer.Serialize(status, Constants.JsonSerializerOptions)
            |> parseJsonOutput

        document.RootElement.Clone()

    /// Builds the default authentication status context used by auth-status output assertions.
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
            SecureStoreAvailable = false
            SecureStoreError = None
            ConfigError = None
            Now = now
        }

    /// Verifies that try get access token returns error when auth is not configured.
    [<Test>]
    let ``tryGetAccessToken returns Error when auth is not configured`` () =
        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken None (fun () ->
                let result = Auth.tryGetAccessToken().GetAwaiter().GetResult()

                match result with
                | Ok _ -> Assert.Fail("Expected Error for missing auth configuration.")
                | Error _ -> ()))

    /// Verifies that try get access token prefers grace token env var.
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

    /// Verifies that try get access token rejects invalid grace token.
    [<Test>]
    let ``tryGetAccessToken rejects invalid GRACE_TOKEN`` () =
        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some "not-a-pat") (fun () ->
                let result = Auth.tryGetAccessToken().GetAwaiter().GetResult()

                match result with
                | Ok _ -> Assert.Fail("Expected Error for invalid GRACE_TOKEN.")
                | Error _ -> ()))

    /// Verifies that auth status human output writes authenticated banner before active source.
    [<Test>]
    let ``auth status human output writes authenticated banner before active source`` () =
        let token = PersonalAccessToken.formatToken "user-1" (Guid.NewGuid()) (Array.zeroCreate 32)

        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                /// Verifies that the CLI auth scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "authenticate"
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

    /// Verifies that auth status json emits structured authenticated return value.
    [<Test>]
    let ``auth status json emits structured authenticated return value`` () =
        let token = PersonalAccessToken.formatToken "user-1" (Guid.NewGuid()) (Array.zeroCreate 32)

        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                /// Verifies that the CLI auth scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "authenticate"
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

    /// Verifies that auth status json reports unauthenticated when no source is available.
    [<Test>]
    let ``auth status json reports unauthenticated when no source is available`` () =
        withoutAuthEnv (fun () ->
            /// Verifies that the CLI auth scenario exits with the expected process status.
            let exitCode, standardOut, standardError =
                runWithCapturedStdoutAndStderr [| "--output"
                                                  "Json"
                                                  "authenticate"
                                                  "status" |]

            exitCode |> should equal 0
            standardError |> should equal String.Empty

            let returnValue = assertAuthStatusJson standardOut

            assertBooleanProperty returnValue "Authenticated" false

            returnValue.GetProperty("Status").GetString()
            |> should equal "Not authenticated"

            returnValue
                .GetProperty("ActiveSource")
                .GetString()
            |> should equal "None"

            let sources = returnValue.GetProperty("Sources")
            let graceToken = sources.GetProperty("GraceToken")
            let graceTokenFile = sources.GetProperty("GraceTokenFile")
            let m2m = sources.GetProperty("M2m")
            let interactive = sources.GetProperty("Interactive")

            assertBooleanProperty graceToken "Present" false
            assertBooleanProperty graceToken "Valid" false
            assertBooleanProperty graceTokenFile "Present" false
            assertBooleanProperty graceTokenFile "Supported" false
            assertBooleanProperty m2m "Configured" false
            assertBooleanProperty interactive "Configured" false
            assertBooleanProperty interactive "TokenPresent" false
            assertBooleanProperty interactive "SecureStoreAvailable" false)

    /// Verifies that auth status json reports invalid grace token without leaking raw value.
    [<Test>]
    let ``auth status json reports invalid GRACE_TOKEN without leaking raw value`` () =
        let token = "not-a-pat-secret-value"

        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                /// Verifies that the CLI auth scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Json"
                                                      "authenticate"
                                                      "status" |]

                exitCode |> should equal 0
                standardError |> should equal String.Empty
                standardOut |> should not' (contain token)

                let returnValue = assertAuthStatusJson standardOut

                assertBooleanProperty returnValue "Authenticated" false

                returnValue
                    .GetProperty("ActiveSource")
                    .GetString()
                |> should equal "Environment (GRACE_TOKEN invalid)"

                let graceToken =
                    returnValue
                        .GetProperty("Sources")
                        .GetProperty("GraceToken")

                assertBooleanProperty graceToken "Present" true
                assertBooleanProperty graceToken "Valid" false

                returnValue
                    .GetProperty("Diagnostics")
                    .GetArrayLength()
                |> should be (greaterThan 0)))

    /// Verifies that auth status json reports configured m2 m as unverified and unauthenticated.
    [<Test>]
    let ``auth status json reports configured M2M as unverified and unauthenticated`` () =
        let m2mSecret = "m2m-client-secret-value"

        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken None (fun () ->
                withEnv Constants.EnvironmentVariables.GraceAuthOidcAuthority (Some "https://tenant.example.com/") (fun () ->
                    withEnv Constants.EnvironmentVariables.GraceAuthOidcAudience (Some "https://api.example.com") (fun () ->
                        withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientId (Some "m2m-client-id") (fun () ->
                            withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret (Some m2mSecret) (fun () ->
                                /// Verifies that the CLI auth scenario exits with the expected process status.
                                let exitCode, standardOut, standardError =
                                    runWithCapturedStdoutAndStderr [| "--output"
                                                                      "Json"
                                                                      "authenticate"
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

    /// Verifies that auth status json keeps stale interactive cache nested when grace token is active.
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
                    SecureStoreAvailable = true
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

    /// Verifies that auth status json reports expired interactive cache as unauthenticated.
    [<Test>]
    let ``auth status json reports expired interactive cache as unauthenticated`` () =
        let expiresAt = Instant.FromUtc(2024, 1, 1, 0, 0)

        let status =
            Auth.buildAuthStatus
                { defaultStatusContext (expiresAt.Plus(Duration.FromDays(30.0))) with
                    InteractiveConfigured = true
                    InteractiveTokenPresent = true
                    SecureStoreAvailable = true
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

    /// Verifies that auth status json promotes fresh interactive cache details when interactive is active.
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
                    SecureStoreAvailable = true
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

    /// Verifies that auth whoami reports empty unauthorized body without json parse exception.
    [<Test>]
    let ``auth whoami reports empty unauthorized body without json parse exception`` () =
        withoutAuthEnv (fun () ->
            withSingleHttpResponse 401 "Unauthorized" String.Empty (fun () ->
                /// Verifies that the CLI auth scenario exits with the expected process status.
                let exitCode, standardOut, standardError =
                    runWithCapturedStdoutAndStderr [| "--output"
                                                      "Verbose"
                                                      "authenticate"
                                                      "whoami" |]

                exitCode |> should not' (equal 0)
                standardOut |> should contain "401 Unauthorized"
                standardOut |> should contain "authenticate/me"

                standardOut
                |> should not' (contain "JsonException")

                standardOut
                |> should not' (contain "The input does not contain any JSON tokens")

                standardError |> should equal String.Empty))

    /// Verifies that auth token create reports empty unauthorized body without json parse exception.
    [<Test>]
    let ``auth token create reports empty unauthorized body without json parse exception`` () =
        let token = PersonalAccessToken.formatToken "user-1" (Guid.NewGuid()) (Array.zeroCreate 32)

        clearOidcEnv (fun () ->
            withEnv Constants.EnvironmentVariables.GraceToken (Some token) (fun () ->
                withSingleHttpResponse 401 "Unauthorized" String.Empty (fun () ->
                    /// Verifies that the CLI auth scenario exits with the expected process status.
                    let exitCode, standardOut, standardError =
                        runWithCapturedStdoutAndStderr [| "--output"
                                                          "Verbose"
                                                          "authenticate"
                                                          "token"
                                                          "create"
                                                          "--name"
                                                          "local-dev"
                                                          "--expires-in"
                                                          "30d" |]

                    exitCode |> should not' (equal 0)
                    standardOut |> should contain "401 Unauthorized"

                    standardOut
                    |> should contain "authenticate/token/create"

                    standardOut
                    |> should not' (contain "JsonException")

                    standardOut
                    |> should not' (contain "The input does not contain any JSON tokens")

                    standardError |> should equal String.Empty)))
