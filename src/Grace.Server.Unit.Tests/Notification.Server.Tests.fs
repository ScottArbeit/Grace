namespace Grace.Server.Tests

open Grace.Server.Notification
open Grace.Types.Common
open Grace.Types.Reference
open Grace.Types.Visibility
open NodaTime
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

    /// Verifies that deleted references are treated as hidden for public projection fanout.
    [<Test>]
    member _.ReferenceProjectionSuppressesDeletedPublicReference() =
        let referenceDto =
            { ReferenceDto.Default with
                ReferenceId = System.Guid.NewGuid()
                Visibility = ResourceVisibility.Public
                Ownership = ResourceOwnership.RepositoryOwned
                DeletedAt = Some(Instant.FromUtc(2026, 7, 9, 10, 0))
            }

        Assert.That(Subscriber.referenceDtoAllowsPublicProjection referenceDto, Is.False)

    /// Verifies that derived quick-scan fanout observes the current reference projection decision.
    [<Test>]
    member _.DerivedReferenceProjectionSuppressesCurrentHiddenReference() =
        Assert.That(Subscriber.shouldRecordDerivedReferenceProjection (Some false), Is.False)

    /// Verifies that derived quick-scan fanout remains available when no current reference override is available.
    [<Test>]
    member _.DerivedReferenceProjectionAllowsUnscopedReference() = Assert.That(Subscriber.shouldRecordDerivedReferenceProjection None, Is.True)
