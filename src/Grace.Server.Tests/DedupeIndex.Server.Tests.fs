namespace Grace.Server.UnitTests

open Grace.Server
open Grace.Shared
open Grace.Shared.Parameters.Storage
open Grace.Types.ContentBlockMetadata
open Grace.Types.Types
open Grace.Types.UploadSession
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Text

[<Parallelizable(ParallelScope.All)>]
type DedupeIndexServerTests() =

    let timestamp = Instant.FromUtc(2026, 5, 24, 14, 0)
    let storagePoolId = StoragePoolId "pool-main"
    let repositoryId = Guid.Parse("f6494929-27ef-4f68-897c-442f5ead4941")

    let bytes (text: string) = Encoding.UTF8.GetBytes(text)

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

    let decodedChunkAddresses (block: ContentBlockFormat.EncodedContentBlock) =
        block.Chunks
        |> Array.map (fun chunk -> chunk.Address)

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

    let finalizedSession manifest =
        { UploadSessionDto.Default with
            RepositoryId = repositoryId
            LifecycleState = UploadSessionLifecycleState.RetentionPending
            FinalizedManifestAddress = Some manifest.ManifestAddress
        }

    let nonFinalizedSession () =
        { UploadSessionDto.Default with
            RepositoryId = repositoryId
            LifecycleState = UploadSessionLifecycleState.UploadingBlocks
            FinalizedManifestAddress = None
        }

    let metadataFor activeManifestCount metadataVersion (block: ContentBlockFormat.EncodedContentBlock) =
        {
            Class = nameof ContentBlockMetadata
            StoragePoolId = storagePoolId
            ContentBlockAddress = block.Address
            BlockFormatVersion = 1s
            StoragePlacement = { ObjectKey = $"cas/content-blocks/{block.Address}"; ETag = Some $"etag-{metadataVersion}" }
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

    let sourceFor session manifest (block: ContentBlockFormat.EncodedContentBlock) metadata : DedupeIndex.FinalizedManifestIndexSource =
        { StoragePoolId = storagePoolId; Session = session; Manifest = manifest; BlockPayloads = [| payloadFor block |]; Metadata = [| metadata |] }

    let discover records requested = DedupeIndex.discover storagePoolId requested timestamp records

    let candidateShape (candidate: ContentBlockDiscoveryCandidate) =
        candidate.ContentBlockAddress, candidate.OrdinalStart, candidate.OrdinalCount, candidate.MetadataVersion, candidate.ProtectedChunkAddresses

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

    [<Test>]
    member _.DiscoveryLimitsCandidateWindowsForAKeyChunk() =
        let firstBlock = encodedBlock "limit-shared" 12
        let sharedChunkBytes = firstBlock.Chunks[0].Bytes

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

        let eventMetadata = { Timestamp = timestamp; CorrelationId = "corr-stale-candidate"; Principal = "tester"; Properties = Dictionary<string, string>() }

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
