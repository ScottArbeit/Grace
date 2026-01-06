namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.PersonalAccessToken
open Grace.Types.Types
open NUnit.Framework
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading.Tasks

[<Parallelizable(ParallelScope.All)>]
type AuthInfo = { GraceUserId: string; Claims: string list; RawClaims: (string * string) list }

[<Parallelizable(ParallelScope.All)>]
type AuthEndpoints() =

    [<Test>]
    member _.LoginPageShowsNoProvidersWhenNotConfigured() =
        task {
            let! response = Client.GetAsync("/auth/login")
            response.EnsureSuccessStatusCode() |> ignore
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain("Interactive browser login is not available on the server in this phase."))
        }

    [<Test>]
    member _.LoginProviderReturnsNotFoundWhenNotConfigured() =
        task {
            let! response = Client.GetAsync("/auth/login/microsoft")
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
        }

    [<Test>]
    member _.AuthMeReturnsGraceUserId() =
        task {
            let! response = Client.GetAsync("/auth/me")
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<AuthInfo>> response
            Assert.That(returnValue.ReturnValue.GraceUserId, Is.EqualTo(testUserId))
            Assert.That(returnValue.ReturnValue.Claims, Is.Empty)
            Assert.That(returnValue.ReturnValue.RawClaims, Has.Some.EqualTo((PrincipalMapper.GraceUserIdClaim, testUserId)))
        }

    [<Test>]
    member _.AuthMeRequiresAuthentication() =
        task {
            use unauthenticatedClient = new HttpClient()
            unauthenticatedClient.BaseAddress <- Client.BaseAddress
            let! response = unauthenticatedClient.GetAsync("/auth/me")
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
        }

    [<Test>]
    member _.AuthTokenCreateAndUse() =
        task {
            let parameters = Grace.Shared.Parameters.Auth.CreatePersonalAccessTokenParameters()
            parameters.TokenName <- $"pat-{System.Guid.NewGuid():N}"
            let! response = Client.PostAsync("/auth/token/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore

            let! returnValue = deserializeContent<GraceReturnValue<PersonalAccessTokenCreated>> response
            let token = returnValue.ReturnValue.Token

            use patClient = new HttpClient()
            patClient.BaseAddress <- Client.BaseAddress
            patClient.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", token)

            let! meResponse = patClient.GetAsync("/auth/me")
            meResponse.EnsureSuccessStatusCode() |> ignore

            let! meValue = deserializeContent<GraceReturnValue<AuthInfo>> meResponse
            Assert.That(meValue.ReturnValue.GraceUserId, Is.EqualTo(testUserId))
        }

    [<Test>]
    member _.AuthTokenRevokeBlocksAccess() =
        task {
            let parameters = Grace.Shared.Parameters.Auth.CreatePersonalAccessTokenParameters()
            parameters.TokenName <- $"pat-{System.Guid.NewGuid():N}"
            let! response = Client.PostAsync("/auth/token/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! returnValue = deserializeContent<GraceReturnValue<PersonalAccessTokenCreated>> response
            let created = returnValue.ReturnValue

            let revokeParameters = Grace.Shared.Parameters.Auth.RevokePersonalAccessTokenParameters()
            revokeParameters.TokenId <- created.Summary.TokenId
            let! revokeResponse = Client.PostAsync("/auth/token/revoke", createJsonContent revokeParameters)
            revokeResponse.EnsureSuccessStatusCode() |> ignore

            use patClient = new HttpClient()
            patClient.BaseAddress <- Client.BaseAddress
            patClient.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", created.Token)
            let! meResponse = patClient.GetAsync("/auth/me")
            Assert.That(meResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
        }

    [<Test>]
    member _.AuthTokenMaxLifetimeEnforced() =
        task {
            let parameters = Grace.Shared.Parameters.Auth.CreatePersonalAccessTokenParameters()
            parameters.TokenName <- $"pat-{System.Guid.NewGuid():N}"
            parameters.ExpiresInSeconds <- 400L * 86400L
            let! response = Client.PostAsync("/auth/token/create", createJsonContent parameters)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))
        }

    [<Test>]
    member _.AuthTokenListIncludesCreated() =
        task {
            let parameters = Grace.Shared.Parameters.Auth.CreatePersonalAccessTokenParameters()
            parameters.TokenName <- $"pat-{System.Guid.NewGuid():N}"
            let! response = Client.PostAsync("/auth/token/create", createJsonContent parameters)
            response.EnsureSuccessStatusCode() |> ignore
            let! createdReturn = deserializeContent<GraceReturnValue<PersonalAccessTokenCreated>> response

            let listParameters = Grace.Shared.Parameters.Auth.ListPersonalAccessTokensParameters()
            let! listResponse = Client.PostAsync("/auth/token/list", createJsonContent listParameters)
            listResponse.EnsureSuccessStatusCode() |> ignore
            let! listReturn = deserializeContent<GraceReturnValue<PersonalAccessTokenSummary list>> listResponse

            let createdId = createdReturn.ReturnValue.Summary.TokenId
            let containsToken = listReturn.ReturnValue |> List.exists (fun token -> token.TokenId = createdId)
            Assert.That(containsToken, Is.True)

            let revokeParameters = Grace.Shared.Parameters.Auth.RevokePersonalAccessTokenParameters()
            revokeParameters.TokenId <- createdId
            let! revokeResponse = Client.PostAsync("/auth/token/revoke", createJsonContent revokeParameters)
            revokeResponse.EnsureSuccessStatusCode() |> ignore

            let listWithRevoked = Grace.Shared.Parameters.Auth.ListPersonalAccessTokensParameters()
            listWithRevoked.IncludeRevoked <- true
            let! revokedResponse = Client.PostAsync("/auth/token/list", createJsonContent listWithRevoked)
            revokedResponse.EnsureSuccessStatusCode() |> ignore
            let! revokedReturn = deserializeContent<GraceReturnValue<PersonalAccessTokenSummary list>> revokedResponse

            let revokedToken =
                revokedReturn.ReturnValue
                |> List.tryFind (fun token -> token.TokenId = createdId)

            Assert.That(revokedToken.IsSome, Is.True)
            Assert.That(revokedToken.Value.RevokedAt.IsSome, Is.True)
        }
