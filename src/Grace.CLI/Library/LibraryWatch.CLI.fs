namespace Grace.CLI.Command

open Grace.CLI.LibraryLocalState
open Grace.Types.Library
open System
open System.Threading
open System.Threading.Tasks

/// Runs Grace Watch's Library work for one timer tick, so a Library failure never stops Watch.
/// Watch reads the catalog on every copy, because it needs the Library roots to keep Library files out of
/// version control. A failed step is recorded for `grace library sync status`, logged, and retried on a backoff.
/// Version-control work runs on the same tick whatever happens here. Only cancellation of Watch itself propagates.
module internal LibraryWatch =

    /// The ordinary interval between Library pulls, and between catalog refreshes, while nothing is failing.
    let pullInterval = TimeSpan.FromSeconds 5.0

    /// Spaces retries after consecutive failures: 5 seconds, doubling each time, capped at 5 minutes.
    let retryDelay consecutiveFailures =
        if consecutiveFailures <= 0 then
            TimeSpan.Zero
        else
            let seconds =
                5.0
                * Math.Pow(2.0, float (min (consecutiveFailures - 1) 10))

            TimeSpan.FromSeconds(min seconds 300.0)

    /// The effects one tick needs, supplied by Watch and replaced by tests.
    type Ports =
        {
            /// Reads the current time.
            Now: unit -> DateTime
            /// Reads this copy's local Library participation.
            ReadStatus: unit -> Task<LibrarySynchronization.LibrarySynchronizationStatus>
            /// Registers this Watch for Library wake hints, if it isn't registered yet.
            EnsureRegistered: unit -> Task
            /// Classifies the paths Watch observed in Library roots since the last tick.
            Classify: unit -> Task
            /// Runs one Library sync. A pause isn't a failure and returns normally.
            Run: unit -> Task
            /// Reads the repository's Library catalog from the server.
            ReadCatalog: unit -> Task<LibraryCatalogDto>
            /// Reads the latest recorded failure and success.
            ReadHealth: unit -> SyncHealth
            /// Records a failure for status.
            RecordFailure: SyncFailure -> unit
            /// Clears recorded failures that a success of the same kind disproves.
            ClearFailure: SyncFailureSource list -> unit
            /// Logs a message to Watch's output.
            Log: string -> unit
        }

    /// What the tick remembers between timer ticks.
    type State =
        {
            /// The last catalog Watch read successfully.
            Catalog: LibraryCatalogDto
            /// When Library sync may next run without a new observation or wake hint.
            NextSync: DateTime
            /// How many Library steps failed in a row; while nonzero, sync waits for NextSync.
            SyncFailures: int
            /// When Watch next reads the catalog.
            NextCatalogRefresh: DateTime
            /// How many catalog reads failed in a row.
            CatalogFailures: int
            /// Failure sources that might still be recorded and should be cleared by their next success.
            MayHaveFailures: Set<SyncFailureSource>
        }

    /// Starts tick state from the catalog Watch read at startup. Earlier runs may have left failures recorded.
    let initialState (now: DateTime) (catalog: LibraryCatalogDto) =
        {
            Catalog = catalog
            NextSync = DateTime.MinValue
            SyncFailures = 0
            NextCatalogRefresh = now + pullInterval
            CatalogFailures = 0
            MayHaveFailures =
                Set.ofList [ SyncFailureSource.Watch
                             SyncFailureSource.Catalog ]
        }

    /// Reports whether an exception means Watch itself is stopping, which is the only thing allowed to propagate.
    let private isStopping (cancellationToken: CancellationToken) (ex: exn) =
        ex :? OperationCanceledException
        && cancellationToken.IsCancellationRequested

    /// Records a failure that the failing step didn't record itself, then logs it. Never throws.
    let private reportFailure (ports: Ports) source (tickStartTicks: int64) (ex: exn) =
        try
            // A sync run records its own failure with the affected path; keep that richer record.
            let failure =
                match ports.ReadHealth().LastFailure with
                | Some recorded when recorded.AtUnixTicks >= tickStartTicks -> recorded
                | _ ->
                    let described = LibrarySynchronization.describeFailure source None None ex
                    ports.RecordFailure described
                    described

            let path =
                failure.Path
                |> Option.map (fun value -> $" Path: {value}.")
                |> Option.defaultValue ""

            ports.Log $"Library sync failed: {failure.Message}{path} Next step: {failure.NextStep}"
        with
        | recordingFailure -> ports.Log $"Library sync failed: {ex.Message} (Grace couldn't record this for status: {recordingFailure.Message})"

    /// Clears a recorded failure after a success of the same kind, if one might be recorded. Never throws.
    let private clearIfNeeded (ports: Ports) source (state: State) =
        if state.MayHaveFailures.Contains source then
            try
                ports.ClearFailure [ source ]
                { state with MayHaveFailures = state.MayHaveFailures.Remove source }
            with
            | ex ->
                ports.Log $"Grace couldn't clear a resolved Library failure from status: {ex.Message}"
                state
        else
            state

    /// Runs Library sync and the catalog refresh for one tick and returns the next state. Throws only when Watch is stopping.
    let tick (ports: Ports) (cancellationToken: CancellationToken) (observed: bool) (woke: bool) (state: State) =
        task {
            let now = ports.Now()

            let tickStartTicks =
                NodaTime
                    .Instant
                    .FromDateTimeUtc(now.ToUniversalTime())
                    .ToUnixTimeTicks()

            let mutable state = state

            // Library sync: local status, wake registration, observation classification, then the run itself.
            // After a failure, none of it runs until the backoff ends; observations stay queued until then.
            let inBackoff = state.SyncFailures > 0 && now < state.NextSync

            if not inBackoff then
                // Watch's own steps. Their failures are recorded as Watch failures and cleared when the steps next succeed.
                let mutable runDue = false

                try
                    let! status = ports.ReadStatus()

                    if status.Enabled && not status.Paused then
                        do! ports.EnsureRegistered()
                        do! ports.Classify()
                        state <- clearIfNeeded ports SyncFailureSource.Watch state

                        runDue <-
                            state.SyncFailures > 0
                            || woke
                            || observed
                            || status.State <> "current"
                            || now >= state.NextSync
                    else
                        // A paused or disabled copy has nothing to retry.
                        state <- clearIfNeeded ports SyncFailureSource.Watch { state with SyncFailures = 0 }
                with
                | ex when not (isStopping cancellationToken ex) ->
                    reportFailure ports SyncFailureSource.Watch tickStartTicks ex
                    let failures = state.SyncFailures + 1

                    state <-
                        { state with
                            SyncFailures = failures
                            NextSync = now + retryDelay failures
                            MayHaveFailures = state.MayHaveFailures.Add SyncFailureSource.Watch
                        }

                // The sync run. It records its own failures and successes; only a later successful run clears them.
                if runDue then
                    try
                        do! ports.Run()
                        state <- { state with SyncFailures = 0; NextSync = ports.Now() + pullInterval }
                    with
                    | ex when not (isStopping cancellationToken ex) ->
                        reportFailure ports SyncFailureSource.Sync tickStartTicks ex
                        let failures = state.SyncFailures + 1
                        state <- { state with SyncFailures = failures; NextSync = now + retryDelay failures }

            // Catalog refresh: keep the last catalog that was read. Watch widens its exclusion when State.Catalog changes.
            if now >= state.NextCatalogRefresh then
                try
                    let! catalog = ports.ReadCatalog()

                    if catalog <> state.Catalog then
                        ports.Log "The repository's Library catalog changed. Grace Watch now keeps the old and new Library roots out of version control."

                    state <-
                        clearIfNeeded
                            ports
                            SyncFailureSource.Catalog
                            { state with Catalog = catalog; CatalogFailures = 0; NextCatalogRefresh = now + pullInterval }
                with
                | ex when not (isStopping cancellationToken ex) ->
                    let failure =
                        { LibrarySynchronization.describeFailure SyncFailureSource.Catalog None None ex with
                            NextStep = "Grace Watch keeps using the last Library catalog it read and retries automatically."
                        }

                    try
                        ports.RecordFailure failure
                        ports.Log $"Library catalog read failed: {failure.Message} Next step: {failure.NextStep}"
                    with
                    | recordingFailure ->
                        ports.Log $"Library catalog read failed: {ex.Message} (Grace couldn't record this for status: {recordingFailure.Message})"

                    let failures = state.CatalogFailures + 1

                    state <-
                        { state with
                            CatalogFailures = failures
                            NextCatalogRefresh = now + retryDelay failures
                            MayHaveFailures = state.MayHaveFailures.Add SyncFailureSource.Catalog
                        }

            return state
        }
