namespace Grace.Server.Tests.Measurement

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

/// Projects unbounded diagnostic sources into deterministic, inspectable evidence fields.
module BoundedEvidence =

    [<Literal>]
    let private WorktreeStatePreviewCharacters = 4096

    [<Literal>]
    let private RunCommandVerbatimSerializedBytes = 8192

    [<Literal>]
    let private RunCommandPreviewCharacters = 4096

    [<Literal>]
    let private AssertionDetailVerbatimSerializedBytes = 8192

    [<Literal>]
    let private AssertionDetailPreviewCharacters = 4096

    [<Literal>]
    let private RuntimeFailurePreviewCharacters = 3072

    [<Literal>]
    let private RuntimeFailureRetainedEntries = 8

    /// Computes the lowercase SHA-256 identity for a diagnostic byte sequence.
    let private sha256Bytes (bytes: byte array) =
        bytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun digest -> digest.ToLowerInvariant()

    /// Converts diagnostic text to a deterministic ASCII alphabet that the default JSON encoder preserves one-for-one.
    let private printableAscii (value: string) =
        let builder = StringBuilder(value.Length)

        value
        |> Seq.iter (fun character ->
            let printable =
                match character with
                | '\r'
                | '\n'
                | '\t' -> ' '
                | value when Char.IsAsciiLetterOrDigit value -> value
                | ' '
                | '.'
                | ','
                | ':'
                | ';'
                | '='
                | '_'
                | '-'
                | '/'
                | '?' -> character
                | _ -> '?'

            builder.Append printable |> ignore)

        builder.ToString()

    /// Retains deterministic head and tail diagnostics inside the supplied character budget.
    let private boundedPreview maximumCharacters (value: string) =
        if maximumCharacters <= 0 then
            invalidArg (nameof maximumCharacters) "A positive diagnostic preview limit is required."

        if value.Length <= maximumCharacters then
            printableAscii value
        else
            let mutable omittedMarker = " ... omittedChars=0 ... "
            let mutable markerStable = false

            while not markerStable do
                let retainedCharacters = maximumCharacters - omittedMarker.Length
                let nextMarker = $" ... omittedChars={value.Length - retainedCharacters} ... "
                markerStable <- nextMarker.Equals(omittedMarker, StringComparison.Ordinal)
                omittedMarker <- nextMarker

            let retainedCharacters = maximumCharacters - omittedMarker.Length
            let headCharacters = retainedCharacters / 2
            let tailCharacters = retainedCharacters - headCharacters
            let head = value.Substring(0, headCharacters)
            let tail = value.Substring(value.Length - tailCharacters, tailCharacters)
            printableAscii $"{head}{omittedMarker}{tail}"

    /// Summarizes one diagnostic with its original size, digest, and bounded head/tail preview.
    let private summarize maximumPreviewCharacters (value: string) =
        let source = if isNull value then String.Empty else value
        let bytes = Encoding.UTF8.GetBytes source
        let truncated = source.Length > maximumPreviewCharacters

        $"sourceChars={source.Length}; sourceUtf8Bytes={bytes.Length}; sha256={sha256Bytes bytes}; truncated={truncated.ToString().ToLowerInvariant()}; preview={boundedPreview maximumPreviewCharacters source}"

    /// Preserves a value while its exact default-JSON representation fits, otherwise retaining a bounded source summary.
    let private boundedText maximumVerbatimSerializedBytes maximumPreviewCharacters value =
        let source = if isNull value then String.Empty else value
        let serializedBytes = JsonSerializer.SerializeToUtf8Bytes source

        if serializedBytes.Length
           <= maximumVerbatimSerializedBytes then
            source
        else
            summarize maximumPreviewCharacters source

    /// Preserves an ordinary hosted command verbatim and summarizes values that would consume an unsafe serialized budget.
    let command value = boundedText RunCommandVerbatimSerializedBytes RunCommandPreviewCharacters value

    /// Preserves ordinary assertion diagnostics and summarizes failure-controlled details before evidence serialization.
    let assertionDetail value = boundedText AssertionDetailVerbatimSerializedBytes AssertionDetailPreviewCharacters value

    /// Represents raw Git porcelain state without allowing path count to exceed one run record.
    let worktreeState value =
        if String.IsNullOrWhiteSpace value then
            "clean"
        else
            let pathEntryCount =
                value
                    .Split(
                        [| '\r'; '\n' |],
                        StringSplitOptions.RemoveEmptyEntries
                    )
                    .Length

            $"pathEntryCount={pathEntryCount}; {summarize WorktreeStatePreviewCharacters value}"

    /// Represents a nonempty failure ledger with bounded first/last entries and a digest of the complete ledger.
    let runtimeFailures (failures: string array) =
        if Array.isEmpty failures then
            Array.empty
        else
            let retainedPerSide = RuntimeFailureRetainedEntries / 2

            let retainedIndexes =
                if failures.Length <= RuntimeFailureRetainedEntries then
                    [| 0 .. failures.Length - 1 |]
                else
                    Array.append [| 0 .. retainedPerSide - 1 |] [|
                        failures.Length - retainedPerSide .. failures.Length - 1
                    |]

            let ledgerBytes = JsonSerializer.SerializeToUtf8Bytes failures

            let totalFailureBytes =
                failures
                |> Array.sumBy (fun failure -> if isNull failure then 0L else int64 (Encoding.UTF8.GetByteCount failure))

            let ledger =
                retainedIndexes
                |> Array.map (fun index -> $"failureIndex={index}; {summarize RuntimeFailurePreviewCharacters failures[index]}")

            Array.append
                [|
                    $"failureCount={failures.Length}; retainedCount={retainedIndexes.Length}; omittedCount={failures.Length - retainedIndexes.Length}; sourceUtf8Bytes={totalFailureBytes}; sha256={sha256Bytes ledgerBytes}"
                |]
                ledger

/// Captures the immutable metadata that identifies one hosted measurement execution.
[<CLIMutable>]
type MeasurementRun =
    {
        RecordType: string
        RunId: string
        CommitSha: string
        Worktree: string
        WorktreeState: string
        Command: string
        EvidenceDirectory: string
        Scenarios: string array
        StartedAt: string
    }

    /// Builds run metadata from the scenario plan that will actually execute.
    static member Create(runId, commitSha, worktree, worktreeState, command, evidenceDirectory, executedScenarioPlan) =
        {
            RecordType = nameof MeasurementRun
            RunId = runId
            CommitSha = commitSha
            Worktree = worktree
            WorktreeState = BoundedEvidence.worktreeState worktreeState
            Command = BoundedEvidence.command command
            EvidenceDirectory = evidenceDirectory
            Scenarios = Array.copy executedScenarioPlan
            StartedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        }

/// Captures one typed metric or durable-state observation used by a scenario assertion.
[<CLIMutable>]
type MeasurementSample =
    {
        RecordType: string
        RunId: string
        ScenarioId: string
        SampleId: string
        Name: string
        Value: int64
        Labels: Dictionary<string, string>
        ObservedAt: string
    }

    /// Builds a bounded sample without accepting an outcome decision from the caller.
    static member Create(runId, scenarioId, sampleId, name, value, labels: IDictionary<string, string>) =
        {
            RecordType = nameof MeasurementSample
            RunId = runId
            ScenarioId = scenarioId
            SampleId = sampleId
            Name = name
            Value = value
            Labels = Dictionary<string, string>(labels, StringComparer.Ordinal)
            ObservedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        }

/// Captures one named proof result that contributes to a derived scenario outcome.
[<CLIMutable>]
type MeasurementAssertion =
    {
        RecordType: string
        RunId: string
        ScenarioId: string
        AssertionId: string
        Passed: bool
        Detail: string
        ObservedAt: string
    }

    /// Builds one assertion record while leaving terminal outcome derivation to ScenarioSummary.
    static member Create(runId, scenarioId, assertionId, passed, detail) =
        {
            RecordType = nameof MeasurementAssertion
            RunId = runId
            ScenarioId = scenarioId
            AssertionId = assertionId
            Passed = passed
            Detail = BoundedEvidence.assertionDetail detail
            ObservedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        }

/// Captures the derived terminal result for one executed or skipped scenario.
[<CLIMutable>]
type ScenarioSummary =
    {
        RecordType: string
        RunId: string
        ScenarioId: string
        Outcome: string
        RequiredAssertionIds: string array
        RequiredAssertionCount: int
        PassedAssertionCount: int
        FailedAssertionIds: string array
        RuntimeFailures: string array
        CompletedAt: string
    }

/// Defines the only Baseline assertion identities permitted to produce a passing summary.
module Baseline =

    /// Lists the exact assertion identities required by the MCA Baseline tracer.
    let requiredAssertionIds =
        [|
            "baseline.setup-deliveries-completed"
            "baseline.stimulus-deliveries-completed"
            "baseline.reference-root-set"
            "baseline.manifest-relationship-set"
            "baseline.logical-counts"
            "baseline.workflow-counts"
            "baseline.physical-active-counts"
            "baseline.message-delta"
            "baseline.duration-delta"
            "baseline.identity-isolation"
            "baseline.evidence-integrity"
        |]

/// Captures structured diagnosis evidence and the action support derived by the production repair planner.
type RepairDiagnosisEvidence =
    {
        OutcomeIsIncompleteRetain: bool
        ExpectedActionIdentity: string
        MissingRelationships: string array
        StaleRelationships: string array
        RepairTargets: string array
        ProductionPlanActionKinds: string array
        ProductionPlanActionIdentities: string array
        UnknownFields: string array
        EvidenceGaps: string array
    }

/// Captures the mutation-sensitive facts produced by the real repair dry-run route.
type RepairDryRunEvidence =
    {
        Execute: bool
        ExpectedActionIdentity: string
        ProposedActionKinds: string array
        ProposedActionIdentities: string array
        AppliedActionKinds: string array
        ReferenceRootPresent: bool
    }

/// Captures the action, identity, broker, and durable facts required to attribute repair republication.
type RepairExecuteEvidence =
    {
        Execute: bool
        ExpectedActionIdentity: string
        ProposedActionKinds: string array
        ProposedActionIdentities: string array
        AppliedActionKinds: string array
        AppliedActionIdentities: string array
        OriginalReferenceId: Guid
        RepairCorrelationId: string
        OriginalEventCorrelationId: string
        ExpectedMessageId: string
        ObservedMessageIds: string array
        ObservedCorrelationIds: string array
        MessageDelta: int64
        DurationDelta: int64
        ReferenceRootRestored: bool
    }

/// Defines and validates the exact evidence contract for one missing Reference-root repair witness.
module Repair =

    [<Literal>]
    let private SupportedActionKind = "RepublishReferenceCreated"

    /// Lists the exact assertion identities required by the MCA Repair scenario.
    let requiredAssertionIds =
        [|
            "repair.seed-deliveries-completed"
            "repair.corruption-applied"
            "repair.diagnosis-one-supported-action"
            "repair.dry-run-no-mutation"
            "repair.execute-one-action"
            "repair.republication-message-delta"
            "repair.republication-duration-delta"
            "repair.reference-root-restored"
            "repair.logical-state-unchanged"
            "repair.workflow-state-unchanged"
            "repair.physical-state-unchanged"
            "repair.evidence-integrity"
        |]

    /// Requires one supported action and rejects zero, duplicate, or unrelated repair plans.
    let private validateOneSupportedAction
        description
        expectedIdentity
        (actionKinds: string array)
        (actionIdentities: string array)
        (errors: ResizeArray<string>)
        =
        if
            isNull actionKinds
            || actionKinds.Length <> 1
            || not (String.Equals(actionKinds[0], SupportedActionKind, StringComparison.Ordinal))
            || isNull actionIdentities
            || actionIdentities.Length <> 1
            || not (String.Equals(actionIdentities[0], expectedIdentity, StringComparison.Ordinal))
        then
            errors.Add($"{description} must contain exactly one {SupportedActionKind} action for the expected relationship identity.")

    /// Accepts retained diagnosis uncertainty only when the production planner still derives the exact supported Reference-root action.
    let validateDiagnosis (evidence: RepairDiagnosisEvidence) =
        let errors = ResizeArray<string>()

        if not evidence.OutcomeIsIncompleteRetain then
            errors.Add("The one-missing-root diagnosis must retain an incomplete outcome until repair executes.")

        if String.IsNullOrWhiteSpace evidence.ExpectedActionIdentity then
            errors.Add("The expected missing Reference-root relationship identity must not be empty.")

        if
            isNull evidence.MissingRelationships
            || evidence.MissingRelationships.Length <> 1
            || not (String.Equals(evidence.MissingRelationships[0], evidence.ExpectedActionIdentity, StringComparison.Ordinal))
        then
            errors.Add("Diagnosis must contain exactly the expected missing Reference-root relationship.")

        if isNull evidence.StaleRelationships
           || evidence.StaleRelationships.Length <> 0 then
            errors.Add("Diagnosis must not contain a stale relationship for the one-missing-root scenario.")

        let expectedTarget = $"{SupportedActionKind}:{evidence.ExpectedActionIdentity}"

        if
            isNull evidence.RepairTargets
            || evidence.RepairTargets.Length <> 1
            || not (String.Equals(evidence.RepairTargets[0], expectedTarget, StringComparison.Ordinal))
        then
            errors.Add("Diagnosis must report exactly the supported Reference-created republication target.")

        validateOneSupportedAction
            "The production repair plan"
            evidence.ExpectedActionIdentity
            evidence.ProductionPlanActionKinds
            evidence.ProductionPlanActionIdentities
            errors

        if isNull evidence.UnknownFields then
            errors.Add("Diagnosis UnknownFields must be retained as an explicit array.")

        if isNull evidence.EvidenceGaps then
            errors.Add("Diagnosis EvidenceGaps must be retained as an explicit array.")

        errors.ToArray()

    /// Rejects a dry run that executes, mutates, or fails to retain the one-action plan.
    let validateDryRun (evidence: RepairDryRunEvidence) =
        let errors = ResizeArray<string>()

        if evidence.Execute then
            errors.Add("The repair dry run was marked for execution.")

        validateOneSupportedAction
            "The repair dry-run plan"
            evidence.ExpectedActionIdentity
            evidence.ProposedActionKinds
            evidence.ProposedActionIdentities
            errors

        if isNull evidence.AppliedActionKinds
           || evidence.AppliedActionKinds.Length <> 0 then
            errors.Add("The repair dry run applied a mutation.")

        if evidence.ReferenceRootPresent then
            errors.Add("The missing Reference-root relationship changed during dry run.")

        errors.ToArray()

    /// Rejects execute evidence unless one original deterministic delivery both settles and restores the relationship.
    let validateExecute (evidence: RepairExecuteEvidence) =
        let errors = ResizeArray<string>()

        if not evidence.Execute then
            errors.Add("The repair execute response was marked as a dry run.")

        validateOneSupportedAction
            "The repair execute plan"
            evidence.ExpectedActionIdentity
            evidence.ProposedActionKinds
            evidence.ProposedActionIdentities
            errors

        validateOneSupportedAction
            "The repair applied prefix"
            evidence.ExpectedActionIdentity
            evidence.AppliedActionKinds
            evidence.AppliedActionIdentities
            errors

        if evidence.OriginalReferenceId = Guid.Empty then
            errors.Add("The original Reference identity must not be empty.")

        if
            String.IsNullOrWhiteSpace evidence.RepairCorrelationId
            || String.Equals(evidence.RepairCorrelationId, string evidence.OriginalReferenceId, StringComparison.OrdinalIgnoreCase)
        then
            errors.Add("The repair request correlation identity must differ from the original Reference identity.")

        if String.IsNullOrWhiteSpace evidence.OriginalEventCorrelationId then
            errors.Add("The original persisted Reference-created correlation identity must not be empty.")

        let deterministicMessageId = $"Reference/{evidence.OriginalReferenceId}/Created"

        if not (String.Equals(evidence.ExpectedMessageId, deterministicMessageId, StringComparison.Ordinal)) then
            errors.Add("Repair republication must reuse the original deterministic Reference-created message identity.")

        if
            isNull evidence.ObservedMessageIds
            || evidence.ObservedMessageIds.Length <> 1
            || not (String.Equals(evidence.ObservedMessageIds[0], deterministicMessageId, StringComparison.Ordinal))
        then
            errors.Add("Repair republication must observe exactly one original deterministic Reference-created envelope.")

        if
            isNull evidence.ObservedCorrelationIds
            || evidence.ObservedCorrelationIds.Length <> 1
            || not (String.Equals(evidence.ObservedCorrelationIds[0], evidence.OriginalEventCorrelationId, StringComparison.Ordinal))
        then
            errors.Add("Repair republication must preserve the original persisted Reference-created correlation identity.")

        if evidence.MessageDelta <> 1L then
            errors.Add($"Repair republication requires an exact completed message delta of one, observed {evidence.MessageDelta}.")

        if evidence.DurationDelta <> 1L then
            errors.Add($"Repair republication requires an exact completed duration delta of one, observed {evidence.DurationDelta}.")

        if not evidence.ReferenceRootRestored then
            errors.Add("Repair republication did not restore the exact Reference-root relationship.")

        errors.ToArray()

/// Defines the only HotManifest assertion identities permitted to produce a passing summary.
module HotManifest =

    /// Lists the exact assertion identities required by the MCA HotManifest topology.
    let requiredAssertionIds =
        [|
            "hot-manifest.setup-deliveries-completed"
            "hot-manifest.stimulus-deliveries-completed"
            "hot-manifest.reference-root-cardinality"
            "hot-manifest.manifest-relationship-cardinality"
            "hot-manifest.logical-count"
            "hot-manifest.workflow-count"
            "hot-manifest.physical-active-count"
            "hot-manifest.message-delta"
            "hot-manifest.duration-delta"
            "hot-manifest.identity-isolation"
            "hot-manifest.evidence-integrity"
        |]

/// Defines the only HighlySharedDirectoryVersion assertion identities permitted to produce a passing summary.
module HighlySharedDirectoryVersion =

    /// Lists the exact assertion identities required by the MCA HighlySharedDirectoryVersion topology.
    let requiredAssertionIds =
        [|
            "highly-shared.setup-deliveries-completed"
            "highly-shared.stimulus-deliveries-completed"
            "highly-shared.reference-root-cardinality"
            "highly-shared.manifest-relationship-cardinality"
            "highly-shared.logical-count"
            "highly-shared.workflow-count"
            "highly-shared.physical-active-count"
            "highly-shared.message-delta"
            "highly-shared.duration-delta"
            "highly-shared.identity-isolation"
            "highly-shared.evidence-integrity"
        |]

/// Declares the exact identities and cardinalities that one topology is allowed to produce.
type TopologyCardinalityExpectation =
    {
        ScenarioId: string
        RepositoryId: string
        RequiredAssertionIds: string array
        DeclaredIdentityIds: string array
        SetupMessageIds: string array
        StimulusMessageIds: string array
        ReferenceRootRelationshipIds: string array
        ManifestRelationshipIds: string array
        LogicalCount: int64
        WorkflowCount: int64
        PhysicalActiveCount: int64
    }

/// Captures the completed-only deliveries and durable graph observed for one topology.
type TopologyCardinalityObservation =
    {
        SetupObservedMessageIds: string array
        SetupSettledBeforeStimulusBaseline: bool
        StimulusObservedMessageIds: string array
        ReferenceRootRelationshipIds: string array
        ManifestRelationshipIds: string array
        LogicalCount: int64
        WorkflowCount: int64
        PhysicalActiveCount: int64
        MessageDelta: int64
        DurationDelta: int64
    }

/// Projects exact topology evidence into the assertion decisions used by hosted scenarios.
type TopologyCardinalityEvaluation =
    {
        SetupDeliveriesCompleted: bool
        StimulusDeliveriesCompleted: bool
        ReferenceRootCardinality: bool
        ManifestRelationshipCardinality: bool
        LogicalCount: bool
        WorkflowCount: bool
        PhysicalActiveCount: bool
        MessageDelta: bool
        DurationDelta: bool
        IdentityIsolation: bool
        AllPassed: bool
    }

/// Evaluates topology evidence without allowing counts or duplicate identities to stand in for the declared graph.
module TopologyCardinality =

    /// Requires exact unique ordinal identity sets on both sides.
    let private exactUniqueSet (expected: string array) (observed: string array) =
        let expectedSet = HashSet<string>(expected, StringComparer.Ordinal)
        let observedSet = HashSet<string>(observed, StringComparer.Ordinal)

        expectedSet.Count = expected.Length
        && observedSet.Count = observed.Length
        && expectedSet.SetEquals observedSet

    /// Rejects repository, scenario, or declared production identities shared by two topology declarations.
    let validateScenarioIsolation (expectations: TopologyCardinalityExpectation array) =
        let errors = ResizeArray<string>()

        let requireUnique description values =
            values
            |> Array.countBy id
            |> Array.filter (fun (_, count) -> count > 1)
            |> Array.iter (fun (value, count) -> errors.Add($"{description} '{value}' occurred {count} times."))

        expectations
        |> Array.collect (fun expectation -> expectation.DeclaredIdentityIds)
        |> requireUnique "Declared topology identity"

        expectations
        |> Array.map (fun expectation -> expectation.RepositoryId)
        |> requireUnique "Topology repository"

        expectations
        |> Array.map (fun expectation -> expectation.ScenarioId)
        |> requireUnique "Scenario identity"

        errors.ToArray()

    /// Evaluates every cardinality and delivery gate using exact equality and unique identities.
    let evaluate (expected: TopologyCardinalityExpectation) (observed: TopologyCardinalityObservation) =
        let setupDeliveriesCompleted =
            expected.SetupMessageIds.Length > 0
            && observed.SetupSettledBeforeStimulusBaseline
            && exactUniqueSet expected.SetupMessageIds observed.SetupObservedMessageIds

        let expectedStimulusDelta = int64 expected.StimulusMessageIds.Length

        let stimulusIdentitiesComplete =
            expected.StimulusMessageIds.Length > 0
            && exactUniqueSet expected.StimulusMessageIds observed.StimulusObservedMessageIds

        let messageDelta = observed.MessageDelta = expectedStimulusDelta
        let durationDelta = observed.DurationDelta = expectedStimulusDelta

        let stimulusDeliveriesCompleted =
            stimulusIdentitiesComplete
            && messageDelta
            && durationDelta

        let referenceRootCardinality = exactUniqueSet expected.ReferenceRootRelationshipIds observed.ReferenceRootRelationshipIds

        let manifestRelationshipCardinality = exactUniqueSet expected.ManifestRelationshipIds observed.ManifestRelationshipIds

        let logicalCount = observed.LogicalCount = expected.LogicalCount
        let workflowCount = observed.WorkflowCount = expected.WorkflowCount
        let physicalActiveCount = observed.PhysicalActiveCount = expected.PhysicalActiveCount

        let identityIsolation =
            validateScenarioIsolation [| expected |]
            |> Array.isEmpty
            && stimulusIdentitiesComplete
            && exactUniqueSet expected.SetupMessageIds observed.SetupObservedMessageIds

        let allPassed =
            setupDeliveriesCompleted
            && stimulusDeliveriesCompleted
            && referenceRootCardinality
            && manifestRelationshipCardinality
            && logicalCount
            && workflowCount
            && physicalActiveCount
            && messageDelta
            && durationDelta
            && identityIsolation

        {
            SetupDeliveriesCompleted = setupDeliveriesCompleted
            StimulusDeliveriesCompleted = stimulusDeliveriesCompleted
            ReferenceRootCardinality = referenceRootCardinality
            ManifestRelationshipCardinality = manifestRelationshipCardinality
            LogicalCount = logicalCount
            WorkflowCount = workflowCount
            PhysicalActiveCount = physicalActiveCount
            MessageDelta = messageDelta
            DurationDelta = durationDelta
            IdentityIsolation = identityIsolation
            AllPassed = allPassed
        }

/// Defines the exact proof contract for deterministic duplicate-backlog recovery.
module DuplicateBacklog =

    /// Lists the exact assertion identities required by the duplicate-backlog witness.
    let requiredAssertionIds =
        [|
            "duplicate-backlog.seed-deliveries-completed"
            "duplicate-backlog.pre-stop-terminal-barrier"
            "duplicate-backlog.visible-while-stopped"
            "duplicate-backlog.fresh-server-readiness"
            "duplicate-backlog.replay-message-delta"
            "duplicate-backlog.replay-duration-delta"
            "duplicate-backlog.unrelated-event-excluded"
            "duplicate-backlog.reference-root-state-unchanged"
            "duplicate-backlog.manifest-state-unchanged"
            "duplicate-backlog.logical-state-unchanged"
            "duplicate-backlog.workflow-state-unchanged"
            "duplicate-backlog.physical-state-unchanged"
            "duplicate-backlog.identity-isolation"
            "duplicate-backlog.evidence-integrity"
        |]

    /// Rejects a stop boundary until the exact finite seed inventory, completed delivery, and durable convergence all agree.
    let validatePreStopBarrier (expectedMessageIds: string array) (observedMessageIds: string array) deliveryCompleted durableConverged =
        let errors = ResizeArray<string>()
        let expected = HashSet<string>(expectedMessageIds, StringComparer.Ordinal)
        let observed = HashSet<string>(observedMessageIds, StringComparer.Ordinal)

        if expected.Count <> expectedMessageIds.Length then
            errors.Add("Expected seed inventory contains duplicate identities.")

        if observed.Count <> observedMessageIds.Length then
            errors.Add("Observed seed inventory contains duplicate deliveries.")

        expected
        |> Seq.filter (observed.Contains >> not)
        |> Seq.iter (fun messageId -> errors.Add($"Missing seed envelope '{messageId}'."))

        observed
        |> Seq.filter (expected.Contains >> not)
        |> Seq.iter (fun messageId -> errors.Add($"Unclassified seed envelope '{messageId}'."))

        if not deliveryCompleted then
            errors.Add("Seed delivery completion was not terminal before Grace.Server stopped.")

        if not durableConverged then
            errors.Add("Seed durable state had not converged before Grace.Server stopped.")

        errors.ToArray()

    /// Requires every selected replay identity to appear in at least one observed stopped-server broker snapshot.
    let validateStoppedBacklogVisibility (selectedMessageIds: string array) (observedMessageIds: string array) =
        let errors = ResizeArray<string>()
        let selected = HashSet<string>(selectedMessageIds, StringComparer.Ordinal)
        let observed = HashSet<string>(observedMessageIds, StringComparer.Ordinal)

        if selected.Count <> selectedMessageIds.Length then
            errors.Add("Selected replay identities contain duplicates.")

        if Array.isEmpty observedMessageIds then
            errors.Add("No broker state was observed while Grace.Server was stopped.")

        selected
        |> Seq.filter (observed.Contains >> not)
        |> Seq.iter (fun messageId -> errors.Add($"Replay envelope '{messageId}' was not visible while Grace.Server was stopped."))

        errors.ToArray()

    /// Requires post-command health to be freshly observed and followed by successful HTTP readiness.
    let validateFreshServerReadiness (commandStartedAt: DateTimeOffset) (healthObservedAt: DateTimeOffset) httpReady =
        let errors = ResizeArray<string>()

        if healthObservedAt <= commandStartedAt then
            errors.Add("Grace.Server health was not observed after the start command began.")

        if not httpReady then
            errors.Add("Grace.Server HTTP readiness failed after fresh health.")

        errors.ToArray()

/// Derives a scenario outcome from exact assertion identities and the runtime-failure ledger.
module ScenarioSummary =

    /// Derives Passed, Failed, or Skipped without accepting a caller-supplied success value.
    let derive runId scenarioId (requiredAssertionIds: string array) (assertions: MeasurementAssertion array) runtimeFailures prerequisiteSkipped =
        let required = HashSet<string>(requiredAssertionIds, StringComparer.Ordinal)

        let observedIds =
            assertions
            |> Array.map (fun assertion -> assertion.AssertionId)

        let observed = HashSet<string>(observedIds, StringComparer.Ordinal)
        let duplicates = observedIds.Length <> observed.Count
        let requiredHasDuplicates = requiredAssertionIds.Length <> required.Count

        let identitiesMatch =
            not requiredHasDuplicates
            && not duplicates
            && required.SetEquals observed
            && assertions
               |> Array.forall (fun assertion ->
                   assertion.RunId.Equals(runId, StringComparison.Ordinal)
                   && assertion.ScenarioId.Equals(scenarioId, StringComparison.Ordinal))

        let allPassed =
            assertions
            |> Array.forall (fun assertion -> assertion.Passed)

        let passedAssertionCount =
            assertions
            |> Array.filter (fun assertion ->
                assertion.Passed
                && required.Contains assertion.AssertionId)
            |> Array.map (fun assertion -> assertion.AssertionId)
            |> fun assertionIds -> HashSet<string>(assertionIds, StringComparer.Ordinal)
            |> fun assertionIds -> assertionIds.Count

        let failedAssertionIds =
            requiredAssertionIds
            |> Array.filter (fun requiredId ->
                assertions
                |> Array.exists (fun assertion ->
                    assertion.AssertionId.Equals(requiredId, StringComparison.Ordinal)
                    && assertion.Passed)
                |> not)

        let cleanSkip =
            prerequisiteSkipped
            && Array.isEmpty assertions
            && Array.isEmpty runtimeFailures

        let outcome =
            if cleanSkip then
                "Skipped"
            elif identitiesMatch
                 && allPassed
                 && Array.isEmpty runtimeFailures
                 && not prerequisiteSkipped then
                "Passed"
            else
                "Failed"

        {
            RecordType = nameof ScenarioSummary
            RunId = runId
            ScenarioId = scenarioId
            Outcome = outcome
            RequiredAssertionIds = Array.copy requiredAssertionIds
            RequiredAssertionCount = requiredAssertionIds.Length
            PassedAssertionCount = passedAssertionCount
            FailedAssertionIds = failedAssertionIds
            RuntimeFailures = BoundedEvidence.runtimeFailures runtimeFailures
            CompletedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        }

/// Reports whether exact cumulative settlement metrics are complete, still pending, or invalid.
type DeltaEvaluation =
    | Complete of messageDelta: int64 * durationDelta: int64
    | Pending
    | Invalid of reason: string

/// Parses only the two exact production OpenMetrics settlement samples used by the Baseline witness.
module OpenMetrics =

    [<Literal>]
    let private messageMetricName = "grace_manifest_contribution_messages_total"

    [<Literal>]
    let private durationMetricName = "grace_manifest_contribution_processing_duration_milliseconds_count"

    let private requiredLabels =
        dict [ "otel_scope_name", "Grace.ManifestContributionAccounting"
               "stage", "settle"
               "outcome", "completed" ]

    let private samplePattern =
        Regex("^(?<name>[A-Za-z_:][A-Za-z0-9_:]*)(?:\\{(?<labels>.*)\\})?\\s+(?<value>[^\\s]+)(?:\\s+.*)?$", RegexOptions.CultureInvariant)

    let private labelPattern = Regex("(?:^|,)\\s*(?<key>[A-Za-z_][A-Za-z0-9_]*)=\"(?<value>(?:\\\\.|[^\"])*)\"\\s*(?=,|$)", RegexOptions.CultureInvariant)

    let private freshProcessZeroBaseline =
        """
grace_manifest_contribution_messages_total{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 0
grace_manifest_contribution_processing_duration_milliseconds_count{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 0
"""

    /// Unescapes one OpenMetrics label value after the label grammar has bounded it.
    let private unescapeLabelValue (value: string) =
        value
            .Replace("\\\"", "\"")
            .Replace("\\\\", "\\")
            .Replace("\\n", "\n")

    /// Parses a complete label list and rejects duplicate or unparsed fragments.
    let private tryParseLabels (text: string) =
        let labels = Dictionary<string, string>(StringComparer.Ordinal)
        let matches = labelPattern.Matches(text)
        let mutable valid = true
        let mutable consumed = 0

        for labelMatch in matches |> Seq.cast<Match> do
            let separatorLength =
                let prefix = text.Substring(consumed, labelMatch.Index - consumed)

                if String.IsNullOrWhiteSpace prefix
                   || prefix.Trim() = "," then
                    prefix.Length
                else
                    -1

            if separatorLength < 0 then valid <- false

            let key = labelMatch.Groups["key"].Value
            let value = unescapeLabelValue labelMatch.Groups["value"].Value

            if not (labels.TryAdd(key, value)) then valid <- false

            consumed <- labelMatch.Index + labelMatch.Length

        if text.Substring(consumed).Trim().Length > 0 then valid <- false

        if valid then Some labels else None

    /// Parses an exact nonnegative integer sample value.
    let private tryParseValue (text: string) =
        let mutable value = 0M

        if Decimal.TryParse(
            text,
            NumberStyles.AllowLeadingSign
            ||| NumberStyles.AllowDecimalPoint
            ||| NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture,
            &value
           )
           && value >= 0M
           && value = Decimal.Truncate value
           && value <= decimal Int64.MaxValue then
            Some(int64 value)
        else
            None

    /// Requires exactly one matching completed-settlement series for each production metric.
    let private parseCompletedSettlementSamples (scrape: string) =
        let values = Dictionary<string, ResizeArray<int64>>(StringComparer.Ordinal)
        values[messageMetricName] <- ResizeArray<int64>()
        values[durationMetricName] <- ResizeArray<int64>()
        let errors = ResizeArray<string>()

        scrape.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.iter (fun rawLine ->
            let line = rawLine.Trim()

            if not (line.StartsWith("#", StringComparison.Ordinal)) then
                let sampleMatch = samplePattern.Match(line)

                if sampleMatch.Success then
                    let metricName = sampleMatch.Groups["name"].Value

                    if values.ContainsKey metricName then
                        match tryParseLabels sampleMatch.Groups["labels"].Value, tryParseValue sampleMatch.Groups["value"].Value with
                        | Some labels, Some value ->
                            let labelsMatch =
                                labels.Count = requiredLabels.Count
                                && requiredLabels
                                   |> Seq.forall (fun pair ->
                                       match labels.TryGetValue pair.Key with
                                       | true, actual -> actual.Equals(pair.Value, StringComparison.Ordinal)
                                       | _ -> false)

                            if labelsMatch then
                                values[ metricName ].Add value
                            else
                                errors.Add($"{metricName} contained a non-completed-settlement label set.")
                        | _ -> errors.Add($"{metricName} was malformed.")
                    elif
                        metricName.StartsWith(messageMetricName, StringComparison.Ordinal)
                        || metricName.StartsWith(durationMetricName, StringComparison.Ordinal)
                    then
                        errors.Add("A completed settlement metric used a forbidden suffixed name.")
                elif
                    line.StartsWith(messageMetricName, StringComparison.Ordinal)
                    || line.StartsWith(durationMetricName, StringComparison.Ordinal)
                then
                    errors.Add("An exact settlement metric line was malformed."))

        if errors.Count > 0 then
            Error(String.Join("; ", errors))
        elif values[messageMetricName].Count <> 1 then
            Error($"{messageMetricName} required exactly one sample but found {values[messageMetricName].Count}.")
        elif values[durationMetricName].Count <> 1 then
            Error($"{durationMetricName} required exactly one sample but found {values[durationMetricName].Count}.")
        else
            Ok(values[messageMetricName][0], values[durationMetricName][0])

    /// Captures a freshly restarted process baseline while normalizing only the uninstantiated paired-zero series shape.
    let captureFreshProcessCompletedSettlementBaseline (scrape: string) =
        let hasRelevantSeries =
            scrape.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.exists (fun rawLine ->
                let line = rawLine.Trim()

                if line.StartsWith("#", StringComparison.Ordinal) then
                    false
                else
                    let sampleMatch = samplePattern.Match line

                    if sampleMatch.Success then
                        let metricName = sampleMatch.Groups["name"].Value

                        metricName.StartsWith(messageMetricName, StringComparison.Ordinal)
                        || metricName.StartsWith(durationMetricName, StringComparison.Ordinal)
                    else
                        line.StartsWith(messageMetricName, StringComparison.Ordinal)
                        || line.StartsWith(durationMetricName, StringComparison.Ordinal))

        if not hasRelevantSeries then
            Ok freshProcessZeroBaseline
        else
            match parseCompletedSettlementSamples scrape with
            | Ok (0L, 0L) -> Ok scrape
            | Ok (messages, durations) -> Error($"Fresh-process settlement metrics must both be zero: messages={messages}, durations={durations}.")
            | Error error -> Error error

    /// Evaluates exact cumulative equality while allowing only unchanged or partial deltas to keep waiting.
    let evaluateCompletedSettlementDelta expectedDelta baselineScrape observedScrape =
        match parseCompletedSettlementSamples baselineScrape, parseCompletedSettlementSamples observedScrape with
        | Error error, _ -> Invalid($"Invalid baseline scrape: {error}")
        | _, Error error -> Invalid($"Invalid observed scrape: {error}")
        | Ok (baselineMessages, baselineDurations), Ok (observedMessages, observedDurations) ->
            let messageDelta = observedMessages - baselineMessages
            let durationDelta = observedDurations - baselineDurations

            if messageDelta < 0L || durationDelta < 0L then
                Invalid("A completed settlement metric reset below its baseline.")
            elif messageDelta > expectedDelta
                 || durationDelta > expectedDelta then
                Invalid($"Completed settlement metrics overshot the exact delta {expectedDelta}: messages={messageDelta}, durations={durationDelta}.")
            elif messageDelta = expectedDelta
                 && durationDelta = expectedDelta then
                Complete(messageDelta, durationDelta)
            else
                Pending

/// Validates that every observed Reference-created producer identity is classified exactly once.
module ProducerInventory =

    /// Returns bounded errors for missing, duplicate, or unclassified message identities.
    let validate (expectedMessageIds: string array) (observedMessageIds: string array) =
        let errors = ResizeArray<string>()
        let expected = HashSet<string>(expectedMessageIds, StringComparer.Ordinal)
        let observed = HashSet<string>(observedMessageIds, StringComparer.Ordinal)

        if expected.Count <> expectedMessageIds.Length then
            errors.Add("Expected producer inventory contains duplicate identities.")

        observedMessageIds
        |> Array.countBy id
        |> Array.filter (fun (_, count) -> count > 1)
        |> Array.iter (fun (messageId, count) -> errors.Add($"Observed producer inventory contains duplicate delivery '{messageId}' with count {count}."))

        expected
        |> Seq.filter (observed.Contains >> not)
        |> Seq.iter (fun messageId -> errors.Add($"Missing expected Reference-created envelope '{messageId}'."))

        observed
        |> Seq.filter (expected.Contains >> not)
        |> Seq.iter (fun messageId -> errors.Add($"Unclassified Reference-created envelope '{messageId}'."))

        errors.ToArray()

/// Defines the deterministic proof contract for one replay after a real Grace.Server restart.
module ServerRestart =

    /// Returns whether retained state or health text positively identifies a non-ready Grace.Server observation.
    let isAffirmativeNonReady resourceState healthStatus =
        let resourceStateIsKnown =
            not (String.IsNullOrWhiteSpace resourceState)
            && not (String.Equals(resourceState, "Unknown", StringComparison.OrdinalIgnoreCase))

        let healthStatusIsKnown =
            not (String.IsNullOrWhiteSpace healthStatus)
            && not (String.Equals(healthStatus, "Unknown", StringComparison.OrdinalIgnoreCase))

        (resourceStateIsKnown
         && not (String.Equals(resourceState, "Running", StringComparison.Ordinal)))
        || (healthStatusIsKnown
            && not (String.Equals(healthStatus, "Healthy", StringComparison.Ordinal)))

    /// Lists the exact assertion identities required by the server-restart replay witness.
    let requiredAssertionIds =
        [|
            "server-restart.seed-deliveries-completed"
            "server-restart.command-completed"
            "server-restart.fresh-health"
            "server-restart.http-ready"
            "server-restart.replay-message-delta"
            "server-restart.replay-duration-delta"
            "server-restart.reference-root-state-unchanged"
            "server-restart.manifest-state-unchanged"
            "server-restart.logical-state-unchanged"
            "server-restart.workflow-state-unchanged"
            "server-restart.physical-state-unchanged"
            "server-restart.evidence-integrity"
        |]

    /// Requires a completed restart command, retained non-ready transition, fresh Healthy event, and bounded HTTP readiness in strict order.
    let validateFreshReadiness
        commandCompleted
        (commandStartedAt: DateTimeOffset)
        (commandCompletedAt: DateTimeOffset)
        (nonReadyEventObservedAt: DateTimeOffset)
        nonReadyResourceState
        nonReadyHealthStatus
        (resourceEventObservedAt: DateTimeOffset)
        resourceState
        (httpReadyObservedAt: DateTimeOffset)
        httpReady
        =
        let errors = ResizeArray<string>()

        if not commandCompleted then
            errors.Add("The Grace.Server restart command did not complete successfully.")

        if commandCompletedAt < commandStartedAt then
            errors.Add("Grace.Server restart command completion preceded its start.")

        if nonReadyEventObservedAt <= commandCompletedAt then
            errors.Add("The Grace.Server non-ready transition was not observed after restart command completion.")

        if not (isAffirmativeNonReady nonReadyResourceState nonReadyHealthStatus) then
            errors.Add("The retained Grace.Server transition did not demonstrate a non-ready state.")

        if resourceEventObservedAt <= nonReadyEventObservedAt then
            errors.Add("The fresh Grace.Server Healthy event did not follow the retained non-ready transition.")

        if not (String.Equals(resourceState, "Healthy", StringComparison.Ordinal)) then
            errors.Add($"The fresh Grace.Server resource event was not Healthy: {resourceState}.")

        if httpReadyObservedAt <= resourceEventObservedAt then
            errors.Add("Grace.Server HTTP readiness did not follow the fresh Healthy resource event.")

        if not httpReady then
            errors.Add("Grace.Server HTTP readiness failed after the fresh Healthy resource event.")

        errors.ToArray()

    /// Requires one exact observed replay identity plus one completed message and duration settlement observation.
    let validateReplayCompletion expectedMessageId observedMessageIds messageDelta durationDelta settlementCompleted =
        let errors = ResizeArray<string>()

        ProducerInventory.validate [| expectedMessageId |] observedMessageIds
        |> errors.AddRange

        if messageDelta <> 1L then
            errors.Add($"The replay completed message delta required 1 but observed {messageDelta}.")

        if durationDelta <> 1L then
            errors.Add($"The replay completed duration delta required 1 but observed {durationDelta}.")

        if not settlementCompleted then
            errors.Add("The replay settlement failed or did not reach terminal completion.")

        errors.ToArray()

/// Reports whether the bounded producer-inventory drain is still receiving, complete, or failed.
type ProducerInventoryDrainStatus =
    | Receiving
    | Complete
    | Failed

/// Retains the classified Reference-created identities and quiet-window progress for one inventory drain.
type ProducerInventoryDrainState = private { ObservedMessageIds: string array; ConsecutiveEmptyWindows: int; IsComplete: bool; Failure: string option }

/// Advances the deterministic producer-inventory protocol without depending on Service Bus or Aspire.
module ProducerInventoryDrain =

    let private surplusErrors (expectedMessageIds: string array) (observedMessageIds: string array) =
        let errors = ResizeArray<string>()
        let expected = HashSet<string>(expectedMessageIds, StringComparer.Ordinal)
        let observed = HashSet<string>(observedMessageIds, StringComparer.Ordinal)

        if observed.Count <> observedMessageIds.Length then
            errors.Add("Observed producer inventory contains duplicate deliveries.")

        observed
        |> Seq.filter (expected.Contains >> not)
        |> Seq.iter (fun messageId -> errors.Add($"Unclassified Reference-created envelope '{messageId}'."))

        errors.ToArray()

    /// Starts an empty producer inventory that has not observed the expected set.
    let start = { ObservedMessageIds = Array.empty; ConsecutiveEmptyWindows = 0; IsComplete = false; Failure = None }

    /// Returns the externally observable terminal state of the drain.
    let status state =
        match state.Failure, state.IsComplete with
        | Some _, _ -> ProducerInventoryDrainStatus.Failed
        | None, true -> ProducerInventoryDrainStatus.Complete
        | None, false -> ProducerInventoryDrainStatus.Receiving

    /// Returns every Reference-created identity consumed before the current terminal or receiving state.
    let observedMessageIds state = Array.copy state.ObservedMessageIds

    /// Returns the terminal failure detail, or an empty string while the drain has not failed.
    let failure state = state.Failure |> Option.defaultValue String.Empty

    let private fail detail state =
        if state.IsComplete || state.Failure.IsSome then
            state
        else
            { state with Failure = Some detail }

    /// Fails a still-active drain when the shared inventory deadline expires.
    let deadlineExpired state = fail "The producer inventory deadline expired." state

    /// Fails a still-active drain when its caller cancels broker observation.
    let cancelled state = fail "The producer inventory receive was cancelled." state

    /// Fails a still-active drain when the broker receive operation rejects the window.
    let receiveFailed detail state = fail $"The producer inventory receive failed: {detail}" state

    /// Fails a still-active drain when its terminal evidence cannot be written.
    let evidenceWriteFailed detail state = fail $"The producer inventory evidence write failed: {detail}" state

    /// Records one nonempty broker batch and resets quiet progress even when it contains no Reference-created identity.
    let receiveBatch (expectedMessageIds: string array) (referenceMessageIds: string array) state =
        if state.IsComplete || state.Failure.IsSome then
            state
        else
            let observedMessageIds = Array.append state.ObservedMessageIds referenceMessageIds
            let errors = surplusErrors expectedMessageIds observedMessageIds

            { state with
                ObservedMessageIds = observedMessageIds
                ConsecutiveEmptyWindows = 0
                Failure = if Array.isEmpty errors then None else Some(String.Join("; ", errors))
            }

    /// Records one empty broker receive window and completes only after two quiet windows follow the exact expected set.
    let emptyWindow expectedMessageIds state =
        if state.IsComplete || state.Failure.IsSome then
            state
        else
            let exactSetObserved =
                ProducerInventory.validate expectedMessageIds state.ObservedMessageIds
                |> Array.isEmpty

            if exactSetObserved then
                let consecutiveEmptyWindows = state.ConsecutiveEmptyWindows + 1

                { state with ConsecutiveEmptyWindows = consecutiveEmptyWindows; IsComplete = consecutiveEmptyWindows >= 2 }
            else
                { state with ConsecutiveEmptyWindows = 0 }

/// Writes bounded complete records to one retained UTF-8-without-BOM NDJSON evidence file.
type EvidenceWriter(directory: string, maximumRecordBytes: int) =
    let syncRoot = obj ()
    let utf8 = UTF8Encoding(false)
    let path = Path.Combine(directory, "evidence.ndjson")
    let serializerOptions = JsonSerializerOptions(PropertyNamingPolicy = null, WriteIndented = false)

    do
        if String.IsNullOrWhiteSpace directory then
            invalidArg (nameof directory) "An evidence directory is required."

        if maximumRecordBytes <= 0 then
            invalidArg (nameof maximumRecordBytes) "The maximum record size must be positive."

        Directory.CreateDirectory(directory) |> ignore
        use stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)
        stream.Flush(true)

    /// Gets the retained NDJSON evidence path.
    member _.Path = path

    /// Appends one complete bounded JSON line under the writer's single-record lock.
    member _.Append<'T>(record: 'T) =
        let jsonBytes = JsonSerializer.SerializeToUtf8Bytes(record, serializerOptions)

        if jsonBytes.Length > maximumRecordBytes then
            raise (InvalidDataException($"Evidence record size {jsonBytes.Length} exceeds the maximum {maximumRecordBytes} bytes."))

        let lineBytes = Array.append jsonBytes [| byte '\n' |]

        lock syncRoot (fun () ->
            use stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
            stream.Write(lineBytes, 0, lineBytes.Length)
            stream.Flush(true))

    interface IDisposable with
        member _.Dispose() = ()
