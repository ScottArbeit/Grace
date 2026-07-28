namespace Grace.Server

open Giraffe
open Grace.Actors
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.ApplicationContext
open Grace.Server.ManifestContributionDiagnosis
open Grace.Server.Services
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.ManifestContributionAccounting
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.RepositoryContentCounter
open Microsoft.AspNetCore.Http
open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Provides dry-run-first, bounded convergence for one signed manifest contribution diagnosis.
module ManifestContributionRepair =

    /// Carries one internal repair request without adding a public Grace contract.
    [<AllowNullLiteral>]
    type RepairManifestContributionParameters() =
        member val ReportJson = String.Empty with get, set
        member val ExpectedReportSha256 = String.Empty with get, set
        member val Execute = false with get, set

    /// Names the retain-safe terminal outcomes consumed by the operator script.
    type RepairOutcome =
        | VerifiedComplete
        | IncompleteRetain
        | FailedRetain

    /// Preserves the typed target used only inside the repair process.
    type internal RepairMutationTarget =
        | Relationship of ExactRelationship
        | Counter of CounterTuple * rebuiltCount: int64

    /// Describes one stable serialized action without leaking actor or F# union internals.
    type RepairAction = { Kind: string; Identity: string }

    /// Couples a wire-safe action description to its internal typed mutation target.
    type internal RepairMutation = { Action: RepairAction; Target: RepairMutationTarget }

    /// Reports the exact initial plan, applied prefix, and retain-safe terminal result.
    type ManifestContributionRepairReport =
        {
            SchemaVersion: string
            GeneratedAt: string
            DiagnosisReportSha256: string
            Execute: bool
            ProposedActions: RepairAction array
            AppliedActions: RepairAction array
            Outcome: RepairOutcome
            Message: string
        }

    /// Supplies bounded current diagnosis and one action callback to the repair convergence loop.
    type internal RepairDependencies =
        {
            DiagnoseCurrent: DiagnosisSelector -> ExactRelationshipReadBound -> CancellationToken -> Task<ManifestContributionDiagnosisReport>
            ApplyAction: ManifestContributionDiagnosisReport -> RepairMutation -> CancellationToken -> Task
        }

    /// Supplies immediate actor rereads for one destructive exact-relationship removal.
    type internal StaleRemovalDependencies =
        {
            GetDirectoryVersion: DirectoryVersionId -> Task<DirectoryVersionDto>
            GetCounter: CounterTuple -> Task<RepositoryContentCounterDto>
            GetWorkflow: CounterTuple -> Task<ManifestContributionWorkflowDto>
            EnsureAbsent: ExactRelationship -> CancellationToken -> Task<ExactRelationshipWriteOutcome>
        }

    /// Supplies current actor reads and existing counter/workflow operations for one bounded reconciliation step.
    type internal CounterReconciliationDependencies =
        {
            GetCounter: CounterTuple -> Task<RepositoryContentCounterDto>
            GetWorkflow: CounterTuple -> Task<ManifestContributionWorkflowDto>
            HandleCounter: RepositoryContentCounterCommand -> Task<RepositoryContentCounterDecision>
            StartWorkflow: ManifestContributionWorkflowOperationId
                -> CounterTuple
                -> ManifestContributionDirection
                -> ManifestContributionWorkflowRange array
                -> int64
                -> Task
        }

    /// Converts the signed diagnosis target back into its bounded selector.
    let selectorFromTarget (target: DiagnosisTarget) =
        let requiredGuid name (value: string) =
            match Guid.TryParse value with
            | true, parsed when parsed <> Guid.Empty -> Ok parsed
            | _ -> Error $"{name} must contain a non-empty GUID."

        match target with
        | DiagnosisTarget.Reference value ->
            requiredGuid "Target.Reference" value
            |> Result.map (fun parsed -> DiagnosisSelector.ReferenceId(parsed, None))
        | DiagnosisTarget.DirectoryVersion value ->
            requiredGuid "Target.DirectoryVersion" value
            |> Result.map (fun parsed -> DiagnosisSelector.DirectoryVersionId(parsed, None))
        | DiagnosisTarget.Counter (repositoryId, storagePoolId, manifestAddress) ->
            match requiredGuid "Target.Counter.RepositoryId" repositoryId with
            | Error error -> Error error
            | Ok parsed when String.IsNullOrWhiteSpace storagePoolId -> Error "Target.Counter.StoragePoolId must not be empty."
            | Ok _ when String.IsNullOrWhiteSpace manifestAddress -> Error "Target.Counter.ManifestAddress must not be empty."
            | Ok parsed -> Ok(DiagnosisSelector.CounterTuple { RepositoryId = parsed; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress })
        | DiagnosisTarget.Operation value when String.IsNullOrWhiteSpace value -> Error "Target.Operation must not be empty."
        | DiagnosisTarget.Operation value -> Ok(DiagnosisSelector.OperationId value)

    /// Validates the schema, embedded digest, operator-expected digest, selector, and relationship bound.
    let validateRequest reportJson expectedReportSha256 execute =
        if String.IsNullOrWhiteSpace reportJson then
            Error "ReportJson is required."
        elif String.IsNullOrWhiteSpace expectedReportSha256
             || expectedReportSha256.Length <> 64
             || expectedReportSha256
                |> Seq.exists (fun character -> not (Uri.IsHexDigit character)) then
            Error "ExpectedReportSha256 must contain exactly 64 hexadecimal characters."
        else
            try
                let report = JsonSerializer.Deserialize<ManifestContributionDiagnosisReport>(reportJson, Grace.Shared.Constants.JsonSerializerOptions)

                if isNull (box report) then
                    Error "ReportJson must contain a diagnosis report."
                elif not (String.Equals(report.SchemaVersion, "grace.manifest-contribution-diagnosis.v1", StringComparison.Ordinal)) then
                    Error "The diagnosis report schema is not supported by this repair route."
                elif not (String.Equals(expectedReportSha256, report.ReportSha256, StringComparison.OrdinalIgnoreCase)) then
                    Error "ExpectedReportSha256 does not match the signed diagnosis report."
                elif not (verifySerializedReportSha256 reportJson) then
                    Error "The diagnosis report SHA-256 does not match its complete content."
                else
                    match selectorFromTarget report.Target with
                    | Error error -> Error error
                    | Ok selector ->
                        match ExactRelationshipReadBound.create report.MaxRelationships with
                        | Error error -> Error error
                        | Ok bound -> Ok(report, selector, bound, execute)
            with
            | :? JsonException -> Error "ReportJson must contain valid diagnosis JSON."

    /// Parses the provider-neutral identity emitted by diagnosis back into one exact relationship.
    let private parseRelationshipIdentity identity =
        if String.IsNullOrWhiteSpace identity then
            Error "A repair relationship identity must not be empty."
        else
            let separator = identity.IndexOf('|')

            if separator <= 0
               || separator = identity.Length - 1
               || identity.IndexOf('|', separator + 1) >= 0 then
                Error $"Repair relationship identity '{identity}' is malformed."
            else
                ExactRelationshipKey.tryParse { PartitionKey = identity[.. separator - 1]; ItemId = identity[separator + 1 ..] }

    /// Creates the counter tuple named by complete count evidence.
    let private counterFromEvidence (evidence: ManifestCountEvidence) =
        match Guid.TryParse evidence.RepositoryId with
        | true, repositoryId when
            repositoryId <> Guid.Empty
            && not (String.IsNullOrWhiteSpace evidence.StoragePoolId)
            && not (String.IsNullOrWhiteSpace evidence.ManifestAddress)
            ->
            Ok { RepositoryId = repositoryId; StoragePoolId = evidence.StoragePoolId; ManifestAddress = evidence.ManifestAddress }
        | _ -> Error "Complete count evidence contains an invalid counter target."

    /// Derives the only allowed deterministic action plan from structured diagnosis evidence.
    let internal buildPlan (report: ManifestContributionDiagnosisReport) =
        let actions = ResizeArray<RepairMutation>()
        let errors = ResizeArray<string>()

        let addRelationship kind identity =
            match parseRelationshipIdentity identity with
            | Error error -> errors.Add error
            | Ok relationship ->
                actions.Add { Action = { Kind = kind relationship; Identity = identity }; Target = RepairMutationTarget.Relationship relationship }

        report.MissingRelationships
        |> Array.iter (
            addRelationship (function
                | ExactRelationship.DirectoryVersionManifest _ -> "ResendDeterministicEvent"
                | _ -> "GetOrAddExactRelationship")
        )

        report.StaleRelationships
        |> Array.iter (fun identity ->
            match parseRelationshipIdentity identity with
            | Ok (ExactRelationship.DirectoryVersionManifest _ as relationship) ->
                actions.Add { Action = { Kind = "RemoveStaleExactRelationship"; Identity = identity }; Target = RepairMutationTarget.Relationship relationship }
            | Ok _ -> errors.Add $"Only an exact DirectoryVersion-manifest relationship can be removed as stale: '{identity}'."
            | Error error -> errors.Add error)

        report.CountEvidence
        |> Array.iter (fun evidence ->
            match evidence.StoredCount, evidence.RebuiltCount with
            | Some stored, Some rebuilt when
                stored <> rebuilt
                && String.Equals(evidence.Completeness, "Complete", StringComparison.Ordinal)
                ->
                match counterFromEvidence evidence with
                | Error error -> errors.Add error
                | Ok counter ->
                    actions.Add
                        {
                            Action = { Kind = "ReconcileCounter"; Identity = $"{evidence.RepositoryId}|{evidence.StoragePoolId}|{evidence.ManifestAddress}" }
                            Target = RepairMutationTarget.Counter(counter, rebuilt)
                        }
            | Some stored, Some rebuilt when stored <> rebuilt -> errors.Add "Counter reconciliation requires complete rebuilt count evidence."
            | _ -> ())

        let allowedTargets =
            report.RepairTargets
            |> Array.filter (fun target ->
                target.StartsWith("GetOrAddExactRelationship:", StringComparison.Ordinal)
                || target.StartsWith("RemoveStaleExactRelationship:", StringComparison.Ordinal)
                || target.StartsWith("ReconcileCounter:", StringComparison.Ordinal))

        let actionTarget action =
            match action.Action.Kind with
            | "ResendDeterministicEvent"
            | "GetOrAddExactRelationship" -> $"GetOrAddExactRelationship:{action.Action.Identity}"
            | "RemoveStaleExactRelationship" -> $"RemoveStaleExactRelationship:{action.Action.Identity}"
            | "ReconcileCounter" -> $"ReconcileCounter:{action.Action.Identity}"
            | _ -> String.Empty

        let derivedTargets =
            actions
            |> Seq.map actionTarget
            |> Seq.sort
            |> Seq.toArray

        if derivedTargets <> (allowedTargets |> Array.sort) then
            errors.Add "Structured diagnosis differences do not match the report's concrete repair targets."

        if errors.Count > 0 then
            Error(String.Join(" ", errors))
        else
            let priority action =
                match action.Action.Kind with
                | "ResendDeterministicEvent" -> 0
                | "GetOrAddExactRelationship" -> 1
                | "RemoveStaleExactRelationship" -> 2
                | "ReconcileCounter" -> 3
                | _ -> 4

            actions
            |> Seq.sortBy (fun action -> priority action, action.Action.Identity)
            |> Seq.toArray
            |> Ok

    /// Returns the source actor facts that must remain byte-for-byte stable while the planned projection converges.
    let private sourceFacts (report: ManifestContributionDiagnosisReport) =
        report.ActorFacts
        |> Array.filter (fun fact ->
            String.Equals(fact.ActorType, "Reference", StringComparison.Ordinal)
            || String.Equals(fact.ActorType, "DirectoryVersion", StringComparison.Ordinal))

    /// Finds the exact actor snapshot recorded by the immediately preceding bounded diagnosis.
    let private actorFact actorType actorId (report: ManifestContributionDiagnosisReport) =
        report.ActorFacts
        |> Array.tryFind (fun fact ->
            String.Equals(fact.ActorType, actorType, StringComparison.Ordinal)
            && String.Equals(fact.ActorId, actorId, StringComparison.Ordinal))

    /// Removes one stale exact relationship only while source, counter, and workflow snapshots still match the fresh diagnosis.
    let internal removeStaleRelationshipWith
        (dependencies: StaleRemovalDependencies)
        (report: ManifestContributionDiagnosisReport)
        identity
        (relationship: DirectoryVersionManifestRelationship)
        (cancellationToken: CancellationToken)
        =
        task {
            let counterTuple =
                { RepositoryId = relationship.RepositoryId; StoragePoolId = relationship.StoragePoolId; ManifestAddress = relationship.ManifestAddress }

            let counterId = RepositoryContentCounter.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress

            let workflowId = ManifestContributionWorkflow.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress

            let expectedTarget = $"RemoveStaleExactRelationship:{identity}"

            if
                not
                    (
                        report.StaleRelationships
                        |> Array.contains identity
                    )
                || not (
                    report.RepairTargets
                    |> Array.contains expectedTarget
                )
            then
                invalidOp "Fresh diagnosis did not authorize this exact stale relationship removal."

            let countEvidence =
                report.CountEvidence
                |> Array.tryFind (fun evidence ->
                    String.Equals(evidence.RepositoryId, $"{counterTuple.RepositoryId:D}", StringComparison.Ordinal)
                    && String.Equals(evidence.StoragePoolId, counterTuple.StoragePoolId, StringComparison.Ordinal)
                    && String.Equals(evidence.ManifestAddress, counterTuple.ManifestAddress, StringComparison.Ordinal))
                |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not contain counter evidence for the stale relationship.")

            if not (String.Equals(countEvidence.Completeness, "Complete", StringComparison.Ordinal))
               || countEvidence.StoredCount.IsNone
               || countEvidence.RebuiltCount.IsNone then
                invalidOp "Stale relationship removal requires complete current counter reconstruction."

            let! source = dependencies.GetDirectoryVersion relationship.DirectoryVersionId
            let! counter = dependencies.GetCounter counterTuple
            let! workflow = dependencies.GetWorkflow counterTuple

            let sourceFact =
                actorFact "DirectoryVersion" $"{relationship.DirectoryVersionId:D}" report
                |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not retain the DirectoryVersion snapshot.")

            let counterFact =
                actorFact "RepositoryContentCounter" counterId report
                |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not retain the repository counter snapshot.")

            let workflowFact =
                actorFact "ManifestContributionWorkflow" workflowId report
                |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not retain the workflow snapshot.")

            if
                source.DirectoryVersion.DirectoryVersionId
                <> relationship.DirectoryVersionId
                || source.DirectoryVersion.RepositoryId
                   <> relationship.RepositoryId
                || not (String.Equals(sourceFact.SnapshotJson, actorSnapshotJson source, StringComparison.Ordinal))
            then
                invalidOp "DirectoryVersion source changed before stale relationship removal."

            match directManifests source sourceFact.ActorId with
            | Error error -> invalidOp $"DirectoryVersion manifests became unreadable before stale relationship removal: {error.Error}"
            | Ok manifests when
                manifests
                |> Array.exists (fun manifest ->
                    String.Equals(manifest.StoragePoolId, relationship.StoragePoolId, StringComparison.Ordinal)
                    && String.Equals(manifest.ManifestAddress, relationship.ManifestAddress, StringComparison.Ordinal))
                ->
                invalidOp "DirectoryVersion source names the manifest again; stale relationship removal was retained."
            | Ok _ -> ()

            let counterMatches =
                counter.RepositoryId = counterTuple.RepositoryId
                && counter.StoragePoolId = counterTuple.StoragePoolId
                && counter.ManifestAddress = counterTuple.ManifestAddress
                && counterFact.Revision = Some counter.Revision
                && String.Equals(counterFact.SnapshotJson, actorSnapshotJson counter, StringComparison.Ordinal)
                && countEvidence.StoredCount = Some counter.Count

            let workflowMatches =
                workflow.RepositoryId = counterTuple.RepositoryId
                && workflow.StoragePoolId = counterTuple.StoragePoolId
                && workflow.ManifestAddress = counterTuple.ManifestAddress
                && workflowFact.Revision = Some workflow.Revision
                && String.Equals(workflowFact.SnapshotJson, workflowSnapshotJson workflow, StringComparison.Ordinal)
                && workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
                && workflow.FailedRanges.Length = 0
                && workflow.CompletedRanges.Length = workflow.Ranges.Length
                && workflow.CounterRevision <= counter.Revision

            if not counterMatches || not workflowMatches then
                invalidOp "Counter or workflow evidence changed before stale relationship removal."

            let! _ = dependencies.EnsureAbsent (ExactRelationship.DirectoryVersionManifest relationship) cancellationToken

            return ()
        }

    /// Builds the deterministic command identity that lets a partial reconciliation resume from the current counter revision.
    let internal counterRepairOperationId (counterTuple: CounterTuple) rebuiltCount currentRevision direction =
        RepositoryContentCounterOperationId
            $"manifest-repair:{counterTuple.RepositoryId:N}:{counterTuple.StoragePoolId}:{counterTuple.ManifestAddress}:target:{rebuiltCount}:revision:{currentRevision}:{direction}"

    /// Recognizes only the exact current actor evidence left by a repair counter transition whose workflow still needs replay.
    let private tryResumableCounterMutation (report: ManifestContributionDiagnosisReport) (proposed: RepairMutation array) =
        proposed
        |> Array.tryPick (fun mutation ->
            match mutation.Target with
            | RepairMutationTarget.Counter (counterTuple, rebuiltCount) ->
                let counterId = RepositoryContentCounter.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress

                let workflowId = ManifestContributionWorkflow.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress

                match actorFact "RepositoryContentCounter" counterId report, actorFact "ManifestContributionWorkflow" workflowId report with
                | Some counterFact, Some workflowFact ->
                    try
                        let counter =
                            JsonSerializer.Deserialize<RepositoryContentCounterDto>(counterFact.SnapshotJson, Grace.Shared.Constants.JsonSerializerOptions)

                        match counter.LastCompletedChange with
                        | Some change ->
                            let directionName =
                                match change.Operation with
                                | RepositoryContentCounterChangeOperation.Added -> "add"
                                | RepositoryContentCounterChangeOperation.Removed -> "remove"

                            let expectedCounterOperation = counterRepairOperationId counterTuple rebuiltCount (change.Revision - 1L) directionName

                            let expectedWorkflowOperation = $"{expectedCounterOperation}:workflow"
                            use workflowDocument = JsonDocument.Parse(workflowFact.SnapshotJson)
                            let root = workflowDocument.RootElement
                            let startOperationId = root.GetProperty("StartOperationId").GetString()
                            let lifecycleState = root.GetProperty("LifecycleState").GetString()
                            let counterRevision = root.GetProperty("CounterRevision").GetInt64()

                            if counter.Count = rebuiltCount
                               && change.CurrentCount = rebuiltCount
                               && change.OperationId = expectedCounterOperation
                               && String.Equals(startOperationId, expectedWorkflowOperation, StringComparison.Ordinal)
                               && not (String.Equals(lifecycleState, "Completed", StringComparison.Ordinal))
                               && counterRevision = counter.Revision then
                                Some mutation
                            else
                                None
                        | None -> None
                    with
                    | :? JsonException -> None
                | _ -> None
            | _ -> None)

    /// Applies exactly one freshly validated counter command and its intrinsic zero-crossing StoragePool workflow.
    let internal reconcileCounterStepWith
        (dependencies: CounterReconciliationDependencies)
        (report: ManifestContributionDiagnosisReport)
        (counterTuple: CounterTuple)
        rebuiltCount
        =
        task {
            let counterId = RepositoryContentCounter.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress

            let workflowId = ManifestContributionWorkflow.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress

            let! counter = dependencies.GetCounter counterTuple
            let! workflow = dependencies.GetWorkflow counterTuple

            let counterFact =
                actorFact "RepositoryContentCounter" counterId report
                |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not retain the repository counter snapshot.")

            let workflowFact =
                actorFact "ManifestContributionWorkflow" workflowId report
                |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not retain the workflow snapshot.")

            let snapshotsMatch =
                counter.RepositoryId = counterTuple.RepositoryId
                && counter.StoragePoolId = counterTuple.StoragePoolId
                && counter.ManifestAddress = counterTuple.ManifestAddress
                && counterFact.Revision = Some counter.Revision
                && String.Equals(counterFact.SnapshotJson, actorSnapshotJson counter, StringComparison.Ordinal)
                && workflow.RepositoryId = counterTuple.RepositoryId
                && workflow.StoragePoolId = counterTuple.StoragePoolId
                && workflow.ManifestAddress = counterTuple.ManifestAddress
                && workflowFact.Revision = Some workflow.Revision
                && String.Equals(workflowFact.SnapshotJson, workflowSnapshotJson workflow, StringComparison.Ordinal)

            if not snapshotsMatch then
                invalidOp "Counter or workflow evidence changed before reconciliation."

            let resumableWorkflow =
                counter.LastCompletedChange
                |> Option.bind (fun change ->
                    let directionName, direction =
                        match change.Operation with
                        | RepositoryContentCounterChangeOperation.Added -> "add", ManifestContributionDirection.Increment
                        | RepositoryContentCounterChangeOperation.Removed -> "remove", ManifestContributionDirection.Decrement

                    let expectedCounterOperation = counterRepairOperationId counterTuple rebuiltCount (change.Revision - 1L) directionName

                    let expectedWorkflowOperation = ManifestContributionWorkflowOperationId $"{expectedCounterOperation}:workflow"

                    if counter.Count = rebuiltCount
                       && change.CurrentCount = rebuiltCount
                       && change.OperationId = expectedCounterOperation
                       && workflow.StartOperationId = Some expectedWorkflowOperation
                       && workflow.CounterRevision = counter.Revision
                       && workflow.LifecycleState
                          <> ManifestContributionWorkflowLifecycleState.Completed
                       && workflow.Ranges.Length > 0 then
                        Some(expectedWorkflowOperation, direction)
                    else
                        None)

            match resumableWorkflow with
            | Some (operationId, direction) -> do! dependencies.StartWorkflow operationId counterTuple direction workflow.Ranges counter.Revision
            | None ->
                let countEvidence =
                    report.CountEvidence
                    |> Array.tryFind (fun evidence ->
                        String.Equals(evidence.RepositoryId, $"{counterTuple.RepositoryId:D}", StringComparison.Ordinal)
                        && String.Equals(evidence.StoragePoolId, counterTuple.StoragePoolId, StringComparison.Ordinal)
                        && String.Equals(evidence.ManifestAddress, counterTuple.ManifestAddress, StringComparison.Ordinal))
                    |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not contain the requested counter evidence.")

                if not (String.Equals(countEvidence.Completeness, "Complete", StringComparison.Ordinal))
                   || countEvidence.StoredCount.IsNone
                   || countEvidence.RebuiltCount <> Some rebuiltCount then
                    invalidOp "Counter reconciliation requires a fresh complete reconstruction for the same target count."

                let counterMatches =
                    counter.RepositoryId = counterTuple.RepositoryId
                    && counter.StoragePoolId = counterTuple.StoragePoolId
                    && counter.ManifestAddress = counterTuple.ManifestAddress
                    && counterFact.Revision = Some counter.Revision
                    && String.Equals(counterFact.SnapshotJson, actorSnapshotJson counter, StringComparison.Ordinal)
                    && countEvidence.StoredCount = Some counter.Count

                let workflowMatches =
                    workflow.RepositoryId = counterTuple.RepositoryId
                    && workflow.StoragePoolId = counterTuple.StoragePoolId
                    && workflow.ManifestAddress = counterTuple.ManifestAddress
                    && workflowFact.Revision = Some workflow.Revision
                    && String.Equals(workflowFact.SnapshotJson, workflowSnapshotJson workflow, StringComparison.Ordinal)
                    && workflow.Direction = ManifestContributionDirection.Increment
                    && workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
                    && workflow.Ranges.Length > 0
                    && workflow.FailedRanges.Length = 0
                    && workflow.CompletedRanges.Length = workflow.Ranges.Length
                    && workflow.CounterRevision <= counter.Revision

                if not counterMatches || not workflowMatches then
                    invalidOp "Counter or workflow evidence changed before reconciliation."

                if counter.Count = rebuiltCount then
                    invalidOp "The repository manifest counter already matches the rebuilt count."

                let direction, command =
                    if counter.Count < rebuiltCount then
                        let operationId = counterRepairOperationId counterTuple rebuiltCount counter.Revision "add"

                        ManifestContributionDirection.Increment,
                        RepositoryContentCounterCommand.AddReference(
                            operationId,
                            counterTuple.RepositoryId,
                            counterTuple.StoragePoolId,
                            counterTuple.ManifestAddress
                        )
                    else
                        let operationId = counterRepairOperationId counterTuple rebuiltCount counter.Revision "remove"

                        ManifestContributionDirection.Decrement,
                        RepositoryContentCounterCommand.RemoveReference(
                            operationId,
                            counterTuple.RepositoryId,
                            counterTuple.StoragePoolId,
                            counterTuple.ManifestAddress
                        )

                let! decision = dependencies.HandleCounter command

                let workflowRevision =
                    decision.Intents
                    |> List.tryPick (function
                        | RepositoryContentCounterIntent.IncrementManifestReferenceCount (repositoryId, storagePoolId, manifestAddress, revision)
                        | RepositoryContentCounterIntent.DecrementManifestReferenceCount (repositoryId, storagePoolId, manifestAddress, revision) when
                            repositoryId = counterTuple.RepositoryId
                            && storagePoolId = counterTuple.StoragePoolId
                            && manifestAddress = counterTuple.ManifestAddress
                            ->
                            Some revision
                        | _ -> None)

                match workflowRevision with
                | None -> ()
                | Some revision ->
                    let commandOperationId =
                        match command with
                        | RepositoryContentCounterCommand.AddReference (operationId, _, _, _)
                        | RepositoryContentCounterCommand.RemoveReference (operationId, _, _, _) -> operationId

                    do!
                        dependencies.StartWorkflow
                            (ManifestContributionWorkflowOperationId $"{commandOperationId}:workflow")
                            counterTuple
                            direction
                            workflow.Ranges
                            revision
        }

    /// Requires the supplied report to describe exactly the current bounded evidence before execute may start.
    let private initialEvidenceMatches (expected: ManifestContributionDiagnosisReport) (current: ManifestContributionDiagnosisReport) =
        expected.Target = current.Target
        && expected.MaxRelationships = current.MaxRelationships
        && expected.RelationshipsRead = current.RelationshipsRead
        && expected.ActorFacts = current.ActorFacts
        && expected.ExpectedRelationships = current.ExpectedRelationships
        && expected.ObservedRelationships = current.ObservedRelationships
        && expected.MissingRelationships = current.MissingRelationships
        && expected.StaleRelationships = current.StaleRelationships
        && expected.CountEvidence = current.CountEvidence
        && expected.DeterministicIdentities = current.DeterministicIdentities
        && expected.RepairTargets = current.RepairTargets
        && expected.UnknownFields = current.UnknownFields
        && expected.EvidenceGaps = current.EvidenceGaps
        && expected.Outcome = current.Outcome

    /// Creates one terminal repair report without persisting a repair lifecycle.
    let private result generatedAt (report: ManifestContributionDiagnosisReport) execute proposed applied outcome message =
        {
            SchemaVersion = "grace.manifest-contribution-repair.v1"
            GeneratedAt = generatedAt
            DiagnosisReportSha256 = report.ReportSha256
            Execute = execute
            ProposedActions =
                proposed
                |> Array.map (fun mutation -> mutation.Action)
            AppliedActions =
                applied
                |> Array.map (fun mutation -> mutation.Action)
            Outcome = outcome
            Message = message
        }

    /// Revalidates bounded evidence before every mutation and verifies current state after the applied prefix.
    let internal repairWith
        (dependencies: RepairDependencies)
        generatedAt
        (correlationId: string)
        (cancellationToken: CancellationToken)
        (bound: ExactRelationshipReadBound)
        (report: ManifestContributionDiagnosisReport)
        execute
        =
        task {
            match selectorFromTarget report.Target, buildPlan report with
            | Error error, _
            | _, Error error -> return result generatedAt report execute Array.empty Array.empty RepairOutcome.FailedRetain error
            | Ok selector, Ok proposed ->
                try
                    let! initialCurrent = dependencies.DiagnoseCurrent selector bound cancellationToken

                    let resumableInitialCounter =
                        execute
                        && sourceFacts initialCurrent = sourceFacts report
                        && (tryResumableCounterMutation initialCurrent proposed)
                            .IsSome

                    if
                        not (initialEvidenceMatches report initialCurrent)
                        && not resumableInitialCounter
                    then
                        return
                            result
                                generatedAt
                                report
                                execute
                                proposed
                                Array.empty
                                RepairOutcome.IncompleteRetain
                                "Current source or projection evidence changed after the diagnosis report was created."
                    elif not execute then
                        let outcome =
                            if proposed.Length = 0
                               && initialCurrent.Outcome = DiagnosisOutcome.VerifiedComplete then
                                RepairOutcome.VerifiedComplete
                            else
                                RepairOutcome.IncompleteRetain

                        return
                            result
                                generatedAt
                                report
                                false
                                proposed
                                Array.empty
                                outcome
                                (if proposed.Length = 0 then
                                     "Dry run verified that no repair action is required."
                                 else
                                     "Dry run produced the bounded ordered repair plan and performed zero writes.")
                    else
                        let applied = ResizeArray<RepairMutation>()
                        let attemptedWorkflowResumes = HashSet<string * string>()

                        let proposedKeys =
                            proposed
                            |> Array.map (fun action -> action.Action.Kind, action.Action.Identity)
                            |> set

                        let mutable completed: ManifestContributionRepairReport option = None
                        let mutable previousPlan: (string * string) array option = None

                        while completed.IsNone do
                            cancellationToken.ThrowIfCancellationRequested()

                            let! current = dependencies.DiagnoseCurrent selector bound cancellationToken

                            if sourceFacts current <> sourceFacts report then
                                completed <-
                                    Some(
                                        result
                                            generatedAt
                                            report
                                            true
                                            proposed
                                            (applied.ToArray())
                                            RepairOutcome.IncompleteRetain
                                            "Current source evidence changed before a repair mutation."
                                    )
                            else
                                match tryResumableCounterMutation current proposed with
                                | Some recoveryAction ->
                                    let recoveryKey = recoveryAction.Action.Kind, recoveryAction.Action.Identity

                                    if not (attemptedWorkflowResumes.Add recoveryKey) then
                                        completed <-
                                            Some(
                                                result
                                                    generatedAt
                                                    report
                                                    true
                                                    proposed
                                                    (applied.ToArray())
                                                    RepairOutcome.FailedRetain
                                                    "The deterministic counter workflow replay returned without changing current evidence."
                                            )
                                    else
                                        try
                                            do! dependencies.ApplyAction current recoveryAction cancellationToken
                                            applied.Add recoveryAction
                                            previousPlan <- None
                                        with
                                        | ex ->
                                            completed <-
                                                Some(
                                                    result
                                                        generatedAt
                                                        report
                                                        true
                                                        proposed
                                                        (applied.ToArray())
                                                        RepairOutcome.FailedRetain
                                                        $"Repair dependency failed while resuming the deterministic counter workflow: {ex.Message}"
                                                )
                                | None ->
                                    match buildPlan current with
                                    | Error error ->
                                        completed <- Some(result generatedAt report true proposed (applied.ToArray()) RepairOutcome.IncompleteRetain error)
                                    | Ok currentPlan ->
                                        let currentKeys =
                                            currentPlan
                                            |> Array.map (fun action -> action.Action.Kind, action.Action.Identity)

                                        if currentKeys
                                           |> Array.exists (fun key -> not (proposedKeys.Contains key)) then
                                            completed <-
                                                Some(
                                                    result
                                                        generatedAt
                                                        report
                                                        true
                                                        proposed
                                                        (applied.ToArray())
                                                        RepairOutcome.IncompleteRetain
                                                        "Current projection evidence requires an action that was absent from the validated report."
                                                )
                                        elif currentPlan.Length = 0 then
                                            let outcome, message =
                                                if current.Outcome = DiagnosisOutcome.VerifiedComplete then
                                                    RepairOutcome.VerifiedComplete, "Current post-repair verification is complete."
                                                else
                                                    RepairOutcome.IncompleteRetain, "No allowed repair action remains, but current evidence is not complete."

                                            completed <- Some(result generatedAt report true proposed (applied.ToArray()) outcome message)
                                        elif previousPlan = Some currentKeys then
                                            completed <-
                                                Some(
                                                    result
                                                        generatedAt
                                                        report
                                                        true
                                                        proposed
                                                        (applied.ToArray())
                                                        RepairOutcome.FailedRetain
                                                        "The previous repair action returned without changing the bounded current plan."
                                                )
                                        else
                                            let action = currentPlan[0]
                                            previousPlan <- Some currentKeys

                                            try
                                                do! dependencies.ApplyAction current action cancellationToken
                                                applied.Add action
                                            with
                                            | ex ->
                                                completed <-
                                                    Some(
                                                        result
                                                            generatedAt
                                                            report
                                                            true
                                                            proposed
                                                            (applied.ToArray())
                                                            RepairOutcome.FailedRetain
                                                            $"Repair dependency failed after retaining current evidence: {ex.Message}"
                                                    )

                        return completed.Value
                with
                | :? OperationCanceledException as ex ->
                    return
                        result
                            generatedAt
                            report
                            execute
                            proposed
                            Array.empty
                            RepairOutcome.FailedRetain
                            $"Repair was cancelled and retained current evidence: {ex.Message}"
                | ex ->
                    return
                        result
                            generatedAt
                            report
                            execute
                            proposed
                            Array.empty
                            RepairOutcome.FailedRetain
                            $"Repair failed before convergence and retained current evidence: {ex.Message}"
        }

    /// Creates the production bounded read and mutation adapters for the internal repair route.
    let private productionDependencies (context: HttpContext) =
        let store = ManifestContributionAccounting.CosmosExactRelationshipStore(cosmosContainer) :> IExactRelationshipStore
        let correlationId = getCorrelationId context
        let metadata = createMetadata context

        let applyAction currentReport action cancellationToken =
            task {
                match action.Action.Kind, action.Target with
                | "ResendDeterministicEvent", RepairMutationTarget.Relationship (ExactRelationship.DirectoryVersionManifest relationship) ->
                    let actor = grainFactory.CreateActorProxyWithCorrelationId<IDirectoryVersionActor>(relationship.DirectoryVersionId, correlationId)

                    let! current = actor.Get correlationId

                    if current.DirectoryVersion.DirectoryVersionId
                       <> relationship.DirectoryVersionId
                       || current.DirectoryVersion.RepositoryId
                          <> relationship.RepositoryId then
                        invalidOp "The DirectoryVersion source changed before deterministic event resend."

                    let manifest =
                        match DirectoryVersion.getManifestReferencesForSaveBoundary current.DirectoryVersion correlationId with
                        | Error graceError -> invalidOp graceError.Error
                        | Ok references ->
                            references
                            |> Seq.map (fun reference -> reference.Manifest)
                            |> Seq.tryFind (fun candidate ->
                                String.Equals(candidate.StoragePoolId, relationship.StoragePoolId, StringComparison.Ordinal)
                                && String.Equals(candidate.ManifestAddress, relationship.ManifestAddress, StringComparison.Ordinal))
                            |> Option.defaultWith (fun () -> invalidOp "The direct manifest source changed before deterministic event resend.")

                    do! ManifestContributionAccounting.ensureDirectoryVersionManifest store relationship manifest metadata cancellationToken
                | "GetOrAddExactRelationship", RepairMutationTarget.Relationship relationship ->
                    let! _ = store.EnsurePresentAsync(relationship, cancellationToken)
                    ()
                | "RemoveStaleExactRelationship", RepairMutationTarget.Relationship (ExactRelationship.DirectoryVersionManifest relationship) ->
                    let staleDependencies =
                        {
                            GetDirectoryVersion =
                                fun directoryVersionId ->
                                    let actor = grainFactory.CreateActorProxyWithCorrelationId<IDirectoryVersionActor>(directoryVersionId, correlationId)

                                    actor.Get correlationId
                            GetCounter =
                                fun counterTuple ->
                                    let actor =
                                        grainFactory.CreateActorProxyWithCorrelationId<IRepositoryContentCounterActor>(
                                            RepositoryContentCounter.primaryKey
                                                counterTuple.RepositoryId
                                                counterTuple.StoragePoolId
                                                counterTuple.ManifestAddress,
                                            correlationId
                                        )

                                    actor.Get correlationId
                            GetWorkflow =
                                fun counterTuple ->
                                    let actor =
                                        grainFactory.CreateActorProxyWithCorrelationId<IManifestContributionWorkflowActor>(
                                            ManifestContributionWorkflow.primaryKey
                                                counterTuple.RepositoryId
                                                counterTuple.StoragePoolId
                                                counterTuple.ManifestAddress,
                                            correlationId
                                        )

                                    actor.Get correlationId
                            EnsureAbsent = fun relationship cancellationToken -> store.EnsureAbsentAsync(relationship, cancellationToken)
                        }

                    do! removeStaleRelationshipWith staleDependencies currentReport action.Action.Identity relationship cancellationToken
                | "ReconcileCounter", RepairMutationTarget.Counter (counterTuple, rebuiltCount) ->
                    let counterActor =
                        grainFactory.CreateActorProxyWithCorrelationId<IRepositoryContentCounterActor>(
                            RepositoryContentCounter.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress,
                            correlationId
                        )

                    let workflowActor =
                        grainFactory.CreateActorProxyWithCorrelationId<IManifestContributionWorkflowActor>(
                            ManifestContributionWorkflow.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress,
                            correlationId
                        )

                    let counterDependencies =
                        {
                            GetCounter = fun _ -> counterActor.Get correlationId
                            GetWorkflow = fun _ -> workflowActor.Get correlationId
                            HandleCounter =
                                fun command ->
                                    task {
                                        match! counterActor.Handle command metadata with
                                        | Ok value -> return value.ReturnValue
                                        | Error error -> return invalidOp error.Error
                                    }
                            StartWorkflow =
                                fun operationId target direction ranges counterRevision ->
                                    task {
                                        match!
                                            workflowActor.Start
                                                operationId
                                                target.RepositoryId
                                                target.StoragePoolId
                                                target.ManifestAddress
                                                direction
                                                ranges
                                                counterRevision
                                                metadata
                                            with
                                        | Ok _ -> return ()
                                        | Error error -> return invalidOp error.Error
                                    }
                        }

                    do! reconcileCounterStepWith counterDependencies currentReport counterTuple rebuiltCount
                | _ -> invalidOp "The validated repair plan contained an unsupported mutation."
            }
            :> Task

        {
            DiagnoseCurrent =
                fun selector bound cancellationToken ->
                    diagnoseWith
                        (ManifestContributionDiagnosis.productionDependencies context)
                        (getCurrentInstantExtended ())
                        correlationId
                        cancellationToken
                        bound
                        selector
            ApplyAction = applyAction
        }

    /// Handles the internal SystemAdmin repair route with dry-run as the default.
    let Repair: HttpHandler =
        fun next context ->
            task {
                try
                    let! parameters = context.BindJsonAsync<RepairManifestContributionParameters>()

                    match validateRequest parameters.ReportJson parameters.ExpectedReportSha256 parameters.Execute with
                    | Error error -> return! RequestErrors.BAD_REQUEST error next context
                    | Ok (report, _, bound, execute) ->
                        let! repairReport =
                            repairWith
                                (productionDependencies context)
                                (getCurrentInstantExtended ())
                                (getCorrelationId context)
                                context.RequestAborted
                                bound
                                report
                                execute

                        return! context.WriteJsonAsync repairReport
                with
                | RelationshipBoundExceeded error -> return! RequestErrors.BAD_REQUEST error next context
                | :? JsonException -> return! RequestErrors.BAD_REQUEST "The repair request body must be valid JSON." next context
            }
