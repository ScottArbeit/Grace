namespace Grace.Types.Tests

open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.MaterializationPlan
open NUnit.Framework
open System
open System.Collections.Generic
open System.Text.Json

/// Contains deterministic Materialization Plan contract test data.
module MaterializationPlanTestData =

    /// Provides the immutable root directory version identity used by V1 plans.
    let targetRootDirectoryVersionId = DirectoryVersionId.Parse("11111111-1111-1111-1111-111111111111")

    /// Provides a second root identity used to prove descriptor/root mismatch handling.
    let otherDirectoryVersionId = DirectoryVersionId.Parse("22222222-2222-2222-2222-222222222222")

    /// Provides a deterministic reference id for selector serialization coverage.
    let referenceId = ReferenceId.Parse("33333333-3333-3333-3333-333333333333")

    /// Provides a deterministic branch name for selector serialization coverage.
    let branchName = BranchName "feature/cache-plan"

    /// Provides a deterministic storage pool id for CAS descriptor coverage.
    let storagePoolId = StoragePoolId "storage-pool-main"

    /// Provides a deterministic cache entry key that does not imply a direct source URL.
    let cacheKey = "cache/materialization/11111111/root.zip"

    /// Builds the V1 required target-root artifact descriptors for the supplied root.
    let rootArtifacts targetRoot =
        [
            MaterializationArtifactDescriptor.DirectoryVersionZip(targetRoot, Some(MaterializationArtifactSource.CacheOnly cacheKey))
            MaterializationArtifactDescriptor.RecursiveDirectoryMetadata(targetRoot, Some MaterializationArtifactSource.Deferred)
        ]

    /// Builds a valid Materialization Plan with all current artifact descriptor kinds represented.
    let validPlan () =
        MaterializationPlan.Create(
            targetRootDirectoryVersionId,
            MaterializationExecutionMode.CacheRequired,
            MaterializationCacheSelection.Required,
            [
                yield! rootArtifacts targetRootDirectoryVersionId
                MaterializationArtifactDescriptor.WholeFileContent(
                    targetRootDirectoryVersionId,
                    RelativePath "src/app.fs",
                    Some(Sha256Hash "sha256-file"),
                    Some(Blake3Hash "blake3-file"),
                    Some(MaterializationArtifactSource.CacheOnly "cache/file/src/app.fs")
                )
                MaterializationArtifactDescriptor.FileManifest(
                    targetRootDirectoryVersionId,
                    ManifestAddress "manifest-blake3-abc123",
                    storagePoolId,
                    Some(MaterializationArtifactSource.CacheOnly "cache/manifest/abc123")
                )
                MaterializationArtifactDescriptor.ContentBlock(
                    targetRootDirectoryVersionId,
                    ContentBlockAddress "block-blake3-def456",
                    storagePoolId,
                    Some(MaterializationArtifactSource.CacheOnly "cache/block/def456")
                )
            ]
        )

/// Contains tests covering Materialization Plan contract serialization and validation.
[<Parallelizable(ParallelScope.All)>]
type MaterializationPlanContractTests() =

    /// Verifies that contract validation fails with a message containing the expected text.
    let assertInvalid expected (validationResult: Result<unit, string list>) =
        match validationResult with
        | Ok () -> Assert.Fail($"Contract should fail validation with '{expected}'.")
        | Error errors -> Assert.That(errors, Has.Some.Contains(expected))

    /// Verifies that contract validation succeeds and reports validation errors on failure.
    let assertValid (validationResult: Result<unit, string list>) =
        match validationResult with
        | Ok () -> ()
        | Error errors ->
            let errorText = String.Join("; ", errors)
            Assert.Fail($"Contract should pass validation, but failed with: {errorText}")

    /// Reads a JSON property as a string for deterministic shape assertions.
    let stringProperty (document: JsonDocument) (name: string) = document.RootElement.GetProperty(name).GetString()

    /// Verifies that every supported execution mode serializes, deserializes, and validates as an explicit value.
    [<TestCase(MaterializationExecutionMode.Direct)>]
    [<TestCase(MaterializationExecutionMode.CachePreferred)>]
    [<TestCase(MaterializationExecutionMode.CacheRequired)>]
    member _.ExecutionModesRoundTripThroughJson(mode: MaterializationExecutionMode) =
        let request =
            MaterializationPlanRequest.Create(
                MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId,
                mode,
                MaterializationCacheSelection.Preferred,
                [
                    MaterializationArtifactKind.DirectoryVersionZip
                ]
            )

        let roundTrip = deserialize<MaterializationPlanRequest> (serialize request)

        Assert.That(roundTrip.ExecutionMode, Is.EqualTo(mode))
        assertValid (Validation.validateRequest roundTrip)

    /// Verifies that each target selector shape round trips through Grace JSON serialization.
    [<Test>]
    member _.TargetSelectorsRoundTripThroughJson() =
        let selectors =
            [
                MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId
                MaterializationTargetSelector.ForReference MaterializationPlanTestData.referenceId
                MaterializationTargetSelector.ForBranch MaterializationPlanTestData.branchName
            ]

        for selector in selectors do
            let roundTrip = deserialize<MaterializationTargetSelector> (serialize selector)

            Assert.That(roundTrip, Is.EqualTo(selector))
            assertValid (Validation.validateTargetSelector roundTrip)

    /// Verifies that all artifact kinds and source shapes are represented in one valid plan response.
    [<Test>]
    member _.MaterializationPlanSerializesAllArtifactKindsAndSourceShapes() =
        let plan = MaterializationPlanTestData.validPlan ()
        let json = serialize plan
        let roundTrip = deserialize<MaterializationPlan> json

        use document = JsonDocument.Parse(json)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(stringProperty document "Class", Is.EqualTo(nameof MaterializationPlan))
                Assert.That(stringProperty document "TargetRootDirectoryVersionId", Is.EqualTo("11111111-1111-1111-1111-111111111111"))
                Assert.That(stringProperty document "ExecutionMode", Is.EqualTo("cacheRequired"))
                Assert.That(roundTrip.RequiredArtifacts, Has.Count.EqualTo(5))

                Assert.That(
                    roundTrip.RequiredArtifacts
                    |> Seq.map (fun artifact -> artifact.ArtifactKind),
                    Is.EquivalentTo(Enum.GetValues<MaterializationArtifactKind>())
                )

                assertValid (Validation.validatePlan roundTrip))
        )

    /// Verifies that cache-required plans can point only at cache entries or deferred sources.
    [<Test>]
    member _.CacheRequiredPlanAllowsCacheOnlyArtifactSourcesWithoutDirectUris() =
        let plan = MaterializationPlanTestData.validPlan ()

        Assert.That(plan.ExecutionMode, Is.EqualTo(MaterializationExecutionMode.CacheRequired))
        Assert.That(plan.CacheSelection.SelectionKind, Is.EqualTo(MaterializationCacheSelectionKind.RequireCache))
        assertValid (Validation.validatePlan plan)

        for descriptor in plan.RequiredArtifacts do
            match descriptor.Source with
            | Some source when source.SourceKind = MaterializationArtifactSourceKind.CacheEntry ->
                Assert.That(source.DirectUri.IsNone, Is.True)
                assertValid (Validation.validateArtifactSource source)
            | Some source when source.SourceKind = MaterializationArtifactSourceKind.Deferred -> Assert.That(source.DirectUri.IsNone, Is.True)
            | _ -> Assert.Fail("CacheRequired test plan should not require a direct artifact URI.")

    /// Verifies that CacheRequired plans fail closed when a direct source URI is present.
    [<Test>]
    member _.CacheRequiredPlanWithDirectUriSourceFailsValidationClearly() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CacheRequired,
                MaterializationCacheSelection.Required,
                [
                    MaterializationArtifactDescriptor.DirectoryVersionZip(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        Some(MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/root.zip")
                    )
                    MaterializationArtifactDescriptor.RecursiveDirectoryMetadata(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        Some(MaterializationArtifactSource.CacheOnly MaterializationPlanTestData.cacheKey)
                    )
                ]
            )

        assertInvalid "CacheRequired plans must not require DirectUri artifact sources." (Validation.validatePlan plan)

    /// Verifies that CacheRequired execution mode fails closed even when cache selection is inconsistent.
    [<Test>]
    member _.CacheRequiredExecutionModeWithBypassSelectionRejectsDirectUriSource() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CacheRequired,
                MaterializationCacheSelection.Bypass,
                [
                    MaterializationArtifactDescriptor.DirectoryVersionZip(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        Some(MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/root.zip")
                    )
                    MaterializationArtifactDescriptor.RecursiveDirectoryMetadata(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        Some MaterializationArtifactSource.Deferred
                    )
                ]
            )

        assertInvalid "CacheRequired plans must not require DirectUri artifact sources." (Validation.validatePlan plan)

    /// Verifies that malformed plan DTOs with missing cache selection fail validation without throwing.
    [<Test>]
    member _.NullCacheSelectionFailsValidationWithoutDereferencing() =
        let plan = { MaterializationPlanTestData.validPlan () with CacheSelection = Unchecked.defaultof<MaterializationCacheSelection> }

        Assert.DoesNotThrow(Action(fun () -> Validation.validatePlan plan |> ignore))
        assertInvalid "CacheSelection is required." (Validation.validatePlan plan)

    /// Verifies that a plan response with an empty resolved root fails validation.
    [<Test>]
    member _.EmptyTargetRootFailsValidationClearly() =
        let plan =
            MaterializationPlan.Create(
                DirectoryVersionId.Empty,
                MaterializationExecutionMode.Direct,
                MaterializationCacheSelection.Bypass,
                MaterializationPlanTestData.rootArtifacts DirectoryVersionId.Empty
            )

        assertInvalid "TargetRootDirectoryVersionId is required." (Validation.validatePlan plan)

    /// Verifies that a null plan response fails validation instead of dereferencing.
    [<Test>]
    member _.NullPlanFailsValidationClearly() =
        let plan = Unchecked.defaultof<MaterializationPlan>

        Assert.DoesNotThrow(Action(fun () -> Validation.validatePlan plan |> ignore))
        assertInvalid "MaterializationPlan is required." (Validation.validatePlan plan)

    /// Verifies that descriptors must carry stable identity fields for their kind.
    [<Test>]
    member _.ArtifactDescriptorWithKindButNoStableIdentityFailsValidationClearly() =
        let descriptor =
            {
                Class = nameof MaterializationArtifactDescriptor
                ArtifactKind = MaterializationArtifactKind.WholeFileContent
                TargetRootDirectoryVersionId = MaterializationPlanTestData.targetRootDirectoryVersionId
                RelativePath = None
                Sha256Hash = None
                Blake3Hash = None
                ManifestAddress = None
                ContentBlockAddress = None
                StoragePoolId = None
                Source = None
            }

        let result = Validation.validateArtifactDescriptor descriptor

        assertInvalid "Artifact RelativePath is required for WholeFileContent descriptors." result
        assertInvalid "Artifact Sha256Hash or Blake3Hash is required for WholeFileContent descriptors." result

    /// Verifies that CAS artifact descriptors require StoragePoolId.
    [<TestCase(MaterializationArtifactKind.FileManifest)>]
    [<TestCase(MaterializationArtifactKind.ContentBlock)>]
    member _.CasArtifactMissingStoragePoolIdentityFailsValidationClearly(kind: MaterializationArtifactKind) =
        let descriptor =
            match kind with
            | MaterializationArtifactKind.FileManifest ->
                { MaterializationArtifactDescriptor.FileManifest(
                      MaterializationPlanTestData.targetRootDirectoryVersionId,
                      ManifestAddress "manifest-blake3-abc123",
                      MaterializationPlanTestData.storagePoolId,
                      None
                  ) with
                    StoragePoolId = None
                }
            | MaterializationArtifactKind.ContentBlock ->
                { MaterializationArtifactDescriptor.ContentBlock(
                      MaterializationPlanTestData.targetRootDirectoryVersionId,
                      ContentBlockAddress "block-blake3-def456",
                      MaterializationPlanTestData.storagePoolId,
                      None
                  ) with
                    StoragePoolId = None
                }
            | _ -> failwith "Unsupported test kind."

        assertInvalid "Artifact StoragePoolId is required for CAS artifact descriptors." (Validation.validateArtifactDescriptor descriptor)

    /// Verifies that target-root artifact descriptors reject identity fields owned by file and CAS artifact shapes.
    [<TestCase(MaterializationArtifactKind.DirectoryVersionZip)>]
    [<TestCase(MaterializationArtifactKind.RecursiveDirectoryMetadata)>]
    member _.TargetRootArtifactDescriptorsRejectCrossKindIdentityFields(kind: MaterializationArtifactKind) =
        let descriptor =
            match kind with
            | MaterializationArtifactKind.DirectoryVersionZip ->
                MaterializationArtifactDescriptor.DirectoryVersionZip(MaterializationPlanTestData.targetRootDirectoryVersionId, None)
            | MaterializationArtifactKind.RecursiveDirectoryMetadata ->
                MaterializationArtifactDescriptor.RecursiveDirectoryMetadata(MaterializationPlanTestData.targetRootDirectoryVersionId, None)
            | _ -> failwith "Unsupported test kind."

        let ambiguousDescriptor =
            { descriptor with
                RelativePath = Some(RelativePath "src/app.fs")
                Sha256Hash = Some(Sha256Hash "sha256-file")
                Blake3Hash = Some(Blake3Hash "blake3-file")
                ManifestAddress = Some(ManifestAddress "manifest-blake3-abc123")
                ContentBlockAddress = Some(ContentBlockAddress "block-blake3-def456")
                StoragePoolId = Some MaterializationPlanTestData.storagePoolId
            }

        let result = Validation.validateArtifactDescriptor ambiguousDescriptor

        assertInvalid $"Artifact RelativePath must be empty for {string kind} descriptors." result
        assertInvalid $"Artifact Sha256Hash must be empty for {string kind} descriptors." result
        assertInvalid $"Artifact Blake3Hash must be empty for {string kind} descriptors." result
        assertInvalid $"Artifact ManifestAddress must be empty for {string kind} descriptors." result
        assertInvalid $"Artifact ContentBlockAddress must be empty for {string kind} descriptors." result
        assertInvalid $"Artifact StoragePoolId must be empty for {string kind} descriptors." result

    /// Verifies that file and CAS artifact descriptors reject identity fields owned by other descriptor shapes.
    [<Test>]
    member _.FileAndCasArtifactDescriptorsRejectCrossKindIdentityFields() =
        let wholeFile =
            { MaterializationArtifactDescriptor.WholeFileContent(
                  MaterializationPlanTestData.targetRootDirectoryVersionId,
                  RelativePath "src/app.fs",
                  Some(Sha256Hash "sha256-file"),
                  None,
                  None
              ) with
                ManifestAddress = Some(ManifestAddress "manifest-blake3-abc123")
                ContentBlockAddress = Some(ContentBlockAddress "block-blake3-def456")
                StoragePoolId = Some MaterializationPlanTestData.storagePoolId
            }

        let fileManifest =
            { MaterializationArtifactDescriptor.FileManifest(
                  MaterializationPlanTestData.targetRootDirectoryVersionId,
                  ManifestAddress "manifest-blake3-abc123",
                  MaterializationPlanTestData.storagePoolId,
                  None
              ) with
                RelativePath = Some(RelativePath "src/app.fs")
                Sha256Hash = Some(Sha256Hash "sha256-file")
                Blake3Hash = Some(Blake3Hash "blake3-file")
                ContentBlockAddress = Some(ContentBlockAddress "block-blake3-def456")
            }

        let contentBlock =
            { MaterializationArtifactDescriptor.ContentBlock(
                  MaterializationPlanTestData.targetRootDirectoryVersionId,
                  ContentBlockAddress "block-blake3-def456",
                  MaterializationPlanTestData.storagePoolId,
                  None
              ) with
                RelativePath = Some(RelativePath "src/app.fs")
                Sha256Hash = Some(Sha256Hash "sha256-file")
                Blake3Hash = Some(Blake3Hash "blake3-file")
                ManifestAddress = Some(ManifestAddress "manifest-blake3-abc123")
            }

        assertInvalid "Artifact ManifestAddress must be empty for WholeFileContent descriptors." (Validation.validateArtifactDescriptor wholeFile)
        assertInvalid "Artifact ContentBlockAddress must be empty for WholeFileContent descriptors." (Validation.validateArtifactDescriptor wholeFile)
        assertInvalid "Artifact StoragePoolId must be empty for WholeFileContent descriptors." (Validation.validateArtifactDescriptor wholeFile)
        assertInvalid "Artifact RelativePath must be empty for FileManifest descriptors." (Validation.validateArtifactDescriptor fileManifest)
        assertInvalid "Artifact Sha256Hash must be empty for FileManifest descriptors." (Validation.validateArtifactDescriptor fileManifest)
        assertInvalid "Artifact Blake3Hash must be empty for FileManifest descriptors." (Validation.validateArtifactDescriptor fileManifest)
        assertInvalid "Artifact ContentBlockAddress must be empty for FileManifest descriptors." (Validation.validateArtifactDescriptor fileManifest)
        assertInvalid "Artifact RelativePath must be empty for ContentBlock descriptors." (Validation.validateArtifactDescriptor contentBlock)
        assertInvalid "Artifact Sha256Hash must be empty for ContentBlock descriptors." (Validation.validateArtifactDescriptor contentBlock)
        assertInvalid "Artifact Blake3Hash must be empty for ContentBlock descriptors." (Validation.validateArtifactDescriptor contentBlock)
        assertInvalid "Artifact ManifestAddress must be empty for ContentBlock descriptors." (Validation.validateArtifactDescriptor contentBlock)

    /// Verifies that whole-file artifact paths must be normalized repository-relative paths.
    [<TestCase("../app.fs")>]
    [<TestCase("src/../app.fs")>]
    [<TestCase("/src/app.fs")>]
    [<TestCase("C:\\src\\app.fs")>]
    [<TestCase("src\\app.fs")>]
    [<TestCase("src//app.fs")>]
    [<TestCase("./src/app.fs")>]
    member _.WholeFileContentRejectsUnsafeRelativePath(relativePath: string) =
        let descriptor =
            MaterializationArtifactDescriptor.WholeFileContent(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                RelativePath relativePath,
                Some(Sha256Hash "sha256-file"),
                None,
                None
            )

        assertInvalid
            "Artifact RelativePath must be a normalized repository-relative path for WholeFileContent descriptors."
            (Validation.validateArtifactDescriptor descriptor)

    /// Verifies that invalid enum values fail validation rather than silently becoming accepted modes.
    [<Test>]
    member _.InvalidExecutionModeValueFailsValidationClearly() =
        let request =
            MaterializationPlanRequest.Create(
                MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId,
                enum<MaterializationExecutionMode> 999,
                MaterializationCacheSelection.Preferred,
                [
                    MaterializationArtifactKind.DirectoryVersionZip
                ]
            )

        assertInvalid "ExecutionMode '999' is not supported." (Validation.validateRequest request)

    /// Verifies that invalid artifact kind values fail validation rather than silently becoming accepted artifacts.
    [<Test>]
    member _.InvalidArtifactKindValueFailsValidationClearly() =
        let descriptor =
            MaterializationArtifactDescriptor.DirectoryVersionZip(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                Some MaterializationArtifactSource.Deferred
            )

        let invalidDescriptor = { descriptor with ArtifactKind = enum<MaterializationArtifactKind> 999 }

        assertInvalid "ArtifactKind '999' is not supported." (Validation.validateArtifactDescriptor invalidDescriptor)

    /// Verifies that unknown future execution mode strings fail deserialization clearly.
    [<Test>]
    member _.UnknownExecutionModeStringFailsDeserializationClearly() =
        let request =
            MaterializationPlanRequest.Create(
                MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.Direct,
                MaterializationCacheSelection.Bypass,
                [
                    MaterializationArtifactKind.DirectoryVersionZip
                ]
            )

        let json =
            (serialize request)
                .Replace("\"ExecutionMode\": \"direct\"", "\"ExecutionMode\": \"serverOnly\"")

        let ex =
            Assert.Throws<JsonException>(
                Action (fun () ->
                    deserialize<MaterializationPlanRequest> json
                    |> ignore)
            )

        Assert.That(
            ex.Message,
            Does
                .Contain("serverOnly")
                .Or.Contains("MaterializationExecutionMode")
        )

    /// Verifies that unknown future artifact kind strings fail deserialization clearly.
    [<Test>]
    member _.UnknownArtifactKindStringFailsDeserializationClearly() =
        let descriptor =
            MaterializationArtifactDescriptor.DirectoryVersionZip(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                Some MaterializationArtifactSource.Deferred
            )

        let json =
            (serialize descriptor)
                .Replace("directoryVersionZip", "baselinePlusDelta")

        let ex =
            Assert.Throws<JsonException>(
                Action (fun () ->
                    deserialize<MaterializationArtifactDescriptor> json
                    |> ignore)
            )

        Assert.That(
            ex.Message,
            Does
                .Contain("baselinePlusDelta")
                .Or.Contains("MaterializationArtifactKind")
        )

    /// Verifies that the V1 response requires both target-root artifacts for the resolved root.
    [<Test>]
    member _.PlanMissingV1RequiredRootArtifactFailsValidationClearly() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.Direct,
                MaterializationCacheSelection.Bypass,
                [
                    MaterializationArtifactDescriptor.DirectoryVersionZip(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        Some MaterializationArtifactSource.Deferred
                    )
                ]
            )

        assertInvalid "RequiredArtifacts must include RecursiveDirectoryMetadata for the target root." (Validation.validatePlan plan)

    /// Verifies that every artifact descriptor in a response belongs to the resolved target root.
    [<Test>]
    member _.PlanArtifactForDifferentRootFailsValidationClearly() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.Direct,
                MaterializationCacheSelection.Bypass,
                [
                    yield! MaterializationPlanTestData.rootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
                    MaterializationArtifactDescriptor.DirectoryVersionZip(
                        MaterializationPlanTestData.otherDirectoryVersionId,
                        Some MaterializationArtifactSource.Deferred
                    )
                ]
            )

        assertInvalid "Artifact TargetRootDirectoryVersionId must match the plan TargetRootDirectoryVersionId." (Validation.validatePlan plan)

    /// Verifies that cache-entry sources require only cache identity, not direct source URLs.
    [<Test>]
    member _.CacheEntrySourceValidatesWithoutDirectUri() =
        let source = MaterializationArtifactSource.CacheOnly MaterializationPlanTestData.cacheKey

        Assert.That(source.DirectUri.IsNone, Is.True)
        assertValid (Validation.validateArtifactSource source)

    /// Verifies that non-direct source kinds cannot smuggle direct fetch URLs.
    [<Test>]
    member _.NonDirectArtifactSourcesRejectDirectUri() =
        let cacheEntrySource =
            { MaterializationArtifactSource.CacheOnly MaterializationPlanTestData.cacheKey with
                DirectUri = Some "https://cache.example.test/artifacts/root.zip"
            }

        let deferredSource = { MaterializationArtifactSource.Deferred with DirectUri = Some "https://cache.example.test/artifacts/root.zip" }

        assertInvalid "Artifact DirectUri must be empty for CacheEntry sources." (Validation.validateArtifactSource cacheEntrySource)
        assertInvalid "Artifact DirectUri must be empty for Deferred sources." (Validation.validateArtifactSource deferredSource)

    /// Verifies that non-cache-entry source kinds cannot smuggle cache-entry identity.
    [<Test>]
    member _.NonCacheEntryArtifactSourcesRejectCacheKey() =
        let directSource =
            { MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/root.zip" with CacheKey = Some MaterializationPlanTestData.cacheKey }

        let deferredSource = { MaterializationArtifactSource.Deferred with CacheKey = Some MaterializationPlanTestData.cacheKey }

        assertInvalid "Artifact CacheKey must be empty for DirectUri sources." (Validation.validateArtifactSource directSource)
        assertInvalid "Artifact CacheKey must be empty for Deferred sources." (Validation.validateArtifactSource deferredSource)

    /// Verifies that direct URI sources carry a direct location and round trip through JSON.
    [<Test>]
    member _.DirectUriSourceRoundTripsThroughJson() =
        let source = MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/root.zip"
        let roundTrip = deserialize<MaterializationArtifactSource> (serialize source)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(roundTrip.SourceKind, Is.EqualTo(MaterializationArtifactSourceKind.DirectUri))
                Assert.That(roundTrip.DirectUri, Is.EqualTo(Some "https://cache.example.test/artifacts/root.zip"))
                Assert.That(roundTrip.CacheKey.IsNone, Is.True)
                assertValid (Validation.validateArtifactSource roundTrip))
        )

    /// Verifies that cache-entry sources fail closed when cache identity is absent.
    [<Test>]
    member _.CacheEntrySourceWithoutCacheKeyFailsValidationClearly() =
        let source = { MaterializationArtifactSource.CacheOnly MaterializationPlanTestData.cacheKey with CacheKey = None }

        assertInvalid "Artifact CacheKey is required for CacheEntry sources." (Validation.validateArtifactSource source)

    /// Verifies that all current artifact kinds are accepted in request artifact selection.
    [<Test>]
    member _.RequestArtifactKindSelectionAcceptsAllCurrentKinds() =
        let request =
            MaterializationPlanRequest.Create(
                MaterializationTargetSelector.ForBranch MaterializationPlanTestData.branchName,
                MaterializationExecutionMode.CachePreferred,
                MaterializationCacheSelection.Preferred,
                Enum.GetValues<MaterializationArtifactKind>()
            )

        let roundTrip = deserialize<MaterializationPlanRequest> (serialize request)

        Assert.That(roundTrip.RequestedArtifactKinds, Is.EquivalentTo(Enum.GetValues<MaterializationArtifactKind>()))
        assertValid (Validation.validateRequest roundTrip)

    /// Verifies that public request cache controls fail closed when cache-required mode tries to bypass cache.
    [<Test>]
    member _.CacheRequiredRequestWithBypassSelectionFailsValidationClearly() =
        let request =
            MaterializationPlanRequest.Create(
                MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CacheRequired,
                MaterializationCacheSelection.Bypass,
                [
                    MaterializationArtifactKind.DirectoryVersionZip
                ]
            )

        assertInvalid "CacheRequired materialization must not use BypassCache selection." (Validation.validateRequest request)

    /// Verifies that each selector kind rejects identity fields owned by other selector kinds.
    [<Test>]
    member _.TargetSelectorsRejectAmbiguousIdentityFields() =
        let directorySelector =
            { MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId with
                ReferenceId = Some MaterializationPlanTestData.referenceId
            }

        let referenceSelector =
            { MaterializationTargetSelector.ForReference MaterializationPlanTestData.referenceId with BranchName = Some MaterializationPlanTestData.branchName }

        let branchSelector =
            { MaterializationTargetSelector.ForBranch MaterializationPlanTestData.branchName with
                DirectoryVersionId = Some MaterializationPlanTestData.targetRootDirectoryVersionId
            }

        assertInvalid "TargetSelector.ReferenceId must be empty for DirectoryVersionId selectors." (Validation.validateTargetSelector directorySelector)
        assertInvalid "TargetSelector.BranchName must be empty for ReferenceId selectors." (Validation.validateTargetSelector referenceSelector)
        assertInvalid "TargetSelector.DirectoryVersionId must be empty for BranchName selectors." (Validation.validateTargetSelector branchSelector)
