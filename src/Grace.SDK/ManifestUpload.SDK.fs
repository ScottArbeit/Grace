namespace Grace.SDK

open Grace.Shared
open Grace.Shared.Parameters.Storage
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.ContentBlockMetadata
open Grace.Types.Types
open Grace.Types.UploadSession
open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks

module ManifestUpload =
    [<Literal>]
    let OptInEnvironmentVariable = "GRACE_MANIFEST_UPLOADS"

    type ManifestUploadRequest =
        {
            OwnerId: OwnerId
            OwnerName: OwnerName
            OrganizationId: OrganizationId
            OrganizationName: OrganizationName
            RepositoryId: RepositoryId
            RepositoryName: RepositoryName
            AuthorizedScope: RelativePath
            FileVersion: FileVersion
            LocalFilePath: string
            CorrelationId: CorrelationId
            PlannerOptions: LocalPlanner.Options
        }

    type ManifestUploadResult =
        {
            FileVersion: FileVersion
            Manifest: FileManifest option
            UploadSessionId: UploadSessionId option
            UploadedBlockCount: int
            UsedManifestUpload: bool
        }

    type ManifestUploadClient =
        {
            StartSession: StartManifestUploadSessionParameters -> Task<GraceResult<UploadSessionDecision>>
            RegisterBlockUpload: RegisterContentBlockUploadParameters -> Task<GraceResult<UploadSessionDecision>>
            UploadContentBlock: GetContentBlockUploadUriParameters -> byte array -> Task<GraceResult<ContentBlockStoragePlacement>>
            ConfirmBlockUploaded: ConfirmContentBlockUploadParameters -> Task<GraceResult<UploadSessionDecision>>
            FinalizeManifest: FinalizeManifestUploadParameters -> Task<GraceResult<UploadSessionDecision>>
        }

    let isOptedIn () = String.Equals(Environment.GetEnvironmentVariable(OptInEnvironmentVariable), "1", StringComparison.OrdinalIgnoreCase)

    let private setStorageParameters (request: ManifestUploadRequest) (parameters: StorageParameters) =
        parameters.OwnerId <- $"{request.OwnerId}"
        parameters.OwnerName <- request.OwnerName
        parameters.OrganizationId <- $"{request.OrganizationId}"
        parameters.OrganizationName <- request.OrganizationName
        parameters.RepositoryId <- $"{request.RepositoryId}"
        parameters.RepositoryName <- request.RepositoryName
        parameters.CorrelationId <- request.CorrelationId
        parameters

    let private error correlationId message = Error(GraceError.Create message correlationId)

    let private createManifest (plan: LocalPlanner.LocalFilePlan) =
        match plan.ManifestAddress with
        | None -> None
        | Some manifestAddress ->
            let blocks =
                plan.Blocks
                |> Array.map (fun block -> ContentBlock.Create(block.Address, block.Offset, block.Size))
                |> List.ofArray

            Some(FileManifest.Create(manifestAddress, plan.ChunkingSuiteId, plan.FileContentHash, plan.ExpectedSize, blocks))

    let private encodeBlock correlationId (uploadPlan: LocalPlanner.ContentBlockUploadPlan) =
        match ContentBlockFormat.encode [ { PhysicalOffset = 0L; Bytes = uploadPlan.Bytes } ] with
        | Error encodeError -> error correlationId $"Failed to encode ContentBlock {uploadPlan.BlockAddress}: {encodeError}."
        | Ok encodedBlock when encodedBlock.Address <> uploadPlan.BlockAddress ->
            error correlationId $"Encoded ContentBlock address mismatch. Expected {uploadPlan.BlockAddress}, actual {encodedBlock.Address}."
        | Ok encodedBlock -> Ok encodedBlock

    let private buildStartParameters request uploadSessionId (plan: LocalPlanner.LocalFilePlan) =
        let parameters = StartManifestUploadSessionParameters()
        setStorageParameters request parameters |> ignore
        parameters.UploadSessionId <- uploadSessionId
        parameters.AuthorizedScope <- request.AuthorizedScope
        parameters.FileContentHash <- plan.FileContentHash
        parameters.ExpectedSize <- plan.ExpectedSize
        parameters.ChunkingSuiteId <- plan.ChunkingSuiteId
        parameters.SamplingPolicySnapshot <- "sdk-no-global-dedupe-v1"
        parameters.OperationId <- $"manifest-upload-{uploadSessionId:N}-start"
        parameters

    let private buildRegisterParameters request uploadSessionId operationIndex (block: ContentBlockFormat.EncodedContentBlock) logicalOffset logicalLength =
        let parameters = RegisterContentBlockUploadParameters()
        setStorageParameters request parameters |> ignore
        parameters.UploadSessionId <- uploadSessionId
        parameters.OperationId <- $"manifest-upload-{uploadSessionId:N}-register-{operationIndex}"
        parameters.ContentBlockAddress <- block.Address
        parameters.LogicalOffset <- logicalOffset
        parameters.LogicalLength <- logicalLength
        parameters.ExpectedPayloadLength <- int64 block.Payload.Length
        parameters

    let private buildUploadUriParameters request contentBlockAddress =
        let parameters = GetContentBlockUploadUriParameters()
        setStorageParameters request parameters |> ignore
        parameters.ContentBlockAddress <- contentBlockAddress
        parameters

    let private buildConfirmParameters request uploadSessionId operationIndex (block: ContentBlockFormat.EncodedContentBlock) placement =
        let parameters = ConfirmContentBlockUploadParameters()
        setStorageParameters request parameters |> ignore
        parameters.UploadSessionId <- uploadSessionId
        parameters.OperationId <- $"manifest-upload-{uploadSessionId:N}-confirm-{operationIndex}"
        parameters.ContentBlockAddress <- block.Address
        parameters.Payload <- block.Payload
        parameters.StoragePlacement <- placement
        parameters

    let private buildFinalizeParameters request uploadSessionId manifest blockPayloads =
        let parameters = FinalizeManifestUploadParameters()
        setStorageParameters request parameters |> ignore
        parameters.UploadSessionId <- uploadSessionId
        parameters.OperationId <- $"manifest-upload-{uploadSessionId:N}-finalize"
        parameters.Manifest <- manifest
        parameters.BlockPayloads <- blockPayloads
        parameters

    let private manifestFileVersion (fileVersion: FileVersion) manifest =
        let manifestVersion = FileVersion.Create fileVersion.RelativePath fileVersion.Sha256Hash fileVersion.BlobUri fileVersion.IsBinary fileVersion.Size
        manifestVersion.CreatedAt <- fileVersion.CreatedAt
        manifestVersion.ContentReference <- FileContentReference.FileManifest manifest
        manifestVersion

    let uploadFileWithClient (client: ManifestUploadClient) (request: ManifestUploadRequest) : Task<GraceResult<ManifestUploadResult>> =
        task {
            if isNull (box request.FileVersion) then
                return error request.CorrelationId "FileVersion is required for manifest upload."
            elif String.IsNullOrWhiteSpace request.LocalFilePath then
                return error request.CorrelationId "LocalFilePath is required for manifest upload."
            elif not (File.Exists request.LocalFilePath) then
                return error request.CorrelationId $"LocalFilePath does not exist: {request.LocalFilePath}."
            else
                let plan = LocalPlanner.analyzeFile request.PlannerOptions request.LocalFilePath

                match plan.ReferenceType, createManifest plan with
                | FileContentReferenceType.FileManifest, Some manifest ->
                    let encodedBlocks = ResizeArray<ContentBlockFormat.EncodedContentBlock>()
                    let encodedBlocksByAddress = Dictionary<ContentBlockAddress, ContentBlockFormat.EncodedContentBlock>()
                    let mutable encodeError = None

                    let mutable uploadPlanIndex = 0

                    while uploadPlanIndex < plan.ContentBlockUploads.Length do
                        match encodeBlock request.CorrelationId plan.ContentBlockUploads[uploadPlanIndex] with
                        | Ok encodedBlock ->
                            encodedBlocks.Add encodedBlock
                            encodedBlocksByAddress[encodedBlock.Address] <- encodedBlock
                        | Error error -> encodeError <- Some error

                        uploadPlanIndex <- uploadPlanIndex + 1

                    match encodeError with
                    | Some error -> return Error error
                    | None ->
                        let uploadSessionId = Guid.NewGuid()

                        match! client.StartSession(buildStartParameters request uploadSessionId plan) with
                        | Error error -> return Error error
                        | Ok _ ->
                            let errors = ResizeArray<GraceError>()
                            let mutable registerIndex = 0

                            while registerIndex < plan.Blocks.Length do
                                let logicalBlockPlan = plan.Blocks[registerIndex]

                                match encodedBlocksByAddress.TryGetValue logicalBlockPlan.Address with
                                | false, _ ->
                                    errors.Add(
                                        GraceError.Create
                                            $"Missing encoded ContentBlock payload for manifest block {logicalBlockPlan.Address}."
                                            request.CorrelationId
                                    )
                                | true, encodedBlock ->
                                    match!
                                        client.RegisterBlockUpload
                                            (
                                                buildRegisterParameters
                                                    request
                                                    uploadSessionId
                                                    registerIndex
                                                    encodedBlock
                                                    logicalBlockPlan.Offset
                                                    logicalBlockPlan.Size
                                            )
                                        with
                                    | Error error -> errors.Add error
                                    | Ok _ -> ()

                                registerIndex <- registerIndex + 1

                            let mutable index = 0

                            while index < encodedBlocks.Count do
                                let encodedBlock = encodedBlocks[index]

                                match! client.UploadContentBlock (buildUploadUriParameters request encodedBlock.Address) encodedBlock.Payload with
                                | Error error -> errors.Add error
                                | Ok placementResult ->
                                    match!
                                        client.ConfirmBlockUploaded
                                            (buildConfirmParameters request uploadSessionId index encodedBlock placementResult.ReturnValue)
                                        with
                                    | Error error -> errors.Add error
                                    | Ok _ -> ()

                                index <- index + 1

                            if errors.Count > 0 then
                                return Error errors[0]
                            else
                                let blockPayloads =
                                    encodedBlocks
                                    |> Seq.map (fun block -> { Address = block.Address; Payload = block.Payload })
                                    |> Seq.toArray

                                match! client.FinalizeManifest(buildFinalizeParameters request uploadSessionId manifest blockPayloads) with
                                | Error error -> return Error error
                                | Ok _ ->
                                    return
                                        Ok(
                                            GraceReturnValue.Create
                                                {
                                                    FileVersion = manifestFileVersion request.FileVersion manifest
                                                    Manifest = Some manifest
                                                    UploadSessionId = Some uploadSessionId
                                                    UploadedBlockCount = encodedBlocks.Count
                                                    UsedManifestUpload = true
                                                }
                                                request.CorrelationId
                                        )
                | _ ->
                    return
                        Ok(
                            GraceReturnValue.Create
                                {
                                    FileVersion = request.FileVersion
                                    Manifest = None
                                    UploadSessionId = None
                                    UploadedBlockCount = 0
                                    UsedManifestUpload = false
                                }
                                request.CorrelationId
                        )
        }

    let serverClient =
        {
            StartSession = Storage.StartManifestUploadSession
            RegisterBlockUpload = Storage.RegisterContentBlockUpload
            UploadContentBlock =
                fun parameters payload ->
                    task {
                        match! Storage.GetContentBlockUploadUri parameters with
                        | Error error -> return Error error
                        | Ok uri ->
                            return!
                                Storage.SaveContentBlockToObjectStorage parameters.ContentBlockAddress payload (Uri(uri.ReturnValue)) parameters.CorrelationId
                    }
            ConfirmBlockUploaded = Storage.ConfirmContentBlockUpload
            FinalizeManifest = Storage.FinalizeManifestUpload
        }

    let uploadFile request = uploadFileWithClient serverClient request
