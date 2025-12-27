namespace Grace.Server.Middleware

open Grace.Shared
open Grace.Shared.Constants
open Microsoft.AspNetCore.Http
open System.Security.Claims
open System.Threading.Tasks

type DevAuthHeaderMiddleware(next: RequestDelegate) =

    member _.Invoke(context: HttpContext) : Task =
        let headerValue = context.Request.Headers[Constants.Authentication.DevUserIdHeader].ToString()

        if not <| System.String.IsNullOrWhiteSpace headerValue then
            if
                isNull context.User
                || isNull context.User.Identity
                || not context.User.Identity.IsAuthenticated
            then
                let claims =
                    [ Claim(Constants.Authentication.GraceUserIdClaim, headerValue)
                      Claim(ClaimTypes.NameIdentifier, headerValue)
                      Claim(ClaimTypes.Name, headerValue) ]

                let identity = ClaimsIdentity(claims, "DevHeader")
                context.User <- ClaimsPrincipal(identity)

        next.Invoke(context)
