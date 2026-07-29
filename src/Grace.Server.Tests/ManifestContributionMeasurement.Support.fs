namespace Grace.Server.Tests

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

/// Carries the stable result fields returned by an Aspire resource command.
type ResourceCommandObservation = { Success: bool; Canceled: bool; Message: string }

/// Classifies one Aspire command before readiness checks begin.
type ResourceCommandOutcome =
    | Completed
    | Canceled of message: string
    | Failed of message: string

/// Carries one typed raw measurement emitted by the manifest-accounting fixture.
type MeasurementSample =
    { schemaVersion: string
      scenario: string
      sampleType: string
      sequence: int
      timestampUtc: DateTimeOffset
      correlationKey: string
      measurements: IReadOnlyDictionary<string, obj> }

/// Carries one false-positive-resistant assertion and its raw evidence files.
type MeasurementAssertion =
    { assertionId: string; scenario: string; description: string; expected: string; actual: string; passed: bool; evidenceFiles: string array }

/// Summarizes one completed runtime scenario without claiming unsupported Azure behavior.
type ScenarioSummary =
    { scenario: string; startedAtUtc: DateTimeOffset; completedAtUtc: DateTimeOffset; passed: bool; assertionCount: int; evidenceFiles: string array }

/// Identifies one local measurement run and the scenarios selected for its shared Aspire session.
type MeasurementRun =
    { schemaVersion: string; runId: string; environment: string; startedAtUtc: DateTimeOffset; scenarios: string array; unmeasured: string array }

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

    /// Replaces recognized credential values while retaining non-secret endpoint and state context.
    let private redactDiagnosticSecrets (diagnostic: string) =
        Regex.Replace(diagnostic, "(?i)(AccountKey|SharedAccessKey|SharedAccessSignature|Password)=([^;\\r\\n]*)", "$1=***", RegexOptions.CultureInvariant)

    /// Redacts secret-bearing connection-string segments and bounds the resulting diagnostic.
    let formatBoundedDiagnostic (context: string) (resourceState: string) (logs: string list) : string =
        let joinedLogs = logs |> List.truncate 50 |> String.concat Environment.NewLine

        let diagnostic =
            $"Context: {context}{Environment.NewLine}Resource: {resourceState}{Environment.NewLine}Logs:{Environment.NewLine}{joinedLogs}"
            |> redactDiagnosticSecrets

        if diagnostic.Length <= MaximumDiagnosticCharacters then
            diagnostic
        else
            let suffix = $"{Environment.NewLine}[diagnostic truncated]"

            diagnostic.Substring(0, MaximumDiagnosticCharacters - suffix.Length) + suffix

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
                    let valueFields = line.Substring(valueStart).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    let mutable value = 0.0

                    if
                        valueFields.Length > 0
                        && predicate sampleName
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
            with :? JsonException as ex ->
                raise (InvalidDataException($"Evidence record {index + 1} is not valid JSON.", ex)))
        |> Seq.toArray
