namespace Grace.Shared

open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Common
open System

/// Contains authorization helpers.
module Authorization =

    /// Contains role catalog helpers.
    module RoleCatalog =

        let private scopeSystem = "system"
        let private scopeOwner = "owner"
        let private scopeOrganization = "organization"
        let private scopeRepository = "repository"
        let private scopeBranch = "branch"

        let private systemAdminOperations =
            set [ SystemAdmin
                  SystemOperate
                  SystemRead
                  OwnerAdmin
                  OwnerWrite
                  OwnerRead
                  OrganizationAdmin
                  OrganizationWrite
                  OrganizationRead
                  RepositoryAdmin
                  RepositoryWrite
                  RepositoryRead
                  BranchAdmin
                  BranchWrite
                  BranchRead
                  PathRead
                  PathWrite
                  ApprovalPolicyManage
                  ApprovalRequestRead
                  ApprovalRequestRespond
                  WebhookManage
                  WebhookDeliveryRead ]

        let private systemOperatorOperations =
            set [ SystemOperate
                  SystemRead
                  OwnerAdmin
                  OwnerWrite
                  OwnerRead
                  OrganizationAdmin
                  OrganizationWrite
                  OrganizationRead
                  RepositoryAdmin
                  RepositoryWrite
                  RepositoryRead
                  BranchAdmin
                  BranchWrite
                  BranchRead
                  PathRead
                  PathWrite
                  ApprovalPolicyManage
                  ApprovalRequestRead
                  ApprovalRequestRespond
                  WebhookManage
                  WebhookDeliveryRead ]

        let private systemReaderOperations =
            set [ SystemRead
                  OwnerRead
                  OrganizationRead
                  RepositoryRead
                  BranchRead
                  PathRead
                  ApprovalRequestRead
                  WebhookDeliveryRead ]

        let private ownerAdminOperations =
            set [ OwnerAdmin
                  OwnerWrite
                  OwnerRead
                  OrganizationAdmin
                  OrganizationWrite
                  OrganizationRead
                  RepositoryAdmin
                  RepositoryWrite
                  RepositoryRead
                  BranchAdmin
                  BranchWrite
                  BranchRead
                  PathRead
                  PathWrite
                  ApprovalPolicyManage
                  ApprovalRequestRead
                  ApprovalRequestRespond
                  WebhookManage
                  WebhookDeliveryRead ]

        let private ownerContributorOperations =
            set [ OwnerWrite
                  OwnerRead
                  OrganizationWrite
                  OrganizationRead
                  RepositoryWrite
                  RepositoryRead
                  BranchWrite
                  BranchRead
                  PathWrite
                  PathRead ]

        let private ownerReaderOperations =
            set [ OwnerRead
                  OrganizationRead
                  RepositoryRead
                  BranchRead
                  PathRead ]

        let private organizationAdminOperations =
            set [ OrganizationAdmin
                  OrganizationWrite
                  OrganizationRead
                  RepositoryAdmin
                  RepositoryWrite
                  RepositoryRead
                  PathWrite
                  PathRead
                  BranchAdmin
                  BranchWrite
                  BranchRead
                  ApprovalPolicyManage
                  ApprovalRequestRead
                  ApprovalRequestRespond
                  WebhookManage
                  WebhookDeliveryRead ]

        let private organizationContributorOperations =
            set [ OrganizationWrite
                  OrganizationRead
                  RepositoryWrite
                  RepositoryRead
                  PathWrite
                  PathRead
                  BranchWrite
                  BranchRead ]

        let private organizationReaderOperations =
            set [ OrganizationRead
                  RepositoryRead
                  PathRead
                  BranchRead ]

        let private repositoryAdminOperations =
            set [ RepositoryAdmin
                  RepositoryWrite
                  RepositoryRead
                  PathWrite
                  PathRead
                  BranchAdmin
                  BranchWrite
                  BranchRead
                  ApprovalPolicyManage
                  ApprovalRequestRead
                  ApprovalRequestRespond
                  WebhookManage
                  WebhookDeliveryRead ]

        let private repositoryContributorOperations =
            set [ RepositoryWrite
                  RepositoryRead
                  PathWrite
                  PathRead
                  BranchWrite
                  BranchRead ]

        let private repositoryReaderOperations =
            set [ RepositoryRead
                  PathRead
                  BranchRead
                  ApprovalRequestRead ]

        let private branchAdminOperations =
            set [ BranchAdmin
                  BranchWrite
                  BranchRead ]

        let private branchWriterOperations = set [ BranchWrite; BranchRead ]

        let private branchReaderOperations = set [ BranchRead ]

        let private approvalResponderOperations =
            set [ ApprovalRequestRead
                  ApprovalRequestRespond ]

        let private legacyApprovalResponderRoleId = "ApprovalResponder"

        /// Returns role assignments that are effective for the requested organization, repository, or global scope.
        let private roles: RoleDefinition list =
            [
                { RoleId = "SystemAdmin"; AllowedOperations = systemAdminOperations; AppliesTo = Set.ofList [ scopeSystem ] }
                { RoleId = "SystemOperator"; AllowedOperations = systemOperatorOperations; AppliesTo = Set.ofList [ scopeSystem ] }
                { RoleId = "SystemReader"; AllowedOperations = systemReaderOperations; AppliesTo = Set.ofList [ scopeSystem ] }
                { RoleId = "OwnerAdmin"; AllowedOperations = ownerAdminOperations; AppliesTo = Set.ofList [ scopeOwner ] }
                { RoleId = "OwnerContributor"; AllowedOperations = ownerContributorOperations; AppliesTo = Set.ofList [ scopeOwner ] }
                { RoleId = "OwnerReader"; AllowedOperations = ownerReaderOperations; AppliesTo = Set.ofList [ scopeOwner ] }
                { RoleId = "OrganizationAdmin"; AllowedOperations = organizationAdminOperations; AppliesTo = Set.ofList [ scopeOrganization ] }
                { RoleId = "OrganizationContributor"; AllowedOperations = organizationContributorOperations; AppliesTo = Set.ofList [ scopeOrganization ] }
                { RoleId = "OrganizationReader"; AllowedOperations = organizationReaderOperations; AppliesTo = Set.ofList [ scopeOrganization ] }
                { RoleId = "RepositoryAdmin"; AllowedOperations = repositoryAdminOperations; AppliesTo = Set.ofList [ scopeRepository ] }
                { RoleId = "RepositoryContributor"; AllowedOperations = repositoryContributorOperations; AppliesTo = Set.ofList [ scopeRepository ] }
                { RoleId = "RepositoryReader"; AllowedOperations = repositoryReaderOperations; AppliesTo = Set.ofList [ scopeRepository ] }
                { RoleId = "BranchAdmin"; AllowedOperations = branchAdminOperations; AppliesTo = Set.ofList [ scopeBranch ] }
                { RoleId = "BranchWriter"; AllowedOperations = branchWriterOperations; AppliesTo = Set.ofList [ scopeBranch ] }
                { RoleId = "BranchReader"; AllowedOperations = branchReaderOperations; AppliesTo = Set.ofList [ scopeBranch ] }
                { RoleId = "RepositoryApprovalResponder"; AllowedOperations = approvalResponderOperations; AppliesTo = Set.ofList [ scopeRepository ] }
                { RoleId = "BranchApprovalResponder"; AllowedOperations = approvalResponderOperations; AppliesTo = Set.ofList [ scopeBranch ] }
            ]

        /// Gets all.
        let getAll () = roles

        /// Attempts to get.
        let tryGet (roleId: RoleId) =
            roles
            |> List.tryFind (fun role -> role.RoleId.Equals(roleId, StringComparison.OrdinalIgnoreCase))

        /// Attempts to get single scope kind.
        let tryGetSingleScopeKind (roleId: RoleId) =
            tryGet roleId
            |> Option.bind (fun role ->
                if role.AppliesTo.Count = 1 then
                    role.AppliesTo |> Seq.exactlyOne |> Some
                else
                    None)

        /// Attempts to get legacy assignment role.
        let tryGetLegacyAssignmentRole (roleId: RoleId) (assignmentScopeKind: string) =
            if
                legacyApprovalResponderRoleId.Equals(roleId, StringComparison.OrdinalIgnoreCase)
                && (assignmentScopeKind.Equals(scopeRepository, StringComparison.OrdinalIgnoreCase)
                    || assignmentScopeKind.Equals(scopeBranch, StringComparison.OrdinalIgnoreCase))
            then
                Some { RoleId = legacyApprovalResponderRoleId; AllowedOperations = approvalResponderOperations; AppliesTo = Set.ofList [ assignmentScopeKind ] }
            else
                None

    /// Builds the resource scopes that can satisfy an authorization check for the requested resource.
    let scopesForResource (resource: Resource) =
        match resource with
        | Resource.System -> [ Scope.System ]
        | Resource.Owner ownerId -> [ Scope.Owner ownerId; Scope.System ]
        | Resource.Organization (ownerId, organizationId) ->
            [
                Scope.Organization(ownerId, organizationId)
                Scope.Owner ownerId
                Scope.System
            ]
        | Resource.Repository (ownerId, organizationId, repositoryId) ->
            [
                Scope.Repository(ownerId, organizationId, repositoryId)
                Scope.Organization(ownerId, organizationId)
                Scope.Owner ownerId
                Scope.System
            ]
        | Resource.Branch (ownerId, organizationId, repositoryId, branchId) ->
            [
                Scope.Branch(ownerId, organizationId, repositoryId, branchId)
                Scope.Repository(ownerId, organizationId, repositoryId)
                Scope.Organization(ownerId, organizationId)
                Scope.Owner ownerId
                Scope.System
            ]
        | Resource.Path (ownerId, organizationId, repositoryId, _relativePath) ->
            [
                Scope.Repository(ownerId, organizationId, repositoryId)
                Scope.Organization(ownerId, organizationId)
                Scope.Owner ownerId
                Scope.System
            ]

    /// Classifies a scope string as global, organization, repository, or unsupported.
    let private scopeKind (scope: Scope) =
        match scope with
        | Scope.System -> "system"
        | Scope.Owner _ -> "owner"
        | Scope.Organization _ -> "organization"
        | Scope.Repository _ -> "repository"
        | Scope.Branch _ -> "branch"

    /// Combines matching role grants into the operation set available to the principal.
    let effectiveOperations (roleCatalog: RoleDefinition list) (assignments: RoleAssignment list) (principalSet: Principal list) (resource: Resource) =
        let scopeSet = scopesForResource resource |> Set.ofList
        let principalLookup = principalSet |> Set.ofList

        assignments
        |> List.choose (fun assignment ->
            if principalLookup.Contains assignment.Principal
               && scopeSet.Contains assignment.Scope then
                let scopeKind = scopeKind assignment.Scope

                roleCatalog
                |> List.tryFind (fun role ->
                    role.RoleId.Equals(assignment.RoleId, StringComparison.OrdinalIgnoreCase)
                    && role.AppliesTo.Contains scopeKind)
                |> Option.orElseWith (fun () -> RoleCatalog.tryGetLegacyAssignmentRole assignment.RoleId scopeKind)
            else
                None)
        |> List.collect (fun role -> role.AllowedOperations |> Set.toList)
        |> Set.ofList

    /// Checks path permission.
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
            | Resource.Path (_, _, _, relativePath) -> checkPathPermission pathPermissions effectiveClaims relativePath operation
            | _ -> None

        match pathDecision with
        | Some decision -> decision
        | None ->
            let effectiveOps = effectiveOperations roleCatalog assignments principalSet resource

            if effectiveOps.Contains operation then
                Allowed $"Allowed by role assignments for {operation}."
            else
                Denied $"Denied: missing permission {operation}."
