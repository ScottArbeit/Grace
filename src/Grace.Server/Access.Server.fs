namespace Grace.Server

open Giraffe
open Grace.Actors
open Grace.Actors.Extensions
open Grace.Server.Security
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Parameters.Access
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Types
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Threading.Tasks

module Access =

    let private requireGraceUser (handler: HttpHandler) : HttpHandler =
        fun next context ->
            match PrincipalMapper.tryGetUserId context.User with
            | Some _ -> handler next context
            | None -> RequestErrors.UNAUTHORIZED "Grace" "Access" "Authentication required." next context

    let private parseGuid (value: string) (fieldName: string) (correlationId: CorrelationId) =
        let mutable parsed = Guid.Empty

        if String.IsNullOrWhiteSpace value then
            Error(GraceError.Create $"{fieldName} is required." correlationId)
        else if Guid.TryParse(value, &parsed) then
            Ok parsed
        else
            Error(GraceError.Create $"{fieldName} must be a valid Guid." correlationId)

    let private parsePrincipal (principalType: string) (principalId: string) (correlationId: CorrelationId) =
        if String.IsNullOrWhiteSpace principalType
           || String.IsNullOrWhiteSpace principalId then
            Error(GraceError.Create "PrincipalType and PrincipalId are required." correlationId)
        else
            match discriminatedUnionFromString<PrincipalType> principalType with
            | Some parsed -> Ok { PrincipalType = parsed; PrincipalId = principalId }
            | None -> Error(GraceError.Create $"Invalid PrincipalType '{principalType}'." correlationId)

    let private parseScope (scopeKind: string) (parameters: AccessParameters) (correlationId: CorrelationId) =
        let normalized =
            if String.IsNullOrWhiteSpace scopeKind then
                String.Empty
            else
                scopeKind.Trim().ToLowerInvariant()

        match normalized with
        | "" -> Error(GraceError.Create "ScopeKind is required." correlationId)
        | "system" -> Ok Scope.System
        | "owner" ->
            parseGuid parameters.OwnerId (nameof parameters.OwnerId) correlationId
            |> Result.map (fun ownerId -> Scope.Owner ownerId)
        | "org"
        | "organization" ->
            match parseGuid parameters.OwnerId (nameof parameters.OwnerId) correlationId with
            | Error error -> Error error
            | Ok ownerId ->
                parseGuid parameters.OrganizationId (nameof parameters.OrganizationId) correlationId
                |> Result.map (fun organizationId -> Scope.Organization(ownerId, organizationId))
        | "repo"
        | "repository" ->
            match parseGuid parameters.OwnerId (nameof parameters.OwnerId) correlationId with
            | Error error -> Error error
            | Ok ownerId ->
                match parseGuid parameters.OrganizationId (nameof parameters.OrganizationId) correlationId with
                | Error error -> Error error
                | Ok organizationId ->
                    parseGuid parameters.RepositoryId (nameof parameters.RepositoryId) correlationId
                    |> Result.map (fun repositoryId -> Scope.Repository(ownerId, organizationId, repositoryId))
        | "branch" ->
            match parseGuid parameters.OwnerId (nameof parameters.OwnerId) correlationId with
            | Error error -> Error error
            | Ok ownerId ->
                match parseGuid parameters.OrganizationId (nameof parameters.OrganizationId) correlationId with
                | Error error -> Error error
                | Ok organizationId ->
                    match parseGuid parameters.RepositoryId (nameof parameters.RepositoryId) correlationId with
                    | Error error -> Error error
                    | Ok repositoryId ->
                        parseGuid parameters.BranchId (nameof parameters.BranchId) correlationId
                        |> Result.map (fun branchId -> Scope.Branch(ownerId, organizationId, repositoryId, branchId))
        | other -> Error(GraceError.Create $"Invalid ScopeKind '{other}'." correlationId)

    let private parseResource (resourceKind: string) (parameters: CheckPermissionParameters) (correlationId: CorrelationId) =
        let normalized =
            if String.IsNullOrWhiteSpace resourceKind then
                String.Empty
            else
                resourceKind.Trim().ToLowerInvariant()

        match normalized with
        | "" -> Error(GraceError.Create "ResourceKind is required." correlationId)
        | "system" -> Ok Resource.System
        | "owner" ->
            parseGuid parameters.OwnerId (nameof parameters.OwnerId) correlationId
            |> Result.map (fun ownerId -> Resource.Owner ownerId)
        | "org"
        | "organization" ->
            match parseGuid parameters.OwnerId (nameof parameters.OwnerId) correlationId with
            | Error error -> Error error
            | Ok ownerId ->
                parseGuid parameters.OrganizationId (nameof parameters.OrganizationId) correlationId
                |> Result.map (fun organizationId -> Resource.Organization(ownerId, organizationId))
        | "repo"
        | "repository" ->
            match parseGuid parameters.OwnerId (nameof parameters.OwnerId) correlationId with
            | Error error -> Error error
            | Ok ownerId ->
                match parseGuid parameters.OrganizationId (nameof parameters.OrganizationId) correlationId with
                | Error error -> Error error
                | Ok organizationId ->
                    parseGuid parameters.RepositoryId (nameof parameters.RepositoryId) correlationId
                    |> Result.map (fun repositoryId -> Resource.Repository(ownerId, organizationId, repositoryId))
        | "branch" ->
            match parseGuid parameters.OwnerId (nameof parameters.OwnerId) correlationId with
            | Error error -> Error error
            | Ok ownerId ->
                match parseGuid parameters.OrganizationId (nameof parameters.OrganizationId) correlationId with
                | Error error -> Error error
                | Ok organizationId ->
                    match parseGuid parameters.RepositoryId (nameof parameters.RepositoryId) correlationId with
                    | Error error -> Error error
                    | Ok repositoryId ->
                        parseGuid parameters.BranchId (nameof parameters.BranchId) correlationId
                        |> Result.map (fun branchId -> Resource.Branch(ownerId, organizationId, repositoryId, branchId))
        | "path" ->
            if String.IsNullOrWhiteSpace parameters.Path then
                Error(GraceError.Create "Path is required for Path resources." correlationId)
            else
                match parseGuid parameters.OwnerId (nameof parameters.OwnerId) correlationId with
                | Error error -> Error error
                | Ok ownerId ->
                    match parseGuid parameters.OrganizationId (nameof parameters.OrganizationId) correlationId with
                    | Error error -> Error error
                    | Ok organizationId ->
                        parseGuid parameters.RepositoryId (nameof parameters.RepositoryId) correlationId
                        |> Result.map (fun repositoryId -> Resource.Path(ownerId, organizationId, repositoryId, parameters.Path))
        | other -> Error(GraceError.Create $"Invalid ResourceKind '{other}'." correlationId)

    let private parseOperation (operation: string) (correlationId: CorrelationId) =
        if String.IsNullOrWhiteSpace operation then
            Error(GraceError.Create "Operation is required." correlationId)
        else
            match discriminatedUnionFromString<Operation> operation with
            | Some parsed -> Ok parsed
            | None -> Error(GraceError.Create $"Invalid Operation '{operation}'." correlationId)

    let private tryParsePrincipalFilter (principalType: string) (principalId: string) (correlationId: CorrelationId) =
        if String.IsNullOrWhiteSpace principalType
           && String.IsNullOrWhiteSpace principalId then
            Ok None
        elif String.IsNullOrWhiteSpace principalType
             || String.IsNullOrWhiteSpace principalId then
            Error(GraceError.Create "PrincipalType and PrincipalId must be provided together." correlationId)
        else
            parsePrincipal principalType principalId correlationId
            |> Result.map Some

    let GrantRole: HttpHandler =
        requireGraceUser (fun next context ->
            task {
                let! parameters = context |> parse<GrantRoleParameters>
                let correlationId = parameters.CorrelationId

                match parseScope parameters.ScopeKind parameters correlationId with
                | Error error -> return! context |> result400BadRequest error
                | Ok scope ->
                    match parsePrincipal parameters.PrincipalType parameters.PrincipalId correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok principal ->
                        if String.IsNullOrWhiteSpace parameters.RoleId then
                            return!
                                context
                                |> result400BadRequest (GraceError.Create "RoleId is required." correlationId)
                        else
                            let assignment =
                                {
                                    Principal = principal
                                    Scope = scope
                                    RoleId = parameters.RoleId
                                    Source =
                                        if String.IsNullOrWhiteSpace parameters.Source then
                                            "manual"
                                        else
                                            parameters.Source
                                    SourceDetail =
                                        if String.IsNullOrWhiteSpace parameters.SourceDetail then
                                            None
                                        else
                                            Some parameters.SourceDetail
                                    CreatedAt = getCurrentInstant ()
                                }

                            let scopeKey = AccessControl.getScopeKey scope
                            let actorProxy = ActorProxy.AccessControl.CreateActorProxy scopeKey correlationId

                            match! actorProxy.Handle (AccessControlCommand.GrantRole assignment) (createMetadata context) with
                            | Ok returnValue -> return! context |> result200Ok returnValue
                            | Error error -> return! context |> result400BadRequest error
            })

    let RevokeRole: HttpHandler =
        requireGraceUser (fun next context ->
            task {
                let! parameters = context |> parse<RevokeRoleParameters>
                let correlationId = parameters.CorrelationId

                match parseScope parameters.ScopeKind parameters correlationId with
                | Error error -> return! context |> result400BadRequest error
                | Ok scope ->
                    match parsePrincipal parameters.PrincipalType parameters.PrincipalId correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok principal ->
                        if String.IsNullOrWhiteSpace parameters.RoleId then
                            return!
                                context
                                |> result400BadRequest (GraceError.Create "RoleId is required." correlationId)
                        else
                            let scopeKey = AccessControl.getScopeKey scope
                            let actorProxy = ActorProxy.AccessControl.CreateActorProxy scopeKey correlationId

                            match! actorProxy.Handle (AccessControlCommand.RevokeRole(principal, parameters.RoleId)) (createMetadata context) with
                            | Ok returnValue -> return! context |> result200Ok returnValue
                            | Error error -> return! context |> result400BadRequest error
            })

    let ListRoleAssignments: HttpHandler =
        requireGraceUser (fun next context ->
            task {
                let! parameters = context |> parse<ListRoleAssignmentsParameters>
                let correlationId = parameters.CorrelationId

                match parseScope parameters.ScopeKind parameters correlationId with
                | Error error -> return! context |> result400BadRequest error
                | Ok scope ->
                    match tryParsePrincipalFilter parameters.PrincipalType parameters.PrincipalId correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok principalFilter ->
                        let scopeKey = AccessControl.getScopeKey scope
                        let actorProxy = ActorProxy.AccessControl.CreateActorProxy scopeKey correlationId

                        match! actorProxy.Handle (AccessControlCommand.ListAssignments principalFilter) (createMetadata context) with
                        | Ok returnValue -> return! context |> result200Ok returnValue
                        | Error error -> return! context |> result400BadRequest error
            })

    let private parseClaimPermissions (claimPermissions: IList<ClaimPermissionParameters>) (correlationId: string) : Result<List<ClaimPermission>, GraceError> =
        if isNull claimPermissions
           || claimPermissions.Count = 0 then
            Error(GraceError.Create "ClaimPermissions are required." correlationId)
        else
            let folder (state: Result<List<ClaimPermission>, GraceError>) (permission: ClaimPermissionParameters) =
                match state with
                | Error _ -> state
                | Ok permissions ->
                    if String.IsNullOrWhiteSpace permission.Claim then
                        Error(GraceError.Create "Claim is required." correlationId)
                    else
                        match discriminatedUnionFromString<DirectoryPermission> permission.DirectoryPermission with
                        | None -> Error(GraceError.Create $"Invalid DirectoryPermission '{permission.DirectoryPermission}'." correlationId)
                        | Some parsed ->
                            permissions.Add({ Claim = permission.Claim; DirectoryPermission = parsed })
                            Ok permissions

            claimPermissions
            |> Seq.fold folder (Ok(List<ClaimPermission>()))

    let private tryBuildUpsertPathPermission (parameters: UpsertPathPermissionParameters) (correlationId: string) =
        match parseGuid parameters.OwnerId (nameof parameters.OwnerId) correlationId with
        | Error error -> Error error
        | Ok _ ->
            match parseGuid parameters.OrganizationId (nameof parameters.OrganizationId) correlationId with
            | Error error -> Error error
            | Ok _ ->
                match parseGuid parameters.RepositoryId (nameof parameters.RepositoryId) correlationId with
                | Error error -> Error error
                | Ok repositoryId ->
                    if String.IsNullOrWhiteSpace parameters.Path then
                        Error(GraceError.Create "Path is required." correlationId)
                    else
                        match parseClaimPermissions parameters.ClaimPermissions correlationId with
                        | Error error -> Error error
                        | Ok permissions -> Ok(repositoryId, { Path = parameters.Path; Permissions = permissions })

    let UpsertPathPermission: HttpHandler =
        requireGraceUser (fun next context ->
            task {
                let! parameters = context |> parse<UpsertPathPermissionParameters>
                let correlationId = parameters.CorrelationId

                match tryBuildUpsertPathPermission parameters correlationId with
                | Error error -> return! context |> result400BadRequest error
                | Ok (repositoryId, pathPermission) ->
                    let actorProxy = ActorProxy.RepositoryPermission.CreateActorProxy repositoryId correlationId

                    let! upsertResult = actorProxy.Handle (RepositoryPermissionCommand.UpsertPathPermission pathPermission) (createMetadata context)

                    match upsertResult with
                    | Ok returnValue -> return! context |> result200Ok returnValue
                    | Error error -> return! context |> result400BadRequest error
            })

    let RemovePathPermission: HttpHandler =
        requireGraceUser (fun next context ->
            task {
                let! parameters = context |> parse<RemovePathPermissionParameters>
                let correlationId = parameters.CorrelationId

                match parseGuid parameters.OwnerId (nameof parameters.OwnerId) correlationId with
                | Error error -> return! context |> result400BadRequest error
                | Ok _ ->
                    match parseGuid parameters.OrganizationId (nameof parameters.OrganizationId) correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok _ ->
                        match parseGuid parameters.RepositoryId (nameof parameters.RepositoryId) correlationId with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok repositoryId ->
                            if String.IsNullOrWhiteSpace parameters.Path then
                                return!
                                    context
                                    |> result400BadRequest (GraceError.Create "Path is required." correlationId)
                            else
                                let actorProxy = ActorProxy.RepositoryPermission.CreateActorProxy repositoryId correlationId

                                match! actorProxy.Handle (RepositoryPermissionCommand.RemovePathPermission parameters.Path) (createMetadata context) with
                                | Ok returnValue -> return! context |> result200Ok returnValue
                                | Error error -> return! context |> result400BadRequest error
            })

    let ListPathPermissions: HttpHandler =
        requireGraceUser (fun next context ->
            task {
                let! parameters = context |> parse<ListPathPermissionsParameters>
                let correlationId = parameters.CorrelationId

                match parseGuid parameters.OwnerId (nameof parameters.OwnerId) correlationId with
                | Error error -> return! context |> result400BadRequest error
                | Ok _ ->
                    match parseGuid parameters.OrganizationId (nameof parameters.OrganizationId) correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok _ ->
                        match parseGuid parameters.RepositoryId (nameof parameters.RepositoryId) correlationId with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok repositoryId ->
                            let pathFilter = if String.IsNullOrWhiteSpace parameters.Path then None else Some parameters.Path

                            let actorProxy = ActorProxy.RepositoryPermission.CreateActorProxy repositoryId correlationId

                            match! actorProxy.Handle (RepositoryPermissionCommand.ListPathPermissions pathFilter) (createMetadata context) with
                            | Ok returnValue -> return! context |> result200Ok returnValue
                            | Error error -> return! context |> result400BadRequest error
            })

    let CheckPermission: HttpHandler =
        requireGraceUser (fun next context ->
            task {
                let! parameters = context |> parse<CheckPermissionParameters>
                let correlationId = parameters.CorrelationId

                match parseOperation parameters.Operation correlationId with
                | Error error -> return! context |> result400BadRequest error
                | Ok operation ->
                    match parseResource parameters.ResourceKind parameters correlationId with
                    | Error error -> return! context |> result400BadRequest error
                    | Ok resource ->
                        let principalFilter = tryParsePrincipalFilter parameters.PrincipalType parameters.PrincipalId correlationId

                        match principalFilter with
                        | Error error -> return! context |> result400BadRequest error
                        | Ok principalOption ->
                            let principalSet, effectiveClaims =
                                match principalOption with
                                | Some principal -> [ principal ], Set.empty
                                | None ->
                                    let principals = PrincipalMapper.getPrincipals context.User
                                    let claims = PrincipalMapper.getEffectiveClaims context.User
                                    principals, claims

                            let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()
                            let! decision = evaluator.CheckAsync(principalSet, effectiveClaims, operation, resource)

                            let returnValue = GraceReturnValue.Create decision correlationId
                            return! context |> result200Ok returnValue
            })

    let ListRoles: HttpHandler =
        requireGraceUser (fun next context ->
            task {
                let correlationId = getCorrelationId context
                let roles = Authorization.RoleCatalog.getAll ()
                let returnValue = GraceReturnValue.Create roles correlationId
                return! context |> result200Ok returnValue
            })
