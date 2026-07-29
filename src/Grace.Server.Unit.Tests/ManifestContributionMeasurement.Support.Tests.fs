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
            Action (fun () ->
                Assert.That(completed, Is.EqualTo(ResourceCommandOutcome.Completed))
                Assert.That(canceled, Is.EqualTo(ResourceCommandOutcome.Canceled "operator canceled"))
                Assert.That(failed, Is.EqualTo(ResourceCommandOutcome.Failed "Resource command failed without details.")))
        )

    /// Verifies only start and restart commands wait for healthy runtime state.
    [<Test>]
    member _.ResourceCommandReadinessMatchesBuiltInCommandSemantics() =
        Assert.Multiple(
            Action (fun () ->
                Assert.That(ManifestContributionMeasurementSupport.commandRequiresHealthyResource "resource-start", Is.True)
                Assert.That(ManifestContributionMeasurementSupport.commandRequiresHealthyResource "resource-restart", Is.True)
                Assert.That(ManifestContributionMeasurementSupport.commandRequiresHealthyResource "resource-stop", Is.False)
                Assert.That(ManifestContributionMeasurementSupport.commandRequiresHealthyResource "custom-command", Is.False))
        )

    /// Verifies only an explicitly healthy Aspire snapshot can bypass resource-start recovery.
    [<Test>]
    member _.ResourceRecoveryRequiresExplicitHealthySnapshot() =
        Assert.Multiple(
            Action (fun () ->
                Assert.That(ManifestContributionMeasurementSupport.isHealthyResourceStatus "Healthy", Is.True)
                Assert.That(ManifestContributionMeasurementSupport.isHealthyResourceStatus " healthy ", Is.True)
                Assert.That(ManifestContributionMeasurementSupport.isHealthyResourceStatus "Unhealthy", Is.False)
                Assert.That(ManifestContributionMeasurementSupport.isHealthyResourceStatus "Stopped", Is.False)
                Assert.That(ManifestContributionMeasurementSupport.isHealthyResourceStatus String.Empty, Is.False))
        )

    /// Verifies stale Healthy state cannot bypass bounded recovery after resource-specific readiness fails.
    [<Test>]
    member _.StaleHealthyReadinessFailureRoutesThroughRecovery() =
        task {
            let observations = ResizeArray<string>()
            let mutable readinessAttempt = 0

            let proveReadinessAsync () =
                task {
                    readinessAttempt <- readinessAttempt + 1
                    observations.Add($"readiness-{readinessAttempt}")

                    if readinessAttempt = 1 then raise (TimeoutException("stale Healthy readiness"))
                }

            let recoverAsync () = task { observations.Add("recover") }

            let! recoveryWarning = ManifestContributionMeasurementSupport.recoverResourceReadinessAsync true proveReadinessAsync recoverAsync

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(
                        observations.ToArray(),
                        Is.EqualTo<string array>(
                            [|
                                "readiness-1"
                                "recover"
                                "readiness-2"
                            |]
                        ),
                        "A stale Healthy snapshot must recover and then prove readiness again."
                    )

                    Assert.That(recoveryWarning, Is.EqualTo<Exception option>(None)))
            )
        }

    /// Verifies a failed start result can recover when fresh readiness succeeds and remains observable.
    [<Test>]
    member _.FreshReadinessCanRecoverAfterFailedStartResult() =
        task {
            let mutable readinessAttempt = 0

            let proveReadinessAsync () =
                task {
                    readinessAttempt <- readinessAttempt + 1

                    if readinessAttempt = 1 then raise (TimeoutException("stale Healthy readiness"))
                }

            let recoverAsync () = task { raise (InvalidOperationException("start reported failure")) }

            let! recoveryWarning = ManifestContributionMeasurementSupport.recoverResourceReadinessAsync true proveReadinessAsync recoverAsync

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(readinessAttempt, Is.EqualTo(2))

                    Assert.That(
                        recoveryWarning
                        |> Option.map (fun warning -> warning.Message),
                        Is.EqualTo<string option>(Some "start reported failure")
                    ))
            )
        }

    /// Verifies failed recovery retains the initial, command, and final readiness diagnostics.
    [<Test>]
    member _.ResourceRecoveryFailureRetainsAllDiagnostics() =
        task {
            let mutable readinessAttempt = 0

            let proveReadinessAsync () =
                task {
                    readinessAttempt <- readinessAttempt + 1

                    if readinessAttempt = 1 then
                        raise (TimeoutException("initial stale readiness"))
                    else
                        raise (TimeoutException("final readiness failure"))
                }

            let recoverAsync () = task { raise (InvalidOperationException("bounded start failure")) }

            let failure =
                Assert.ThrowsAsync<AggregateException>(
                    Func<Task>(fun () -> ManifestContributionMeasurementSupport.recoverResourceReadinessAsync true proveReadinessAsync recoverAsync)
                )

            let diagnostic = failure.ToString()

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(diagnostic, Does.Contain("initial stale readiness"))
                    Assert.That(diagnostic, Does.Contain("bounded start failure"))
                    Assert.That(diagnostic, Does.Contain("final readiness failure"))
                    Assert.That(readinessAttempt, Is.EqualTo(2)))
            )
        }

    /// Verifies command failures retain bounded actionable state without leaking connection-string credentials.
    [<Test>]
    member _.ResourceCommandDiagnosticsAreBoundedAndRedacted() =
        let secret = "service-bus-secret"

        let logs =
            [
                $"Endpoint=sb://localhost:5672;SharedAccessKey={secret};UseDevelopmentEmulator=true;"
                String(
                    'x',
                    ManifestContributionMeasurementSupport.MaximumDiagnosticCharacters
                    * 2
                )
            ]

        let diagnostic =
            ManifestContributionMeasurementSupport.formatBoundedDiagnostic "redis restart" "State=Failed; AccountKey=cosmos-secret; Password=sql-secret" logs

        Assert.Multiple(
            Action (fun () ->
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
                {
                    schemaVersion = "1.0"
                    scenario = "HotManifest"
                    sampleType = "final-state"
                    sequence = 1
                    timestampUtc = DateTimeOffset.Parse("2026-07-28T12:00:00Z")
                    correlationKey = "hot-manifest"
                    measurements = measurements
                }

            ManifestContributionMeasurementSupport.appendEvidenceRecord path sample
            let bytes = File.ReadAllBytes path
            let records = ManifestContributionMeasurementSupport.readEvidenceRecords path

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(bytes, Has.Length.GreaterThan(0))
                    Assert.That(bytes[0], Is.Not.EqualTo(0xEFuy), "UTF-8 evidence must not begin with a BOM.")
                    Assert.That(records, Has.Length.EqualTo(1))
                    Assert.That(records[ 0 ].GetProperty("scenario").GetString(), Is.EqualTo("HotManifest"))

                    Assert.That(
                        records[0]
                            .GetProperty("measurements")
                            .GetProperty("logicalCount")
                            .GetInt64(),
                        Is.EqualTo(3L)
                    ))
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
                Action (fun () ->
                    Assert.That(result, Is.Empty)
                    Assert.That(observations, Is.EqualTo(1), "Absence must be proven by at least one broker observation."))
            )
        }

    /// Verifies a terminal wait continues until expected telemetry is observed.
    [<Test>]
    member _.TerminalWaitRequiresExpectedTelemetry() =
        task {
            let observations = Queue<int>([ 0; 1; 2 ])

            let observeAsync () = Task.FromResult(observations.Dequeue())

            let! result =
                ManifestContributionMeasurementSupport.waitForTerminalStateAsync (TimeSpan.FromSeconds 1.0) TimeSpan.Zero observeAsync (fun telemetryCount ->
                    telemetryCount >= 2)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(result, Is.EqualTo(2))
                    Assert.That(observations, Is.Empty, "The wait must consume non-terminal observations before returning."))
            )
        }

    /// Verifies baseline telemetry is terminal only after both exact delivery observations are exported.
    [<Test>]
    member _.BaselineTerminalTelemetryRequiresExactMessageAndDurationDeltas() =
        let baselineMessages = 10.0
        let baselineDurations = 10.0
        let expectedDeliveries = 2.0

        Assert.Multiple(
            Action (fun () ->
                Assert.That(
                    ManifestContributionMeasurementSupport.hasExactTelemetryDelta expectedDeliveries baselineMessages baselineDurations 12.0 11.0,
                    Is.False,
                    "A message-only terminal observation must not end the baseline wait."
                )

                Assert.That(
                    ManifestContributionMeasurementSupport.hasExactTelemetryDelta expectedDeliveries baselineMessages baselineDurations 11.0 12.0,
                    Is.False,
                    "A duration-only terminal observation must not end the baseline wait."
                )

                Assert.That(
                    ManifestContributionMeasurementSupport.hasExactTelemetryDelta expectedDeliveries baselineMessages baselineDurations 12.0 12.0,
                    Is.True
                )

                Assert.That(
                    ManifestContributionMeasurementSupport.hasExactTelemetryDelta expectedDeliveries baselineMessages baselineDurations 13.0 12.0,
                    Is.False,
                    "Unrelated extra message telemetry must not satisfy the exact scenario delta."
                ))
        )

    /// Verifies unchanged pre-replay telemetry cannot prove server-restart replay settlement.
    [<Test>]
    member _.ServerRestartReplaySettlementRequiresFreshExactTelemetryDelta() =
        let postRestartMessages = 0.0
        let postRestartDurations = 0.0

        Assert.Multiple(
            Action (fun () ->
                Assert.That(
                    ManifestContributionMeasurementSupport.hasExactTelemetryDelta
                        1.0
                        postRestartMessages
                        postRestartDurations
                        postRestartMessages
                        postRestartDurations,
                    Is.False,
                    "The unchanged post-restart baseline must not stand in for replay settlement."
                )

                Assert.That(
                    ManifestContributionMeasurementSupport.hasExactTelemetryDelta 1.0 postRestartMessages postRestartDurations 1.0 0.0,
                    Is.False,
                    "Partial telemetry publication must not prove replay settlement."
                )

                Assert.That(ManifestContributionMeasurementSupport.hasExactTelemetryDelta 1.0 postRestartMessages postRestartDurations 1.0 1.0, Is.True)

                Assert.That(
                    ManifestContributionMeasurementSupport.hasExactTelemetryDelta 1.0 postRestartMessages postRestartDurations 2.0 2.0,
                    Is.False,
                    "A prior delivery settling beside the replay must not satisfy the exact replay predicate."
                ))
        )

    /// Verifies the centralized duplicate backlog contract declares all six recorded assertions.
    [<Test>]
    member _.DuplicateBacklogContractDeclaresSixAssertions() =
        Assert.That(ManifestContributionMeasurementContracts.DuplicateBacklogRecovery.ExpectedAssertionCount, Is.EqualTo(6))

    /// Verifies every scenario before the first server stop owns an exact terminal telemetry requirement.
    [<Test>]
    member _.PreStopScenariosRequireExactTerminalTelemetry() =
        let requirements = ManifestContributionMeasurementContracts.PreStopTerminalTelemetry

        Assert.That(
            requirements
            |> Array.map (fun requirement -> requirement.Scenario, requirement.ExpectedDeliveries),
            Is.EqualTo<(string * int) array>(
                [|
                    "Baseline", 2
                    "HotManifest", 3
                    "HighlySharedDirectoryVersion", 3
                |]
            )
        )

        Assert.DoesNotThrow(
            Action (fun () ->
                ManifestContributionMeasurementSupport.requirePreStopTerminalTelemetry
                    ManifestContributionMeasurementContracts.All
                    ManifestContributionMeasurementContracts.DuplicateBacklogRecovery
                    requirements)
        )

        let missingHotManifest =
            requirements
            |> Array.filter (fun requirement ->
                requirement.Scenario
                <> ManifestContributionMeasurementContracts.HotManifest.Scenario)

        Assert.That(
            Action (fun () ->
                ManifestContributionMeasurementSupport.requirePreStopTerminalTelemetry
                    ManifestContributionMeasurementContracts.All
                    ManifestContributionMeasurementContracts.DuplicateBacklogRecovery
                    missingHotManifest),
            Throws
                .TypeOf<InvalidOperationException>()
                .With
                .Message
                .EqualTo(
                    "Pre-stop terminal telemetry inventory mismatch: expected [Baseline, HotManifest, HighlySharedDirectoryVersion]; actual [Baseline, HighlySharedDirectoryVersion]."
                )
        )

    /// Verifies canonical metadata order matches execution, with Repair before terminal DeadLetter.
    [<Test>]
    member _.ScenarioContractsPreserveExecutionOrder() =
        let contracts = ManifestContributionMeasurementContracts.All

        let names =
            contracts
            |> Array.map (fun contract -> contract.Scenario)

        Assert.That(
            names,
            Is.EqualTo<string array>(
                [|
                    "Baseline"
                    "HotManifest"
                    "HighlySharedDirectoryVersion"
                    "DuplicateBacklogRecovery"
                    "RedisRestart"
                    "ServerRestartRecovery"
                    "Repair"
                    "DeadLetter"
                |]
            )
        )

        Assert.Multiple(
            Action (fun () ->
                Assert.DoesNotThrow(
                    Action (fun () ->
                        ManifestContributionMeasurementSupport.requireScenarioExecutionOrder contracts 6 ManifestContributionMeasurementContracts.Repair)
                )

                Assert.DoesNotThrow(Action(fun () -> ManifestContributionMeasurementSupport.requireScenarioExecutionComplete contracts contracts.Length))

                Assert.That(
                    Action (fun () ->
                        ManifestContributionMeasurementSupport.requireScenarioExecutionOrder contracts 6 ManifestContributionMeasurementContracts.DeadLetter),
                    Throws
                        .TypeOf<InvalidOperationException>()
                        .With.Message.EqualTo("Scenario execution order mismatch at index 6: expected 'Repair' but received 'DeadLetter'.")
                )

                Assert.That(
                    Action(fun () -> ManifestContributionMeasurementSupport.requireScenarioExecutionComplete contracts (contracts.Length - 1)),
                    Throws
                        .TypeOf<InvalidOperationException>()
                        .With.Message.EqualTo("Scenario execution ended after 7 entries; canonical contract requires 8.")
                ))
        )

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
                Action (fun () ->
                    Assert.That(assertion.GetProperty("passed").GetBoolean(), Is.False)
                    Assert.That(summary.GetProperty("passed").GetBoolean(), Is.False)
                    Assert.That(summary.GetProperty("assertionCount").GetInt32(), Is.EqualTo(1)))
            )
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    /// Verifies a scenario summary cannot pass when a declared assertion was never recorded.
    [<Test>]
    member _.ScenarioSummaryRejectsMissingAssertions() =
        let root = Path.Combine(Path.GetTempPath(), "grace-server-unit-tests", $"mca-missing-summary-{Guid.NewGuid():N}")

        try
            let sink = MeasurementEvidenceSink root
            sink.Assertion("Regression", "only-assertion", "Only recorded assertion", true, true, true, [||])
            sink.Summary("Regression", DateTimeOffset.UtcNow, 2, [||])

            let summary =
                ManifestContributionMeasurementSupport.readEvidenceRecords (Path.Combine(root, "summaries.ndjson"))
                |> Array.exactlyOne

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(summary.GetProperty("passed").GetBoolean(), Is.False)
                    Assert.That(summary.GetProperty("assertionCount").GetInt32(), Is.EqualTo(1)))
            )
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    /// Verifies repeated collected identities cannot satisfy the grouped scenario isolation contract.
    [<Test>]
    member _.ScenarioIsolationRequiresRealDistinctIdentities() =
        let contracts =
            [|
                { Scenario = "First"; ExpectedAssertionCount = 1; CreatedReferenceCount = 2; DistinctDirectoryVersionCount = 1 }
                { Scenario = "Second"; ExpectedAssertionCount = 1; CreatedReferenceCount = 1; DistinctDirectoryVersionCount = 1 }
            |]

        let isolation = ManifestContributionMeasurementSupport.evaluateIdentityIsolation contracts [ "ref-1"; "ref-2"; "ref-2" ] [ "dir-1"; "dir-2" ]

        let distinctIsolation = ManifestContributionMeasurementSupport.evaluateIdentityIsolation contracts [ "ref-1"; "ref-2"; "ref-3" ] [ "dir-1"; "dir-2" ]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(isolation.ExpectedReferenceCount, Is.EqualTo(3))
                Assert.That(isolation.ActualDistinctReferenceCount, Is.EqualTo(2))
                Assert.That(isolation.ExpectedDirectoryVersionCount, Is.EqualTo(2))
                Assert.That(isolation.ActualDistinctDirectoryVersionCount, Is.EqualTo(2))
                Assert.That(isolation.Passed, Is.False)
                Assert.That(distinctIsolation.ActualDistinctReferenceCount, Is.EqualTo(3))
                Assert.That(distinctIsolation.Passed, Is.True))
        )
