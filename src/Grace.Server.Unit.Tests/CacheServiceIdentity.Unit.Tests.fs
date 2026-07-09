namespace Grace.Server.Tests

open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.Extensions.Configuration
open NUnit.Framework
open System
open System.Collections.Generic
open System.Security.Claims

/// Covers Grace Cache service identity and registration configuration decisions.
[<Parallelizable(ParallelScope.All)>]
type CacheServiceIdentityUnitTests() =

    /// Builds configuration test data for Cache service identity scenarios.
    let configuration (values: (string * string) list) =
        let pairs =
            values
            |> List.map (fun (key, value) -> KeyValuePair<string, string>(getConfigKey key, value))

        ConfigurationBuilder()
            .AddInMemoryCollection(pairs)
            .Build()
        :> IConfiguration

    /// Builds the minimal enabled Cache registration config used by authorization assertions.
    let enabledConfig: CacheServiceIdentity.CacheRegistrationConfig =
        {
            Enabled = true
            ServicePrincipalIds = [ "cache-service-client" ]
            AllowedScopes =
                [
                    "repository:owner/org/repo"
                    "storage-pool:pool-1"
                ]
            AllowedCapabilities = [ "Register"; "PublishHealth" ]
        }

    /// Builds a claims principal with a specific authentication scheme and claims.
    let principal (scheme: string) (claims: Claim list) = ClaimsPrincipal(ClaimsIdentity((claims :> Claim seq), scheme))

    /// Builds an OIDC JWT bearer service principal fixture.
    let servicePrincipal servicePrincipalId =
        principal
            JwtBearerDefaults.AuthenticationScheme
            [
                Claim("azp", servicePrincipalId)
                Claim("gty", "client-credentials")
            ]

    /// Verifies that disabled registration does not require service policy configuration.
    [<Test>]
    member _.DisabledRegistrationDoesNotRequireServicePolicyConfiguration() =
        let config = configuration []

        match CacheServiceIdentity.tryGetRegistrationConfig config with
        | Ok registration ->
            Assert.That(registration.Enabled, Is.False)
            Assert.That(registration.ServicePrincipalIds, Is.Empty)
            Assert.That(registration.AllowedScopes, Is.Empty)
            Assert.That(registration.AllowedCapabilities, Is.Empty)
        | Error errors ->
            let errorText = String.Join("; ", errors)
            Assert.Fail($"Expected disabled registration config, got {errorText}.")

    /// Verifies that enabled Cache registration fails early when service policy configuration is incomplete.
    [<Test>]
    member _.EnabledRegistrationRequiresServicePrincipalsScopesAndCapabilities() =
        let config = configuration [ EnvironmentVariables.GraceCacheRegistrationEnabled, "true" ]

        match CacheServiceIdentity.tryGetRegistrationConfig config with
        | Ok _ -> Assert.Fail("Expected Cache registration config errors.")
        | Error errors ->
            Assert.That(errors, Has.Some.Contains(EnvironmentVariables.GraceCacheRegistrationServicePrincipalIds))
            Assert.That(errors, Has.Some.Contains(EnvironmentVariables.GraceCacheRegistrationAllowedScopes))
            Assert.That(errors, Has.Some.Contains(EnvironmentVariables.GraceCacheRegistrationAllowedCapabilities))

    /// Verifies that invalid enablement values fail before a Cache service exposes listeners.
    [<Test>]
    member _.InvalidRegistrationEnablementFailsConfigurationValidation() =
        let config = configuration [ EnvironmentVariables.GraceCacheRegistrationEnabled, "sometimes" ]

        match CacheServiceIdentity.tryGetRegistrationConfig config with
        | Ok _ -> Assert.Fail("Expected invalid enablement to fail.")
        | Error errors ->
            Assert.That(
                errors,
                Is.EquivalentTo(
                    [
                        $"{EnvironmentVariables.GraceCacheRegistrationEnabled} must be true or false."
                    ]
                )
            )

    /// Verifies that configured Cache registration values are trimmed and deduplicated.
    [<Test>]
    member _.RegistrationConfigurationTrimsAndDeduplicatesValues() =
        let config =
            configuration [ EnvironmentVariables.GraceCacheRegistrationEnabled, "true"
                            EnvironmentVariables.GraceCacheRegistrationServicePrincipalIds, " cache-service-client ; CACHE-SERVICE-CLIENT "
                            EnvironmentVariables.GraceCacheRegistrationAllowedScopes, " repository:owner/org/repo ; storage-pool:pool-1 "
                            EnvironmentVariables.GraceCacheRegistrationAllowedCapabilities, " Register ; register ; PublishHealth " ]

        match CacheServiceIdentity.tryGetRegistrationConfig config with
        | Error errors ->
            let errorText = String.Join("; ", errors)
            Assert.Fail($"Expected Cache registration config, got {errorText}.")
        | Ok registration ->
            Assert.That(registration.Enabled, Is.True)
            Assert.That(registration.ServicePrincipalIds, Is.EquivalentTo([ "cache-service-client" ]))

            Assert.That(
                registration.AllowedScopes,
                Is.EquivalentTo(
                    [
                        "repository:owner/org/repo"
                        "storage-pool:pool-1"
                    ]
                )
            )

            Assert.That(registration.AllowedCapabilities, Is.EquivalentTo([ "Register"; "PublishHealth" ]))

    /// Verifies that a configured OIDC service principal can register approved scopes and capabilities.
    [<Test>]
    member _.ConfiguredJwtServicePrincipalCanRegisterApprovedScopesAndCapabilities() =
        let decision =
            CacheServiceIdentity.decideRegistration
                enabledConfig
                (servicePrincipal "cache-service-client")
                [ "repository:owner/org/repo" ]
                [ "Register"; "PublishHealth" ]

        Assert.That(decision, Is.EqualTo(CacheServiceIdentity.CacheRegistrationDecision.Allowed "cache-service-client"))

    /// Verifies that Grace PAT identities cannot register Cache services even when the principal ID matches.
    [<Test>]
    member _.GracePatPrincipalCannotRegisterCacheService() =
        let patPrincipal =
            principal
                PersonalAccessTokenAuth.SchemeName
                [
                    Claim(PrincipalMapper.GraceUserIdClaim, "cache-service-client")
                ]

        let decision = CacheServiceIdentity.decideRegistration enabledConfig patPrincipal [ "repository:owner/org/repo" ] [ "Register" ]

        Assert.That(decision, Is.EqualTo(CacheServiceIdentity.CacheRegistrationDecision.Denied "Cache registration requires OIDC JWT bearer authentication."))

    /// Verifies that normal user OIDC credentials cannot register Cache services without a configured service principal.
    [<Test>]
    member _.NormalJwtUserPrincipalCannotRegisterCacheService() =
        let userPrincipal = principal JwtBearerDefaults.AuthenticationScheme [ Claim("sub", "auth0|developer-user") ]

        let decision = CacheServiceIdentity.decideRegistration enabledConfig userPrincipal [ "repository:owner/org/repo" ] [ "Register" ]

        Assert.That(
            decision,
            Is.EqualTo(CacheServiceIdentity.CacheRegistrationDecision.Denied "Cache registration requires an OIDC client-credentials service token.")
        )

    /// Verifies that a configured principal ID still needs an OIDC client-credentials service token.
    [<Test>]
    member _.ConfiguredPrincipalIdWithoutClientCredentialsGrantCannotRegisterCacheService() =
        let userCompatibleConfig: CacheServiceIdentity.CacheRegistrationConfig = { enabledConfig with ServicePrincipalIds = [ "auth0|developer-user" ] }

        let userPrincipal = principal JwtBearerDefaults.AuthenticationScheme [ Claim("sub", "auth0|developer-user") ]

        let decision = CacheServiceIdentity.decideRegistration userCompatibleConfig userPrincipal [ "repository:owner/org/repo" ] [ "Register" ]

        Assert.That(
            decision,
            Is.EqualTo(CacheServiceIdentity.CacheRegistrationDecision.Denied "Cache registration requires an OIDC client-credentials service token.")
        )

    /// Verifies that unapproved scopes and capabilities are rejected by server configuration.
    [<Test>]
    member _.UnapprovedScopesAndCapabilitiesAreRejectedByServerConfiguration() =
        let scopeDecision =
            CacheServiceIdentity.decideRegistration enabledConfig (servicePrincipal "cache-service-client") [ "repository:other/org/repo" ] [ "Register" ]

        let capabilityDecision =
            CacheServiceIdentity.decideRegistration
                enabledConfig
                (servicePrincipal "cache-service-client")
                [ "repository:owner/org/repo" ]
                [ "ApproveOwnScope" ]

        Assert.That(
            scopeDecision,
            Is.EqualTo(
                CacheServiceIdentity.CacheRegistrationDecision.Denied "Cache registration requested a scope that is not approved by server configuration."
            )
        )

        Assert.That(
            capabilityDecision,
            Is.EqualTo(
                CacheServiceIdentity.CacheRegistrationDecision.Denied "Cache registration requested a capability that is not approved by server configuration."
            )
        )

    /// Verifies that status summaries redact configured service principal and scope values.
    [<Test>]
    member _.ConfigurationSummaryDoesNotExposeServicePrincipalOrScopeValues() =
        let summary = CacheServiceIdentity.summarizeConfig enabledConfig
        let rendered = $"{summary}"

        Assert.That(summary.Enabled, Is.True)
        Assert.That(summary.ServicePrincipalCount, Is.EqualTo(1))
        Assert.That(summary.AllowedScopeCount, Is.EqualTo(2))
        Assert.That(summary.AllowedCapabilities, Is.EquivalentTo([ "Register"; "PublishHealth" ]))
        Assert.That(rendered, Does.Not.Contain("cache-service-client"))
        Assert.That(rendered, Does.Not.Contain("repository:owner/org/repo"))
