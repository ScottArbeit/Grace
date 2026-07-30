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
            let assertion = MeasurementAssertion.Create("run-1", "baseline", requiredAssertionIds[0], false, String.replicate 1024 "x")

            Assert.Throws<InvalidDataException>(Action(fun () -> writer.Append(assertion)))
            |> ignore

            Assert.That(File.ReadAllText(writer.Path), Is.Empty)
        finally
            if Directory.Exists directory then Directory.Delete(directory, true)
