namespace Grace.Server.Tests

open Grace.Server.Security
open Grace.Types.PersonalAccessToken
open Microsoft.AspNetCore.Authentication.JwtBearer
open NUnit.Framework

[<Parallelizable(ParallelScope.All)>]
type AuthSchemeSelectionUnitTests() =

    let gracePatHeader = $"Bearer {TokenPrefix}user.token.secret"

    let assertScheme (expected: string) isTesting hasOidc (authorization: string) (testUserId: string) =
        Assert.That(AuthSchemeSelection.selectScheme isTesting hasOidc authorization testUserId, Is.EqualTo(expected))

    [<Test>]
    member _.TestingModeWithNoAuthHeaderSelectsTestAuth() = assertScheme TestAuth.SchemeName true false null null

    [<Test>]
    member _.TestingModeWithGracePatBearerSelectsPatAuth() = assertScheme PersonalAccessTokenAuth.SchemeName true false gracePatHeader null

    [<Test>]
    member _.TestingModeWithGracePatBearerAndTestUserSelectsPatAuth() = assertScheme PersonalAccessTokenAuth.SchemeName true true gracePatHeader "debug-user"

    [<Test>]
    member _.TestingModeWithOidcAndNonPatBearerSelectsJwt() = assertScheme JwtBearerDefaults.AuthenticationScheme true true "Bearer external-jwt-token" null

    [<Test>]
    member _.TestingModeWithOidcAndNonPatBearerPlusTestUserSelectsJwt() =
        assertScheme JwtBearerDefaults.AuthenticationScheme true true "Bearer external-jwt-token" "debug-user"

    [<Test>]
    member _.TestingModeWithNonPatBearerWithoutOidcSelectsPatAuth() =
        assertScheme PersonalAccessTokenAuth.SchemeName true false "Bearer external-jwt-token" null

    [<Test>]
    member _.TestingModeWithExplicitTestUserSelectsTestAuth() = assertScheme TestAuth.SchemeName true false null "debug-user"

    [<Test>]
    member _.TestingModeWithNonPatBearerWithoutOidcAndExplicitTestUserSelectsTestAuth() =
        assertScheme TestAuth.SchemeName true false "Bearer external-jwt-token" "debug-user"

    [<Test>]
    member _.ProductionWithOidcAndNonPatBearerSelectsJwt() = assertScheme JwtBearerDefaults.AuthenticationScheme false true "Bearer external-jwt-token" null

    [<Test>]
    member _.ProductionWithOidcAndGracePatBearerSelectsPatAuth() = assertScheme PersonalAccessTokenAuth.SchemeName false true gracePatHeader null

    [<Test>]
    member _.ProductionWithOidcAndNoBearerSelectsJwt() = assertScheme JwtBearerDefaults.AuthenticationScheme false true null null

    [<Test>]
    member _.ProductionWithoutOidcSelectsPat() =
        assertScheme PersonalAccessTokenAuth.SchemeName false false "Bearer external-jwt-token" null

        assertScheme PersonalAccessTokenAuth.SchemeName false false null null

    [<Test>]
    member _.WhitespaceAndMalformedHeadersSelectExistingFallback() =
        assertScheme TestAuth.SchemeName true false "   " null
        assertScheme TestAuth.SchemeName true false "Basic abc123" null
        assertScheme JwtBearerDefaults.AuthenticationScheme false true "   " null
        assertScheme JwtBearerDefaults.AuthenticationScheme false true "Basic abc123" null
        assertScheme PersonalAccessTokenAuth.SchemeName false false "Bearer    " null
        assertScheme PersonalAccessTokenAuth.SchemeName false false "Basic abc123" null
