namespace Grace.Types

open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System.Runtime.Serialization

module Authorization =

    /// Internal principal identifier.
    type PrincipalId = string

    /// The type of principal represented in authorization decisions.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type PrincipalType =
        | User
        | Group
        | Service

        static member GetKnownTypes() = GetKnownTypes<PrincipalType>()

    /// Identifies a principal (user, group, or service).
    [<GenerateSerializer>]
    type Principal = { PrincipalType: PrincipalType; PrincipalId: PrincipalId }

    /// Scope for role assignments in the resource hierarchy.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type Scope =
        | System
        | Owner of ownerId: OwnerId
        | Organization of ownerId: OwnerId * organizationId: OrganizationId
        | Repository of ownerId: OwnerId * organizationId: OrganizationId * repositoryId: RepositoryId
        | Branch of ownerId: OwnerId * organizationId: OrganizationId * repositoryId: RepositoryId * branchId: BranchId

        static member GetKnownTypes() = GetKnownTypes<Scope>()

    /// Resource targeted by an authorization check.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type Resource =
        | System
        | Owner of ownerId: OwnerId
        | Organization of ownerId: OwnerId * organizationId: OrganizationId
        | Repository of ownerId: OwnerId * organizationId: OrganizationId * repositoryId: RepositoryId
        | Branch of ownerId: OwnerId * organizationId: OrganizationId * repositoryId: RepositoryId * branchId: BranchId
        | Path of ownerId: OwnerId * organizationId: OrganizationId * repositoryId: RepositoryId * relativePath: RelativePath

        static member GetKnownTypes() = GetKnownTypes<Resource>()

    /// Operation requested on a resource.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type Operation =
        | SystemAdmin
        | OwnerAdmin
        | OwnerRead
        | OrgAdmin
        | OrgRead
        | RepoAdmin
        | RepoRead
        | RepoWrite
        | BranchAdmin
        | BranchRead
        | BranchWrite
        | PathRead
        | PathWrite

        static member GetKnownTypes() = GetKnownTypes<Operation>()

    /// Unique identifier for a role.
    type RoleId = string

    /// Defines a role's allowed operations and applicable scope kinds.
    [<GenerateSerializer>]
    type RoleDefinition =
        { RoleId: RoleId
          AllowedOperations: Set<Operation>
          AppliesTo: Set<string> }

    /// Binds a principal to a role at a specific scope.
    [<GenerateSerializer>]
    type RoleAssignment =
        { Principal: Principal
          Scope: Scope
          RoleId: RoleId
          Source: string
          SourceDetail: string option
          CreatedAt: Instant }

    /// Result of an authorization check with a human-readable reason.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type PermissionCheckResult =
        | Allowed of reason: string
        | Denied of reason: string

        static member GetKnownTypes() = GetKnownTypes<PermissionCheckResult>()

    /// Commands for managing role assignments.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type AccessControlCommand =
        | GrantRole of RoleAssignment
        | RevokeRole of Principal * RoleId
        | ListAssignments of Principal option

        static member GetKnownTypes() = GetKnownTypes<AccessControlCommand>()

    /// Commands for managing repository path permissions.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type RepositoryPermissionCommand =
        | UpsertPathPermission of PathPermission
        | RemovePathPermission of RelativePath
        | ListPathPermissions of RelativePath option

        static member GetKnownTypes() = GetKnownTypes<RepositoryPermissionCommand>()
