namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Access
open Grace.Shared.Parameters.Common
open Grace.Types.Authorization
open Grace.Types.Types

type Access() =

    /// Grants a role to a principal at a scope.
    static member public GrantRole(parameters: GrantRoleParameters) =
        postServer<GrantRoleParameters, RoleAssignment list> (parameters |> ensureCorrelationIdIsSet, $"access/{nameof (Access.GrantRole)}")

    /// Revokes a role from a principal at a scope.
    static member public RevokeRole(parameters: RevokeRoleParameters) =
        postServer<RevokeRoleParameters, RoleAssignment list> (parameters |> ensureCorrelationIdIsSet, $"access/{nameof (Access.RevokeRole)}")

    /// Lists role assignments at a scope, optionally filtered by principal.
    static member public ListRoleAssignments(parameters: ListRoleAssignmentsParameters) =
        postServer<ListRoleAssignmentsParameters, RoleAssignment list> (parameters |> ensureCorrelationIdIsSet, $"access/{nameof (Access.ListRoleAssignments)}")

    /// Upserts a repository path permission.
    static member public UpsertPathPermission(parameters: UpsertPathPermissionParameters) =
        postServer<UpsertPathPermissionParameters, PathPermission list> (
            parameters |> ensureCorrelationIdIsSet,
            $"access/{nameof (Access.UpsertPathPermission)}"
        )

    /// Removes a repository path permission.
    static member public RemovePathPermission(parameters: RemovePathPermissionParameters) =
        postServer<RemovePathPermissionParameters, PathPermission list> (
            parameters |> ensureCorrelationIdIsSet,
            $"access/{nameof (Access.RemovePathPermission)}"
        )

    /// Lists repository path permissions.
    static member public ListPathPermissions(parameters: ListPathPermissionsParameters) =
        postServer<ListPathPermissionsParameters, PathPermission list> (parameters |> ensureCorrelationIdIsSet, $"access/{nameof (Access.ListPathPermissions)}")

    /// Checks a permission against the current or specified principal.
    static member public CheckPermission(parameters: CheckPermissionParameters) =
        postServer<CheckPermissionParameters, PermissionCheckResult> (parameters |> ensureCorrelationIdIsSet, $"access/{nameof (Access.CheckPermission)}")

    /// Lists the configured roles.
    static member public ListRoles(parameters: CommonParameters) =
        getServer<CommonParameters, RoleDefinition list> (parameters |> ensureCorrelationIdIsSet, "access/listRoles")
