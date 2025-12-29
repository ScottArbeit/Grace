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
        fun _ context ->
            task {
                context.SetStatusCode StatusCodes.Status403Forbidden

                let message =
                    if includeReason && not (String.IsNullOrWhiteSpace reason) then
                        reason
                    else
                        "Forbidden."

                return! context.WriteTextAsync message
            }

    let private requireAuthenticated (context: HttpContext) (next: HttpFunc) (handler: HttpHandler) =
        match PrincipalMapper.tryGetUserId context.User with
        | Some _ -> handler next context
        | None -> RequestErrors.UNAUTHORIZED "Grace" "Access" "Authentication required." next context

    let requiresPermission (operation: Operation) (resourceFromContext: HttpContext -> Task<Resource>) : HttpHandler =
        fun next context ->
            task {
                return!
                    requireAuthenticated context next (fun next innerContext ->
                        task {
                            let principals = PrincipalMapper.getPrincipals innerContext.User
                            let claims = PrincipalMapper.getEffectiveClaims innerContext.User
                            let evaluator = innerContext.RequestServices.GetRequiredService<IGracePermissionEvaluator>()
                            let! resource = resourceFromContext innerContext
                            let! decision = evaluator.CheckAsync(principals, claims, operation, resource)

                            match decision with
                            | Allowed _ -> return! next innerContext
                            | Denied reason -> return! forbidden reason next innerContext
                        })
            }

    let requiresPermissions (operation: Operation) (resourcesFromContext: HttpContext -> Task<Resource list>) : HttpHandler =
        fun next context ->
            task {
                return!
                    requireAuthenticated context next (fun next innerContext ->
                        task {
                            let principals = PrincipalMapper.getPrincipals innerContext.User
                            let claims = PrincipalMapper.getEffectiveClaims innerContext.User
                            let evaluator = innerContext.RequestServices.GetRequiredService<IGracePermissionEvaluator>()
                            let! resources = resourcesFromContext innerContext

                            let rec checkResources remaining =
                                task {
                                    match remaining with
                                    | [] -> return! next innerContext
                                    | resource :: rest ->
                                        let! decision = evaluator.CheckAsync(principals, claims, operation, resource)

                                        match decision with
                                        | Allowed _ -> return! checkResources rest
                                        | Denied reason -> return! forbidden reason next innerContext
                                }

                            return! checkResources resources
                        })
            }
