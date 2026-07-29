namespace Grace.Server.Tests

open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Threading.Tasks

/// Covers deterministic manifest-contribution measurement support without starting Aspire.
[<TestFixture>]
type ManifestContributionMeasurementSupportTests() =

    /// Verifies built-in resource commands distinguish success, cancellation, and failure before readiness checks.
    [<Test>]
    member _.ResourceCommandClassificationPreservesTerminalResult() =
        let completed = ManifestContributionMeasurementSupport.classifyResourceCommand { Success = true; Canceled = false; Message = "accepted" }

        let canceled = ManifestContributionMeasurementSupport.classifyResourceCommand { Success = false; Canceled = true; Message = "operator canceled" }

        let failed = ManifestContributionMeasurementSupport.classifyResourceCommand { Success = false; Canceled = false; Message = String.Empty }

        Assert.Multiple(
            Action(fun () ->
                Assert.That(completed, Is.EqualTo(ResourceCommandOutcome.Completed))
                Assert.That(canceled, Is.EqualTo(ResourceCommandOutcome.Canceled "operator canceled"))
                Assert.That(failed, Is.EqualTo(ResourceCommandOutcome.Failed "Resource command failed without details.")))
        )

    /// Verifies only start and restart commands wait for healthy runtime state.
    [<Test>]
    member _.ResourceCommandReadinessMatchesBuiltInCommandSemantics() =
        Assert.Multiple(
            Action(fun () ->
                Assert.That(ManifestContributionMeasurementSupport.commandRequiresHealthyResource "resource-start", Is.True)
                Assert.That(ManifestContributionMeasurementSupport.commandRequiresHealthyResource "resource-restart", Is.True)
                Assert.That(ManifestContributionMeasurementSupport.commandRequiresHealthyResource "resource-stop", Is.False)
                Assert.That(ManifestContributionMeasurementSupport.commandRequiresHealthyResource "custom-command", Is.False))
        )

    /// Verifies command failures retain bounded actionable state without leaking connection-string credentials.
    [<Test>]
    member _.ResourceCommandDiagnosticsAreBoundedAndRedacted() =
        let secret = "service-bus-secret"

        let logs =
            [ $"Endpoint=sb://localhost:5672;SharedAccessKey={secret};UseDevelopmentEmulator=true;"
              String('x', ManifestContributionMeasurementSupport.MaximumDiagnosticCharacters * 2) ]

        let diagnostic =
            ManifestContributionMeasurementSupport.formatBoundedDiagnostic "redis restart" "State=Failed; AccountKey=cosmos-secret; Password=sql-secret" logs

        Assert.Multiple(
            Action(fun () ->
                Assert.That(diagnostic.Length, Is.LessThanOrEqualTo(ManifestContributionMeasurementSupport.MaximumDiagnosticCharacters))
                Assert.That(diagnostic, Does.Contain("redis restart"))
                Assert.That(diagnostic, Does.Contain("State=Failed"))
                Assert.That(diagnostic, Does.Contain("SharedAccessKey=***"))
                Assert.That(diagnostic, Does.Contain("AccountKey=***"))
                Assert.That(diagnostic, Does.Contain("Password=***"))
                Assert.That(diagnostic, Does.Not.Contain(secret))
                Assert.That(diagnostic, Does.Not.Contain("cosmos-secret"))
                Assert.That(diagnostic, Does.Not.Contain("sql-secret")))
        )

    /// Verifies evidence records are UTF-8 no-BOM, parseable, and rejected when they exceed the fixture bound.
    [<Test>]
    member _.MeasurementEvidenceIsParseableUtf8AndBounded() =
        let path = Path.Combine(Path.GetTempPath(), "grace-server-unit-tests", $"mca-evidence-{Guid.NewGuid():N}.ndjson")

        try
            let measurements = Dictionary<string, obj>()
            measurements["logicalCount"] <- 3L

            let sample: MeasurementSample =
                { schemaVersion = "1.0"
                  scenario = "HotManifest"
                  sampleType = "final-state"
                  sequence = 1
                  timestampUtc = DateTimeOffset.Parse("2026-07-28T12:00:00Z")
                  correlationKey = "hot-manifest"
                  measurements = measurements }

            ManifestContributionMeasurementSupport.appendEvidenceRecord path sample
            let bytes = File.ReadAllBytes path
            let records = ManifestContributionMeasurementSupport.readEvidenceRecords path

            Assert.Multiple(
                Action(fun () ->
                    Assert.That(bytes, Has.Length.GreaterThan(0))
                    Assert.That(bytes[0], Is.Not.EqualTo(0xEFuy), "UTF-8 evidence must not begin with a BOM.")
                    Assert.That(records, Has.Length.EqualTo(1))
                    Assert.That(records[0].GetProperty("scenario").GetString(), Is.EqualTo("HotManifest"))

                    Assert.That(records[0].GetProperty("measurements").GetProperty("logicalCount").GetInt64(), Is.EqualTo(3L)))
            )

            let oversized = {| payload = String('z', ManifestContributionMeasurementSupport.MaximumEvidenceRecordBytes) |}

            Assert.That(Action(fun () -> ManifestContributionMeasurementSupport.appendEvidenceRecord path oversized), Throws.TypeOf<InvalidDataException>())
        finally
            if File.Exists path then File.Delete path

    /// Verifies OpenMetrics timestamps are never mistaken for manifest-accounting sample values.
    [<Test>]
    member _.MeasurementEvidenceParsesOpenMetricsValuesBeforeTimestamps() =
        let metrics =
            """
# TYPE grace_manifest_contribution_messages_total counter
grace_manifest_contribution_messages_total{outcome="completed"} 2 1785304963133
grace_manifest_contribution_messages_total{outcome="ignored"} 1 1785304963133
other_metric_total 99 1785304963133
"""

        let actual = ManifestContributionMeasurementSupport.sumOpenMetricsSamples (fun name -> name = "grace_manifest_contribution_messages_total") metrics

        Assert.That(actual, Is.EqualTo(3.0))

    /// Verifies an absence wait observes broker state before accepting its terminal predicate.
    [<Test>]
    member _.TerminalWaitObservesStateBeforeAcceptingAbsence() =
        task {
            let mutable observations = 0

            let observeAsync () =
                observations <- observations + 1
                Task.FromResult Set.empty<string>

            let! result = ManifestContributionMeasurementSupport.waitForTerminalStateAsync (TimeSpan.FromSeconds 1.0) TimeSpan.Zero observeAsync Set.isEmpty

            Assert.Multiple(
                Action(fun () ->
                    Assert.That(result, Is.Empty)
                    Assert.That(observations, Is.EqualTo(1), "Absence must be proven by at least one broker observation."))
            )
        }

    /// Verifies a scenario summary cannot report success after one of its recorded assertions failed.
    [<Test>]
    member _.ScenarioSummaryMatchesRecordedAssertionResults() =
        let root = Path.Combine(Path.GetTempPath(), "grace-server-unit-tests", $"mca-summary-{Guid.NewGuid():N}")

        try
            let sink = MeasurementEvidenceSink root
            sink.Assertion("Regression", "failed-assertion", "Regression assertion", true, false, false, [||])
            sink.Summary("Regression", DateTimeOffset.UtcNow, 1, [||])

            let summary =
                ManifestContributionMeasurementSupport.readEvidenceRecords (Path.Combine(root, "summaries.ndjson"))
                |> Array.exactlyOne

            let assertion =
                ManifestContributionMeasurementSupport.readEvidenceRecords (Path.Combine(root, "assertions.ndjson"))
                |> Array.exactlyOne

            Assert.Multiple(
                Action(fun () ->
                    Assert.That(assertion.GetProperty("passed").GetBoolean(), Is.False)
                    Assert.That(summary.GetProperty("passed").GetBoolean(), Is.False)
                    Assert.That(summary.GetProperty("assertionCount").GetInt32(), Is.EqualTo(1)))
            )
        finally
            if Directory.Exists root then Directory.Delete(root, true)
