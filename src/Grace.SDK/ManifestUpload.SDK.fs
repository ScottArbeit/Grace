namespace Grace.SDK

open Grace.Shared
open Grace.Shared.Parameters.Storage
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.ContentBlockMetadata
open Grace.Types.Common
open Grace.Types.UploadSession
open NodaTime
open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
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
            DiscoverContentBlocks: DiscoverContentBlocksParameters -> Task<GraceResult<DiscoverContentBlocksResult>>
            IssueDedupeDiscovery: IssueDedupeDiscoveryParameters -> Task<GraceResult<UploadSessionDecision>>
            ClaimReuseRanges: ClaimReuseRangesParameters -> Task<GraceResult<UploadSessionDecision>>
            RegisterBlockUpload: RegisterContentBlockUploadParameters -> Task<GraceResult<UploadSessionDecision>>
            UploadContentBlock: GetContentBlockUploadUriParameters -> byte array -> Task<GraceResult<ContentBlockStoragePlacement>>
            ConfirmBlockUploaded: ConfirmContentBlockUploadParameters -> Task<GraceResult<UploadSessionDecision>>
            FinalizeManifest: FinalizeManifestUploadParameters -> Task<GraceResult<UploadSessionDecision>>
        }

    let isOptedIn () = true

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

    let private buildDiscoveryParameters request (plan: LocalPlanner.LocalFilePlan) =
        let parameters = DiscoverContentBlocksParameters()
        setStorageParameters request parameters |> ignore

        parameters.KeyChunkAddresses <-
            plan.KeyChunks
            |> Array.map (fun keyChunk -> keyChunk.Address)

        parameters

    let private buildIssueDiscoveryParameters request uploadSessionId operationIndex expiresAt minimumReuseRunLength hints =
        let parameters = IssueDedupeDiscoveryParameters()
        setStorageParameters request parameters |> ignore
        parameters.UploadSessionId <- uploadSessionId
        parameters.AuthorizedScope <- request.AuthorizedScope
        parameters.OperationId <- $"manifest-upload-{uploadSessionId:N}-discovery-{operationIndex}"
        parameters.ExpiresAt <- expiresAt
        parameters.MinimumReuseRunLength <- minimumReuseRunLength
        parameters.Hints <- hints
        parameters

    let private buildClaimReuseRangesParameters request uploadSessionId operationIndex discoveryOperationId hints =
        let parameters = ClaimReuseRangesParameters()
        setStorageParameters request parameters |> ignore
        parameters.UploadSessionId <- uploadSessionId
        parameters.AuthorizedScope <- request.AuthorizedScope
        parameters.OperationId <- $"manifest-upload-{uploadSessionId:N}-claim-{operationIndex}"
        parameters.DiscoveryOperationId <- discoveryOperationId
        parameters.Hints <- hints
        parameters

    let private buildRegisterParameters request uploadSessionId operationIndex (block: ContentBlockFormat.EncodedContentBlock) logicalOffset logicalLength =
        let parameters = RegisterContentBlockUploadParameters()
        setStorageParameters request parameters |> ignore
        parameters.UploadSessionId <- uploadSessionId
        parameters.AuthorizedScope <- request.AuthorizedScope
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
        parameters.AuthorizedScope <- request.AuthorizedScope
        parameters

    let private buildConfirmParameters request uploadSessionId operationIndex (block: ContentBlockFormat.EncodedContentBlock) placement =
        let parameters = ConfirmContentBlockUploadParameters()
        setStorageParameters request parameters |> ignore
        parameters.UploadSessionId <- uploadSessionId
        parameters.AuthorizedScope <- request.AuthorizedScope
        parameters.OperationId <- $"manifest-upload-{uploadSessionId:N}-confirm-{operationIndex}"
        parameters.ContentBlockAddress <- block.Address
        parameters.Payload <- block.Payload
        parameters.StoragePlacement <- placement
        parameters

    let private buildFinalizeParameters request uploadSessionId manifest =
        let parameters = FinalizeManifestUploadParameters()
        setStorageParameters request parameters |> ignore
        parameters.UploadSessionId <- uploadSessionId
        parameters.AuthorizedScope <- request.AuthorizedScope
        parameters.OperationId <- $"manifest-upload-{uploadSessionId:N}-finalize"
        parameters.Manifest <- manifest
        parameters

    let private manifestFileVersion (fileVersion: FileVersion) manifest =
        let manifestVersion = FileVersion.Create fileVersion.RelativePath fileVersion.Sha256Hash fileVersion.BlobUri fileVersion.IsBinary fileVersion.Size
        manifestVersion.CreatedAt <- fileVersion.CreatedAt
        manifestVersion.ContentReference <- FileContentReference.FileManifest manifest
        manifestVersion

    let private protectedChunkAddress storagePoolId chunkAddress =
        let preimage = $"grace.dedupe-index.v1.protected-window\n{storagePoolId}\n{chunkAddress}"
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(preimage))
        $"protected-sha256:{Convert.ToHexString(hash).ToLowerInvariant()}"

    let private candidateMatchesBlock (block: LocalPlanner.ContentBlockPlan) (candidate: ContentBlockDiscoveryCandidate) =
        candidate.ContentBlockAddress = block.Address
        && block.ChunkAddresses
           |> Array.exists (fun chunkAddress ->
               candidate.ProtectedChunkAddresses
               |> Array.exists (fun protectedAddress -> protectedAddress = protectedChunkAddress candidate.StoragePoolId chunkAddress))

    let private reusableHints (plan: LocalPlanner.LocalFilePlan) (discovery: DiscoverContentBlocksResult) =
        if isNull discovery.CandidateContentBlocks
           || isNull (box discovery.Policy)
           || discovery.Policy.PositiveCandidatesEnabled |> not then
            Array.empty
        else
            let minimumRunLength = discovery.Policy.MinimumAcceptedReuseRunLength
            let hints = ResizeArray<ContentBlockReuseRangeHint>()
            let claimedBlockAddresses = HashSet<ContentBlockAddress>()
            let mutable candidateIndex = 0

            while candidateIndex < discovery.CandidateContentBlocks.Length do
                let candidate = discovery.CandidateContentBlocks[candidateIndex]

                if candidate.OrdinalCount >= minimumRunLength then
                    let matchingBlock =
                        plan.Blocks
                        |> Array.tryFind (fun block -> candidateMatchesBlock block candidate)

                    match matchingBlock with
                    | Some block when claimedBlockAddresses.Add block.Address -> hints.Add(DedupeIndex.toReuseRangeHint candidate)
                    | _ -> ()

                candidateIndex <- candidateIndex + 1

            hints.ToArray()

    let private addFullyClaimedBlockAddresses
        (plan: LocalPlanner.LocalFilePlan)
        (claimedBlockAddresses: HashSet<ContentBlockAddress>)
        (session: UploadSessionDto)
        =
        if
            not (isNull (box session))
            && not (isNull session.ClaimedReuseRanges)
        then
            let mutable claimedRangeIndex = 0

            while claimedRangeIndex < session.ClaimedReuseRanges.Length do
                let claimedRange = session.ClaimedReuseRanges[claimedRangeIndex]

                plan.Blocks
                |> Array.tryFind (fun block ->
                    block.Address = claimedRange.ContentBlockAddress
                    && block.Size = claimedRange.PhysicalLength)
                |> Option.iter (fun block -> claimedBlockAddresses.Add block.Address |> ignore)

                claimedRangeIndex <- claimedRangeIndex + 1

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
                            let claimedBlockAddresses = HashSet<ContentBlockAddress>()

                            match! client.DiscoverContentBlocks(buildDiscoveryParameters request plan) with
                            | Ok discoveryResult when
                                not (isNull discoveryResult.ReturnValue.CandidateContentBlocks)
                                && discoveryResult.ReturnValue.CandidateContentBlocks.Length > 0
                                ->
                                let reuseHints = reusableHints plan discoveryResult.ReturnValue

                                if reuseHints.Length > 0 then
                                    let issueParameters =
                                        buildIssueDiscoveryParameters
                                            request
                                            uploadSessionId
                                            0
                                            (getCurrentInstant()
                                                .Plus(Duration.FromSeconds(int64 discoveryResult.ReturnValue.Policy.ResponseTtlSeconds)))
                                            discoveryResult.ReturnValue.Policy.MinimumAcceptedReuseRunLength
                                            reuseHints

                                    match! client.IssueDedupeDiscovery issueParameters with
                                    | Ok _ ->
                                        let claimParameters = buildClaimReuseRangesParameters request uploadSessionId 0 issueParameters.OperationId reuseHints

                                        match! client.ClaimReuseRanges claimParameters with
                                        | Ok claimResult -> addFullyClaimedBlockAddresses plan claimedBlockAddresses claimResult.ReturnValue.Session
                                        | Error _ -> ()
                                    | Error _ -> ()
                            | _ -> ()

                            let errors = ResizeArray<GraceError>()
                            let mutable registerIndex = 0

                            while registerIndex < plan.Blocks.Length do
                                let logicalBlockPlan = plan.Blocks[registerIndex]

                                if claimedBlockAddresses.Contains logicalBlockPlan.Address then
                                    ()
                                else
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
                            let mutable uploadedBlockCount = 0

                            while index < encodedBlocks.Count do
                                let encodedBlock = encodedBlocks[index]

                                if claimedBlockAddresses.Contains encodedBlock.Address then
                                    ()
                                else
                                    match! client.UploadContentBlock (buildUploadUriParameters request encodedBlock.Address) encodedBlock.Payload with
                                    | Error error -> errors.Add error
                                    | Ok placementResult ->
                                        match!
                                            client.ConfirmBlockUploaded
                                                (buildConfirmParameters request uploadSessionId index encodedBlock placementResult.ReturnValue)
                                            with
                                        | Error error -> errors.Add error
                                        | Ok _ -> uploadedBlockCount <- uploadedBlockCount + 1

                                index <- index + 1

                            if errors.Count > 0 then
                                return Error errors[0]
                            else
                                match! client.FinalizeManifest(buildFinalizeParameters request uploadSessionId manifest) with
                                | Error error -> return Error error
                                | Ok _ ->
                                    return
                                        Ok(
                                            GraceReturnValue.Create
                                                {
                                                    FileVersion = manifestFileVersion request.FileVersion manifest
                                                    Manifest = Some manifest
                                                    UploadSessionId = Some uploadSessionId
                                                    UploadedBlockCount = uploadedBlockCount
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
            DiscoverContentBlocks = Storage.DiscoverContentBlocks
            IssueDedupeDiscovery = Storage.IssueDedupeDiscovery
            ClaimReuseRanges = Storage.ClaimReuseRanges
            RegisterBlockUpload = Storage.RegisterContentBlockUpload
            UploadContentBlock =
                fun parameters payload ->
                    task {
                        match! Storage.GetContentBlockUploadUri parameters with
                        | Error error -> return Error error
                        | Ok uri ->
                            match Uri.TryCreate(uri.ReturnValue, UriKind.Absolute) with
                            | false, _ ->
                                return
                                    Error(
                                        GraceError.Create
                                            $"Failed to get a valid ContentBlock upload URI for {parameters.ContentBlockAddress}."
                                            parameters.CorrelationId
                                    )
                            | true, uploadUri ->
                                return! Storage.SaveContentBlockToObjectStorage parameters.ContentBlockAddress payload uploadUri parameters.CorrelationId
                    }
            ConfirmBlockUploaded = Storage.ConfirmContentBlockUpload
            FinalizeManifest = Storage.FinalizeManifestUpload
        }

    let uploadFile request = uploadFileWithClient serverClient request
