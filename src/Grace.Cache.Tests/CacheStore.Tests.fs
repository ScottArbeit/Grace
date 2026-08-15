namespace Grace.Cache.Tests

open System
open System.Diagnostics
open System.IO
open System.Reflection
open Grace.Cache
open Microsoft.Data.Sqlite
open NUnit.Framework

/// Shares isolated database and child-process setup across retained cache-store tests.
module CacheStoreTestSupport =

    /// Creates an isolated cache database location for one test.
    let createDatabasePath () =
        let directory = Path.Combine(Path.GetTempPath(), "grace-cache-tests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(directory) |> ignore
        Path.Combine(directory, "cache.db")

    /// Deletes the isolated database directory after every test has released its store lease.
    let deleteDatabasePath (databasePath: string) =
        let directory = Path.GetDirectoryName(databasePath)
        SqliteConnection.ClearAllPools()

        if Directory.Exists(directory) then Directory.Delete(directory, true)

    /// Opens a cache store and fails the test when another process still owns its database.
    let openStore databasePath =
        match CacheStore.openStore databasePath with
        | Opened (store, _) -> store
        | CacheDatabaseInUse ->
            Assert.Fail("The test database unexpectedly remained owned by another process.")
            Unchecked.defaultof<CacheStore>

    /// Starts this test executable in a child process to exercise the cross-process ownership lock.
    let startChild mode databasePath =
        let testAssembly = Assembly.GetExecutingAssembly().Location
        let startInfo = ProcessStartInfo("dotnet", $"\"{testAssembly}\" {mode} \"{databasePath}\"")
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        Process.Start(startInfo)

/// Verifies retained SQLite settings and process-lock behavior after the schema reset.
[<TestFixture>]
type CacheStoreTests() =

    /// Confirms every opened store records the selected #623 SQLite settings.
    [<Test>]
    member _.``opened cache store reports the selected SQLite settings``() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let store = CacheStoreTestSupport.openStore databasePath

        try
            let diagnostics = CacheStore.getDiagnostics store
            Assert.That(diagnostics.SchemaVersion, Is.EqualTo 2)
            Assert.That(diagnostics.ForeignKeysEnabled, Is.True)
            Assert.That(diagnostics.JournalMode, Is.EqualTo "wal")
            Assert.That(diagnostics.BusyTimeoutMilliseconds, Is.GreaterThanOrEqualTo 5000)
        finally
            CacheStore.disposeStore store
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Confirms process ownership remains held until all same-process leases release it.
    [<Test>]
    member _.``same-process callers share the cache-store lease until the final release``() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let first = CacheStoreTestSupport.openStore databasePath
        let second = CacheStoreTestSupport.openStore databasePath

        try
            CacheStore.disposeStore first

            use child = CacheStoreTestSupport.startChild "--attempt-store-open" databasePath
            let output = child.StandardOutput.ReadToEnd()
            child.WaitForExit()
            Assert.That(child.ExitCode, Is.EqualTo 0)
            Assert.That(output, Does.Contain("DATABASE-IN-USE"))

            CacheStore.disposeStore second

            use releasedChild = CacheStoreTestSupport.startChild "--attempt-store-open" databasePath
            let releasedOutput = releasedChild.StandardOutput.ReadToEnd()
            releasedChild.WaitForExit()
            Assert.That(releasedChild.ExitCode, Is.EqualTo 0)
            Assert.That(releasedOutput, Does.Contain("OPENED"))
        finally
            CacheStore.disposeStore first
            CacheStore.disposeStore second
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Confirms the sidecar process lock rejects another process before it reaches SQLite state.
    [<Test>]
    member _.``external process cannot open a cache database while this process owns it``() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let store = CacheStoreTestSupport.openStore databasePath

        try
            use child = CacheStoreTestSupport.startChild "--attempt-store-open" databasePath
            let output = child.StandardOutput.ReadToEnd()
            child.WaitForExit()
            Assert.That(child.ExitCode, Is.EqualTo 0)
            Assert.That(output, Does.Contain("DATABASE-IN-USE"))
        finally
            CacheStore.disposeStore store
            CacheStoreTestSupport.deleteDatabasePath databasePath

    /// Confirms released leases cannot start new operations and can be released more than once.
    [<Test>]
    member _.``disposed leases are idempotent and reject later diagnostics``() =
        let databasePath = CacheStoreTestSupport.createDatabasePath ()
        let store = CacheStoreTestSupport.openStore databasePath

        try
            CacheStore.disposeStore store
            CacheStore.disposeStore store

            Assert.Throws<InvalidOperationException>(Action(fun () -> CacheStore.getDiagnostics store |> ignore))
            |> ignore
        finally
            CacheStore.disposeStore store
            CacheStoreTestSupport.deleteDatabasePath databasePath
