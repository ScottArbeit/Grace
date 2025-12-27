namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.Services
open Grace.Shared.Authorization
open Grace.Shared.Parameters.Access
open Grace.Shared.Validation.Errors
open Grace.Shared.Utilities
open Grace.Shared
open Grace.Types.Access
open Grace.Types.Organization
open Grace.Types.Owner
open Grace.Types.Repository
open Grace.Types.Types
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open System
open System.Collections.Generic
open System.Threading.Tasks

module Access =

    let private errorFromAccess (error: AccessError) correlationId = GraceError.Create (AccessError.getErrorMessage error) correlationId

    let private parsePrincipal (principalDto: PrincipalDto) correlationId =
        if
            isNull principalDto
            || String.IsNullOrWhiteSpace principalDto.Type
            || String.IsNullOrWhiteSpace principalDto.Id
        then
            Error(errorFromAccess AccessError.PrincipalIsRequired correlationId)
        else
            match principalDto.Type.Trim().ToLowerInvariant() with
            | "user" -> Ok(Principal.User principalDto.Id)
            | "group" -> Ok(Principal.Group principalDto.Id)
            | "service" -> Ok(Principal.Service principalDto.Id)
            | _ -> Error(errorFromAccess AccessError.InvalidPrincipalType correlationId)

    let private tryParseGuid (value: string) =
        let mutable guid = Guid.Empty
        if Guid.TryParse(value, &guid) && guid <> Guid.Empty then Some guid else None

    let private parseScope (scopeDto: ScopeDto) correlationId =
        if isNull scopeDto || String.IsNullOrWhiteSpace scopeDto.Type then
            Error(errorFromAccess AccessError.ScopeIsRequired correlationId)
        else
            match scopeDto.Type.Trim().ToLowerInvariant() with
            | "system" -> Ok ScopeSystem
            | "owner" ->
                match tryParseGuid scopeDto.OwnerId with
                | Some ownerId -> Ok(ScopeOwner ownerId)
                | None -> Error(GraceError.Create (OwnerError.getErrorMessage OwnerError.InvalidOwnerId) correlationId)
            | "org" ->
                match tryParseGuid scopeDto.OwnerId, tryParseGuid scopeDto.OrganizationId with
                | Some ownerId, Some organizationId -> Ok(ScopeOrg(ownerId, organizationId))
                | _ -> Error(errorFromAccess AccessError.InvalidScopeType correlationId)
            | "repo" ->
                match tryParseGuid scopeDto.OwnerId, tryParseGuid scopeDto.OrganizationId, tryParseGuid scopeDto.RepositoryId with
                | Some ownerId, Some organizationId, Some repositoryId -> Ok(ScopeRepo(ownerId, organizationId, repositoryId))
                | _ -> Error(errorFromAccess AccessError.InvalidScopeType correlationId)
            | "branch" ->
                match
                    tryParseGuid scopeDto.OwnerId, tryParseGuid scopeDto.OrganizationId, tryParseGuid scopeDto.RepositoryId, tryParseGuid scopeDto.BranchId
                with
                | Some ownerId, Some organizationId, Some repositoryId, Some branchId -> Ok(ScopeBranch(ownerId, organizationId, repositoryId, branchId))
                | _ -> Error(errorFromAccess AccessError.InvalidScopeType correlationId)
            | _ -> Error(errorFromAccess AccessError.InvalidScopeType correlationId)

    let private parseResource (resourceDto: ResourceDto) correlationId =
        if isNull resourceDto || String.IsNullOrWhiteSpace resourceDto.Type then
            Error(errorFromAccess AccessError.InvalidScopeType correlationId)
        else
            match resourceDto.Type.Trim().ToLowerInvariant() with
            | "system" -> Ok Resource.System
            | "owner" ->
                match tryParseGuid resourceDto.OwnerId with
                | Some ownerId -> Ok(Resource.Owner ownerId)
                | None -> Error(GraceError.Create (OwnerError.getErrorMessage OwnerError.InvalidOwnerId) correlationId)
            | "org" ->
                match tryParseGuid resourceDto.OwnerId, tryParseGuid resourceDto.OrganizationId with
                | Some ownerId, Some organizationId -> Ok(Resource.Org(ownerId, organizationId))
                | _ -> Error(errorFromAccess AccessError.InvalidScopeType correlationId)
            | "repo" ->
                match tryParseGuid resourceDto.OwnerId, tryParseGuid resourceDto.OrganizationId, tryParseGuid resourceDto.RepositoryId with
                | Some ownerId, Some organizationId, Some repositoryId -> Ok(Resource.Repo(ownerId, organizationId, repositoryId))
                | _ -> Error(errorFromAccess AccessError.InvalidScopeType correlationId)
            | "branch" ->
                match
                    tryParseGuid resourceDto.OwnerId,
                    tryParseGuid resourceDto.OrganizationId,
                    tryParseGuid resourceDto.RepositoryId,
                    tryParseGuid resourceDto.BranchId
                with
                | Some ownerId, Some organizationId, Some repositoryId, Some branchId -> Ok(Resource.Branch(ownerId, organizationId, repositoryId, branchId))
                | _ -> Error(errorFromAccess AccessError.InvalidScopeType correlationId)
            | "path" ->
                match tryParseGuid resourceDto.OwnerId, tryParseGuid resourceDto.OrganizationId, tryParseGuid resourceDto.RepositoryId with
                | Some ownerId, Some organizationId, Some repositoryId ->
                    if String.IsNullOrWhiteSpace resourceDto.Path then
                        Error(errorFromAccess AccessError.InvalidPath correlationId)
                    else
                        Ok(Resource.Path(ownerId, organizationId, repositoryId, resourceDto.Path))
                | _ -> Error(errorFromAccess AccessError.InvalidScopeType correlationId)
            | _ -> Error(errorFromAccess AccessError.InvalidScopeType correlationId)

    let private parseOperation (operation: string) correlationId =
        match discriminatedUnionFromString<Operation> operation with
        | Some op -> Ok op
        | None -> Error(errorFromAccess AccessError.InvalidOperation correlationId)

    let private parseAccessType (access: string) correlationId =
        match access.Trim().ToLowerInvariant() with
        | "allow" -> Ok Access.Allow
        | "deny" -> Ok Access.Deny
        | _ -> Error(errorFromAccess AccessError.InvalidAccessType correlationId)

    let private parsePermissions (permissions: List<string>) correlationId =
        if isNull permissions || permissions.Count = 0 then
            Error(errorFromAccess AccessError.InvalidPermissions correlationId)
        else
            let mutable parsed = List<Grace.Types.Access.PathPermission>()

            for permission in permissions do
                match permission.Trim().ToLowerInvariant() with
                | "read" -> parsed.Add Grace.Types.Access.PathPermRead
                | "write" -> parsed.Add Grace.Types.Access.PathPermWrite
                | "read-write"
                | "write-read" ->
                    parsed.Add Grace.Types.Access.PathPermRead
                    parsed.Add Grace.Types.Access.PathPermWrite
                | _ -> ()

            parsed
            |> Seq.distinct
            |> Seq.toList
            |> function
                | [] -> Error(errorFromAccess AccessError.InvalidPermissions correlationId)
                | list -> Ok list

    let private toRoleDefinitionDto (role: RoleDefinition) =
        let scopeTypes =
            match role.RoleId with
            | "OwnerAdmin" -> [ "owner" ]
            | "OrgAdmin"
            | "OrgReader" -> [ "org" ]
            | "RepoAdmin"
            | "RepoContributor"
            | "RepoReader" -> [ "repo" ]
            | _ -> [ "repo" ]

        let dto = RoleDefinitionDto()
        dto.RoleId <- role.RoleId
        dto.ScopeTypes <- List<string>(scopeTypes)
        dto.Operations <- List<string>(role.Operations |> List.map getDiscriminatedUnionCaseName)
        dto

    module Role =
        let Grant: HttpHandler =
            fun next context ->
                task {
                    let! parameters = context |> Services.parse<GrantRoleRequest>
                    let correlationId = getCorrelationId context

                    let role =
                        roleCatalog
                        |> List.tryFind (fun role -> role.RoleId.Equals(parameters.RoleId, StringComparison.OrdinalIgnoreCase))

                    match role with
                    | None ->
                        let error = errorFromAccess AccessError.InvalidRoleId correlationId
                        return! context |> Services.result400BadRequest error
                    | Some _ ->
                        match parsePrincipal parameters.Principal correlationId, parseScope parameters.Scope correlationId with
                        | Ok principal, Ok scope ->
                            let roleAssignment =
                                { Principal = principal
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
                                  CreatedAt = getCurrentInstant () }

                            let! result =
                                match scope with
                                | ScopeOwner ownerId ->
                                    let actor = Owner.CreateActorProxy ownerId correlationId
                                    actor.Handle (OwnerCommand.GrantRoleAssignment roleAssignment) (Services.createMetadata context)
                                | ScopeOrg(_, organizationId) ->
                                    let actor = Organization.CreateActorProxy organizationId correlationId
                                    actor.Handle (OrganizationCommand.GrantRoleAssignment roleAssignment) (Services.createMetadata context)
                                | ScopeRepo(_, organizationId, repositoryId) ->
                                    let actor = Repository.CreateActorProxy organizationId repositoryId correlationId
                                    actor.Handle (RepositoryCommand.GrantRoleAssignment roleAssignment) (Services.createMetadata context)
                                | _ ->
                                    Error(GraceError.Create (AccessError.getErrorMessage AccessError.InvalidScopeType) correlationId)
                                    |> Task.FromResult

                            match result with
                            | Ok graceReturnValue -> return! context |> Services.result200Ok graceReturnValue
                            | Error graceError -> return! context |> Services.result400BadRequest graceError
                        | Error error, _
                        | _, Error error -> return! context |> Services.result400BadRequest error
                }

        let Revoke: HttpHandler =
            fun next context ->
                task {
                    let! parameters = context |> Services.parse<RevokeRoleRequest>
                    let correlationId = getCorrelationId context

                    match parsePrincipal parameters.Principal correlationId, parseScope parameters.Scope correlationId with
                    | Ok principal, Ok scope ->
                        if String.IsNullOrWhiteSpace parameters.RoleId then
                            return!
                                context
                                |> Services.result400BadRequest (errorFromAccess AccessError.RoleIdIsRequired correlationId)
                        else
                            let! result =
                                match scope with
                                | ScopeOwner ownerId ->
                                    let actor = Owner.CreateActorProxy ownerId correlationId
                                    actor.Handle (OwnerCommand.RevokeRoleAssignment(principal, parameters.RoleId)) (Services.createMetadata context)
                                | ScopeOrg(_, organizationId) ->
                                    let actor = Organization.CreateActorProxy organizationId correlationId
                                    actor.Handle (OrganizationCommand.RevokeRoleAssignment(principal, parameters.RoleId)) (Services.createMetadata context)
                                | ScopeRepo(_, organizationId, repositoryId) ->
                                    let actor = Repository.CreateActorProxy organizationId repositoryId correlationId
                                    actor.Handle (RepositoryCommand.RevokeRoleAssignment(principal, parameters.RoleId)) (Services.createMetadata context)
                                | _ ->
                                    Error(GraceError.Create (AccessError.getErrorMessage AccessError.InvalidScopeType) correlationId)
                                    |> Task.FromResult

                            match result with
                            | Ok graceReturnValue -> return! context |> Services.result200Ok graceReturnValue
                            | Error graceError -> return! context |> Services.result400BadRequest graceError
                    | Error error, _
                    | _, Error error -> return! context |> Services.result400BadRequest error
                }

        let ListAssignments: HttpHandler =
            fun next context ->
                task {
                    let! parameters = context |> Services.parse<ListRoleAssignmentsRequest>
                    let correlationId = getCorrelationId context

                    match parseScope parameters.Scope correlationId with
                    | Error error -> return! context |> Services.result400BadRequest error
                    | Ok scope ->
                        let! assignments =
                            match scope with
                            | ScopeOwner ownerId ->
                                let actor = Owner.CreateActorProxy ownerId correlationId
                                actor.GetRoleAssignments correlationId
                            | ScopeOrg(_, organizationId) ->
                                let actor = Organization.CreateActorProxy organizationId correlationId
                                actor.GetRoleAssignments correlationId
                            | ScopeRepo(_, organizationId, repositoryId) ->
                                let actor = Repository.CreateActorProxy organizationId repositoryId correlationId
                                actor.GetRoleAssignments correlationId
                            | _ -> Task.FromResult(List<RoleAssignment>() :> IReadOnlyList<RoleAssignment>)

                        let filtered =
                            match parsePrincipal parameters.Principal correlationId with
                            | Ok principal ->
                                assignments
                                |> Seq.filter (fun assignment -> assignment.Principal = principal)
                                |> Seq.toList
                            | Error _ -> assignments |> Seq.toList

                        let returnValue = GraceReturnValue.Create filtered correlationId
                        return! context |> Services.result200Ok returnValue
                }

        let ListRoles: HttpHandler =
            fun next context ->
                task {
                    let roles = roleCatalog |> List.map toRoleDefinitionDto
                    let returnValue = GraceReturnValue.Create roles (getCorrelationId context)
                    return! context |> Services.result200Ok returnValue
                }

        let GetRoleById (roleId: string) : HttpHandler =
            fun next context ->
                task {
                    let role =
                        roleCatalog
                        |> List.tryFind (fun role -> role.RoleId.Equals(roleId, StringComparison.OrdinalIgnoreCase))

                    match role with
                    | None -> return! context |> Services.result404NotFound
                    | Some roleDef ->
                        let dto = toRoleDefinitionDto roleDef
                        let returnValue = GraceReturnValue.Create dto (getCorrelationId context)
                        return! context |> Services.result200Ok returnValue
                }

    module Path =
        let Add: HttpHandler =
            fun next context ->
                task {
                    let! parameters = context |> Services.parse<AddPathAceRequest>
                    let correlationId = getCorrelationId context

                    match parsePrincipal parameters.Principal correlationId with
                    | Error error -> return! context |> Services.result400BadRequest error
                    | Ok principal ->
                        match tryParseGuid parameters.OwnerId, tryParseGuid parameters.OrganizationId, tryParseGuid parameters.RepositoryId with
                        | Some ownerId, Some organizationId, Some repositoryId ->
                            match parseAccessType parameters.Access correlationId, parsePermissions parameters.Permissions correlationId with
                            | Ok accessType, Ok permissions ->
                                if String.IsNullOrWhiteSpace parameters.Path then
                                    return!
                                        context
                                        |> Services.result400BadRequest (errorFromAccess AccessError.InvalidPath correlationId)
                                else
                                    let pathAce =
                                        { Principal = principal
                                          OwnerId = ownerId
                                          OrganizationId = organizationId
                                          RepositoryId = repositoryId
                                          Path = normalizeRepoPath parameters.Path
                                          Access = accessType
                                          Permissions = permissions
                                          CreatedAt = getCurrentInstant () }

                                    let actor = Repository.CreateActorProxy organizationId repositoryId correlationId
                                    let! result = actor.Handle (RepositoryCommand.AddPathAce pathAce) (Services.createMetadata context)

                                    match result with
                                    | Ok graceReturnValue -> return! context |> Services.result200Ok graceReturnValue
                                    | Error graceError -> return! context |> Services.result400BadRequest graceError
                            | Error error, _
                            | _, Error error -> return! context |> Services.result400BadRequest error
                        | _ ->
                            return!
                                context
                                |> Services.result400BadRequest (errorFromAccess AccessError.InvalidScopeType correlationId)
                }

        let Remove: HttpHandler =
            fun next context ->
                task {
                    let! parameters = context |> Services.parse<RemovePathAceRequest>
                    let correlationId = getCorrelationId context

                    match parsePrincipal parameters.Principal correlationId with
                    | Error error -> return! context |> Services.result400BadRequest error
                    | Ok principal ->
                        match tryParseGuid parameters.OwnerId, tryParseGuid parameters.OrganizationId, tryParseGuid parameters.RepositoryId with
                        | Some _, Some organizationId, Some repositoryId ->
                            if String.IsNullOrWhiteSpace parameters.Path then
                                return!
                                    context
                                    |> Services.result400BadRequest (errorFromAccess AccessError.InvalidPath correlationId)
                            else
                                let permissions =
                                    if isNull parameters.Permissions || parameters.Permissions.Count = 0 then
                                        []
                                    else
                                        match parsePermissions parameters.Permissions correlationId with
                                        | Ok permissions -> permissions
                                        | Error _ -> []

                                let actor = Repository.CreateActorProxy organizationId repositoryId correlationId

                                let! result =
                                    actor.Handle
                                        (RepositoryCommand.RemovePathAce(principal, normalizeRepoPath parameters.Path, permissions))
                                        (Services.createMetadata context)

                                match result with
                                | Ok graceReturnValue -> return! context |> Services.result200Ok graceReturnValue
                                | Error graceError -> return! context |> Services.result400BadRequest graceError
                        | _ ->
                            return!
                                context
                                |> Services.result400BadRequest (errorFromAccess AccessError.InvalidScopeType correlationId)
                }

        let List: HttpHandler =
            fun next context ->
                task {
                    let! parameters = context |> Services.parse<ListPathAclsRequest>
                    let correlationId = getCorrelationId context

                    match tryParseGuid parameters.OwnerId, tryParseGuid parameters.OrganizationId, tryParseGuid parameters.RepositoryId with
                    | Some _, Some organizationId, Some repositoryId ->
                        let actor = Repository.CreateActorProxy organizationId repositoryId correlationId
                        let! acls = actor.GetPathAcls correlationId

                        let filteredByPrincipal =
                            match parsePrincipal parameters.Principal correlationId with
                            | Ok principal -> acls |> Seq.filter (fun ace -> ace.Principal = principal) |> Seq.toList
                            | Error _ -> acls |> Seq.toList

                        let filteredByPath =
                            if String.IsNullOrWhiteSpace parameters.PathPrefix then
                                filteredByPrincipal
                            else
                                let prefix = normalizeRepoPath parameters.PathPrefix

                                filteredByPrincipal
                                |> List.filter (fun ace ->
                                    normalizeRepoPath ace.Path
                                    |> fun path -> path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))

                        let returnValue = GraceReturnValue.Create filteredByPath correlationId
                        return! context |> Services.result200Ok returnValue
                    | _ ->
                        return!
                            context
                            |> Services.result400BadRequest (errorFromAccess AccessError.InvalidScopeType correlationId)
                }

    module Permission =
        let Check: HttpHandler =
            fun next context ->
                task {
                    let! parameters = context |> Services.parse<CheckPermissionRequest>
                    let correlationId = getCorrelationId context

                    match
                        parsePrincipal parameters.Principal correlationId,
                        parseOperation parameters.Operation correlationId,
                        parseResource parameters.Resource correlationId
                    with
                    | Ok principal, Ok operation, Ok resource ->
                        let evaluator = context.GetService<Authorization.IGracePermissionEvaluator>()
                        let! result = evaluator.CheckAsync(principal, operation, resource)

                        let response =
                            match result with
                            | Allowed reason -> CheckPermissionResponse(Allowed = true, Reason = reason)
                            | Denied reason -> CheckPermissionResponse(Allowed = false, Reason = reason)

                        let returnValue = GraceReturnValue.Create response correlationId
                        return! context |> Services.result200Ok returnValue
                    | Error error, _, _
                    | _, Error error, _
                    | _, _, Error error -> return! context |> Services.result400BadRequest error
                }
