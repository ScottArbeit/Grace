namespace Grace.Server.Tests

open Grace.Server.Notification
open Grace.Types
open Grace.Types.Events
open Grace.Types.Queue
open Grace.Types.Common
open Grace.Types.Reference
open Grace.Types.Visibility
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

/// Covers notification Server behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type NotificationServerTests() =

    /// Constructs metadata fixtures used by the notification projection assertions.
    let metadata correlationId repositoryId =
        let properties = Dictionary<string, string>()
        properties[nameof RepositoryId] <- $"{repositoryId}"

        {
            Timestamp = Instant.FromUtc(2026, 7, 9, 10, 30)
            CorrelationId = correlationId
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = properties
        }

    /// Marks an event fixture as private at creation time.
    let markPrivateContributorOwned (metadata: EventMetadata) =
        metadata.Properties[ "Visibility" ] <- $"{ResourceVisibility.Private}"
        metadata.Properties[ "Ownership" ] <- $"{ResourceOwnership.ContributorOwned}"
        metadata

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

    /// Verifies that private reference creation metadata stays suppressed even after the current reference is public.
    [<Test>]
    member _.PrivateReferenceCreatedProjectionStaysSuppressedAfterReveal() =
        let eventMetadata =
            metadata "corr-private-created-after-reveal" (Guid.NewGuid())
            |> markPrivateContributorOwned

        Assert.That(Subscriber.shouldNotifyReferenceCreatedProjection eventMetadata (Some true), Is.False)
        Assert.That(Subscriber.shouldRecordDerivedReferenceCreatedProjection eventMetadata (Some true), Is.False)

    /// Verifies that public reference creation still publishes when the current reference is public.
    [<Test>]
    member _.PublicReferenceCreatedProjectionAllowsCurrentPublicReference() =
        let eventMetadata = metadata "corr-public-created" (Guid.NewGuid())

        Assert.That(Subscriber.shouldNotifyReferenceCreatedProjection eventMetadata (Some true), Is.True)
        Assert.That(Subscriber.shouldRecordDerivedReferenceCreatedProjection eventMetadata (Some true), Is.True)

    /// Verifies that PromotionSet created events carry their own source id for projection rechecks.
    [<Test>]
    member _.PromotionSetCreatedProjectionIdUsesCreatedEventSource() =
        let repositoryId = Guid.NewGuid()
        let promotionSetId = Guid.NewGuid()

        let promotionSetEvent: PromotionSet.PromotionSetEvent =
            {
                Event =
                    PromotionSet.PromotionSetEventType.Created(
                        promotionSetId,
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        repositoryId,
                        Guid.NewGuid(),
                        ResourceVisibility.Public,
                        ResourceOwnership.RepositoryOwned,
                        Some(UserId "creator@example.test")
                    )
                Metadata = metadata "corr-promotion-set-created" repositoryId
            }

        let projectionId = Subscriber.tryGetPromotionSetProjectionId (GraceEvent.PromotionSetEvent promotionSetEvent)
        Assert.That(projectionId, Is.EqualTo(Some promotionSetId))

    /// Verifies that non-create PromotionSet events derive source scope from actor identity metadata.
    [<Test>]
    member _.PromotionSetNonCreateProjectionIdUsesActorIdWhenScopeMetadataIsMissing() =
        let repositoryId = Guid.NewGuid()
        let promotionSetId = Guid.NewGuid()
        let eventMetadata = metadata "corr-promotion-set-applied" repositoryId
        eventMetadata.Properties[ "ActorId" ] <- $"{promotionSetId}"

        let promotionSetEvent: PromotionSet.PromotionSetEvent = { Event = PromotionSet.PromotionSetEventType.Applied(Guid.NewGuid()); Metadata = eventMetadata }

        let projectionId = Subscriber.tryGetPromotionSetProjectionId (GraceEvent.PromotionSetEvent promotionSetEvent)
        Assert.That(projectionId, Is.EqualTo(Some promotionSetId))

    /// Verifies that non-create PromotionSet events without source scope remain unscoped for suppression.
    [<Test>]
    member _.PromotionSetNonCreateProjectionIdIsMissingWithoutScopeMetadata() =
        let repositoryId = Guid.NewGuid()
        let eventMetadata = metadata "corr-promotion-set-missing-scope" repositoryId

        let promotionSetEvent: PromotionSet.PromotionSetEvent = { Event = PromotionSet.PromotionSetEventType.ApplyStarted; Metadata = eventMetadata }

        let projectionId = Subscriber.tryGetPromotionSetProjectionId (GraceEvent.PromotionSetEvent promotionSetEvent)
        Assert.That(projectionId, Is.EqualTo(Option.None))

    /// Verifies that retry-like queue events carry PromotionSet scope directly from the event payload.
    [<Test>]
    member _.PromotionQueueProjectionIdUsesEventPromotionSetId() =
        let promotionSetId = Guid.NewGuid()

        let queueEvent: PromotionQueueEvent =
            { Event = PromotionQueueEventType.PromotionSetEnqueued promotionSetId; Metadata = metadata "corr-queue-scope" (Guid.NewGuid()) }

        let projectionId = Subscriber.tryGetPromotionSetProjectionId (GraceEvent.QueueEvent queueEvent)
        Assert.That(projectionId, Is.EqualTo(Some promotionSetId))
