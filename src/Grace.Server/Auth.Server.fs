namespace Grace.Server

open Giraffe
open Grace.Server.Security
open Grace.Server.Services
open Grace.Shared.Utilities
open Grace.Types.Types
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Http
open System
open System.Text

module Auth =

    type AuthInfo =
        { GraceUserId: string
          Claims: string list }

    let private tryGetQueryValue (context: HttpContext) (name: string) =
        let values = context.Request.Query[name]
        if values.Count = 0 then None else Some(values.ToString())

    let private getReturnUrl (context: HttpContext) =
        match tryGetQueryValue context "returnUrl" with
        | Some value when not (String.IsNullOrWhiteSpace value) -> value
        | _ -> "/"

    let private renderLoginPage (providers: ExternalAuthConfig.AuthProvider list) (returnUrl: string) =
        let builder = StringBuilder()
        builder.AppendLine("<!doctype html>") |> ignore
        builder.AppendLine("<html lang=\"en\">") |> ignore
        builder.AppendLine("<head><meta charset=\"utf-8\" /><title>Grace Login</title></head>") |> ignore
        builder.AppendLine("<body>") |> ignore
        builder.AppendLine("<h1>Sign in to Grace</h1>") |> ignore

        if providers |> List.isEmpty then
            builder.AppendLine("<p>No login providers are configured.</p>") |> ignore
        else
            builder.AppendLine("<ul>") |> ignore

            for provider in providers do
                let encodedReturnUrl = Uri.EscapeDataString(returnUrl)
                builder.AppendLine(
                    $"<li><a href=\"/auth/login/{provider.Id}?returnUrl={encodedReturnUrl}\">{provider.DisplayName}</a></li>"
                )
                |> ignore

            builder.AppendLine("</ul>") |> ignore

        builder.AppendLine("</body></html>") |> ignore
        builder.ToString()

    let Login: HttpHandler =
        fun next context ->
            task {
                let configuration = ApplicationContext.Configuration()
                let providers = ExternalAuthConfig.getEnabledProviders configuration
                let returnUrl = getReturnUrl context
                let html = renderLoginPage providers returnUrl
                return! htmlString html next context
            }

    let LoginProvider (providerId: string) : HttpHandler =
        fun next context ->
            task {
                let configuration = ApplicationContext.Configuration()
                let returnUrl = getReturnUrl context

                let isMicrosoft =
                    providerId.Equals(ExternalAuthConfig.MicrosoftProviderId, StringComparison.OrdinalIgnoreCase)

                if isMicrosoft && ExternalAuthConfig.isMicrosoftWebConfigured configuration then
                    let properties = AuthenticationProperties()
                    properties.RedirectUri <- returnUrl
                    do! context.ChallengeAsync(ExternalAuthConfig.MicrosoftScheme, properties)
                    return Some context
                else
                    return! RequestErrors.NOT_FOUND "Login provider not available." next context
            }

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
                | None ->
                    return! RequestErrors.UNAUTHORIZED "Grace" "Auth" "Authentication required." next context
                | Some userId ->
                    let claims = PrincipalMapper.getEffectiveClaims context.User |> Set.toList
                    let info =
                        { GraceUserId = userId
                          Claims = claims }

                    let correlationId = getCorrelationId context
                    let returnValue = GraceReturnValue.Create info correlationId
                    return! context |> result200Ok returnValue
            }
