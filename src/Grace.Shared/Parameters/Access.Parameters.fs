namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open System
open System.Collections.Generic

module Access =

    type AccessParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public BranchId = String.Empty with get, set

    type GrantRoleParameters() =
        inherit AccessParameters()
        member val public PrincipalType = String.Empty with get, set
        member val public PrincipalId = String.Empty with get, set
        member val public ScopeKind = String.Empty with get, set
        member val public RoleId = String.Empty with get, set
        member val public Source = String.Empty with get, set
        member val public SourceDetail = String.Empty with get, set

    type RevokeRoleParameters() =
        inherit AccessParameters()
        member val public PrincipalType = String.Empty with get, set
        member val public PrincipalId = String.Empty with get, set
        member val public ScopeKind = String.Empty with get, set
        member val public RoleId = String.Empty with get, set

    type ListRoleAssignmentsParameters() =
        inherit AccessParameters()
        member val public PrincipalType = String.Empty with get, set
        member val public PrincipalId = String.Empty with get, set
        member val public ScopeKind = String.Empty with get, set

    type ClaimPermissionParameters() =
        member val public Claim = String.Empty with get, set
        member val public DirectoryPermission = String.Empty with get, set

    type UpsertPathPermissionParameters() =
        inherit AccessParameters()
        member val public Path = String.Empty with get, set
        member val public ClaimPermissions = List<ClaimPermissionParameters>() with get, set

    type RemovePathPermissionParameters() =
        inherit AccessParameters()
        member val public Path = String.Empty with get, set

    type ListPathPermissionsParameters() =
        inherit AccessParameters()
        member val public Path = String.Empty with get, set

    type CheckPermissionParameters() =
        inherit AccessParameters()
        member val public Operation = String.Empty with get, set
        member val public ResourceKind = String.Empty with get, set
        member val public Path = String.Empty with get, set
        member val public PrincipalType = String.Empty with get, set
        member val public PrincipalId = String.Empty with get, set
