namespace Grace.Server.Tests

open Grace.Shared.Authorization
open Grace.Shared.Parameters.Access
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Common
open NUnit.Framework
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type AuthorizationUnit() =

    let roleCatalog = RoleCatalog.getAll ()

    let createAssignment principal scope roleId =
        { Principal = principal; Scope = scope; RoleId = roleId; Source = "test"; SourceDetail = None; CreatedAt = getCurrentInstant () }

    let createRepositoryParameters ownerId organizationId repositoryId =
        let parameters = AccessParameters()
        parameters.OwnerId <- string ownerId
        parameters.OrganizationId <- string organizationId
        parameters.RepositoryId <- string repositoryId
        parameters

    let assertAllowed result =
        match result with
        | Allowed _ -> ()
        | Denied reason -> Assert.Fail($"Expected Allowed but got Denied: {reason}")

    let assertDenied result =
        match result with
        | Denied _ -> ()
        | Allowed reason -> Assert.Fail($"Expected Denied but got Allowed: {reason}")

    [<Test>]
    member _.RoleInheritanceAllowsRepositoryWriteFromOrganizationAdmin() =
        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()

        let principal = { PrincipalType = PrincipalType.User; PrincipalId = "user-1" }

        let assignments =
            [
                createAssignment principal (Scope.Organization(ownerId, organizationId)) "OrganizationAdmin"
            ]

        let result =
            checkPermission
                roleCatalog
                assignments
                []
                [ principal ]
                Set.empty
                Operation.RepositoryWrite
                (Resource.Repository(ownerId, organizationId, repositoryId))

        assertAllowed result

    [<Test>]
    member _.PathPermissionDenyOverridesRoleAllow() =
        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()

        let principal = { PrincipalType = PrincipalType.User; PrincipalId = "user-2" }

        let assignments =
            [
                createAssignment principal (Scope.Organization(ownerId, organizationId)) "OrganizationAdmin"
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
                createAssignment principal (Scope.Organization(ownerId, organizationId)) "OrganizationReader"
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

        let result =
            checkPermission roleCatalog [] [] [ principal ] Set.empty Operation.RepositoryRead (Resource.Repository(ownerId, organizationId, repositoryId))

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
                createAssignment groupPrincipal (Scope.Repository(ownerId, organizationId, repositoryId)) "RepositoryReader"
            ]

        let result =
            checkPermission
                roleCatalog
                assignments
                []
                [ userPrincipal; groupPrincipal ]
                Set.empty
                Operation.RepositoryRead
                (Resource.Repository(ownerId, organizationId, repositoryId))

        assertAllowed result

    [<Test>]
    member _.RepositoryAdminIncludesBranchAdmin() =
        let repoAdmin =
            roleCatalog
            |> List.find (fun role -> role.RoleId.Equals("RepositoryAdmin", StringComparison.OrdinalIgnoreCase))

        Assert.That(repoAdmin.AllowedOperations.Contains Operation.BranchAdmin, Is.True)

    [<Test>]
    member _.RevokeScopeParsingAllowsExplicitScopeForNonCatalogRoleIds() =
        let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
        let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
        let repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
        let parameters = createRepositoryParameters ownerId organizationId repositoryId

        let result = Grace.Server.Access.parseRevokeRoleScope "DeletedRole" "repository" parameters (string (Guid.NewGuid()))

        match result with
        | Ok (Scope.Repository (parsedOwnerId, parsedOrganizationId, parsedRepositoryId)) ->
            Assert.That(parsedOwnerId, Is.EqualTo(ownerId))
            Assert.That(parsedOrganizationId, Is.EqualTo(organizationId))
            Assert.That(parsedRepositoryId, Is.EqualTo(repositoryId))
        | Ok scope -> Assert.Fail($"Expected repository scope but got {scope}.")
        | Error error -> Assert.Fail($"Expected stale role revocation scope to parse but got {error.Error}.")

    [<Test>]
    member _.RevokeScopeParsingRequiresExplicitScopeForNonCatalogRoleIds() =
        let ownerId = Guid.NewGuid()
        let organizationId = Guid.NewGuid()
        let repositoryId = Guid.NewGuid()
        let parameters = createRepositoryParameters ownerId organizationId repositoryId

        let result = Grace.Server.Access.parseRevokeRoleScope "DeletedRole" String.Empty parameters (string (Guid.NewGuid()))

        match result with
        | Error error -> Assert.That(error.Error, Is.EqualTo("ScopeKind is required when revoking an unknown RoleId."))
        | Ok scope -> Assert.Fail($"Expected missing scope kind to fail but got {scope}.")

    [<Test>]
    member _.RevokeScopeParsingStillUsesCatalogScopeInferenceForCurrentRoleIds() =
        let ownerId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        let organizationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
        let repositoryId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")
        let parameters = createRepositoryParameters ownerId organizationId repositoryId

        let inferredResult = Grace.Server.Access.parseRevokeRoleScope "RepositoryReader" String.Empty parameters (string (Guid.NewGuid()))

        match inferredResult with
        | Ok (Scope.Repository (parsedOwnerId, parsedOrganizationId, parsedRepositoryId)) ->
            Assert.That(parsedOwnerId, Is.EqualTo(ownerId))
            Assert.That(parsedOrganizationId, Is.EqualTo(organizationId))
            Assert.That(parsedRepositoryId, Is.EqualTo(repositoryId))
        | Ok scope -> Assert.Fail($"Expected repository scope but got {scope}.")
        | Error error -> Assert.Fail($"Expected catalog role revocation scope to parse but got {error.Error}.")

        let conflictingResult = Grace.Server.Access.parseRevokeRoleScope "RepositoryReader" "branch" parameters (string (Guid.NewGuid()))

        match conflictingResult with
        | Error error -> Assert.That(error.Error, Is.EqualTo("ScopeKind 'branch' conflicts with role 'RepositoryReader' assignment scope 'repository'."))
        | Ok scope -> Assert.Fail($"Expected conflicting scope kind to fail but got {scope}.")
