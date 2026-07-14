namespace Grace.Cache

open System
open System.IO
open System.Text.Json
open System.Threading

/// Holds the machine-scoped operational facts that a registered Grace Cache may retain locally.
type CacheMachineConfiguration = { CacheId: Guid; Endpoint: string; AllowHttpEndpoint: bool; ServerUri: string }

/// Represents the safe operational facts that may be returned by the local cache control surface.
type CacheRuntimeStatus = { Lifecycle: string; CacheId: string option; Transport: string option }

/// Provides machine-scoped Grace Cache configuration without consulting repository configuration or CLI local state.
module CacheMachineConfiguration =

    /// The loopback-only control address shared by the cache host and the CLI launcher.
    let ControlEndpoint = Uri("http://127.0.0.1:48731")

    /// Resolves the service-account configuration path outside every repository working copy.
    let configurationPath () = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Grace", "Cache", "cache.runtime.json")

    /// Validates the exact endpoint and explicit HTTP exception before a cache process can use it.
    let validateEndpoint (endpoint: string) allowHttpEndpoint =
        match Uri.TryCreate(endpoint, UriKind.Absolute) with
        | false, _ -> Error "Cache endpoint must be an absolute HTTP or HTTPS URI."
        | true, uri when
            uri.Scheme = Uri.UriSchemeHttps
            && not allowHttpEndpoint
            ->
            Ok uri
        | true, uri when
            uri.Scheme = Uri.UriSchemeHttps
            && allowHttpEndpoint
            ->
            Error "--allow-http is valid only for an HTTP endpoint."
        | true, uri when
            uri.Scheme = Uri.UriSchemeHttp
            && allowHttpEndpoint
            ->
            Ok uri
        | true, uri when uri.Scheme = Uri.UriSchemeHttp -> Error "HTTP cache endpoints require the explicit --allow-http exception."
        | _ -> Error "Cache endpoint must use HTTP or HTTPS."

    /// Validates a configuration before it is persisted or used to bind a listener.
    let validate (configuration: CacheMachineConfiguration) =
        if
            isNull (box configuration)
            || configuration.CacheId = Guid.Empty
        then
            Error "A registered CacheId is required."
        elif String.IsNullOrWhiteSpace configuration.ServerUri then
            Error "Grace Server URI is required."
        else
            match validateEndpoint configuration.Endpoint configuration.AllowHttpEndpoint, Uri.TryCreate(configuration.ServerUri, UriKind.Absolute) with
            | Error error, _ -> Error error
            | Ok _, (true, serverUri) when
                serverUri.Scheme = Uri.UriSchemeHttps
                || serverUri.Scheme = Uri.UriSchemeHttp
                ->
                Ok()
            | Ok _, _ -> Error "Grace Server URI must be an absolute HTTP or HTTPS URI."

    /// Reads the registered machine configuration without creating files or recovering missing enrollment state.
    let tryRead (path: string) =
        try
            if not (File.Exists path) then
                Error "Grace Cache is not enrolled on this machine."
            else
                let configuration = JsonSerializer.Deserialize<CacheMachineConfiguration>(File.ReadAllText path)

                match validate configuration with
                | Ok () -> Ok configuration
                | Error error -> Error $"Grace Cache configuration is invalid: {error}"
        with
        | :? JsonException -> Error "Grace Cache configuration is invalid."
        | :? IOException -> Error "Grace Cache configuration could not be read."
        | :? UnauthorizedAccessException -> Error "Grace Cache configuration could not be read."

    /// Atomically persists only the server-accepted cache identity and safe machine operational settings.
    let write (path: string) (configuration: CacheMachineConfiguration) =
        match validate configuration with
        | Error error -> Error error
        | Ok () ->
            try
                let directory = Path.GetDirectoryName path

                if String.IsNullOrWhiteSpace directory then
                    Error "Grace Cache configuration path is invalid."
                else
                    Directory.CreateDirectory directory |> ignore
                    let temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp"

                    try
                        File.WriteAllText(temporaryPath, JsonSerializer.Serialize configuration)
                        File.Move(temporaryPath, path, true)
                        Ok()
                    finally
                        if File.Exists temporaryPath then File.Delete temporaryPath
            with
            | :? IOException -> Error "Grace Cache configuration could not be written."
            | :? UnauthorizedAccessException -> Error "Grace Cache configuration could not be written."

    /// Converts registered configuration to redacted status without exposing server URLs, tokens, keys, or repository assignments.
    let toStatus (configuration: CacheMachineConfiguration) =
        let transport =
            match Uri.TryCreate(configuration.Endpoint, UriKind.Absolute) with
            | true, uri when uri.Scheme = Uri.UriSchemeHttps -> Some "https"
            | true, uri when uri.Scheme = Uri.UriSchemeHttp -> Some "http-approved"
            | _ -> None

        { Lifecycle = "registered"; CacheId = Some(configuration.CacheId.ToString("D")); Transport = transport }

/// Holds the operating-system mutex lease that makes one active cache process machine-wide.
type MachineInstanceLease internal (mutex: Mutex) =
    interface IDisposable with
        /// Releases the operating-system guard on normal cache-process shutdown.
        member _.Dispose() =
            mutex.ReleaseMutex()
            mutex.Dispose()

/// Acquires the fixed operating-system guard before any cache store, listener, recovery, or server mutation can begin.
module MachineInstanceGuard =

    /// Uses a Windows global mutex so independent sessions cannot start separate cache processes.
    let private guardName =
        if OperatingSystem.IsWindows() then
            "Global\\Grace.Cache.MachineSingleton.v1"
        else
            "Grace.Cache.MachineSingleton.v1"

    /// Acquires a named guard atomically and returns a loser result without attempting later cache side effects.
    let tryAcquireWithName name =
        let mutable createdNew = false
        let mutex = new Mutex(true, name, &createdNew)

        if createdNew then
            Ok(new MachineInstanceLease(mutex))
        else
            mutex.Dispose()
            Error "A Grace Cache process is already active on this machine."

    /// Acquires the one fixed cache guard whose identity cannot vary with database, endpoint, repository, or runtime settings.
    let tryAcquire () = tryAcquireWithName guardName
