namespace Grace.Server.Tests

open Grace.Server
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
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

        let selected = Storage.tryFindFinalizedScopedContentBlockMetadata storagePoolId authorizedScope manifestAddress contentBlockAddress state

        Assert.That(selected, Is.EqualTo(Some metadata))
        Assert.That(selected.Value.StoragePlacement, Is.Not.EqualTo(currentRoutePlacement))

        let rejected = Storage.tryFindFinalizedScopedContentBlockMetadata storagePoolId "/other/scope.bin" manifestAddress contentBlockAddress state

        Assert.That(rejected, Is.EqualTo(None))

    [<Test>]
    member _.DownloadAuthorizationUsesTargetedDedupeIndexLookupInsteadOfFullSnapshotState() =
        let methodInfo = typeof<IDedupeIndexActor>.GetMethod ("TryGetFinalizedScopedContentBlockMetadata", BindingFlags.Public ||| BindingFlags.Instance)

        Assert.That(methodInfo, Is.Not.Null)

        let storageServerPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Storage.Server.fs"))
        let storageServerSource = File.ReadAllText(storageServerPath)

        Assert.That(storageServerSource, Does.Contain("TryGetFinalizedScopedContentBlockMetadata"))
        Assert.That(storageServerSource, Does.Not.Contain("SnapshotState correlationId"))

    [<Test>]
    member _.CreatedFinalPayloadCleanupSkipsDeletionWhenMetadataReferenceExists() =
        task {
            let calls = ResizeArray<StoragePoolId * ContentBlockAddress * ContentBlockRangeQuery * CorrelationId>()

            let getRangePresence storagePoolId contentBlockAddress query correlationId =
                calls.Add(storagePoolId, contentBlockAddress, query, correlationId)
                Task.FromResult ContentBlockRangePresence.Active

            let! shouldDelete =
                Storage.shouldDeleteCreatedFinalContentBlockPayload
                    getRangePresence
                    (StoragePoolId "pool-cas-race")
                    (ContentBlockAddress "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                    "corr-cas-race"

            Assert.That(shouldDelete, Is.False)
            Assert.That(calls, Has.Count.EqualTo(1))
            let storagePoolId, contentBlockAddress, query, correlationId = calls[0]
            Assert.That(storagePoolId, Is.EqualTo(StoragePoolId "pool-cas-race"))
            Assert.That(contentBlockAddress, Is.EqualTo(ContentBlockAddress "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"))
            Assert.That(query.OrdinalStart, Is.EqualTo(0))
            Assert.That(query.OrdinalCount, Is.EqualTo(1))
            Assert.That(correlationId, Is.EqualTo("corr-cas-race"))
        }

    [<Test>]
    member _.CreatedFinalPayloadCleanupUsesRepositoryMetadataPoolForRangePresence() =
        task {
            let calls = ResizeArray<StoragePoolId * ContentBlockAddress * ContentBlockRangeQuery * CorrelationId>()
            let repositoryId = Guid.Parse("77777777-7777-7777-7777-777777777777")
            let metadataPoolId = Storage.finalContentBlockMetadataPoolIdForCleanup repositoryId
            let routePoolId = StoragePoolId "selected-route-pool"

            Assert.That(metadataPoolId, Is.EqualTo(StoragePoolId $"{repositoryId}"))
            Assert.That(metadataPoolId, Is.Not.EqualTo(routePoolId))

            let getRangePresence storagePoolId contentBlockAddress query correlationId =
                calls.Add(storagePoolId, contentBlockAddress, query, correlationId)

                if storagePoolId = metadataPoolId then
                    Task.FromResult ContentBlockRangePresence.Active
                else
                    Task.FromResult ContentBlockRangePresence.Absent

            let! shouldDelete =
                Storage.shouldDeleteCreatedFinalContentBlockPayload
                    getRangePresence
                    metadataPoolId
                    (ContentBlockAddress "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd")
                    "corr-cas-metadata-pool"

            Assert.That(shouldDelete, Is.False)
            Assert.That(calls, Has.Count.EqualTo(1))
            let storagePoolId, _, _, _ = calls[0]
            Assert.That(storagePoolId, Is.EqualTo(metadataPoolId))
        }

    [<Test>]
    member _.CreatedFinalPayloadCleanupAllowsDeletionWhenMetadataRangeIsAbsent() =
        task {
            let getRangePresence _ _ _ _ = Task.FromResult ContentBlockRangePresence.Absent

            let! shouldDelete =
                Storage.shouldDeleteCreatedFinalContentBlockPayload
                    getRangePresence
                    (StoragePoolId "pool-cas-absent")
                    (ContentBlockAddress "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")
                    "corr-cas-absent"

            Assert.That(shouldDelete, Is.True)
        }
