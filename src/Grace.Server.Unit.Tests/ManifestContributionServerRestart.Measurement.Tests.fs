namespace Grace.Server.Tests

open Grace.Server.Tests.Measurement
open NUnit.Framework
open System

/// Proves the deterministic contract used by the Grace.Server restart replay witness.
[<TestFixture>]
type ManifestContributionServerRestartMeasurementTests() =

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

    /// Verifies the scenario can pass only with the exact twelve issue-owned assertion identities.
    [<Test>]
    member _.RequiredAssertionIdentifiersAreExact() = Assert.That(ServerRestart.requiredAssertionIds = requiredAssertionIds, Is.True)

    /// Verifies restart readiness retains the post-command non-ready transition before fresh Healthy and HTTP success.
    [<Test>]
    member _.FreshRestartReadinessPassesInRequiredOrder() =
        let commandStartedAt = DateTimeOffset.Parse("2026-07-31T10:00:00Z")
        let commandCompletedAt = commandStartedAt.AddSeconds 1.0
        let nonReadyObservedAt = commandCompletedAt.AddSeconds 1.0
        let healthyObservedAt = nonReadyObservedAt.AddSeconds 1.0

        let errors =
            ServerRestart.validateFreshReadiness
                true
                commandStartedAt
                commandCompletedAt
                nonReadyObservedAt
                "Starting"
                "Unhealthy"
                healthyObservedAt
                "Healthy"
                (healthyObservedAt.AddSeconds 1.0)
                true

        Assert.That(errors, Is.Empty)

    /// Verifies a stale health snapshot cannot satisfy a real post-command restart proof.
    [<Test>]
    member _.StaleHealthOrMissingReadinessFails() =
        let commandStartedAt = DateTimeOffset.Parse("2026-07-31T10:00:00Z")

        let errors =
            ServerRestart.validateFreshReadiness
                false
                commandStartedAt
                commandStartedAt
                commandStartedAt
                "Running"
                "Healthy"
                commandStartedAt
                "Running"
                (commandStartedAt.AddSeconds -1.0)
                false

        Assert.That(errors, Has.Length.EqualTo(7))
        Assert.That(String.Join("; ", errors), Does.Contain("command did not complete"))
        Assert.That(String.Join("; ", errors), Does.Contain("non-ready transition was not observed after"))
        Assert.That(String.Join("; ", errors), Does.Contain("did not demonstrate a non-ready state"))
        Assert.That(String.Join("; ", errors), Does.Contain("Healthy event did not follow"))
        Assert.That(String.Join("; ", errors), Does.Contain("not Healthy"))
        Assert.That(String.Join("; ", errors), Does.Contain("did not follow"))
        Assert.That(String.Join("; ", errors), Does.Contain("HTTP readiness failed"))

    /// Verifies a Healthy observation cannot hide an omitted or out-of-order non-ready restart transition.
    [<Test>]
    member _.MissingOrOutOfOrderNonReadyTransitionFails() =
        let commandStartedAt = DateTimeOffset.Parse("2026-07-31T10:00:00Z")
        let commandCompletedAt = commandStartedAt.AddSeconds 1.0
        let healthyObservedAt = commandCompletedAt.AddSeconds 2.0

        let errors =
            ServerRestart.validateFreshReadiness
                true
                commandStartedAt
                commandCompletedAt
                commandStartedAt
                "Running"
                "Healthy"
                healthyObservedAt
                "Healthy"
                (healthyObservedAt.AddSeconds 1.0)
                true

        Assert.That(errors, Has.Length.EqualTo(2))
        Assert.That(String.Join("; ", errors), Does.Contain("non-ready transition was not observed after"))
        Assert.That(String.Join("; ", errors), Does.Contain("did not demonstrate a non-ready state"))

    /// Verifies unchanged durable state cannot replace exact replay identity and settlement completion.
    [<Test>]
    member _.UnsettledReplayFailsEvenWhenDurableStateIsUnchanged() =
        let expectedMessageId = "Reference/one/Created"

        let errors = ServerRestart.validateReplayCompletion expectedMessageId Array.empty 0L 0L false

        Assert.That(errors, Has.Length.EqualTo(4))
        Assert.That(String.Join("; ", errors), Does.Contain("Missing expected"))
        Assert.That(String.Join("; ", errors), Does.Contain("message delta"))
        Assert.That(String.Join("; ", errors), Does.Contain("duration delta"))
        Assert.That(String.Join("; ", errors), Does.Contain("settlement failed"))

    /// Verifies exactly one matching replay delivery and both terminal metric observations are required.
    [<Test>]
    member _.ExactReplayCompletionPasses() =
        let expectedMessageId = "Reference/one/Created"

        let errors = ServerRestart.validateReplayCompletion expectedMessageId [| expectedMessageId |] 1L 1L true

        Assert.That(errors, Is.Empty)

    /// Verifies duplicate or unrelated replay identities cannot hide inside an otherwise exact metric delta.
    [<Test>]
    member _.DuplicateOrUnclassifiedReplayIdentityFails() =
        let expectedMessageId = "Reference/one/Created"

        let errors =
            ServerRestart.validateReplayCompletion
                expectedMessageId
                [|
                    expectedMessageId
                    expectedMessageId
                    "Reference/unclassified/Created"
                |]
                1L
                1L
                true

        Assert.That(errors, Is.Not.Empty)
        Assert.That(String.Join("; ", errors), Does.Contain("duplicate"))
        Assert.That(String.Join("; ", errors), Does.Contain("Unclassified"))

    /// Verifies a suffixed settlement series fails even when both required exact samples are also present.
    [<Test>]
    member _.SuffixedSettlementSeriesCannotHideBesideExactSamples() =
        let baseline =
            """
grace_manifest_contribution_messages_total{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 7
grace_manifest_contribution_processing_duration_milliseconds_count{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 7
"""

        let observed =
            """
grace_manifest_contribution_messages_total{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 8
grace_manifest_contribution_processing_duration_milliseconds_count{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 8
grace_manifest_contribution_messages_total_created{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 8
"""

        match OpenMetrics.evaluateCompletedSettlementDelta 1L baseline observed with
        | DeltaEvaluation.Invalid error -> Assert.That(error, Does.Contain("suffixed"))
        | result -> Assert.Fail($"A suffixed settlement series unexpectedly produced {result}.")

    /// Verifies a fresh process with both settlement series absent normalizes to the exact paired-zero baseline.
    [<Test>]
    member _.AbsentFreshProcessSeriesNormalizeToPairedZero() =
        let observed =
            """
grace_manifest_contribution_messages_total{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 1
grace_manifest_contribution_processing_duration_milliseconds_count{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 1
"""

        match OpenMetrics.captureFreshProcessCompletedSettlementBaseline "# unrelated metrics only" with
        | Ok baseline -> Assert.That(OpenMetrics.evaluateCompletedSettlementDelta 1L baseline observed, Is.EqualTo(DeltaEvaluation.Complete(1L, 1L)))
        | Error error -> Assert.Fail($"Expected an absent fresh-process pair to normalize, got {error}.")

    /// Verifies an exact paired-zero fresh-process scrape remains a valid captured baseline.
    [<Test>]
    member _.ExactFreshProcessZeroSeriesRemainValid() =
        let zero =
            """
grace_manifest_contribution_messages_total{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 0
grace_manifest_contribution_processing_duration_milliseconds_count{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 0
"""

        match OpenMetrics.captureFreshProcessCompletedSettlementBaseline zero with
        | Ok baseline -> Assert.That(baseline, Is.EqualTo(zero))
        | Error error -> Assert.Fail($"Expected an exact zero baseline to remain valid, got {error}.")

    /// Verifies every ambiguous or nonzero fresh-process settlement shape fails instead of becoming a replay baseline.
    [<Test>]
    member _.UnsafeFreshProcessBaselineShapesFail() =
        let messages value =
            $"grace_manifest_contribution_messages_total{{otel_scope_name=\"Grace.ManifestContributionAccounting\",stage=\"settle\",outcome=\"completed\"}} {value}"

        let durations value =
            $"grace_manifest_contribution_processing_duration_milliseconds_count{{otel_scope_name=\"Grace.ManifestContributionAccounting\",stage=\"settle\",outcome=\"completed\"}} {value}"

        let invalidScrapes =
            [|
                "partial", messages 0
                "nonzero", $"{messages 1}{Environment.NewLine}{durations 1}"
                "suffix", "grace_manifest_contribution_messages_total_created 0"
                "other-label", "grace_manifest_contribution_messages_total{outcome=\"completed\"} 0"
                "settlement-failed", "grace_manifest_contribution_messages_total{outcome=\"settlement_failed\"} 0"
                "duplicate", $"{messages 0}{Environment.NewLine}{messages 0}{Environment.NewLine}{durations 0}"
                "malformed", "grace_manifest_contribution_messages_total{ 0"
            |]

        invalidScrapes
        |> Array.iter (fun (name, scrape) ->
            match OpenMetrics.captureFreshProcessCompletedSettlementBaseline scrape with
            | Error _ -> ()
            | Ok _ -> Assert.Fail($"{name} unexpectedly normalized to a fresh-process baseline."))
