namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework

[<Parallelizable(ParallelScope.All)>]
module DoctorCliParsingTests =

    [<Test>]
    let ``doctor command accepts v1 options`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "doctor"
                    "--full"
                    "--offline"
                    "--list-checks"
                    "--check"
                    "config"
                    "--check"
                    "cli.catalog"
                    "--strict"
                |]
            )

        parseResult.Errors.Count |> should equal 0

    [<Test>]
    let ``doctor command accepts global introspection and selection options`` () =
        let cases =
            [
                [| "doctor"; "--schema" |]
                [| "doctor"; "--examples" |]
                [|
                    "--output"
                    "Json"
                    "doctor"
                    "--select"
                    "Status"
                |]
            ]

        for args in cases do
            let parseResult = GraceCommand.rootCommand.Parse(args)

            parseResult.Errors.Count |> should equal 0

    [<Test>]
    let ``doctor command rejects repair option in scaffold slice`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "doctor"; "--repair" |])

        parseResult.Errors.Count
        |> should be (greaterThan 0)
