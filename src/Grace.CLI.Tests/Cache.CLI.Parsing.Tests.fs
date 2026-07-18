namespace Grace.CLI.Tests

open System.Diagnostics
open System.ComponentModel
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open NUnit.Framework

/// Verifies parse-only Grace Cache command registration and transport safeguards.
[<Parallelizable(ParallelScope.All)>]
module CacheCommandParsingTests =

    /// Verifies the normal cache lifecycle commands parse without loading repository configuration.
    [<TestCase("run")>]
    [<TestCase("status")>]
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

    /// Verifies non-enrollment cache children receive no inherited Grace credentials while enrollment gets only its resolved transient token.
    [<Test>]
    let ``cache child environment removes inherited credentials`` () =
        let child = ProcessStartInfo("Grace.Cache")
        child.UseShellExecute <- false

        child.Environment[
            Constants.EnvironmentVariables.GraceToken
        ] <- "operator-token"

        child.Environment[
            Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret
        ] <- "m2m-secret"

        child.Environment[ "GRACE_CACHE_ENROLLMENT_TOKEN" ] <- "stale-enrollment-token"

        child.Environment[
            Constants.EnvironmentVariables.GraceServerUri
        ] <- "https://server.example.test/grace/"

        CacheCommand.removeAuthenticationEnvironment child

        Assert.That(child.Environment.ContainsKey(Constants.EnvironmentVariables.GraceToken), Is.False)
        Assert.That(child.Environment.ContainsKey(Constants.EnvironmentVariables.GraceAuthOidcM2mClientSecret), Is.False)
        Assert.That(child.Environment.ContainsKey("GRACE_CACHE_ENROLLMENT_TOKEN"), Is.False)
        Assert.That(child.Environment[Constants.EnvironmentVariables.GraceServerUri], Is.EqualTo("https://server.example.test/grace/"))

        let enrollment = CacheCommand.createProcessStartInfo "Grace.Cache" (Some "resolved-enrollment-token") (Some "https://server.example.test/grace/")
        let nonEnrollment = CacheCommand.createProcessStartInfo "Grace.Cache" None None

        Assert.That(enrollment.Environment["GRACE_CACHE_ENROLLMENT_TOKEN"], Is.EqualTo("resolved-enrollment-token"))
        Assert.That(enrollment.Environment[Constants.EnvironmentVariables.GraceServerUri], Is.EqualTo("https://server.example.test/grace/"))
        Assert.That(nonEnrollment.Environment.ContainsKey("GRACE_CACHE_ENROLLMENT_TOKEN"), Is.False)
        Assert.That(nonEnrollment.Environment.ContainsKey(Constants.EnvironmentVariables.GraceToken), Is.False)

        Assert.That(
            CacheCommand.tryGetEffectiveServerUri (Some "https://environment.example.test/grace/") (Some "https://configured.example.test/"),
            Is.EqualTo(Some "https://environment.example.test/grace/")
        )

        Assert.That(
            CacheCommand.tryGetEffectiveServerUri None (Some "https://configured.example.test/grace/api/"),
            Is.EqualTo(Some "https://configured.example.test/grace/api/")
        )

        Assert.That(CacheCommand.tryGetEffectiveServerUri None None, Is.EqualTo(None))

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

    /// Verifies a cache executable launch failure is converted to the stable exit-one result without rendering launch details.
    [<Test>]
    let ``cache executable launch failures are redacted`` () =
        let secretPath = "C:\\sensitive\\Grace.Cache.exe"

        let exitCode = CacheCommand.invokeProcessWith (fun _ -> raise (new Win32Exception($"Could not start {secretPath}"))) secretPath [ "--status" ] None None

        Assert.That(exitCode, Is.EqualTo(1))
