namespace Grace.Server.Tests

open Grace.Server
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
open Grace.Types.Repository
open Grace.Types.UploadSession
open NUnit.Framework
open System
open System.IO
open System.Reflection
open System.Text
open System.Threading.Tasks

[<Parallelizable(ParallelScope.All)>]
type StorageContentBlockSdkContract() =

    let getStorageParameterType typeName =
        typeof<Parameters.Storage.StorageParameters>.Assembly.GetType ($"Grace.Shared.Parameters.Storage+{typeName}", throwOnError = false)

    let bytes (value: string) = Encoding.UTF8.GetBytes value

    let expectEncodedOk (result: Result<ContentBlockFormat.EncodedContentBlock, ContentBlockFormat.ContentBlockFormatError>) =
        match result with
        | Ok value -> value
        | Error error ->
            Assert.Fail($"Expected ContentBlockFormat.encode Ok but got {error}.")
            Unchecked.defaultof<ContentBlockFormat.EncodedContentBlock>

    let contentBlockPayload physicalOffset payloadBytes : ContentBlockFormat.EncodedContentBlock =
        ContentBlockFormat.encode [ ContentBlockFormat.createChunk physicalOffset payloadBytes ]
        |> expectEncodedOk

    let buildManifest chunkingSuiteId payloadBytes (blockPayloads: ContentBlockFormat.EncodedContentBlock array) =
        let mutable offset = 0L

        let blocks =
            blockPayloads
            |> Array.map (fun payload ->
                let contentLength =
                    payload.Chunks
                    |> Array.sumBy (fun chunk -> chunk.Length)
                    |> int64

                let block = ContentBlock.Create(payload.Address, offset, contentLength)
                offset <- offset + contentLength
                block)
            |> Array.toList

        let fileContentHash = FileContentHash(ContentAddress.computeBlake3Hex payloadBytes)
        let manifest = FileManifest.Create(ManifestAddress String.Empty, chunkingSuiteId, fileContentHash, int64 payloadBytes.Length, blocks)

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let assertContentBlockParameterShape typeName =
        let parameterType = getStorageParameterType typeName
        Assert.That(parameterType, Is.Not.Null, $"{typeName} should be defined in Grace.Shared.Parameters.Storage.")
        Assert.That(parameterType.IsSubclassOf(typeof<Parameters.Storage.StorageParameters>), Is.True)
        Assert.That(parameterType.GetProperty("RelativePath"), Is.Null)
        Assert.That(parameterType.GetProperty("ContentBlockAddress"), Is.Not.Null)

    let assertSdkMethod methodName parameterTypeName =
        let parameterType = getStorageParameterType parameterTypeName
        Assert.That(parameterType, Is.Not.Null)

        let storageType = typeof<Grace.SDK.AgentSession>.Assembly.GetType ("Grace.SDK.Storage", throwOnError = false)
        Assert.That(storageType, Is.Not.Null, "Grace.SDK.Storage should be available as an SDK module.")

        let methodInfo: MethodInfo = storageType.GetMethod(methodName, BindingFlags.Public ||| BindingFlags.Static)

        Assert.That(methodInfo, Is.Not.Null, $"Grace.SDK.Storage.{methodName} should be a public SDK method.")
        let parameters: ParameterInfo array = methodInfo.GetParameters()
        Assert.That(parameters, Has.Length.EqualTo(1))
        Assert.That(parameters[0].ParameterType, Is.EqualTo(parameterType))

    [<Test>]
    member _.ContentBlockSasParametersExposeAddressWithoutCallerSuppliedPath() =
        assertContentBlockParameterShape "GetContentBlockUploadUriParameters"
        assertContentBlockParameterShape "GetContentBlockDownloadUriParameters"

        Assert.That(
            (getStorageParameterType "GetContentBlockUploadUriParameters")
                .GetProperty("UploadSessionId"),
            Is.Not.Null
        )

        Assert.That(
            (getStorageParameterType "GetContentBlockUploadUriParameters")
                .GetProperty("AuthorizedScope"),
            Is.Not.Null
        )

        Assert.That(
            (getStorageParameterType "GetContentBlockDownloadUriParameters")
                .GetProperty("AuthorizedScope"),
            Is.Not.Null
        )

        Assert.That(
            (getStorageParameterType "GetContentBlockDownloadUriParameters")
                .GetProperty("ManifestAddress"),
            Is.Not.Null
        )

        Assert.That(
            (getStorageParameterType "GetContentBlockDownloadUriParameters")
                .GetProperty("Manifest"),
            Is.Null
        )

    [<Test>]
    member _.ContentBlockSasSdkMethodsMatchSharedParameterContracts() =
        assertSdkMethod "GetContentBlockUploadUri" "GetContentBlockUploadUriParameters"
        assertSdkMethod "GetContentBlockDownloadUri" "GetContentBlockDownloadUriParameters"

    [<Test>]
    member _.ContentBlockDiscoverySdkMethodMatchesSharedParameterContract() =
        let parameterType = getStorageParameterType "DiscoverContentBlocksParameters"
        Assert.That(parameterType, Is.Not.Null)
        Assert.That(parameterType.IsSubclassOf(typeof<Parameters.Storage.StorageParameters>), Is.True)
        Assert.That(parameterType.GetProperty("KeyChunkAddresses"), Is.Not.Null)
        Assert.That(parameterType.GetProperty("ContentBlockAddress"), Is.Null)
        assertSdkMethod "DiscoverContentBlocks" "DiscoverContentBlocksParameters"

    [<Test>]
    member _.DownloadPlacementResolutionUsesFinalizedScopedMetadataPlacement() =
        let storagePoolId = StoragePoolId "pool-download-placement"
        let authorizedScope = "/download/recorded-placement.bin"
        let manifestAddress = ManifestAddress "manifest-recorded-placement"
        let contentBlockAddress = ContentBlockAddress "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

        let recordedPlacement =
            {
                StorageAccountName = "recorded-account"
                StorageContainerName = StorageContainerName "recorded-container"
                ObjectKey = "compacted/cas/content/aaaaaaaa"
                ETag = Some "etag-recorded"
            }

        let currentRoutePlacement =
            { recordedPlacement with
                StorageAccountName = "current-account"
                StorageContainerName = StorageContainerName "current-container"
                ObjectKey = "cas/content/aaaaaaaa"
            }

        let metadata =
            { ContentBlockMetadata.Empty with
                StoragePoolId = storagePoolId
                ContentBlockAddress = contentBlockAddress
                StoragePlacement = recordedPlacement
                MetadataVersion = 7L
            }

        let registration: DedupeIndex.RuntimeFinalizedManifestRegistration =
            {
                StoragePoolId = storagePoolId
                Session = { UploadSessionDto.Default with AuthorizedScope = authorizedScope }
                ManifestAddress = manifestAddress
                Blocks =
                    [|
                        { Address = contentBlockAddress; ChunkAddresses = Array.empty }
                    |]
            }

        let state = { DedupeIndex.DedupeIndexState.Empty with FinalizedManifests = [| registration |]; MetadataRecords = [| metadata |] }

        let repositoryId = Guid.Parse("36805fab-e95a-409e-84c2-4b02698d18c8")
        let otherRepositoryId = Guid.Parse("d12cf61c-d824-4199-98d0-b83b57e1ecc5")
        let registration = { registration with Session = { registration.Session with RepositoryId = repositoryId } }
        let state = { state with FinalizedManifests = [| registration |] }

        let selected = Storage.tryFindFinalizedScopedContentBlockMetadata storagePoolId repositoryId authorizedScope manifestAddress contentBlockAddress state

        Assert.That(selected, Is.EqualTo(Some metadata))
        Assert.That(selected.Value.StoragePlacement, Is.Not.EqualTo(currentRoutePlacement))

        let rejected =
            Storage.tryFindFinalizedScopedContentBlockMetadata storagePoolId repositoryId "/other/scope.bin" manifestAddress contentBlockAddress state

        Assert.That(rejected, Is.EqualTo(None))

        let crossRepositoryRejected =
            Storage.tryFindFinalizedScopedContentBlockMetadata storagePoolId otherRepositoryId authorizedScope manifestAddress contentBlockAddress state

        Assert.That(crossRepositoryRejected, Is.EqualTo(None))

    [<Test>]
    member _.DefaultRepositoryDedupePoolResolutionUsesRepositoryScopedPool() =
        let repositoryId = Guid.Parse("9fce80dd-b0e5-462c-953d-1cc9e357d515")

        let repository = { RepositoryDto.Default with RepositoryId = repositoryId; StoragePoolId = StoragePoolRouting.defaultStoragePoolId }

        match Storage.resolveRepositoryDedupeStoragePoolId repository "corr-dedupe-pool" with
        | Error error -> Assert.Fail($"Expected default repository dedupe pool to resolve, got {error.Error}.")
        | Ok storagePoolId ->
            Assert.That(storagePoolId, Is.EqualTo(DedupeIndex.storagePoolIdForRepositoryId repositoryId))
            Assert.That(storagePoolId, Is.Not.EqualTo(StoragePoolRouting.defaultStoragePoolId))

    [<Test>]
    member _.UploadSessionRepositoryValidationRejectsCrossRepositorySession() =
        let requestRepositoryId = Guid.Parse("89be65f0-fb98-45fb-bbcf-b11683948430")
        let recordedRepositoryId = Guid.Parse("77544495-e6f0-41cf-9dc5-3c23c7921ce9")

        let session =
            { UploadSessionDto.Default with UploadSessionId = Guid.Parse("b16cf0e8-ef17-49ea-92b6-9f9e3896594d"); RepositoryId = recordedRepositoryId }

        match Storage.validateUploadSessionRepositoryId requestRepositoryId session "corr-cross-repo-session" with
        | Ok () -> Assert.Fail("Expected a session from a different repository to be rejected.")
        | Error error ->
            Assert.That(error.Error, Does.Contain("RepositoryId must match the request repository"))
            Assert.That(error.Error, Does.Contain($"{recordedRepositoryId}"))
            Assert.That(error.Error, Does.Contain($"{requestRepositoryId}"))
            Assert.That(error.CorrelationId, Is.EqualTo("corr-cross-repo-session"))

        match Storage.validateUploadSessionRepositoryId recordedRepositoryId session "corr-same-repo-session" with
        | Error error -> Assert.Fail($"Expected matching repository session to be accepted, got {error.Error}.")
        | Ok () -> Assert.Pass()

    [<Test>]
    member _.FinalizeReplayPreHydrationValidationAcceptsPayloadlessDurableManifest() =
        let payloadBytes = bytes "payload-less finalize replay"
        let block = contentBlockPayload 0L payloadBytes
        let manifest = buildManifest RabinChunking.SuiteName payloadBytes [| block |]

        let session =
            { UploadSessionDto.Default with
                StoragePoolId = manifest.StoragePoolId
                ExpectedSize = manifest.Size
                FileContentHash = manifest.FileContentHash
                ChunkingSuiteId = manifest.ChunkingSuiteId
            }

        match Storage.validateFinalizeReplayManifestBeforeHydration session manifest.ManifestAddress manifest "corr-payloadless-replay" with
        | Ok () -> Assert.Pass()
        | Error error -> Assert.Fail($"Expected payload-less replay manifest shape to validate before hydration, got {error.Error}.")

    [<Test>]
    member _.FinalizeReplayPreHydrationValidationRejectsTamperedBlockListWithoutPayloads() =
        let payloadBytes = bytes "payload-less finalize replay"
        let block = contentBlockPayload 0L payloadBytes
        let manifest = buildManifest RabinChunking.SuiteName payloadBytes [| block |]

        let session =
            { UploadSessionDto.Default with
                StoragePoolId = manifest.StoragePoolId
                ExpectedSize = manifest.Size
                FileContentHash = manifest.FileContentHash
                ChunkingSuiteId = manifest.ChunkingSuiteId
            }

        let tamperedBlock = ContentBlock.Create(ContentBlockAddress "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", 0L, manifest.Size)

        let tamperedManifest =
            { manifest with
                Blocks =
                    [ tamperedBlock ]
                    |> System.Collections.Generic.List<ContentBlock>
            }

        match Storage.validateFinalizeReplayManifestBeforeHydration session manifest.ManifestAddress tamperedManifest "corr-tampered-replay" with
        | Ok () -> Assert.Fail("Expected payload-less replay with a tampered block list to fail before hydration.")
        | Error error ->
            Assert.That(error.Error, Does.Contain("stable replay manifest address"))
            Assert.That(error.CorrelationId, Is.EqualTo("corr-tampered-replay"))

    [<Test>]
    member _.FinalizeManifestHydratesAndValidatesReplayEvidenceBeforeActorSideEffects() =
        let storageServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Storage.Server.fs"))
        let storageServerSource = File.ReadAllText(storageServerPath)
        let finalizeStart = storageServerSource.IndexOf("let FinalizeManifestUpload", StringComparison.Ordinal)
        let finalizeEnd = storageServerSource.IndexOf("let GetUploadUris", finalizeStart, StringComparison.Ordinal)

        Assert.That(finalizeStart, Is.GreaterThanOrEqualTo(0))
        Assert.That(finalizeEnd, Is.GreaterThan(finalizeStart))

        let finalizeSource = storageServerSource.Substring(finalizeStart, finalizeEnd - finalizeStart)
        let compactedStorageSource = String.Join(" ", storageServerSource.Split([| '\r'; '\n'; '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries))
        let scopeValidationIndex = finalizeSource.IndexOf("validateUploadSessionScope requestContext parameters correlationId true", StringComparison.Ordinal)

        let replayEventIndex = finalizeSource.IndexOf("getFinalizeReplayState requestContext parameters.OperationId correlationId", StringComparison.Ordinal)

        let replayPreHydrationValidateIndex =
            finalizeSource.IndexOf("validateFinalizeReplayManifestBeforeHydration", replayEventIndex, StringComparison.Ordinal)

        let replayHydrateIndex =
            finalizeSource.IndexOf("hydrateFinalizeReplayEvidence requestContext parameters parameters.Manifest correlationId", StringComparison.Ordinal)

        let replayValidateIndex = finalizeSource.IndexOf("validateFinalizeReplayEvidence", StringComparison.Ordinal)

        let actorHandleIndex =
            finalizeSource.IndexOf(
                "requestContext.UploadSessionActor.Handle (createFinalizeCommand evidence) requestContext.Metadata",
                StringComparison.Ordinal
            )

        let normalHydrateIndex =
            finalizeSource.IndexOf("hydrateFinalizeEvidence requestContext parameters parameters.Manifest true correlationId", StringComparison.Ordinal)

        Assert.That(scopeValidationIndex, Is.GreaterThanOrEqualTo(0))
        Assert.That(replayEventIndex, Is.GreaterThan(scopeValidationIndex))
        Assert.That(replayPreHydrationValidateIndex, Is.GreaterThan(replayEventIndex))
        Assert.That(replayHydrateIndex, Is.GreaterThan(replayPreHydrationValidateIndex))
        Assert.That(replayValidateIndex, Is.GreaterThan(replayHydrateIndex))
        Assert.That(actorHandleIndex, Is.GreaterThan(replayValidateIndex))
        Assert.That(normalHydrateIndex, Is.GreaterThan(actorHandleIndex))
        Assert.That(finalizeSource, Does.Not.Contain("hydrateFinalizeBlockPayloads context"))

        Assert.That(
            finalizeSource,
            Does.Not.Contain("ClaimedMetadata = Array.empty"),
            "Same-operation finalize replay side effects must not run with empty claimed metadata."
        )

        Assert.That(
            finalizeSource,
            Does.Not.Contain("BlockPayloads = parameters.BlockPayloads"),
            "Same-operation finalize replay side effects must not run with retry-supplied block payloads."
        )

        Assert.That(
            compactedStorageSource,
            Does.Contain(
                "validateFinalizeReplayManifestBeforeHydration requestContext.SessionForScope finalizedManifestAddress parameters.Manifest correlationId"
            ),
            "Finalize replay must validate retry manifest shape and address before reading authoritative metadata/blob placements."
        )

        Assert.That(
            compactedStorageSource,
            Does.Not.Contain(
                "validateFinalizeReplayManifestBeforeHydration requestContext.SessionForScope finalizedManifestAddress parameters.Manifest parameters.BlockPayloads correlationId"
            ),
            "Payload-less finalize replays must not validate request BlockPayloads before authoritative replay hydration."
        )

        Assert.That(
            finalizeSource,
            Does.Contain("operationAlreadyAppliedToDifferentUploadSessionEventError parameters.OperationId correlationId"),
            "Finalize must not let actor replay semantics turn a non-finalize operation-id collision into side effects."
        )

        Assert.That(
            compactedStorageSource,
            Does
                .Contain("&& not (isNull parameters.BlockPayloads)")
                .And.Contain("requestPayloads[payload.Address] <- payload")
                .And.Contain("match requestPayloads.TryGetValue address with"),
            "Fresh finalization may still use request payloads for non-claimed blocks; replay hydration opts out through the false flag."
        )

        let authoritativeUploadedHydrationIndex =
            compactedStorageSource.IndexOf(
                "manifestBlockWasUploaded requestContext.SessionForScope block -> match! tryReadFinalizeBlockPayloadFromAuthoritativeMetadata requestContext address correlationId",
                StringComparison.Ordinal
            )

        let confirmedUploadFallbackIndex = compactedStorageSource.IndexOf("confirmedBlockUploads |> Array.tryFind", StringComparison.Ordinal)

        Assert.That(
            authoritativeUploadedHydrationIndex,
            Is.GreaterThanOrEqualTo(0),
            "Uploaded replay hydration must prove session ownership before trying current authoritative metadata."
        )

        Assert.That(
            confirmedUploadFallbackIndex,
            Is.GreaterThan(authoritativeUploadedHydrationIndex),
            "ConfirmedBlockUploads must remain only a fallback after current authoritative uploaded metadata is unavailable."
        )

        Assert.That(
            compactedStorageSource,
            Does.Contain("| Some metadata -> match! readFinalizeBlockPayloadFromPlacement address metadata.StoragePlacement correlationId"),
            "Claimed reuse payload hydration must read authoritative storage placement even when request payloads exist."
        )

        Assert.That(
            finalizeSource,
            Does.Not.Contain("hydrateFinalizeEvidence requestContext parameters parameters.Manifest false correlationId"),
            "Same-operation finalize replay must not call the fresh claimed-metadata validation path."
        )

        Assert.That(
            storageServerSource,
            Does.Not.Contain("match! loadClaimedMetadataForFinalizeReplay requestContext manifest correlationId"),
            "Same-operation finalize replay must not rebuild repair evidence from upload-session claimed ranges that cleanup removes."
        )

        Assert.That(
            storageServerSource,
            Does.Not.Contain("{ authoritativeMetadata with MetadataVersion = claimedRange.MetadataVersion }"),
            "Finalize hydration must preserve current authoritative metadata versions so mixed-version claimed covers remain valid."
        )

    [<Test>]
    member _.FinalizeManifestLoadsClaimedReuseEvidenceFromAuthoritativeMetadataActorKey() =
        let storageServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Storage.Server.fs"))
        let storageServerSource = File.ReadAllText(storageServerPath)
        let compactedSource = String.Join(" ", storageServerSource.Split([| '\r'; '\n'; '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries))

        Assert.That(
            compactedSource,
            Does.Contain("ContentBlockMetadataActorKey.Create claimedRange.StoragePoolId claimedRange.ContentBlockAddress"),
            "Finalize must load claimed reuse proof by the authoritative StoragePoolId + ContentBlockAddress key."
        )

        Assert.That(
            compactedSource,
            Does.Contain("readFinalizeBlockPayloadFromPlacement address metadata.StoragePlacement"),
            "Claimed reuse payload hydration must use authoritative metadata placement, not a recomputed route."
        )

    [<Test>]
    member _.FinalizeClaimedReuseEvidenceAllowsEquivalentCurrentStateButFailsClosedOnUnsafeDrift() =
        let storagePoolId = StoragePoolId "pool-finalize-replay"
        let contentBlockAddress = ContentBlockAddress "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

        let placement =
            {
                StorageAccountName = "cas-account"
                StorageContainerName = StorageContainerName "cas-container"
                ObjectKey = "cas/content/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                ETag = Some "etag-current"
            }

        let durableRange = { OrdinalStart = 4; OrdinalCount = 8; ActiveManifestCount = 1; PhysicalOffset = 1024L; PhysicalLength = 4096L }

        let claimedRange: ClaimedReuseRange =
            {
                StoragePoolId = storagePoolId
                ContentBlockAddress = contentBlockAddress
                OrdinalStart = durableRange.OrdinalStart
                OrdinalCount = durableRange.OrdinalCount
                PhysicalOffset = durableRange.PhysicalOffset
                PhysicalLength = durableRange.PhysicalLength
                MetadataVersion = 7L
                ClaimedAt = NodaTime.Instant.FromUtc(2026, 6, 1, 12, 0)
            }

        let currentMetadata =
            { ContentBlockMetadata.Empty with
                StoragePoolId = storagePoolId
                ContentBlockAddress = contentBlockAddress
                BlockFormatVersion = 1s
                StoragePlacement = placement
                Ranges = [| durableRange |]
                MetadataVersion = 8L
            }

        Assert.That(
            Storage.authoritativeClaimedRangeMatchesForFinalizeReplay claimedRange currentMetadata,
            Is.True,
            "Replay hydration must not reject only because finalization advanced MetadataVersion after the original claim."
        )

        Assert.That(
            Storage.authoritativeClaimedRangeMatchesForFinalize claimedRange currentMetadata,
            Is.True,
            "Fresh finalization must accept an already-finalized equivalent actor state when metadata version advanced."
        )

        let inactiveExactRange = { durableRange with ActiveManifestCount = 0 }

        Assert.That(
            Storage.authoritativeClaimedRangeMatchesForFinalize claimedRange { currentMetadata with Ranges = [| inactiveExactRange |] },
            Is.True,
            "Fresh finalization may accept current exact range evidence even when metadata version advanced after the claim."
        )

        Assert.That(
            Storage.authoritativeClaimedRangeMatchesForFinalizeReplay claimedRange { currentMetadata with StoragePoolId = StoragePoolId "pool-other" },
            Is.False,
            "Replay hydration must fail closed for the wrong storage pool."
        )

        Assert.That(
            Storage.authoritativeClaimedRangeMatchesForFinalizeReplay
                claimedRange
                { currentMetadata with ContentBlockAddress = ContentBlockAddress "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" },
            Is.False,
            "Replay hydration must fail closed for the wrong content block address."
        )

        let relocatedFinalizedRange = { durableRange with PhysicalOffset = durableRange.PhysicalOffset + 1L }

        Assert.That(
            Storage.authoritativeClaimedRangeMatchesForFinalizeReplay claimedRange { currentMetadata with Ranges = [| relocatedFinalizedRange |] },
            Is.True,
            "Replay hydration must accept the actor's current finalized location after the original range was relocated."
        )

        Assert.That(
            Storage.authoritativeClaimedRangeMatchesForFinalize claimedRange { currentMetadata with Ranges = [| relocatedFinalizedRange |] },
            Is.True,
            "Fresh finalization must accept a finalized equivalent logical range after benign metadata churn."
        )

        let inactiveRelocatedRange = { relocatedFinalizedRange with ActiveManifestCount = 0 }

        Assert.That(
            Storage.authoritativeClaimedRangeMatchesForFinalizeReplay claimedRange { currentMetadata with Ranges = [| inactiveRelocatedRange |] },
            Is.False,
            "Replay hydration must fail closed when relocated metadata is not an active finalized range."
        )

        Assert.That(
            Storage.authoritativeClaimedRangeMatchesForFinalizeReplay
                claimedRange
                { currentMetadata with StoragePlacement = ContentBlockStoragePlacement.Empty },
            Is.False,
            "Replay hydration must fail closed when authoritative placement is missing."
        )

    [<Test>]
    member _.FinalizeClaimedReuseCoverUsesLogicalOrdinalWindowsAndRejectsPartialCovers() =
        let storagePoolId = StoragePoolId "pool-cover"
        let contentBlockAddress = ContentBlockAddress "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
        let claimedAt = NodaTime.Instant.FromUtc(2026, 6, 20, 12, 0)

        let claimedRange ordinalStart ordinalCount physicalOffset physicalLength metadataVersion =
            {
                StoragePoolId = storagePoolId
                ContentBlockAddress = contentBlockAddress
                OrdinalStart = ordinalStart
                OrdinalCount = ordinalCount
                PhysicalOffset = physicalOffset
                PhysicalLength = physicalLength
                MetadataVersion = metadataVersion
                ClaimedAt = claimedAt.Plus(NodaTime.Duration.FromSeconds(int64 ordinalStart))
            }

        let wholeBlockWithSourceOffset = claimedRange 0 1 4096L 10L 11L

        Assert.That(
            Storage.claimedReuseRangesCoverManifestBlock 10L [| wholeBlockWithSourceOffset |],
            Is.True,
            "Fresh finalization must not treat a source PhysicalOffset as the manifest-local logical byte offset."
        )

        let first = claimedRange 0 1 4096L 4L 11L
        let second = claimedRange 1 1 16384L 6L 12L

        Assert.That(
            Storage.claimedReuseRangesCoverManifestBlock 10L [| second; first |],
            Is.True,
            "Fresh finalization must accept mixed-version claimed ranges that cover adjacent logical ordinal windows."
        )

        Assert.That(
            Storage.claimedReuseRangesCoverManifestBlock
                10L
                [|
                    first
                    { second with OrdinalStart = 2 }
                |],
            Is.False,
            "Fresh finalization must reject claimed-range covers with an ordinal gap."
        )

        Assert.That(
            Storage.claimedReuseRangesCoverManifestBlock
                10L
                [|
                    first
                    { second with OrdinalStart = 0 }
                |],
            Is.False,
            "Fresh finalization must reject overlapping ordinal windows that do not form a complete block cover."
        )

        Assert.That(
            Storage.claimedReuseRangesCoverManifestBlock
                10L
                [|
                    first
                    { second with PhysicalLength = 7L }
                |],
            Is.False,
            "Fresh finalization must reject claimed ranges that run past the manifest block length."
        )

    [<Test>]
    member _.FinalizeClaimedReuseFreshHydrationSelectsCoverWithoutWholeBlockLengthFilter() =
        let storageServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Storage.Server.fs"))
        let storageServerSource = File.ReadAllText(storageServerPath)
        let compactedSource = String.Join(" ", storageServerSource.Split([| '\r'; '\n'; '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries))

        Assert.That(
            compactedSource,
            Does
                .Contain("claimedRangesForManifestBlockNewestFirst requestContext.SessionForScope block.Address")
                .And.Contain("trySelectClaimedRangeCover block.Size validatedClaims"),
            "Fresh finalization must validate the set of claimed ranges that covers the block."
        )

        Assert.That(
            compactedSource,
            Does.Not.Contain("&& claimedRange.PhysicalLength = physicalLength"),
            "Fresh finalization must not require a single claimed range to equal the whole manifest block length."
        )

    [<Test>]
    member _.FinalizeReplayHydratesRepairEvidenceFromCurrentManifestMetadataAfterSessionCleanup() =
        let storageServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Storage.Server.fs"))
        let storageServerSource = File.ReadAllText(storageServerPath)
        let replayStart = storageServerSource.IndexOf("let private hydrateFinalizeReplayEvidenceFromCurrentMetadata", StringComparison.Ordinal)

        let replayEnd =
            storageServerSource.IndexOf(
                "let private hydrateFinalizeReplayEvidence",
                replayStart
                + "let private hydrateFinalizeReplayEvidenceFromCurrentMetadata"
                    .Length,
                StringComparison.Ordinal
            )

        Assert.That(replayStart, Is.GreaterThanOrEqualTo(0))
        Assert.That(replayEnd, Is.GreaterThan(replayStart))

        let replaySource = storageServerSource.Substring(replayStart, replayEnd - replayStart)
        let compactedReplaySource = String.Join(" ", replaySource.Split([| '\r'; '\n'; '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries))

        Assert.That(
            compactedReplaySource,
            Does
                .Contain("let manifestBlocks = manifestBlocksForPayloadHydration manifest")
                .And.Contain("tryLoadAuthoritativeContentBlockMetadataForFinalize requestContext block.Address correlationId")
                .And.Contain("readFinalizeBlockPayloadFromPlacement block.Address authoritativeMetadata.StoragePlacement correlationId")
                .And.Contain("confirmedBlockUploads |> Array.tryFind")
                .And.Contain("readFinalizeBlockPayloadFromPlacement confirmedBlock.ContentBlockAddress confirmedBlock.StoragePlacement correlationId")
                .And.Contain("no confirmed upload placement remains in the upload session")
                .And.Contain("ClaimedMetadata = metadata.ToArray()"),
            "Finalize replay repair must prefer current metadata, fall back to retained confirmed upload placement, and fail closed after cleanup."
        )

        Assert.That(
            replaySource,
            Does
                .Not
                .Contain("manifestBlocksRequiringClaimedMetadata")
                .And.Not.Contain("ClaimedReuseRanges"),
            "Cleanup removes transient claimed-reuse arrays, so replay repair cannot depend on them."
        )

    [<Test>]
    member _.FinalizeManifestHydratesClaimedMetadataOnlyForBlocksNotSatisfiedByConfirmedUploads() =
        let storageServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Storage.Server.fs"))
        let storageServerSource = File.ReadAllText(storageServerPath)
        let compactedSource = String.Join(" ", storageServerSource.Split([| '\r'; '\n'; '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries))

        Assert.That(compactedSource, Does.Contain("manifestBlocksRequiringClaimedMetadata requestContext.SessionForScope manifest"))

        Assert.That(
            compactedSource,
            Does.Contain("not (manifestBlockWasUploaded session block)"),
            "Claimed metadata hydration must skip stale claims for manifest blocks already satisfied by confirmed uploads."
        )

    [<Test>]
    member _.DownloadAuthorizationUsesTargetedDedupeIndexLookupInsteadOfFullSnapshotState() =
        let methodInfo = typeof<IDedupeIndexActor>.GetMethod ("TryGetFinalizedScopedContentBlockMetadata", BindingFlags.Public ||| BindingFlags.Instance)

        Assert.That(methodInfo, Is.Not.Null)

        let storageServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Storage.Server.fs"))
        let storageServerSource = File.ReadAllText(storageServerPath)

        Assert.That(storageServerSource, Does.Contain("TryGetFinalizedScopedContentBlockMetadata"))
        Assert.That(storageServerSource, Does.Not.Contain("SnapshotState correlationId"))

    [<Test>]
    member _.UploadSessionBackedSasUsesRecordedSessionPoolRoute() =
        let storageServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Storage.Server.fs"))
        let storageServerSource = File.ReadAllText(storageServerPath)
        let compactedSource = String.Join(" ", storageServerSource.Split([| '\r'; '\n'; '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries))

        Assert.That(compactedSource, Does.Contain("resolveUploadSessionStoragePoolRoute session.RepositoryId session.StoragePoolId"))

        Assert.That(
            compactedSource,
            Does.Contain("resolveUploadSessionStoragePoolRoute requestContext.SessionForScope.RepositoryId requestContext.SessionForScope.StoragePoolId")
        )

        Assert.That(storageServerSource, Does.Contain("StoragePoolId = storagePoolId"))
        Assert.That(storageServerSource, Does.Contain("validateManifestForContentBlockDownload storagePoolId repositoryId"))
        Assert.That(storageServerSource, Does.Contain("DedupeIndex.discover storagePoolId"))

    [<Test>]
    member _.UploadSessionStagingCleanupUsesRecordedSessionPoolRoute() =
        let servicesPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "Services.Actor.fs"))
        let servicesSource = File.ReadAllText(servicesPath)
        let cleanupStart = servicesSource.IndexOf("let deleteUploadSessionStagingPayloads", StringComparison.Ordinal)
        let cleanupEnd = servicesSource.IndexOf("let getAzureBlobClientForFileVersion", cleanupStart, StringComparison.Ordinal)

        Assert.That(cleanupStart, Is.GreaterThanOrEqualTo(0))
        Assert.That(cleanupEnd, Is.GreaterThan(cleanupStart))

        let cleanupSource = servicesSource.Substring(cleanupStart, cleanupEnd - cleanupStart)
        let compactedSource = String.Join(" ", cleanupSource.Split([| '\r'; '\n'; '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries))

        Assert.That(
            compactedSource,
            Does.Contain("resolveUploadSessionStoragePoolRoute session.RepositoryId session.StoragePoolId"),
            "Cleanup must resolve the route from the session's recorded StoragePoolId, not current repository state."
        )

        Assert.That(
            compactedSource,
            Does.Not.Contain("StoragePoolRouting.resolveRepositoryRoute repositoryDto correlationId"),
            "Cleanup must not reload the repository and follow mutable route state after SAS issuance."
        )

    [<Test>]
    member _.ConfirmActorRejectionDoesNotDeleteFinalCasPlacement() =
        let storageServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Storage.Server.fs"))
        let storageServerSource = File.ReadAllText(storageServerPath)

        let compactedSource =
            storageServerSource
                .Replace("\r", String.Empty)
                .Replace("\n", String.Empty)
                .Replace(" ", String.Empty)

        Assert.That(storageServerSource, Does.Not.Contain("shouldDeleteCreatedFinalContentBlockPayload"))
        Assert.That(compactedSource, Does.Not.Contain("deleteContentBlockPayloadBestEffortfinalMaterialization.StoragePlacement"))
