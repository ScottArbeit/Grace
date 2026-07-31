namespace Grace.Server.Measurements

open Grace.Server.Tests
open Grace.Server.Tests.Measurement
open Grace.Shared
open Grace.Types
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
open Grace.Types.ManifestContributionAccounting
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Reference
open Grace.Types.RepositoryContentCounter
open Microsoft.Azure.Cosmos
open NUnit.Framework
open StackExchange.Redis
open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Captures one hot manifest's independently observable durable state across Redis restart recovery.
type private RedisRestartDurableState =
    {
        ReferenceRootPresent: bool
        ManifestRelationshipPresent: bool
        LogicalCount: int64
        WorkflowFingerprint: string
        PhysicalActiveCount: int64
        Detail: string
    }

/// Implements one selected-process Redis restart recovery measurement over the R1 host and evidence boundary.
module private RedisRestartRuntime =

    [<Literal>]
    let MaximumRecordBytes = 65536

    /// Reads the exact counter, workflow snapshot, physical range, and relationship state for one hot manifest.
    let readDurableStateAsync state repositoryId (asset: BaselineAsset) requiredReferenceId =
        task {
            let! counters = BaselineRuntime.readActorSnapshotsAsync<RepositoryContentCounterDto> state "RepoContentCounter"
            let! workflows = BaselineRuntime.readActorSnapshotsAsync<ManifestContributionWorkflowDto> state "ManifestContributionWorkflow"
            let! metadataStreams = BaselineRuntime.readActorEventStreamsAsync<ContentBlockMetadataEvent> state "ContentBlockMetadata"

            let matchingCounters =
                counters
                |> Array.filter (fun counter ->
                    counter.RepositoryId = repositoryId
                    && counter.StoragePoolId = asset.Manifest.StoragePoolId
                    && counter.ManifestAddress = asset.Manifest.ManifestAddress)

            let logicalCount =
                if matchingCounters.Length = 1 then
                    matchingCounters[0].Count
                else
                    Int64.MinValue

            let matchingWorkflows =
                workflows
                |> Array.filter (fun workflow ->
                    workflow.RepositoryId = repositoryId
                    && workflow.StoragePoolId = asset.Manifest.StoragePoolId
                    && workflow.ManifestAddress = asset.Manifest.ManifestAddress)
                |> Array.map (fun workflow -> JsonSerializer.Serialize(workflow, Constants.JsonSerializerOptions))
                |> Array.sort

            let workflowFingerprint = String.Join("|", matchingWorkflows)

            let metadata =
                metadataStreams
                |> Array.map (fun events ->
                    events
                    |> Array.fold (fun current event -> ContentBlockMetadataDto.UpdateDto event current) ContentBlockMetadataDto.Empty)
                |> Array.choose (fun dto -> dto.Metadata)
                |> Array.filter (fun value ->
                    value.StoragePoolId = asset.Manifest.StoragePoolId
                    && value.ContentBlockAddress = asset.BlockAddress)

            let physicalActiveCount =
                if metadata.Length = 1
                   && metadata[0].Ranges.Length > 0
                   && metadata[0].Ranges
                      |> Array.map (fun range -> range.ActiveManifestCount)
                      |> Array.distinct
                      |> Array.length
                      |> (=) 1 then
                    int64 metadata[0].Ranges[0].ActiveManifestCount
                else
                    Int64.MinValue

            let! referenceRootPresent =
                match requiredReferenceId with
                | Some referenceId ->
                    BaselineRuntime.exactRelationshipExistsAsync
                        state
                        (ExactRelationship.ReferenceRoot
                            { RepositoryId = repositoryId; RootDirectoryVersionId = asset.Root.DirectoryVersionId; ReferenceId = referenceId })
                | None -> Task.FromResult true

            let! manifestRelationshipPresent =
                BaselineRuntime.exactRelationshipExistsAsync
                    state
                    (ExactRelationship.DirectoryVersionManifest
                        {
                            RepositoryId = repositoryId
                            StoragePoolId = asset.Manifest.StoragePoolId
                            ManifestAddress = asset.Manifest.ManifestAddress
                            DirectoryVersionId = asset.Root.DirectoryVersionId
                        })

            return
                {
                    ReferenceRootPresent = referenceRootPresent
                    ManifestRelationshipPresent = manifestRelationshipPresent
                    LogicalCount = logicalCount
                    WorkflowFingerprint = workflowFingerprint
                    PhysicalActiveCount = physicalActiveCount
                    Detail =
                        $"referenceRoot={referenceRootPresent}; manifest={manifestRelationshipPresent}; logical={logicalCount}; workflows={matchingWorkflows.Length}; physical={physicalActiveCount}"
                }
        }

    /// Waits for one exact durable recovery state without using elapsed quiet time as correctness evidence.
    let waitForDurableStateAsync state repositoryId asset requiredReferenceId expectedLogicalCount expectedWorkflowFingerprint =
        task {
            let timeoutAt = DateTime.UtcNow.AddSeconds(45.0)

            let mutable observed =
                {
                    ReferenceRootPresent = false
                    ManifestRelationshipPresent = false
                    LogicalCount = Int64.MinValue
                    WorkflowFingerprint = String.Empty
                    PhysicalActiveCount = Int64.MinValue
                    Detail = "not observed"
                }

            let complete value =
                value.ReferenceRootPresent
                && value.ManifestRelationshipPresent
                && value.LogicalCount = expectedLogicalCount
                && value.WorkflowFingerprint.Equals(expectedWorkflowFingerprint, StringComparison.Ordinal)
                && value.PhysicalActiveCount = 1L

            while not (complete observed)
                  && DateTime.UtcNow < timeoutAt do
                let! current = readDurableStateAsync state repositoryId asset requiredReferenceId
                observed <- current

                if not (complete observed) then do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            return observed
        }

    /// Performs one bounded Redis PING against the endpoint published after the resource restart.
    let proveProtocolReadyAsync state =
        task {
            let endpoint = AspireTestHost.getRedisEndpoint state
            let configuration = ConfigurationOptions()
            configuration.EndPoints.Add(endpoint.Host, endpoint.Port)
            configuration.AbortOnConnectFail <- false
            configuration.ConnectTimeout <- 10000
            configuration.AsyncTimeout <- 10000
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds(15.0))
            let timer = Stopwatch.StartNew()

            let! connection =
                ConnectionMultiplexer
                    .ConnectAsync(configuration)
                    .WaitAsync(cts.Token)

            use connection = connection

            let! latency =
                connection
                    .GetDatabase()
                    .PingAsync()
                    .WaitAsync(cts.Token)

            timer.Stop()
            let latencyText = latency.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)
            let elapsedText = timer.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)
            return latency >= TimeSpan.Zero, $"endpoint={endpoint}; latencyMs={latencyText}; elapsedMs={elapsedText}"
        }

    /// Adds one typed Redis restart sample using the shared evidence writer.
    let recordSample (writer: EvidenceWriter) runId sampleId name value labels =
        writer.Append(MeasurementSample.Create(runId, "redis-restart", sampleId, name, value, labels))

/// Proves one hot-manifest Reference converges exactly once after a real pinned Redis resource restart.
[<NonParallelizable>]
type ManifestContributionRedisRestartMeasurementTests() =

    /// Emits truthful restart, readiness, settlement, durable-state, identity, cleanup, and evidence results.
    [<Test; Explicit("Run only through the focused MCA Redis restart measurement selector.")>]
    member _.``hot manifest converges one Reference after Redis restart``() =
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
            use writer = new EvidenceWriter(evidenceDirectory, RedisRestartRuntime.MaximumRecordBytes)
            let plan = [| "redis-restart" |]
            writer.Append(MeasurementRun.Create(runId, commitSha, worktree, worktreeState, command, evidenceDirectory, plan))
            let assertions = ResizeArray<MeasurementAssertion>()
            let failures = ResizeArray<string>()
            let expectedMessageIds = ResizeArray<string>()
            let observedMessageIds = ResizeArray<string>()
            let mutable host: TestHostState option = None

            let recordAssertion assertionId passed detail =
                let assertion = MeasurementAssertion.Create(runId, "redis-restart", assertionId, passed, detail)
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
                    invalidOp "The Redis restart repository default Reference inventory did not match its persisted branch."

                let defaultMessageId = $"Reference/{defaultReferenceId}/Created"
                expectedMessageIds.Add defaultMessageId
                let! defaultObserved = BaselineRuntime.observeReferenceEnvelopesAsync state [| defaultMessageId |] "repository default"

                observedMessageIds.AddRange(
                    defaultObserved
                    |> Array.map (fun envelope -> envelope.MessageId)
                )

                let! initialMetrics = BaselineRuntime.waitForCompletedSettlementSamplesAsync state
                let! blockAddress, manifest, bytes = BaselineRuntime.createManifestAssetAsync state ownerId organizationId repositoryId 0
                let root = BaselineRuntime.createRoot ownerId organizationId repositoryId 0 manifest bytes
                do! BaselineRuntime.saveRootAsync state ownerId organizationId repositoryId root
                let seedRebaseReferenceId = Guid.NewGuid()
                let seedSaveReferenceId = Guid.NewGuid()
                let! seedBranch = BaselineRuntime.createBranchAsync state ownerId organizationId repositoryId defaultBranch 0 seedRebaseReferenceId

                let seedAsset =
                    {
                        BlockAddress = blockAddress
                        Manifest = manifest
                        Root = root
                        Branch = seedBranch
                        RebaseReferenceId = seedRebaseReferenceId
                        SaveReferenceId = seedSaveReferenceId
                    }

                let seedRebaseMessageId = $"Reference/{seedRebaseReferenceId}/Created"
                expectedMessageIds.Add seedRebaseMessageId
                let! seedRebaseObserved = BaselineRuntime.observeReferenceEnvelopesAsync state [| seedRebaseMessageId |] "seed branch Rebase"

                observedMessageIds.AddRange(
                    seedRebaseObserved
                    |> Array.map (fun envelope -> envelope.MessageId)
                )

                let! seedRebaseMessageDelta, seedRebaseDurationDelta, seedSaveBaseline =
                    BaselineRuntime.waitForCompletedSettlementDeltaAsync state 1L initialMetrics

                let! _ = BaselineRuntime.saveReferenceAsync state ownerId organizationId repositoryId seedAsset
                let seedSaveMessageId = $"Reference/{seedSaveReferenceId}/Created"
                expectedMessageIds.Add seedSaveMessageId
                let! seedSaveObserved = BaselineRuntime.observeReferenceEnvelopesAsync state [| seedSaveMessageId |] "seed explicit Save"

                observedMessageIds.AddRange(
                    seedSaveObserved
                    |> Array.map (fun envelope -> envelope.MessageId)
                )

                let! seedDurable = BaselineRuntime.waitForDurableStatusAsync state repositoryId [| seedAsset |]

                let! seedSaveMessageDelta, seedSaveDurationDelta, restartMetricsBaseline =
                    BaselineRuntime.waitForCompletedSettlementDeltaAsync state 1L seedSaveBaseline

                let seedDeliveriesComplete =
                    defaultObserved.Length = 1
                    && seedRebaseObserved.Length = 1
                    && seedSaveObserved.Length = 1
                    && seedRebaseMessageDelta = 1L
                    && seedRebaseDurationDelta = 1L
                    && seedSaveMessageDelta = 1L
                    && seedSaveDurationDelta = 1L
                    && seedDurable.ReferenceRoots
                    && seedDurable.ManifestRelationships
                    && seedDurable.LogicalCounts
                    && seedDurable.WorkflowCounts
                    && seedDurable.PhysicalActiveCounts

                recordAssertion
                    "redis-restart.seed-deliveries-completed"
                    seedDeliveriesComplete
                    $"default={defaultObserved.Length}; rebase={seedRebaseObserved.Length}/{seedRebaseMessageDelta}/{seedRebaseDurationDelta}; save={seedSaveObserved.Length}/{seedSaveMessageDelta}/{seedSaveDurationDelta}; durable={seedDurable.Detail}"

                if not seedDeliveriesComplete then
                    invalidOp "Redis restart was not attempted because the hot-manifest seed deliveries did not settle exactly."

                let! beforeRestart = RedisRestartRuntime.readDurableStateAsync state repositoryId seedAsset (Some seedSaveReferenceId)
                let! restart = AspireTestHost.restartRedisAsync state

                recordAssertion
                    "redis-restart.command-completed"
                    restart.PostCommandResourceEventObserved
                    $"postCommandEvent={restart.PostCommandResourceEventObserved}; preStart={restart.PreCommandStartTimestamp}; postStart={restart.PostCommandStartTimestamp}; state={restart.PostCommandState}"

                let! protocolSucceeded, protocolDetail = RedisRestartRuntime.proveProtocolReadyAsync state

                let readiness =
                    RedisRestart.evaluateReadiness
                        {
                            PostCommandResourceEventObserved = restart.PostCommandResourceEventObserved
                            PostCommandHealth = restart.PostCommandHealth
                            ProtocolOperationSucceeded = protocolSucceeded
                        }

                recordAssertion
                    "redis-restart.fresh-health"
                    (readiness.FreshResourceEvent && readiness.Healthy)
                    $"postCommandEvent={restart.PostCommandResourceEventObserved}; health={restart.PostCommandHealth}"

                recordAssertion "redis-restart.protocol-ready" readiness.ProtocolReady protocolDetail

                if
                    not
                        (
                            readiness.FreshResourceEvent
                            && readiness.Healthy
                            && readiness.ProtocolReady
                        )
                then
                    invalidOp "Post-restart Reference setup was not attempted because fresh Redis readiness was not proven."

                let branchRebaseReferenceId = Guid.NewGuid()
                let explicitSaveReferenceId = Guid.NewGuid()
                let! recoveryBranch = BaselineRuntime.createBranchAsync state ownerId organizationId repositoryId seedBranch 1 branchRebaseReferenceId
                let branchRebaseMessageId = $"Reference/{branchRebaseReferenceId}/Created"
                expectedMessageIds.Add branchRebaseMessageId
                let! branchRebaseObserved = BaselineRuntime.observeReferenceEnvelopesAsync state [| branchRebaseMessageId |] "post-restart branch Rebase"

                observedMessageIds.AddRange(
                    branchRebaseObserved
                    |> Array.map (fun envelope -> envelope.MessageId)
                )

                let! branchMessageDelta, branchDurationDelta, explicitMetricsBaseline =
                    BaselineRuntime.waitForCompletedSettlementDeltaAsync state 1L restartMetricsBaseline

                let recoveryAsset =
                    { seedAsset with Branch = recoveryBranch; RebaseReferenceId = branchRebaseReferenceId; SaveReferenceId = explicitSaveReferenceId }

                let expectedBranchLogicalCount = beforeRestart.LogicalCount + 1L

                let! explicitDurableBaseline =
                    RedisRestartRuntime.waitForDurableStateAsync
                        state
                        repositoryId
                        recoveryAsset
                        (Some branchRebaseReferenceId)
                        expectedBranchLogicalCount
                        beforeRestart.WorkflowFingerprint

                let branchSetupComplete =
                    branchRebaseObserved.Length = 1
                    && branchMessageDelta = 1L
                    && branchDurationDelta = 1L
                    && explicitDurableBaseline.LogicalCount = expectedBranchLogicalCount
                    && explicitDurableBaseline.WorkflowFingerprint.Equals(beforeRestart.WorkflowFingerprint, StringComparison.Ordinal)

                recordAssertion
                    "redis-restart.branch-setup-delivery-completed"
                    branchSetupComplete
                    $"observed={branchRebaseObserved.Length}; messages={branchMessageDelta}; durations={branchDurationDelta}; durable={explicitDurableBaseline.Detail}"

                if not branchSetupComplete then
                    invalidOp "The explicit Reference was not created because the branch Rebase delivery did not settle before its baseline."

                let! _ = BaselineRuntime.saveReferenceAsync state ownerId organizationId repositoryId recoveryAsset
                let explicitMessageId = $"Reference/{explicitSaveReferenceId}/Created"
                expectedMessageIds.Add explicitMessageId
                let! explicitObserved = BaselineRuntime.observeReferenceEnvelopesAsync state [| explicitMessageId |] "post-restart explicit Save"

                observedMessageIds.AddRange(
                    explicitObserved
                    |> Array.map (fun envelope -> envelope.MessageId)
                )

                let! finalDurable =
                    RedisRestartRuntime.waitForDurableStateAsync
                        state
                        repositoryId
                        recoveryAsset
                        (Some explicitSaveReferenceId)
                        (explicitDurableBaseline.LogicalCount + 1L)
                        explicitDurableBaseline.WorkflowFingerprint

                let! stimulusMessageDelta, stimulusDurationDelta, _ = BaselineRuntime.waitForCompletedSettlementDeltaAsync state 1L explicitMetricsBaseline

                recordAssertion
                    "redis-restart.stimulus-message-delta"
                    (explicitObserved.Length = 1
                     && stimulusMessageDelta = 1L)
                    $"observed={explicitObserved.Length}; delta={stimulusMessageDelta}"

                recordAssertion "redis-restart.stimulus-duration-delta" (stimulusDurationDelta = 1L) $"delta={stimulusDurationDelta}"
                recordAssertion "redis-restart.reference-root-present" finalDurable.ReferenceRootPresent finalDurable.Detail
                recordAssertion "redis-restart.manifest-relationship-present" finalDurable.ManifestRelationshipPresent finalDurable.Detail

                recordAssertion
                    "redis-restart.logical-count-plus-one"
                    (finalDurable.LogicalCount = explicitDurableBaseline.LogicalCount + 1L)
                    $"baseline={explicitDurableBaseline.LogicalCount}; observed={finalDurable.LogicalCount}"

                recordAssertion
                    "redis-restart.workflow-unchanged"
                    (finalDurable.WorkflowFingerprint.Equals(beforeRestart.WorkflowFingerprint, StringComparison.Ordinal)
                     && finalDurable.WorkflowFingerprint.Equals(explicitDurableBaseline.WorkflowFingerprint, StringComparison.Ordinal))
                    $"beforeRestartLength={beforeRestart.WorkflowFingerprint.Length}; explicitBaselineLength={explicitDurableBaseline.WorkflowFingerprint.Length}; finalLength={finalDurable.WorkflowFingerprint.Length}"

                recordAssertion "redis-restart.physical-active-count-one" (finalDurable.PhysicalActiveCount = 1L) finalDurable.Detail

                let labels = Dictionary<string, string>()
                labels["stage"] <- "settle"
                labels["outcome"] <- "completed"

                RedisRestartRuntime.recordSample writer runId "stimulus-messages" "grace_manifest_contribution_messages_total.delta" stimulusMessageDelta labels

                RedisRestartRuntime.recordSample
                    writer
                    runId
                    "stimulus-durations"
                    "grace_manifest_contribution_processing_duration_milliseconds_count.delta"
                    stimulusDurationDelta
                    labels
            with
            | ex -> failures.Add(ex.ToString())

            match host with
            | Some state ->
                try
                    do! AspireTestHost.stopIsolatedAsync state
                with
                | ex -> failures.Add($"cleanup: {ex}")
            | None -> ()

            if
                not
                    (
                        assertions
                        |> Seq.exists (fun assertion -> assertion.AssertionId = "redis-restart.evidence-integrity")
                    )
            then
                try
                    let inventoryErrors = ProducerInventory.validate (expectedMessageIds.ToArray()) (observedMessageIds.ToArray())
                    let fileIntegrity = BaselineRuntime.verifyEvidenceIntegrity writer
                    let inventoryDetail = String.Join("; ", inventoryErrors)

                    recordAssertion
                        "redis-restart.evidence-integrity"
                        (fileIntegrity && inventoryErrors.Length = 0)
                        $"path={writer.Path}; expected={expectedMessageIds.Count}; observed={observedMessageIds.Count}; inventory={inventoryDetail}"
                with
                | ex -> recordAssertion "redis-restart.evidence-integrity" false ex.Message

            RedisRestart.requiredAssertionIds
            |> Array.iter (fun assertionId ->
                if
                    not
                        (
                            assertions
                            |> Seq.exists (fun assertion -> assertion.AssertionId = assertionId)
                        )
                then
                    recordAssertion assertionId false "The runtime failed before this assertion could be evaluated.")

            let summary = ScenarioSummary.derive runId "redis-restart" RedisRestart.requiredAssertionIds (assertions.ToArray()) (failures.ToArray()) false

            writer.Append summary
            TestContext.Progress.WriteLine($"MCA Redis restart evidence directory: {evidenceDirectory}")
            TestContext.Progress.Flush()

            Assert.That(
                summary.Outcome,
                Is.EqualTo("Passed"),
                $"Evidence: {evidenceDirectory}{Environment.NewLine}{String.Join(Environment.NewLine, failures)}"
            )
        }
