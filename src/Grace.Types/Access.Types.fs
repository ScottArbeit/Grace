namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Access =

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type Principal =
        | User of string
        | Group of string
        | Service of string

        static member GetKnownTypes() = GetKnownTypes<Principal>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type Operation =
        | OwnerAdmin
        | OrgAdmin
        | OrgRead
        | RepoAdmin
        | RepoRead
        | RepoWrite
        | BranchRead
        | BranchWrite
        | BranchAdmin
        | PathRead
        | PathWrite

        static member GetKnownTypes() = GetKnownTypes<Operation>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type Resource =
        | System
        | Owner of OwnerId
        | Org of OwnerId * OrganizationId
        | Repo of OwnerId * OrganizationId * RepositoryId
        | Branch of OwnerId * OrganizationId * RepositoryId * BranchId
        | Path of OwnerId * OrganizationId * RepositoryId * string

        static member GetKnownTypes() = GetKnownTypes<Resource>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type Scope =
        | ScopeSystem
        | ScopeOwner of OwnerId
        | ScopeOrg of OwnerId * OrganizationId
        | ScopeRepo of OwnerId * OrganizationId * RepositoryId
        | ScopeBranch of OwnerId * OrganizationId * RepositoryId * BranchId

        static member GetKnownTypes() = GetKnownTypes<Scope>()

    [<GenerateSerializer>]
    type RoleDefinition = { RoleId: string; Operations: Operation list }

    [<GenerateSerializer>]
    type RoleAssignment = { Principal: Principal; Scope: Scope; RoleId: string; Source: string; SourceDetail: string option; CreatedAt: Instant }

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type Access =
        | Allow
        | Deny

        static member GetKnownTypes() = GetKnownTypes<Access>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type PathPermission =
        | PathPermRead
        | PathPermWrite

        static member GetKnownTypes() = GetKnownTypes<PathPermission>()

    [<GenerateSerializer>]
    type PathAce =
        { Principal: Principal
          OwnerId: OwnerId
          OrganizationId: OrganizationId
          RepositoryId: RepositoryId
          Path: string
          Access: Access
          Permissions: PathPermission list
          CreatedAt: Instant }

    [<GenerateSerializer>]
    type AuthState = { Roles: RoleDefinition list; Assignments: RoleAssignment list; PathAcls: PathAce list }

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type PermissionCheckResult =
        | Allowed of string
        | Denied of string

        static member GetKnownTypes() = GetKnownTypes<PermissionCheckResult>()

module Identity =

    [<GenerateSerializer>]
    type ExternalIdentity =
        { Provider: string
          Subject: string
          TenantId: string option
          Email: string option
          DisplayName: string option
          LastSeenAt: Instant
          RawClaims: string option }

    [<GenerateSerializer>]
    type UserState =
        { UserId: UserId
          PrimaryDisplayName: string
          PrimaryEmail: string option
          IsDisabled: bool
          CreatedAt: Instant
          ExternalIdentities: ExternalIdentity list }

    [<GenerateSerializer>]
    type ExternalIdentityIndexEntry = { Provider: string; Subject: string; TenantId: string option; UserId: UserId }
