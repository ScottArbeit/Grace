namespace Grace.CLI.Tests

open FsUnit
open Grace.CLI
open Grace.CLI.Command
open Grace.Shared
open Grace.Types.Library
open Microsoft.Data.Sqlite
open NUnit.Framework
open NodaTime
open SQLitePCL
open System
open System.IO
open System.Security.Cryptography

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

    /// Verifies restart classifies exact published bytes and completes SQLite without another filesystem write.
    [<Test>]
    let ``restart after filesystem publication completes without rewriting target`` () =
        withDatabase (fun dbPath ->
            let repositoryId = Guid.NewGuid()
            let workingCopyId = Guid.NewGuid()
            let catalogVersion = Guid.NewGuid()
            let operationId = Guid.NewGuid()
            let itemId = Guid.NewGuid()
            let targetPath = Path.Combine(Path.GetDirectoryName(dbPath), "shared", "file.txt")
            let bytes = Text.Encoding.UTF8.GetBytes("restart bytes")
            let blake3 = ContentAddress.computeBlake3Hex bytes

            let sha256 =
                SHA256.HashData(bytes)
                |> Convert.ToHexString
                |> fun value -> value.ToLowerInvariant()

            LibraryLocalState.enable dbPath repositoryId workingCopyId catalogVersion [| "shared" |] "epoch-1" "cursor-0"
            |> fun operation -> operation.GetAwaiter().GetResult()

            LibraryLocalState.recordPending
                dbPath
                repositoryId
                operationId
                LibraryLocalState.OperationDirection.Remote
                "createFile"
                "request"
                catalogVersion
                (Some itemId)
                None
                (Some "shared/file.txt")
                None
                None
                (Some "cursor-0")
                (Some "cursor-1")
                (Some "epoch-1")
                (Some blake3)
                (Some sha256)
                (Some(int64 bytes.Length))
            |> fun operation -> operation.GetAwaiter().GetResult()

            LibraryFilesystem.publishAtomic targetPath blake3 sha256 (int64 bytes.Length) bytes
            let publishedAt = File.GetLastWriteTimeUtc(targetPath)

            let change =
                {
                    Cursor = "cursor-1"
                    OperationId = operationId
                    ChangeKind = ChangeKind.CreateFile
                    ItemId = itemId
                    ItemKind = ItemKind.File
                    AcceptedAt = Instant.FromUnixTimeTicks(1L)
                    AcceptedBy = "principal"
                    LibraryCatalogVersion = catalogVersion
                    Namespace =
                        Some
                            {
                                Parent = { Kind = "root"; LibraryPath = Some "shared"; ItemId = None }
                                Name = "file.txt"
                                NormalizedPath = "shared/file.txt"
                                NamespaceVersion = Guid.NewGuid()
                                SlotVersion = Guid.NewGuid()
                            }
                    Content =
                        Some
                            {
                                ContentVersionId = Guid.NewGuid()
                                Blake3Hash = blake3
                                Sha256Hash = sha256
                                Size = int64 bytes.Length
                                CreatedAt = Instant.FromUnixTimeTicks(1L)
                            }
                    Tombstone = None
                    Conflict = None
                }

            LibraryFilesystem.matchesContent targetPath blake3 sha256 (int64 bytes.Length)
            |> should equal true

            LibraryLocalState.markFilesystemPublished dbPath repositoryId operationId catalogVersion
            |> fun operation -> operation.GetAwaiter().GetResult()

            LibraryLocalState.completeAcceptedFile dbPath repositoryId change
            |> fun operation -> operation.GetAwaiter().GetResult()

            LibraryLocalState.tryAdvanceCursor dbPath repositoryId operationId catalogVersion "epoch-1" "cursor-0" "cursor-1"
            |> fun operation -> operation.GetAwaiter().GetResult()
            |> should equal true

            File.GetLastWriteTimeUtc(targetPath)
            |> should equal publishedAt

            LibraryLocalState.tryConsumeWatchEcho dbPath repositoryId operationId itemId "shared/other.txt" blake3
            |> fun operation -> operation.GetAwaiter().GetResult()
            |> should equal false

            LibraryLocalState.tryConsumeWatchEcho dbPath repositoryId operationId itemId "shared/file.txt" (String.replicate 64 "0")
            |> fun operation -> operation.GetAwaiter().GetResult()
            |> should equal false

            LibraryLocalState.tryConsumeWatchEcho dbPath repositoryId operationId itemId "shared/file.txt" blake3
            |> fun operation -> operation.GetAwaiter().GetResult()
            |> should equal true

            LibraryLocalState.tryConsumeWatchEcho dbPath repositoryId operationId itemId "shared/file.txt" blake3
            |> fun operation -> operation.GetAwaiter().GetResult()
            |> should equal false)
