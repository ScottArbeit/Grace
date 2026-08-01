namespace Grace.Server.Measurements

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open NUnit.Framework
open Grace.Server.Tests
open Grace.Server.Tests.Measurement

module private GroupedRuntime =

    let private options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    /// Runs one bounded metadata command and returns its trimmed standard output.
    let runCommandAsync (fileName: string) (arguments: string) =
        task {
            let startInfo = ProcessStartInfo(fileName, arguments)
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.UseShellExecute <- false
            startInfo.CreateNoWindow <- true
            use proc = new Process(StartInfo = startInfo)

            if not (proc.Start()) then invalidOp $"Failed to start {fileName}."

            let outputTask = proc.StandardOutput.ReadToEndAsync()
            let errorTask = proc.StandardError.ReadToEndAsync()
            do! proc.WaitForExitAsync()
            let! output = outputTask
            let! error = errorTask

            if proc.ExitCode <> 0 then
                invalidOp $"{fileName} exited {proc.ExitCode}: {error.Trim()}"

            return output.Trim()
        }

    /// Reads accepted leaf records and projects them onto the one grouped RunId.
    let collectLeafRecords groupRunId stagingDirectory =
        let samples = ResizeArray<MeasurementSample>()
        let assertions = ResizeArray<MeasurementAssertion>()
        let summaries = ResizeArray<ScenarioSummary>()

        let evidencePaths = Directory.GetFiles(stagingDirectory, "evidence.ndjson", SearchOption.AllDirectories)

        evidencePaths
        |> Array.iter (fun path ->
            File.ReadLines path
            |> Seq.iter (fun line ->
                use document = JsonDocument.Parse line

                let recordType =
                    document
                        .RootElement
                        .GetProperty("RecordType")
                        .GetString()

                match recordType with
                | "MeasurementSample" ->
                    let value = JsonSerializer.Deserialize<MeasurementSample>(line, options)
                    samples.Add({ value with RunId = groupRunId })
                | "MeasurementAssertion" ->
                    let value = JsonSerializer.Deserialize<MeasurementAssertion>(line, options)
                    assertions.Add({ value with RunId = groupRunId })
                | "ScenarioSummary" ->
                    let value = JsonSerializer.Deserialize<ScenarioSummary>(line, options)
                    summaries.Add({ value with RunId = groupRunId })
                | "MeasurementRun" -> ()
                | value -> invalidOp $"Unknown leaf evidence record type '{value}' in {path}."))

        evidencePaths, samples.ToArray(), assertions.ToArray(), summaries.ToArray()

    /// Serializes complete UTF-8 NDJSON records without a BOM.
    let writeNdjson path (records: obj array) =
        let lines = records |> Array.map JsonSerializer.Serialize
        File.WriteAllLines(path, lines, UTF8Encoding(false))

    /// Checks planned records against the exact writer byte and JSON bounds before final packet emission.
    let validatePlannedRecords maximumBytes (records: obj array) =
        let mutable bounded = true
        let mutable parseable = true

        records
        |> Array.iter (fun record ->
            let line = JsonSerializer.Serialize record

            bounded <-
                bounded
                && Encoding.UTF8.GetByteCount line <= maximumBytes

            try
                use _ = JsonDocument.Parse line
                ()
            with
            | :? JsonException -> parseable <- false)

        bounded, parseable

[<TestFixture>]
[<NonParallelizable>]
type ManifestContributionGroupedMeasurementTests() =

    /// Runs all accepted leaf fixtures in canonical order inside one fixture-owned Aspire session and emits one exact-head packet.
    [<Test; Explicit("Run only through the focused MCA grouped measurement selector.")>]
    member _.``grouped exact head run composes every accepted leaf``() =
        task {
            let groupRunId = Guid.NewGuid().ToString("N")

            let worktree =
                BaselineRuntime.requireEnvironment "GRACE_MCA_WORKTREE"
                |> Path.GetFullPath

            let command = BaselineRuntime.requireEnvironment "GRACE_MCA_HOSTED_COMMAND"

            let evidenceRoot =
                BaselineRuntime.requireEnvironment "GRACE_MCA_EVIDENCE_ROOT"
                |> Path.GetFullPath

            let expectedSha = BaselineRuntime.requireEnvironment "GRACE_MCA_EXPECTED_SHA"
            let outputDirectory = Path.Combine(evidenceRoot, groupRunId)
            let stagingDirectory = Path.Combine(outputDirectory, "leaf-staging")

            Directory.CreateDirectory stagingDirectory
            |> ignore

            let failures = ResizeArray<string>()
            let attemptedScenarioIds = HashSet<string>(StringComparer.Ordinal)
            let scenarioFailures = Dictionary<string, string>(StringComparer.Ordinal)
            let mutable commitSha = String.Empty
            let mutable branch = String.Empty
            let mutable status = "preflight-not-completed"
            let mutable dotnetVersion = String.Empty
            let mutable dockerVersion = String.Empty

            try
                let! observedCommitSha = BaselineRuntime.runGitAsync worktree [| "rev-parse"; "HEAD" |]
                commitSha <- observedCommitSha
                let! observedBranch = BaselineRuntime.runGitAsync worktree [| "branch"; "--show-current" |]
                branch <- observedBranch

                let! observedStatus =
                    BaselineRuntime.runGitAsync
                        worktree
                        [|
                            "status"
                            "--porcelain=v1"
                            "--untracked-files=all"
                        |]

                status <- observedStatus
                let! observedDotnetVersion = GroupedRuntime.runCommandAsync "dotnet" "--version"
                dotnetVersion <- observedDotnetVersion
                let! observedDockerVersion = GroupedRuntime.runCommandAsync "docker" "version --format {{.Server.Version}}"
                dockerVersion <- observedDockerVersion
            with
            | ex -> failures.Add($"grouped-preflight: {ex}")

            let metadata =
                {
                    CommitSha = commitSha
                    Branch = branch
                    Dirty = not (String.IsNullOrWhiteSpace status)
                    Command = command
                    DotnetVersion = dotnetVersion
                    DockerVersion = dockerVersion
                    ScenarioIds = Array.copy GroupedMeasurement.scenarioIds
                }

            let metadataErrors = GroupedMeasurement.auditMetadata expectedSha branch metadata

            metadataErrors
            |> Array.iter (fun error -> failures.Add($"grouped-metadata: {error}"))

            let originalEvidenceRoot = Environment.GetEnvironmentVariable "GRACE_MCA_EVIDENCE_ROOT"
            Environment.SetEnvironmentVariable("GRACE_MCA_EVIDENCE_ROOT", stagingDirectory)
            let mutable continueRun = failures.Count = 0

            let invoke (scenarioIds: string array) (run: unit -> Task) =
                task {
                    if continueRun then
                        scenarioIds
                        |> Array.iter (attemptedScenarioIds.Add >> ignore)

                        try
                            do! run ()
                        with
                        | ex ->
                            let combinedScenarioId = String.Join("+", scenarioIds)
                            let detail = $"scenario={combinedScenarioId}; {ex}"
                            failures.Add detail

                            scenarioIds
                            |> Array.iter (fun scenarioId -> scenarioFailures[scenarioId] <- detail)

                            continueRun <- false
                }

            try
                try
                    let bootstrapUserId = Guid.NewGuid().ToString("D")
                    let! _ = ManifestContributionGroupedRuntime.beginSessionAsync bootstrapUserId

                    do!
                        invoke [| "baseline" |] (fun () ->
                            ManifestContributionBaselineMeasurementTests()
                                .``isolated Baseline emits truthful completed evidence`` ())

                    do!
                        invoke [| "hot-manifest"; "highly-shared" |] (fun () ->
                            ManifestContributionTopologyCardinalityMeasurementTests()
                                .``isolated topology pair emits truthful completed evidence`` ())

                    do!
                        invoke [| "duplicate-backlog" |] (fun () ->
                            ManifestContributionDuplicateBacklogMeasurementTests()
                                .``isolated duplicate backlog completes exactly and preserves durable state`` ())

                    do!
                        invoke [| "redis-restart" |] (fun () ->
                            ManifestContributionRedisRestartMeasurementTests()
                                .``hot manifest converges one Reference after Redis restart`` ())

                    do!
                        invoke [| "server-restart" |] (fun () ->
                            ManifestContributionServerRestartMeasurementTests()
                                .``isolated persisted envelope completes after Grace Server restart`` ())

                    do!
                        invoke [| "repair" |] (fun () ->
                            ManifestContributionRepairMeasurementTests()
                                .``repair republication restores only the missing Reference root`` ())

                    do!
                        invoke [| "dead-letter" |] (fun () ->
                            ManifestContributionDeadLetterMeasurementTests()
                                .``isolated broker witness reaches dead-letter delivery eleven`` ())
                with
                | ex -> failures.Add($"grouped-session: {ex}")

                try
                    do! ManifestContributionGroupedRuntime.endSessionAsync ()
                with
                | ex -> failures.Add($"grouped-cleanup: {ex}")
            finally
                Environment.SetEnvironmentVariable("GRACE_MCA_EVIDENCE_ROOT", originalEvidenceRoot)

            let evidencePaths, samples, leafAssertions, leafSummaries =
                try
                    GroupedRuntime.collectLeafRecords groupRunId stagingDirectory
                with
                | ex ->
                    failures.Add($"grouped-evidence-collection: {ex}")
                    Array.empty, Array.empty, Array.empty, Array.empty

            let repositories =
                ManifestContributionGroupedRuntime.registeredRepositories ()
                |> dict

            let observedResults = ResizeArray<GroupedScenarioResult>()

            leafSummaries
            |> Array.iter (fun summary ->
                let repositoryId =
                    match repositories.TryGetValue summary.ScenarioId with
                    | true, value -> value
                    | _ -> String.Empty

                let failureReason =
                    if summary.Outcome = "Failed" then
                        let failedAssertions = String.Join(",", summary.FailedAssertionIds)
                        let runtimeFailures = String.Join("; ", summary.RuntimeFailures)
                        $"scenario={summary.ScenarioId}; failedAssertions={failedAssertions}; runtimeFailures={runtimeFailures}"
                    else
                        String.Empty

                observedResults.Add(
                    {
                        ScenarioId = summary.ScenarioId
                        Outcome = summary.Outcome
                        AssertionIds = summary.RequiredAssertionIds
                        RepositoryId = repositoryId
                        IdentityIds =
                            if String.IsNullOrWhiteSpace repositoryId then
                                Array.empty
                            else
                                [| repositoryId |]
                        CleanupSucceeded = summary.Outcome = "Passed"
                        SideEffectsStarted = true
                        FailureReason = failureReason
                    }
                ))

            GroupedMeasurement.scenarioIds
            |> Array.filter (fun scenarioId ->
                attemptedScenarioIds.Contains scenarioId
                && (leafSummaries
                    |> Array.exists (fun summary -> summary.ScenarioId = scenarioId)
                    |> not))
            |> Array.iter (fun scenarioId ->
                let failureReason =
                    match scenarioFailures.TryGetValue scenarioId with
                    | true, value -> value
                    | _ -> $"scenario={scenarioId}; terminal summary was not emitted"

                let repositoryId =
                    match repositories.TryGetValue scenarioId with
                    | true, value -> value
                    | _ -> String.Empty

                observedResults.Add(
                    {
                        ScenarioId = scenarioId
                        Outcome = "Failed"
                        AssertionIds = Array.empty
                        RepositoryId = repositoryId
                        IdentityIds =
                            if String.IsNullOrWhiteSpace repositoryId then
                                Array.empty
                            else
                                [| repositoryId |]
                        CleanupSucceeded = false
                        SideEffectsStarted = true
                        FailureReason = failureReason
                    }
                ))

            let scenarioResults = GroupedMeasurement.materializeResults (observedResults.ToArray())
            let completedLeafSummaries = GroupedMeasurement.completeSummaries groupRunId scenarioResults leafSummaries

            let groupedFailures = ResizeArray<string>(failures)

            scenarioResults
            |> Array.filter (fun result ->
                result.Outcome = "Failed"
                && not (String.IsNullOrWhiteSpace result.FailureReason))
            |> Array.iter (fun result -> groupedFailures.Add result.FailureReason)

            let groupedFailureLedger = groupedFailures |> Seq.distinct |> Seq.toArray

            let outcomeErrors = GroupedMeasurement.auditScenarioOutcomes scenarioResults
            let isolationErrors = GroupedMeasurement.auditScenarioIsolation scenarioResults
            let lifecycleErrors = GroupedMeasurement.auditLifecycleDependencyPropagation scenarioResults
            let rawMetricErrors = GroupedMeasurement.auditRawMetricSnapshots samples

            let assertionAudit =
                GroupedMeasurement.auditAssertionIds (
                    Array.append
                        (leafAssertions
                         |> Array.map (fun item -> item.AssertionId))
                        GroupedMeasurement.groupedAssertionIds
                )

            let claimErrors = GroupedMeasurement.auditClaimBoundary GroupedMeasurement.localClaims GroupedMeasurement.azureOnlyClaims
            let groupedAssertions = ResizeArray<MeasurementAssertion>()

            let addAudit assertionId errors successDetail = groupedAssertions.Add(GroupedMeasurement.auditAssertion groupRunId assertionId errors successDetail)

            let canonicalPlanErrors =
                [|
                    if metadata.ScenarioIds
                       <> GroupedMeasurement.scenarioIds then
                        "metadata plan is not canonical"
                    if scenarioResults
                       |> Array.map (fun result -> result.ScenarioId)
                       <> GroupedMeasurement.scenarioIds then
                        "outcome ledger plan is not canonical"
                |]

            let unknownAssertionErrors =
                assertionAudit
                |> Array.filter (fun value -> value.StartsWith("unknown="))

            let repositoryCountErrors =
                if repositories.Count = 8 then
                    Array.empty
                else
                    [|
                        $"repository-count={repositories.Count}; expected=8"
                    |]

            addAudit "grouped.exact-epic-head-sha" metadataErrors $"head={commitSha}; expected={expectedSha}"
            addAudit "grouped.canonical-plan-order" canonicalPlanErrors (String.Join(",", GroupedMeasurement.scenarioIds))
            addAudit "grouped.required-scenario-outcomes" outcomeErrors "all canonical scenarios passed with cleanup"
            addAudit "grouped.required-assertion-id-coverage" assertionAudit "exact leaf and grouped assertion closure observed"
            addAudit "grouped.no-unknown-assertion-ids" unknownAssertionErrors "no unknown assertion IDs observed"

            addAudit
                "grouped.cross-scenario-identity-isolation"
                (Array.append isolationErrors repositoryCountErrors)
                "eight scenario-local identities are disjoint"

            addAudit "grouped.lifecycle-dependency-propagation" lifecycleErrors "failed prerequisites propagate side-effect-free skips"
            addAudit "grouped.local-vs-azure-claim-boundary" claimErrors "local and Azure-only claim sets are exact"

            let baseRecords =
                Array.concat [| [| box metadata |]
                                samples |> Array.map box
                                leafAssertions |> Array.map box
                                completedLeafSummaries |> Array.map box |]

            let bounded, parseable = GroupedRuntime.validatePlannedRecords BaselineRuntime.MaximumRecordBytes baseRecords

            let boundedErrors =
                if bounded then
                    Array.empty
                else
                    [|
                        "one or more exact serialized records exceed the byte bound"
                    |]

            let parseableErrors =
                Array.concat [| if parseable then
                                    Array.empty
                                else
                                    [|
                                        "one or more exact serialized records are not parseable JSON"
                                    |]
                                rawMetricErrors |]

            addAudit "grouped.records-bounded" boundedErrors "exact serialized records satisfy the byte bound"
            addAudit "grouped.records-parseable" parseableErrors "exact serialized records parse and contain raw metric snapshots"

            addAudit "grouped.artifact-hashes" [| "pending exact packet hash audit" |] "final packet bytes pass exact SHA-256 audit"

            let allAssertions = Array.append leafAssertions (groupedAssertions.ToArray())

            let groupedSummary =
                ScenarioSummary.derive groupRunId "grouped" GroupedMeasurement.groupedAssertionIds (groupedAssertions.ToArray()) groupedFailureLedger false

            let allSummaries = Array.append completedLeafSummaries [| groupedSummary |]

            let runPath = Path.Combine(outputDirectory, "run.ndjson")
            let samplesPath = Path.Combine(outputDirectory, "samples.ndjson")
            let assertionsPath = Path.Combine(outputDirectory, "assertions.ndjson")
            let summariesPath = Path.Combine(outputDirectory, "summaries.ndjson")

            let packetPaths =
                [|
                    runPath
                    samplesPath
                    assertionsPath
                    summariesPath
                |]

            let writePacket (assertions: MeasurementAssertion array) (summaries: ScenarioSummary array) =
                GroupedRuntime.writeNdjson runPath [| box metadata |]
                GroupedRuntime.writeNdjson samplesPath (samples |> Array.map box)
                GroupedRuntime.writeNdjson assertionsPath (assertions |> Array.map box)
                GroupedRuntime.writeNdjson summariesPath (summaries |> Array.map box)
                let hashes = GroupedMeasurement.artifactHashes packetPaths
                File.WriteAllText(Path.Combine(outputDirectory, "artifact-hashes.json"), JsonSerializer.Serialize hashes, UTF8Encoding(false))

                let ndjsonErrors =
                    packetPaths
                    |> Array.collect (GroupedMeasurement.auditNdjson BaselineRuntime.MaximumRecordBytes)

                let hashErrors = GroupedMeasurement.auditArtifactHashes packetPaths hashes
                ndjsonErrors, hashErrors

            let firstNdjsonErrors, firstHashErrors = writePacket allAssertions allSummaries

            let replaceAudit assertionId errors successDetail (assertions: MeasurementAssertion array) =
                assertions
                |> Array.map (fun assertion ->
                    if assertion.AssertionId = assertionId then
                        GroupedMeasurement.auditAssertion groupRunId assertionId errors successDetail
                    else
                        assertion)

            let actualBoundedErrors =
                firstNdjsonErrors
                |> Array.filter (fun error -> error.StartsWith("oversized-line="))

            let actualParseableErrors =
                Array.concat [| firstNdjsonErrors
                                |> Array.filter (fun error -> not (error.StartsWith("oversized-line=")))
                                rawMetricErrors |]

            let finalGroupedAssertions =
                groupedAssertions.ToArray()
                |> replaceAudit "grouped.records-bounded" actualBoundedErrors "final NDJSON records satisfy the byte bound"
                |> replaceAudit "grouped.records-parseable" actualParseableErrors "final NDJSON records parse and contain raw metric snapshots"
                |> replaceAudit "grouped.artifact-hashes" firstHashErrors "final packet bytes pass exact SHA-256 audit"

            let finalAllAssertions = Array.append leafAssertions finalGroupedAssertions

            let finalGroupedSummary =
                ScenarioSummary.derive groupRunId "grouped" GroupedMeasurement.groupedAssertionIds finalGroupedAssertions groupedFailureLedger false

            let finalAllSummaries = Array.append completedLeafSummaries [| finalGroupedSummary |]
            let finalNdjsonErrors, finalHashErrors = writePacket finalAllAssertions finalAllSummaries

            let packetErrors =
                Array.concat [| finalNdjsonErrors
                                finalHashErrors
                                GroupedMeasurement.auditAssertionIds (
                                    finalAllAssertions
                                    |> Array.map (fun assertion -> assertion.AssertionId)
                                )
                                rawMetricErrors |]

            TestContext.Progress.WriteLine($"MCA grouped evidence directory: {outputDirectory}")
            TestContext.Progress.Flush()

            Assert.That(evidencePaths.Length, Is.EqualTo(7), "Every accepted leaf fixture must emit one staging artifact.")
            Assert.That(finalAllAssertions.Length, Is.EqualTo(104))
            Assert.That(packetErrors, Is.Empty)
            Assert.That(finalGroupedSummary.Outcome, Is.EqualTo("Passed"), String.Join(Environment.NewLine, groupedFailureLedger))
        }
