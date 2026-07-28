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
open Grace.Types.Reference
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

    /// Supplies bounded current diagnosis and one target-specific action callback to the finite repair executor.
    type internal RepairDependencies =
        {
            DiagnoseCurrent: DiagnosisSelector -> ExactRelationshipReadBound -> CancellationToken -> Task<ManifestContributionDiagnosisReport>
            ApplyAction: ManifestContributionDiagnosisReport -> ManifestContributionDiagnosisReport -> RepairMutation -> CancellationToken -> Task
        }

    /// Supplies immediate source rereads and the projection-only mutations for missing exact relationships.
    type internal MissingRelationshipDependencies =
        {
            GetReference: ReferenceId -> Task<ReferenceDto>
            RepublishReferenceCreated: ReferenceId -> Task
            GetDirectoryVersion: DirectoryVersionId -> Task<DirectoryVersionDto>
            GetOrAdd: ExactRelationship -> CancellationToken -> Task<ExactRelationshipWriteOutcome>
        }

    /// Supplies immediate actor rereads for one destructive exact-relationship removal.
    type internal StaleRemovalDependencies =
        {
            GetDirectoryVersion: DirectoryVersionId -> Task<DirectoryVersionDto>
            GetCounter: CounterTuple -> Task<RepositoryContentCounterDto>
            GetWorkflow: CounterTuple -> Task<ManifestContributionWorkflowDto>
            EnsureAbsent: ExactRelationship -> CancellationToken -> Task<ExactRelationshipWriteOutcome>
        }

    /// Supplies immediate logical and physical evidence rereads plus the repair-only atomic counter command.
    type internal CounterReconciliationDependencies =
        {
            GetCounter: CounterTuple -> Task<RepositoryContentCounterDto>
            GetWorkflow: CounterTuple -> Task<ManifestContributionWorkflowDto>
            Reconcile: RepositoryContentCounterRepairCommand -> Task<RepositoryContentCounterDecision>
        }

    /// Converts the signed diagnosis target back into its bounded selector.
    let selectorFromTarget (target: DiagnosisTarget) =
        /// Parses one required selector GUID without accepting the empty value.
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
            | Ok _ when String.IsNullOrWhiteSpace storagePoolId -> Error "Target.Counter.StoragePoolId must not be empty."
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
        if isNull (box evidence) then
            Error "Count evidence must not contain JSON null."
        else
            match Guid.TryParse evidence.RepositoryId with
            | true, repositoryId when
                repositoryId <> Guid.Empty
                && not (String.IsNullOrWhiteSpace evidence.StoragePoolId)
                && not (String.IsNullOrWhiteSpace evidence.ManifestAddress)
                ->
                Ok { RepositoryId = repositoryId; StoragePoolId = evidence.StoragePoolId; ManifestAddress = evidence.ManifestAddress }
            | _ -> Error "Complete count evidence contains an invalid counter target."

    /// Maps one typed repair mutation to the exact concrete target retained by diagnosis.
    let internal actionTarget mutation = $"{mutation.Action.Kind}:{mutation.Action.Identity}"

    /// Returns the stable identity used to reject duplicate actions and validate remaining-plan prefixes.
    let private actionKey mutation = mutation.Action.Kind, mutation.Action.Identity

    /// Confirms current bounded diagnosis proved completed Increment accounting for one exact manifest tuple.
    let private hasCompleteManifestAccountingEvidence (report: ManifestContributionDiagnosisReport) (relationship: DirectoryVersionManifestRelationship) =
        not (isNull report.CountEvidence)
        && (report.CountEvidence
            |> Array.exists (fun evidence ->
                not (isNull (box evidence))
                && String.Equals(evidence.RepositoryId, $"{relationship.RepositoryId:D}", StringComparison.Ordinal)
                && String.Equals(evidence.StoragePoolId, relationship.StoragePoolId, StringComparison.Ordinal)
                && String.Equals(evidence.ManifestAddress, relationship.ManifestAddress, StringComparison.Ordinal)
                && String.Equals(evidence.Completeness, "Complete", StringComparison.Ordinal)
                && evidence.StoredCount.IsSome
                && evidence.RebuiltCount.IsSome))

    /// Derives the only allowed finite action plan from structured diagnosis evidence.
    let internal buildPlan (report: ManifestContributionDiagnosisReport) =
        let actions = ResizeArray<RepairMutation>()
        let errors = ResizeArray<string>()

        if isNull report.ActorFacts then
            errors.Add "ActorFacts must not be JSON null."
        elif report.ActorFacts
             |> Array.exists (fun fact -> isNull (box fact)) then
            errors.Add "ActorFacts must not contain JSON null."

        if isNull report.MissingRelationships then
            errors.Add "MissingRelationships must not be JSON null."
        else
            for identity in report.MissingRelationships do
                match parseRelationshipIdentity identity with
                | Error error -> errors.Add error
                | Ok relationship ->
                    match relationship with
                    | ExactRelationship.ReferenceRoot _ ->
                        actions.Add
                            { Action = { Kind = "RepublishReferenceCreated"; Identity = identity }; Target = RepairMutationTarget.Relationship relationship }
                    | ExactRelationship.ParentChild _ ->
                        actions.Add
                            { Action = { Kind = "GetOrAddExactRelationship"; Identity = identity }; Target = RepairMutationTarget.Relationship relationship }
                    | ExactRelationship.DirectoryVersionManifest manifestRelationship when hasCompleteManifestAccountingEvidence report manifestRelationship ->
                        actions.Add
                            { Action = { Kind = "GetOrAddExactRelationship"; Identity = identity }; Target = RepairMutationTarget.Relationship relationship }
                    | ExactRelationship.DirectoryVersionManifest _ -> ()

        if isNull report.StaleRelationships then
            errors.Add "StaleRelationships must not be JSON null."
        else
            for identity in report.StaleRelationships do
                match parseRelationshipIdentity identity with
                | Ok (ExactRelationship.DirectoryVersionManifest _ as relationship) ->
                    actions.Add
                        { Action = { Kind = "RemoveStaleExactRelationship"; Identity = identity }; Target = RepairMutationTarget.Relationship relationship }
                | Ok _ -> errors.Add $"Only an exact DirectoryVersion-manifest relationship can be removed as stale: '{identity}'."
                | Error error -> errors.Add error

        if isNull report.CountEvidence then
            errors.Add "CountEvidence must not be JSON null."
        else
            for evidence in report.CountEvidence do
                if isNull (box evidence) then
                    errors.Add "CountEvidence must not contain JSON null."
                else
                    match evidence.StoredCount, evidence.RebuiltCount with
                    | Some stored, Some rebuilt when
                        stored <> rebuilt
                        && String.Equals(evidence.Completeness, "Complete", StringComparison.Ordinal)
                        ->
                        if rebuilt <= 0L then
                            errors.Add "Repository content count repair requires a rebuilt positive count."
                        elif rebuilt > int64 report.MaxRelationships then
                            errors.Add "Repository content count repair exceeds the signed MaxRelationships bound."
                        else
                            match counterFromEvidence evidence with
                            | Error error -> errors.Add error
                            | Ok counter ->
                                actions.Add
                                    {
                                        Action =
                                            {
                                                Kind = "ReconcileRepositoryContentCount"
                                                Identity = $"{evidence.RepositoryId}|{evidence.StoragePoolId}|{evidence.ManifestAddress}"
                                            }
                                        Target = RepairMutationTarget.Counter(counter, rebuilt)
                                    }
                    | Some stored, Some rebuilt when stored <> rebuilt -> errors.Add "Counter reconciliation requires complete rebuilt count evidence."
                    | _ -> ()

        let keys = actions |> Seq.map actionKey |> Seq.toArray

        if keys.Length <> HashSet<_>(keys).Count then
            errors.Add "The signed diagnosis produces duplicate repair actions."

        if report.MaxRelationships <= 0 then
            errors.Add "The signed MaxRelationships bound must be positive."
        elif actions.Count > 2 * report.MaxRelationships then
            errors.Add "The repair plan exceeds the 2 * MaxRelationships action bound."

        if isNull report.RepairTargets then
            errors.Add "RepairTargets must not be JSON null."
        else
            let recognizedTargets =
                report.RepairTargets
                |> Array.filter (fun target ->
                    not (isNull target)
                    && (target.StartsWith("RepublishReferenceCreated:", StringComparison.Ordinal)
                        || target.StartsWith("GetOrAddExactRelationship:", StringComparison.Ordinal)
                        || target.StartsWith("RemoveStaleExactRelationship:", StringComparison.Ordinal)
                        || target.StartsWith("ReconcileRepositoryContentCount:", StringComparison.Ordinal)))

            let derivedTargets =
                actions
                |> Seq.map actionTarget
                |> Seq.sort
                |> Seq.toArray

            if derivedTargets
               <> (recognizedTargets |> Array.sort) then
                errors.Add "Structured diagnosis differences do not match the report's concrete repair targets."

        if errors.Count > 0 then
            Error(String.Join(" ", errors))
        else
            /// Orders projection repairs before stale cleanup and logical count replacement.
            let priority mutation =
                match mutation.Action.Kind with
                | "RepublishReferenceCreated" -> 0
                | "GetOrAddExactRelationship" -> 1
                | "RemoveStaleExactRelationship" -> 2
                | "ReconcileRepositoryContentCount" -> 3
                | _ -> 4

            actions
            |> Seq.sortBy (fun mutation -> priority mutation, mutation.Action.Identity)
            |> Seq.toArray
            |> Ok

    /// Returns the source actor facts that must remain byte-for-byte stable during one signed execution.
    let private sourceFacts (report: ManifestContributionDiagnosisReport) =
        report.ActorFacts
        |> Array.filter (fun fact ->
            String.Equals(fact.ActorType, "Reference", StringComparison.Ordinal)
            || String.Equals(fact.ActorType, "DirectoryVersion", StringComparison.Ordinal))

    /// Finds the exact actor snapshot recorded by one bounded diagnosis.
    let private actorFact actorType actorId (report: ManifestContributionDiagnosisReport) =
        report.ActorFacts
        |> Array.tryFind (fun fact ->
            String.Equals(fact.ActorType, actorType, StringComparison.Ordinal)
            && String.Equals(fact.ActorId, actorId, StringComparison.Ordinal))

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

    /// Confirms that a fresh diagnosis still authorizes exactly one missing relationship action.
    let private requireMissingAction (report: ManifestContributionDiagnosisReport) identity mutation =
        if
            not
                (
                    report.MissingRelationships
                    |> Array.contains identity
                )
            || not (
                report.RepairTargets
                |> Array.contains (actionTarget mutation)
            )
        then
            invalidOp "Fresh diagnosis did not authorize this exact missing relationship repair."

    /// Rereads the target source immediately before one projection-only missing relationship repair.
    let internal repairMissingRelationshipWith
        (dependencies: MissingRelationshipDependencies)
        (report: ManifestContributionDiagnosisReport)
        (mutation: RepairMutation)
        (cancellationToken: CancellationToken)
        =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            requireMissingAction report mutation.Action.Identity mutation

            match mutation.Action.Kind, mutation.Target with
            | "RepublishReferenceCreated", RepairMutationTarget.Relationship (ExactRelationship.ReferenceRoot relationship) ->
                let! current = dependencies.GetReference relationship.ReferenceId

                let fact =
                    actorFact "Reference" $"{relationship.ReferenceId:D}" report
                    |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not retain the Reference snapshot.")

                if
                    current.ReferenceId <> relationship.ReferenceId
                    || current.RepositoryId <> relationship.RepositoryId
                    || current.DirectoryId
                       <> relationship.RootDirectoryVersionId
                    || current.DeletedAt.IsSome
                    || not (String.Equals(fact.SnapshotJson, actorSnapshotJson current, StringComparison.Ordinal))
                then
                    invalidOp "Reference source changed before Created event republication."

                cancellationToken.ThrowIfCancellationRequested()
                do! dependencies.RepublishReferenceCreated relationship.ReferenceId
            | "GetOrAddExactRelationship", RepairMutationTarget.Relationship (ExactRelationship.ParentChild relationship as exact) ->
                let! current = dependencies.GetDirectoryVersion relationship.ParentDirectoryVersionId

                let fact =
                    actorFact "DirectoryVersion" $"{relationship.ParentDirectoryVersionId:D}" report
                    |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not retain the parent DirectoryVersion snapshot.")

                if
                    current.DirectoryVersion.DirectoryVersionId
                    <> relationship.ParentDirectoryVersionId
                    || current.DirectoryVersion.RepositoryId
                       <> relationship.RepositoryId
                    || not (
                        current.DirectoryVersion.Directories
                        |> Seq.contains relationship.ChildDirectoryVersionId
                    )
                    || not (String.Equals(fact.SnapshotJson, actorSnapshotJson current, StringComparison.Ordinal))
                then
                    invalidOp "Parent DirectoryVersion source changed before exact relationship repair."

                cancellationToken.ThrowIfCancellationRequested()
                let! _ = dependencies.GetOrAdd exact cancellationToken

                ()
            | "GetOrAddExactRelationship", RepairMutationTarget.Relationship (ExactRelationship.DirectoryVersionManifest relationship as exact) ->
                let! current = dependencies.GetDirectoryVersion relationship.DirectoryVersionId

                let fact =
                    actorFact "DirectoryVersion" $"{relationship.DirectoryVersionId:D}" report
                    |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not retain the DirectoryVersion snapshot.")

                let sourceContainsManifest =
                    match directManifests current fact.ActorId with
                    | Error error -> invalidOp $"DirectoryVersion manifests became unreadable before exact relationship repair: {error.Error}"
                    | Ok manifests ->
                        manifests
                        |> Array.exists (fun manifest ->
                            manifest.StoragePoolId = relationship.StoragePoolId
                            && manifest.ManifestAddress = relationship.ManifestAddress)

                if
                    current.DirectoryVersion.DirectoryVersionId
                    <> relationship.DirectoryVersionId
                    || current.DirectoryVersion.RepositoryId
                       <> relationship.RepositoryId
                    || not sourceContainsManifest
                    || not (String.Equals(fact.SnapshotJson, actorSnapshotJson current, StringComparison.Ordinal))
                then
                    invalidOp "DirectoryVersion manifest source changed before exact relationship repair."

                if not (hasCompleteManifestAccountingEvidence report relationship) then
                    invalidOp "DirectoryVersion manifest repair requires complete current Increment accounting evidence."

                cancellationToken.ThrowIfCancellationRequested()
                let! _ = dependencies.GetOrAdd exact cancellationToken

                ()
            | _ -> invalidOp "The missing relationship action did not match its typed target."
        }

    /// Removes one stale relationship only while source absence and signed physical evidence remain unchanged.
    let internal removeStaleRelationshipWith
        (dependencies: StaleRemovalDependencies)
        (signedReport: ManifestContributionDiagnosisReport)
        (currentReport: ManifestContributionDiagnosisReport)
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
                        currentReport.StaleRelationships
                        |> Array.contains identity
                    )
                || not (
                    currentReport.RepairTargets
                    |> Array.contains expectedTarget
                )
            then
                invalidOp "Fresh diagnosis did not authorize this exact stale relationship removal."

            let currentCounterFact =
                actorFact "RepositoryContentCounter" counterId currentReport
                |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not retain the repository counter snapshot.")

            let currentWorkflowFact =
                actorFact "ManifestContributionWorkflow" workflowId currentReport
                |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not retain the workflow snapshot.")

            if actorFact "RepositoryContentCounter" counterId signedReport
               <> Some currentCounterFact
               || actorFact "ManifestContributionWorkflow" workflowId signedReport
                  <> Some currentWorkflowFact then
                invalidOp "Counter or workflow evidence changed after the signed diagnosis."

            let countEvidence =
                currentReport.CountEvidence
                |> Array.tryFind (fun evidence ->
                    String.Equals(evidence.RepositoryId, $"{counterTuple.RepositoryId:D}", StringComparison.Ordinal)
                    && String.Equals(evidence.StoragePoolId, counterTuple.StoragePoolId, StringComparison.Ordinal)
                    && String.Equals(evidence.ManifestAddress, counterTuple.ManifestAddress, StringComparison.Ordinal))
                |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not contain counter evidence for the stale relationship.")

            if not (String.Equals(countEvidence.Completeness, "Complete", StringComparison.Ordinal))
               || countEvidence.StoredCount.IsNone
               || countEvidence.RebuiltCount.IsNone then
                invalidOp "Stale relationship removal requires complete current counter reconstruction."

            cancellationToken.ThrowIfCancellationRequested()
            let! source = dependencies.GetDirectoryVersion relationship.DirectoryVersionId

            let! counter = dependencies.GetCounter counterTuple
            let! workflow = dependencies.GetWorkflow counterTuple

            let sourceFact =
                actorFact "DirectoryVersion" $"{relationship.DirectoryVersionId:D}" currentReport
                |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not retain the DirectoryVersion snapshot.")

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
                && currentCounterFact.Revision = Some counter.Revision
                && String.Equals(currentCounterFact.SnapshotJson, actorSnapshotJson counter, StringComparison.Ordinal)
                && countEvidence.StoredCount = Some counter.Count

            let workflowMatches =
                workflow.RepositoryId = counterTuple.RepositoryId
                && workflow.StoragePoolId = counterTuple.StoragePoolId
                && workflow.ManifestAddress = counterTuple.ManifestAddress
                && currentWorkflowFact.Revision = Some workflow.Revision
                && String.Equals(currentWorkflowFact.SnapshotJson, workflowSnapshotJson workflow, StringComparison.Ordinal)
                && workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
                && workflow.FailedRanges.Length = 0
                && workflow.CompletedRanges.Length = workflow.Ranges.Length
                && workflow.CounterRevision <= counter.Revision

            if not counterMatches || not workflowMatches then
                invalidOp "Counter or workflow evidence changed before stale relationship removal."

            cancellationToken.ThrowIfCancellationRequested()
            let! _ = dependencies.EnsureAbsent (ExactRelationship.DirectoryVersionManifest relationship) cancellationToken

            return ()
        }

    /// Applies one atomic positive logical count replacement after immediate actor rereads match signed evidence.
    let internal reconcileCounterWith
        (dependencies: CounterReconciliationDependencies)
        (signedReport: ManifestContributionDiagnosisReport)
        (currentReport: ManifestContributionDiagnosisReport)
        (bound: ExactRelationshipReadBound)
        (counterTuple: CounterTuple)
        rebuiltCount
        =
        task {
            let maximumRelationships = ExactRelationshipReadBound.value bound

            if rebuiltCount <= 0L then
                invalidOp "Repository content count repair requires a rebuilt positive count."
            elif rebuiltCount > int64 maximumRelationships then
                invalidOp "Repository content count repair exceeds the signed MaxRelationships bound."

            let counterId = RepositoryContentCounter.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress

            let workflowId = ManifestContributionWorkflow.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress

            let currentCounterFact =
                actorFact "RepositoryContentCounter" counterId currentReport
                |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not retain the repository counter snapshot.")

            let currentWorkflowFact =
                actorFact "ManifestContributionWorkflow" workflowId currentReport
                |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not retain the workflow snapshot.")

            if actorFact "RepositoryContentCounter" counterId signedReport
               <> Some currentCounterFact
               || actorFact "ManifestContributionWorkflow" workflowId signedReport
                  <> Some currentWorkflowFact then
                invalidOp "Counter or workflow evidence changed after the signed diagnosis."

            let countEvidence =
                currentReport.CountEvidence
                |> Array.tryFind (fun evidence ->
                    String.Equals(evidence.RepositoryId, $"{counterTuple.RepositoryId:D}", StringComparison.Ordinal)
                    && String.Equals(evidence.StoragePoolId, counterTuple.StoragePoolId, StringComparison.Ordinal)
                    && String.Equals(evidence.ManifestAddress, counterTuple.ManifestAddress, StringComparison.Ordinal))
                |> Option.defaultWith (fun () -> invalidOp "Fresh diagnosis did not contain the requested counter evidence.")

            if not (String.Equals(countEvidence.Completeness, "Complete", StringComparison.Ordinal))
               || countEvidence.StoredCount.IsNone
               || countEvidence.RebuiltCount <> Some rebuiltCount then
                invalidOp "Counter repair requires fresh complete reconstruction for the same positive target."

            let! counter = dependencies.GetCounter counterTuple
            let! workflow = dependencies.GetWorkflow counterTuple

            let counterMatches =
                counter.RepositoryId = counterTuple.RepositoryId
                && counter.StoragePoolId = counterTuple.StoragePoolId
                && counter.ManifestAddress = counterTuple.ManifestAddress
                && currentCounterFact.Revision = Some counter.Revision
                && String.Equals(currentCounterFact.SnapshotJson, actorSnapshotJson counter, StringComparison.Ordinal)
                && countEvidence.StoredCount = Some counter.Count

            let workflowMatches =
                workflow.RepositoryId = counterTuple.RepositoryId
                && workflow.StoragePoolId = counterTuple.StoragePoolId
                && workflow.ManifestAddress = counterTuple.ManifestAddress
                && currentWorkflowFact.Revision = Some workflow.Revision
                && String.Equals(currentWorkflowFact.SnapshotJson, workflowSnapshotJson workflow, StringComparison.Ordinal)
                && workflow.Direction = ManifestContributionDirection.Increment
                && workflow.LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
                && workflow.Ranges.Length > 0
                && workflow.FailedRanges.Length = 0
                && workflow.CompletedRanges.Length = workflow.Ranges.Length
                && workflow.CounterRevision <= counter.Revision

            if not counterMatches || not workflowMatches then
                invalidOp "Counter or workflow evidence changed before logical count repair."

            if counter.Count = rebuiltCount then
                invalidOp "The repository manifest logical count already matches the rebuilt count."

            let operationId =
                RepositoryContentCounter.repairOperationId
                    counterTuple.RepositoryId
                    counterTuple.StoragePoolId
                    counterTuple.ManifestAddress
                    counter.Revision
                    rebuiltCount

            let command =
                {
                    OperationId = operationId
                    RepositoryId = counterTuple.RepositoryId
                    StoragePoolId = counterTuple.StoragePoolId
                    ManifestAddress = counterTuple.ManifestAddress
                    ExpectedRevision = counter.Revision
                    RebuiltCount = rebuiltCount
                }

            let! decision = dependencies.Reconcile command

            if decision.OperationId <> operationId
               || decision.Counter.Count <> rebuiltCount
               || decision.Counter.Revision <> counter.Revision + 1L
               || not decision.Events.IsEmpty
               || not decision.Intents.IsEmpty
               || decision.Counter.LastCompletedChange
                  |> Option.exists (fun change ->
                      change.OperationId = operationId
                      && change.PreviousCount = counter.Count
                      && change.CurrentCount = rebuiltCount
                      && change.Revision = counter.Revision + 1L)
                  |> not then
                invalidOp "RepositoryContentCounter repair returned an invalid atomic logical transition."
        }

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

    /// Executes each distinct signed action at most once, then runs one final bounded diagnosis.
    let internal repairWith
        (dependencies: RepairDependencies)
        generatedAt
        (_correlationId: string)
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
                let applied = ResizeArray<RepairMutation>()
                let mutable terminal: ManifestContributionRepairReport option = None

                try
                    let! initialCurrent = dependencies.DiagnoseCurrent selector bound cancellationToken

                    if not (initialEvidenceMatches report initialCurrent) then
                        terminal <-
                            Some(
                                result
                                    generatedAt
                                    report
                                    execute
                                    proposed
                                    Array.empty
                                    RepairOutcome.IncompleteRetain
                                    "Current evidence does not exactly match the signed diagnosis; run a fresh diagnosis."
                            )
                    elif not execute then
                        let outcome =
                            if proposed.Length = 0
                               && initialCurrent.Outcome = DiagnosisOutcome.VerifiedComplete then
                                RepairOutcome.VerifiedComplete
                            else
                                RepairOutcome.IncompleteRetain

                        terminal <-
                            Some(
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
                            )
                    else
                        let mutable index = 0

                        while index < proposed.Length && terminal.IsNone do
                            try
                                cancellationToken.ThrowIfCancellationRequested()

                                let! current = dependencies.DiagnoseCurrent selector bound cancellationToken

                                match buildPlan current with
                                | Error error ->
                                    terminal <-
                                        Some(
                                            result
                                                generatedAt
                                                report
                                                true
                                                proposed
                                                (applied.ToArray())
                                                RepairOutcome.IncompleteRetain
                                                $"{error} Run a fresh diagnosis."
                                        )
                                | Ok currentPlan ->
                                    let expectedRemaining = proposed[index..] |> Array.map actionKey

                                    let actualRemaining = currentPlan |> Array.map actionKey

                                    if sourceFacts current <> sourceFacts report
                                       || actualRemaining <> expectedRemaining then
                                        terminal <-
                                            Some(
                                                result
                                                    generatedAt
                                                    report
                                                    true
                                                    proposed
                                                    (applied.ToArray())
                                                    RepairOutcome.IncompleteRetain
                                                    "Durable evidence changed outside the confirmed applied prefix; run a fresh diagnosis."
                                            )
                                    else
                                        let action = proposed[index]

                                        do! dependencies.ApplyAction report current action cancellationToken

                                        applied.Add action
                                        index <- index + 1
                            with
                            | :? OperationCanceledException as ex ->
                                terminal <-
                                    Some(
                                        result
                                            generatedAt
                                            report
                                            true
                                            proposed
                                            (applied.ToArray())
                                            RepairOutcome.FailedRetain
                                            $"Repair was cancelled after preserving the confirmed applied prefix; the current action outcome is unknown and requires a fresh diagnosis: {ex.Message}"
                                    )
                            | ex ->
                                terminal <-
                                    Some(
                                        result
                                            generatedAt
                                            report
                                            true
                                            proposed
                                            (applied.ToArray())
                                            RepairOutcome.FailedRetain
                                            $"Repair stopped after preserving the confirmed applied prefix; the current action outcome is unknown and requires a fresh diagnosis: {ex.Message}"
                                    )

                        if terminal.IsNone then
                            let! finalCurrent = dependencies.DiagnoseCurrent selector bound cancellationToken

                            let finalPlan = buildPlan finalCurrent

                            let verified =
                                sourceFacts finalCurrent = sourceFacts report
                                && finalCurrent.Outcome = DiagnosisOutcome.VerifiedComplete
                                && (match finalPlan with
                                    | Ok actions -> actions.Length = 0
                                    | Error _ -> false)

                            terminal <-
                                Some(
                                    result
                                        generatedAt
                                        report
                                        true
                                        proposed
                                        (applied.ToArray())
                                        (if verified then
                                             RepairOutcome.VerifiedComplete
                                         else
                                             RepairOutcome.IncompleteRetain)
                                        (if verified then
                                             "Current post-repair bounded diagnosis is complete."
                                         else
                                             "Final bounded diagnosis remains incomplete; preserve the applied prefix and run a fresh diagnosis.")
                                )
                with
                | :? OperationCanceledException as ex ->
                    terminal <-
                        Some(
                            result
                                generatedAt
                                report
                                execute
                                proposed
                                (applied.ToArray())
                                RepairOutcome.FailedRetain
                                $"Repair was cancelled after preserving the confirmed applied prefix; run a fresh diagnosis: {ex.Message}"
                        )
                | ex ->
                    terminal <-
                        Some(
                            result
                                generatedAt
                                report
                                execute
                                proposed
                                (applied.ToArray())
                                RepairOutcome.FailedRetain
                                $"Repair failed after preserving the confirmed applied prefix; run a fresh diagnosis: {ex.Message}"
                        )

                return terminal.Value
        }

    /// Creates the production bounded reads and the five approved repair-only mutations.
    let private productionDependencies (context: HttpContext) (bound: ExactRelationshipReadBound) =
        let store = ManifestContributionAccounting.CosmosExactRelationshipStore(cosmosContainer) :> IExactRelationshipStore

        let correlationId = getCorrelationId context
        let metadata = createMetadata context

        /// Creates a Reference actor proxy for one immediate read or original-event republication.
        let referenceActor referenceId = grainFactory.CreateActorProxyWithCorrelationId<IReferenceActor>(referenceId, correlationId)

        /// Creates a DirectoryVersion actor proxy for one immediate source reread.
        let directoryVersionActor directoryVersionId = grainFactory.CreateActorProxyWithCorrelationId<IDirectoryVersionActor>(directoryVersionId, correlationId)

        /// Applies one already-validated signed mutation through only its repair-specific production boundary.
        let applyAction signedReport currentReport action cancellationToken =
            task {
                match action.Action.Kind, action.Target with
                | ("RepublishReferenceCreated"
                  | "GetOrAddExactRelationship"),
                  RepairMutationTarget.Relationship _ ->
                    let missingDependencies =
                        {
                            GetReference = fun referenceId -> (referenceActor referenceId).Get correlationId
                            RepublishReferenceCreated =
                                fun referenceId ->
                                    task {
                                        match! (referenceActor referenceId)
                                            .RepublishCreated correlationId
                                            with
                                        | Ok _ -> return ()
                                        | Error error -> return invalidOp error.Error
                                    }
                            GetDirectoryVersion =
                                fun directoryVersionId ->
                                    (directoryVersionActor directoryVersionId)
                                        .Get correlationId
                            GetOrAdd = fun relationship token -> store.EnsurePresentAsync(relationship, token)
                        }

                    do! repairMissingRelationshipWith missingDependencies currentReport action cancellationToken
                | "RemoveStaleExactRelationship", RepairMutationTarget.Relationship (ExactRelationship.DirectoryVersionManifest relationship) ->
                    let staleDependencies =
                        {
                            GetDirectoryVersion =
                                fun directoryVersionId ->
                                    (directoryVersionActor directoryVersionId)
                                        .Get correlationId
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
                            EnsureAbsent = fun relationship token -> store.EnsureAbsentAsync(relationship, token)
                        }

                    do! removeStaleRelationshipWith staleDependencies signedReport currentReport action.Action.Identity relationship cancellationToken
                | "ReconcileRepositoryContentCount", RepairMutationTarget.Counter (counterTuple, rebuiltCount) ->
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
                            Reconcile =
                                fun command ->
                                    task {
                                        match! counterActor.ReconcilePositiveCount command metadata with
                                        | Ok value -> return value.ReturnValue
                                        | Error error -> return invalidOp error.Error
                                    }
                        }

                    do! reconcileCounterWith counterDependencies signedReport currentReport bound counterTuple rebuiltCount
                | _ -> invalidOp "The validated repair plan contained an unsupported mutation."
            }
            :> Task

        {
            DiagnoseCurrent =
                fun selector readBound cancellationToken ->
                    diagnoseWith
                        (ManifestContributionDiagnosis.productionDependencies context)
                        (getCurrentInstantExtended ())
                        correlationId
                        cancellationToken
                        readBound
                        selector
            ApplyAction = applyAction
        }

    /// Handles the internal SystemAdmin repair route with dry-run as the default.
    let Repair: HttpHandler =
        fun next context ->
            task {
                try
                    let! parameters = context.BindJsonAsync<RepairManifestContributionParameters>()

                    if isNull parameters then
                        return! RequestErrors.BAD_REQUEST "The repair request body must be a JSON object." next context
                    else
                        match validateRequest parameters.ReportJson parameters.ExpectedReportSha256 parameters.Execute with
                        | Error error -> return! RequestErrors.BAD_REQUEST error next context
                        | Ok (report, _, bound, execute) ->
                            let! repairReport =
                                repairWith
                                    (productionDependencies context bound)
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
