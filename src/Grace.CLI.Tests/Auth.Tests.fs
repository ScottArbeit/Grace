namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI.Command
open Grace.Shared
open Grace.Types
open NUnit.Framework
open System

[<NonParallelizable>]
module AuthTests =
    let private withEnv (name: string) (value: string option) (action: unit -> unit) =
        let original = Environment.GetEnvironmentVariable(name)

        match value with
        | Some v -> Environment.SetEnvironmentVariable(name, v)
        | None -> Environment.SetEnvironmentVariable(name, null)

        try
            action ()
        finally
            Environment.SetEnvironmentVariable(name, original)

    let private clearOidcEnv (action: unit -> unit) =
        withEnv Constants.EnvironmentVariables.GraceAuthOidcAuthority None (fun () ->
            withEnv Constants.EnvironmentVariables.GraceAuthOidcAudience None (fun () ->
                withEnv Constants.EnvironmentVariables.GraceAuthOidcCliClientId None (fun () ->
                    withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientId None (fun () ->
                        withEnv Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret None (fun () ->
                            withEnv Constants.EnvironmentVariables.GraceServerUri None action)))))

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
