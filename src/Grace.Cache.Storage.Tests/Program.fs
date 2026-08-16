namespace Grace.Cache.Storage.Tests

open System
open Grace.Cache.Storage

/// Hosts the child-process store ownership helper used by focused lock tests.
module Program =

    /// Opens and holds the storage lease directly until the parent releases the child.
    let private holdStore databasePath =
        match CacheStore.openStore databasePath with
        | Opened store ->
            Console.Out.WriteLine("READY")
            Console.Out.Flush()
            Console.ReadLine() |> ignore
            CacheStore.disposeStore store
            0
        | CacheDatabaseInUse ->
            Console.Out.WriteLine("DATABASE-IN-USE")
            Console.Out.Flush()
            2

    /// Attempts store ownership without adding a runtime command surface.
    let private attemptStoreOpen databasePath =
        match CacheStore.openStore databasePath with
        | Opened store ->
            CacheStore.disposeStore store
            Console.Out.WriteLine("OPENED")
            Console.Out.Flush()
            0
        | CacheDatabaseInUse ->
            Console.Out.WriteLine("DATABASE-IN-USE")
            Console.Out.Flush()
            0

    /// Runs focused test helper modes without exposing a production process entry point.
    [<EntryPoint>]
    let main args =
        match args with
        | [| "--hold-store"; databasePath |] -> holdStore databasePath
        | [| "--attempt-store-open"; databasePath |] -> attemptStoreOpen databasePath
        | _ -> 0
