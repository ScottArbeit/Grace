namespace Grace.Authorization.Tests

open Grace.Server.Security
open Grace.Shared.Authorization
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Types
open NUnit.Framework
open System
open System.Threading.Tasks

[<Parallelizable(ParallelScope.All)>]
type PermissionEvaluatorTests() =

    let principal = { PrincipalType = PrincipalType.User; PrincipalId = "user-1" }
    let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
    let branchId = Guid.Parse("44444444-4444-4444-4444-444444444444")

    let createAssignment scope roleId =
        {
            Principal = principal
            Scope = scope
            RoleId = roleId
            Source = "test"
            SourceDetail = None
            CreatedAt = getCurrentInstant ()
        }

    let emptyPathPermissions (_repositoryId: RepositoryId, _correlationId: CorrelationId) =
        Task.FromResult([ ])

    [<Test>]
    member _.QueriesScopesForResource() =
        task {
            let capturedScopes = ResizeArray<Scope>()

            let getAssignmentsForScope (scope: Scope, _correlationId: CorrelationId) =
                capturedScopes.Add scope
                Task.FromResult([ ])

            let evaluator = GracePermissionEvaluator(getAssignmentsForScope, emptyPathPermissions)
            let resource = Resource.Repository(ownerId, organizationId, repositoryId)
            let! _ = (evaluator :> IGracePermissionEvaluator).CheckAsync([ principal ], Set.empty, Operation.RepoRead, resource)

            let expected = scopesForResource resource
            Assert.That(capturedScopes, Is.EquivalentTo(expected))
        }

    [<Test>]
    member _.UnionsAssignmentsAcrossScopes() =
        task {
            let getAssignmentsForScope (scope: Scope, _correlationId: CorrelationId) =
                match scope with
                | Scope.Organization _ -> Task.FromResult([ createAssignment scope "OrgAdmin" ])
                | _ -> Task.FromResult([ ])

            let evaluator = GracePermissionEvaluator(getAssignmentsForScope, emptyPathPermissions)
            let resource = Resource.Repository(ownerId, organizationId, repositoryId)
            let! result = (evaluator :> IGracePermissionEvaluator).CheckAsync([ principal ], Set.empty, Operation.RepoWrite, resource)

            match result with
            | Allowed _ -> ()
            | Denied reason -> Assert.Fail($"Expected Allowed but got Denied: {reason}")
        }

    [<Test>]
    member _.FetchesPathPermissionsOnlyForPathResources() =
        task {
            let mutable pathPermissionCalls = 0

            let getPathPermissions (_repositoryId: RepositoryId, _correlationId: CorrelationId) =
                pathPermissionCalls <- pathPermissionCalls + 1
                Task.FromResult([ ])

            let evaluator =
                GracePermissionEvaluator(
                    (fun _ -> Task.FromResult([ ])),
                    getPathPermissions
                )

            let repositoryResource = Resource.Repository(ownerId, organizationId, repositoryId)
            let! _ = (evaluator :> IGracePermissionEvaluator).CheckAsync([ principal ], Set.empty, Operation.RepoRead, repositoryResource)

            Assert.That(pathPermissionCalls, Is.EqualTo(0))

            let pathResource = Resource.Path(ownerId, organizationId, repositoryId, "/docs/readme.md")
            let! _ = (evaluator :> IGracePermissionEvaluator).CheckAsync([ principal ], Set.empty, Operation.PathRead, pathResource)

            Assert.That(pathPermissionCalls, Is.EqualTo(1))
        }

    [<Test>]
    member _.FailsClosedWhenRoleMissing() =
        task {
            let getAssignmentsForScope (scope: Scope, _correlationId: CorrelationId) =
                Task.FromResult([ createAssignment scope "MissingRole" ])

            let evaluator = GracePermissionEvaluator(getAssignmentsForScope, emptyPathPermissions)
            let resource = Resource.Repository(ownerId, organizationId, repositoryId)
            let! result = (evaluator :> IGracePermissionEvaluator).CheckAsync([ principal ], Set.empty, Operation.RepoRead, resource)

            match result with
            | Denied _ -> ()
            | Allowed reason -> Assert.Fail($"Expected Denied but got Allowed: {reason}")
        }
