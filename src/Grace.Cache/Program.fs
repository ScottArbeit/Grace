namespace Grace.Cache

open System

/// Starts the narrowly scoped cache process without enrolling, serving, or persisting artifacts.
module Program =

    /// Validates the private process marker before binding the fixed cache health and status routes.
    [<EntryPoint>]
    let main args =
        match CacheHostSettings.fromEnvironment Environment.GetEnvironmentVariable with
        | Ok settings ->
            CacheHost.build settings args
            |> fun app -> app.Run()

            0
        | Error message ->
            Console.Error.WriteLine(message)
            1
