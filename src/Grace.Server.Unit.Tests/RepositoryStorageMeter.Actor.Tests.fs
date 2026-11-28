namespace Grace.Server.Tests

open Grace.Types.Common
open Grace.Types.RepositoryContentCounter
open Grace.Types.Usage
open Microsoft.Extensions.Logging.Abstractions
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Threading.Tasks

module MeterCounter = Grace.Actors.RepositoryContentCounter
module Meter = Grace.Actors.RepositoryStorageMeter

/// Durable counter snapshots for one repository, changed only through the counter's own decision and release functions.
type internal MeterTestCounters(repositoryId: RepositoryId) =
    let counters = Dictionary<Meter.CounterTarget, RepositoryContentCounterDto>()

    let metadata =
        {
            Timestamp = Instant.FromUtc(2026, 11, 28, 12, 0)
            CorrelationId = "storage-meter-test"
            Principal = "tester"
            ClientType = None
            Properties = Dictionary<string, string>()
        }

    member _.Get target =
        match counters.TryGetValue target with
        | true, counter -> counter
        | _ -> RepositoryContentCounterDto.Default

    /// Runs one counter command the way a save or a release does; the result is what the counter persists.
    member this.Handle (target: Meter.CounterTarget) command =
        match MeterCounter.decideCommand Seq.empty (this.Get target) command metadata with
        | Ok decision ->
            counters[target] <- decision.Counter
            decision
        | Error error -> failwith error.Error

    member this.Add (storagePoolId, manifestAddress) operationId size =
        this.Handle
            (storagePoolId, manifestAddress)
            (RepositoryContentCounterCommand.AddReference(operationId, repositoryId, storagePoolId, manifestAddress, size))

    member this.Remove (storagePoolId, manifestAddress) operationId =
        this.Handle
            (storagePoolId, manifestAddress)
            (RepositoryContentCounterCommand.RemoveReference(operationId, repositoryId, storagePoolId, manifestAddress))

    /// Applies a repair-only count replacement.
    member this.Repair (storagePoolId, manifestAddress) rebuiltCount =
        let current = this.Get(storagePoolId, manifestAddress)

        let command =
            {
                OperationId = MeterCounter.repairOperationId repositoryId storagePoolId manifestAddress current.Revision rebuiltCount
                RepositoryId = repositoryId
                StoragePoolId = storagePoolId
                ManifestAddress = manifestAddress
                ExpectedRevision = current.Revision
                RebuiltCount = rebuiltCount
            }

        match MeterCounter.decideRepairForKey None current command metadata with
        | Ok decision -> counters[(storagePoolId, manifestAddress)] <- decision.Counter
        | Error error -> failwith error.Error

    member this.Release target revisions = counters[target] <- MeterCounter.releaseMeterChanges revisions (this.Get target)

    member _.Targets = counters.Keys |> Seq.toList

    member _.PendingCount =
        counters.Values
        |> Seq.sumBy (fun counter -> counter.PendingMeterChanges.Length)

    /// The retained bytes Grace's own retention state has: the recorded size of every counter above zero.
    member _.Expected =
        counters.Values
        |> Seq.sumBy (fun counter -> if counter.Count > 0L then counter.LogicalSize else 0L)

/// Covers the storage meter's sweep against the counter's real transitions, with failures injected at each step.
[<Parallelizable(ParallelScope.All)>]
type RepositoryStorageMeterTests() =
    let repositoryId = Guid.Parse("6d1c0e43-3a51-4b36-9a06-3f0d46f1d2c1")
    let manifestA: Meter.CounterTarget = "pool-main", "manifest:blake3:alpha"
    let manifestB: Meter.CounterTarget = "pool-main", "manifest:blake3:beta"

    /// Builds sweep dependencies over the counters. `faults` names the step that fails the next time it runs.
    let dependencies (counters: MeterTestCounters) (persisted: RepositoryStorageMeterDto ref) (faults: HashSet<string>) : Meter.SweepDependencies =
        {
            Collect =
                fun target ->
                    task {
                        if faults.Remove "collect" then failwith "counter unavailable"
                        return (counters.Get target).PendingMeterChanges
                    }
            Release =
                fun target revisions ->
                    task {
                        if faults.Remove "release-before" then failwith "release never ran"
                        counters.Release target revisions

                        if faults.Remove "release-response-lost" then
                            failwith "release ran, response lost"
                    }
            Persist =
                fun next ->
                    task {
                        if faults.Remove "persist" then failwith "meter storage unavailable"
                        persisted.Value <- next
                    }
        }

    /// Sweeps from the meter's persisted state, the way a fresh activation does after a restart.
    let sweepAfterRestart counters persisted faults = Meter.sweepWith (dependencies counters persisted faults) persisted.Value counters.Targets

    /// Heals every fault and sweeps until nothing is pending or applied-unreleased.
    let quiesce (counters: MeterTestCounters) (persisted: RepositoryStorageMeterDto ref) =
        task {
            let mutable rounds = 0

            while (counters.PendingCount > 0
                   || not persisted.Value.AppliedChanges.IsEmpty)
                  && rounds < 10 do
                let! _ = sweepAfterRestart counters persisted (HashSet())
                rounds <- rounds + 1
        }

    /// Done-when check: a replayed counter intent plus a meter restart must not double count.
    [<Test>]
    member _.``replaying a counter add and restarting the meter counts the change once``() =
        task {
            let counters = MeterTestCounters(repositoryId)
            let persisted = ref RepositoryStorageMeterDto.Default

            let first = counters.Add manifestA "directory-version:root:add" 2765L
            Assert.That(first.Intents, Is.Not.Empty, "The keep should emit the counter's zero-crossing intent.")

            // The meter applies and persists the change, but the counter's release never runs.
            let! applied = sweepAfterRestart counters persisted (HashSet [ "release-before" ])
            Assert.That(applied.Meter.RetainedManifestBytes, Is.EqualTo 2765L)
            Assert.That(counters.PendingCount, Is.EqualTo 1)

            // The caller retries the same add, so the counter replays its intent; then the meter restarts and sweeps.
            let replay = counters.Add manifestA "directory-version:root:add" 2765L
            Assert.That(replay.WasIdempotentReplay, Is.True)
            Assert.That(replay.Intents, Is.Not.Empty, "The replay should re-create the counter's intent.")

            let! afterRestart = sweepAfterRestart counters persisted (HashSet())
            Assert.That(afterRestart.Meter.RetainedManifestBytes, Is.EqualTo 2765L)

            do! quiesce counters persisted
            Assert.That(persisted.Value.RetainedManifestBytes, Is.EqualTo 2765L)
            Assert.That(persisted.Value.AppliedChanges, Is.Empty)
            Assert.That(counters.PendingCount, Is.Zero)
        }

    /// Done-when check: a meter failure never fails the save, and the change is applied later without loss.
    [<Test>]
    member _.``a meter that cannot write never blocks a save and applies the change later``() =
        task {
            let counters = MeterTestCounters(repositoryId)
            let persisted = ref RepositoryStorageMeterDto.Default
            counters.Add manifestA "add-a" 2765L |> ignore

            // The hint the counter sends after its commit fails; the commit has already happened and the save goes on.
            do! MeterCounter.notifyMeterSafely NullLogger.Instance (fun () -> raise (InvalidOperationException "meter down"))

            let sweep () = sweepAfterRestart counters persisted (HashSet [ "persist" ]) :> Task

            Assert.ThrowsAsync<Exception>(fun () -> sweep ())
            |> ignore

            Assert.That(persisted.Value.RetainedManifestBytes, Is.Zero)

            // Saves keep working while the meter is down.
            counters.Add manifestB "add-b" 1000L |> ignore

            counters.Add manifestA "add-a-branch-2" 2765L
            |> ignore

            Assert.That(counters.PendingCount, Is.EqualTo 2)

            do! quiesce counters persisted
            Assert.That(persisted.Value.RetainedManifestBytes, Is.EqualTo 3765L)
            Assert.That(persisted.Value.RetainedManifestBytes, Is.EqualTo counters.Expected)
        }

    /// A release that ran but lost its response keeps the id applied, so the next sweep releases it without re-adding.
    [<Test>]
    member _.``a release whose response is lost is released again without counting twice``() =
        task {
            let counters = MeterTestCounters(repositoryId)
            let persisted = ref RepositoryStorageMeterDto.Default
            counters.Add manifestA "add-a" 2765L |> ignore

            let! first = sweepAfterRestart counters persisted (HashSet [ "release-response-lost" ])
            Assert.That(counters.PendingCount, Is.Zero)
            Assert.That(first.Meter.AppliedChanges, Has.Length.EqualTo 1)

            do! quiesce counters persisted
            Assert.That(persisted.Value.RetainedManifestBytes, Is.EqualTo 2765L)
            Assert.That(persisted.Value.AppliedChanges, Is.Empty)
        }

    /// A counter that cannot be read is reported back, so a read never answers as if it had no changes.
    [<Test>]
    member _.``a counter that cannot be read is reported and applied on a later sweep``() =
        task {
            let counters = MeterTestCounters(repositoryId)
            let persisted = ref RepositoryStorageMeterDto.Default
            counters.Add manifestA "add-a" 2765L |> ignore

            let! first = sweepAfterRestart counters persisted (HashSet [ "collect" ])
            Assert.That(first.Unreached, Is.EqualTo [ manifestA ])
            Assert.That(first.Meter.RetainedManifestBytes, Is.Zero)

            do! quiesce counters persisted
            Assert.That(persisted.Value.RetainedManifestBytes, Is.EqualTo 2765L)
        }

    /// Same content on another branch changes nothing; releasing the last reference reads as zero, not missing.
    [<Test>]
    member _.``only zero crossings move the total and the last release returns it to zero``() =
        task {
            let counters = MeterTestCounters(repositoryId)
            let persisted = ref RepositoryStorageMeterDto.Default
            counters.Add manifestA "branch-a" 2765L |> ignore
            do! quiesce counters persisted
            counters.Add manifestA "branch-b" 2765L |> ignore
            Assert.That(counters.PendingCount, Is.Zero)
            do! quiesce counters persisted
            Assert.That(persisted.Value.RetainedManifestBytes, Is.EqualTo 2765L)

            counters.Remove manifestA "remove-b" |> ignore
            counters.Remove manifestA "remove-a" |> ignore
            counters.Add manifestA "branch-c" 2765L |> ignore
            counters.Remove manifestA "remove-c" |> ignore
            do! quiesce counters persisted
            Assert.That(persisted.Value.RetainedManifestBytes, Is.EqualTo 0L)
        }

    /// A repair from zero keeps the manifest again with no manifest in hand, so it adds the size the counter recorded.
    [<Test>]
    member _.``a repair from zero adds the recorded size``() =
        task {
            let counters = MeterTestCounters(repositoryId)
            let persisted = ref RepositoryStorageMeterDto.Default
            counters.Add manifestA "add-a" 2765L |> ignore
            counters.Remove manifestA "remove-a" |> ignore
            do! quiesce counters persisted
            Assert.That(persisted.Value.RetainedManifestBytes, Is.Zero)

            counters.Repair manifestA 2L
            do! quiesce counters persisted
            Assert.That(persisted.Value.RetainedManifestBytes, Is.EqualTo 2765L)
            Assert.That(persisted.Value.RetainedManifestBytes, Is.EqualTo counters.Expected)
        }
