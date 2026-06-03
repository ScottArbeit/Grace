namespace Grace.Server.Tests

open Grace.Shared.Authorization
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Types
open NUnit.Framework
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type AuthorizationUnit() =

    let roleCatalog = RoleCatalog.getAll ()

    let createAssignment principal scope roleId =
        { Principal = principal; Scope = scope; RoleId = roleId; Source = "test"; SourceDetail = None; CreatedAt = getCurrentInstant () }

    let assertAllowed result =
        match result with
        | Allowed _ -> ()
        | Denied reason -> Assert.Fail($"Expected Allowed but got Denied: {reason}")

    let assertDenied result =
        match result with
        | Denied _ -> ()
        | Allowed reason -> Assert.Fail($"Expected Denied but got Allowed: {reason}")

    [<Test>]
    member _.RoleInheritanceAllowsRepoWriteFromOrgAdmin() =
        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()

        let principal = { PrincipalType = PrincipalType.User; PrincipalId = "user-1" }

        let assignments =
            [
                createAssignment principal (Scope.Organization(ownerId, organizationId)) "OrgAdmin"
            ]

        let result =
            checkPermission roleCatalog assignments [] [ principal ] Set.empty Operation.RepoWrite (Resource.Repository(ownerId, organizationId, repositoryId))

        assertAllowed result

    [<Test>]
    member _.PathPermissionDenyOverridesRoleAllow() =
        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()

        let principal = { PrincipalType = PrincipalType.User; PrincipalId = "user-2" }

        let assignments =
            [
                createAssignment principal (Scope.Organization(ownerId, organizationId)) "OrgAdmin"
            ]

        let denyPermissions = List<ClaimPermission>()
        denyPermissions.Add({ Claim = "engineering"; DirectoryPermission = DirectoryPermission.NoAccess })

        let pathPermissions =
            [
                { Path = "/images"; Permissions = denyPermissions }
            ]

        let result =
            checkPermission
                roleCatalog
                assignments
                pathPermissions
                [ principal ]
                (Set.ofList [ "engineering" ])
                Operation.PathWrite
                (Resource.Path(ownerId, organizationId, repositoryId, "/images"))

        assertDenied result

    [<Test>]
    member _.PathPermissionAllowOverridesRoleDeny() =
        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()

        let principal = { PrincipalType = PrincipalType.User; PrincipalId = "user-3" }

        let assignments =
            [
                createAssignment principal (Scope.Organization(ownerId, organizationId)) "OrgReader"
            ]

        let allowPermissions = List<ClaimPermission>()
        allowPermissions.Add({ Claim = "engineering"; DirectoryPermission = DirectoryPermission.Modify })

        let pathPermissions =
            [
                { Path = "/images"; Permissions = allowPermissions }
            ]

        let result =
            checkPermission
                roleCatalog
                assignments
                pathPermissions
                [ principal ]
                (Set.ofList [ "engineering" ])
                Operation.PathWrite
                (Resource.Path(ownerId, organizationId, repositoryId, "/images"))

        assertAllowed result

    [<Test>]
    member _.NoPermissionsDenied() =
        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()

        let principal = { PrincipalType = PrincipalType.User; PrincipalId = "user-4" }

        let result = checkPermission roleCatalog [] [] [ principal ] Set.empty Operation.RepoRead (Resource.Repository(ownerId, organizationId, repositoryId))

        assertDenied result

    [<Test>]
    member _.GroupPrincipalAssignmentsApply() =
        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()

        let userPrincipal = { PrincipalType = PrincipalType.User; PrincipalId = "user-5" }
        let groupPrincipal = { PrincipalType = PrincipalType.Group; PrincipalId = "group-1" }

        let assignments =
            [
                createAssignment groupPrincipal (Scope.Repository(ownerId, organizationId, repositoryId)) "RepoReader"
            ]

        let result =
            checkPermission
                roleCatalog
                assignments
                []
                [ userPrincipal; groupPrincipal ]
                Set.empty
                Operation.RepoRead
                (Resource.Repository(ownerId, organizationId, repositoryId))

        assertAllowed result

    [<Test>]
    member _.RepoAdminIncludesBranchAdmin() =
        let repoAdmin =
            roleCatalog
            |> List.find (fun role -> role.RoleId.Equals("RepoAdmin", StringComparison.OrdinalIgnoreCase))

        Assert.That(repoAdmin.AllowedOperations.Contains Operation.BranchAdmin, Is.True)
