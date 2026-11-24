namespace Grace.Types

open Grace.Shared.Utilities
open Grace.Types.Common
open NodaTime
open Orleans
open System.Runtime.Serialization

/// Contains authorization helpers.
module Authorization =

    /// Internal principal identifier.
    type PrincipalId = string

    /// The type of principal represented in authorization decisions.
    [<KnownType("GetKnownTypes")>]
    type PrincipalType =
        | User
        | Group
        | Service

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<PrincipalType>()

        /// Every case name, in declaration order, so CLI option validation doesn't reflect over the union at startup.
        static member Names =
            [|
                nameof PrincipalType.User
                nameof PrincipalType.Group
                nameof PrincipalType.Service
            |]

    /// Identifies a principal (user, group, or service).
    [<GenerateSerializer>]
    type Principal =
        {
            [<Id 0u>]
            PrincipalType: PrincipalType
            [<Id 1u>]
            PrincipalId: PrincipalId
        }

    /// Scope for role assignments in the resource hierarchy.
    [<KnownType("GetKnownTypes")>]
    type Scope =
        | [<Id 0u>] System
        | [<Id 1u>] Owner of OwnerId
        | [<Id 2u>] Organization of OwnerId * OrganizationId
        | [<Id 3u>] Repository of OwnerId * OrganizationId * RepositoryId
        | [<Id 4u>] Branch of OwnerId * OrganizationId * RepositoryId * BranchId

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<Scope>()

    /// Resource targeted by an authorization check.
    [<KnownType("GetKnownTypes")>]
    type Resource =
        | [<Id 0u>] System
        | [<Id 1u>] Owner of OwnerId
        | [<Id 2u>] Organization of OwnerId * OrganizationId
        | [<Id 3u>] Repository of OwnerId * OrganizationId * RepositoryId
        | [<Id 4u>] Branch of OwnerId * OrganizationId * RepositoryId * BranchId
        | [<Id 5u>] Path of OwnerId * OrganizationId * RepositoryId * RelativePath

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<Resource>()

    /// Operation requested on a resource.
    [<KnownType("GetKnownTypes")>]
    type Operation =
        | SystemAdmin
        | SystemOperate
        | SystemRead
        | OwnerAdmin
        | OwnerWrite
        | OwnerRead
        | OrganizationAdmin
        | OrganizationWrite
        | OrganizationRead
        | RepositoryAdmin
        | RepositoryWrite
        | RepositoryRead
        | LibraryRead
        | LibraryWrite
        | BranchAdmin
        | BranchWrite
        | BranchRead
        | PathRead
        | PathWrite
        | ApprovalPolicyManage
        | ApprovalRequestRead
        | ApprovalRequestRespond
        | WebhookManage
        | WebhookDeliveryRead

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<Operation>()

        /// Every case name, in declaration order, so CLI option validation doesn't reflect over the union at startup.
        static member Names =
            [|
                nameof Operation.SystemAdmin
                nameof Operation.SystemOperate
                nameof Operation.SystemRead
                nameof Operation.OwnerAdmin
                nameof Operation.OwnerWrite
                nameof Operation.OwnerRead
                nameof Operation.OrganizationAdmin
                nameof Operation.OrganizationWrite
                nameof Operation.OrganizationRead
                nameof Operation.RepositoryAdmin
                nameof Operation.RepositoryWrite
                nameof Operation.RepositoryRead
                nameof Operation.LibraryRead
                nameof Operation.LibraryWrite
                nameof Operation.BranchAdmin
                nameof Operation.BranchWrite
                nameof Operation.BranchRead
                nameof Operation.PathRead
                nameof Operation.PathWrite
                nameof Operation.ApprovalPolicyManage
                nameof Operation.ApprovalRequestRead
                nameof Operation.ApprovalRequestRespond
                nameof Operation.WebhookManage
                nameof Operation.WebhookDeliveryRead
            |]

    /// Unique identifier for a role.
    type RoleId = string

    /// Defines a role's allowed operations and applicable scope kinds.
    [<GenerateSerializer>]
    type RoleDefinition =
        {
            [<Id 0u>]
            RoleId: RoleId
            [<Id 1u>]
            AllowedOperations: Set<Operation>
            [<Id 2u>]
            AppliesTo: Set<string>
        }

    /// Binds a principal to a role at a specific scope.
    [<GenerateSerializer>]
    type RoleAssignment =
        {
            [<Id 0u>]
            Principal: Principal
            [<Id 1u>]
            Scope: Scope
            [<Id 2u>]
            RoleId: RoleId
            [<Id 3u>]
            Source: string
            [<Id 4u>]
            SourceDetail: string option
            [<Id 5u>]
            CreatedAt: Instant
        }

    /// Result of an authorization check with a human-readable reason.
    [<KnownType("GetKnownTypes")>]
    type PermissionCheckResult =
        | [<Id 0u>] Allowed of string
        | [<Id 1u>] Denied of string

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<PermissionCheckResult>()

    /// Commands for managing role assignments.
    [<KnownType("GetKnownTypes")>]
    type AccessControlCommand =
        | [<Id 0u>] GrantRole of RoleAssignment
        | [<Id 1u>] RevokeRole of Principal * RoleId
        | [<Id 2u>] ListAssignments of Principal option

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<AccessControlCommand>()

    /// Commands for managing repository path permissions.
    [<KnownType("GetKnownTypes")>]
    type RepositoryPermissionCommand =
        | [<Id 0u>] UpsertPathPermission of PathPermission
        | [<Id 1u>] RemovePathPermission of RelativePath
        | [<Id 2u>] ListPathPermissions of RelativePath option

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<RepositoryPermissionCommand>()
