namespace Grace.CLI.Command

open Grace.CLI
open Grace.Shared.Constants
open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

module WorkingDirectoryUpdate = WorkingDirectoryUpdateContracts

/// Owns branch-independent leases and marker evidence for one repository and local working root.
module internal WorkingDirectoryUpdateCoordination =
    let private markerSchemaVersion = 1
    let private retryDelay = TimeSpan.FromMilliseconds(25.0)
    let private leaseFileName = "working-directory-update.lease"
    let private markerFileName = "working-directory-update.marker.json"
    let private sidecarFileName = "working-directory-update.sidecar.json"
    let private jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    /// Represents the immutable repository and normalized-local-root tuple used to serialize one update location.
    type Scope = private Scope of repositoryId: Guid * rootScope: string * directory: string

    /// Holds the exclusive OS file handle that protects one coordination scope.
    type Lease internal (stream: FileStream) =
        interface IDisposable with
            member _.Dispose() = stream.Dispose()

    /// Names the only marker inspection outcomes callers may use before planning a fresh update.
    type MarkerInspection =
        | Missing
        | Adopt
        | RequiresDoctor

    /// Holds one versioned marker record written only while its scope lease is held.
    type internal MarkerDocument =
        {
            SchemaVersion: int
            AttemptToken: string
            CallerKind: string
            OperationId: string
            RepositoryId: string
            BranchId: string
            Target: string
            LocalRootScope: string
            StartedUtc: string
            ProcessId: int
        }

    /// Holds the derived completion evidence later consumers may use after marker cleanup.
    type private SidecarDocument = { SchemaVersion: int; OperationId: string; CompletedUtc: string }

    /// Extracts the repository identifier from a private scope before marker parsing validates persisted facts.
    let private scopeRepositoryId (Scope (repositoryId, _, _)) = repositoryId

    /// Extracts the lower-case root hash from a private scope before marker parsing validates persisted facts.
    let private scopeValue (Scope (_, rootScope, _)) = rootScope

    /// Converts private caller-kind tags to the stable marker vocabulary.
    let private callerKindText =
        function
        | WorkingDirectoryUpdate.CallerKind.Watch -> "watch"
        | WorkingDirectoryUpdate.CallerKind.Branch -> "branch"
        | WorkingDirectoryUpdate.CallerKind.Connect -> "connect"

    /// Checks that a persisted caller-kind tag is one of the Product V1 operations.
    let private isKnownCallerKind =
        function
        | "watch"
        | "branch"
        | "connect" -> true
        | _ -> false

    /// Reads one required JSON string property without accepting an omitted or non-string value.
    let private requiredString (root: JsonElement) (name: string) =
        let mutable property = Unchecked.defaultof<JsonElement>

        if
            root.TryGetProperty(name, &property)
            && property.ValueKind = JsonValueKind.String
        then
            let value = property.GetString()

            if String.IsNullOrWhiteSpace(value) then None else Some value
        else
            None

    /// Requires a current Product V1 attempt token rather than a PID-derived identifier.
    let private isAttemptToken (value: string) =
        match Guid.TryParseExact(value, "N") with
        | true, _ -> true
        | _ -> false

    /// Requires the complete lower-case SHA-256 operation vocabulary defined by the update contracts.
    let private isOperationId (value: string) =
        value.StartsWith("sha256:", StringComparison.Ordinal)
        && Sha256FullHashRegex.IsMatch(value.Substring("sha256:".Length))

    /// Validates the exact canonical target encoding retained in a marker without exposing a mutable target object.
    let private isCanonicalTarget (repositoryId: string) (branchId: string) (target: string) =
        let lines = target.Split('\n', StringSplitOptions.None)

        /// Decodes one named canonical target field only when its Base64 spelling round-trips exactly.
        let tryDecode index name =
            let prefix = name + ":"

            if
                index >= lines.Length
                || not
                    (
                        lines[index]
                            .StartsWith(prefix, StringComparison.Ordinal)
                    )
            then
                None
            else
                try
                    let encoded = lines[ index ].Substring(prefix.Length)
                    let value = Encoding.UTF8.GetString(Convert.FromBase64String(encoded))

                    if lines[index] = prefix
                                      + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) then
                        Some value
                    else
                        None
                with
                | :? FormatException -> None

        if lines.Length <> 7
           || lines[0]
              <> "grace.working-directory-update.target.v1"
           || lines[6] <> String.Empty then
            false
        else
            match tryDecode 1 "repository", tryDecode 2 "branch", tryDecode 3 "root-directory-version", tryDecode 4 "sha256", tryDecode 5 "blake3" with
            | Some targetRepositoryId, Some targetBranchId, Some rootDirectoryVersionId, Some sha256Hash, Some blake3Hash ->
                match Guid.TryParseExact(targetRepositoryId, "N"), Guid.TryParseExact(targetBranchId, "N"), Guid.TryParseExact(rootDirectoryVersionId, "N") with
                | (true, parsedRepositoryId), (true, parsedBranchId), (true, parsedRootDirectoryVersionId) ->
                    parsedRepositoryId <> Guid.Empty
                    && parsedBranchId <> Guid.Empty
                    && parsedRootDirectoryVersionId <> Guid.Empty
                    && targetRepositoryId = repositoryId
                    && targetBranchId = branchId
                    && Sha256FullHashRegex.IsMatch(sha256Hash)
                    && Blake3FullHashRegex.IsMatch(blake3Hash)
                | _ -> false
            | _ -> false

    /// Parses a marker only when every schema field and scope fact is complete and known.
    let private tryReadMarkerDocument (scope: Scope) (content: string) =
        try
            use document = JsonDocument.Parse(content)
            let root = document.RootElement

            let expectedNames =
                Set.ofList [ "schemaVersion"
                             "attemptToken"
                             "callerKind"
                             "operationId"
                             "repositoryId"
                             "branchId"
                             "target"
                             "localRootScope"
                             "startedUtc"
                             "processId" ]

            let actualNames =
                root.EnumerateObject()
                |> Seq.map (fun property -> property.Name)
                |> Set.ofSeq

            let mutable schemaElement = Unchecked.defaultof<JsonElement>
            let mutable processElement = Unchecked.defaultof<JsonElement>
            let mutable schemaVersion = 0
            let mutable processId = 0

            let validNumbers =
                root.TryGetProperty("schemaVersion", &schemaElement)
                && root.TryGetProperty("processId", &processElement)
                && schemaElement.TryGetInt32(&schemaVersion)
                && processElement.TryGetInt32(&processId)

            match requiredString root "attemptToken",
                  requiredString root "callerKind",
                  requiredString root "operationId",
                  requiredString root "repositoryId",
                  requiredString root "branchId",
                  requiredString root "target",
                  requiredString root "localRootScope",
                  requiredString root "startedUtc"
                with
            | Some attemptToken, Some callerKind, Some operationId, Some repositoryId, Some branchId, Some target, Some localRootScope, Some startedUtc when
                root.ValueKind = JsonValueKind.Object
                && actualNames = expectedNames
                && validNumbers
                && schemaVersion = markerSchemaVersion
                && processId > 0
                && isAttemptToken attemptToken
                && isKnownCallerKind callerKind
                && isOperationId operationId
                && isCanonicalTarget repositoryId branchId target
                ->
                match Guid.TryParseExact(repositoryId, "N"),
                      Guid.TryParseExact(branchId, "N"),
                      DateTimeOffset.TryParse(startedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    with
                | (true, markerRepositoryId), (true, markerBranchId), (true, _) when
                    markerRepositoryId <> Guid.Empty
                    && markerBranchId <> Guid.Empty
                    && markerRepositoryId = scopeRepositoryId scope
                    && localRootScope = scopeValue scope
                    ->
                    Ok
                        {
                            SchemaVersion = schemaVersion
                            AttemptToken = attemptToken
                            CallerKind = callerKind
                            OperationId = operationId
                            RepositoryId = repositoryId
                            BranchId = branchId
                            Target = target
                            LocalRootScope = localRootScope
                            StartedUtc = startedUtc
                            ProcessId = processId
                        }
                | _ -> Error "Marker repository, branch, timestamp, or scope is not valid for this update."
            | _ -> Error "Marker does not match the supported Working Directory Update schema."
        with
        | :? JsonException
        | :? FormatException
        | :? InvalidOperationException -> Error "Marker is malformed."

    /// Supplies construction and path access for a stable repository/local-root coordination scope.
    module Scope =
        /// Creates a scope from a repository ID and normalized absolute local root, excluding selected branch identity.
        let create repositoryId localRoot =
            if repositoryId = Guid.Empty then
                Error "Working Directory Update coordination requires a repository id."
            else
                WorkingDirectoryUpdate.LocalRootScope.create localRoot
                |> Result.map (fun localRootScope ->
                    let rootScope = WorkingDirectoryUpdate.LocalRootScope.value localRootScope
                    Scope(repositoryId, rootScope, Services.workingDirectoryUpdateTempDirectory repositoryId rootScope))

        /// Rebuilds the stable lease location from the immutable local-root scope retained by a recovery-only request.
        let createFromLocalRootScope repositoryId localRootScope =
            if repositoryId = Guid.Empty then
                Error "Working Directory Update coordination requires a repository id."
            else
                let rootScope = WorkingDirectoryUpdate.LocalRootScope.value localRootScope
                Ok(Scope(repositoryId, rootScope, Services.workingDirectoryUpdateTempDirectory repositoryId rootScope))

        /// Returns the lower-case SHA-256 local-root component of a scope.
        let value (scope: Scope) = scopeValue scope

        /// Returns the repository component of a scope.
        let repositoryId (scope: Scope) = scopeRepositoryId scope

        /// Returns the directory that contains this scope's lease, marker, and sidecar files.
        let directory (Scope (_, _, directory)) = directory

        /// Returns the exclusive lease file path for this stable scope.
        let leasePath (scope: Scope) = Path.Combine(directory scope, leaseFileName)

        /// Returns the owned marker file path for this stable scope.
        let markerPath (scope: Scope) = Path.Combine(directory scope, markerFileName)

        /// Returns the derived completion-sidecar path for this stable scope.
        let sidecarPath (scope: Scope) = Path.Combine(directory scope, sidecarFileName)

    /// Supplies cancellable exclusive-handle acquisition for a coordination scope.
    module Lease =
        /// Waits for one real exclusive lease handle while respecting cancellation between contention retries.
        let acquire (scope: Scope) (cancellationToken: CancellationToken) =
            task {
                Directory.CreateDirectory(Scope.directory scope)
                |> ignore

                let mutable lease = None

                while Option.isNone lease do
                    cancellationToken.ThrowIfCancellationRequested()

                    try
                        lease <- Some(new FileStream(Scope.leasePath scope, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    with
                    | :? IOException -> do! Task.Delay(retryDelay, cancellationToken)

                return new Lease(Option.get lease)
            }

        /// Releases the held operating-system handle without deleting durable coordination evidence.
        let dispose (lease: Lease) = (lease :> IDisposable).Dispose()

    /// Supplies versioned marker serialization, inspection, and exact-token cleanup.
    module Marker =
        /// Creates an owned marker only when its target, operation, and repository facts bind to the same scope.
        let create scope attemptToken target operation =
            if WorkingDirectoryUpdate.Target.repositoryId target
               <> Scope.repositoryId scope then
                Error "Working Directory Update marker repository does not match its local-root scope."
            elif not (WorkingDirectoryUpdate.Operation.matchesTarget target operation) then
                Error "Working Directory Update marker operation does not match its target."
            else
                Ok
                    {
                        SchemaVersion = markerSchemaVersion
                        AttemptToken = WorkingDirectoryUpdate.AttemptToken.value attemptToken
                        CallerKind =
                            WorkingDirectoryUpdate.Operation.callerKind operation
                            |> callerKindText
                        OperationId = WorkingDirectoryUpdate.Operation.value operation
                        RepositoryId =
                            Scope.repositoryId scope
                            |> fun value -> value.ToString("N")
                        BranchId =
                            WorkingDirectoryUpdate.Target.branchId target
                            |> fun value -> value.ToString("N")
                        Target = WorkingDirectoryUpdate.Target.canonical target
                        LocalRootScope = Scope.value scope
                        StartedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                        ProcessId = Environment.ProcessId
                    }

        /// Writes a complete owned marker after the caller holds the matching scope lease.
        let write (scope: Scope) (marker: MarkerDocument) =
            task {
                Directory.CreateDirectory(Scope.directory scope)
                |> ignore

                let serialized =
                    JsonSerializer.Serialize(
                        {|
                            schemaVersion = marker.SchemaVersion
                            attemptToken = marker.AttemptToken
                            callerKind = marker.CallerKind
                            operationId = marker.OperationId
                            repositoryId = marker.RepositoryId
                            branchId = marker.BranchId
                            target = marker.Target
                            localRootScope = marker.LocalRootScope
                            startedUtc = marker.StartedUtc
                            processId = marker.ProcessId
                        |},
                        jsonOptions
                    )

                File.WriteAllText(Scope.markerPath scope, serialized)
            }

        /// Classifies a marker as adoptable only when its complete target, caller kind, and operation exactly match the retry.
        let inspect scope expectedTarget operation =
            task {
                if not (WorkingDirectoryUpdate.Operation.matchesTarget expectedTarget operation) then
                    return RequiresDoctor
                else
                    let path = Scope.markerPath scope

                    if not (File.Exists(path)) then
                        return Missing
                    else
                        try
                            let content = File.ReadAllText(path)

                            match tryReadMarkerDocument scope content with
                            | Ok marker when
                                marker.OperationId = WorkingDirectoryUpdate.Operation.value operation
                                && marker.Target = WorkingDirectoryUpdate.Target.canonical expectedTarget
                                && marker.CallerKind = (WorkingDirectoryUpdate.Operation.callerKind operation
                                                        |> callerKindText)
                                ->
                                return Adopt
                            | Ok _ -> return RequiresDoctor
                            | Error _ -> return RequiresDoctor
                        with
                        | :? IOException
                        | :? UnauthorizedAccessException -> return RequiresDoctor
            }

        /// Removes a marker only after its currently persisted token exactly matches this attempt token.
        let tryRemoveOwned scope attemptToken =
            task {
                let path = Scope.markerPath scope

                if not (File.Exists(path)) then
                    return false
                else
                    try
                        let content = File.ReadAllText(path)

                        match tryReadMarkerDocument scope content with
                        | Ok marker when marker.AttemptToken = WorkingDirectoryUpdate.AttemptToken.value attemptToken ->
                            File.Delete(path)
                            return true
                        | _ -> return false
                    with
                    | :? IOException
                    | :? UnauthorizedAccessException -> return false
            }

    /// Supplies derived sidecar creation without changing or deleting marker evidence.
    module Sidecar =
        /// Writes the completed operation identity as derived local notification evidence for the held scope.
        let write (scope: Scope) operation =
            task {
                Directory.CreateDirectory(Scope.directory scope)
                |> ignore

                let sidecar: SidecarDocument =
                    {
                        SchemaVersion = markerSchemaVersion
                        OperationId = WorkingDirectoryUpdate.Operation.value operation
                        CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    }

                let serialized =
                    JsonSerializer.Serialize(
                        {| schemaVersion = sidecar.SchemaVersion; operationId = sidecar.OperationId; completedUtc = sidecar.CompletedUtc |},
                        jsonOptions
                    )

                File.WriteAllText(Scope.sidecarPath scope, serialized)
            }
