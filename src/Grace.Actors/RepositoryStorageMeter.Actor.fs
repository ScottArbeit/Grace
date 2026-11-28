namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.RepositoryContentCounter
open Grace.Types.Usage
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Groups the storage meter's keys and its sweep: the per-repository running total of retained manifest bytes.
///
/// The counter is the outbox: it commits a meter change in the same snapshot write as a zero crossing. A sweep collects
/// pending changes from the live counters, applies the ones it has not applied, and saves the total with their ids in one
/// write (the meter's commit point). It then asks each counter to release them durably and forgets the released ids.
/// A crash or lost response at any step leaves a state the next sweep finishes without losing or repeating a change.
module RepositoryStorageMeter =

    /// Keys the meter by repository in the same "N" format as RepositoryContentCounter, so both share a partition.
    let primaryKey (repositoryId: RepositoryId) = RepositoryContentCounter.storageMeterPrimaryKey repositoryId

    /// The most counters one sweep collects from, which bounds the applied ids the meter holds at once.
    [<Literal>]
    let SweepBatchLimit = 256

    /// The most sweeps one read runs while draining pending changes.
    [<Literal>]
    let ReadRoundLimit = 4096

    /// One counter that may hold pending meter changes.
    type CounterTarget = StoragePoolId * ManifestAddress

    /// The effects a sweep needs. Tests inject failures here; the actor wires them to counters and its own state.
    type SweepDependencies =
        {
            /// Reads the live counter's pending changes; the counter is the authority.
            Collect: CounterTarget -> Task<RepositoryStorageMeterChange list>
            /// Asks the counter to durably remove the given revisions.
            Release: CounterTarget -> int64 list -> Task
            /// Saves the meter's whole state in one write.
            Persist: RepositoryStorageMeterDto -> Task
        }

    /// What one sweep did: the meter after the sweep and the counters it could not read.
    type SweepResult = { Meter: RepositoryStorageMeterDto; Unreached: CounterTarget list; Progressed: bool }

    /// Names one change inside this repository's meter.
    let changeId ((storagePoolId, manifestAddress): CounterTarget) (change: RepositoryStorageMeterChange) =
        { StoragePoolId = storagePoolId; ManifestAddress = manifestAddress; Revision = change.Revision }

    /// Collects one counter's pending changes, reporting a failure instead of throwing.
    let private tryCollect (collect: CounterTarget -> Task<RepositoryStorageMeterChange list>) target =
        task {
            try
                let! changes = collect target
                return Ok(target, changes)
            with
            | _ -> return Error target
        }

    /// Releases one counter's applied revisions, reporting whether the counter confirmed it.
    let private tryRelease (release: CounterTarget -> int64 list -> Task) (target: CounterTarget, ids: RepositoryStorageMeterChangeId list) =
        task {
            try
                do! release target (ids |> List.map (fun id -> id.Revision))
                return ids
            with
            | _ -> return []
        }

    /// Runs one sweep over the given counters and returns the new meter state. A failed persist of the applied changes
    /// throws and leaves the meter unchanged; every other failure is retried by a later sweep.
    let sweepWith (dependencies: SweepDependencies) (meter: RepositoryStorageMeterDto) (targets: CounterTarget list) =
        task {
            let! collected =
                targets
                |> List.distinct
                |> List.map (tryCollect dependencies.Collect)
                |> Task.WhenAll

            let unreached =
                collected
                |> Array.choose (function
                    | Error target -> Some target
                    | Ok _ -> None)
                |> Array.toList

            let applied = HashSet<RepositoryStorageMeterChangeId>(meter.AppliedChanges)

            let fresh =
                collected
                |> Array.choose (function
                    | Ok (target, changes) ->
                        Some(
                            changes
                            |> List.map (fun change -> changeId target change, change.LogicalBytesDelta)
                        )
                    | Error _ -> None)
                |> List.concat
                |> List.distinctBy fst
                |> List.filter (fun (id, _) -> not (applied.Contains id))

            let! afterApply =
                task {
                    if fresh.IsEmpty then
                        return meter
                    else
                        let next =
                            { meter with
                                RetainedManifestBytes =
                                    meter.RetainedManifestBytes
                                    + (fresh |> List.sumBy snd)
                                AppliedChanges = meter.AppliedChanges @ (fresh |> List.map fst)
                            }

                        do! dependencies.Persist next
                        return next
                }

            // Release everything applied, including ids left by an earlier sweep that crashed or lost a response.
            let! released =
                afterApply.AppliedChanges
                |> List.groupBy (fun id -> id.StoragePoolId, id.ManifestAddress)
                |> List.map (tryRelease dependencies.Release)
                |> Task.WhenAll

            let releasedIds = HashSet<RepositoryStorageMeterChangeId>(Seq.concat released)

            let! afterRelease =
                task {
                    if releasedIds.Count = 0 then
                        return afterApply
                    else
                        let next =
                            { afterApply with
                                AppliedChanges =
                                    afterApply.AppliedChanges
                                    |> List.filter (releasedIds.Contains >> not)
                            }

                        try
                            do! dependencies.Persist next
                            return next
                        with
                        | _ ->
                            // The released ids stay applied; releasing them again is harmless.
                            return afterApply
                }

            return { Meter = afterRelease; Unreached = unreached; Progressed = not fresh.IsEmpty || releasedIds.Count > 0 }
        }

    /// Reads the counters in this repository's partition that still hold pending meter changes. Staleness only delays a
    /// change: the sweep reads each live counter, so a stale row can never apply a change twice.
    let queryPendingCounters (container: Container) (repositoryId: RepositoryId) (cancellationToken: CancellationToken) =
        task {
            if isNull container then
                return []
            else
                let partitionKey = primaryKey repositoryId

                let query =
                    QueryDefinition(
                        "SELECT c.State.StoragePoolId, c.State.ManifestAddress FROM c WHERE c.GrainType = @grainType AND c.PartitionKey = @partitionKey AND ARRAY_LENGTH(c.State.PendingMeterChanges) > 0"
                    )
                        .WithParameter("@grainType", StateName.RepositoryContentCounter)
                        .WithParameter("@partitionKey", partitionKey)

                let options = QueryRequestOptions(PartitionKey = Nullable(PartitionKey partitionKey), MaxItemCount = Nullable SweepBatchLimit)
                use iterator = container.GetItemQueryIterator<JsonElement>(query, requestOptions = options)
                let targets = List<CounterTarget>()

                while iterator.HasMoreResults do
                    let! page = iterator.ReadNextAsync cancellationToken

                    page.Resource
                    |> Seq.iter (fun row -> targets.Add(row.GetProperty("StoragePoolId").GetString(), row.GetProperty("ManifestAddress").GetString()))

                return targets |> Seq.toList
        }

    /// Implements the Orleans grain for one repository's storage meter.
    type RepositoryStorageMeterActor
        (
            [<PersistentState(StateName.RepositoryStorageMeter, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<RepositoryStorageMeterDto>
        ) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("RepositoryStorageMeter.Actor")
        let mutable meter = RepositoryStorageMeterDto.Default
        let hints = HashSet<CounterTarget>()
        let mutable sweepTimer: IGrainTimer = null

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        /// The repository this meter counts for, taken from its key.
        member private this.RepositoryId = Guid.ParseExact(this.GetPrimaryKeyString(), "N")

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            meter <-
                if state.RecordExists then
                    state.State
                else
                    { RepositoryStorageMeterDto.Default with RepositoryId = this.RepositoryId }

            // Finish releasing changes a previous activation applied.
            if not meter.AppliedChanges.IsEmpty then this.ScheduleSweep()

            Task.CompletedTask

        /// Saves the whole meter state; the in-memory copy changes only after the write succeeds.
        member private this.Persist(next: RepositoryStorageMeterDto) : Task =
            task {
                let previous = state.State
                state.State <- next

                try
                    do! state.WriteStateAsync()
                    meter <- next
                with
                | ex ->
                    state.State <- previous
                    return raise ex
            }

        /// Wires the sweep to this repository's counters and this meter's state.
        member private this.Dependencies =
            let grainFactory = this.GrainFactory
            let repositoryId = this.RepositoryId

            let counter (storagePoolId, manifestAddress) =
                grainFactory.GetGrain<IRepositoryContentCounterActor>(RepositoryContentCounter.primaryKey repositoryId storagePoolId manifestAddress)

            {
                Collect =
                    fun target ->
                        (counter target)
                            .GetPendingMeterChanges this.correlationId
                Release = fun target revisions -> (counter target).ReleaseMeterChanges revisions this.correlationId
                Persist = fun next -> this.Persist next
            }

        /// Runs one sweep over up to a batch of hinted counters and the given extra targets.
        member private this.SweepOnce(extra: CounterTarget list) =
            task {
                let targets =
                    Seq.append hints extra
                    |> Seq.distinct
                    |> Seq.truncate SweepBatchLimit
                    |> Seq.toList

                targets |> List.iter (hints.Remove >> ignore)

                let! result = sweepWith this.Dependencies meter targets

                // Counters that could not be read get another try on the next sweep.
                result.Unreached
                |> List.iter (hints.Add >> ignore)

                return result
            }

        /// Starts a short one-shot timer so hints that arrive together share one sweep and one meter write.
        member private this.ScheduleSweep() =
            if isNull sweepTimer then
                sweepTimer <-
                    (this :> IGrainBase)
                        .RegisterGrainTimer((fun (_: CancellationToken) -> this.OnSweepTimer()), TimeSpan.FromMilliseconds 500.0, Timeout.InfiniteTimeSpan)

        /// Sweeps the hinted counters, then schedules another sweep while work remains.
        member private this.OnSweepTimer() : Task =
            task {
                if not (isNull sweepTimer) then
                    sweepTimer.Dispose()
                    sweepTimer <- null

                try
                    let! _ = this.SweepOnce []
                    ()
                with
                | ex ->
                    log.LogWarning(
                        "{CurrentInstant}: Node: {HostName}; RepositoryId: {RepositoryId}; Storage meter sweep failed and will retry. Error: {Error}",
                        getCurrentInstantExtended (),
                        getMachineName,
                        this.RepositoryId,
                        ex.Message
                    )

                if hints.Count > 0
                   || not meter.AppliedChanges.IsEmpty then
                    this.ScheduleSweep()
            }

        interface IRepositoryStorageMeterActor with
            /// Records the hint and schedules a sweep; the counter that sent it never waits.
            member this.NotifyPendingChange storagePoolId manifestAddress =
                hints.Add((storagePoolId, manifestAddress))
                |> ignore

                this.ScheduleSweep()
                Task.CompletedTask

            /// Drains every pending change the partition query finds, then answers from the total.
            member this.ReadRetainedManifestBytes correlationId =
                task {
                    this.correlationId <- correlationId
                    let mutable rounds = 0
                    let mutable finished = false
                    let unreached = HashSet<CounterTarget>()

                    while not finished && rounds < ReadRoundLimit do
                        rounds <- rounds + 1
                        let! pending = queryPendingCounters cosmosContainer this.RepositoryId CancellationToken.None
                        let targets = pending |> List.filter (unreached.Contains >> not)

                        if targets.IsEmpty && hints.Count = 0 then
                            finished <- true
                        else
                            let! result = this.SweepOnce targets

                            result.Unreached
                            |> List.iter (unreached.Add >> ignore)

                            if not result.Progressed
                               && result.Unreached.IsEmpty
                               && targets.Length <= SweepBatchLimit then
                                // Only stale rows remain: every counter returned nothing new to apply.
                                finished <- true

                    if unreached.Count > 0 then
                        return
                            invalidOp
                                $"The storage meter could not read {unreached.Count} counter(s) with pending changes for repository {this.RepositoryId}. Retry the read."
                    else
                        return meter.RetainedManifestBytes
                }
