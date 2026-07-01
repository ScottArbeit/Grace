namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Access
open Grace.Shared.Parameters.Common
open Grace.Types.Authorization
open Grace.Types.Common

/// SDK entry point for authorization roles, scoped role assignments, path permissions, and permission checks.
type Access() =

    /// Grants a role to a principal at a scope.
    static member public GrantRole(parameters: GrantRoleParameters) =
        postServer<GrantRoleParameters, RoleAssignment list> (parameters |> ensureCorrelationIdIsSet, "authorize/grant-role")

    /// Revokes a role from a principal at a scope.
    static member public RevokeRole(parameters: RevokeRoleParameters) =
        postServer<RevokeRoleParameters, RoleAssignment list> (parameters |> ensureCorrelationIdIsSet, "authorize/revoke-role")

    /// Lists role assignments at a scope, optionally filtered by principal.
    static member public ListRoleAssignments(parameters: ListRoleAssignmentsParameters) =
        postServer<ListRoleAssignmentsParameters, RoleAssignment list> (parameters |> ensureCorrelationIdIsSet, "authorize/list-role-assignments")

    /// Displays role assignments for the current authenticated principal set.
    static member public ShowRoleAssignments(parameters: ShowRoleAssignmentsParameters) =
        postServer<ShowRoleAssignmentsParameters, RoleAssignment list> (parameters |> ensureCorrelationIdIsSet, "authorize/show")

    /// Upserts a repository path permission.
    static member public UpsertPathPermission(parameters: UpsertPathPermissionParameters) =
        postServer<UpsertPathPermissionParameters, PathPermission list> (parameters |> ensureCorrelationIdIsSet, "authorize/upsert-path-permission")

    /// Removes a repository path permission.
    static member public RemovePathPermission(parameters: RemovePathPermissionParameters) =
        postServer<RemovePathPermissionParameters, PathPermission list> (parameters |> ensureCorrelationIdIsSet, "authorize/remove-path-permission")

    /// Lists repository path permissions.
    static member public ListPathPermissions(parameters: ListPathPermissionsParameters) =
        postServer<ListPathPermissionsParameters, PathPermission list> (parameters |> ensureCorrelationIdIsSet, "authorize/list-path-permissions")

    /// Checks a permission against the current or specified principal.
    static member public CheckPermission(parameters: CheckPermissionParameters) =
        postServer<CheckPermissionParameters, PermissionCheckResult> (parameters |> ensureCorrelationIdIsSet, "authorize/check-permission")

    /// Lists the configured roles.
    static member public ListRoles(parameters: CommonParameters) =
        getServer<CommonParameters, RoleDefinition list> (parameters |> ensureCorrelationIdIsSet, "authorize/list-roles")
