namespace Grace.Server.Tests

open Grace.Actors
open Grace.Types.Events
open Grace.Types.PromotionSet
open Grace.Types.Types
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type PromotionSetCommandValidationTests() =

    let createMetadata correlationId =
        { Timestamp = Instant.FromUtc(2026, 2, 21, 11, 0); CorrelationId = correlationId; Principal = "tester"; Properties = Dictionary<string, string>() }

    let existingPromotionSet status computationStatus =
        { PromotionSetDto.Default with
            PromotionSetId = Guid.NewGuid()
            OwnerId = Guid.NewGuid()
            OrganizationId = Guid.NewGuid()
            RepositoryId = Guid.NewGuid()
            TargetBranchId = Guid.NewGuid()
            Status = status
            StepsComputationStatus = computationStatus
        }

    [<Test>]
    member _.ApplyRejectedWhenPromotionSetAlreadySucceeded() =
        let dto = existingPromotionSet PromotionSetStatus.Succeeded StepsComputationStatus.Computed
        let metadata = createMetadata "corr-apply-succeeded"

        match PromotionSet.validateCommandForState [] dto PromotionSetCommand.Apply metadata with
        | Ok _ -> Assert.Fail("Expected apply validation to fail for succeeded PromotionSet.")
        | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("PromotionSet has already been applied successfully."))

    [<Test>]
    member _.ApplyRejectedWhenPromotionSetAlreadyRunning() =
        let dto = existingPromotionSet PromotionSetStatus.Running StepsComputationStatus.Computing
        let metadata = createMetadata "corr-apply-running"

        match PromotionSet.validateCommandForState [] dto PromotionSetCommand.Apply metadata with
        | Ok _ -> Assert.Fail("Expected apply validation to fail for running PromotionSet.")
        | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("PromotionSet is already running."))

    [<Test>]
    member _.RecomputeRejectedWhenStepsAlreadyComputing() =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computing
        let metadata = createMetadata "corr-recompute-computing"

        match PromotionSet.validateCommandForState [] dto (PromotionSetCommand.RecomputeStepsIfStale(Option.None)) metadata with
        | Ok _ -> Assert.Fail("Expected recompute validation to fail while already computing.")
        | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("PromotionSet steps are already computing."))

    [<Test>]
    member _.ResolveConflictsRejectedWhenNotBlocked() =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.ComputeFailed
        let metadata = createMetadata "corr-resolve-not-blocked"

        let resolutions =
            [
                { FilePath = "src/app.fs"; Accepted = true; OverrideContentArtifactId = Option.None }
            ]

        match PromotionSet.validateCommandForState [] dto (PromotionSetCommand.ResolveConflicts(Guid.NewGuid(), resolutions)) metadata with
        | Ok _ -> Assert.Fail("Expected resolve validation to fail when PromotionSet is not blocked.")
        | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("PromotionSet is not blocked for conflict review."))

    [<Test>]
    member _.DuplicateCorrelationIdRejected() =
        let dto = existingPromotionSet PromotionSetStatus.Ready StepsComputationStatus.Computed
        let duplicateCorrelationId = "corr-duplicate"

        let existingEvents: PromotionSetEvent list =
            [
                { Event = PromotionSetEventType.ApplyStarted; Metadata = createMetadata duplicateCorrelationId }
            ]

        match PromotionSet.validateCommandForState existingEvents dto PromotionSetCommand.Apply (createMetadata duplicateCorrelationId) with
        | Ok _ -> Assert.Fail("Expected duplicate correlation ID validation to fail.")
        | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("Duplicate correlation ID for PromotionSet command."))

    [<Test>]
    member _.UpdateInputPromotionsRejectedAfterSuccess() =
        let dto = existingPromotionSet PromotionSetStatus.Succeeded StepsComputationStatus.Computed
        let metadata = createMetadata "corr-update-succeeded"

        let pointers =
            [
                { BranchId = Guid.NewGuid(); ReferenceId = Guid.NewGuid(); DirectoryVersionId = Guid.NewGuid() }
            ]

        match PromotionSet.validateCommandForState [] dto (PromotionSetCommand.UpdateInputPromotions pointers) metadata with
        | Ok _ -> Assert.Fail("Expected update-input validation to fail for succeeded PromotionSet.")
        | Error graceError -> Assert.That(graceError.Error, Is.EqualTo("PromotionSet has already succeeded and cannot be edited."))
