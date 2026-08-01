namespace Grace.Server.Tests.Measurement

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Globalization
open System.Text.RegularExpressions
open System.Text
open System.Text.Json

/// Declares one scenario's canonical dependency and mutation position in the grouped run.
type GroupedScenarioPlan = { ScenarioId: string; DependsOn: string array; MutatesLifecycleOrBroker: bool }

/// Captures the composition-visible outcome without redefining any leaf scenario evidence.
type GroupedScenarioResult =
    {
        ScenarioId: string
        Outcome: string
        AssertionIds: string array
        RepositoryId: string
        IdentityIds: string array
        CleanupSucceeded: bool
        SideEffectsStarted: bool
        FailureReason: string
    }

/// Selects whether a scenario may execute or must be emitted as a side-effect-free skip.
type GroupedScenarioDecision =
    | Execute
    | Skip of string

/// Captures exact grouped-run metadata that must agree with the executing clean commit.
[<CLIMutable>]
type GroupedRunMetadata =
    {
        CommitSha: string
        Branch: string
        Dirty: bool
        Command: string
        DotnetVersion: string
        DockerVersion: string
        ScenarioIds: string array
    }

/// Captures one final artifact digest for exact packet verification.
[<CLIMutable>]
type GroupedArtifactHash = { FileName: string; Sha256: string }

/// Defines the canonical Issue 763 scenario composition without introducing runtime or product behavior.
module GroupedMeasurement =

    /// Lists the eight accepted leaf scenarios in their required execution and evidence order.
    let scenarioIds =
        [|
            "baseline"
            "hot-manifest"
            "highly-shared"
            "duplicate-backlog"
            "redis-restart"
            "server-restart"
            "repair"
            "dead-letter"
        |]

    /// Lists the canonical dependency chain that prevents later mutations from running after contamination.
    let scenarioPlan =
        scenarioIds
        |> Array.mapi (fun index scenarioId ->
            {
                ScenarioId = scenarioId
                DependsOn = if index = 0 then Array.empty else [| scenarioIds[index - 1] |]
                MutatesLifecycleOrBroker =
                    scenarioId = "duplicate-backlog"
                    || scenarioId = "redis-restart"
                    || scenarioId = "server-restart"
                    || scenarioId = "repair"
                    || scenarioId = "dead-letter"
            })

    /// Lists the exact Issue 763 grouped assertion identifiers accepted by the final coverage audit.
    let groupedAssertionIds =
        [|
            "grouped.exact-epic-head-sha"
            "grouped.canonical-plan-order"
            "grouped.required-scenario-outcomes"
            "grouped.required-assertion-id-coverage"
            "grouped.no-unknown-assertion-ids"
            "grouped.records-bounded"
            "grouped.records-parseable"
            "grouped.cross-scenario-identity-isolation"
            "grouped.lifecycle-dependency-propagation"
            "grouped.local-vs-azure-claim-boundary"
            "grouped.artifact-hashes"
        |]

    /// Lists every immutable leaf assertion identifier in canonical scenario order.
    let leafAssertionIds =
        Array.concat [| Baseline.requiredAssertionIds
                        HotManifest.requiredAssertionIds
                        HighlySharedDirectoryVersion.requiredAssertionIds
                        DuplicateBacklog.requiredAssertionIds
                        RedisRestart.requiredAssertionIds
                        ServerRestart.requiredAssertionIds
                        Repair.requiredAssertionIds
                        DeadLetter.requiredAssertionIds |]

    /// Lists the exact unique assertion closure required by the final raw packet.
    let requiredAssertionIds = Array.append leafAssertionIds groupedAssertionIds

    /// Returns the immutable assertion closure owned by one canonical leaf scenario.
    let requiredAssertionsForScenario scenarioId =
        match scenarioId with
        | "baseline" -> Baseline.requiredAssertionIds
        | "hot-manifest" -> HotManifest.requiredAssertionIds
        | "highly-shared" -> HighlySharedDirectoryVersion.requiredAssertionIds
        | "duplicate-backlog" -> DuplicateBacklog.requiredAssertionIds
        | "redis-restart" -> RedisRestart.requiredAssertionIds
        | "server-restart" -> ServerRestart.requiredAssertionIds
        | "repair" -> Repair.requiredAssertionIds
        | "dead-letter" -> DeadLetter.requiredAssertionIds
        | value -> invalidArg (nameof scenarioId) $"Unknown grouped scenario '{value}'."

    /// Returns Execute only when every named prerequisite has a Passed result with successful cleanup.
    let decideScenario (priorResults: GroupedScenarioResult array) scenario =
        let priorById = Dictionary<string, GroupedScenarioResult>(StringComparer.Ordinal)

        priorResults
        |> Array.iter (fun result -> priorById[result.ScenarioId] <- result)

        let failedDependency =
            scenario.DependsOn
            |> Array.tryPick (fun dependency ->
                match priorById.TryGetValue dependency with
                | true, result when
                    result.Outcome = "Passed"
                    && result.CleanupSucceeded
                    ->
                    None
                | true, result -> Some $"prerequisite={dependency}; outcome={result.Outcome}; cleanup={result.CleanupSucceeded}"
                | _ -> Some $"prerequisite={dependency}; outcome=Missing")

        match failedDependency with
        | Some reason -> Skip reason
        | None -> Execute

    /// Executes only runnable scenarios and derives side-effect-free skips for every failed dependency.
    let compose (execute: GroupedScenarioPlan -> GroupedScenarioResult) =
        let results = ResizeArray<GroupedScenarioResult>()

        scenarioPlan
        |> Array.iter (fun scenario ->
            match decideScenario (results.ToArray()) scenario with
            | Execute -> results.Add(execute scenario)
            | Skip reason ->
                results.Add(
                    {
                        ScenarioId = scenario.ScenarioId
                        Outcome = "Skipped"
                        AssertionIds = Array.empty
                        RepositoryId = String.Empty
                        IdentityIds = Array.empty
                        CleanupSucceeded = false
                        SideEffectsStarted = false
                        FailureReason = reason
                    }
                ))

        results.ToArray()

    /// Completes the canonical outcome ledger from observed execution without dropping failures or dependent skips.
    let materializeResults (observedResults: GroupedScenarioResult array) =
        let observedByScenario = Dictionary<string, ResizeArray<GroupedScenarioResult>>(StringComparer.Ordinal)

        observedResults
        |> Array.iter (fun result ->
            match observedByScenario.TryGetValue result.ScenarioId with
            | true, values -> values.Add result
            | _ ->
                let values = ResizeArray<GroupedScenarioResult>()
                values.Add result
                observedByScenario[result.ScenarioId] <- values)

        let results = ResizeArray<GroupedScenarioResult>()

        scenarioPlan
        |> Array.iter (fun scenario ->
            match decideScenario (results.ToArray()) scenario with
            | Skip reason ->
                results.Add(
                    {
                        ScenarioId = scenario.ScenarioId
                        Outcome = "Skipped"
                        AssertionIds = Array.empty
                        RepositoryId = String.Empty
                        IdentityIds = Array.empty
                        CleanupSucceeded = false
                        SideEffectsStarted = false
                        FailureReason = reason
                    }
                )
            | Execute ->
                match observedByScenario.TryGetValue scenario.ScenarioId with
                | true, values when values.Count = 1 -> results.Add values[0]
                | true, values ->
                    results.Add(
                        {
                            ScenarioId = scenario.ScenarioId
                            Outcome = "Failed"
                            AssertionIds = Array.empty
                            RepositoryId = String.Empty
                            IdentityIds = Array.empty
                            CleanupSucceeded = false
                            SideEffectsStarted = true
                            FailureReason = $"scenario={scenario.ScenarioId}; duplicate-results={values.Count}"
                        }
                    )
                | _ ->
                    results.Add(
                        {
                            ScenarioId = scenario.ScenarioId
                            Outcome = "Failed"
                            AssertionIds = Array.empty
                            RepositoryId = String.Empty
                            IdentityIds = Array.empty
                            CleanupSucceeded = false
                            SideEffectsStarted = false
                            FailureReason = $"scenario={scenario.ScenarioId}; outcome=Missing"
                        }
                    ))

        results.ToArray()

    /// Retains accepted leaf summaries and derives truthful failed or skipped summaries for every missing outcome.
    let completeSummaries runId (results: GroupedScenarioResult array) (observedSummaries: ScenarioSummary array) =
        results
        |> Array.map (fun result ->
            observedSummaries
            |> Array.tryFind (fun summary ->
                summary.ScenarioId = result.ScenarioId
                && summary.Outcome = result.Outcome)
            |> Option.defaultWith (fun () ->
                let failures =
                    if result.Outcome = "Failed" then
                        [|
                            if String.IsNullOrWhiteSpace result.FailureReason then
                                $"scenario={result.ScenarioId}; outcome=Failed"
                            else
                                result.FailureReason
                        |]
                    else
                        Array.empty

                ScenarioSummary.derive
                    runId
                    result.ScenarioId
                    (requiredAssertionsForScenario result.ScenarioId)
                    Array.empty
                    failures
                    (result.Outcome = "Skipped")))

    /// Creates one grouped assertion whose outcome is derived only from the supplied audit errors.
    let auditAssertion runId assertionId (errors: string array) successDetail =
        let detail = if Array.isEmpty errors then successDetail else String.Join("; ", errors)
        MeasurementAssertion.Create(runId, "grouped", assertionId, Array.isEmpty errors, detail)

    /// Rejects any failed-to-skipped chain that starts side effects after a prerequisite stops passing.
    let auditLifecycleDependencyPropagation (results: GroupedScenarioResult array) =
        let errors = ResizeArray<string>()

        if results.Length <> scenarioIds.Length then
            errors.Add($"outcome-count={results.Length}; expected={scenarioIds.Length}")
        else
            results
            |> Array.iteri (fun index result ->
                if result.ScenarioId <> scenarioIds[index] then
                    errors.Add($"index={index}; scenario={result.ScenarioId}; expected={scenarioIds[index]}")

                if index > 0
                   && results[index - 1].Outcome <> "Passed" then
                    if result.Outcome <> "Skipped" then
                        errors.Add($"scenario={result.ScenarioId}; expected=Skipped; actual={result.Outcome}")

                    if result.SideEffectsStarted then
                        errors.Add($"scenario={result.ScenarioId}; skipped-side-effects=true"))

        errors.ToArray()

    /// Requires raw cumulative completed-settlement baselines and terminal observations for every canonical stimulus phase.
    let auditRawMetricSnapshots (samples: MeasurementSample array) =
        let errors = ResizeArray<string>()

        let metricNames =
            [|
                "grace_manifest_contribution_messages_total"
                "grace_manifest_contribution_processing_duration_milliseconds_count"
            |]

        scenarioIds
        |> Array.iter (fun scenarioId ->
            [| "baseline"; "terminal" |]
            |> Array.iter (fun observation ->
                metricNames
                |> Array.iter (fun metricName ->
                    let matches =
                        samples
                        |> Array.filter (fun sample ->
                            sample.ScenarioId = scenarioId
                            && sample.Name = metricName
                            && sample.Labels.TryGetValue("stage") = (true, "settle")
                            && sample.Labels.TryGetValue("outcome") = (true, "completed")
                            && sample.Labels.TryGetValue("phase") = (true, "stimulus")
                            && sample.Labels.TryGetValue("observation") = (true, observation))

                    if matches.Length <> 1 then
                        errors.Add($"scenario={scenarioId}; phase=stimulus; observation={observation}; metric={metricName}; count={matches.Length}"))))

        errors.ToArray()

    /// Captures the two exact cumulative metric values after the accepted strict parser validates the scrape.
    let captureCompletedSettlementSnapshot scrape =
        match OpenMetrics.evaluateCompletedSettlementDelta 0L scrape scrape with
        | DeltaEvaluation.Invalid reason -> Error reason
        | DeltaEvaluation.Pending -> Error "A scrape could not validate against itself."
        | DeltaEvaluation.Complete _ ->
            let capture metricName =
                let pattern = $"(?m)^{Regex.Escape(metricName)}(?:\\{{[^\\r\\n]*\\}})?\\s+(?<value>[^\\s]+)"
                let matched = Regex.Match(scrape, pattern, RegexOptions.CultureInvariant)
                let mutable value = 0M

                if matched.Success
                   && Decimal.TryParse(
                       matched.Groups["value"].Value,
                       NumberStyles.AllowLeadingSign
                       ||| NumberStyles.AllowDecimalPoint
                       ||| NumberStyles.AllowExponent,
                       CultureInfo.InvariantCulture,
                       &value
                   )
                   && value = Decimal.Truncate value
                   && value >= 0M
                   && value <= decimal Int64.MaxValue then
                    Ok(int64 value)
                else
                    Error $"The validated metric '{metricName}' could not be captured as an integer."

            match capture "grace_manifest_contribution_messages_total", capture "grace_manifest_contribution_processing_duration_milliseconds_count" with
            | Ok messages, Ok durations -> Ok(messages, durations)
            | Error error, _
            | _, Error error -> Error error

    /// Rejects missing, duplicate, or unknown assertion identities against the exact final closure.
    let auditAssertionIds (actualAssertionIds: string array) =
        let required = HashSet<string>(requiredAssertionIds, StringComparer.Ordinal)
        let seen = HashSet<string>(StringComparer.Ordinal)
        let errors = ResizeArray<string>()

        actualAssertionIds
        |> Array.iter (fun assertionId ->
            if not (required.Contains assertionId) then errors.Add($"unknown={assertionId}")
            elif not (seen.Add assertionId) then errors.Add($"duplicate={assertionId}"))

        requiredAssertionIds
        |> Array.iter (fun assertionId -> if not (seen.Contains assertionId) then errors.Add($"missing={assertionId}"))

        errors.ToArray()

    /// Rejects missing, reordered, failed, skipped, or cleanup-incomplete scenario outcomes.
    let auditScenarioOutcomes (results: GroupedScenarioResult array) =
        let errors = ResizeArray<string>()

        if results
           |> Array.map (fun result -> result.ScenarioId)
           <> scenarioIds then
            errors.Add("Scenario outcomes do not match canonical order.")

        results
        |> Array.iter (fun result ->
            if result.Outcome <> "Passed" then
                errors.Add($"scenario={result.ScenarioId}; outcome={result.Outcome}")

            if not result.CleanupSucceeded then
                errors.Add($"scenario={result.ScenarioId}; cleanup=false"))

        errors.ToArray()

    /// Rejects any shared Repository or retained identity across executed leaf scenarios.
    let auditScenarioIsolation (results: GroupedScenarioResult array) =
        let errors = ResizeArray<string>()
        let repositories = HashSet<string>(StringComparer.Ordinal)
        let identities = HashSet<string>(StringComparer.Ordinal)

        results
        |> Array.filter (fun result -> result.SideEffectsStarted)
        |> Array.iter (fun result ->
            if
                String.IsNullOrWhiteSpace result.RepositoryId
                || not (repositories.Add result.RepositoryId)
            then
                errors.Add($"repository={result.RepositoryId}; scenario={result.ScenarioId}")

            result.IdentityIds
            |> Array.iter (fun identity ->
                if
                    String.IsNullOrWhiteSpace identity
                    || not (identities.Add identity)
                then
                    errors.Add($"identity={identity}; scenario={result.ScenarioId}")))

        errors.ToArray()

    /// Requires exact clean-head, branch, command, runtime-version, and canonical-plan metadata.
    let auditMetadata expectedSha expectedBranch metadata =
        let errors = ResizeArray<string>()

        if not (String.Equals(metadata.CommitSha, expectedSha, StringComparison.OrdinalIgnoreCase)) then
            errors.Add("Commit SHA does not match the executing Epic head.")

        if not (String.Equals(metadata.Branch, expectedBranch, StringComparison.Ordinal)) then
            errors.Add("Branch does not match the executing grouped branch.")

        if metadata.Dirty then errors.Add("Grouped measurement worktree is dirty.")

        if String.IsNullOrWhiteSpace metadata.Command then
            errors.Add("Focused command is missing.")

        if String.IsNullOrWhiteSpace metadata.DotnetVersion then
            errors.Add(".NET version is missing.")

        if String.IsNullOrWhiteSpace metadata.DockerVersion then
            errors.Add("Docker version is missing.")

        if metadata.ScenarioIds <> scenarioIds then
            errors.Add("Metadata scenario plan is not canonical.")

        errors.ToArray()

    /// Names the local evidence claims the grouped packet may make.
    let localClaims =
        [|
            "direct-cosmos-request-charge"
            "local-response-latency"
            "local-partition-key-concentration"
            "local-emulator-broker-delivery"
            "local-redis-reconnect"
        |]

    /// Names the Azure-only claims the local grouped packet must explicitly decline.
    let azureOnlyClaims =
        [|
            "complete-orleans-persistence-request-units"
            "azure-production-partition-heat-or-throttling"
            "cross-region-failover-or-availability"
            "production-slos"
        |]

    /// Rejects missing, duplicated, or overlapping local and Azure-only claim declarations.
    let auditClaimBoundary (actualLocal: string array) (actualAzureOnly: string array) =
        let expectedLocal = HashSet<string>(localClaims, StringComparer.Ordinal)
        let expectedAzure = HashSet<string>(azureOnlyClaims, StringComparer.Ordinal)
        let local = HashSet<string>(actualLocal, StringComparer.Ordinal)
        let azure = HashSet<string>(actualAzureOnly, StringComparer.Ordinal)
        let errors = ResizeArray<string>()

        if
            actualLocal.Length <> local.Count
            || not (local.SetEquals expectedLocal)
        then
            errors.Add("Local claim set is not exact.")

        if
            actualAzureOnly.Length <> azure.Count
            || not (azure.SetEquals expectedAzure)
        then
            errors.Add("Azure-only claim set is not exact.")

        if local.Overlaps azure then errors.Add("Local and Azure-only claims overlap.")
        errors.ToArray()

    /// Parses every nonempty NDJSON line and rejects serialized records above the declared byte bound.
    let auditNdjson (maximumRecordBytes: int) (path: string) =
        let errors = ResizeArray<string>()

        if maximumRecordBytes <= 0 then
            invalidArg (nameof maximumRecordBytes) "A positive record bound is required."

        if not (File.Exists path) then
            errors.Add($"missing-file={Path.GetFileName path}")
        else
            File.ReadAllLines path
            |> Array.iteri (fun index line ->
                if String.IsNullOrWhiteSpace line then
                    errors.Add($"blank-line={index + 1}")
                else
                    if Encoding.UTF8.GetByteCount line > maximumRecordBytes then
                        errors.Add($"oversized-line={index + 1}")

                    try
                        use _ = JsonDocument.Parse line
                        ()
                    with
                    | :? JsonException -> errors.Add($"malformed-line={index + 1}"))

        errors.ToArray()

    /// Computes lowercase SHA-256 hashes for the exact supplied artifact paths in caller order.
    let artifactHashes (paths: string array) =
        paths
        |> Array.map (fun path ->
            let bytes = File.ReadAllBytes path

            {
                FileName = Path.GetFileName path
                Sha256 =
                    SHA256.HashData(bytes)
                    |> Convert.ToHexString
                    |> fun value -> value.ToLowerInvariant()
            })

    /// Rejects missing, duplicate, unknown, or mismatched final artifact hashes.
    let auditArtifactHashes (paths: string array) (recorded: GroupedArtifactHash array) =
        let expected = artifactHashes paths
        let errors = ResizeArray<string>()

        if recorded.Length
           <> (recorded
               |> Array.distinctBy (fun item -> item.FileName))
               .Length then
            errors.Add("Duplicate artifact hash name.")

        if expected <> recorded then
            errors.Add("Artifact hashes do not match packet bytes.")

        errors.ToArray()
