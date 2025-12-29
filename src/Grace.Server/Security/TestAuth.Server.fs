namespace Grace.Server.Security

open Microsoft.AspNetCore.Authentication
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open System
open System.Security.Claims
open System.Text.Encodings.Web
open System.Threading.Tasks

module TestAuth =

    [<Literal>]
    let SchemeName = "GraceTest"

    [<Literal>]
    let UserIdHeader = "x-grace-user-id"

    [<Literal>]
    let ClaimsHeader = "x-grace-claims"

    type GraceTestAuthHandler
        (
            options: IOptionsMonitor<AuthenticationSchemeOptions>,
            loggerFactory: ILoggerFactory,
            encoder: UrlEncoder,
            clock: ISystemClock
        ) =
        inherit AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder, clock)

        override this.HandleAuthenticateAsync() =
            let tryGetHeader (name: string) =
                let values = this.Request.Headers[name]
                if values.Count = 0 then None else Some(values.ToString())

            match tryGetHeader UserIdHeader with
            | None -> Task.FromResult(AuthenticateResult.NoResult())
            | Some userId when String.IsNullOrWhiteSpace userId -> Task.FromResult(AuthenticateResult.NoResult())
            | Some userId ->
                let claims = ResizeArray<Claim>()
                claims.Add(Claim(PrincipalMapper.GraceUserIdClaim, userId))

                match tryGetHeader ClaimsHeader with
                | None -> ()
                | Some claimHeader when not (String.IsNullOrWhiteSpace claimHeader) ->
                    claimHeader.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun claimValue -> claimValue.Trim())
                    |> Array.filter (fun claimValue -> not (String.IsNullOrWhiteSpace claimValue))
                    |> Array.iter (fun claimValue -> claims.Add(Claim(PrincipalMapper.GraceClaim, claimValue)))
                | _ -> ()

                let identity = ClaimsIdentity(claims, SchemeName)
                let principal = ClaimsPrincipal(identity)
                let ticket = AuthenticationTicket(principal, SchemeName)
                Task.FromResult(AuthenticateResult.Success(ticket))
