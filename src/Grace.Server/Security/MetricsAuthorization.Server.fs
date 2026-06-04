namespace Grace.Server.Security

open Grace.Types.Authorization
open Microsoft.AspNetCore.Http
open System

type MetricsAuthorizationGateDecision =
    | PassThrough
    | RequirePermissionCheck
    | RejectRequest of statusCode: int * message: string

module MetricsAuthorization =

    [<Literal>]
    let private MetricsPath = "/metrics"

    [<Literal>]
    let AuthenticationRequiredMessage = "Authentication required."

    [<Literal>]
    let ForbiddenMessage = "Forbidden."

    let isMetricsPath (path: PathString) = path.StartsWithSegments(PathString(MetricsPath))

    let decideBeforePermissionCheck (path: PathString) (userId: string option) =
        if not (isMetricsPath path) then
            PassThrough
        else
            match userId with
            | None -> RejectRequest(StatusCodes.Status401Unauthorized, AuthenticationRequiredMessage)
            | Some _ -> RequirePermissionCheck

    let decideAfterPermissionCheck (includeDeniedReason: bool) (permissionResult: PermissionCheckResult) =
        match permissionResult with
        | Allowed _ -> PassThrough
        | Denied reason ->
            let message =
                if
                    includeDeniedReason
                    && not (String.IsNullOrWhiteSpace reason)
                then
                    reason
                else
                    ForbiddenMessage

            RejectRequest(StatusCodes.Status403Forbidden, message)
