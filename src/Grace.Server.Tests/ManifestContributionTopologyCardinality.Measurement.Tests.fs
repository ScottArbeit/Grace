namespace Grace.Server.Measurements

open Grace.Server.Tests
open Grace.Server.Tests.Measurement
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
open Grace.Types.ManifestContributionAccounting
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Reference
open Grace.Types.RepositoryContentCounter
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json

/// Selects the declared topology graph while keeping one shared R1 measurement runtime.
type private TopologyKind =
    | HotManifest
    | HighlySharedDirectoryVersion

/// Retains one scenario's evidence state until shared-host cleanup can affect its terminal summary.
type private TopologyExecution =
    {
        ScenarioId: string
        RequiredAssertionIds: string array
        Assertions: ResizeArray<MeasurementAssertion>
        Failures: ResizeArray<string>
        Expectation: TopologyCardinalityExpectation option
        ExpectedProducerIds: string array
        ObservedProducerIds: string array
        PrerequisiteSkipped: bool
    }

/// Runs both topology graphs through the selected-process host introduced by the Baseline tracer.
module private TopologyCardinalityRuntime =

    [<Literal>]
    let SelectedTopologyCount = 3

    /// Returns the stable scenario identity used by assertions and retained evidence.
    let scenarioId kind =
        match kind with
        | TopologyKind.HotManifest -> "hot-manifest"
        | TopologyKind.HighlySharedDirectoryVersion -> "highly-shared"

    /// Returns the exact required assertion set for one supported topology.
    let requiredAssertionIds kind =
        match kind with
        | TopologyKind.HotManifest -> HotManifest.requiredAssertionIds
        | TopologyKind.HighlySharedDirectoryVersion -> HighlySharedDirectoryVersion.requiredAssertionIds

    /// Converts one exact relationship into its canonical storage identity for set comparison.
    let relationshipId relationship =
        let key =
            ExactRelationshipKey.create relationship
            |> Result.defaultWith invalidOp

        $"{key.PartitionKey}/{key.ItemId}"

    /// Reads every Reference-root relationship in the exact incoming partitions declared by the topology.
    let readReferenceRootRelationshipsAsync state repositoryId (assets: BaselineAsset array) =
        task {
            let roots =
                assets
                |> Array.distinctBy (fun asset -> asset.Root.DirectoryVersionId)

            let relationships = ResizeArray<ExactRelationship>()
            let mutable index = 0

            while index < roots.Length do
                let! partition =
                    BaselineRuntime.readExactRelationshipsAsync
                        state
                        (ExactRelationshipPartition.IncomingDirectoryVersion(repositoryId, roots[index].Root.DirectoryVersionId))

                partition
                |> Array.iter (function
                    | ExactRelationship.ReferenceRoot _ as relationship -> relationships.Add relationship
                    | _ -> ())

                index <- index + 1

            return relationships.ToArray()
        }

    /// Reads every DirectoryVersion-manifest relationship from the topology's one shared manifest partition.
    let readManifestRelationshipsAsync state repositoryId (asset: BaselineAsset) =
        task {
            let! relationships =
                BaselineRuntime.readExactRelationshipsAsync
                    state
                    (ExactRelationshipPartition.Manifest(repositoryId, asset.Manifest.StoragePoolId, asset.Manifest.ManifestAddress))

            return
                relationships
                |> Array.choose (function
                    | ExactRelationship.DirectoryVersionManifest _ as relationship -> Some relationship
                    | _ -> None)
        }

    /// Returns one exact counter count, or an impossible sentinel when durable cardinality is missing or duplicated.
    let logicalCount repositoryId (asset: BaselineAsset) (counters: RepositoryContentCounterDto array) =
        let matching =
            counters
            |> Array.filter (fun counter ->
                counter.RepositoryId = repositoryId
                && counter.StoragePoolId = asset.Manifest.StoragePoolId
                && counter.ManifestAddress = asset.Manifest.ManifestAddress)

        if matching.Length = 1 then matching[0].Count else Int64.MinValue

    /// Counts exactly one completed zero-to-one workflow and rejects any other workflow for the shared manifest.
    let workflowCount repositoryId (asset: BaselineAsset) (workflows: ManifestContributionWorkflowDto array) =
        let matching =
            workflows
            |> Array.filter (fun workflow ->
                workflow.RepositoryId = repositoryId
                && workflow.StoragePoolId = asset.Manifest.StoragePoolId
                && workflow.ManifestAddress = asset.Manifest.ManifestAddress)

        let exactZeroToOne =
            matching
            |> Array.forall (fun workflow ->
                workflow.Direction = ManifestContributionDirection.Increment
                && workflow.CounterRevision = 1L
                && workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
                && workflow.Ranges.Length = 1
                && workflow.CompletedRanges.Length = 1
                && workflow.FailedRanges.Length = 0)

        if exactZeroToOne then int64 matching.Length else Int64.MinValue

    /// Reads the one physical active-manifest count and rejects missing, duplicate, or inconsistent ranges.
    let physicalActiveCount (asset: BaselineAsset) (streams: ContentBlockMetadataEvent array array) =
        let matching =
            streams
            |> Array.map (fun events ->
                events
                |> Array.fold (fun current event -> ContentBlockMetadataDto.UpdateDto event current) ContentBlockMetadataDto.Empty)
            |> Array.choose (fun dto ->
                dto.Metadata
                |> Option.filter (fun metadata ->
                    metadata.StoragePoolId = asset.Manifest.StoragePoolId
                    && metadata.ContentBlockAddress = asset.BlockAddress))

        if matching.Length <> 1
           || matching[0].Ranges.Length = 0 then
            Int64.MinValue
        else
            let counts =
                matching[0].Ranges
                |> Array.map (fun range -> range.ActiveManifestCount)
                |> Array.distinct

            if counts.Length = 1 then counts[0] else Int64.MinValue

    /// Reads the exact shared-manifest graph without using broker completion as a durable-state proxy.
    let readObservationAsync state repositoryId (assets: BaselineAsset array) =
        task {
            let! referenceRoots = readReferenceRootRelationshipsAsync state repositoryId assets
            let! manifests = readManifestRelationshipsAsync state repositoryId assets[0]
            let! counters = BaselineRuntime.readActorSnapshotsAsync<RepositoryContentCounterDto> state "RepoContentCounter"
            let! workflows = BaselineRuntime.readActorSnapshotsAsync<ManifestContributionWorkflowDto> state "ManifestContributionWorkflow"
            let! metadata = BaselineRuntime.readActorEventStreamsAsync<ContentBlockMetadataEvent> state "ContentBlockMetadata"

            return
                referenceRoots |> Array.map relationshipId,
                manifests |> Array.map relationshipId,
                logicalCount repositoryId assets[0] counters,
                workflowCount repositoryId assets[0] workflows,
                physicalActiveCount assets[0] metadata
        }

    /// Waits for the exact durable topology graph using state rereads rather than a correctness sleep.
    let waitForObservationAsync state (expected: TopologyCardinalityExpectation) (assets: BaselineAsset array) =
        task {
            let timeoutAt = DateTime.UtcNow.AddSeconds(45.0)

            let mutable observed = Array.empty, Array.empty, Int64.MinValue, Int64.MinValue, Int64.MinValue

            let mutable complete = false

            while not complete && DateTime.UtcNow < timeoutAt do
                let! current = readObservationAsync state (Guid.Parse expected.RepositoryId) assets
                observed <- current
                let referenceRoots, manifests, logical, workflows, physical = current

                complete <-
                    referenceRoots.Length = expected.ReferenceRootRelationshipIds.Length
                    && manifests.Length = expected.ManifestRelationshipIds.Length
                    && logical = expected.LogicalCount
                    && workflows = expected.WorkflowCount
                    && physical = expected.PhysicalActiveCount

                if not complete then
                    do! Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(250.0))

            return observed
        }

    /// Appends one scenario-scoped sample with stable settlement labels.
    let recordSample (writer: EvidenceWriter) runId scenarioId sampleId name value labels =
        writer.Append(MeasurementSample.Create(runId, scenarioId, sampleId, name, value, labels))

    /// Creates one topology's roots while persisting each distinct DirectoryVersion exactly once.
    let createRootsAsync state ownerId organizationId repositoryId kind rootFileIndex blockAddress manifest bytes =
        task {
            match kind with
            | TopologyKind.HotManifest ->
                let roots = ResizeArray<DirectoryVersion>()
                let mutable index = 0

                while index < SelectedTopologyCount do
                    let root = BaselineRuntime.createRoot ownerId organizationId repositoryId rootFileIndex manifest bytes

                    do! BaselineRuntime.saveRootAsync state ownerId organizationId repositoryId root
                    roots.Add root
                    index <- index + 1

                return roots.ToArray()
            | TopologyKind.HighlySharedDirectoryVersion ->
                let root = BaselineRuntime.createRoot ownerId organizationId repositoryId rootFileIndex manifest bytes

                do! BaselineRuntime.saveRootAsync state ownerId organizationId repositoryId root
                return Array.replicate SelectedTopologyCount root
        }

    /// Builds the exact topology declaration after setup identities exist and before any explicit Save stimulus.
    let createExpectation kind repositoryId (assets: BaselineAsset array) =
        let scenarioId = scenarioId kind

        let setupMessageIds =
            assets
            |> Array.map (fun asset -> $"Reference/{asset.RebaseReferenceId}/Created")

        let stimulusMessageIds =
            assets
            |> Array.map (fun asset -> $"Reference/{asset.SaveReferenceId}/Created")

        let referenceRootRelationships =
            assets
            |> Array.map (fun asset ->
                ExactRelationship.ReferenceRoot
                    { RepositoryId = repositoryId; RootDirectoryVersionId = asset.Root.DirectoryVersionId; ReferenceId = asset.SaveReferenceId })

        let manifestRelationships =
            assets
            |> Array.map (fun asset ->
                ExactRelationship.DirectoryVersionManifest
                    {
                        RepositoryId = repositoryId
                        StoragePoolId = asset.Manifest.StoragePoolId
                        ManifestAddress = asset.Manifest.ManifestAddress
                        DirectoryVersionId = asset.Root.DirectoryVersionId
                    })
            |> Array.distinct

        {
            ScenarioId = scenarioId
            RepositoryId = string repositoryId
            RequiredAssertionIds = requiredAssertionIds kind
            DeclaredIdentityIds =
                Array.concat [| [| $"repository:{repositoryId}" |]
                                assets
                                |> Array.map (fun asset -> $"branch:{asset.Branch.BranchId}")
                                assets
                                |> Array.map (fun asset -> $"reference:{asset.RebaseReferenceId}")
                                assets
                                |> Array.map (fun asset -> $"reference:{asset.SaveReferenceId}")
                                assets
                                |> Array.map (fun asset -> asset.Root.DirectoryVersionId)
                                |> Array.distinct
                                |> Array.map (fun rootId -> $"directory-version:{rootId}") |]
            SetupMessageIds = setupMessageIds
            StimulusMessageIds = stimulusMessageIds
            ReferenceRootRelationshipIds =
                referenceRootRelationships
                |> Array.map relationshipId
            ManifestRelationshipIds = manifestRelationships |> Array.map relationshipId
            LogicalCount =
                match kind with
                | TopologyKind.HotManifest -> int64 SelectedTopologyCount
                | TopologyKind.HighlySharedDirectoryVersion -> 1L
            WorkflowCount = 1L
            PhysicalActiveCount = 1L
        }

    /// Executes one isolated topology and retains its assertion state for post-cleanup finalization.
    let executeAsync (writer: EvidenceWriter) runId state ownerId organizationId kind prerequisitePassed =
        task {
            let scenarioId = scenarioId kind
            let requiredAssertionIds = requiredAssertionIds kind
            let assertions = ResizeArray<MeasurementAssertion>()
            let failures = ResizeArray<string>()
            let mutable expectation: TopologyCardinalityExpectation option = None
            let mutable expectedProducerIds = Array.empty
            let mutable observedProducerIds = Array.empty

            let recordAssertion assertionId passed detail =
                let assertion = MeasurementAssertion.Create(runId, scenarioId, assertionId, passed, detail)
                assertions.Add assertion
                writer.Append assertion

            if not prerequisitePassed then
                return
                    {
                        ScenarioId = scenarioId
                        RequiredAssertionIds = requiredAssertionIds
                        Assertions = assertions
                        Failures = failures
                        Expectation = None
                        ExpectedProducerIds = Array.empty
                        ObservedProducerIds = Array.empty
                        PrerequisiteSkipped = true
                    }
            else
                try
                    let repositoryId = Guid.NewGuid()
                    ManifestContributionGroupedRuntime.registerRepository scenarioId repositoryId
                    let! repositoryBaseline = BaselineRuntime.scrapeMetricsAsync state
                    let! defaultBranchId, defaultReferenceId = BaselineRuntime.createRepositoryAsync state ownerId organizationId repositoryId

                    let! defaultBranch = BaselineRuntime.getBranchAsync state ownerId organizationId repositoryId defaultBranchId

                    if defaultBranch.LatestReference.ReferenceId
                       <> defaultReferenceId then
                        invalidOp "The topology repository default Reference inventory did not match its persisted branch."

                    let defaultMessageId = $"Reference/{defaultReferenceId}/Created"
                    let! defaultEnvelopes = BaselineRuntime.observeReferenceEnvelopesAsync state [| defaultMessageId |] $"{scenarioId} repository default"

                    let defaultObserved =
                        defaultEnvelopes
                        |> Array.map (fun envelope -> envelope.MessageId)

                    let! _, _, _ = BaselineRuntime.waitForCompletedSettlementDeltaAsync state 1L repositoryBaseline

                    let manifestAssetIndex =
                        match kind with
                        | TopologyKind.HotManifest -> 100
                        | TopologyKind.HighlySharedDirectoryVersion -> 200

                    let! blockAddress, manifest, bytes = BaselineRuntime.createManifestAssetAsync state ownerId organizationId repositoryId manifestAssetIndex

                    let! roots = createRootsAsync state ownerId organizationId repositoryId kind manifestAssetIndex blockAddress manifest bytes

                    let! setupBaseline = BaselineRuntime.scrapeMetricsAsync state
                    let assets = ResizeArray<BaselineAsset>()
                    let mutable index = 0

                    while index < SelectedTopologyCount do
                        let rebaseReferenceId = Guid.NewGuid()
                        let saveReferenceId = Guid.NewGuid()

                        let! branch = BaselineRuntime.createBranchAsync state ownerId organizationId repositoryId defaultBranch index rebaseReferenceId

                        assets.Add(
                            {
                                BlockAddress = blockAddress
                                Manifest = manifest
                                Root = roots[index]
                                Branch = branch
                                RebaseReferenceId = rebaseReferenceId
                                SaveReferenceId = saveReferenceId
                            }
                        )

                        index <- index + 1

                    let assetArray = assets.ToArray()
                    let declared = createExpectation kind repositoryId assetArray
                    expectation <- Some declared

                    let! setupEnvelopes = BaselineRuntime.observeReferenceEnvelopesAsync state declared.SetupMessageIds $"{scenarioId} branch Rebase"

                    let setupObserved =
                        setupEnvelopes
                        |> Array.map (fun envelope -> envelope.MessageId)

                    let! setupMessageDelta, setupDurationDelta, saveBaseline =
                        BaselineRuntime.waitForCompletedSettlementDeltaAsync state (int64 declared.SetupMessageIds.Length) setupBaseline

                    let setupComplete =
                        setupMessageDelta = int64 declared.SetupMessageIds.Length
                        && setupDurationDelta = int64 declared.SetupMessageIds.Length
                        && ProducerInventory.validate declared.SetupMessageIds setupObserved
                           |> Array.isEmpty

                    recordAssertion
                        $"{scenarioId}.setup-deliveries-completed"
                        setupComplete
                        $"observed={setupObserved.Length}; messages={setupMessageDelta}; durations={setupDurationDelta}"

                    let labels = Dictionary<string, string>()
                    labels["stage"] <- "settle"
                    labels["outcome"] <- "completed"
                    recordSample writer runId scenarioId "setup-messages" "grace_manifest_contribution_messages_total.delta" setupMessageDelta labels

                    recordSample
                        writer
                        runId
                        scenarioId
                        "setup-durations"
                        "grace_manifest_contribution_processing_duration_milliseconds_count.delta"
                        setupDurationDelta
                        labels

                    let saves = ResizeArray<Reference.ReferenceDto>()
                    index <- 0

                    while index < assetArray.Length do
                        let! saved = BaselineRuntime.saveReferenceAsync state ownerId organizationId repositoryId assetArray[index]

                        saves.Add saved
                        index <- index + 1

                    let! stimulusEnvelopes = BaselineRuntime.observeReferenceEnvelopesAsync state declared.StimulusMessageIds $"{scenarioId} explicit Save"

                    let stimulusObserved =
                        stimulusEnvelopes
                        |> Array.map (fun envelope -> envelope.MessageId)

                    let! referenceRoots, manifests, logical, workflows, physical = waitForObservationAsync state declared assetArray

                    let! messageDelta, durationDelta, stimulusTerminal =
                        BaselineRuntime.waitForCompletedSettlementDeltaAsync state (int64 declared.StimulusMessageIds.Length) saveBaseline

                    let observation =
                        {
                            SetupObservedMessageIds = setupObserved
                            SetupSettledBeforeStimulusBaseline = setupComplete
                            StimulusObservedMessageIds = stimulusObserved
                            ReferenceRootRelationshipIds = referenceRoots
                            ManifestRelationshipIds = manifests
                            LogicalCount = logical
                            WorkflowCount = workflows
                            PhysicalActiveCount = physical
                            MessageDelta = messageDelta
                            DurationDelta = durationDelta
                        }

                    let evaluated = TopologyCardinality.evaluate declared observation

                    let persistedSaves =
                        saves.Count = assetArray.Length
                        && saves
                           |> Seq.forall (fun saved ->
                               saved.ReferenceType = ReferenceType.Save
                               && assetArray
                                  |> Array.exists (fun asset ->
                                      saved.ReferenceId = asset.SaveReferenceId
                                      && saved.DirectoryId = asset.Root.DirectoryVersionId))

                    let detail =
                        $"referenceRoots={referenceRoots.Length}; manifests={manifests.Length}; logical={logical}; workflows={workflows}; physical={physical}"

                    recordAssertion
                        $"{scenarioId}.stimulus-deliveries-completed"
                        (persistedSaves
                         && evaluated.StimulusDeliveriesCompleted)
                        $"persisted={persistedSaves}; observed={stimulusObserved.Length}; messages={messageDelta}; durations={durationDelta}"

                    recordAssertion $"{scenarioId}.reference-root-cardinality" evaluated.ReferenceRootCardinality detail
                    recordAssertion $"{scenarioId}.manifest-relationship-cardinality" evaluated.ManifestRelationshipCardinality detail
                    recordAssertion $"{scenarioId}.logical-count" evaluated.LogicalCount detail
                    recordAssertion $"{scenarioId}.workflow-count" evaluated.WorkflowCount detail
                    recordAssertion $"{scenarioId}.physical-active-count" evaluated.PhysicalActiveCount detail
                    recordAssertion $"{scenarioId}.message-delta" evaluated.MessageDelta $"delta={messageDelta}"
                    recordAssertion $"{scenarioId}.duration-delta" evaluated.DurationDelta $"delta={durationDelta}"
                    recordSample writer runId scenarioId "stimulus-messages" "grace_manifest_contribution_messages_total.delta" messageDelta labels

                    recordSample
                        writer
                        runId
                        scenarioId
                        "stimulus-durations"
                        "grace_manifest_contribution_processing_duration_milliseconds_count.delta"
                        durationDelta
                        labels

                    BaselineRuntime.recordMetricSnapshot writer runId scenarioId "stimulus" "baseline" saveBaseline
                    BaselineRuntime.recordMetricSnapshot writer runId scenarioId "stimulus" "terminal" stimulusTerminal

                    expectedProducerIds <-
                        Array.concat [| [| defaultMessageId |]
                                        declared.SetupMessageIds
                                        declared.StimulusMessageIds |]

                    observedProducerIds <-
                        Array.concat [| defaultObserved
                                        setupObserved
                                        stimulusObserved |]
                with
                | ex -> failures.Add(ex.ToString())

                return
                    {
                        ScenarioId = scenarioId
                        RequiredAssertionIds = requiredAssertionIds
                        Assertions = assertions
                        Failures = failures
                        Expectation = expectation
                        ExpectedProducerIds = expectedProducerIds
                        ObservedProducerIds = observedProducerIds
                        PrerequisiteSkipped = false
                    }
        }

    /// Adds the assertion that is intentionally delayed until both scenario identity sets are known.
    let recordIdentityAssertion (writer: EvidenceWriter) runId passed detail (execution: TopologyExecution) =
        let assertion = MeasurementAssertion.Create(runId, execution.ScenarioId, $"{execution.ScenarioId}.identity-isolation", passed, detail)

        execution.Assertions.Add assertion
        writer.Append assertion

    /// Adds the final evidence-integrity assertion after runtime cleanup has reached a terminal result.
    let recordEvidenceIntegrityAssertion (writer: EvidenceWriter) runId passed (execution: TopologyExecution) =
        let assertion = MeasurementAssertion.Create(runId, execution.ScenarioId, $"{execution.ScenarioId}.evidence-integrity", passed, $"path={writer.Path}")

        execution.Assertions.Add assertion
        writer.Append assertion

    /// Fills every missing assertion as failed so a runtime exception cannot create a false complete set.
    let fillMissingAssertions (writer: EvidenceWriter) runId (execution: TopologyExecution) =
        execution.RequiredAssertionIds
        |> Array.iter (fun assertionId ->
            if
                not
                    (
                        execution.Assertions
                        |> Seq.exists (fun assertion -> assertion.AssertionId = assertionId)
                    )
            then
                let assertion =
                    MeasurementAssertion.Create(runId, execution.ScenarioId, assertionId, false, "The runtime failed before this assertion could be evaluated.")

                execution.Assertions.Add assertion
                writer.Append assertion)

/// Proves both supported topology graphs in one fresh explicitly selected test process.
[<NonParallelizable>]
type ManifestContributionTopologyCardinalityMeasurementTests() =

    /// Emits exact HotManifest and HighlySharedDirectoryVersion evidence without a second measurement architecture.
    [<Test; Explicit("Run only through the focused MCA topology-cardinality measurement selector.")>]
    member _.``isolated topology pair emits truthful completed evidence``() =
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

            use writer = new EvidenceWriter(evidenceDirectory, BaselineRuntime.MaximumRecordBytes)
            let plan = [| "hot-manifest"; "highly-shared" |]

            writer.Append(MeasurementRun.Create(runId, commitSha, worktree, status, command, evidenceDirectory, plan))

            let executions = ResizeArray<TopologyExecution>()
            let mutable host: TestHostState option = None
            let mutable fixtureExpectedProducerIds = Array.empty
            let mutable fixtureObservedProducerIds = Array.empty

            try
                let bootstrapUserId = Guid.NewGuid().ToString("D")
                let! state = ManifestContributionGroupedRuntime.acquireAsync bootstrapUserId
                host <- Some state
                ManifestContributionGroupedRuntime.selectBootstrapUser state bootstrapUserId
                let! _ = AspireTestHost.drainServiceBusAsync state
                let ownerId = Guid.NewGuid()
                let organizationId = Guid.NewGuid()
                do! BaselineRuntime.createOwnerAsync state ownerId
                do! BaselineRuntime.createOrganizationAsync state ownerId organizationId
                let fixtureRepositoryId = Guid.NewGuid()

                let! fixtureBranchId, fixtureReferenceId = BaselineRuntime.createRepositoryAsync state ownerId organizationId fixtureRepositoryId

                let! fixtureBranch = BaselineRuntime.getBranchAsync state ownerId organizationId fixtureRepositoryId fixtureBranchId

                if fixtureBranch.LatestReference.ReferenceId
                   <> fixtureReferenceId then
                    invalidOp "The fixture repository Reference inventory did not match its persisted branch."

                let fixtureMessageId = $"Reference/{fixtureReferenceId}/Created"

                let! fixtureEnvelopes =
                    BaselineRuntime.observeReferenceEnvelopesAsync state [| fixtureMessageId |] "selected-process fixture repository default"

                let fixtureObserved =
                    fixtureEnvelopes
                    |> Array.map (fun envelope -> envelope.MessageId)

                fixtureExpectedProducerIds <- [| fixtureMessageId |]
                fixtureObservedProducerIds <- fixtureObserved
                let! _ = BaselineRuntime.waitForCompletedSettlementSamplesAsync state

                let! hot = TopologyCardinalityRuntime.executeAsync writer runId state ownerId organizationId TopologyKind.HotManifest true

                executions.Add hot

                let hotPassedPrerequisite =
                    hot.Failures.Count = 0
                    && hot.Assertions
                       |> Seq.forall (fun assertion -> assertion.Passed)

                let! highlyShared =
                    TopologyCardinalityRuntime.executeAsync
                        writer
                        runId
                        state
                        ownerId
                        organizationId
                        TopologyKind.HighlySharedDirectoryVersion
                        hotPassedPrerequisite

                executions.Add highlyShared
            with
            | ex ->
                if executions.Count = 0 then
                    executions.Add(
                        {
                            ScenarioId = "hot-manifest"
                            RequiredAssertionIds = HotManifest.requiredAssertionIds
                            Assertions = ResizeArray<MeasurementAssertion>()
                            Failures = ResizeArray<string>([ ex.ToString() ])
                            Expectation = None
                            ExpectedProducerIds = Array.empty
                            ObservedProducerIds = Array.empty
                            PrerequisiteSkipped = false
                        }
                    )

                if executions.Count = 1 then
                    executions.Add(
                        {
                            ScenarioId = "highly-shared"
                            RequiredAssertionIds = HighlySharedDirectoryVersion.requiredAssertionIds
                            Assertions = ResizeArray<MeasurementAssertion>()
                            Failures = ResizeArray<string>()
                            Expectation = None
                            ExpectedProducerIds = Array.empty
                            ObservedProducerIds = Array.empty
                            PrerequisiteSkipped = true
                        }
                    )

            match host with
            | Some state ->
                try
                    do! ManifestContributionGroupedRuntime.releaseAsync state
                with
                | ex ->
                    executions
                    |> Seq.filter (fun execution -> not execution.PrerequisiteSkipped)
                    |> Seq.iter (fun execution -> execution.Failures.Add($"cleanup: {ex}"))
            | None -> ()

            let executed =
                executions
                |> Seq.filter (fun execution -> not execution.PrerequisiteSkipped)
                |> Seq.toArray

            let declarations =
                executed
                |> Array.choose (fun execution -> execution.Expectation)

            let identityErrors =
                Array.append
                    (TopologyCardinality.validateScenarioIsolation declarations)
                    (ProducerInventory.validate
                        (Array.append
                            fixtureExpectedProducerIds
                            (executed
                             |> Array.collect (fun execution -> execution.ExpectedProducerIds)))
                        (Array.append
                            fixtureObservedProducerIds
                            (executed
                             |> Array.collect (fun execution -> execution.ObservedProducerIds))))

            executed
            |> Array.iter (TopologyCardinalityRuntime.recordIdentityAssertion writer runId (identityErrors.Length = 0) (String.Join("; ", identityErrors)))

            let evidenceIntegrity =
                try
                    BaselineRuntime.verifyEvidenceIntegrity writer
                with
                | :? JsonException -> false
                | :? IOException -> false

            executed
            |> Array.iter (TopologyCardinalityRuntime.recordEvidenceIntegrityAssertion writer runId evidenceIntegrity)

            let summaries =
                executions
                |> Seq.map (fun execution ->
                    if not execution.PrerequisiteSkipped then
                        TopologyCardinalityRuntime.fillMissingAssertions writer runId execution

                    let summary =
                        ScenarioSummary.derive
                            runId
                            execution.ScenarioId
                            execution.RequiredAssertionIds
                            (execution.Assertions.ToArray())
                            (execution.Failures.ToArray())
                            execution.PrerequisiteSkipped

                    writer.Append summary
                    summary)
                |> Seq.toArray

            TestContext.Progress.WriteLine($"MCA topology-cardinality evidence directory: {evidenceDirectory}")
            TestContext.Progress.Flush()

            let failures =
                executions
                |> Seq.collect (fun execution -> execution.Failures)

            Assert.That(
                summaries
                |> Array.forall (fun summary -> summary.Outcome = "Passed"),
                Is.True,
                $"Evidence: {evidenceDirectory}{Environment.NewLine}{String.Join(Environment.NewLine, failures)}"
            )
        }
