namespace Grace.Server.Security

open Grace.Types.PersonalAccessToken
open Microsoft.AspNetCore.Authentication.JwtBearer
open System

module AuthSchemeSelection =

    [<Literal>]
    let private BearerPrefix = "Bearer "

    let private isGracePatBearer (authorization: string) =
        if
            not (String.IsNullOrWhiteSpace authorization)
            && authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
        then
            let token =
                authorization
                    .Substring(BearerPrefix.Length)
                    .Trim()

            token.StartsWith(TokenPrefix, StringComparison.Ordinal)
        else
            false

    let selectScheme (isTesting: bool) (hasOidc: bool) (authorization: string) =
        if isGracePatBearer authorization then PersonalAccessTokenAuth.SchemeName
        else if isTesting then TestAuth.SchemeName
        else if hasOidc then JwtBearerDefaults.AuthenticationScheme
        else PersonalAccessTokenAuth.SchemeName
