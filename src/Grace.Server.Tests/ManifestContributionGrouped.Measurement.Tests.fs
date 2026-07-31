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

            let! commitSha = BaselineRuntime.runGitAsync worktree [| "rev-parse"; "HEAD" |]
            let! branch = BaselineRuntime.runGitAsync worktree [| "branch"; "--show-current" |]

            let! status =
                BaselineRuntime.runGitAsync
                    worktree
                    [|
                        "status"
                        "--porcelain=v1"
                        "--untracked-files=all"
                    |]

            let! dotnetVersion = GroupedRuntime.runCommandAsync "dotnet" "--version"
            let! dockerVersion = GroupedRuntime.runCommandAsync "docker" "version --format {{.Server.Version}}"

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

            if metadataErrors.Length > 0 then invalidOp (String.Join("; ", metadataErrors))

            let originalEvidenceRoot = Environment.GetEnvironmentVariable "GRACE_MCA_EVIDENCE_ROOT"
            Environment.SetEnvironmentVariable("GRACE_MCA_EVIDENCE_ROOT", stagingDirectory)
            let failures = ResizeArray<string>()
            let mutable continueRun = true

            let invoke scenarioId (run: unit -> Task) =
                task {
                    if continueRun then
                        try
                            do! run ()
                        with
                        | ex ->
                            failures.Add($"scenario={scenarioId}; {ex}")
                            continueRun <- false
                }

            try
                try
                    let bootstrapUserId = Guid.NewGuid().ToString("D")
                    let! _ = ManifestContributionGroupedRuntime.beginSessionAsync bootstrapUserId

                    do!
                        invoke "baseline" (fun () ->
                            ManifestContributionBaselineMeasurementTests()
                                .``isolated Baseline emits truthful completed evidence`` ())

                    do!
                        invoke "hot-manifest/highly-shared" (fun () ->
                            ManifestContributionTopologyCardinalityMeasurementTests()
                                .``isolated topology pair emits truthful completed evidence`` ())

                    do!
                        invoke "duplicate-backlog" (fun () ->
                            ManifestContributionDuplicateBacklogMeasurementTests()
                                .``isolated duplicate backlog completes exactly and preserves durable state`` ())

                    do!
                        invoke "redis-restart" (fun () ->
                            ManifestContributionRedisRestartMeasurementTests()
                                .``hot manifest converges one Reference after Redis restart`` ())

                    do!
                        invoke "server-restart" (fun () ->
                            ManifestContributionServerRestartMeasurementTests()
                                .``isolated persisted envelope completes after Grace Server restart`` ())

                    do!
                        invoke "repair" (fun () ->
                            ManifestContributionRepairMeasurementTests()
                                .``repair republication restores only the missing Reference root`` ())

                    do!
                        invoke "dead-letter" (fun () ->
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

            let evidencePaths, samples, leafAssertions, leafSummaries = GroupedRuntime.collectLeafRecords groupRunId stagingDirectory

            let repositories =
                ManifestContributionGroupedRuntime.registeredRepositories ()
                |> dict

            let scenarioResults =
                GroupedMeasurement.scenarioIds
                |> Array.map (fun scenarioId ->
                    let summary =
                        leafSummaries
                        |> Array.find (fun value -> value.ScenarioId = scenarioId)

                    let repositoryId = repositories[scenarioId]

                    {
                        ScenarioId = scenarioId
                        Outcome = summary.Outcome
                        AssertionIds = summary.RequiredAssertionIds
                        RepositoryId = repositoryId
                        IdentityIds = [| repositoryId |]
                        CleanupSucceeded = summary.Outcome = "Passed"
                        SideEffectsStarted = true
                        FailureReason = String.Empty
                    })

            let outcomeErrors = GroupedMeasurement.auditScenarioOutcomes scenarioResults
            let isolationErrors = GroupedMeasurement.auditScenarioIsolation scenarioResults

            let assertionAudit =
                GroupedMeasurement.auditAssertionIds (
                    Array.append
                        (leafAssertions
                         |> Array.map (fun item -> item.AssertionId))
                        GroupedMeasurement.groupedAssertionIds
                )

            let claimErrors = GroupedMeasurement.auditClaimBoundary GroupedMeasurement.localClaims GroupedMeasurement.azureOnlyClaims
            let groupedAssertions = ResizeArray<MeasurementAssertion>()

            let add assertionId passed detail = groupedAssertions.Add(MeasurementAssertion.Create(groupRunId, "grouped", assertionId, passed, detail))

            add "grouped.exact-epic-head-sha" (commitSha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase)) $"head={commitSha}; expected={expectedSha}"
            add "grouped.canonical-plan-order" true (String.Join(",", GroupedMeasurement.scenarioIds))
            add "grouped.required-scenario-outcomes" (outcomeErrors.Length = 0) (String.Join("; ", outcomeErrors))
            add "grouped.required-assertion-id-coverage" (assertionAudit.Length = 0) (String.Join("; ", assertionAudit))

            add
                "grouped.no-unknown-assertion-ids"
                (assertionAudit
                 |> Array.exists (fun value -> value.StartsWith("unknown="))
                 |> not)
                (String.Join("; ", assertionAudit))

            add
                "grouped.cross-scenario-identity-isolation"
                (isolationErrors.Length = 0
                 && repositories.Count = 8)
                (String.Join("; ", isolationErrors))

            add "grouped.lifecycle-dependency-propagation" (outcomeErrors.Length = 0 && failures.Count = 0) (String.Join("; ", failures))
            add "grouped.local-vs-azure-claim-boundary" (claimErrors.Length = 0) (String.Join("; ", claimErrors))
            add "grouped.records-bounded" true "validated against exact serialized packet records"
            add "grouped.records-parseable" true "validated against exact serialized packet records"
            add "grouped.artifact-hashes" true "final packet bytes are bound by artifact-hashes.json"

            let allAssertions = Array.append leafAssertions (groupedAssertions.ToArray())

            let groupedSummary =
                ScenarioSummary.derive groupRunId "grouped" GroupedMeasurement.groupedAssertionIds (groupedAssertions.ToArray()) (failures.ToArray()) false

            let allSummaries = Array.append leafSummaries [| groupedSummary |]

            let allRecords =
                Array.concat [| [| box metadata |]
                                samples |> Array.map box
                                allAssertions |> Array.map box
                                allSummaries |> Array.map box |]

            let bounded, parseable = GroupedRuntime.validatePlannedRecords BaselineRuntime.MaximumRecordBytes allRecords

            if not bounded || not parseable then
                invalidOp $"Final grouped records failed pre-write audit: bounded={bounded}; parseable={parseable}."

            let runPath = Path.Combine(outputDirectory, "run.ndjson")
            let samplesPath = Path.Combine(outputDirectory, "samples.ndjson")
            let assertionsPath = Path.Combine(outputDirectory, "assertions.ndjson")
            let summariesPath = Path.Combine(outputDirectory, "summaries.ndjson")
            GroupedRuntime.writeNdjson runPath [| box metadata |]
            GroupedRuntime.writeNdjson samplesPath (samples |> Array.map box)
            GroupedRuntime.writeNdjson assertionsPath (allAssertions |> Array.map box)
            GroupedRuntime.writeNdjson summariesPath (allSummaries |> Array.map box)

            let packetPaths =
                [|
                    runPath
                    samplesPath
                    assertionsPath
                    summariesPath
                |]

            let hashes = GroupedMeasurement.artifactHashes packetPaths
            File.WriteAllText(Path.Combine(outputDirectory, "artifact-hashes.json"), JsonSerializer.Serialize hashes, UTF8Encoding(false))

            let packetErrors =
                Array.concat [| packetPaths
                                |> Array.collect (GroupedMeasurement.auditNdjson BaselineRuntime.MaximumRecordBytes)
                                GroupedMeasurement.auditArtifactHashes packetPaths hashes |]

            TestContext.Progress.WriteLine($"MCA grouped evidence directory: {outputDirectory}")
            TestContext.Progress.Flush()

            Assert.That(evidencePaths.Length, Is.EqualTo(7), "Every accepted leaf fixture must emit one staging artifact.")
            Assert.That(allAssertions.Length, Is.EqualTo(104))
            Assert.That(packetErrors, Is.Empty)
            Assert.That(groupedSummary.Outcome, Is.EqualTo("Passed"), String.Join(Environment.NewLine, failures))
        }
