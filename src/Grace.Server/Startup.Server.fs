namespace Grace.Server

open Asp.Versioning
open Asp.Versioning.ApiExplorer
open Azure.Core
open Azure.Identity
open Azure.Monitor.OpenTelemetry.Exporter
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Giraffe
open Giraffe.EndpointRouting
open Grace.Actors
open Grace.Shared
open Grace.Shared.AzureEnvironment
open Grace.Shared.Utilities
open Grace.Server
open Grace.Server.Middleware
open Grace.Server.ReminderService
open Grace.Server.Security
open Grace.Server.Security.TestAuth
open Grace.Shared.Converters
open Grace.Shared.Parameters
open Grace.Types.Types
open Grace.Types.Authorization
open Grace.Types.PersonalAccessToken
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.Authentication.JwtBearer
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.HttpLogging
open Microsoft.AspNetCore.Mvc
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Hosting.Internal
open Microsoft.Extensions.Logging
open Microsoft.OpenApi
open NodaTime
open OpenTelemetry
open OpenTelemetry.Exporter
open OpenTelemetry.Instrumentation.AspNetCore
open OpenTelemetry.Metrics
open OpenTelemetry.Resources
open OpenTelemetry.Trace
open Orleans
open Orleans.Persistence.Cosmos
open Orleans.Serialization
open Orleans.Serialization.NodaTime
open Swashbuckle.AspNetCore.Swagger
open System
open System.Linq
open System.Reflection
open System.Text.Json
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Linq
open System.Text
open System.Threading
open System.Threading.Tasks
open FSharpPlus
open System.Net.Http
open System.Net.Security
open Microsoft.IdentityModel.Tokens
open System.Security.Claims

module Application =

    type AuthResource = Grace.Types.Authorization.Resource

    type SecurityClassification =
        | AllowAnonymous
        | Authenticated
        | Authorized of Operation * (HttpContext -> Task<AuthResource>)
        | AuthorizedMany of Operation * (HttpContext -> Task<AuthResource list>)
        | AuthorizedResolved of (HttpContext -> Task<Result<Operation * AuthResource, GraceError>>)
        | AuthorizedResolvedOptional of (HttpContext -> Task<Result<(Operation * AuthResource) option, GraceError>>)

    type CosmosWarmup(cosmosClient: CosmosClient, log: ILogger<CosmosWarmup>) =
        interface IHostedService with
            member _.StartAsync(ct: CancellationToken) : Task =
                log.LogInformation("Waiting for Cosmos DB emulator to be ready...")

                let rec loop i =
                    task {
                        if i > 120 || ct.IsCancellationRequested then
                            log.LogError("Cosmos DB emulator was not ready in time.")
                            return ()
                        else
                            try
                                let! _ = cosmosClient.ReadAccountAsync()
                                log.LogInformation("Cosmos DB emulator ready.")
                                return ()
                            with
                            | ex ->
                                log.LogWarning("Cosmos DB emulator not ready, retry {Try}...", i)
                                do! Task.Delay(1000, ct)
                                return! loop (i + 1)
                    }

                loop 1 :> Task

            member _.StopAsync(_ct: CancellationToken) : Task = Task.CompletedTask

    let private defaultAzureCredential = lazy (DefaultAzureCredential())

    type Startup(configuration: IConfiguration) =

        do
            ApplicationContext.setConfiguration configuration

            ApplicationContext.configurePubSubSettings ()
            |> ApplicationContext.setPubSubSettings

        let notLoggedIn = RequestErrors.UNAUTHORIZED "Basic" "Some Realm" "You must be logged in."

        let mustBeLoggedIn = requiresAuthentication notLoggedIn

        let graceServerVersion =
            FileVersionInfo
                .GetVersionInfo(
                    Assembly.GetExecutingAssembly().Location
                )
                .FileVersion

        let repositoryResourceFromContext (context: HttpContext) =
            task {
                let graceIds = Services.getGraceIds context
                return AuthResource.Repository(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId)
            }

        let uploadPathResourcesFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Storage.GetUploadMetadataForFilesParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let graceIds = Services.getGraceIds context

                let resources =
                    parameters.FileVersions
                    |> Seq.map (fun fileVersion ->
                        AuthResource.Path(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId, fileVersion.RelativePath))
                    |> Seq.toList

                return resources
            }

        let downloadPathResourceFromContext (context: HttpContext) =
            task {
                context.Request.EnableBuffering()
                let! parameters = context.BindJsonAsync<Storage.GetDownloadUriParameters>()

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                let graceIds = Services.getGraceIds context
                return AuthResource.Path(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId, parameters.FileVersion.RelativePath)
            }

        let branchResourceFromContext (context: HttpContext) =
            task {
                let graceIds = Services.getGraceIds context
                return AuthResource.Branch(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId, graceIds.BranchId)
            }

        let organizationResourceFromContext (context: HttpContext) =
            task {
                let graceIds = Services.getGraceIds context
                return AuthResource.Organization(graceIds.OwnerId, graceIds.OrganizationId)
            }

        let ownerResourceFromContext (context: HttpContext) =
            task {
                let graceIds = Services.getGraceIds context
                return AuthResource.Owner(graceIds.OwnerId)
            }

        let systemResourceFromContext (_context: HttpContext) = Task.FromResult AuthResource.System

        let makeSecurityKey (methodName: string) (path: string) =
            $"{methodName.ToUpperInvariant()} {path}"

        let securityEntries =
            [
                makeSecurityKey "GET" "/", AllowAnonymous
                makeSecurityKey "GET" "/healthz", AllowAnonymous
                makeSecurityKey "GET" "/auth/oidc/config", AllowAnonymous
                makeSecurityKey "GET" "/auth/login", AllowAnonymous
                makeSecurityKey "GET" "/auth/login/%s", AllowAnonymous

                makeSecurityKey "GET" "/notifications", Authenticated
                makeSecurityKey "GET" "/metrics", Authenticated

                makeSecurityKey "GET" "/auth/me", Authenticated
                makeSecurityKey "GET" "/auth/logout", Authenticated
                makeSecurityKey "POST" "/auth/token/create", Authenticated
                makeSecurityKey "POST" "/auth/token/list", Authenticated
                makeSecurityKey "POST" "/auth/token/revoke", Authenticated

                makeSecurityKey "GET" "/access/listRoles", Authenticated
                makeSecurityKey "POST" "/access/grantRole",
                AuthorizedResolved(Grace.Server.Access.resolveAdminRequirementFromAccessParameters (fun (p: Access.GrantRoleParameters) -> p.ScopeKind))
                makeSecurityKey "POST" "/access/revokeRole",
                AuthorizedResolved(Grace.Server.Access.resolveAdminRequirementFromAccessParameters (fun (p: Access.RevokeRoleParameters) -> p.ScopeKind))
                makeSecurityKey "POST" "/access/listRoleAssignments",
                AuthorizedResolved(
                    Grace.Server.Access.resolveAdminRequirementFromAccessParameters (fun (p: Access.ListRoleAssignmentsParameters) -> p.ScopeKind)
                )
                makeSecurityKey "POST" "/access/upsertPathPermission",
                AuthorizedResolved(Grace.Server.Access.resolveRepoAdminRequirementFromAccessParameters<Access.UpsertPathPermissionParameters>)
                makeSecurityKey "POST" "/access/removePathPermission",
                AuthorizedResolved(Grace.Server.Access.resolveRepoAdminRequirementFromAccessParameters<Access.RemovePathPermissionParameters>)
                makeSecurityKey "POST" "/access/listPathPermissions",
                AuthorizedResolved(Grace.Server.Access.resolveRepoAdminRequirementFromAccessParameters<Access.ListPathPermissionsParameters>)
                makeSecurityKey "POST" "/access/checkPermission",
                AuthorizedResolvedOptional(Grace.Server.Access.resolveCheckPermissionAdminRequirement)

                makeSecurityKey "POST" "/branch/get", Authorized(Operation.BranchRead, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/getEvents", Authorized(Operation.BranchRead, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/getExternals", Authorized(Operation.BranchRead, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/getCheckpoints", Authorized(Operation.BranchRead, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/getCommits", Authorized(Operation.BranchRead, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/getDiffsForReferenceType", Authorized(Operation.BranchRead, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/getParentBranch", Authorized(Operation.BranchRead, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/getPromotions", Authorized(Operation.BranchRead, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/getRecursiveSize", Authorized(Operation.BranchRead, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/getReference", Authorized(Operation.BranchRead, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/getReferences", Authorized(Operation.BranchRead, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/getSaves", Authorized(Operation.BranchRead, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/getTags", Authorized(Operation.BranchRead, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/getVersion", Authorized(Operation.BranchRead, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/listContents", Authorized(Operation.BranchRead, branchResourceFromContext)

                makeSecurityKey "POST" "/branch/create", Authorized(Operation.BranchWrite, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/assign", Authorized(Operation.BranchWrite, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/checkpoint", Authorized(Operation.BranchWrite, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/commit", Authorized(Operation.BranchWrite, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/createExternal", Authorized(Operation.BranchWrite, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/promote", Authorized(Operation.BranchWrite, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/rebase", Authorized(Operation.BranchWrite, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/save", Authorized(Operation.BranchWrite, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/tag", Authorized(Operation.BranchWrite, branchResourceFromContext)

                makeSecurityKey "POST" "/branch/delete", Authorized(Operation.BranchAdmin, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/enableAssign", Authorized(Operation.BranchAdmin, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/enableAutoRebase", Authorized(Operation.BranchAdmin, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/enableCheckpoint", Authorized(Operation.BranchAdmin, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/enableCommit", Authorized(Operation.BranchAdmin, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/enableExternal", Authorized(Operation.BranchAdmin, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/enablePromotion", Authorized(Operation.BranchAdmin, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/enableSave", Authorized(Operation.BranchAdmin, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/enableTag", Authorized(Operation.BranchAdmin, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/setPromotionMode", Authorized(Operation.BranchAdmin, branchResourceFromContext)
                makeSecurityKey "POST" "/branch/updateParentBranch", Authorized(Operation.BranchAdmin, branchResourceFromContext)

                makeSecurityKey "POST" "/diff/getDiff", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/diff/getDiffBySha256Hash", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/diff/populate", Authorized(Operation.RepoRead, repositoryResourceFromContext)

                makeSecurityKey "POST" "/directory/get", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/directory/getByDirectoryIds", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/directory/getBySha256Hash", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/directory/getDirectoryVersionsRecursive", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/directory/getZipFile", Authorized(Operation.RepoRead, repositoryResourceFromContext)

                makeSecurityKey "POST" "/directory/create", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/directory/saveDirectoryVersions", Authorized(Operation.RepoWrite, repositoryResourceFromContext)

                makeSecurityKey "POST" "/owner/create", Authorized(Operation.SystemAdmin, systemResourceFromContext)
                makeSecurityKey "POST" "/owner/get", Authorized(Operation.OwnerRead, ownerResourceFromContext)
                makeSecurityKey "POST" "/owner/listOrganizations", Authorized(Operation.OwnerRead, ownerResourceFromContext)
                makeSecurityKey "POST" "/owner/delete", Authorized(Operation.OwnerAdmin, ownerResourceFromContext)
                makeSecurityKey "POST" "/owner/setDescription", Authorized(Operation.OwnerAdmin, ownerResourceFromContext)
                makeSecurityKey "POST" "/owner/setName", Authorized(Operation.OwnerAdmin, ownerResourceFromContext)
                makeSecurityKey "POST" "/owner/setSearchVisibility", Authorized(Operation.OwnerAdmin, ownerResourceFromContext)
                makeSecurityKey "POST" "/owner/setType", Authorized(Operation.OwnerAdmin, ownerResourceFromContext)
                makeSecurityKey "POST" "/owner/undelete", Authorized(Operation.OwnerAdmin, ownerResourceFromContext)

                makeSecurityKey "POST" "/organization/create", Authorized(Operation.OwnerAdmin, ownerResourceFromContext)
                makeSecurityKey "POST" "/organization/get", Authorized(Operation.OrgRead, organizationResourceFromContext)
                makeSecurityKey "POST" "/organization/listRepositories", Authorized(Operation.OrgRead, organizationResourceFromContext)
                makeSecurityKey "POST" "/organization/delete", Authorized(Operation.OrgAdmin, organizationResourceFromContext)
                makeSecurityKey "POST" "/organization/setDescription", Authorized(Operation.OrgAdmin, organizationResourceFromContext)
                makeSecurityKey "POST" "/organization/setName", Authorized(Operation.OrgAdmin, organizationResourceFromContext)
                makeSecurityKey "POST" "/organization/setSearchVisibility", Authorized(Operation.OrgAdmin, organizationResourceFromContext)
                makeSecurityKey "POST" "/organization/setType", Authorized(Operation.OrgAdmin, organizationResourceFromContext)
                makeSecurityKey "POST" "/organization/undelete", Authorized(Operation.OrgAdmin, organizationResourceFromContext)

                makeSecurityKey "POST" "/repository/create", Authorized(Operation.OrgAdmin, organizationResourceFromContext)
                makeSecurityKey "POST" "/repository/exists", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/get", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/getBranches", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/getBranchesByBranchId", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/getReferencesByReferenceId", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/isEmpty", Authorized(Operation.RepoRead, repositoryResourceFromContext)

                makeSecurityKey "POST" "/repository/delete", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/undelete", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/setAllowsLargeFiles", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/setAnonymousAccess", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/setCheckpointDays", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/setDiffCacheDays", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/setDirectoryVersionCacheDays", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/setDefaultServerApiVersion", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/setConflictResolutionPolicy", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/setDescription", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/setLogicalDeleteDays", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/setName", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/setRecordSaves", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/setSaveDays", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/setStatus", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/repository/setVisibility", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)

                makeSecurityKey "POST" "/storage/getUploadMetadataForFiles", AuthorizedMany(Operation.PathWrite, uploadPathResourcesFromContext)
                makeSecurityKey "POST" "/storage/getUploadUri", AuthorizedMany(Operation.PathWrite, uploadPathResourcesFromContext)
                makeSecurityKey "POST" "/storage/getDownloadUri", Authorized(Operation.PathRead, downloadPathResourceFromContext)

                makeSecurityKey "POST" "/promotionGroup/get", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/promotionGroup/getEvents", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/promotionGroup/create", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/promotionGroup/addPromotion", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/promotionGroup/removePromotion", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/promotionGroup/reorderPromotions", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/promotionGroup/schedule", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/promotionGroup/markReady", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/promotionGroup/start", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/promotionGroup/complete", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/promotionGroup/block", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/promotionGroup/delete", Authorized(Operation.RepoWrite, repositoryResourceFromContext)

                makeSecurityKey "POST" "/work/get", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/work/create", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/work/update", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/work/link/reference", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/work/link/promotion-group", Authorized(Operation.RepoWrite, repositoryResourceFromContext)

                makeSecurityKey "POST" "/policy/current", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/policy/acknowledge", Authorized(Operation.RepoWrite, repositoryResourceFromContext)

                makeSecurityKey "POST" "/review/packet", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/review/checkpoint", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/review/resolve", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/review/deepen", Authorized(Operation.RepoWrite, repositoryResourceFromContext)

                makeSecurityKey "POST" "/queue/status", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/queue/enqueue", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/queue/pause", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/queue/resume", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/queue/dequeue", Authorized(Operation.RepoWrite, repositoryResourceFromContext)

                makeSecurityKey "POST" "/candidate/get", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/candidate/required-actions", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/candidate/attestations", Authorized(Operation.RepoRead, repositoryResourceFromContext)
                makeSecurityKey "POST" "/candidate/cancel", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/candidate/retry", Authorized(Operation.RepoWrite, repositoryResourceFromContext)
                makeSecurityKey "POST" "/candidate/gate/rerun", Authorized(Operation.RepoWrite, repositoryResourceFromContext)

                makeSecurityKey "POST" "/reminder/list", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/reminder/get", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/reminder/create", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/reminder/delete", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/reminder/updateTime", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)
                makeSecurityKey "POST" "/reminder/reschedule", Authorized(Operation.RepoAdmin, repositoryResourceFromContext)

                makeSecurityKey "POST" "/admin/deleteAllFromCosmosDB", Authorized(Operation.SystemAdmin, systemResourceFromContext)
                makeSecurityKey "POST" "/admin/deleteAllRemindersFromCosmosDB", Authorized(Operation.SystemAdmin, systemResourceFromContext)
            ]

        let securityMap =
            let map = Dictionary<string, SecurityClassification>(StringComparer.OrdinalIgnoreCase)

            for (key, classification) in securityEntries do
                if map.ContainsKey key then
                    invalidOp $"Duplicate security mapping for {key}."
                else
                    map.Add(key, classification)

            map

        let composeHandlers (first: HttpHandler) (second: HttpHandler) : HttpHandler =
            fun next context -> first (second next) context

        let applySecurity (security: SecurityClassification) (handler: HttpHandler) =
            match security with
            | AllowAnonymous -> handler
            | Authenticated -> composeHandlers mustBeLoggedIn handler
            | Authorized (operation, resourceFromContext) ->
                composeHandlers (AuthorizationMiddleware.requiresPermission operation resourceFromContext) handler
            | AuthorizedMany (operation, resourcesFromContext) ->
                composeHandlers (AuthorizationMiddleware.requiresPermissions operation resourcesFromContext) handler
            | AuthorizedResolved resolver ->
                composeHandlers (AuthorizationMiddleware.requiresPermissionResolved resolver) handler
            | AuthorizedResolvedOptional resolver ->
                composeHandlers (AuthorizationMiddleware.requiresPermissionResolvedOptional resolver) handler

        let resolveSecurity (methodName: string) (path: string) =
            let key = makeSecurityKey methodName path

            match securityMap.TryGetValue key with
            | true, classification -> classification
            | false, _ -> invalidOp $"Missing security classification for {key}."

        let secureHandler (methodName: string) (path: string) (handler: HttpHandler) =
            handler |> applySecurity (resolveSecurity methodName path)

        let endpoints =
            [
                GET [ route
                          "/"
                          (secureHandler
                              "GET"
                              "/"
                              (warbler (fun _ ->
                                  htmlString
                                      $"<h1>Hello From Grace Server {graceServerVersion}!</h1><br/><p>The current server time is: {getCurrentInstantExtended ()}.</p>")))
                      route
                          "/healthz"
                          (secureHandler
                              "GET"
                              "/healthz"
                              (warbler (fun _ ->
                                  htmlString $"<h1>Grace server seems healthy!</h1><br/><p>The current server time is: {getCurrentInstantExtended ()}.</p>"))) ]
                PUT []
                subRoute
                    "/branch"
                    [
                        POST [ route "/assign" (secureHandler "POST" "/branch/assign" Branch.Assign)
                               |> addMetadata typeof<Branch.AssignParameters>

                               route "/checkpoint" (secureHandler "POST" "/branch/checkpoint" Branch.Checkpoint)
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/commit" (secureHandler "POST" "/branch/commit" Branch.Commit)
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/create" (secureHandler "POST" "/branch/create" Branch.Create)
                               |> addMetadata typeof<Branch.CreateBranchParameters>

                               route "/createExternal" (secureHandler "POST" "/branch/createExternal" Branch.CreateExternal)
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/delete" (secureHandler "POST" "/branch/delete" Branch.Delete)
                               |> addMetadata typeof<Branch.DeleteBranchParameters>

                               route "/enableAssign" (secureHandler "POST" "/branch/enableAssign" Branch.EnableAssign)
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/enableAutoRebase" (secureHandler "POST" "/branch/enableAutoRebase" Branch.EnableAutoRebase)
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/enableCheckpoint" (secureHandler "POST" "/branch/enableCheckpoint" Branch.EnableCheckpoint)
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/enableCommit" (secureHandler "POST" "/branch/enableCommit" Branch.EnableCommit)
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/enableExternal" (secureHandler "POST" "/branch/enableExternal" Branch.EnableExternal)
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/enablePromotion" (secureHandler "POST" "/branch/enablePromotion" Branch.EnablePromotion)
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/enableSave" (secureHandler "POST" "/branch/enableSave" Branch.EnableSave)
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/enableTag" (secureHandler "POST" "/branch/enableTag" Branch.EnableTag)
                               |> addMetadata typeof<Branch.EnableFeatureParameters>

                               route "/setPromotionMode" (secureHandler "POST" "/branch/setPromotionMode" Branch.SetPromotionMode)
                               |> addMetadata typeof<Branch.SetPromotionModeParameters>

                               route "/get" (secureHandler "POST" "/branch/get" Branch.Get)
                               |> addMetadata typeof<Branch.GetBranchParameters>

                               route "/getEvents" (secureHandler "POST" "/branch/getEvents" Branch.GetEvents)
                               |> addMetadata typeof<Branch.GetBranchParameters>

                               route "/getExternals" (secureHandler "POST" "/branch/getExternals" Branch.GetExternals)
                               |> addMetadata typeof<Branch.GetReferenceParameters>

                               route "/getCheckpoints" (secureHandler "POST" "/branch/getCheckpoints" Branch.GetCheckpoints)
                               |> addMetadata typeof<Branch.GetBranchParameters>

                               route "/getCommits" (secureHandler "POST" "/branch/getCommits" Branch.GetCommits)
                               |> addMetadata typeof<Branch.GetBranchParameters>

                               route "/getDiffsForReferenceType"
                                   (secureHandler "POST" "/branch/getDiffsForReferenceType" Branch.GetDiffsForReferenceType)
                               |> addMetadata typeof<Branch.GetDiffsForReferenceTypeParameters>

                               route "/getParentBranch" (secureHandler "POST" "/branch/getParentBranch" Branch.GetParentBranch)
                               |> addMetadata typeof<Branch.GetBranchParameters>

                               route "/getPromotions" (secureHandler "POST" "/branch/getPromotions" Branch.GetPromotions)
                               |> addMetadata typeof<Branch.GetReferenceParameters>

                               route "/getRecursiveSize" (secureHandler "POST" "/branch/getRecursiveSize" Branch.GetRecursiveSize)
                               |> addMetadata typeof<Branch.ListContentsParameters>

                               route "/getReference" (secureHandler "POST" "/branch/getReference" Branch.GetReference)
                               |> addMetadata typeof<Branch.GetReferenceParameters>

                               route "/getReferences" (secureHandler "POST" "/branch/getReferences" Branch.GetReferences)
                               |> addMetadata typeof<Branch.GetReferencesParameters>

                               route "/getSaves" (secureHandler "POST" "/branch/getSaves" Branch.GetSaves)
                               |> addMetadata typeof<Branch.GetReferenceParameters>

                               route "/getTags" (secureHandler "POST" "/branch/getTags" Branch.GetTags)
                               |> addMetadata typeof<Branch.GetReferenceParameters>

                               route "/getVersion" (secureHandler "POST" "/branch/getVersion" Branch.GetVersion)
                               |> addMetadata typeof<Branch.GetBranchVersionParameters>

                               route "/listContents" (secureHandler "POST" "/branch/listContents" Branch.ListContents)
                               |> addMetadata typeof<Branch.ListContentsParameters>

                               route "/promote" (secureHandler "POST" "/branch/promote" Branch.Promote)
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/rebase" (secureHandler "POST" "/branch/rebase" Branch.Rebase)
                               |> addMetadata typeof<Branch.RebaseParameters>

                               route "/save" (secureHandler "POST" "/branch/save" Branch.Save)
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/tag" (secureHandler "POST" "/branch/tag" Branch.Tag)
                               |> addMetadata typeof<Branch.CreateReferenceParameters>

                               route "/updateParentBranch" (secureHandler "POST" "/branch/updateParentBranch" Branch.UpdateParentBranch)
                               |> addMetadata typeof<Branch.UpdateParentBranchParameters> ]
                    ]
                subRoute
                    "/diff"
                    [
                        POST [ route "/getDiff" (secureHandler "POST" "/diff/getDiff" Diff.GetDiff)
                               |> addMetadata typeof<Diff.GetDiffParameters>

                               route "/getDiffBySha256Hash" (secureHandler "POST" "/diff/getDiffBySha256Hash" Diff.GetDiffBySha256Hash)
                               |> addMetadata typeof<Diff.GetDiffBySha256HashParameters>

                               route "/populate" (secureHandler "POST" "/diff/populate" Diff.Populate)
                               |> addMetadata typeof<Diff.PopulateParameters> ]
                    ]
                subRoute
                    "/directory"
                    [
                        POST [ route "/create" (secureHandler "POST" "/directory/create" DirectoryVersion.Create)
                               |> addMetadata typeof<DirectoryVersion.CreateParameters>

                               route "/get" (secureHandler "POST" "/directory/get" DirectoryVersion.Get)
                               |> addMetadata typeof<DirectoryVersion.GetParameters>

                               route "/getByDirectoryIds" (secureHandler "POST" "/directory/getByDirectoryIds" DirectoryVersion.GetByDirectoryIds)
                               |> addMetadata typeof<DirectoryVersion.GetByDirectoryIdsParameters>

                               route "/getBySha256Hash" (secureHandler "POST" "/directory/getBySha256Hash" DirectoryVersion.GetBySha256Hash)
                               |> addMetadata typeof<DirectoryVersion.GetBySha256HashParameters>

                               route "/getDirectoryVersionsRecursive"
                                   (secureHandler "POST" "/directory/getDirectoryVersionsRecursive" DirectoryVersion.GetDirectoryVersionsRecursive)
                               |> addMetadata typeof<DirectoryVersion.GetParameters>

                               route "/getZipFile" (secureHandler "POST" "/directory/getZipFile" DirectoryVersion.GetZipFile)
                               |> addMetadata typeof<DirectoryVersion.GetZipFileParameters>

                               route "/saveDirectoryVersions" (secureHandler "POST" "/directory/saveDirectoryVersions" DirectoryVersion.SaveDirectoryVersions)
                               |> addMetadata typeof<DirectoryVersion.SaveDirectoryVersionsParameters> ]
                    ]
                subRoute "/notifications" [ GET [] ]
                subRoute
                    "/organization"
                    [
                        POST [ route "/create" (secureHandler "POST" "/organization/create" Organization.Create)
                               |> addMetadata typeof<Organization.CreateOrganizationParameters>

                               route "/delete" (secureHandler "POST" "/organization/delete" Organization.Delete)
                               |> addMetadata typeof<Organization.DeleteOrganizationParameters>

                               route "/get" (secureHandler "POST" "/organization/get" Organization.Get)
                               |> addMetadata typeof<Organization.GetOrganizationParameters>

                               route "/listRepositories" (secureHandler "POST" "/organization/listRepositories" Organization.ListRepositories)
                               |> addMetadata typeof<Organization.GetOrganizationParameters>

                               route "/setDescription" (secureHandler "POST" "/organization/setDescription" Organization.SetDescription)
                               |> addMetadata typeof<Organization.SetOrganizationDescriptionParameters>

                               route "/setName" (secureHandler "POST" "/organization/setName" Organization.SetName)
                               |> addMetadata typeof<Organization.SetOrganizationNameParameters>

                               route "/setSearchVisibility"
                                   (secureHandler "POST" "/organization/setSearchVisibility" Organization.SetSearchVisibility)
                               |> addMetadata typeof<Organization.SetOrganizationSearchVisibilityParameters>

                               route "/setType" (secureHandler "POST" "/organization/setType" Organization.SetType)
                               |> addMetadata typeof<Organization.SetOrganizationTypeParameters>

                               route "/undelete" (secureHandler "POST" "/organization/undelete" Organization.Undelete)
                               |> addMetadata typeof<Organization.UndeleteOrganizationParameters> ]
                    ]
                subRoute
                    "/owner"
                    [
                        POST [ route "/create" (secureHandler "POST" "/owner/create" Owner.Create)
                               |> addMetadata typeof<Owner.CreateOwnerParameters>

                               route "/delete" (secureHandler "POST" "/owner/delete" Owner.Delete)
                               |> addMetadata typeof<Owner.DeleteOwnerParameters>

                               route "/get" (secureHandler "POST" "/owner/get" Owner.Get)
                               |> addMetadata typeof<Owner.GetOwnerParameters>

                               route "/listOrganizations" (secureHandler "POST" "/owner/listOrganizations" Owner.ListOrganizations)
                               |> addMetadata typeof<Owner.GetOwnerParameters>

                               route "/setDescription" (secureHandler "POST" "/owner/setDescription" Owner.SetDescription)
                               |> addMetadata typeof<Owner.SetOwnerDescriptionParameters>

                               route "/setName" (secureHandler "POST" "/owner/setName" Owner.SetName)
                               |> addMetadata typeof<Owner.SetOwnerNameParameters>

                               route "/setSearchVisibility" (secureHandler "POST" "/owner/setSearchVisibility" Owner.SetSearchVisibility)
                               |> addMetadata typeof<Owner.SetOwnerSearchVisibilityParameters>

                               route "/setType" (secureHandler "POST" "/owner/setType" Owner.SetType)
                               |> addMetadata typeof<Owner.SetOwnerTypeParameters>

                               route "/undelete" (secureHandler "POST" "/owner/undelete" Owner.Undelete)
                               |> addMetadata typeof<Owner.UndeleteOwnerParameters> ]
                    ]
                subRoute
                    "/promotionGroup"
                    [
                        POST [ route "/create" (secureHandler "POST" "/promotionGroup/create" PromotionGroup.Create)
                               |> addMetadata typeof<PromotionGroup.CreatePromotionGroupParameters>

                               route "/get" (secureHandler "POST" "/promotionGroup/get" PromotionGroup.Get)
                               |> addMetadata typeof<PromotionGroup.GetPromotionGroupParameters>

                               route "/getEvents" (secureHandler "POST" "/promotionGroup/getEvents" PromotionGroup.GetEvents)
                               |> addMetadata typeof<PromotionGroup.GetPromotionGroupParameters>

                               route "/addPromotion" (secureHandler "POST" "/promotionGroup/addPromotion" PromotionGroup.AddPromotion)
                               |> addMetadata typeof<PromotionGroup.AddPromotionParameters>

                               route "/removePromotion" (secureHandler "POST" "/promotionGroup/removePromotion" PromotionGroup.RemovePromotion)
                               |> addMetadata typeof<PromotionGroup.RemovePromotionParameters>

                               route "/reorderPromotions"
                                   (secureHandler "POST" "/promotionGroup/reorderPromotions" PromotionGroup.ReorderPromotions)
                               |> addMetadata typeof<PromotionGroup.ReorderPromotionsParameters>

                               route "/schedule" (secureHandler "POST" "/promotionGroup/schedule" PromotionGroup.Schedule)
                               |> addMetadata typeof<PromotionGroup.ScheduleParameters>

                               route "/markReady" (secureHandler "POST" "/promotionGroup/markReady" PromotionGroup.MarkReady)
                               |> addMetadata typeof<PromotionGroup.MarkReadyParameters>

                               route "/start" (secureHandler "POST" "/promotionGroup/start" PromotionGroup.Start)
                               |> addMetadata typeof<PromotionGroup.StartParameters>

                               route "/complete" (secureHandler "POST" "/promotionGroup/complete" PromotionGroup.Complete)
                               |> addMetadata typeof<PromotionGroup.CompleteParameters>

                               route "/block" (secureHandler "POST" "/promotionGroup/block" PromotionGroup.Block)
                               |> addMetadata typeof<PromotionGroup.BlockParameters>

                               route "/delete" (secureHandler "POST" "/promotionGroup/delete" PromotionGroup.Delete)
                               |> addMetadata typeof<PromotionGroup.DeletePromotionGroupParameters> ]
                    ]
                subRoute
                    "/work"
                    [
                        POST [ route "/create" (secureHandler "POST" "/work/create" WorkItem.Create)
                               |> addMetadata typeof<WorkItem.CreateWorkItemParameters>

                               route "/get" (secureHandler "POST" "/work/get" WorkItem.Get)
                               |> addMetadata typeof<WorkItem.GetWorkItemParameters>

                               route "/update" (secureHandler "POST" "/work/update" WorkItem.Update)
                               |> addMetadata typeof<WorkItem.UpdateWorkItemParameters>

                               route "/link/reference" (secureHandler "POST" "/work/link/reference" WorkItem.LinkReference)
                               |> addMetadata typeof<WorkItem.LinkReferenceParameters>

                               route "/link/promotion-group"
                                   (secureHandler "POST" "/work/link/promotion-group" WorkItem.LinkPromotionGroup)
                               |> addMetadata typeof<WorkItem.LinkPromotionGroupParameters> ]
                    ]
                subRoute
                    "/policy"
                    [
                        POST [ route "/current" (secureHandler "POST" "/policy/current" Policy.GetCurrent)
                               |> addMetadata typeof<Policy.GetPolicyParameters>

                               route "/acknowledge" (secureHandler "POST" "/policy/acknowledge" Policy.Acknowledge)
                               |> addMetadata typeof<Policy.AcknowledgePolicyParameters> ]
                    ]
                subRoute
                    "/review"
                    [
                        POST [ route "/packet" (secureHandler "POST" "/review/packet" Review.GetPacket)
                               |> addMetadata typeof<Review.GetReviewPacketParameters>

                               route "/checkpoint" (secureHandler "POST" "/review/checkpoint" Review.Checkpoint)
                               |> addMetadata typeof<Review.ReviewCheckpointParameters>

                               route "/resolve" (secureHandler "POST" "/review/resolve" Review.ResolveFinding)
                               |> addMetadata typeof<Review.ResolveFindingParameters>

                               route "/deepen" (secureHandler "POST" "/review/deepen" Review.Deepen)
                               |> addMetadata typeof<Review.DeepenReviewParameters> ]
                    ]
                subRoute
                    "/queue"
                    [
                        POST [ route "/status" (secureHandler "POST" "/queue/status" Queue.Status)
                               |> addMetadata typeof<Queue.QueueStatusParameters>

                               route "/enqueue" (secureHandler "POST" "/queue/enqueue" Queue.Enqueue)
                               |> addMetadata typeof<Queue.EnqueueParameters>

                               route "/pause" (secureHandler "POST" "/queue/pause" Queue.Pause)
                               |> addMetadata typeof<Queue.QueueActionParameters>

                               route "/resume" (secureHandler "POST" "/queue/resume" Queue.Resume)
                               |> addMetadata typeof<Queue.QueueActionParameters>

                               route "/dequeue" (secureHandler "POST" "/queue/dequeue" Queue.Dequeue)
                               |> addMetadata typeof<Queue.CandidateActionParameters> ]
                    ]
                subRoute
                    "/candidate"
                    [
                        POST [ route "/get" (secureHandler "POST" "/candidate/get" Queue.GetCandidate)
                               |> addMetadata typeof<Queue.CandidateParameters>

                               route "/cancel" (secureHandler "POST" "/candidate/cancel" Queue.CancelCandidate)
                               |> addMetadata typeof<Queue.CandidateActionParameters>

                               route "/retry" (secureHandler "POST" "/candidate/retry" Queue.RetryCandidate)
                               |> addMetadata typeof<Queue.CandidateActionParameters>

                               route "/required-actions" (secureHandler "POST" "/candidate/required-actions" Queue.RequiredActions)
                               |> addMetadata typeof<Queue.CandidateParameters>

                               route "/attestations" (secureHandler "POST" "/candidate/attestations" Queue.Attestations)
                               |> addMetadata typeof<Queue.CandidateAttestationsParameters>

                               route "/gate/rerun" (secureHandler "POST" "/candidate/gate/rerun" Queue.RerunGate)
                               |> addMetadata typeof<Queue.CandidateGateRerunParameters> ]
                    ]
                subRoute
                    "/repository"
                    [
                        POST [ route "/create" (secureHandler "POST" "/repository/create" Repository.Create)
                               |> addMetadata typeof<Repository.CreateRepositoryParameters>

                               route "/delete" (secureHandler "POST" "/repository/delete" Repository.Delete)
                               |> addMetadata typeof<Repository.DeleteRepositoryParameters>

                               route "/exists" (secureHandler "POST" "/repository/exists" Repository.Exists)
                               |> addMetadata typeof<Repository.RepositoryParameters>

                               route "/get" (secureHandler "POST" "/repository/get" Repository.Get)
                               |> addMetadata typeof<Repository.RepositoryParameters>

                               route "/getBranches" (secureHandler "POST" "/repository/getBranches" Repository.GetBranches)
                               |> addMetadata typeof<Repository.GetBranchesParameters>

                               route "/getBranchesByBranchId"
                                   (secureHandler "POST" "/repository/getBranchesByBranchId" Repository.GetBranchesByBranchId)
                               |> addMetadata typeof<Repository.GetBranchesByBranchIdParameters>

                               route "/getReferencesByReferenceId"
                                   (secureHandler "POST" "/repository/getReferencesByReferenceId" Repository.GetReferencesByReferenceId)
                               |> addMetadata typeof<Repository.GetReferencesByReferenceIdParameters>

                               route "/isEmpty" (secureHandler "POST" "/repository/isEmpty" Repository.IsEmpty)
                               |> addMetadata typeof<Repository.IsEmptyParameters>

                               route "/setAllowsLargeFiles"
                                   (secureHandler "POST" "/repository/setAllowsLargeFiles" Repository.SetAllowsLargeFiles)
                               |> addMetadata typeof<Repository.SetAllowsLargeFilesParameters>

                               route "/setAnonymousAccess"
                                   (secureHandler "POST" "/repository/setAnonymousAccess" Repository.SetAnonymousAccess)
                               |> addMetadata typeof<Repository.SetAnonymousAccessParameters>

                               route "/setCheckpointDays"
                                   (secureHandler "POST" "/repository/setCheckpointDays" Repository.SetCheckpointDays)
                               |> addMetadata typeof<Repository.SetCheckpointDaysParameters>

                               route "/setDiffCacheDays" (secureHandler "POST" "/repository/setDiffCacheDays" Repository.SetDiffCacheDays)
                               |> addMetadata typeof<Repository.SetDiffCacheDaysParameters>

                               route "/setDirectoryVersionCacheDays"
                                   (secureHandler "POST" "/repository/setDirectoryVersionCacheDays" Repository.SetDirectoryVersionCacheDays)
                               |> addMetadata typeof<Repository.SetDirectoryVersionCacheDaysParameters>

                               route "/setDefaultServerApiVersion"
                                   (secureHandler "POST" "/repository/setDefaultServerApiVersion" Repository.SetDefaultServerApiVersion)
                               |> addMetadata typeof<Repository.SetDefaultServerApiVersionParameters>

                               route "/setConflictResolutionPolicy"
                                   (secureHandler "POST" "/repository/setConflictResolutionPolicy" Repository.SetConflictResolutionPolicy)
                               |> addMetadata typeof<Repository.SetConflictResolutionPolicyParameters>

                               route "/setDescription" (secureHandler "POST" "/repository/setDescription" Repository.SetDescription)
                               |> addMetadata typeof<Repository.SetRepositoryDescriptionParameters>

                               route "/setLogicalDeleteDays"
                                   (secureHandler "POST" "/repository/setLogicalDeleteDays" Repository.SetLogicalDeleteDays)
                               |> addMetadata typeof<Repository.SetLogicalDeleteDaysParameters>

                               route "/setName" (secureHandler "POST" "/repository/setName" Repository.SetName)
                               |> addMetadata typeof<Repository.SetRepositoryNameParameters>

                               route "/setRecordSaves" (secureHandler "POST" "/repository/setRecordSaves" Repository.SetRecordSaves)
                               |> addMetadata typeof<Repository.RecordSavesParameters>

                               route "/setSaveDays" (secureHandler "POST" "/repository/setSaveDays" Repository.SetSaveDays)
                               |> addMetadata typeof<Repository.SetSaveDaysParameters>

                               route "/setStatus" (secureHandler "POST" "/repository/setStatus" Repository.SetStatus)
                               |> addMetadata typeof<Repository.SetRepositoryStatusParameters>

                               route "/setVisibility" (secureHandler "POST" "/repository/setVisibility" Repository.SetVisibility)
                               |> addMetadata typeof<Repository.SetRepositoryVisibilityParameters>

                               route "/undelete" (secureHandler "POST" "/repository/undelete" Repository.Undelete)
                               |> addMetadata typeof<Repository.UndeleteRepositoryParameters> ]
                    ]
                subRoute
                    "/storage"
                    [
                        POST [ route "/getUploadMetadataForFiles"
                                   (secureHandler "POST" "/storage/getUploadMetadataForFiles" Storage.GetUploadMetadataForFiles)
                               |> addMetadata typeof<Storage.GetUploadMetadataForFilesParameters>

                               route "/getDownloadUri" (secureHandler "POST" "/storage/getDownloadUri" Storage.GetDownloadUri)
                               |> addMetadata typeof<Storage.GetDownloadUriParameters>

                               route "/getUploadUri" (secureHandler "POST" "/storage/getUploadUri" Storage.GetUploadUris)
                               |> addMetadata typeof<Storage.GetUploadUriParameters> ]
                    ]
                subRoute
                    "/access"
                    [
                        POST [ route "/grantRole" (secureHandler "POST" "/access/grantRole" Access.GrantRole)
                               |> addMetadata typeof<Access.GrantRoleParameters>

                               route "/revokeRole" (secureHandler "POST" "/access/revokeRole" Access.RevokeRole)
                               |> addMetadata typeof<Access.RevokeRoleParameters>

                               route "/listRoleAssignments"
                                   (secureHandler "POST" "/access/listRoleAssignments" Access.ListRoleAssignments)
                               |> addMetadata typeof<Access.ListRoleAssignmentsParameters>

                               route "/upsertPathPermission"
                                   (secureHandler "POST" "/access/upsertPathPermission" Access.UpsertPathPermission)
                               |> addMetadata typeof<Access.UpsertPathPermissionParameters>

                               route "/removePathPermission"
                                   (secureHandler "POST" "/access/removePathPermission" Access.RemovePathPermission)
                               |> addMetadata typeof<Access.RemovePathPermissionParameters>

                               route "/listPathPermissions"
                                   (secureHandler "POST" "/access/listPathPermissions" Access.ListPathPermissions)
                               |> addMetadata typeof<Access.ListPathPermissionsParameters>

                               route "/checkPermission" (secureHandler "POST" "/access/checkPermission" Access.CheckPermission)
                               |> addMetadata typeof<Access.CheckPermissionParameters> ]
                        GET [ route "/listRoles" (secureHandler "GET" "/access/listRoles" Access.ListRoles) ]
                    ]
                subRoute
                    "/auth"
                    [
                        GET [ route "/me" (secureHandler "GET" "/auth/me" Auth.Me)
                              route "/oidc/config" (secureHandler "GET" "/auth/oidc/config" (Auth.OidcConfig configuration))
                              route "/login" (secureHandler "GET" "/auth/login" Auth.Login)
                              routef "/login/%s" (fun providerId ->
                                  Auth.LoginProvider providerId |> secureHandler "GET" "/auth/login/%s")
                              route "/logout" (secureHandler "GET" "/auth/logout" Auth.Logout) ]
                        POST [ route "/token/create" (secureHandler "POST" "/auth/token/create" (Auth.TokenCreate configuration))
                               |> addMetadata typeof<Auth.CreatePersonalAccessTokenParameters>
                               route "/token/list" (secureHandler "POST" "/auth/token/list" (Auth.TokenList configuration))
                               |> addMetadata typeof<Auth.ListPersonalAccessTokensParameters>
                               route "/token/revoke" (secureHandler "POST" "/auth/token/revoke" (Auth.TokenRevoke configuration))
                               |> addMetadata typeof<Auth.RevokePersonalAccessTokenParameters> ]
                    ]
                subRoute
                    "/reminder"
                    [
                        POST [ route "/list" (secureHandler "POST" "/reminder/list" Reminder.List)
                               |> addMetadata typeof<Reminder.ListRemindersParameters>

                               route "/get" (secureHandler "POST" "/reminder/get" Reminder.Get)
                               |> addMetadata typeof<Reminder.GetReminderParameters>

                               route "/delete" (secureHandler "POST" "/reminder/delete" Reminder.Delete)
                               |> addMetadata typeof<Reminder.DeleteReminderParameters>

                               route "/updateTime" (secureHandler "POST" "/reminder/updateTime" Reminder.UpdateTime)
                               |> addMetadata typeof<Reminder.UpdateReminderTimeParameters>

                               route "/reschedule" (secureHandler "POST" "/reminder/reschedule" Reminder.Reschedule)
                               |> addMetadata typeof<Reminder.RescheduleReminderParameters>

                               route "/create" (secureHandler "POST" "/reminder/create" Reminder.Create)
                               |> addMetadata typeof<Reminder.CreateReminderParameters> ]
                    ]
                subRoute
                    "/admin"
                    [
                        POST [
#if DEBUG
                               route "/deleteAllFromCosmosDB"
                                   (secureHandler "POST" "/admin/deleteAllFromCosmosDB" Storage.DeleteAllFromCosmosDB)
                               route "/deleteAllRemindersFromCosmosDB"
                                   (secureHandler "POST" "/admin/deleteAllRemindersFromCosmosDB" Storage.DeleteAllRemindersFromCosmosDB)
#endif
                                ]
                    ]
            ]

        let notFoundHandler = "Not Found" |> text |> RequestErrors.notFound

        let mutable currentWorkingSet = String.Empty
        let mutable maxWorkingSet = String.Empty
        let mutable lastMetricsUpdateTime = Instant.MinValue
        let mutable threadCount = String.Empty

        let enrichTelemetry (activity: Activity) (request: HttpRequest) = //(eventName: string) (obj: Object) =
            let currentProcess = Process.GetCurrentProcess()
            let context = request.HttpContext

            if (lastMetricsUpdateTime + Duration.FromSeconds 10.0) < getCurrentInstant () then
                currentWorkingSet <- currentProcess.WorkingSet64.ToString("N0")
                maxWorkingSet <- currentProcess.PeakWorkingSet64.ToString("N0")
                lastMetricsUpdateTime <- getCurrentInstant ()
                threadCount <- currentProcess.Threads.Count.ToString("N0")

            let user = context.User

            if user.Identity.IsAuthenticated then
                let claimsList = stringBuilderPool.Get()

                try
                    if not <| isNull user.Claims then
                        for claim in user.Claims do
                            claimsList.Append($"{claim.Type}:{claim.Value};")
                            |> ignore

                    if claimsList.Length > 1 then
                        claimsList.Remove(claimsList.Length - 1, 1)
                        |> ignore

                    activity
                        .AddTag("enduser.id", user.Identity.Name)
                        .AddTag("enduser.claims", claimsList.ToString())
                    |> ignore
                finally
                    stringBuilderPool.Return(claimsList)

            activity
                .AddTag("working_set", currentWorkingSet)
                .AddTag("max_working_set", maxWorkingSet)
                .AddTag("thread_count", threadCount)
                .AddTag("http.client_ip", context.Connection.RemoteIpAddress)
                .AddTag("enduser.is_authenticated", user.Identity.IsAuthenticated)
            |> ignore

        member _.ConfigureServices(services: IServiceCollection) =
            let mutable configurationObj: obj = null

            if
                not
                <| memoryCache.TryGetValue(Constants.MemoryCache.GraceConfiguration, &configurationObj)
            then
                invalidOp "Grace configuration not found in memory cache."

            let configuration = configurationObj :?> IConfigurationRoot

            let azureMonitorConnectionString =
                let connectionString = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.ApplicationInsightsConnectionString

                if String.IsNullOrWhiteSpace connectionString then
                    configuration["Grace:ApplicationInsightsConnectionString"]
                else
                    connectionString

            ApplicationContext.setActorStateStorageProvider ActorStateStorageProvider.AzureCosmosDb

            // OpenTelemetry trace attribute specifications: https://github.com/open-telemetry/opentelemetry-specification/tree/main/specification/trace/semantic_conventions
            let globalOpenTelemetryAttributes = Dictionary<string, obj>()
            globalOpenTelemetryAttributes.Add("host.name", Environment.MachineName)
            globalOpenTelemetryAttributes.Add("process.pid", Environment.ProcessId)

            globalOpenTelemetryAttributes.Add(
                "process.starttime",
                Process
                    .GetCurrentProcess()
                    .StartTime.ToUniversalTime()
                    .ToString("u")
            )

            globalOpenTelemetryAttributes.Add("process.executable.name", Process.GetCurrentProcess().ProcessName)
            globalOpenTelemetryAttributes.Add("process.runtime.version", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)

            let openApiInfo = new OpenApiInfo()
            openApiInfo.Description <- "Grace is a version control system. Code and documentation can be found at https://gracevcs.com."
            openApiInfo.Title <- "Grace Server API"
            openApiInfo.Version <- "v0.2"
            openApiInfo.Contact <- new OpenApiContact()
            openApiInfo.Contact.Name <- "Scott Arbeit"
            openApiInfo.Contact.Email <- "scott.arbeit@outlook.com"
            openApiInfo.Contact.Url <- Uri("https://gracevcs.com")

            // Telemetry configuration
            let graceServerAppId = "grace-server-integration-test"

            let tracingOtlpEndpoint = Environment.GetEnvironmentVariable("OTLP_ENDPOINT_URL")
            let otel = services.AddOpenTelemetry()

            otel
                .ConfigureResource(fun resourceBuilder ->
                    resourceBuilder
                        .AddService(graceServerAppId)
                        .AddTelemetrySdk()
                        .AddAttributes(globalOpenTelemetryAttributes)
                    |> ignore)

                .WithMetrics(fun metricsBuilder ->
                    metricsBuilder
                        .AddAspNetCoreInstrumentation()
                        .AddMeter("Microsoft.AspNetCore.Hosting")
                        .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
                        .AddPrometheusExporter(fun prometheusOptions -> prometheusOptions.ScrapeEndpointPath <- "/metrics")
                    |> ignore

                    if
                        not
                        <| String.IsNullOrWhiteSpace(azureMonitorConnectionString)
                    then
                        logToConsole "OpenTelemetry: Configuring Azure Monitor metrics exporter"

                        metricsBuilder.AddAzureMonitorMetricExporter(fun options -> options.ConnectionString <- azureMonitorConnectionString)
                        |> ignore)
                .WithTracing(fun traceBuilder ->
                    traceBuilder
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddSource(graceServerAppId)
                    |> ignore

                    if
                        not
                        <| String.IsNullOrWhiteSpace(tracingOtlpEndpoint)
                    then
                        logToConsole $"OpenTelemetry: Configuring OTLP exporter to {tracingOtlpEndpoint}"

                        traceBuilder.AddOtlpExporter(fun options -> options.Endpoint <- Uri(tracingOtlpEndpoint))
                        |> ignore
                    else
                        traceBuilder.AddConsoleExporter() |> ignore

                    if
                        not
                        <| String.IsNullOrWhiteSpace(azureMonitorConnectionString)
                    then
                        logToConsole "OpenTelemetry: Configuring Azure Monitor trace exporter"

                        traceBuilder.AddAzureMonitorTraceExporter(fun options -> options.ConnectionString <- azureMonitorConnectionString)
                        |> ignore)
            |> ignore

            let isTesting =
                match Environment.GetEnvironmentVariable("GRACE_TESTING") with
                | null -> false
                | value ->
                    value.Equals("1", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("yes", StringComparison.OrdinalIgnoreCase)

            if isTesting then
                services
                    .AddAuthentication(fun options ->
                        options.DefaultScheme <- "GraceAuth"
                        options.DefaultChallengeScheme <- "GraceAuth")
                    .AddPolicyScheme(
                        "GraceAuth",
                        "GraceAuth",
                        fun options ->
                            options.ForwardDefaultSelector <-
                                fun context ->
                                    let authorization = context.Request.Headers.Authorization.ToString()

                                    if
                                        not (String.IsNullOrWhiteSpace authorization)
                                        && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                                    then
                                        let token = authorization.Substring("Bearer ".Length).Trim()

                                        if token.StartsWith(TokenPrefix, StringComparison.Ordinal) then
                                            PersonalAccessTokenAuth.SchemeName
                                        else
                                            TestAuth.SchemeName
                                    else
                                        TestAuth.SchemeName
                    )
                    .AddScheme<AuthenticationSchemeOptions, GraceTestAuthHandler>(TestAuth.SchemeName, (fun _ -> ()))
                    .AddScheme<AuthenticationSchemeOptions, PersonalAccessTokenAuth.PersonalAccessTokenAuthHandler>(
                        PersonalAccessTokenAuth.SchemeName,
                        fun _ -> ()
                    )
                |> ignore
            else
                ExternalAuthConfig.warnIfMicrosoftConfigPresent configuration

                let oidcConfig = ExternalAuthConfig.tryGetOidcConfig configuration
                let hasOidc = oidcConfig |> Option.isSome

                let authBuilder =
                    services.AddAuthentication (fun options ->
                        options.DefaultScheme <- "GraceAuth"
                        options.DefaultChallengeScheme <- "GraceAuth")

                authBuilder
                    .AddPolicyScheme(
                        "GraceAuth",
                        "GraceAuth",
                        fun options ->
                            options.ForwardDefaultSelector <-
                                fun context ->
                                    let authorization = context.Request.Headers.Authorization.ToString()

                                    if
                                        not (String.IsNullOrWhiteSpace authorization)
                                        && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                                    then
                                        let token = authorization.Substring("Bearer ".Length).Trim()

                                        if token.StartsWith(TokenPrefix, StringComparison.Ordinal) then
                                            PersonalAccessTokenAuth.SchemeName
                                        else if hasOidc then
                                            JwtBearerDefaults.AuthenticationScheme
                                        else
                                            PersonalAccessTokenAuth.SchemeName
                                    else if hasOidc then
                                        JwtBearerDefaults.AuthenticationScheme
                                    else
                                        PersonalAccessTokenAuth.SchemeName
                    )
                    .AddScheme<AuthenticationSchemeOptions, PersonalAccessTokenAuth.PersonalAccessTokenAuthHandler>(
                        PersonalAccessTokenAuth.SchemeName,
                        fun _ -> ()
                    )
                |> ignore

                match oidcConfig with
                | Some config ->
                    authBuilder.AddJwtBearer(
                        JwtBearerDefaults.AuthenticationScheme,
                        fun options ->
                            options.Authority <- config.Authority
                            options.Audience <- config.Audience

                            options.TokenValidationParameters <-
                                TokenValidationParameters(
                                    ValidateIssuer = true,
                                    ValidateAudience = true,
                                    NameClaimType = "name",
                                    RoleClaimType = "roles",
                                    ValidAudience = config.Audience
                                )
                    )
                    |> ignore
                | None -> ()

            services.AddAuthorization() |> ignore

            services.AddTransient<IClaimsTransformation, GraceClaimsTransformation>()
            |> ignore

            services.AddSingleton<IGracePermissionEvaluator, GracePermissionEvaluator>()
            |> ignore

            services.AddW3CLogging (fun options ->
                options.FileName <- "Grace.Server.log-"

                let tempRoot =
                    let value = Environment.GetEnvironmentVariable("TEMP")
                    if String.IsNullOrWhiteSpace value then
                        Path.GetTempPath()
                    else
                        value

                options.LogDirectory <- Path.Combine(tempRoot, "Grace.Server.Logs"))
            |> ignore

            services
                .AddHostedService<CosmosWarmup>()
                .AddGiraffe()
                // Next line adds the Json serializer that Giraffe uses internally.
                .AddSingleton<Json.ISerializer>(
                    Json.Serializer(Constants.JsonSerializerOptions)
                )
                .AddSingleton<IPartitionKeyProvider, GracePartitionKeyProvider>()
                .AddRouting()
                .AddLogging()
                .AddHostedService<ReminderService>()
                .AddHostedService<Notification.Subscriber.GraceEventSubscriptionService>()
                .AddHttpLogging()
                .AddOrleans(fun siloBuilder ->
                    siloBuilder.Services.AddSerializer (fun serializerBuilder ->
                        serializerBuilder.AddNodaTimeSerializers()
                        |> ignore)
                    |> ignore)
            |> ignore

            services.AddSingleton<CosmosClient> (fun serviceProvider ->
                let cosmosConnectionString = configuration.GetValue<string>(getConfigKey Constants.EnvironmentVariables.AzureCosmosDBConnectionString)

                // Force SNI = "localhost" while we connect to 127.0.0.1.
                let httpHandler =
                    // Create and configure SslClientAuthenticationOptions
                    let sslOptions = SslClientAuthenticationOptions()
                    sslOptions.TargetHost <- "localhost" // SNI host_name must be DNS per RFC 6066
                    sslOptions.RemoteCertificateValidationCallback <- RemoteCertificateValidationCallback(fun _ _ _ _ -> true)
                    new SocketsHttpHandler(SslOptions = sslOptions)

                let options =
                    new CosmosClientOptions(
                        ConnectionMode = ConnectionMode.Gateway,
                        UseSystemTextJsonSerializerWithOptions = Constants.JsonSerializerOptions,
                        HttpClientFactory = (fun () -> new HttpClient(httpHandler, disposeHandler = true)),
                        LimitToEndpoint = true // prevents discovery probes that can trigger TLS issues on emulator
                    )

                if AzureEnvironment.useManagedIdentity then
                    let endpoint =
                        AzureEnvironment.tryGetCosmosEndpointUri ()
                        |> Option.defaultWith (fun () -> invalidOp "Azure Cosmos DB endpoint must be configured when using a managed identity.")

                    new CosmosClient(endpoint.AbsoluteUri, defaultAzureCredential.Value, options)
                else
                    if String.IsNullOrWhiteSpace cosmosConnectionString then
                        invalidOp "Azure Cosmos DB connection string is required when managed identity is disabled."

                    new CosmosClient(cosmosConnectionString, options))
            |> ignore

            let apiVersioningBuilder =
                services.AddApiVersioning (fun options ->
                    options.ReportApiVersions <- true
                    options.DefaultApiVersion <- new ApiVersion(1, 0)
                    options.AssumeDefaultVersionWhenUnspecified <- true
                    // Use whatever reader you want
                    options.ApiVersionReader <-
                        ApiVersionReader.Combine(
                            new UrlSegmentApiVersionReader(),
                            new HeaderApiVersionReader("x-api-version"),
                            new MediaTypeApiVersionReader("x-api-version")
                        ))

            apiVersioningBuilder.AddApiExplorer (fun options ->
                // add the versioned api explorer, which also adds IApiVersionDescriptionProvider service
                // note: the specified format code will format the version as "'v'major[.minor][-status]"
                options.GroupNameFormat <- "'v'VVV"
                options.DefaultApiVersion <- ApiVersion(DateOnly(2023, 10, 1))
                options.AssumeDefaultVersionWhenUnspecified <- true
                options.ApiVersionParameterSource <- HeaderApiVersionReader(Constants.ServerApiVersionHeaderKey)

                // note: this option is only necessary when versioning by url segment. the SubstitutionFormat
                // can also be used to control the format of the API version in route templates
                options.SubstituteApiVersionInUrl <- true)
            |> ignore

            services
                .AddSignalR(fun options -> options.EnableDetailedErrors <- true)
                //.AddStackExchangeRedis(
                //    "localhost:6379",
                //    fun options ->
                //        options.Configuration.ChannelPrefix <- StackExchange.Redis.RedisChannel.Literal("grace-server")
                //        options.Configuration.AbortOnConnectFail <- false
                //)
                .AddJsonProtocol(fun options -> options.PayloadSerializerOptions <- Constants.JsonSerializerOptions)
            |> ignore

            services.AddSingleton<ReviewModels.IReviewModelProvider>(fun _ -> ReviewModels.createProvider configuration)
            |> ignore

            logToConsole $"Exiting ConfigureServices."

        // List all services to the log.
        //services |> Seq.iter (fun service -> logToConsole $"Service: {service.ServiceType}.")

        member _.Configure(app: IApplicationBuilder, env: IWebHostEnvironment) =
            let blobServiceClient = Context.blobServiceClient
            let containers = blobServiceClient.GetBlobContainers()

            let diffContainerName = Environment.GetEnvironmentVariable Constants.EnvironmentVariables.DiffContainerName

            if not
               <| containers.Any(fun c -> c.Name = diffContainerName) then
                logToConsole $"Creating blob container: {diffContainerName}."

                blobServiceClient.CreateBlobContainer(diffContainerName, PublicAccessType.None)
                |> ignore

            if env.IsDevelopment() then app.UseDeveloperExceptionPage() |> ignore

            app
                //.UseMiddleware<FakeMiddleware>()
                .UseMiddleware<HttpSecurityHeadersMiddleware>()
                .UseW3CLogging()
                .UseMiddleware<CorrelationIdMiddleware>()
                .UseMiddleware<LogRequestHeadersMiddleware>()
                .UseStaticFiles()
                .UseRouting()
                .UseAuthentication()
                .UseAuthorization()
                .UseStatusCodePages()
                //.UseMiddleware<TimingMiddleware>()
                .UseMiddleware<ValidateIdsMiddleware>()
            |> ignore

            let allowAnonymousMetrics =
                let envValue = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceMetricsAllowAnonymous)

                let configValue =
                    if String.IsNullOrWhiteSpace envValue then
                        configuration[ getConfigKey Constants.EnvironmentVariables.GraceMetricsAllowAnonymous ]
                    else
                        envValue

                match Boolean.TryParse configValue with
                | true, parsed -> parsed
                | _ -> false

            app
                .UseEndpoints(fun endpointBuilder ->
                    // Add Giraffe (Web API) endpoints
                    endpointBuilder.MapGiraffeEndpoints(endpoints)

                    // Add Prometheus scraping endpoint
                    let metricsEndpoint = endpointBuilder.MapPrometheusScrapingEndpoint()

                    match resolveSecurity "GET" "/metrics" with
                    | Authenticated ->
                        if allowAnonymousMetrics then
                            logToConsole "Warning: /metrics is configured for anonymous access."
                        else
                            metricsEndpoint.RequireAuthorization() |> ignore
                    | AllowAnonymous -> ()
                    | _ -> invalidOp "Unsupported security classification for /metrics."

                    // Add SignalR hub endpoints
                    let notificationsEndpoint =
                        endpointBuilder.MapHub<Notification.NotificationHub>("/notifications")

                    match resolveSecurity "GET" "/notifications" with
                    | Authenticated -> notificationsEndpoint.RequireAuthorization() |> ignore
                    | AllowAnonymous -> ()
                    | _ -> invalidOp "Unsupported security classification for /notifications."
                    |> ignore)

                // If we get here, we didn't find a route.
                .UseGiraffe(
                    notFoundHandler
                )

            // Set the global ApplicationContext.
            ApplicationContext.serviceProvider <- app.ApplicationServices
            ApplicationContext.Set().Wait()

            logToConsole $"Grace Server started successfully."
