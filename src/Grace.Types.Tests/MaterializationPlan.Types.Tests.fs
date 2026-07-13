namespace Grace.Types.Tests

open Grace.Shared.Utilities
open Grace.Types.ArtifactGrant
open Grace.Types.Common
open Grace.Types.MaterializationPlan
open Grace.Types.Reference
open NodaTime
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
    let branchName = BranchName "feature-cache-plan"

    /// Provides a deterministic branch id for selector serialization coverage.
    let branchId = BranchId.Parse("44444444-4444-4444-4444-444444444444")

    /// Provides a deterministic reference type for selector serialization coverage.
    let referenceType = ReferenceType.Promotion

    /// Provides a deterministic storage pool id for CAS descriptor coverage.
    let storagePoolId = StoragePoolId "storage-pool-main"

    /// Provides a deterministic cache entry key that does not imply a direct source URL.
    let cacheKey = "cache/materialization/11111111/root.zip"

    /// Provides the selected Cache identity used by plans that include an artifact grant.
    let cacheId = "33333333-3333-3333-3333-333333333333"

    /// Provides the canonical identity used by existing directory-version zip storage.
    let directoryVersionZipIdentity = "Grace-ZipFiles/11111111-1111-1111-1111-111111111111.zip"

    /// Provides the canonical identity used by existing recursive directory metadata storage.
    let recursiveDirectoryMetadataIdentity = "11111111-1111-1111-1111-111111111111.msgpack"

    /// Provides a deterministic byte length for projection artifact descriptor validation.
    let projectionArtifactSize = 4096L

    /// Provides a canonical lowercase SHA-256 hash value for root projection descriptor validation.
    let projectionArtifactSha256Hash = Sha256Hash "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

    /// Provides a canonical lowercase BLAKE3 hash value for root projection descriptor validation.
    let projectionArtifactBlake3Hash = Blake3Hash "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

    /// Provides a canonical lowercase BLAKE3 manifest address for descriptor validation.
    let manifestAddress = ManifestAddress "5dd11fdb1534c0f1c4fecca3c07498f9b59a7dff26d69fe78e43f7ce50d90895"

    /// Provides a canonical lowercase BLAKE3 content block address for descriptor validation.
    let contentBlockAddress = ContentBlockAddress "75359c5d838dda90b12c6cbcdd9b71a1f193daf6c23fa2bfd496065bed57a30f"

    /// Provides a canonical lowercase SHA-256 hash value for whole-file descriptor validation.
    let sha256Hash = Sha256Hash "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

    /// Provides a canonical lowercase BLAKE3 hash value for whole-file descriptor validation.
    let blake3Hash = Blake3Hash "89abcdef0123456789abcdef0123456789abcdef0123456789abcdef01234567"

    /// Builds a root zip descriptor with explicit identity, represented root, size, and integrity.
    let directoryVersionZip targetRoot source =
        MaterializationArtifactDescriptor.DirectoryVersionZip(
            targetRoot,
            projectionArtifactSize,
            Some projectionArtifactSha256Hash,
            Some projectionArtifactBlake3Hash,
            source
        )

    /// Builds a recursive metadata descriptor with explicit identity, represented root, size, and integrity.
    let recursiveDirectoryMetadata targetRoot source =
        MaterializationArtifactDescriptor.RecursiveDirectoryMetadata(
            targetRoot,
            projectionArtifactSize,
            Some projectionArtifactSha256Hash,
            Some projectionArtifactBlake3Hash,
            source
        )

    /// Builds the V1 required target-root artifact descriptors for the supplied root.
    let rootArtifacts targetRoot =
        [
            directoryVersionZip targetRoot (Some(MaterializationArtifactSource.CacheOnly cacheKey))
            recursiveDirectoryMetadata targetRoot (Some(MaterializationArtifactSource.CacheOnly $"{cacheKey}/metadata"))
        ]

    /// Builds V1 target-root artifact descriptors that are compatible with direct materialization.
    let directRootArtifacts targetRoot =
        [
            directoryVersionZip targetRoot (Some(MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/root.zip"))
            recursiveDirectoryMetadata targetRoot (Some(MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/metadata.msgpack"))
        ]

    /// Binds every CacheEntry in a plan to one selected Cache and attaches the exact matching artifact grant payload.
    let withMatchingCacheGrant (plan: MaterializationPlan) =
        let requiredArtifacts =
            plan.RequiredArtifacts
            |> Seq.map (fun artifact ->
                match artifact.Source with
                | Some source when source.SourceKind = MaterializationArtifactSourceKind.CacheEntry ->
                    { artifact with Source = Some { source with CacheEndpoint = Some "https://cache.example.test"; CacheId = Some cacheId } }
                | _ -> artifact)
            |> List<MaterializationArtifactDescriptor>

        let boundPlan = { plan with RequiredArtifacts = requiredArtifacts }

        let artifactIdentities =
            boundPlan.RequiredArtifacts
            |> Seq.choose (fun artifact -> artifact.CanonicalArtifactIdentity)

        let payload =
            ArtifactGrantPayload.Create(
                "user-11111111-1111-1111-1111-111111111111",
                "holder-thumbprint",
                cacheId,
                "https://cache.example.test",
                boundPlan.TargetRootDirectoryVersionId,
                boundPlan.ExecutionMode,
                artifactIdentities,
                Instant.FromUnixTimeMilliseconds 0L,
                ArtifactGrantContract.DefaultGrantTtl
            )

        boundPlan.WithArtifactGrant(SignedArtifactGrant.Create(ArtifactGrantHeader.Create "test-key", payload, "test-signature"))

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
                    Some sha256Hash,
                    Some blake3Hash,
                    Some(MaterializationArtifactSource.CacheOnly "cache/file/src/app.fs")
                )
                MaterializationArtifactDescriptor.FileManifest(
                    targetRootDirectoryVersionId,
                    manifestAddress,
                    storagePoolId,
                    Some(MaterializationArtifactSource.CacheOnly "cache/manifest/abc123")
                )
                MaterializationArtifactDescriptor.ContentBlock(
                    targetRootDirectoryVersionId,
                    contentBlockAddress,
                    storagePoolId,
                    Some(MaterializationArtifactSource.CacheOnly "cache/block/def456")
                )
            ]
        )
        |> withMatchingCacheGrant

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
        let cacheSelection =
            match mode with
            | MaterializationExecutionMode.Direct -> MaterializationCacheSelection.Bypass
            | MaterializationExecutionMode.CachePreferred -> MaterializationCacheSelection.Preferred
            | MaterializationExecutionMode.CacheRequired -> MaterializationCacheSelection.Required
            | _ -> MaterializationCacheSelection.Preferred

        let request =
            MaterializationPlanRequest.Create(
                MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId,
                mode,
                cacheSelection,
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
                MaterializationTargetSelector.ForReferenceType(MaterializationPlanTestData.branchName, MaterializationPlanTestData.referenceType)
                MaterializationTargetSelector.ForReferenceType(MaterializationPlanTestData.branchId, MaterializationPlanTestData.referenceType)
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

    /// Verifies that cache-required plans can point only at cache entries.
    [<Test>]
    member _.CacheRequiredPlanAllowsOnlyCacheEntryArtifactSourcesWithoutDirectUris() =
        let plan = MaterializationPlanTestData.validPlan ()

        Assert.That(plan.ExecutionMode, Is.EqualTo(MaterializationExecutionMode.CacheRequired))
        Assert.That(plan.CacheSelection.SelectionKind, Is.EqualTo(MaterializationCacheSelectionKind.RequireCache))
        assertValid (Validation.validatePlan (MaterializationPlanTestData.withMatchingCacheGrant plan))

        for descriptor in plan.RequiredArtifacts do
            match descriptor.Source with
            | Some source when source.SourceKind = MaterializationArtifactSourceKind.CacheEntry ->
                Assert.That(source.DirectUri.IsNone, Is.True)
                assertValid (Validation.validateArtifactSource source)
            | _ -> Assert.Fail("CacheRequired test plan should require cache-entry artifact sources.")

    /// Verifies that CacheRequired plans cannot reach runtime without an exact artifact grant.
    [<Test>]
    member _.CacheRequiredPlanRejectsMissingArtifactGrant() =
        let plan = { MaterializationPlanTestData.validPlan () with ArtifactGrant = None }

        assertInvalid
            "ArtifactGrant is required for CacheRequired plans, CacheEntry sources, and plans that are not entirely Direct."
            (Validation.validatePlan plan)

    /// Verifies that CachePreferred plans also require a grant when any source is a CacheEntry.
    [<Test>]
    member _.CachePreferredPlanWithCacheEntryRejectsMissingArtifactGrant() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CachePreferred,
                MaterializationCacheSelection.Preferred,
                MaterializationPlanTestData.rootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
            )

        assertInvalid
            "ArtifactGrant is required for CacheRequired plans, CacheEntry sources, and plans that are not entirely Direct."
            (Validation.validatePlan plan)

    /// Verifies that a direct-only CachePreferred plan remains deliberately grant-free.
    [<Test>]
    member _.CachePreferredPlanWithOnlyDirectSourcesAllowsNoArtifactGrant() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CachePreferred,
                MaterializationCacheSelection.Preferred,
                MaterializationPlanTestData.directRootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
            )

        assertValid (Validation.validatePlan plan)

    /// Verifies that a null artifact descriptor is reported without breaking source or grant aggregation.
    [<Test>]
    member _.PlanRejectsNullArtifactDescriptorWithoutThrowing() =
        let validPlan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CacheRequired,
                MaterializationCacheSelection.Required,
                MaterializationPlanTestData.rootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
            )
            |> MaterializationPlanTestData.withMatchingCacheGrant

        let requiredArtifacts = List<MaterializationArtifactDescriptor>(validPlan.RequiredArtifacts)
        requiredArtifacts.Add(Unchecked.defaultof<MaterializationArtifactDescriptor>)

        let plan = { validPlan with RequiredArtifacts = requiredArtifacts }

        assertInvalid "Artifact descriptor is required." (Validation.validatePlan plan)

    /// Verifies that a Cache plan rejects a grant whose artifact identity set differs from the exact plan identities.
    [<Test>]
    member _.CachePlanRejectsArtifactGrantIdentityMismatch() =
        let plan = MaterializationPlanTestData.validPlan ()

        let grant =
            plan.ArtifactGrant
            |> Option.defaultWith (fun () -> failwith "The test plan must include an artifact grant.")

        let mismatchedPlan =
            { plan with ArtifactGrant = Some { grant with Payload = { grant.Payload with ArtifactIdentities = List<string>([ "not-the-planned-artifact" ]) } } }

        assertInvalid "ArtifactGrant artifact identities must exactly match the Materialization Plan artifacts." (Validation.validatePlan mismatchedPlan)

    /// Verifies that CacheRequired plans fail closed when a direct source URI is present.
    [<Test>]
    member _.CacheRequiredPlanWithDirectUriSourceFailsValidationClearly() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CacheRequired,
                MaterializationCacheSelection.Required,
                [
                    MaterializationPlanTestData.directoryVersionZip
                        MaterializationPlanTestData.targetRootDirectoryVersionId
                        (Some(MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/root.zip"))
                    MaterializationPlanTestData.recursiveDirectoryMetadata
                        MaterializationPlanTestData.targetRootDirectoryVersionId
                        (Some(MaterializationArtifactSource.CacheOnly MaterializationPlanTestData.cacheKey))
                ]
            )

        assertInvalid "CacheRequired plans must require CacheEntry artifact sources for every required artifact." (Validation.validatePlan plan)

    /// Verifies that CacheRequired execution mode fails closed even when cache selection is inconsistent.
    [<Test>]
    member _.CacheRequiredExecutionModeWithBypassSelectionRejectsDirectUriSource() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CacheRequired,
                MaterializationCacheSelection.Bypass,
                [
                    MaterializationPlanTestData.directoryVersionZip
                        MaterializationPlanTestData.targetRootDirectoryVersionId
                        (Some(MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/root.zip"))
                    MaterializationPlanTestData.recursiveDirectoryMetadata
                        MaterializationPlanTestData.targetRootDirectoryVersionId
                        (Some MaterializationArtifactSource.Deferred)
                ]
            )

        assertInvalid "CacheRequired plans must require CacheEntry artifact sources for every required artifact." (Validation.validatePlan plan)

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
                CanonicalArtifactIdentity = None
                RepresentedRootDirectoryVersionId = Some MaterializationPlanTestData.targetRootDirectoryVersionId
                TargetRootDirectoryVersionId = MaterializationPlanTestData.targetRootDirectoryVersionId
                SizeInBytes = None
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

    /// Verifies that root projection artifacts use deterministic identities instead of retrieval locations.
    [<Test>]
    member _.ProjectionArtifactsBuildCanonicalIdentityForRepresentedRoot() =
        let zip =
            MaterializationPlanTestData.directoryVersionZip
                MaterializationPlanTestData.targetRootDirectoryVersionId
                (Some(MaterializationArtifactSource.Direct "https://cache.example.test/transient/downloads/root.zip?token=abc"))

        let metadata =
            MaterializationPlanTestData.recursiveDirectoryMetadata
                MaterializationPlanTestData.targetRootDirectoryVersionId
                (Some(MaterializationArtifactSource.CacheOnly "cache/temporary/metadata-entry"))

        Assert.Multiple(
            Action (fun () ->
                Assert.That(zip.CanonicalArtifactIdentity, Is.EqualTo(Some MaterializationPlanTestData.directoryVersionZipIdentity))
                Assert.That(metadata.CanonicalArtifactIdentity, Is.EqualTo(Some MaterializationPlanTestData.recursiveDirectoryMetadataIdentity))
                Assert.That(zip.RepresentedRootDirectoryVersionId, Is.EqualTo(Some MaterializationPlanTestData.targetRootDirectoryVersionId))
                Assert.That(metadata.RepresentedRootDirectoryVersionId, Is.EqualTo(Some MaterializationPlanTestData.targetRootDirectoryVersionId))
                Assert.That(zip.SizeInBytes, Is.EqualTo(Some MaterializationPlanTestData.projectionArtifactSize))
                Assert.That(metadata.SizeInBytes, Is.EqualTo(Some MaterializationPlanTestData.projectionArtifactSize))
                Assert.That(zip.CanonicalArtifactIdentity, Is.Not.EqualTo(zip.Source.Value.DirectUri))
                Assert.That(metadata.CanonicalArtifactIdentity, Is.Not.EqualTo(metadata.Source.Value.CacheKey))
                assertValid (Validation.validateArtifactDescriptor zip)
                assertValid (Validation.validateArtifactDescriptor metadata))
        )

    /// Verifies that projection artifact identity must match the represented root and existing storage naming.
    [<TestCase(MaterializationArtifactKind.DirectoryVersionZip,
               "https://cache.example.test/transient/downloads/root.zip",
               "Artifact CanonicalArtifactIdentity must be Grace-ZipFiles/11111111-1111-1111-1111-111111111111.zip for DirectoryVersionZip descriptors.")>]
    [<TestCase(MaterializationArtifactKind.RecursiveDirectoryMetadata,
               "Grace-ZipFiles/22222222-2222-2222-2222-222222222222.zip",
               "Artifact CanonicalArtifactIdentity must be 11111111-1111-1111-1111-111111111111.msgpack for RecursiveDirectoryMetadata descriptors.")>]
    member _.ProjectionArtifactsRejectMismatchedCanonicalIdentity(kind: MaterializationArtifactKind, artifactIdentity: string, expected: string) =
        let descriptor =
            match kind with
            | MaterializationArtifactKind.DirectoryVersionZip ->
                { MaterializationPlanTestData.directoryVersionZip MaterializationPlanTestData.targetRootDirectoryVersionId None with
                    CanonicalArtifactIdentity = Some artifactIdentity
                }
            | MaterializationArtifactKind.RecursiveDirectoryMetadata ->
                { MaterializationPlanTestData.recursiveDirectoryMetadata MaterializationPlanTestData.targetRootDirectoryVersionId None with
                    CanonicalArtifactIdentity = Some artifactIdentity
                }
            | _ -> failwith "Unsupported projection artifact kind."

        assertInvalid expected (Validation.validateArtifactDescriptor descriptor)

    /// Verifies that projection artifacts cannot omit represented root, byte length, or integrity.
    [<TestCase(MaterializationArtifactKind.DirectoryVersionZip)>]
    [<TestCase(MaterializationArtifactKind.RecursiveDirectoryMetadata)>]
    member _.ProjectionArtifactsRejectMissingRepresentedRootSizeAndIntegrity(kind: MaterializationArtifactKind) =
        let descriptor =
            match kind with
            | MaterializationArtifactKind.DirectoryVersionZip ->
                { MaterializationPlanTestData.directoryVersionZip MaterializationPlanTestData.targetRootDirectoryVersionId None with
                    RepresentedRootDirectoryVersionId = None
                    SizeInBytes = None
                    Sha256Hash = None
                    Blake3Hash = None
                }
            | MaterializationArtifactKind.RecursiveDirectoryMetadata ->
                { MaterializationPlanTestData.recursiveDirectoryMetadata MaterializationPlanTestData.targetRootDirectoryVersionId None with
                    RepresentedRootDirectoryVersionId = None
                    SizeInBytes = None
                    Sha256Hash = None
                    Blake3Hash = None
                }
            | _ -> failwith "Unsupported projection artifact kind."

        let result = Validation.validateArtifactDescriptor descriptor

        assertInvalid $"Artifact RepresentedRootDirectoryVersionId is required for {string kind} descriptors." result
        assertInvalid $"Artifact SizeInBytes is required for {string kind} descriptors." result
        assertInvalid $"Artifact Sha256Hash or Blake3Hash is required for {string kind} descriptors." result

    /// Verifies that projection artifacts cannot rely on byte length without integrity evidence.
    [<TestCase(MaterializationArtifactKind.DirectoryVersionZip)>]
    [<TestCase(MaterializationArtifactKind.RecursiveDirectoryMetadata)>]
    member _.ProjectionArtifactsRejectSizeOnlyIntegrity(kind: MaterializationArtifactKind) =
        let descriptor =
            match kind with
            | MaterializationArtifactKind.DirectoryVersionZip ->
                { MaterializationPlanTestData.directoryVersionZip MaterializationPlanTestData.targetRootDirectoryVersionId None with
                    Sha256Hash = None
                    Blake3Hash = None
                }
            | MaterializationArtifactKind.RecursiveDirectoryMetadata ->
                { MaterializationPlanTestData.recursiveDirectoryMetadata MaterializationPlanTestData.targetRootDirectoryVersionId None with
                    Sha256Hash = None
                    Blake3Hash = None
                }
            | _ -> failwith "Unsupported projection artifact kind."

        assertInvalid $"Artifact Sha256Hash or Blake3Hash is required for {string kind} descriptors." (Validation.validateArtifactDescriptor descriptor)

    /// Verifies that projection artifacts reject metadata that names a different represented root.
    [<TestCase(MaterializationArtifactKind.DirectoryVersionZip)>]
    [<TestCase(MaterializationArtifactKind.RecursiveDirectoryMetadata)>]
    member _.ProjectionArtifactsRejectRepresentedRootMismatch(kind: MaterializationArtifactKind) =
        let descriptor =
            match kind with
            | MaterializationArtifactKind.DirectoryVersionZip ->
                { MaterializationPlanTestData.directoryVersionZip MaterializationPlanTestData.targetRootDirectoryVersionId None with
                    RepresentedRootDirectoryVersionId = Some MaterializationPlanTestData.otherDirectoryVersionId
                }
            | MaterializationArtifactKind.RecursiveDirectoryMetadata ->
                { MaterializationPlanTestData.recursiveDirectoryMetadata MaterializationPlanTestData.targetRootDirectoryVersionId None with
                    RepresentedRootDirectoryVersionId = Some MaterializationPlanTestData.otherDirectoryVersionId
                }
            | _ -> failwith "Unsupported projection artifact kind."

        assertInvalid "Artifact RepresentedRootDirectoryVersionId must match TargetRootDirectoryVersionId." (Validation.validateArtifactDescriptor descriptor)

    /// Verifies that projection artifact integrity uses the same canonical lowercase digest rules as file content.
    [<TestCase(MaterializationArtifactKind.DirectoryVersionZip, true, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")>]
    [<TestCase(MaterializationArtifactKind.DirectoryVersionZip, false, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbg")>]
    [<TestCase(MaterializationArtifactKind.RecursiveDirectoryMetadata, true, "")>]
    [<TestCase(MaterializationArtifactKind.RecursiveDirectoryMetadata, false, "bbbbbbbbbbbbbbbb")>]
    member _.ProjectionArtifactsRejectMalformedIntegrity(kind: MaterializationArtifactKind, evaluateSha256: bool, malformedHash: string) =
        let descriptor =
            match kind with
            | MaterializationArtifactKind.DirectoryVersionZip ->
                MaterializationPlanTestData.directoryVersionZip MaterializationPlanTestData.targetRootDirectoryVersionId None
            | MaterializationArtifactKind.RecursiveDirectoryMetadata ->
                MaterializationPlanTestData.recursiveDirectoryMetadata MaterializationPlanTestData.targetRootDirectoryVersionId None
            | _ -> failwith "Unsupported projection artifact kind."

        let malformedDescriptor =
            if evaluateSha256 then
                { descriptor with Sha256Hash = Some(Sha256Hash malformedHash) }
            else
                { descriptor with Blake3Hash = Some(Blake3Hash malformedHash) }

        let expected =
            if evaluateSha256 then
                $"Artifact Sha256Hash must be a canonical lowercase 64-character hexadecimal value for {string kind} descriptors."
            else
                $"Artifact Blake3Hash must be a canonical lowercase 64-character hexadecimal value for {string kind} descriptors."

        assertInvalid expected (Validation.validateArtifactDescriptor malformedDescriptor)

    /// Verifies that projection artifact size must be a concrete non-negative byte length.
    [<TestCase(MaterializationArtifactKind.DirectoryVersionZip)>]
    [<TestCase(MaterializationArtifactKind.RecursiveDirectoryMetadata)>]
    member _.ProjectionArtifactsRejectNegativeSize(kind: MaterializationArtifactKind) =
        let descriptor =
            match kind with
            | MaterializationArtifactKind.DirectoryVersionZip ->
                { MaterializationPlanTestData.directoryVersionZip MaterializationPlanTestData.targetRootDirectoryVersionId None with SizeInBytes = Some -1L }
            | MaterializationArtifactKind.RecursiveDirectoryMetadata ->
                { MaterializationPlanTestData.recursiveDirectoryMetadata MaterializationPlanTestData.targetRootDirectoryVersionId None with
                    SizeInBytes = Some -1L
                }
            | _ -> failwith "Unsupported projection artifact kind."

        assertInvalid $"Artifact SizeInBytes must be non-negative for {string kind} descriptors." (Validation.validateArtifactDescriptor descriptor)

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
                MaterializationPlanTestData.directoryVersionZip MaterializationPlanTestData.targetRootDirectoryVersionId None
            | MaterializationArtifactKind.RecursiveDirectoryMetadata ->
                MaterializationPlanTestData.recursiveDirectoryMetadata MaterializationPlanTestData.targetRootDirectoryVersionId None
            | _ -> failwith "Unsupported test kind."

        let ambiguousDescriptor =
            { descriptor with
                RelativePath = Some(RelativePath "src/app.fs")
                ManifestAddress = Some(ManifestAddress "manifest-blake3-abc123")
                ContentBlockAddress = Some(ContentBlockAddress "block-blake3-def456")
                StoragePoolId = Some MaterializationPlanTestData.storagePoolId
            }

        let result = Validation.validateArtifactDescriptor ambiguousDescriptor

        assertInvalid $"Artifact RelativePath must be empty for {string kind} descriptors." result
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
    [<TestCase("C:/src/app.fs")>]
    [<TestCase("C:\\src\\app.fs")>]
    [<TestCase("src:C/app.fs")>]
    [<TestCase("src\\app.fs")>]
    [<TestCase("src//app.fs")>]
    [<TestCase("./src/app.fs")>]
    member _.WholeFileContentRejectsUnsafeRelativePath(relativePath: string) =
        let descriptor =
            MaterializationArtifactDescriptor.WholeFileContent(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                RelativePath relativePath,
                Some MaterializationPlanTestData.sha256Hash,
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
            MaterializationPlanTestData.directoryVersionZip
                MaterializationPlanTestData.targetRootDirectoryVersionId
                (Some MaterializationArtifactSource.Deferred)

        let invalidDescriptor = { descriptor with ArtifactKind = enum<MaterializationArtifactKind> 999 }

        assertInvalid "ArtifactKind '999' is not supported." (Validation.validateArtifactDescriptor invalidDescriptor)

    /// Verifies that invalid target selector kind values fail validation rather than choosing an identity shape.
    [<Test>]
    member _.InvalidTargetSelectorKindValueFailsValidationClearly() =
        let selector =
            { MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId with
                SelectorKind = enum<MaterializationTargetSelectorKind> 999
            }

        assertInvalid "TargetSelector.SelectorKind '999' is not supported." (Validation.validateTargetSelector selector)

    /// Verifies that invalid cache selection kind values fail validation rather than choosing cache authority.
    [<Test>]
    member _.InvalidCacheSelectionKindValueFailsValidationClearly() =
        let cacheSelection = { MaterializationCacheSelection.Preferred with SelectionKind = enum<MaterializationCacheSelectionKind> 999 }

        assertInvalid "CacheSelection.SelectionKind '999' is not supported." (Validation.validateCacheSelection cacheSelection)

    /// Verifies that invalid artifact source kind values fail validation rather than choosing source identity.
    [<Test>]
    member _.InvalidArtifactSourceKindValueFailsValidationClearly() =
        let source = { MaterializationArtifactSource.Deferred with SourceKind = enum<MaterializationArtifactSourceKind> 999 }

        assertInvalid "Artifact SourceKind '999' is not supported." (Validation.validateArtifactSource source)

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
            MaterializationPlanTestData.directoryVersionZip
                MaterializationPlanTestData.targetRootDirectoryVersionId
                (Some MaterializationArtifactSource.Deferred)

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
                    MaterializationPlanTestData.directoryVersionZip
                        MaterializationPlanTestData.targetRootDirectoryVersionId
                        (Some MaterializationArtifactSource.Deferred)
                ]
            )

        assertInvalid "RequiredArtifacts must include RecursiveDirectoryMetadata for the target root." (Validation.validatePlan plan)

    /// Verifies that the V1 response also fails when the target-root zip artifact is absent.
    [<Test>]
    member _.PlanMissingDirectoryVersionZipRootArtifactFailsValidationClearly() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.Direct,
                MaterializationCacheSelection.Bypass,
                [
                    MaterializationPlanTestData.recursiveDirectoryMetadata
                        MaterializationPlanTestData.targetRootDirectoryVersionId
                        (Some MaterializationArtifactSource.Deferred)
                ]
            )

        assertInvalid "RequiredArtifacts must include DirectoryVersionZip for the target root." (Validation.validatePlan plan)

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
                    MaterializationPlanTestData.directoryVersionZip
                        MaterializationPlanTestData.otherDirectoryVersionId
                        (Some MaterializationArtifactSource.Deferred)
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

    /// Verifies that cache-entry sources reject blank, whitespace, and null cache identity.
    [<Test>]
    member _.CacheEntrySourceRejectsMalformedCacheKeys() =
        let malformedSources =
            [
                "missing", None
                "blank", Some ""
                "whitespace", Some "   "
                "null", Some Unchecked.defaultof<string>
            ]

        for _, cacheKey in malformedSources do
            let source = { MaterializationArtifactSource.CacheOnly MaterializationPlanTestData.cacheKey with CacheKey = cacheKey }

            assertInvalid "Artifact CacheKey is required for CacheEntry sources." (Validation.validateArtifactSource source)

    /// Verifies that direct sources reject missing, blank, whitespace, and null direct URIs.
    [<Test>]
    member _.DirectUriSourceRejectsMissingBlankWhitespaceAndNullUris() =
        let malformedSources =
            [
                "missing", None, "Artifact DirectUri is required for DirectUri sources."
                "blank", Some "", "Artifact DirectUri must be an absolute http or https URI for DirectUri sources."
                "whitespace", Some "   ", "Artifact DirectUri must be an absolute http or https URI for DirectUri sources."
                "null", Some Unchecked.defaultof<string>, "Artifact DirectUri must be an absolute http or https URI for DirectUri sources."
            ]

        for _, directUri, expected in malformedSources do
            let source = { MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/root.zip" with DirectUri = directUri }

            assertInvalid expected (Validation.validateArtifactSource source)

    /// Verifies that cache registration scope is server-owned and cannot be supplied through the public selection contract.
    [<Test>]
    member _.CacheSelectionDoesNotSerializeCallerSuppliedScope() =
        let callerSuppliedJson = """{"Class":"MaterializationCacheSelection","SelectionKind":"requireCache","CacheScope":"storage-pool:caller-supplied"}"""

        let selection = JsonSerializer.Deserialize<MaterializationCacheSelection>(callerSuppliedJson, Grace.Shared.Constants.JsonSerializerOptions)

        let json: string = JsonSerializer.Serialize<MaterializationCacheSelection>(selection, Grace.Shared.Constants.JsonSerializerOptions)

        use document = JsonDocument.Parse(json)
        let mutable cacheScope = Unchecked.defaultof<JsonElement>

        Assert.That(selection.SelectionKind, Is.EqualTo(MaterializationCacheSelectionKind.RequireCache))
        Assert.That(document.RootElement.TryGetProperty("CacheScope", &cacheScope), Is.False)

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

    /// Verifies that cache-required request intent has one authoritative cache-required selection.
    [<Test>]
    member _.CacheRequiredRequestWithPreferCacheSelectionFailsValidationClearly() =
        let request =
            MaterializationPlanRequest.Create(
                MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CacheRequired,
                MaterializationCacheSelection.Preferred,
                [
                    MaterializationArtifactKind.DirectoryVersionZip
                ]
            )

        assertInvalid "CacheRequired materialization must use RequireCache selection." (Validation.validateRequest request)

    /// Verifies that cache-required plans have one authoritative cache-required selection.
    [<Test>]
    member _.CacheRequiredPlanWithPreferCacheSelectionFailsValidationClearly() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CacheRequired,
                MaterializationCacheSelection.Preferred,
                MaterializationPlanTestData.rootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
            )

        assertInvalid "CacheRequired materialization must use RequireCache selection." (Validation.validatePlan plan)

    /// Verifies that each supported public execution/cache-selection request pair is intentional.
    [<TestCase(MaterializationExecutionMode.Direct, MaterializationCacheSelectionKind.BypassCache)>]
    [<TestCase(MaterializationExecutionMode.CachePreferred, MaterializationCacheSelectionKind.PreferCache)>]
    [<TestCase(MaterializationExecutionMode.CacheRequired, MaterializationCacheSelectionKind.RequireCache)>]
    member _.SupportedRequestExecutionCacheSelectionPairsValidate(mode: MaterializationExecutionMode, selectionKind: MaterializationCacheSelectionKind) =
        let request =
            MaterializationPlanRequest.Create(
                MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId,
                mode,
                { MaterializationCacheSelection.Preferred with SelectionKind = selectionKind },
                [
                    MaterializationArtifactKind.DirectoryVersionZip
                ]
            )

        assertValid (Validation.validateRequest request)

    /// Verifies that unsupported public execution/cache-selection request pairs fail closed.
    [<TestCase(MaterializationExecutionMode.Direct, MaterializationCacheSelectionKind.PreferCache, "Direct materialization must use BypassCache selection.")>]
    [<TestCase(MaterializationExecutionMode.Direct,
               MaterializationCacheSelectionKind.RequireCache,
               "Direct materialization must not use RequireCache selection.")>]
    [<TestCase(MaterializationExecutionMode.CachePreferred,
               MaterializationCacheSelectionKind.BypassCache,
               "CachePreferred materialization must use PreferCache selection.")>]
    [<TestCase(MaterializationExecutionMode.CachePreferred,
               MaterializationCacheSelectionKind.RequireCache,
               "CachePreferred materialization must use PreferCache selection.")>]
    [<TestCase(MaterializationExecutionMode.CacheRequired,
               MaterializationCacheSelectionKind.BypassCache,
               "CacheRequired materialization must not use BypassCache selection.")>]
    [<TestCase(MaterializationExecutionMode.CacheRequired,
               MaterializationCacheSelectionKind.PreferCache,
               "CacheRequired materialization must use RequireCache selection.")>]
    member _.UnsupportedRequestExecutionCacheSelectionPairsFailValidation
        (
            mode: MaterializationExecutionMode,
            selectionKind: MaterializationCacheSelectionKind,
            expected: string
        ) =
        let request =
            MaterializationPlanRequest.Create(
                MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId,
                mode,
                { MaterializationCacheSelection.Preferred with SelectionKind = selectionKind },
                [
                    MaterializationArtifactKind.DirectoryVersionZip
                ]
            )

        assertInvalid expected (Validation.validateRequest request)

    /// Verifies that each supported public execution/cache-selection plan pair is intentional.
    [<TestCase(MaterializationExecutionMode.Direct, MaterializationCacheSelectionKind.BypassCache)>]
    [<TestCase(MaterializationExecutionMode.CachePreferred, MaterializationCacheSelectionKind.PreferCache)>]
    [<TestCase(MaterializationExecutionMode.CacheRequired, MaterializationCacheSelectionKind.RequireCache)>]
    member _.SupportedPlanExecutionCacheSelectionPairsValidate(mode: MaterializationExecutionMode, selectionKind: MaterializationCacheSelectionKind) =
        let rootArtifacts =
            match mode with
            | MaterializationExecutionMode.Direct -> MaterializationPlanTestData.directRootArtifacts
            | _ -> MaterializationPlanTestData.rootArtifacts

        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                mode,
                { MaterializationCacheSelection.Preferred with SelectionKind = selectionKind },
                rootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
            )

        let plan =
            if mode = MaterializationExecutionMode.Direct then
                plan
            else
                MaterializationPlanTestData.withMatchingCacheGrant plan

        assertValid (Validation.validatePlan plan)

    /// Verifies that unsupported public execution/cache-selection plan pairs fail closed.
    [<TestCase(MaterializationExecutionMode.Direct, MaterializationCacheSelectionKind.PreferCache, "Direct materialization must use BypassCache selection.")>]
    [<TestCase(MaterializationExecutionMode.Direct,
               MaterializationCacheSelectionKind.RequireCache,
               "Direct materialization must not use RequireCache selection.")>]
    [<TestCase(MaterializationExecutionMode.CachePreferred,
               MaterializationCacheSelectionKind.BypassCache,
               "CachePreferred materialization must use PreferCache selection.")>]
    [<TestCase(MaterializationExecutionMode.CachePreferred,
               MaterializationCacheSelectionKind.RequireCache,
               "CachePreferred materialization must use PreferCache selection.")>]
    [<TestCase(MaterializationExecutionMode.CacheRequired,
               MaterializationCacheSelectionKind.BypassCache,
               "CacheRequired materialization must not use BypassCache selection.")>]
    [<TestCase(MaterializationExecutionMode.CacheRequired,
               MaterializationCacheSelectionKind.PreferCache,
               "CacheRequired materialization must use RequireCache selection.")>]
    member _.UnsupportedPlanExecutionCacheSelectionPairsFailValidation
        (
            mode: MaterializationExecutionMode,
            selectionKind: MaterializationCacheSelectionKind,
            expected: string
        ) =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                mode,
                { MaterializationCacheSelection.Preferred with SelectionKind = selectionKind },
                MaterializationPlanTestData.rootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
            )

        assertInvalid expected (Validation.validatePlan plan)

    /// Verifies that direct execution cannot also require Grace Cache artifact sourcing.
    [<Test>]
    member _.DirectRequestWithRequireCacheSelectionFailsValidationClearly() =
        let request =
            MaterializationPlanRequest.Create(
                MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.Direct,
                MaterializationCacheSelection.Required,
                [
                    MaterializationArtifactKind.DirectoryVersionZip
                ]
            )

        assertInvalid "Direct materialization must not use RequireCache selection." (Validation.validateRequest request)

    /// Verifies that direct plans cannot claim every artifact must come from Grace Cache.
    [<Test>]
    member _.DirectPlanWithRequireCacheSelectionFailsValidationClearly() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.Direct,
                MaterializationCacheSelection.Required,
                MaterializationPlanTestData.rootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
            )

        assertInvalid "Direct materialization must not use RequireCache selection." (Validation.validatePlan plan)

    /// Verifies that branch target selectors use the same Grace object-name contract as branch commands.
    [<TestCase("feature/cache-plan")>]
    [<TestCase("1feature")>]
    [<TestCase("f")>]
    member _.BranchTargetSelectorsRejectNamesThatBranchCommandsReject(branchName: string) =
        let selector = MaterializationTargetSelector.ForBranch(BranchName branchName)

        assertInvalid "TargetSelector.BranchName must be a valid Grace branch name." (Validation.validateTargetSelector selector)

    /// Verifies that malformed branch target selector DTOs fail closed instead of reaching regex with a null input.
    [<Test>]
    member _.BranchTargetSelectorWithNullNameFailsValidationWithoutThrowing() =
        let selector = { MaterializationTargetSelector.ForBranch MaterializationPlanTestData.branchName with BranchName = Some Unchecked.defaultof<BranchName> }

        Assert.DoesNotThrow(
            Action (fun () ->
                Validation.validateTargetSelector selector
                |> ignore)
        )

        assertInvalid "TargetSelector.BranchName is required." (Validation.validateTargetSelector selector)

    /// Verifies that branch target selectors reject blank and whitespace-only selector identity.
    [<TestCase("")>]
    [<TestCase("   ")>]
    member _.BranchTargetSelectorsRejectBlankAndWhitespaceNames(branchName: string) =
        let selector = MaterializationTargetSelector.ForBranch(BranchName branchName)

        assertInvalid "TargetSelector.BranchName is required." (Validation.validateTargetSelector selector)

    /// Verifies that direct plans reject cache-entry artifact sources even when the execution/cache pair is valid.
    [<Test>]
    member _.DirectBypassPlanRejectsCacheEntryArtifactSources() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.Direct,
                MaterializationCacheSelection.Bypass,
                [
                    MaterializationPlanTestData.directoryVersionZip
                        MaterializationPlanTestData.targetRootDirectoryVersionId
                        (Some(MaterializationArtifactSource.CacheOnly MaterializationPlanTestData.cacheKey))
                    MaterializationPlanTestData.recursiveDirectoryMetadata
                        MaterializationPlanTestData.targetRootDirectoryVersionId
                        (Some MaterializationArtifactSource.Deferred)
                ]
            )

        assertInvalid "Direct/Bypass plans must not require CacheEntry artifact sources." (Validation.validatePlan plan)

    /// Verifies that BypassCache selection alone rejects cache-entry sources even when execution mode is not Direct.
    [<Test>]
    member _.BypassSelectionRejectsCacheEntrySourcesWhenExecutionModeIsNotDirect() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CachePreferred,
                MaterializationCacheSelection.Bypass,
                MaterializationPlanTestData.rootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
            )

        assertInvalid "Direct/Bypass plans must not require CacheEntry artifact sources." (Validation.validatePlan plan)

    /// Verifies that Direct execution rejects cache-entry sources even when cache selection is inconsistent.
    [<Test>]
    member _.DirectExecutionRejectsCacheEntrySourcesWhenSelectionIsInconsistent() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.Direct,
                MaterializationCacheSelection.Preferred,
                MaterializationPlanTestData.rootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
            )

        assertInvalid "Direct/Bypass plans must not require CacheEntry artifact sources." (Validation.validatePlan plan)

    /// Verifies that whole-file artifact identity is unique by target root and repository-relative path.
    [<Test>]
    member _.PlanRejectsDuplicateWholeFileContentForSameRootAndRelativePath() =
        let duplicatePath = RelativePath "src/app.fs"

        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.Direct,
                MaterializationCacheSelection.Bypass,
                [
                    yield! MaterializationPlanTestData.directRootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
                    MaterializationArtifactDescriptor.WholeFileContent(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        duplicatePath,
                        Some MaterializationPlanTestData.sha256Hash,
                        None,
                        Some(MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/src/app-v1.fs")
                    )
                    MaterializationArtifactDescriptor.WholeFileContent(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        duplicatePath,
                        None,
                        Some MaterializationPlanTestData.blake3Hash,
                        Some(MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/src/app-v2.fs")
                    )
                ]
            )

        assertInvalid "RequiredArtifacts must include at most one WholeFileContent for each target root and relative path." (Validation.validatePlan plan)

    /// Verifies that whole-file descriptors for distinct repository-relative paths remain valid in direct plans.
    [<Test>]
    member _.PlanAllowsWholeFileContentForDistinctRelativePaths() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.Direct,
                MaterializationCacheSelection.Bypass,
                [
                    yield! MaterializationPlanTestData.directRootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
                    MaterializationArtifactDescriptor.WholeFileContent(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        RelativePath "src/app.fs",
                        Some MaterializationPlanTestData.sha256Hash,
                        None,
                        Some(MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/src/app.fs")
                    )
                    MaterializationArtifactDescriptor.WholeFileContent(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        RelativePath "src/appsettings.json",
                        Some(Sha256Hash "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
                        None,
                        Some(MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/src/appsettings.json")
                    )
                ]
            )

        assertValid (Validation.validatePlan (MaterializationPlanTestData.withMatchingCacheGrant plan))

    /// Verifies that whole-file descriptors reject blank hash fields even when the other hash is valid.
    [<TestCase(true)>]
    [<TestCase(false)>]
    member _.WholeFileContentRejectsBlankHashOptions(evaluateSha256: bool) =
        let descriptor =
            MaterializationArtifactDescriptor.WholeFileContent(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                RelativePath "src/app.fs",
                (if evaluateSha256 then
                     Some(Sha256Hash "   ")
                 else
                     Some MaterializationPlanTestData.sha256Hash),
                (if evaluateSha256 then
                     Some MaterializationPlanTestData.blake3Hash
                 else
                     Some(Blake3Hash "")),
                Some(MaterializationArtifactSource.CacheOnly MaterializationPlanTestData.cacheKey)
            )

        let expected =
            if evaluateSha256 then
                "Artifact Sha256Hash must be a canonical lowercase 64-character hexadecimal value for WholeFileContent descriptors."
            else
                "Artifact Blake3Hash must be a canonical lowercase 64-character hexadecimal value for WholeFileContent descriptors."

        assertInvalid expected (Validation.validateArtifactDescriptor descriptor)

    /// Verifies that whole-file descriptors require canonical lowercase 64-hex hash identity.
    [<TestCase(true, "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")>]
    [<TestCase(true, "0123456789abcdef")>]
    [<TestCase(true, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")>]
    [<TestCase(false, "89ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF01234567")>]
    [<TestCase(false, "89abcdef01234567")>]
    [<TestCase(false, "89abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456g")>]
    member _.WholeFileContentRejectsMalformedHashOptions(evaluateSha256: bool, malformedHash: string) =
        let descriptor =
            MaterializationArtifactDescriptor.WholeFileContent(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                RelativePath "src/app.fs",
                (if evaluateSha256 then
                     Some(Sha256Hash malformedHash)
                 else
                     Some MaterializationPlanTestData.sha256Hash),
                (if evaluateSha256 then
                     Some MaterializationPlanTestData.blake3Hash
                 else
                     Some(Blake3Hash malformedHash)),
                Some(MaterializationArtifactSource.CacheOnly MaterializationPlanTestData.cacheKey)
            )

        let expected =
            if evaluateSha256 then
                "Artifact Sha256Hash must be a canonical lowercase 64-character hexadecimal value for WholeFileContent descriptors."
            else
                "Artifact Blake3Hash must be a canonical lowercase 64-character hexadecimal value for WholeFileContent descriptors."

        assertInvalid expected (Validation.validateArtifactDescriptor descriptor)

    /// Verifies that cache-required root artifacts must carry an authoritative cache source.
    [<Test>]
    member _.CacheRequiredPlanRejectsMissingRootArtifactSources() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CacheRequired,
                MaterializationCacheSelection.Required,
                [
                    MaterializationPlanTestData.directoryVersionZip MaterializationPlanTestData.targetRootDirectoryVersionId None
                    MaterializationPlanTestData.recursiveDirectoryMetadata
                        MaterializationPlanTestData.targetRootDirectoryVersionId
                        (Some(MaterializationArtifactSource.CacheOnly MaterializationPlanTestData.cacheKey))
                ]
            )

        assertInvalid "CacheRequired plans must require CacheEntry artifact sources for every required artifact." (Validation.validatePlan plan)

    /// Verifies that cache-required plans fail closed unless each artifact source is an authoritative cache entry.
    [<TestCase(MaterializationArtifactSourceKind.DirectUri)>]
    [<TestCase(MaterializationArtifactSourceKind.Deferred)>]
    member _.CacheRequiredPlanRequiresCacheEntryArtifactSources(sourceKind: MaterializationArtifactSourceKind) =
        let source =
            match sourceKind with
            | MaterializationArtifactSourceKind.DirectUri -> MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/root.zip"
            | MaterializationArtifactSourceKind.Deferred -> MaterializationArtifactSource.Deferred
            | _ -> failwith "Unsupported source kind."

        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CacheRequired,
                MaterializationCacheSelection.Required,
                [
                    MaterializationPlanTestData.directoryVersionZip MaterializationPlanTestData.targetRootDirectoryVersionId (Some source)
                    MaterializationPlanTestData.recursiveDirectoryMetadata
                        MaterializationPlanTestData.targetRootDirectoryVersionId
                        (Some(MaterializationArtifactSource.CacheOnly MaterializationPlanTestData.cacheKey))
                ]
            )

        assertInvalid "CacheRequired plans must require CacheEntry artifact sources for every required artifact." (Validation.validatePlan plan)

    /// Verifies that duplicate FileManifest identity cannot name multiple sources for one target root and storage pool.
    [<Test>]
    member _.PlanRejectsDuplicateFileManifestIdentityForSameRootStoragePoolAndAddress() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CacheRequired,
                MaterializationCacheSelection.Required,
                [
                    yield! MaterializationPlanTestData.rootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
                    MaterializationArtifactDescriptor.FileManifest(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        MaterializationPlanTestData.manifestAddress,
                        MaterializationPlanTestData.storagePoolId,
                        Some(MaterializationArtifactSource.CacheOnly "cache/manifest/first")
                    )
                    MaterializationArtifactDescriptor.FileManifest(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        MaterializationPlanTestData.manifestAddress,
                        MaterializationPlanTestData.storagePoolId,
                        Some(MaterializationArtifactSource.CacheOnly "cache/manifest/second")
                    )
                ]
            )

        assertInvalid
            "RequiredArtifacts must include at most one FileManifest for each target root, storage pool, and manifest address."
            (Validation.validatePlan plan)

    /// Verifies that duplicate ContentBlock identity cannot name multiple sources for one target root and storage pool.
    [<Test>]
    member _.PlanRejectsDuplicateContentBlockIdentityForSameRootStoragePoolAndAddress() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CacheRequired,
                MaterializationCacheSelection.Required,
                [
                    yield! MaterializationPlanTestData.rootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
                    MaterializationArtifactDescriptor.ContentBlock(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        MaterializationPlanTestData.contentBlockAddress,
                        MaterializationPlanTestData.storagePoolId,
                        Some(MaterializationArtifactSource.CacheOnly "cache/block/first")
                    )
                    MaterializationArtifactDescriptor.ContentBlock(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        MaterializationPlanTestData.contentBlockAddress,
                        MaterializationPlanTestData.storagePoolId,
                        Some(MaterializationArtifactSource.CacheOnly "cache/block/second")
                    )
                ]
            )

        assertInvalid
            "RequiredArtifacts must include at most one ContentBlock for each target root, storage pool, and content block address."
            (Validation.validatePlan plan)

    /// Verifies that distinct CAS descriptor identities remain valid in the same target root.
    [<Test>]
    member _.PlanAllowsDistinctCasDescriptorIdentities() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CacheRequired,
                MaterializationCacheSelection.Required,
                [
                    yield! MaterializationPlanTestData.rootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
                    MaterializationArtifactDescriptor.FileManifest(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        MaterializationPlanTestData.manifestAddress,
                        MaterializationPlanTestData.storagePoolId,
                        Some(MaterializationArtifactSource.CacheOnly "cache/manifest/first")
                    )
                    MaterializationArtifactDescriptor.FileManifest(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        ManifestAddress "1111111111111111111111111111111111111111111111111111111111111111",
                        MaterializationPlanTestData.storagePoolId,
                        Some(MaterializationArtifactSource.CacheOnly "cache/manifest/second")
                    )
                    MaterializationArtifactDescriptor.ContentBlock(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        MaterializationPlanTestData.contentBlockAddress,
                        MaterializationPlanTestData.storagePoolId,
                        Some(MaterializationArtifactSource.CacheOnly "cache/block/first")
                    )
                    MaterializationArtifactDescriptor.ContentBlock(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        ContentBlockAddress "2222222222222222222222222222222222222222222222222222222222222222",
                        MaterializationPlanTestData.storagePoolId,
                        Some(MaterializationArtifactSource.CacheOnly "cache/block/second")
                    )
                ]
            )

        assertValid (Validation.validatePlan (MaterializationPlanTestData.withMatchingCacheGrant plan))

    /// Verifies that CAS uniqueness includes storage pool, allowing same addresses in distinct pools.
    [<Test>]
    member _.PlanAllowsCasDescriptorsWithSameAddressInDifferentStoragePools() =
        let secondaryStoragePoolId = StoragePoolId "storage-pool-secondary"

        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.CacheRequired,
                MaterializationCacheSelection.Required,
                [
                    yield! MaterializationPlanTestData.rootArtifacts MaterializationPlanTestData.targetRootDirectoryVersionId
                    MaterializationArtifactDescriptor.FileManifest(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        MaterializationPlanTestData.manifestAddress,
                        MaterializationPlanTestData.storagePoolId,
                        Some(MaterializationArtifactSource.CacheOnly "cache/manifest/main")
                    )
                    MaterializationArtifactDescriptor.FileManifest(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        MaterializationPlanTestData.manifestAddress,
                        secondaryStoragePoolId,
                        Some(MaterializationArtifactSource.CacheOnly "cache/manifest/secondary")
                    )
                    MaterializationArtifactDescriptor.ContentBlock(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        MaterializationPlanTestData.contentBlockAddress,
                        MaterializationPlanTestData.storagePoolId,
                        Some(MaterializationArtifactSource.CacheOnly "cache/block/main")
                    )
                    MaterializationArtifactDescriptor.ContentBlock(
                        MaterializationPlanTestData.targetRootDirectoryVersionId,
                        MaterializationPlanTestData.contentBlockAddress,
                        secondaryStoragePoolId,
                        Some(MaterializationArtifactSource.CacheOnly "cache/block/secondary")
                    )
                ]
            )

        assertValid (Validation.validatePlan (MaterializationPlanTestData.withMatchingCacheGrant plan))

    /// Verifies that V1 plans reject duplicate singleton root artifact descriptors.
    [<Test>]
    member _.PlanRejectsDuplicateTargetRootSingletonArtifacts() =
        let plan =
            MaterializationPlan.Create(
                MaterializationPlanTestData.targetRootDirectoryVersionId,
                MaterializationExecutionMode.Direct,
                MaterializationCacheSelection.Bypass,
                [
                    MaterializationPlanTestData.directoryVersionZip
                        MaterializationPlanTestData.targetRootDirectoryVersionId
                        (Some(MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/root.zip"))
                    MaterializationPlanTestData.directoryVersionZip
                        MaterializationPlanTestData.targetRootDirectoryVersionId
                        (Some(MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/root-copy.zip"))
                    MaterializationPlanTestData.recursiveDirectoryMetadata
                        MaterializationPlanTestData.targetRootDirectoryVersionId
                        (Some MaterializationArtifactSource.Deferred)
                    MaterializationPlanTestData.recursiveDirectoryMetadata
                        MaterializationPlanTestData.targetRootDirectoryVersionId
                        (Some(MaterializationArtifactSource.Direct "https://cache.example.test/artifacts/metadata.json"))
                ]
            )

        let result = Validation.validatePlan plan

        assertInvalid "RequiredArtifacts must include exactly one DirectoryVersionZip for the target root." result
        assertInvalid "RequiredArtifacts must include exactly one RecursiveDirectoryMetadata for the target root." result

    /// Verifies that CAS descriptors use canonical lowercase BLAKE3 addresses.
    [<TestCase(MaterializationArtifactKind.FileManifest, "manifest-blake3-abc123")>]
    [<TestCase(MaterializationArtifactKind.FileManifest, "5DD11FDB1534C0F1C4FECCA3C07498F9B59A7DFF26D69FE78E43F7CE50D90895")>]
    [<TestCase(MaterializationArtifactKind.ContentBlock, "block-blake3-def456")>]
    [<TestCase(MaterializationArtifactKind.ContentBlock, "75359c5d838dda90b12c6cbcdd9b71a1f193daf6c23fa2bfd496065bed57a30g")>]
    member _.CasArtifactDescriptorsRejectMalformedAddresses(kind: MaterializationArtifactKind, address: string) =
        let descriptor =
            match kind with
            | MaterializationArtifactKind.FileManifest ->
                MaterializationArtifactDescriptor.FileManifest(
                    MaterializationPlanTestData.targetRootDirectoryVersionId,
                    ManifestAddress address,
                    MaterializationPlanTestData.storagePoolId,
                    Some(MaterializationArtifactSource.CacheOnly MaterializationPlanTestData.cacheKey)
                )
            | MaterializationArtifactKind.ContentBlock ->
                MaterializationArtifactDescriptor.ContentBlock(
                    MaterializationPlanTestData.targetRootDirectoryVersionId,
                    ContentBlockAddress address,
                    MaterializationPlanTestData.storagePoolId,
                    Some(MaterializationArtifactSource.CacheOnly MaterializationPlanTestData.cacheKey)
                )
            | _ -> failwith "Unsupported test kind."

        let expected =
            match kind with
            | MaterializationArtifactKind.FileManifest ->
                "Artifact ManifestAddress must be a canonical lowercase 64-character hexadecimal value for FileManifest descriptors."
            | MaterializationArtifactKind.ContentBlock ->
                "Artifact ContentBlockAddress must be a canonical lowercase 64-character hexadecimal value for ContentBlock descriptors."
            | _ -> failwith "Unsupported test kind."

        assertInvalid expected (Validation.validateArtifactDescriptor descriptor)

    /// Verifies that direct sources require absolute fetch URIs with an allowed scheme.
    [<TestCase("artifacts/root.zip")>]
    [<TestCase("/artifacts/root.zip")>]
    [<TestCase("file:///tmp/root.zip")>]
    [<TestCase("ftp://cache.example.test/artifacts/root.zip")>]
    member _.DirectUriSourceRejectsRelativeFileAndUnsupportedSchemes(uri: string) =
        let source = MaterializationArtifactSource.Direct uri

        assertInvalid "Artifact DirectUri must be an absolute http or https URI for DirectUri sources." (Validation.validateArtifactSource source)

    /// Verifies malformed null cache sources remain validation failures when grant matching also runs.
    [<Test>]
    member _.PlanWithNullArtifactSourceAndGrantReturnsValidationErrorsWithoutThrowing() =
        let validPlan = MaterializationPlanTestData.validPlan ()

        let malformedArtifacts =
            validPlan.RequiredArtifacts
            |> Seq.mapi (fun index artifact ->
                if index = 0 then
                    { artifact with Source = Some Unchecked.defaultof<MaterializationArtifactSource> }
                else
                    artifact)
            |> List<MaterializationArtifactDescriptor>

        let malformedPlan = { validPlan with RequiredArtifacts = malformedArtifacts }

        Assert.DoesNotThrow(Action(fun () -> Validation.validatePlan malformedPlan |> ignore))
        assertInvalid "Artifact Source is required." (Validation.validatePlan malformedPlan)

    /// Verifies cache-backed plans require the complete signed-grant envelope before client use.
    [<Test>]
    member _.CacheBackedPlanRejectsMissingGrantHeaderAndBlankSignature() =
        let validPlan = MaterializationPlanTestData.validPlan ()
        let validGrant = validPlan.ArtifactGrant.Value
        let missingHeaderPlan = { validPlan with ArtifactGrant = Some { validGrant with Header = Unchecked.defaultof<ArtifactGrantHeader> } }
        let blankSignaturePlan = { validPlan with ArtifactGrant = Some { validGrant with Signature = " " } }

        assertInvalid "ArtifactGrant must contain a complete signed envelope." (Validation.validatePlan missingHeaderPlan)
        assertInvalid "ArtifactGrant must contain a complete signed envelope." (Validation.validatePlan blankSignaturePlan)

    /// Verifies cache-plan endpoint substitution is rejected before a client can present its grant or holder proof.
    [<Test>]
    member _.CacheBackedPlanRejectsSignedGrantEndpointSubstitution() =
        let validPlan = MaterializationPlanTestData.validPlan ()

        let substitutedArtifacts =
            validPlan.RequiredArtifacts
            |> Seq.map (fun artifact ->
                match artifact.Source with
                | Some source when source.SourceKind = MaterializationArtifactSourceKind.CacheEntry ->
                    { artifact with Source = Some { source with CacheEndpoint = Some "https://other-cache.example.test" } }
                | _ -> artifact)
            |> List<MaterializationArtifactDescriptor>

        let substitutedPlan = { validPlan with RequiredArtifacts = substitutedArtifacts }

        assertInvalid "ArtifactGrant CacheEndpoint must match every Cache source exactly." (Validation.validatePlan substitutedPlan)

    /// Verifies an HTTP cache source remains valid only when the complete grant binds that exact approved endpoint.
    [<Test>]
    member _.CacheBackedPlanAllowsGrantBoundHttpEndpoint() =
        let validPlan = MaterializationPlanTestData.validPlan ()
        let httpEndpoint = "http://cache.example.test:8080/artifacts"

        let httpArtifacts =
            validPlan.RequiredArtifacts
            |> Seq.map (fun artifact ->
                match artifact.Source with
                | Some source when source.SourceKind = MaterializationArtifactSourceKind.CacheEntry ->
                    { artifact with Source = Some { source with CacheEndpoint = Some httpEndpoint } }
                | _ -> artifact)
            |> List<MaterializationArtifactDescriptor>

        let httpGrant = { validPlan.ArtifactGrant.Value with Payload = { validPlan.ArtifactGrant.Value.Payload with CacheEndpoint = httpEndpoint } }
        let httpPlan = { validPlan with RequiredArtifacts = httpArtifacts; ArtifactGrant = Some httpGrant }

        assertValid (Validation.validatePlan httpPlan)

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

        let referenceTypeSelector =
            { MaterializationTargetSelector.ForReferenceType(MaterializationPlanTestData.branchName, MaterializationPlanTestData.referenceType) with
                ReferenceId = Some MaterializationPlanTestData.referenceId
            }

        assertInvalid "TargetSelector.ReferenceId must be empty for DirectoryVersionId selectors." (Validation.validateTargetSelector directorySelector)
        assertInvalid "TargetSelector.BranchName must be empty for ReferenceId selectors." (Validation.validateTargetSelector referenceSelector)
        assertInvalid "TargetSelector.DirectoryVersionId must be empty for BranchName selectors." (Validation.validateTargetSelector branchSelector)
        assertInvalid "TargetSelector.ReferenceId must be empty for ReferenceType selectors." (Validation.validateTargetSelector referenceTypeSelector)

    /// Verifies that every selector kind rejects each identity field owned by the other selector kinds.
    [<Test>]
    member _.TargetSelectorsRejectEveryOffKindIdentityField() =
        let cases =
            [
                { MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId with
                    ReferenceId = Some MaterializationPlanTestData.referenceId
                },
                "TargetSelector.ReferenceId must be empty for DirectoryVersionId selectors."
                { MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId with
                    BranchName = Some MaterializationPlanTestData.branchName
                },
                "TargetSelector.BranchName must be empty for DirectoryVersionId selectors."
                { MaterializationTargetSelector.ForDirectoryVersion MaterializationPlanTestData.targetRootDirectoryVersionId with
                    BranchId = Some MaterializationPlanTestData.branchId
                },
                "TargetSelector.BranchId must be empty for DirectoryVersionId selectors."
                { MaterializationTargetSelector.ForReference MaterializationPlanTestData.referenceId with
                    DirectoryVersionId = Some MaterializationPlanTestData.targetRootDirectoryVersionId
                },
                "TargetSelector.DirectoryVersionId must be empty for ReferenceId selectors."
                { MaterializationTargetSelector.ForReference MaterializationPlanTestData.referenceId with
                    BranchName = Some MaterializationPlanTestData.branchName
                },
                "TargetSelector.BranchName must be empty for ReferenceId selectors."
                { MaterializationTargetSelector.ForReference MaterializationPlanTestData.referenceId with BranchId = Some MaterializationPlanTestData.branchId },
                "TargetSelector.BranchId must be empty for ReferenceId selectors."
                { MaterializationTargetSelector.ForBranch MaterializationPlanTestData.branchName with
                    DirectoryVersionId = Some MaterializationPlanTestData.targetRootDirectoryVersionId
                },
                "TargetSelector.DirectoryVersionId must be empty for BranchName selectors."
                { MaterializationTargetSelector.ForBranch MaterializationPlanTestData.branchName with
                    ReferenceId = Some MaterializationPlanTestData.referenceId
                },
                "TargetSelector.ReferenceId must be empty for BranchName selectors."
                { MaterializationTargetSelector.ForBranch MaterializationPlanTestData.branchName with BranchId = Some MaterializationPlanTestData.branchId },
                "TargetSelector.BranchId must be empty for BranchName selectors."
                { MaterializationTargetSelector.ForBranch MaterializationPlanTestData.branchName with
                    ReferenceType = Some MaterializationPlanTestData.referenceType
                },
                "TargetSelector.ReferenceType must be empty for BranchName selectors."
                { MaterializationTargetSelector.ForReferenceType(MaterializationPlanTestData.branchName, MaterializationPlanTestData.referenceType) with
                    DirectoryVersionId = Some MaterializationPlanTestData.targetRootDirectoryVersionId
                },
                "TargetSelector.DirectoryVersionId must be empty for ReferenceType selectors."
                { MaterializationTargetSelector.ForReferenceType(MaterializationPlanTestData.branchName, MaterializationPlanTestData.referenceType) with
                    ReferenceId = Some MaterializationPlanTestData.referenceId
                },
                "TargetSelector.ReferenceId must be empty for ReferenceType selectors."
            ]

        for selector, expected in cases do
            assertInvalid expected (Validation.validateTargetSelector selector)

    /// Verifies ReferenceType selectors reject missing or ambiguous branch identity.
    [<Test>]
    member _.ReferenceTypeSelectorsRequireExactlyOneBranchIdentity() =
        let missingIdentity =
            { MaterializationTargetSelector.ForReferenceType(MaterializationPlanTestData.branchName, MaterializationPlanTestData.referenceType) with
                BranchName = None
            }

        let ambiguousIdentity =
            { MaterializationTargetSelector.ForReferenceType(MaterializationPlanTestData.branchName, MaterializationPlanTestData.referenceType) with
                BranchId = Some MaterializationPlanTestData.branchId
            }

        let emptyBranchId =
            { MaterializationTargetSelector.ForReferenceType(BranchId.Empty, MaterializationPlanTestData.referenceType) with BranchId = Some BranchId.Empty }

        assertInvalid
            "TargetSelector must include exactly one of BranchId or BranchName for ReferenceType selectors."
            (Validation.validateTargetSelector missingIdentity)

        assertInvalid
            "TargetSelector must include exactly one of BranchId or BranchName for ReferenceType selectors."
            (Validation.validateTargetSelector ambiguousIdentity)

        assertInvalid "TargetSelector.BranchId is required when supplied for ReferenceType selectors." (Validation.validateTargetSelector emptyBranchId)
