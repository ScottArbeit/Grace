namespace Grace.Server.Tests

open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Auth
open Microsoft.Extensions.Configuration
open NUnit.Framework
open System.Collections.Generic

/// Covers external Auth Config Unit behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type ExternalAuthConfigUnitTests() =

    /// Builds configuration test data for the server unit external Auth Config scenarios in this file.
    let configuration (values: (string * string) list) =
        let pairs =
            values
            |> List.map (fun (key, value) -> KeyValuePair<string, string>(getConfigKey key, value))

        ConfigurationBuilder()
            .AddInMemoryCollection(pairs)
            .Build()
        :> IConfiguration

    /// Asserts the oidc Config condition so failures identify the violated server unit external Auth Config invariant.
    let assertOidcConfig (expectedAuthority: string) (expectedAudience: string) (result: ExternalAuthConfig.OidcAuthConfig option) =
        match result with
        | Some config ->
            Assert.That(config.Authority, Is.EqualTo(expectedAuthority))
            Assert.That(config.Audience, Is.EqualTo(expectedAudience))
        | None -> Assert.Fail("Expected OIDC config to be returned.")

    /// Asserts the client Config condition so failures identify the violated server unit external Auth Config invariant.
    let assertClientConfig (expectedAuthority: string) (expectedAudience: string) (expectedCliClientId: string) (result: OidcClientConfig option) =
        match result with
        | Some config ->
            Assert.That(config.Authority, Is.EqualTo(expectedAuthority))
            Assert.That(config.Audience, Is.EqualTo(expectedAudience))
            Assert.That(config.CliClientId, Is.EqualTo(expectedCliClientId))
        | None -> Assert.Fail("Expected OIDC client config to be returned.")

    /// Builds oidc Configuration test data for the server unit external Auth Config scenarios in this file.
    let oidcConfiguration authority audience =
        configuration [ Constants.EnvironmentVariables.GraceAuthOidcAuthority, authority
                        Constants.EnvironmentVariables.GraceAuthOidcAudience, audience ]

    /// Builds oidc Client Configuration test data for the server unit external Auth Config scenarios in this file.
    let oidcClientConfiguration authority audience cliClientId =
        configuration [ Constants.EnvironmentVariables.GraceAuthOidcAuthority, authority
                        Constants.EnvironmentVariables.GraceAuthOidcAudience, audience
                        Constants.EnvironmentVariables.GraceAuthOidcCliClientId, cliClientId ]

    /// Verifies that null Configuration Returns None.
    [<Test>]
    member _.NullConfigurationReturnsNone() =
        let nullConfiguration: IConfiguration = null

        Assert.That(ExternalAuthConfig.tryGetOidcConfig nullConfiguration, Is.EqualTo(None))
        Assert.That(ExternalAuthConfig.tryGetOidcClientConfig nullConfiguration, Is.EqualTo(None))
        Assert.That(ExternalAuthConfig.isOidcConfigured nullConfiguration, Is.False)

    /// Verifies that missing Authority Or Audience Returns None.
    [<Test>]
    member _.MissingAuthorityOrAudienceReturnsNone() =
        let missingAuthority = configuration [ Constants.EnvironmentVariables.GraceAuthOidcAudience, "api://grace" ]

        let missingAudience = configuration [ Constants.EnvironmentVariables.GraceAuthOidcAuthority, "https://login.example.test/tenant" ]

        let blankAuthority = oidcConfiguration "   " "api://grace"
        let blankAudience = oidcConfiguration "https://login.example.test/tenant" "   "

        Assert.That(ExternalAuthConfig.tryGetOidcConfig missingAuthority, Is.EqualTo(None))
        Assert.That(ExternalAuthConfig.tryGetOidcConfig missingAudience, Is.EqualTo(None))
        Assert.That(ExternalAuthConfig.tryGetOidcConfig blankAuthority, Is.EqualTo(None))
        Assert.That(ExternalAuthConfig.tryGetOidcConfig blankAudience, Is.EqualTo(None))

        Assert.That(ExternalAuthConfig.isOidcConfigured missingAuthority, Is.False)
        Assert.That(ExternalAuthConfig.isOidcConfigured missingAudience, Is.False)
        Assert.That(ExternalAuthConfig.isOidcConfigured blankAuthority, Is.False)
        Assert.That(ExternalAuthConfig.isOidcConfigured blankAudience, Is.False)

    /// Verifies that values Are Trimmed And Authority Gets One Trailing Slash.
    [<Test>]
    member _.ValuesAreTrimmedAndAuthorityGetsOneTrailingSlash() =
        oidcConfiguration "  https://login.example.test/tenant  " "  api://grace  "
        |> ExternalAuthConfig.tryGetOidcConfig
        |> assertOidcConfig "https://login.example.test/tenant/" "api://grace"

        oidcConfiguration "  https://login.example.test/tenant/  " "  api://grace  "
        |> ExternalAuthConfig.tryGetOidcConfig
        |> assertOidcConfig "https://login.example.test/tenant/" "api://grace"

    /// Verifies that client Config Requires Cli Client Id.
    [<Test>]
    member _.ClientConfigRequiresCliClientId() =
        let missingClientId = oidcConfiguration "https://login.example.test/tenant" "api://grace"
        let blankClientId = oidcClientConfiguration "https://login.example.test/tenant" "api://grace" "   "

        Assert.That(ExternalAuthConfig.tryGetOidcClientConfig missingClientId, Is.EqualTo(None))
        Assert.That(ExternalAuthConfig.tryGetOidcClientConfig blankClientId, Is.EqualTo(None))

    /// Verifies that client Config Trims Values And Normalizes Authority.
    [<Test>]
    member _.ClientConfigTrimsValuesAndNormalizesAuthority() =
        oidcClientConfiguration "  https://login.example.test/tenant  " "  api://grace  " "  grace-cli  "
        |> ExternalAuthConfig.tryGetOidcClientConfig
        |> assertClientConfig "https://login.example.test/tenant/" "api://grace" "grace-cli"

    /// Verifies that is Oidc Configured Reflects Try Get Oidc Config.
    [<Test>]
    member _.IsOidcConfiguredReflectsTryGetOidcConfig() =
        let configured = oidcConfiguration "https://login.example.test/tenant" "api://grace"
        let notConfigured = oidcConfiguration "https://login.example.test/tenant" "   "

        Assert.That(
            ExternalAuthConfig.tryGetOidcConfig configured
            |> Option.isSome,
            Is.True
        )

        Assert.That(ExternalAuthConfig.isOidcConfigured configured, Is.True)
        Assert.That(ExternalAuthConfig.tryGetOidcConfig notConfigured, Is.EqualTo(None))
        Assert.That(ExternalAuthConfig.isOidcConfigured notConfigured, Is.False)
