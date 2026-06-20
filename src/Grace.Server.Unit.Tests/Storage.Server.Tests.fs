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
open System.Threading.Tasks

[<Parallelizable(ParallelScope.All)>]
type StorageContentBlockSdkContract() =

    let getStorageParameterType typeName =
        typeof<Parameters.Storage.StorageParameters>.Assembly.GetType ($"Grace.Shared.Parameters.Storage+{typeName}", throwOnError = false)

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
    member _.FinalizeManifestValidatesRequestRepositoryBeforeHydratingEvidence() =
        let storageServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Storage.Server.fs"))
        let storageServerSource = File.ReadAllText(storageServerPath)
        let finalizeStart = storageServerSource.IndexOf("let FinalizeManifestUpload", StringComparison.Ordinal)
        let finalizeEnd = storageServerSource.IndexOf("let GetUploadUris", finalizeStart, StringComparison.Ordinal)

        Assert.That(finalizeStart, Is.GreaterThanOrEqualTo(0))
        Assert.That(finalizeEnd, Is.GreaterThan(finalizeStart))

        let finalizeSource = storageServerSource.Substring(finalizeStart, finalizeEnd - finalizeStart)
        let scopeValidationIndex = finalizeSource.IndexOf("validateUploadSessionScope requestContext parameters correlationId true", StringComparison.Ordinal)

        let replayIndex =
            finalizeSource.IndexOf("tryReplayUploadSessionCommand requestContext replayCommand parameters.OperationId correlationId", StringComparison.Ordinal)

        let hydrateIndex =
            finalizeSource.IndexOf("hydrateFinalizeEvidence requestContext parameters parameters.Manifest correlationId", StringComparison.Ordinal)

        Assert.That(scopeValidationIndex, Is.GreaterThanOrEqualTo(0))
        Assert.That(replayIndex, Is.GreaterThan(scopeValidationIndex))
        Assert.That(hydrateIndex, Is.GreaterThan(replayIndex))
        Assert.That(hydrateIndex, Is.GreaterThan(scopeValidationIndex))
        Assert.That(finalizeSource, Does.Not.Contain("hydrateFinalizeBlockPayloads context"))

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
    member _.FinalizeManifestHydratesClaimedMetadataOnlyForBlocksNotSatisfiedByConfirmedUploads() =
        let storageServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Storage.Server.fs"))
        let storageServerSource = File.ReadAllText(storageServerPath)
        let compactedSource = String.Join(" ", storageServerSource.Split([| '\r'; '\n'; '\t'; ' ' |], StringSplitOptions.RemoveEmptyEntries))

        Assert.That(compactedSource, Does.Contain("manifestBlockAddressesRequiringClaimedMetadata requestContext.SessionForScope manifest"))

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
