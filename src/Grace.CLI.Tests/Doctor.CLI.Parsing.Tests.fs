namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open NUnit.Framework

/// Groups doctor cli parsing coverage for the CLI test project.
[<Parallelizable(ParallelScope.All)>]
module DoctorCliParsingTests =

    let emptyCheckCases: obj array = [| [| "doctor"; "--check" |] :> obj |]

    /// Verifies that doctor command accepts v1 options.
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

    /// Verifies that doctor command accepts global introspection and selection options.
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

    /// Verifies that doctor command rejects repair option in scaffold slice.
    [<Test>]
    let ``doctor command rejects repair option in scaffold slice`` () =
        let parseResult = GraceCommand.rootCommand.Parse([| "doctor"; "--repair" |])

        parseResult.Errors.Count
        |> should be (greaterThan 0)

    /// Verifies that doctor command rejects check option without value.
    [<TestCaseSource(nameof emptyCheckCases)>]
    let ``doctor command rejects check option without value`` (args: string array) =
        let parseResult = GraceCommand.rootCommand.Parse(args)

        parseResult.Errors.Count
        |> should be (greaterThan 0)
