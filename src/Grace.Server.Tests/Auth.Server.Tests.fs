namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open NUnit.Framework
open System.Net
open System.Net.Http
open System.Threading.Tasks

[<Parallelizable(ParallelScope.All)>]
type AuthInfo =
    { GraceUserId: string
      Claims: string list
      RawClaims: (string * string) list }

[<Parallelizable(ParallelScope.All)>]
type AuthEndpoints() =

    [<Test>]
    member _.LoginPageShowsNoProvidersWhenNotConfigured() =
        task {
            let! response = Client.GetAsync("/auth/login")
            response.EnsureSuccessStatusCode() |> ignore
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain("No login providers are configured."))
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
            Assert.That(
                returnValue.ReturnValue.RawClaims,
                Has.Some.EqualTo((PrincipalMapper.GraceUserIdClaim, testUserId))
            )
        }

    [<Test>]
    member _.AuthMeRequiresAuthentication() =
        task {
            use unauthenticatedClient = new HttpClient()
            unauthenticatedClient.BaseAddress <- Client.BaseAddress
            let! response = unauthenticatedClient.GetAsync("/auth/me")
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))
        }
