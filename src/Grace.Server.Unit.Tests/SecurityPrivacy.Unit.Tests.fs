namespace Grace.Server.Tests

open Grace.Server.Middleware
open Grace.Server.Security
open Grace.Shared.Validation.Errors
open Grace.Types.Common
open Grace.Types.PersonalAccessToken
open Microsoft.AspNetCore.Http
open NUnit.Framework
open System
open System.Security.Claims

/// Covers security Privacy Unit behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type SecurityPrivacyUnitTests() =

    /// Builds valid Pat For test data for the server unit security Privacy scenarios in this file.
    let validPatFor userId =
        let tokenId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
        let secret = [| for index in 0..31 -> byte index |]
        formatToken userId tokenId secret

    let createPrincipal claims = ClaimsPrincipal(ClaimsIdentity(claims, "test"))

    /// Verifies that malformed Grace Pat Fails Closed Without Echoing Token Material.
    [<Test>]
    member _.MalformedGracePatFailsClosedWithoutEchoingTokenMaterial() =
        let rawToken = $"{TokenPrefix}not-valid-token-material"

        let result = PersonalAccessTokenAuth.parseAuthorizationHeader $"Bearer {rawToken}"

        Assert.That(result, Is.EqualTo(PersonalAccessTokenAuth.MalformedGracePersonalAccessToken))
        Assert.That(PersonalAccessTokenAuth.InvalidTokenFailure, Does.Not.Contain(rawToken))
        Assert.That(PersonalAccessTokenAuth.InvalidTokenFailure, Does.Not.Contain(TokenPrefix))

    /// Verifies that external Bearer Is Left For Other Auth Schemes.
    [<Test>]
    member _.ExternalBearerIsLeftForOtherAuthSchemes() =
        let result = PersonalAccessTokenAuth.parseAuthorizationHeader "Bearer external.jwt.token"

        Assert.That(result, Is.EqualTo(PersonalAccessTokenAuth.NotGracePersonalAccessToken))

    /// Verifies that revoked Expired Or Wrong Owner Actor Misses Use Generic Failure Message.
    [<Test>]
    member _.RevokedExpiredOrWrongOwnerActorMissesUseGenericFailureMessage() =
        let token = validPatFor "user-1"
        let parsed = PersonalAccessTokenAuth.parseAuthorizationHeader $"Bearer {token}"

        match parsed with
        | PersonalAccessTokenAuth.ParsedGracePersonalAccessToken (userId, tokenId, secret) ->
            Assert.That(userId, Is.EqualTo("user-1"))
            Assert.That(tokenId, Is.EqualTo(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")))
            Assert.That(secret, Has.Length.EqualTo(32))
        | other -> Assert.Fail($"Expected parsed PAT, got {other}.")

        Assert.That(PersonalAccessTokenAuth.InvalidTokenFailure, Is.EqualTo("Invalid token."))
        Assert.That(PersonalAccessTokenAuth.InvalidTokenFailure, Does.Not.Contain("revoked"))
        Assert.That(PersonalAccessTokenAuth.InvalidTokenFailure, Does.Not.Contain("expired"))
        Assert.That(PersonalAccessTokenAuth.InvalidTokenFailure, Does.Not.Contain("owner"))
        Assert.That(PersonalAccessTokenAuth.InvalidTokenFailure, Does.Not.Contain(token))

    /// Verifies that successful Pat Projection Keeps Only Grace Claims And Groups.
    [<Test>]
    member _.SuccessfulPatProjectionKeepsOnlyGraceClaimsAndGroups() =
        let validation = { TokenId = Guid.NewGuid(); UserId = "user-1"; Claims = [ "RepositoryRead"; "RepositoryWrite" ]; GroupIds = [ "group-1" ] }

        let claims = PersonalAccessTokenAuth.toAuthenticationClaims validation

        Assert.That(
            claims |> Seq.map (fun claim -> claim.Type),
            Is.EquivalentTo(
                [
                    PrincipalMapper.GraceUserIdClaim
                    PrincipalMapper.GraceClaim
                    PrincipalMapper.GraceClaim
                    PrincipalMapper.GraceGroupIdClaim
                ]
            )
        )

        Assert.That(
            claims
            |> Seq.exists (fun claim -> claim.Value = "user-1"),
            Is.True
        )

    /// Verifies that telemetry Claims Allowlist Redacts Identity And Drops Secret Bearing Claims.
    [<Test>]
    member _.TelemetryClaimsAllowlistRedactsIdentityAndDropsSecretBearingClaims() =
        let principal =
            createPrincipal [ Claim(PrincipalMapper.GraceUserIdClaim, "user-secret-id")
                              Claim(PrincipalMapper.GraceClaim, "RepositoryRead")
                              Claim(PrincipalMapper.GraceGroupIdClaim, "group-1")
                              Claim("roles", "Admin")
                              Claim("email", "person@example.test")
                              Claim("access_token", "raw-provider-token")
                              Claim("Authorization", "Bearer raw-pat") ]

        let claimsTag = TelemetryEnrichment.safeClaimsTag principal

        Assert.That(TelemetryEnrichment.safeEndUserId principal, Is.EqualTo("REDACTED"))
        Assert.That(claimsTag, Does.Contain($"{PrincipalMapper.GraceUserIdClaim}:REDACTED"))
        Assert.That(claimsTag, Does.Contain($"{PrincipalMapper.GraceClaim}:RepositoryRead"))
        Assert.That(claimsTag, Does.Contain($"{PrincipalMapper.GraceGroupIdClaim}:group-1"))
        Assert.That(claimsTag, Does.Contain("roles:Admin"))
        Assert.That(claimsTag, Does.Not.Contain("person@example.test"))
        Assert.That(claimsTag, Does.Not.Contain("raw-provider-token"))
        Assert.That(claimsTag, Does.Not.Contain("raw-pat"))

    /// Verifies that request Header Redaction Covers Authorization Cookie And Token Named Headers.
    [<Test>]
    member _.RequestHeaderRedactionCoversAuthorizationCookieAndTokenNamedHeaders() =
        let authorization = RequestHeaderRedaction.redactHeaderValue "Authorization" "Bearer grace_pat_v1_secret"
        let cookie = RequestHeaderRedaction.redactHeaderValue "Cookie" "session=secret"
        let customToken = RequestHeaderRedaction.redactHeaderValue "x-api-token" "token-secret"
        let safe = RequestHeaderRedaction.redactHeaderValue "X-Correlation-Id" "correlation-1"

        Assert.That(authorization, Is.EqualTo("[REDACTED]"))
        Assert.That(cookie, Is.EqualTo("[REDACTED]"))
        Assert.That(customToken, Is.EqualTo("[REDACTED]"))
        Assert.That(safe, Is.EqualTo("correlation-1"))

    /// Verifies that request Header Redaction Covers Provider Keys Signing Secrets Signatures And Credentials.
    [<TestCase("X-API-Key", "provider-key")>]
    [<TestCase("Api-Key", "provider-key")>]
    [<TestCase("OpenAI-Api-Key", "provider-key")>]
    [<TestCase("Client-Secret", "client-secret")>]
    [<TestCase("client_secret", "client-secret")>]
    [<TestCase("X-Signing-Secret", "signing-secret")>]
    [<TestCase("X-Grace-Webhook-Signature", "sha256=signature")>]
    [<TestCase("X-Amz-Credential", "aws-credential")>]
    member _.RequestHeaderRedactionCoversProviderKeysSigningSecretsSignaturesAndCredentials(headerName: string, value: string) =
        let redacted = RequestHeaderRedaction.redactHeaderValue headerName value

        Assert.That(redacted, Is.EqualTo("[REDACTED]"))

    /// Verifies that request Header Redaction Keeps Useful Non Sensitive Headers Visible.
    [<Test>]
    member _.RequestHeaderRedactionKeepsUsefulNonSensitiveHeadersVisible() =
        let correlationId = RequestHeaderRedaction.redactHeaderValue "X-Correlation-Id" "correlation-1"

        Assert.That(correlationId, Is.EqualTo("correlation-1"))

    /// Verifies that validate Ids Not Found Contract Is Bad Request With Original Correlation.
    [<TestCase(ValidateIdsDecisions.EntityKind.Owner, true)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Owner, false)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Organization, true)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Organization, false)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Repository, true)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Repository, false)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Branch, true)>]
    [<TestCase(ValidateIdsDecisions.EntityKind.Branch, false)>]
    member _.ValidateIdsNotFoundContractIsBadRequestWithOriginalCorrelation(entityKind: ValidateIdsDecisions.EntityKind, hasId: bool) =
        let correlationId = "corr-validate-1"
        let id = if hasId then Guid.NewGuid().ToString() else ""
        let message = ValidateIdsDecisions.notFoundErrorMessage entityKind id
        let error = GraceError.Create message correlationId

        let contract = ValidateIdsDecisions.badRequestResponse error

        Assert.That(contract.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest))
        Assert.That(contract.Error.Error, Is.EqualTo(message))
        Assert.That(contract.Error.CorrelationId, Is.EqualTo(correlationId))
