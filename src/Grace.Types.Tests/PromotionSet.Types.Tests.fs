namespace Grace.Types.Tests

open Grace.Types.PromotionSet
open Grace.Types.Common
open Grace.Types.Visibility
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

/// Contains tests covering promotion set determinism behavior.
[<Parallelizable(ParallelScope.All)>]
type PromotionSetDeterminismTests() =

    /// Builds a deterministic metadata fixture for the types promotion Set assertions.
    let createMetadata correlationId principal timestamp =
        {
            Timestamp = timestamp
            CorrelationId = correlationId
            Principal = principal
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    /// Builds a deterministic promotion set dto fixture for the types promotion Set assertions.
    let createPromotionSetDto timestamp =
        let createdEvent: PromotionSetEvent =
            {
                Event =
                    PromotionSetEventType.Created(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        ResourceVisibility.Public,
                        ResourceOwnership.RepositoryOwned,
                        Option.None
                    )
                Metadata = createMetadata "corr-created" "tester" timestamp
            }

        PromotionSetDto.UpdateDto createdEvent PromotionSetDto.Default

    /// Verifies that input promotions updated resets computed state.
    [<Test>]
    member _.InputPromotionsUpdatedResetsComputedState() =
        let timestamp = Instant.FromUtc(2026, 2, 18, 8, 0)
        let seedDto = createPromotionSetDto timestamp

        let previouslyComputed =
            { seedDto with
                Status = PromotionSetStatus.Blocked
                StepsComputationStatus = StepsComputationStatus.Computed
                StepsComputationAttempt = 3
                StepsComputationError = Option.Some "stale"
                ComputedAgainstParentTerminalPromotionReferenceId = Option.Some(Guid.NewGuid())
            }

        let pointers =
            [
                { BranchId = Guid.NewGuid(); ReferenceId = Guid.NewGuid(); DirectoryVersionId = Guid.NewGuid() }
            ]

        let inputUpdatedEvent: PromotionSetEvent =
            {
                Event = PromotionSetEventType.InputPromotionsUpdated pointers
                Metadata = createMetadata "corr-input-updated" "tester" (timestamp + Duration.FromMinutes(1.0))
            }

        let updated = PromotionSetDto.UpdateDto inputUpdatedEvent previouslyComputed

        Assert.That(updated.Steps.Length, Is.EqualTo(1))
        Assert.That(updated.Status, Is.EqualTo(PromotionSetStatus.Ready))
        Assert.That(updated.StepsComputationStatus, Is.EqualTo(StepsComputationStatus.NotComputed))
        Assert.That(updated.ComputedAgainstParentTerminalPromotionReferenceId.IsNone, Is.True)
        Assert.That(updated.StepsComputationError.IsNone, Is.True)
        Assert.That(updated.StepsComputationAttempt, Is.EqualTo(3))

    /// Verifies that steps updated increments attempt and clears blocked status.
    [<Test>]
    member _.StepsUpdatedIncrementsAttemptAndClearsBlockedStatus() =
        let timestamp = Instant.FromUtc(2026, 2, 18, 9, 0)
        let seedDto = createPromotionSetDto timestamp

        let blocked =
            { seedDto with
                Status = PromotionSetStatus.Blocked
                StepsComputationStatus = StepsComputationStatus.ComputeFailed
                StepsComputationAttempt = 1
                StepsComputationError = Option.Some "manual review required"
            }

        let promotionSetStep: PromotionSetStep =
            {
                StepId = Guid.NewGuid()
                Order = 0
                OriginalPromotion = { BranchId = Guid.NewGuid(); ReferenceId = Guid.NewGuid(); DirectoryVersionId = Guid.NewGuid() }
                OriginalBasePromotionReferenceId = Guid.NewGuid()
                OriginalBaseDirectoryVersionId = Guid.NewGuid()
                ComputedAgainstBaseDirectoryVersionId = Guid.NewGuid()
                AppliedDirectoryVersionId = Guid.NewGuid()
                ConflictSummaryArtifactId = Option.None
                ConflictStatus = StepConflictStatus.AutoResolved
            }

        let computedAgainstTerminalReferenceId = Guid.NewGuid()

        let stepsUpdatedEvent: PromotionSetEvent =
            {
                Event = PromotionSetEventType.StepsUpdated([ promotionSetStep ], computedAgainstTerminalReferenceId)
                Metadata = createMetadata "corr-steps-updated" "tester" (timestamp + Duration.FromMinutes(2.0))
            }

        let updated = PromotionSetDto.UpdateDto stepsUpdatedEvent blocked

        Assert.That(updated.Status, Is.EqualTo(PromotionSetStatus.Ready))
        Assert.That(updated.StepsComputationStatus, Is.EqualTo(StepsComputationStatus.Computed))
        Assert.That(updated.StepsComputationAttempt, Is.EqualTo(2))
        Assert.That(updated.StepsComputationError.IsNone, Is.True)
        Assert.That(updated.ComputedAgainstParentTerminalPromotionReferenceId, Is.EqualTo(Option.Some computedAgainstTerminalReferenceId))

    /// Verifies that created private promotion set events persist the audience facts used by route and event gates.
    [<Test>]
    member _.CreatedPrivatePromotionSetPersistsVisibilityOwnershipAndCreator() =
        let timestamp = Instant.FromUtc(2026, 7, 7, 12, 0)
        let creatorUserId = UserId "reviewer@example.test"

        let createdEvent: PromotionSetEvent =
            {
                Event =
                    PromotionSetEventType.Created(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        ResourceVisibility.Private,
                        ResourceOwnership.ContributorOwned,
                        Some creatorUserId
                    )
                Metadata = createMetadata "corr-private-created" "reviewer@example.test" timestamp
            }

        let promotionSet = PromotionSetDto.UpdateDto createdEvent PromotionSetDto.Default

        Assert.That(promotionSet.Visibility, Is.EqualTo(ResourceVisibility.Private))
        Assert.That(promotionSet.Ownership, Is.EqualTo(ResourceOwnership.ContributorOwned))
        Assert.That(promotionSet.CreatorUserId, Is.EqualTo(Some creatorUserId))
