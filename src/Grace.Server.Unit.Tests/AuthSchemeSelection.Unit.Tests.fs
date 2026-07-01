namespace Grace.Server.Tests

open Grace.Server.Security
open Grace.Types.PersonalAccessToken
open Microsoft.AspNetCore.Authentication.JwtBearer
open NUnit.Framework

/// Covers auth Scheme Selection Unit behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type AuthSchemeSelectionUnitTests() =

    let gracePatHeader = $"Bearer {TokenPrefix}user.token.secret"

    /// Asserts the scheme condition so failures identify the violated server unit auth Scheme Selection invariant.
    let assertScheme (expected: string) isTesting hasOidc (authorization: string) (testUserId: string) =
        Assert.That(AuthSchemeSelection.selectScheme isTesting hasOidc authorization testUserId, Is.EqualTo(expected))

    /// Verifies that testing Mode With No Auth Header Selects Test Auth.
    [<Test>]
    member _.TestingModeWithNoAuthHeaderSelectsTestAuth() = assertScheme TestAuth.SchemeName true false null null

    /// Verifies that testing Mode With Grace Pat Bearer Selects Pat Auth.
    [<Test>]
    member _.TestingModeWithGracePatBearerSelectsPatAuth() = assertScheme PersonalAccessTokenAuth.SchemeName true false gracePatHeader null

    /// Verifies that testing Mode With Grace Pat Bearer And Test User Selects Pat Auth.
    [<Test>]
    member _.TestingModeWithGracePatBearerAndTestUserSelectsPatAuth() = assertScheme PersonalAccessTokenAuth.SchemeName true true gracePatHeader "debug-user"

    /// Verifies that testing Mode With Oidc And Non Pat Bearer Selects Jwt.
    [<Test>]
    member _.TestingModeWithOidcAndNonPatBearerSelectsJwt() = assertScheme JwtBearerDefaults.AuthenticationScheme true true "Bearer external-jwt-token" null

    /// Verifies that testing Mode With Oidc And Non Pat Bearer Plus Test User Selects Jwt.
    [<Test>]
    member _.TestingModeWithOidcAndNonPatBearerPlusTestUserSelectsJwt() =
        assertScheme JwtBearerDefaults.AuthenticationScheme true true "Bearer external-jwt-token" "debug-user"

    /// Verifies that testing Mode With Non Pat Bearer Without Oidc Selects Pat Auth.
    [<Test>]
    member _.TestingModeWithNonPatBearerWithoutOidcSelectsPatAuth() =
        assertScheme PersonalAccessTokenAuth.SchemeName true false "Bearer external-jwt-token" null

    /// Verifies that testing Mode With Explicit Test User Selects Test Auth.
    [<Test>]
    member _.TestingModeWithExplicitTestUserSelectsTestAuth() = assertScheme TestAuth.SchemeName true false null "debug-user"

    /// Verifies that testing Mode With Non Pat Bearer Without Oidc And Explicit Test User Selects Test Auth.
    [<Test>]
    member _.TestingModeWithNonPatBearerWithoutOidcAndExplicitTestUserSelectsTestAuth() =
        assertScheme TestAuth.SchemeName true false "Bearer external-jwt-token" "debug-user"

    /// Verifies that production With Oidc And Non Pat Bearer Selects Jwt.
    [<Test>]
    member _.ProductionWithOidcAndNonPatBearerSelectsJwt() = assertScheme JwtBearerDefaults.AuthenticationScheme false true "Bearer external-jwt-token" null

    /// Verifies that production With Oidc And Grace Pat Bearer Selects Pat Auth.
    [<Test>]
    member _.ProductionWithOidcAndGracePatBearerSelectsPatAuth() = assertScheme PersonalAccessTokenAuth.SchemeName false true gracePatHeader null

    /// Verifies that production With Oidc And No Bearer Selects Jwt.
    [<Test>]
    member _.ProductionWithOidcAndNoBearerSelectsJwt() = assertScheme JwtBearerDefaults.AuthenticationScheme false true null null

    /// Verifies that production Without Oidc Selects Pat.
    [<Test>]
    member _.ProductionWithoutOidcSelectsPat() =
        assertScheme PersonalAccessTokenAuth.SchemeName false false "Bearer external-jwt-token" null

        assertScheme PersonalAccessTokenAuth.SchemeName false false null null

    /// Verifies that whitespace And Malformed Headers Select Existing Fallback.
    [<Test>]
    member _.WhitespaceAndMalformedHeadersSelectExistingFallback() =
        assertScheme TestAuth.SchemeName true false "   " null
        assertScheme TestAuth.SchemeName true false "Basic abc123" null
        assertScheme JwtBearerDefaults.AuthenticationScheme false true "   " null
        assertScheme JwtBearerDefaults.AuthenticationScheme false true "Basic abc123" null
        assertScheme PersonalAccessTokenAuth.SchemeName false false "Bearer    " null
        assertScheme PersonalAccessTokenAuth.SchemeName false false "Basic abc123" null
