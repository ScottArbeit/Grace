namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Server.Security
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Parameters.Auth
open Grace.Shared.Utilities
open Grace.Types.PersonalAccessToken
open Grace.Types.Types
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open NodaTime
open System
open System.Text

module Auth =

    type AuthInfo = { GraceUserId: string; Claims: string list; RawClaims: (string * string) list }

    let private isTesting () =
        match Environment.GetEnvironmentVariable("GRACE_TESTING") with
        | null -> false
        | value ->
            value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)

    let private tryGetQueryValue (context: HttpContext) (name: string) =
        let values = context.Request.Query[name]
        if values.Count = 0 then None else Some(values.ToString())

    let private getReturnUrl (context: HttpContext) =
        match tryGetQueryValue context "returnUrl" with
        | Some value when not (String.IsNullOrWhiteSpace value) -> value
        | _ -> "/"

    let private renderLoginPage (returnUrl: string) =
        let builder = StringBuilder()
        builder.AppendLine("<!doctype html>") |> ignore
        builder.AppendLine("<html lang=\"en\">") |> ignore

        builder.AppendLine("<head><meta charset=\"utf-8\" /><title>Grace Login</title></head>")
        |> ignore

        builder.AppendLine("<body>") |> ignore
        builder.AppendLine("<h1>Sign in to Grace</h1>") |> ignore

        builder.AppendLine(
            "<p>Interactive browser login is not available on the server in this phase. Use the CLI (grace auth login) or provide GRACE_TOKEN / Auth0 M2M credentials.</p>"
        )
        |> ignore

        builder.AppendLine("</body></html>") |> ignore
        builder.ToString()

    let Login: HttpHandler =
        fun next context ->
            task {
                let returnUrl = getReturnUrl context
                let html = renderLoginPage returnUrl
                return! htmlString html next context
            }

    let LoginProvider (providerId: string) : HttpHandler =
        fun next context -> task { return! RequestErrors.NOT_FOUND "Login provider not available." next context }

    let Logout: HttpHandler =
        fun next context ->
            task {
                let returnUrl = getReturnUrl context
                do! context.SignOutAsync()
                return! redirectTo false returnUrl next context
            }

    let Me: HttpHandler =
        fun next context ->
            task {
                match PrincipalMapper.tryGetUserId context.User with
                | None -> return! RequestErrors.UNAUTHORIZED "Grace" "Auth" "Authentication required." next context
                | Some userId ->
                    let claims = PrincipalMapper.getEffectiveClaims context.User |> Set.toList

                    let rawClaims =
                        context.User.Claims
                        |> Seq.map (fun claim -> (claim.Type, claim.Value))
                        |> Seq.sortBy (fun (claimType, claimValue) -> (claimType, claimValue))
                        |> Seq.toList

                    let info = { GraceUserId = userId; Claims = claims; RawClaims = rawClaims }

                    let correlationId = getCorrelationId context
                    let returnValue = GraceReturnValue.Create info correlationId
                    return! context |> result200Ok returnValue
            }

    let private tryGetConfigValue (configuration: IConfiguration) (name: string) =
        if isNull configuration then
            None
        else
            let value = configuration[getConfigKey name]
            if String.IsNullOrWhiteSpace value then None else Some(value.Trim())

    let private getIntConfig (configuration: IConfiguration) (name: string) (defaultValue: int) =
        match tryGetConfigValue configuration name with
        | None -> defaultValue
        | Some value ->
            match Int32.TryParse value with
            | true, parsed when parsed > 0 -> parsed
            | _ -> defaultValue

    let private getBoolConfig (configuration: IConfiguration) (name: string) (defaultValue: bool) =
        match tryGetConfigValue configuration name with
        | None -> defaultValue
        | Some value ->
            match Boolean.TryParse value with
            | true, parsed -> parsed
            | _ -> defaultValue

    let private getPatPolicy (configuration: IConfiguration) =
        let defaultDays = getIntConfig configuration Constants.EnvironmentVariables.GraceAuthPatDefaultLifetimeDays 90

        let maxDays = getIntConfig configuration Constants.EnvironmentVariables.GraceAuthPatMaxLifetimeDays 365

        let normalizedMax = if maxDays <= 0 then 365 else maxDays
        let normalizedDefault = if defaultDays <= 0 then 90 else defaultDays
        let effectiveDefault = min normalizedDefault normalizedMax
        let allowNoExpiry = getBoolConfig configuration Constants.EnvironmentVariables.GraceAuthPatAllowNoExpiry false

        effectiveDefault, normalizedMax, allowNoExpiry

    let private getClaimValues (context: HttpContext) (claimType: string) =
        context.User.Claims
        |> Seq.filter (fun claim -> claim.Type = claimType)
        |> Seq.map (fun claim -> claim.Value)
        |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> Seq.distinct
        |> Seq.toList

    let TokenCreate (configuration: IConfiguration) : HttpHandler =
        fun next context ->
            task {
                let correlationId = getCorrelationId context

                match PrincipalMapper.tryGetUserId context.User with
                | None -> return! RequestErrors.UNAUTHORIZED "Grace" "Auth" "Authentication required." next context
                | Some userId ->
                    let! parameters = context.BindJsonAsync<CreatePersonalAccessTokenParameters>()
                    let now = getCurrentInstant ()
                    let defaultDays, maxDays, allowNoExpiry = getPatPolicy configuration
                    let maxSeconds = int64 maxDays * 86400L

                    let expiresAtOptionResult =
                        if parameters.NoExpiry then
                            if not allowNoExpiry then
                                Error "No-expiry tokens are disabled by server policy."
                            else
                                Ok None
                        else
                            let requestedSeconds =
                                if parameters.ExpiresInSeconds > 0L then
                                    parameters.ExpiresInSeconds
                                else
                                    int64 defaultDays * 86400L

                            if requestedSeconds > maxSeconds then
                                Error $"Requested lifetime exceeds max of {maxDays} days."
                            else
                                let expiresAt = now.Plus(Duration.FromSeconds(float requestedSeconds))
                                Ok(Some expiresAt)

                    match expiresAtOptionResult with
                    | Error message ->
                        let graceError = GraceError.Create message correlationId
                        return! context |> result400BadRequest graceError
                    | Ok expiresAtOption ->
                        let claims = getClaimValues context PrincipalMapper.GraceClaim
                        let groupIds = getClaimValues context PrincipalMapper.GraceGroupIdClaim
                        let actor = PersonalAccessToken.CreateActorProxy userId correlationId

                        let! result = actor.CreateToken parameters.TokenName claims groupIds expiresAtOption now correlationId

                        match result with
                        | Ok created ->
                            let returnValue = GraceReturnValue.Create created correlationId
                            return! context |> result200Ok returnValue
                        | Error error -> return! context |> result400BadRequest error
            }

    let TokenList (configuration: IConfiguration) : HttpHandler =
        fun next context ->
            task {
                let correlationId = getCorrelationId context

                match PrincipalMapper.tryGetUserId context.User with
                | None -> return! RequestErrors.UNAUTHORIZED "Grace" "Auth" "Authentication required." next context
                | Some userId ->
                    let! parameters = context.BindJsonAsync<ListPersonalAccessTokensParameters>()
                    let now = getCurrentInstant ()
                    let actor = PersonalAccessToken.CreateActorProxy userId correlationId
                    let! tokens = actor.ListTokens parameters.IncludeRevoked parameters.IncludeExpired now correlationId
                    let returnValue = GraceReturnValue.Create tokens correlationId
                    return! context |> result200Ok returnValue
            }

    let TokenRevoke (configuration: IConfiguration) : HttpHandler =
        fun next context ->
            task {
                let correlationId = getCorrelationId context

                match PrincipalMapper.tryGetUserId context.User with
                | None -> return! RequestErrors.UNAUTHORIZED "Grace" "Auth" "Authentication required." next context
                | Some userId ->
                    let! parameters = context.BindJsonAsync<RevokePersonalAccessTokenParameters>()
                    let now = getCurrentInstant ()
                    let actor = PersonalAccessToken.CreateActorProxy userId correlationId
                    let! result = actor.RevokeToken parameters.TokenId now correlationId

                    match result with
                    | Ok summary ->
                        let returnValue = GraceReturnValue.Create summary correlationId
                        return! context |> result200Ok returnValue
                    | Error error -> return! context |> result400BadRequest error
            }
