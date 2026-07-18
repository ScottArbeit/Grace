namespace Grace.Cache

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Grace.Types.CacheRegistration
open Grace.Shared
open System
open System.Net
open System.Threading
open NodaTime

/// Represents the minimal private process identity required before the cache host listens.
type CacheHostSettings = private { InstanceName: string }

/// Validates the cache process startup seam without introducing enrollment or machine-wide configuration behavior.
module CacheHostSettings =

    [<Literal>]
    let private expectedInstanceName = "grace-cache-service"

    /// Resolves the required private instance marker and rejects blank values before Kestrel is built.
    let fromEnvironment (readEnvironment: string -> string) =
        let instanceName = readEnvironment "GRACE_CACHE_INSTANCE_NAME"

        if
            String.IsNullOrWhiteSpace instanceName
            || not (String.Equals(instanceName.Trim(), expectedInstanceName, StringComparison.Ordinal))
        then
            Error "Missing required setting 'GRACE_CACHE_INSTANCE_NAME'."
        else
            Ok { InstanceName = instanceName.Trim() }

/// Converts listener startup exceptions into the stable redacted failure expected at the cache process boundary.
module CacheHostStartup =

    /// Runs the Kestrel start action without allowing bind or certificate details to escape the process result.
    let start startHost =
        try
            startHost ()
            Ok()
        with
        | _ -> Error "Grace Cache host could not start."

/// Calculates the in-memory recovery timing for a failed registration refresh from the server-issued expiry instant.
module CacheRefreshSchedule =

    /// Keeps the approved five-minute operational retry cadence outside the expiry reserve.
    let retryInterval = TimeSpan.FromMinutes 5.0

    /// Reserves the final fifteen minutes for operator recovery rather than another automatic refresh attempt.
    let expiryReserve = Duration.FromMinutes 15.0

    /// Distinguishes another permitted retry from waiting for expiry or terminal expiry handling.
    type RetryAction =
        | RetryAfter of TimeSpan
        | WaitForExpiry of TimeSpan
        | Expired

    /// Calculates the next recovery action without persisting a retry workflow or deriving expiry from callback cadence.
    let nextRetryAction (now: Instant) (expiresAt: Instant) =
        if now >= expiresAt then
            Expired
        else
            let untilExpiry = (expiresAt - now).ToTimeSpan()
            let untilReserve = (expiresAt - expiryReserve - now).ToTimeSpan()

            if untilReserve <= TimeSpan.Zero
               || untilReserve <= retryInterval then
                WaitForExpiry untilExpiry
            else
                RetryAfter retryInterval

    /// Computes the normal refresh delay from the actual successful registration rather than a process-start interval.
    let normalRefreshDelay (now: Instant) (registration: CacheRegistration) =
        let delay = (registration.RefreshAfter - now).ToTimeSpan()
        if delay <= TimeSpan.Zero then TimeSpan.Zero else delay

/// Calculates the initial key-rotation callback from the server-issued due instant so restart cannot reset the server cadence.
module CacheRotationSchedule =

    /// Schedules immediately when rotation is due and otherwise waits only until the authoritative server deadline.
    let initialDelayForDue (now: Instant) rotationDueAt =
        let delay = (rotationDueAt - now).ToTimeSpan()
        if delay <= TimeSpan.Zero then TimeSpan.Zero else delay

    /// Calculates the initial delay from the registration response returned by Grace Server.
    let initialDelay (now: Instant) (registration: CacheRegistration) = initialDelayForDue now registration.RotationDueAt

/// Owns the fixed, non-serving HTTP surface of the Grace Cache tracer process.
module CacheHost =

    /// Marks this scaffold as non-serving until #625 installs the authorized artifact-serving contract.
    let artifactServingAvailable = false

    /// Holds the server-defined one-hour interval that refreshes registration before its two-hour active lifetime expires.
    let registrationRefreshInterval = RegistrationLifetime.RefreshAfter.ToTimeSpan()

    /// Owns one in-memory refresh callback that follows server-issued expiry timing and stops automatic recovery at expiry.
    type private RegistrationRefreshScheduler(initialRegistration: CacheRegistration) =
        let mutable currentExpiry = initialRegistration.ExpiresAt
        let mutable timer: Timer option = None
        let mutable expiryOnly = false

        /// Reschedules the sole callback without creating a queue or a concurrent lifecycle operation.
        let scheduleRefresh delay =
            expiryOnly <- false

            match timer with
            | Some activeTimer ->
                activeTimer.Change(delay, Timeout.InfiniteTimeSpan)
                |> ignore
            | None -> ()

        /// Defers the final callback to expiry without allowing a refresh request inside the protected reserve.
        let scheduleExpiry delay =
            expiryOnly <- true

            match timer with
            | Some activeTimer ->
                activeTimer.Change(delay, Timeout.InfiniteTimeSpan)
                |> ignore
            | None -> ()

        /// Marks expiry through the serialized runtime boundary and emits only a fixed redacted operational failure.
        let markExpired () =
            CacheRuntimeControl.markRegistrationExpired ()
            |> ignore

            Console.Error.WriteLine("Grace Cache registration expired; operator recovery is required.")

        /// Handles one normal or recovery callback, rebuilding refresh proof in the runtime for every request-level attempt.
        let onTimer _ =
            if expiryOnly then
                markExpired ()
            else
                match CacheRuntimeControl.refreshRegistrationNow () with
                | Ok registration ->
                    currentExpiry <- registration.ExpiresAt
                    scheduleRefresh (CacheRefreshSchedule.normalRefreshDelay (SystemClock.Instance.GetCurrentInstant()) registration)
                | Error _ ->
                    match CacheRefreshSchedule.nextRetryAction (SystemClock.Instance.GetCurrentInstant()) currentExpiry with
                    | CacheRefreshSchedule.RetryAfter delay -> scheduleRefresh delay
                    | CacheRefreshSchedule.WaitForExpiry delay -> scheduleExpiry delay
                    | CacheRefreshSchedule.Expired -> markExpired ()

        do
            let refreshTimer = new Timer(TimerCallback onTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)
            timer <- Some refreshTimer
            scheduleRefresh (CacheRefreshSchedule.normalRefreshDelay (SystemClock.Instance.GetCurrentInstant()) initialRegistration)

        interface IDisposable with
            /// Disposes the one process-local scheduler when the host stops so no refresh callback can survive shutdown.
            member _.Dispose() =
                timer
                |> Option.iter (fun activeTimer -> activeTimer.Dispose())

    /// Starts the server-expiry-based refresh scheduler after startup refresh has returned authoritative registration timing.
    let startRegistrationRefresh initialRegistration = new RegistrationRefreshScheduler(initialRegistration) :> IDisposable

    /// Holds the in-memory rotation timer that retries failures after five minutes and otherwise follows server-issued due time.
    type private KeyRotationScheduler(initialRegistration: CacheRegistration) =
        let mutable timer: Timer option = None

        /// Schedules the next single rotation callback without allowing timer cadence to override the server due instant.
        let schedule delay =
            timer
            |> Option.iter (fun activeTimer ->
                activeTimer.Change(delay, Timeout.InfiniteTimeSpan)
                |> ignore)

        /// Rotates through the serialized runtime, retaining a failed candidate and publishing unhealthy state before retrying it.
        let onTimer _ =
            match CacheRuntimeControl.synchronizeIdentity false with
            | Ok _ ->
                match CacheRuntimeControl.refreshRegistrationNow () with
                | Ok registration -> schedule (CacheRotationSchedule.initialDelay (SystemClock.Instance.GetCurrentInstant()) registration)
                | Error _ -> schedule CacheRefreshSchedule.retryInterval
            | Error _ ->
                CacheRuntimeControl.refreshNow () |> ignore

                schedule CacheRefreshSchedule.retryInterval

        do
            let rotationTimer = new Timer(TimerCallback onTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)
            timer <- Some rotationTimer
            schedule (CacheRotationSchedule.initialDelay (SystemClock.Instance.GetCurrentInstant()) initialRegistration)

        interface IDisposable with
            /// Releases the process-local rotation timer when the cache host stops.
            member _.Dispose() =
                timer
                |> Option.iter (fun activeTimer -> activeTimer.Dispose())

    /// Starts key rotation from the authoritative server due time returned by the startup refresh.
    let startKeyRotation initialRegistration = new KeyRotationScheduler(initialRegistration) :> IDisposable

    /// Lists the only routes available before cache enrollment, storage, or artifact serving exists.
    let routeInventory = [ "/healthz"; "/status" ]

    /// Builds the cache HTTP host after the runtime has completed its server-synchronized startup transition.
    let build (settings: CacheHostSettings) (configuration: CacheMachineConfiguration) (args: string array) =
        let builder = WebApplication.CreateBuilder(args)
        let app = builder.Build()

        app.Urls.Add(configuration.Endpoint)

        app.MapGet("/healthz", Func<string>(fun () -> "Grace Cache scaffold healthy."))
        |> ignore

        app.MapGet("/status", Func<IResult>(fun () -> Results.Json(CacheMachineConfiguration.toStatus configuration, Constants.JsonSerializerOptions)))
        |> ignore

        Ok app
