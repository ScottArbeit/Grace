namespace Grace.Cache

open System
open Microsoft.Extensions.Hosting

/// Dispatches Grace Cache process verbs through the cache runtime boundary.
module Program =

    /// Reads only redacted machine configuration status for an inert process-control query.
    let private showStatus () = CacheRuntimeControl.status ()

    /// Rejects an invalid private process marker before parsing a verb or allowing any configuration, recovery, key, listener, or server effect.
    let executeWithMarker marker effects args =
        match CacheHostSettings.fromEnvironment (fun _ -> marker) with
        | Error _ -> CacheProcessCommand.processFailure ()
        | Ok _ -> CacheProcessCommand.execute effects args

    /// Starts the registered cache host only after acquiring the machine-wide guard before configuration, listener, or store work.
    let private runHost settings =
        match MachineInstanceGuard.tryAcquire () with
        | Error message -> Error message
        | Ok lease ->
            use _lease = lease

            match CacheRuntimeControl.getReadyConfiguration () with
            | Error message -> Error message
            | Ok configuration ->
                match CacheHost.build settings configuration [||] with
                | Error message -> Error message
                | Ok app ->
                    use app = app

                    match CacheHostStartup.start (fun () -> app.StartAsync().GetAwaiter().GetResult()) with
                    | Error message -> Error message
                    | Ok () ->
                        match CacheRuntimeControl.startupRefresh CacheHost.artifactServingAvailable with
                        | Error message ->
                            app.StopAsync().GetAwaiter().GetResult()
                            Error message
                        | Ok (refreshedConfiguration, registration) ->
                            use _refreshSchedule = CacheHost.startRegistrationRefresh registration
                            use _rotationSchedule = CacheHost.startKeyRotation registration

                            app
                                .WaitForShutdownAsync()
                                .GetAwaiter()
                                .GetResult()

                            Ok(CacheMachineConfiguration.toStatus refreshedConfiguration)

    /// Executes exactly one supported cache process verb and writes its redacted machine-readable result.
    [<EntryPoint>]
    let main args =
        let result =
            match CacheHostSettings.fromEnvironment Environment.GetEnvironmentVariable with
            | Error _ -> CacheProcessCommand.processFailure ()
            | Ok settings ->
                let effects =
                    {
                        Enroll = CacheRuntimeControl.enroll
                        RotateNow = fun () -> CacheLocalControl.requestRotation ()
                        Status = fun () -> showStatus ()
                        Run = fun () -> runHost settings
                    }

                CacheProcessCommand.execute effects args

        Console.Out.WriteLine(result.Payload)
        result.ExitCode
