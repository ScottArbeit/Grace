namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI.Command
open Grace.Shared
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

    [<Test>]
    let ``tryGetAccessToken returns None when auth is not configured`` () =
        withEnv Constants.EnvironmentVariables.GraceAuthMicrosoftCliClientId None (fun () ->
            withEnv Constants.EnvironmentVariables.GraceAuthMicrosoftApiScope None (fun () ->
                let result = Auth.tryGetAccessToken().GetAwaiter().GetResult()
                result |> should equal None))
