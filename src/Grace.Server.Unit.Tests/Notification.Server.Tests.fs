namespace Grace.Server.Tests

open Grace.Server.Notification
open Grace.Types.Common
open NUnit.Framework

/// Covers notification Server behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type NotificationServerTests() =

    /// Verifies that branch Name Glob Matching Is Case Insensitive And Supports Wildcard.
    [<TestCase("main", "main", true)>]
    [<TestCase("MAIN", "main", true)>]
    [<TestCase("release/2026.02", "release/*", true)>]
    [<TestCase("feature/promo-set", "feature/*", true)>]
    [<TestCase("main", "*", true)>]
    [<TestCase("release", "main", false)>]
    [<TestCase("feature/promo", "release/*", false)>]
    member _.BranchNameGlobMatchingIsCaseInsensitiveAndSupportsWildcard(branchName: string, glob: string, expected: bool) =
        let actual = Subscriber.matchesBranchGlob (BranchName branchName) glob
        Assert.That(actual, Is.EqualTo(expected))

    /// Verifies that SignalR reference notification fanout is suppressed for currently hidden references.
    [<Test>]
    member _.ReferenceNotificationProjectionSuppressesCurrentHiddenReference() = Assert.That(Subscriber.shouldNotifyReferenceProjection (Some false), Is.False)

    /// Verifies that SignalR reference notification fanout remains enabled when no current source override is available.
    [<Test>]
    member _.ReferenceNotificationProjectionAllowsUnscopedLegacyReference() = Assert.That(Subscriber.shouldNotifyReferenceProjection None, Is.True)
