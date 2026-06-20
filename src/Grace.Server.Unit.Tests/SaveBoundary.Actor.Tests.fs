namespace Grace.Server.Tests

open Grace.Shared
open Grace.Types.ContentBlockMetadata
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Reference
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
    let storagePoolId = StoragePoolId "pool-shared"
    let branchId = Guid.Parse("a649ad57-3620-4936-8f2f-fbff8f99f449")
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

    let saveCreatedEventWithStoragePool storagePoolId : ReferenceEvent =
        let eventMetadata = metadata "corr-save-pool-event"

        eventMetadata.Properties[
            ReferenceActor.SaveStoragePoolIdMetadataKey
        ] <- $"{storagePoolId}"

        {
            Event =
                ReferenceEventType.Created(
                    referenceId,
                    ownerId,
                    organizationId,
                    repositoryId,
                    branchId,
                    directoryVersionId,
                    Sha256Hash "sha",
                    Blake3Hash "blake3",
                    ReferenceType.Save,
                    ReferenceText "save",
                    Seq.empty
                )
            Metadata = eventMetadata
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

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest; StoragePoolId = storagePoolId }

    let finalizedManifest () = finalizedManifestWithBlockCopies 1

    let finalizedManifestWithStoragePool storagePoolId = { finalizedManifest () with StoragePoolId = storagePoolId }

    let appendRepeatedBlock (manifest: FileManifest) =
        let firstBlock = manifest.Blocks[0]
        let blocks = List<ContentBlock>()

        blocks.AddRange manifest.Blocks
        blocks.Add(ContentBlock.Create(firstBlock.Address, manifest.Size, firstBlock.Size))

        let resizedManifest =
            { manifest with
                FileContentHash =
                    $"{manifest.FileContentHash}:expanded:{manifest.StoragePoolId}"
                    |> Encoding.UTF8.GetBytes
                    |> ContentAddress.computeBlake3Hex
                    |> FileContentHash
                Size = manifest.Size + firstBlock.Size
                Blocks = blocks
            }

        { resizedManifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest resizedManifest }

    let manifestFile (manifest: FileManifest) =
        let fileVersion = FileVersion.Create "/large.bin" "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" String.Empty true manifest.Size
        fileVersion.ContentReference <- FileContentReference.FileManifest manifest
        fileVersion

    let manifestFileAt relativePath (manifest: FileManifest) =
        let fileVersion = FileVersion.Create relativePath "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" String.Empty true manifest.Size

        fileVersion.ContentReference <- FileContentReference.FileManifest manifest
        fileVersion

    let wholeFile () = FileVersion.Create "/small.txt" "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd" String.Empty false 42L

    let fileWithHashes (relativePath: string) (sha256Hash: string) (blake3Hash: string) size =
        FileVersion.CreateWithHashes (RelativePath relativePath) (Sha256Hash sha256Hash) (Blake3Hash blake3Hash) String.Empty false size

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

    let hashedDirectory directoryId relativePath (childDirectories: DirectoryVersion seq) (files: FileVersion seq) =
        let childDirectories = childDirectories |> Seq.toArray
        let files = List<FileVersion>(files)
        let sha256Hash, blake3Hash = DirectoryVersionActor.computeDirectoryVersionHashesFromChildren relativePath childDirectories files

        Grace.Types.Common.DirectoryVersion.CreateWithHashes
            directoryId
            ownerId
            organizationId
            repositoryId
            relativePath
            sha256Hash
            blake3Hash
            (List<DirectoryVersionId>(
                childDirectories
                |> Seq.map (fun directoryVersion -> directoryVersion.DirectoryVersionId)
            ))
            files
            (files
             |> Seq.sumBy (fun fileVersion -> fileVersion.Size))

    let assertDirectoryHashesValid directoryVersion childDirectories =
        match DirectoryVersionActor.validateDirectoryVersionHashesWithChildren "corr-directory-hash" directoryVersion childDirectories with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected DirectoryVersion hashes to validate, got {error.Error}.")

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

        let result = ReferenceActor.planManifestSaveBoundary repositoryId storagePoolId referenceId directoryVersion "corr-unfinalized"

        match result with
        | Ok _ -> Assert.Fail("Expected an unfinalized manifest reference to reject before save publication.")
        | Error error -> Assert.That(error.Error, Does.Contain("must be finalized before Save"))

    [<Test>]
    member _.SaveBoundaryRejectsManifestWithoutStoragePoolId() =
        let manifest = { finalizedManifest () with StoragePoolId = StoragePoolId String.Empty }
        let fileVersion = manifestFile manifest

        match DirectoryVersionActor.validateManifestBackedFileForSaveBoundary "corr-manifest-pool-missing" fileVersion manifest with
        | Ok () -> Assert.Fail("Expected manifest-backed FileVersion without StoragePoolId to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("StoragePoolId"))

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
    member _.DirectoryVersionHashValidationRejectsNewManifestBackedFileBlake3Gap() =
        let manifest = finalizedManifest ()
        let fileVersion = manifestFile manifest
        fileVersion.Blake3Hash <- Blake3Hash String.Empty
        let directoryVersion = hashedDirectory directoryVersionId (RelativePath "/") [] [ fileVersion ]

        match DirectoryVersionActor.validateDirectoryVersionHashesWithChildren "corr-legacy-manifest-file-blake3" directoryVersion [] with
        | Ok () -> Assert.Fail("Expected new manifest-backed FileVersion without BLAKE3 to reject before Save.")
        | Error error ->
            Assert.That(
                error.Error,
                Does
                    .Contain("child file")
                    .And.Contain("Blake3Hash")
            )

    [<Test>]
    member _.DirectoryVersionHashValidationAllowsUnchangedLegacyManifestBackedFileBlake3Gap() =
        let manifest = finalizedManifest ()
        let fileVersion = manifestFile manifest
        fileVersion.Blake3Hash <- Blake3Hash String.Empty
        let previousFileVersion = manifestFile manifest
        previousFileVersion.Blake3Hash <- Blake3Hash String.Empty
        let directoryVersion = hashedDirectory directoryVersionId (RelativePath "/") [] [ fileVersion ]

        match
            DirectoryVersionActor.validateDirectoryVersionHashesWithChildrenAndPreviousFiles
                "corr-legacy-manifest-file-blake3"
                directoryVersion
                []
                [ previousFileVersion ]
            with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected unchanged legacy manifest-backed FileVersion without BLAKE3 to validate, got {error.Error}.")

    [<Test>]
    member _.DirectoryVersionHashValidationRequiresDirectoryBlake3BeforeSave() =
        let directoryVersion = hashedDirectory directoryVersionId (RelativePath "/") [] []
        directoryVersion.Blake3Hash <- Blake3Hash String.Empty

        match DirectoryVersionActor.validateDirectoryVersionHashesWithChildren "corr-missing-directory-blake3" directoryVersion [] with
        | Ok () -> Assert.Fail("Expected missing DirectoryVersion.Blake3Hash to reject before Save.")
        | Error error -> Assert.That(error.Error, Does.Contain("DirectoryVersion.Blake3Hash"))

    [<Test>]
    member _.DirectoryVersionHashValidationRejectsMismatchedDirectoryBlake3BeforeSave() =
        let directoryVersion = hashedDirectory directoryVersionId (RelativePath "/") [] []
        directoryVersion.Blake3Hash <- Blake3Hash "0000000000000000000000000000000000000000000000000000000000000000"

        match DirectoryVersionActor.validateDirectoryVersionHashesWithChildren "corr-mismatched-directory-blake3" directoryVersion [] with
        | Ok () -> Assert.Fail("Expected mismatched DirectoryVersion.Blake3Hash to reject before Save.")
        | Error error -> Assert.That(error.Error, Does.Contain("mismatched Blake3Hash"))

    [<Test>]
    member _.DirectoryVersionHashValidationRejectsMismatchedDirectorySha256BeforeSave() =
        let directoryVersion = hashedDirectory directoryVersionId (RelativePath "/") [] []
        directoryVersion.Sha256Hash <- Sha256Hash "0000000000000000000000000000000000000000000000000000000000000000"

        match DirectoryVersionActor.validateDirectoryVersionHashesWithChildren "corr-mismatched-directory-sha" directoryVersion [] with
        | Ok () -> Assert.Fail("Expected mismatched DirectoryVersion.Sha256Hash to reject before Save.")
        | Error error -> Assert.That(error.Error, Does.Contain("mismatched Sha256Hash"))

    [<Test>]
    member _.DirectoryVersionHashValidationNormalizesJsonOmittedZeroSizeBeforeSave() =
        let directoryVersion = hashedDirectory directoryVersionId (RelativePath "/empty/") [] []
        directoryVersion.Size <- Constants.InitialDirectorySize

        let normalizedDirectoryVersion = DirectoryVersionActor.normalizeDirectoryVersionForSaveBoundary directoryVersion

        Assert.That(normalizedDirectoryVersion.Size, Is.EqualTo(0L))
        assertDirectoryHashesValid normalizedDirectoryVersion []

    [<Test>]
    member _.DirectoryVersionHashValidationRejectsMissingChildDirectoryBlake3() =
        let child = hashedDirectory (Guid.NewGuid()) (RelativePath "/src/") [] []
        child.Blake3Hash <- Blake3Hash String.Empty
        let parent = hashedDirectory directoryVersionId (RelativePath "/") [ child ] []

        match DirectoryVersionActor.validateDirectoryVersionHashesWithChildren "corr-missing-child-directory-blake3" parent [ child ] with
        | Ok () -> Assert.Fail("Expected missing child DirectoryVersion.Blake3Hash to reject before Save.")
        | Error error ->
            Assert.That(
                error.Error,
                Does
                    .Contain("child directory")
                    .And.Contain("Blake3Hash")
            )

    [<Test>]
    member _.DirectoryVersionHashValidationAllowsValidatedLegacyChildDirectoryBlake3Gap() =
        let child = hashedDirectory (Guid.NewGuid()) (RelativePath "/src/") [] []
        child.HashesValidated <- true
        child.Blake3Hash <- Blake3Hash String.Empty
        let parent = hashedDirectory directoryVersionId (RelativePath "/") [ child ] []

        match DirectoryVersionActor.validateDirectoryVersionHashesWithChildren "corr-legacy-child-directory-blake3" parent [ child ] with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected validated legacy child DirectoryVersion without BLAKE3 to validate, got {error.Error}.")

    [<Test>]
    member _.DirectoryVersionHashValidationRejectsNewWholeFileBlake3Gap() =
        let fileVersion =
            fileWithHashes
                "/a.txt"
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                1L

        fileVersion.Blake3Hash <- Blake3Hash String.Empty
        let directoryVersion = hashedDirectory directoryVersionId (RelativePath "/") [] [ fileVersion ]

        match DirectoryVersionActor.validateDirectoryVersionHashesWithChildren "corr-legacy-whole-file-blake3" directoryVersion [] with
        | Ok () -> Assert.Fail("Expected new whole-file FileVersion without BLAKE3 to reject before Save.")
        | Error error ->
            Assert.That(
                error.Error,
                Does
                    .Contain("child file")
                    .And.Contain("Blake3Hash")
            )

    [<Test>]
    member _.DirectoryVersionHashValidationAllowsUnchangedLegacyWholeFileBlake3Gap() =
        let fileVersion =
            fileWithHashes
                "/a.txt"
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                1L

        fileVersion.Blake3Hash <- Blake3Hash String.Empty

        let previousFileVersion =
            fileWithHashes
                "/a.txt"
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                1L

        previousFileVersion.Blake3Hash <- Blake3Hash String.Empty
        let directoryVersion = hashedDirectory directoryVersionId (RelativePath "/") [] [ fileVersion ]

        match
            DirectoryVersionActor.validateDirectoryVersionHashesWithChildrenAndPreviousFiles
                "corr-legacy-whole-file-blake3"
                directoryVersion
                []
                [ previousFileVersion ]
            with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected unchanged legacy whole-file FileVersion without BLAKE3 to validate, got {error.Error}.")

    [<Test>]
    member _.DirectoryVersionHashValidationRejectsMissingManifestChildFileBlake3() =
        let fileVersion =
            fileWithHashes
                "/a.txt"
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                1L

        fileVersion.ContentReference <- { Class = "FileContentReference"; ReferenceType = FileContentReferenceType.FileManifest; Manifest = None }

        fileVersion.Blake3Hash <- Blake3Hash String.Empty
        let directoryVersion = hashedDirectory directoryVersionId (RelativePath "/") [] [ fileVersion ]

        match DirectoryVersionActor.validateDirectoryVersionHashesWithChildren "corr-missing-child-file-blake3" directoryVersion [] with
        | Ok () -> Assert.Fail("Expected missing child file Blake3Hash to reject before Save.")
        | Error error ->
            Assert.That(
                error.Error,
                Does
                    .Contain("child file")
                    .And.Contain("Blake3Hash")
            )

    [<Test>]
    member _.DirectoryVersionHashesAreStableForReorderedChildren() =
        let fileA =
            fileWithHashes
                "/a.txt"
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                1L

        let fileB =
            fileWithHashes
                "/b.txt"
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
                1L

        let first = hashedDirectory directoryVersionId (RelativePath "/") [] [ fileA; fileB ]
        let second = hashedDirectory directoryVersionId (RelativePath "/") [] [ fileB; fileA ]

        Assert.That(second.Sha256Hash, Is.EqualTo(first.Sha256Hash))
        Assert.That(second.Blake3Hash, Is.EqualTo(first.Blake3Hash))
        assertDirectoryHashesValid first []

    [<Test>]
    member _.DirectoryVersionHashesIncludeChildNamesAndMovedDirectoryPath() =
        let sameSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        let sameBlake = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        let fileA = fileWithHashes "/a.txt" sameSha sameBlake 10L
        let fileB = fileWithHashes "/b.txt" sameSha sameBlake 10L

        let namedA = hashedDirectory directoryVersionId (RelativePath "/") [] [ fileA ]
        let namedB = hashedDirectory directoryVersionId (RelativePath "/") [] [ fileB ]
        let movedA = hashedDirectory directoryVersionId (RelativePath "/moved/") [] [ fileA ]

        Assert.That(namedB.Sha256Hash, Is.Not.EqualTo(namedA.Sha256Hash))
        Assert.That(namedB.Blake3Hash, Is.Not.EqualTo(namedA.Blake3Hash))
        Assert.That(movedA.Sha256Hash, Is.Not.EqualTo(namedA.Sha256Hash))
        Assert.That(movedA.Blake3Hash, Is.Not.EqualTo(namedA.Blake3Hash))

    [<Test>]
    member _.DirectoryVersionHashesPreserveManifestBackedSiblingHashData() =
        let manifest = finalizedManifest ()
        let manifestBackedFile = manifestFile manifest
        manifestBackedFile.Blake3Hash <- Blake3Hash manifest.FileContentHash

        let changedFile =
            fileWithHashes
                "/changed.txt"
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
                12L

        let directoryVersion = hashedDirectory directoryVersionId (RelativePath "/") [] [ manifestBackedFile; changedFile ]

        assertDirectoryHashesValid directoryVersion []

        Assert.That(
            directoryVersion.Files[0]
                .ContentReference
                .ReferenceType,
            Is.EqualTo(FileContentReferenceType.FileManifest)
        )

    [<Test>]
    member _.ManifestSaveBoundaryRejectsAbsentContentBlockMetadataRanges() =
        let manifest = finalizedManifest ()

        let getRangePresence _ _ _ = Task.FromResult ContentBlockRangePresence.Absent

        match (DirectoryVersionActor.validateManifestReferencesForSaveBoundaryWithResolver getRangePresence "corr-absent-block" [ manifest ])
            .Result
            with
        | Ok () -> Assert.Fail("Expected manifest-backed save to reject absent ContentBlock metadata before Save.")
        | Error error -> Assert.That(error.Error, Does.Contain("no finalized ContentBlock metadata range exists"))

    [<Test>]
    member _.ManifestSaveBoundaryAcceptsExistingReclaimableContentBlockMetadataRanges() =
        let manifest = finalizedManifest ()

        let getRangePresence _ _ _ = Task.FromResult ContentBlockRangePresence.Reclaimable

        match (DirectoryVersionActor.validateManifestReferencesForSaveBoundaryWithResolver getRangePresence "corr-reclaimable-block" [ manifest ])
            .Result
            with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected existing ContentBlock metadata to be accepted before Save, got {error.Error}.")

    [<Test>]
    member _.ManifestSaveBoundaryChecksEachContentBlockAtOrdinalZero() =
        let manifest = finalizedManifestWithBlockCopies 2
        let queries = ResizeArray<ContentBlockRangeQuery>()

        let getRangePresence _ _ query =
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
    member _.ManifestSaveBoundaryKeysRangePresenceByPoolAndContentBlockAddress() =
        let poolA = StoragePoolId "pool-a"
        let poolB = StoragePoolId "pool-b"
        let manifestA = finalizedManifestWithStoragePool poolA
        let manifestB = { appendRepeatedBlock manifestA with StoragePoolId = poolB }
        let sharedAddress = manifestA.Blocks[0].Address
        let lookups = ResizeArray<StoragePoolId * ContentBlockAddress>()

        let getRangePresence storagePoolId contentBlockAddress _ =
            lookups.Add((storagePoolId, contentBlockAddress))

            if storagePoolId = poolA
               && contentBlockAddress = sharedAddress then
                Task.FromResult ContentBlockRangePresence.Absent
            else
                Task.FromResult ContentBlockRangePresence.Reclaimable

        match (DirectoryVersionActor.validateManifestReferencesForSaveBoundaryWithResolver
                   getRangePresence
                   "corr-duplicate-block-pools"
                   [ manifestA; manifestB ])
            .Result
            with
        | Ok () -> Assert.Fail("Expected save validation to reject the manifest whose own pool lacks finalized metadata.")
        | Error error ->
            Assert.That(error.Error, Does.Contain($"{manifestA.ManifestAddress}"))
            Assert.That(lookups, Does.Contain((poolA, sharedAddress)))

    [<Test>]
    member _.ManifestSaveBoundaryUsesResolvedStoragePoolForContentBlockMetadata() =
        let contentBlockAddress = ContentBlockAddress "block-address"
        let expected = $"{storagePoolId}|{contentBlockAddress}"

        let actual = DirectoryVersionActor.contentBlockMetadataActorKeyForSaveBoundary storagePoolId contentBlockAddress

        Assert.That(actual, Is.EqualTo(expected))

    [<Test>]
    member _.SaveBoundaryAcceptsFinalizedManifestAfterDurableIncrementIntent() =
        let manifest = finalizedManifest ()
        let directoryVersion = directoryWith [ manifestFile manifest ]

        let plan =
            ReferenceActor.planManifestSaveBoundary repositoryId storagePoolId referenceId directoryVersion "corr-finalized"
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
            ReferenceActor.planManifestSaveBoundary repositoryId storagePoolId referenceId directoryVersion "corr-replay"
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
            ReferenceActor.planManifestSaveBoundary repositoryId storagePoolId referenceId directoryVersion "corr-fanout"
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
        let expectedStoragePoolId = manifest.StoragePoolId

        let plan =
            ReferenceActor.planManifestSaveBoundary repositoryId storagePoolId referenceId directoryVersion "corr-repeated-block"
            |> expectPlan

        Assert.That(plan.WorkflowRanges, Has.Length.EqualTo(1))

        Assert.That(
            plan.WorkflowRanges
            |> Seq.forall (fun range ->
                range.StoragePoolId = expectedStoragePoolId
                && range.OrdinalStart = 0
                && range.OrdinalCount = 1),
            Is.True
        )

        let intent = RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, manifest.ManifestAddress)

        match ReferenceActor.tryCreateManifestContributionStart plan intent with
        | Some startCommand ->
            let workflowDecision =
                ManifestContributionWorkflowActor.decideCommand [] ManifestContributionWorkflowDto.Default startCommand (metadata "corr-repeated-workflow")

            match workflowDecision with
            | Ok decision -> Assert.That(ManifestContributionWorkflowActor.pendingRanges decision.Workflow, Has.Length.EqualTo(1))
            | Error error -> Assert.Fail($"Expected repeated block occurrences to start contribution workflow, got {error.Error}.")
        | None -> Assert.Fail("Expected increment intent to start manifest contribution workflow fan-out.")

    [<Test>]
    member _.SaveBoundaryContributionWorkflowRangesUseEachManifestStoragePool() =
        let routePool = StoragePoolId "pool-after-route-change"
        let poolA = StoragePoolId "pool-a-before-route-change"
        let poolB = StoragePoolId "pool-b-before-route-change"
        let manifestA = finalizedManifestWithStoragePool poolA
        let manifestB = finalizedManifestWithStoragePool poolB

        let directoryVersion =
            directoryWith [ manifestFileAt "/a.bin" manifestA
                            manifestFileAt "/b.bin" manifestB ]

        match ReferenceActor.planManifestSaveBoundary repositoryId routePool referenceId directoryVersion "corr-mixed-manifest-pools" with
        | Error error -> Assert.Fail($"Expected mixed-pool manifest save planning to succeed, got {error.Error}.")
        | Ok plans ->
            Assert.That(plans |> Seq.length, Is.EqualTo(2))

            let plannedPools =
                plans
                |> Seq.map (fun plan ->
                    plan.Manifest.ManifestAddress,
                    plan.WorkflowRanges
                    |> Seq.map (fun range -> range.StoragePoolId)
                    |> Seq.distinct
                    |> Seq.toArray)
                |> dict

            Assert.That(plannedPools[manifestA.ManifestAddress], Has.Length.EqualTo(1))
            Assert.That(plannedPools[manifestA.ManifestAddress][0], Is.EqualTo(poolA))
            Assert.That(plannedPools[manifestB.ManifestAddress], Has.Length.EqualTo(1))
            Assert.That(plannedPools[manifestB.ManifestAddress][0], Is.EqualTo(poolB))

            Assert.That(
                plans
                |> Seq.collect (fun plan -> plan.WorkflowRanges)
                |> Seq.exists (fun range -> range.StoragePoolId = routePool),
                Is.False
            )

    [<Test>]
    member _.SaveExpiryStartsDecrementContributionWorkflow() =
        let manifest = finalizedManifest ()
        let directoryVersion = directoryWith [ manifestFile manifest ]

        let savePlan =
            ReferenceActor.planManifestSaveBoundary repositoryId storagePoolId referenceId directoryVersion "corr-expiry-plan"
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
    member _.SaveExpiryRequiresDurableStoragePoolEvidenceButRangesUseManifestPool() =
        let originalPool = StoragePoolId "pool-before-route-change"
        let currentRepositoryPool = StoragePoolId "pool-after-route-change"
        let manifest = finalizedManifest ()
        let directoryVersion = directoryWith [ manifestFile manifest ]
        let createdEvent = saveCreatedEventWithStoragePool originalPool

        Assert.That(ReferenceActor.tryGetSaveStoragePoolIdFromCreatedEvent createdEvent, Is.EqualTo(Some originalPool))

        match ReferenceActor.requireSaveStoragePoolId "corr-expiry-missing" referenceId None with
        | Ok pool -> Assert.Fail($"Expected missing save StoragePoolId evidence to fail closed, got {pool}.")
        | Error error -> Assert.That(error.Error, Does.Contain("does not have a recorded StoragePoolId"))

        let expiryPlan =
            ReferenceActor.planManifestSaveExpiryBoundary repositoryId originalPool referenceId directoryVersion "corr-expiry-original-pool"
            |> expectPlan

        Assert.That(currentRepositoryPool, Is.Not.EqualTo(originalPool))
        Assert.That(manifest.StoragePoolId, Is.Not.EqualTo(originalPool))
        Assert.That(manifest.StoragePoolId, Is.Not.EqualTo(currentRepositoryPool))
        Assert.That(expiryPlan.WorkflowRanges, Has.Length.EqualTo(manifest.Blocks.Count))

        Assert.That(
            expiryPlan.WorkflowRanges
            |> Seq.forall (fun range -> range.StoragePoolId = manifest.StoragePoolId),
            Is.True
        )

        Assert.That(
            expiryPlan.WorkflowRanges
            |> Seq.exists (fun range -> range.StoragePoolId = currentRepositoryPool),
            Is.False
        )

        Assert.That(
            expiryPlan.WorkflowRanges
            |> Seq.exists (fun range -> range.StoragePoolId = originalPool),
            Is.False
        )

    [<Test>]
    member _.SaveExpiryDecrementWorkflowUsesDistinctOperationIdAfterIncrementWorkflow() =
        let manifest = finalizedManifest ()
        let directoryVersion = directoryWith [ manifestFile manifest ]

        let savePlan =
            ReferenceActor.planManifestSaveBoundary repositoryId storagePoolId referenceId directoryVersion "corr-expiry-operation-plan"
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
            ReferenceActor.planManifestSaveBoundary repositoryId storagePoolId referenceId directoryVersion "corr-expiry-replay-plan"
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
            ReferenceActor.planManifestSaveBoundary repositoryId storagePoolId referenceId directoryVersion "corr-expiry-gc-plan"
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

        match ReferenceActor.planManifestSaveBoundary repositoryId storagePoolId referenceId directoryVersion "corr-whole-file-expiry" with
        | Ok plans -> Assert.That(plans, Is.Empty)
        | Error error -> Assert.Fail($"Expected whole-file save expiry planning to remain a no-op, got {error.Error}.")

    [<Test>]
    member _.PhysicalDeletionReminderAppliesExpiryBoundaryOnlyForLiveSaveReferences() =
        Assert.That(ReferenceActor.shouldApplySaveExpiryBoundary (referenceDto ReferenceType.Save), Is.True)
        Assert.That(ReferenceActor.shouldApplySaveExpiryBoundary (referenceDto ReferenceType.Checkpoint), Is.False)
        Assert.That(ReferenceActor.shouldApplySaveExpiryBoundary Grace.Types.Reference.ReferenceDto.Default, Is.False)
