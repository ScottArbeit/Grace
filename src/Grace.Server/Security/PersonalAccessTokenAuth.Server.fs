namespace Grace.Server.Security

open Grace.Actors.Extensions.ActorProxy
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.PersonalAccessToken
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open System
open System.Security.Claims
open System.Text.Encodings.Web
open System.Threading.Tasks

module PersonalAccessTokenAuth =
    [<Literal>]
    let SchemeName = "GracePat"

    type PersonalAccessTokenAuthHandler(options: IOptionsMonitor<AuthenticationSchemeOptions>, loggerFactory: ILoggerFactory, encoder: UrlEncoder) =
        inherit AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)

        let tryGetCorrelationId (context: HttpContext) =
            match context.Items.TryGetValue(Constants.CorrelationId) with
            | true, value ->
                match value with
                | :? string as correlationId -> correlationId
                | _ -> String.Empty
            | _ -> String.Empty

        override this.HandleAuthenticateAsync() =
            let request = this.Request
            let httpContext = this.Context

            task {
                let authorization = request.Headers.Authorization.ToString()

                if String.IsNullOrWhiteSpace authorization then
                    return AuthenticateResult.NoResult()
                elif not (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) then
                    return AuthenticateResult.NoResult()
                else
                    let token = authorization.Substring("Bearer ".Length).Trim()

                    if String.IsNullOrWhiteSpace token then
                        return AuthenticateResult.NoResult()
                    elif not (token.StartsWith(TokenPrefix, StringComparison.Ordinal)) then
                        return AuthenticateResult.NoResult()
                    else
                        match tryParseToken token with
                        | None -> return AuthenticateResult.Fail("Invalid token.")
                        | Some(userId, tokenId, secret) ->
                            let correlationId = tryGetCorrelationId httpContext
                            let actor = PersonalAccessToken.CreateActorProxy userId correlationId
                            let! validation = actor.ValidateToken tokenId secret (getCurrentInstant ()) correlationId

                            match validation with
                            | None -> return AuthenticateResult.Fail("Invalid token.")
                            | Some result ->
                                let claims = ResizeArray<Claim>()
                                claims.Add(Claim(PrincipalMapper.GraceUserIdClaim, result.UserId))

                                result.Claims
                                |> List.iter (fun claimValue -> claims.Add(Claim(PrincipalMapper.GraceClaim, claimValue)))

                                result.GroupIds
                                |> List.iter (fun groupId -> claims.Add(Claim(PrincipalMapper.GraceGroupIdClaim, groupId)))

                                let identity = ClaimsIdentity(claims, SchemeName)
                                let principal = ClaimsPrincipal(identity)
                                let ticket = AuthenticationTicket(principal, SchemeName)
                                return AuthenticateResult.Success(ticket)
            }
