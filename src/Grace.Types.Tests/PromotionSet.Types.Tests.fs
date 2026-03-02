namespace Grace.Types.Tests

open Grace.Types.PromotionSet
open Grace.Types.Types
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type PromotionSetDeterminismTests() =

    let createMetadata correlationId principal timestamp =
        { Timestamp = timestamp; CorrelationId = correlationId; Principal = principal; Properties = Dictionary<string, string>() }

    let createPromotionSetDto timestamp =
        let createdEvent: PromotionSetEvent =
            {
                Event = PromotionSetEventType.Created(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
                Metadata = createMetadata "corr-created" "tester" timestamp
            }

        PromotionSetDto.UpdateDto createdEvent PromotionSetDto.Default

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
