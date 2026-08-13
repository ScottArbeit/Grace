namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework

/// Groups pure parser coverage for repository-independent Cache commands.
[<Parallelizable(ParallelScope.All)>]
module CacheCliParsingTests =

    /// Verifies the Cache status leaf accepts global output and introspection options without repository configuration.
    [<Test>]
    let ``cache status parser accepts output schema and examples`` () =
        for args in
            [
                [| "cache"; "status" |]
                [|
                    "--output"
                    "Json"
                    "cache"
                    "status"
                |]
                [| "cache"; "status"; "--schema" |]
                [| "cache"; "status"; "--examples" |]
            ] do
            let parseResult = GraceCommand.rootCommand.Parse(args)
            parseResult.Errors.Count |> should equal 0

    /// Verifies the approved enrollment grammar accepts repeated repository assignments and derived defaults without repository configuration.
    [<Test>]
    let ``cache enroll parser accepts required derived-boundary grammar`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "cache"
                    "enroll"
                    "--owner-id"
                    "11111111-1111-1111-1111-111111111111"
                    "--repository"
                    "22222222-2222-2222-2222-222222222222/33333333-3333-3333-3333-333333333333"
                    "--repository"
                    "22222222-2222-2222-2222-222222222222/44444444-4444-4444-4444-444444444444"
                    "--endpoint"
                    "https://cache.example.test/enroll"
                |]
            )

        parseResult.Errors.Count |> should equal 0
