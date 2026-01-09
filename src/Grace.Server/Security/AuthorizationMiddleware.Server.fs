namespace Grace.Server.Security

open Giraffe
open Grace.Server.ApplicationContext
open Grace.Server.Services
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open System
open System.Threading.Tasks

module AuthorizationMiddleware =

    let private log = loggerFactory.CreateLogger("AuthorizationMiddleware.Server")

    let private includeReason = Environment.GetEnvironmentVariable("GRACE_TESTING") = "1"

    let private forbidden (reason: string) : HttpHandler =
        let message =
            if includeReason && not (String.IsNullOrWhiteSpace reason) then
                reason
            else
                "Forbidden."

        setStatusCode StatusCodes.Status403Forbidden >=> text message

    let private formatResource (resource: Resource) =
        match resource with
        | Resource.System -> "System"
        | Resource.Owner ownerId -> $"Owner:{ownerId}"
        | Resource.Organization(ownerId, organizationId) -> $"Organization:{ownerId}/{organizationId}"
        | Resource.Repository(ownerId, organizationId, repositoryId) -> $"Repository:{ownerId}/{organizationId}/{repositoryId}"
        | Resource.Branch(ownerId, organizationId, repositoryId, branchId) -> $"Branch:{ownerId}/{organizationId}/{repositoryId}/{branchId}"
        | Resource.Path(ownerId, organizationId, repositoryId, relativePath) -> $"Path:{ownerId}/{organizationId}/{repositoryId}:{relativePath}"

    let private formatResourceSummary (resources: Resource list) =
        match resources with
        | [] -> "None"
        | head :: tail ->
            let headText = formatResource head
            if tail.IsEmpty then headText else $"{headText} (+{tail.Length} more)"

    let private formatPrincipal (principal: Principal) = $"{principal.PrincipalType}:{principal.PrincipalId}"

    let private formatPrincipals (principals: Principal list) =
        match principals with
        | [] -> "None"
        | _ -> principals |> List.map formatPrincipal |> String.concat ", "

    let private tryGetIdentityName (context: HttpContext) =
        let identity = context.User.Identity

        if isNull identity || String.IsNullOrWhiteSpace identity.Name then
            None
        else
            Some identity.Name

    let private formatPrincipalSummary (context: HttpContext) (principals: Principal list) =
        if principals.IsEmpty then
            match tryGetIdentityName context with
            | Some name -> $"Identity:{name}"
            | None -> "None"
        else
            formatPrincipals principals

    let private logMissingAuthentication (context: HttpContext) (operation: Operation) (principalSummary: string) =
        if log.IsEnabled(LogLevel.Warning) then
            log.LogWarning(
                "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Authorization: Authentication required for {operation}. Principal: {principal}. Path: {path}.",
                getCurrentInstantExtended (),
                getMachineName,
                getCorrelationId context,
                operation,
                principalSummary,
                context.Request.Path.ToString()
            )

    let private logDenied (context: HttpContext) (operation: Operation) (principalSummary: string) (resourceSummary: string) (reason: string) =
        if log.IsEnabled(LogLevel.Warning) then
            log.LogWarning(
                "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Authorization denied for {operation} by {principal} on {resourceSummary}. Reason: {reason}. Path: {path}.",
                getCurrentInstantExtended (),
                getMachineName,
                getCorrelationId context,
                operation,
                principalSummary,
                resourceSummary,
                reason,
                context.Request.Path.ToString()
            )

    let requiresPermission (operation: Operation) (resourceFromContext: HttpContext -> Task<Resource>) : HttpHandler =
        fun next context ->
            task {
                match PrincipalMapper.tryGetUserId context.User with
                | None ->
                    let principalSummary = PrincipalMapper.getPrincipals context.User |> formatPrincipalSummary context

                    logMissingAuthentication context operation principalSummary
                    return! RequestErrors.UNAUTHORIZED "Grace" "Access" "Authentication required." next context
                | Some _ ->
                    let principals = PrincipalMapper.getPrincipals context.User
                    let principalSummary = formatPrincipals principals
                    let claims = PrincipalMapper.getEffectiveClaims context.User
                    let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()
                    let! resource = resourceFromContext context
                    let! decision = evaluator.CheckAsync(principals, claims, operation, resource)

                    match decision with
                    | Allowed _ -> return! next context
                    | Denied reason ->
                        logDenied context operation principalSummary (formatResource resource) reason
                        return! forbidden reason next context
            }

    let requiresPermissions (operation: Operation) (resourcesFromContext: HttpContext -> Task<Resource list>) : HttpHandler =
        fun next context ->
            task {
                match PrincipalMapper.tryGetUserId context.User with
                | None ->
                    let principalSummary = PrincipalMapper.getPrincipals context.User |> formatPrincipalSummary context

                    logMissingAuthentication context operation principalSummary
                    return! RequestErrors.UNAUTHORIZED "Grace" "Access" "Authentication required." next context
                | Some _ ->
                    let principals = PrincipalMapper.getPrincipals context.User
                    let principalSummary = formatPrincipals principals
                    let claims = PrincipalMapper.getEffectiveClaims context.User
                    let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()
                    let! resources = resourcesFromContext context

                    let mutable deniedReason: string option = None

                    for resource in resources do
                        if deniedReason.IsNone then
                            let! decision = evaluator.CheckAsync(principals, claims, operation, resource)

                            match decision with
                            | Allowed _ -> ()
                            | Denied reason -> deniedReason <- Some reason

                    match deniedReason with
                    | None -> return! next context
                    | Some reason ->
                        logDenied context operation principalSummary (formatResourceSummary resources) reason
                        return! forbidden reason next context
            }
