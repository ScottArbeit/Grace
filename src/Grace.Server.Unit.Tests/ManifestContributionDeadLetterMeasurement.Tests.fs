namespace Grace.Server.Tests

open Grace.Server.Tests.Measurement
open NUnit.Framework
open System

/// Proves the pure identity, boundary, reason, cleanup, and derived-outcome contract used by the hosted DLQ witness.
[<TestFixture>]
type ManifestContributionDeadLetterMeasurementTests() =

    let completed messages durations =
        $"grace_manifest_contribution_messages_total{{otel_scope_name=\"Grace.ManifestContributionAccounting\",stage=\"settle\",outcome=\"completed\"}} {messages}\n"
        + $"grace_manifest_contribution_processing_duration_milliseconds_count{{otel_scope_name=\"Grace.ManifestContributionAccounting\",stage=\"settle\",outcome=\"completed\"}} {durations}\n"

    /// Verifies the exact nine assertion identities are the only successful dead-letter summary shape.
    [<Test>]
    member _.``dead-letter summary requires the exact derived assertion set``() =
        let assertions =
            DeadLetter.requiredAssertionIds
            |> Array.map (fun assertionId -> MeasurementAssertion.Create("run", "dead-letter", assertionId, true, "proved"))

        let summary = ScenarioSummary.derive "run" "dead-letter" DeadLetter.requiredAssertionIds assertions Array.empty false

        Assert.That(summary.Outcome, Is.EqualTo("Passed"))
        Assert.That(summary.RequiredAssertionCount, Is.EqualTo(9))
        Assert.That(summary.PassedAssertionCount, Is.EqualTo(9))

    /// Verifies a wrong broker identity cannot satisfy active, DLQ, or cleanup evidence.
    [<Test>]
    member _.``wrong identity cannot pass the broker witness``() =
        let expected = "mca-dead-letter-expected"
        let wrong = "mca-dead-letter-wrong"

        Assert.That(DeadLetter.identityMatches expected wrong, Is.False)
        Assert.That(DeadLetter.dlqMessageObserved expected wrong, Is.False)
        Assert.That(DeadLetter.belowMaximumRemainsActive expected wrong DeadLetter.MaximumDeliveryCount Array.empty, Is.False)
        Assert.That(DeadLetter.deadLetterObservationPasses expected wrong DeadLetter.DeadLetterDeliveryCount "MaxDeliveryCountExceeded", Is.False)
        Assert.That(DeadLetter.cleanupComplete expected [| wrong |] [| wrong |], Is.True)

    /// Verifies the selected fixture/default producer inventory rejects any unexpected active Reference identity.
    [<Test>]
    member _.``selected fixture producer inventory requires only the declared default Reference``() =
        let expected = [| "Reference/default/Created" |]

        Assert.That(ProducerInventory.validate expected expected, Is.Empty)

        let unexpected =
            ProducerInventory.validate
                expected
                [|
                    expected[0]
                    "Reference/unexpected/Created"
                |]

        Assert.That(unexpected, Has.One.Items)
        Assert.That(unexpected[0], Does.Contain("Unclassified Reference-created envelope"))

    /// Verifies delivery ten remains active and absent from the DLQ, while nearby false boundaries fail.
    [<Test>]
    member _.``delivery boundary requires active ten and no matching DLQ identity``() =
        let expected = "mca-dead-letter-boundary"

        Assert.That(DeadLetter.belowMaximumRemainsActive expected expected 10 Array.empty, Is.True)
        Assert.That(DeadLetter.belowMaximumRemainsActive expected expected 9 Array.empty, Is.False)
        Assert.That(DeadLetter.belowMaximumRemainsActive expected expected 10 [| expected |], Is.False)

    /// Verifies delivery eleven and a nonempty bounded redacted broker reason are mandatory.
    [<Test>]
    member _.``DLQ observation requires delivery eleven and bounded redacted reason``() =
        let expected = "mca-dead-letter-terminal"

        Assert.That(DeadLetter.deadLetterObservationPasses expected expected 11 "MaxDeliveryCountExceeded", Is.True)
        Assert.That(DeadLetter.deadLetterObservationPasses expected expected 10 "MaxDeliveryCountExceeded", Is.False)
        Assert.That(DeadLetter.deadLetterObservationPasses expected expected 11 String.Empty, Is.False)

        let projected = DeadLetter.boundedBrokerReason ($"MaxDeliveryCountExceeded SharedAccessKey={String('s', 512)}")
        Assert.That(projected, Does.Contain("SharedAccessKey=***"))
        Assert.That(projected, Does.Not.Contain(String('s', 32)))
        Assert.That(projected.Length, Is.LessThanOrEqualTo(256))

    /// Verifies cleanup fails while the exact witness remains in either subqueue.
    [<Test>]
    member _.``cleanup requires exact witness absence from active and DLQ``() =
        let expected = "mca-dead-letter-cleanup"

        Assert.That(DeadLetter.cleanupComplete expected Array.empty Array.empty, Is.True)
        Assert.That(DeadLetter.cleanupComplete expected [| expected |] Array.empty, Is.False)
        Assert.That(DeadLetter.cleanupComplete expected Array.empty [| expected |], Is.False)

    /// Verifies unchanged production settlement telemetry requires one exact sample of each family on both scrapes.
    [<Test>]
    member _.``unchanged telemetry rejects absent malformed and changed samples``() =
        match OpenMetrics.evaluateCompletedSettlementUnchanged String.Empty String.Empty with
        | UnchangedEvaluation.UnchangedInvalid _ -> ()
        | result -> Assert.Fail($"Expected two absent scrapes to be invalid, got {result}.")

        match OpenMetrics.evaluateCompletedSettlementUnchanged (completed 4 4) (completed 4 4) with
        | UnchangedEvaluation.Unchanged (4L, 4L) -> ()
        | result -> Assert.Fail($"Expected equal exact samples, got {result}.")

        match OpenMetrics.evaluateCompletedSettlementUnchanged (completed 4 4) (completed 5 4) with
        | UnchangedEvaluation.Changed _ -> ()
        | result -> Assert.Fail($"Expected changed samples to fail, got {result}.")

        match OpenMetrics.evaluateCompletedSettlementUnchanged String.Empty (completed 1 1) with
        | UnchangedEvaluation.UnchangedInvalid _ -> ()
        | result -> Assert.Fail($"Expected one-sided absence to be invalid, got {result}.")

        let invalidScrapes =
            [|
                "missing",
                "grace_manifest_contribution_messages_total{otel_scope_name=\"Grace.ManifestContributionAccounting\",stage=\"settle\",outcome=\"completed\"} 4"
                "duplicate",
                completed 4 4
                + Environment.NewLine
                + completed 4 4
                "suffix",
                """
grace_manifest_contribution_messages_total_suffix{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 4
grace_manifest_contribution_processing_duration_milliseconds_count_suffix{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 4
"""
                "other-label",
                """
grace_manifest_contribution_messages_total{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed",reference_type="Save"} 4
grace_manifest_contribution_processing_duration_milliseconds_count{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 4
"""
                "settlement-failed",
                """
grace_manifest_contribution_messages_total{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="settlement_failed"} 4
grace_manifest_contribution_processing_duration_milliseconds_count{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="settlement_failed"} 4
"""
            |]

        invalidScrapes
        |> Array.iter (fun (name, scrape) ->
            match OpenMetrics.evaluateCompletedSettlementUnchanged (completed 4 4) scrape with
            | UnchangedEvaluation.UnchangedInvalid _ -> ()
            | result -> Assert.Fail($"Expected {name} samples to be invalid, got {result}."))

        match OpenMetrics.evaluateCompletedSettlementUnchanged (completed 4 4) (completed 3 4) with
        | UnchangedEvaluation.Changed _ -> ()
        | result -> Assert.Fail($"Expected reset samples to fail, got {result}.")
