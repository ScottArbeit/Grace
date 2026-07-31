namespace Grace.Server.Measurements

open Azure.Messaging.ServiceBus
open Grace.Server.Tests
open Grace.Server.Tests.Measurement
open Grace.Shared
open Grace.Types
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
open Grace.Types.Events
open Grace.Types.ManifestContributionAccounting
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.RepositoryContentCounter
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks

/// Captures the five independently authoritative durable projections compared across restart replay.
type private ServerRestartSnapshot =
    {
        ReferenceRoots: string array
        ManifestRelationships: string array
        LogicalState: string array
        WorkflowState: string array
        PhysicalState: string array
        IsConverged: bool
    }

/// Implements one persisted-envelope replay after a real Grace.Server restart on the R1 measurement boundary.
module private ServerRestartRuntime =

    [<Literal>]
    let ScenarioId = "server-restart"

    /// Serializes a durable projection with the stable Grace JSON contract used for exact comparison.
    let private serialize value = JsonSerializer.Serialize(value, Constants.JsonSerializerOptions)

    /// Returns whether every selected projection contains one converged entry.
    let snapshotComplete snapshot =
        snapshot.ReferenceRoots.Length = 1
        && snapshot.ManifestRelationships.Length = 1
        && snapshot.LogicalState.Length = 1
        && snapshot.WorkflowState.Length = 1
        && snapshot.PhysicalState.Length = 1
        && snapshot.IsConverged
        && snapshot.ReferenceRoots
           |> Array.forall (fun value -> not (value.StartsWith("missing:", StringComparison.Ordinal)))
        && snapshot.ManifestRelationships
           |> Array.forall (fun value -> not (value.StartsWith("missing:", StringComparison.Ordinal)))

    /// Reads every selected durable projection directly from Cosmos without consulting Redis or a pre-restart actor.
    let readSnapshotAsync state repositoryId (asset: BaselineAsset) =
        task {
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
            let! counters = BaselineRuntime.readActorSnapshotsAsync<RepositoryContentCounterDto> state "RepoContentCounter"
            let! workflows = BaselineRuntime.readActorSnapshotsAsync<ManifestContributionWorkflowDto> state "ManifestContributionWorkflow"
            let! metadataStreams = BaselineRuntime.readActorEventStreamsAsync<ContentBlockMetadataEvent> state "ContentBlockMetadata"

            let metadata =
                metadataStreams
                |> Array.map (fun events ->
                    events
                    |> Array.fold (fun current event -> ContentBlockMetadataDto.UpdateDto event current) ContentBlockMetadataDto.Empty)

            let selectedCounters =
                counters
                |> Array.filter (fun counter ->
                    counter.RepositoryId = repositoryId
                    && counter.StoragePoolId = asset.Manifest.StoragePoolId
                    && counter.ManifestAddress = asset.Manifest.ManifestAddress)

            let selectedWorkflows =
                workflows
                |> Array.filter (fun workflow ->
                    workflow.RepositoryId = repositoryId
                    && workflow.StoragePoolId = asset.Manifest.StoragePoolId
                    && workflow.ManifestAddress = asset.Manifest.ManifestAddress)

            let selectedMetadata =
                metadata
                |> Array.filter (fun dto ->
                    dto.Metadata
                    |> Option.exists (fun value ->
                        value.StoragePoolId = asset.Manifest.StoragePoolId
                        && value.ContentBlockAddress = asset.BlockAddress))

            let logicalConverged =
                selectedCounters.Length = 1
                && selectedCounters[0].Count = 1L

            let workflowConverged =
                selectedWorkflows.Length = 1
                && selectedWorkflows[0].Direction = ManifestContributionDirection.Increment
                && selectedWorkflows[0].CounterRevision = 1L
                && selectedWorkflows[0].LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
                && selectedWorkflows[0].Ranges.Length = 1
                && selectedWorkflows[0].CompletedRanges.Length = 1
                && selectedWorkflows[0].FailedRanges.Length = 0

            let physicalConverged =
                selectedMetadata.Length = 1
                && selectedMetadata[0].Metadata
                   |> Option.exists (fun value ->
                       value.Ranges.Length > 0
                       && value.Ranges
                          |> Array.forall (fun range -> range.ActiveManifestCount = 1))

            return
                {
                    ReferenceRoots =
                        [|
                            if referenceRootExists then
                                serialize referenceRoot
                            else
                                $"missing:{serialize referenceRoot}"
                        |]
                    ManifestRelationships =
                        [|
                            if manifestRelationshipExists then
                                serialize manifestRelationship
                            else
                                $"missing:{serialize manifestRelationship}"
                        |]
                    LogicalState =
                        selectedCounters
                        |> Array.map serialize
                        |> Array.sort
                    WorkflowState =
                        selectedWorkflows
                        |> Array.map serialize
                        |> Array.sort
                    PhysicalState =
                        selectedMetadata
                        |> Array.map serialize
                        |> Array.sort
                    IsConverged =
                        logicalConverged
                        && workflowConverged
                        && physicalConverged
                }
        }

    /// Waits for the one selected manifest to converge across all five durable projection layers.
    let waitForSnapshotAsync state repositoryId asset =
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

            while not (snapshotComplete snapshot)
                  && DateTime.UtcNow < timeoutAt do
                let! current = readSnapshotAsync state repositoryId asset
                snapshot <- current

                if not (snapshotComplete snapshot) then
                    do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            return snapshot
        }

    /// Publishes exactly one captured Reference envelope to the production topic.
    let publishCapturedEnvelopeAsync (state: TestHostState) (envelope: CapturedReferenceEnvelope) =
        task {
            use client = new ServiceBusClient(state.ServiceBusConnectionString)
            let sender = client.CreateSender(state.ServiceBusTopic)

            try
                let message = ServiceBusMessage(BinaryData(envelope.Body))
                message.MessageId <- envelope.MessageId
                message.CorrelationId <- envelope.CorrelationId
                message.Subject <- envelope.Subject
                message.ContentType <- envelope.ContentType
                let properties = envelope.ApplicationProperties |> Seq.toArray
                let mutable index = 0

                while index < properties.Length do
                    let property = properties[index]
                    message.ApplicationProperties[ property.Key ] <- property.Value
                    index <- index + 1

                do! sender.SendMessageAsync message
            finally
                sender
                    .DisposeAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult()
        }

    /// Adds one typed restart-replay sample with stable completed-settlement labels.
    let recordSample (writer: EvidenceWriter) runId sampleId name value =
        let labels = Dictionary<string, string>()
        labels["stage"] <- "settle"
        labels["outcome"] <- "completed"
        writer.Append(MeasurementSample.Create(runId, ScenarioId, sampleId, name, value, labels))

/// Proves one persisted Reference envelope completes idempotently after a real Grace.Server restart.
[<NonParallelizable>]
type ManifestContributionServerRestartMeasurementTests() =

    /// Emits truthful restart, readiness, replay settlement, durable equality, and evidence results.
    [<Test; Explicit("Run only through the focused MCA server-restart measurement selector.")>]
    member _.``isolated persisted envelope completes after Grace Server restart``() =
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
            let plan = [| ServerRestartRuntime.ScenarioId |]
            writer.Append(MeasurementRun.Create(runId, commitSha, worktree, worktreeState, command, evidenceDirectory, plan))
            let assertions = ResizeArray<MeasurementAssertion>()
            let failures = ResizeArray<string>()
            let mutable host: TestHostState option = None

            let recordAssertion assertionId passed detail =
                let assertion = MeasurementAssertion.Create(runId, ServerRestartRuntime.ScenarioId, assertionId, passed, detail)
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
                ManifestContributionGroupedRuntime.registerRepository ServerRestartRuntime.ScenarioId repositoryId
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
                let! blockAddress, manifest, bytes = BaselineRuntime.createManifestAssetAsync state ownerId organizationId repositoryId 0
                let root = BaselineRuntime.createRoot ownerId organizationId repositoryId 0 manifest bytes
                do! BaselineRuntime.saveRootAsync state ownerId organizationId repositoryId root
                let rebaseReferenceId = Guid.NewGuid()
                let saveReferenceId = Guid.NewGuid()
                let! rebaseBaseline = BaselineRuntime.scrapeMetricsAsync state
                let! branch = BaselineRuntime.createBranchAsync state ownerId organizationId repositoryId defaultBranch 0 rebaseReferenceId

                let asset =
                    {
                        BlockAddress = blockAddress
                        Manifest = manifest
                        Root = root
                        Branch = branch
                        RebaseReferenceId = rebaseReferenceId
                        SaveReferenceId = saveReferenceId
                    }

                let rebaseMessageId = $"Reference/{rebaseReferenceId}/Created"
                let! rebaseObserved = BaselineRuntime.observeReferenceEnvelopesAsync state [| rebaseMessageId |] "branch Rebase"

                let! rebaseMessageDelta, rebaseDurationDelta, saveBaseline = BaselineRuntime.waitForCompletedSettlementDeltaAsync state 1L rebaseBaseline

                let! persistedSave = BaselineRuntime.saveReferenceAsync state ownerId organizationId repositoryId asset
                let saveMessageId = $"Reference/{saveReferenceId}/Created"
                let! saveObserved = BaselineRuntime.observeReferenceEnvelopesAsync state [| saveMessageId |] "explicit Save"

                if saveObserved.Length <> 1 then
                    invalidOp $"Expected one captured explicit Save envelope but observed {saveObserved.Length}."

                let! beforeRestart = ServerRestartRuntime.waitForSnapshotAsync state repositoryId asset

                if not (ServerRestartRuntime.snapshotComplete beforeRestart) then
                    invalidOp "The selected manifest did not converge before Grace.Server restart."

                let! saveMessageDelta, saveDurationDelta, _ = BaselineRuntime.waitForCompletedSettlementDeltaAsync state 1L saveBaseline

                let seedExpected =
                    [|
                        defaultMessageId
                        rebaseMessageId
                        saveMessageId
                    |]

                let seedObserved =
                    Array.concat [| defaultObserved
                                    rebaseObserved
                                    saveObserved |]
                    |> Array.map (fun envelope -> envelope.MessageId)

                let seedErrors = ProducerInventory.validate seedExpected seedObserved
                let seedErrorDetail = String.Join("; ", seedErrors)

                let seedCompleted =
                    seedErrors.Length = 0
                    && persistedSave.ReferenceId = saveReferenceId
                    && rebaseMessageDelta = 1L
                    && rebaseDurationDelta = 1L
                    && saveMessageDelta = 1L
                    && saveDurationDelta = 1L

                recordAssertion
                    "server-restart.seed-deliveries-completed"
                    seedCompleted
                    $"inventory={seedErrorDetail}; rebaseMessages={rebaseMessageDelta}; rebaseDurations={rebaseDurationDelta}; saveMessages={saveMessageDelta}; saveDurations={saveDurationDelta}"

                let! restart = AspireTestHost.restartGraceServerWithEvidenceAsync state "ManifestContributionServerRestart.persisted-envelope-replay"

                let readinessErrors =
                    ServerRestart.validateFreshReadiness
                        true
                        restart.CommandStartedAt
                        restart.CommandCompletedAt
                        restart.NonReadyEventObservedAt
                        restart.NonReadyResourceState
                        restart.NonReadyHealthStatus
                        restart.ResourceEventObservedAt
                        restart.ResourceState
                        restart.HttpReadyObservedAt
                        true

                let readinessErrorDetail = String.Join("; ", readinessErrors)

                recordAssertion
                    "server-restart.command-completed"
                    (restart.CommandCompletedAt
                     >= restart.CommandStartedAt)
                    $"started={restart.CommandStartedAt:O}; completed={restart.CommandCompletedAt:O}"

                recordAssertion
                    "server-restart.fresh-health"
                    (restart.NonReadyEventObservedAt > restart.CommandCompletedAt
                     && restart.ResourceEventObservedAt > restart.NonReadyEventObservedAt
                     && restart.ResourceState = "Healthy")
                    $"commandCompleted={restart.CommandCompletedAt:O}; nonReadyState={restart.NonReadyResourceState}; nonReadyHealth={restart.NonReadyHealthStatus}; nonReadyObserved={restart.NonReadyEventObservedAt:O}; healthyState={restart.ResourceState}; healthyObserved={restart.ResourceEventObservedAt:O}; errors={readinessErrorDetail}"

                recordAssertion
                    "server-restart.http-ready"
                    (restart.HttpReadyObservedAt > restart.ResourceEventObservedAt
                     && readinessErrors.Length = 0)
                    $"ready={restart.HttpReadyObservedAt:O}; errors={readinessErrorDetail}"

                let! rawReplayBaseline = BaselineRuntime.scrapeMetricsAsync state

                let replayBaseline =
                    OpenMetrics.captureFreshProcessCompletedSettlementBaseline rawReplayBaseline
                    |> Result.defaultWith (fun error -> invalidOp $"Invalid fresh-process replay baseline: {error}")

                let replayEnvelope = saveObserved[0]
                do! ServerRestartRuntime.publishCapturedEnvelopeAsync state replayEnvelope
                let! replayObserved = BaselineRuntime.observeReferenceEnvelopesAsync state [| saveMessageId |] "server-restart replay"

                let! replayMessageDelta, replayDurationDelta, _ = BaselineRuntime.waitForCompletedSettlementDeltaAsync state 1L replayBaseline

                let replayObservedIds =
                    replayObserved
                    |> Array.map (fun envelope -> envelope.MessageId)

                let replayErrors = ServerRestart.validateReplayCompletion saveMessageId replayObservedIds replayMessageDelta replayDurationDelta true

                let replayErrorDetail = String.Join("; ", replayErrors)

                recordAssertion
                    "server-restart.replay-message-delta"
                    (replayMessageDelta = 1L && replayErrors.Length = 0)
                    $"delta={replayMessageDelta}; errors={replayErrorDetail}"

                recordAssertion
                    "server-restart.replay-duration-delta"
                    (replayDurationDelta = 1L
                     && replayErrors.Length = 0)
                    $"delta={replayDurationDelta}; errors={replayErrorDetail}"

                let! afterRestart = ServerRestartRuntime.readSnapshotAsync state repositoryId asset

                recordAssertion
                    "server-restart.reference-root-state-unchanged"
                    (afterRestart.ReferenceRoots = beforeRestart.ReferenceRoots)
                    "exact Reference-root state compared"

                recordAssertion
                    "server-restart.manifest-state-unchanged"
                    (afterRestart.ManifestRelationships = beforeRestart.ManifestRelationships)
                    "exact DirectoryVersion-manifest state compared"

                recordAssertion
                    "server-restart.logical-state-unchanged"
                    (afterRestart.LogicalState = beforeRestart.LogicalState)
                    "logical counter state compared"

                recordAssertion "server-restart.workflow-state-unchanged" (afterRestart.WorkflowState = beforeRestart.WorkflowState) "workflow state compared"

                recordAssertion
                    "server-restart.physical-state-unchanged"
                    (afterRestart.PhysicalState = beforeRestart.PhysicalState)
                    "physical active-range state compared"

                ServerRestartRuntime.recordSample writer runId "replay-messages" "grace_manifest_contribution_messages_total.delta" replayMessageDelta

                ServerRestartRuntime.recordSample
                    writer
                    runId
                    "replay-durations"
                    "grace_manifest_contribution_processing_duration_milliseconds_count.delta"
                    replayDurationDelta
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
                        |> Seq.exists (fun assertion -> assertion.AssertionId = "server-restart.evidence-integrity")
                    )
            then
                try
                    let valid = BaselineRuntime.verifyEvidenceIntegrity writer
                    recordAssertion "server-restart.evidence-integrity" valid $"path={writer.Path}"
                with
                | ex -> recordAssertion "server-restart.evidence-integrity" false ex.Message

            ServerRestart.requiredAssertionIds
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
                    ServerRestartRuntime.ScenarioId
                    ServerRestart.requiredAssertionIds
                    (assertions.ToArray())
                    (failures.ToArray())
                    false

            writer.Append summary
            TestContext.Progress.WriteLine($"MCA server-restart evidence directory: {evidenceDirectory}")
            TestContext.Progress.Flush()

            Assert.That(
                summary.Outcome,
                Is.EqualTo("Passed"),
                $"Evidence: {evidenceDirectory}{Environment.NewLine}{String.Join(Environment.NewLine, failures)}"
            )
        }
