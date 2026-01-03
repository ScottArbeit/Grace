namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI.Command
open Grace.Shared
open NUnit.Framework
open System
open System.IO

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

    [<Test>]
    let ``tryGetAccessToken returns None when auth is not configured`` () =
        withEnv Constants.EnvironmentVariables.GraceAuthMicrosoftCliClientId None (fun () ->
            withEnv Constants.EnvironmentVariables.GraceAuthMicrosoftApiScope None (fun () ->
                let result = Auth.tryGetAccessToken().GetAwaiter().GetResult()
                result |> should equal None))

    [<Test>]
    let ``tryGetAccessToken prefers GRACE_TOKEN env var`` () =
        withEnv Constants.EnvironmentVariables.GraceToken (Some "Bearer env-token") (fun () ->
            withEnv Constants.EnvironmentVariables.GraceAuthMicrosoftCliClientId None (fun () ->
                withEnv Constants.EnvironmentVariables.GraceAuthMicrosoftApiScope None (fun () ->
                    let result = Auth.tryGetAccessToken().GetAwaiter().GetResult()
                    result |> should equal (Some "env-token"))))

    [<Test>]
    let ``tryGetAccessToken uses token file when env var missing`` () =
        let tempPath = Path.Combine(Path.GetTempPath(), $"grace-token-{Guid.NewGuid():N}")

        try
            File.WriteAllText(tempPath, "file-token")

            withEnv Constants.EnvironmentVariables.GraceToken None (fun () ->
                withEnv Constants.EnvironmentVariables.GraceTokenFile (Some tempPath) (fun () ->
                    withEnv Constants.EnvironmentVariables.GraceAuthMicrosoftCliClientId None (fun () ->
                        withEnv Constants.EnvironmentVariables.GraceAuthMicrosoftApiScope None (fun () ->
                            let result = Auth.tryGetAccessToken().GetAwaiter().GetResult()
                            result |> should equal (Some "file-token")))))
        finally
            if File.Exists(tempPath) then File.Delete(tempPath)
