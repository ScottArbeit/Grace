namespace Grace.Server.Tests

open Grace.Shared
open Grace.Types.Common
open Grace.Types.UploadSession
open NodaTime
open NUnit.Framework
open System
open System.Reflection

module DedupeIndex = Grace.Shared.DedupeIndex
module StorageServer = Grace.Server.Storage

[<Parallelizable(ParallelScope.All)>]
type StorageContentBlockSdkContract() =

    let sessionWithPool storagePoolId =
        { UploadSessionDto.Default with
            UploadSessionId = Guid.Parse("f5bdfb2a-1f4a-4509-a6d6-b610f851cfdd")
            StoragePoolId = storagePoolId
            LifecycleState = UploadSessionLifecycleState.Started
        }

    let reuseHint storagePoolId : ContentBlockReuseRangeHint =
        {
            StoragePoolId = storagePoolId
            ContentBlockAddress = ContentBlockAddress "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            OrdinalStart = 0
            OrdinalCount = 8
            MetadataVersion = 7L
        }

    let dedupeRecord storagePoolId : DedupeIndex.DedupeIndexRecord =
        let hint = reuseHint storagePoolId

        {
            StoragePoolId = hint.StoragePoolId
            ManifestAddress = ManifestAddress "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
            ContentBlockAddress = hint.ContentBlockAddress
            OrdinalStart = hint.OrdinalStart
            OrdinalCount = hint.OrdinalCount
            MetadataVersion = hint.MetadataVersion
            ProtectedChunkAddresses =
                [|
                    "protected-sha256:session-original"
                |]
        }

    let discoveryFor (hint: ContentBlockReuseRangeHint) : DedupeDiscoverySnapshot =
        {
            OperationId = UploadSessionOperationId "discover-original"
            ExpiresAt = Instant.FromUtc(2026, 6, 19, 12, 0)
            MinimumReuseRunLength = hint.OrdinalCount
            Hints = [| hint |]
        }

    let manifestWithBlock contentBlockAddress =
        FileManifest.Create(
            ManifestAddress "manifest-address",
            10L,
            [
                ContentBlock.Create(contentBlockAddress, 0L, 10L)
            ]
        )

    let claimedRange storagePoolId contentBlockAddress : ClaimedReuseRange =
        {
            StoragePoolId = storagePoolId
            ContentBlockAddress = contentBlockAddress
            OrdinalStart = 0
            OrdinalCount = 8
            PhysicalOffset = 0L
            PhysicalLength = 10L
            MetadataVersion = 7L
            ClaimedAt = Instant.FromUtc(2026, 6, 19, 12, 1)
        }

    let confirmedBlock contentBlockAddress : ConfirmedBlockUpload =
        {
            ContentBlockAddress = contentBlockAddress
            PayloadLength = 10L
            StoragePlacement = { ObjectKey = "cas/content/confirmed"; ETag = Some "etag-confirmed" }
            Ranges = Array.empty
            ConfirmedAt = Instant.FromUtc(2026, 6, 19, 12, 2)
        }

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
    member _.ContentBlockUploadUriParametersArePinnedToUploadSession() =
        let parameterType = getStorageParameterType "GetContentBlockUploadUriParameters"
        Assert.That(parameterType, Is.Not.Null)
        Assert.That(parameterType.IsSubclassOf(typeof<Parameters.Storage.UploadSessionStorageParameters>), Is.True)
        Assert.That(parameterType.GetProperty("UploadSessionId"), Is.Not.Null)
        Assert.That(parameterType.GetProperty("AuthorizedScope"), Is.Not.Null)

    [<Test>]
    member _.ContentBlockDownloadUriParametersCanCarryManifestStoragePool() =
        let parameterType = getStorageParameterType "GetContentBlockDownloadUriParameters"
        Assert.That(parameterType, Is.Not.Null)
        Assert.That(parameterType.GetProperty("StoragePoolId"), Is.Not.Null)
        Assert.That(parameterType.GetProperty("Manifest"), Is.Not.Null)
        Assert.That(parameterType.GetProperty("UploadSessionId"), Is.Not.Null)

    [<Test>]
    member _.FinalizePayloadHydrationSelectsClaimedReuseRangeWhenBlockWasNotConfirmed() =
        let storagePoolId = StoragePoolId "pool-original"
        let contentBlockAddress = ContentBlockAddress "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        let claimedRange = claimedRange storagePoolId contentBlockAddress
        let manifest = manifestWithBlock contentBlockAddress

        let sources = StorageServer.selectFinalizeBlockPayloadSources manifest Array.empty [| claimedRange |]

        Assert.That(sources, Has.Length.EqualTo(1))
        Assert.That(fst sources[0], Is.EqualTo(contentBlockAddress))

        match snd sources[0] with
        | StorageServer.ClaimedReuseRange selectedRange -> Assert.That(selectedRange, Is.EqualTo(claimedRange))
        | StorageServer.ConfirmedUpload _ -> Assert.Fail("Expected unconfirmed manifest block to hydrate from the claimed reuse range.")

    [<Test>]
    member _.FinalizePayloadHydrationPrefersConfirmedUploadWhenBothConfirmedAndClaimedExist() =
        let storagePoolId = StoragePoolId "pool-original"
        let contentBlockAddress = ContentBlockAddress "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        let claimedRange = claimedRange storagePoolId contentBlockAddress
        let confirmedBlock = confirmedBlock contentBlockAddress
        let manifest = manifestWithBlock contentBlockAddress

        let sources = StorageServer.selectFinalizeBlockPayloadSources manifest [| confirmedBlock |] [| claimedRange |]

        Assert.That(sources, Has.Length.EqualTo(1))

        match snd sources[0] with
        | StorageServer.ConfirmedUpload selectedBlock -> Assert.That(selectedBlock, Is.EqualTo(confirmedBlock))
        | StorageServer.ClaimedReuseRange _ -> Assert.Fail("Expected confirmed upload payload hydration to preserve existing precedence.")

    [<Test>]
    member _.IssueDedupeDiscoveryValidationUsesSessionStoragePoolAfterRepositoryRouteDrift() =
        let originalPool = StoragePoolId "pool-original"
        let currentRepositoryPool = StoragePoolId "pool-after-route-change"
        let hint = reuseHint originalPool
        let records = [| dedupeRecord originalPool |]

        match StorageServer.validateIssuedDedupeDiscoveryHintsForSession "corr-discovery-original" (sessionWithPool originalPool) [| hint |] records with
        | Ok boundHints ->
            Assert.That(boundHints, Has.Length.EqualTo(1))
            Assert.That(boundHints[0].StoragePoolId, Is.EqualTo(originalPool))
        | Error error -> Assert.Fail($"Expected original session pool discovery hints to validate, got {error.Error}.")

        match StorageServer.validateIssuedDedupeDiscoveryHintsForSession "corr-discovery-drift" (sessionWithPool currentRepositoryPool) [| hint |] records with
        | Ok _ -> Assert.Fail("Expected discovery validation to reject hints that do not match the session-recorded storage pool.")
        | Error error -> Assert.That(error.Error, Does.Contain("this repository"))

    [<Test>]
    member _.ClaimReuseRangeValidationUsesSessionStoragePoolAfterRepositoryRouteDrift() =
        let originalPool = StoragePoolId "pool-original"
        let currentRepositoryPool = StoragePoolId "pool-after-route-change"
        let hint = reuseHint originalPool
        let discovery = discoveryFor hint

        match StorageServer.validateClaimReuseHintsForSession "corr-claim-original" (sessionWithPool originalPool) discovery [| hint |] with
        | Ok boundHints ->
            Assert.That(boundHints, Has.Length.EqualTo(1))
            Assert.That(boundHints[0].StoragePoolId, Is.EqualTo(originalPool))
        | Error error -> Assert.Fail($"Expected original session pool claim hints to validate, got {error.Error}.")

        match StorageServer.validateClaimReuseHintsForSession "corr-claim-drift" (sessionWithPool currentRepositoryPool) discovery [| hint |] with
        | Ok _ -> Assert.Fail("Expected claim validation to reject hints that do not match the session-recorded storage pool.")
        | Error error -> Assert.That(error.Error, Does.Contain("upload session repository storage pool"))
