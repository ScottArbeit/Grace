namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Microsoft.Data.Sqlite
open NUnit.Framework
open SQLitePCL
open System
open System.IO

/// Verifies the finite Library synchronization state stored in the existing local database.
[<NonParallelizable>]
module LibraryLocalStateTests =

    /// Runs one test against a disposable local-state database.
    let private withDatabase action =
        let root = Path.Combine(Path.GetTempPath(), $"grace-library-state-{Guid.NewGuid():N}")
        Directory.CreateDirectory(root) |> ignore
        let dbPath = Path.Combine(root, "grace-local.db")

        try
            action dbPath
        finally
            SqliteConnection.ClearAllPools()
            if Directory.Exists(root) then Directory.Delete(root, true)

    /// Opens the disposable SQLite database for independent shape assertions.
    let private openConnection dbPath =
        Batteries_V2.Init()
        let connection = new SqliteConnection($"Data Source={dbPath}")
        connection.Open()
        connection

    /// Reads one scalar string from SQLite.
    let private scalarString (connection: SqliteConnection) sql =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteScalar() :?> string

    /// Reads one scalar integer from SQLite.
    let private scalarInt (connection: SqliteConnection) sql =
        use command = connection.CreateCommand()
        command.CommandText <- sql
        command.ExecuteScalar() |> Convert.ToInt32

    /// Verifies schema 12 adds exactly the six accepted Library tables and leaves the WDU table intact.
    [<Test>]
    let ``schema 12 creates the finite Library state beside unchanged WDU state`` () =
        withDatabase (fun dbPath ->
            LocalStateDb.ensureDbInitialized dbPath
            |> fun operation -> operation.GetAwaiter().GetResult()

            use connection = openConnection dbPath

            scalarString connection "SELECT value FROM meta WHERE key='schema_version';"
            |> should equal "12"

            let tableNames =
                [|
                    "library_repository_state"
                    "libraries"
                    "library_items"
                    "library_namespace_slots"
                    "library_operations"
                    "library_conflicts"
                |]

            for tableName in tableNames do
                scalarInt connection $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}';"
                |> should equal 1

            scalarInt connection "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='working_directory_update_completions';"
            |> should equal 1

            let wduSql = scalarString connection "SELECT sql FROM sqlite_master WHERE type='table' AND name='working_directory_update_completions';"

            wduSql.Contains("caller_kind IN ('Watch', 'Branch', 'Connect')", StringComparison.Ordinal)
            |> should equal true)

    /// Verifies cursor progress is rejected until the exact operation is terminal under the same catalog version.
    [<Test>]
    let ``cursor CAS requires terminal operation exact predecessor and unchanged catalog`` () =
        withDatabase (fun dbPath ->
            let repositoryId = Guid.NewGuid()
            let workingCopyId = Guid.NewGuid()
            let catalogVersion = Guid.NewGuid()
            let operationId = Guid.NewGuid()

            LibraryLocalState.enable dbPath repositoryId workingCopyId catalogVersion [| "shared" |] "epoch-1" "cursor-0"
            |> fun operation -> operation.GetAwaiter().GetResult()

            LibraryLocalState.recordPending
                dbPath
                repositoryId
                operationId
                LibraryLocalState.OperationDirection.Remote
                "createFile"
                "request-hash"
                catalogVersion
                (Some(Guid.NewGuid()))
                None
                (Some "shared/file.txt")
                None
                None
                (Some "cursor-0")
                (Some "cursor-1")
                (Some "epoch-1")
                (Some(String.replicate 64 "a"))
                (Some(String.replicate 64 "b"))
                (Some 5L)
            |> fun operation -> operation.GetAwaiter().GetResult()

            LibraryLocalState.tryAdvanceCursor dbPath repositoryId operationId catalogVersion "epoch-1" "cursor-0" "cursor-1"
            |> fun operation -> operation.GetAwaiter().GetResult()
            |> should equal false

            LibraryLocalState.markTerminalWithoutProjection dbPath repositoryId operationId catalogVersion
            |> fun operation -> operation.GetAwaiter().GetResult()

            LibraryLocalState.tryAdvanceCursor dbPath repositoryId operationId catalogVersion "epoch-1" "cursor-0" "cursor-1"
            |> fun operation -> operation.GetAwaiter().GetResult()
            |> should equal true

            let state =
                LibraryLocalState.readRepositoryState dbPath repositoryId
                |> fun operation -> operation.GetAwaiter().GetResult()
                |> Option.get

            state.AppliedCursor
            |> should equal (Some "cursor-1"))
