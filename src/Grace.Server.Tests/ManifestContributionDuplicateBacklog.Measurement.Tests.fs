namespace Grace.Server.Measurements

open Azure.Messaging.ServiceBus
open Grace.Server.Tests
open Grace.Server.Tests.Measurement
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
open Grace.Types.Events
open Grace.Types.ManifestContributionAccounting
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Repository
open Grace.Types.RepositoryContentCounter
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Captures the five independently authoritative durable projections compared across duplicate replay.
type private DuplicateBacklogSnapshot =
    {
        ReferenceRoots: string array
        ManifestRelationships: string array
        LogicalState: string array
        WorkflowState: string array
        PhysicalState: string array
        IsConverged: bool
    }

/// Implements the duplicate-backlog scenario on the selected-process Baseline host and evidence boundary.
module private DuplicateBacklogRuntime =

    [<Literal>]
    let ScenarioId = "duplicate-backlog"

    /// Serializes a typed durable projection into the stable Grace JSON contract used for field-equivalent comparison.
    let private serialize value = JsonSerializer.Serialize(value, Constants.JsonSerializerOptions)

    /// Returns whether all five durable projections contain exactly one selected entry per replay envelope.
    let snapshotComplete expectedCount snapshot =
        snapshot.ReferenceRoots.Length = expectedCount
        && snapshot.ManifestRelationships.Length = expectedCount
        && snapshot.LogicalState.Length = expectedCount
        && snapshot.WorkflowState.Length = expectedCount
        && snapshot.PhysicalState.Length = expectedCount
        && snapshot.IsConverged
        && snapshot.ReferenceRoots
           |> Array.forall (fun value -> not (value.StartsWith("missing:", StringComparison.Ordinal)))
        && snapshot.ManifestRelationships
           |> Array.forall (fun value -> not (value.StartsWith("missing:", StringComparison.Ordinal)))

    /// Reads the complete selected exact, logical, workflow, and physical state without using Redis.
    let readSnapshotAsync state repositoryId (assets: BaselineAsset array) =
        task {
            let referenceRoots = ResizeArray<string>()
            let manifestRelationships = ResizeArray<string>()
            let mutable index = 0

            while index < assets.Length do
                let asset = assets[index]

                let referenceRoot =
                    ExactRelationship.ReferenceRoot
                        { RepositoryId = repositoryId; RootDirectoryVersionId = asset.Root.DirectoryVersionId; ReferenceId = asset.SaveReferenceId }

                let manifestRelationship =
                    ExactRelationship.DirectoryVersionManifest
                        {
                            RepositoryId = repositoryId
                            StoragePoolId = asset.Manifest.StoragePoolId
                            ManifestAddress = asset.Manifest.ManifestAddress
                            DirectoryVersionId = asset.Root.DirectoryVersionId
                        }

                let! referenceRootExists = BaselineRuntime.exactRelationshipExistsAsync state referenceRoot
                let! manifestRelationshipExists = BaselineRuntime.exactRelationshipExistsAsync state manifestRelationship

                referenceRoots.Add(
                    if referenceRootExists then
                        serialize referenceRoot
                    else
                        $"missing:{serialize referenceRoot}"
                )

                manifestRelationships.Add(
                    if manifestRelationshipExists then
                        serialize manifestRelationship
                    else
                        $"missing:{serialize manifestRelationship}"
                )

                index <- index + 1

            let! counters = BaselineRuntime.readActorSnapshotsAsync<RepositoryContentCounterDto> state "RepoContentCounter"
            let! workflows = BaselineRuntime.readActorSnapshotsAsync<ManifestContributionWorkflowDto> state "ManifestContributionWorkflow"
            let! metadataStreams = BaselineRuntime.readActorEventStreamsAsync<ContentBlockMetadataEvent> state "ContentBlockMetadata"

            let metadata =
                metadataStreams
                |> Array.map (fun events ->
                    events
                    |> Array.fold (fun current event -> ContentBlockMetadataDto.UpdateDto event current) ContentBlockMetadataDto.Empty)

            let selectedCounter (counter: RepositoryContentCounterDto) =
                counter.RepositoryId = repositoryId
                && assets
                   |> Array.exists (fun asset ->
                       counter.StoragePoolId = asset.Manifest.StoragePoolId
                       && counter.ManifestAddress = asset.Manifest.ManifestAddress)

            let selectedWorkflow (workflow: ManifestContributionWorkflowDto) =
                workflow.RepositoryId = repositoryId
                && assets
                   |> Array.exists (fun asset ->
                       workflow.StoragePoolId = asset.Manifest.StoragePoolId
                       && workflow.ManifestAddress = asset.Manifest.ManifestAddress)

            let selectedMetadata (dto: ContentBlockMetadataDto) =
                dto.Metadata
                |> Option.exists (fun value ->
                    assets
                    |> Array.exists (fun asset ->
                        value.StoragePoolId = asset.Manifest.StoragePoolId
                        && value.ContentBlockAddress = asset.BlockAddress))

            let selectedCounters = counters |> Array.filter selectedCounter

            let selectedWorkflows = workflows |> Array.filter selectedWorkflow

            let selectedMetadataRecords = metadata |> Array.filter selectedMetadata

            let logicalConverged =
                selectedCounters.Length = assets.Length
                && selectedCounters
                   |> Array.forall (fun counter -> counter.Count = 1L)

            let workflowConverged =
                selectedWorkflows.Length = assets.Length
                && selectedWorkflows
                   |> Array.forall (fun workflow ->
                       workflow.Direction = ManifestContributionDirection.Increment
                       && workflow.CounterRevision = 1L
                       && workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
                       && workflow.Ranges.Length = 1
                       && workflow.CompletedRanges.Length = 1
                       && workflow.FailedRanges.Length = 0)

            let physicalConverged =
                selectedMetadataRecords.Length = assets.Length
                && selectedMetadataRecords
                   |> Array.forall (fun dto ->
                       dto.Metadata
                       |> Option.exists (fun value ->
                           value.Ranges.Length > 0
                           && value.Ranges
                              |> Array.forall (fun range -> range.ActiveManifestCount = 1)))

            return
                {
                    ReferenceRoots = referenceRoots.ToArray() |> Array.sort
                    ManifestRelationships = manifestRelationships.ToArray() |> Array.sort
                    LogicalState =
                        selectedCounters
                        |> Array.map serialize
                        |> Array.sort
                    WorkflowState =
                        selectedWorkflows
                        |> Array.map serialize
                        |> Array.sort
                    PhysicalState =
                        selectedMetadataRecords
                        |> Array.map serialize
                        |> Array.sort
                    IsConverged =
                        logicalConverged
                        && workflowConverged
                        && physicalConverged
                }
        }

    /// Waits for every selected durable projection and returns the final complete or timed-out snapshot.
    let waitForSnapshotAsync state repositoryId (assets: BaselineAsset array) =
        task {
            let timeoutAt = DateTime.UtcNow.AddSeconds(45.0)

            let mutable snapshot =
                {
                    ReferenceRoots = Array.empty
                    ManifestRelationships = Array.empty
                    LogicalState = Array.empty
                    WorkflowState = Array.empty
                    PhysicalState = Array.empty
                    IsConverged = false
                }

            while not (snapshotComplete assets.Length snapshot)
                  && DateTime.UtcNow < timeoutAt do
                let! current = readSnapshotAsync state repositoryId assets
                snapshot <- current

                if not (snapshotComplete assets.Length snapshot) then
                    do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            return snapshot
        }

    /// Sends the exact persisted Reference envelopes and one valid non-Reference Grace event to the production topic.
    let publishBacklogAsync (state: TestHostState) (envelopes: CapturedReferenceEnvelope array) unrelatedMessageId =
        task {
            use client = new ServiceBusClient(state.ServiceBusConnectionString)
            let sender = client.CreateSender(state.ServiceBusTopic)

            try
                let mutable index = 0

                while index < envelopes.Length do
                    let envelope = envelopes[index]
                    let message = ServiceBusMessage(BinaryData(envelope.Body))
                    message.MessageId <- envelope.MessageId
                    message.CorrelationId <- envelope.CorrelationId
                    message.Subject <- envelope.Subject
                    message.ContentType <- envelope.ContentType

                    let properties = envelope.ApplicationProperties |> Seq.toArray
                    let mutable propertyIndex = 0

                    while propertyIndex < properties.Length do
                        let property = properties[propertyIndex]
                        message.ApplicationProperties[ property.Key ] <- property.Value
                        propertyIndex <- propertyIndex + 1

                    do! sender.SendMessageAsync message
                    index <- index + 1

                let unrelatedMetadata = EventMetadata.New (generateCorrelationId ()) "MCA-08B-R3"

                let unrelatedEvent = GraceEvent.RepositoryEvent { Event = RepositoryEventType.Initialized; Metadata = unrelatedMetadata }

                let unrelatedMessage = ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(unrelatedEvent, Constants.JsonSerializerOptions))

                unrelatedMessage.MessageId <- unrelatedMessageId
                unrelatedMessage.CorrelationId <- unrelatedMetadata.CorrelationId
                unrelatedMessage.Subject <- "GraceEvent"
                unrelatedMessage.ContentType <- "application/json"
                unrelatedMessage.ApplicationProperties[ "graceEventType" ] <- getDiscriminatedUnionFullName unrelatedEvent
                do! sender.SendMessageAsync unrelatedMessage
            finally
                sender
                    .DisposeAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
        }

    /// Peeks the server subscription until every selected replay identity has been observed at least once.
    let observeStoppedBacklogAsync (state: TestHostState) (selectedMessageIds: string array) =
        task {
            use client = new ServiceBusClient(state.ServiceBusConnectionString)
            let receiver = client.CreateReceiver(state.ServiceBusTopic, state.ServiceBusServerSubscription)
            let observed = HashSet<string>(StringComparer.Ordinal)
            let timeoutAt = DateTime.UtcNow.AddSeconds(15.0)
            let mutable complete = false

            while not complete && DateTime.UtcNow < timeoutAt do
                let! messages = receiver.PeekMessagesAsync(50)

                messages
                |> Seq.iter (fun message -> observed.Add message.MessageId |> ignore)

                complete <-
                    DuplicateBacklog.validateStoppedBacklogVisibility selectedMessageIds (observed |> Seq.toArray)
                    |> Array.isEmpty

                if not complete then do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            return observed |> Seq.toArray
        }

    /// Adds one typed duplicate-backlog sample with stable completed-settlement labels.
    let recordSample (writer: EvidenceWriter) runId sampleId name value =
        let labels = Dictionary<string, string>()
        labels["stage"] <- "settle"
        labels["outcome"] <- "completed"
        writer.Append(MeasurementSample.Create(runId, ScenarioId, sampleId, name, value, labels))

/// Proves deterministic duplicate replay from a stopped-server backlog in one selected Aspire process.
[<NonParallelizable>]
type ManifestContributionDuplicateBacklogMeasurementTests() =

    /// Emits truthful stop, backlog, restart, completed replay, unchanged state, and evidence results.
    [<Test; Explicit("Run only through the focused MCA duplicate-backlog measurement selector.")>]
    member _.``isolated duplicate backlog completes exactly and preserves durable state``() =
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
            let plan = [| DuplicateBacklogRuntime.ScenarioId |]
            writer.Append(MeasurementRun.Create(runId, commitSha, worktree, worktreeState, command, evidenceDirectory, plan))
            let assertions = ResizeArray<MeasurementAssertion>()
            let failures = ResizeArray<string>()
            let mutable host: TestHostState option = None
            let mutable serverStopped = false

            let recordAssertion assertionId passed detail =
                let assertion = MeasurementAssertion.Create(runId, DuplicateBacklogRuntime.ScenarioId, assertionId, passed, detail)
                assertions.Add assertion
                writer.Append assertion

            try
                let bootstrapUserId = Guid.NewGuid().ToString("D")
                let! state = AspireTestHost.startIsolatedAsync bootstrapUserId
                host <- Some state
                state.Client.DefaultRequestHeaders.Add("x-grace-user-id", bootstrapUserId)
                let! _ = AspireTestHost.drainServiceBusAsync state
                let ownerId = Guid.NewGuid()
                let organizationId = Guid.NewGuid()
                let repositoryId = Guid.NewGuid()
                do! BaselineRuntime.createOwnerAsync state ownerId
                do! BaselineRuntime.createOrganizationAsync state ownerId organizationId
                let! defaultBranchId, defaultReferenceId = BaselineRuntime.createRepositoryAsync state ownerId organizationId repositoryId
                let! defaultBranch = BaselineRuntime.getBranchAsync state ownerId organizationId repositoryId defaultBranchId

                if defaultBranch.LatestReference.ReferenceId
                   <> defaultReferenceId then
                    invalidOp "The duplicate-backlog repository default Reference did not match its persisted branch."

                let defaultMessageId = $"Reference/{defaultReferenceId}/Created"
                let! defaultObserved = BaselineRuntime.observeReferenceEnvelopesAsync state [| defaultMessageId |] "duplicate-backlog repository default"
                let! _ = BaselineRuntime.waitForCompletedSettlementSamplesAsync state
                let assetsWithoutBranches = ResizeArray<ContentBlockAddress * FileManifest * DirectoryVersion>()
                let mutable assetIndex = 0

                while assetIndex < BaselineRuntime.SelectedTopologyCount do
                    let! blockAddress, manifest, bytes = BaselineRuntime.createManifestAssetAsync state ownerId organizationId repositoryId assetIndex

                    let root = BaselineRuntime.createRoot ownerId organizationId repositoryId assetIndex manifest bytes
                    do! BaselineRuntime.saveRootAsync state ownerId organizationId repositoryId root
                    assetsWithoutBranches.Add(blockAddress, manifest, root)
                    assetIndex <- assetIndex + 1

                let! rebaseBaseline = BaselineRuntime.scrapeMetricsAsync state
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

                let rebaseMessageIds =
                    assets
                    |> Seq.map (fun asset -> $"Reference/{asset.RebaseReferenceId}/Created")
                    |> Seq.toArray

                let! rebaseObserved = BaselineRuntime.observeReferenceEnvelopesAsync state rebaseMessageIds "duplicate-backlog branch Rebase"

                let! rebaseMessageDelta, rebaseDurationDelta, saveBaseline =
                    BaselineRuntime.waitForCompletedSettlementDeltaAsync state (int64 assets.Count) rebaseBaseline

                let persistedSaves = ResizeArray<Reference.ReferenceDto>()
                assetIndex <- 0

                while assetIndex < assets.Count do
                    let! persisted = BaselineRuntime.saveReferenceAsync state ownerId organizationId repositoryId assets[assetIndex]
                    persistedSaves.Add persisted
                    assetIndex <- assetIndex + 1

                let saveMessageIds =
                    assets
                    |> Seq.map (fun asset -> $"Reference/{asset.SaveReferenceId}/Created")
                    |> Seq.toArray

                let! saveObserved = BaselineRuntime.observeReferenceEnvelopesAsync state saveMessageIds "duplicate-backlog explicit Save"

                let assetArray = assets.ToArray()
                let! beforeSnapshot = DuplicateBacklogRuntime.waitForSnapshotAsync state repositoryId assetArray

                let! saveMessageDelta, saveDurationDelta, replayBaseline =
                    BaselineRuntime.waitForCompletedSettlementDeltaAsync state (int64 assets.Count) saveBaseline

                let allSeedExpected =
                    Array.concat [| [| defaultMessageId |]
                                    rebaseMessageIds
                                    saveMessageIds |]

                let allSeedObserved =
                    Array.concat [| defaultObserved
                                    rebaseObserved
                                    saveObserved |]
                    |> Array.map (fun envelope -> envelope.MessageId)

                let persistedSeedComplete =
                    persistedSaves.Count = assets.Count
                    && persistedSaves
                       |> Seq.forall (fun persisted ->
                           assets
                           |> Seq.exists (fun asset ->
                               persisted.ReferenceId = asset.SaveReferenceId
                               && persisted.DirectoryId = asset.Root.DirectoryVersionId))

                let seedDeliveryCompleted =
                    rebaseMessageDelta = int64 assets.Count
                    && rebaseDurationDelta = int64 assets.Count
                    && saveMessageDelta = int64 assets.Count
                    && saveDurationDelta = int64 assets.Count

                let durableConverged = DuplicateBacklogRuntime.snapshotComplete assets.Count beforeSnapshot

                let seedInventoryErrors = ProducerInventory.validate allSeedExpected allSeedObserved
                let seedInventoryDetail = String.Join("; ", seedInventoryErrors)

                recordAssertion
                    "duplicate-backlog.seed-deliveries-completed"
                    (persistedSeedComplete
                     && seedDeliveryCompleted
                     && seedInventoryErrors.Length = 0)
                    $"persisted={persistedSeedComplete}; rebaseMessages={rebaseMessageDelta}; rebaseDurations={rebaseDurationDelta}; saveMessages={saveMessageDelta}; saveDurations={saveDurationDelta}; inventoryErrors={seedInventoryDetail}"

                let barrierErrors = DuplicateBacklog.validatePreStopBarrier allSeedExpected allSeedObserved seedDeliveryCompleted durableConverged

                recordAssertion "duplicate-backlog.pre-stop-terminal-barrier" (barrierErrors.Length = 0) (String.Join("; ", barrierErrors))

                if barrierErrors.Length > 0 then
                    let barrierDetail = String.Join("; ", barrierErrors)
                    invalidOp $"The duplicate-backlog pre-stop barrier failed: {barrierDetail}"

                do! AspireTestHost.stopGraceServerAsync state "ManifestContributionDuplicateBacklogMeasurementTests.isolated duplicate backlog"

                serverStopped <- true
                let unrelatedMessageId = $"DuplicateBacklog/Unrelated/{Guid.NewGuid():N}"
                do! DuplicateBacklogRuntime.publishBacklogAsync state saveObserved unrelatedMessageId

                let! stoppedObserved = DuplicateBacklogRuntime.observeStoppedBacklogAsync state saveMessageIds

                let visibilityErrors = DuplicateBacklog.validateStoppedBacklogVisibility saveMessageIds stoppedObserved

                let stoppedObservedDetail = String.Join(",", stoppedObserved)
                let visibilityDetail = String.Join("; ", visibilityErrors)

                recordAssertion
                    "duplicate-backlog.visible-while-stopped"
                    (visibilityErrors.Length = 0)
                    $"observed={stoppedObservedDetail}; errors={visibilityDetail}"

                if visibilityErrors.Length > 0 then
                    invalidOp $"The duplicate backlog was not visible while Grace.Server was stopped: {visibilityDetail}"

                let! commandStartedAt, healthObservedAt =
                    AspireTestHost.startGraceServerAsync state "ManifestContributionDuplicateBacklogMeasurementTests.isolated duplicate backlog"

                serverStopped <- false

                let readinessErrors = DuplicateBacklog.validateFreshServerReadiness commandStartedAt healthObservedAt true

                let readinessDetail = String.Join("; ", readinessErrors)

                recordAssertion
                    "duplicate-backlog.fresh-server-readiness"
                    (readinessErrors.Length = 0)
                    $"commandStartedAt={commandStartedAt:O}; healthObservedAt={healthObservedAt:O}; errors={readinessDetail}"

                let! replayObserved = BaselineRuntime.observeReferenceEnvelopesAsync state saveMessageIds "duplicate-backlog replay"

                let! replayMessageDelta, replayDurationDelta, _ = BaselineRuntime.waitForCompletedSettlementDeltaAsync state (int64 assets.Count) replayBaseline

                let! afterSnapshot = DuplicateBacklogRuntime.waitForSnapshotAsync state repositoryId assetArray

                let replayObservedIds =
                    replayObserved
                    |> Array.map (fun envelope -> envelope.MessageId)

                let replayIdentityErrors = ProducerInventory.validate saveMessageIds replayObservedIds

                let replayIdentityDetail = String.Join("; ", replayIdentityErrors)

                recordAssertion
                    "duplicate-backlog.replay-message-delta"
                    (replayMessageDelta = int64 assets.Count)
                    $"expected={assets.Count}; actual={replayMessageDelta}"

                recordAssertion
                    "duplicate-backlog.replay-duration-delta"
                    (replayDurationDelta = int64 assets.Count)
                    $"expected={assets.Count}; actual={replayDurationDelta}"

                recordAssertion
                    "duplicate-backlog.unrelated-event-excluded"
                    (replayMessageDelta = int64 assets.Count
                     && replayDurationDelta = int64 assets.Count)
                    $"unrelatedMessageId={unrelatedMessageId}; manifestMessages={replayMessageDelta}; manifestDurations={replayDurationDelta}"

                recordAssertion
                    "duplicate-backlog.reference-root-state-unchanged"
                    (beforeSnapshot.ReferenceRoots = afterSnapshot.ReferenceRoots)
                    $"before={beforeSnapshot.ReferenceRoots.Length}; after={afterSnapshot.ReferenceRoots.Length}"

                recordAssertion
                    "duplicate-backlog.manifest-state-unchanged"
                    (beforeSnapshot.ManifestRelationships = afterSnapshot.ManifestRelationships)
                    $"before={beforeSnapshot.ManifestRelationships.Length}; after={afterSnapshot.ManifestRelationships.Length}"

                recordAssertion
                    "duplicate-backlog.logical-state-unchanged"
                    (beforeSnapshot.LogicalState = afterSnapshot.LogicalState)
                    $"before={beforeSnapshot.LogicalState.Length}; after={afterSnapshot.LogicalState.Length}"

                recordAssertion
                    "duplicate-backlog.workflow-state-unchanged"
                    (beforeSnapshot.WorkflowState = afterSnapshot.WorkflowState)
                    $"before={beforeSnapshot.WorkflowState.Length}; after={afterSnapshot.WorkflowState.Length}"

                recordAssertion
                    "duplicate-backlog.physical-state-unchanged"
                    (beforeSnapshot.PhysicalState = afterSnapshot.PhysicalState)
                    $"before={beforeSnapshot.PhysicalState.Length}; after={afterSnapshot.PhysicalState.Length}"

                recordAssertion
                    "duplicate-backlog.identity-isolation"
                    (seedInventoryErrors.Length = 0
                     && replayIdentityErrors.Length = 0)
                    $"seedErrors={seedInventoryDetail}; replayErrors={replayIdentityDetail}"

                DuplicateBacklogRuntime.recordSample writer runId "replay-messages" "grace_manifest_contribution_messages_total.delta" replayMessageDelta

                DuplicateBacklogRuntime.recordSample
                    writer
                    runId
                    "replay-durations"
                    "grace_manifest_contribution_processing_duration_milliseconds_count.delta"
                    replayDurationDelta
            with
            | ex -> failures.Add(ex.ToString())

            match host with
            | Some state ->
                if serverStopped then
                    try
                        let! _ = AspireTestHost.startGraceServerAsync state "duplicate-backlog failed-scenario cleanup"

                        serverStopped <- false
                    with
                    | ex -> failures.Add($"cleanup-start: {ex}")

                try
                    do! AspireTestHost.stopIsolatedAsync state
                with
                | ex -> failures.Add($"cleanup-host: {ex}")
            | None -> ()

            if
                not
                    (
                        assertions
                        |> Seq.exists (fun assertion -> assertion.AssertionId = "duplicate-backlog.evidence-integrity")
                    )
            then
                try
                    let valid = BaselineRuntime.verifyEvidenceIntegrity writer
                    recordAssertion "duplicate-backlog.evidence-integrity" valid $"path={writer.Path}"
                with
                | ex -> recordAssertion "duplicate-backlog.evidence-integrity" false ex.Message

            DuplicateBacklog.requiredAssertionIds
            |> Array.iter (fun assertionId ->
                if
                    not
                        (
                            assertions
                            |> Seq.exists (fun assertion -> assertion.AssertionId = assertionId)
                        )
                then
                    recordAssertion assertionId false "The runtime failed before this assertion could be evaluated.")

            let summary =
                ScenarioSummary.derive
                    runId
                    DuplicateBacklogRuntime.ScenarioId
                    DuplicateBacklog.requiredAssertionIds
                    (assertions.ToArray())
                    (failures.ToArray())
                    false

            writer.Append summary
            TestContext.Progress.WriteLine($"MCA duplicate-backlog evidence directory: {evidenceDirectory}")
            TestContext.Progress.Flush()

            Assert.That(
                summary.Outcome,
                Is.EqualTo("Passed"),
                $"Evidence: {evidenceDirectory}{Environment.NewLine}{String.Join(Environment.NewLine, failures)}"
            )
        }
