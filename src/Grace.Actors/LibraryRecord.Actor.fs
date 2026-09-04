namespace Grace.Actors

open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Types.Library
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Implements shared exact-write rules for bounded Library record actors.
module private LibraryRecord =

    [<Literal>]
    let HistorySegmentEntryLimit = 512

    [<Literal>]
    let HistorySegmentByteLimit = 921600

    /// Serializes a persisted value with Grace's stable JSON settings for deterministic retry comparison.
    let private serialize value = JsonSerializer.Serialize(value, Constants.JsonSerializerOptions)

    /// Reports whether two persisted values are byte-equivalent under Grace's stable serialization.
    let equivalent left right = String.Equals(serialize left, serialize right, StringComparison.Ordinal)

    /// Returns the actor record when the Orleans state provider reports that it exists.
    let read (state: IPersistentState<'T>) = if state.RecordExists then Some state.State else None

    /// Creates an immutable actor record or confirms that an existing retry is exact.
    let createExact identity (state: IPersistentState<'T>) candidate =
        task {
            if state.RecordExists then
                if not (equivalent state.State candidate) then
                    invalidOp $"Immutable Library record {identity} is not byte-equivalent to its deterministic retry."
            else
                state.State <- candidate
                do! state.WriteStateAsync()
        }

    /// Applies one cursor-ordered projection without allowing an older or different equal-position value to win.
    let upsert identity (state: IPersistentState<'T>) candidate candidateCursor existingCursor =
        task {
            if not state.RecordExists then
                state.State <- candidate
                do! state.WriteStateAsync()
            else
                let currentCursor = existingCursor state.State

                if currentCursor = candidateCursor then
                    if not (equivalent state.State candidate) then
                        invalidOp $"Library projection {identity} at cursor {candidateCursor} is not byte-equivalent to its deterministic retry."
                elif currentCursor < candidateCursor then
                    state.State <- candidate
                    do! state.WriteStateAsync()
        }

    /// Returns the current Orleans storage version without exposing a null provider value.
    let version (state: IPersistentState<'T>) = if isNull state.Etag then String.Empty else state.Etag

/// Implements the proven send-first, failure-only GraceEvent fallback effect order.
module GraceEventDelivery =

    /// Reports whether the exact envelope was delivered or remains deferred after a terminal send failure.
    type Result =
        | Delivered
        | Deferred

    /// Sends first, persists only a new terminal failure, and clears existing fallback only after send success.
    let attempt (send: FailedGraceEventEnvelope -> Task) (persist: FailedGraceEventEnvelope -> Task) (clear: unit -> Task) fallbackExists envelope =
        task {
            let! sendResult =
                task {
                    try
                        do! send envelope
                        return Ok()
                    with
                    | _ -> return Error()
                }

            match sendResult with
            | Ok () ->
                if fallbackExists then do! clear ()
                return Delivered
            | Error () ->
                if not fallbackExists then do! persist envelope
                return Deferred
        }

/// Persists one repository's authoritative Library control record.
type LibraryControlRecordActor([<PersistentState("library-control-record", Constants.LibraryControlStorage)>] state: IPersistentState<LibraryControlDocument>) =
    inherit Grain()

    interface ILibraryControlRecordActor with

        member _.Ensure candidate =
            task {
                if not state.RecordExists then
                    state.State <- candidate
                    do! state.WriteStateAsync()

                return state.State, LibraryRecord.version state
            }

        member _.Read() =
            Task.FromResult(
                LibraryRecord.read state
                |> Option.map (fun document -> document, LibraryRecord.version state)
            )

        member _.Replace candidate expectedVersion =
            task {
                if
                    not state.RecordExists
                    || not (String.Equals(LibraryRecord.version state, expectedVersion, StringComparison.Ordinal))
                then
                    return false
                else
                    state.State <- candidate
                    do! state.WriteStateAsync()
                    return true
            }

/// Persists one catalog-operation projection whose accepted result is final only after the control version is recorded.
type LibraryCatalogOperationRecordActor
    (
        [<PersistentState("library-catalog-operation", Constants.LibraryControlStorage)>] state: IPersistentState<LibraryCatalogOperationDocument>
    ) =
    inherit Grain()

    interface ILibraryCatalogOperationRecordActor with
        member _.Read() = Task.FromResult(LibraryRecord.read state)

        member _.StoreAuthoritative candidate =
            task {
                match LibraryRecord.read state with
                | None ->
                    state.State <- candidate
                    do! state.WriteStateAsync()
                | Some existing when LibraryRecord.equivalent existing candidate -> ()
                | Some existing when
                    existing.OperationId = candidate.OperationId
                    && String.Equals(existing.RequestHash, candidate.RequestHash, StringComparison.Ordinal)
                    && existing.ControlCommitVersion.IsNone
                    && existing.Result.Outcome = OutcomeKind.Accepted
                    && (candidate.ControlCommitVersion.IsSome
                        || candidate.Result.Outcome <> OutcomeKind.Accepted)
                    ->
                    state.State <- candidate
                    do! state.WriteStateAsync()
                | Some _ -> invalidOp $"Catalog operation {candidate.id} cannot be replaced by different authoritative content."
            }

/// Persists one immutable accepted Library change under its deterministic cursor identity.
type LibraryCanonicalChangeRecordActor
    (
        [<PersistentState("library-canonical-change", Constants.LibraryChangesStorage)>] state: IPersistentState<LibraryCanonicalChangeDocument>
    ) =
    inherit Grain()

    interface ILibraryCanonicalChangeRecordActor with
        member _.Read() = Task.FromResult(LibraryRecord.read state)
        member _.CreateExact candidate = LibraryRecord.createExact candidate.id state candidate

/// Persists one bounded current Library item projection.
type LibraryCurrentItemRecordActor
    (
        [<PersistentState("library-current-item", Constants.LibraryCurrentStorage)>] state: IPersistentState<LibraryCurrentItemDocument>
    ) =
    inherit Grain()

    interface ILibraryCurrentItemRecordActor with
        member _.Read() = Task.FromResult(LibraryRecord.read state)

        member _.Upsert candidate = LibraryRecord.upsert candidate.id state candidate candidate.LastCursor (fun current -> current.LastCursor)

/// Persists one bounded current Library namespace-slot projection.
type LibraryCurrentSlotRecordActor
    (
        [<PersistentState("library-current-slot", Constants.LibraryCurrentStorage)>] state: IPersistentState<LibraryCurrentSlotDocument>
    ) =
    inherit Grain()

    interface ILibraryCurrentSlotRecordActor with
        member _.Read() = Task.FromResult(LibraryRecord.read state)

        member _.Upsert candidate = LibraryRecord.upsert candidate.id state candidate candidate.LastCursor (fun current -> current.LastCursor)

/// Persists one bounded deterministic-identity bucket for current Library projections.
type LibraryCurrentIndexBucketActor
    (
        [<PersistentState("library-current-index-bucket", Constants.LibraryCurrentStorage)>] state: IPersistentState<LibraryCurrentIndexBucketDocument>
    ) =
    inherit Grain()

    interface ILibraryCurrentIndexBucketActor with

        member _.Add identity =
            task {
                let identities = if state.RecordExists then state.State.Identities else Array.empty

                if identities |> Array.contains identity then
                    return identities
                elif identities.Length >= 512 then
                    return invalidOp "A bounded Library current-index bucket reached its 512-identity limit."
                else
                    let replacement =
                        Array.append identities [| identity |]
                        |> Array.sort

                    state.State <- { Identities = replacement }
                    do! state.WriteStateAsync()
                    return replacement
            }

        member _.Read() = Task.FromResult(if state.RecordExists then state.State.Identities else Array.empty)

/// Persists one fixed-width occupancy directory for bounded current-index buckets.
type LibraryCurrentIndexDirectoryActor
    (
        [<PersistentState("library-current-index-directory", Constants.LibraryCurrentStorage)>] state: IPersistentState<LibraryCurrentIndexDirectoryDocument>
    ) =
    inherit Grain()

    interface ILibraryCurrentIndexDirectoryActor with

        member _.SetCount lowByte count =
            task {
                if lowByte < 0 || lowByte > 255 then
                    invalidArg (nameof lowByte) "A Library current-index directory entry must be between 0 and 255."

                if count < 0 || count > 512 then
                    invalidArg (nameof count) "A Library current-index bucket count must be between 0 and 512."

                let counts =
                    if state.RecordExists then
                        Array.copy state.State.Counts
                    else
                        Array.zeroCreate 256

                if counts.Length <> 256 then
                    invalidOp "A Library current-index directory does not contain exactly 256 entries."

                if counts[lowByte] <> count then
                    counts[lowByte] <- count
                    state.State <- { Counts = counts }
                    do! state.WriteStateAsync()
            }

        member _.Read() =
            Task.FromResult(
                if state.RecordExists then
                    Array.copy state.State.Counts
                else
                    Array.zeroCreate 256
            )

/// Persists one deterministic Library operation receipt.
type LibraryReceiptRecordActor([<PersistentState("library-receipt", Constants.LibraryReceiptsStorage)>] state: IPersistentState<LibraryReceiptDocument>) =
    inherit Grain()

    interface ILibraryReceiptRecordActor with
        member _.Read() = Task.FromResult(LibraryRecord.read state)

        member _.Upsert candidate = LibraryRecord.upsert candidate.id state candidate candidate.AppliedThrough (fun current -> current.AppliedThrough)

/// Persists one bounded, cursor-ordered Library history segment.
type LibraryHistorySegmentRecordActor
    (
        [<PersistentState("library-history-segment", Constants.LibraryHistoryStorage)>] state: IPersistentState<LibraryHistorySegmentDocument>
    ) =
    inherit Grain()

    interface ILibraryHistorySegmentRecordActor with
        member _.Read() = Task.FromResult(LibraryRecord.read state)

        member this.Append emptySegment entry =
            task {
                let current = if state.RecordExists then state.State else emptySegment

                match current.Entries
                      |> Array.tryFind (fun candidate -> candidate.Cursor = entry.Cursor)
                    with
                | Some existing when LibraryRecord.equivalent existing entry -> ()
                | Some _ -> invalidOp $"Library history cursor {entry.Cursor} is not byte-equivalent to its deterministic retry."
                | None ->
                    if current.Entries.Length
                       >= LibraryRecord.HistorySegmentEntryLimit then
                        invalidOp $"Library history segment {current.id} reached its entry bound."

                    let entries =
                        Array.append current.Entries [| entry |]
                        |> Array.sortBy (fun candidate -> candidate.Cursor)

                    let replacement =
                        { current with
                            FirstCursor = entries[0].Cursor
                            LastCursor = entries[entries.Length - 1].Cursor
                            EntryCount = entries.Length
                            Entries = entries
                        }

                    let serializedBytes =
                        replacement
                        |> JsonSerializer.Serialize
                        |> Encoding.UTF8.GetByteCount

                    if serializedBytes > LibraryRecord.HistorySegmentByteLimit then
                        invalidOp $"Library history segment {current.id} reached its byte bound."

                    state.State <- replacement
                    do! state.WriteStateAsync()
            }

/// Persists one immutable byte-bounded Library baseline shard.
type LibraryBaselineShardRecordActor
    (
        [<PersistentState("library-baseline-shard", Constants.LibraryBaselinesStorage)>] state: IPersistentState<LibraryBaselineShardDocument>
    ) =
    inherit Grain()

    interface ILibraryBaselineShardRecordActor with
        member _.Read() = Task.FromResult(LibraryRecord.read state)
        member _.CreateExact candidate = LibraryRecord.createExact candidate.id state candidate

/// Persists one immutable published Library baseline manifest.
type LibraryBaselineManifestRecordActor
    (
        [<PersistentState("library-baseline-manifest", Constants.LibraryBaselinesStorage)>] state: IPersistentState<LibraryBaselineManifestDocument>
    ) =
    inherit Grain()

    interface ILibraryBaselineManifestRecordActor with
        member _.Read() = Task.FromResult(LibraryRecord.read state)
        member _.CreateExact candidate = LibraryRecord.createExact candidate.id state candidate

/// Persists one retained immutable-content location behind its public Library identity.
type LibraryContentLocationRecordActor
    (
        [<PersistentState("library-content-location", Constants.LibraryReceiptsStorage)>] state: IPersistentState<LibraryContentLocationDocument>
    ) =
    inherit Grain()

    interface ILibraryContentLocationRecordActor with
        member _.Read() = Task.FromResult(LibraryRecord.read state)
        member _.CreateExact candidate = LibraryRecord.createExact candidate.id state candidate

/// Retains one exact failed Library GraceEvent envelope and retries it whenever Orleans activates the record actor.
type LibraryGraceEventFallbackActor
    (
        [<PersistentState("library-grace-event-fallback", Constants.LibraryReceiptsStorage)>] state: IPersistentState<FailedGraceEventEnvelope>,
        sender: Services.IGraceEventSender
    ) =
    inherit Grain()

    let log = Context.loggerFactory.CreateLogger("LibraryGraceEventFallback.Actor")

    /// Requests deactivation without exposing the inherited protected call to an F# task state machine.
    member private this.DeactivateActorOnIdle() = this.DeactivateOnIdle()

    /// Sends one exact envelope and owns its failure-only Orleans state transition.
    member private this.Deliver(envelope: FailedGraceEventEnvelope) =
        task {
            let expectedActorKey = $"{envelope.RepositoryId:D}|{envelope.RecordKind}|{envelope.RecordKey}"

            if not (String.Equals(this.GetPrimaryKeyString(), expectedActorKey, StringComparison.Ordinal)) then
                invalidArg (nameof envelope) "The failed GraceEvent envelope identity does not match the fallback actor key."

            let fallbackExists = state.RecordExists

            if
                fallbackExists
                && not (LibraryRecord.equivalent state.State envelope)
            then
                invalidOp $"Failed GraceEvent envelope {envelope.MessageId} is not byte-equivalent to its deterministic retry."

            let! result =
                GraceEventDelivery.attempt
                    (fun candidate -> sender.SendAsync(candidate, CancellationToken.None))
                    (fun candidate ->
                        task {
                            state.State <- candidate
                            do! state.WriteStateAsync()
                        }
                        :> Task)
                    (fun () -> state.ClearStateAsync())
                    fallbackExists
                    envelope

            match result with
            | GraceEventDelivery.Delivered -> log.LogInformation("Delivered Library GraceEvent {MessageId}; fallback state is clear.", envelope.MessageId)
            | GraceEventDelivery.Deferred ->
                log.LogWarning(
                    "Deferred Library GraceEvent {MessageId} after terminal transport failure; exact fallback state is retained.",
                    envelope.MessageId
                )

                this.DeactivateActorOnIdle()

            return result
        }

    override this.OnActivateAsync(_cancellationToken) =
        task {
            if state.RecordExists then
                let! _ = this.Deliver state.State
                ()
        }
        :> Task

    interface ILibraryGraceEventFallbackActor with

        member this.Publish envelope =
            task {
                let! _ = this.Deliver envelope
                ()
            }
            :> Task

        member this.Retry() =
            task {
                if state.RecordExists then
                    let! _ = this.Deliver state.State
                    return not state.RecordExists
                else
                    return true
            }

        member _.Read() = Task.FromResult(LibraryRecord.read state)
