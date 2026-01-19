namespace Grace.Authorization.Tests

open FsCheck
open Grace.Shared.Authorization
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Types
open NUnit.Framework
open System
open System.Reflection
open Microsoft.FSharp.Reflection

[<Parallelizable(ParallelScope.All)>]
type AuthorizationSemanticsTests() =

    let roleCatalog = RoleCatalog.getAll ()

    let principal = { PrincipalType = PrincipalType.User; PrincipalId = "user-1" }
    let otherPrincipal = { PrincipalType = PrincipalType.User; PrincipalId = "user-2" }

    let ownerId = Guid.Parse("11111111-1111-1111-1111-111111111111")
    let organizationId = Guid.Parse("22222222-2222-2222-2222-222222222222")
    let repositoryId = Guid.Parse("33333333-3333-3333-3333-333333333333")
    let branchId = Guid.Parse("44444444-4444-4444-4444-444444444444")

    let resources =
        [
            Resource.System
            Resource.Owner ownerId
            Resource.Organization(ownerId, organizationId)
            Resource.Repository(ownerId, organizationId, repositoryId)
            Resource.Branch(ownerId, organizationId, repositoryId, branchId)
            Resource.Path(ownerId, organizationId, repositoryId, "/docs/readme.md")
        ]

    let allOperations =
        FSharpType.GetUnionCases typeof<Operation>
        |> Array.map (fun caseInfo -> FSharpValue.MakeUnion(caseInfo, [||]) :?> Operation)
        |> Array.toList

    let createAssignment scope roleId =
        {
            Principal = principal
            Scope = scope
            RoleId = roleId
            Source = "test"
            SourceDetail = None
            CreatedAt = getCurrentInstant ()
        }

    let scopeKind scope =
        match scope with
        | Scope.System -> "system"
        | Scope.Owner _ -> "owner"
        | Scope.Organization _ -> "organization"
        | Scope.Repository _ -> "repository"
        | Scope.Branch _ -> "branch"

    let assertAllowed result =
        match result with
        | Allowed _ -> ()
        | Denied reason -> Assert.Fail($"Expected Allowed but got Denied: {reason}")

    let assertDenied result =
        match result with
        | Denied _ -> ()
        | Allowed reason -> Assert.Fail($"Expected Denied but got Allowed: {reason}")

    [<Test>]
    member _.RoleCatalogMatrixMatchesPermissionChecks() =
        for role in roleCatalog do
            for resource in resources do
                let scopes = scopesForResource resource

                for scope in scopes do
                    for operation in allOperations do
                        let assignments = [ createAssignment scope role.RoleId ]

                        let result =
                            checkPermission
                                roleCatalog
                                assignments
                                []
                                [ principal ]
                                Set.empty
                                operation
                                resource

                        let expectedAllowed =
                            role.AllowedOperations.Contains operation
                            && role.AppliesTo.Contains(scopeKind scope)

                        if expectedAllowed then
                            assertAllowed result
                        else
                            assertDenied result

    [<Test>]
    member _.RepoAdminIncludesBranchAdmin() =
        let repoAdmin =
            roleCatalog
            |> List.find (fun role -> role.RoleId.Equals("RepoAdmin", StringComparison.OrdinalIgnoreCase))

        Assert.That(repoAdmin.AllowedOperations.Contains BranchAdmin, Is.True)

    [<Test>]
    member _.IrrelevantAssignmentsDoNotAffectDecision() =
        let property (operation: Operation) =
            let scope = Scope.Repository(ownerId, organizationId, repositoryId)
            let resource = Resource.Repository(ownerId, organizationId, repositoryId)
            let assignment = createAssignment scope "RepoAdmin"
            let baseResult = checkPermission roleCatalog [ assignment ] [] [ principal ] Set.empty operation resource

            let extraAssignment =
                {
                    assignment with
                        Principal = otherPrincipal
                }

            let withExtra =
                checkPermission
                    roleCatalog
                    [ extraAssignment; assignment ]
                    []
                    [ principal ]
                    Set.empty
                    operation
                    resource

            baseResult = withExtra

        Check.QuickThrowOnFailure property

    [<Test>]
    member _.ScopeIrrelevanceDoesNotAffectDecision() =
        let property (operation: Operation) =
            let scope = Scope.Repository(ownerId, organizationId, repositoryId)
            let resource = Resource.Repository(ownerId, organizationId, repositoryId)
            let assignment = createAssignment scope "RepoAdmin"
            let baseResult = checkPermission roleCatalog [ assignment ] [] [ principal ] Set.empty operation resource

            let unrelatedAssignment =
                createAssignment (Scope.Branch(ownerId, organizationId, repositoryId, branchId)) "RepoAdmin"

            let withUnrelated =
                checkPermission
                    roleCatalog
                    [ unrelatedAssignment; assignment ]
                    []
                    [ principal ]
                    Set.empty
                    operation
                    resource

            baseResult = withUnrelated

        Check.QuickThrowOnFailure property

    [<Test>]
    member _.RoleIdCaseInsensitive() =
        let property (operation: Operation) =
            let scope = Scope.Repository(ownerId, organizationId, repositoryId)
            let resource = Resource.Repository(ownerId, organizationId, repositoryId)
            let assignment = createAssignment scope "RepoReader"
            let baseResult = checkPermission roleCatalog [ assignment ] [] [ principal ] Set.empty operation resource

            let mixedCaseAssignment = createAssignment scope "rEpOrEaDeR"

            let withMixedCase =
                checkPermission
                    roleCatalog
                    [ mixedCaseAssignment ]
                    []
                    [ principal ]
                    Set.empty
                    operation
                    resource

            baseResult = withMixedCase

        Check.QuickThrowOnFailure property

    [<Test>]
    member _.RbacMonotonicityForNonPathOps() =
        let property (operation: Operation) =
            match operation with
            | PathRead
            | PathWrite -> true
            | _ ->
                let scope = Scope.Repository(ownerId, organizationId, repositoryId)
                let resource = Resource.Repository(ownerId, organizationId, repositoryId)
                let baseAssignment = createAssignment scope "RepoReader"
                let extraAssignment = createAssignment scope "RepoAdmin"

                let baseResult =
                    checkPermission roleCatalog [ baseAssignment ] [] [ principal ] Set.empty operation resource

                let withExtra =
                    checkPermission
                        roleCatalog
                        [ extraAssignment; baseAssignment ]
                        []
                        [ principal ]
                        Set.empty
                        operation
                        resource

                match baseResult with
                | Allowed _ ->
                    match withExtra with
                    | Allowed _ -> true
                    | Denied _ -> false
                | Denied _ -> true

        Check.QuickThrowOnFailure property
