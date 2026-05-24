namespace Grace.Server.Tests

open Grace.Shared
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.RepositoryContentCounter
open Grace.Types.Types
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Text

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

    let metadata correlationId = { Timestamp = timestamp; CorrelationId = correlationId; Principal = "tester"; Properties = Dictionary<string, string>() }

    let finalizedManifest () : FileManifest =
        let bytes = Encoding.UTF8.GetBytes($"large manifest-backed file {Guid.NewGuid():N}")

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
                FileContentHash(ContentAddress.computeBlake3Hex bytes),
                int64 bytes.Length,
                [
                    ContentBlock.Create(block.Address, 0L, int64 bytes.Length)
                ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let manifestFile (manifest: FileManifest) =
        let fileVersion = FileVersion.Create "/large.bin" "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" String.Empty true manifest.Size
        fileVersion.ContentReference <- FileContentReference.FileManifest manifest
        fileVersion

    let wholeFile () = FileVersion.Create "/small.txt" "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd" String.Empty false 42L

    let directoryWith (files: FileVersion seq) =
        let fileList = List<FileVersion>(files)

        Grace.Types.Types.DirectoryVersion.Create
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
