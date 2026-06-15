namespace Grace.Server.Tests

open Grace.Server.Security
open Grace.Shared.Authorization
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Common
open NUnit.Framework
open System
open System.Threading.Tasks

[<Parallelizable(ParallelScope.All)>]
type CreatorScopeAdminGrantUnit() =

    let roleCatalog = RoleCatalog.getAll ()

    let userPrincipal userId = { PrincipalType = PrincipalType.User; PrincipalId = userId }

    let groupPrincipal groupId = { PrincipalType = PrincipalType.Group; PrincipalId = groupId }

    let assignment (principal: Principal) (scope: Scope) (roleId: RoleId) : RoleAssignment =
        { Principal = principal; Scope = scope; RoleId = roleId; Source = "test"; SourceDetail = None; CreatedAt = getCurrentInstant () }

    let runEnsure (creatorUserId: string option) (principals: Principal list) (initialAssignments: RoleAssignment list) (scope: Scope) =
        task {
            let grants = ResizeArray<RoleAssignment>()
            let mutable assignments: RoleAssignment list = initialAssignments

            let checkAdmin principals effectiveClaims operation resource =
                Task.FromResult(checkPermission roleCatalog assignments [] principals effectiveClaims operation resource)

            let grantCreatorAdmin (grant: RoleAssignment) =
                task {
                    grants.Add grant
                    assignments <- grant :: assignments
                    return Ok(GraceReturnValue.Create assignments "test-correlation")
                }

            let! result = CreatorScopeAdminGrant.ensureCreatorAdminCore "test-correlation" creatorUserId principals Set.empty scope checkAdmin grantCreatorAdmin

            return result, Seq.toList grants
        }

    let assertSingleGrant (expectedPrincipal: Principal) (expectedScope: Scope) (expectedRole: RoleId) (grants: RoleAssignment list) =
        Assert.That(grants.Length, Is.EqualTo(1))
        let grant = grants.Head
        Assert.That(grant.Principal, Is.EqualTo(expectedPrincipal))
        Assert.That(grant.Scope, Is.EqualTo(expectedScope))
        Assert.That(grant.RoleId, Is.EqualTo(expectedRole))
        Assert.That(grant.Source, Is.EqualTo("scope-create"))
        Assert.That(grant.SourceDetail, Is.EqualTo(Some "creator-admin"))

    let assertOk (expected: CreatorScopeAdminGrant.CreatorAdminGrantResult) (result: Result<CreatorScopeAdminGrant.CreatorAdminGrantResult, GraceError>) =
        match result with
        | Ok actual -> Assert.That(actual, Is.EqualTo(expected))
        | Error error -> Assert.Fail($"Expected Ok {expected} but got Error: {error.Error}")

    [<Test>]
    member _.OwnerCreationGrantsOwnerAdminToCreatorUserWhenNoInheritedAdminApplies() =
        task {
            let ownerId = Guid.NewGuid()
            let creator = userPrincipal "creator-user"
            let scope = Scope.Owner ownerId

            let! result, grants = runEnsure (Some creator.PrincipalId) [ creator ] [] scope

            assertOk CreatorScopeAdminGrant.DirectGrantPersisted result
            assertSingleGrant creator scope "OwnerAdmin" grants
        }

    [<Test>]
    member _.OrganizationCreationByOwnerContributorGrantsOrganizationAdminToCreatorUser() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let creator = userPrincipal "creator-user"
            let ownerScope = Scope.Owner ownerId
            let organizationScope = Scope.Organization(ownerId, organizationId)

            let initialAssignments =
                [
                    assignment creator ownerScope "OwnerContributor"
                ]

            let! result, grants = runEnsure (Some creator.PrincipalId) [ creator ] initialAssignments organizationScope

            assertOk CreatorScopeAdminGrant.DirectGrantPersisted result
            assertSingleGrant creator organizationScope "OrganizationAdmin" grants
        }

    [<Test>]
    member _.RepositoryCreationByOrganizationContributorGrantsRepositoryAdminToCreatorUser() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let creator = userPrincipal "creator-user"
            let organizationScope = Scope.Organization(ownerId, organizationId)
            let repositoryScope = Scope.Repository(ownerId, organizationId, repositoryId)

            let initialAssignments =
                [
                    assignment creator organizationScope "OrganizationContributor"
                ]

            let! result, grants = runEnsure (Some creator.PrincipalId) [ creator ] initialAssignments repositoryScope

            assertOk CreatorScopeAdminGrant.DirectGrantPersisted result
            assertSingleGrant creator repositoryScope "RepositoryAdmin" grants
        }

    [<Test>]
    member _.BranchCreationByRepositoryContributorGrantsBranchAdminToCreatorUser() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let repositoryId = Guid.NewGuid()
            let branchId = Guid.NewGuid()
            let creator = userPrincipal "creator-user"
            let repositoryScope = Scope.Repository(ownerId, organizationId, repositoryId)
            let branchScope = Scope.Branch(ownerId, organizationId, repositoryId, branchId)

            let initialAssignments =
                [
                    assignment creator repositoryScope "RepositoryContributor"
                ]

            let! result, grants = runEnsure (Some creator.PrincipalId) [ creator ] initialAssignments branchScope

            assertOk CreatorScopeAdminGrant.DirectGrantPersisted result
            assertSingleGrant creator branchScope "BranchAdmin" grants
        }

    [<Test>]
    member _.InheritedAdminDoesNotCreateRedundantDirectGrant() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let creator = userPrincipal "creator-user"
            let ownerScope = Scope.Owner ownerId
            let organizationScope = Scope.Organization(ownerId, organizationId)

            let initialAssignments =
                [
                    assignment creator ownerScope "OwnerAdmin"
                ]

            let! result, grants = runEnsure (Some creator.PrincipalId) [ creator ] initialAssignments organizationScope

            assertOk CreatorScopeAdminGrant.InheritedAdminAlreadyApplies result
            Assert.That(grants, Is.Empty)
        }

    [<Test>]
    member _.GroupAuthorizedCreationGrantsOnlyTheCreatingUserPrincipal() =
        task {
            let ownerId = Guid.NewGuid()
            let organizationId = Guid.NewGuid()
            let creator = userPrincipal "creator-user"
            let group = groupPrincipal "owner-contributors"
            let ownerScope = Scope.Owner ownerId
            let organizationScope = Scope.Organization(ownerId, organizationId)

            let initialAssignments =
                [
                    assignment group ownerScope "OwnerContributor"
                ]

            let! result, grants = runEnsure (Some creator.PrincipalId) [ creator; group ] initialAssignments organizationScope

            assertOk CreatorScopeAdminGrant.DirectGrantPersisted result
            assertSingleGrant creator organizationScope "OrganizationAdmin" grants
        }

    [<Test>]
    member _.GrantPersistenceFailurePreventsSuccess() =
        task {
            let ownerId = Guid.NewGuid()
            let creator = userPrincipal "creator-user"
            let scope = Scope.Owner ownerId
            let checkAdmin _ _ _ _ = Task.FromResult(Denied "missing admin")
            let grantCreatorAdmin _ = Task.FromResult(Error(GraceError.Create "grant failed" "test-correlation"))

            let! result =
                CreatorScopeAdminGrant.ensureCreatorAdminCore
                    "test-correlation"
                    (Some creator.PrincipalId)
                    [ creator ]
                    Set.empty
                    scope
                    checkAdmin
                    grantCreatorAdmin

            match result with
            | Error error -> Assert.That(error.Error, Is.EqualTo("grant failed"))
            | Ok value -> Assert.Fail($"Expected grant failure but got {value}.")
        }

    [<Test>]
    member _.UnprovenGrantPreventsSuccess() =
        task {
            let ownerId = Guid.NewGuid()
            let creator = userPrincipal "creator-user"
            let scope = Scope.Owner ownerId
            let checkAdmin _ _ _ _ = Task.FromResult(Denied "missing admin")
            let grantCreatorAdmin _ = Task.FromResult(Ok(GraceReturnValue.Create [] "test-correlation"))

            let! result =
                CreatorScopeAdminGrant.ensureCreatorAdminCore
                    "test-correlation"
                    (Some creator.PrincipalId)
                    [ creator ]
                    Set.empty
                    scope
                    checkAdmin
                    grantCreatorAdmin

            match result with
            | Error error -> Assert.That(error.Error, Does.Contain("could not be proven"))
            | Ok value -> Assert.Fail($"Expected unproven grant failure but got {value}.")
        }

    [<Test>]
    member _.MissingMappedCreatorUserPreventsSuccess() =
        task {
            let ownerId = Guid.NewGuid()
            let scope = Scope.Owner ownerId
            let checkAdmin _ _ _ _ = Task.FromResult(Denied "missing admin")
            let grantCreatorAdmin _ = Task.FromResult(Ok(GraceReturnValue.Create [] "test-correlation"))

            let! result = CreatorScopeAdminGrant.ensureCreatorAdminCore "test-correlation" None [] Set.empty scope checkAdmin grantCreatorAdmin

            match result with
            | Error error -> Assert.That(error.Error, Does.Contain("no mapped Grace user id"))
            | Ok value -> Assert.Fail($"Expected missing user failure but got {value}.")
        }
