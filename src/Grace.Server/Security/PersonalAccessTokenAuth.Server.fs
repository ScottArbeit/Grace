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

    [<Literal>]
    let InvalidTokenFailure = "Invalid token."

    type ParsedAuthorization =
        | NoToken
        | NotGracePersonalAccessToken
        | MalformedGracePersonalAccessToken
        | ParsedGracePersonalAccessToken of userId: string * tokenId: PersonalAccessTokenId * secret: byte array

    let parseAuthorizationHeader (authorization: string) =
        if String.IsNullOrWhiteSpace authorization then
            NoToken
        elif not (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) then
            NoToken
        else
            let token = authorization.Substring("Bearer ".Length).Trim()

            if String.IsNullOrWhiteSpace token then
                NoToken
            elif not (token.StartsWith(TokenPrefix, StringComparison.Ordinal)) then
                NotGracePersonalAccessToken
            else
                match tryParseToken token with
                | None -> MalformedGracePersonalAccessToken
                | Some (userId, tokenId, secret) -> ParsedGracePersonalAccessToken(userId, tokenId, secret)

    let toAuthenticationClaims (result: PersonalAccessTokenValidationResult) =
        let claims = ResizeArray<Claim>()
        claims.Add(Claim(PrincipalMapper.GraceUserIdClaim, result.UserId))

        result.Claims
        |> List.iter (fun claimValue -> claims.Add(Claim(PrincipalMapper.GraceClaim, claimValue)))

        result.GroupIds
        |> List.iter (fun groupId -> claims.Add(Claim(PrincipalMapper.GraceGroupIdClaim, groupId)))

        claims |> Seq.toList

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

                match parseAuthorizationHeader authorization with
                | NoToken -> return AuthenticateResult.NoResult()
                | NotGracePersonalAccessToken -> return AuthenticateResult.NoResult()
                | MalformedGracePersonalAccessToken -> return AuthenticateResult.Fail(InvalidTokenFailure)
                | ParsedGracePersonalAccessToken (userId, tokenId, secret) ->
                    let correlationId = tryGetCorrelationId httpContext
                    let actor = PersonalAccessToken.CreateActorProxy userId correlationId
                    let! validation = actor.ValidateToken tokenId secret (getCurrentInstant ()) correlationId

                    match validation with
                    | None -> return AuthenticateResult.Fail(InvalidTokenFailure)
                    | Some result ->
                        let claims = toAuthenticationClaims result
                        let identity = ClaimsIdentity(claims, SchemeName)
                        let principal = ClaimsPrincipal(identity)
                        let ticket = AuthenticationTicket(principal, SchemeName)
                        return AuthenticateResult.Success(ticket)
            }
