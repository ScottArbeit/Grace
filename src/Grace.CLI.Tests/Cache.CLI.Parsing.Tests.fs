namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework

/// Groups pure parser coverage for the repository-independent Cache status command.
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
