namespace Grace.Server.Security

open Grace.Types.PersonalAccessToken
open Microsoft.AspNetCore.Authentication.JwtBearer
open System

/// Contains Grace Server auth scheme selection behavior and supporting helpers.
module AuthSchemeSelection =

    [<Literal>]
    let private BearerPrefix = "Bearer "

    /// Determines whether grace pat bearer.
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

    /// Implements has bearer authorization for the server request pipeline.
    let private hasBearerAuthorization (authorization: string) =
        not (String.IsNullOrWhiteSpace authorization)
        && authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)

    /// Implements has explicit test user for the server request pipeline.
    let private hasExplicitTestUser (testUserId: string) = not (String.IsNullOrWhiteSpace testUserId)

    /// Implements select scheme for the server request pipeline.
    let selectScheme (isTesting: bool) (hasOidc: bool) (authorization: string) (testUserId: string) =
        if isGracePatBearer authorization then
            PersonalAccessTokenAuth.SchemeName
        elif hasBearerAuthorization authorization && hasOidc then
            JwtBearerDefaults.AuthenticationScheme
        elif isTesting && hasExplicitTestUser testUserId then
            TestAuth.SchemeName
        elif
            isTesting
            && not (hasBearerAuthorization authorization)
        then
            TestAuth.SchemeName
        else if hasOidc then
            JwtBearerDefaults.AuthenticationScheme
        else
            PersonalAccessTokenAuth.SchemeName
