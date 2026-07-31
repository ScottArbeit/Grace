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

    /// Requires successful restart command execution followed by a fresh Healthy event and bounded HTTP readiness.
    let validateFreshReadiness
        commandCompleted
        (commandStartedAt: DateTimeOffset)
        (resourceEventObservedAt: DateTimeOffset)
        resourceState
        (httpReadyObservedAt: DateTimeOffset)
        httpReady
        =
        let errors = ResizeArray<string>()

        if not commandCompleted then
            errors.Add("The Grace.Server restart command did not complete successfully.")

        if resourceEventObservedAt <= commandStartedAt then
            errors.Add("Grace.Server health was not observed after the restart command began.")

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
