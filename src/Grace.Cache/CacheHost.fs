namespace Grace.Cache

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Grace.Types.CacheRegistration
open Grace.Shared
open System
open System.Net
open System.Threading

/// Represents the minimal private process identity required before the cache host listens.
type CacheHostSettings = private { InstanceName: string }

/// Validates the cache process startup seam without introducing enrollment or machine-wide configuration behavior.
module CacheHostSettings =

    /// Resolves the required private instance marker and rejects blank values before Kestrel is built.
    let fromEnvironment (readEnvironment: string -> string) =
        let instanceName = readEnvironment "GRACE_CACHE_INSTANCE_NAME"

        if String.IsNullOrWhiteSpace instanceName then
            Error "Missing required setting 'GRACE_CACHE_INSTANCE_NAME'."
        else
            Ok { InstanceName = instanceName.Trim() }

/// Owns the fixed, non-serving HTTP surface of the Grace Cache tracer process.
module CacheHost =

    /// Marks this scaffold as non-serving until #625 installs the authorized artifact-serving contract.
    let artifactServingAvailable = false

    /// Holds the server-defined four-hour interval for cache identity key rotation.
    let keyRotationInterval = RegistrationLifetime.KeyRotationInterval.ToTimeSpan()

    /// Holds the server-defined one-hour interval that refreshes registration before its two-hour active lifetime expires.
    let registrationRefreshInterval = RegistrationLifetime.RefreshAfter.ToTimeSpan()

    /// Lists the only routes available before cache enrollment, storage, or artifact serving exists.
    let routeInventory =
        [
            "/healthz"
            "/status"
            "/control/status"
        ]

    /// Builds the cache HTTP host only after protected local control has been established for the active process.
    let build (settings: CacheHostSettings) (configuration: CacheMachineConfiguration) (args: string array) =
        match CacheLocalControl.start () with
        | Error error -> Error error
        | Ok localControl ->
            let builder = WebApplication.CreateBuilder(args)
            let app = builder.Build()

            app.Urls.Add(configuration.Endpoint)

            app.MapGet("/healthz", Func<string>(fun () -> "Grace Cache scaffold healthy."))
            |> ignore

            app.MapGet("/status", Func<IResult>(fun () -> Results.Json(CacheMachineConfiguration.toStatus configuration, Constants.JsonSerializerOptions)))
            |> ignore

            app.MapGet(
                "/control/status",
                Func<HttpContext, IResult> (fun context ->
                    if IPAddress.IsLoopback context.Connection.RemoteIpAddress then
                        Results.Json(CacheMachineConfiguration.toStatus configuration, Constants.JsonSerializerOptions)
                    else
                        Results.NotFound())
            )
            |> ignore

            let refreshTimer =
                new Timer(TimerCallback(fun _ -> CacheRuntimeControl.refreshNow () |> ignore), null, registrationRefreshInterval, registrationRefreshInterval)

            let rotationTimer = new Timer(TimerCallback(fun _ -> CacheRuntimeControl.rotateNow () |> ignore), null, keyRotationInterval, keyRotationInterval)

            app.Lifetime.ApplicationStopping.Register(
                Action (fun () ->
                    refreshTimer.Dispose()
                    rotationTimer.Dispose()
                    localControl.Dispose())
            )
            |> ignore

            Ok app
