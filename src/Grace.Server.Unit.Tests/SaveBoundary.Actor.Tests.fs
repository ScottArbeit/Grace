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
open System.IO
open System.Text
open System.Threading.Tasks

module DirectoryVersionActor = Grace.Actors.DirectoryVersion

module ManifestContributionWorkflowActor = Grace.Actors.ManifestContributionWorkflow

module ReferenceActor = Grace.Actors.Reference

module RepositoryContentCounterActor = Grace.Actors.RepositoryContentCounter

/// Covers save Boundary Actor behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type SaveBoundaryActorTests() =

    let timestamp = Instant.FromUtc(2026, 5, 24, 16, 0)
    let ownerId = Guid.Parse("06fed2a6-8de5-4ef2-86b7-e465448cf77d")
    let organizationId = Guid.Parse("5a602145-3f0a-47c8-bc7c-f6618425c07f")
    let repositoryId = Guid.Parse("e34f8949-6306-4fb1-89ca-e9eb831022b0")
    let directoryVersionId = Guid.Parse("29e93e9b-3e5f-4b6e-b8c3-2a964d8d33f3")
    let childDirectoryVersionId = Guid.Parse("599af9a9-cb67-49cb-a6df-7a2b5b63ff78")
    let referenceId = Guid.Parse("9b26f91a-fd44-46b3-9cc7-17645bb388a2")
    let storagePoolId = StoragePoolId Constants.DefaultStoragePoolId
    let archiveStoragePoolId = StoragePoolId "pool-archive"

    /// Constructs metadata fixtures used by the server unit save Boundary Actor assertions.
    let metadata correlationId =
        {
            Timestamp = timestamp
            CorrelationId = correlationId
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    /// Builds finalized Manifest With Block Copies test data for the server unit save Boundary Actor scenarios in this file.
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

    let finalizedManifestInPool storagePoolId = { finalizedManifest () with StoragePoolId = storagePoolId }

    /// Builds manifest File At test data for the server unit save Boundary Actor scenarios in this file.
    let manifestFileAt relativePath (manifest: FileManifest) =
        let fileVersion =
            FileVersion.CreateWithHashes
                relativePath
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                (Blake3Hash manifest.FileContentHash)
                String.Empty
                true
                manifest.Size

        fileVersion.ContentReference <- FileContentReference.FileManifest manifest
        fileVersion

    let manifestFile (manifest: FileManifest) = manifestFileAt "/large.bin" manifest

    /// Builds manifest Reference test data for the server unit save Boundary Actor scenarios in this file.
    let manifestReference (manifest: FileManifest) : DirectoryVersionActor.ManifestReferenceForSaveBoundary =
        { Manifest = manifest; AuthorizedScope = RelativePath "/large.bin" }

    let wholeFile () =
        FileVersion.CreateWithHashes
            "/small.txt"
            "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd"
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            String.Empty
            false
            42L

    /// Builds file With Hashes test data for the server unit save Boundary Actor scenarios in this file.
    let fileWithHashes (relativePath: string) (sha256Hash: string) (blake3Hash: string) size =
        FileVersion.CreateWithHashes (RelativePath relativePath) (Sha256Hash sha256Hash) (Blake3Hash blake3Hash) String.Empty false size

    /// Builds reference Dto test data for the server unit save Boundary Actor scenarios in this file.
    let referenceDto referenceType =
        { Grace.Types.Reference.ReferenceDto.Default with
            ReferenceId = referenceId
            RepositoryId = repositoryId
            DirectoryId = directoryVersionId
            ReferenceType = referenceType
        }

    /// Builds directory With test data for the server unit save Boundary Actor scenarios in this file.
    let directoryWith (files: FileVersion seq) =
        let fileList = List<FileVersion>(files)

        Grace.Types.Common.DirectoryVersion.CreateWithHashes
            directoryVersionId
            ownerId
            organizationId
            repositoryId
            (RelativePath "/")
            (Sha256Hash "fedcbafedcbafedcbafedcbafedcbafedcbafedcbafedcbafedcbafedcba")
            (Blake3Hash "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd")
            (List<DirectoryVersionId>())
            fileList
            (fileList
             |> Seq.sumBy (fun fileVersion -> fileVersion.Size))

    /// Builds hashed Directory test data for the server unit save Boundary Actor scenarios in this file.
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

    /// Asserts the directory Hashes Valid condition so failures identify the violated server unit save Boundary Actor invariant.
    let assertDirectoryHashesValid directoryVersion childDirectories =
        match DirectoryVersionActor.validateDirectoryVersionHashesWithChildren "corr-directory-hash" directoryVersion childDirectories with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected DirectoryVersion hashes to validate, got {error.Error}.")

    /// Builds expect Plan test data for the server unit save Boundary Actor scenarios in this file.
    let expectPlan result =
        match result with
        | Ok plans -> plans |> Seq.exactlyOne
        | Error error ->
            Assert.Fail($"Expected save boundary plan to succeed, got {error.Error}.")
            Unchecked.defaultof<ReferenceActor.ManifestSaveContributionPlan>

    /// Builds expect Plans test data for the server unit save Boundary Actor scenarios in this file.
    let expectPlans result =
        match result with
        | Ok plans -> plans |> List.toArray
        | Error error ->
            Assert.Fail($"Expected save boundary plans to succeed, got {error.Error}.")
            Array.empty

    /// Verifies that save Boundary Rejects Unfinalized Manifest References.
    [<Test>]
    member _.SaveBoundaryRejectsUnfinalizedManifestReferences() =
        let unfinalizedManifest = { finalizedManifest () with ManifestAddress = ManifestAddress String.Empty }
        let directoryVersion = directoryWith [ manifestFile unfinalizedManifest ]

        let result = ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-unfinalized"

        match result with
        | Ok _ -> Assert.Fail("Expected an unfinalized manifest reference to reject before save publication.")
        | Error error -> Assert.That(error.Error, Does.Contain("must be finalized before Save"))

    /// Verifies that directory Version Whole File Validation Set Ignores Finalized Manifest Backed Files.
    [<Test>]
    member _.DirectoryVersionWholeFileValidationSetIgnoresFinalizedManifestBackedFiles() =
        let manifest = finalizedManifest ()
        let wholeFile = wholeFile ()
        let files = List<FileVersion>([ wholeFile; manifestFile manifest ])

        let filesToValidate = DirectoryVersionActor.getFilesToValidateForSaveBoundary files (List<FileVersion>())

        Assert.That(filesToValidate, Has.Length.EqualTo(1))
        Assert.That(filesToValidate[0].RelativePath, Is.EqualTo(wholeFile.RelativePath))

    /// Verifies that manifest Save Boundary Rejects Mismatched File Blake3 When Present.
    [<Test>]
    member _.ManifestSaveBoundaryRejectsMismatchedFileBlake3WhenPresent() =
        let manifest = finalizedManifest ()
        let fileVersion = manifestFile manifest
        fileVersion.Blake3Hash <- Blake3Hash "wrong-blake3"

        match DirectoryVersionActor.validateManifestBackedFileForSaveBoundary "corr-manifest-blake3-mismatch" fileVersion manifest with
        | Ok () -> Assert.Fail("Expected manifest-backed FileVersion BLAKE3 mismatch to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("FileVersion.Blake3Hash"))

    /// Verifies that manifest save rejects an empty file BLAKE3 hash.
    [<Test>]
    member _.ManifestSaveBoundaryRejectsEmptyFileBlake3() =
        let manifest = finalizedManifest ()
        let fileVersion = manifestFile manifest
        fileVersion.Blake3Hash <- Blake3Hash String.Empty

        match DirectoryVersionActor.validateManifestBackedFileForSaveBoundary "corr-manifest-empty-blake3" fileVersion manifest with
        | Ok () -> Assert.Fail("Expected an empty FileVersion BLAKE3 hash to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("FileVersion.Blake3Hash"))

    /// Verifies that directory Version Hash Validation Rejects New Manifest Backed File Blake3 Gap.
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

    /// Verifies that a previously validated file cannot grandfather an empty BLAKE3 hash.
    [<Test>]
    member _.DirectoryVersionHashValidationRejectsPreviouslyValidatedManifestFileWithoutBlake3() =
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
        | Ok () -> Assert.Fail("Expected a previously validated FileVersion without BLAKE3 to be rejected.")
        | Error error ->
            Assert.That(
                error.Error,
                Does
                    .Contain("child file")
                    .And.Contain("Blake3Hash")
            )

    /// Verifies that directory Version Hash Validation Requires Directory Blake3 Before Save.
    [<Test>]
    member _.DirectoryVersionHashValidationRequiresDirectoryBlake3BeforeSave() =
        let directoryVersion = hashedDirectory directoryVersionId (RelativePath "/") [] []
        directoryVersion.Blake3Hash <- Blake3Hash String.Empty

        match DirectoryVersionActor.validateDirectoryVersionHashesWithChildren "corr-missing-directory-blake3" directoryVersion [] with
        | Ok () -> Assert.Fail("Expected missing DirectoryVersion.Blake3Hash to reject before Save.")
        | Error error -> Assert.That(error.Error, Does.Contain("DirectoryVersion.Blake3Hash"))

    /// Verifies that directory Version Hash Validation Rejects Mismatched Directory Blake3 Before Save.
    [<Test>]
    member _.DirectoryVersionHashValidationRejectsMismatchedDirectoryBlake3BeforeSave() =
        let directoryVersion = hashedDirectory directoryVersionId (RelativePath "/") [] []
        directoryVersion.Blake3Hash <- Blake3Hash "0000000000000000000000000000000000000000000000000000000000000000"

        match DirectoryVersionActor.validateDirectoryVersionHashesWithChildren "corr-mismatched-directory-blake3" directoryVersion [] with
        | Ok () -> Assert.Fail("Expected mismatched DirectoryVersion.Blake3Hash to reject before Save.")
        | Error error -> Assert.That(error.Error, Does.Contain("mismatched Blake3Hash"))

    /// Verifies that directory Version Hash Validation Rejects Mismatched Directory Sha256 Before Save.
    [<Test>]
    member _.DirectoryVersionHashValidationRejectsMismatchedDirectorySha256BeforeSave() =
        let directoryVersion = hashedDirectory directoryVersionId (RelativePath "/") [] []
        directoryVersion.Sha256Hash <- Sha256Hash "0000000000000000000000000000000000000000000000000000000000000000"

        match DirectoryVersionActor.validateDirectoryVersionHashesWithChildren "corr-mismatched-directory-sha" directoryVersion [] with
        | Ok () -> Assert.Fail("Expected mismatched DirectoryVersion.Sha256Hash to reject before Save.")
        | Error error -> Assert.That(error.Error, Does.Contain("mismatched Sha256Hash"))

    /// Verifies that directory Version Hash Validation Normalizes Json Omitted Zero Size Before Save.
    [<Test>]
    member _.DirectoryVersionHashValidationNormalizesJsonOmittedZeroSizeBeforeSave() =
        let directoryVersion = hashedDirectory directoryVersionId (RelativePath "/empty/") [] []
        directoryVersion.Size <- Constants.InitialDirectorySize

        let normalizedDirectoryVersion = DirectoryVersionActor.normalizeDirectoryVersionForSaveBoundary directoryVersion

        Assert.That(normalizedDirectoryVersion.Size, Is.EqualTo(0L))
        assertDirectoryHashesValid normalizedDirectoryVersion []

    /// Verifies that directory Version Hash Validation Rejects Missing Child Directory Blake3.
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

    /// Verifies that HashesValidated cannot bypass a missing child directory BLAKE3 hash.
    [<Test>]
    member _.DirectoryVersionHashValidationRejectsValidatedChildDirectoryWithoutBlake3() =
        let child = hashedDirectory (Guid.NewGuid()) (RelativePath "/src/") [] []
        child.HashesValidated <- true
        child.Blake3Hash <- Blake3Hash String.Empty
        let parent = hashedDirectory directoryVersionId (RelativePath "/") [ child ] []

        match DirectoryVersionActor.validateDirectoryVersionHashesWithChildren "corr-legacy-child-directory-blake3" parent [ child ] with
        | Ok () -> Assert.Fail("Expected HashesValidated not to bypass a missing child DirectoryVersion BLAKE3 hash.")
        | Error error ->
            Assert.That(
                error.Error,
                Does
                    .Contain("child directory")
                    .And.Contain("Blake3Hash")
            )

    /// Verifies that directory Version Hash Validation Rejects New Whole File Blake3 Gap.
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

    /// Verifies that a previous whole-file record cannot grandfather an empty BLAKE3 hash.
    [<Test>]
    member _.DirectoryVersionHashValidationRejectsPreviouslyValidatedWholeFileWithoutBlake3() =
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
        | Ok () -> Assert.Fail("Expected a previously validated whole-file record without BLAKE3 to be rejected.")
        | Error error ->
            Assert.That(
                error.Error,
                Does
                    .Contain("child file")
                    .And.Contain("Blake3Hash")
            )

    /// Verifies that directory Version Hash Validation Rejects Missing Manifest Child File Blake3.
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

    /// Verifies that directory Version Hashes Are Stable For Reordered Children.
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

    /// Verifies that directory Version Hashes Include Child Names And Moved Directory Path.
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

    /// Verifies that directory Version Hashes Preserve Manifest Backed Sibling Hash Data.
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

    /// Verifies that manifest Save Boundary Rejects Absent Content Block Metadata Ranges.
    [<Test>]
    member _.ManifestSaveBoundaryRejectsAbsentContentBlockMetadataRanges() =
        let manifest = finalizedManifest ()

        let getRangePresence _ _ _ _ _ _ = Task.FromResult ContentBlockRangePresence.Absent

        match (DirectoryVersionActor.validateManifestReferencesForSaveBoundaryWithResolver
                   getRangePresence
                   repositoryId
                   "corr-absent-block"
                   [ manifestReference manifest ])
            .Result
            with
        | Ok () -> Assert.Fail("Expected manifest-backed save to reject absent ContentBlock metadata before Save.")
        | Error error -> Assert.That(error.Error, Does.Contain("no finalized scoped ContentBlock metadata range exists"))

    /// Verifies that manifest Save Boundary Accepts Existing Reclaimable Content Block Metadata Ranges.
    [<Test>]
    member _.ManifestSaveBoundaryAcceptsExistingReclaimableContentBlockMetadataRanges() =
        let manifest = finalizedManifest ()

        let getRangePresence _ _ _ _ _ _ = Task.FromResult ContentBlockRangePresence.Reclaimable

        match (DirectoryVersionActor.validateManifestReferencesForSaveBoundaryWithResolver
                   getRangePresence
                   repositoryId
                   "corr-reclaimable-block"
                   [ manifestReference manifest ])
            .Result
            with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected existing ContentBlock metadata to be accepted before Save, got {error.Error}.")

    /// Verifies that manifest Save Boundary Checks Each Content Block At Ordinal Zero.
    [<Test>]
    member _.ManifestSaveBoundaryChecksEachContentBlockAtOrdinalZero() =
        let manifest = finalizedManifestWithBlockCopies 2
        let queries = ResizeArray<ContentBlockRangeQuery>()

        /// Extracts range Presence from the scenario result so assertions stay focused on server unit save Boundary Actor behavior.
        let getRangePresence _ _ _ _ _ query =
            queries.Add(query)
            Task.FromResult ContentBlockRangePresence.Reclaimable

        match (DirectoryVersionActor.validateManifestReferencesForSaveBoundaryWithResolver
                   getRangePresence
                   repositoryId
                   "corr-ordinal-zero"
                   [ manifestReference manifest ])
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

    /// Verifies that manifest Save Boundary Uses Manifest Storage Pool For Content Block Metadata.
    [<Test>]
    member _.ManifestSaveBoundaryUsesManifestStoragePoolForContentBlockMetadata() =
        let contentBlockAddress = ContentBlockAddress "block-address"
        let expected = $"{archiveStoragePoolId}|{contentBlockAddress}"

        let actual = DirectoryVersionActor.contentBlockMetadataActorKeyForSaveBoundary archiveStoragePoolId contentBlockAddress

        Assert.That(actual, Is.EqualTo(expected))

    /// Verifies that manifest Save Boundary Resolver Uses Stored Manifest Pool After Route Drift.
    [<Test>]
    member _.ManifestSaveBoundaryResolverUsesStoredManifestPoolAfterRouteDrift() =
        let manifest = finalizedManifestInPool archiveStoragePoolId
        let requestedPools = ResizeArray<StoragePoolId>()

        /// Extracts range Presence from the scenario result so assertions stay focused on server unit save Boundary Actor behavior.
        let getRangePresence storagePoolId _ _ _ _ _ =
            requestedPools.Add(storagePoolId)
            Task.FromResult ContentBlockRangePresence.Reclaimable

        match (DirectoryVersionActor.validateManifestReferencesForSaveBoundaryWithResolver
                   getRangePresence
                   repositoryId
                   "corr-route-drift"
                   [ manifestReference manifest ])
            .Result
            with
        | Ok () -> Assert.That(requestedPools, Is.EquivalentTo([ archiveStoragePoolId ]))
        | Error error -> Assert.Fail($"Expected manifest-stored pool to validate despite repository route drift, got {error.Error}.")

    /// Verifies that manifest Save Boundary Resolver Carries Repository Scope And Manifest Pool Before Range Trust.
    [<Test>]
    member _.ManifestSaveBoundaryResolverCarriesRepositoryScopeAndManifestPoolBeforeRangeTrust() =
        let manifest = finalizedManifestInPool archiveStoragePoolId
        let observed = ResizeArray<StoragePoolId * RepositoryId * RelativePath * ManifestAddress * ContentBlockAddress>()

        /// Extracts range Presence from the scenario result so assertions stay focused on server unit save Boundary Actor behavior.
        let getRangePresence storagePoolId repositoryId authorizedScope manifestAddress contentBlockAddress _ =
            observed.Add((storagePoolId, repositoryId, authorizedScope, manifestAddress, contentBlockAddress))
            Task.FromResult ContentBlockRangePresence.Absent

        match (DirectoryVersionActor.validateManifestReferencesForSaveBoundaryWithResolver
                   getRangePresence
                   repositoryId
                   "corr-scoped-pool-proof"
                   [ manifestReference manifest ])
            .Result
            with
        | Ok () -> Assert.Fail("Expected missing scoped finalization evidence to reject before trusting the recorded pool.")
        | Error error ->
            let block = manifest.Blocks[0]

            Assert.That(
                error.Error,
                Does
                    .Contain("repository")
                    .And.Contain("authorized scope")
            )

            Assert.That(
                observed,
                Is.EquivalentTo(
                    [
                        (archiveStoragePoolId, repositoryId, RelativePath "/large.bin", manifest.ManifestAddress, block.Address)
                    ]
                )
            )

    /// Verifies that save Boundary Accepts Finalized Manifest After Durable Increment Intent.
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

            Assert.That(
                decision.Intents[0],
                Is.EqualTo(RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, storagePoolId, manifest.ManifestAddress))
            )
        | Error error -> Assert.Fail($"Expected repository content counter increment to succeed, got {error.Error}.")

    /// Verifies that save Boundary Restarts Contribution Workflow After Counter Replay.
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

    /// Verifies that save Boundary Starts Pending Contribution Workflow Without Waiting For Range Completion.
    [<Test>]
    member _.SaveBoundaryStartsPendingContributionWorkflowWithoutWaitingForRangeCompletion() =
        let manifest = finalizedManifest ()
        let directoryVersion = directoryWith [ manifestFile manifest ]

        let plan =
            ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-fanout"
            |> expectPlan

        let intent = RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, storagePoolId, manifest.ManifestAddress)

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

    /// Verifies that reference Boundary Plans Commit And Checkpoint Manifest Ownership.
    [<Test>]
    member _.ReferenceBoundaryPlansCommitAndCheckpointManifestOwnership() =
        let manifest = finalizedManifest ()
        let directoryVersion = directoryWith [ manifestFile manifest ]

        let commitPlan =
            ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-commit"
            |> expectPlan

        Assert.That(commitPlan.Manifest.StoragePoolId, Is.EqualTo(storagePoolId))
        Assert.That(commitPlan.WorkflowRanges[0].StoragePoolId, Is.EqualTo(storagePoolId))

        let checkpointPlan =
            ReferenceActor.planManifestSaveExpiryBoundary repositoryId referenceId directoryVersion "corr-checkpoint-expiry"
            |> expectPlan

        match checkpointPlan.CounterCommand with
        | RepositoryContentCounterCommand.RemoveReference (_, _, commandStoragePoolId, manifestAddress) ->
            Assert.That(commandStoragePoolId, Is.EqualTo(storagePoolId))
            Assert.That(manifestAddress, Is.EqualTo(manifest.ManifestAddress))
        | _ -> Assert.Fail("Expected checkpoint expiry planning to remove manifest ownership.")

    /// Verifies that reference Boundary Plans Nested Child Directory Manifest Ownership By Stored Pool.
    [<Test>]
    member _.ReferenceBoundaryPlansNestedChildDirectoryManifestOwnershipByStoredPool() =
        let rootManifest = finalizedManifest ()
        let childManifest = finalizedManifestInPool archiveStoragePoolId

        let childDirectory =
            hashedDirectory
                childDirectoryVersionId
                (RelativePath "/src/")
                []
                [
                    manifestFileAt "/src/large.bin" childManifest
                ]

        let rootDirectory = hashedDirectory directoryVersionId (RelativePath "/") [ childDirectory ] [ manifestFile rootManifest ]

        let plans =
            ReferenceActor.planManifestSaveBoundaryForDirectoryVersions repositoryId referenceId [ rootDirectory; childDirectory ] "corr-recursive-plan"
            |> expectPlans

        Assert.That(plans, Has.Length.EqualTo(2))

        Assert.That(
            plans
            |> Seq.map (fun plan -> plan.Manifest.StoragePoolId, plan.Manifest.ManifestAddress),
            Is.EquivalentTo(
                [
                    storagePoolId, rootManifest.ManifestAddress
                    archiveStoragePoolId, childManifest.ManifestAddress
                ]
            )
        )

        let childPlan =
            plans
            |> Seq.find (fun plan -> plan.Manifest.ManifestAddress = childManifest.ManifestAddress)

        Assert.That(childPlan.Manifest.StoragePoolId, Is.EqualTo(archiveStoragePoolId))

        Assert.That(
            childPlan.WorkflowRanges
            |> Seq.forall (fun range -> range.StoragePoolId = archiveStoragePoolId),
            Is.True
        )

        match childPlan.CounterCommand with
        | RepositoryContentCounterCommand.AddReference (_, _, commandStoragePoolId, manifestAddress) ->
            Assert.That(commandStoragePoolId, Is.EqualTo(archiveStoragePoolId))
            Assert.That(manifestAddress, Is.EqualTo(childManifest.ManifestAddress))
        | _ -> Assert.Fail("Expected nested child manifest planning to add stored-pool manifest ownership.")

    /// Verifies that recursive Save Boundary Planning Rejects Missing Root Directory Version.
    [<Test>]
    member _.RecursiveSaveBoundaryPlanningRejectsMissingRootDirectoryVersion() =
        let childManifest = finalizedManifestInPool archiveStoragePoolId

        let childDirectory =
            hashedDirectory
                childDirectoryVersionId
                (RelativePath "/src/")
                []
                [
                    manifestFileAt "/src/large.bin" childManifest
                ]

        let result =
            ReferenceActor.planManifestSaveBoundaryForRecursiveDirectoryVersions
                repositoryId
                referenceId
                directoryVersionId
                [ childDirectory ]
                "corr-recursive-missing-root"

        match result with
        | Ok _ -> Assert.Fail("Expected recursive save boundary planning to reject traversal results without the root directory.")
        | Error error -> Assert.That(error.Error, Does.Contain("did not include the root DirectoryVersion"))

    /// Verifies that recursive Save Boundary Planning Rejects Declared Child Missing From Traversal.
    [<Test>]
    member _.RecursiveSaveBoundaryPlanningRejectsDeclaredChildMissingFromTraversal() =
        let rootManifest = finalizedManifest ()
        let childManifest = finalizedManifestInPool archiveStoragePoolId

        let childDirectory =
            hashedDirectory
                childDirectoryVersionId
                (RelativePath "/src/")
                []
                [
                    manifestFileAt "/src/large.bin" childManifest
                ]

        let rootDirectory = hashedDirectory directoryVersionId (RelativePath "/") [ childDirectory ] [ manifestFile rootManifest ]

        let result =
            ReferenceActor.planManifestSaveBoundaryForRecursiveDirectoryVersions
                repositoryId
                referenceId
                directoryVersionId
                [ rootDirectory ]
                "corr-recursive-missing-child"

        match result with
        | Ok _ -> Assert.Fail("Expected recursive save boundary planning to reject traversal results missing a declared child directory.")
        | Error error -> Assert.That(error.Error, Does.Contain("did not include a declared child DirectoryVersion"))

    /// Verifies that reference Boundary Deduplicates Recursive Manifest References By Stored Pool And Manifest Address.
    [<Test>]
    member _.ReferenceBoundaryDeduplicatesRecursiveManifestReferencesByStoredPoolAndManifestAddress() =
        let manifest = finalizedManifestInPool archiveStoragePoolId

        let childDirectory =
            hashedDirectory
                childDirectoryVersionId
                (RelativePath "/src/")
                []
                [
                    manifestFileAt "/src/large.bin" manifest
                ]

        let rootDirectory = hashedDirectory directoryVersionId (RelativePath "/") [ childDirectory ] [ manifestFile manifest ]

        let plans =
            ReferenceActor.planManifestSaveBoundaryForDirectoryVersions
                repositoryId
                referenceId
                [ rootDirectory; childDirectory ]
                "corr-recursive-duplicate-plan"
            |> expectPlans

        Assert.That(plans, Has.Length.EqualTo(1))
        Assert.That(plans[0].Manifest.StoragePoolId, Is.EqualTo(archiveStoragePoolId))
        Assert.That(plans[0].Manifest.ManifestAddress, Is.EqualTo(manifest.ManifestAddress))

    /// Verifies that save Expiry Planning Matches Recursive Save Manifest Keys By Stored Pool.
    [<Test>]
    member _.SaveExpiryPlanningMatchesRecursiveSaveManifestKeysByStoredPool() =
        let rootManifest = finalizedManifest ()
        let childManifest = finalizedManifestInPool archiveStoragePoolId

        let childDirectory =
            hashedDirectory
                childDirectoryVersionId
                (RelativePath "/src/")
                []
                [
                    manifestFileAt "/src/large.bin" childManifest
                ]

        let rootDirectory = hashedDirectory directoryVersionId (RelativePath "/") [ childDirectory ] [ manifestFile rootManifest ]
        let recursiveDirectoryVersions = [ rootDirectory; childDirectory ]

        let savePlans =
            ReferenceActor.planManifestSaveBoundaryForDirectoryVersions repositoryId referenceId recursiveDirectoryVersions "corr-recursive-save-plan"
            |> expectPlans

        let expiryPlans =
            ReferenceActor.planManifestSaveExpiryBoundaryForDirectoryVersions repositoryId referenceId recursiveDirectoryVersions "corr-recursive-expiry-plan"
            |> expectPlans

        let saveKeys =
            savePlans
            |> Seq.map (fun plan -> plan.Manifest.StoragePoolId, plan.Manifest.ManifestAddress)
            |> Seq.toArray

        let expiryKeys =
            expiryPlans
            |> Seq.map (fun plan -> plan.Manifest.StoragePoolId, plan.Manifest.ManifestAddress)
            |> Seq.toArray

        Assert.That(expiryKeys, Is.EquivalentTo(saveKeys))

        Assert.That(
            expiryPlans
            |> Seq.forall (fun plan ->
                match plan.CounterCommand with
                | RepositoryContentCounterCommand.RemoveReference (_, _, commandStoragePoolId, manifestAddress) ->
                    commandStoragePoolId = plan.Manifest.StoragePoolId
                    && manifestAddress = plan.Manifest.ManifestAddress
                | _ -> false),
            Is.True
        )

    /// Verifies that save Boundary Starts Contribution Workflow For Repeated Manifest Block Occurrences.
    [<Test>]
    member _.SaveBoundaryStartsContributionWorkflowForRepeatedManifestBlockOccurrences() =
        let manifest = finalizedManifestWithBlockCopies 2
        let directoryVersion = directoryWith [ manifestFile manifest ]
        let expectedStoragePoolId = manifest.StoragePoolId

        let plan =
            ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-repeated-block"
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

        let intent = RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, storagePoolId, manifest.ManifestAddress)

        match ReferenceActor.tryCreateManifestContributionStart plan intent with
        | Some startCommand ->
            let workflowDecision =
                ManifestContributionWorkflowActor.decideCommand [] ManifestContributionWorkflowDto.Default startCommand (metadata "corr-repeated-workflow")

            match workflowDecision with
            | Ok decision -> Assert.That(ManifestContributionWorkflowActor.pendingRanges decision.Workflow, Has.Length.EqualTo(1))
            | Error error -> Assert.Fail($"Expected repeated block occurrences to start contribution workflow, got {error.Error}.")
        | None -> Assert.Fail("Expected increment intent to start manifest contribution workflow fan-out.")

    /// Verifies that save Expiry Starts Decrement Contribution Workflow.
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
                        RepositoryContentCounterOperationId $"reference-expiry:{referenceId:N}:{storagePoolId}:{manifest.ManifestAddress}",
                        repositoryId,
                        storagePoolId,
                        manifest.ManifestAddress
                    )
            }

        let intent = RepositoryContentCounterIntent.DecrementManifestReferenceCount(repositoryId, storagePoolId, manifest.ManifestAddress)

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

    /// Verifies that save Expiry Decrement Workflow Uses Distinct Operation Id After Increment Workflow.
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
                    (RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, storagePoolId, manifest.ManifestAddress))
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
                        RepositoryContentCounterOperationId $"reference-expiry:{referenceId:N}:{storagePoolId}:{manifest.ManifestAddress}",
                        repositoryId,
                        storagePoolId,
                        manifest.ManifestAddress
                    )
            }

        let decrementStart =
            match
                ReferenceActor.tryCreateManifestContributionStart
                    decrementPlan
                    (RepositoryContentCounterIntent.DecrementManifestReferenceCount(repositoryId, storagePoolId, manifest.ManifestAddress))
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

    /// Verifies that save Expiry Counter Replay Restarts Decrement Workflow Without Second Counter Event.
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
                        RepositoryContentCounterOperationId $"reference-expiry:{referenceId:N}:{storagePoolId}:{manifest.ManifestAddress}",
                        repositoryId,
                        storagePoolId,
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

    /// Verifies that pending Save Expiry Decrement Blocks Unsafe Gc Until Range Completes.
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
                        RepositoryContentCounterOperationId $"reference-expiry:{referenceId:N}:{storagePoolId}:{manifest.ManifestAddress}",
                        repositoryId,
                        storagePoolId,
                        manifest.ManifestAddress
                    )
            }

        let startCommand =
            match
                ReferenceActor.tryCreateManifestContributionStart
                    decrementPlan
                    (RepositoryContentCounterIntent.DecrementManifestReferenceCount(repositoryId, storagePoolId, manifest.ManifestAddress))
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

        let completed =
            match
                ManifestContributionWorkflowActor.decideCommand
                    []
                    started
                    (ManifestContributionWorkflowCommand.RecordRangeSucceeded(
                        ManifestContributionWorkflowOperationId "range-expiry-done",
                        repositoryId,
                        storagePoolId,
                        manifest.ManifestAddress,
                        range
                    ))
                    (metadata "corr-expiry-gc-range")
                with
            | Ok decision -> decision.Workflow
            | Error error ->
                Assert.Fail($"Expected decrement range completion to succeed, got {error.Error}.")
                started

        Assert.That(ManifestContributionWorkflowActor.blocksUnsafeDeletion completed range, Is.False)

    /// Verifies that save Expiry Planning Keeps Whole File Cleanup As No Op.
    [<Test>]
    member _.SaveExpiryPlanningKeepsWholeFileCleanupAsNoOp() =
        let directoryVersion = directoryWith [ wholeFile () ]

        match ReferenceActor.planManifestSaveBoundary repositoryId referenceId directoryVersion "corr-whole-file-expiry" with
        | Ok plans -> Assert.That(plans, Is.Empty)
        | Error error -> Assert.Fail($"Expected whole-file save expiry planning to remain a no-op, got {error.Error}.")

    /// Verifies that physical Deletion Reminder Applies Expiry Boundary Only For Live Save References.
    [<Test>]
    member _.PhysicalDeletionReminderAppliesExpiryBoundaryOnlyForLiveSaveReferences() =
        Assert.That(ReferenceActor.shouldApplyManifestExpiryBoundary (referenceDto ReferenceType.Save), Is.True)
        Assert.That(ReferenceActor.shouldApplyManifestExpiryBoundary (referenceDto ReferenceType.Commit), Is.False)
        Assert.That(ReferenceActor.shouldApplyManifestExpiryBoundary (referenceDto ReferenceType.Checkpoint), Is.False)
        Assert.That(ReferenceActor.shouldApplyManifestExpiryBoundary Grace.Types.Reference.ReferenceDto.Default, Is.False)

    /// Verifies that save Expiry Reference Planning Skips Recursive Fetch For Checkpoint References.
    [<Test>]
    member _.SaveExpiryReferencePlanningSkipsRecursiveFetchForCheckpointReferences() =
        task {
            let mutable fetchCalled = false

            /// Extracts recursive Directory Versions from the scenario result so assertions stay focused on server unit save Boundary Actor behavior.
            let getRecursiveDirectoryVersions () =
                fetchCalled <- true
                Task.FromResult Array.empty<Grace.Types.DirectoryVersion.DirectoryVersionDto>

            let! result =
                ReferenceActor.planManifestSaveExpiryBoundaryForReferenceDirectoryVersions
                    repositoryId
                    referenceId
                    directoryVersionId
                    (referenceDto ReferenceType.Checkpoint)
                    getRecursiveDirectoryVersions
                    "corr-checkpoint-expiry-skip"

            match result with
            | Error error -> Assert.Fail($"Expected checkpoint expiry planning to skip manifest traversal, got {error.Error}.")
            | Ok plans -> Assert.That(plans, Is.Empty)

            Assert.That(fetchCalled, Is.False)
        }

    /// Verifies that recursive Directory Traversal Completeness Rejects Missing Declared Child Before Cache Write.
    [<Test>]
    member _.RecursiveDirectoryTraversalCompletenessRejectsMissingDeclaredChildBeforeCacheWrite() =
        let rootDirectory =
            DirectoryVersion.CreateWithHashes
                directoryVersionId
                ownerId
                organizationId
                repositoryId
                (RelativePath ".")
                (Sha256Hash "root-sha256")
                (Blake3Hash "root-blake3")
                (List<DirectoryVersionId>([ childDirectoryVersionId ]))
                (List<FileVersion>())
                0L

        let rootDto = { Grace.Types.DirectoryVersion.DirectoryVersionDto.Default with DirectoryVersion = rootDirectory }

        match DirectoryVersionActor.validateRecursiveDirectoryVersionsComplete directoryVersionId [ rootDto ] "corr-cache-poison-missing-child" with
        | Ok _ -> Assert.Fail("Expected incomplete recursive traversal to be rejected before cache write.")
        | Error error ->
            Assert.That(error.Error, Does.Contain("declared child DirectoryVersion"))
            Assert.That(error.Properties["ParentDirectoryVersionId"], Is.EqualTo(string directoryVersionId))
            Assert.That(error.Properties["ChildDirectoryVersionId"], Is.EqualTo(string childDirectoryVersionId))

    /// Verifies that recursive Directory Versions Caches Only Complete Traversal Results.
    [<Test>]
    member _.RecursiveDirectoryVersionsCachesOnlyCompleteTraversalResults() =
        let actorPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "DirectoryVersion.Actor.fs"))
        let actorSource = File.ReadAllText actorPath

        let traversalListStart = actorSource.IndexOf("let subdirectoryVersionsList =", StringComparison.Ordinal)

        Assert.That(traversalListStart, Is.GreaterThanOrEqualTo(0), "Expected recursive traversal list assembly to exist.")

        let validationStart = actorSource.IndexOf("match validateRecursiveDirectoryVersionsComplete", traversalListStart, StringComparison.Ordinal)

        Assert.That(validationStart, Is.GreaterThan(traversalListStart), "Expected cache writes to be gated by traversal completeness.")

        let cacheWriteStart = actorSource.IndexOf("OpenWriteAsync", validationStart, StringComparison.Ordinal)

        Assert.That(cacheWriteStart, Is.GreaterThan(validationStart), "Expected recursive cache write to remain present.")

        let guardedSlice = actorSource.Substring(validationStart, cacheWriteStart - validationStart)

        Assert.That(guardedSlice, Does.Contain("| Error graceError ->"))
        Assert.That(guardedSlice, Does.Contain("Skipping recursive directory version cache write"))
        Assert.That(guardedSlice, Does.Contain("| Ok completeSubdirectoryVersionsList ->"))
