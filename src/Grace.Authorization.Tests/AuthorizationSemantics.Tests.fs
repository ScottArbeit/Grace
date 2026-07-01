namespace Grace.Authorization.Tests

open FsCheck
open Grace.Shared.Authorization
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Common
open NUnit.Framework
open System
open System.Reflection
open Microsoft.FSharp.Reflection

/// Contains tests covering authorization semantics behavior.
[<Parallelizable(ParallelScope.All)>]
type AuthorizationSemanticsTests() =

    let roleCatalog = RoleCatalog.getAll ()

    let principal = { PrincipalType = PrincipalType.User; PrincipalId = "user-1" }
    let otherPrincipal = { PrincipalType = PrincipalType.User; PrincipalId = "user-2" }
    let groupPrincipal = { PrincipalType = PrincipalType.Group; PrincipalId = "group-1" }

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

    /// Builds a deterministic assignment fixture for the authorization authorization Semantics assertions.
    let createAssignment scope roleId =
        { Principal = principal; Scope = scope; RoleId = roleId; Source = "test"; SourceDetail = None; CreatedAt = getCurrentInstant () }

    /// Exercises scope kind coverage for the authorization authorization Semantics contract.
    let scopeKind scope =
        match scope with
        | Scope.System -> "system"
        | Scope.Owner _ -> "owner"
        | Scope.Organization _ -> "organization"
        | Scope.Repository _ -> "repository"
        | Scope.Branch _ -> "branch"

    /// Asserts allowed.
    let assertAllowed result =
        match result with
        | Allowed _ -> ()
        | Denied reason -> Assert.Fail($"Expected Allowed but got Denied: {reason}")

    /// Asserts denied.
    let assertDenied result =
        match result with
        | Denied _ -> ()
        | Allowed reason -> Assert.Fail($"Expected Denied but got Allowed: {reason}")

    /// Asserts operation allowed.
    let assertOperationAllowed roleId scope operation resource =
        let result = checkPermission roleCatalog [ createAssignment scope roleId ] [] [ principal ] Set.empty operation resource

        assertAllowed result

    /// Asserts operation denied.
    let assertOperationDenied roleId scope operation resource =
        let result = checkPermission roleCatalog [ createAssignment scope roleId ] [] [ principal ] Set.empty operation resource

        assertDenied result

    /// Verifies that role catalog contains only canonical roles.
    [<Test>]
    member _.RoleCatalogContainsOnlyCanonicalRoles() =
        let expectedRoleIds =
            [
                "SystemAdmin"
                "SystemOperator"
                "SystemReader"
                "OwnerAdmin"
                "OwnerContributor"
                "OwnerReader"
                "OrganizationAdmin"
                "OrganizationContributor"
                "OrganizationReader"
                "RepositoryAdmin"
                "RepositoryContributor"
                "RepositoryReader"
                "BranchAdmin"
                "BranchWriter"
                "BranchReader"
                "RepositoryApprovalResponder"
                "BranchApprovalResponder"
            ]

        let actualRoleIds = roleCatalog |> List.map (fun role -> role.RoleId)

        Assert.That(actualRoleIds, Is.EquivalentTo expectedRoleIds)
        Assert.That(actualRoleIds |> List.length, Is.EqualTo(expectedRoleIds.Length))

    /// Verifies that old abbreviated role ids are unknown.
    [<Test>]
    member _.OldAbbreviatedRoleIdsAreUnknown() =
        let oldRoleIds =
            [
                "OrgAdmin"
                "OrgReader"
                "RepoAdmin"
                "RepoContributor"
                "RepoReader"
                "ApprovalResponder"
                "rEpOrEaDeR"
                "oRgAdMiN"
            ]

        for roleId in oldRoleIds do
            Assert.That(RoleCatalog.tryGet roleId |> Option.isNone, Is.True, $"Old role ID '{roleId}' should not be accepted.")

    /// Verifies that legacy approval responder assignments grant only approval response operations.
    [<Test>]
    member _.LegacyApprovalResponderAssignmentsGrantOnlyApprovalResponseOperations() =
        let repositoryScope = Scope.Repository(ownerId, organizationId, repositoryId)
        let repositoryResource = Resource.Repository(ownerId, organizationId, repositoryId)
        let branchScope = Scope.Branch(ownerId, organizationId, repositoryId, branchId)
        let branchResource = Resource.Branch(ownerId, organizationId, repositoryId, branchId)
        let ownerScope = Scope.Owner ownerId
        let ownerResource = Resource.Owner ownerId

        let legacyRepositoryAssignment = createAssignment repositoryScope "ApprovalResponder"
        let legacyBranchAssignment = createAssignment branchScope "ApprovalResponder"
        let legacyOwnerAssignment = createAssignment ownerScope "ApprovalResponder"

        let repositoryRead = checkPermission roleCatalog [ legacyRepositoryAssignment ] [] [ principal ] Set.empty ApprovalRequestRead repositoryResource

        let repositoryRespond = checkPermission roleCatalog [ legacyRepositoryAssignment ] [] [ principal ] Set.empty ApprovalRequestRespond repositoryResource

        let repositoryPolicyManage =
            checkPermission roleCatalog [ legacyRepositoryAssignment ] [] [ principal ] Set.empty ApprovalPolicyManage repositoryResource

        let repositoryWrite = checkPermission roleCatalog [ legacyRepositoryAssignment ] [] [ principal ] Set.empty RepositoryWrite repositoryResource

        let branchRespond = checkPermission roleCatalog [ legacyBranchAssignment ] [] [ principal ] Set.empty ApprovalRequestRespond branchResource

        let ownerRespond = checkPermission roleCatalog [ legacyOwnerAssignment ] [] [ principal ] Set.empty ApprovalRequestRespond ownerResource

        assertAllowed repositoryRead
        assertAllowed repositoryRespond
        assertDenied repositoryPolicyManage
        assertDenied repositoryWrite
        assertAllowed branchRespond
        assertDenied ownerRespond

    /// Verifies that role catalog matrix matches permission checks.
    [<Test>]
    member _.RoleCatalogMatrixMatchesPermissionChecks() =
        for role in roleCatalog do
            for resource in resources do
                let scopes = scopesForResource resource

                for scope in scopes do
                    for operation in allOperations do
                        let assignments = [ createAssignment scope role.RoleId ]

                        let result = checkPermission roleCatalog assignments [] [ principal ] Set.empty operation resource

                        let expectedAllowed =
                            role.AllowedOperations.Contains operation
                            && role.AppliesTo.Contains(scopeKind scope)

                        if expectedAllowed then assertAllowed result else assertDenied result

    /// Verifies that repository admin includes branch admin.
    [<Test>]
    member _.RepositoryAdminIncludesBranchAdmin() =
        let repositoryAdmin =
            roleCatalog
            |> List.find (fun role -> role.RoleId.Equals("RepositoryAdmin", StringComparison.OrdinalIgnoreCase))

        Assert.That(repositoryAdmin.AllowedOperations.Contains BranchAdmin, Is.True)

    /// Verifies that every role maps to exactly one assignment scope.
    [<Test>]
    member _.EveryRoleMapsToExactlyOneAssignmentScope() =
        for role in roleCatalog do
            Assert.That(role.AppliesTo.Count, Is.EqualTo(1), $"Role '{role.RoleId}' must map to exactly one assignment scope.")

            RoleCatalog.tryGetSingleScopeKind role.RoleId
            |> Option.isSome
            |> fun hasScope -> Assert.That(hasScope, Is.True, $"Role '{role.RoleId}' should expose a derived assignment scope.")

    /// Verifies that self assignment queries use only effective caller principals across scope ladder.
    [<Test>]
    member _.SelfAssignmentQueriesUseOnlyEffectiveCallerPrincipalsAcrossScopeLadder() =
        let resource = Resource.Repository(ownerId, organizationId, repositoryId)

        let queries = Grace.Server.Access.selfAssignmentQueriesForResource [ principal; groupPrincipal; principal ] resource

        let expectedScopes = scopesForResource resource
        let actualScopes = queries |> List.map fst |> List.distinct
        Assert.That((actualScopes = expectedScopes), Is.True)

        let actualPrincipals = queries |> List.map snd |> Set.ofList
        Assert.That((actualPrincipals = Set.ofList [ principal; groupPrincipal ]), Is.True)

        queries
        |> List.exists (fun (_, queryPrincipal) -> queryPrincipal = otherPrincipal)
        |> fun containsOtherPrincipal -> Assert.That(containsOtherPrincipal, Is.False)

        Assert.That(queries.Length, Is.EqualTo(expectedScopes.Length * 2))

    /// Verifies that approval operations use conservative role grants.
    [<Test>]
    member _.ApprovalOperationsUseConservativeRoleGrants() =
        let repositoryAdmin =
            roleCatalog
            |> List.find (fun role -> role.RoleId.Equals("RepositoryAdmin", StringComparison.OrdinalIgnoreCase))

        let repositoryReader =
            roleCatalog
            |> List.find (fun role -> role.RoleId.Equals("RepositoryReader", StringComparison.OrdinalIgnoreCase))

        let repositoryContributor =
            roleCatalog
            |> List.find (fun role -> role.RoleId.Equals("RepositoryContributor", StringComparison.OrdinalIgnoreCase))

        let repositoryResponder =
            roleCatalog
            |> List.find (fun role -> role.RoleId.Equals("RepositoryApprovalResponder", StringComparison.OrdinalIgnoreCase))

        let branchResponder =
            roleCatalog
            |> List.find (fun role -> role.RoleId.Equals("BranchApprovalResponder", StringComparison.OrdinalIgnoreCase))

        Assert.That(repositoryAdmin.AllowedOperations.Contains ApprovalPolicyManage, Is.True)
        Assert.That(repositoryAdmin.AllowedOperations.Contains ApprovalRequestRead, Is.True)
        Assert.That(repositoryAdmin.AllowedOperations.Contains ApprovalRequestRespond, Is.True)
        Assert.That(repositoryAdmin.AllowedOperations.Contains WebhookManage, Is.True)
        Assert.That(repositoryAdmin.AllowedOperations.Contains WebhookDeliveryRead, Is.True)

        Assert.That(repositoryReader.AllowedOperations.Contains ApprovalRequestRead, Is.True)
        Assert.That(repositoryReader.AllowedOperations.Contains ApprovalRequestRespond, Is.False)
        Assert.That(repositoryContributor.AllowedOperations.Contains ApprovalRequestRespond, Is.False)

        Assert.That(repositoryResponder.AppliesTo, Is.EquivalentTo([ "repository" ]))
        Assert.That(repositoryResponder.AllowedOperations.Count, Is.EqualTo(2))
        Assert.That(repositoryResponder.AllowedOperations.Contains ApprovalRequestRead, Is.True)
        Assert.That(repositoryResponder.AllowedOperations.Contains ApprovalRequestRespond, Is.True)

        Assert.That(branchResponder.AppliesTo, Is.EquivalentTo([ "branch" ]))
        Assert.That(branchResponder.AllowedOperations.Count, Is.EqualTo(2))
        Assert.That(branchResponder.AllowedOperations.Contains ApprovalRequestRead, Is.True)
        Assert.That(branchResponder.AllowedOperations.Contains ApprovalRequestRespond, Is.True)

    /// Verifies that system operator has descendant admin write read but no system admin.
    [<Test>]
    member _.SystemOperatorHasDescendantAdminWriteReadButNoSystemAdmin() =
        let systemScope = Scope.System
        let ownerResource = Resource.Owner ownerId
        let organizationResource = Resource.Organization(ownerId, organizationId)
        let repositoryResource = Resource.Repository(ownerId, organizationId, repositoryId)
        let branchResource = Resource.Branch(ownerId, organizationId, repositoryId, branchId)
        let pathResource = Resource.Path(ownerId, organizationId, repositoryId, "/docs/readme.md")

        assertOperationAllowed "SystemOperator" systemScope SystemOperate Resource.System
        assertOperationAllowed "SystemOperator" systemScope SystemRead Resource.System
        assertOperationDenied "SystemOperator" systemScope SystemAdmin Resource.System

        assertOperationAllowed "SystemOperator" systemScope OwnerAdmin ownerResource
        assertOperationAllowed "SystemOperator" systemScope OwnerWrite ownerResource
        assertOperationAllowed "SystemOperator" systemScope OwnerRead ownerResource
        assertOperationAllowed "SystemOperator" systemScope OrganizationAdmin organizationResource
        assertOperationAllowed "SystemOperator" systemScope OrganizationWrite organizationResource
        assertOperationAllowed "SystemOperator" systemScope OrganizationRead organizationResource
        assertOperationAllowed "SystemOperator" systemScope RepositoryAdmin repositoryResource
        assertOperationAllowed "SystemOperator" systemScope RepositoryWrite repositoryResource
        assertOperationAllowed "SystemOperator" systemScope RepositoryRead repositoryResource
        assertOperationAllowed "SystemOperator" systemScope BranchAdmin branchResource
        assertOperationAllowed "SystemOperator" systemScope BranchWrite branchResource
        assertOperationAllowed "SystemOperator" systemScope BranchRead branchResource
        assertOperationAllowed "SystemOperator" systemScope PathWrite pathResource
        assertOperationAllowed "SystemOperator" systemScope PathRead pathResource

    /// Verifies that system reader has only system wide read coverage.
    [<Test>]
    member _.SystemReaderHasOnlySystemWideReadCoverage() =
        let systemScope = Scope.System
        let ownerResource = Resource.Owner ownerId
        let organizationResource = Resource.Organization(ownerId, organizationId)
        let repositoryResource = Resource.Repository(ownerId, organizationId, repositoryId)
        let branchResource = Resource.Branch(ownerId, organizationId, repositoryId, branchId)
        let pathResource = Resource.Path(ownerId, organizationId, repositoryId, "/docs/readme.md")

        assertOperationAllowed "SystemReader" systemScope SystemRead Resource.System
        assertOperationDenied "SystemReader" systemScope SystemOperate Resource.System
        assertOperationDenied "SystemReader" systemScope SystemAdmin Resource.System
        assertOperationAllowed "SystemReader" systemScope OwnerRead ownerResource
        assertOperationAllowed "SystemReader" systemScope OrganizationRead organizationResource
        assertOperationAllowed "SystemReader" systemScope RepositoryRead repositoryResource
        assertOperationAllowed "SystemReader" systemScope BranchRead branchResource
        assertOperationAllowed "SystemReader" systemScope PathRead pathResource
        assertOperationDenied "SystemReader" systemScope OwnerWrite ownerResource
        assertOperationDenied "SystemReader" systemScope OrganizationWrite organizationResource
        assertOperationDenied "SystemReader" systemScope RepositoryWrite repositoryResource
        assertOperationDenied "SystemReader" systemScope BranchWrite branchResource
        assertOperationDenied "SystemReader" systemScope PathWrite pathResource
        assertOperationDenied "SystemReader" systemScope OwnerAdmin ownerResource
        assertOperationDenied "SystemReader" systemScope OrganizationAdmin organizationResource
        assertOperationDenied "SystemReader" systemScope RepositoryAdmin repositoryResource
        assertOperationDenied "SystemReader" systemScope BranchAdmin branchResource

    /// Verifies that contributor roles write descendants without admin authority.
    [<Test>]
    member _.ContributorRolesWriteDescendantsWithoutAdminAuthority() =
        let ownerScope = Scope.Owner ownerId
        let organizationScope = Scope.Organization(ownerId, organizationId)
        let repositoryScope = Scope.Repository(ownerId, organizationId, repositoryId)
        let ownerResource = Resource.Owner ownerId
        let organizationResource = Resource.Organization(ownerId, organizationId)
        let repositoryResource = Resource.Repository(ownerId, organizationId, repositoryId)
        let branchResource = Resource.Branch(ownerId, organizationId, repositoryId, branchId)
        let pathResource = Resource.Path(ownerId, organizationId, repositoryId, "/docs/readme.md")

        assertOperationAllowed "OwnerContributor" ownerScope OwnerWrite ownerResource
        assertOperationAllowed "OwnerContributor" ownerScope OrganizationWrite organizationResource
        assertOperationAllowed "OwnerContributor" ownerScope RepositoryWrite repositoryResource
        assertOperationAllowed "OwnerContributor" ownerScope BranchWrite branchResource
        assertOperationAllowed "OwnerContributor" ownerScope PathWrite pathResource
        assertOperationDenied "OwnerContributor" ownerScope OwnerAdmin ownerResource
        assertOperationDenied "OwnerContributor" ownerScope OrganizationAdmin organizationResource
        assertOperationDenied "OwnerContributor" ownerScope RepositoryAdmin repositoryResource
        assertOperationDenied "OwnerContributor" ownerScope BranchAdmin branchResource

        assertOperationAllowed "OrganizationContributor" organizationScope OrganizationWrite organizationResource
        assertOperationAllowed "OrganizationContributor" organizationScope RepositoryWrite repositoryResource
        assertOperationAllowed "OrganizationContributor" organizationScope BranchWrite branchResource
        assertOperationAllowed "OrganizationContributor" organizationScope PathWrite pathResource
        assertOperationDenied "OrganizationContributor" organizationScope OrganizationAdmin organizationResource
        assertOperationDenied "OrganizationContributor" organizationScope RepositoryAdmin repositoryResource
        assertOperationDenied "OrganizationContributor" organizationScope BranchAdmin branchResource

        assertOperationAllowed "RepositoryContributor" repositoryScope RepositoryWrite repositoryResource
        assertOperationAllowed "RepositoryContributor" repositoryScope BranchWrite branchResource
        assertOperationAllowed "RepositoryContributor" repositoryScope PathWrite pathResource
        assertOperationDenied "RepositoryContributor" repositoryScope RepositoryAdmin repositoryResource
        assertOperationDenied "RepositoryContributor" repositoryScope BranchAdmin branchResource

    /// Verifies that irrelevant assignments do not affect decision.
    [<Test>]
    member _.IrrelevantAssignmentsDoNotAffectDecision() =
        /// Defines the property assertion used to explore generated inputs for the authorization authorization Semantics invariant.
        let property (operation: Operation) =
            let scope = Scope.Repository(ownerId, organizationId, repositoryId)
            let resource = Resource.Repository(ownerId, organizationId, repositoryId)
            let assignment = createAssignment scope "RepositoryAdmin"
            let baseResult = checkPermission roleCatalog [ assignment ] [] [ principal ] Set.empty operation resource

            let extraAssignment = { assignment with Principal = otherPrincipal }

            let withExtra = checkPermission roleCatalog [ extraAssignment; assignment ] [] [ principal ] Set.empty operation resource

            baseResult = withExtra

        Check.QuickThrowOnFailure property

    /// Verifies that scope irrelevance does not affect decision.
    [<Test>]
    member _.ScopeIrrelevanceDoesNotAffectDecision() =
        /// Defines the property assertion used to explore generated inputs for the authorization authorization Semantics invariant.
        let property (operation: Operation) =
            let scope = Scope.Repository(ownerId, organizationId, repositoryId)
            let resource = Resource.Repository(ownerId, organizationId, repositoryId)
            let assignment = createAssignment scope "RepositoryAdmin"
            let baseResult = checkPermission roleCatalog [ assignment ] [] [ principal ] Set.empty operation resource

            let unrelatedAssignment = createAssignment (Scope.Branch(ownerId, organizationId, repositoryId, branchId)) "RepositoryAdmin"

            let withUnrelated = checkPermission roleCatalog [ unrelatedAssignment; assignment ] [] [ principal ] Set.empty operation resource

            baseResult = withUnrelated

        Check.QuickThrowOnFailure property

    /// Verifies that role id case insensitive.
    [<Test>]
    member _.RoleIdCaseInsensitive() =
        /// Defines the property assertion used to explore generated inputs for the authorization authorization Semantics invariant.
        let property (operation: Operation) =
            let scope = Scope.Repository(ownerId, organizationId, repositoryId)
            let resource = Resource.Repository(ownerId, organizationId, repositoryId)
            let assignment = createAssignment scope "RepositoryReader"
            let baseResult = checkPermission roleCatalog [ assignment ] [] [ principal ] Set.empty operation resource

            let mixedCaseAssignment = createAssignment scope "rEpOsItOrYrEaDeR"

            let withMixedCase = checkPermission roleCatalog [ mixedCaseAssignment ] [] [ principal ] Set.empty operation resource

            baseResult = withMixedCase

        Check.QuickThrowOnFailure property

    /// Verifies that rbac monotonicity for non path ops.
    [<Test>]
    member _.RbacMonotonicityForNonPathOps() =
        /// Defines the property assertion used to explore generated inputs for the authorization authorization Semantics invariant.
        let property (operation: Operation) =
            match operation with
            | PathRead
            | PathWrite -> true
            | _ ->
                let scope = Scope.Repository(ownerId, organizationId, repositoryId)
                let resource = Resource.Repository(ownerId, organizationId, repositoryId)
                let baseAssignment = createAssignment scope "RepositoryReader"
                let extraAssignment = createAssignment scope "RepositoryAdmin"

                let baseResult = checkPermission roleCatalog [ baseAssignment ] [] [ principal ] Set.empty operation resource

                let withExtra = checkPermission roleCatalog [ extraAssignment; baseAssignment ] [] [ principal ] Set.empty operation resource

                match baseResult with
                | Allowed _ ->
                    match withExtra with
                    | Allowed _ -> true
                    | Denied _ -> false
                | Denied _ -> true

        Check.QuickThrowOnFailure property
