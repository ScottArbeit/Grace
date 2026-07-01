namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.PersonalAccessToken
open Grace.Types.Auth
open Grace.Types.Common
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading.Tasks

/// Captures auth info values used by the test suite.
[<Parallelizable(ParallelScope.All)>]
type AuthInfo = { GraceUserId: string; Claims: string list; RawClaims: (string * string) list }

/// Covers auth endpoints scenarios.
[<Parallelizable(ParallelScope.All)>]
type AuthEndpoints() =

    /// Verifies the login page shows no providers when not configured scenario.
    [<Test>]
    member _.LoginPageShowsNoProvidersWhenNotConfigured() =
        task {
            let! response = Client.GetAsync("/authenticate/login")
            response.EnsureSuccessStatusCode() |> ignore
            Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("text/html"))
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(body, Does.StartWith("<!doctype html>"))
            Assert.That(body, Does.Contain("<title>Grace Login</title>"))
            Assert.That(body, Does.Contain("Interactive browser login is not available on the server in this phase."))
            Assert.That(body, Does.Contain("grace authenticate login"))
        }

    /// Verifies the login provider returns not found when not configured scenario.
    [<Test>]
    member _.LoginProviderReturnsNotFoundWhenNotConfigured() =
        task {
            let! response = Client.GetAsync("/authenticate/login/microsoft")
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
            Assert.That(body, Does.Contain("Login provider not available."))
        }

    /// Verifies the auth OIDC config returns configured values scenario.
    [<Test>]
    member _.AuthOidcConfigReturnsConfiguredValues() =
        task {
            let rawAuthority = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceAuthOidcAuthority)

            let rawAudience = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceAuthOidcAudience)

            let rawClientId = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceAuthOidcCliClientId)

            Assert.That(rawAuthority, Is.Not.Null.And.Not.Empty)
            Assert.That(rawAudience, Is.Not.Null.And.Not.Empty)
            Assert.That(rawClientId, Is.Not.Null.And.Not.Empty)

            /// Normalizes authority for stable assertions.
            let normalizeAuthority (value: string) =
                let trimmed = value.Trim()

                if trimmed.EndsWith("/", StringComparison.Ordinal) then
                    trimmed
                else
                    $"{trimmed}/"

            use unauthenticatedClient = new HttpClient()
            unauthenticatedClient.BaseAddress <- Client.BaseAddress

            let! response = unauthenticatedClient.GetAsync("/authenticate/oidc/config")
            response.EnsureSuccessStatusCode() |> ignore

            let! returnValue = deserializeContent<GraceReturnValue<OidcClientConfig>> response

            Assert.That(returnValue.ReturnValue.Authority, Is.EqualTo(normalizeAuthority rawAuthority))
            Assert.That(returnValue.ReturnValue.Audience, Is.EqualTo(rawAudience.Trim()))
            Assert.That(returnValue.ReturnValue.CliClientId, Is.EqualTo(rawClientId.Trim()))
        }

    /// Verifies the auth me returns grace user ID scenario.
    [<Test>]
    member _.AuthMeReturnsGraceUserId() =
        task {
            let! response = Client.GetAsync("/authenticate/me")
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<AuthInfo>> response
            Assert.That(returnValue.ReturnValue.GraceUserId, Is.EqualTo(testUserId))
            Assert.That(returnValue.ReturnValue.Claims, Is.Empty)
            Assert.That(returnValue.ReturnValue.RawClaims, Has.Some.EqualTo((PrincipalMapper.GraceUserIdClaim, testUserId)))
        }

    /// Verifies the auth me requires authentication scenario.
    [<Test>]
    member _.AuthMeRequiresAuthentication() =
        task {
            use unauthenticatedClient = new HttpClient()
            unauthenticatedClient.BaseAddress <- Client.BaseAddress
            let! response = unauthenticatedClient.GetAsync("/authenticate/me")
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
        }

    /// Verifies the auth token create and use scenario.
    [<Test>]
    member _.AuthTokenCreateAndUse() =
        task {
            let parameters = Grace.Shared.Parameters.Auth.CreatePersonalAccessTokenParameters()
            parameters.TokenName <- $"pat-{System.Guid.NewGuid():N}"
            let! response = Client.PostAsync("/authenticate/token/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore

            let! returnValue = deserializeContent<GraceReturnValue<PersonalAccessTokenCreated>> response
            let token = returnValue.ReturnValue.Token

            use patClient = new HttpClient()
            patClient.BaseAddress <- Client.BaseAddress
            patClient.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)

            let! meResponse = patClient.GetAsync("/authenticate/me")
            meResponse.EnsureSuccessStatusCode() |> ignore

            let! meValue = deserializeContent<GraceReturnValue<AuthInfo>> meResponse
            Assert.That(meValue.ReturnValue.GraceUserId, Is.EqualTo(testUserId))
        }

    /// Verifies the auth token revoke blocks access scenario.
    [<Test>]
    member _.AuthTokenRevokeBlocksAccess() =
        task {
            let parameters = Grace.Shared.Parameters.Auth.CreatePersonalAccessTokenParameters()
            parameters.TokenName <- $"pat-{System.Guid.NewGuid():N}"
            let! response = Client.PostAsync("/authenticate/token/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<PersonalAccessTokenCreated>> response
            let created = returnValue.ReturnValue

            let revokeParameters = Grace.Shared.Parameters.Auth.RevokePersonalAccessTokenParameters()
            revokeParameters.TokenId <- created.Summary.TokenId
            let! revokeResponse = Client.PostAsync("/authenticate/token/revoke", createJsonContent revokeParameters)
            revokeResponse.EnsureSuccessStatusCode() |> ignore

            use patClient = new HttpClient()
            patClient.BaseAddress <- Client.BaseAddress
            patClient.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", created.Token)
            let! meResponse = patClient.GetAsync("/authenticate/me")
            Assert.That(meResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
        }

    /// Verifies the auth token max lifetime enforced scenario.
    [<Test>]
    member _.AuthTokenMaxLifetimeEnforced() =
        task {
            let parameters = Grace.Shared.Parameters.Auth.CreatePersonalAccessTokenParameters()
            parameters.TokenName <- $"pat-{System.Guid.NewGuid():N}"
            parameters.ExpiresInSeconds <- 400L * 86400L
            let! response = Client.PostAsync("/authenticate/token/create", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    /// Verifies the auth token list includes created scenario.
    [<Test>]
    member _.AuthTokenListIncludesCreated() =
        task {
            let parameters = Grace.Shared.Parameters.Auth.CreatePersonalAccessTokenParameters()
            parameters.TokenName <- $"pat-{System.Guid.NewGuid():N}"
            let! response = Client.PostAsync("/authenticate/token/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! createdReturn = deserializeContent<GraceReturnValue<PersonalAccessTokenCreated>> response

            let listParameters = Grace.Shared.Parameters.Auth.ListPersonalAccessTokensParameters()
            let! listResponse = Client.PostAsync("/authenticate/token/list", createJsonContent listParameters)
            listResponse.EnsureSuccessStatusCode() |> ignore
            let! listReturn = deserializeContent<GraceReturnValue<PersonalAccessTokenSummary list>> listResponse

            let createdId = createdReturn.ReturnValue.Summary.TokenId

            let containsToken =
                listReturn.ReturnValue
                |> List.exists (fun token -> token.TokenId = createdId)

            Assert.That(containsToken, Is.True)

            let revokeParameters = Grace.Shared.Parameters.Auth.RevokePersonalAccessTokenParameters()
            revokeParameters.TokenId <- createdId
            let! revokeResponse = Client.PostAsync("/authenticate/token/revoke", createJsonContent revokeParameters)
            revokeResponse.EnsureSuccessStatusCode() |> ignore

            let listWithRevoked = Grace.Shared.Parameters.Auth.ListPersonalAccessTokensParameters()
            listWithRevoked.IncludeRevoked <- true
            let! revokedResponse = Client.PostAsync("/authenticate/token/list", createJsonContent listWithRevoked)

            revokedResponse.EnsureSuccessStatusCode()
            |> ignore

            let! revokedReturn = deserializeContent<GraceReturnValue<PersonalAccessTokenSummary list>> revokedResponse

            let revokedToken =
                revokedReturn.ReturnValue
                |> List.tryFind (fun token -> token.TokenId = createdId)

            Assert.That(revokedToken.IsSome, Is.True)
            Assert.That(revokedToken.Value.RevokedAt.IsSome, Is.True)
        }
