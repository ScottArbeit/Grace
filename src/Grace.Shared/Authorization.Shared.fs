namespace Grace.Shared

open Grace.Shared.Utilities
open Grace.Types.Access
open System

module Authorization =

    let normalizeRepoPath (path: string) =
        let normalized = normalizeFilePath path
        let trimmed = normalized.Trim()

        let withLeading =
            if String.IsNullOrWhiteSpace trimmed then "/"
            elif trimmed.StartsWith("/") then trimmed
            else $"/{trimmed}"

        let withoutTrailing =
            if withLeading.Length > 1 && withLeading.EndsWith("/") then
                withLeading.TrimEnd('/')
            else
                withLeading

        withoutTrailing.ToLowerInvariant()

    let roleCatalog: RoleDefinition list =
        [ { RoleId = "OwnerAdmin"
            Operations =
              [ OwnerAdmin
                OrgAdmin
                OrgRead
                RepoAdmin
                RepoRead
                RepoWrite
                BranchAdmin
                BranchRead
                BranchWrite
                PathRead
                PathWrite ] }
          { RoleId = "OrgAdmin"
            Operations =
              [ OrgAdmin
                OrgRead
                RepoRead
                RepoWrite
                BranchRead
                BranchWrite
                PathRead
                PathWrite ] }
          { RoleId = "OrgReader"; Operations = [ OrgRead; RepoRead; BranchRead; PathRead ] }
          { RoleId = "RepoAdmin"
            Operations =
              [ RepoAdmin
                RepoRead
                RepoWrite
                BranchAdmin
                BranchRead
                BranchWrite
                PathRead
                PathWrite ] }
          { RoleId = "RepoContributor"; Operations = [ RepoRead; RepoWrite; BranchRead; BranchWrite; PathRead; PathWrite ] }
          { RoleId = "RepoReader"; Operations = [ RepoRead; BranchRead; PathRead ] } ]

    let scopesForResource resource =
        match resource with
        | System -> [ ScopeSystem ]
        | Owner ownerId -> [ ScopeOwner ownerId; ScopeSystem ]
        | Org(ownerId, organizationId) -> [ ScopeOrg(ownerId, organizationId); ScopeOwner ownerId; ScopeSystem ]
        | Repo(ownerId, organizationId, repositoryId) ->
            [ ScopeRepo(ownerId, organizationId, repositoryId)
              ScopeOrg(ownerId, organizationId)
              ScopeOwner ownerId
              ScopeSystem ]
        | Branch(ownerId, organizationId, repositoryId, branchId) ->
            [ ScopeBranch(ownerId, organizationId, repositoryId, branchId)
              ScopeRepo(ownerId, organizationId, repositoryId)
              ScopeOrg(ownerId, organizationId)
              ScopeOwner ownerId
              ScopeSystem ]
        | Path(ownerId, organizationId, repositoryId, _) ->
            [ ScopeRepo(ownerId, organizationId, repositoryId)
              ScopeOrg(ownerId, organizationId)
              ScopeOwner ownerId
              ScopeSystem ]

    let effectiveOperations (authState: AuthState) (principal: Principal) (resource: Resource) =
        let scopes = scopesForResource resource |> Set.ofList

        let roleLookup =
            authState.Roles
            |> List.map (fun role -> role.RoleId.ToLowerInvariant(), role)
            |> Map.ofList

        authState.Assignments
        |> List.filter (fun assignment -> assignment.Principal = principal && scopes.Contains assignment.Scope)
        |> List.choose (fun assignment -> roleLookup.TryFind(assignment.RoleId.ToLowerInvariant()))
        |> List.collect (fun role -> role.Operations)
        |> Set.ofList

    let checkPathAcl (authState: AuthState) (principal: Principal) (repoId: string) (path: string) (operation: Operation) =
        let permission =
            match operation with
            | PathRead -> Some PathPermRead
            | PathWrite -> Some PathPermWrite
            | _ -> None

        match permission with
        | None -> None
        | Some permission ->
            let normalizedPath = normalizeRepoPath path

            let applicable =
                authState.PathAcls
                |> List.filter (fun ace ->
                    ace.Principal = principal
                    && ace.RepositoryId.ToString().Equals(repoId, StringComparison.OrdinalIgnoreCase)
                    && normalizeRepoPath ace.Path = normalizedPath
                    && ace.Permissions |> List.contains permission)

            if applicable |> List.exists (fun ace -> ace.Access = Deny) then
                Some(Denied "Denied by path ACL.")
            elif applicable |> List.exists (fun ace -> ace.Access = Allow) then
                Some(Allowed "Allowed by path ACL.")
            else
                None

    let checkPermission (authState: AuthState) (principal: Principal) (operation: Operation) (resource: Resource) =
        let aclResult =
            match resource with
            | Path(_, _, repoId, path) -> checkPathAcl authState principal (repoId.ToString()) path operation
            | _ -> None

        match aclResult with
        | Some result -> result
        | None ->
            let operations = effectiveOperations authState principal resource

            if operations.Contains operation then
                Allowed "Allowed by role assignment."
            else
                Denied "No matching role assignment."
