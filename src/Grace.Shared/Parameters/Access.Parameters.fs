namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open System
open System.Collections.Generic

module Access =

    [<AllowNullLiteral>]
    type PrincipalDto() =
        member val public Type: string = String.Empty with get, set
        member val public Id: string = String.Empty with get, set

    [<AllowNullLiteral>]
    type ScopeDto() =
        member val public Type: string = String.Empty with get, set
        member val public OwnerId: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public RepositoryId: string = String.Empty with get, set
        member val public BranchId: string = String.Empty with get, set

    [<AllowNullLiteral>]
    type ResourceDto() =
        member val public Type: string = String.Empty with get, set
        member val public OwnerId: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public RepositoryId: string = String.Empty with get, set
        member val public BranchId: string = String.Empty with get, set
        member val public Path: string = String.Empty with get, set

    type GrantRoleRequest() =
        inherit CommonParameters()
        member val public Principal: PrincipalDto = null with get, set
        member val public Scope: ScopeDto = null with get, set
        member val public RoleId: string = String.Empty with get, set
        member val public Source: string = String.Empty with get, set
        member val public SourceDetail: string = String.Empty with get, set

    type RevokeRoleRequest() =
        inherit CommonParameters()
        member val public Principal: PrincipalDto = null with get, set
        member val public Scope: ScopeDto = null with get, set
        member val public RoleId: string = String.Empty with get, set

    type ListRoleAssignmentsRequest() =
        inherit CommonParameters()
        member val public Principal: PrincipalDto = null with get, set
        member val public Scope: ScopeDto = null with get, set

    type AddPathAceRequest() =
        inherit CommonParameters()
        member val public Principal: PrincipalDto = null with get, set
        member val public OwnerId: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public RepositoryId: string = String.Empty with get, set
        member val public Path: string = String.Empty with get, set
        member val public Access: string = String.Empty with get, set
        member val public Permissions: List<string> = List<string>() with get, set

    type RemovePathAceRequest() =
        inherit CommonParameters()
        member val public Principal: PrincipalDto = null with get, set
        member val public OwnerId: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public RepositoryId: string = String.Empty with get, set
        member val public Path: string = String.Empty with get, set
        member val public Permissions: List<string> = List<string>() with get, set

    type ListPathAclsRequest() =
        inherit CommonParameters()
        member val public OwnerId: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public RepositoryId: string = String.Empty with get, set
        member val public PathPrefix: string = String.Empty with get, set
        member val public Principal: PrincipalDto = null with get, set

    type CheckPermissionRequest() =
        inherit CommonParameters()
        member val public Principal: PrincipalDto = null with get, set
        member val public Operation: string = String.Empty with get, set
        member val public Resource: ResourceDto = null with get, set

    type CheckPermissionResponse() =
        member val public Allowed: bool = false with get, set
        member val public Reason: string = String.Empty with get, set

    type RoleDefinitionDto() =
        member val public RoleId: string = String.Empty with get, set
        member val public ScopeTypes: List<string> = List<string>() with get, set
        member val public Operations: List<string> = List<string>() with get, set
