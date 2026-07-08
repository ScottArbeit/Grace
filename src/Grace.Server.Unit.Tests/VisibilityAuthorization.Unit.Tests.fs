namespace Grace.Server.Unit.Tests

open Grace.Server.Security
open Grace.Server.Security.VisibilityAuthorization
open Grace.Types.Common
open Grace.Types.Visibility
open NUnit.Framework
open System

/// Covers server-side visibility helper behavior without HTTP hosting or Aspire resources.
[<Parallelizable(ParallelScope.All)>]
type VisibilityAuthorizationUnitTests() =

    /// Constructs a test user id from stable text so failed assertions are easy to read.
    let userId (value: string) = UserId value

    /// Constructs visibility resource facts used by the helper assertions.
    let resource key visibility ownership creator = { AuthorityKey = key; Visibility = visibility; Ownership = ownership; CreatorUserId = creator }

    /// Constructs an authenticated low-privilege audience.
    let caller userId = { VisibilityCallerAudience.Anonymous with UserId = Some userId }

    /// Resolves no resource-scoped reviewer, maintainer, or branch-admin authority for a candidate.
    let noResourceAudience _ = VisibilityResourceAudience.None

    /// Verifies that authorized contributor and elevated audiences can observe contributor-owned private branches.
    [<Test>]
    member _.``canObserveBranch allows contributor branch admin reviewer maintainer and elevated administrators``() =
        let owner = userId "creator"
        let hiddenBranch = resource "branch-1" ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some owner)

        let creatorCaller = caller owner

        let reviewerAudience _ = { VisibilityResourceAudience.None with IsExplicitReviewer = true }

        let maintainerAudience _ = { VisibilityResourceAudience.None with IsExplicitMaintainer = true }

        let branchAdminAudience _ = { VisibilityResourceAudience.None with HasBranchAdministration = true }

        let repositoryAdminCaller = { caller (userId "repo-admin") with HasRepositoryAdministration = true }

        let securityAdminCaller = { caller (userId "security-admin") with HasSecurityAdministration = true }

        Assert.That(canObserveBranch creatorCaller noResourceAudience hiddenBranch, Is.True)
        Assert.That(canObserveBranch (caller (userId "branch-admin")) branchAdminAudience hiddenBranch, Is.True)
        Assert.That(canObserveBranch (caller (userId "reviewer")) reviewerAudience hiddenBranch, Is.True)
        Assert.That(canObserveBranch (caller (userId "maintainer")) maintainerAudience hiddenBranch, Is.True)
        Assert.That(canObserveBranch repositoryAdminCaller noResourceAudience hiddenBranch, Is.True)
        Assert.That(canObserveBranch securityAdminCaller noResourceAudience hiddenBranch, Is.True)

    /// Verifies that anonymous and low-privilege callers do not inherit elevated contributor-owned branch observability.
    [<Test>]
    member _.``canObserveBranch fails closed for anonymous and low privilege callers``() =
        let hiddenBranch = resource "branch-1" ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "creator"))

        Assert.That(canObserveBranch VisibilityCallerAudience.Anonymous noResourceAudience hiddenBranch, Is.False)
        Assert.That(canObserveBranch (caller (userId "other-user")) noResourceAudience hiddenBranch, Is.False)

    /// Verifies that branch administration authority is limited to branch-owned reference read surfaces.
    [<Test>]
    member _.``canObserveBranchReference allows branch administration while generic helpers ignore it``() =
        let hiddenResource = resource "reference-1" ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "creator"))
        let branchAdminAudience _ = { VisibilityResourceAudience.None with HasBranchAdministration = true }

        let branchAdminCaller = caller (userId "branch-admin")

        Assert.That(canObserveBranchReference branchAdminCaller branchAdminAudience hiddenResource, Is.True)
        Assert.That(canObserveReference branchAdminCaller branchAdminAudience hiddenResource, Is.False)
        Assert.That(canObservePromotionSet branchAdminCaller branchAdminAudience hiddenResource, Is.False)

    /// Verifies that hidden branches use the same route model that nonexistent branches use.
    [<Test>]
    member _.``requireObservableBranch returns route equivalent missing for hidden branch``() =
        let hiddenBranch = resource "branch-1" ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "creator"))

        let missingModel = { Missing = "branch-id-does-not-exist" }

        let hiddenResult = requireObservableBranch (caller (userId "other-user")) noResourceAudience missingModel hiddenBranch

        match hiddenResult with
        | Observable _ -> Assert.Fail("Expected hidden branch to use the route-equivalent missing model.")
        | RouteEquivalentMissing routeModel -> Assert.That(routeModel, Is.EqualTo(missingModel))

    /// Verifies that reviewer and maintainer authority is resolved for each candidate before list shaping.
    [<Test>]
    member _.``filterVisibleWindow scopes reviewer and maintainer authority per resource``() =
        let reviewed = resource "reviewed" ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "reviewed-owner"))
        let maintained = resource "maintained" ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "maintained-owner"))
        let unrelated = resource "unrelated" ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "unrelated-owner"))

        let resourceAudience candidate =
            match candidate.AuthorityKey with
            | "reviewed" -> { VisibilityResourceAudience.None with IsExplicitReviewer = true }
            | "maintained" -> { VisibilityResourceAudience.None with IsExplicitMaintainer = true }
            | _ -> VisibilityResourceAudience.None

        let window =
            [ reviewed; unrelated; maintained ]
            |> filterVisibleWindow None (canObserveBranch (caller (userId "resource-audience")) resourceAudience)

        Assert.That(window.Items.Length, Is.EqualTo(2))
        Assert.That(window.Items[0], Is.EqualTo(reviewed))
        Assert.That(window.Items[1], Is.EqualTo(maintained))
        Assert.That(window.VisibleCount, Is.EqualTo(2))
        Assert.That(window.Latest, Is.EqualTo(Some reviewed))
        Assert.That(window.HasMoreVisible, Is.False)

    /// Verifies that filtering happens before public list limits and counts are calculated.
    [<Test>]
    member _.``filterVisibleWindow filters before limit and count``() =
        let hidden1 = resource "hidden-1" ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "hidden-1"))
        let visible1 = resource "visible-1" ResourceVisibility.Public ResourceOwnership.ContributorOwned (Some(userId "visible-1"))
        let hidden2 = resource "hidden-2" ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "hidden-2"))
        let visible2 = resource "visible-2" ResourceVisibility.Public ResourceOwnership.ContributorOwned (Some(userId "visible-2"))

        let window =
            [ hidden1; visible1; hidden2; visible2 ]
            |> filterVisibleWindow (Some 1) (canObserveBranch VisibilityCallerAudience.Anonymous noResourceAudience)

        Assert.That(window.Items.Length, Is.EqualTo(1))
        Assert.That(window.Items.Head, Is.EqualTo(visible1))
        Assert.That(window.VisibleCount, Is.EqualTo(2))
        Assert.That(window.Latest, Is.EqualTo(Some visible1))
        Assert.That(window.HasMoreVisible, Is.True)

        let negativeLimitWindow =
            [ hidden1; visible1; visible2 ]
            |> filterVisibleWindow (Some -1) (canObserveBranch VisibilityCallerAudience.Anonymous noResourceAudience)

        Assert.That(negativeLimitWindow.Items, Is.Empty)
        Assert.That(negativeLimitWindow.VisibleCount, Is.EqualTo(2))

    /// Verifies that count and latest helpers ignore hidden rows before reducing public output.
    [<Test>]
    member _.``countVisible and tryLatestVisible ignore hidden candidates``() =
        let hidden = resource "hidden" ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "hidden"))
        let latestVisible = resource "latest-visible" ResourceVisibility.Public ResourceOwnership.RepositoryOwned None
        let olderVisible = resource "older-visible" ResourceVisibility.Public ResourceOwnership.ContributorOwned (Some(userId "visible"))
        let canObserve = canObserveBranch VisibilityCallerAudience.Anonymous noResourceAudience

        Assert.That(countVisible canObserve [ hidden; latestVisible; olderVisible ], Is.EqualTo(2))
        Assert.That(tryLatestVisible canObserve [ hidden; latestVisible; olderVisible ], Is.EqualTo(Some latestVisible))

    /// Verifies that projection gating and traversal seams do not leak hidden resources.
    [<Test>]
    member _.``projection and root traversal gates reject hidden resources``() =
        let hidden = resource "hidden" ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "hidden"))
        let visible = resource "visible" ResourceVisibility.Public ResourceOwnership.RepositoryOwned None
        let canObserve = canObserveBranch VisibilityCallerAudience.Anonymous noResourceAudience

        Assert.That(gateProjection canObserve (fun item -> item.Visibility.ToString()) hidden, Is.EqualTo(None))
        Assert.That(gateProjection canObserve (fun item -> item.Visibility.ToString()) visible, Is.EqualTo(Some "Public"))
        Assert.That(canTraverseRoot canObserve [ visible; hidden ], Is.False)
        Assert.That(canTraverseRoot canObserve [ visible ], Is.True)

    /// Verifies reference and promotion-set seams share the same fail-closed contributor-owned model.
    [<Test>]
    member _.``reference and promotion set helpers share branch visibility semantics``() =
        let hidden = resource "resource-1" ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "creator"))
        let anonymous = VisibilityCallerAudience.Anonymous

        Assert.That(canObserveReference anonymous noResourceAudience hidden, Is.False)
        Assert.That(canObservePromotionSet anonymous noResourceAudience hidden, Is.False)
