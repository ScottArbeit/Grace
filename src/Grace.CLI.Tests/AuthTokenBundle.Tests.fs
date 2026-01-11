namespace Grace.CLI.Tests

open Grace.CLI.Command
open Grace.Shared
open NodaTime
open NUnit.Framework
open System.Text.Json

[<Parallelizable(ParallelScope.All)>]
type AuthTokenBundleTests() =
    [<Test>]
    member _.TokenBundleRoundTrips() =
        let now = Instant.FromUtc(2025, 12, 31, 0, 0)

        let bundle: Auth.TokenBundle =
            {
                RefreshToken = "refresh"
                AccessToken = "access"
                AccessTokenExpiresAt = now.Plus(Duration.FromHours(1.0))
                Issuer = "https://tenant.us.auth0.com/"
                Audience = "https://api.gracevcs.com"
                Scopes = "openid profile email offline_access"
                Subject = Some "user-1"
                ClientId = "cli-client"
                CreatedAt = now
                UpdatedAt = now
            }

        let json = JsonSerializer.Serialize(bundle, Constants.JsonSerializerOptions)
        let roundTrip = JsonSerializer.Deserialize<Auth.TokenBundle>(json, Constants.JsonSerializerOptions)

        Assert.That(roundTrip, Is.EqualTo(bundle))
