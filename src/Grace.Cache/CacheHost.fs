namespace Grace.Cache

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open System

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

    /// Lists the only routes available before cache enrollment, storage, or artifact serving exists.
    let routeInventory = [ "/healthz"; "/status" ]

    /// Builds the cache HTTP host after startup settings have already been validated.
    let build (settings: CacheHostSettings) (args: string array) =
        let builder = WebApplication.CreateBuilder(args)
        let app = builder.Build()

        app.MapGet("/healthz", Func<string>(fun () -> "Grace Cache scaffold healthy."))
        |> ignore

        app.MapGet("/status", Func<IResult>(fun () -> Results.Json({| service = "Grace.Cache"; status = "ready" |})))
        |> ignore

        app
