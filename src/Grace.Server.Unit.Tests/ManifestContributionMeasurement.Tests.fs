namespace Grace.Server.Unit.Tests

open Grace.Server.Tests.Measurement
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks

/// Proves the pure Baseline measurement and evidence contracts without starting Aspire.
[<Parallelizable(ParallelScope.All)>]
type ManifestContributionMeasurementTests() =

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

    let duplicateBacklogRequiredAssertionIds =
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

    let completedMetrics messages durations =
        $"""
# TYPE grace_manifest_contribution_messages_total counter
grace_manifest_contribution_messages_total{{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"}} {messages}
grace_manifest_contribution_processing_duration_milliseconds_count{{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"}} {durations}
"""

    /// Creates one passing assertion with the supplied identifier.
    let passingAssertion assertionId = MeasurementAssertion.Create("run-1", "baseline", assertionId, true, "proved")

    /// Computes the independently expected digest for one retained diagnostic source.
    let sha256 (value: string) =
        let bytes = Encoding.UTF8.GetBytes value
        let digest = SHA256.HashData bytes
        Convert.ToHexString(digest).ToLowerInvariant()

    /// Verifies the Baseline assertion contract is an exact stable set.
    [<Test>]
    member _.RequiredAssertionIdentifiersAreExact() = Assert.That(Baseline.requiredAssertionIds = requiredAssertionIds, Is.True)

    /// Verifies the duplicate-backlog assertion contract is an exact stable set.
    [<Test>]
    member _.DuplicateBacklogRequiredAssertionIdentifiersAreExact() =
        Assert.That(DuplicateBacklog.requiredAssertionIds = duplicateBacklogRequiredAssertionIds, Is.True)

    /// Verifies a stop barrier requires every seed identity plus independent durable and delivery terminal signals.
    [<Test>]
    member _.DuplicateBacklogStopBarrierRejectsUnsettledSeedDelivery() =
        let expected =
            [|
                "Reference/one/Created"
                "Reference/two/Created"
            |]

        let missingIdentity = DuplicateBacklog.validatePreStopBarrier expected [| expected[0] |] true true

        let incompleteDelivery = DuplicateBacklog.validatePreStopBarrier expected expected false true

        let unconvergedDurableState = DuplicateBacklog.validatePreStopBarrier expected expected true false

        Assert.That(missingIdentity, Is.Not.Empty)
        Assert.That(incompleteDelivery, Has.One.Items)
        Assert.That(incompleteDelivery[0], Does.Contain("completion"))
        Assert.That(unconvergedDurableState, Has.One.Items)
        Assert.That(unconvergedDurableState[0], Does.Contain("converged"))

    /// Verifies stopped-server visibility supports one and multiple selected replays without vacuous empty-peek success.
    [<Test>]
    member _.DuplicateBacklogVisibilityRequiresEverySelectedIdentityAtLeastOnce() =
        let one = [| "Reference/one/Created" |]

        let selected =
            [|
                "Reference/one/Created"
                "Reference/two/Created"
                "Reference/three/Created"
            |]

        Assert.That(DuplicateBacklog.validateStoppedBacklogVisibility one one, Is.Empty)

        Assert.That(
            DuplicateBacklog.validateStoppedBacklogVisibility
                selected
                [|
                    "unrelated/test-event"
                    selected[2]
                    selected[0]
                    selected[1]
                    selected[0]
                |],
            Is.Empty
        )

        Assert.That(DuplicateBacklog.validateStoppedBacklogVisibility selected Array.empty, Is.Not.Empty)
        Assert.That(DuplicateBacklog.validateStoppedBacklogVisibility selected [| selected[0]; selected[1] |], Is.Not.Empty)

    /// Verifies stale health or failed HTTP readiness cannot satisfy a post-start readiness gate.
    [<Test>]
    member _.DuplicateBacklogFreshReadinessRejectsStaleHealthAndFailedHttp() =
        let commandStartedAt = DateTimeOffset.Parse("2026-07-31T08:00:00Z")
        let staleHealth = commandStartedAt.AddMilliseconds(-1.0)
        let freshHealth = commandStartedAt.AddMilliseconds(1.0)

        Assert.That(DuplicateBacklog.validateFreshServerReadiness commandStartedAt freshHealth true, Is.Empty)
        Assert.That(DuplicateBacklog.validateFreshServerReadiness commandStartedAt staleHealth true, Is.Not.Empty)
        Assert.That(DuplicateBacklog.validateFreshServerReadiness commandStartedAt freshHealth false, Is.Not.Empty)

    /// Verifies byte-equivalent durable state cannot substitute for completed replay settlement while duplicates remain queued.
    [<Test>]
    member _.DuplicateBacklogUnchangedStateCannotSatisfyQueuedReplay() =
        let baseline = completedMetrics 7 7
        let unchangedDurableState = true
        let replayCompletion = OpenMetrics.evaluateCompletedSettlementDelta 3L baseline baseline

        Assert.That(unchangedDurableState, Is.True)
        Assert.That(replayCompletion, Is.EqualTo(DeltaEvaluation.Pending))

    /// Verifies exact production settlement samples complete only at the requested cumulative deltas.
    [<Test>]
    member _.ExactCompletedSettlementDeltasPass() =
        let baseline = completedMetrics 7 7
        let observed = completedMetrics 10 10

        Assert.That(OpenMetrics.evaluateCompletedSettlementDelta 3L baseline observed, Is.EqualTo(DeltaEvaluation.Complete(3L, 3L)))

    /// Verifies unchanged and partial observations remain pending until the bounded runtime timeout.
    [<TestCase(7, 7)>]
    [<TestCase(8, 9)>]
    [<TestCase(9, 8)>]
    member _.UnchangedOrPartialSettlementDeltasWait(messages, durations) =
        let baseline = completedMetrics 7 7
        let observed = completedMetrics messages durations

        Assert.That(OpenMetrics.evaluateCompletedSettlementDelta 3L baseline observed, Is.EqualTo(DeltaEvaluation.Pending))

    /// Verifies overshoot, reset, missing, duplicate, suffix, unrelated-label, and settlement-failed samples cannot pass.
    [<Test>]
    member _.InvalidSettlementMetricsFail() =
        let baseline = completedMetrics 7 7

        let invalidScrapes =
            [|
                "overshoot", completedMetrics 11 10
                "reset", completedMetrics 6 10
                "missing",
                "grace_manifest_contribution_messages_total{otel_scope_name=\"Grace.ManifestContributionAccounting\",stage=\"settle\",outcome=\"completed\"} 10"
                "duplicate",
                completedMetrics 10 10
                + Environment.NewLine
                + completedMetrics 10 10
                "suffix",
                """
grace_manifest_contribution_messages_total_created{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 10
grace_manifest_contribution_processing_duration_milliseconds_count_extra{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 10
"""
                "unrelated-label",
                completedMetrics 10 10
                + "grace_manifest_contribution_messages_total{otel_scope_name=\"Grace.ManifestContributionAccounting\",stage=\"settle\",outcome=\"completed\",reference_type=\"Save\"} 10"
                "settlement-failed",
                """
grace_manifest_contribution_messages_total{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="settlement_failed"} 10
grace_manifest_contribution_processing_duration_milliseconds_count{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="settlement_failed"} 10
"""
            |]

        invalidScrapes
        |> Array.iter (fun (name, scrape) ->
            match OpenMetrics.evaluateCompletedSettlementDelta 3L baseline scrape with
            | DeltaEvaluation.Invalid _ -> ()
            | result -> Assert.Fail($"{name} unexpectedly produced {result}."))

    /// Verifies one identity and the selected topology require exact, unique producer envelopes.
    [<Test>]
    member _.ProducerInventoryRequiresExactUniqueIdentities() =
        Assert.That(
            ProducerInventory.validate [| "Reference/one/Created" |] [|
                "Reference/one/Created"
            |],
            Is.Empty
        )

        let selected =
            [|
                "Reference/one/Created"
                "Reference/two/Created"
                "Reference/three/Created"
            |]

        Assert.That(ProducerInventory.validate selected selected, Is.Empty)
        Assert.That(ProducerInventory.validate selected [| selected[0]; selected[1] |], Is.Not.Empty)

        Assert.That(
            ProducerInventory.validate
                selected
                [|
                    yield! selected
                    "Reference/unclassified/Created"
                |],
            Is.Not.Empty
        )

        Assert.That(
            ProducerInventory.validate
                selected
                [|
                    selected[0]
                    selected[0]
                    selected[1]
                    selected[2]
                |],
            Is.Not.Empty
        )

    /// Verifies an exact producer set becomes terminal only after two consecutive quiet receive windows.
    [<Test>]
    member _.ExactProducerInventoryRequiresTwoQuietWindows() =
        let expected = [| "Reference/one/Created" |]

        let observed =
            ProducerInventoryDrain.start
            |> ProducerInventoryDrain.receiveBatch expected expected

        Assert.That(ProducerInventoryDrain.status observed, Is.EqualTo(ProducerInventoryDrainStatus.Receiving))

        let oneQuietWindow =
            observed
            |> ProducerInventoryDrain.emptyWindow expected

        Assert.That(ProducerInventoryDrain.status oneQuietWindow, Is.EqualTo(ProducerInventoryDrainStatus.Receiving))

        let twoQuietWindows =
            oneQuietWindow
            |> ProducerInventoryDrain.emptyWindow expected

        Assert.That(ProducerInventoryDrain.status twoQuietWindows, Is.EqualTo(ProducerInventoryDrainStatus.Complete))

    /// Verifies a duplicate delivered after the expected set is observed fails with the complete received inventory.
    [<Test>]
    member _.LateDuplicateProducerIdentityFailsTruthfully() =
        let expected = [| "Reference/one/Created" |]

        let failed =
            ProducerInventoryDrain.start
            |> ProducerInventoryDrain.receiveBatch expected expected
            |> ProducerInventoryDrain.receiveBatch expected expected

        Assert.That(ProducerInventoryDrain.status failed, Is.EqualTo(ProducerInventoryDrainStatus.Failed))
        Assert.That(ProducerInventoryDrain.observedMessageIds failed = [| expected[0]; expected[0] |], Is.True)
        Assert.That(ProducerInventoryDrain.failure failed, Does.Contain("duplicate"))

    /// Verifies an unclassified identity delivered after the expected set fails with truthful terminal detail.
    [<Test>]
    member _.LateUnclassifiedProducerIdentityFailsTruthfully() =
        let expected = [| "Reference/one/Created" |]

        let failed =
            ProducerInventoryDrain.start
            |> ProducerInventoryDrain.receiveBatch expected expected
            |> ProducerInventoryDrain.receiveBatch expected [| "Reference/unclassified/Created" |]

        Assert.That(ProducerInventoryDrain.status failed, Is.EqualTo(ProducerInventoryDrainStatus.Failed))
        Assert.That(ProducerInventoryDrain.failure failed, Does.Contain("Unclassified"))

    /// Verifies any broker delivery after the first quiet window restarts the two-window drain.
    [<Test>]
    member _.ReceivedBatchAfterFirstQuietWindowResetsDrain() =
        let expected = [| "Reference/one/Created" |]

        let afterReceivedBatch =
            ProducerInventoryDrain.start
            |> ProducerInventoryDrain.receiveBatch expected expected
            |> ProducerInventoryDrain.emptyWindow expected
            |> ProducerInventoryDrain.receiveBatch expected Array.empty

        let oneQuietWindowAfterDelivery =
            afterReceivedBatch
            |> ProducerInventoryDrain.emptyWindow expected

        Assert.That(ProducerInventoryDrain.status oneQuietWindowAfterDelivery, Is.EqualTo(ProducerInventoryDrainStatus.Receiving))

        let twoQuietWindowsAfterDelivery =
            oneQuietWindowAfterDelivery
            |> ProducerInventoryDrain.emptyWindow expected

        Assert.That(ProducerInventoryDrain.status twoQuietWindowsAfterDelivery, Is.EqualTo(ProducerInventoryDrainStatus.Complete))

    /// Verifies expiry after expected-set observation remains a terminal failed evidence outcome.
    [<Test>]
    member _.InventoryDeadlineAfterExpectedSetProducesFailedEvidence() =
        let expected = [| "Reference/one/Created" |]

        let failed =
            ProducerInventoryDrain.start
            |> ProducerInventoryDrain.receiveBatch expected expected
            |> ProducerInventoryDrain.deadlineExpired

        let failure = ProducerInventoryDrain.failure failed
        let assertions = requiredAssertionIds |> Array.map passingAssertion
        let summary = ScenarioSummary.derive "run-1" "baseline" requiredAssertionIds assertions [| failure |] false

        Assert.That(ProducerInventoryDrain.status failed, Is.EqualTo(ProducerInventoryDrainStatus.Failed))
        Assert.That(failure, Does.Contain("deadline"))
        Assert.That(summary.Outcome, Is.EqualTo("Failed"))
        Assert.That(summary.RuntimeFailures, Is.Not.Empty)

    /// Verifies cancellation after expected-set observation remains terminal and cannot yield passing evidence.
    [<Test>]
    member _.InventoryCancellationAfterExpectedSetProducesFailedEvidence() =
        let expected = [| "Reference/one/Created" |]

        let failed =
            ProducerInventoryDrain.start
            |> ProducerInventoryDrain.receiveBatch expected expected
            |> ProducerInventoryDrain.cancelled

        let failure = ProducerInventoryDrain.failure failed
        let assertions = requiredAssertionIds |> Array.map passingAssertion
        let summary = ScenarioSummary.derive "run-1" "baseline" requiredAssertionIds assertions [| failure |] false

        Assert.That(ProducerInventoryDrain.status failed, Is.EqualTo(ProducerInventoryDrainStatus.Failed))
        Assert.That(failure, Does.Contain("cancelled"))
        Assert.That(summary.Outcome, Is.EqualTo("Failed"))
        Assert.That(summary.RuntimeFailures, Is.Not.Empty)

    /// Verifies receive and evidence-write failures during quiet drain remain terminal failed evidence.
    [<Test>]
    member _.QuietDrainInfrastructureFailuresProduceFailedEvidence() =
        let expected = [| "Reference/one/Created" |]

        let observed =
            ProducerInventoryDrain.start
            |> ProducerInventoryDrain.receiveBatch expected expected
            |> ProducerInventoryDrain.emptyWindow expected

        let failures =
            [|
                ProducerInventoryDrain.receiveFailed "broker unavailable" observed
                ProducerInventoryDrain.evidenceWriteFailed "disk full" observed
            |]

        let assertions = requiredAssertionIds |> Array.map passingAssertion

        failures
        |> Array.iter (fun failed ->
            let failure = ProducerInventoryDrain.failure failed
            let summary = ScenarioSummary.derive "run-1" "baseline" requiredAssertionIds assertions [| failure |] false

            Assert.That(ProducerInventoryDrain.status failed, Is.EqualTo(ProducerInventoryDrainStatus.Failed))
            Assert.That(failure, Is.Not.Empty)
            Assert.That(summary.Outcome, Is.EqualTo("Failed"))
            Assert.That(summary.RuntimeFailures, Is.Not.Empty))

    /// Verifies summary success is derived only from the exact unique assertion set and an empty failure ledger.
    [<Test>]
    member _.SummaryPassIsDerivedFromExactAssertions() =
        let assertions = requiredAssertionIds |> Array.map passingAssertion
        let summary = ScenarioSummary.derive "run-1" "baseline" requiredAssertionIds assertions Array.empty false

        Assert.That(summary.Outcome, Is.EqualTo("Passed"))
        Assert.That(summary.PassedAssertionCount, Is.EqualTo(requiredAssertionIds.Length))
        Assert.That(summary.RequiredAssertionCount, Is.EqualTo(requiredAssertionIds.Length))

    /// Verifies false, missing, duplicate, wrong identifiers, and runtime failures cannot produce a passing summary.
    [<Test>]
    member _.SummaryRejectsEveryFalseSuccessShape() =
        let passing = requiredAssertionIds |> Array.map passingAssertion

        let invalidSets =
            [|
                "false",
                passing
                |> Array.mapi (fun index assertion -> if index = 0 then { assertion with Passed = false } else assertion),
                Array.empty
                "missing", passing |> Array.skip 1, Array.empty
                "duplicate", Array.append passing [| passing[0] |], Array.empty
                "wrong-id",
                passing
                |> Array.mapi (fun index assertion ->
                    if index = 0 then
                        { assertion with AssertionId = "baseline.wrong" }
                    else
                        assertion),
                Array.empty
                "runtime-failure", passing, [| "metrics scrape failed" |]
            |]

        invalidSets
        |> Array.iter (fun (name, assertions, failures) ->
            let summary = ScenarioSummary.derive "run-1" "baseline" requiredAssertionIds assertions failures false
            Assert.That(summary.Outcome, Is.EqualTo("Failed"), name))

    /// Verifies a failed prerequisite is represented as Skipped without pretending side effects ran.
    [<Test>]
    member _.FailedPrerequisiteProducesSkippedSummary() =
        let summary = ScenarioSummary.derive "run-1" "baseline" requiredAssertionIds Array.empty Array.empty true

        Assert.That(summary.Outcome, Is.EqualTo("Skipped"))
        Assert.That(summary.PassedAssertionCount, Is.Zero)

    /// Verifies run scenario order comes directly from the executed plan.
    [<Test>]
    member _.RunMetadataPreservesExecutedScenarioOrder() =
        let plan = [| "baseline"; "later-a"; "later-b" |]
        let run = MeasurementRun.Create("run-1", "commit", "worktree", "clean", "command", "evidence", plan)

        Assert.That(run.Scenarios = plan, Is.True)

    /// Verifies an ordinary hosted selector command remains verbatim rather than becoming diagnostic projection text.
    [<Test>]
    member _.OrdinaryHostedCommandRemainsVerbatim() =
        let commandPrefix = "dotnet test --configuration Release --no-build --filter "

        let command =
            commandPrefix
            + String('x', 208 - commandPrefix.Length)

        let run = MeasurementRun.Create("run-1", "commit", "worktree", "clean", command, "evidence", [| "baseline" |])

        Assert.That(command, Has.Length.EqualTo(208))
        Assert.That(run.Command, Is.EqualTo(command))

    /// Verifies large worktree metadata remains writable while retaining a bounded preview and source digest.
    [<Test>]
    member _.LargeWorktreeStateProducesBoundedTruthfulRunMetadata() =
        let directory = Path.Combine(Path.GetTempPath(), $"grace-mca-measurement-{Guid.NewGuid():N}")

        let worktreeState =
            Array.init 2048 (fun index -> $"?? untracked-{index:D4}-{String('x', 48)}.txt")
            |> String.concat Environment.NewLine

        try
            use writer = new EvidenceWriter(directory, 65536)

            let run = MeasurementRun.Create("run-1", "commit", "worktree", worktreeState, "command", directory, [| "baseline" |])

            writer.Append run

            let line = File.ReadAllText(writer.Path).TrimEnd()
            Assert.That(Encoding.UTF8.GetByteCount(line), Is.LessThanOrEqualTo(65536))
            Assert.That(run.WorktreeState, Does.Contain("pathEntryCount=2048"))
            Assert.That(run.WorktreeState, Does.Contain($"sourceUtf8Bytes={Encoding.UTF8.GetByteCount worktreeState}"))
            Assert.That(run.WorktreeState, Does.Contain($"sha256={sha256 worktreeState}"))
            Assert.That(run.WorktreeState, Does.Contain("untracked-0000"))
            Assert.That(run.WorktreeState, Does.Contain("untracked-2047"))
            Assert.That(run.WorktreeState, Does.Not.Contain("untracked-1024"))
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)

    /// Verifies oversized caller-controlled command metadata remains truthful and writable through the real writer.
    [<Test>]
    member _.EscapeHeavyHostedCommandProducesBoundedTruthfulRunMetadata() =
        let directory = Path.Combine(Path.GetTempPath(), $"grace-mca-measurement-{Guid.NewGuid():N}")
        let command = String.replicate 8192 "<>&"

        let worktreeState =
            Array.init 2048 (fun index -> $"?? untracked-{index:D4}-{String('x', 48)}.txt")
            |> String.concat Environment.NewLine

        try
            use writer = new EvidenceWriter(directory, 65536)

            let run = MeasurementRun.Create("run-1", "commit", "worktree", worktreeState, command, directory, [| "baseline" |])

            writer.Append run

            let line = File.ReadAllText(writer.Path).TrimEnd()
            Assert.That(Encoding.UTF8.GetByteCount(line), Is.LessThanOrEqualTo(65536))
            Assert.That(run.WorktreeState, Does.Contain("pathEntryCount=2048"))
            Assert.That(run.Command, Does.Contain($"sourceChars={command.Length}"))
            Assert.That(run.Command, Does.Contain($"sourceUtf8Bytes={Encoding.UTF8.GetByteCount command}"))
            Assert.That(run.Command, Does.Contain($"sha256={sha256 command}"))
            Assert.That(run.Command, Does.Contain("truncated=true"))

            let repeated = MeasurementRun.Create("run-1", "commit", "worktree", worktreeState, command, directory, [| "baseline" |])
            Assert.That(repeated.Command, Is.EqualTo(run.Command))
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)

    /// Verifies log-heavy failures retain terminal truth and bounded diagnostics in a writable summary.
    [<Test>]
    member _.LargeRuntimeFailureLedgerProducesBoundedTruthfulTerminalSummary() =
        let directory = Path.Combine(Path.GetTempPath(), $"grace-mca-measurement-{Guid.NewGuid():N}")

        let failures =
            Array.init 64 (fun index ->
                let phase = if index = 63 then "cleanup" else "startup"
                $"{phase}-failure-{index:D3}:{String(char (int 'a' + index % 26), 32768)}")

        let assertions = requiredAssertionIds |> Array.map passingAssertion

        try
            use writer = new EvidenceWriter(directory, 65536)
            let summary = ScenarioSummary.derive "run-1" "baseline" requiredAssertionIds assertions failures false
            writer.Append summary

            let line = File.ReadAllText(writer.Path).TrimEnd()
            Assert.That(Encoding.UTF8.GetByteCount(line), Is.LessThanOrEqualTo(65536))
            Assert.That(summary.Outcome, Is.EqualTo("Failed"))
            Assert.That(summary.RuntimeFailures, Is.Not.Empty)
            Assert.That(summary.RuntimeFailures[0], Does.Contain("failureCount=64"))
            Assert.That(String.concat " " summary.RuntimeFailures, Does.Contain($"sha256={sha256 failures[0]}"))
            Assert.That(String.concat " " summary.RuntimeFailures, Does.Contain($"sha256={sha256 failures[63]}"))
            Assert.That(String.concat " " summary.RuntimeFailures, Does.Contain("startup-failure-000"))
            Assert.That(String.concat " " summary.RuntimeFailures, Does.Contain("cleanup-failure-063"))
            Assert.That(summary.RuntimeFailures.Length, Is.LessThan(failures.Length))
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)

    /// Verifies JSON-escape-heavy failures retain bounded truthful terminal evidence through the real writer.
    [<Test>]
    member _.EscapeHeavyRuntimeFailuresProduceBoundedTruthfulTerminalSummary() =
        let directory = Path.Combine(Path.GetTempPath(), $"grace-mca-measurement-{Guid.NewGuid():N}")

        let escapeHeavyDetail = String.replicate 8192 "<>&"

        let failures = Array.init 8 (fun index -> $"failure-{index:D2}:{escapeHeavyDetail}")

        let assertions = requiredAssertionIds |> Array.map passingAssertion

        try
            use writer = new EvidenceWriter(directory, 65536)
            let summary = ScenarioSummary.derive "run-1" "baseline" requiredAssertionIds assertions failures false
            writer.Append summary

            let line = File.ReadAllText(writer.Path).TrimEnd()
            let retainedFailures = String.concat " " summary.RuntimeFailures
            Assert.That(Encoding.UTF8.GetByteCount(line), Is.LessThanOrEqualTo(65536))
            Assert.That(summary.Outcome, Is.EqualTo("Failed"))
            Assert.That(summary.RuntimeFailures[0], Does.Contain("failureCount=8"))
            Assert.That(retainedFailures, Does.Contain($"sourceChars={failures[0].Length}"))
            Assert.That(retainedFailures, Does.Contain($"sourceUtf8Bytes={Encoding.UTF8.GetByteCount failures[0]}"))
            Assert.That(retainedFailures, Does.Contain($"sha256={sha256 failures[0]}"))
            Assert.That(retainedFailures, Does.Contain("truncated=true"))
            Assert.That(retainedFailures, Does.Contain("failure-00"))
            Assert.That(retainedFailures, Does.Contain("failure-07"))
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)

    /// Verifies failure-controlled assertion detail retains source truth without escaping past the record limit.
    [<Test>]
    member _.EscapeHeavyAssertionDetailProducesBoundedTruthfulEvidence() =
        let directory = Path.Combine(Path.GetTempPath(), $"grace-mca-measurement-{Guid.NewGuid():N}")
        let detail = String.replicate 8192 "<>&"

        try
            use writer = new EvidenceWriter(directory, 65536)
            let assertion = MeasurementAssertion.Create("run-1", "baseline", requiredAssertionIds[0], false, detail)
            writer.Append assertion

            let line = File.ReadAllText(writer.Path).TrimEnd()
            Assert.That(Encoding.UTF8.GetByteCount(line), Is.LessThanOrEqualTo(65536))
            Assert.That(assertion.Detail, Does.Contain($"sourceChars={detail.Length}"))
            Assert.That(assertion.Detail, Does.Contain($"sourceUtf8Bytes={Encoding.UTF8.GetByteCount detail}"))
            Assert.That(assertion.Detail, Does.Contain($"sha256={sha256 detail}"))
            Assert.That(assertion.Detail, Does.Contain("truncated=true"))
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)

    /// Audits the supported Baseline constructors against the exact writer serialization for all four evidence types.
    [<Test>]
    member _.SupportedBaselineEvidenceRecordTypesRemainWithinSerializedLimit() =
        let directory = Path.Combine(Path.GetTempPath(), $"grace-mca-measurement-{Guid.NewGuid():N}")
        let commandPrefix = "dotnet test --configuration Release --no-build --filter "

        let command =
            commandPrefix
            + String('x', 208 - commandPrefix.Length)

        let labels = Dictionary<string, string>(StringComparer.Ordinal)
        labels["stage"] <- "settle"
        labels["outcome"] <- "completed"

        let run = MeasurementRun.Create("0123456789abcdef0123456789abcdef", String('a', 40), @"C:\Source\Grace", "clean", command, directory, [| "baseline" |])

        let sample =
            MeasurementSample.Create(
                "0123456789abcdef0123456789abcdef",
                "baseline",
                "stimulus-durations",
                "grace_manifest_contribution_processing_duration_milliseconds_count.delta",
                3L,
                labels
            )

        let assertion =
            MeasurementAssertion.Create(
                "0123456789abcdef0123456789abcdef",
                "baseline",
                "baseline.manifest-relationship-set",
                true,
                "references=3; relationships=3; logicalCounts=3; workflowCounts=3; physicalActiveCounts=3"
            )

        let summary = ScenarioSummary.derive "run-1" "baseline" requiredAssertionIds (requiredAssertionIds |> Array.map passingAssertion) Array.empty false

        Assert.That(run.Command, Is.EqualTo(command))
        Assert.That(run.Scenarios = [| "baseline" |], Is.True)
        Assert.That(sample.Name, Is.EqualTo("grace_manifest_contribution_processing_duration_milliseconds_count.delta"))
        Assert.That(sample.Labels["stage"], Is.EqualTo("settle"))
        Assert.That(sample.Labels["outcome"], Is.EqualTo("completed"))
        Assert.That(assertion.Detail, Is.EqualTo("references=3; relationships=3; logicalCounts=3; workflowCounts=3; physicalActiveCounts=3"))
        Assert.That(summary.RequiredAssertionIds = requiredAssertionIds, Is.True)

        try
            use writer = new EvidenceWriter(directory, 65536)
            writer.Append run
            writer.Append sample
            writer.Append assertion
            writer.Append summary

            let lines = File.ReadAllLines(writer.Path)
            Assert.That(lines, Has.Length.EqualTo(4))

            let recordTypes =
                lines
                |> Array.map (fun line ->
                    Assert.That(Encoding.UTF8.GetByteCount(line), Is.LessThanOrEqualTo(65536))
                    use document = JsonDocument.Parse(line)

                    document
                        .RootElement
                        .GetProperty("RecordType")
                        .GetString())

            let expectedRecordTypes =
                [|
                    "MeasurementRun"
                    "MeasurementSample"
                    "MeasurementAssertion"
                    "ScenarioSummary"
                |]

            Assert.That((recordTypes = expectedRecordTypes), Is.True)
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)

    /// Verifies concurrent records remain individually bounded UTF-8 NDJSON values without a BOM.
    [<Test>]
    member _.EvidenceWriterProducesBoundedAtomicUtf8NdjsonRecords() =
        task {
            let directory = Path.Combine(Path.GetTempPath(), $"grace-mca-measurement-{Guid.NewGuid():N}")

            try
                use writer = new EvidenceWriter(directory, 4096)

                do!
                    [| 0..31 |]
                    |> Array.map (fun index ->
                        Task.Run (fun () ->
                            writer.Append(
                                MeasurementSample.Create("run-1", "baseline", $"sample-{index:D2}", "test.value", int64 index, Dictionary<string, string>())
                            )))
                    |> Task.WhenAll

                let bytes = File.ReadAllBytes(writer.Path)
                Assert.That(bytes, Is.Not.Empty)
                Assert.That(bytes |> Array.take (min 3 bytes.Length), Is.Not.EqualTo([| 0xEFuy; 0xBBuy; 0xBFuy |]))

                let lines = File.ReadAllLines(writer.Path)
                Assert.That(lines, Has.Length.EqualTo(32))

                lines
                |> Array.iter (fun line ->
                    Assert.That(Text.Encoding.UTF8.GetByteCount(line), Is.LessThanOrEqualTo(4096))
                    use document = JsonDocument.Parse(line)

                    Assert.That(
                        document
                            .RootElement
                            .GetProperty("RecordType")
                            .GetString(),
                        Is.EqualTo("MeasurementSample")
                    ))
            finally
                if Directory.Exists directory then Directory.Delete(directory, true)
        }

    /// Verifies oversized records fail before any partial line reaches the evidence file.
    [<Test>]
    member _.EvidenceWriterRejectsOversizedRecordWithoutPartialOutput() =
        let directory = Path.Combine(Path.GetTempPath(), $"grace-mca-measurement-{Guid.NewGuid():N}")

        try
            use writer = new EvidenceWriter(directory, 256)

            let assertion =
                {
                    RecordType = nameof MeasurementAssertion
                    RunId = "run-1"
                    ScenarioId = "baseline"
                    AssertionId = requiredAssertionIds[0]
                    Passed = false
                    Detail = String.replicate 1024 "x"
                    ObservedAt = "2026-07-30T00:00:00.0000000+00:00"
                }

            Assert.Throws<InvalidDataException>(Action(fun () -> writer.Append(assertion)))
            |> ignore

            Assert.That(File.ReadAllText(writer.Path), Is.Empty)
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)
