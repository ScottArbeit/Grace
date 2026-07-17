namespace Grace.Cache

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Grace.Types.CacheRegistration
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

    /// Holds the server-defined four-hour interval for cache identity key rotation.
    let keyRotationInterval = RegistrationLifetime.KeyRotationInterval.ToTimeSpan()

    /// Lists the only routes available before cache enrollment, storage, or artifact serving exists.
    let routeInventory =
        [
            "/healthz"
            "/status"
            "/control/status"
        ]

    /// Builds the cache HTTP host after startup settings have already been validated.
    let build (settings: CacheHostSettings) (configuration: CacheMachineConfiguration) (args: string array) =
        let builder = WebApplication.CreateBuilder(args)
        let app = builder.Build()

        app.Urls.Add(configuration.Endpoint)
        app.Urls.Add(CacheMachineConfiguration.ControlEndpoint.AbsoluteUri)

        app.MapGet("/healthz", Func<string>(fun () -> "Grace Cache scaffold healthy."))
        |> ignore

        app.MapGet("/status", Func<IResult>(fun () -> Results.Json(CacheMachineConfiguration.toStatus configuration)))
        |> ignore

        app.MapGet(
            "/control/status",
            Func<HttpContext, IResult> (fun context ->
                if IPAddress.IsLoopback context.Connection.RemoteIpAddress then
                    Results.Json(CacheMachineConfiguration.toStatus configuration)
                else
                    Results.NotFound())
        )
        |> ignore

        let rotationTimer = new Timer(TimerCallback(fun _ -> CacheRuntimeControl.rotateNow () |> ignore), null, keyRotationInterval, keyRotationInterval)

        app.Lifetime.ApplicationStopping.Register(Action rotationTimer.Dispose)
        |> ignore

        app
