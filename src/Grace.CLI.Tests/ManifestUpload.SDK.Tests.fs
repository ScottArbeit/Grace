namespace Grace.CLI.Tests

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

[<Parallelizable(ParallelScope.All)>]
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

    static member private CreateRequest tempPath fileVersion correlationId : ManifestUpload.ManifestUploadRequest =
        {
            OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222")
            OwnerName = "owner"
            OrganizationId = Guid.Parse("33333333-3333-3333-3333-333333333333")
            OrganizationName = "org"
            RepositoryId = Guid.Parse("11111111-1111-1111-1111-111111111111")
            RepositoryName = "repo"
            AuthorizedScope = "/"
            FileVersion = fileVersion
            LocalFilePath = tempPath
            CorrelationId = correlationId
            PlannerOptions = { LocalPlanner.Options.Default with EligibilityPolicy = ManifestUploadSdkTests.BinaryPolicy 1024L }
        }

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
                                calls.Add("start")
                                sessionIds.Add(parameters.UploadSessionId)
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        RegisterBlockUpload =
                            fun parameters ->
                                calls.Add($"register:{parameters.ContentBlockAddress}")
                                Assert.That(parameters.ExpectedPayloadLength, Is.GreaterThan(0L))
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        UploadContentBlock =
                            fun parameters payload ->
                                calls.Add($"upload:{parameters.ContentBlockAddress}")
                                uploadedBlocks[parameters.ContentBlockAddress] <- payload

                                let placement =
                                    { ObjectKey = $"cas/content-blocks/{parameters.ContentBlockAddress}"; ETag = Some $"etag-{uploadedBlocks.Count}" }

                                Task.FromResult(Ok(GraceReturnValue.Create placement correlationId))
                        ConfirmBlockUploaded =
                            fun parameters ->
                                calls.Add($"confirm:{parameters.ContentBlockAddress}")
                                confirmedBlocks[parameters.ContentBlockAddress] <- parameters.Payload
                                ManifestUploadSdkTests.Decision correlationId parameters.UploadSessionId parameters.OperationId
                        FinalizeManifest =
                            fun parameters ->
                                calls.Add("finalize")
                                finalizedManifest <- Some parameters.Manifest
                                Assert.That(parameters.BlockPayloads, Has.Length.EqualTo(uploadedBlocks.Count))
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
