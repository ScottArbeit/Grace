namespace Grace.Shared

open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Types
open System

module Authorization =

    module RoleCatalog =

        let private scopeSystem = "system"
        let private scopeOwner = "owner"
        let private scopeOrganization = "organization"
        let private scopeRepository = "repository"

        let private systemAdminOperations =
            set
                [ SystemAdmin
                  OwnerAdmin
                  OwnerRead
                  OrgAdmin
                  OrgRead
                  RepoAdmin
                  RepoRead
                  RepoWrite
                  BranchAdmin
                  BranchRead
                  BranchWrite
                  PathRead
                  PathWrite ]

        let private ownerAdminOperations =
            set
                [ OwnerAdmin
                  OwnerRead
                  OrgAdmin
                  OrgRead
                  RepoAdmin
                  RepoRead
                  RepoWrite
                  BranchAdmin
                  BranchRead
                  BranchWrite
                  PathRead
                  PathWrite ]

        let private ownerReaderOperations = set [ OwnerRead; OrgRead; RepoRead; BranchRead; PathRead ]

        let private orgAdminOperations =
            set
                [ OrgAdmin
                  OrgRead
                  RepoAdmin
                  RepoWrite
                  RepoRead
                  PathWrite
                  PathRead
                  BranchAdmin
                  BranchWrite
                  BranchRead ]

        let private orgReaderOperations = set [ OrgRead; RepoRead; PathRead; BranchRead ]

        let private repoAdminOperations = set [ RepoAdmin; RepoWrite; RepoRead; PathWrite; PathRead; BranchWrite; BranchRead ]

        let private repoContributorOperations = set [ RepoWrite; RepoRead; PathWrite; PathRead; BranchWrite; BranchRead ]

        let private repoReaderOperations = set [ RepoRead; PathRead; BranchRead ]

        let private roles: RoleDefinition list =
            [ { RoleId = "SystemAdmin"; AllowedOperations = systemAdminOperations; AppliesTo = Set.ofList [ scopeSystem ] }
              { RoleId = "OwnerAdmin"; AllowedOperations = ownerAdminOperations; AppliesTo = Set.ofList [ scopeOwner ] }
              { RoleId = "OwnerReader"; AllowedOperations = ownerReaderOperations; AppliesTo = Set.ofList [ scopeOwner ] }
              { RoleId = "OrgAdmin"; AllowedOperations = orgAdminOperations; AppliesTo = Set.ofList [ scopeOrganization ] }
              { RoleId = "OrgReader"; AllowedOperations = orgReaderOperations; AppliesTo = Set.ofList [ scopeOrganization ] }
              { RoleId = "RepoAdmin"; AllowedOperations = repoAdminOperations; AppliesTo = Set.ofList [ scopeRepository ] }
              { RoleId = "RepoContributor"; AllowedOperations = repoContributorOperations; AppliesTo = Set.ofList [ scopeRepository ] }
              { RoleId = "RepoReader"; AllowedOperations = repoReaderOperations; AppliesTo = Set.ofList [ scopeRepository ] } ]

        let getAll () = roles

        let tryGet (roleId: RoleId) =
            roles
            |> List.tryFind (fun role -> role.RoleId.Equals(roleId, StringComparison.OrdinalIgnoreCase))

    let scopesForResource (resource: Resource) =
        match resource with
        | Resource.System -> [ Scope.System ]
        | Resource.Owner ownerId -> [ Scope.Owner ownerId; Scope.System ]
        | Resource.Organization(ownerId, organizationId) ->
            [ Scope.Organization(ownerId, organizationId)
              Scope.Owner ownerId
              Scope.System ]
        | Resource.Repository(ownerId, organizationId, repositoryId) ->
            [ Scope.Repository(ownerId, organizationId, repositoryId)
              Scope.Organization(ownerId, organizationId)
              Scope.Owner ownerId
              Scope.System ]
        | Resource.Branch(ownerId, organizationId, repositoryId, branchId) ->
            [ Scope.Branch(ownerId, organizationId, repositoryId, branchId)
              Scope.Repository(ownerId, organizationId, repositoryId)
              Scope.Organization(ownerId, organizationId)
              Scope.Owner ownerId
              Scope.System ]
        | Resource.Path(ownerId, organizationId, repositoryId, _relativePath) ->
            [ Scope.Repository(ownerId, organizationId, repositoryId)
              Scope.Organization(ownerId, organizationId)
              Scope.Owner ownerId
              Scope.System ]

    let private scopeKind (scope: Scope) =
        match scope with
        | Scope.System -> "system"
        | Scope.Owner _ -> "owner"
        | Scope.Organization _ -> "organization"
        | Scope.Repository _ -> "repository"
        | Scope.Branch _ -> "branch"

    let effectiveOperations (roleCatalog: RoleDefinition list) (assignments: RoleAssignment list) (principalSet: Principal list) (resource: Resource) =
        let scopeSet = scopesForResource resource |> Set.ofList
        let principalLookup = principalSet |> Set.ofList

        assignments
        |> List.choose (fun assignment ->
            if
                principalLookup.Contains assignment.Principal
                && scopeSet.Contains assignment.Scope
            then
                let scopeKind = scopeKind assignment.Scope

                roleCatalog
                |> List.tryFind (fun role ->
                    role.RoleId.Equals(assignment.RoleId, StringComparison.OrdinalIgnoreCase)
                    && role.AppliesTo.Contains scopeKind)
            else
                None)
        |> List.collect (fun role -> role.AllowedOperations |> Set.toList)
        |> Set.ofList

    let checkPathPermission (pathPermissions: PathPermission list) (effectiveClaims: Set<string>) (targetPath: RelativePath) (operation: Operation) =
        match operation with
        | PathRead
        | PathWrite ->
            let normalizedTarget = normalizeFilePath targetPath

            let matchingClaimPermissions =
                pathPermissions
                |> List.filter (fun pathPermission -> normalizeFilePath pathPermission.Path = normalizedTarget)
                |> List.collect (fun pathPermission -> pathPermission.Permissions |> Seq.toList)
                |> List.filter (fun permission -> effectiveClaims.Contains permission.Claim)

            let hasDeny =
                matchingClaimPermissions
                |> List.exists (fun permission -> permission.DirectoryPermission = DirectoryPermission.NoAccess)

            if hasDeny then
                Some(Denied($"Denied by path permission at '{normalizedTarget}'."))
            else
                let allowsRead, allowsWrite =
                    matchingClaimPermissions
                    |> List.fold
                        (fun (readAllowed, writeAllowed) permission ->
                            match permission.DirectoryPermission with
                            | DirectoryPermission.FullControl
                            | DirectoryPermission.Modify -> true, true
                            | DirectoryPermission.Read
                            | DirectoryPermission.ListContents -> true, writeAllowed
                            | DirectoryPermission.NoAccess
                            | DirectoryPermission.NotSet -> readAllowed, writeAllowed)
                        (false, false)

                match operation with
                | PathRead when allowsRead -> Some(Allowed($"Allowed by path permission at '{normalizedTarget}'."))
                | PathWrite when allowsWrite -> Some(Allowed($"Allowed by path permission at '{normalizedTarget}'."))
                | _ -> None
        | _ -> None

    let checkPermission
        (roleCatalog: RoleDefinition list)
        (assignments: RoleAssignment list)
        (pathPermissions: PathPermission list)
        (principalSet: Principal list)
        (effectiveClaims: Set<string>)
        (operation: Operation)
        (resource: Resource)
        =
        let pathDecision =
            match resource with
            | Resource.Path(_, _, _, relativePath) -> checkPathPermission pathPermissions effectiveClaims relativePath operation
            | _ -> None

        match pathDecision with
        | Some decision -> decision
        | None ->
            let effectiveOps = effectiveOperations roleCatalog assignments principalSet resource

            if effectiveOps.Contains operation then
                Allowed $"Allowed by role assignments for {operation}."
            else
                Denied $"Denied: missing permission {operation}."
