namespace Grace.Server.Measurements

open Azure
open Azure.Messaging.ServiceBus
open Azure.Storage.Blobs.Models
open Azure.Storage.Blobs.Specialized
open Grace.Server.Tests
open Grace.Server.Tests.Measurement
open Grace.Shared
open Grace.Shared.Services
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
open Grace.Types.Events
open Grace.Types.ManifestContributionAccounting
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Reference
open Grace.Types.RepositoryContentCounter
open Grace.Types.UploadSession
open Microsoft.Azure.Cosmos
open NUnit.Framework
open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Carries one manifest, root, branch, and explicit Save identity through the shared selected-process measurement runtime.
type internal BaselineAsset =
    {
        BlockAddress: ContentBlockAddress
        Manifest: FileManifest
        Root: DirectoryVersion
        Branch: Branch.BranchDto
        RebaseReferenceId: ReferenceId
        SaveReferenceId: ReferenceId
    }

/// Captures each independent durable convergence result without treating one state store as broker evidence.
type internal DurableStatus =
    {
        ReferenceRoots: bool
        ManifestRelationships: bool
        LogicalCounts: bool
        WorkflowCounts: bool
        PhysicalActiveCounts: bool
        Detail: string
    }

/// Retains the persisted Reference-created broker envelope needed by later replay witnesses.
type internal CapturedReferenceEnvelope =
    {
        Body: byte array
        MessageId: string
        CorrelationId: string
        Subject: string
        ContentType: string
        ApplicationProperties: Dictionary<string, obj>
    }

/// Implements the shared fixture-owned selected-process measurement runtime introduced by the Baseline tracer.
module internal BaselineRuntime =

    [<Literal>]
    let SelectedTopologyCount = 3

    [<Literal>]
    let MaximumRecordBytes = 65536

    /// Requires a nonblank selected-process input before the fixture causes side effects.
    let requireEnvironment name =
        match Environment.GetEnvironmentVariable name with
        | value when not (String.IsNullOrWhiteSpace value) -> value.Trim()
        | _ -> invalidOp $"The explicit manifest-contribution witness requires {name}."

    /// Runs one Git query while concurrently draining both redirected streams.
    let runGitAsync worktree arguments =
        task {
            let startInfo = ProcessStartInfo("git")
            startInfo.WorkingDirectory <- worktree
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.UseShellExecute <- false
            startInfo.ArgumentList.Add("-C")
            startInfo.ArgumentList.Add(worktree)

            arguments |> Array.iter startInfo.ArgumentList.Add

            use gitProcess = Process.Start startInfo
            let outputTask = gitProcess.StandardOutput.ReadToEndAsync()
            let errorTask = gitProcess.StandardError.ReadToEndAsync()
            do! gitProcess.WaitForExitAsync()
            let! output = outputTask
            let! error = errorTask
            let output = output.Trim()
            let error = error.Trim()

            if gitProcess.ExitCode <> 0 then
                let argumentText = String.Join(" ", arguments)
                invalidOp $"git {argumentText} failed: {error}"

            return output
        }

    /// Requires an HTTP success response and returns its body for typed inspection.
    let requireOkAsync description (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()

            if response.StatusCode <> HttpStatusCode.OK then
                invalidOp $"{description} returned {response.StatusCode}: {body}"

            return body
        }

    /// Scrapes the current production OpenMetrics endpoint through the fixture bootstrap identity.
    let scrapeMetricsAsync (state: TestHostState) =
        task {
            use! response = state.Client.GetAsync("/metrics")
            return! requireOkAsync "GET /metrics" response
        }

    /// Waits for the first exact completed-settlement samples before any cumulative baseline is captured.
    let waitForCompletedSettlementSamplesAsync state =
        task {
            let timeoutAt = DateTime.UtcNow.AddSeconds(30.0)
            let mutable scrape = String.Empty
            let mutable complete = false
            let mutable failure = String.Empty

            while not complete && DateTime.UtcNow < timeoutAt do
                let! currentScrape = scrapeMetricsAsync state
                scrape <- currentScrape

                match OpenMetrics.evaluateCompletedSettlementDelta 0L scrape scrape with
                | DeltaEvaluation.Complete _ -> complete <- true
                | DeltaEvaluation.Pending -> ()
                | DeltaEvaluation.Invalid reason when reason.Contains("required exactly one sample but found 0", StringComparison.Ordinal) ->
                    failure <- reason
                    do! Task.Delay(TimeSpan.FromMilliseconds(250.0))
                | DeltaEvaluation.Invalid reason -> invalidOp reason

            if not complete then
                invalidOp $"Timed out waiting for initial completed-settlement samples. {failure}"

            return scrape
        }

    /// Waits for exact message and duration deltas, failing immediately on reset, overshoot, or invalid series.
    let waitForCompletedSettlementDeltaAsync state expectedDelta baseline =
        task {
            let timeoutAt = DateTime.UtcNow.AddSeconds(45.0)
            let mutable result: (int64 * int64 * string) option = None

            while result.IsNone && DateTime.UtcNow < timeoutAt do
                let! scrape = scrapeMetricsAsync state

                match OpenMetrics.evaluateCompletedSettlementDelta expectedDelta baseline scrape with
                | DeltaEvaluation.Complete (messageDelta, durationDelta) -> result <- Some(messageDelta, durationDelta, scrape)
                | DeltaEvaluation.Pending -> do! Task.Delay(TimeSpan.FromMilliseconds(250.0))
                | DeltaEvaluation.Invalid reason -> invalidOp reason

            return
                result
                |> Option.defaultWith (fun () -> invalidOp $"Timed out waiting for exact completed-settlement delta {expectedDelta}.")
        }

    /// Creates the required owner, preserving POST /owner/create as the host's first HTTP request.
    let createOwnerAsync state ownerId =
        task {
            let parameters = Parameters.Owner.CreateOwnerParameters()
            parameters.OwnerId <- string ownerId
            parameters.OwnerName <- $"McaBaselineOwner{ownerId:N}"
            parameters.CorrelationId <- generateCorrelationId ()
            use! response = state.Client.PostAsync("/owner/create", createJsonContent parameters)
            let! _ = requireOkAsync "POST /owner/create" response
            let! _ = AspireTestHost.waitForOwnerCreatedEventAsync state (string ownerId)
            return ()
        }

    /// Creates the fixture organization under its isolated owner.
    let createOrganizationAsync state ownerId organizationId =
        task {
            let parameters = Parameters.Organization.CreateOrganizationParameters()
            parameters.OwnerId <- string ownerId
            parameters.OrganizationId <- string organizationId
            parameters.OrganizationName <- $"McaBaselineOrganization{organizationId:N}"
            parameters.CorrelationId <- generateCorrelationId ()
            use! response = state.Client.PostAsync("/organization/create", createJsonContent parameters)
            let! _ = requireOkAsync "POST /organization/create" response
            return ()
        }

    /// Creates the one scenario repository and returns its deterministic default branch and Reference identities.
    let createRepositoryAsync state ownerId organizationId repositoryId =
        task {
            let parameters = Parameters.Repository.CreateRepositoryParameters()
            parameters.OwnerId <- string ownerId
            parameters.OrganizationId <- string organizationId
            parameters.RepositoryId <- string repositoryId
            parameters.RepositoryName <- $"mca-baseline-{repositoryId:N}"
            parameters.CorrelationId <- generateCorrelationId ()
            use! response = state.Client.PostAsync("/repository/create", createJsonContent parameters)
            let! body = requireOkAsync "POST /repository/create" response
            let result = deserialize<GraceReturnValue<string>> body
            let branchId = Grace.Server.Tests.Common.requireGuidProperty (nameof BranchId) result.Properties[nameof BranchId]
            let referenceId = Grace.Server.Tests.Common.requireGuidProperty (nameof ReferenceId) result.Properties[nameof ReferenceId]
            return branchId, referenceId
        }

    /// Reads one persisted branch from the selected repository.
    let getBranchAsync state ownerId organizationId repositoryId branchId =
        task {
            let parameters = Parameters.Branch.GetBranchParameters()
            parameters.OwnerId <- string ownerId
            parameters.OrganizationId <- string organizationId
            parameters.RepositoryId <- string repositoryId
            parameters.BranchId <- string branchId
            parameters.CorrelationId <- generateCorrelationId ()
            use! response = state.Client.PostAsync("/branch/get", createJsonContent parameters)
            let! body = requireOkAsync "POST /branch/get" response

            return
                (deserialize<GraceReturnValue<Branch.BranchDto>> body)
                    .ReturnValue
        }

    /// Reads persisted References for one branch so API acceptance cannot stand in for durable envelope proof.
    let getBranchReferencesAsync state ownerId organizationId repositoryId branchId =
        task {
            let parameters = Parameters.Branch.GetReferencesParameters()
            parameters.OwnerId <- string ownerId
            parameters.OrganizationId <- string organizationId
            parameters.RepositoryId <- string repositoryId
            parameters.BranchId <- string branchId
            parameters.MaxCount <- 100
            parameters.CorrelationId <- generateCorrelationId ()
            use! response = state.Client.PostAsync("/branch/getReferences", createJsonContent parameters)
            let! body = requireOkAsync "POST /branch/getReferences" response

            return
                (deserialize<GraceReturnValue<Reference.ReferenceDto array>> body)
                    .ReturnValue
        }

    /// Observes every Reference-created envelope encountered while waiting for an exact producer inventory.
    let observeReferenceEnvelopesAsync state (expectedMessageIds: string array) description =
        task {
            use client = new ServiceBusClient(state.ServiceBusConnectionString)
            let options = ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete)
            use receiver = client.CreateReceiver(state.ServiceBusTopic, state.ServiceBusTestSubscription, options)
            let timeoutAt = DateTime.UtcNow.AddSeconds(30.0)
            let mutable drain = ProducerInventoryDrain.start
            let captured = ResizeArray<CapturedReferenceEnvelope>()

            while ProducerInventoryDrain.status drain = ProducerInventoryDrainStatus.Receiving
                  && DateTime.UtcNow < timeoutAt do
                let remaining = timeoutAt - DateTime.UtcNow

                if remaining <= TimeSpan.Zero then
                    drain <- ProducerInventoryDrain.deadlineExpired drain
                else
                    let receiveWindow = min remaining (TimeSpan.FromSeconds(2.0))

                    try
                        let! messages = receiver.ReceiveMessagesAsync(50, receiveWindow)
                        let batch = messages |> Seq.toArray

                        if DateTime.UtcNow >= timeoutAt then
                            drain <- ProducerInventoryDrain.deadlineExpired drain
                        elif Array.isEmpty batch then
                            drain <- ProducerInventoryDrain.emptyWindow expectedMessageIds drain
                        else
                            let observedInBatch = ResizeArray<string>()
                            let mutable index = 0

                            while index < batch.Length do
                                let message = batch[index]

                                try
                                    let graceEvent = JsonSerializer.Deserialize<GraceEvent>(message.Body.ToArray(), Constants.JsonSerializerOptions)

                                    match graceEvent with
                                    | GraceEvent.ReferenceEvent referenceEvent ->
                                        match referenceEvent.Event with
                                        | ReferenceEventType.Created (referenceId, _, _, _, _, _, _, _, _, _, _) ->
                                            let expectedMessageId = $"Reference/{referenceId}/Created"

                                            if not (message.MessageId.Equals(expectedMessageId, StringComparison.Ordinal)) then
                                                observedInBatch.Add($"{message.MessageId} (body identity {expectedMessageId})")
                                            else
                                                observedInBatch.Add message.MessageId

                                                captured.Add(
                                                    {
                                                        Body = message.Body.ToArray()
                                                        MessageId = message.MessageId
                                                        CorrelationId = message.CorrelationId
                                                        Subject = message.Subject
                                                        ContentType = message.ContentType
                                                        ApplicationProperties = Dictionary<string, obj>(message.ApplicationProperties, StringComparer.Ordinal)
                                                    }
                                                )
                                        | _ -> ()
                                    | _ -> ()
                                with
                                | :? JsonException -> ()

                                index <- index + 1

                            drain <- ProducerInventoryDrain.receiveBatch expectedMessageIds (observedInBatch.ToArray()) drain
                    with
                    | :? OperationCanceledException -> drain <- ProducerInventoryDrain.cancelled drain
                    | ex -> drain <- ProducerInventoryDrain.receiveFailed ex.Message drain

            if ProducerInventoryDrain.status drain = ProducerInventoryDrainStatus.Receiving then
                drain <- ProducerInventoryDrain.deadlineExpired drain

            match ProducerInventoryDrain.status drain with
            | ProducerInventoryDrainStatus.Complete -> return captured.ToArray()
            | ProducerInventoryDrainStatus.Failed -> return invalidOp $"{description} producer inventory failed: {ProducerInventoryDrain.failure drain}"
            | ProducerInventoryDrainStatus.Receiving -> return invalidOp $"{description} producer inventory stopped without terminal evidence."
        }

    /// Encodes deterministic but distinct content for one selected topology position.
    let createPayload index =
        let bytes = Array.zeroCreate<byte> 65536
        let mutable state = 0x6284A1D7u + uint32 index
        let mutable offset = 0

        while offset < bytes.Length do
            state <- state ^^^ (state <<< 13)
            state <- state ^^^ (state >>> 17)
            state <- state ^^^ (state <<< 5)
            bytes[offset] <- byte (state &&& 0xffu)
            offset <- offset + 1

        bytes

    /// Encodes one ContentBlock and fails before side effects if the payload is invalid.
    let encodeBlock bytes =
        match ContentBlockFormat.encode [ { PhysicalOffset = 0L; Bytes = bytes } ] with
        | Ok block -> block
        | Error error -> invalidOp $"Baseline ContentBlock encoding failed: {error}"

    /// Builds one finalized manifest for the storage pool selected by the upload session.
    let createManifest storagePoolId bytes (block: ContentBlockFormat.EncodedContentBlock) =
        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                ChunkingSuiteId RabinChunking.SuiteName,
                FileContentHash(ContentAddress.computeBlake3Hex bytes),
                int64 bytes.Length,
                storagePoolId,
                [
                    ContentBlock.Create(block.Address, 0L, int64 bytes.Length)
                ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    /// Converts one upload URI into the physical placement persisted by ContentBlock metadata.
    let contentBlockPlacementFromUri (blobUriWithSasToken: Uri) eTag =
        let segments =
            blobUriWithSasToken
                .AbsolutePath
                .Trim('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map Uri.UnescapeDataString

        let pathStyle =
            blobUriWithSasToken.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(blobUriWithSasToken.Host)
               |> fst

        let containerIndex = if pathStyle then 1 else 0

        {
            StorageAccountName = if pathStyle then segments[0] else blobUriWithSasToken.Host.Split('.')[0]
            StorageContainerName = StorageContainerName segments[containerIndex]
            ObjectKey = String.Join("/", segments |> Array.skip (containerIndex + 1))
            ETag = eTag
        }

    /// Applies the isolated fixture identity to one storage request.
    let setStorageParameters (parameters: Parameters.Storage.StorageParameters) ownerId organizationId repositoryId correlationId =
        parameters.OwnerId <- string ownerId
        parameters.OrganizationId <- string organizationId
        parameters.RepositoryId <- string repositoryId
        parameters.CorrelationId <- correlationId

    /// Posts one upload-session decision and returns its persisted response.
    let postUploadDecisionAsync (state: TestHostState) (route: string) parameters : Task<UploadSessionDecision> =
        task {
            use! response = state.Client.PostAsync(route, createJsonContent parameters)
            let! body = requireOkAsync $"POST {route}" response

            return
                (deserialize<GraceReturnValue<UploadSessionDecision>> body)
                    .ReturnValue
        }

    /// Creates and finalizes one distinct manifest-backed asset.
    let createManifestAssetAsync (state: TestHostState) (ownerId: Guid) (organizationId: Guid) (repositoryId: Guid) index =
        task {
            let payloadIndex = ManifestContributionGroupedRuntime.selectAssetIndex repositoryId index
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let scope = $"baseline-{index}.bin"
            let bytes = createPayload payloadIndex
            let block = encodeBlock bytes
            let initialManifest = createManifest (StoragePoolId Constants.DefaultStoragePoolId) bytes block

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            setStorageParameters start ownerId organizationId repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- scope
            start.FileContentHash <- initialManifest.FileContentHash
            start.ExpectedSize <- initialManifest.Size
            start.ChunkingSuiteId <- initialManifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- "mca-baseline"
            start.OperationId <- "start"
            let! started = postUploadDecisionAsync state "/storage/startManifestUploadSession" start
            let manifest = createManifest started.Session.StoragePoolId bytes block

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            setStorageParameters register ownerId organizationId repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- scope
            register.OperationId <- "register"
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 bytes.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length
            let! _ = postUploadDecisionAsync state "/storage/registerContentBlockUpload" register

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            setStorageParameters uploadUriParameters ownerId organizationId repositoryId correlationId
            uploadUriParameters.UploadSessionId <- sessionId
            uploadUriParameters.ContentBlockAddress <- block.Address
            uploadUriParameters.AuthorizedScope <- scope
            use! uploadUriResponse = state.Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = requireOkAsync "POST /storage/getContentBlockUploadUri" uploadUriResponse
            let uploadUri = Uri uploadUriBody
            let blobClient = BlockBlobClient(uploadUri)
            use payloadStream = new MemoryStream(block.Payload, writable = false)
            let uploadOptions = BlobUploadOptions()
            uploadOptions.Conditions <- BlobRequestConditions(IfNoneMatch = ETag.All)
            let! uploaded = blobClient.UploadAsync(payloadStream, uploadOptions)

            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            setStorageParameters confirm ownerId organizationId repositoryId correlationId
            confirm.UploadSessionId <- sessionId
            confirm.AuthorizedScope <- scope
            confirm.OperationId <- "confirm"
            confirm.ContentBlockAddress <- block.Address
            confirm.Payload <- block.Payload
            confirm.StoragePlacement <- contentBlockPlacementFromUri uploadUri (Some(uploaded.Value.ETag.ToString()))
            let! _ = postUploadDecisionAsync state "/storage/confirmContentBlockUpload" confirm

            let finalize = Parameters.Storage.FinalizeManifestUploadParameters()
            setStorageParameters finalize ownerId organizationId repositoryId correlationId
            finalize.UploadSessionId <- sessionId
            finalize.AuthorizedScope <- scope
            finalize.OperationId <- "finalize"
            finalize.Manifest <- manifest
            let! finalized = postUploadDecisionAsync state "/storage/finalizeManifestUpload" finalize

            if finalized.Session.FinalizedManifestAddress
               <> Some manifest.ManifestAddress then
                invalidOp "The Baseline manifest did not finalize with its exact address."

            return block.Address, manifest, bytes
        }

    /// Builds a root containing exactly one distinct manifest-backed file.
    let createRoot (ownerId: Guid) (organizationId: Guid) (repositoryId: Guid) index (manifest: FileManifest) (bytes: byte array) =
        let sha256 =
            SHA256.HashData bytes
            |> fun hash -> byteArrayToString (hash.AsSpan())

        let file =
            FileVersion.CreateWithHashes
                (RelativePath $"baseline-{index}.bin")
                (Sha256Hash sha256)
                (Blake3Hash manifest.FileContentHash)
                String.Empty
                true
                manifest.Size

        file.ContentReference <- FileContentReference.FileManifest manifest

        let entries =
            [|
                DirectoryVersionPreimageEntry.File file.RelativePath file.Size file.Blake3Hash file.Sha256Hash
            |]

        DirectoryVersion.CreateWithHashes
            (Guid.NewGuid())
            ownerId
            organizationId
            repositoryId
            Constants.RootDirectoryPath
            (computeSha256ForDirectoryEntries Constants.RootDirectoryPath entries)
            (computeBlake3ForDirectory Constants.RootDirectoryPath entries)
            (List<DirectoryVersionId>())
            (List<FileVersion>([ file ]))
            file.Size

    /// Persists one root before any branch-created setup delivery begins.
    let saveRootAsync state ownerId organizationId repositoryId root =
        task {
            let parameters = Parameters.DirectoryVersion.SaveDirectoryVersionsParameters()
            parameters.OwnerId <- string ownerId
            parameters.OrganizationId <- string organizationId
            parameters.RepositoryId <- string repositoryId
            parameters.CorrelationId <- generateCorrelationId ()
            parameters.DirectoryVersions.Add root
            use! response = state.Client.PostAsync("/directory/saveDirectoryVersions", createJsonContent parameters)
            let! _ = requireOkAsync "POST /directory/saveDirectoryVersions" response
            return ()
        }

    /// Creates one child branch with caller-selected permissions and a caller-owned automatic Rebase Reference identity.
    let createBranchWithPermissionsAsync state ownerId organizationId repositoryId (parent: Branch.BranchDto) index rebaseReferenceId initialPermissions =
        task {
            let branchId = Guid.NewGuid()
            let parameters = Parameters.Branch.CreateBranchParameters()
            parameters.OwnerId <- string ownerId
            parameters.OrganizationId <- string organizationId
            parameters.RepositoryId <- string repositoryId
            parameters.BranchId <- string branchId
            parameters.BranchName <- $"baseline-{index}-{branchId:N}"
            parameters.ParentBranchId <- string parent.BranchId
            parameters.ParentBranchName <- string parent.BranchName
            parameters.ReferenceId <- rebaseReferenceId
            parameters.InitialPermissions <- initialPermissions
            parameters.CorrelationId <- generateCorrelationId ()
            use! response = state.Client.PostAsync("/branch/create", createJsonContent parameters)
            let! _ = requireOkAsync "POST /branch/create" response
            return! getBranchAsync state ownerId organizationId repositoryId branchId
        }

    /// Creates one child branch with the default writable permissions and a caller-owned automatic Rebase Reference identity.
    let createBranchAsync state ownerId organizationId repositoryId (parent: Branch.BranchDto) index rebaseReferenceId =
        let defaults =
            Parameters
                .Branch
                .CreateBranchParameters()
                .InitialPermissions

        createBranchWithPermissionsAsync state ownerId organizationId repositoryId parent index rebaseReferenceId defaults

    /// Creates one explicit Save Reference with a caller-owned identity.
    let saveReferenceAsync state ownerId organizationId repositoryId (asset: BaselineAsset) =
        task {
            let parameters = Parameters.Branch.CreateReferenceParameters()
            parameters.OwnerId <- string ownerId
            parameters.OrganizationId <- string organizationId
            parameters.RepositoryId <- string repositoryId
            parameters.BranchId <- string asset.Branch.BranchId
            parameters.ReferenceId <- asset.SaveReferenceId
            parameters.DirectoryVersionId <- asset.Root.DirectoryVersionId
            parameters.Sha256Hash <- asset.Root.Sha256Hash
            parameters.Message <- "MCA-08B-R1 Baseline explicit Save"
            parameters.CorrelationId <- generateCorrelationId ()
            use! response = state.Client.PostAsync("/branch/save", createJsonContent parameters)
            let! _ = requireOkAsync "POST /branch/save" response
            let! references = getBranchReferencesAsync state ownerId organizationId repositoryId asset.Branch.BranchId

            return
                references
                |> Array.tryFind (fun reference -> reference.ReferenceId = asset.SaveReferenceId)
                |> Option.defaultWith (fun () -> invalidOp $"Save Reference {asset.SaveReferenceId} was not durably observable.")
        }

    /// Reads persisted actor snapshots of one exact grain type from the isolated Cosmos container.
    let readActorSnapshotsAsync<'T> state grainType =
        task {
            use client = AspireTestHost.createCosmosClient state
            let container = client.GetContainer(state.CosmosDatabaseName, state.CosmosContainerName)

            let query =
                QueryDefinition("SELECT * FROM c WHERE c.GrainType = @grainType")
                    .WithParameter("@grainType", grainType)

            use iterator = container.GetItemQueryIterator<Dictionary<string, obj>>(query)
            let snapshots = ResizeArray<'T>()

            while iterator.HasMoreResults do
                let! page = iterator.ReadNextAsync()
                let documents = page |> Seq.toArray
                let mutable index = 0

                while index < documents.Length do
                    match documents[ index ].TryGetValue "State" with
                    | true, (:? JsonElement as value) -> snapshots.Add(JsonSerializer.Deserialize<'T>(value.GetRawText(), Constants.JsonSerializerOptions))
                    | _ -> ()

                    index <- index + 1

            return snapshots.ToArray()
        }

    /// Reads persisted event streams for one exact grain type from the isolated Cosmos container.
    let readActorEventStreamsAsync<'T> state grainType =
        task {
            let! streams = readActorSnapshotsAsync<List<'T>> state grainType
            return streams |> Array.map Seq.toArray
        }

    /// Tests one exact relationship by its canonical Cosmos identity.
    let exactRelationshipExistsAsync state relationship =
        task {
            let key =
                ExactRelationshipKey.create relationship
                |> Result.defaultWith invalidOp

            use client = AspireTestHost.createCosmosClient state
            let container = client.GetContainer(state.CosmosDatabaseName, state.CosmosContainerName)

            try
                let! _ = container.ReadItemAsync<JsonElement>(key.ItemId, PartitionKey key.PartitionKey)
                return true
            with
            | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.NotFound -> return false
        }

    /// Enumerates one complete exact-relationship partition through its canonical identity.
    let readExactRelationshipsAsync state partition =
        task {
            let partitionKey =
                ExactRelationshipKey.createPartitionKey partition
                |> Result.defaultWith invalidOp

            use client = AspireTestHost.createCosmosClient state
            let container = client.GetContainer(state.CosmosDatabaseName, state.CosmosContainerName)
            let options = QueryRequestOptions(PartitionKey = Nullable(PartitionKey partitionKey), MaxItemCount = Nullable 100)

            use iterator = container.GetItemQueryIterator<JsonElement>(QueryDefinition("SELECT c.id, c.PartitionKey FROM c"), requestOptions = options)

            let relationships = ResizeArray<ExactRelationship>()

            while iterator.HasMoreResults do
                let! page = iterator.ReadNextAsync()

                page
                |> Seq.iter (fun document ->
                    ExactRelationshipKey.tryParse
                        { PartitionKey = document.GetProperty("PartitionKey").GetString(); ItemId = document.GetProperty("id").GetString() }
                    |> Result.iter relationships.Add)

            return relationships.ToArray()
        }

    /// Captures all five independent durable assertions at one point in time.
    let readDurableStatusAsync state repositoryId (assets: BaselineAsset array) =
        task {
            let mutable referenceRoots = true
            let mutable manifestRelationships = true
            let mutable index = 0

            while index < assets.Length do
                let asset = assets[index]

                let! hasRoot =
                    exactRelationshipExistsAsync
                        state
                        (ExactRelationship.ReferenceRoot
                            { RepositoryId = repositoryId; RootDirectoryVersionId = asset.Root.DirectoryVersionId; ReferenceId = asset.SaveReferenceId })

                let! hasManifest =
                    exactRelationshipExistsAsync
                        state
                        (ExactRelationship.DirectoryVersionManifest
                            {
                                RepositoryId = repositoryId
                                StoragePoolId = asset.Manifest.StoragePoolId
                                ManifestAddress = asset.Manifest.ManifestAddress
                                DirectoryVersionId = asset.Root.DirectoryVersionId
                            })

                referenceRoots <- referenceRoots && hasRoot
                manifestRelationships <- manifestRelationships && hasManifest
                index <- index + 1

            let! counters = readActorSnapshotsAsync<RepositoryContentCounterDto> state "RepoContentCounter"
            let! workflows = readActorSnapshotsAsync<ManifestContributionWorkflowDto> state "ManifestContributionWorkflow"
            let! metadataStreams = readActorEventStreamsAsync<ContentBlockMetadataEvent> state "ContentBlockMetadata"

            let metadata =
                metadataStreams
                |> Array.map (fun events ->
                    events
                    |> Array.fold (fun current event -> ContentBlockMetadataDto.UpdateDto event current) ContentBlockMetadataDto.Empty)

            let logicalCounts =
                assets
                |> Array.forall (fun asset ->
                    counters
                    |> Array.filter (fun counter ->
                        counter.RepositoryId = repositoryId
                        && counter.StoragePoolId = asset.Manifest.StoragePoolId
                        && counter.ManifestAddress = asset.Manifest.ManifestAddress
                        && counter.Count = 1L)
                    |> Array.length
                    |> (=) 1)

            let workflowCounts =
                assets
                |> Array.forall (fun asset ->
                    workflows
                    |> Array.filter (fun workflow ->
                        workflow.RepositoryId = repositoryId
                        && workflow.StoragePoolId = asset.Manifest.StoragePoolId
                        && workflow.ManifestAddress = asset.Manifest.ManifestAddress
                        && workflow.Direction = ManifestContributionDirection.Increment
                        && workflow.CounterRevision = 1L
                        && workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
                        && workflow.Ranges.Length = 1
                        && workflow.CompletedRanges.Length = 1
                        && workflow.FailedRanges.Length = 0)
                    |> Array.length
                    |> (=) 1)

            let physicalActiveCounts =
                assets
                |> Array.forall (fun asset ->
                    metadata
                    |> Array.filter (fun dto ->
                        dto.Metadata
                        |> Option.exists (fun value ->
                            value.StoragePoolId = asset.Manifest.StoragePoolId
                            && value.ContentBlockAddress = asset.BlockAddress
                            && value.Ranges.Length > 0
                            && value.Ranges
                               |> Array.forall (fun range -> range.ActiveManifestCount = 1)))
                    |> Array.length
                    |> (=) 1)

            return
                {
                    ReferenceRoots = referenceRoots
                    ManifestRelationships = manifestRelationships
                    LogicalCounts = logicalCounts
                    WorkflowCounts = workflowCounts
                    PhysicalActiveCounts = physicalActiveCounts
                    Detail =
                        $"roots={referenceRoots}; manifests={manifestRelationships}; counters={logicalCounts}; workflows={workflowCounts}; physical={physicalActiveCounts}"
                }
        }

    /// Waits for every durable assertion using state signals rather than a correctness sleep.
    let waitForDurableStatusAsync state repositoryId assets =
        task {
            let timeoutAt = DateTime.UtcNow.AddSeconds(45.0)

            let mutable status =
                {
                    ReferenceRoots = false
                    ManifestRelationships = false
                    LogicalCounts = false
                    WorkflowCounts = false
                    PhysicalActiveCounts = false
                    Detail = "not observed"
                }

            let complete value =
                value.ReferenceRoots
                && value.ManifestRelationships
                && value.LogicalCounts
                && value.WorkflowCounts
                && value.PhysicalActiveCounts

            while not (complete status)
                  && DateTime.UtcNow < timeoutAt do
                let! currentStatus = readDurableStatusAsync state repositoryId assets
                status <- currentStatus

                if not (complete status) then do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            return status
        }

    /// Adds one typed sample with stable labels.
    let recordSample (writer: EvidenceWriter) runId sampleId name value labels =
        writer.Append(MeasurementSample.Create(runId, "baseline", sampleId, name, value, labels))

    /// Verifies every pre-summary record is bounded, complete JSON with the declared typed discriminator.
    let verifyEvidenceIntegrity (writer: EvidenceWriter) =
        let bytes = File.ReadAllBytes writer.Path

        let noBom =
            bytes.Length < 3
            || bytes[0..2] <> [| 0xEFuy; 0xBBuy; 0xBFuy |]

        let lines = File.ReadAllLines writer.Path

        noBom
        && lines.Length > 0
        && lines
           |> Array.forall (fun line ->
               Encoding.UTF8.GetByteCount line
               <= MaximumRecordBytes
               && try
                   use document = JsonDocument.Parse line

                   let recordType =
                       document
                           .RootElement
                           .GetProperty("RecordType")
                           .GetString()

                   not (String.IsNullOrWhiteSpace recordType)
                  with
                  | :? JsonException -> false)

/// Covers redirected Git process output without starting the Aspire fixture.
[<TestFixture>]
type GitProcessDrainageTests() =

    /// Verifies a status result larger than a process pipe is drained without deadlock or omission.
    [<Test>]
    member _.``large redirected status output is drained concurrently``() =
        task {
            let directory = Path.Combine(Path.GetTempPath(), $"grace-git-drainage-{Guid.NewGuid():N}")
            Directory.CreateDirectory(directory) |> ignore

            try
                let! _ = BaselineRuntime.runGitAsync directory [| "init"; "--quiet" |]
                let fileCount = 2048

                Array.init fileCount (fun index -> Path.Combine(directory, $"untracked-{index:D4}-{String('x', 48)}.txt"))
                |> Array.iter (fun path -> File.WriteAllText(path, "x"))

                let! status =
                    BaselineRuntime.runGitAsync
                        directory
                        [|
                            "status"
                            "--porcelain=v1"
                            "--untracked-files=all"
                        |]

                let paths = status.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                Assert.That(paths, Has.Length.EqualTo(fileCount))
            finally
                Directory.Delete(directory, true)
        }

/// Proves one reproducible Baseline evidence packet in a fresh explicitly selected test process.
[<NonParallelizable>]
type ManifestContributionBaselineMeasurementTests() =

    /// Emits truthful setup, stimulus, durable, settlement, identity, cleanup, and evidence results for the Baseline topology.
    [<Test; Explicit("Run only through the focused MCA Baseline measurement selector.")>]
    member _.``isolated Baseline emits truthful completed evidence``() =
        task {
            let runId = Guid.NewGuid().ToString("N")

            let worktree =
                BaselineRuntime.requireEnvironment "GRACE_MCA_WORKTREE"
                |> Path.GetFullPath

            let command = BaselineRuntime.requireEnvironment "GRACE_MCA_HOSTED_COMMAND"

            let evidenceRoot =
                BaselineRuntime.requireEnvironment "GRACE_MCA_EVIDENCE_ROOT"
                |> Path.GetFullPath

            let evidenceDirectory = Path.Combine(evidenceRoot, runId)
            let! commitSha = BaselineRuntime.runGitAsync worktree [| "rev-parse"; "HEAD" |]

            let! status =
                BaselineRuntime.runGitAsync
                    worktree
                    [|
                        "status"
                        "--porcelain=v1"
                        "--untracked-files=all"
                    |]

            let worktreeState = if String.IsNullOrWhiteSpace status then "clean" else status
            use writer = new EvidenceWriter(evidenceDirectory, BaselineRuntime.MaximumRecordBytes)
            let plan = [| "baseline" |]
            writer.Append(MeasurementRun.Create(runId, commitSha, worktree, worktreeState, command, evidenceDirectory, plan))
            let assertions = ResizeArray<MeasurementAssertion>()
            let failures = ResizeArray<string>()
            let mutable host: TestHostState option = None

            let recordAssertion assertionId passed detail =
                let assertion = MeasurementAssertion.Create(runId, "baseline", assertionId, passed, detail)
                assertions.Add assertion
                writer.Append assertion

            try
                let bootstrapUserId = Guid.NewGuid().ToString("D")
                let! state = ManifestContributionGroupedRuntime.acquireAsync bootstrapUserId
                host <- Some state
                ManifestContributionGroupedRuntime.selectBootstrapUser state bootstrapUserId
                let! _ = AspireTestHost.drainServiceBusAsync state
                let ownerId = Guid.NewGuid()
                let organizationId = Guid.NewGuid()
                let repositoryId = Guid.NewGuid()
                ManifestContributionGroupedRuntime.registerRepository "baseline" repositoryId
                do! BaselineRuntime.createOwnerAsync state ownerId
                do! BaselineRuntime.createOrganizationAsync state ownerId organizationId
                let! defaultBranchId, defaultReferenceId = BaselineRuntime.createRepositoryAsync state ownerId organizationId repositoryId
                let! defaultBranch = BaselineRuntime.getBranchAsync state ownerId organizationId repositoryId defaultBranchId

                if defaultBranch.LatestReference.ReferenceId
                   <> defaultReferenceId then
                    invalidOp "The repository default Reference inventory did not match its persisted branch."

                let defaultMessageId = $"Reference/{defaultReferenceId}/Created"
                let! defaultObserved = BaselineRuntime.observeReferenceEnvelopesAsync state [| defaultMessageId |] "repository default"
                let! _ = BaselineRuntime.waitForCompletedSettlementSamplesAsync state
                let assetsWithoutBranches = ResizeArray<ContentBlockAddress * FileManifest * DirectoryVersion>()
                let mutable assetIndex = 0

                while assetIndex < BaselineRuntime.SelectedTopologyCount do
                    let! blockAddress, manifest, bytes = BaselineRuntime.createManifestAssetAsync state ownerId organizationId repositoryId assetIndex

                    let root = BaselineRuntime.createRoot ownerId organizationId repositoryId assetIndex manifest bytes
                    do! BaselineRuntime.saveRootAsync state ownerId organizationId repositoryId root
                    assetsWithoutBranches.Add(blockAddress, manifest, root)
                    assetIndex <- assetIndex + 1

                let! setupBaseline = BaselineRuntime.scrapeMetricsAsync state
                let assets = ResizeArray<BaselineAsset>()
                assetIndex <- 0

                while assetIndex < assetsWithoutBranches.Count do
                    let blockAddress, manifest, root = assetsWithoutBranches[assetIndex]
                    let rebaseReferenceId = Guid.NewGuid()
                    let saveReferenceId = Guid.NewGuid()

                    let! branch = BaselineRuntime.createBranchAsync state ownerId organizationId repositoryId defaultBranch assetIndex rebaseReferenceId

                    assets.Add(
                        {
                            BlockAddress = blockAddress
                            Manifest = manifest
                            Root = root
                            Branch = branch
                            RebaseReferenceId = rebaseReferenceId
                            SaveReferenceId = saveReferenceId
                        }
                    )

                    assetIndex <- assetIndex + 1

                let setupMessageIds =
                    assets
                    |> Seq.map (fun asset -> $"Reference/{asset.RebaseReferenceId}/Created")
                    |> Seq.toArray

                let! setupObserved = BaselineRuntime.observeReferenceEnvelopesAsync state setupMessageIds "branch Rebase"

                let! setupMessageDelta, setupDurationDelta, saveBaseline =
                    BaselineRuntime.waitForCompletedSettlementDeltaAsync state (int64 assets.Count) setupBaseline

                recordAssertion
                    "baseline.setup-deliveries-completed"
                    (setupObserved.Length = assets.Count
                     && setupMessageDelta = int64 assets.Count
                     && setupDurationDelta = int64 assets.Count)
                    $"observed={setupObserved.Length}; messages={setupMessageDelta}; durations={setupDurationDelta}"

                let labels = Dictionary<string, string>()
                labels["stage"] <- "settle"
                labels["outcome"] <- "completed"
                BaselineRuntime.recordSample writer runId "setup-messages" "grace_manifest_contribution_messages_total.delta" setupMessageDelta labels

                BaselineRuntime.recordSample
                    writer
                    runId
                    "setup-durations"
                    "grace_manifest_contribution_processing_duration_milliseconds_count.delta"
                    setupDurationDelta
                    labels

                let saveEnvelopes = ResizeArray<Reference.ReferenceDto>()
                assetIndex <- 0

                while assetIndex < assets.Count do
                    let! envelope = BaselineRuntime.saveReferenceAsync state ownerId organizationId repositoryId assets[assetIndex]
                    saveEnvelopes.Add envelope
                    assetIndex <- assetIndex + 1

                let saveMessageIds =
                    assets
                    |> Seq.map (fun asset -> $"Reference/{asset.SaveReferenceId}/Created")
                    |> Seq.toArray

                let! saveObserved = BaselineRuntime.observeReferenceEnvelopesAsync state saveMessageIds "explicit Save"
                let assetArray = assets.ToArray()
                let! durable = BaselineRuntime.waitForDurableStatusAsync state repositoryId assetArray
                recordAssertion "baseline.reference-root-set" durable.ReferenceRoots durable.Detail
                recordAssertion "baseline.manifest-relationship-set" durable.ManifestRelationships durable.Detail
                recordAssertion "baseline.logical-counts" durable.LogicalCounts durable.Detail
                recordAssertion "baseline.workflow-counts" durable.WorkflowCounts durable.Detail
                recordAssertion "baseline.physical-active-counts" durable.PhysicalActiveCounts durable.Detail

                let! saveMessageDelta, saveDurationDelta, _ = BaselineRuntime.waitForCompletedSettlementDeltaAsync state (int64 assets.Count) saveBaseline

                let persistedSaves =
                    saveEnvelopes.Count = assets.Count
                    && saveEnvelopes
                       |> Seq.forall (fun envelope ->
                           envelope.ReferenceType = ReferenceType.Save
                           && assets
                              |> Seq.exists (fun asset ->
                                  asset.SaveReferenceId = envelope.ReferenceId
                                  && asset.Root.DirectoryVersionId = envelope.DirectoryId))

                recordAssertion
                    "baseline.stimulus-deliveries-completed"
                    (persistedSaves
                     && saveObserved.Length = assets.Count
                     && saveMessageDelta = int64 assets.Count
                     && saveDurationDelta = int64 assets.Count)
                    $"persisted={persistedSaves}; observed={saveObserved.Length}; messages={saveMessageDelta}; durations={saveDurationDelta}"

                recordAssertion "baseline.message-delta" (saveMessageDelta = int64 assets.Count) $"delta={saveMessageDelta}"
                recordAssertion "baseline.duration-delta" (saveDurationDelta = int64 assets.Count) $"delta={saveDurationDelta}"

                let allExpected =
                    Array.concat [| [| defaultMessageId |]
                                    setupMessageIds
                                    saveMessageIds |]

                let allObserved =
                    Array.concat [| defaultObserved
                                    setupObserved
                                    saveObserved |]
                    |> Array.map (fun envelope -> envelope.MessageId)

                let identityErrors = ProducerInventory.validate allExpected allObserved
                recordAssertion "baseline.identity-isolation" (identityErrors.Length = 0) (String.Join("; ", identityErrors))
                BaselineRuntime.recordSample writer runId "stimulus-messages" "grace_manifest_contribution_messages_total.delta" saveMessageDelta labels

                BaselineRuntime.recordSample
                    writer
                    runId
                    "stimulus-durations"
                    "grace_manifest_contribution_processing_duration_milliseconds_count.delta"
                    saveDurationDelta
                    labels
            with
            | ex -> failures.Add(ex.ToString())

            match host with
            | Some state ->
                try
                    do! ManifestContributionGroupedRuntime.releaseAsync state
                with
                | ex -> failures.Add($"cleanup: {ex}")
            | None -> ()

            if
                not
                    (
                        assertions
                        |> Seq.exists (fun assertion -> assertion.AssertionId = "baseline.evidence-integrity")
                    )
            then
                try
                    let valid = BaselineRuntime.verifyEvidenceIntegrity writer
                    recordAssertion "baseline.evidence-integrity" valid $"path={writer.Path}"
                with
                | ex -> recordAssertion "baseline.evidence-integrity" false ex.Message

            Baseline.requiredAssertionIds
            |> Array.iter (fun assertionId ->
                if
                    not
                        (
                            assertions
                            |> Seq.exists (fun assertion -> assertion.AssertionId = assertionId)
                        )
                then
                    recordAssertion assertionId false "The runtime failed before this assertion could be evaluated.")

            let summary = ScenarioSummary.derive runId "baseline" Baseline.requiredAssertionIds (assertions.ToArray()) (failures.ToArray()) false

            writer.Append summary
            TestContext.Progress.WriteLine($"MCA Baseline evidence directory: {evidenceDirectory}")
            TestContext.Progress.Flush()

            Assert.That(
                summary.Outcome,
                Is.EqualTo("Passed"),
                $"Evidence: {evidenceDirectory}{Environment.NewLine}{String.Join(Environment.NewLine, failures)}"
            )
        }
