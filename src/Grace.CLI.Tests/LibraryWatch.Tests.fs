namespace Grace.CLI.Tests

open Grace.CLI.Command
open Grace.CLI.LibraryLocalState
open Grace.Shared.Client.Configuration
open Grace.Shared.Utilities
open Grace.Types.Library
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks

/// Proves a Library failure never stops Grace Watch: each tick records the failure for status, retries on a backoff
/// and keeps using the last catalog it read. Status is read back through the real local database.
[<NonParallelizable>]
module LibraryWatchTests =

    /// Builds one catalog version for a repository.
    let private catalog repositoryId libraries =
        {
            RepositoryId = repositoryId
            Version = Guid.NewGuid()
            Libraries = libraries
            CreatedAt = getCurrentInstant ()
            CreatedBy = "library-watch-test"
            PreviousVersion = None
        }

    /// Creates a disposable working copy whose Library sync is enabled, paused or disabled.
    let private workingCopy participation =
        task {
            let root = Path.Combine(Path.GetTempPath(), $"grace-library-watch-{Guid.NewGuid():N}")
            Directory.CreateDirectory(root) |> ignore
            let configuration = GraceConfiguration()
            configuration.RootDirectory <- root
            configuration.RepositoryId <- Guid.NewGuid()
            configuration.GraceStatusFile <- Path.Combine(root, "grace-local.db")
            do! initialize configuration.GraceStatusFile
            let startup = catalog configuration.RepositoryId [| "Library" |]

            if participation <> "disabled" then
                enable
                    configuration.GraceStatusFile
                    {
                        RepositoryId = configuration.RepositoryId
                        Catalog = startup
                        AppliedCursor = "opaque-applied"
                        BaselinePageToken = None
                        State = "current"
                        Paused = (participation = "paused")
                        Baseline = None
                    }

            return configuration, startup
        }

    /// Counts what the tick asked Watch to do.
    type private Calls() =
        member val Runs = 0 with get, set
        member val CatalogReads = 0 with get, set
        member val Logs = List<string>()

    /// Builds tick ports over the real local database, with a controllable clock, catalog read and sync run.
    let private ports
        (configuration: GraceConfiguration)
        (clock: unit -> DateTime)
        (calls: Calls)
        (readCatalog: unit -> Task<LibraryCatalogDto>)
        (run: unit -> Task)
        : LibraryWatch.Ports
        =
        {
            Now = clock
            ReadStatus = fun () -> LibrarySynchronization.status configuration
            EnsureRegistered = fun () -> Task.CompletedTask
            Classify = fun () -> Task.CompletedTask
            Run =
                fun () ->
                    calls.Runs <- calls.Runs + 1
                    run ()
            ReadCatalog =
                fun () ->
                    calls.CatalogReads <- calls.CatalogReads + 1
                    readCatalog ()
            ReadHealth = fun () -> readHealth configuration.GraceStatusFile configuration.RepositoryId
            RecordFailure = recordFailure configuration.GraceStatusFile configuration.RepositoryId
            ClearFailure = clearFailure configuration.GraceStatusFile configuration.RepositoryId
            Log = calls.Logs.Add
        }

    /// Retries start at five seconds, double after each failure, and stop growing at five minutes.
    [<Test>]
    let ``retry delay doubles from five seconds and caps at five minutes`` () =
        let seconds =
            [ 0; 1; 2; 3; 6; 7; 50 ]
            |> List.map (fun failures -> (LibraryWatch.retryDelay failures).TotalSeconds)

        Assert.That(
            seconds,
            Is.EqualTo(
                [
                    0.0
                    5.0
                    10.0
                    20.0
                    160.0
                    300.0
                    300.0
                ]
            )
        )

    /// A failed catalog read never stops Watch, keeps the last catalog and is shown in status, whatever the copy's participation.
    [<TestCase("enabled"); TestCase("paused"); TestCase("disabled")>]
    let ``failed catalog read keeps the last catalog and is shown in status`` participation =
        task {
            let! configuration, startup = workingCopy participation
            let calls = Calls()
            let now = ref DateTime.UtcNow
            let catalogAvailable = ref false

            let readCatalog () =
                if catalogAvailable.Value then
                    Task.FromResult startup
                else
                    Task.FromException<LibraryCatalogDto>(HttpRequestException("The Library catalog route is unavailable."))

            let tickPorts = ports configuration (fun () -> now.Value) calls readCatalog (fun () -> Task.CompletedTask)

            let initial = LibraryWatch.initialState now.Value startup
            now.Value <- now.Value + LibraryWatch.pullInterval
            let! failed = LibraryWatch.tick tickPorts CancellationToken.None false false initial

            Assert.That(calls.CatalogReads, Is.EqualTo(1))
            Assert.That(failed.Catalog, Is.EqualTo(startup), "Watch must keep excluding the last catalog's roots.")
            Assert.That(failed.CatalogFailures, Is.EqualTo(1))
            Assert.That(failed.NextCatalogRefresh, Is.EqualTo(now.Value + LibraryWatch.retryDelay 1))
            Assert.That(calls.Runs, Is.EqualTo(if participation = "enabled" then 1 else 0))

            let! status = LibrarySynchronization.status configuration
            Assert.That(status.Enabled, Is.EqualTo(participation <> "disabled"))
            Assert.That(status.LastError, Is.EqualTo(Some "The Library catalog route is unavailable."))
            Assert.That(status.NextStep.Value, Does.Contain("last Library catalog"))
            Assert.That(status.LastErrorAt.IsSome, Is.True)

            Assert.That(
                calls.Logs
                |> Seq.exists (fun line -> line.Contains("Library catalog read failed")),
                Is.True
            )

            // Before the backoff ends, Watch doesn't read the catalog again.
            now.Value <- now.Value + TimeSpan.FromSeconds 1.0
            let! waiting = LibraryWatch.tick tickPorts CancellationToken.None false false failed
            Assert.That(calls.CatalogReads, Is.EqualTo(1))

            // Once the catalog can be read again, the failure stops showing in status.
            catalogAvailable.Value <- true
            now.Value <- waiting.NextCatalogRefresh
            let! recovered = LibraryWatch.tick tickPorts CancellationToken.None false false waiting
            Assert.That(calls.CatalogReads, Is.EqualTo(2))
            Assert.That(recovered.CatalogFailures, Is.Zero)
            let! clean = LibrarySynchronization.status configuration
            Assert.That(clean.LastError, Is.EqualTo(None))
            Assert.That(clean.NextStep, Is.EqualTo(None))
        }

    /// A sync that keeps failing is retried on a backoff, not on every one-second tick, and status shows the failure.
    [<Test>]
    let ``failing sync is retried on a backoff`` () =
        task {
            let! configuration, startup = workingCopy "enabled"
            let calls = Calls()
            let start = DateTime.UtcNow
            let now = ref start
            let succeed = ref false

            let run () =
                if succeed.Value then
                    recordSyncSuccess configuration.GraceStatusFile configuration.RepositoryId (getCurrentInstant().ToUnixTimeTicks())
                    Task.CompletedTask
                else
                    Task.FromException(InvalidOperationException("Simulated Library sync failure."))

            let tickPorts = ports configuration (fun () -> now.Value) calls (fun () -> Task.FromResult startup) run

            let mutable state = LibraryWatch.initialState start startup
            let runsAt = List<int>()

            // Tick once a second for 40 seconds and note which seconds ran a sync.
            let mutable second = 0

            while second <= 40 do
                now.Value <- start + TimeSpan.FromSeconds(float second)
                let before = calls.Runs
                let! next = LibraryWatch.tick tickPorts CancellationToken.None false false state
                state <- next

                if calls.Runs > before then runsAt.Add second

                second <- second + 1

            Assert.That(runsAt, Is.EqualTo([ 0; 5; 15; 35 ]))
            Assert.That(state.SyncFailures, Is.EqualTo(4))

            let! failed = LibrarySynchronization.status configuration
            Assert.That(failed.LastError, Is.EqualTo(Some "Simulated Library sync failure."))
            Assert.That(failed.NextStep, Is.EqualTo(Some LibrarySynchronization.retryNextStep))

            // When sync succeeds again, the error stops showing and the ordinary five-second pull resumes.
            succeed.Value <- true
            now.Value <- state.NextSync
            let! recovered = LibraryWatch.tick tickPorts CancellationToken.None false false state
            Assert.That(recovered.SyncFailures, Is.Zero)
            Assert.That(recovered.NextSync, Is.EqualTo(now.Value + LibraryWatch.pullInterval))
            let! clean = LibrarySynchronization.status configuration
            Assert.That(clean.LastError, Is.EqualTo(None))
            Assert.That(clean.LastSuccessfulSyncAt.IsSome, Is.True)
        }

    /// A failure the sync run recorded itself, with its affected path, isn't replaced by the tick's less specific record.
    [<Test>]
    let ``tick keeps the failure a sync run recorded with its path`` () =
        task {
            let! configuration, startup = workingCopy "enabled"
            let calls = Calls()
            let now = DateTime.UtcNow
            let operationId = Guid.NewGuid()

            let run () =
                LibrarySynchronization.LibrarySyncFailureException(
                    "Library creation slot is occupied; saved bytes are retained.",
                    Some "Library/same.txt",
                    Some operationId,
                    "Another working copy created a file with this name first."
                )
                :> exn
                |> LibrarySynchronization.describeFailure SyncFailureSource.Sync None None
                |> recordFailure configuration.GraceStatusFile configuration.RepositoryId

                Task.FromException(InvalidOperationException("Library creation slot is occupied; saved bytes are retained."))

            let tickPorts = ports configuration (fun () -> now) calls (fun () -> Task.FromResult startup) run

            let! _ = LibraryWatch.tick tickPorts CancellationToken.None false false (LibraryWatch.initialState now startup)
            let! status = LibrarySynchronization.status configuration
            Assert.That(status.LastErrorPath, Is.EqualTo(Some "Library/same.txt"))
            Assert.That(status.LastErrorOperationId, Is.EqualTo(Some operationId))
            Assert.That(status.NextStep, Is.EqualTo(Some "Another working copy created a file with this name first."))
        }

    /// A catalog failure, and its recovery, never erase the reason a sync is still blocked.
    [<Test>]
    let ``catalog failure and recovery keep the blocked sync reason`` () =
        task {
            let! configuration, _ = workingCopy "enabled"
            let db, repositoryId = configuration.GraceStatusFile, configuration.RepositoryId
            let now = getCurrentInstant().ToUnixTimeTicks()

            recordFailure
                db
                repositoryId
                {
                    Source = SyncFailureSource.Sync
                    Message = "Library creation slot is occupied; saved bytes are retained."
                    Path = Some "Library/same.txt"
                    OperationId = None
                    NextStep = "Another working copy created a file with this name first."
                    AtUnixTicks = now
                }

            recordFailure
                db
                repositoryId
                {
                    Source = SyncFailureSource.Catalog
                    Message = "The Library catalog route is unavailable."
                    Path = None
                    OperationId = None
                    NextStep = "Grace Watch keeps using the last Library catalog it read and retries automatically."
                    AtUnixTicks = now + 1L
                }

            let! duringOutage = LibrarySynchronization.status configuration
            Assert.That(duringOutage.LastError, Is.EqualTo(Some "The Library catalog route is unavailable."))

            clearFailure db repositoryId SyncFailureSource.Catalog
            let! afterRecovery = LibrarySynchronization.status configuration
            Assert.That(afterRecovery.LastError, Is.EqualTo(Some "Library creation slot is occupied; saved bytes are retained."))
            Assert.That(afterRecovery.LastErrorPath, Is.EqualTo(Some "Library/same.txt"))

            recordSyncSuccess db repositoryId (now + 2L)
            let! afterSync = LibrarySynchronization.status configuration
            Assert.That(afterSync.LastError, Is.EqualTo(None))
            Assert.That(afterSync.LastSuccessfulSyncAt.IsSome, Is.True)
        }

    /// A failure that takes longer than its backoff still gets the full rest before the next attempt.
    [<Test>]
    let ``backoff starts when a slow failure ends`` () =
        task {
            let! configuration, startup = workingCopy "enabled"
            let calls = Calls()
            let start = DateTime.UtcNow
            let now = ref start

            // Each failed attempt takes 15 seconds, longer than the first 5-second backoff.
            let run () =
                now.Value <- now.Value + TimeSpan.FromSeconds 15.0
                Task.FromException(TimeoutException("The Library request timed out."))

            let tickPorts = ports configuration (fun () -> now.Value) calls (fun () -> Task.FromResult startup) run

            let! first = LibraryWatch.tick tickPorts CancellationToken.None false false (LibraryWatch.initialState start startup)
            Assert.That(calls.Runs, Is.EqualTo(1))
            Assert.That(first.NextSync, Is.EqualTo(start + TimeSpan.FromSeconds 20.0))

            now.Value <- now.Value + TimeSpan.FromSeconds 1.0
            let! _ = LibraryWatch.tick tickPorts CancellationToken.None false false first
            Assert.That(calls.Runs, Is.EqualTo(1), "The next timer tick retried before the backoff after the failure ended.")
        }

    /// A requested stop isn't a failure: the run records nothing and doesn't mark the copy blocked.
    [<Test>]
    let ``requested stop leaves status unchanged while other failures block`` () =
        task {
            let! configuration, _ = workingCopy "enabled"
            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()

            LibrarySynchronization.finishFailedRun configuration None cancellation.Token (OperationCanceledException(cancellation.Token))
            let! stopped = LibrarySynchronization.status configuration
            Assert.That(stopped.State, Is.EqualTo("current"))
            Assert.That(stopped.LastError, Is.EqualTo(None))

            LibrarySynchronization.finishFailedRun configuration None CancellationToken.None (InvalidOperationException("Simulated Library sync failure."))
            let! failed = LibrarySynchronization.status configuration
            Assert.That(failed.State, Is.EqualTo("blocked"))
            Assert.That(failed.LastError, Is.EqualTo(Some "Simulated Library sync failure."))
        }

    /// Stopping Watch is the one thing allowed to propagate out of a tick, and it isn't recorded as a failure.
    [<Test>]
    let ``cancellation propagates and is not recorded`` () =
        task {
            let! configuration, startup = workingCopy "enabled"
            let calls = Calls()
            let now = DateTime.UtcNow
            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()

            let tickPorts = ports configuration (fun () -> now) calls (fun () -> Task.FromResult startup) (fun () -> Task.FromCanceled(cancellation.Token))

            try
                let! _ = LibraryWatch.tick tickPorts cancellation.Token false false (LibraryWatch.initialState now startup)
                Assert.Fail("Cancellation of Watch must propagate.")
            with
            | :? OperationCanceledException -> ()

            let! status = LibrarySynchronization.status configuration
            Assert.That(status.LastError, Is.EqualTo(None))
        }

    /// A changed catalog replaces the last catalog Watch read, so Watch can widen its Library exclusion.
    [<Test>]
    let ``changed catalog is returned for Watch to widen exclusion`` () =
        task {
            let! configuration, startup = workingCopy "disabled"
            let calls = Calls()
            let now = DateTime.UtcNow
            let changed = catalog configuration.RepositoryId [| "Library"; "Added" |]

            let tickPorts =
                ports configuration (fun () -> now + LibraryWatch.pullInterval) calls (fun () -> Task.FromResult changed) (fun () -> Task.CompletedTask)

            let! next = LibraryWatch.tick tickPorts CancellationToken.None false false (LibraryWatch.initialState now startup)
            Assert.That(next.Catalog, Is.EqualTo(changed))

            Assert.That(
                calls.Logs
                |> Seq.exists (fun line -> line.Contains("Library catalog changed")),
                Is.True
            )
        }

    /// Status text names the failure, the affected path and the next step, after the unchanged participation line.
    [<Test>]
    let ``status text shows the failure and next step`` () =
        let status: LibrarySynchronization.LibrarySynchronizationStatus =
            {
                Enabled = true
                Paused = false
                State = "blocked"
                LibraryCatalogVersion = None
                AppliedCursor = None
                PendingOperationCount = 1
                LastError = Some "Library creation slot is occupied; saved bytes are retained."
                LastErrorAt = Some(NodaTime.Instant.FromUtc(2026, 11, 25, 10, 0))
                LastErrorPath = Some "Library/same.txt"
                LastErrorOperationId = None
                NextStep = Some "Another working copy created a file with this name first."
                LastSuccessfulSyncAt = None
            }

        let text = LibraryCommand.synchronizationStatusText status
        Assert.That(text, Does.StartWith("Library synchronization: Enabled=True, Paused=False, State=blocked, Pending=1."))
        Assert.That(text, Does.Contain("Last error at 2026-11-25T10:00:00Z: Library creation slot is occupied"))
        Assert.That(text, Does.Contain("Affected path: Library/same.txt"))
        Assert.That(text, Does.Contain("Next step: Another working copy created a file with this name first."))
