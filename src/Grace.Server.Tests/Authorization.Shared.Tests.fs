namespace Grace.Server.Tests

open Grace.Shared.Authorization
open Grace.Types.Access
open Grace.Types.Types
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
type AuthorizationSharedTests() =

    let ownerId = Guid.NewGuid()
    let organizationId = Guid.NewGuid()
    let repositoryId = Guid.NewGuid()

    let principal = Principal.User "user-1"

    let buildAssignment scope roleId =
        { Principal = principal
          Scope = scope
          RoleId = roleId
          Source = "test"
          SourceDetail = None
          CreatedAt = NodaTime.SystemClock.Instance.GetCurrentInstant() }

    [<Test>]
    member _.``OrgAdmin implies RepoWrite``() =
        let authState = { Roles = roleCatalog; Assignments = [ buildAssignment (ScopeOrg(ownerId, organizationId)) "OrgAdmin" ]; PathAcls = [] }

        let result = checkPermission authState principal Operation.RepoWrite (Resource.Repo(ownerId, organizationId, repositoryId))

        match result with
        | Allowed _ -> Assert.Pass()
        | Denied reason -> Assert.Fail($"Expected allowed; got denied: {reason}")

    [<Test>]
    member _.``Path ACL deny overrides role allow``() =
        let authState =
            { Roles = roleCatalog
              Assignments = [ buildAssignment (ScopeRepo(ownerId, organizationId, repositoryId)) "RepoContributor" ]
              PathAcls =
                [ { Principal = principal
                    OwnerId = ownerId
                    OrganizationId = organizationId
                    RepositoryId = repositoryId
                    Path = "/images"
                    Access = Access.Deny
                    Permissions = [ PathPermWrite ]
                    CreatedAt = NodaTime.SystemClock.Instance.GetCurrentInstant() } ] }

        let result = checkPermission authState principal Operation.PathWrite (Resource.Path(ownerId, organizationId, repositoryId, "/images"))

        match result with
        | Denied _ -> Assert.Pass()
        | Allowed _ -> Assert.Fail("Expected denied due to path ACL deny.")

    [<Test>]
    member _.``No roles yields denied``() =
        let authState = { Roles = roleCatalog; Assignments = []; PathAcls = [] }
        let result = checkPermission authState principal Operation.RepoRead (Resource.Repo(ownerId, organizationId, repositoryId))

        match result with
        | Denied _ -> Assert.Pass()
        | Allowed _ -> Assert.Fail("Expected denied when no role assignments exist.")

    [<Test>]
    member _.``Multiple roles union operations``() =
        let authState =
            { Roles = roleCatalog
              Assignments =
                [ buildAssignment (ScopeOrg(ownerId, organizationId)) "OrgReader"
                  buildAssignment (ScopeRepo(ownerId, organizationId, repositoryId)) "RepoContributor" ]
              PathAcls = [] }

        let ops = effectiveOperations authState principal (Resource.Repo(ownerId, organizationId, repositoryId))

        Assert.That(ops.Contains Operation.RepoWrite, Is.True)
        Assert.That(ops.Contains Operation.OrgRead, Is.True)

    [<Test>]
    member _.``Path normalization is consistent``() =
        let normalizedA = normalizeRepoPath "images/"
        let normalizedB = normalizeRepoPath "\\Images\\"

        Assert.That(normalizedA, Is.EqualTo(normalizedB))
        Assert.That(normalizedA.StartsWith("/"), Is.True)
        Assert.That(normalizedA.EndsWith("/"), Is.False)
