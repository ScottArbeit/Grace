namespace Grace.Authorization.Tests

open Grace.Server.Security
open Grace.Shared.Authorization
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Common
open NUnit.Framework
open System
open System.Threading.Tasks

/// Contains tests covering permission evaluator behavior.
[<Parallelizable(ParallelScope.All)>]
type PermissionEvaluatorTests() =

    let principal = { PrincipalType = PrincipalType.User; PrincipalId = "user-1" }
    let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
    let branchId = Guid.Parse("44444444-4444-4444-4444-444444444444")

    /// Builds a deterministic assignment fixture for the authorization permission Evaluator assertions.
    let createAssignment scope roleId =
        { Principal = principal; Scope = scope; RoleId = roleId; Source = "test"; SourceDetail = None; CreatedAt = getCurrentInstant () }

    /// Exercises empty path permissions coverage for the authorization permission Evaluator contract.
    let emptyPathPermissions (_repositoryId: RepositoryId, _correlationId: CorrelationId) = Task.FromResult([])

    /// Verifies that queries scopes for resource.
    [<Test>]
    member _.QueriesScopesForResource() =
        task {
            let capturedScopes = ResizeArray<Scope>()

            /// Gets assignments for scope.
            let getAssignmentsForScope (scope: Scope, _correlationId: CorrelationId) =
                capturedScopes.Add scope
                Task.FromResult([])

            let evaluator = GracePermissionEvaluator(getAssignmentsForScope, emptyPathPermissions)
            let resource = Resource.Repository(ownerId, organizationId, repositoryId)

            let! _ =
                (evaluator :> IGracePermissionEvaluator)
                    .CheckAsync([ principal ], Set.empty, Operation.RepositoryRead, resource)

            let expected = scopesForResource resource
            Assert.That(capturedScopes, Is.EquivalentTo(expected))
        }

    /// Verifies that unions assignments across scopes.
    [<Test>]
    member _.UnionsAssignmentsAcrossScopes() =
        task {
            /// Gets assignments for scope.
            let getAssignmentsForScope (scope: Scope, _correlationId: CorrelationId) =
                match scope with
                | Scope.Organization _ ->
                    Task.FromResult(
                        [
                            createAssignment scope "OrganizationAdmin"
                        ]
                    )
                | _ -> Task.FromResult([])

            let evaluator = GracePermissionEvaluator(getAssignmentsForScope, emptyPathPermissions)
            let resource = Resource.Repository(ownerId, organizationId, repositoryId)

            let! result =
                (evaluator :> IGracePermissionEvaluator)
                    .CheckAsync([ principal ], Set.empty, Operation.RepositoryWrite, resource)

            match result with
            | Allowed _ -> ()
            | Denied reason -> Assert.Fail($"Expected Allowed but got Denied: {reason}")
        }

    /// Verifies that fetches path permissions only for path resources.
    [<Test>]
    member _.FetchesPathPermissionsOnlyForPathResources() =
        task {
            /// Tracks path Permission Calls changes so this scenario can assert the resulting side effect explicitly.
            let mutable pathPermissionCalls = 0

            /// Gets path permissions.
            let getPathPermissions (_repositoryId: RepositoryId, _correlationId: CorrelationId) =
                pathPermissionCalls <- pathPermissionCalls + 1
                Task.FromResult([])

            let evaluator = GracePermissionEvaluator((fun _ -> Task.FromResult([])), getPathPermissions)

            let repositoryResource = Resource.Repository(ownerId, organizationId, repositoryId)

            let! _ =
                (evaluator :> IGracePermissionEvaluator)
                    .CheckAsync([ principal ], Set.empty, Operation.RepositoryRead, repositoryResource)

            Assert.That(pathPermissionCalls, Is.EqualTo(0))

            let pathResource = Resource.Path(ownerId, organizationId, repositoryId, "/docs/readme.md")

            let! _ =
                (evaluator :> IGracePermissionEvaluator)
                    .CheckAsync([ principal ], Set.empty, Operation.PathRead, pathResource)

            Assert.That(pathPermissionCalls, Is.EqualTo(1))
        }

    /// Verifies that fails closed when role missing.
    [<Test>]
    member _.FailsClosedWhenRoleMissing() =
        task {
            /// Gets assignments for scope.
            let getAssignmentsForScope (scope: Scope, _correlationId: CorrelationId) = Task.FromResult([ createAssignment scope "MissingRole" ])

            let evaluator = GracePermissionEvaluator(getAssignmentsForScope, emptyPathPermissions)
            let resource = Resource.Repository(ownerId, organizationId, repositoryId)

            let! result =
                (evaluator :> IGracePermissionEvaluator)
                    .CheckAsync([ principal ], Set.empty, Operation.RepositoryRead, resource)

            match result with
            | Denied _ -> ()
            | Allowed reason -> Assert.Fail($"Expected Denied but got Allowed: {reason}")
        }
