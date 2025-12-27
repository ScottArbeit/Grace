namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Access
open Grace.Shared.Parameters.Common
open Grace.Types.Access
open Grace.Shared.Utilities
open System

module Access =

    type Role() =
        static member public GrantRoleAsync(parameters: GrantRoleRequest) =
            postServer<GrantRoleRequest, string> (parameters |> ensureCorrelationIdIsSet, "access/role/grant")

        static member public RevokeRoleAsync(parameters: RevokeRoleRequest) =
            postServer<RevokeRoleRequest, string> (parameters |> ensureCorrelationIdIsSet, "access/role/revoke")

        static member public ListRoleAssignmentsAsync(parameters: ListRoleAssignmentsRequest) =
            postServer<ListRoleAssignmentsRequest, RoleAssignment list> (parameters |> ensureCorrelationIdIsSet, "access/role/list")

        static member public ListRolesAsync() =
            let parameters = CommonParameters()
            parameters.CorrelationId <- ensureNonEmptyCorrelationId parameters.CorrelationId
            getServer<CommonParameters, RoleDefinitionDto list> (parameters, "access/roles")

        static member public GetRoleAsync(roleId: string) =
            let parameters = CommonParameters()
            parameters.CorrelationId <- ensureNonEmptyCorrelationId parameters.CorrelationId
            getServer<CommonParameters, RoleDefinitionDto> (parameters, $"access/roles/{roleId}")

    type Path() =
        static member public AddPathAceAsync(parameters: AddPathAceRequest) =
            postServer<AddPathAceRequest, string> (parameters |> ensureCorrelationIdIsSet, "access/path/add")

        static member public RemovePathAceAsync(parameters: RemovePathAceRequest) =
            postServer<RemovePathAceRequest, string> (parameters |> ensureCorrelationIdIsSet, "access/path/remove")

        static member public ListPathAclsAsync(parameters: ListPathAclsRequest) =
            postServer<ListPathAclsRequest, PathAce list> (parameters |> ensureCorrelationIdIsSet, "access/path/list")

    type Permission() =
        static member public CheckPermissionAsync(parameters: CheckPermissionRequest) =
            postServer<CheckPermissionRequest, CheckPermissionResponse> (parameters |> ensureCorrelationIdIsSet, "access/check")
