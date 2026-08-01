namespace Grace.Server.Tests

open System
open System.IO
open System.Text
open System.Collections.Generic
open NUnit.Framework
open Grace.Server.Tests.Measurement

[<TestFixture>]
type ManifestContributionGroupedMeasurementTests() =

    /// Verifies the grouped measurement plan preserves the canonical leaf scenario order from Issue 763.
    [<Test>]
    member _.``grouped plan preserves canonical scenario order``() =
        let expected =
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

        Assert.That(GroupedMeasurement.scenarioIds = expected, Is.True)

    /// Verifies every scenario depends on the immediately preceding canonical leaf.
    [<Test>]
    member _.``grouped plan preserves dependency order``() =
        let dependencies =
            GroupedMeasurement.scenarioPlan
            |> Array.map (fun item -> item.DependsOn)

        let expected =
            [|
                Array.empty
                [| "baseline" |]
                [| "hot-manifest" |]
                [| "highly-shared" |]
                [| "duplicate-backlog" |]
                [| "redis-restart" |]
                [| "server-restart" |]
                [| "repair" |]
            |]

        let dependenciesMatch = dependencies = expected
        Assert.That(dependenciesMatch, Is.True)

    /// Verifies a failed lifecycle scenario skips every later mutation without invoking its executor.
    [<Test>]
    member _.``failed prerequisite propagates skips without later side effects``() =
        let invoked = ResizeArray<string>()

        let results =
            GroupedMeasurement.compose (fun scenario ->
                invoked.Add scenario.ScenarioId

                {
                    ScenarioId = scenario.ScenarioId
                    Outcome = if scenario.ScenarioId = "redis-restart" then "Failed" else "Passed"
                    AssertionIds = Array.empty
                    RepositoryId = $"repository-{scenario.ScenarioId}"
                    IdentityIds = [| $"identity-{scenario.ScenarioId}" |]
                    CleanupSucceeded = scenario.ScenarioId <> "redis-restart"
                    SideEffectsStarted = true
                    FailureReason = String.Empty
                })

        Assert.That(invoked.ToArray() = GroupedMeasurement.scenarioIds[0..4], Is.True)

        Assert.That(
            results[5..]
            |> Array.forall (fun result ->
                result.Outcome = "Skipped"
                && not result.SideEffectsStarted),
            Is.True
        )

    /// Verifies a missing failed leaf becomes a truthful failure and every dependent result remains materialized.
    [<Test>]
    member _.``failure aggregation preserves original failure and complete skipped ledger``() =
        let observed =
            GroupedMeasurement.scenarioIds[0..2]
            |> Array.map (fun scenarioId ->
                {
                    ScenarioId = scenarioId
                    Outcome = "Passed"
                    AssertionIds = Array.empty
                    RepositoryId = $"repository-{scenarioId}"
                    IdentityIds = [| $"identity-{scenarioId}" |]
                    CleanupSucceeded = true
                    SideEffectsStarted = true
                    FailureReason = String.Empty
                })

        let failed =
            {
                ScenarioId = "duplicate-backlog"
                Outcome = "Failed"
                AssertionIds = Array.empty
                RepositoryId = "repository-duplicate-backlog"
                IdentityIds = [| "identity-duplicate-backlog" |]
                CleanupSucceeded = true
                SideEffectsStarted = true
                FailureReason = "original duplicate-backlog failure"
            }

        let results = GroupedMeasurement.materializeResults (Array.append observed [| failed |])
        Assert.That(results.Length, Is.EqualTo(8))
        Assert.That(results[3].Outcome, Is.EqualTo("Failed"))
        Assert.That(results[3].FailureReason, Is.EqualTo("original duplicate-backlog failure"))

        Assert.That(
            results[4..]
            |> Array.forall (fun result ->
                result.Outcome = "Skipped"
                && not result.SideEffectsStarted),
            Is.True
        )

        let summaries = GroupedMeasurement.completeSummaries "run" results Array.empty
        Assert.That(summaries.Length, Is.EqualTo(8))
        Assert.That(summaries[3].Outcome, Is.EqualTo("Failed"))
        Assert.That(summaries[3].RuntimeFailures, Has.Some.Contains("original duplicate-backlog failure"))

        Assert.That(
            summaries[4..]
            |> Array.forall (fun summary -> summary.Outcome = "Skipped"),
            Is.True
        )

    /// Verifies grouped assertion truth is derived from concrete audit errors instead of caller-supplied success.
    [<Test>]
    member _.``grouped audit assertions derive pass and failure from recorded errors``() =
        let passed = GroupedMeasurement.auditAssertion "run" "grouped.canonical-plan-order" Array.empty "canonical"
        let failed = GroupedMeasurement.auditAssertion "run" "grouped.canonical-plan-order" [| "reordered" |] "canonical"
        Assert.That(passed.Passed, Is.True)
        Assert.That(failed.Passed, Is.False)
        Assert.That(failed.Detail, Does.Contain("reordered"))

    /// Verifies every canonical scenario contributes exact raw stimulus baseline and terminal metric snapshots.
    [<Test>]
    member _.``raw metric snapshot audit requires canonical baseline and terminal pairs``() =
        let samples =
            GroupedMeasurement.scenarioIds
            |> Array.collect (fun scenarioId ->
                [| "baseline"; "terminal" |]
                |> Array.collect (fun observation ->
                    let labels = Dictionary<string, string>()
                    labels["stage"] <- "settle"
                    labels["outcome"] <- "completed"
                    labels["phase"] <- "stimulus"
                    labels["observation"] <- observation

                    [|
                        MeasurementSample.Create(
                            "run",
                            scenarioId,
                            $"stimulus-{observation}-messages",
                            "grace_manifest_contribution_messages_total",
                            10L,
                            labels
                        )
                        MeasurementSample.Create(
                            "run",
                            scenarioId,
                            $"stimulus-{observation}-durations",
                            "grace_manifest_contribution_processing_duration_milliseconds_count",
                            10L,
                            labels
                        )
                    |]))

        Assert.That(GroupedMeasurement.auditRawMetricSnapshots samples, Is.Empty)
        Assert.That(GroupedMeasurement.auditRawMetricSnapshots samples[1..], Is.Not.Empty)

    /// Verifies raw snapshot capture retains the exact cumulative pair only after strict completed-only validation.
    [<Test>]
    member _.``raw metric snapshot capture rejects non completed series``() =
        let exact =
            """
grace_manifest_contribution_messages_total{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 41
grace_manifest_contribution_processing_duration_milliseconds_count{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="completed"} 42
"""

        match GroupedMeasurement.captureCompletedSettlementSnapshot exact with
        | Ok (messages, durations) ->
            Assert.That(messages, Is.EqualTo(41L))
            Assert.That(durations, Is.EqualTo(42L))
        | Error error -> Assert.Fail(error)

        let invalid =
            exact
            + """
grace_manifest_contribution_messages_total{otel_scope_name="Grace.ManifestContributionAccounting",stage="settle",outcome="settlement_failed"} 1
"""

        Assert.That(
            GroupedMeasurement.captureCompletedSettlementSnapshot invalid
            |> Result.isError,
            Is.True
        )

    /// Verifies the exact raw-packet assertion closure contains every leaf ID and the 11 live Issue 763 IDs once.
    [<Test>]
    member _.``required assertion closure is exact and unique``() =
        Assert.That(GroupedMeasurement.leafAssertionIds.Length, Is.EqualTo(93))
        Assert.That(GroupedMeasurement.groupedAssertionIds.Length, Is.EqualTo(11))
        Assert.That(GroupedMeasurement.requiredAssertionIds.Length, Is.EqualTo(104))
        Assert.That(GroupedMeasurement.auditAssertionIds GroupedMeasurement.requiredAssertionIds, Is.Empty)

    /// Verifies missing, duplicate, and unknown assertion IDs each fail the closure audit.
    [<Test>]
    member _.``assertion audit rejects missing duplicate and unknown IDs``() =
        let invalid =
            Array.concat [| GroupedMeasurement.requiredAssertionIds[1..]
                            [|
                                GroupedMeasurement.requiredAssertionIds[1]
                                "grouped.unknown"
                            |] |]

        let errors = GroupedMeasurement.auditAssertionIds invalid
        Assert.That(errors, Has.Some.EqualTo("missing=baseline.setup-deliveries-completed"))
        Assert.That(errors, Has.Some.EqualTo("duplicate=baseline.stimulus-deliveries-completed"))
        Assert.That(errors, Has.Some.EqualTo("unknown=grouped.unknown"))

    /// Verifies exact SHA, clean state, runtime versions, and canonical plan are mandatory metadata.
    [<Test>]
    member _.``run metadata must agree with exact clean head``() =
        let metadata =
            {
                CommitSha = "abc"
                Branch = "agent/763-mca-grouped-runtime"
                Dirty = false
                Command = "dotnet test --filter grouped"
                DotnetVersion = "10.0.100"
                DockerVersion = "28.0"
                ScenarioIds = Array.copy GroupedMeasurement.scenarioIds
            }

        Assert.That(GroupedMeasurement.auditMetadata "abc" "agent/763-mca-grouped-runtime" metadata, Is.Empty)
        Assert.That(GroupedMeasurement.auditMetadata "def" "agent/763-mca-grouped-runtime" { metadata with Dirty = true }, Is.Not.Empty)

    /// Verifies scenario-local Repository and retained identities cannot collide across leaves.
    [<Test>]
    member _.``cross scenario identity collisions fail``() =
        let result scenario repository identity =
            {
                ScenarioId = scenario
                Outcome = "Passed"
                AssertionIds = Array.empty
                RepositoryId = repository
                IdentityIds = [| identity |]
                CleanupSucceeded = true
                SideEffectsStarted = true
                FailureReason = String.Empty
            }

        let errors =
            GroupedMeasurement.auditScenarioIsolation [| result "baseline" "repo" "identity"
                                                         result "hot-manifest" "repo" "identity" |]

        Assert.That(errors.Length, Is.EqualTo(2))

    /// Verifies the exact local-only and Azure-only claim sets remain disjoint and complete.
    [<Test>]
    member _.``local and Azure claim boundary is exact``() =
        Assert.That(GroupedMeasurement.auditClaimBoundary GroupedMeasurement.localClaims GroupedMeasurement.azureOnlyClaims, Is.Empty)

        let invalidAzure = Array.append GroupedMeasurement.azureOnlyClaims [| GroupedMeasurement.localClaims[0] |]
        Assert.That(GroupedMeasurement.auditClaimBoundary GroupedMeasurement.localClaims invalidAzure, Is.Not.Empty)

    /// Verifies raw packet records are both parseable and bounded after UTF-8 serialization.
    [<Test>]
    member _.``NDJSON audit rejects malformed and oversized records``() =
        let directory = Path.Combine(Path.GetTempPath(), $"grace-grouped-audit-{Guid.NewGuid():N}")
        Directory.CreateDirectory directory |> ignore
        let validPath = Path.Combine(directory, "valid.ndjson")
        let invalidPath = Path.Combine(directory, "invalid.ndjson")

        try
            File.WriteAllText(
                validPath,
                "{\"recordType\":\"sample\"}"
                + Environment.NewLine,
                UTF8Encoding(false)
            )

            File.WriteAllText(
                invalidPath,
                "{broken}"
                + Environment.NewLine
                + "{\"value\":\"0123456789\"}"
                + Environment.NewLine,
                UTF8Encoding(false)
            )

            Assert.That(GroupedMeasurement.auditNdjson 64 validPath, Is.Empty)
            Assert.That(GroupedMeasurement.auditNdjson 8 invalidPath, Is.Not.Empty)
        finally
            Directory.Delete(directory, true)

    /// Verifies recorded artifact hashes bind the final packet to its exact bytes.
    [<Test>]
    member _.``artifact hash audit rejects changed bytes``() =
        let directory = Path.Combine(Path.GetTempPath(), $"grace-grouped-hash-{Guid.NewGuid():N}")
        Directory.CreateDirectory directory |> ignore
        let path = Path.Combine(directory, "run.ndjson")

        try
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("abc"))
            let recorded = GroupedMeasurement.artifactHashes [| path |]
            Assert.That(recorded[0].Sha256, Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"))
            Assert.That(GroupedMeasurement.auditArtifactHashes [| path |] recorded, Is.Empty)
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("changed"))
            Assert.That(GroupedMeasurement.auditArtifactHashes [| path |] recorded, Is.Not.Empty)
        finally
            Directory.Delete(directory, true)
