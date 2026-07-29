namespace Grace.Server.Tests

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks

/// Carries the stable result fields returned by an Aspire resource command.
type ResourceCommandObservation = { Success: bool; Canceled: bool; Message: string }

/// Classifies one Aspire command before readiness checks begin.
type ResourceCommandOutcome =
    | Completed
    | Canceled of message: string
    | Failed of message: string

/// Carries one typed raw measurement emitted by the manifest-accounting fixture.
type MeasurementSample =
    {
        schemaVersion: string
        scenario: string
        sampleType: string
        sequence: int
        timestampUtc: DateTimeOffset
        correlationKey: string
        measurements: IReadOnlyDictionary<string, obj>
    }

/// Carries one false-positive-resistant assertion and its raw evidence files.
type MeasurementAssertion =
    {
        assertionId: string
        scenario: string
        description: string
        expected: string
        actual: string
        passed: bool
        evidenceFiles: string array
    }

/// Summarizes one completed runtime scenario without claiming unsupported Azure behavior.
type ScenarioSummary =
    {
        scenario: string
        startedAtUtc: DateTimeOffset
        completedAtUtc: DateTimeOffset
        passed: bool
        assertionCount: int
        evidenceFiles: string array
    }

/// Identifies one local measurement run and the scenarios selected for its shared Aspire session.
type MeasurementRun =
    {
        schemaVersion: string
        runId: string
        environment: string
        startedAtUtc: DateTimeOffset
        scenarios: string array
        unmeasured: string array
    }

/// Declares the assertion and identity cardinalities one runtime scenario must contribute.
type MeasurementScenarioContract = { Scenario: string; ExpectedAssertionCount: int; CreatedReferenceCount: int; DistinctDirectoryVersionCount: int }

/// Reports whether collected runtime identities satisfy the grouped scenario contract.
type MeasurementIdentityIsolation =
    {
        ExpectedReferenceCount: int
        ActualDistinctReferenceCount: int
        ExpectedDirectoryVersionCount: int
        ActualDistinctDirectoryVersionCount: int
        Passed: bool
    }

/// Centralizes the grouped runtime scenario contract so summaries and isolation proof share one declaration.
module ManifestContributionMeasurementContracts =

    /// Declares the baseline scenario's assertions and newly created identities.
    let Baseline = { Scenario = "Baseline"; ExpectedAssertionCount = 12; CreatedReferenceCount = 2; DistinctDirectoryVersionCount = 2 }

    /// Declares the hot-manifest scenario's assertions and newly created identities.
    let HotManifest = { Scenario = "HotManifest"; ExpectedAssertionCount = 5; CreatedReferenceCount = 3; DistinctDirectoryVersionCount = 3 }

    /// Declares the highly shared root scenario's assertions and newly created identities.
    let HighlySharedDirectoryVersion =
        { Scenario = "HighlySharedDirectoryVersion"; ExpectedAssertionCount = 4; CreatedReferenceCount = 3; DistinctDirectoryVersionCount = 1 }

    /// Declares the duplicate backlog scenario's six assertions and replay-only identity behavior.
    let DuplicateBacklogRecovery =
        { Scenario = "DuplicateBacklogRecovery"; ExpectedAssertionCount = 6; CreatedReferenceCount = 0; DistinctDirectoryVersionCount = 0 }

    /// Declares the Redis restart scenario's assertions and newly created identities.
    let RedisRestart = { Scenario = "RedisRestart"; ExpectedAssertionCount = 4; CreatedReferenceCount = 1; DistinctDirectoryVersionCount = 1 }

    /// Declares the server restart scenario's assertions and replay-only identity behavior.
    let ServerRestartRecovery = { Scenario = "ServerRestartRecovery"; ExpectedAssertionCount = 4; CreatedReferenceCount = 0; DistinctDirectoryVersionCount = 0 }

    /// Declares the dead-letter scenario's assertions and absence of created repository identities.
    let DeadLetter = { Scenario = "DeadLetter"; ExpectedAssertionCount = 3; CreatedReferenceCount = 0; DistinctDirectoryVersionCount = 0 }

    /// Declares the repair scenario's assertions and newly created identities.
    let Repair = { Scenario = "Repair"; ExpectedAssertionCount = 10; CreatedReferenceCount = 1; DistinctDirectoryVersionCount = 1 }

    /// Lists every scenario executed by the grouped runtime fixture in execution order.
    let All =
        [|
            Baseline
            HotManifest
            HighlySharedDirectoryVersion
            DuplicateBacklogRecovery
            RedisRestart
            ServerRestartRecovery
            DeadLetter
            Repair
        |]

/// Provides pure classification and bounded evidence behavior used by the hosted measurement fixture.
module ManifestContributionMeasurementSupport =

    let private evidenceWriteLock = obj ()

    /// Caps any one structured diagnostic payload before it is attached to a test failure.
    [<Literal>]
    let MaximumDiagnosticCharacters = 4096

    /// Caps one NDJSON record so a failed scenario cannot create unbounded evidence.
    [<Literal>]
    let MaximumEvidenceRecordBytes = 65536

    /// Classifies a resource-command result without treating a response label as runtime readiness.
    let classifyResourceCommand (observation: ResourceCommandObservation) : ResourceCommandOutcome =
        let messageOr fallback =
            if String.IsNullOrWhiteSpace observation.Message then
                fallback
            else
                observation.Message.Trim()

        if observation.Success then
            ResourceCommandOutcome.Completed
        elif observation.Canceled then
            ResourceCommandOutcome.Canceled(messageOr "Resource command was canceled.")
        else
            ResourceCommandOutcome.Failed(messageOr "Resource command failed without details.")

    /// Reports whether a built-in command must be followed by a healthy-resource wait.
    let commandRequiresHealthyResource (commandName: string) : bool =
        commandName.Equals("resource-start", StringComparison.OrdinalIgnoreCase)
        || commandName.Equals("resource-restart", StringComparison.OrdinalIgnoreCase)

    /// Polls until a terminal observation is reached or the bounded wait expires.
    let waitForTerminalStateAsync
        (timeout: TimeSpan)
        (pollInterval: TimeSpan)
        (observeAsync: unit -> Task<'state>)
        (isTerminal: 'state -> bool)
        : Task<'state>
        =
        task {
            let stopwatch = Diagnostics.Stopwatch.StartNew()
            let! initialObservation = observeAsync ()
            let mutable current = initialObservation

            while stopwatch.Elapsed < timeout
                  && not (isTerminal current) do
                do! Task.Delay pollInterval
                let! nextObservation = observeAsync ()
                current <- nextObservation

            if not (isTerminal current) then
                raise (TimeoutException("Timed out waiting for terminal observed state."))

            return current
        }

    /// Compares collected identities with the distinct cardinalities required by all scenario contracts.
    let evaluateIdentityIsolation
        (contracts: MeasurementScenarioContract array)
        (referenceIdentities: string seq)
        (directoryVersionIdentities: string seq)
        : MeasurementIdentityIsolation
        =
        let expectedReferences =
            contracts
            |> Array.sumBy (fun contract -> contract.CreatedReferenceCount)

        let expectedDirectoryVersions =
            contracts
            |> Array.sumBy (fun contract -> contract.DistinctDirectoryVersionCount)

        let actualReferences = referenceIdentities |> Seq.distinct |> Seq.length

        let actualDirectoryVersions =
            directoryVersionIdentities
            |> Seq.distinct
            |> Seq.length

        {
            ExpectedReferenceCount = expectedReferences
            ActualDistinctReferenceCount = actualReferences
            ExpectedDirectoryVersionCount = expectedDirectoryVersions
            ActualDistinctDirectoryVersionCount = actualDirectoryVersions
            Passed =
                actualReferences = expectedReferences
                && actualDirectoryVersions = expectedDirectoryVersions
        }

    /// Replaces recognized credential values while retaining non-secret endpoint and state context.
    let private redactDiagnosticSecrets (diagnostic: string) =
        Regex.Replace(diagnostic, "(?i)(AccountKey|SharedAccessKey|SharedAccessSignature|Password)=([^;\\r\\n]*)", "$1=***", RegexOptions.CultureInvariant)

    /// Redacts secret-bearing connection-string segments and bounds the resulting diagnostic.
    let formatBoundedDiagnostic (context: string) (resourceState: string) (logs: string list) : string =
        let joinedLogs =
            logs
            |> List.truncate 50
            |> String.concat Environment.NewLine

        let diagnostic =
            $"Context: {context}{Environment.NewLine}Resource: {resourceState}{Environment.NewLine}Logs:{Environment.NewLine}{joinedLogs}"
            |> redactDiagnosticSecrets

        if diagnostic.Length <= MaximumDiagnosticCharacters then
            diagnostic
        else
            let suffix = $"{Environment.NewLine}[diagnostic truncated]"

            diagnostic.Substring(0, MaximumDiagnosticCharacters - suffix.Length)
            + suffix

    /// Sums matching OpenMetrics sample values while ignoring the optional scrape timestamp field.
    let sumOpenMetricsSamples (predicate: string -> bool) (metrics: string) : float =
        metrics.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            if line.StartsWith("#", StringComparison.Ordinal) then
                None
            else
                let labelEnd = line.IndexOf('}')

                let valueStart = if labelEnd >= 0 then labelEnd + 1 else line.IndexOf(' ')

                let labelStart = line.IndexOf('{')
                let nameEnd = if labelStart >= 0 then labelStart else valueStart

                if valueStart <= 0 || nameEnd <= 0 then
                    None
                else
                    let sampleName = line.Substring(0, nameEnd)

                    let valueFields =
                        line
                            .Substring(valueStart)
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries)

                    let mutable value = 0.0

                    if
                        valueFields.Length > 0 && predicate sampleName
                        && Double.TryParse(valueFields[0], NumberStyles.Float, CultureInfo.InvariantCulture, &value)
                    then
                        Some value
                    else
                        None)
        |> Array.sum

    /// Appends one UTF-8 no-BOM NDJSON record as a single locked write.
    let appendEvidenceRecord (path: string) (record: obj) : unit =
        if isNull record then nullArg (nameof record)

        let serializerOptions = JsonSerializerOptions(Grace.Shared.Constants.JsonSerializerOptions)
        serializerOptions.WriteIndented <- false

        let json = JsonSerializer.SerializeToUtf8Bytes(record, record.GetType(), serializerOptions)

        if json.Length + 1 > MaximumEvidenceRecordBytes then
            raise (InvalidDataException($"Evidence record exceeds the {MaximumEvidenceRecordBytes}-byte limit."))

        let parent = Path.GetDirectoryName path

        if not (String.IsNullOrWhiteSpace parent) then
            Directory.CreateDirectory parent |> ignore

        let bytes = Array.zeroCreate<byte> (json.Length + 1)
        Buffer.BlockCopy(json, 0, bytes, 0, json.Length)
        bytes[bytes.Length - 1] <- byte '\n'

        lock evidenceWriteLock (fun () ->
            use stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush(true))

    /// Reads all evidence records and rejects malformed or over-limit lines.
    let readEvidenceRecords (path: string) : JsonElement array =
        File.ReadLines(path, UTF8Encoding(false, true))
        |> Seq.mapi (fun index line ->
            if String.IsNullOrWhiteSpace line then
                raise (InvalidDataException($"Evidence record {index + 1} is blank."))

            if Encoding.UTF8.GetByteCount(line) + 1 > MaximumEvidenceRecordBytes then
                raise (InvalidDataException($"Evidence record {index + 1} exceeds the {MaximumEvidenceRecordBytes}-byte limit."))

            try
                use document = JsonDocument.Parse line
                document.RootElement.Clone()
            with
            | :? JsonException as ex -> raise (InvalidDataException($"Evidence record {index + 1} is not valid JSON.", ex)))
        |> Seq.toArray

/// Writes typed, bounded evidence while retaining all failed assertion identifiers for one grouped run.
type MeasurementEvidenceSink(rootDirectory: string) =
    let samplesPath = Path.Combine(rootDirectory, "samples.ndjson")
    let assertionsPath = Path.Combine(rootDirectory, "assertions.ndjson")
    let summariesPath = Path.Combine(rootDirectory, "summaries.ndjson")
    let mutable sequence = 0
    let failures = ResizeArray<string>()
    let assertionsByScenario = Dictionary<string, ResizeArray<MeasurementAssertion>>(StringComparer.Ordinal)

    do Directory.CreateDirectory(rootDirectory) |> ignore

    /// Returns the directory that preserves every raw artifact for this run.
    member _.RootDirectory = rootDirectory

    /// Returns the typed sample stream path.
    member _.SamplesPath = samplesPath

    /// Appends one typed sample with a monotonic sequence number.
    member _.Sample(scenario: string, sampleType: string, correlationKey: string, measurements: (string * obj) seq) =
        sequence <- sequence + 1
        let values = Dictionary<string, obj>(StringComparer.Ordinal)

        measurements
        |> Seq.iter (fun (name, value) -> values[name] <- value)

        let sample: MeasurementSample =
            {
                schemaVersion = "1.0"
                scenario = scenario
                sampleType = sampleType
                sequence = sequence
                timestampUtc = DateTimeOffset.UtcNow
                correlationKey = correlationKey
                measurements = values
            }

        ManifestContributionMeasurementSupport.appendEvidenceRecord samplesPath sample

    /// Records one assertion without allowing a response label or log line to stand in for the actual value.
    member _.Assertion(scenario: string, assertionId: string, description: string, expected: obj, actual: obj, passed: bool, evidenceFiles: string array) =
        let assertion: MeasurementAssertion =
            {
                assertionId = assertionId
                scenario = scenario
                description = description
                expected = string expected
                actual = string actual
                passed = passed
                evidenceFiles = evidenceFiles
            }

        ManifestContributionMeasurementSupport.appendEvidenceRecord assertionsPath assertion

        let scenarioAssertions =
            match assertionsByScenario.TryGetValue scenario with
            | true, existing -> existing
            | false, _ ->
                let created = ResizeArray<MeasurementAssertion>()
                assertionsByScenario[scenario] <- created
                created

        scenarioAssertions.Add assertion

        if not passed then
            failures.Add($"{scenario}/{assertionId}: expected {expected}; actual {actual}")

    /// Writes a terminal summary whose success and count come from the scenario's recorded assertions.
    member _.Summary(scenario: string, startedAtUtc: DateTimeOffset, expectedAssertionCount: int, evidenceFiles: string array) =
        let recordedAssertions =
            match assertionsByScenario.TryGetValue scenario with
            | true, assertions -> assertions.ToArray()
            | false, _ -> Array.empty

        let passed =
            recordedAssertions.Length = expectedAssertionCount
            && (recordedAssertions
                |> Array.forall (fun assertion -> assertion.passed))

        let summary: ScenarioSummary =
            {
                scenario = scenario
                startedAtUtc = startedAtUtc
                completedAtUtc = DateTimeOffset.UtcNow
                passed = passed
                assertionCount = recordedAssertions.Length
                evidenceFiles = evidenceFiles
            }

        ManifestContributionMeasurementSupport.appendEvidenceRecord summariesPath summary

        if not passed then
            failures.Add($"{scenario}/summary: expected {expectedAssertionCount} passing assertions; actual {recordedAssertions.Length} recorded assertions")

    /// Fails the grouped fixture after every selected scenario has had an opportunity to preserve evidence.
    member _.FailIfNeeded(runtimeFailures: string array) =
        let allFailures =
            Seq.append failures runtimeFailures
            |> Seq.distinct
            |> Seq.toArray

        if allFailures.Length > 0 then
            let bounded =
                allFailures
                |> Array.truncate 20
                |> String.concat Environment.NewLine

            NUnit.Framework.Assert.Fail($"Manifest contribution measurement scenarios failed. Artifacts={rootDirectory}{Environment.NewLine}{bounded}")
