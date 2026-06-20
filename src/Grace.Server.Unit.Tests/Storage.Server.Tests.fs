namespace Grace.Server.Tests

open Azure.Storage.Sas
open Grace.Shared
open Grace.Types.Common
open Grace.Types.Repository
open Grace.Types.UploadSession
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Reflection

module DedupeIndex = Grace.Shared.DedupeIndex
module ActorServices = Grace.Actors.Services
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

    let downloadManifestWithBlock storagePoolId contentBlockAddress =
        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                ChunkingSuiteId RabinChunking.SuiteName,
                FileContentHash "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210",
                10L,
                [
                    ContentBlock.Create(contentBlockAddress, 0L, 10L)
                ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest; StoragePoolId = storagePoolId }

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

    let repositoryWithStoragePool storagePoolId =
        { RepositoryDto.Default with
            RepositoryId = Guid.Parse("4d509875-a266-49ea-87d9-6f57b1b919c6")
            OwnerId = Guid.Parse("241d960f-1aba-46ef-93d6-765a8b82252c")
            OrganizationId = Guid.Parse("1a266008-bc58-45d3-99ad-4b6380ebd623")
            RepositoryStatus = RepositoryStatus.Active
            ObjectStorageProvider = ObjectStorageProvider.AzureBlobStorage
            StorageAccountName = Constants.DefaultCasStorageAccountName
            StorageContainerName = Constants.DefaultCasStorageContainerName
            StoragePoolId = storagePoolId
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
    member _.ContentBlockCreateSasPermissionsDoNotGrantOverwriteWrite() =
        Assert.That(ActorServices.azureBlobCreatePermissions.HasFlag BlobSasPermissions.Create, Is.True)
        Assert.That(ActorServices.azureBlobCreatePermissions.HasFlag BlobSasPermissions.Write, Is.False)

    [<Test>]
    member _.ContentBlockDownloadUriParametersCanCarryManifestStoragePool() =
        let parameterType = getStorageParameterType "GetContentBlockDownloadUriParameters"
        Assert.That(parameterType, Is.Not.Null)
        Assert.That(parameterType.GetProperty("StoragePoolId"), Is.Not.Null)
        Assert.That(parameterType.GetProperty("FilePath"), Is.Not.Null)
        Assert.That(parameterType.GetProperty("Manifest"), Is.Not.Null)
        Assert.That(parameterType.GetProperty("UploadSessionId"), Is.Not.Null)

    [<Test>]
    member _.ContentBlockDownloadRouteUsesStoredManifestPoolAfterRepositoryRouteDrift() =
        let originalPool = StoragePoolId Constants.DefaultStoragePoolId
        let currentRepositoryPool = StoragePoolId "pool-after-download-route-drift"
        let contentBlockAddress = ContentBlockAddress "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        let manifest = downloadManifestWithBlock originalPool contentBlockAddress
        let repository = repositoryWithStoragePool currentRepositoryPool
        let parameters = Parameters.Storage.GetContentBlockDownloadUriParameters()
        parameters.ContentBlockAddress <- contentBlockAddress
        parameters.StoragePoolId <- originalPool
        parameters.Manifest <- manifest

        match StorageServer.resolveContentBlockDownloadStorageRoute "corr-download-route-drift" repository parameters with
        | Ok route ->
            Assert.That(route.StoragePoolId, Is.EqualTo(originalPool))
            Assert.That(route.StorageShard.StorageContainerName, Is.EqualTo(Constants.DefaultCasStorageContainerName))
        | Error error -> Assert.Fail($"Expected stored manifest pool to resolve after repository route drift, got {error.Error}.")

    [<Test>]
    member _.ContentBlockDownloadRouteRejectsStoragePoolIdThatDoesNotMatchManifest() =
        let originalPool = StoragePoolId Constants.DefaultStoragePoolId
        let contentBlockAddress = ContentBlockAddress "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        let manifest = downloadManifestWithBlock originalPool contentBlockAddress
        let repository = repositoryWithStoragePool originalPool
        let parameters = Parameters.Storage.GetContentBlockDownloadUriParameters()
        parameters.ContentBlockAddress <- contentBlockAddress
        parameters.StoragePoolId <- StoragePoolId "pool-swapped"
        parameters.Manifest <- manifest

        match StorageServer.resolveContentBlockDownloadStorageRoute "corr-download-route-swap" repository parameters with
        | Ok route -> Assert.Fail($"Expected mismatched StoragePoolId to fail closed, got {route.StoragePoolId}.")
        | Error error -> Assert.That(error.Error, Does.Contain("must match"))

    [<Test>]
    member _.ContentBlockDownloadFilePathEvidenceRejectsManifestSwap() =
        let authorizedPath = RelativePath "src/authorized.bin"
        let contentBlockAddress = ContentBlockAddress "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        let authorizedManifest = downloadManifestWithBlock (StoragePoolId Constants.DefaultStoragePoolId) contentBlockAddress

        let swappedManifest =
            downloadManifestWithBlock
                (StoragePoolId Constants.DefaultStoragePoolId)
                (ContentBlockAddress "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")

        let fileVersion = FileVersion.Create authorizedPath authorizedManifest.FileContentHash String.Empty false authorizedManifest.Size
        fileVersion.ContentReference <- FileContentReference.FileManifest authorizedManifest

        match StorageServer.validateContentBlockDownloadFilePathEvidence "corr-path-swap" authorizedPath (Some fileVersion) swappedManifest with
        | Ok () -> Assert.Fail("Expected FilePath evidence to reject a caller-supplied manifest for a different saved file.")
        | Error error -> Assert.That(error.Error, Does.Contain("ManifestAddress"))

        match StorageServer.validateContentBlockDownloadFilePathEvidence "corr-path-bound" authorizedPath (Some fileVersion) authorizedManifest with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected FilePath evidence to accept the saved file manifest, got {error.Error}.")

    [<Test>]
    member _.ContentBlockDownloadRepositoryOwnershipDoesNotRequireLatestPathMatch() =
        let historicalPath = RelativePath "src/replaced.bin"
        let contentBlockAddress = ContentBlockAddress "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        let historicalManifest = downloadManifestWithBlock (StoragePoolId Constants.DefaultStoragePoolId) contentBlockAddress

        let latestManifest =
            downloadManifestWithBlock
                (StoragePoolId Constants.DefaultStoragePoolId)
                (ContentBlockAddress "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")

        let latestFileVersion = FileVersion.Create historicalPath latestManifest.FileContentHash String.Empty false latestManifest.Size
        latestFileVersion.ContentReference <- FileContentReference.FileManifest latestManifest

        match
            StorageServer.validateContentBlockDownloadFilePathEvidence
                "corr-historical-path-regression"
                historicalPath
                (Some latestFileVersion)
                historicalManifest
            with
        | Ok () -> Assert.Fail("The latest path evidence should reject the historical manifest after the path is replaced.")
        | Error error -> Assert.That(error.Error, Does.Contain("ManifestAddress"))

        match StorageServer.validateContentBlockDownloadRepositoryOwnership "corr-historical-repository-owned" true with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected durable repository manifest ownership to authorize the historical manifest, got {error.Error}.")

        match StorageServer.validateContentBlockDownloadRepositoryOwnership "corr-historical-not-owned" false with
        | Ok () -> Assert.Fail("Expected missing repository manifest ownership to fail closed.")
        | Error error -> Assert.That(error.Error, Does.Contain("repository-owned manifest"))

    [<Test>]
    member _.GeneratedRawClientsCarryFilePathForContentBlockDownloads() =
        let repoRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."))

        let assertContains (relativePath: string) expected =
            let fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))
            Assert.That(File.Exists fullPath, Is.True, $"{relativePath} should exist.")
            Assert.That(File.ReadAllText fullPath, Does.Contain(expected), $"{relativePath} should include {expected}.")

        assertContains "sdk/generated/matrix/openapi-generator/typescript-fetch/src/models/GetContentBlockDownloadUriParameters.ts" "filePath"

        assertContains
            "sdk/generated/matrix/openapi-generator/typescript-fetch/src/models/GetContentBlockDownloadUriParameters.ts"
            "'FilePath': value['filePath']"

        assertContains
            "sdk/generated/matrix/openapi-generator/python/grace_generated_openapi_probe/models/get_content_block_download_uri_parameters.py"
            "file_path:"

        assertContains
            "sdk/generated/matrix/openapi-generator/python/grace_generated_openapi_probe/models/get_content_block_download_uri_parameters.py"
            "\"FilePath\": obj.get(\"FilePath\")"

        assertContains "sdk/generated/matrix/openapi-generator/rust/src/models/get_content_block_download_uri_parameters.rs" "pub file_path: Option<String>"

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
    member _.FinalizeManifestUploadRejectsCallerSuppliedBlockPayloadFallbacks() =
        let contentBlockAddress = ContentBlockAddress "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        let callerPayload: FinalizeManifestBlockPayload = { Address = contentBlockAddress; Payload = [| 1uy; 2uy; 3uy |] }

        match StorageServer.validateFinalizeBlockPayloadContract "corr-finalize-fallback" [| callerPayload |] with
        | Ok _ -> Assert.Fail("Expected caller-supplied finalize block payload bytes to be rejected.")
        | Error error -> Assert.That(error.Error, Does.Contain("server-hydrated"))

        match StorageServer.validateFinalizeBlockPayloadContract "corr-finalize-empty" Array.empty with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected empty finalize payload array to remain valid, got {error.Error}.")

    [<Test>]
    member _.DownloadManifestValidationReturnsGraceErrorsForMalformedEvidence() =
        let storagePoolId = StoragePoolId "pool-download-validation"
        let contentBlockAddress = ContentBlockAddress "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        let manifest = downloadManifestWithBlock storagePoolId contentBlockAddress

        let invalidCases =
            [|
                { manifest with ChunkingSuiteId = ChunkingSuiteId String.Empty }, "ChunkingSuiteId"
                { manifest with FileContentHash = FileContentHash "not-a-blake3-address" }, "FileContentHash"
                { manifest with
                    Blocks =
                        List<ContentBlock>(
                            [
                                ContentBlock.Create(contentBlockAddress, 0L, 0L)
                            ]
                        )
                },
                "Size must be positive"
                { manifest with
                    Blocks =
                        List<ContentBlock>(
                            [
                                ContentBlock.Create(contentBlockAddress, 1L, 10L)
                            ]
                        )
                },
                "contiguous from zero"
            |]

        for invalidManifest, expectedMessage in invalidCases do
            match StorageServer.validateContentBlockDownloadManifest "corr-download-validation" contentBlockAddress storagePoolId invalidManifest with
            | Ok () -> Assert.Fail($"Expected malformed manifest to fail validation for {expectedMessage}.")
            | Error error -> Assert.That(error.Error, Does.Contain(expectedMessage))

        match StorageServer.validateContentBlockDownloadManifest "corr-download-valid" contentBlockAddress storagePoolId manifest with
        | Ok () -> ()
        | Error error -> Assert.Fail($"Expected valid manifest download evidence to pass, got {error.Error}.")

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
