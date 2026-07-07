namespace Grace.Server.Unit.Tests

open Grace.Actors.DirectoryVersion
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.MaterializationPlan
open NUnit.Framework
open System.Threading.Tasks

/// Covers deterministic DirectoryVersion projection helper behavior.
[<TestFixture>]
type DirectoryVersionProjectionEnsureTests() =

    /// Builds projection evidence for the helper seam without requiring object storage.
    let evidence artifactKind size source =
        {
            ArtifactKind = artifactKind
            SizeInBytes = size
            Sha256Hash = Some(Sha256Hash "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
            Blake3Hash = Some(Blake3Hash "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
            Source = source
        }

    /// Verifies the projection ensure helper returns exactly the two V1 target-root descriptors.
    [<Test>]
    member _.EnsureRequiredProjectionArtifactsReturnsTargetRootZipAndRecursiveMetadataDescriptors() =
        task {
            let targetRootDirectoryVersionId = DirectoryVersionId.Parse "11111111-1111-1111-1111-111111111111"
            let zipSource = Some(MaterializationArtifactSource.Direct "https://example.invalid/root.zip")
            let metadataSource = Some(MaterializationArtifactSource.CacheOnly "recursive/root.msgpack")

            let! result =
                ensureRequiredProjectionArtifactsWith
                    targetRootDirectoryVersionId
                    (fun () -> Task.FromResult(Ok(evidence MaterializationArtifactKind.DirectoryVersionZip 123L zipSource)))
                    (fun () -> Task.FromResult(Ok(evidence MaterializationArtifactKind.RecursiveDirectoryMetadata 456L metadataSource)))
                    "corr-projection"

            match result with
            | Error error -> Assert.Fail(error.Error)
            | Ok descriptors ->
                Assert.That(descriptors, Has.Length.EqualTo(2))

                let zip = descriptors[0]
                let metadata = descriptors[1]

                Assert.That(zip.ArtifactKind, Is.EqualTo(MaterializationArtifactKind.DirectoryVersionZip))
                Assert.That(metadata.ArtifactKind, Is.EqualTo(MaterializationArtifactKind.RecursiveDirectoryMetadata))
                Assert.That(zip.TargetRootDirectoryVersionId, Is.EqualTo(targetRootDirectoryVersionId))
                Assert.That(metadata.TargetRootDirectoryVersionId, Is.EqualTo(targetRootDirectoryVersionId))
                Assert.That(zip.RepresentedRootDirectoryVersionId, Is.EqualTo(Some targetRootDirectoryVersionId))
                Assert.That(metadata.RepresentedRootDirectoryVersionId, Is.EqualTo(Some targetRootDirectoryVersionId))
                Assert.That(zip.CanonicalArtifactIdentity, Is.EqualTo(Some(canonicalDirectoryVersionZipIdentity targetRootDirectoryVersionId)))
                Assert.That(metadata.CanonicalArtifactIdentity, Is.EqualTo(Some(canonicalRecursiveDirectoryMetadataIdentity targetRootDirectoryVersionId)))
                Assert.That(zip.SizeInBytes, Is.EqualTo(Some 123L))
                Assert.That(metadata.SizeInBytes, Is.EqualTo(Some 456L))
                Assert.That(zip.Source, Is.EqualTo(zipSource))
                Assert.That(metadata.Source, Is.EqualTo(metadataSource))

                match Validation.validateArtifactDescriptor zip with
                | Ok () -> ()
                | Error errors -> Assert.Fail(String.concat "; " errors)

                match Validation.validateArtifactDescriptor metadata with
                | Ok () -> ()
                | Error errors -> Assert.Fail(String.concat "; " errors)
        }

    /// Verifies projection failures block descriptor success and later artifact work.
    [<Test>]
    member _.EnsureRequiredProjectionArtifactsStopsWhenZipProjectionFails() =
        task {
            let targetRootDirectoryVersionId = DirectoryVersionId.Parse "22222222-2222-2222-2222-222222222222"
            let mutable recursiveMetadataCalled = false

            let! result =
                ensureRequiredProjectionArtifactsWith
                    targetRootDirectoryVersionId
                    (fun () -> Task.FromResult(Error(GraceError.Create "zip projection failed" "corr-zip-failure")))
                    (fun () ->
                        recursiveMetadataCalled <- true
                        Task.FromResult(Ok(evidence MaterializationArtifactKind.RecursiveDirectoryMetadata 456L None)))
                    "corr-zip-failure"

            match result with
            | Ok _ -> Assert.Fail("Expected zip projection failure to block descriptor success.")
            | Error error ->
                Assert.That(error.Error, Is.EqualTo("zip projection failed"))
                Assert.That(recursiveMetadataCalled, Is.False)
        }

    /// Verifies recursive metadata failures block descriptor success after zip evidence is available.
    [<Test>]
    member _.EnsureRequiredProjectionArtifactsFailsWhenRecursiveMetadataProjectionFails() =
        task {
            let targetRootDirectoryVersionId = DirectoryVersionId.Parse "33333333-3333-3333-3333-333333333333"

            let! result =
                ensureRequiredProjectionArtifactsWith
                    targetRootDirectoryVersionId
                    (fun () -> Task.FromResult(Ok(evidence MaterializationArtifactKind.DirectoryVersionZip 123L None)))
                    (fun () -> Task.FromResult(Error(GraceError.Create "recursive metadata projection failed" "corr-metadata-failure")))
                    "corr-metadata-failure"

            match result with
            | Ok _ -> Assert.Fail("Expected recursive metadata failure to block descriptor success.")
            | Error error -> Assert.That(error.Error, Is.EqualTo("recursive metadata projection failed"))
        }

    /// Verifies malformed projection evidence cannot produce a successful descriptor response.
    [<Test>]
    member _.EnsureRequiredProjectionArtifactsRejectsMalformedProjectionEvidence() =
        task {
            let targetRootDirectoryVersionId = DirectoryVersionId.Parse "44444444-4444-4444-4444-444444444444"

            let! wrongKindResult =
                ensureRequiredProjectionArtifactsWith
                    targetRootDirectoryVersionId
                    (fun () -> Task.FromResult(Ok(evidence MaterializationArtifactKind.RecursiveDirectoryMetadata 123L None)))
                    (fun () -> Task.FromResult(Ok(evidence MaterializationArtifactKind.RecursiveDirectoryMetadata 456L None)))
                    "corr-wrong-kind"

            match wrongKindResult with
            | Ok _ -> Assert.Fail("Expected wrong projection artifact kind to block descriptor success.")
            | Error error -> Assert.That(error.Error, Does.Contain("while ensuring DirectoryVersionZip"))

            let! negativeSizeResult =
                ensureRequiredProjectionArtifactsWith
                    targetRootDirectoryVersionId
                    (fun () -> Task.FromResult(Ok(evidence MaterializationArtifactKind.DirectoryVersionZip -1L None)))
                    (fun () -> Task.FromResult(Ok(evidence MaterializationArtifactKind.RecursiveDirectoryMetadata 456L None)))
                    "corr-negative-size"

            match negativeSizeResult with
            | Ok _ -> Assert.Fail("Expected negative projection artifact size to block descriptor success.")
            | Error error -> Assert.That(error.Error, Does.Contain("negative size"))
        }
