namespace Grace.CLI.Tests

open Grace.CLI
open NUnit.Framework

/// Verifies parse-only Grace Cache command registration and transport safeguards.
[<Parallelizable(ParallelScope.All)>]
module CacheCommandParsingTests =

    /// Verifies the normal cache lifecycle commands parse without loading repository configuration.
    [<TestCase("run")>]
    [<TestCase("status")>]
    [<TestCase("rotate-now")>]
    let ``cache lifecycle command parses`` commandName =
        let parseResult = GraceCommand.rootCommand.Parse([| "cache"; commandName |])
        Assert.That(parseResult.Errors.Count, Is.EqualTo(0))

    /// Verifies enrollment requires exact explicit stable identifiers and an endpoint.
    [<Test>]
    let ``cache enroll parses explicit stable inputs`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "cache"
                    "enroll"
                    "--display-name"
                    "edge-cache"
                    "--owner-id"
                    "11111111-1111-1111-1111-111111111111"
                    "--organization-id"
                    "22222222-2222-2222-2222-222222222222"
                    "--repository-id"
                    "33333333-3333-3333-3333-333333333333"
                    "44444444-4444-4444-4444-444444444444"
                    "--repository-organization-id"
                    "22222222-2222-2222-2222-222222222222"
                    "22222222-2222-2222-2222-222222222222"
                    "--endpoint"
                    "https://cache.example.test"
                |]
            )

        Assert.That(parseResult.Errors.Count, Is.EqualTo(0))

    /// Verifies the parser requires the endpoint instead of allowing a silent transport default.
    [<Test>]
    let ``cache enroll requires endpoint`` () =
        let parseResult =
            GraceCommand.rootCommand.Parse(
                [|
                    "cache"
                    "enroll"
                    "--display-name"
                    "edge-cache"
                    "--owner-id"
                    "11111111-1111-1111-1111-111111111111"
                    "--repository-id"
                    "33333333-3333-3333-3333-333333333333"
                    "--repository-organization-id"
                    "22222222-2222-2222-2222-222222222222"
                |]
            )

        Assert.That(parseResult.Errors.Count, Is.GreaterThan(0))
