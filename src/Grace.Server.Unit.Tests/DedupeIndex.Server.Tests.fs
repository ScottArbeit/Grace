namespace Grace.Server.UnitTests

open Grace.Server
open Grace.Shared
open Grace.Shared.Parameters.Storage
open Grace.Types.ContentBlockMetadata
open Grace.Types.Common
open Grace.Types.Repository
open Grace.Types.UploadSession
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text

/// Covers dedupe Index Server behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type DedupeIndexServerTests() =

    let timestamp = Instant.FromUtc(2026, 5, 24, 14, 0)
    let storagePoolId = StoragePoolId "pool-main"
    let repositoryId = Guid.Parse("f6494929-27ef-4f68-897c-442f5ead4941")

    let bytes (text: string) = Encoding.UTF8.GetBytes(text)

    /// Builds encoded Block test data for the server unit dedupe Index scenarios in this file.
    let encodedBlock name chunkCount =
        let mutable physicalOffset = 0L

        let chunks =
            [|
                for index in 0 .. chunkCount - 1 do
                    let chunkBytes = bytes $"{name}-chunk-{index:D2}-{String('x', 128)}"
                    let input: ContentBlockFormat.ContentBlockInputChunk = { PhysicalOffset = physicalOffset; Bytes = chunkBytes }
                    physicalOffset <- physicalOffset + int64 chunkBytes.Length
                    input
            |]

        match ContentBlockFormat.encode chunks with
        | Ok block -> block
        | Error error ->
            Assert.Fail($"Expected test content block to encode, got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    /// Builds decoded Chunk Addresses test data for the server unit dedupe Index scenarios in this file.
    let decodedChunkAddresses (block: ContentBlockFormat.EncodedContentBlock) =
        block.Chunks
        |> Array.map (fun chunk -> chunk.Address)

    /// Builds manifest For test data for the server unit dedupe Index scenarios in this file.
    let manifestFor (block: ContentBlockFormat.EncodedContentBlock) =
        let size =
            block.Chunks
            |> Array.sumBy (fun chunk -> int64 chunk.Length)

        let fileHash = FileContentHash(ContentAddress.computeBlake3Hex block.Payload)

        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                RabinChunking.SuiteName,
                fileHash,
                size,
                [
                    ContentBlock.Create(block.Address, 0L, size)
                ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    /// Builds finalized Session test data for the server unit dedupe Index scenarios in this file.
    let finalizedSession manifest =
        { UploadSessionDto.Default with
            RepositoryId = repositoryId
            LifecycleState = UploadSessionLifecycleState.RetentionPending
            FinalizedManifestAddress = Some manifest.ManifestAddress
        }

    /// Builds non Finalized Session test data for the server unit dedupe Index scenarios in this file.
    let nonFinalizedSession () =
        { UploadSessionDto.Default with
            RepositoryId = repositoryId
            LifecycleState = UploadSessionLifecycleState.UploadingBlocks
            FinalizedManifestAddress = None
        }

    /// Builds metadata For test data for the server unit dedupe Index scenarios in this file.
    let metadataFor activeManifestCount metadataVersion (block: ContentBlockFormat.EncodedContentBlock) =
        {
            Class = nameof ContentBlockMetadata
            StoragePoolId = storagePoolId
            ContentBlockAddress = block.Address
            BlockFormatVersion = 1s
            StoragePlacement =
                {
                    StorageAccountName = "cas-account"
                    StorageContainerName = StorageContainerName "cas-container"
                    ObjectKey = StorageKeys.contentBlockObjectKey block.Address
                    ETag = Some $"etag-{metadataVersion}"
                }
            Ranges =
                [|
                    {
                        OrdinalStart = 0
                        OrdinalCount = block.Chunks.Length
                        ActiveManifestCount = activeManifestCount
                        PhysicalOffset = 0L
                        PhysicalLength = block.Payload.LongLength
                    }
                |]
            TotalPhysicalBytes = block.Payload.LongLength
            ActivePhysicalBytes = if activeManifestCount > 0 then block.Payload.LongLength else 0L
            MetadataVersion = metadataVersion
            UpdatedAt = timestamp
        }

    let payloadFor (block: ContentBlockFormat.EncodedContentBlock) : FinalizeManifestBlockPayload = { Address = block.Address; Payload = block.Payload }

    /// Builds source For test data for the server unit dedupe Index scenarios in this file.
    let sourceFor session manifest (block: ContentBlockFormat.EncodedContentBlock) metadata : DedupeIndex.FinalizedManifestIndexSource =
        { StoragePoolId = storagePoolId; Session = session; Manifest = manifest; BlockPayloads = [| payloadFor block |]; Metadata = [| metadata |] }

    let discover records requested = DedupeIndex.discover storagePoolId requested timestamp records

    let defaultRepository repositoryId = { RepositoryDto.Default with RepositoryId = repositoryId; StoragePoolId = StoragePoolRouting.defaultStoragePoolId }

    /// Builds candidate Shape test data for the server unit dedupe Index scenarios in this file.
    let candidateShape (candidate: ContentBlockDiscoveryCandidate) =
        candidate.ContentBlockAddress, candidate.OrdinalStart, candidate.OrdinalCount, candidate.MetadataVersion, candidate.ProtectedChunkAddresses

    /// Builds protected Chunk Address test data for the server unit dedupe Index scenarios in this file.
    let protectedChunkAddress storagePoolId chunkAddress =
        let preimage = $"grace.dedupe-index.v1.protected-window\n{storagePoolId}\n{chunkAddress}"
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(preimage))
        $"protected-sha256:{Convert.ToHexString(hash).ToLowerInvariant()}"

    /// Verifies that protected Chunk Address Known Vector Keeps Sha256 Label And Preimage Stable.
    [<Test>]
    member _.ProtectedChunkAddressKnownVectorKeepsSha256LabelAndPreimageStable() =
        let chunkAddress = ChunkAddress "chunk-blake3-0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        let protectedAddress = protectedChunkAddress storagePoolId chunkAddress

        Assert.That(protectedAddress, Is.EqualTo("protected-sha256:12ddb278d672194eeefe9d1bec08a381a8a514a40c65fc206296f68b213dab86"))

        Assert.That(protectedAddress, Does.StartWith("protected-sha256:"))
        Assert.That(protectedAddress, Does.Not.Contain("blake3"))

    /// Verifies that writes Only After Finalized Manifest And Active Metadata Without Raw Inventory.
    [<Test>]
    member _.WritesOnlyAfterFinalizedManifestAndActiveMetadataWithoutRawInventory() =
        let block = encodedBlock "primary" 12
        let manifest = manifestFor block
        let activeMetadata = metadataFor 1 7L block
        let reclaimableMetadata = metadataFor 0 7L block
        let requestedChunk = (decodedChunkAddresses block)[0]

        let nonFinalized = DedupeIndex.recordsAfterFinalize (sourceFor (nonFinalizedSession ()) manifest block activeMetadata)

        let inactive = DedupeIndex.recordsAfterFinalize (sourceFor (finalizedSession manifest) manifest block reclaimableMetadata)

        let active = DedupeIndex.recordsAfterFinalize (sourceFor (finalizedSession manifest) manifest block activeMetadata)

        Assert.That(nonFinalized, Is.Empty, "Upload-session block inventory must not be indexed before finalization.")
        Assert.That(inactive, Is.Empty, "Reclaimable metadata ranges are not authoritative reusable candidates.")
        Assert.That(active, Is.Not.Empty, "Finalized manifest plus active metadata should publish rebuildable candidates.")

        let result = discover active [| requestedChunk |]
        Assert.That(result.CandidateContentBlocks, Has.Length.EqualTo(1))

        let candidate = result.CandidateContentBlocks[0]
        Assert.That(candidate.ContentBlockAddress, Is.EqualTo(block.Address))
        Assert.That(candidate.ProtectedChunkAddresses, Is.Not.Empty)
        Assert.That(candidate.ProtectedChunkAddresses, Has.None.EqualTo(requestedChunk))

        for rawChunkAddress in decodedChunkAddresses block do
            Assert.That(candidate.ProtectedChunkAddresses, Has.None.EqualTo(rawChunkAddress))

    /// Verifies that finalized Sdk Granularity One Chunk Blocks Publish Cross Repository Candidates.
    [<Test>]
    member _.FinalizedSdkGranularityOneChunkBlocksPublishCrossRepositoryCandidates() =
        let repoA = defaultRepository (Guid.Parse("9a6b2b42-22de-4763-9d2c-f7187128bafe"))
        let repoB = defaultRepository (Guid.Parse("40901fa3-3e69-4028-bf1d-2763d53a633e"))
        let block = encodedBlock "sdk-one-chunk" 1
        let manifest = manifestFor block

        let metadata =
            { metadataFor 1 12L block with
                StoragePoolId = StoragePoolRouting.defaultStoragePoolId
                Ranges =
                    [|
                        { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 1; PhysicalOffset = 0L; PhysicalLength = block.Payload.LongLength }
                    |]
            }

        let records =
            DedupeIndex.recordsAfterFinalize
                {
                    StoragePoolId = StoragePoolRouting.defaultStoragePoolId
                    Session = { finalizedSession manifest with RepositoryId = repoA.RepositoryId; StoragePoolId = StoragePoolRouting.defaultStoragePoolId }
                    Manifest = manifest
                    BlockPayloads = [| payloadFor block |]
                    Metadata = [| metadata |]
                }

        let requestedChunk = (decodedChunkAddresses block)[0]
        let repoBResult = DedupeIndex.discover (DedupeIndex.storagePoolIdForRepository repoB) [| requestedChunk |] timestamp records

        let candidate =
            repoBResult.CandidateContentBlocks
            |> Array.exactlyOne

        Assert.That(candidate.StoragePoolId, Is.EqualTo(StoragePoolRouting.defaultStoragePoolId))
        Assert.That(candidate.ManifestAddress, Is.EqualTo(manifest.ManifestAddress))
        Assert.That(candidate.ContentBlockAddress, Is.EqualTo(block.Address))
        Assert.That(candidate.OrdinalStart, Is.EqualTo(0))
        Assert.That(candidate.OrdinalCount, Is.EqualTo(1))
        Assert.That(candidate.MetadataVersion, Is.EqualTo(metadata.MetadataVersion))
        Assert.That(candidate.ProtectedChunkAddresses, Has.Length.EqualTo(1))
        Assert.That(candidate.ProtectedChunkAddresses[0], Is.EqualTo(protectedChunkAddress StoragePoolRouting.defaultStoragePoolId requestedChunk))
        Assert.That(candidate.ProtectedChunkAddresses, Has.None.EqualTo(requestedChunk))
        Assert.That(repoBResult.Policy.MinimumAcceptedReuseRunLength, Is.EqualTo(1))

        Assert.That(
            repoBResult.Policy.MinimumAcceptedReuseRunLength,
            Is.LessThan(MinimumAcceptedReuseRunLength),
            "Whole-block one-chunk candidates must be issued with a claimable minimum even though larger-block fragments still use the guarded threshold."
        )

        let hint = DedupeIndex.toReuseRangeHint candidate

        let discovery =
            UploadSessionCommand.IssueDedupeDiscovery
                {
                    OperationId = "op-one-chunk-discovery"
                    ExpiresAt = timestamp.Plus(Duration.FromMinutes(5L))
                    MinimumReuseRunLength = repoBResult.Policy.MinimumAcceptedReuseRunLength
                    Hints = [| hint |]
                }

        let started =
            { UploadSessionDto.Default with
                UploadSessionId = Guid.NewGuid()
                RepositoryId = repoB.RepositoryId
                StoragePoolId = StoragePoolRouting.defaultStoragePoolId
                LifecycleState = UploadSessionLifecycleState.Started
            }

        let eventMetadata =
            {
                Timestamp = timestamp
                CorrelationId = "corr-one-chunk-claimable"
                Principal = "tester"
                ClientType = Microsoft.FSharp.Core.Option.None
                Properties = Dictionary<string, string>()
            }

        let issuedSession, discoveryEvents =
            match Grace.Actors.UploadSession.decideCommand [] started discovery eventMetadata with
            | Ok decision -> decision.Session, decision.Events
            | Error error ->
                Assert.Fail($"Expected one-chunk discovery issue to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let claim =
            UploadSessionCommand.ClaimReuseRanges
                {
                    OperationId = "op-one-chunk-claim"
                    DiscoveryOperationId = "op-one-chunk-discovery"
                    Ranges =
                        [|
                            { Hint = hint; Metadata = metadata }
                        |]
                }

        match Grace.Actors.UploadSession.decideCommand discoveryEvents issuedSession claim eventMetadata with
        | Ok decision ->
            Assert.That(decision.Session.ClaimedReuseRanges, Has.Length.EqualTo(1))

            Assert.That(
                decision.Session.ClaimedReuseRanges[0]
                    .ContentBlockAddress,
                Is.EqualTo(block.Address)
            )

            Assert.That(
                decision.Session.ClaimedReuseRanges[0]
                    .OrdinalCount,
                Is.EqualTo(1)
            )

            Assert.That(
                decision.Session.ClaimedReuseRanges[0]
                    .MetadataVersion,
                Is.EqualTo(metadata.MetadataVersion)
            )
        | Error error -> Assert.Fail($"Expected whole one-chunk reuse range claim to succeed, got {error.Error}.")

    /// Verifies that one Chunk Fragments Inside Larger Blocks Stay Below The Accepted Reuse Run Length.
    [<Test>]
    member _.OneChunkFragmentsInsideLargerBlocksStayBelowTheAcceptedReuseRunLength() =
        let block = encodedBlock "larger-block-short-fragment" 12
        let manifest = manifestFor block

        let fragmentMetadata =
            { metadataFor 1 13L block with
                Ranges =
                    [|
                        { OrdinalStart = 0; OrdinalCount = 1; ActiveManifestCount = 1; PhysicalOffset = 0L; PhysicalLength = int64 block.Chunks[0].Length }
                    |]
            }

        let records = DedupeIndex.recordsAfterFinalize (sourceFor (finalizedSession manifest) manifest block fragmentMetadata)

        Assert.That(records, Is.Empty, "A one-chunk fragment inside a larger ContentBlock must not bypass the minimum reuse-run guard.")

    /// Verifies that protected Chunk Addresses Use Stable Preimage Separator.
    [<Test>]
    member _.ProtectedChunkAddressesUseStablePreimageSeparator() =
        let block = encodedBlock "stable-protection" 12
        let manifest = manifestFor block
        let metadata = metadataFor 1 8L block
        let records = DedupeIndex.recordsAfterFinalize (sourceFor (finalizedSession manifest) manifest block metadata)
        let expectedProtectedAddress = protectedChunkAddress storagePoolId (decodedChunkAddresses block).[0]

        Assert.That(records, Is.Not.Empty)
        Assert.That(records[0].ProtectedChunkAddresses[0], Is.EqualTo(expectedProtectedAddress))

    /// Verifies that default Storage Pool Exposes Cross Repository Metadata After Physical Placement Is Shard Aware.
    [<Test>]
    member _.DefaultStoragePoolExposesCrossRepositoryMetadataAfterPhysicalPlacementIsShardAware() =
        let repoA = defaultRepository (Guid.Parse("507ccba8-8026-426c-ab65-c36d44625f1f"))
        let repoB = defaultRepository (Guid.Parse("9ab312e7-d73f-48ca-8f44-07e6e4954a89"))
        let repoAStoragePoolId = DedupeIndex.storagePoolIdForRepository repoA
        let repoBStoragePoolId = DedupeIndex.storagePoolIdForRepository repoB
        let block = encodedBlock "cross-repository-default-pool" 12
        let manifest = manifestFor block
        let repoAMetadata = { metadataFor 1 9L block with StoragePoolId = repoAStoragePoolId }

        let repoARecords =
            DedupeIndex.recordsAfterFinalize
                {
                    StoragePoolId = repoAStoragePoolId
                    Session = { finalizedSession manifest with RepositoryId = repoA.RepositoryId }
                    Manifest = manifest
                    BlockPayloads = [| payloadFor block |]
                    Metadata = [| repoAMetadata |]
                }

        let requestedChunk = (decodedChunkAddresses block)[0]
        let repoAResult = DedupeIndex.discover repoAStoragePoolId [| requestedChunk |] timestamp repoARecords
        let repoBResult = DedupeIndex.discover repoBStoragePoolId [| requestedChunk |] timestamp repoARecords

        Assert.That(repoA.StoragePoolId, Is.EqualTo(repoB.StoragePoolId))
        Assert.That(repoAStoragePoolId, Is.EqualTo(repoBStoragePoolId))
        Assert.That(repoAResult.CandidateContentBlocks, Has.Length.EqualTo(1))

        Assert.That(
            repoBResult.CandidateContentBlocks,
            Has.Length.EqualTo(1),
            "Repo B should discover repo A metadata when both repositories use the same StoragePool and physical CAS placement is shard-aware."
        )

        Assert.That(
            repoBResult.CandidateContentBlocks[0]
                .StoragePoolId,
            Is.EqualTo(StoragePoolRouting.defaultStoragePoolId)
        )

        Assert.That(
            repoBResult.CandidateContentBlocks[0]
                .ManifestAddress,
            Is.EqualTo(manifest.ManifestAddress)
        )

        Assert.That(
            repoBResult.CandidateContentBlocks[0]
                .ContentBlockAddress,
            Is.EqualTo(block.Address)
        )

    /// Verifies that finalized Manifest Registrations Are Scoped By Repository Before Shared Pool Collapse.
    [<Test>]
    member _.FinalizedManifestRegistrationsAreScopedByRepositoryBeforeSharedPoolCollapse() =
        let uploadSessionId = Guid.Parse("7121d3c7-61dc-4a47-a433-62d5a42288a7")
        let scope = "/shared/path.bin"
        let block = encodedBlock "shared-finalized-manifest-scope" 12
        let manifest = manifestFor block
        let metadata = metadataFor 1 51L block
        let repoA = Guid.Parse("ff4b53c2-bac9-4d8b-b31b-5f7f2d82d6d8")
        let repoB = Guid.Parse("568ee3cc-5c49-4446-80c7-910875ea1a03")

        /// Builds session For test data for the server unit dedupe Index scenarios in this file.
        let sessionFor repositoryId =
            { finalizedSession manifest with
                UploadSessionId = uploadSessionId
                RepositoryId = repositoryId
                AuthorizedScope = scope
                StoragePoolId = StoragePoolRouting.defaultStoragePoolId
            }

        /// Builds registration For test data for the server unit dedupe Index scenarios in this file.
        let registrationFor repositoryId : DedupeIndex.FinalizedManifestRegistration =
            {
                StoragePoolId = StoragePoolRouting.defaultStoragePoolId
                Session = sessionFor repositoryId
                Manifest = manifest
                BlockPayloads = [| payloadFor block |]
            }

        let stateAfterRepoA, _ = DedupeIndex.registerFinalizedManifestInState DedupeIndex.DedupeIndexState.Empty (registrationFor repoA)

        let stateAfterRepoB, _ = DedupeIndex.registerFinalizedManifestInState stateAfterRepoA (registrationFor repoB)
        let stateAfterMetadata, _ = DedupeIndex.writeAfterAuthoritativeMetadataInState stateAfterRepoB metadata

        Assert.That(stateAfterMetadata.FinalizedManifests, Has.Length.EqualTo(2))

        Assert.That(
            DedupeIndex.finalizedScopedManifestContainsBlock
                StoragePoolRouting.defaultStoragePoolId
                repoA
                scope
                manifest.ManifestAddress
                block.Address
                stateAfterMetadata,
            Is.True
        )

        Assert.That(
            DedupeIndex.finalizedScopedManifestContainsBlock
                StoragePoolRouting.defaultStoragePoolId
                repoB
                scope
                manifest.ManifestAddress
                block.Address
                stateAfterMetadata,
            Is.True
        )

    /// Verifies that shared Pool Metadata Refresh Rewrites All Matching Manifest Registrations.
    [<Test>]
    member _.SharedPoolMetadataRefreshRewritesAllMatchingManifestRegistrations() =
        let block = encodedBlock "shared-refresh-all-manifests" 12
        let firstManifest = { manifestFor block with ManifestAddress = ManifestAddress "manifest-shared-refresh-first" }

        let secondManifest = { manifestFor block with ManifestAddress = ManifestAddress "manifest-shared-refresh-second" }

        let metadata = { metadataFor 2 61L block with StoragePoolId = StoragePoolRouting.defaultStoragePoolId }

        /// Builds registration For test data for the server unit dedupe Index scenarios in this file.
        let registrationFor manifest : DedupeIndex.FinalizedManifestRegistration =
            let session = { finalizedSession manifest with StoragePoolId = StoragePoolRouting.defaultStoragePoolId }

            { StoragePoolId = StoragePoolRouting.defaultStoragePoolId; Session = session; Manifest = manifest; BlockPayloads = [| payloadFor block |] }

        let stateAfterFirst, _ = DedupeIndex.registerFinalizedManifestInState DedupeIndex.DedupeIndexState.Empty (registrationFor firstManifest)
        let stateAfterSecond, _ = DedupeIndex.registerFinalizedManifestInState stateAfterFirst (registrationFor secondManifest)
        let stateAfterMetadata, newRecords = DedupeIndex.writeAfterAuthoritativeMetadataInState stateAfterSecond metadata

        let refreshedManifestAddresses =
            stateAfterMetadata.Records
            |> Array.filter (fun record ->
                record.StoragePoolId = StoragePoolRouting.defaultStoragePoolId
                && record.ContentBlockAddress = block.Address
                && record.MetadataVersion = metadata.MetadataVersion)
            |> Array.map (fun record -> record.ManifestAddress)
            |> Set.ofArray

        Assert.That(newRecords, Has.Length.GreaterThanOrEqualTo(2))
        Assert.That(refreshedManifestAddresses.Contains firstManifest.ManifestAddress, Is.True)
        Assert.That(refreshedManifestAddresses.Contains secondManifest.ManifestAddress, Is.True)

    /// Verifies that coalesced Active Ranges Publish Bounded Windows Beyond First Max Window.
    [<Test>]
    member _.CoalescedActiveRangesPublishBoundedWindowsBeyondFirstMaxWindow() =
        let block =
            encodedBlock
                "bounded-windows"
                (MaxWindowChunks
                 + MinimumAcceptedReuseRunLength
                 + 4)

        let manifest = manifestFor block
        let metadata = metadataFor 1 52L block
        let records = DedupeIndex.recordsAfterFinalize (sourceFor (finalizedSession manifest) manifest block metadata)

        let laterWindow =
            records
            |> Array.tryFind (fun record -> record.OrdinalStart = MaxWindowChunks)

        Assert.That(
            records
            |> Array.exists (fun record -> record.OrdinalStart = 0),
            Is.True
        )

        Assert.That(laterWindow.IsSome, Is.True, "Long coalesced ranges must expose later bounded windows.")

        let laterChunk = (decodedChunkAddresses block)[MaxWindowChunks]
        let result = discover records [| laterChunk |]

        Assert.That(result.CandidateContentBlocks, Has.Length.EqualTo(1))
        Assert.That(result.CandidateContentBlocks[0].OrdinalStart, Is.EqualTo(MaxWindowChunks))

    /// Verifies that coalesced Active Ranges Publish Overlapping Tail Window When Remainder Is Short.
    [<Test>]
    member _.CoalescedActiveRangesPublishOverlappingTailWindowWhenRemainderIsShort() =
        let block = encodedBlock "short-tail-window" (MaxWindowChunks + 4)
        let manifest = manifestFor block
        let metadata = metadataFor 1 53L block
        let records = DedupeIndex.recordsAfterFinalize (sourceFor (finalizedSession manifest) manifest block metadata)

        let tailStart = 4

        Assert.That(
            records
            |> Array.exists (fun record -> record.OrdinalStart = 0),
            Is.True
        )

        Assert.That(
            records
            |> Array.exists (fun record ->
                record.OrdinalStart = tailStart
                && record.OrdinalCount = MaxWindowChunks),
            Is.True,
            "A short tail after a full dedupe window still needs an overlapping record so tail chunks remain discoverable."
        )

        let tailChunk = (decodedChunkAddresses block)[MaxWindowChunks + 3]
        let result = discover records [| tailChunk |]

        Assert.That(result.CandidateContentBlocks, Has.Length.EqualTo(1))
        Assert.That(result.CandidateContentBlocks[0].OrdinalStart, Is.EqualTo(tailStart))

    /// Verifies that contiguous Per Chunk Active Ranges Publish Only Maximal Chain Windows.
    [<Test>]
    member _.ContiguousPerChunkActiveRangesPublishOnlyMaximalChainWindows() =
        let block = encodedBlock "per-chunk-maximal-chain" (MinimumAcceptedReuseRunLength + 4)
        let manifest = manifestFor block

        let ranges =
            block.Chunks
            |> Array.mapi (fun index chunk ->
                { OrdinalStart = index; OrdinalCount = 1; ActiveManifestCount = 1; PhysicalOffset = chunk.PhysicalOffset; PhysicalLength = int64 chunk.Length })

        let metadata =
            { metadataFor 1 55L block with Ranges = ranges; TotalPhysicalBytes = block.Payload.LongLength; ActivePhysicalBytes = block.Payload.LongLength }

        let records = DedupeIndex.recordsAfterFinalize (sourceFor (finalizedSession manifest) manifest block metadata)

        Assert.That(records, Has.Length.EqualTo(1))
        Assert.That(records[0].OrdinalStart, Is.EqualTo(0))
        Assert.That(records[0].OrdinalCount, Is.EqualTo(block.Chunks.Length))

        let suffixChunk = (decodedChunkAddresses block)[MinimumAcceptedReuseRunLength]
        let result = discover records [| suffixChunk |]

        Assert.That(result.CandidateContentBlocks, Has.Length.EqualTo(1))
        Assert.That(result.CandidateContentBlocks[0].OrdinalStart, Is.EqualTo(0))

    /// Verifies that coalesced Active Ranges Backtracks Around Duplicate Physical Copies.
    [<Test>]
    member _.CoalescedActiveRangesBacktracksAroundDuplicatePhysicalCopies() =
        let block = encodedBlock "alternate-chain" 12
        let manifest = manifestFor block

        let metadata =
            { metadataFor 1 54L block with
                Ranges =
                    [|
                        { OrdinalStart = 0; OrdinalCount = 4; ActiveManifestCount = 1; PhysicalOffset = 0L; PhysicalLength = 400L }
                        { OrdinalStart = 0; OrdinalCount = 4; ActiveManifestCount = 1; PhysicalOffset = 1024L; PhysicalLength = 400L }
                        { OrdinalStart = 4; OrdinalCount = 4; ActiveManifestCount = 1; PhysicalOffset = 400L; PhysicalLength = 400L }
                    |]
                TotalPhysicalBytes = 1200L
                ActivePhysicalBytes = 1200L
            }

        let records = DedupeIndex.recordsAfterFinalize (sourceFor (finalizedSession manifest) manifest block metadata)
        let requestedChunk = (decodedChunkAddresses block)[4]
        let result = discover records [| requestedChunk |]

        Assert.That(
            records
            |> Array.exists (fun record ->
                record.OrdinalStart = 0
                && record.OrdinalCount = MinimumAcceptedReuseRunLength),
            Is.True,
            "The duplicate physical copy at the same logical start must not interrupt the active split chain."
        )

        Assert.That(result.CandidateContentBlocks, Has.Length.EqualTo(1))
        Assert.That(result.CandidateContentBlocks[0].OrdinalCount, Is.EqualTo(MinimumAcceptedReuseRunLength))

    /// Verifies that discovery Limits Candidate Windows For A Key Chunk.
    [<Test>]
    member _.DiscoveryLimitsCandidateWindowsForAKeyChunk() =
        let firstBlock = encodedBlock "limit-shared" 12
        let sharedChunkBytes = firstBlock.Chunks[0].Bytes

        /// Constructs block fixtures used by the server unit dedupe Index assertions.
        let createBlock index =
            let tail = bytes $"limit-tail-{index}-{String('y', 128)}"

            let chunks: ContentBlockFormat.ContentBlockInputChunk array =
                [|
                    let firstChunk: ContentBlockFormat.ContentBlockInputChunk = { PhysicalOffset = 0L; Bytes = sharedChunkBytes }
                    firstChunk

                    for ordinal in 1..11 do
                        let chunk: ContentBlockFormat.ContentBlockInputChunk =
                            {
                                PhysicalOffset =
                                    int64 (
                                        sharedChunkBytes.Length
                                        + (ordinal - 1) * tail.Length
                                    )
                                Bytes = bytes $"limit-{index}-{ordinal}-{String('z', 128)}"
                            }

                        chunk
                |]

            match ContentBlockFormat.encode chunks with
            | Ok block -> block
            | Error error ->
                Assert.Fail($"Expected limit block to encode, got {error}.")
                Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

        let records =
            [|
                for index in 0 .. MaxCandidateWindowsPerKeyChunk + 2 do
                    let block = createBlock index
                    let manifest = manifestFor block
                    DedupeIndex.recordsAfterFinalize (sourceFor (finalizedSession manifest) manifest block (metadataFor 1 (int64 index + 1L) block))
            |]
            |> Array.concat

        let requestedChunk = (decodedChunkAddresses firstBlock)[0]
        let result = discover records [| requestedChunk |]

        Assert.That(result.CandidateContentBlocks.Length, Is.EqualTo(MaxCandidateWindowsPerKeyChunk))
        Assert.That(result.IsPartial, Is.True)

    /// Verifies that discovery Deduplicates And Caps Key Chunks Without Making Empty Results Authoritative.
    [<Test>]
    member _.DiscoveryDeduplicatesAndCapsKeyChunksWithoutMakingEmptyResultsAuthoritative() =
        let duplicateBlock = encodedBlock "duplicate-requested" 12
        let matchingChunk = (decodedChunkAddresses duplicateBlock)[0]

        let requested =
            [|
                yield matchingChunk
                yield matchingChunk
                yield String.Empty
                for index in 0 .. MaxDiscoveryKeyChunkAddresses + 10 do
                    yield $"missing-key-{index:D3}"
            |]

        let result = discover Array.empty requested

        Assert.That(result.RequestedKeyChunkCount, Is.EqualTo(requested.Length))
        Assert.That(result.AcceptedKeyChunkCount, Is.EqualTo(MaxDiscoveryKeyChunkAddresses))
        Assert.That(result.CandidateContentBlocks, Is.Empty)
        Assert.That(result.Policy.EmptyResponseMeansAbsent, Is.False)
        Assert.That(result.Policy.IsAuthoritative, Is.False)
        Assert.That(result.IsPartial, Is.True)
        Assert.That(result.Message, Does.Contain("non-authoritative"))

    /// Verifies that discovery Response Budget Truncates Protected Windows And Marks Partial.
    [<Test>]
    member _.DiscoveryResponseBudgetTruncatesProtectedWindowsAndMarksPartial() =
        let blocks =
            [|
                for index in
                    0 .. MaxResponseProtectedChunks
                         / MinimumAcceptedReuseRunLength
                         + 8 do
                    encodedBlock $"budget-{index:D4}" MinimumAcceptedReuseRunLength
            |]

        let records =
            blocks
            |> Array.mapi (fun index block ->
                let manifest = manifestFor block
                let metadata = metadataFor 1 (int64 index + 1L) block
                DedupeIndex.recordsAfterFinalize (sourceFor (finalizedSession manifest) manifest block metadata))
            |> Array.concat

        let requested =
            blocks
            |> Array.map (fun block -> (decodedChunkAddresses block)[0])

        let result = discover records requested

        let protectedChunkCount =
            result.CandidateContentBlocks
            |> Array.sumBy (fun candidate -> candidate.ProtectedChunkAddresses.Length)

        Assert.That(result.CandidateContentBlocks, Is.Not.Empty)
        Assert.That(protectedChunkCount, Is.LessThanOrEqualTo(MaxResponseProtectedChunks))
        Assert.That(result.CandidateContentBlocks.Length, Is.LessThan(records.Length))
        Assert.That(result.IsPartial, Is.True)

        for candidate in result.CandidateContentBlocks do
            for rawChunkAddress in requested do
                Assert.That(candidate.ProtectedChunkAddresses, Has.None.EqualTo(rawChunkAddress))

    /// Verifies that stale Index Candidates Still Require Authoritative Range Claims.
    [<Test>]
    member _.StaleIndexCandidatesStillRequireAuthoritativeRangeClaims() =
        let block = encodedBlock "stale" 12
        let manifest = manifestFor block
        let records = DedupeIndex.recordsAfterFinalize (sourceFor (finalizedSession manifest) manifest block (metadataFor 1 7L block))
        let result = discover records [| (decodedChunkAddresses block)[0] |]
        let candidate = result.CandidateContentBlocks |> Array.exactlyOne

        let hint = DedupeIndex.toReuseRangeHint candidate

        let authoritativeMetadata =
            { metadataFor 1 8L block with
                Ranges =
                    [|
                        {
                            OrdinalStart = candidate.OrdinalStart
                            OrdinalCount = candidate.OrdinalCount
                            ActiveManifestCount = 1
                            PhysicalOffset = 0L
                            PhysicalLength = block.Payload.LongLength
                        }
                    |]
            }

        let discovery =
            UploadSessionCommand.IssueDedupeDiscovery
                {
                    OperationId = "op-discovery"
                    ExpiresAt = timestamp.Plus(Duration.FromMinutes(5L))
                    MinimumReuseRunLength = MinimumAcceptedReuseRunLength
                    Hints = [| hint |]
                }

        let started =
            { UploadSessionDto.Default with
                UploadSessionId = Guid.NewGuid()
                RepositoryId = repositoryId
                LifecycleState = UploadSessionLifecycleState.Started
            }

        let eventMetadata =
            {
                Timestamp = timestamp
                CorrelationId = "corr-stale-candidate"
                Principal = "tester"
                ClientType = Microsoft.FSharp.Core.Option.None
                Properties = Dictionary<string, string>()
            }

        let issued =
            match Grace.Actors.UploadSession.decideCommand [] started discovery eventMetadata with
            | Ok decision -> decision.Session, decision.Events
            | Error error ->
                Assert.Fail($"Expected discovery issue to succeed, got {error.Error}.")
                UploadSessionDto.Default, []

        let discoveredSession, discoveryEvents = issued

        let claim =
            UploadSessionCommand.ClaimReuseRanges
                {
                    OperationId = "op-claim"
                    DiscoveryOperationId = "op-discovery"
                    Ranges =
                        [|
                            { Hint = hint; Metadata = authoritativeMetadata }
                        |]
                }

        match Grace.Actors.UploadSession.decideCommand discoveryEvents discoveredSession claim eventMetadata with
        | Ok _ -> Assert.Fail("Expected stale index candidate to be rejected by authoritative metadata at claim time.")
        | Error error -> Assert.That(error.Error, Does.Contain("stale"))

    /// Verifies that rebuild From Finalized Manifests And Metadata Reproduces Candidates.
    [<Test>]
    member _.RebuildFromFinalizedManifestsAndMetadataReproducesCandidates() =
        let block = encodedBlock "rebuild" 12
        let manifest = manifestFor block
        let metadata = metadataFor 2 9L block
        let source = sourceFor (finalizedSession manifest) manifest block metadata

        let incremental = DedupeIndex.recordsAfterFinalize source
        let rebuilt = DedupeIndex.rebuild [| source |]
        let requested = [| (decodedChunkAddresses block)[0] |]

        let incrementalCandidates =
            (discover incremental requested)
                .CandidateContentBlocks
            |> Array.map candidateShape

        let rebuiltCandidates =
            (discover rebuilt requested)
                .CandidateContentBlocks
            |> Array.map candidateShape

        Assert.That(rebuiltCandidates.Length, Is.EqualTo(incrementalCandidates.Length))

        for index in 0 .. rebuiltCandidates.Length - 1 do
            Assert.That(rebuiltCandidates[index], Is.EqualTo(incrementalCandidates[index]))

    /// Verifies that rebuild Deduplicates Candidate Windows With Newest Metadata Version.
    [<Test>]
    member _.RebuildDeduplicatesCandidateWindowsWithNewestMetadataVersion() =
        let block = encodedBlock "rebuild-newest" 12
        let manifest = manifestFor block
        let olderMetadata = metadataFor 1 10L block
        let newerMetadata = metadataFor 1 11L block

        let rebuilt =
            DedupeIndex.rebuild [| sourceFor (finalizedSession manifest) manifest block olderMetadata
                                   sourceFor (finalizedSession manifest) manifest block newerMetadata |]

        let matchingRecords =
            rebuilt
            |> Array.filter (fun record ->
                record.ManifestAddress = manifest.ManifestAddress
                && record.ContentBlockAddress = block.Address
                && record.OrdinalStart = 0)

        Assert.That(matchingRecords, Has.Length.EqualTo(1))
        Assert.That(matchingRecords[0].MetadataVersion, Is.EqualTo(newerMetadata.MetadataVersion))

    /// Verifies that finalize Registration Publishes When Authoritative Metadata Arrives.
    [<Test>]
    member _.FinalizeRegistrationPublishesWhenAuthoritativeMetadataArrives() =
        let block = encodedBlock "actor-publish" 12
        let manifest = manifestFor block
        let metadata = metadataFor 1 17L block

        let registration: DedupeIndex.FinalizedManifestRegistration =
            { StoragePoolId = storagePoolId; Session = finalizedSession manifest; Manifest = manifest; BlockPayloads = [| payloadFor block |] }

        let beforeMetadata = DedupeIndex.registerFinalizedManifest registration
        let afterMetadata = DedupeIndex.writeAfterAuthoritativeMetadata metadata
        let result = DedupeIndex.discover storagePoolId [| (decodedChunkAddresses block)[0] |] timestamp (DedupeIndex.snapshot ())

        Assert.That(beforeMetadata, Is.Empty, "Finalization alone must not expose candidates before authoritative metadata is available.")
        Assert.That(afterMetadata, Is.Not.Empty, "Authoritative metadata should publish candidates for an already-finalized manifest.")
        Assert.That(result.CandidateContentBlocks, Has.Length.GreaterThanOrEqualTo(1))
        Assert.That(result.CandidateContentBlocks[0].ManifestAddress, Is.EqualTo(manifest.ManifestAddress))

    /// Verifies that finalize Registration Ignores Authoritative Metadata From Different Storage Pool.
    [<Test>]
    member _.FinalizeRegistrationIgnoresAuthoritativeMetadataFromDifferentStoragePool() =
        let block = encodedBlock "cross-pool" 12
        let manifest = manifestFor block
        let foreignStoragePoolId = StoragePoolId "pool-foreign"
        let localMetadata = metadataFor 1 18L block
        let foreignMetadata = { localMetadata with StoragePoolId = foreignStoragePoolId; MetadataVersion = 19L }

        let registration: DedupeIndex.FinalizedManifestRegistration =
            { StoragePoolId = storagePoolId; Session = finalizedSession manifest; Manifest = manifest; BlockPayloads = [| payloadFor block |] }

        let beforeMetadata = DedupeIndex.registerFinalizedManifest registration
        let afterForeignMetadata = DedupeIndex.writeAfterAuthoritativeMetadata foreignMetadata
        let afterLocalMetadata = DedupeIndex.writeAfterAuthoritativeMetadata localMetadata

        let result = DedupeIndex.discover storagePoolId [| (decodedChunkAddresses block)[0] |] timestamp (DedupeIndex.snapshot ())

        Assert.That(beforeMetadata, Is.Empty)
        Assert.That(afterForeignMetadata, Is.Empty, "Metadata from another storage pool must not publish this registration.")
        Assert.That(afterLocalMetadata, Is.Not.Empty, "The original registration must remain publishable by matching storage-pool metadata.")
        Assert.That(result.CandidateContentBlocks, Has.Length.GreaterThanOrEqualTo(1))
        Assert.That(result.CandidateContentBlocks[0].StoragePoolId, Is.EqualTo(storagePoolId))
        Assert.That(result.CandidateContentBlocks[0].MetadataVersion, Is.EqualTo(localMetadata.MetadataVersion))

    /// Verifies that identical Content Blocks Remain Reusable Only Inside The Same Storage Pool.
    [<Test>]
    member _.IdenticalContentBlocksRemainReusableOnlyInsideTheSameStoragePool() =
        let block = encodedBlock "identical-cross-pool" 12
        let poolA = StoragePoolId "pool-a"
        let poolB = StoragePoolId "pool-b"

        /// Builds manifest For Pool test data for the server unit dedupe Index scenarios in this file.
        let manifestForPool storagePoolId =
            let size =
                block.Chunks
                |> Array.sumBy (fun chunk -> int64 chunk.Length)

            let manifest =
                FileManifest.Create(
                    ManifestAddress String.Empty,
                    RabinChunking.SuiteName,
                    FileContentHash(ContentAddress.computeBlake3Hex block.Payload),
                    size,
                    storagePoolId,
                    [
                        ContentBlock.Create(block.Address, 0L, size)
                    ]
                )

            { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

        /// Builds session For test data for the server unit dedupe Index scenarios in this file.
        let sessionFor storagePoolId manifest =
            { finalizedSession manifest with StoragePoolId = storagePoolId; FinalizedManifestAddress = Some manifest.ManifestAddress }

        /// Builds metadata For Pool test data for the server unit dedupe Index scenarios in this file.
        let metadataForPool storagePoolId metadataVersion =
            { metadataFor 1 metadataVersion block with
                StoragePoolId = storagePoolId
                StoragePlacement =
                    {
                        StorageAccountName = $"cas-{storagePoolId}"
                        StorageContainerName = StorageContainerName $"cas-{storagePoolId}"
                        ObjectKey = $"{storagePoolId}/{StorageKeys.contentBlockObjectKey block.Address}"
                        ETag = Some $"etag-{metadataVersion}"
                    }
            }

        /// Builds registration For test data for the server unit dedupe Index scenarios in this file.
        let registrationFor storagePoolId manifest : DedupeIndex.FinalizedManifestRegistration =
            { StoragePoolId = storagePoolId; Session = sessionFor storagePoolId manifest; Manifest = manifest; BlockPayloads = [| payloadFor block |] }

        let manifestA = manifestForPool poolA
        let manifestB = manifestForPool poolB

        let stateAfterPoolARegistration, _ = DedupeIndex.registerFinalizedManifestInState DedupeIndex.DedupeIndexState.Empty (registrationFor poolA manifestA)

        let stateAfterPoolAMetadata, _ = DedupeIndex.writeAfterAuthoritativeMetadataInState stateAfterPoolARegistration (metadataForPool poolA 31L)

        let stateAfterPoolBRegistration, _ = DedupeIndex.registerFinalizedManifestInState stateAfterPoolAMetadata (registrationFor poolB manifestB)
        let stateAfterBothPools, _ = DedupeIndex.writeAfterAuthoritativeMetadataInState stateAfterPoolBRegistration (metadataForPool poolB 41L)

        let requestedChunk = (decodedChunkAddresses block)[0]
        let poolAResult = DedupeIndex.discover poolA [| requestedChunk |] timestamp stateAfterBothPools.Records
        let poolBResult = DedupeIndex.discover poolB [| requestedChunk |] timestamp stateAfterBothPools.Records
        let defaultPoolResult = DedupeIndex.discover storagePoolId [| requestedChunk |] timestamp stateAfterBothPools.Records

        let poolACandidate =
            poolAResult.CandidateContentBlocks
            |> Array.exactlyOne

        let poolBCandidate =
            poolBResult.CandidateContentBlocks
            |> Array.exactlyOne

        Assert.That(defaultPoolResult.CandidateContentBlocks, Is.Empty)
        Assert.That(poolACandidate.StoragePoolId, Is.EqualTo(poolA))
        Assert.That(poolACandidate.ManifestAddress, Is.EqualTo(manifestA.ManifestAddress))
        Assert.That(poolACandidate.ContentBlockAddress, Is.EqualTo(block.Address))
        Assert.That(poolBCandidate.StoragePoolId, Is.EqualTo(poolB))
        Assert.That(poolBCandidate.ManifestAddress, Is.EqualTo(manifestB.ManifestAddress))
        Assert.That(poolBCandidate.ContentBlockAddress, Is.EqualTo(block.Address))
        Assert.That(poolACandidate.ProtectedChunkAddresses[0], Is.Not.EqualTo(poolBCandidate.ProtectedChunkAddresses[0]))

    /// Verifies that newer Non Authoritative Metadata Evicts Older Candidate Windows.
    [<Test>]
    member _.NewerNonAuthoritativeMetadataEvictsOlderCandidateWindows() =
        let block = encodedBlock "metadata-evict" 12
        let manifest = manifestFor block
        let activeMetadata = metadataFor 1 20L block
        let inactiveMetadata = metadataFor 0 21L block

        DedupeIndex.writeAfterFinalize (sourceFor (finalizedSession manifest) manifest block activeMetadata)
        |> ignore

        let beforeInactiveMetadata = DedupeIndex.discover storagePoolId [| (decodedChunkAddresses block)[0] |] timestamp (DedupeIndex.snapshot ())

        let afterInactiveMetadata = DedupeIndex.writeAfterAuthoritativeMetadata inactiveMetadata

        let afterInactiveMetadataResult = DedupeIndex.discover storagePoolId [| (decodedChunkAddresses block)[0] |] timestamp (DedupeIndex.snapshot ())

        Assert.That(beforeInactiveMetadata.CandidateContentBlocks, Has.Length.GreaterThanOrEqualTo(1))
        Assert.That(afterInactiveMetadata, Is.Empty, "Inactive metadata should not create replacement candidate windows.")

        Assert.That(
            afterInactiveMetadataResult.CandidateContentBlocks
            |> Array.exists (fun candidate ->
                candidate.ManifestAddress = manifest.ManifestAddress
                && candidate.ContentBlockAddress = block.Address),
            Is.False,
            "Newer metadata with no reusable ranges should evict older windows for that content block."
        )

    /// Verifies that published Registration Rebuilds Candidates For Later Metadata Versions.
    [<Test>]
    member _.PublishedRegistrationRebuildsCandidatesForLaterMetadataVersions() =
        let block = encodedBlock "metadata-rebuild" 12
        let manifest = manifestFor block
        let firstMetadata = metadataFor 1 22L block
        let secondMetadata = metadataFor 1 23L block

        let registration: DedupeIndex.FinalizedManifestRegistration =
            { StoragePoolId = storagePoolId; Session = finalizedSession manifest; Manifest = manifest; BlockPayloads = [| payloadFor block |] }

        DedupeIndex.registerFinalizedManifest registration
        |> ignore

        let firstRecords = DedupeIndex.writeAfterAuthoritativeMetadata firstMetadata
        let secondRecords = DedupeIndex.writeAfterAuthoritativeMetadata secondMetadata

        let result = DedupeIndex.discover storagePoolId [| (decodedChunkAddresses block)[0] |] timestamp (DedupeIndex.snapshot ())

        let matchingSnapshot =
            DedupeIndex.snapshot ()
            |> Array.filter (fun record ->
                record.ManifestAddress = manifest.ManifestAddress
                && record.ContentBlockAddress = block.Address)

        Assert.That(firstRecords, Is.Not.Empty)
        Assert.That(secondRecords, Is.Not.Empty, "Published finalized registrations should rebuild windows for later metadata versions.")
        Assert.That(result.CandidateContentBlocks, Has.Length.GreaterThanOrEqualTo(1))
        Assert.That(result.CandidateContentBlocks[0].MetadataVersion, Is.EqualTo(secondMetadata.MetadataVersion))

        Assert.That(
            matchingSnapshot
            |> Array.exists (fun record -> record.MetadataVersion = firstMetadata.MetadataVersion),
            Is.False
        )

        Assert.That(
            matchingSnapshot
            |> Array.exists (fun record -> record.MetadataVersion = secondMetadata.MetadataVersion),
            Is.True
        )

    /// Verifies that incomplete Replay Registration Does Not Replace Finalized Registration.
    [<Test>]
    member _.IncompleteReplayRegistrationDoesNotReplaceFinalizedRegistration() =
        let block = encodedBlock "replay-preserve" 12
        let manifest = manifestFor block
        let firstMetadata = metadataFor 1 30L block
        let secondMetadata = metadataFor 1 31L block

        let registration: DedupeIndex.FinalizedManifestRegistration =
            { StoragePoolId = storagePoolId; Session = finalizedSession manifest; Manifest = manifest; BlockPayloads = [| payloadFor block |] }

        let stateAfterRegistration, _ = DedupeIndex.registerFinalizedManifestInState DedupeIndex.DedupeIndexState.Empty registration
        let stateAfterFirstMetadata, firstRecords = DedupeIndex.writeAfterAuthoritativeMetadataInState stateAfterRegistration firstMetadata

        let incompleteReplayRegistration = { registration with BlockPayloads = Array.empty }
        let stateAfterIncompleteReplay, replayRecords = DedupeIndex.registerFinalizedManifestInState stateAfterFirstMetadata incompleteReplayRegistration
        let stateAfterSecondMetadata, secondRecords = DedupeIndex.writeAfterAuthoritativeMetadataInState stateAfterIncompleteReplay secondMetadata

        let matchingSnapshot =
            stateAfterSecondMetadata.Records
            |> Array.filter (fun record ->
                record.ManifestAddress = manifest.ManifestAddress
                && record.ContentBlockAddress = block.Address)

        Assert.That(firstRecords, Is.Not.Empty)
        Assert.That(replayRecords, Is.Empty, "Incomplete replay payloads should not replace an existing decoded registration.")
        Assert.That(secondRecords, Is.Not.Empty, "Later metadata must still rebuild from the preserved finalized registration.")

        Assert.That(
            matchingSnapshot
            |> Array.exists (fun record -> record.MetadataVersion = firstMetadata.MetadataVersion),
            Is.False
        )

        Assert.That(
            matchingSnapshot
            |> Array.exists (fun record -> record.MetadataVersion = secondMetadata.MetadataVersion),
            Is.True
        )

    /// Verifies that newer Metadata Versions Replace Older Candidate Windows.
    [<Test>]
    member _.NewerMetadataVersionsReplaceOlderCandidateWindows() =
        let block = encodedBlock "newer-metadata" 12
        let manifest = manifestFor block
        let olderMetadata = metadataFor 1 1L block
        let newerMetadata = metadataFor 1 5L block
        let olderRecords = DedupeIndex.recordsAfterFinalize (sourceFor (finalizedSession manifest) manifest block olderMetadata)
        let newerRecords = DedupeIndex.recordsAfterFinalize (sourceFor (finalizedSession manifest) manifest block newerMetadata)

        DedupeIndex.writeAfterFinalize (sourceFor (finalizedSession manifest) manifest block olderMetadata)
        |> ignore

        DedupeIndex.writeAfterFinalize (sourceFor (finalizedSession manifest) manifest block newerMetadata)
        |> ignore

        let result = discover (Array.append olderRecords newerRecords) [| (decodedChunkAddresses block)[0] |]

        let matchingSnapshot =
            DedupeIndex.snapshot ()
            |> Array.filter (fun record ->
                record.ManifestAddress = manifest.ManifestAddress
                && record.ContentBlockAddress = block.Address)

        Assert.That(result.CandidateContentBlocks[0].MetadataVersion, Is.EqualTo(5L))

        Assert.That(
            matchingSnapshot
            |> Array.exists (fun record -> record.MetadataVersion = 1L),
            Is.False
        )

        Assert.That(
            matchingSnapshot
            |> Array.exists (fun record -> record.MetadataVersion = 5L),
            Is.True
        )
