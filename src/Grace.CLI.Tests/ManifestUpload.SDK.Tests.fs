namespace Grace.CLI.Tests

open Azure
open Grace.CLI
open Grace.SDK
open Grace.Shared
open Grace.Shared.Parameters.Storage
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.ContentBlockMetadata
open Grace.Types.Common
open Grace.Types.UploadSession
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

[<NonParallelizable>]
type ManifestUploadSdkTests() =
    static member private PseudoRandomBytes length =
        let bytes = Array.zeroCreate<byte> length
        let mutable state = 0x4567abcdu

        for index in 0 .. length - 1 do
            state <- state ^^^ (state <<< 13)
            state <- state ^^^ (state >>> 17)
            state <- state ^^^ (state <<< 5)
            bytes[index] <- byte (state &&& 0xffu)

        bytes

    static member private TextLikePseudoRandomBytes length =
        ManifestUploadSdkTests.PseudoRandomBytes length
        |> Array.map (fun value -> if value = 0uy then 1uy else value)

    static member private BinaryPolicy thresholdBytes = { ManifestEligibilityPolicy.Default with ThresholdBytes = thresholdBytes; BinaryScanBytes = 16 }

    static member private ComputeSha256Hash(bytes: byte array) = Sha256Hash(byteArrayToString (SHA256.HashData(bytes).AsSpan()))

    static member private UploadParameters correlationId fileVersions =
        let parameters = GetUploadMetadataForFilesParameters()
        parameters.OwnerId <- "22222222-2222-2222-2222-222222222222"
        parameters.OwnerName <- "owner"
        parameters.OrganizationId <- "33333333-3333-3333-3333-333333333333"
        parameters.OrganizationName <- "org"
        parameters.RepositoryId <- "11111111-1111-1111-1111-111111111111"
        parameters.RepositoryName <- "repo"
        parameters.CorrelationId <- correlationId
        parameters.FileVersions <- fileVersions
        parameters

    static member private Decision correlationId sessionId operationId =
        let session =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                RepositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
                LastOperationId = Some operationId
            }

        let decision = { Session = session; OperationId = operationId; Events = []; WasIdempotentReplay = false; Message = "accepted" }

        Task.FromResult(Ok(GraceReturnValue.Create decision correlationId))

    static member private ClaimDecision correlationId sessionId operationId claimedRanges =
        let session =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                RepositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
                ClaimedReuseRanges = claimedRanges
                LastOperationId = Some operationId
            }

        let decision = { Session = session; OperationId = operationId; Events = []; WasIdempotentReplay = false; Message = "accepted" }

        Task.FromResult(Ok(GraceReturnValue.Create decision correlationId))

    static member private DiscoveryPolicy minimumReuseRunLength =
        {
            MaxKeyChunkAddresses = 256
            MaxCandidateWindowsPerKeyChunk = 4
            MaxWindowChunks = 256
            MaxResponseProtectedChunks = 16384
            ResponseTtlSeconds = 300
            MinimumAcceptedReuseRunLength = minimumReuseRunLength
            PositiveCandidatesEnabled = true
            EmptyResponseMeansAbsent = false
            IsAuthoritative = false
        }

    static member private EmptyDiscovery correlationId requested =
        {
            RequestedKeyChunkCount = requested
            AcceptedKeyChunkCount = requested
            Policy = ManifestUploadSdkTests.DiscoveryPolicy 8
            CandidateContentBlocks = Array.empty
            IsPartial = false
            Message = "empty discovery is non-authoritative"
        }
        |> fun discovery -> Task.FromResult(Ok(GraceReturnValue.Create discovery correlationId))

    static member private ProtectedChunkAddress storagePoolId chunkAddress =
        let preimage = $"grace.dedupe-index.v1.protected-window\n{storagePoolId}\n{chunkAddress}"
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(preimage))
        $"protected-sha256:{Convert.ToHexString(hash).ToLowerInvariant()}"

    static member private CreateRequest tempPath (fileVersion: FileVersion) correlationId : ManifestUpload.ManifestUploadRequest =
        {
            OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            OwnerName = "owner"
            OrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            OrganizationName = "org"
            RepositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            RepositoryName = "repo"
            AuthorizedScope = fileVersion.RelativePath
            FileVersion = fileVersion
            LocalFilePath = tempPath
            CorrelationId = correlationId
            PlannerOptions = { LocalPlanner.Options.Default with EligibilityPolicy = ManifestUploadSdkTests.BinaryPolicy 1024L }
        }

    [<Test>]
    member _.ContentBlockUploadConflictDetectionOnlyAcceptsAzureBlobAlreadyExistsConflict() =
        let existingBlob = RequestFailedException(409, "The specified blob already exists.", "BlobAlreadyExists", null)
        let leaseConflict = RequestFailedException(409, "There is a lease conflict.", "LeaseAlreadyPresent", null)
        let forbidden = RequestFailedException(403, "Forbidden.", "BlobAlreadyExists", null)

        Assert.That(Storage.isExistingContentBlockUploadConflict existingBlob, Is.True)
        Assert.That(Storage.isExistingContentBlockUploadConflict leaseConflict, Is.False)
        Assert.That(Storage.isExistingContentBlockUploadConflict forbidden, Is.False)

    [<Test>]
    member _.WholeFileLocalObjectCacheNameIncludesBlake3() =
        let fileVersion =
            FileVersion.CreateWithHashes
                (RelativePath "src/cache/whole-file.txt")
                (Sha256Hash "sha256-value")
                (Blake3Hash "blake3-value")
                String.Empty
                false
                10L

        Assert.That(fileVersion.GetObjectFileName, Is.EqualTo("whole-file_sha256-value.txt"))
        Assert.That(Storage.getLocalObjectCacheFileName fileVersion, Is.EqualTo("whole-file_sha256-value_blake3-value.txt"))

    [<Test>]
    member _.WholeFileLocalObjectCacheNameIncludesBlake3ForExtensionlessFiles() =
        let fileVersion =
            FileVersion.CreateWithHashes (RelativePath "Dockerfile") (Sha256Hash "sha256-value") (Blake3Hash "blake3-value") String.Empty false 10L

        Assert.That(fileVersion.GetObjectFileName, Is.EqualTo("Dockerfile_sha256-value"))
        Assert.That(Storage.getLocalObjectCacheFileName fileVersion, Is.EqualTo("Dockerfile_sha256-value_blake3-value"))

    [<Test>]
    member _.WholeFileLocalObjectCacheNameKeepsLegacyShaOnlyNameWithoutBlake3() =
        let fileVersion = FileVersion.Create (RelativePath "src/cache/legacy-file.txt") (Sha256Hash "sha256-value") String.Empty false 10L

        Assert.That(Storage.getLocalObjectCacheFileName fileVersion, Is.EqualTo(fileVersion.GetObjectFileName))

    [<Test>]
    member _.UploadMetadataMatchingIncludesBlake3WhenPathAndShaCollide() =
        let firstFileVersion =
            FileVersion.CreateWithHashes (RelativePath "src/cache/collision.txt") (Sha256Hash "same-sha256") (Blake3Hash "first-blake3") String.Empty false 10L

        let secondFileVersion =
            FileVersion.CreateWithHashes (RelativePath "src/cache/collision.txt") (Sha256Hash "same-sha256") (Blake3Hash "second-blake3") String.Empty false 10L

        let uploadMetadata: Parameters.Storage.UploadMetadata =
            {
                RelativePath = secondFileVersion.RelativePath
                BlobUriWithSasToken = Uri("https://example.test/upload")
                Sha256Hash = secondFileVersion.Sha256Hash
                Blake3Hash = secondFileVersion.Blake3Hash
                ContentReference = FileContentReference.WholeFileContent
            }

        let matchedFileVersion =
            Services.findFileVersionForUploadMetadata
                [|
                    firstFileVersion
                    secondFileVersion
                |]
                uploadMetadata

        Assert.That(matchedFileVersion, Is.EqualTo(secondFileVersion))

    [<Test>]
    member _.ManifestUploadFlowStartsUploadsConfirmsAndFinalizesNewBlocksOnly() =
        task {
            let payload = ManifestUploadSdkTests.PseudoRandomBytes 220000
            payload[0] <- 0uy

            let tempPath = Path.Combine(Path.GetTempPath(), $"grace-manifest-upload-{Guid.NewGuid():N}.bin")
            let correlationId = "corr-sdk-manifest-upload"
            let sessionIds = ResizeArray<UploadSessionId>()
            let calls = ResizeArray<string>()
            let uploadedBlocks = Dictionary<ContentBlockAddress, byte array>()
            let confirmedBlocks = Dictionary<ContentBlockAddress, byte array>()
            let mutable finalizedManifest = None

            try
                File.WriteAllBytes(tempPath, payload)

                let fileVersion = FileVersion.Create "large.bin" (ManifestUploadSdkTests.ComputeSha256Hash payload) String.Empty true (int64 payload.Length)

                let client: ManifestUpload.ManifestUploadClient =
                    {
                        StartSession =
                            fun parameters ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                calls.Add("start")
                                sessionIds.Add(parameters.UploadSessionId)
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        DiscoverContentBlocks =
                            fun parameters ->
                                calls.Add("discover")
                                ManifestUploadSdkTests.EmptyDiscovery correlationId parameters.KeyChunkAddresses.Length
                        IssueDedupeDiscovery =
                            fun parameters ->
                                calls.Add("issue-discovery")
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        ClaimReuseRanges =
                            fun parameters ->
                                calls.Add("claim")
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        RegisterBlockUpload =
                            fun parameters ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                calls.Add($"register:{parameters.ContentBlockAddress}")
                                Assert.That(parameters.ExpectedPayloadLength, Is.GreaterThan(0L))
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        UploadContentBlock =
                            fun parameters payload ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                calls.Add($"upload:{parameters.ContentBlockAddress}")
                                uploadedBlocks[parameters.ContentBlockAddress] <- payload

                                let placement =
                                    { ObjectKey = StorageKeys.contentBlockObjectKey parameters.ContentBlockAddress; ETag = Some $"etag-{uploadedBlocks.Count}" }

                                Task.FromResult(Ok(GraceReturnValue.Create placement correlationId))
                        ConfirmBlockUploaded =
                            fun parameters ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                calls.Add($"confirm:{parameters.ContentBlockAddress}")
                                confirmedBlocks[parameters.ContentBlockAddress] <- parameters.Payload
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        FinalizeManifest =
                            fun parameters ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                calls.Add("finalize")
                                finalizedManifest <- Some parameters.Manifest
                                Assert.That(parameters.BlockPayloads, Is.Empty)
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                    }

                let! result = ManifestUpload.uploadFileWithClient client (ManifestUploadSdkTests.CreateRequest tempPath fileVersion correlationId)

                match result with
                | Error error -> Assert.Fail(error.Error)
                | Ok returnValue ->
                    let uploadResult = returnValue.ReturnValue
                    Assert.That(uploadResult.UsedManifestUpload, Is.True)
                    Assert.That(uploadResult.UploadedBlockCount, Is.EqualTo(uploadedBlocks.Count))
                    Assert.That(uploadResult.FileVersion.ContentReference.ReferenceType, Is.EqualTo(FileContentReferenceType.FileManifest))
                    Assert.That(uploadResult.Manifest, Is.EqualTo(finalizedManifest))
                    Assert.That(uploadResult.FileVersion.Blake3Hash, Is.EqualTo(Blake3Hash(ContentAddress.computeBlake3Hex payload)))
                    Assert.That(sessionIds |> Seq.distinct |> Seq.length, Is.EqualTo(1))
                    Assert.That(calls[0], Is.EqualTo("start"))
                    Assert.That(calls[calls.Count - 1], Is.EqualTo("finalize"))

                    Assert.That(calls, Does.Contain("discover"))
                    Assert.That(calls, Does.Not.Contain("issue-discovery"))
                    Assert.That(calls, Does.Not.Contain("claim"))

                    Assert.That(uploadedBlocks.Keys, Is.EquivalentTo(confirmedBlocks.Keys))
            finally
                if File.Exists(tempPath) then File.Delete(tempPath)
        }

    [<Test>]
    member _.SmallSourceFileUsesWholeFileContentWithoutStartingManifestSession() =
        task {
            let payload = Encoding.UTF8.GetBytes("module Tiny\n\nlet answer = 42\n")
            let tempPath = Path.Combine(Path.GetTempPath(), $"grace-manifest-small-source-{Guid.NewGuid():N}.fs")
            let correlationId = "corr-sdk-manifest-small-source"

            try
                File.WriteAllBytes(tempPath, payload)

                let fileVersion = FileVersion.Create "Tiny.fs" (ManifestUploadSdkTests.ComputeSha256Hash payload) String.Empty false (int64 payload.Length)

                let client: ManifestUpload.ManifestUploadClient =
                    {
                        StartSession = fun _ -> failwith "Small source files must not start manifest upload sessions."
                        DiscoverContentBlocks = fun _ -> failwith "Small source files must not discover manifest blocks."
                        IssueDedupeDiscovery = fun _ -> failwith "Small source files must not issue dedupe discovery."
                        ClaimReuseRanges = fun _ -> failwith "Small source files must not claim manifest ranges."
                        RegisterBlockUpload = fun _ -> failwith "Small source files must not register manifest blocks."
                        UploadContentBlock = fun _ _ -> failwith "Small source files must not upload manifest blocks."
                        ConfirmBlockUploaded = fun _ -> failwith "Small source files must not confirm manifest blocks."
                        FinalizeManifest = fun _ -> failwith "Small source files must not finalize manifests."
                    }

                let! result = ManifestUpload.uploadFileWithClient client (ManifestUploadSdkTests.CreateRequest tempPath fileVersion correlationId)

                match result with
                | Error error -> Assert.Fail(error.Error)
                | Ok returnValue ->
                    let uploadResult = returnValue.ReturnValue
                    Assert.That(uploadResult.UsedManifestUpload, Is.False)
                    Assert.That(uploadResult.UploadedBlockCount, Is.EqualTo(0))
                    Assert.That(uploadResult.FileVersion.Blake3Hash, Is.EqualTo(fileVersion.Blake3Hash))
                    Assert.That(uploadResult.FileVersion.ContentReference.ReferenceType, Is.EqualTo(FileContentReferenceType.WholeFileContent))
                    Assert.That(uploadResult.Manifest, Is.EqualTo(None))
                    Assert.That(uploadResult.UploadSessionId, Is.EqualTo(None))
            finally
                if File.Exists(tempPath) then File.Delete(tempPath)
        }

    [<Test>]
    member _.LargeCompressedTextUsesManifestUploadByDefaultPolicy() =
        task {
            let payload =
                ManifestUploadSdkTests.TextLikePseudoRandomBytes(
                    int ManifestEligibilityPolicy.Default.ThresholdBytes
                    + 65536
                )

            let tempPath = Path.Combine(Path.GetTempPath(), $"grace-manifest-large-text-{Guid.NewGuid():N}.txt")
            let correlationId = "corr-sdk-manifest-large-text"
            let uploadedBlocks = Dictionary<ContentBlockAddress, byte array>()

            try
                File.WriteAllBytes(tempPath, payload)

                let fileVersion = FileVersion.Create "large.txt" (ManifestUploadSdkTests.ComputeSha256Hash payload) String.Empty false (int64 payload.Length)

                let client: ManifestUpload.ManifestUploadClient =
                    {
                        StartSession = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        DiscoverContentBlocks = fun parameters -> ManifestUploadSdkTests.EmptyDiscovery correlationId parameters.KeyChunkAddresses.Length
                        IssueDedupeDiscovery = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        ClaimReuseRanges = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        RegisterBlockUpload = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        UploadContentBlock =
                            fun parameters payload ->
                                uploadedBlocks[parameters.ContentBlockAddress] <- payload

                                let placement =
                                    { ObjectKey = StorageKeys.contentBlockObjectKey parameters.ContentBlockAddress; ETag = Some $"etag-{uploadedBlocks.Count}" }

                                Task.FromResult(Ok(GraceReturnValue.Create placement correlationId))
                        ConfirmBlockUploaded = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        FinalizeManifest = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                    }

                let! result = ManifestUpload.uploadFileWithClient client (ManifestUploadSdkTests.CreateRequest tempPath fileVersion correlationId)

                match result with
                | Error error -> Assert.Fail(error.Error)
                | Ok returnValue ->
                    let uploadResult = returnValue.ReturnValue
                    Assert.That(uploadResult.UsedManifestUpload, Is.True)
                    Assert.That(uploadResult.UploadedBlockCount, Is.EqualTo(uploadedBlocks.Count))
                    Assert.That(uploadResult.FileVersion.ContentReference.ReferenceType, Is.EqualTo(FileContentReferenceType.FileManifest))
                    Assert.That(uploadResult.Manifest, Is.Not.EqualTo(None))
                    Assert.That(uploadResult.UploadSessionId, Is.Not.EqualTo(None))
            finally
                if File.Exists(tempPath) then File.Delete(tempPath)
        }

    [<Test>]
    member _.UploadFilesToObjectStorageFallsBackOnlyForNonManifestResults() =
        task {
            let correlationId = "corr-cli-manifest-fallback-routing"
            let manifestFile = FileVersion.Create "large.bin" (Sha256Hash "manifest-sha") String.Empty true 2048L
            let ineligibleFile = FileVersion.Create "small.fs" (Sha256Hash "ineligible-sha") String.Empty false 128L
            let manifestAttempts = ResizeArray<string>()
            let wholeFileFallbacks = ResizeArray<FileVersion array>()

            let manifestUpload (fileVersion: FileVersion) : Task<GraceResult<ManifestUpload.ManifestUploadResult>> =
                task {
                    manifestAttempts.Add(fileVersion.RelativePath)

                    let result: ManifestUpload.ManifestUploadResult =
                        {
                            FileVersion = fileVersion
                            Manifest = None
                            UploadSessionId = None
                            UploadedBlockCount = if fileVersion.RelativePath = manifestFile.RelativePath then 1 else 0
                            UsedManifestUpload = fileVersion.RelativePath = manifestFile.RelativePath
                        }

                    return Ok(GraceReturnValue.Create result correlationId)
                }

            let wholeFileUpload (parameters: GetUploadMetadataForFilesParameters) =
                task {
                    wholeFileFallbacks.Add(parameters.FileVersions)
                    return Ok(GraceReturnValue.Create parameters.FileVersions correlationId)
                }

            let parameters = ManifestUploadSdkTests.UploadParameters correlationId [| manifestFile; ineligibleFile |]

            let! result = Services.uploadFilesToObjectStorageWithClients manifestUpload wholeFileUpload parameters

            match result with
            | Error error -> Assert.Fail($"{error.Error}{Environment.NewLine}{serialize error.Properties}")
            | Ok returnValue ->
                Assert.That(returnValue.ReturnValue, Is.EquivalentTo([| manifestFile; ineligibleFile |]))

                Assert.That(
                    manifestAttempts,
                    Is.EquivalentTo(
                        [|
                            manifestFile.RelativePath
                            ineligibleFile.RelativePath
                        |]
                    )
                )

                Assert.That(wholeFileFallbacks.Count, Is.EqualTo(1))
                Assert.That(wholeFileFallbacks[0].Length, Is.EqualTo(1))
                Assert.That(wholeFileFallbacks[0][0], Is.EqualTo(ineligibleFile))
        }

    [<Test>]
    member _.ManifestUploadClaimsValidDiscoveryCandidateAndSkipsUploadingClaimedBlock() =
        task {
            let payload = ManifestUploadSdkTests.PseudoRandomBytes 220000
            payload[0] <- 1uy

            let tempPath = Path.Combine(Path.GetTempPath(), $"grace-manifest-upload-reuse-{Guid.NewGuid():N}.bin")
            let correlationId = "corr-sdk-manifest-upload-reuse"
            let uploadedBlocks = Dictionary<ContentBlockAddress, byte array>()
            let confirmedBlocks = Dictionary<ContentBlockAddress, byte array>()
            let claimedHints = ResizeArray<ContentBlockReuseRangeHint>()

            try
                File.WriteAllBytes(tempPath, payload)

                let fileVersion =
                    FileVersion.Create "reuse-large.bin" (ManifestUploadSdkTests.ComputeSha256Hash payload) String.Empty true (int64 payload.Length)

                let request = ManifestUploadSdkTests.CreateRequest tempPath fileVersion correlationId
                let plan = LocalPlanner.analyzeFile request.PlannerOptions tempPath
                let claimedBlock = plan.Blocks[0]
                let storagePoolId = StoragePoolId Constants.DefaultStoragePoolId
                let protectedKeyChunkAddress = ManifestUploadSdkTests.ProtectedChunkAddress storagePoolId claimedBlock.KeyChunkAddress

                Assert.That(protectedKeyChunkAddress, Does.StartWith("protected-sha256:"))
                Assert.That(protectedKeyChunkAddress, Is.Not.EqualTo($"{storagePoolId}|{claimedBlock.KeyChunkAddress}"))

                let client: ManifestUpload.ManifestUploadClient =
                    {
                        StartSession = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        DiscoverContentBlocks =
                            fun parameters ->
                                Assert.That(parameters.KeyChunkAddresses, Does.Contain(claimedBlock.KeyChunkAddress))

                                let candidate =
                                    {
                                        StoragePoolId = storagePoolId
                                        ManifestAddress = ManifestAddress "manifest-reuse-candidate"
                                        ContentBlockAddress = claimedBlock.Address
                                        OrdinalStart = 0
                                        OrdinalCount = 8
                                        MetadataVersion = 7L
                                        MatchingKeyChunkCount = 1
                                        ProtectedChunkAddresses = [| protectedKeyChunkAddress |]
                                    }

                                let discovery =
                                    {
                                        RequestedKeyChunkCount = parameters.KeyChunkAddresses.Length
                                        AcceptedKeyChunkCount = parameters.KeyChunkAddresses.Length
                                        Policy = ManifestUploadSdkTests.DiscoveryPolicy 8
                                        CandidateContentBlocks = [| candidate |]
                                        IsPartial = false
                                        Message = "candidate"
                                    }

                                Task.FromResult(Ok(GraceReturnValue.Create discovery correlationId))
                        IssueDedupeDiscovery =
                            fun parameters ->
                                Assert.That(parameters.Hints, Has.Length.EqualTo(1))
                                Assert.That(parameters.Hints[0].ContentBlockAddress, Is.EqualTo(claimedBlock.Address))
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        ClaimReuseRanges =
                            fun parameters ->
                                Assert.That(parameters.Hints, Has.Length.EqualTo(1))
                                claimedHints.AddRange(parameters.Hints)

                                let claimedRange =
                                    {
                                        StoragePoolId = storagePoolId
                                        ContentBlockAddress = claimedBlock.Address
                                        OrdinalStart = parameters.Hints[0].OrdinalStart
                                        OrdinalCount = parameters.Hints[0].OrdinalCount
                                        PhysicalOffset = 0L
                                        PhysicalLength = claimedBlock.Size
                                        MetadataVersion = parameters.Hints[0].MetadataVersion
                                        ClaimedAt = getCurrentInstant ()
                                    }

                                ManifestUploadSdkTests.ClaimDecision correlationId parameters.UploadSessionId parameters.OperationId [| claimedRange |]
                        RegisterBlockUpload =
                            fun parameters ->
                                Assert.That(parameters.ContentBlockAddress, Is.Not.EqualTo(claimedBlock.Address))
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        UploadContentBlock =
                            fun parameters payload ->
                                Assert.That(parameters.ContentBlockAddress, Is.Not.EqualTo(claimedBlock.Address))
                                uploadedBlocks[parameters.ContentBlockAddress] <- payload

                                let placement =
                                    { ObjectKey = StorageKeys.contentBlockObjectKey parameters.ContentBlockAddress; ETag = Some $"etag-{uploadedBlocks.Count}" }

                                Task.FromResult(Ok(GraceReturnValue.Create placement correlationId))
                        ConfirmBlockUploaded =
                            fun parameters ->
                                Assert.That(parameters.ContentBlockAddress, Is.Not.EqualTo(claimedBlock.Address))
                                confirmedBlocks[parameters.ContentBlockAddress] <- parameters.Payload
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        FinalizeManifest = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                    }

                let! result = ManifestUpload.uploadFileWithClient client request

                match result with
                | Error error -> Assert.Fail(error.Error)
                | Ok returnValue ->
                    Assert.That(returnValue.ReturnValue.UsedManifestUpload, Is.True)
                    Assert.That(claimedHints, Has.Count.EqualTo(1))
                    Assert.That(uploadedBlocks.ContainsKey claimedBlock.Address, Is.False)
                    Assert.That(confirmedBlocks.ContainsKey claimedBlock.Address, Is.False)
                    Assert.That(uploadedBlocks.Count, Is.EqualTo(plan.ContentBlockUploads.Length - 1))
                    Assert.That(returnValue.ReturnValue.UploadedBlockCount, Is.EqualTo(uploadedBlocks.Count))
            finally
                if File.Exists(tempPath) then File.Delete(tempPath)
        }

    [<Test>]
    member _.ManifestUploadUploadsBlockWhenReuseClaimCoversOnlyPartialWindow() =
        task {
            let payload = ManifestUploadSdkTests.PseudoRandomBytes 220000
            payload[0] <- 6uy

            let tempPath = Path.Combine(Path.GetTempPath(), $"grace-manifest-upload-partial-reuse-{Guid.NewGuid():N}.bin")
            let correlationId = "corr-sdk-manifest-upload-partial-reuse"
            let uploadedBlocks = Dictionary<ContentBlockAddress, byte array>()
            let confirmedBlocks = Dictionary<ContentBlockAddress, byte array>()
            let mutable attemptedClaim = false

            try
                File.WriteAllBytes(tempPath, payload)

                let fileVersion =
                    FileVersion.Create "partial-reuse-large.bin" (ManifestUploadSdkTests.ComputeSha256Hash payload) String.Empty true (int64 payload.Length)

                let request = ManifestUploadSdkTests.CreateRequest tempPath fileVersion correlationId
                let plan = LocalPlanner.analyzeFile request.PlannerOptions tempPath
                let partiallyClaimedBlock = plan.Blocks[0]
                let storagePoolId = StoragePoolId Constants.DefaultStoragePoolId

                let client: ManifestUpload.ManifestUploadClient =
                    {
                        StartSession = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        DiscoverContentBlocks =
                            fun parameters ->
                                let candidate =
                                    {
                                        StoragePoolId = storagePoolId
                                        ManifestAddress = ManifestAddress "manifest-partial-candidate"
                                        ContentBlockAddress = partiallyClaimedBlock.Address
                                        OrdinalStart = 0
                                        OrdinalCount = 8
                                        MetadataVersion = 7L
                                        MatchingKeyChunkCount = 1
                                        ProtectedChunkAddresses =
                                            [|
                                                ManifestUploadSdkTests.ProtectedChunkAddress storagePoolId partiallyClaimedBlock.KeyChunkAddress
                                            |]
                                    }

                                let discovery =
                                    {
                                        RequestedKeyChunkCount = parameters.KeyChunkAddresses.Length
                                        AcceptedKeyChunkCount = parameters.KeyChunkAddresses.Length
                                        Policy = ManifestUploadSdkTests.DiscoveryPolicy 8
                                        CandidateContentBlocks = [| candidate |]
                                        IsPartial = false
                                        Message = "partial candidate"
                                    }

                                Task.FromResult(Ok(GraceReturnValue.Create discovery correlationId))
                        IssueDedupeDiscovery = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        ClaimReuseRanges =
                            fun parameters ->
                                attemptedClaim <- true

                                let partialRange =
                                    {
                                        StoragePoolId = storagePoolId
                                        ContentBlockAddress = partiallyClaimedBlock.Address
                                        OrdinalStart = parameters.Hints[0].OrdinalStart
                                        OrdinalCount = parameters.Hints[0].OrdinalCount
                                        PhysicalOffset = 0L
                                        PhysicalLength = partiallyClaimedBlock.Size - 1L
                                        MetadataVersion = parameters.Hints[0].MetadataVersion
                                        ClaimedAt = getCurrentInstant ()
                                    }

                                ManifestUploadSdkTests.ClaimDecision correlationId parameters.UploadSessionId parameters.OperationId [| partialRange |]
                        RegisterBlockUpload = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        UploadContentBlock =
                            fun parameters payload ->
                                uploadedBlocks[parameters.ContentBlockAddress] <- payload

                                let placement =
                                    { ObjectKey = StorageKeys.contentBlockObjectKey parameters.ContentBlockAddress; ETag = Some $"etag-{uploadedBlocks.Count}" }

                                Task.FromResult(Ok(GraceReturnValue.Create placement correlationId))
                        ConfirmBlockUploaded =
                            fun parameters ->
                                confirmedBlocks[parameters.ContentBlockAddress] <- parameters.Payload
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        FinalizeManifest = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                    }

                let! result = ManifestUpload.uploadFileWithClient client request

                match result with
                | Error error -> Assert.Fail(error.Error)
                | Ok returnValue ->
                    Assert.That(returnValue.ReturnValue.UsedManifestUpload, Is.True)
                    Assert.That(attemptedClaim, Is.True)
                    Assert.That(uploadedBlocks.ContainsKey partiallyClaimedBlock.Address, Is.True)
                    Assert.That(confirmedBlocks.ContainsKey partiallyClaimedBlock.Address, Is.True)
                    Assert.That(uploadedBlocks.Count, Is.EqualTo(plan.ContentBlockUploads.Length))
                    Assert.That(returnValue.ReturnValue.UploadedBlockCount, Is.EqualTo(uploadedBlocks.Count))
            finally
                if File.Exists(tempPath) then File.Delete(tempPath)
        }

    [<Test>]
    member _.ManifestUploadTreatsDiscoveryFailureAsNonAuthoritativeAndUploadsBlocks() =
        task {
            let payload = ManifestUploadSdkTests.PseudoRandomBytes 220000
            payload[0] <- 3uy

            let tempPath = Path.Combine(Path.GetTempPath(), $"grace-manifest-upload-discovery-failure-{Guid.NewGuid():N}.bin")
            let correlationId = "corr-sdk-manifest-upload-discovery-failure"
            let uploadedBlocks = Dictionary<ContentBlockAddress, byte array>()
            let mutable issuedDiscovery = false
            let mutable claimedReuse = false

            try
                File.WriteAllBytes(tempPath, payload)

                let fileVersion =
                    FileVersion.Create "discovery-failure-large.bin" (ManifestUploadSdkTests.ComputeSha256Hash payload) String.Empty true (int64 payload.Length)

                let request = ManifestUploadSdkTests.CreateRequest tempPath fileVersion correlationId
                let plan = LocalPlanner.analyzeFile request.PlannerOptions tempPath

                let client: ManifestUpload.ManifestUploadClient =
                    {
                        StartSession = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        DiscoverContentBlocks = fun _parameters -> Task.FromResult(Error(GraceError.Create "discovery unavailable" correlationId))
                        IssueDedupeDiscovery =
                            fun parameters ->
                                issuedDiscovery <- true
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        ClaimReuseRanges =
                            fun parameters ->
                                claimedReuse <- true
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        RegisterBlockUpload = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        UploadContentBlock =
                            fun parameters payload ->
                                uploadedBlocks[parameters.ContentBlockAddress] <- payload

                                let placement =
                                    { ObjectKey = StorageKeys.contentBlockObjectKey parameters.ContentBlockAddress; ETag = Some $"etag-{uploadedBlocks.Count}" }

                                Task.FromResult(Ok(GraceReturnValue.Create placement correlationId))
                        ConfirmBlockUploaded = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        FinalizeManifest = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                    }

                let! result = ManifestUpload.uploadFileWithClient client request

                match result with
                | Error error -> Assert.Fail(error.Error)
                | Ok returnValue ->
                    Assert.That(returnValue.ReturnValue.UsedManifestUpload, Is.True)
                    Assert.That(issuedDiscovery, Is.False)
                    Assert.That(claimedReuse, Is.False)
                    Assert.That(uploadedBlocks.Count, Is.EqualTo(plan.ContentBlockUploads.Length))
            finally
                if File.Exists(tempPath) then File.Delete(tempPath)
        }

    [<Test>]
    member _.ManifestUploadDuplicatesDiscoveryCandidatesBelowMinimumReuseRunLength() =
        task {
            let payload = ManifestUploadSdkTests.PseudoRandomBytes 220000
            payload[0] <- 4uy

            let tempPath = Path.Combine(Path.GetTempPath(), $"grace-manifest-upload-short-reuse-{Guid.NewGuid():N}.bin")
            let correlationId = "corr-sdk-manifest-upload-short-reuse"
            let uploadedBlocks = Dictionary<ContentBlockAddress, byte array>()
            let mutable issuedDiscovery = false
            let mutable claimedReuse = false

            try
                File.WriteAllBytes(tempPath, payload)

                let fileVersion =
                    FileVersion.Create "short-reuse-large.bin" (ManifestUploadSdkTests.ComputeSha256Hash payload) String.Empty true (int64 payload.Length)

                let request = ManifestUploadSdkTests.CreateRequest tempPath fileVersion correlationId
                let plan = LocalPlanner.analyzeFile request.PlannerOptions tempPath
                let candidateBlock = plan.Blocks[0]
                let storagePoolId = StoragePoolId Constants.DefaultStoragePoolId

                let client: ManifestUpload.ManifestUploadClient =
                    {
                        StartSession = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        DiscoverContentBlocks =
                            fun parameters ->
                                let candidate =
                                    {
                                        StoragePoolId = storagePoolId
                                        ManifestAddress = ManifestAddress "manifest-short-candidate"
                                        ContentBlockAddress = candidateBlock.Address
                                        OrdinalStart = 0
                                        OrdinalCount = 7
                                        MetadataVersion = 7L
                                        MatchingKeyChunkCount = 1
                                        ProtectedChunkAddresses =
                                            [|
                                                ManifestUploadSdkTests.ProtectedChunkAddress storagePoolId candidateBlock.KeyChunkAddress
                                            |]
                                    }

                                let discovery =
                                    {
                                        RequestedKeyChunkCount = parameters.KeyChunkAddresses.Length
                                        AcceptedKeyChunkCount = parameters.KeyChunkAddresses.Length
                                        Policy = ManifestUploadSdkTests.DiscoveryPolicy 8
                                        CandidateContentBlocks = [| candidate |]
                                        IsPartial = false
                                        Message = "short candidate"
                                    }

                                Task.FromResult(Ok(GraceReturnValue.Create discovery correlationId))
                        IssueDedupeDiscovery =
                            fun parameters ->
                                issuedDiscovery <- true
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        ClaimReuseRanges =
                            fun parameters ->
                                claimedReuse <- true
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        RegisterBlockUpload = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        UploadContentBlock =
                            fun parameters payload ->
                                uploadedBlocks[parameters.ContentBlockAddress] <- payload

                                let placement =
                                    { ObjectKey = StorageKeys.contentBlockObjectKey parameters.ContentBlockAddress; ETag = Some $"etag-{uploadedBlocks.Count}" }

                                Task.FromResult(Ok(GraceReturnValue.Create placement correlationId))
                        ConfirmBlockUploaded = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        FinalizeManifest = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                    }

                let! result = ManifestUpload.uploadFileWithClient client request

                match result with
                | Error error -> Assert.Fail(error.Error)
                | Ok returnValue ->
                    Assert.That(returnValue.ReturnValue.UsedManifestUpload, Is.True)
                    Assert.That(issuedDiscovery, Is.False)
                    Assert.That(claimedReuse, Is.False)
                    Assert.That(uploadedBlocks.ContainsKey candidateBlock.Address, Is.True)
                    Assert.That(uploadedBlocks.Count, Is.EqualTo(plan.ContentBlockUploads.Length))
            finally
                if File.Exists(tempPath) then File.Delete(tempPath)
        }

    [<Test>]
    member _.ManifestUploadFallsBackToUploadingBlocksWhenReuseClaimIsStale() =
        task {
            let payload = ManifestUploadSdkTests.PseudoRandomBytes 220000
            payload[0] <- 5uy

            let tempPath = Path.Combine(Path.GetTempPath(), $"grace-manifest-upload-stale-reuse-{Guid.NewGuid():N}.bin")
            let correlationId = "corr-sdk-manifest-upload-stale-reuse"
            let uploadedBlocks = Dictionary<ContentBlockAddress, byte array>()
            let mutable issuedDiscovery = false
            let mutable attemptedClaim = false

            try
                File.WriteAllBytes(tempPath, payload)

                let fileVersion =
                    FileVersion.Create "stale-reuse-large.bin" (ManifestUploadSdkTests.ComputeSha256Hash payload) String.Empty true (int64 payload.Length)

                let request = ManifestUploadSdkTests.CreateRequest tempPath fileVersion correlationId
                let plan = LocalPlanner.analyzeFile request.PlannerOptions tempPath
                let candidateBlock = plan.Blocks[0]
                let storagePoolId = StoragePoolId Constants.DefaultStoragePoolId

                let client: ManifestUpload.ManifestUploadClient =
                    {
                        StartSession = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        DiscoverContentBlocks =
                            fun parameters ->
                                let candidate =
                                    {
                                        StoragePoolId = storagePoolId
                                        ManifestAddress = ManifestAddress "manifest-stale-candidate"
                                        ContentBlockAddress = candidateBlock.Address
                                        OrdinalStart = 0
                                        OrdinalCount = 8
                                        MetadataVersion = 6L
                                        MatchingKeyChunkCount = 1
                                        ProtectedChunkAddresses =
                                            [|
                                                ManifestUploadSdkTests.ProtectedChunkAddress storagePoolId candidateBlock.KeyChunkAddress
                                            |]
                                    }

                                let discovery =
                                    {
                                        RequestedKeyChunkCount = parameters.KeyChunkAddresses.Length
                                        AcceptedKeyChunkCount = parameters.KeyChunkAddresses.Length
                                        Policy = ManifestUploadSdkTests.DiscoveryPolicy 8
                                        CandidateContentBlocks = [| candidate |]
                                        IsPartial = false
                                        Message = "stale candidate"
                                    }

                                Task.FromResult(Ok(GraceReturnValue.Create discovery correlationId))
                        IssueDedupeDiscovery =
                            fun parameters ->
                                issuedDiscovery <- true
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        ClaimReuseRanges =
                            fun _parameters ->
                                attemptedClaim <- true
                                Task.FromResult(Error(GraceError.Create "stale reuse range hint rejected" correlationId))
                        RegisterBlockUpload = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        UploadContentBlock =
                            fun parameters payload ->
                                uploadedBlocks[parameters.ContentBlockAddress] <- payload

                                let placement =
                                    { ObjectKey = StorageKeys.contentBlockObjectKey parameters.ContentBlockAddress; ETag = Some $"etag-{uploadedBlocks.Count}" }

                                Task.FromResult(Ok(GraceReturnValue.Create placement correlationId))
                        ConfirmBlockUploaded = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        FinalizeManifest = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                    }

                let! result = ManifestUpload.uploadFileWithClient client request

                match result with
                | Error error -> Assert.Fail(error.Error)
                | Ok returnValue ->
                    Assert.That(returnValue.ReturnValue.UsedManifestUpload, Is.True)
                    Assert.That(issuedDiscovery, Is.True)
                    Assert.That(attemptedClaim, Is.True)
                    Assert.That(uploadedBlocks.ContainsKey candidateBlock.Address, Is.True)
                    Assert.That(uploadedBlocks.Count, Is.EqualTo(plan.ContentBlockUploads.Length))
            finally
                if File.Exists(tempPath) then File.Delete(tempPath)
        }

    [<Test>]
    member _.ManifestUploadCanConfirmExistingContentBlockPlacementWithoutNewEtag() =
        task {
            let payload = ManifestUploadSdkTests.PseudoRandomBytes 220000
            payload[0] <- 2uy

            let tempPath = Path.Combine(Path.GetTempPath(), $"grace-manifest-upload-existing-block-{Guid.NewGuid():N}.bin")
            let correlationId = "corr-sdk-manifest-upload-existing-block"
            let existingPlacements = ResizeArray<ContentBlockStoragePlacement>()
            let confirmedBlocks = Dictionary<ContentBlockAddress, ContentBlockStoragePlacement>()
            let mutable manifestBlockCount = 0

            try
                File.WriteAllBytes(tempPath, payload)

                let fileVersion =
                    FileVersion.Create "existing-block-large.bin" (ManifestUploadSdkTests.ComputeSha256Hash payload) String.Empty true (int64 payload.Length)

                let client: ManifestUpload.ManifestUploadClient =
                    {
                        StartSession =
                            fun parameters ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        DiscoverContentBlocks = fun parameters -> ManifestUploadSdkTests.EmptyDiscovery correlationId parameters.KeyChunkAddresses.Length
                        IssueDedupeDiscovery = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        ClaimReuseRanges = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        RegisterBlockUpload =
                            fun parameters ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        UploadContentBlock =
                            fun parameters _payload ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))

                                let placement = { ObjectKey = StorageKeys.contentBlockObjectKey parameters.ContentBlockAddress; ETag = None }

                                existingPlacements.Add(placement)
                                Task.FromResult(Ok(GraceReturnValue.Create placement correlationId))
                        ConfirmBlockUploaded =
                            fun parameters ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                confirmedBlocks[parameters.ContentBlockAddress] <- parameters.StoragePlacement
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        FinalizeManifest =
                            fun parameters ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                manifestBlockCount <- parameters.Manifest.Blocks.Count
                                Assert.That(parameters.BlockPayloads, Is.Empty)
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                    }

                let! result = ManifestUpload.uploadFileWithClient client (ManifestUploadSdkTests.CreateRequest tempPath fileVersion correlationId)

                match result with
                | Error error -> Assert.Fail(error.Error)
                | Ok returnValue ->
                    Assert.That(returnValue.ReturnValue.UsedManifestUpload, Is.True)
                    Assert.That(existingPlacements, Is.Not.Empty)
                    Assert.That(confirmedBlocks.Values, Is.EquivalentTo(existingPlacements))
                    Assert.That(manifestBlockCount, Is.GreaterThanOrEqualTo(confirmedBlocks.Count))
            finally
                if File.Exists(tempPath) then File.Delete(tempPath)
        }

    [<Test>]
    member _.ManifestUploadRegistersEveryDuplicateBlockOccurrenceButUploadsPayloadOnce() =
        task {
            let payload = Array.zeroCreate<byte> (RabinChunking.MaximumChunkSize * 2)
            let tempPath = Path.Combine(Path.GetTempPath(), $"grace-manifest-upload-duplicates-{Guid.NewGuid():N}.bin")
            let correlationId = "corr-sdk-manifest-upload-duplicates"
            let registeredRanges = ResizeArray<ContentBlockAddress * int64 * int64>()
            let uploadedBlocks = Dictionary<ContentBlockAddress, byte array>()
            let confirmedBlocks = Dictionary<ContentBlockAddress, byte array>()
            let mutable manifestBlockCount = 0

            try
                File.WriteAllBytes(tempPath, payload)

                let fileVersion =
                    FileVersion.Create "duplicate-large.bin" (ManifestUploadSdkTests.ComputeSha256Hash payload) String.Empty true (int64 payload.Length)

                let client: ManifestUpload.ManifestUploadClient =
                    {
                        StartSession =
                            fun parameters ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        DiscoverContentBlocks = fun parameters -> ManifestUploadSdkTests.EmptyDiscovery correlationId parameters.KeyChunkAddresses.Length
                        IssueDedupeDiscovery = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        ClaimReuseRanges = fun parameters -> ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        RegisterBlockUpload =
                            fun parameters ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                registeredRanges.Add(parameters.ContentBlockAddress, parameters.LogicalOffset, parameters.LogicalLength)
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        UploadContentBlock =
                            fun parameters payload ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                uploadedBlocks[parameters.ContentBlockAddress] <- payload

                                let placement =
                                    { ObjectKey = StorageKeys.contentBlockObjectKey parameters.ContentBlockAddress; ETag = Some $"etag-{uploadedBlocks.Count}" }

                                Task.FromResult(Ok(GraceReturnValue.Create placement correlationId))
                        ConfirmBlockUploaded =
                            fun parameters ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                confirmedBlocks[parameters.ContentBlockAddress] <- parameters.Payload
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        FinalizeManifest =
                            fun parameters ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                manifestBlockCount <- parameters.Manifest.Blocks.Count
                                Assert.That(parameters.BlockPayloads, Is.Empty)
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                    }

                let! result = ManifestUpload.uploadFileWithClient client (ManifestUploadSdkTests.CreateRequest tempPath fileVersion correlationId)

                match result with
                | Error error -> Assert.Fail(error.Error)
                | Ok returnValue ->
                    Assert.That(returnValue.ReturnValue.UsedManifestUpload, Is.True)
                    Assert.That(manifestBlockCount, Is.GreaterThan(uploadedBlocks.Count))
                    Assert.That(registeredRanges, Has.Count.EqualTo(manifestBlockCount))
                    Assert.That(uploadedBlocks, Has.Count.EqualTo(1))
                    Assert.That(confirmedBlocks, Has.Count.EqualTo(1))

                    Assert.That(
                        registeredRanges
                        |> Seq.map (fun (_, offset, length) -> offset, length),
                        Is.EquivalentTo(
                            [|
                                0L, int64 RabinChunking.MaximumChunkSize
                                int64 RabinChunking.MaximumChunkSize, int64 RabinChunking.MaximumChunkSize
                            |]
                        )
                    )
            finally
                if File.Exists(tempPath) then File.Delete(tempPath)
        }

    [<Test>]
    member _.ManifestUploadsAreEnabledByDefaultAfterManifestDownloadReconstruction() =
        let previous = Environment.GetEnvironmentVariable(ManifestUpload.OptInEnvironmentVariable)

        try
            Environment.SetEnvironmentVariable(ManifestUpload.OptInEnvironmentVariable, null)
            Assert.That(ManifestUpload.isOptedIn (), Is.True)

            Environment.SetEnvironmentVariable(ManifestUpload.OptInEnvironmentVariable, "0")
            Assert.That(ManifestUpload.isOptedIn (), Is.True)

            Environment.SetEnvironmentVariable(ManifestUpload.OptInEnvironmentVariable, "1")
            Assert.That(ManifestUpload.isOptedIn (), Is.True)
        finally
            Environment.SetEnvironmentVariable(ManifestUpload.OptInEnvironmentVariable, previous)
