namespace Grace.Cache.Tests

open System
open Grace.Cache
open NUnit.Framework

/// Hosts the focused F# cache service test assembly.
module Program =

    /// Hosts a child process that retains a pending ingest until the parent terminates it to prove crash recovery.
    let private holdStoreUntilTerminated databasePath =
        let descriptor: CacheArtifactDescriptor =
            {
                ArtifactId = "child-pending-artifact"
                Digest = String.replicate 64 "a"
                UncompressedSize = 4096L
                RootDirectoryVersionId = "child-pending-root"
                FillToken = "child-pending-fill"
            }

        match CacheStore.openStore databasePath with
        | Opened (store, _) ->
            match CacheStore.beginIngest store descriptor with
            | CacheIngestBeginResult.Pending ->
                Console.Out.WriteLine("READY")
                Console.Out.Flush()
                Console.ReadLine() |> ignore
                CacheStore.disposeStore store
                0
            | _ -> 3
        | CacheDatabaseInUse ->
            Console.Out.WriteLine("DATABASE-IN-USE")
            Console.Out.Flush()
            2

    /// Hosts a child process that attempts cache ownership without opening SQLite when another process owns the path.
    let private attemptStoreOpen databasePath =
        match CacheStore.openStore databasePath with
        | Opened (store, _) ->
            CacheStore.disposeStore store
            Console.Out.WriteLine("OPENED")
            Console.Out.Flush()
            0
        | CacheDatabaseInUse ->
            Console.Out.WriteLine("DATABASE-IN-USE")
            Console.Out.Flush()
            0

    /// Runs the NUnit test host without adding a cache CLI surface.
    [<EntryPoint>]
    let main args =
        match args with
        | [| "--hold-store"; databasePath |] -> holdStoreUntilTerminated databasePath
        | [| "--attempt-store-open"; databasePath |] -> attemptStoreOpen databasePath
        | _ -> 0
