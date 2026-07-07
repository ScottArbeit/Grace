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
    let resource visibility ownership creator = { Visibility = visibility; Ownership = ownership; CreatorUserId = creator }

    /// Constructs an authenticated low-privilege audience.
    let caller userId = { VisibilityCallerAudience.Anonymous with UserId = Some userId }

    /// Verifies that authorized contributor and elevated audiences can observe contributor-owned private branches.
    [<Test>]
    member _.``canObserveBranch allows contributor reviewer maintainer and elevated administrators``() =
        let owner = userId "creator"
        let hiddenBranch = resource ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some owner)

        let creatorCaller = caller owner

        let reviewerCaller = { caller (userId "reviewer") with IsExplicitReviewer = true }

        let maintainerCaller = { caller (userId "maintainer") with IsExplicitMaintainer = true }

        let repositoryAdminCaller = { caller (userId "repo-admin") with HasRepositoryAdministration = true }

        let securityAdminCaller = { caller (userId "security-admin") with HasSecurityAdministration = true }

        Assert.That(canObserveBranch creatorCaller hiddenBranch, Is.True)
        Assert.That(canObserveBranch reviewerCaller hiddenBranch, Is.True)
        Assert.That(canObserveBranch maintainerCaller hiddenBranch, Is.True)
        Assert.That(canObserveBranch repositoryAdminCaller hiddenBranch, Is.True)
        Assert.That(canObserveBranch securityAdminCaller hiddenBranch, Is.True)

    /// Verifies that anonymous and low-privilege callers do not inherit elevated contributor-owned branch observability.
    [<Test>]
    member _.``canObserveBranch fails closed for anonymous and low privilege callers``() =
        let hiddenBranch = resource ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "creator"))

        Assert.That(canObserveBranch VisibilityCallerAudience.Anonymous hiddenBranch, Is.False)
        Assert.That(canObserveBranch (caller (userId "other-user")) hiddenBranch, Is.False)

    /// Verifies that hidden branches use the same route model that nonexistent branches use.
    [<Test>]
    member _.``requireObservableBranch returns route equivalent missing for hidden branch``() =
        let hiddenBranch = resource ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "creator"))

        let missingModel = { Missing = "branch-id-does-not-exist" }

        let hiddenResult = requireObservableBranch (caller (userId "other-user")) missingModel hiddenBranch

        match hiddenResult with
        | Observable _ -> Assert.Fail("Expected hidden branch to use the route-equivalent missing model.")
        | RouteEquivalentMissing routeModel -> Assert.That(routeModel, Is.EqualTo(missingModel))

    /// Verifies that filtering happens before public list limits and counts are calculated.
    [<Test>]
    member _.``filterVisibleWindow filters before limit and count``() =
        let hidden1 = resource ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "hidden-1"))
        let visible1 = resource ResourceVisibility.Public ResourceOwnership.ContributorOwned (Some(userId "visible-1"))
        let hidden2 = resource ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "hidden-2"))
        let visible2 = resource ResourceVisibility.Public ResourceOwnership.ContributorOwned (Some(userId "visible-2"))

        let window =
            [ hidden1; visible1; hidden2; visible2 ]
            |> filterVisibleWindow (Some 1) (canObserveBranch VisibilityCallerAudience.Anonymous)

        Assert.That(window.Items.Length, Is.EqualTo(1))
        Assert.That(window.Items.Head, Is.EqualTo(visible1))
        Assert.That(window.VisibleCount, Is.EqualTo(2))
        Assert.That(window.Latest, Is.EqualTo(Some visible1))
        Assert.That(window.HasMoreVisible, Is.True)

        let negativeLimitWindow =
            [ hidden1; visible1; visible2 ]
            |> filterVisibleWindow (Some -1) (canObserveBranch VisibilityCallerAudience.Anonymous)

        Assert.That(negativeLimitWindow.Items, Is.Empty)
        Assert.That(negativeLimitWindow.VisibleCount, Is.EqualTo(2))

    /// Verifies that count and latest helpers ignore hidden rows before reducing public output.
    [<Test>]
    member _.``countVisible and tryLatestVisible ignore hidden candidates``() =
        let hidden = resource ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "hidden"))
        let latestVisible = resource ResourceVisibility.Public ResourceOwnership.RepositoryOwned None
        let olderVisible = resource ResourceVisibility.Public ResourceOwnership.ContributorOwned (Some(userId "visible"))
        let canObserve = canObserveBranch VisibilityCallerAudience.Anonymous

        Assert.That(countVisible canObserve [ hidden; latestVisible; olderVisible ], Is.EqualTo(2))
        Assert.That(tryLatestVisible canObserve [ hidden; latestVisible; olderVisible ], Is.EqualTo(Some latestVisible))

    /// Verifies that projection gating and traversal seams do not leak hidden resources.
    [<Test>]
    member _.``projection and root traversal gates reject hidden resources``() =
        let hidden = resource ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "hidden"))
        let visible = resource ResourceVisibility.Public ResourceOwnership.RepositoryOwned None
        let canObserve = canObserveBranch VisibilityCallerAudience.Anonymous

        Assert.That(gateProjection canObserve (fun item -> item.Visibility.ToString()) hidden, Is.EqualTo(None))
        Assert.That(gateProjection canObserve (fun item -> item.Visibility.ToString()) visible, Is.EqualTo(Some "Public"))
        Assert.That(canTraverseRoot canObserve [ visible; hidden ], Is.False)
        Assert.That(canTraverseRoot canObserve [ visible ], Is.True)

    /// Verifies reference and promotion-set seams share the same fail-closed contributor-owned model.
    [<Test>]
    member _.``reference and promotion set helpers share branch visibility semantics``() =
        let hidden = resource ResourceVisibility.Private ResourceOwnership.ContributorOwned (Some(userId "creator"))
        let anonymous = VisibilityCallerAudience.Anonymous

        Assert.That(canObserveReference anonymous hidden, Is.False)
        Assert.That(canObservePromotionSet anonymous hidden, Is.False)
