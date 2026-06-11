namespace Grace.Server.Tests

open Grace.Shared
open Grace.Types.ContentBlockMetadata
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.RepositoryContentCounter
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Text
open System.Threading.Tasks

module DirectoryVersionActor = Grace.Actors.DirectoryVersion
module ManifestContributionWorkflowActor = Grace.Actors.ManifestContributionWorkflow
module ReferenceActor = Grace.Actors.Reference
module RepositoryContentCounterActor = Grace.Actors.RepositoryContentCounter

[<Parallelizable(ParallelScope.All)>]
type SaveBoundaryActorTests() =

    let timestamp = Instant.FromUtc(2026, 5, 24, 16, 0)
    let ownerId = Guid.Parse("06fed2a6-8de5-4ef2-86b7-e465448cf77d")
    let organizationId = Guid.Parse("5a602145-3f0a-47c8-bc7c-f6618425c07f")
    let repositoryId = Guid.Parse("e34f8949-6306-4fb1-89ca-e9eb831022b0")
    let directoryVersionId = Guid.Parse("29e93e9b-3e5f-4b6e-b8c3-2a964d8d33f3")
    let referenceId = Guid.Parse("9b26f91a-fd44-46b3-9cc7-17645bb388a2")

    let metadata correlationId =
        {
            Timestamp = timestamp
            CorrelationId = correlationId
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    let finalizedManifestWithBlockCopies blockCopies : FileManifest =
        let bytes = Encoding.UTF8.GetBytes($"large manifest-backed file {Guid.NewGuid():N}")
        let fileBytes = Array.concat (List.replicate blockCopies bytes)

        let block =
            match ContentBlockFormat.encode [ { PhysicalOffset = 0L; Bytes = bytes } ] with
            | Ok block -> block
            | Error error ->
                Assert.Fail($"Expected content block encoding to succeed, got {error}.")
                Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                RabinChunking.SuiteName,
                FileContentHash(ContentAddress.computeBlake3Hex fileBytes),
                int64 fileBytes.Length,
                [
                    for index in 0 .. blockCopies - 1 do
                        ContentBlock.Create(block.Address, int64 (index * bytes.Length), int64 bytes.Length)
                ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let finalizedManifest () = finalizedManifestWithBlockCopies 1

    let manifestFile (manifest: FileManifest) =
        let fileVersion = FileVersion.Create "/large.bin" "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" String.Empty true manifest.Size
        fileVersion.ContentReference <- FileContentReference.FileManifest manifest
        fileVersion

    let wholeFile () = FileVersion.Create "/small.txt" "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd" String.Empty false 42L

    let referenceDto referenceType =
        { Grace.Types.Reference.ReferenceDto.Default with
            ReferenceId = referenceId
            RepositoryId = repositoryId
            DirectoryId = directoryVersionId
            ReferenceType = referenceType
        }

    let directoryWith (files: FileVersion seq) =
        let fileList = List<FileVersion>(files)

        Grace.Types.Common.DirectoryVersion.Create
            directoryVersionId
            ownerId
            organizationId
            repositoryId
            (RelativePath "/")
            (Sha256Hash "fedcbafedcbafedcbafedcbafedcbafedcbafedcbafedcbafedcbafedcba")
            (List<DirectoryVersionId>())
            fileList
            (fileList
             |> Seq.sumBy (fun fileVersion -> fileVersion.Size))

    let expectPlan result =
        match result with
        | Ok plans -> plans |> Seq.exactlyOne
        | Error error ->
            Assert.Fail($"Expected save boundary plan to succeed, got {error.Error}.")
            Unchecked.defaultof<ReferenceActor.ManifestSaveContributionPlan>

    [<Test>]
    member _.SaveBoundaryRejectsUnfinalizedManifestReferences() =
        let unfinalizedManifest = { finalizedManifest () with ManifestAddress = ManifestAddress String.Empty }
        let directoryVersion = directoryWith [ manifestFile unfinalizedManifest ]

        let result = ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-unfinalized"

        match result with
        | Ok _ -> Assert.Fail("Expected an unfinalized manifest reference to reject before save publication.")
        | Error error -> Assert.That(error.Error, Does.Contain("must be finalized before Save"))

    [<Test>]
    member _.DirectoryVersionWholeFileValidationSetIgnoresFinalizedManifestBackedFiles() =
        let manifest = finalizedManifest ()
        let wholeFile = wholeFile ()
        let files = List<FileVersion>([ wholeFile; manifestFile manifest ])

        let filesToValidate = DirectoryVersionActor.getFilesToValidateForSaveBoundary files (List<FileVersion>())

        Assert.That(filesToValidate, Has.Length.EqualTo(1))
        Assert.That(filesToValidate[0].RelativePath, Is.EqualTo(wholeFile.RelativePath))

    [<Test>]
    member _.ManifestSaveBoundaryRejectsMismatchedFileBlake3WhenPresent() =
        let manifest = finalizedManifest ()
        let fileVersion = manifestFile manifest
        fileVersion.Blake3Hash <- Blake3Hash "wrong-blake3"

        match DirectoryVersionActor.validateManifestBackedFileForSaveBoundary "corr-manifest-blake3-mismatch" fileVersion manifest with
        | Ok () -> Assert.Fail("Expected manifest-backed FileVersion BLAKE3 mismatch to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("FileVersion.Blake3Hash"))

    [<Test>]
    member _.ManifestSaveBoundaryAllowsLegacyEmptyFileBlake3() =
        let manifest = finalizedManifest ()
        let fileVersion = manifestFile manifest
        fileVersion.Blake3Hash <- Blake3Hash String.Empty

        match DirectoryVersionActor.validateManifestBackedFileForSaveBoundary "corr-manifest-empty-blake3" fileVersion manifest with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected legacy empty BLAKE3 to be accepted, got {error.Error}.")

    [<Test>]
    member _.ManifestSaveBoundaryRejectsAbsentContentBlockMetadataRanges() =
        let manifest = finalizedManifest ()

        let getRangePresence _ _ = Task.FromResult ContentBlockRangePresence.Absent

        match (DirectoryVersionActor.validateManifestReferencesForSaveBoundaryWithResolver getRangePresence "corr-absent-block" [ manifest ])
            .Result
            with
        | Ok () -> Assert.Fail("Expected manifest-backed save to reject absent ContentBlock metadata before Save.")
        | Error error -> Assert.That(error.Error, Does.Contain("no finalized ContentBlock metadata range exists"))

    [<Test>]
    member _.ManifestSaveBoundaryAcceptsExistingReclaimableContentBlockMetadataRanges() =
        let manifest = finalizedManifest ()

        let getRangePresence _ _ = Task.FromResult ContentBlockRangePresence.Reclaimable

        match (DirectoryVersionActor.validateManifestReferencesForSaveBoundaryWithResolver getRangePresence "corr-reclaimable-block" [ manifest ])
            .Result
            with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected existing ContentBlock metadata to be accepted before Save, got {error.Error}.")

    [<Test>]
    member _.ManifestSaveBoundaryChecksEachContentBlockAtOrdinalZero() =
        let manifest = finalizedManifestWithBlockCopies 2
        let queries = ResizeArray<ContentBlockRangeQuery>()

        let getRangePresence _ query =
            queries.Add(query)
            Task.FromResult ContentBlockRangePresence.Reclaimable

        match (DirectoryVersionActor.validateManifestReferencesForSaveBoundaryWithResolver getRangePresence "corr-ordinal-zero" [ manifest ])
            .Result
            with
        | Ok () ->
            Assert.That(queries, Has.Count.EqualTo(2))

            Assert.That(
                queries
                |> Seq.forall (fun query -> query.OrdinalStart = 0 && query.OrdinalCount = 1),
                Is.True
            )
        | Error error -> Assert.Fail($"Expected multi-block manifest metadata checks to use content-block ordinal zero, got {error.Error}.")

    [<Test>]
    member _.ManifestSaveBoundaryUsesRepositoryDerivedContentBlockMetadataPool() =
        let contentBlockAddress = ContentBlockAddress "block-address"
        let expected = $"{DedupeIndex.storagePoolIdForRepositoryId repositoryId}|{contentBlockAddress}"

        let actual = DirectoryVersionActor.contentBlockMetadataActorKeyForSaveBoundary repositoryId contentBlockAddress

        Assert.That(actual, Is.EqualTo(expected))

    [<Test>]
    member _.SaveBoundaryAcceptsFinalizedManifestAfterDurableIncrementIntent() =
        let manifest = finalizedManifest ()
        let directoryVersion = directoryWith [ manifestFile manifest ]

        let plan =
            ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-finalized"
            |> expectPlan

        let counterDecision = RepositoryContentCounterActor.decideCommand [] RepositoryContentCounterDto.Default plan.CounterCommand (metadata "corr-counter")

        match counterDecision with
        | Ok decision ->
            Assert.That(decision.Counter.ReferenceCount, Is.EqualTo(1L))
            Assert.That(decision.Intents, Has.Length.EqualTo(1))
            Assert.That(decision.Intents[0], Is.EqualTo(RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, manifest.ManifestAddress)))
        | Error error -> Assert.Fail($"Expected repository content counter increment to succeed, got {error.Error}.")

    [<Test>]
    member _.SaveBoundaryRestartsContributionWorkflowAfterCounterReplay() =
        let manifest = finalizedManifest ()
        let directoryVersion = directoryWith [ manifestFile manifest ]

        let plan =
            ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-replay"
            |> expectPlan

        let firstCounterDecision =
            match RepositoryContentCounterActor.decideCommand [] RepositoryContentCounterDto.Default plan.CounterCommand (metadata "corr-counter-first") with
            | Ok decision -> decision
            | Error error ->
                Assert.Fail($"Expected first repository content counter increment to succeed, got {error.Error}.")
                Unchecked.defaultof<RepositoryContentCounterDecision>

        let replayDecision =
            match
                RepositoryContentCounterActor.decideCommand
                    firstCounterDecision.Events
                    firstCounterDecision.Counter
                    plan.CounterCommand
                    (metadata "corr-counter-replay")
                with
            | Ok decision -> decision
            | Error error ->
                Assert.Fail($"Expected repository content counter replay to succeed, got {error.Error}.")
                Unchecked.defaultof<RepositoryContentCounterDecision>

        Assert.That(replayDecision.WasIdempotentReplay, Is.True)
        Assert.That(replayDecision.Intents, Is.Empty)

        match ReferenceActor.tryCreateManifestContributionStartForCounterDecision plan replayDecision firstCounterDecision.Events with
        | Some startCommand ->
            let workflowDecision =
                ManifestContributionWorkflowActor.decideCommand [] ManifestContributionWorkflowDto.Default startCommand (metadata "corr-workflow-replay")

            match workflowDecision with
            | Ok decision -> Assert.That(decision.Workflow.LifecycleState, Is.EqualTo(ManifestContributionWorkflowLifecycleState.InProgress))
            | Error error -> Assert.Fail($"Expected replayed counter boundary to restart contribution workflow, got {error.Error}.")
        | None -> Assert.Fail("Expected replayed zero-crossing counter operation to restart manifest contribution workflow fan-out.")

    [<Test>]
    member _.SaveBoundaryStartsPendingContributionWorkflowWithoutWaitingForRangeCompletion() =
        let manifest = finalizedManifest ()
        let directoryVersion = directoryWith [ manifestFile manifest ]

        let plan =
            ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-fanout"
            |> expectPlan

        let intent = RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, manifest.ManifestAddress)

        match ReferenceActor.tryCreateManifestContributionStart plan intent with
        | Some startCommand ->
            let workflowDecision =
                ManifestContributionWorkflowActor.decideCommand [] ManifestContributionWorkflowDto.Default startCommand (metadata "corr-workflow")

            match workflowDecision with
            | Ok decision ->
                Assert.That(decision.Workflow.LifecycleState, Is.EqualTo(ManifestContributionWorkflowLifecycleState.InProgress))
                Assert.That(ManifestContributionWorkflowActor.pendingRanges decision.Workflow, Has.Length.EqualTo(manifest.Blocks.Count))
            | Error error -> Assert.Fail($"Expected contribution workflow start to succeed, got {error.Error}.")
        | None -> Assert.Fail("Expected increment intent to start manifest contribution workflow fan-out.")

    [<Test>]
    member _.SaveBoundaryStartsContributionWorkflowForRepeatedManifestBlockOccurrences() =
        let manifest = finalizedManifestWithBlockCopies 2
        let directoryVersion = directoryWith [ manifestFile manifest ]

        let plan =
            ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-repeated-block"
            |> expectPlan

        Assert.That(plan.WorkflowRanges, Has.Length.EqualTo(2))
        Assert.That(plan.WorkflowRanges[0].ContentBlockAddress, Is.EqualTo(plan.WorkflowRanges[1].ContentBlockAddress))
        Assert.That(plan.WorkflowRanges[0].OrdinalStart, Is.Not.EqualTo(plan.WorkflowRanges[1].OrdinalStart))

        let intent = RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, manifest.ManifestAddress)

        match ReferenceActor.tryCreateManifestContributionStart plan intent with
        | Some startCommand ->
            let workflowDecision =
                ManifestContributionWorkflowActor.decideCommand [] ManifestContributionWorkflowDto.Default startCommand (metadata "corr-repeated-workflow")

            match workflowDecision with
            | Ok decision -> Assert.That(ManifestContributionWorkflowActor.pendingRanges decision.Workflow, Has.Length.EqualTo(2))
            | Error error -> Assert.Fail($"Expected repeated block occurrences to start contribution workflow, got {error.Error}.")
        | None -> Assert.Fail("Expected increment intent to start manifest contribution workflow fan-out.")

    [<Test>]
    member _.SaveExpiryStartsDecrementContributionWorkflow() =
        let manifest = finalizedManifest ()
        let directoryVersion = directoryWith [ manifestFile manifest ]

        let savePlan =
            ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-expiry-plan"
            |> expectPlan

        let decrementPlan =
            { savePlan with
                CounterCommand =
                    RepositoryContentCounterCommand.RemoveReference(
                        RepositoryContentCounterOperationId $"save-expiry:{referenceId:N}:{manifest.ManifestAddress}",
                        repositoryId,
                        manifest.ManifestAddress
                    )
            }

        let intent = RepositoryContentCounterIntent.DecrementManifestReferenceCount(repositoryId, manifest.ManifestAddress)

        match ReferenceActor.tryCreateManifestContributionStart decrementPlan intent with
        | Some startCommand ->
            let workflowDecision =
                ManifestContributionWorkflowActor.decideCommand [] ManifestContributionWorkflowDto.Default startCommand (metadata "corr-expiry-workflow")

            match workflowDecision with
            | Ok decision ->
                Assert.That(decision.Workflow.Direction, Is.EqualTo(ManifestContributionDirection.Decrement))
                Assert.That(ManifestContributionWorkflowActor.pendingRanges decision.Workflow, Has.Length.EqualTo(manifest.Blocks.Count))
            | Error error -> Assert.Fail($"Expected save expiry decrement workflow to start, got {error.Error}.")
        | None -> Assert.Fail("Expected decrement intent to start manifest contribution workflow fan-out.")

    [<Test>]
    member _.SaveExpiryDecrementWorkflowUsesDistinctOperationIdAfterIncrementWorkflow() =
        let manifest = finalizedManifest ()
        let directoryVersion = directoryWith [ manifestFile manifest ]

        let savePlan =
            ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-expiry-operation-plan"
            |> expectPlan

        let incrementStart =
            match
                ReferenceActor.tryCreateManifestContributionStart
                    savePlan
                    (RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, manifest.ManifestAddress))
                with
            | Some command -> command
            | None ->
                Assert.Fail("Expected increment intent to start manifest contribution workflow fan-out.")
                Unchecked.defaultof<ManifestContributionWorkflowCommand>

        let incrementDecision =
            match ManifestContributionWorkflowActor.decideCommand [] ManifestContributionWorkflowDto.Default incrementStart (metadata "corr-increment-start")
                with
            | Ok decision -> decision
            | Error error ->
                Assert.Fail($"Expected increment workflow to start, got {error.Error}.")
                Unchecked.defaultof<ManifestContributionWorkflowDecision>

        let decrementPlan =
            { savePlan with
                CounterCommand =
                    RepositoryContentCounterCommand.RemoveReference(
                        RepositoryContentCounterOperationId $"save-expiry:{referenceId:N}:{manifest.ManifestAddress}",
                        repositoryId,
                        manifest.ManifestAddress
                    )
            }

        let decrementStart =
            match
                ReferenceActor.tryCreateManifestContributionStart
                    decrementPlan
                    (RepositoryContentCounterIntent.DecrementManifestReferenceCount(repositoryId, manifest.ManifestAddress))
                with
            | Some command -> command
            | None ->
                Assert.Fail("Expected decrement intent to start manifest contribution workflow fan-out.")
                Unchecked.defaultof<ManifestContributionWorkflowCommand>

        let completedIncrementWorkflow = { incrementDecision.Workflow with LifecycleState = ManifestContributionWorkflowLifecycleState.Completed }

        match
            ManifestContributionWorkflowActor.decideCommand incrementDecision.Events completedIncrementWorkflow decrementStart (metadata "corr-decrement-start")
            with
        | Ok decision ->
            Assert.That(decision.Workflow.Direction, Is.EqualTo(ManifestContributionDirection.Decrement))
            Assert.That(decision.OperationId, Is.Not.EqualTo(incrementDecision.OperationId))
        | Error error -> Assert.Fail($"Expected decrement workflow to start after increment workflow, got {error.Error}.")

    [<Test>]
    member _.SaveExpiryCounterReplayRestartsDecrementWorkflowWithoutSecondCounterEvent() =
        let manifest = finalizedManifest ()
        let directoryVersion = directoryWith [ manifestFile manifest ]

        let savePlan =
            ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-expiry-replay-plan"
            |> expectPlan

        let addDecision =
            RepositoryContentCounterActor.decideCommand [] RepositoryContentCounterDto.Default savePlan.CounterCommand (metadata "corr-add-before-expiry")

        let addedDto, addEvents =
            match addDecision with
            | Ok decision -> decision.Counter, decision.Events
            | Error error ->
                Assert.Fail($"Expected save counter increment to succeed, got {error.Error}.")
                RepositoryContentCounterDto.Default, []

        let decrementPlan =
            { savePlan with
                CounterCommand =
                    RepositoryContentCounterCommand.RemoveReference(
                        RepositoryContentCounterOperationId $"save-expiry:{referenceId:N}:{manifest.ManifestAddress}",
                        repositoryId,
                        manifest.ManifestAddress
                    )
            }

        let removeDecision = RepositoryContentCounterActor.decideCommand addEvents addedDto decrementPlan.CounterCommand (metadata "corr-expiry-remove")

        let removedDto, allEvents =
            match removeDecision with
            | Ok decision ->
                Assert.That(decision.Counter.ReferenceCount, Is.EqualTo(0L))
                Assert.That(decision.Intents, Has.Length.EqualTo(1))
                decision.Counter, addEvents @ decision.Events
            | Error error ->
                Assert.Fail($"Expected save expiry counter decrement to succeed, got {error.Error}.")
                RepositoryContentCounterDto.Default, []

        let replayDecision = RepositoryContentCounterActor.decideCommand allEvents removedDto decrementPlan.CounterCommand (metadata "corr-expiry-retry")

        match replayDecision with
        | Ok decision ->
            Assert.That(decision.WasIdempotentReplay, Is.True)
            Assert.That(decision.Events, Is.Empty)
            Assert.That(decision.Intents, Is.Empty)

            match ReferenceActor.tryCreateManifestContributionStartForCounterDecision decrementPlan decision allEvents with
            | Some startCommand ->
                let workflowDecision =
                    ManifestContributionWorkflowActor.decideCommand
                        []
                        ManifestContributionWorkflowDto.Default
                        startCommand
                        (metadata "corr-expiry-retry-workflow")

                match workflowDecision with
                | Ok workflowDecision -> Assert.That(workflowDecision.Workflow.Direction, Is.EqualTo(ManifestContributionDirection.Decrement))
                | Error error -> Assert.Fail($"Expected replayed expiry decrement to restart workflow, got {error.Error}.")
            | None -> Assert.Fail("Expected replayed zero-crossing counter removal to restart decrement workflow fan-out.")
        | Error error -> Assert.Fail($"Expected save expiry counter retry to be idempotent, got {error.Error}.")

    [<Test>]
    member _.PendingSaveExpiryDecrementBlocksUnsafeGcUntilRangeCompletes() =
        let manifest = finalizedManifest ()
        let directoryVersion = directoryWith [ manifestFile manifest ]

        let savePlan =
            ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-expiry-gc-plan"
            |> expectPlan

        let decrementPlan =
            { savePlan with
                CounterCommand =
                    RepositoryContentCounterCommand.RemoveReference(
                        RepositoryContentCounterOperationId $"save-expiry:{referenceId:N}:{manifest.ManifestAddress}",
                        repositoryId,
                        manifest.ManifestAddress
                    )
            }

        let startCommand =
            match
                ReferenceActor.tryCreateManifestContributionStart
                    decrementPlan
                    (RepositoryContentCounterIntent.DecrementManifestReferenceCount(repositoryId, manifest.ManifestAddress))
                with
            | Some command -> command
            | None ->
                Assert.Fail("Expected decrement intent to start manifest contribution workflow fan-out.")
                Unchecked.defaultof<ManifestContributionWorkflowCommand>

        let started =
            match ManifestContributionWorkflowActor.decideCommand [] ManifestContributionWorkflowDto.Default startCommand (metadata "corr-expiry-gc-start") with
            | Ok decision -> decision.Workflow
            | Error error ->
                Assert.Fail($"Expected decrement workflow start to succeed, got {error.Error}.")
                ManifestContributionWorkflowDto.Default

        let range = decrementPlan.WorkflowRanges[0]
        Assert.That(ManifestContributionWorkflowActor.blocksUnsafeDeletion started range, Is.True)

        let progress =
            {
                OperationId = ManifestContributionWorkflowOperationId "range-expiry-done"
                RepositoryId = repositoryId
                ManifestAddress = manifest.ManifestAddress
                Range = range
            }

        let completed =
            match
                ManifestContributionWorkflowActor.decideCommand
                    []
                    started
                    (ManifestContributionWorkflowCommand.RecordRangeSucceeded progress)
                    (metadata "corr-expiry-gc-range")
                with
            | Ok decision -> decision.Workflow
            | Error error ->
                Assert.Fail($"Expected decrement range completion to succeed, got {error.Error}.")
                started

        Assert.That(ManifestContributionWorkflowActor.blocksUnsafeDeletion completed range, Is.False)

    [<Test>]
    member _.SaveExpiryPlanningKeepsWholeFileCleanupAsNoOp() =
        let directoryVersion = directoryWith [ wholeFile () ]

        match ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-whole-file-expiry" with
        | Ok plans -> Assert.That(plans, Is.Empty)
        | Error error -> Assert.Fail($"Expected whole-file save expiry planning to remain a no-op, got {error.Error}.")

    [<Test>]
    member _.PhysicalDeletionReminderAppliesExpiryBoundaryOnlyForLiveSaveReferences() =
        Assert.That(ReferenceActor.shouldApplySaveExpiryBoundary (referenceDto ReferenceType.Save), Is.True)
        Assert.That(ReferenceActor.shouldApplySaveExpiryBoundary (referenceDto ReferenceType.Checkpoint), Is.False)
        Assert.That(ReferenceActor.shouldApplySaveExpiryBoundary Grace.Types.Reference.ReferenceDto.Default, Is.False)
