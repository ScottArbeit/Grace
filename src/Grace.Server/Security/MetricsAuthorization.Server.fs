namespace Grace.Server.Security

open Grace.Types.Authorization
open Microsoft.AspNetCore.Http
open System

/// Represents metrics authorization gate decision used by Grace Server APIs and background services.
type MetricsAuthorizationGateDecision =
    | PassThrough
    | RequirePermissionCheck
    | RejectRequest of statusCode: int * message: string

/// Contains Grace Server metrics authorization behavior and supporting helpers.
module MetricsAuthorization =

    [<Literal>]
    let private MetricsPath = "/metrics"

    [<Literal>]
    let AuthenticationRequiredMessage = "Authentication required."

    [<Literal>]
    let ForbiddenMessage = "Forbidden."

    /// Determines whether metrics path.
    let isMetricsPath (path: PathString) = path.StartsWithSegments(PathString(MetricsPath))

    /// Implements decide before permission check for the server request pipeline.
    let decideBeforePermissionCheck (path: PathString) (userId: string option) =
        if not (isMetricsPath path) then
            PassThrough
        else
            match userId with
            | None -> RejectRequest(StatusCodes.Status401Unauthorized, AuthenticationRequiredMessage)
            | Some _ -> RequirePermissionCheck

    /// Implements decide after permission check for the server request pipeline.
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
