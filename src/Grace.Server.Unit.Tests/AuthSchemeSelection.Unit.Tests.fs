namespace Grace.Server.Tests

open Grace.Server.Security
open Grace.Types.PersonalAccessToken
open Microsoft.AspNetCore.Authentication.JwtBearer
open NUnit.Framework

[<Parallelizable(ParallelScope.All)>]
type AuthSchemeSelectionUnitTests() =

    let gracePatHeader = $"Bearer {TokenPrefix}user.token.secret"

    let assertScheme (expected: string) isTesting hasOidc (authorization: string) =
        Assert.That(AuthSchemeSelection.selectScheme isTesting hasOidc authorization, Is.EqualTo(expected))

    [<Test>]
    member _.TestingModeWithNoAuthHeaderSelectsTestAuth() = assertScheme TestAuth.SchemeName true false null

    [<Test>]
    member _.TestingModeWithGracePatBearerSelectsPatAuth() = assertScheme PersonalAccessTokenAuth.SchemeName true false gracePatHeader

    [<Test>]
    member _.TestingModeWithNonPatBearerSelectsTestAuth() = assertScheme TestAuth.SchemeName true false "Bearer external-jwt-token"

    [<Test>]
    member _.ProductionWithOidcAndNonPatBearerSelectsJwt() = assertScheme JwtBearerDefaults.AuthenticationScheme false true "Bearer external-jwt-token"

    [<Test>]
    member _.ProductionWithOidcAndGracePatBearerSelectsPatAuth() = assertScheme PersonalAccessTokenAuth.SchemeName false true gracePatHeader

    [<Test>]
    member _.ProductionWithOidcAndNoBearerSelectsJwt() = assertScheme JwtBearerDefaults.AuthenticationScheme false true null

    [<Test>]
    member _.ProductionWithoutOidcSelectsPat() =
        assertScheme PersonalAccessTokenAuth.SchemeName false false "Bearer external-jwt-token"

        assertScheme PersonalAccessTokenAuth.SchemeName false false null

    [<Test>]
    member _.WhitespaceAndMalformedHeadersSelectExistingFallback() =
        assertScheme TestAuth.SchemeName true false "   "
        assertScheme TestAuth.SchemeName true false "Basic abc123"
        assertScheme JwtBearerDefaults.AuthenticationScheme false true "   "
        assertScheme JwtBearerDefaults.AuthenticationScheme false true "Basic abc123"
        assertScheme PersonalAccessTokenAuth.SchemeName false false "   "
        assertScheme PersonalAccessTokenAuth.SchemeName false false "Basic abc123"
