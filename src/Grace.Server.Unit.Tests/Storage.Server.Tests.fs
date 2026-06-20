namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
open Grace.Types.UploadSession
open NUnit.Framework
open System.Reflection

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
                .GetProperty("Manifest"),
            Is.Not.Null
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
