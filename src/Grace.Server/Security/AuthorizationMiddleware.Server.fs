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

    let private logMissingAuthentication (context: HttpContext) (operation: Operation) =
        if log.IsEnabled(LogLevel.Warning) then
            log.LogWarning(
                "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Authorization: Authentication required for {operation}. Path: {path}.",
                getCurrentInstantExtended (),
                getMachineName,
                getCorrelationId context,
                operation,
                context.Request.Path.ToString()
            )

    let private logDenied (context: HttpContext) (operation: Operation) (resourceSummary: string) (reason: string) =
        if log.IsEnabled(LogLevel.Warning) then
            log.LogWarning(
                "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Authorization denied for {operation} on {resourceSummary}. Reason: {reason}. Path: {path}.",
                getCurrentInstantExtended (),
                getMachineName,
                getCorrelationId context,
                operation,
                resourceSummary,
                reason,
                context.Request.Path.ToString()
            )

    let requiresPermission (operation: Operation) (resourceFromContext: HttpContext -> Task<Resource>) : HttpHandler =
        fun next context ->
            task {
                match PrincipalMapper.tryGetUserId context.User with
                | None ->
                    logMissingAuthentication context operation
                    return! RequestErrors.UNAUTHORIZED "Grace" "Access" "Authentication required." next context
                | Some _ ->
                    let principals = PrincipalMapper.getPrincipals context.User
                    let claims = PrincipalMapper.getEffectiveClaims context.User
                    let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()
                    let! resource = resourceFromContext context
                    let! decision = evaluator.CheckAsync(principals, claims, operation, resource)

                    match decision with
                    | Allowed _ -> return! next context
                    | Denied reason ->
                        logDenied context operation (formatResource resource) reason
                        return! forbidden reason next context
            }

    let requiresPermissions (operation: Operation) (resourcesFromContext: HttpContext -> Task<Resource list>) : HttpHandler =
        fun next context ->
            task {
                match PrincipalMapper.tryGetUserId context.User with
                | None ->
                    logMissingAuthentication context operation
                    return! RequestErrors.UNAUTHORIZED "Grace" "Access" "Authentication required." next context
                | Some _ ->
                    let principals = PrincipalMapper.getPrincipals context.User
                    let claims = PrincipalMapper.getEffectiveClaims context.User
                    let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()
                    let! resources = resourcesFromContext context

                    let rec checkResources remaining =
                        task {
                            match remaining with
                            | [] -> return! next context
                            | resource :: rest ->
                                let! decision = evaluator.CheckAsync(principals, claims, operation, resource)

                                match decision with
                                | Allowed _ -> return! checkResources rest
                                | Denied reason ->
                                    logDenied context operation (formatResourceSummary resources) reason
                                    return! forbidden reason next context
                        }

                    return! checkResources resources
            }
