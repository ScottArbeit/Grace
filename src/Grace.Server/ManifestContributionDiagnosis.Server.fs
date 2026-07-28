namespace Grace.Server

open Giraffe
open Grace.Actors
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.ApplicationContext
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.ManifestContributionAccounting
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Reference
open Grace.Types.RepositoryContentCounter
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Provides bounded, read-only manifest contribution evidence for Grace operators.
module ManifestContributionDiagnosis =

    /// Carries the internal operator request without adding a public Grace contract.
    [<AllowNullLiteral>]
    type DiagnoseManifestContributionParameters() =
        member val ReferenceId = String.Empty with get, set
        member val DirectoryVersionId = String.Empty with get, set
        member val RepositoryId = String.Empty with get, set
        member val StoragePoolId = String.Empty with get, set
        member val ManifestAddress = String.Empty with get, set
        member val RepositoryContentCounterOperationId = String.Empty with get, set
        member val MaxRelationships = 0 with get, set

    /// Identifies one repository-manifest counter and workflow pair.
    type CounterTuple = { RepositoryId: RepositoryId; StoragePoolId: StoragePoolId; ManifestAddress: ManifestAddress }

    /// Represents the one validated diagnosis selector supplied by an operator.
    type DiagnosisSelector =
        | ReferenceId of referenceId: ReferenceId * repositoryIdHint: RepositoryId option
        | DirectoryVersionId of directoryVersionId: DirectoryVersionId * repositoryIdHint: RepositoryId option
        | CounterTuple of CounterTuple
        | OperationId of RepositoryContentCounterOperationId

    /// Preserves the selector in the exported report without relying on F# union serialization.
    type DiagnosisTarget =
        | Reference of referenceId: string
        | DirectoryVersion of directoryVersionId: string
        | Counter of repositoryId: string * storagePoolId: string * manifestAddress: string
        | Operation of operationId: string

    /// Names the three terminal operator outcomes consumed by the PowerShell script.
    type DiagnosisOutcome =
        | VerifiedComplete
        | IncompleteRetain
        | FailedRetain

    /// Records one actor snapshot exactly as it was read during this diagnosis.
    type ActorFact = { ActorType: string; ActorId: string; Revision: int64 option; SnapshotJson: string }

    /// Compares one durable counter value with a bounded actor-derived reconstruction.
    type ManifestCountEvidence =
        {
            RepositoryId: string
            StoragePoolId: string
            ManifestAddress: string
            StoredCount: int64 option
            RebuiltCount: int64 option
            Completeness: string
        }

    /// Is the complete, self-verifying, read-only operator report written by the diagnosis script.
    type ManifestContributionDiagnosisReport =
        {
            SchemaVersion: string
            GeneratedAt: string
            Target: DiagnosisTarget
            MaxRelationships: int
            RelationshipsRead: int
            ActorFacts: ActorFact array
            ExpectedRelationships: string array
            ObservedRelationships: string array
            MissingRelationships: string array
            StaleRelationships: string array
            CountEvidence: ManifestCountEvidence array
            DeterministicIdentities: string array
            RedisEvidence: string array
            RepairTargets: string array
            UnknownFields: string array
            EvidenceGaps: string array
            ReclamationPermitted: bool
            Outcome: DiagnosisOutcome
            ReportSha256: string
        }

    /// Supplies only read operations to the diagnosis core so tests can prove that diagnosis cannot mutate Grace.
    type DiagnosisDependencies =
        {
            GetReference: ReferenceId -> CorrelationId -> Task<ReferenceDto>
            GetDirectoryVersion: DirectoryVersionId -> CorrelationId -> Task<DirectoryVersionDto>
            EnumerateRelationships: ExactRelationshipPartition
                -> ExactRelationshipReadBound
                -> string option
                -> CancellationToken
                -> Task<ExactRelationshipPage>
            VerifyRelationship: ExactRelationship -> CancellationToken -> Task<ExactRelationshipPresence>
            GetCounter: CounterTuple -> CorrelationId -> Task<RepositoryContentCounterDto>
            GetWorkflow: CounterTuple -> CorrelationId -> Task<ManifestContributionWorkflowDto>
            GetRecentResult: CounterTuple -> RepositoryContentCounterOperationId -> CancellationToken -> Task<RepositoryContentCounterCompletedChange option>
        }

    /// Signals that a bounded read found more relationship identities than the operator allowed.
    exception RelationshipBoundExceeded of string

    /// Validates the optional repository qualifier shared by Reference and DirectoryVersion selectors.
    let private tryRepositoryIdHint (value: string) =
        if String.IsNullOrWhiteSpace value then
            Ok None
        else
            match Guid.TryParse value with
            | true, repositoryId when repositoryId <> Guid.Empty -> Ok(Some(repositoryId: RepositoryId))
            | _ -> Error "RepositoryId must be a non-empty GUID when supplied."

    /// Validates a required non-empty GUID selector.
    let private requiredGuid name (value: string) =
        match Guid.TryParse value with
        | true, parsed when parsed <> Guid.Empty -> Ok parsed
        | _ -> Error $"{name} must be a non-empty GUID."

    /// Enforces exactly one bounded selector before any actor or projection read starts.
    let validateParameters (parameters: DiagnoseManifestContributionParameters) =
        if isNull parameters then
            Error "A diagnosis request body is required."
        else
            match ExactRelationshipReadBound.create parameters.MaxRelationships with
            | Error error -> Error error
            | Ok _ ->
                let hasReference = not (String.IsNullOrWhiteSpace parameters.ReferenceId)
                let hasDirectoryVersion = not (String.IsNullOrWhiteSpace parameters.DirectoryVersionId)
                let hasOperation = not (String.IsNullOrWhiteSpace parameters.RepositoryContentCounterOperationId)
                let hasRepository = not (String.IsNullOrWhiteSpace parameters.RepositoryId)
                let hasStoragePool = not (String.IsNullOrWhiteSpace parameters.StoragePoolId)
                let hasManifest = not (String.IsNullOrWhiteSpace parameters.ManifestAddress)
                let hasCompleteCounterTuple = hasRepository && hasStoragePool && hasManifest

                let tupleComponentOutsideQualifiedSource =
                    hasStoragePool
                    || hasManifest
                    || (hasRepository
                        && not hasReference
                        && not hasDirectoryVersion
                        && not hasOperation)

                if tupleComponentOutsideQualifiedSource
                   && not hasCompleteCounterTuple then
                    Error "RepositoryId, StoragePoolId, and ManifestAddress must form one complete counter tuple."
                else
                    let selectorCount =
                        [
                            hasReference
                            hasDirectoryVersion
                            hasCompleteCounterTuple
                            hasOperation
                        ]
                        |> List.filter id
                        |> List.length

                    if selectorCount <> 1 then
                        Error "Specify exactly one selector: ReferenceId, DirectoryVersionId, a complete counter tuple, or RepositoryContentCounterOperationId."
                    elif hasReference then
                        match requiredGuid "ReferenceId" parameters.ReferenceId, tryRepositoryIdHint parameters.RepositoryId with
                        | Ok referenceId, Ok repositoryIdHint -> Ok(DiagnosisSelector.ReferenceId((referenceId: ReferenceId), repositoryIdHint))
                        | Error error, _
                        | _, Error error -> Error error
                    elif hasDirectoryVersion then
                        match requiredGuid "DirectoryVersionId" parameters.DirectoryVersionId, tryRepositoryIdHint parameters.RepositoryId with
                        | Ok directoryVersionId, Ok repositoryIdHint ->
                            Ok(DiagnosisSelector.DirectoryVersionId((directoryVersionId: DirectoryVersionId), repositoryIdHint))
                        | Error error, _
                        | _, Error error -> Error error
                    elif hasCompleteCounterTuple then
                        match requiredGuid "RepositoryId" parameters.RepositoryId with
                        | Error error -> Error error
                        | Ok repositoryId ->
                            Ok(
                                DiagnosisSelector.CounterTuple
                                    {
                                        RepositoryId = (repositoryId: RepositoryId)
                                        StoragePoolId = StoragePoolId parameters.StoragePoolId
                                        ManifestAddress = ManifestAddress parameters.ManifestAddress
                                    }
                            )
                    else
                        Ok(DiagnosisSelector.OperationId(RepositoryContentCounterOperationId parameters.RepositoryContentCounterOperationId))

    /// Creates a report shell for deterministic tests and terminal failures.
    let emptyReport generatedAt target maxRelationships outcome unknownFields =
        {
            SchemaVersion = "grace.manifest-contribution-diagnosis.v1"
            GeneratedAt = generatedAt
            Target = target
            MaxRelationships = maxRelationships
            RelationshipsRead = 0
            ActorFacts = Array.empty
            ExpectedRelationships = Array.empty
            ObservedRelationships = Array.empty
            MissingRelationships = Array.empty
            StaleRelationships = Array.empty
            CountEvidence = Array.empty
            DeterministicIdentities = Array.empty
            RedisEvidence = Array.empty
            RepairTargets = Array.empty
            UnknownFields = unknownFields
            EvidenceGaps = Array.empty
            ReclamationPermitted = false
            Outcome = outcome
            ReportSha256 = String.Empty
        }

    let private reportDigestOptions =
        let options = JsonSerializerOptions(Constants.JsonSerializerOptions)
        options.WriteIndented <- false
        options

    /// Computes the report digest over compact JSON with ReportSha256 omitted.
    let private reportDigest (report: ManifestContributionDiagnosisReport) =
        let unsigned = JsonSerializer.SerializeToNode(report, reportDigestOptions)

        unsigned
            .AsObject()
            .Remove(nameof report.ReportSha256)
        |> ignore

        let json = unsigned.ToJsonString(reportDigestOptions)

        SHA256.HashData(Encoding.UTF8.GetBytes json)
        |> Convert.ToHexStringLower

    /// Adds the deterministic SHA-256 that the operator script verifies before writing the report.
    let signReport report = { report with ReportSha256 = reportDigest report }

    /// Verifies a serialized report using the same omitted-digest canonical form as the server.
    let verifySerializedReportSha256 (serialized: string) =
        try
            let report = JsonSerializer.Deserialize<ManifestContributionDiagnosisReport>(serialized, Constants.JsonSerializerOptions)

            not (isNull (box report))
            && String.Equals(report.ReportSha256, reportDigest report, StringComparison.OrdinalIgnoreCase)
        with
        | :? JsonException -> false

    /// Converts one exact relationship into the provider-neutral identity shown in operator evidence.
    let relationshipIdentity relationship =
        match ExactRelationshipKey.create relationship with
        | Ok key -> $"{key.PartitionKey}|{key.ItemId}"
        | Error error -> invalidArg (nameof relationship) error

    /// Converts the validated selector into its stable report target.
    let private selectorTarget selector =
        match selector with
        | DiagnosisSelector.ReferenceId (referenceId, _) -> DiagnosisTarget.Reference $"{referenceId:D}"
        | DiagnosisSelector.DirectoryVersionId (directoryVersionId, _) -> DiagnosisTarget.DirectoryVersion $"{directoryVersionId:D}"
        | DiagnosisSelector.CounterTuple target -> DiagnosisTarget.Counter($"{target.RepositoryId:D}", $"{target.StoragePoolId}", $"{target.ManifestAddress}")
        | DiagnosisSelector.OperationId operationId -> DiagnosisTarget.Operation $"{operationId}"

    /// Returns the direct manifests that current DirectoryVersion actor state authoritatively names.
    let private directManifests directoryVersionDto correlationId =
        DirectoryVersion.getManifestReferencesForSaveBoundary directoryVersionDto.DirectoryVersion correlationId
        |> Result.map (fun references ->
            references
            |> Seq.map (fun reference -> reference.Manifest)
            |> Seq.toArray)

    /// Parses the current deterministic DirectoryVersion counter-operation identity.
    let private tryParseOperationSource (operationId: RepositoryContentCounterOperationId) =
        let value = $"{operationId}"
        let prefix = "directory-version:"
        let sourceStart = prefix.Length
        let sourceLength = 32

        if value.StartsWith(prefix, StringComparison.Ordinal)
           && value.Length > sourceStart + sourceLength
           && value[sourceStart + sourceLength] = ':' then
            match Guid.TryParseExact(value.Substring(sourceStart, sourceLength), "N") with
            | true, directoryVersionId when directoryVersionId <> Guid.Empty ->
                if value.EndsWith(":add", StringComparison.Ordinal) then
                    Some((directoryVersionId: DirectoryVersionId), "add")
                elif value.EndsWith(":remove", StringComparison.Ordinal) then
                    Some((directoryVersionId: DirectoryVersionId), "remove")
                else
                    None
            | _ -> None
        else
            None

    /// Builds the deterministic add or remove identity used by current manifest accounting.
    let private operationIdentity action (relationship: DirectoryVersionManifestRelationship) =
        RepositoryContentCounterOperationId
            $"directory-version:{relationship.DirectoryVersionId:N}:{relationship.StoragePoolId}:{relationship.ManifestAddress}:{action}"

    /// Executes one bounded diagnosis entirely through read-only dependencies.
    let diagnoseWith
        (dependencies: DiagnosisDependencies)
        (generatedAt: string)
        (correlationId: CorrelationId)
        (cancellationToken: CancellationToken)
        (bound: ExactRelationshipReadBound)
        (selector: DiagnosisSelector)
        =
        task {
            let maximumRelationships = ExactRelationshipReadBound.value bound
            let target = selectorTarget selector
            let actorFacts = ResizeArray<ActorFact>()
            let expected = Dictionary<string, ExactRelationship>(StringComparer.Ordinal)
            let observed = Dictionary<string, ExactRelationship>(StringComparer.Ordinal)
            let missing = HashSet<string>(StringComparer.Ordinal)
            let stale = HashSet<string>(StringComparer.Ordinal)
            let deterministicIdentities = HashSet<string>(StringComparer.Ordinal)
            let repairTargets = HashSet<string>(StringComparer.Ordinal)
            let unknownFields = HashSet<string>(StringComparer.Ordinal)
            let evidenceGaps = ResizeArray<string>()
            let manifestTargets = Dictionary<string, CounterTuple>(StringComparer.Ordinal)
            let expectedWorkflowRanges = Dictionary<string, HashSet<ManifestContributionWorkflowRange>>(StringComparer.Ordinal)
            let directoryFacts = Dictionary<DirectoryVersionId, DirectoryVersionDto>()
            let relationshipReads = HashSet<string>(StringComparer.Ordinal)
            let mutable sourceBacked = false
            let mutable rootFailure: string option = None
            let mutable operationId: RepositoryContentCounterOperationId option = None

            /// Adds one already-normalized actor snapshot to the report evidence.
            let addActorFactJson actorType actorId revision snapshotJson =
                actorFacts.Add({ ActorType = actorType; ActorId = actorId; Revision = revision; SnapshotJson = snapshotJson })

            /// Serializes and records one actor snapshot whose contract is safe for the shared JSON options.
            let addActorFact actorType actorId revision snapshot =
                addActorFactJson actorType actorId revision (JsonSerializer.Serialize(snapshot, Constants.JsonSerializerOptions))

            /// Preserves every workflow field while representing an uninitialized enum-like union as explicit JSON null.
            let addWorkflowActorFact actorType actorId revision (snapshot: ManifestContributionWorkflowDto) =
                let unionName value = if isNull (box value) then null else $"{value}"

                let optionalIdentity value =
                    match value with
                    | Some identity -> $"{identity}"
                    | None -> null

                let snapshotJson =
                    JsonSerializer.Serialize(
                        {|
                            Class = snapshot.Class
                            RepositoryId = snapshot.RepositoryId
                            StoragePoolId = snapshot.StoragePoolId
                            ManifestAddress = snapshot.ManifestAddress
                            Direction = unionName snapshot.Direction
                            Ranges = snapshot.Ranges
                            CompletedRanges = snapshot.CompletedRanges
                            FailedRanges = snapshot.FailedRanges
                            LifecycleState = unionName snapshot.LifecycleState
                            StartOperationId = optionalIdentity snapshot.StartOperationId
                            LastOperationId = optionalIdentity snapshot.LastOperationId
                            CounterRevision = snapshot.CounterRevision
                            Revision = snapshot.Revision
                        |},
                        Constants.JsonSerializerOptions
                    )

                addActorFactJson actorType actorId revision snapshotJson

            let noteRelationshipRead relationship =
                let identity = relationshipIdentity relationship

                if relationshipReads.Add identity
                   && relationshipReads.Count > maximumRelationships then
                    raise (
                        RelationshipBoundExceeded
                            $"Diagnosis exceeded MaxRelationships={maximumRelationships} while reading '{identity}'. No complete report can be produced at this bound."
                    )

                identity

            let addExpected relationship =
                let identity = relationshipIdentity relationship
                expected.TryAdd(identity, relationship) |> ignore
                deterministicIdentities.Add identity |> ignore

            let addManifestTarget (counterTuple: CounterTuple) =
                let key = RepositoryContentCounter.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress

                manifestTargets.TryAdd(key, counterTuple)
                |> ignore

            /// Records the distinct ContentBlocks that current readable source state requires one manifest workflow to cover.
            let addManifestSourceTarget repositoryId (manifest: FileManifest) =
                let counterTuple = { RepositoryId = repositoryId; StoragePoolId = manifest.StoragePoolId; ManifestAddress = manifest.ManifestAddress }

                addManifestTarget counterTuple

                let key = RepositoryContentCounter.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress

                let ranges =
                    match expectedWorkflowRanges.TryGetValue key with
                    | true, existing -> existing
                    | _ ->
                        let created = HashSet<ManifestContributionWorkflowRange>()
                        expectedWorkflowRanges[key] <- created
                        created

                manifest.Blocks
                |> Seq.distinctBy (fun block -> block.Address)
                |> Seq.iter (fun block ->
                    ranges.Add({ StoragePoolId = manifest.StoragePoolId; ContentBlockAddress = block.Address })
                    |> ignore)

            let readDirectoryVersion directoryVersionId =
                task {
                    match directoryFacts.TryGetValue directoryVersionId with
                    | true, dto -> return dto
                    | _ ->
                        let! dto = dependencies.GetDirectoryVersion directoryVersionId correlationId
                        directoryFacts[directoryVersionId] <- dto

                        addActorFact "DirectoryVersion" $"{directoryVersionId:D}" None dto

                        return dto
                }

            let traverseDirectoryTree repositoryId rootDirectoryVersionId =
                task {
                    let pending = Stack<DirectoryVersionId>()
                    let visited = HashSet<DirectoryVersionId>()
                    pending.Push rootDirectoryVersionId

                    while pending.Count > 0 do
                        cancellationToken.ThrowIfCancellationRequested()
                        let directoryVersionId = pending.Pop()

                        if visited.Add directoryVersionId then
                            let! dto = readDirectoryVersion directoryVersionId
                            let current = dto.DirectoryVersion

                            if current.DirectoryVersionId <> directoryVersionId
                               || current.RepositoryId <> repositoryId then
                                evidenceGaps.Add($"DirectoryVersion '{directoryVersionId:D}' did not return current state for repository '{repositoryId:D}'.")
                            else
                                match directManifests dto correlationId with
                                | Error error ->
                                    evidenceGaps.Add($"DirectoryVersion '{directoryVersionId:D}' manifests could not be interpreted: {error.Error}")
                                | Ok manifests ->
                                    for manifest in manifests do
                                        let relationship =
                                            {
                                                RepositoryId = repositoryId
                                                StoragePoolId = manifest.StoragePoolId
                                                ManifestAddress = manifest.ManifestAddress
                                                DirectoryVersionId = directoryVersionId
                                            }

                                        addExpected (ExactRelationship.DirectoryVersionManifest relationship)

                                        addManifestSourceTarget repositoryId manifest

                                for childDirectoryVersionId in current.Directories |> Seq.distinct do
                                    addExpected (
                                        ExactRelationship.ParentChild
                                            {
                                                RepositoryId = repositoryId
                                                ParentDirectoryVersionId = directoryVersionId
                                                ChildDirectoryVersionId = childDirectoryVersionId
                                            }
                                    )

                                    pending.Push childDirectoryVersionId
                }

            match selector with
            | DiagnosisSelector.ReferenceId (referenceId, repositoryIdHint) ->
                let! referenceDto = dependencies.GetReference referenceId correlationId
                addActorFact "Reference" $"{referenceId:D}" None referenceDto

                let repositoryMatches =
                    repositoryIdHint
                    |> Option.forall (fun repositoryId -> repositoryId = referenceDto.RepositoryId)

                if referenceDto.ReferenceId <> referenceId
                   || referenceDto.RepositoryId = RepositoryId.Empty
                   || referenceDto.DirectoryId = DirectoryVersionId.Empty
                   || referenceDto.DeletedAt.IsSome
                   || not repositoryMatches then
                    rootFailure <- Some $"Reference '{referenceId:D}' has no readable current live root matching the selector."
                else
                    sourceBacked <- true

                    addExpected (
                        ExactRelationship.ReferenceRoot
                            {
                                RepositoryId = referenceDto.RepositoryId
                                RootDirectoryVersionId = referenceDto.DirectoryId
                                ReferenceId = referenceDto.ReferenceId
                            }
                    )

                    do! traverseDirectoryTree referenceDto.RepositoryId referenceDto.DirectoryId
            | DiagnosisSelector.DirectoryVersionId (directoryVersionId, repositoryIdHint) ->
                let! directoryVersionDto = readDirectoryVersion directoryVersionId
                let current = directoryVersionDto.DirectoryVersion

                let repositoryMatches =
                    repositoryIdHint
                    |> Option.forall (fun repositoryId -> repositoryId = current.RepositoryId)

                if current.DirectoryVersionId <> directoryVersionId
                   || current.RepositoryId = RepositoryId.Empty
                   || not repositoryMatches then
                    rootFailure <- Some $"DirectoryVersion '{directoryVersionId:D}' has no readable current state matching the selector."
                else
                    sourceBacked <- true
                    do! traverseDirectoryTree current.RepositoryId directoryVersionId
            | DiagnosisSelector.CounterTuple counterTuple ->
                addManifestTarget counterTuple
                unknownFields.Add "MissingRelationships" |> ignore
                unknownFields.Add "RebuiltCount" |> ignore

                unknownFields.Add "CompleteRepairTargets"
                |> ignore

                evidenceGaps.Add "A counter tuple has no readable source actor that can prove every expected DirectoryVersion-manifest relationship."
            | DiagnosisSelector.OperationId selectedOperationId ->
                operationId <- Some selectedOperationId

                match tryParseOperationSource selectedOperationId with
                | None ->
                    rootFailure <- Some $"Operation id '{selectedOperationId}' is not a current deterministic DirectoryVersion manifest operation identity."
                | Some (directoryVersionId, action) ->
                    let! directoryVersionDto = readDirectoryVersion directoryVersionId
                    let current = directoryVersionDto.DirectoryVersion

                    if current.DirectoryVersionId <> directoryVersionId
                       || current.RepositoryId = RepositoryId.Empty then
                        rootFailure <- Some $"Operation id '{selectedOperationId}' names a DirectoryVersion whose current actor state is not readable."
                    else
                        match directManifests directoryVersionDto correlationId with
                        | Error error ->
                            rootFailure <- Some $"Operation source DirectoryVersion '{directoryVersionId:D}' manifests could not be interpreted: {error.Error}"
                        | Ok manifests ->
                            let matchingManifest =
                                manifests
                                |> Array.tryFind (fun manifest ->
                                    let relationship =
                                        {
                                            RepositoryId = current.RepositoryId
                                            StoragePoolId = manifest.StoragePoolId
                                            ManifestAddress = manifest.ManifestAddress
                                            DirectoryVersionId = directoryVersionId
                                        }

                                    operationIdentity action relationship = selectedOperationId)

                            match matchingManifest with
                            | None -> rootFailure <- Some $"Operation id '{selectedOperationId}' cannot be supported by current source actor state."
                            | Some manifest ->
                                addManifestSourceTarget current.RepositoryId manifest

                                deterministicIdentities.Add $"{selectedOperationId}"
                                |> ignore

                                unknownFields.Add "MissingRelationships" |> ignore
                                unknownFields.Add "RebuiltCount" |> ignore

                                unknownFields.Add "CompleteRepairTargets"
                                |> ignore

                                evidenceGaps.Add "An operation id identifies one source operation, not every actor that may expect this manifest."

            match rootFailure with
            | Some failure ->
                return
                    emptyReport generatedAt target maximumRelationships DiagnosisOutcome.FailedRetain [| "CompleteDiagnosis" |]
                    |> fun report ->
                        { report with
                            ActorFacts = actorFacts.ToArray()
                            DeterministicIdentities = deterministicIdentities |> Seq.sort |> Seq.toArray
                            EvidenceGaps = [| failure |]
                        }
                    |> signReport
            | None ->
                for relationship in expected.Values do
                    cancellationToken.ThrowIfCancellationRequested()
                    let identity = noteRelationshipRead relationship

                    match! dependencies.VerifyRelationship relationship cancellationToken with
                    | ExactRelationshipPresence.Present -> observed.TryAdd(identity, relationship) |> ignore
                    | ExactRelationshipPresence.Absent ->
                        missing.Add identity |> ignore

                        repairTargets.Add $"GetOrAddExactRelationship:{identity}"
                        |> ignore

                let countEvidence = ResizeArray<ManifestCountEvidence>()

                for counterTuple in manifestTargets.Values do
                    cancellationToken.ThrowIfCancellationRequested()

                    let partition = ExactRelationshipPartition.Manifest(counterTuple.RepositoryId, counterTuple.StoragePoolId, counterTuple.ManifestAddress)

                    let mutable continuationToken = None
                    let mutable hasMore = true
                    let mutable enumerationComplete = true
                    let continuationTokens = HashSet<string>(StringComparer.Ordinal)

                    while hasMore do
                        let! page = dependencies.EnumerateRelationships partition bound continuationToken cancellationToken

                        for relationship in page.Relationships do
                            let identity = noteRelationshipRead relationship
                            observed.TryAdd(identity, relationship) |> ignore

                        match page.ContinuationToken with
                        | Some token when
                            not (String.IsNullOrWhiteSpace token)
                            && continuationTokens.Add token
                            ->
                            continuationToken <- Some token
                        | Some _ ->
                            evidenceGaps.Add("Exact relationship enumeration repeated a continuation token before completing the manifest partition.")

                            enumerationComplete <- false
                            hasMore <- false
                        | None -> hasMore <- false

                    let mutable validObservedCount = 0L
                    let mutable sourceStateComplete = true

                    for KeyValue (identity, relationship) in observed do
                        match relationship with
                        | ExactRelationship.DirectoryVersionManifest manifestRelationship when
                            manifestRelationship.RepositoryId = counterTuple.RepositoryId
                            && manifestRelationship.StoragePoolId = counterTuple.StoragePoolId
                            && manifestRelationship.ManifestAddress = counterTuple.ManifestAddress
                            ->
                            let! sourceDto = readDirectoryVersion manifestRelationship.DirectoryVersionId

                            let sourceMatchesRelationship =
                                sourceDto.DirectoryVersion.DirectoryVersionId = manifestRelationship.DirectoryVersionId
                                && sourceDto.DirectoryVersion.RepositoryId = manifestRelationship.RepositoryId

                            if not sourceMatchesRelationship then
                                sourceStateComplete <- false

                                evidenceGaps.Add(
                                    $"DirectoryVersion '{manifestRelationship.DirectoryVersionId:D}' did not return readable current state for repository '{manifestRelationship.RepositoryId:D}'."
                                )
                            else
                                match directManifests sourceDto correlationId with
                                | Ok manifests ->
                                    let sourceNamesManifest =
                                        manifests
                                        |> Array.exists (fun manifest ->
                                            manifest.StoragePoolId = manifestRelationship.StoragePoolId
                                            && manifest.ManifestAddress = manifestRelationship.ManifestAddress)

                                    if sourceNamesManifest then
                                        validObservedCount <- validObservedCount + 1L
                                    else
                                        stale.Add identity |> ignore
                                | Error error ->
                                    sourceStateComplete <- false

                                    evidenceGaps.Add(
                                        $"DirectoryVersion '{manifestRelationship.DirectoryVersionId:D}' manifests could not be interpreted: {error.Error}"
                                    )
                        | _ -> ()

                    let! counterDto = dependencies.GetCounter counterTuple correlationId

                    addActorFact
                        "RepositoryContentCounter"
                        (RepositoryContentCounter.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress)
                        (Some counterDto.Revision)
                        counterDto

                    let counterTargetMatches =
                        counterDto.RepositoryId = counterTuple.RepositoryId
                        && counterDto.StoragePoolId = counterTuple.StoragePoolId
                        && counterDto.ManifestAddress = counterTuple.ManifestAddress

                    let counterReadable =
                        counterTargetMatches
                        && String.Equals(counterDto.Class, nameof RepositoryContentCounterDto, StringComparison.Ordinal)
                        && counterDto.Revision > 0L

                    if not counterReadable then
                        let targetIdentity = $"{counterTuple.RepositoryId:D}|{counterTuple.StoragePoolId}|{counterTuple.ManifestAddress}"

                        evidenceGaps.Add(
                            $"Repository content counter for '{targetIdentity}' was uninitialized, class-incompatible, or returned state for a different target."
                        )

                    let! workflowDto = dependencies.GetWorkflow counterTuple correlationId

                    addWorkflowActorFact
                        "ManifestContributionWorkflow"
                        (ManifestContributionWorkflow.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress)
                        (Some workflowDto.Revision)
                        workflowDto

                    let workflowTargetMatches =
                        workflowDto.RepositoryId = counterTuple.RepositoryId
                        && workflowDto.StoragePoolId = counterTuple.StoragePoolId
                        && workflowDto.ManifestAddress = counterTuple.ManifestAddress

                    let workflowReadable =
                        workflowTargetMatches
                        && String.Equals(workflowDto.Class, nameof ManifestContributionWorkflowDto, StringComparison.Ordinal)
                        && not (isNull (box workflowDto.Direction))
                        && workflowDto.Direction = ManifestContributionDirection.Increment
                        && not (isNull workflowDto.Ranges)
                        && not (isNull workflowDto.CompletedRanges)
                        && not (isNull workflowDto.FailedRanges)
                        && not (isNull (box workflowDto.LifecycleState))
                        && workflowDto.StartOperationId.IsSome
                        && workflowDto.LastOperationId.IsSome
                        && workflowDto.CounterRevision > 0L
                        && workflowDto.Revision > 0L

                    let workflowRangesExact =
                        match
                            expectedWorkflowRanges.TryGetValue
                                (RepositoryContentCounter.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress)
                            with
                        | true, expectedRanges when expectedRanges.Count > 0 && workflowReadable ->
                            let actualRanges = HashSet<ManifestContributionWorkflowRange>(workflowDto.Ranges)

                            workflowDto.Ranges.Length = expectedRanges.Count
                            && actualRanges.Count = workflowDto.Ranges.Length
                            && actualRanges.SetEquals expectedRanges
                        | _ -> false

                    let workflowCompleted =
                        workflowReadable
                        && workflowRangesExact
                        && workflowDto.LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
                        && workflowDto.FailedRanges.Length = 0
                        && workflowDto.CompletedRanges.Length = workflowDto.Ranges.Length
                        && (workflowDto.CompletedRanges
                            |> Array.forall (fun progress ->
                                not (isNull (box progress))
                                && not (isNull (box progress.Range))
                                && progress.RepositoryId = counterTuple.RepositoryId
                                && progress.StoragePoolId = counterTuple.StoragePoolId
                                && progress.ManifestAddress = counterTuple.ManifestAddress))
                        && (let completedRanges =
                                workflowDto.CompletedRanges
                                |> Array.map (fun progress -> progress.Range)
                                |> HashSet<ManifestContributionWorkflowRange>

                            completedRanges.Count = workflowDto.CompletedRanges.Length
                            && completedRanges.SetEquals workflowDto.Ranges)

                    if not workflowCompleted then
                        let targetIdentity = $"{counterTuple.RepositoryId:D}|{counterTuple.StoragePoolId}|{counterTuple.ManifestAddress}"

                        if not workflowTargetMatches then
                            evidenceGaps.Add($"Manifest contribution workflow for '{targetIdentity}' returned state for a different target.")
                        elif not workflowReadable then
                            evidenceGaps.Add($"Manifest contribution workflow for '{targetIdentity}' was absent or unreadable.")
                        else
                            evidenceGaps.Add(
                                $"Manifest contribution workflow for '{targetIdentity}' has unfinished, failed, duplicate, or source-mismatched ranges."
                            )

                    let partitionEvidenceComplete =
                        enumerationComplete
                        && sourceStateComplete
                        && workflowCompleted

                    let completeEvidence = partitionEvidenceComplete && counterReadable

                    if completeEvidence then
                        for KeyValue (identity, relationship) in observed do
                            match relationship with
                            | ExactRelationship.DirectoryVersionManifest manifestRelationship when
                                manifestRelationship.RepositoryId = counterTuple.RepositoryId
                                && manifestRelationship.StoragePoolId = counterTuple.StoragePoolId
                                && manifestRelationship.ManifestAddress = counterTuple.ManifestAddress
                                && stale.Contains identity
                                ->
                                repairTargets.Add $"RemoveStaleExactRelationship:{identity}"
                                |> ignore
                            | _ -> ()

                    let rebuiltCount =
                        if sourceBacked && partitionEvidenceComplete then
                            let missingExpectedForTuple =
                                expected
                                |> Seq.sumBy (fun (KeyValue (identity, relationship)) ->
                                    match relationship with
                                    | ExactRelationship.DirectoryVersionManifest manifestRelationship when
                                        manifestRelationship.RepositoryId = counterTuple.RepositoryId
                                        && manifestRelationship.StoragePoolId = counterTuple.StoragePoolId
                                        && manifestRelationship.ManifestAddress = counterTuple.ManifestAddress
                                        && missing.Contains identity
                                        ->
                                        1L
                                    | _ -> 0L)

                            Some(validObservedCount + missingExpectedForTuple)
                        else
                            None

                    countEvidence.Add(
                        {
                            RepositoryId = $"{counterTuple.RepositoryId:D}"
                            StoragePoolId = $"{counterTuple.StoragePoolId}"
                            ManifestAddress = $"{counterTuple.ManifestAddress}"
                            StoredCount = if counterReadable then Some counterDto.Count else None
                            RebuiltCount = rebuiltCount
                            Completeness = if sourceBacked && completeEvidence then "Complete" else "IncompleteRetain"
                        }
                    )

                    match (if counterReadable then Some counterDto.Count else None), rebuiltCount with
                    | Some stored, Some rebuilt when rebuilt <> stored ->
                        repairTargets.Add($"ReconcileCounter:{counterTuple.RepositoryId:D}|{counterTuple.StoragePoolId}|{counterTuple.ManifestAddress}")
                        |> ignore
                    | _ -> ()

                let redisEvidence = ResizeArray<string>()

                match operationId with
                | Some selectedOperationId when manifestTargets.Count = 1 ->
                    let counterTuple = manifestTargets.Values |> Seq.head
                    let! recent = dependencies.GetRecentResult counterTuple selectedOperationId cancellationToken

                    match recent with
                    | Some change ->
                        redisEvidence.Add(
                            $"Observed:{change.OperationId}|{change.Operation}|{change.PreviousCount}->{change.CurrentCount}|revision:{change.Revision}"
                        )
                    | None ->
                        redisEvidence.Add "AbsentOrUnavailable"
                        evidenceGaps.Add "Redis had no recent result; durable actor and exact evidence remain authoritative."
                | Some _ -> redisEvidence.Add "NotReadableWithoutCounterTuple"
                | None -> redisEvidence.Add "NotRequested"

                if not sourceBacked then
                    for counterTuple in manifestTargets.Values do
                        repairTargets.Add(
                            $"DiagnoseReadableSourceRequired:{counterTuple.RepositoryId:D}|{counterTuple.StoragePoolId}|{counterTuple.ManifestAddress}"
                        )
                        |> ignore

                let countsMatch =
                    countEvidence
                    |> Seq.forall (fun evidence ->
                        match evidence.StoredCount, evidence.RebuiltCount with
                        | Some stored, Some rebuilt -> stored = rebuilt
                        | _ -> false)

                let verified =
                    sourceBacked
                    && missing.Count = 0
                    && stale.Count = 0
                    && evidenceGaps.Count = 0
                    && countsMatch

                let reclamationPermitted =
                    verified
                    && countEvidence.Count > 0
                    && (countEvidence
                        |> Seq.forall (fun evidence ->
                            evidence.StoredCount = Some 0L
                            && evidence.RebuiltCount = Some 0L))

                return
                    {
                        SchemaVersion = "grace.manifest-contribution-diagnosis.v1"
                        GeneratedAt = generatedAt
                        Target = target
                        MaxRelationships = maximumRelationships
                        RelationshipsRead = relationshipReads.Count
                        ActorFacts = actorFacts.ToArray()
                        ExpectedRelationships = expected.Keys |> Seq.sort |> Seq.toArray
                        ObservedRelationships = observed.Keys |> Seq.sort |> Seq.toArray
                        MissingRelationships = missing |> Seq.sort |> Seq.toArray
                        StaleRelationships = stale |> Seq.sort |> Seq.toArray
                        CountEvidence = countEvidence.ToArray()
                        DeterministicIdentities = deterministicIdentities |> Seq.sort |> Seq.toArray
                        RedisEvidence = redisEvidence.ToArray()
                        RepairTargets = repairTargets |> Seq.sort |> Seq.toArray
                        UnknownFields = unknownFields |> Seq.sort |> Seq.toArray
                        EvidenceGaps = evidenceGaps.ToArray()
                        ReclamationPermitted = reclamationPermitted
                        Outcome =
                            if verified then
                                DiagnosisOutcome.VerifiedComplete
                            else
                                DiagnosisOutcome.IncompleteRetain
                        ReportSha256 = String.Empty
                    }
                    |> signReport
        }

    /// Creates production read dependencies without exposing a mutation-capable service to the diagnosis core.
    let private productionDependencies (context: HttpContext) =
        let store = ManifestContributionAccounting.CosmosExactRelationshipStore(cosmosContainer) :> IExactRelationshipStore

        let recentResults = context.RequestServices.GetRequiredService<IRepositoryCounterRecentResult>()

        {
            GetReference =
                fun referenceId correlationId ->
                    let actor = grainFactory.CreateActorProxyWithCorrelationId<IReferenceActor>(referenceId, correlationId)

                    actor.Get correlationId
            GetDirectoryVersion =
                fun directoryVersionId correlationId ->
                    let actor = grainFactory.CreateActorProxyWithCorrelationId<IDirectoryVersionActor>(directoryVersionId, correlationId)

                    actor.Get correlationId
            EnumerateRelationships =
                fun partition bound continuationToken cancellationToken -> store.EnumerateAsync(partition, bound, continuationToken, cancellationToken)
            VerifyRelationship = fun relationship cancellationToken -> store.VerifyAsync(relationship, cancellationToken)
            GetCounter =
                fun counterTuple correlationId ->
                    let actor =
                        grainFactory.CreateActorProxyWithCorrelationId<IRepositoryContentCounterActor>(
                            RepositoryContentCounter.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress,
                            correlationId
                        )

                    actor.Get correlationId
            GetWorkflow =
                fun counterTuple correlationId ->
                    let actor =
                        grainFactory.CreateActorProxyWithCorrelationId<IManifestContributionWorkflowActor>(
                            ManifestContributionWorkflow.primaryKey counterTuple.RepositoryId counterTuple.StoragePoolId counterTuple.ManifestAddress,
                            correlationId
                        )

                    actor.Get correlationId
            GetRecentResult =
                fun counterTuple operationId cancellationToken ->
                    recentResults.TryGetAsync(
                        counterTuple.RepositoryId,
                        counterTuple.StoragePoolId,
                        counterTuple.ManifestAddress,
                        operationId,
                        cancellationToken
                    )
        }

    /// Handles the internal SystemAdmin diagnosis route and returns signed operator evidence without writing Grace state.
    let Diagnose: HttpHandler =
        fun next context ->
            task {
                try
                    let! parameters = context.BindJsonAsync<DiagnoseManifestContributionParameters>()

                    match validateParameters parameters with
                    | Error error -> return! RequestErrors.BAD_REQUEST error next context
                    | Ok selector ->
                        match ExactRelationshipReadBound.create parameters.MaxRelationships with
                        | Error error -> return! RequestErrors.BAD_REQUEST error next context
                        | Ok bound ->
                            let! report =
                                diagnoseWith
                                    (productionDependencies context)
                                    (getCurrentInstantExtended ())
                                    (getCorrelationId context)
                                    context.RequestAborted
                                    bound
                                    selector

                            return! context.WriteJsonAsync report
                with
                | RelationshipBoundExceeded error -> return! RequestErrors.BAD_REQUEST error next context
                | :? JsonException -> return! RequestErrors.BAD_REQUEST "The diagnosis request body must be valid JSON." next context
            }
