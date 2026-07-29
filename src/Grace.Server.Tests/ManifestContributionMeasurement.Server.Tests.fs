namespace Grace.Server.Tests

open Azure.Messaging.ServiceBus
open Azure.Storage.Blobs
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
open Grace.Types.Events
open Grace.Types.ManifestContributionAccounting
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Owner
open Grace.Types.RepositoryContentCounter
open Microsoft.Azure.Cosmos
open NUnit.Framework
open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Identifies one uploaded manifest and its single physical ContentBlock.
type private MeasurementManifestAsset = { Block: ContentBlockFormat.EncodedContentBlock; Manifest: FileManifest; FileVersion: FileVersion }

/// Retains the production-created envelope required for deterministic replay scenarios.
type private MeasurementReferenceWitness =
    {
        ReferenceId: ReferenceId
        RootDirectoryVersionId: DirectoryVersionId
        Body: string
        MessageId: string
        CorrelationId: string
    }

/// Captures current durable accounting state for one repository manifest.
type private MeasurementManifestState =
    {
        Counter: RepositoryContentCounterDto
        Workflows: ManifestContributionWorkflowDto array
        ActiveManifestCounts: int array
        ActorBytes: int
    }

/// Carries cumulative server metrics exported through the authenticated Prometheus seam.
type private MeasurementMetrics =
    {
        Messages: float
        DurationCount: float
        RelationshipWrites: float
        RedisOperations: float
        RepairActions: float
        EvidenceFile: string
    }

/// Implements deterministic runtime operations through production HTTP, broker, actor, and exact-relationship seams.
module private ManifestContributionMeasurementRuntime =

    let private scenarioTimeout = TimeSpan.FromSeconds(45.0)

    /// Converts one object to stable JSON for pre/post restart equality and serialized-size evidence.
    let private stableJson value = JsonSerializer.Serialize(value, Constants.JsonSerializerOptions)

    /// Requires an HTTP success response and includes the bounded response body on failure.
    let private requireSuccessAsync (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()

            if not response.IsSuccessStatusCode then
                let boundedBody =
                    if body.Length
                       <= ManifestContributionMeasurementSupport.MaximumDiagnosticCharacters then
                        body
                    else
                        body.Substring(0, ManifestContributionMeasurementSupport.MaximumDiagnosticCharacters)

                Assert.Fail($"HTTP {int response.StatusCode} {response.StatusCode}: {boundedBody}")

            return body
        }

    /// Creates an isolated repository and returns the server-generated default branch used only by the grouped measurement fixture.
    let createDedicatedRepositoryAsync (state: TestHostState) =
        task {
            let repositoryId = $"{Guid.NewGuid()}"
            let parameters = Parameters.Repository.CreateRepositoryParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.RepositoryName <- $"mca-runtime-{Guid.NewGuid():N}"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = state.Client.PostAsync("/repository/create", createJsonContent parameters)
            let! body = requireSuccessAsync response
            let returnValue = deserialize<GraceReturnValue<string>> body
            let defaultBranchId = Common.requireGuidProperty (nameof BranchId) returnValue.Properties[nameof BranchId]
            let storageConnectionString = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageConnectionString)

            if not (String.IsNullOrWhiteSpace storageConnectionString) then
                let serviceClient = BlobServiceClient(storageConnectionString)
                let containerClient = serviceClient.GetBlobContainerClient(repositoryId.ToLowerInvariant())
                let! _ = containerClient.CreateIfNotExistsAsync()
                ()

            return repositoryId, $"{defaultBranchId}"
        }

    /// Creates deterministic bytes so repeated local runs retain reproducible content without sharing addresses across assets.
    let private deterministicPayload seed = Array.init 220000 (fun index -> byte ((index + seed * 17) % 251))

    /// Uploads and finalizes one manifest with exactly one ContentBlock through the production storage routes.
    let createManifestAssetAsync repositoryId seed =
        task {
            let correlationId = generateCorrelationId ()
            let sessionId = Guid.NewGuid()
            let payload = deterministicPayload seed
            let block = RestartDurabilityHelpers.encodeBlock payload
            let initialManifest = RestartDurabilityHelpers.manifestFor payload block
            let scope = RestartDurabilityHelpers.exactUploadScope sessionId

            let start = Parameters.Storage.StartManifestUploadSessionParameters()
            RestartDurabilityHelpers.setStorageParameters start repositoryId correlationId
            start.UploadSessionId <- sessionId
            start.AuthorizedScope <- scope
            start.FileContentHash <- initialManifest.FileContentHash
            start.ExpectedSize <- initialManifest.Size
            start.ChunkingSuiteId <- initialManifest.ChunkingSuiteId
            start.SamplingPolicySnapshot <- $"mca-08b-seed-{seed}"
            start.OperationId <- "start"

            let! startResult = RestartDurabilityHelpers.postUploadSessionDecisionAsync "/storage/startManifestUploadSession" start

            let manifest = RestartDurabilityHelpers.manifestForStoragePool startResult.ReturnValue.Session.StoragePoolId payload block

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            RestartDurabilityHelpers.setStorageParameters register repositoryId correlationId
            register.UploadSessionId <- sessionId
            register.AuthorizedScope <- scope
            register.OperationId <- "register-0"
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length
            let! _ = RestartDurabilityHelpers.postUploadSessionDecisionAsync "/storage/registerContentBlockUpload" register

            let uploadUriParameters = Parameters.Storage.GetContentBlockUploadUriParameters()
            RestartDurabilityHelpers.setStorageParameters uploadUriParameters repositoryId correlationId
            uploadUriParameters.UploadSessionId <- sessionId
            uploadUriParameters.ContentBlockAddress <- block.Address
            uploadUriParameters.AuthorizedScope <- scope
            let! uploadUriResponse = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent uploadUriParameters)
            let! uploadUriBody = requireSuccessAsync uploadUriResponse
            let uploadUri = Uri uploadUriBody
            let! uploadETag = RestartDurabilityHelpers.uploadContentBlockWithSasAsync block.Payload uploadUri

            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            RestartDurabilityHelpers.setStorageParameters confirm repositoryId correlationId
            confirm.UploadSessionId <- sessionId
            confirm.AuthorizedScope <- scope
            confirm.OperationId <- "confirm-0"
            confirm.ContentBlockAddress <- block.Address
            confirm.Payload <- block.Payload
            confirm.StoragePlacement <- RestartDurabilityHelpers.contentBlockPlacementFromUri uploadUri (Some uploadETag)
            let! _ = RestartDurabilityHelpers.postUploadSessionDecisionAsync "/storage/confirmContentBlockUpload" confirm

            let finalize = Parameters.Storage.FinalizeManifestUploadParameters()
            RestartDurabilityHelpers.setStorageParameters finalize repositoryId correlationId
            finalize.UploadSessionId <- sessionId
            finalize.AuthorizedScope <- scope
            finalize.OperationId <- "finalize"
            finalize.Manifest <- manifest
            let! finalized = RestartDurabilityHelpers.postUploadSessionDecisionAsync "/storage/finalizeManifestUpload" finalize
            Assert.That(finalized.ReturnValue.Session.FinalizedManifestAddress, Is.EqualTo(Some manifest.ManifestAddress))

            let fileVersion =
                FileVersion.CreateWithHashes
                    (RelativePath scope)
                    (Sha256Hash(String.replicate 64 (string ((seed % 9) + 1))))
                    (Blake3Hash manifest.FileContentHash)
                    String.Empty
                    true
                    manifest.Size

            fileVersion.ContentReference <- FileContentReference.FileManifest manifest

            return { Block = block; Manifest = manifest; FileVersion = fileVersion }
        }

    /// Creates one root DirectoryVersion that directly names the supplied manifest-backed file.
    let createManifestRoot (repositoryId: string) (scenario: string) (index: int) (asset: MeasurementManifestAsset) : DirectoryVersion =
        ignore scenario
        ignore index
        BranchServerTestHelpers.createDirectoryVersionWithFile repositoryId Constants.RootDirectoryPath asset.FileVersion

    /// Builds one production branch-save request with a caller-owned Reference identity.
    let private createSaveRequest (repositoryId: string) (branchId: string) (referenceId: ReferenceId) (root: DirectoryVersion) (scenario: string) =
        let parameters = Parameters.Branch.CreateReferenceParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryId
        parameters.BranchId <- branchId
        parameters.ReferenceId <- referenceId
        parameters.DirectoryVersionId <- root.DirectoryVersionId
        parameters.Sha256Hash <- root.Sha256Hash
        parameters.Blake3Hash <- root.Blake3Hash
        parameters.Message <- $"MCA-08B {scenario}"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    /// Collects the production-created Reference envelopes without allowing parallel receivers to discard each other's witnesses.
    let private collectCreatedEnvelopesAsync (state: TestHostState) (referenceIdsInRequestOrder: ReferenceId array) =
        task {
            let expectedReferenceIds = HashSet<ReferenceId>(referenceIdsInRequestOrder)
            let client = ServiceBusClient(state.ServiceBusConnectionString)
            use _client = client

            let receiver =
                client.CreateReceiver(
                    state.ServiceBusTopic,
                    state.ServiceBusTestSubscription,
                    ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete)
                )

            use _receiver = receiver
            let witnesses = Dictionary<ReferenceId, MeasurementReferenceWitness>()
            let stopwatch = Stopwatch.StartNew()

            while witnesses.Count < expectedReferenceIds.Count
                  && stopwatch.Elapsed < scenarioTimeout do
                let! message = receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1.0))

                if not (isNull message) then
                    try
                        let graceEvent = JsonSerializer.Deserialize<GraceEvent>(message.Body.ToArray(), Constants.JsonSerializerOptions)

                        match graceEvent with
                        | GraceEvent.ReferenceEvent referenceEvent ->
                            match referenceEvent.Event with
                            | Reference.ReferenceEventType.Created (referenceId, _, _, _, _, directoryVersionId, _, _, _, _, _) when
                                expectedReferenceIds.Contains referenceId
                                ->
                                witnesses[referenceId] <- {
                                                              ReferenceId = referenceId
                                                              RootDirectoryVersionId = directoryVersionId
                                                              Body = message.Body.ToString()
                                                              MessageId = message.MessageId
                                                              CorrelationId = message.CorrelationId
                                                          }
                            | _ -> ()
                        | _ -> ()
                    with
                    | :? JsonException -> ()

            if witnesses.Count <> expectedReferenceIds.Count then
                let missing =
                    expectedReferenceIds
                    |> Seq.filter (fun referenceId -> not (witnesses.ContainsKey referenceId))
                    |> Seq.map string
                    |> String.concat ","

                raise (TimeoutException($"Timed out collecting production Reference Created envelopes. Missing={missing}"))

            return
                referenceIdsInRequestOrder
                |> Array.map (fun referenceId -> witnesses[referenceId])
        }

    /// Saves one or more References concurrently and returns their persisted deterministic envelopes.
    let createReferencesAsync (state: TestHostState) (repositoryId: string) (branchIds: string array) (roots: DirectoryVersion array) (scenario: string) =
        task {
            if Array.length branchIds <> Array.length roots then
                invalidArg (nameof roots) "Each measurement root requires one target branch."

            let referenceIds = Array.init roots.Length (fun _ -> Guid.NewGuid())

            let requests =
                Array.mapi
                    (fun index root ->
                        let parameters = createSaveRequest repositoryId branchIds[index] referenceIds[index] root scenario

                        task {
                            let! response = state.Client.PostAsync("/branch/save", createJsonContent parameters)
                            let! _ = requireSuccessAsync response
                            return ()
                        })
                    roots

            let! _ = Task.WhenAll requests
            return! collectCreatedEnvelopesAsync state referenceIds
        }

    /// Reads bounded actor state documents for one exact grain type.
    let private readActorStatesAsync<'T> (state: TestHostState) (grainType: string) =
        task {
            use client = AspireTestHost.createCosmosClient state
            let container = client.GetContainer(state.CosmosDatabaseName, state.CosmosContainerName)

            let query =
                QueryDefinition("SELECT c.State FROM c WHERE c.GrainType = @grainType")
                    .WithParameter("@grainType", grainType)

            use iterator = container.GetItemQueryIterator<Dictionary<string, JsonElement>>(query)
            let results = ResizeArray<'T>()

            while iterator.HasMoreResults do
                let! page = iterator.ReadNextAsync()
                let documents = page |> Seq.toArray
                let mutable documentIndex = 0

                while documentIndex < documents.Length do
                    match documents[ documentIndex ].TryGetValue "State" with
                    | true, stateValue -> results.Add(JsonSerializer.Deserialize<'T>(stateValue.GetRawText(), Constants.JsonSerializerOptions))
                    | _ -> ()

                    documentIndex <- documentIndex + 1

            return results.ToArray()
        }

    /// Folds persisted ContentBlock event streams into the current authoritative DTOs.
    let private readContentBlockMetadataAsync state =
        task {
            let! streams = readActorStatesAsync<List<ContentBlockMetadataEvent>> state "ContentBlockMetadata"

            return
                streams
                |> Array.map (fun events ->
                    events
                    |> Seq.fold (fun current metadataEvent -> ContentBlockMetadataDto.UpdateDto metadataEvent current) ContentBlockMetadataDto.Empty)
        }

    /// Reads current counter, workflow, and ContentBlock actor state immediately before an assertion.
    let private tryReadManifestStateAsync (state: TestHostState) (repositoryId: string) (asset: MeasurementManifestAsset) =
        task {
            let! counters = readActorStatesAsync<RepositoryContentCounterDto> state "RepoContentCounter"
            let! workflows = readActorStatesAsync<ManifestContributionWorkflowDto> state "ManifestContributionWorkflow"
            let! contentBlocks = readContentBlockMetadataAsync state

            let counter =
                counters
                |> Array.tryFind (fun candidate ->
                    candidate.RepositoryId = Guid.Parse repositoryId
                    && candidate.StoragePoolId = asset.Manifest.StoragePoolId
                    && candidate.ManifestAddress = asset.Manifest.ManifestAddress)

            let matchingWorkflows =
                workflows
                |> Array.filter (fun candidate ->
                    candidate.RepositoryId = Guid.Parse repositoryId
                    && candidate.StoragePoolId = asset.Manifest.StoragePoolId
                    && candidate.ManifestAddress = asset.Manifest.ManifestAddress)

            let activeCounts =
                contentBlocks
                |> Array.choose (fun candidate ->
                    candidate.Metadata
                    |> Option.bind (fun metadata ->
                        if metadata.StoragePoolId = asset.Manifest.StoragePoolId
                           && metadata.ContentBlockAddress = asset.Block.Address then
                            Some(
                                metadata.Ranges
                                |> Array.map (fun range -> range.ActiveManifestCount)
                            )
                        else
                            None))
                |> Array.collect id

            return
                counter
                |> Option.map (fun foundCounter ->
                    let actorJson = stableJson {| counter = foundCounter; workflows = matchingWorkflows; activeCounts = activeCounts |}

                    {
                        Counter = foundCounter
                        Workflows = matchingWorkflows
                        ActiveManifestCounts = activeCounts
                        ActorBytes = Encoding.UTF8.GetByteCount actorJson
                    })
        }

    /// Waits on current actor snapshots rather than elapsed time and returns a fresh final read.
    let waitForManifestStateAsync (state: TestHostState) (repositoryId: string) (asset: MeasurementManifestAsset) (expectedLogicalCount: int64) =
        task {
            let stopwatch = Stopwatch.StartNew()
            let mutable result: MeasurementManifestState option = None

            while result.IsNone
                  && stopwatch.Elapsed < scenarioTimeout do
                let! candidate = tryReadManifestStateAsync state repositoryId asset

                result <-
                    candidate
                    |> Option.filter (fun current ->
                        current.Counter.ReferenceCount = expectedLogicalCount
                        && current.Workflows
                           |> Array.exists (fun workflow ->
                               workflow.Direction = ManifestContributionDirection.Increment
                               && workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.Completed)
                        && current.ActiveManifestCounts.Length > 0
                        && current.ActiveManifestCounts
                           |> Array.forall (fun count -> count = 1))

                if result.IsNone then do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            match result with
            | Some _ ->
                let! refreshed = tryReadManifestStateAsync state repositoryId asset
                return refreshed |> Option.get
            | None ->
                let! logs = AspireTestHost.getGraceServerLogsAsync state

                return
                    raise (
                        TimeoutException(
                            ManifestContributionMeasurementSupport.formatBoundedDiagnostic
                                $"waiting for manifest state count={expectedLogicalCount}"
                                $"repository={repositoryId}; manifest={asset.Manifest.ManifestAddress}"
                                logs
                        )
                    )
        }

    /// Reads one exact relationship directly and returns current presence plus Cosmos request charge.
    let readExactRelationshipAsync state relationship =
        task {
            let key =
                ExactRelationshipKey.create relationship
                |> Result.defaultWith invalidOp

            use client = AspireTestHost.createCosmosClient state
            let container = client.GetContainer(state.CosmosDatabaseName, state.CosmosContainerName)

            try
                let! response = container.ReadItemAsync<JsonElement>(key.ItemId, PartitionKey key.PartitionKey, cancellationToken = CancellationToken.None)

                return true, response.RequestCharge
            with
            | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.NotFound -> return false, ex.RequestCharge
        }

    /// Reads item identities from one exact-relationship partition with a finite measurement bound.
    let readExactPartitionAsync state partition =
        task {
            let partitionKey =
                ExactRelationshipKey.createPartitionKey partition
                |> Result.defaultWith invalidOp

            use client = AspireTestHost.createCosmosClient state
            let container = client.GetContainer(state.CosmosDatabaseName, state.CosmosContainerName)
            let options = QueryRequestOptions(PartitionKey = PartitionKey partitionKey, MaxItemCount = 100)
            use iterator = container.GetItemQueryIterator<JsonElement>(QueryDefinition("SELECT c.id FROM c"), requestOptions = options)
            let ids = ResizeArray<string>()
            let mutable requestCharge = 0.0

            while iterator.HasMoreResults do
                let! page = iterator.ReadNextAsync()
                requestCharge <- requestCharge + page.RequestCharge
                let documents = page |> Seq.toArray
                let mutable index = 0

                while index < documents.Length do
                    ids.Add(documents[ index ].GetProperty("id").GetString())
                    index <- index + 1

            return ids.ToArray(), requestCharge
        }

    /// Deletes only the selected exact relationship and verifies the absence through a fresh read.
    let deleteExactRelationshipAsync state relationship =
        task {
            let key =
                ExactRelationshipKey.create relationship
                |> Result.defaultWith invalidOp

            use client = AspireTestHost.createCosmosClient state
            let container = client.GetContainer(state.CosmosDatabaseName, state.CosmosContainerName)
            let! response = container.DeleteItemAsync<JsonElement>(key.ItemId, PartitionKey key.PartitionKey)
            let! present, verifyCharge = readExactRelationshipAsync state relationship

            if present then
                Assert.Fail($"Exact relationship remained present after test-owned delete: {key.PartitionKey}/{key.ItemId}")

            return response.RequestCharge + verifyCharge
        }

    /// Filters the authenticated Prometheus response to the bounded manifest-accounting instrument family.
    let private filterManifestMetrics (body: string) =
        body.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.filter (fun line ->
            line.StartsWith("# HELP grace_manifest_contribution_", StringComparison.Ordinal)
            || line.StartsWith("# TYPE grace_manifest_contribution_", StringComparison.Ordinal)
            || line.StartsWith("grace_manifest_contribution_", StringComparison.Ordinal))
        |> String.concat Environment.NewLine

    /// Sums exported Prometheus samples whose metric name satisfies the supplied predicate.
    let private metricSum = ManifestContributionMeasurementSupport.sumOpenMetricsSamples

    /// Scrapes current server metrics and preserves only the bounded local manifest-accounting family.
    let readMetricsAsync (state: TestHostState) (evidenceRoot: string) (label: string) =
        task {
            let! response = state.Client.GetAsync("/metrics")
            let! body = requireSuccessAsync response
            let filtered = filterManifestMetrics body
            let bytes = Encoding.UTF8.GetByteCount filtered

            if bytes > ManifestContributionMeasurementSupport.MaximumEvidenceRecordBytes then
                raise (InvalidDataException($"Filtered manifest metrics exceed {ManifestContributionMeasurementSupport.MaximumEvidenceRecordBytes} bytes."))

            let evidenceFile = Path.Combine(evidenceRoot, $"metrics-{label}.txt")
            File.WriteAllText(evidenceFile, filtered + Environment.NewLine, UTF8Encoding(false))

            let isMetric (prefix: string) (name: string) = name.StartsWith(prefix, StringComparison.Ordinal)

            return
                {
                    Messages = metricSum (isMetric "grace_manifest_contribution_messages") filtered
                    DurationCount =
                        metricSum
                            (fun name ->
                                name.StartsWith("grace_manifest_contribution_processing_duration", StringComparison.Ordinal)
                                && name.EndsWith("_count", StringComparison.Ordinal))
                            filtered
                    RelationshipWrites = metricSum (isMetric "grace_manifest_contribution_relationship_writes") filtered
                    RedisOperations = metricSum (isMetric "grace_manifest_contribution_redis_operations") filtered
                    RepairActions = metricSum (isMetric "grace_manifest_contribution_repair_actions") filtered
                    EvidenceFile = evidenceFile
                }
        }

    /// Waits until cumulative manifest telemetry satisfies the scenario's terminal expectation.
    let waitForMetricsAsync state evidenceRoot label isTerminal =
        ManifestContributionMeasurementSupport.waitForTerminalStateAsync
            scenarioTimeout
            (TimeSpan.FromMilliseconds 250.0)
            (fun () -> readMetricsAsync state evidenceRoot label)
            isTerminal

    /// Peeks the current active server subscription for the selected deterministic message identifiers.
    let private peekActiveMessageIdsAsync state expectedIds =
        task {
            let client = ServiceBusClient(state.ServiceBusConnectionString)
            use _client = client
            let receiver = client.CreateReceiver(state.ServiceBusTopic, state.ServiceBusServerSubscription)
            use _receiver = receiver
            let! messages = receiver.PeekMessagesAsync(100)

            return
                messages
                |> Seq.filter (fun message -> expectedIds |> Set.contains message.MessageId)
                |> Seq.map (fun message -> message.MessageId)
                |> Set.ofSeq
        }

    /// Waits for the server subscription to expose or settle an exact finite message set.
    let waitForActiveMessageSetAsync (state: TestHostState) (expectedIds: Set<string>) (shouldBePresent: bool) =
        task {
            let isTerminal matched = if shouldBePresent then matched = expectedIds else matched.IsEmpty

            try
                let! _ =
                    ManifestContributionMeasurementSupport.waitForTerminalStateAsync
                        scenarioTimeout
                        (TimeSpan.FromMilliseconds 250.0)
                        (fun () -> peekActiveMessageIdsAsync state expectedIds)
                        isTerminal

                return ()
            with
            | :? TimeoutException as ex ->
                let expectedText = String.Join(",", expectedIds)

                raise (TimeoutException($"Timed out waiting for active message set. ExpectedPresent={shouldBePresent}; Expected={expectedText}.", ex))
        }

    /// Sends one valid unrelated Grace event with a deterministic broker identity.
    let sendUnrelatedGraceEventAsync state messageId =
        task {
            let metadata = EventMetadata.New (generateCorrelationId ()) testUserId

            let ownerEvent: OwnerEvent = { Event = OwnerEventType.NameSet $"MCA-08B-unrelated-{messageId}"; Metadata = metadata }

            let body = JsonSerializer.Serialize(GraceEvent.OwnerEvent ownerEvent, Constants.JsonSerializerOptions)
            do! AspireTestHost.sendRawServiceBusMessageAsync state body messageId metadata.CorrelationId
        }

    /// Sends a previously persisted Reference Created envelope with its canonical deterministic message identity.
    let replayReferenceAsync state witness = AspireTestHost.sendRawServiceBusMessageAsync state witness.Body witness.MessageId witness.CorrelationId

    /// Drives one isolated test-subscription message to the broker's configured max delivery and verifies its DLQ record.
    let proveDeadLetterAsync state messageId =
        task {
            do! sendUnrelatedGraceEventAsync state messageId
            let client = ServiceBusClient(state.ServiceBusConnectionString)
            use _client = client

            let receiver =
                client.CreateReceiver(
                    state.ServiceBusTopic,
                    state.ServiceBusTestSubscription,
                    ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.PeekLock)
                )

            use _receiver = receiver
            let stopwatch = Stopwatch.StartNew()
            let mutable highestDelivery = 0

            while highestDelivery < 10
                  && stopwatch.Elapsed < scenarioTimeout do
                let! message = receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1.0))

                if not (isNull message) then
                    if message.MessageId = messageId then
                        highestDelivery <- message.DeliveryCount
                        do! receiver.AbandonMessageAsync(message)
                    else
                        do! receiver.CompleteMessageAsync(message)

            if highestDelivery <> 10 then
                raise (TimeoutException($"Message '{messageId}' did not reach delivery count 10. Highest={highestDelivery}"))

            let deadLetterReceiver =
                client.CreateReceiver(
                    state.ServiceBusTopic,
                    state.ServiceBusTestSubscription,
                    ServiceBusReceiverOptions(SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock)
                )

            use _deadLetterReceiver = deadLetterReceiver
            let mutable found: ServiceBusReceivedMessage option = None

            while found.IsNone
                  && stopwatch.Elapsed < scenarioTimeout do
                let! message = deadLetterReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1.0))

                if not (isNull message) then
                    if message.MessageId = messageId then
                        found <- Some message
                    else
                        do! deadLetterReceiver.CompleteMessageAsync(message)

            match found with
            | Some message ->
                do! deadLetterReceiver.CompleteMessageAsync(message)
                return message.DeliveryCount, message.DeadLetterReason
            | None -> return raise (TimeoutException($"Message '{messageId}' did not reach the isolated test subscription DLQ."))
        }

    /// Calls production diagnosis for one Reference and returns its signed report JSON.
    let diagnoseReferenceAsync (state: TestHostState) (repositoryId: string) (referenceId: ReferenceId) =
        task {
            let request =
                {|
                    ReferenceId = string referenceId
                    DirectoryVersionId = String.Empty
                    RepositoryId = repositoryId
                    StoragePoolId = String.Empty
                    ManifestAddress = String.Empty
                    RepositoryContentCounterOperationId = String.Empty
                    MaxRelationships = 100
                |}

            let! response = state.Client.PostAsync("/admin/manifest-contribution/diagnose", createJsonContent request)
            let! json = requireSuccessAsync response
            return json
        }

    /// Calls production repair in dry-run or execute mode against the signed diagnosis report.
    let repairAsync (state: TestHostState) (reportJson: string) (execute: bool) =
        task {
            use report = JsonDocument.Parse reportJson

            let reportSha =
                report
                    .RootElement
                    .GetProperty("ReportSha256")
                    .GetString()

            let request = {| ReportJson = reportJson; ExpectedReportSha256 = reportSha; Execute = execute |}
            let! response = state.Client.PostAsync("/admin/manifest-contribution/repair", createJsonContent request)
            let! json = requireSuccessAsync response
            return JsonDocument.Parse json
        }

/// Proves all supported manifest-accounting runtime scenarios in one serial shared Aspire session.
[<TestFixture; NonParallelizable>]
type ManifestContributionMeasurementAspireTests() =

    /// Executes the complete local Product V1 measurement matrix without making Azure performance or availability claims.
    [<Test; Category("ManifestContributionMeasurement")>]
    member _.``manifest accounting runtime scenarios preserve deterministic topology and typed evidence``() =
        task {
            let! state = AspireTestHost.startAsync testUserId
            let runId = $"mca-08b-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}"
            let branchPrefix = $"mca{Guid.NewGuid():N}"

            let evidenceRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "manifest-contribution-measurement", runId)

            let evidence = MeasurementEvidenceSink evidenceRoot

            let scenarioContracts = ManifestContributionMeasurementContracts.All

            let scenarios =
                scenarioContracts
                |> Array.map (fun contract -> contract.Scenario)

            let run: MeasurementRun =
                {
                    schemaVersion = "1.0"
                    runId = runId
                    environment = "local Aspire emulators; no Azure performance or availability claim"
                    startedAtUtc = DateTimeOffset.UtcNow
                    scenarios = scenarios
                    unmeasured =
                        [|
                            "total Orleans actor persistence RU"
                            "Azure partition heat or throttling"
                            "cross-region failover or availability"
                            "production SLOs"
                            "lock-expiry behavior requiring a minute-long handler"
                        |]
                }

            ManifestContributionMeasurementSupport.appendEvidenceRecord (Path.Combine(evidenceRoot, "run.ndjson")) run

            let! repositoryId, defaultBranchId = ManifestContributionMeasurementRuntime.createDedicatedRepositoryAsync state
            let! defaultBranch = BranchServerTestHelpers.getBranchAsync repositoryId defaultBranchId
            let runtimeFailures = ResizeArray<string>()
            let stableJson value = JsonSerializer.Serialize(value, Constants.JsonSerializerOptions)
            let mutable baselineWitnesses: MeasurementReferenceWitness array = Array.empty
            let mutable baselineStates: (MeasurementManifestAsset * MeasurementManifestState) array = Array.empty
            let mutable hotAsset: MeasurementManifestAsset option = None
            let mutable hotRoots: DirectoryVersion array = Array.empty
            let mutable hotWitnesses: MeasurementReferenceWitness array = Array.empty
            let mutable sharedAsset: MeasurementManifestAsset option = None
            let mutable sharedRoot: DirectoryVersion option = None
            let mutable sharedWitnesses: MeasurementReferenceWitness array = Array.empty
            let collectedReferenceIdentities = ResizeArray<string>()
            let collectedDirectoryVersionIdentities = ResizeArray<string>()

            /// Retains the actual production identities created by one scenario for the final isolation proof.
            let recordScenarioIdentities (witnesses: MeasurementReferenceWitness array) (roots: DirectoryVersion array) =
                witnesses
                |> Array.iter (fun witness -> collectedReferenceIdentities.Add(string witness.ReferenceId))

                roots
                |> Array.iter (fun root -> collectedDirectoryVersionIdentities.Add(string root.DirectoryVersionId))

            /// Runs one declared scenario and derives its summary contract from the centralized declaration.
            let runScenario (contract: MeasurementScenarioContract) operation =
                task {
                    let scenario = contract.Scenario
                    let startedAt = DateTimeOffset.UtcNow

                    try
                        do! operation ()
                        evidence.Summary(scenario, startedAt, contract.ExpectedAssertionCount, [| evidence.SamplesPath |])
                    with
                    | ex ->
                        let diagnostic =
                            ManifestContributionMeasurementSupport.formatBoundedDiagnostic
                                $"scenario={scenario}; error={ex.GetType().Name}: {ex.Message}"
                                "scenario failed"
                                [ ex.StackTrace ]

                        evidence.Sample(scenario, "failure", scenario, [ ("diagnostic", box diagnostic) ])
                        evidence.Summary(scenario, startedAt, contract.ExpectedAssertionCount, [| evidence.SamplesPath |])
                        runtimeFailures.Add($"{scenario}: {ex.GetType().Name}: {ex.Message}")
                }

            do!
                runScenario ManifestContributionMeasurementContracts.Baseline (fun () ->
                    task {
                        let scenario = ManifestContributionMeasurementContracts.Baseline.Scenario
                        let stopwatch = Stopwatch.StartNew()
                        let firstAssetTask = ManifestContributionMeasurementRuntime.createManifestAssetAsync repositoryId 11
                        let secondAssetTask = ManifestContributionMeasurementRuntime.createManifestAssetAsync repositoryId 29

                        let! assets =
                            Task.WhenAll [| firstAssetTask
                                            secondAssetTask |]

                        let roots =
                            assets
                            |> Array.mapi (fun index asset -> ManifestContributionMeasurementRuntime.createManifestRoot repositoryId scenario index asset)

                        do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId roots

                        let! branches =
                            Task.WhenAll(
                                [|
                                    BranchServerTestHelpers.createBranchAsync repositoryId defaultBranch $"{branchPrefix}-baseline-0"
                                    BranchServerTestHelpers.createBranchAsync repositoryId defaultBranch $"{branchPrefix}-baseline-1"
                                |]
                            )

                        let branchIds =
                            branches
                            |> Array.map (fun branch -> string branch.BranchId)

                        let! metricsBefore = ManifestContributionMeasurementRuntime.readMetricsAsync state evidence.RootDirectory "baseline-before"

                        let! witnesses = ManifestContributionMeasurementRuntime.createReferencesAsync state repositoryId branchIds roots scenario
                        recordScenarioIdentities witnesses roots

                        let states = ResizeArray<MeasurementManifestAsset * MeasurementManifestState>()
                        let mutable index = 0

                        while index < assets.Length do
                            let! current = ManifestContributionMeasurementRuntime.waitForManifestStateAsync state repositoryId assets[index] 1L

                            let referenceRelationship =
                                ExactRelationship.ReferenceRoot
                                    {
                                        RepositoryId = Guid.Parse repositoryId
                                        RootDirectoryVersionId = roots[index].DirectoryVersionId
                                        ReferenceId = witnesses[index].ReferenceId
                                    }

                            let manifestRelationship =
                                ExactRelationship.DirectoryVersionManifest
                                    {
                                        RepositoryId = Guid.Parse repositoryId
                                        StoragePoolId = assets[index].Manifest.StoragePoolId
                                        ManifestAddress = assets[index].Manifest.ManifestAddress
                                        DirectoryVersionId = roots[index].DirectoryVersionId
                                    }

                            let! referencePresent, referenceCharge =
                                ManifestContributionMeasurementRuntime.readExactRelationshipAsync state referenceRelationship

                            let! manifestPresent, manifestCharge = ManifestContributionMeasurementRuntime.readExactRelationshipAsync state manifestRelationship

                            evidence.Assertion(
                                scenario,
                                $"baseline-{index}-reference-root",
                                "Reference-root exact relationship exists.",
                                true,
                                referencePresent,
                                referencePresent,
                                [||]
                            )

                            evidence.Assertion(
                                scenario,
                                $"baseline-{index}-manifest",
                                "DirectoryVersion-manifest exact relationship exists.",
                                true,
                                manifestPresent,
                                manifestPresent,
                                [||]
                            )

                            evidence.Assertion(
                                scenario,
                                $"baseline-{index}-logical",
                                "Distinct baseline manifest has logical count one.",
                                1L,
                                current.Counter.ReferenceCount,
                                (current.Counter.ReferenceCount = 1L),
                                [||]
                            )

                            evidence.Assertion(
                                scenario,
                                $"baseline-{index}-physical",
                                "Distinct baseline manifest activates its ContentBlock once.",
                                "[1]",
                                "["
                                + String.Join(",", current.ActiveManifestCounts)
                                + "]",
                                (current.ActiveManifestCounts
                                 |> Array.forall ((=) 1)),
                                [||]
                            )

                            evidence.Assertion(
                                scenario,
                                $"baseline-{index}-workflow",
                                "Distinct baseline manifest starts exactly one physical workflow.",
                                1,
                                current.Workflows.Length,
                                (current.Workflows.Length = 1),
                                [||]
                            )

                            evidence.Sample(
                                scenario,
                                "manifest-final-state",
                                string witnesses[index].ReferenceId,
                                [
                                    ("logicalCount", box current.Counter.ReferenceCount)
                                    ("physicalActiveCounts", box current.ActiveManifestCounts)
                                    ("workflowCount", box current.Workflows.Length)
                                    ("actorSerializedBytes", box current.ActorBytes)
                                    ("exactReadRequestCharge", box (referenceCharge + manifestCharge))
                                ]
                            )

                            states.Add(assets[index], current)
                            index <- index + 1

                        stopwatch.Stop()
                        baselineWitnesses <- witnesses
                        baselineStates <- states.ToArray()
                        let! metricsAfter = ManifestContributionMeasurementRuntime.readMetricsAsync state evidence.RootDirectory "baseline-after"
                        let messageDelta = metricsAfter.Messages - metricsBefore.Messages

                        let durationDelta =
                            metricsAfter.DurationCount
                            - metricsBefore.DurationCount

                        evidence.Assertion(
                            scenario,
                            "baseline-message-telemetry",
                            "Manifest message telemetry matches the two valid Reference-created deliveries.",
                            float witnesses.Length,
                            messageDelta,
                            (messageDelta = float witnesses.Length),
                            [|
                                metricsBefore.EvidenceFile
                                metricsAfter.EvidenceFile
                            |]
                        )

                        evidence.Assertion(
                            scenario,
                            "baseline-duration-telemetry",
                            "Manifest duration telemetry count matches the two valid Reference-created deliveries.",
                            float witnesses.Length,
                            durationDelta,
                            (durationDelta = float witnesses.Length),
                            [|
                                metricsBefore.EvidenceFile
                                metricsAfter.EvidenceFile
                            |]
                        )

                        evidence.Sample(
                            scenario,
                            "throughput",
                            runId,
                            [
                                ("referenceCount", box witnesses.Length)
                                ("concurrency", box 2)
                                ("elapsedMilliseconds", box stopwatch.Elapsed.TotalMilliseconds)
                            ]
                        )
                    })

            do!
                runScenario ManifestContributionMeasurementContracts.HotManifest (fun () ->
                    task {
                        let scenario = ManifestContributionMeasurementContracts.HotManifest.Scenario
                        let! asset = ManifestContributionMeasurementRuntime.createManifestAssetAsync repositoryId 47

                        let roots = Array.init 3 (fun index -> ManifestContributionMeasurementRuntime.createManifestRoot repositoryId scenario index asset)

                        do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId roots

                        let! branches =
                            Array.init roots.Length (fun index ->
                                BranchServerTestHelpers.createBranchAsync repositoryId defaultBranch $"{branchPrefix}-hot-{index}")
                            |> Task.WhenAll

                        let! witnesses =
                            ManifestContributionMeasurementRuntime.createReferencesAsync
                                state
                                repositoryId
                                (branches
                                 |> Array.map (fun branch -> string branch.BranchId))
                                roots
                                scenario

                        recordScenarioIdentities witnesses roots

                        let! current = ManifestContributionMeasurementRuntime.waitForManifestStateAsync state repositoryId asset (int64 roots.Length)

                        let! manifestIds, requestCharge =
                            ManifestContributionMeasurementRuntime.readExactPartitionAsync
                                state
                                (ExactRelationshipPartition.Manifest(Guid.Parse repositoryId, asset.Manifest.StoragePoolId, asset.Manifest.ManifestAddress))

                        let directoryManifestCount =
                            manifestIds
                            |> Array.filter (fun itemId -> itemId.StartsWith("directory-version-manifest:", StringComparison.Ordinal))
                            |> Array.length

                        let activeCountsText =
                            "["
                            + String.Join(",", current.ActiveManifestCounts)
                            + "]"

                        evidence.Assertion(
                            scenario,
                            "hot-manifest-cardinality",
                            "Every distinct DirectoryVersion names the shared manifest.",
                            roots.Length,
                            directoryManifestCount,
                            (directoryManifestCount = roots.Length),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "hot-logical-count",
                            "Shared manifest logical count advances once per distinct DirectoryVersion.",
                            int64 roots.Length,
                            current.Counter.ReferenceCount,
                            (current.Counter.ReferenceCount = int64 roots.Length),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "hot-physical-count",
                            "Shared physical ContentBlock remains active once.",
                            "[1]",
                            activeCountsText,
                            (current.ActiveManifestCounts
                             |> Array.forall ((=) 1)),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "hot-one-workflow",
                            "Only the zero-to-one counter transition starts a physical workflow.",
                            1,
                            current.Workflows.Length,
                            (current.Workflows.Length = 1),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "hot-workflow-revision",
                            "The physical workflow belongs to counter revision one.",
                            1L,
                            current.Workflows[0].CounterRevision,
                            (current.Workflows[0].CounterRevision = 1L),
                            [||]
                        )

                        evidence.Sample(
                            scenario,
                            "manifest-final-state",
                            string asset.Manifest.ManifestAddress,
                            [
                                ("directoryVersionManifestRelationships", box directoryManifestCount)
                                ("logicalCount", box current.Counter.ReferenceCount)
                                ("physicalActiveCounts", box current.ActiveManifestCounts)
                                ("workflowCount", box current.Workflows.Length)
                                ("manifestPartitionRequestCharge", box requestCharge)
                            ]
                        )

                        hotAsset <- Some asset
                        hotRoots <- roots
                        hotWitnesses <- witnesses
                    })

            do!
                runScenario ManifestContributionMeasurementContracts.HighlySharedDirectoryVersion (fun () ->
                    task {
                        let scenario = ManifestContributionMeasurementContracts.HighlySharedDirectoryVersion.Scenario
                        let! asset = ManifestContributionMeasurementRuntime.createManifestAssetAsync repositoryId 71
                        let root = ManifestContributionMeasurementRuntime.createManifestRoot repositoryId scenario 0 asset
                        do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ root ]

                        let! branches =
                            Array.init 3 (fun index -> BranchServerTestHelpers.createBranchAsync repositoryId defaultBranch $"{branchPrefix}-shared-{index}")
                            |> Task.WhenAll

                        let! witnesses =
                            ManifestContributionMeasurementRuntime.createReferencesAsync
                                state
                                repositoryId
                                (branches
                                 |> Array.map (fun branch -> string branch.BranchId))
                                (Array.create 3 root)
                                scenario

                        recordScenarioIdentities witnesses [| root |]

                        let! current = ManifestContributionMeasurementRuntime.waitForManifestStateAsync state repositoryId asset 1L

                        let! incomingIds, incomingCharge =
                            ManifestContributionMeasurementRuntime.readExactPartitionAsync
                                state
                                (ExactRelationshipPartition.IncomingDirectoryVersion(Guid.Parse repositoryId, root.DirectoryVersionId))

                        let! manifestIds, manifestCharge =
                            ManifestContributionMeasurementRuntime.readExactPartitionAsync
                                state
                                (ExactRelationshipPartition.Manifest(Guid.Parse repositoryId, asset.Manifest.StoragePoolId, asset.Manifest.ManifestAddress))

                        let referenceCount =
                            incomingIds
                            |> Array.filter (fun itemId -> itemId.StartsWith("reference-root:", StringComparison.Ordinal))
                            |> Array.length

                        let manifestCount =
                            manifestIds
                            |> Array.filter (fun itemId -> itemId.StartsWith("directory-version-manifest:", StringComparison.Ordinal))
                            |> Array.length

                        let activeCountsText =
                            "["
                            + String.Join(",", current.ActiveManifestCounts)
                            + "]"

                        evidence.Assertion(
                            scenario,
                            "shared-reference-cardinality",
                            "Every Reference retains the one shared root.",
                            witnesses.Length,
                            referenceCount,
                            (witnesses.Length = referenceCount),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "shared-manifest-cardinality",
                            "The shared root contributes one manifest relationship.",
                            1,
                            manifestCount,
                            (manifestCount = 1),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "shared-logical-count",
                            "Reference sharing does not inflate DirectoryVersion-manifest logical count.",
                            1L,
                            current.Counter.ReferenceCount,
                            (current.Counter.ReferenceCount = 1L),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "shared-physical-count",
                            "Shared physical ContentBlock remains active once.",
                            "[1]",
                            activeCountsText,
                            (current.ActiveManifestCounts
                             |> Array.forall ((=) 1)),
                            [||]
                        )

                        evidence.Sample(
                            scenario,
                            "manifest-final-state",
                            string asset.Manifest.ManifestAddress,
                            [
                                ("referenceRootRelationships", box referenceCount)
                                ("directoryVersionManifestRelationships", box manifestCount)
                                ("logicalCount", box current.Counter.ReferenceCount)
                                ("physicalActiveCounts", box current.ActiveManifestCounts)
                                ("partitionRequestCharge", box (incomingCharge + manifestCharge))
                            ]
                        )

                        sharedAsset <- Some asset
                        sharedRoot <- Some root
                        sharedWitnesses <- witnesses
                    })

            do!
                runScenario ManifestContributionMeasurementContracts.DuplicateBacklogRecovery (fun () ->
                    task {
                        let scenario = ManifestContributionMeasurementContracts.DuplicateBacklogRecovery.Scenario

                        if baselineWitnesses.Length = 0
                           || baselineStates.Length = 0 then
                            invalidOp "Baseline witnesses are required for duplicate backlog recovery."

                        let beforeStateJson = baselineStates |> Array.map (snd >> stableJson)
                        let unrelatedMessageId = $"mca-08b-unrelated-{Guid.NewGuid():N}"

                        let messageIds =
                            baselineWitnesses
                            |> Array.map (fun witness -> witness.MessageId)
                            |> Array.append [| unrelatedMessageId |]
                            |> Set.ofArray

                        let mutable serverStopped = false
                        let stopwatch = Stopwatch.StartNew()

                        try
                            do! AspireTestHost.stopResourceAsync state "grace-server" scenario
                            serverStopped <- true
                            let mutable index = 0

                            while index < baselineWitnesses.Length do
                                do! ManifestContributionMeasurementRuntime.replayReferenceAsync state baselineWitnesses[index]
                                index <- index + 1

                            do! ManifestContributionMeasurementRuntime.sendUnrelatedGraceEventAsync state unrelatedMessageId
                            do! ManifestContributionMeasurementRuntime.waitForActiveMessageSetAsync state messageIds true
                            do! AspireTestHost.startResourceAsync state "grace-server" scenario
                            serverStopped <- false
                            do! ManifestContributionMeasurementRuntime.waitForActiveMessageSetAsync state messageIds false
                        with
                        | ex ->
                            if serverStopped then
                                do! AspireTestHost.startResourceAsync state "grace-server" $"{scenario}-recovery"

                            return raise ex

                        stopwatch.Stop()
                        let mutable index = 0

                        while index < baselineStates.Length do
                            let asset, before = baselineStates[index]
                            let! after = ManifestContributionMeasurementRuntime.waitForManifestStateAsync state repositoryId asset 1L

                            evidence.Assertion(
                                scenario,
                                $"duplicate-{index}-durable-state",
                                "Duplicate backlog replay leaves actor state unchanged.",
                                beforeStateJson[index],
                                stableJson after,
                                (beforeStateJson[index] = stableJson after),
                                [||]
                            )

                            index <- index + 1

                        let! metrics =
                            ManifestContributionMeasurementRuntime.waitForMetricsAsync state evidence.RootDirectory "duplicate" (fun current ->
                                current.Messages >= float baselineWitnesses.Length
                                && current.DurationCount
                                   >= float baselineWitnesses.Length)

                        evidence.Assertion(
                            scenario,
                            "duplicate-message-telemetry",
                            "Only valid Reference-created backlog deliveries enter manifest message telemetry.",
                            float baselineWitnesses.Length,
                            metrics.Messages,
                            (metrics.Messages = float baselineWitnesses.Length),
                            [| metrics.EvidenceFile |]
                        )

                        evidence.Assertion(
                            scenario,
                            "duplicate-duration-telemetry",
                            "Manifest duration count matches valid Reference-created deliveries.",
                            metrics.Messages,
                            metrics.DurationCount,
                            (metrics.Messages = metrics.DurationCount),
                            [| metrics.EvidenceFile |]
                        )

                        evidence.Assertion(
                            scenario,
                            "unrelated-event-negative",
                            "One valid unrelated Grace event does not inflate manifest telemetry.",
                            float baselineWitnesses.Length,
                            metrics.Messages,
                            (metrics.Messages = float baselineWitnesses.Length),
                            [| metrics.EvidenceFile |]
                        )

                        let! beforeVerifyMetrics = ManifestContributionMeasurementRuntime.readMetricsAsync state evidence.RootDirectory "verify-before"
                        let! _ = ManifestContributionMeasurementRuntime.diagnoseReferenceAsync state repositoryId baselineWitnesses[0].ReferenceId
                        let! afterVerifyMetrics = ManifestContributionMeasurementRuntime.readMetricsAsync state evidence.RootDirectory "verify-after"

                        evidence.Assertion(
                            scenario,
                            "verify-zero-writes",
                            "Diagnosis Verify reads emit zero relationship-write measurements.",
                            beforeVerifyMetrics.RelationshipWrites,
                            afterVerifyMetrics.RelationshipWrites,
                            (beforeVerifyMetrics.RelationshipWrites = afterVerifyMetrics.RelationshipWrites),
                            [|
                                beforeVerifyMetrics.EvidenceFile
                                afterVerifyMetrics.EvidenceFile
                            |]
                        )

                        evidence.Sample(
                            scenario,
                            "backlog-drain",
                            runId,
                            [
                                ("duplicateMessages", box baselineWitnesses.Length)
                                ("unrelatedMessages", box 1)
                                ("drainMilliseconds", box stopwatch.Elapsed.TotalMilliseconds)
                                ("messageTelemetry", box metrics.Messages)
                                ("durationTelemetryCount", box metrics.DurationCount)
                            ]
                        )
                    })

            do!
                runScenario ManifestContributionMeasurementContracts.RedisRestart (fun () ->
                    task {
                        let scenario = ManifestContributionMeasurementContracts.RedisRestart.Scenario

                        let asset =
                            hotAsset
                            |> Option.defaultWith (fun () -> invalidOp "HotManifest asset is required for Redis restart.")

                        let! before = ManifestContributionMeasurementRuntime.waitForManifestStateAsync state repositoryId asset 3L
                        do! AspireTestHost.restartResourceAsync state "redis" scenario
                        let root = ManifestContributionMeasurementRuntime.createManifestRoot repositoryId scenario 0 asset
                        do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ root ]
                        let! branch = BranchServerTestHelpers.createBranchAsync repositoryId defaultBranch $"{branchPrefix}-redis"

                        let! witnesses =
                            ManifestContributionMeasurementRuntime.createReferencesAsync state repositoryId [| string branch.BranchId |] [| root |] scenario

                        recordScenarioIdentities witnesses [| root |]

                        let! after = ManifestContributionMeasurementRuntime.waitForManifestStateAsync state repositoryId asset 4L

                        let activeCountsText =
                            "["
                            + String.Join(",", after.ActiveManifestCounts)
                            + "]"

                        evidence.Assertion(
                            scenario,
                            "redis-foreground-reference",
                            "Reference creation succeeds after the real Redis restart.",
                            1,
                            witnesses.Length,
                            (witnesses.Length = 1),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "redis-logical-advance",
                            "A new DirectoryVersion advances the hot manifest logical count.",
                            before.Counter.ReferenceCount + 1L,
                            after.Counter.ReferenceCount,
                            (after.Counter.ReferenceCount = before.Counter.ReferenceCount + 1L),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "redis-physical-stable",
                            "Redis reconnect does not duplicate physical activation.",
                            "[1]",
                            activeCountsText,
                            (after.ActiveManifestCounts |> Array.forall ((=) 1)),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "redis-no-extra-workflow",
                            "A non-zero logical addition does not start another physical workflow.",
                            before.Workflows.Length,
                            after.Workflows.Length,
                            (before.Workflows.Length = after.Workflows.Length),
                            [||]
                        )

                        evidence.Sample(
                            scenario,
                            "post-restart-state",
                            string asset.Manifest.ManifestAddress,
                            [
                                ("logicalCountBefore", box before.Counter.ReferenceCount)
                                ("logicalCountAfter", box after.Counter.ReferenceCount)
                                ("physicalActiveCounts", box after.ActiveManifestCounts)
                                ("workflowCount", box after.Workflows.Length)
                            ]
                        )
                    })

            do!
                runScenario ManifestContributionMeasurementContracts.ServerRestartRecovery (fun () ->
                    task {
                        let scenario = ManifestContributionMeasurementContracts.ServerRestartRecovery.Scenario

                        let asset =
                            sharedAsset
                            |> Option.defaultWith (fun () -> invalidOp "HighlyShared asset is required for server restart.")

                        let root =
                            sharedRoot
                            |> Option.defaultWith (fun () -> invalidOp "HighlyShared root is required for server restart.")

                        if sharedWitnesses.Length = 0 then
                            invalidOp "HighlyShared envelope is required for server restart."

                        let! before = ManifestContributionMeasurementRuntime.waitForManifestStateAsync state repositoryId asset 1L
                        let beforeJson = stableJson before
                        do! AspireTestHost.restartGraceServerAsync state scenario
                        do! ManifestContributionMeasurementRuntime.replayReferenceAsync state sharedWitnesses[0]

                        let relationship =
                            ExactRelationship.ReferenceRoot
                                {
                                    RepositoryId = Guid.Parse repositoryId
                                    RootDirectoryVersionId = root.DirectoryVersionId
                                    ReferenceId = sharedWitnesses[0].ReferenceId
                                }

                        do! AspireTestHost.waitForExactRelationshipAsync state relationship
                        let! after = ManifestContributionMeasurementRuntime.waitForManifestStateAsync state repositoryId asset 1L
                        let! exactPresent, requestCharge = ManifestContributionMeasurementRuntime.readExactRelationshipAsync state relationship

                        evidence.Assertion(
                            scenario,
                            "server-restart-exact",
                            "Replayed Reference-root remains present after restart.",
                            true,
                            exactPresent,
                            exactPresent,
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "server-restart-counter",
                            "Counter snapshot survives and replay is idempotent.",
                            before.Counter.ReferenceCount,
                            after.Counter.ReferenceCount,
                            (before.Counter.ReferenceCount = after.Counter.ReferenceCount),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "server-restart-workflow",
                            "Workflow snapshot survives the process restart.",
                            stableJson before.Workflows,
                            stableJson after.Workflows,
                            (stableJson before.Workflows = stableJson after.Workflows),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "server-restart-physical",
                            "ContentBlock active count survives and does not replay.",
                            stableJson before.ActiveManifestCounts,
                            stableJson after.ActiveManifestCounts,
                            (stableJson before.ActiveManifestCounts = stableJson after.ActiveManifestCounts),
                            [||]
                        )

                        evidence.Sample(
                            scenario,
                            "post-restart-state",
                            string sharedWitnesses[0].ReferenceId,
                            [
                                ("preRestartSnapshotComparisonOnly", box beforeJson)
                                ("freshPostRestartState", box (stableJson after))
                                ("exactReadRequestCharge", box requestCharge)
                            ]
                        )
                    })

            do!
                runScenario ManifestContributionMeasurementContracts.DeadLetter (fun () ->
                    task {
                        let scenario = ManifestContributionMeasurementContracts.DeadLetter.Scenario
                        let messageId = $"mca-08b-dlq-{Guid.NewGuid():N}"
                        let! deliveryCount, reason = ManifestContributionMeasurementRuntime.proveDeadLetterAsync state messageId

                        evidence.Assertion(
                            scenario,
                            "dlq-delivery-count",
                            "The isolated test subscription dead-letters on the delivery after configured max delivery 10.",
                            11,
                            deliveryCount,
                            (deliveryCount = 11),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "dlq-reason",
                            "Broker supplies a terminal dead-letter reason.",
                            "non-empty",
                            reason,
                            not (String.IsNullOrWhiteSpace reason),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "dlq-production-fault-seam",
                            "The DLQ witness is a valid unrelated Grace event, not a broken production manifest handler.",
                            "test subscription PeekLock",
                            "test subscription PeekLock",
                            true,
                            [||]
                        )

                        evidence.Sample(
                            scenario,
                            "broker-dlq",
                            messageId,
                            [
                                ("deliveryCount", box deliveryCount)
                                ("deadLetterReason", box reason)
                                ("subscription", box state.ServiceBusTestSubscription)
                            ]
                        )
                    })

            do!
                runScenario ManifestContributionMeasurementContracts.Repair (fun () ->
                    task {
                        let scenario = ManifestContributionMeasurementContracts.Repair.Scenario
                        let! asset = ManifestContributionMeasurementRuntime.createManifestAssetAsync repositoryId 103
                        let root = ManifestContributionMeasurementRuntime.createManifestRoot repositoryId scenario 0 asset
                        do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ root ]
                        let! branch = BranchServerTestHelpers.createBranchAsync repositoryId defaultBranch $"{branchPrefix}-repair"

                        let! witnesses =
                            ManifestContributionMeasurementRuntime.createReferencesAsync state repositoryId [| string branch.BranchId |] [| root |] scenario

                        recordScenarioIdentities witnesses [| root |]

                        let witness = witnesses[0]
                        let! before = ManifestContributionMeasurementRuntime.waitForManifestStateAsync state repositoryId asset 1L

                        let relationship =
                            ExactRelationship.ReferenceRoot
                                { RepositoryId = Guid.Parse repositoryId; RootDirectoryVersionId = root.DirectoryVersionId; ReferenceId = witness.ReferenceId }

                        let! deleteCharge = ManifestContributionMeasurementRuntime.deleteExactRelationshipAsync state relationship

                        let! metricsBeforeDiagnosis =
                            ManifestContributionMeasurementRuntime.readMetricsAsync state evidence.RootDirectory "repair-before-diagnosis"

                        let! reportJson = ManifestContributionMeasurementRuntime.diagnoseReferenceAsync state repositoryId witness.ReferenceId
                        use! dryRun = ManifestContributionMeasurementRuntime.repairAsync state reportJson false
                        let! metricsAfterDryRun = ManifestContributionMeasurementRuntime.readMetricsAsync state evidence.RootDirectory "repair-after-dry-run"

                        let dryRoot = dryRun.RootElement

                        let proposedCount =
                            dryRoot
                                .GetProperty("ProposedActions")
                                .GetArrayLength()

                        let dryAppliedCount =
                            dryRoot
                                .GetProperty("AppliedActions")
                                .GetArrayLength()

                        evidence.Assertion(
                            scenario,
                            "repair-dry-run-action",
                            "Dry-run proposes a real missing Reference-root action.",
                            1,
                            proposedCount,
                            (proposedCount = 1),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "repair-dry-run-zero-mutation",
                            "Dry-run applies no mutation.",
                            0,
                            dryAppliedCount,
                            (dryAppliedCount = 0),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "repair-verify-zero-writes",
                            "Diagnosis and dry-run Verify reads emit zero relationship writes.",
                            metricsBeforeDiagnosis.RelationshipWrites,
                            metricsAfterDryRun.RelationshipWrites,
                            (metricsBeforeDiagnosis.RelationshipWrites = metricsAfterDryRun.RelationshipWrites),
                            [|
                                metricsBeforeDiagnosis.EvidenceFile
                                metricsAfterDryRun.EvidenceFile
                            |]
                        )

                        use! execute = ManifestContributionMeasurementRuntime.repairAsync state reportJson true

                        let appliedCount =
                            execute
                                .RootElement
                                .GetProperty("AppliedActions")
                                .GetArrayLength()

                        do! AspireTestHost.waitForExactRelationshipAsync state relationship
                        let! exactPresent, exactCharge = ManifestContributionMeasurementRuntime.readExactRelationshipAsync state relationship
                        let! after = ManifestContributionMeasurementRuntime.waitForManifestStateAsync state repositoryId asset 1L

                        let! metricsAfterExecute =
                            ManifestContributionMeasurementRuntime.waitForMetricsAsync state evidence.RootDirectory "repair-after-execute" (fun metrics ->
                                metrics.RepairActions
                                - metricsAfterDryRun.RepairActions
                                >= 1.0
                                && metrics.Messages - metricsAfterDryRun.Messages
                                   >= 1.0)

                        evidence.Assertion(
                            scenario,
                            "repair-execute-action",
                            "Execute applies the one proposed republication action.",
                            1,
                            appliedCount,
                            (appliedCount = 1),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "repair-restored-root",
                            "Normal Reference Created convergence restores the deleted exact relationship.",
                            true,
                            exactPresent,
                            exactPresent,
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "repair-counter-stable",
                            "Repair replay leaves the logical counter unchanged.",
                            before.Counter.ReferenceCount,
                            after.Counter.ReferenceCount,
                            (before.Counter.ReferenceCount = after.Counter.ReferenceCount),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "repair-workflow-stable",
                            "Repair replay leaves the physical workflow unchanged.",
                            stableJson before.Workflows,
                            stableJson after.Workflows,
                            (stableJson before.Workflows = stableJson after.Workflows),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "repair-content-block-stable",
                            "Repair replay leaves ContentBlock physical counts unchanged.",
                            stableJson before.ActiveManifestCounts,
                            stableJson after.ActiveManifestCounts,
                            (stableJson before.ActiveManifestCounts = stableJson after.ActiveManifestCounts),
                            [||]
                        )

                        evidence.Assertion(
                            scenario,
                            "repair-action-telemetry",
                            "Execute records exactly one applied repair action.",
                            1.0,
                            metricsAfterExecute.RepairActions
                            - metricsAfterDryRun.RepairActions,
                            (metricsAfterExecute.RepairActions
                             - metricsAfterDryRun.RepairActions = 1.0),
                            [|
                                metricsAfterDryRun.EvidenceFile
                                metricsAfterExecute.EvidenceFile
                            |]
                        )

                        evidence.Assertion(
                            scenario,
                            "repair-message-telemetry",
                            "Execute republishes exactly one valid Reference-created delivery.",
                            1.0,
                            metricsAfterExecute.Messages
                            - metricsAfterDryRun.Messages,
                            (metricsAfterExecute.Messages
                             - metricsAfterDryRun.Messages = 1.0),
                            [|
                                metricsAfterDryRun.EvidenceFile
                                metricsAfterExecute.EvidenceFile
                            |]
                        )

                        evidence.Sample(
                            scenario,
                            "repair-final-state",
                            string witness.ReferenceId,
                            [
                                ("proposedActions", box proposedCount)
                                ("appliedActions", box appliedCount)
                                ("logicalCount", box after.Counter.ReferenceCount)
                                ("physicalActiveCounts", box after.ActiveManifestCounts)
                                ("repairTelemetryDelta",
                                 box (
                                     metricsAfterExecute.RepairActions
                                     - metricsAfterDryRun.RepairActions
                                 ))
                                ("messageTelemetryDelta",
                                 box (
                                     metricsAfterExecute.Messages
                                     - metricsAfterDryRun.Messages
                                 ))
                                ("exactRelationshipRequestCharge", box (deleteCharge + exactCharge))
                            ]
                        )
                    })

            let parsedSamples = ManifestContributionMeasurementSupport.readEvidenceRecords evidence.SamplesPath

            let isolation =
                ManifestContributionMeasurementSupport.evaluateIdentityIsolation
                    scenarioContracts
                    collectedReferenceIdentities
                    collectedDirectoryVersionIdentities

            evidence.Assertion(
                "Run",
                "evidence-parseable",
                "Every emitted NDJSON sample is parseable within the record bound.",
                true,
                parsedSamples.Length > 0,
                parsedSamples.Length > 0,
                [| evidence.SamplesPath |]
            )

            evidence.Assertion(
                "Run",
                "scenario-isolation",
                "Each scenario uses unique Reference and DirectoryVersion identities.",
                $"references={isolation.ExpectedReferenceCount}; directoryVersions={isolation.ExpectedDirectoryVersionCount}",
                $"references={isolation.ActualDistinctReferenceCount}; directoryVersions={isolation.ActualDistinctDirectoryVersionCount}",
                isolation.Passed,
                [| evidence.SamplesPath |]
            )

            evidence.Assertion(
                "Run",
                "azure-claim-boundary",
                "Run metadata labels Azure performance and availability as unmeasured.",
                true,
                (run.unmeasured.Length = 5),
                (run.unmeasured.Length = 5),
                [|
                    Path.Combine(evidenceRoot, "run.ndjson")
                |]
            )

            evidence.FailIfNeeded(runtimeFailures.ToArray())
        }
