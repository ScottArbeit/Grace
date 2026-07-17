namespace Grace.Cache

open System

/// Dispatches Grace Cache process verbs through the cache runtime boundary.
module Program =

    /// Reads only redacted machine configuration status for an inert process-control query.
    let private showStatus () =
        match CacheMachineConfiguration.tryRead (CacheMachineConfiguration.configurationPath ()) with
        | Ok configuration -> Ok(CacheMachineConfiguration.toStatus configuration)
        | Error message -> Error message

    /// Starts the registered cache host only after acquiring the machine-wide guard before configuration, listener, or store work.
    let private runHost () =
        match MachineInstanceGuard.tryAcquire () with
        | Error message -> Error message
        | Ok lease ->
            use _lease = lease

            match CacheHostSettings.fromEnvironment Environment.GetEnvironmentVariable, CacheRuntimeControl.startupRefresh () with
            | Error message, _
            | _, Error message -> Error message
            | Ok settings, Ok configuration ->
                match CacheHost.build settings configuration [||] with
                | Error message -> Error message
                | Ok app ->
                    app.Run()
                    Ok(CacheMachineConfiguration.toStatus configuration)

    /// Keeps process dispatch thin while preserving key custody and server calls inside CacheRuntimeControl.
    let private effects = { Enroll = CacheRuntimeControl.enroll; RotateNow = CacheLocalControl.requestRotation; Status = showStatus; Run = runHost }

    /// Executes exactly one supported cache process verb and writes its redacted machine-readable result.
    [<EntryPoint>]
    let main args =
        let result = CacheProcessCommand.execute effects args
        Console.Out.WriteLine(result.Payload)
        result.ExitCode
