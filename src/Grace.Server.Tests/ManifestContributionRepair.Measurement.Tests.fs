namespace Grace.Server.Measurements

open Grace.Server
open Grace.Server.ManifestContributionDiagnosis
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
open Grace.Types.Reference
open Grace.Types.RepositoryContentCounter
open Microsoft.Azure.Cosmos
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks

module RepairEvidence = Grace.Server.Tests.Measurement.Repair
module ProductionRepair = Grace.Server.ManifestContributionRepair

/// Retains exact serialized logical, workflow, and physical state across projection-only repair republication.
type private RepairStableState = { Logical: string; Workflow: string; Physical: string }

/// Implements the Repair-specific corruption, production-route, and state-reread operations over the R1 host.
module private RepairRuntime =

    [<Literal>]
    let ScenarioId = "repair"

    [<Literal>]
    let MaximumRelationships = 20

    /// Formats a retained diagnosis array without turning a malformed null field into an evidence-writing failure.
    let joinRetainedValues (separator: string) (values: string array) = if isNull values then "<null>" else String.Join(separator, values)

    /// Reads the persisted correlation from one retained Reference-created body without trusting its broker header.
    let referenceCreatedBodyCorrelationId (envelope: CapturedReferenceEnvelope) =
        let graceEvent = JsonSerializer.Deserialize<GraceEvent>(envelope.Body, Constants.JsonSerializerOptions)

        match graceEvent with
        | GraceEvent.ReferenceEvent referenceEvent ->
            match referenceEvent.Event with
            | ReferenceEventType.Created _ -> referenceEvent.Metadata.CorrelationId
            | _ -> invalidOp "The retained Reference envelope body was not a Created event."
        | _ -> invalidOp "The retained broker body was not a Reference event."

    /// Requires exactly one matching durable snapshot so duplicate persisted state cannot pass as unchanged.
    let private exactlyOne description predicate values =
        let matches = values |> Array.filter predicate

        if matches.Length <> 1 then
            invalidOp $"{description} required exactly one durable snapshot but found {matches.Length}."

        matches[0]

    /// Captures the exact counter, workflow, and ContentBlock metadata facts that repair must not replay.
    let readStableStateAsync state repositoryId (asset: BaselineAsset) =
        task {
            let! counters = BaselineRuntime.readActorSnapshotsAsync<RepositoryContentCounterDto> state "RepoContentCounter"

            let counter =
                counters
                |> exactlyOne "Repair logical counter" (fun value ->
                    value.RepositoryId = repositoryId
                    && value.StoragePoolId = asset.Manifest.StoragePoolId
                    && value.ManifestAddress = asset.Manifest.ManifestAddress)

            let! workflows = BaselineRuntime.readActorSnapshotsAsync<ManifestContributionWorkflowDto> state "ManifestContributionWorkflow"

            let workflow =
                workflows
                |> exactlyOne "Repair contribution workflow" (fun value ->
                    value.RepositoryId = repositoryId
                    && value.StoragePoolId = asset.Manifest.StoragePoolId
                    && value.ManifestAddress = asset.Manifest.ManifestAddress)

            let! metadataStreams = BaselineRuntime.readActorEventStreamsAsync<ContentBlockMetadataEvent> state "ContentBlockMetadata"

            let metadata =
                metadataStreams
                |> Array.map (fun events ->
                    events
                    |> Array.fold (fun current event -> ContentBlockMetadataDto.UpdateDto event current) ContentBlockMetadataDto.Empty)
                |> exactlyOne "Repair physical ContentBlock metadata" (fun value ->
                    value.Metadata
                    |> Option.exists (fun metadata ->
                        metadata.StoragePoolId = asset.Manifest.StoragePoolId
                        && metadata.ContentBlockAddress = asset.BlockAddress))

            return
                {
                    Logical = JsonSerializer.Serialize(counter, Constants.JsonSerializerOptions)
                    Workflow = JsonSerializer.Serialize(workflow, Constants.JsonSerializerOptions)
                    Physical = JsonSerializer.Serialize(metadata, Constants.JsonSerializerOptions)
                }
        }

    /// Deletes only the canonical Reference-root projection item through the fixture Cosmos client.
    let deleteReferenceRootAsync state relationship =
        task {
            let key =
                ExactRelationshipKey.create relationship
                |> Result.defaultWith invalidOp

            use client = AspireTestHost.createCosmosClient state
            let container = client.GetContainer(state.CosmosDatabaseName, state.CosmosContainerName)
            let! response = container.DeleteItemAsync<JsonElement>(key.ItemId, PartitionKey key.PartitionKey)

            if response.StatusCode <> HttpStatusCode.NoContent then
                invalidOp $"Reference-root deletion returned {response.StatusCode}."
        }

    /// Waits for one exact relationship predicate without treating it as broker settlement.
    let waitForRelationshipAsync state relationship expected =
        task {
            let timeoutAt = DateTime.UtcNow.AddSeconds(45.0)
            let mutable present = not expected

            while present <> expected && DateTime.UtcNow < timeoutAt do
                let! current = BaselineRuntime.exactRelationshipExistsAsync state relationship
                present <- current

                if present <> expected then do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            return present
        }

    /// Runs the real production diagnosis route and preserves its exact signed JSON for repair.
    let diagnoseReferenceAsync state referenceId =
        task {
            let parameters = DiagnoseManifestContributionParameters()
            parameters.ReferenceId <- string referenceId
            parameters.MaxRelationships <- MaximumRelationships
            use! response = state.Client.PostAsync("/admin/manifest-contribution/diagnose", createJsonContent parameters)
            let! body = BaselineRuntime.requireOkAsync "POST /admin/manifest-contribution/diagnose" response
            return deserialize<ManifestContributionDiagnosisReport> body, body
        }

    /// Runs the real production repair route with an explicit request correlation identity.
    let repairAsync state (correlationId: string) (reportJson: string) (reportSha256: string) execute =
        task {
            let parameters = ProductionRepair.RepairManifestContributionParameters()
            parameters.ReportJson <- reportJson
            parameters.ExpectedReportSha256 <- reportSha256
            parameters.Execute <- execute
            use request = new HttpRequestMessage(HttpMethod.Post, "/admin/manifest-contribution/repair")
            request.Headers.Add(Constants.CorrelationIdHeaderKey, correlationId)
            request.Content <- createJsonContent parameters
            use! response = state.Client.SendAsync request
            let! body = BaselineRuntime.requireOkAsync "POST /admin/manifest-contribution/repair" response
            return deserialize<ProductionRepair.ManifestContributionRepairReport> body
        }

    /// Verifies bounded UTF-8 NDJSON records without accepting a scenario outcome from the caller.
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
               <= BaselineRuntime.MaximumRecordBytes
               && try
                   use document = JsonDocument.Parse line

                   not (
                       String.IsNullOrWhiteSpace(
                           document
                               .RootElement
                               .GetProperty("RecordType")
                               .GetString()
                       )
                   )
                  with
                  | :? JsonException -> false)

/// Proves production repair republication attribution in one fresh explicitly selected test process.
[<NonParallelizable>]
type ManifestContributionRepairMeasurementTests() =

    /// Emits truthful evidence for one diagnosed, dry-run, executed, settled, and state-preserving Reference-root repair.
    [<Test; Explicit("Run only through the focused MCA Repair measurement selector.")>]
    member _.``repair republication restores only the missing Reference root``() =
        task {
            let runId = Guid.NewGuid().ToString("N")

            let! preflight =
                MeasurementPreflight.prepareAsync
                    runId
                    RepairRuntime.ScenarioId
                    RepairEvidence.requiredAssertionIds
                    BaselineRuntime.MaximumRecordBytes
                    Environment.GetEnvironmentVariable
                    BaselineRuntime.runGitAsync
                    (fun directory maximumBytes -> new EvidenceWriter(directory, maximumBytes))

            let ready =
                match preflight with
                | Ready value -> value
                | Terminal terminal ->
                    terminal.FallbackDiagnostic
                    |> Option.iter (fun diagnostic -> TestContext.Progress.WriteLine diagnostic)

                    terminal.EvidencePath
                    |> Option.iter (fun path -> TestContext.Progress.WriteLine($"MCA Repair terminal evidence: {path}"))

                    TestContext.Progress.Flush()

                    Assert.That(
                        terminal.Summary.Outcome,
                        Is.EqualTo("Passed"),
                        terminal.FallbackDiagnostic
                        |> Option.defaultValue "Repair preflight terminated before runtime readiness."
                    )

                    failwith "Repair preflight terminal assertion did not stop the fixture."

            use writer = ready.Writer
            let evidenceDirectory = ready.EvidenceDirectory
            let assertions = ResizeArray<MeasurementAssertion>()
            let failures = ResizeArray<string>()
            let mutable host: TestHostState option = None

            /// Writes one Repair assertion to both the derived outcome set and retained evidence.
            let recordAssertion assertionId passed detail =
                let assertion = MeasurementAssertion.Create(runId, RepairRuntime.ScenarioId, assertionId, passed, detail)
                assertions.Add assertion

                try
                    writer.Append assertion
                with
                | ex -> failures.Add($"evidence assertion append ({assertionId}): {ex}")

            try
                let bootstrapUserId = Guid.NewGuid().ToString("D")
                let! state = ManifestContributionGroupedRuntime.acquireAsync bootstrapUserId
                host <- Some state
                ManifestContributionGroupedRuntime.selectBootstrapUser state bootstrapUserId
                let! drainedBeforeScenario = AspireTestHost.drainServiceBusAsync state

                if drainedBeforeScenario <> 0 then
                    invalidOp $"The isolated Repair process began with {drainedBeforeScenario} unclassified test-subscription deliveries."

                let ownerId = Guid.NewGuid()
                let organizationId = Guid.NewGuid()
                let repositoryId = Guid.NewGuid()
                ManifestContributionGroupedRuntime.registerRepository RepairRuntime.ScenarioId repositoryId
                do! BaselineRuntime.createOwnerAsync state ownerId
                do! BaselineRuntime.createOrganizationAsync state ownerId organizationId
                let! defaultBranchId, defaultReferenceId = BaselineRuntime.createRepositoryAsync state ownerId organizationId repositoryId
                let! defaultBranch = BaselineRuntime.getBranchAsync state ownerId organizationId repositoryId defaultBranchId

                if defaultBranch.LatestReference.ReferenceId
                   <> defaultReferenceId then
                    invalidOp "The Repair repository default Reference inventory did not match its persisted branch."

                let defaultMessageId = $"Reference/{defaultReferenceId}/Created"
                let! defaultObserved = BaselineRuntime.observeReferenceEnvelopesAsync state [| defaultMessageId |] "repair repository default"

                let defaultObservedIds =
                    defaultObserved
                    |> Array.map (fun envelope -> envelope.MessageId)

                let! initialMetrics = BaselineRuntime.waitForCompletedSettlementSamplesAsync state
                let! blockAddress, manifest, bytes = BaselineRuntime.createManifestAssetAsync state ownerId organizationId repositoryId 761
                let root = BaselineRuntime.createRoot ownerId organizationId repositoryId 761 manifest bytes
                do! BaselineRuntime.saveRootAsync state ownerId organizationId repositoryId root
                let rebaseReferenceId = Guid.NewGuid()
                let explicitReferenceId = Guid.NewGuid()
                let! branch = BaselineRuntime.createBranchAsync state ownerId organizationId repositoryId defaultBranch 761 rebaseReferenceId

                let asset =
                    {
                        BlockAddress = blockAddress
                        Manifest = manifest
                        Root = root
                        Branch = branch
                        RebaseReferenceId = rebaseReferenceId
                        SaveReferenceId = explicitReferenceId
                    }

                let rebaseMessageId = $"Reference/{rebaseReferenceId}/Created"
                let! rebaseObserved = BaselineRuntime.observeReferenceEnvelopesAsync state [| rebaseMessageId |] "repair branch Rebase"

                let rebaseObservedIds =
                    rebaseObserved
                    |> Array.map (fun envelope -> envelope.MessageId)

                let! rebaseMessageDelta, rebaseDurationDelta, explicitBaseline = BaselineRuntime.waitForCompletedSettlementDeltaAsync state 1L initialMetrics

                let! explicitReference = BaselineRuntime.saveReferenceAsync state ownerId organizationId repositoryId asset
                let explicitMessageId = $"Reference/{explicitReferenceId}/Created"
                let! explicitObserved = BaselineRuntime.observeReferenceEnvelopesAsync state [| explicitMessageId |] "repair explicit Reference"

                let explicitObservedIds =
                    explicitObserved
                    |> Array.map (fun envelope -> envelope.MessageId)

                let originalHeaderCorrelationId = explicitObserved[0].CorrelationId
                let originalBodyCorrelationId = RepairRuntime.referenceCreatedBodyCorrelationId explicitObserved[0]

                let! durable = BaselineRuntime.waitForDurableStatusAsync state repositoryId [| asset |]

                let! explicitMessageDelta, explicitDurationDelta, _ = BaselineRuntime.waitForCompletedSettlementDeltaAsync state 1L explicitBaseline

                let setupIdentityErrors =
                    ProducerInventory.validate
                        [|
                            defaultMessageId
                            rebaseMessageId
                            explicitMessageId
                        |]
                        (Array.concat [| defaultObservedIds
                                         rebaseObservedIds
                                         explicitObservedIds |])

                let seedCompleted =
                    rebaseMessageDelta = 1L
                    && rebaseDurationDelta = 1L
                    && explicitMessageDelta = 1L
                    && explicitDurationDelta = 1L
                    && explicitReference.ReferenceId = explicitReferenceId
                    && explicitReference.DirectoryId = root.DirectoryVersionId
                    && durable.ReferenceRoots
                    && durable.ManifestRelationships
                    && durable.LogicalCounts
                    && durable.WorkflowCounts
                    && durable.PhysicalActiveCounts
                    && setupIdentityErrors.Length = 0

                let setupInventoryDetail = String.Join("; ", setupIdentityErrors)

                recordAssertion
                    "repair.seed-deliveries-completed"
                    seedCompleted
                    $"rebaseMessages={rebaseMessageDelta}; rebaseDurations={rebaseDurationDelta}; explicitMessages={explicitMessageDelta}; explicitDurations={explicitDurationDelta}; durable={durable.Detail}; inventory={setupInventoryDetail}"

                let relationship =
                    ExactRelationship.ReferenceRoot
                        { RepositoryId = repositoryId; RootDirectoryVersionId = root.DirectoryVersionId; ReferenceId = explicitReferenceId }

                let manifestRelationship =
                    ExactRelationship.DirectoryVersionManifest
                        {
                            RepositoryId = repositoryId
                            StoragePoolId = manifest.StoragePoolId
                            ManifestAddress = manifest.ManifestAddress
                            DirectoryVersionId = root.DirectoryVersionId
                        }

                let relationshipKey =
                    ExactRelationshipKey.create relationship
                    |> Result.defaultWith invalidOp

                let relationshipIdentity = $"{relationshipKey.PartitionKey}|{relationshipKey.ItemId}"
                let! stableBefore = RepairRuntime.readStableStateAsync state repositoryId asset
                do! RepairRuntime.deleteReferenceRootAsync state relationship
                let! absentAfterDelete = RepairRuntime.waitForRelationshipAsync state relationship false
                recordAssertion "repair.corruption-applied" (not absentAfterDelete) $"identity={relationshipIdentity}; present={absentAfterDelete}"
                let! diagnosis, diagnosisJson = RepairRuntime.diagnoseReferenceAsync state explicitReferenceId

                let diagnosisPlanKinds, diagnosisPlanIdentities =
                    match ProductionRepair.buildPlan diagnosis with
                    | Ok plan ->
                        (plan
                         |> Array.map (fun mutation -> mutation.Action.Kind)),
                        (plan
                         |> Array.map (fun mutation -> mutation.Action.Identity))
                    | Error error -> [| $"Invalid:{error}" |], Array.empty

                let diagnosisEvidence =
                    {
                        OutcomeIsIncompleteRetain = diagnosis.Outcome = DiagnosisOutcome.IncompleteRetain
                        ExpectedActionIdentity = relationshipIdentity
                        MissingRelationships = diagnosis.MissingRelationships
                        StaleRelationships = diagnosis.StaleRelationships
                        RepairTargets = diagnosis.RepairTargets
                        ProductionPlanActionKinds = diagnosisPlanKinds
                        ProductionPlanActionIdentities = diagnosisPlanIdentities
                        UnknownFields = diagnosis.UnknownFields
                        EvidenceGaps = diagnosis.EvidenceGaps
                    }

                let diagnosisErrors = RepairEvidence.validateDiagnosis diagnosisEvidence
                let diagnosisPassed = diagnosisErrors.Length = 0

                let missingDetail = RepairRuntime.joinRetainedValues "," diagnosis.MissingRelationships
                let targetDetail = RepairRuntime.joinRetainedValues "," diagnosis.RepairTargets
                let planKindDetail = RepairRuntime.joinRetainedValues "," diagnosisPlanKinds
                let planIdentityDetail = RepairRuntime.joinRetainedValues "," diagnosisPlanIdentities
                let unknownDetail = RepairRuntime.joinRetainedValues "," diagnosis.UnknownFields
                let gapDetail = RepairRuntime.joinRetainedValues " | " diagnosis.EvidenceGaps
                let diagnosisErrorDetail = String.Join("; ", diagnosisErrors)

                recordAssertion
                    "repair.diagnosis-one-supported-action"
                    diagnosisPassed
                    $"outcome={diagnosis.Outcome}; missing={missingDetail}; targets={targetDetail}; planKinds={planKindDetail}; planIdentities={planIdentityDetail}; unknown={unknownDetail}; gaps={gapDetail}; errors={diagnosisErrorDetail}"

                let dryRunCorrelationId = $"repair-dry-{Guid.NewGuid():N}"

                let! dryRun = RepairRuntime.repairAsync state dryRunCorrelationId diagnosisJson diagnosis.ReportSha256 false

                let! rootAfterDryRun = BaselineRuntime.exactRelationshipExistsAsync state relationship
                let! manifestAfterDryRun = BaselineRuntime.exactRelationshipExistsAsync state manifestRelationship
                let! stableAfterDryRun = RepairRuntime.readStableStateAsync state repositoryId asset

                let dryRunEvidence =
                    {
                        Execute = dryRun.Execute
                        Outcome = string dryRun.Outcome
                        ExpectedActionIdentity = relationshipIdentity
                        ProposedActionKinds =
                            dryRun.ProposedActions
                            |> Array.map (fun action -> action.Kind)
                        ProposedActionIdentities =
                            dryRun.ProposedActions
                            |> Array.map (fun action -> action.Identity)
                        AppliedActionKinds =
                            dryRun.AppliedActions
                            |> Array.map (fun action -> action.Kind)
                        ReferenceRootPresent = rootAfterDryRun
                    }

                let dryRunErrors = RepairEvidence.validateDryRun dryRunEvidence

                let dryRunDetail = String.Join("; ", dryRunErrors)

                recordAssertion
                    "repair.dry-run-no-mutation"
                    (dryRunErrors.Length = 0
                     && manifestAfterDryRun
                     && stableAfterDryRun = stableBefore)
                    $"outcome={dryRun.Outcome}; errors={dryRunDetail}; manifestPresent={manifestAfterDryRun}; stable={stableAfterDryRun = stableBefore}"

                let! executeBaseline = BaselineRuntime.scrapeMetricsAsync state
                let executeCorrelationId = $"repair-execute-{Guid.NewGuid():N}"

                let! execute = RepairRuntime.repairAsync state executeCorrelationId diagnosisJson diagnosis.ReportSha256 true

                let! republicationObserved = BaselineRuntime.observeReferenceEnvelopesAsync state [| explicitMessageId |] "repair republication"

                let republicationObservedIds =
                    republicationObserved
                    |> Array.map (fun envelope -> envelope.MessageId)

                let republicationObservedCorrelationIds =
                    republicationObserved
                    |> Array.map (fun envelope -> envelope.CorrelationId)

                let republicationBodyCorrelationIds =
                    republicationObserved
                    |> Array.map RepairRuntime.referenceCreatedBodyCorrelationId

                let! referenceRootRestored = RepairRuntime.waitForRelationshipAsync state relationship true

                let! repairMessageDelta, repairDurationDelta, repairTerminal = BaselineRuntime.waitForCompletedSettlementDeltaAsync state 1L executeBaseline

                let! stableAfterExecute = RepairRuntime.readStableStateAsync state repositoryId asset
                let! manifestAfterExecute = BaselineRuntime.exactRelationshipExistsAsync state manifestRelationship

                let executeEvidence =
                    {
                        Execute = execute.Execute
                        Outcome = string execute.Outcome
                        ExpectedActionIdentity = relationshipIdentity
                        ProposedActionKinds =
                            execute.ProposedActions
                            |> Array.map (fun action -> action.Kind)
                        ProposedActionIdentities =
                            execute.ProposedActions
                            |> Array.map (fun action -> action.Identity)
                        AppliedActionKinds =
                            execute.AppliedActions
                            |> Array.map (fun action -> action.Kind)
                        AppliedActionIdentities =
                            execute.AppliedActions
                            |> Array.map (fun action -> action.Identity)
                        OriginalReferenceId = explicitReferenceId
                        RepairCorrelationId = executeCorrelationId
                        OriginalHeaderCorrelationId = originalHeaderCorrelationId
                        OriginalBodyCorrelationId = originalBodyCorrelationId
                        ExpectedMessageId = explicitMessageId
                        ObservedMessageIds = republicationObservedIds
                        RepublishedHeaderCorrelationIds = republicationObservedCorrelationIds
                        RepublishedBodyCorrelationIds = republicationBodyCorrelationIds
                        MessageDelta = repairMessageDelta
                        DurationDelta = repairDurationDelta
                        ReferenceRootRestored = referenceRootRestored
                    }

                let executeErrors = RepairEvidence.validateExecute executeEvidence

                let proposedActionMatches =
                    execute.ProposedActions.Length = 1
                    && execute.ProposedActions[0].Kind = "RepublishReferenceCreated"
                    && execute.ProposedActions[0].Identity = relationshipIdentity

                let appliedActionMatches =
                    execute.AppliedActions.Length = 1
                    && execute.AppliedActions[0].Kind = "RepublishReferenceCreated"
                    && execute.AppliedActions[0].Identity = relationshipIdentity

                let executeActionPassed =
                    execute.Execute
                    && proposedActionMatches
                    && appliedActionMatches
                    && not (String.Equals(executeCorrelationId, string explicitReferenceId, StringComparison.OrdinalIgnoreCase))
                    && executeErrors.Length = 0

                let executeErrorDetail = String.Join("; ", executeErrors)

                recordAssertion "repair.execute-one-action" executeActionPassed $"outcome={execute.Outcome}; errors={executeErrorDetail}"

                let republicationMessageDetail = String.Join(",", republicationObservedIds)
                let republicationCorrelationDetail = String.Join(",", republicationObservedCorrelationIds)
                let republicationBodyCorrelationDetail = String.Join(",", republicationBodyCorrelationIds)

                let republicationDetail =
                    $"messages={republicationMessageDetail}; originalHeaderCorrelation={originalHeaderCorrelationId}; originalBodyCorrelation={originalBodyCorrelationId}; republishedHeaderCorrelations={republicationCorrelationDetail}; republishedBodyCorrelations={republicationBodyCorrelationDetail}"

                recordAssertion
                    "repair.republication-message-delta"
                    (repairMessageDelta = 1L
                     && republicationObservedIds = [| explicitMessageId |]
                     && originalHeaderCorrelationId = originalBodyCorrelationId
                     && republicationObservedCorrelationIds = [| originalBodyCorrelationId |]
                     && republicationBodyCorrelationIds = [| originalBodyCorrelationId |])
                    $"delta={repairMessageDelta}; observed={republicationDetail}"

                recordAssertion "repair.republication-duration-delta" (repairDurationDelta = 1L) $"delta={repairDurationDelta}"

                recordAssertion
                    "repair.reference-root-restored"
                    (referenceRootRestored && manifestAfterExecute)
                    $"identity={relationshipIdentity}; present={referenceRootRestored}; manifestPresent={manifestAfterExecute}"

                recordAssertion "repair.logical-state-unchanged" (stableAfterExecute.Logical = stableBefore.Logical) "exact logical counter snapshot compared"
                recordAssertion "repair.workflow-state-unchanged" (stableAfterExecute.Workflow = stableBefore.Workflow) "exact workflow snapshot compared"

                recordAssertion
                    "repair.physical-state-unchanged"
                    (stableAfterExecute.Physical = stableBefore.Physical)
                    "exact physical metadata snapshot compared"

                let labels = Dictionary<string, string>()
                labels["stage"] <- "settle"
                labels["outcome"] <- "completed"

                writer.Append(
                    MeasurementSample.Create(
                        runId,
                        RepairRuntime.ScenarioId,
                        "repair-republication-messages",
                        "grace_manifest_contribution_messages_total.delta",
                        repairMessageDelta,
                        labels
                    )
                )

                BaselineRuntime.recordMetricSnapshot writer runId RepairRuntime.ScenarioId "stimulus" "baseline" executeBaseline
                BaselineRuntime.recordMetricSnapshot writer runId RepairRuntime.ScenarioId "stimulus" "terminal" repairTerminal

                writer.Append(
                    MeasurementSample.Create(
                        runId,
                        RepairRuntime.ScenarioId,
                        "repair-republication-durations",
                        "grace_manifest_contribution_processing_duration_milliseconds_count.delta",
                        repairDurationDelta,
                        labels
                    )
                )
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
                        |> Seq.exists (fun assertion -> assertion.AssertionId = "repair.evidence-integrity")
                    )
            then
                try
                    let valid = RepairRuntime.verifyEvidenceIntegrity writer
                    recordAssertion "repair.evidence-integrity" valid $"path={writer.Path}"
                with
                | ex -> recordAssertion "repair.evidence-integrity" false ex.Message

            RepairEvidence.requiredAssertionIds
            |> Array.iter (fun assertionId ->
                if
                    not
                        (
                            assertions
                            |> Seq.exists (fun assertion -> assertion.AssertionId = assertionId)
                        )
                then
                    recordAssertion assertionId false "The runtime failed before this assertion could be evaluated.")

            let mutable summary =
                ScenarioSummary.derive runId RepairRuntime.ScenarioId RepairEvidence.requiredAssertionIds (assertions.ToArray()) (failures.ToArray()) false

            let mutable fallbackTerminalDiagnostic: string option = None

            try
                writer.Append summary
            with
            | ex ->
                failures.Add($"terminal summary append: {ex}")

                summary <-
                    ScenarioSummary.derive runId RepairRuntime.ScenarioId RepairEvidence.requiredAssertionIds (assertions.ToArray()) (failures.ToArray()) false

                try
                    writer.Append summary
                with
                | retryEx ->
                    fallbackTerminalDiagnostic <-
                        Some(MeasurementPreflight.fallbackDiagnostic summary $"Primary terminal summary append failed twice: {ex}; {retryEx}")

            fallbackTerminalDiagnostic
            |> Option.iter (fun diagnostic -> TestContext.Progress.WriteLine diagnostic)

            TestContext.Progress.WriteLine($"MCA Repair evidence directory: {evidenceDirectory}")
            TestContext.Progress.Flush()

            Assert.That(
                summary.Outcome,
                Is.EqualTo("Passed"),
                $"Evidence: {evidenceDirectory}{Environment.NewLine}{String.Join(Environment.NewLine, failures)}"
            )
        }
