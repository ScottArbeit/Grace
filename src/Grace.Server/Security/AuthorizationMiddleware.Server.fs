namespace Grace.Server.Security

open Giraffe
open Grace.Types.Authorization
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open System
open System.Threading.Tasks

module AuthorizationMiddleware =

    let private includeReason = Environment.GetEnvironmentVariable("GRACE_TESTING") = "1"

    let private forbidden (reason: string) : HttpHandler =
        let message =
            if includeReason && not (String.IsNullOrWhiteSpace reason) then
                reason
            else
                "Forbidden."

        setStatusCode StatusCodes.Status403Forbidden >=> text message

    let requiresPermission (operation: Operation) (resourceFromContext: HttpContext -> Task<Resource>) : HttpHandler =
        fun next context ->
            task {
                match PrincipalMapper.tryGetUserId context.User with
                | None -> return! RequestErrors.UNAUTHORIZED "Grace" "Access" "Authentication required." next context
                | Some _ ->
                    let principals = PrincipalMapper.getPrincipals context.User
                    let claims = PrincipalMapper.getEffectiveClaims context.User
                    let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()
                    let! resource = resourceFromContext context
                    let! decision = evaluator.CheckAsync(principals, claims, operation, resource)

                    match decision with
                    | Allowed _ -> return! next context
                    | Denied reason -> return! forbidden reason next context
            }

    let requiresPermissions (operation: Operation) (resourcesFromContext: HttpContext -> Task<Resource list>) : HttpHandler =
        fun next context ->
            task {
                match PrincipalMapper.tryGetUserId context.User with
                | None -> return! RequestErrors.UNAUTHORIZED "Grace" "Access" "Authentication required." next context
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
                                | Denied reason -> return! forbidden reason next context
                        }

                    return! checkResources resources
            }
