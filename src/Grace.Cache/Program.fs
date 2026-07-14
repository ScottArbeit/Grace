namespace Grace.Cache

open System
open System.Text.Json

/// Starts the narrowly scoped cache process without enrolling, serving, or persisting artifacts.
module Program =

    /// Emits only redacted machine configuration status for an inert process-control query.
    let private showStatus () =
        match CacheMachineConfiguration.tryRead (CacheMachineConfiguration.configurationPath ()) with
        | Ok configuration ->
            Console.Out.WriteLine(JsonSerializer.Serialize(CacheMachineConfiguration.toStatus configuration))
            0
        | Error message ->
            Console.Error.WriteLine(message)
            1

    /// Validates the private process marker before binding the fixed cache health and status routes.
    [<EntryPoint>]
    let main args =
        if args |> Array.contains "--status" then
            showStatus ()
        else
            match MachineInstanceGuard.tryAcquire () with
            | Error message ->
                Console.Error.WriteLine(message)
                1
            | Ok lease ->
                use _lease = lease

                match CacheHostSettings.fromEnvironment Environment.GetEnvironmentVariable,
                      CacheMachineConfiguration.tryRead (CacheMachineConfiguration.configurationPath ())
                    with
                | Error message, _
                | _, Error message ->
                    Console.Error.WriteLine(message)
                    1
                | Ok settings, Ok configuration ->
                    CacheHost.build settings configuration args
                    |> fun app -> app.Run()

                    0
