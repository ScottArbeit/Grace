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

    /// Verifies machine-scoped cache commands are eligible outside a repository while unrelated commands retain the repository gate.
    [<Test>]
    let ``cache bypasses only the repository configuration gate`` () =
        Assert.That(GraceCommand.isAllowedWithoutRepositoryConfiguration "cache" true, Is.True)
        Assert.That(GraceCommand.isAllowedWithoutRepositoryConfiguration "CACHE" true, Is.True)
        Assert.That(GraceCommand.isAllowedWithoutRepositoryConfiguration "branch" true, Is.False)

    /// Verifies root help presents the supported cache workflow with local machine utilities rather than the fallback section.
    [<Test>]
    let ``cache belongs to the local utilities root help section`` () =
        let localUtilities =
            GraceCommand.rootHelpSections
            |> List.find (fun section -> section.Heading = "Local utilities")

        Assert.That(localUtilities.CommandNames, Does.Contain("cache"))

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

    /// Verifies System.CommandLine rejects an explicit false value after the marker-only HTTP exception before process invocation.
    [<Test>]
    let ``cache enroll rejects explicit false allow http value`` () =
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
                    "--endpoint"
                    "http://cache.example.test"
                    "--allow-http"
                    "false"
                |]
            )

        Assert.That(parseResult.Errors.Count, Is.GreaterThan(0))

    /// Verifies cache-enrollment endpoint paths, queries, and fragments are rejected by the CLI before it starts a child process.
    [<TestCase("https://cache.example.test/cache")>]
    [<TestCase("https://cache.example.test/?preview=true")>]
    [<TestCase("https://cache.example.test/#fragment")>]
    let ``cache enroll rejects non-origin endpoints`` endpoint =
        Grace.CLI.Command.CacheCommand.validateEndpoint endpoint false
        |> Result.isError
        |> Assert.That
