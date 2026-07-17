namespace Grace.Cache

open System
open Microsoft.Extensions.Hosting

/// Dispatches Grace Cache process verbs through the cache runtime boundary.
module Program =

    /// Reads only redacted machine configuration status for an inert process-control query.
    let private showStatus () =
        let configurationPath = CacheMachineConfiguration.configurationPath ()

        match CacheEnrollmentRecovery.tryRead (CacheEnrollmentRecovery.recoveryPath configurationPath) with
        | Ok (Some recovery) when CacheEnrollmentRecovery.isUnknown recovery -> Ok CacheRuntimeStatus.enrollmentRecoveryRequired
        | Error message -> Error message
        | Ok _ ->
            CacheRuntimeControl.getReadyConfiguration ()
            |> Result.map CacheMachineConfiguration.toStatus

    /// Starts the registered cache host only after acquiring the machine-wide guard before configuration, listener, or store work.
    let private runHost () =
        match MachineInstanceGuard.tryAcquire () with
        | Error message -> Error message
        | Ok lease ->
            use _lease = lease

            match CacheHostSettings.fromEnvironment Environment.GetEnvironmentVariable, CacheRuntimeControl.getReadyConfiguration () with
            | Error message, _
            | _, Error message -> Error message
            | Ok settings, Ok configuration ->
                match CacheHost.build settings configuration [||] with
                | Error message -> Error message
                | Ok app ->
                    use app = app
                    app.StartAsync().GetAwaiter().GetResult()

                    match CacheRuntimeControl.startupRefresh CacheHost.artifactServingAvailable with
                    | Error message ->
                        app.StopAsync().GetAwaiter().GetResult()
                        Error message
                    | Ok refreshedConfiguration ->
                        app
                            .WaitForShutdownAsync()
                            .GetAwaiter()
                            .GetResult()

                        Ok(CacheMachineConfiguration.toStatus refreshedConfiguration)

    /// Keeps process dispatch thin while preserving key custody and server calls inside CacheRuntimeControl.
    let private effects = { Enroll = CacheRuntimeControl.enroll; RotateNow = CacheLocalControl.requestRotation; Status = showStatus; Run = runHost }

    /// Executes exactly one supported cache process verb and writes its redacted machine-readable result.
    [<EntryPoint>]
    let main args =
        let result = CacheProcessCommand.execute effects args
        Console.Out.WriteLine(result.Payload)
        result.ExitCode
