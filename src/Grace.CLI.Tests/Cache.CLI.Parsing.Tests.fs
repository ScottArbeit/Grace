namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework
open System

/// Groups parser coverage for static Cache enrollment and local status commands.
[<Parallelizable(ParallelScope.All)>]
module CacheCliParsingTests =

    /// Verifies that cache enrollment accepts its explicit static identity inputs without a removed health input.
    [<Test>]
    let ``cache enroll accepts explicit static identity inputs`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "cache"
                    "enroll"
                    "--display-name"
                    "test-cache"
                    "--endpoint"
                    "https://cache.example.test"
                    "--boundary"
                    "organization"
                    "--owner-id"
                    (Guid.NewGuid().ToString())
                    "--organization-id"
                    (Guid.NewGuid().ToString())
                    "--repository-organization-id"
                    (Guid.NewGuid().ToString())
                    "--repository-id"
                    (Guid.NewGuid().ToString())
                |]
            )

        parseResult.Errors.Count |> should equal 0

    /// Verifies that cache status participates in standard JSON contract introspection.
    [<Test>]
    let ``cache status accepts standard output contract options`` () =
        for args in
            [
                [| "cache"; "status"; "--schema" |]
                [| "cache"; "status"; "--examples" |]
            ] do
            GraceCommand.rootCommand.Parse(args).Errors.Count
            |> should equal 0
