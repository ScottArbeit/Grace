namespace Grace.CLI.Tests

open Azure
open Grace.SDK
open Grace.Shared
open Grace.Shared.Parameters.Storage
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.ContentBlockMetadata
open Grace.Types.Types
open Grace.Types.UploadSession
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
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

    static member private BinaryPolicy thresholdBytes = { ManifestEligibilityPolicy.Default with ThresholdBytes = thresholdBytes; BinaryScanBytes = 16 }

    static member private ComputeSha256Hash(bytes: byte array) = Sha256Hash(byteArrayToString (SHA256.HashData(bytes).AsSpan()))

    static member private Decision correlationId sessionId operationId =
        let session =
            { UploadSessionDto.Default with
                UploadSessionId = sessionId
                RepositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
                LastOperationId = Some operationId
            }

        let decision = { Session = session; OperationId = operationId; Events = []; WasIdempotentReplay = false; Message = "accepted" }

        Task.FromResult(Ok(GraceReturnValue.Create decision correlationId))

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
                                    { ObjectKey = $"cas/content-blocks/{parameters.ContentBlockAddress}"; ETag = Some $"etag-{uploadedBlocks.Count}" }

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
                    Assert.That(sessionIds |> Seq.distinct |> Seq.length, Is.EqualTo(1))
                    Assert.That(calls[0], Is.EqualTo("start"))
                    Assert.That(calls[calls.Count - 1], Is.EqualTo("finalize"))

                    Assert.That(
                        calls
                        |> Seq.exists (fun call -> call.StartsWith("discover", StringComparison.OrdinalIgnoreCase)),
                        Is.False
                    )

                    Assert.That(uploadedBlocks.Keys, Is.EquivalentTo(confirmedBlocks.Keys))
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
                        RegisterBlockUpload =
                            fun parameters ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        UploadContentBlock =
                            fun parameters _payload ->
                                Assert.That(parameters.AuthorizedScope, Is.EqualTo(fileVersion.RelativePath))

                                let placement = { ObjectKey = $"cas/content-blocks/{parameters.ContentBlockAddress}"; ETag = None }

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
                                    { ObjectKey = $"cas/content-blocks/{parameters.ContentBlockAddress}"; ETag = Some $"etag-{uploadedBlocks.Count}" }

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
    member _.ManifestUploadsAreDisabledUnlessEnvironmentOptInIsOne() =
        let previous = Environment.GetEnvironmentVariable(ManifestUpload.OptInEnvironmentVariable)

        try
            Environment.SetEnvironmentVariable(ManifestUpload.OptInEnvironmentVariable, null)
            Assert.That(ManifestUpload.isOptedIn (), Is.False)

            Environment.SetEnvironmentVariable(ManifestUpload.OptInEnvironmentVariable, "0")
            Assert.That(ManifestUpload.isOptedIn (), Is.False)

            Environment.SetEnvironmentVariable(ManifestUpload.OptInEnvironmentVariable, "1")
            Assert.That(ManifestUpload.isOptedIn (), Is.True)
        finally
            Environment.SetEnvironmentVariable(ManifestUpload.OptInEnvironmentVariable, previous)
