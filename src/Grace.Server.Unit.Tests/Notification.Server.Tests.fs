namespace Grace.Server.Tests

open Grace.Server.Notification
open Grace.Types.Common
open Grace.Types.Reference
open NUnit.Framework
open System

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

    /// Verifies that current branch SignalR group keys cannot collide with legacy raw GUID groups.
    [<Test>]
    member _.CurrentBranchGroupKeyIsDistinctFromRepositoryAndParentBranchGroups() =
        let repositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
        let branchId = Guid.Parse("22222222-2222-2222-2222-222222222222")
        let groupKey = currentBranchGroupKey repositoryId branchId

        Assert.Multiple(
            Action (fun () ->
                Assert.That(groupKey, Is.EqualTo("current-branch:11111111111111111111111111111111:22222222222222222222222222222222"))
                Assert.That(groupKey, Is.Not.EqualTo($"{repositoryId}"))
                Assert.That(groupKey, Is.Not.EqualTo($"{branchId}")))
        )

    /// Verifies that current-branch Reference notifications are restricted to materializable branch history events.
    [<Test>]
    member _.CurrentBranchReferenceEligibilityExcludesPromotionTagExternalAndRebase() =
        let eligible =
            [|
                ReferenceType.Commit
                ReferenceType.Checkpoint
                ReferenceType.Save
            |]

        let ineligible =
            [|
                ReferenceType.Promotion
                ReferenceType.Tag
                ReferenceType.External
                ReferenceType.Rebase
            |]

        Assert.Multiple(
            Action (fun () ->
                for referenceType in eligible do
                    Assert.That(Subscriber.shouldNotifyCurrentBranchReference referenceType, Is.True, $"Expected {referenceType} current-branch emission.")

                for referenceType in ineligible do
                    Assert.That(
                        Subscriber.shouldNotifyCurrentBranchReference referenceType,
                        Is.False,
                        $"Did not expect {referenceType} current-branch emission."
                    ))
        )

    /// Verifies that the current-branch payload preserves the lookup identity clients need after server recomputation.
    [<Test>]
    member _.CurrentBranchReferencePayloadCarriesBranchAndReferenceLookupIdentity() =
        let referenceId = Guid.Parse("11111111-1111-1111-1111-111111111111")
        let ownerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
        let organizationId = Guid.Parse("33333333-3333-3333-3333-333333333333")
        let repositoryId = Guid.Parse("44444444-4444-4444-4444-444444444444")
        let branchId = Guid.Parse("55555555-5555-5555-5555-555555555555")
        let directoryId = Guid.Parse("66666666-6666-6666-6666-666666666666")

        let payload =
            Subscriber.createCurrentBranchReferenceNotification
                referenceId
                ownerId
                organizationId
                repositoryId
                branchId
                (BranchName "feature/watch")
                directoryId
                (Sha256Hash "sha")
                (Blake3Hash "blake3")
                ReferenceType.Checkpoint
                (ReferenceText "checkpoint")
                "corr-current"

        Assert.Multiple(
            Action (fun () ->
                Assert.That(payload.ReferenceId, Is.EqualTo(referenceId))
                Assert.That(payload.OwnerId, Is.EqualTo(ownerId))
                Assert.That(payload.OrganizationId, Is.EqualTo(organizationId))
                Assert.That(payload.RepositoryId, Is.EqualTo(repositoryId))
                Assert.That(payload.BranchId, Is.EqualTo(branchId))
                Assert.That(payload.BranchName, Is.EqualTo(BranchName "feature/watch"))
                Assert.That(payload.DirectoryId, Is.EqualTo(directoryId))
                Assert.That(payload.ReferenceType, Is.EqualTo(ReferenceType.Checkpoint))
                Assert.That(payload.ReferenceText, Is.EqualTo(ReferenceText "checkpoint"))
                Assert.That(payload.CorrelationId, Is.EqualTo("corr-current")))
        )

    /// Verifies that the additive current-branch contract does not remove parent-branch notification methods.
    [<Test>]
    member _.SignalRClientContractKeepsParentBranchNotificationsWithCurrentBranchPayload() =
        let methodNames =
            typeof<IGraceClientConnection>.GetMethods ()
            |> Array.map (fun methodInfo -> methodInfo.Name)
            |> Set.ofArray

        Assert.Multiple(
            Action (fun () ->
                Assert.That(methodNames, Does.Contain("NotifyOnSave"))
                Assert.That(methodNames, Does.Contain("NotifyOnCheckpoint"))
                Assert.That(methodNames, Does.Contain("NotifyOnCommit"))
                Assert.That(methodNames, Does.Contain("NotifyCurrentBranchReference")))
        )
