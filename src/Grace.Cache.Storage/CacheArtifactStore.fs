namespace Grace.Cache.Storage

open System
open System.IO
open System.Security.Cryptography
open Microsoft.Data.Sqlite

/// Identifies the one immutable DirectoryVersion ZIP artifact accepted by the calibrated local store.
type CacheArtifactTuple = { Kind: string; CanonicalIdentity: string; DirectoryVersionId: string; ExpectedSha256: string; ExpectedSize: int64 }

/// Distinguishes the finite local outcomes available to an artifact commit or eligibility lookup.
type CacheArtifactOutcome =
    | Filled
    | Hit of finalPath: string
    | Absent
    | Conflict of message: string
    | RecoveryRequired of message: string
    | Rejected of message: string

/// Names the executable effect boundaries inherited from the GC-CAL-00 failure matrix.
type internal CacheArtifactEffect =
    | StagingAllocation
    | StagingFileCreation
    | ByteWriteAndClose
    | SizeAndSha256Verification
    | StagingStateTransaction
    | FinalFilePublication
    | CompleteStateTransaction
    | TerminalSuccessPublication

/// Identifies whether a test fault interrupts immediately before or after one durable effect boundary.
type internal CacheArtifactFailureMoment =
    | Before
    | After

/// Supplies one focused test fault without adding runtime retry or recovery machinery.
type internal CacheArtifactFailurePoint = { Effect: CacheArtifactEffect; Moment: CacheArtifactFailureMoment }

/// Signals a deliberate test interruption at one GC-CAL-00 effect boundary.
exception internal CacheArtifactInjectedFailure of CacheArtifactEffect * CacheArtifactFailureMoment

/// Holds one managed artifact root coupled to the already-owned private storage database.
type CacheArtifactStore = private { Store: CacheStore; ManagedRoot: string }

/// Owns exact-tuple local artifact publication and finite restart classification.
module CacheArtifactStore =

    type private DurableState =
        | DurableAbsent
        | DurableStaging of CacheArtifactTuple * string
        | DurableComplete of CacheArtifactTuple

    /// Returns the lowercase SHA-256 digest of a byte sequence used only for opaque managed filenames.
    let private sha256 (bytes: byte array) =
        Convert
            .ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant()

    /// Derives an opaque filename from kind plus canonical identity without treating caller text as a path.
    let private artifactKey tuple =
        $"{tuple.Kind}\n{tuple.CanonicalIdentity}"
        |> Text.Encoding.UTF8.GetBytes
        |> sha256

    /// Returns the managed staging root on the same filesystem as deterministic final files.
    let private stagingRoot store = Path.Combine(store.ManagedRoot, "staging")

    /// Returns the managed final-file root for one opaque artifact key.
    let private finalRoot store = Path.Combine(store.ManagedRoot, "artifacts")

    /// Returns the deterministic opaque final path for an immutable tuple.
    let private finalPath store tuple = Path.Combine(finalRoot store, artifactKey tuple + ".bin")

    /// Validates the exact Product V1 tuple without accepting alternate artifact kinds or digest forms.
    let private validateTuple tuple =
        if tuple.Kind <> "DirectoryVersionZip" then
            Error "Only DirectoryVersionZip artifacts are supported by the calibrated local store."
        elif String.IsNullOrWhiteSpace tuple.CanonicalIdentity then
            Error "Canonical identity is required."
        elif String.IsNullOrWhiteSpace tuple.DirectoryVersionId then
            Error "Directory version identity is required."
        elif tuple.ExpectedSize < 0L then
            Error "Expected artifact size cannot be negative."
        elif tuple.ExpectedSha256.Length <> 64
             || tuple.ExpectedSha256
                |> Seq.exists (fun value ->
                    not (
                        (value >= '0' && value <= '9')
                        || (value >= 'a' && value <= 'f')
                    )) then
            Error "Expected SHA-256 must be exactly 64 lowercase hexadecimal characters."
        else
            Ok()

    /// Creates the reset table that holds only exact Staging and Complete artifact tuples.
    let private ensureArtifactSchema (connection: SqliteConnection) =
        use command = connection.CreateCommand()

        command.CommandText <-
            "CREATE TABLE IF NOT EXISTS cache_artifact_states (artifact_key TEXT PRIMARY KEY NOT NULL, kind TEXT NOT NULL, canonical_identity TEXT NOT NULL, directory_version_id TEXT NOT NULL, expected_sha256 TEXT NOT NULL, expected_size INTEGER NOT NULL CHECK (expected_size >= 0), state TEXT NOT NULL CHECK (state IN ('Staging', 'Complete')), operation_identity TEXT NULL);"

        command.ExecuteNonQuery() |> ignore

    /// Reconstructs a tuple from the fixed artifact-state column order.
    let private readTuple (reader: SqliteDataReader) =
        {
            Kind = reader.GetString(0)
            CanonicalIdentity = reader.GetString(1)
            DirectoryVersionId = reader.GetString(2)
            ExpectedSha256 = reader.GetString(3)
            ExpectedSize = reader.GetInt64(4)
        }

    /// Reads exactly one durable artifact state without exposing raw SQLite tables to callers.
    let private readState (connection: SqliteConnection) key =
        use command = connection.CreateCommand()

        command.CommandText <-
            "SELECT kind, canonical_identity, directory_version_id, expected_sha256, expected_size, state, operation_identity FROM cache_artifact_states WHERE artifact_key = @key;"

        command.Parameters.AddWithValue("@key", key)
        |> ignore

        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            DurableAbsent
        else
            let tuple = readTuple reader

            match reader.GetString(5) with
            | "Staging" when not (reader.IsDBNull(6)) -> DurableStaging(tuple, reader.GetString(6))
            | "Complete" when reader.IsDBNull(6) -> DurableComplete tuple
            | _ -> invalidOp "Cache artifact SQLite state does not satisfy the calibrated state shape."

    /// Verifies size and SHA-256 from disk using streaming reads rather than trusting a caller-provided byte count.
    let private verifyFile path tuple =
        if not (File.Exists path) then
            false
        else
            let info = FileInfo(path)

            if info.Length <> tuple.ExpectedSize then
                false
            else
                use stream = File.OpenRead(path)
                use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                let buffer = Array.zeroCreate<byte> 81920
                let mutable read = stream.Read(buffer, 0, buffer.Length)

                while read > 0 do
                    hash.AppendData(buffer, 0, read)
                    read <- stream.Read(buffer, 0, buffer.Length)

                Convert
                    .ToHexString(hash.GetHashAndReset())
                    .ToLowerInvariant() = tuple.ExpectedSha256

    /// Clears the one-operation staging area before a fresh classification or write advances.
    let private clearStaging store =
        let root = stagingRoot store

        if Directory.Exists root then Directory.Delete(root, true)

        Directory.CreateDirectory(root) |> ignore

    /// Deletes only the exact Staging row after its incomplete owned residue has been handled.
    let private deleteExactStaging (connection: SqliteConnection) tuple =
        use transaction = connection.BeginTransaction()
        use command = connection.CreateCommand()
        command.Transaction <- transaction

        command.CommandText <-
            "DELETE FROM cache_artifact_states WHERE artifact_key = @key AND kind = @kind AND canonical_identity = @identity AND directory_version_id = @directoryVersionId AND expected_sha256 = @sha256 AND expected_size = @size AND state = 'Staging';"

        command.Parameters.AddWithValue("@key", artifactKey tuple)
        |> ignore

        command.Parameters.AddWithValue("@kind", tuple.Kind)
        |> ignore

        command.Parameters.AddWithValue("@identity", tuple.CanonicalIdentity)
        |> ignore

        command.Parameters.AddWithValue("@directoryVersionId", tuple.DirectoryVersionId)
        |> ignore

        command.Parameters.AddWithValue("@sha256", tuple.ExpectedSha256)
        |> ignore

        command.Parameters.AddWithValue("@size", tuple.ExpectedSize)
        |> ignore

        if command.ExecuteNonQuery() <> 1 then
            invalidOp "Exact staging state disappeared during restart cleanup."

        transaction.Commit()

    /// Changes only the exact current Staging tuple and operation into the durable commit-point state.
    let private markComplete (connection: SqliteConnection) tuple operationIdentity =
        use transaction = connection.BeginTransaction()
        use command = connection.CreateCommand()
        command.Transaction <- transaction

        command.CommandText <-
            "UPDATE cache_artifact_states SET state = 'Complete', operation_identity = NULL WHERE artifact_key = @key AND kind = @kind AND canonical_identity = @identity AND directory_version_id = @directoryVersionId AND expected_sha256 = @sha256 AND expected_size = @size AND state = 'Staging' AND operation_identity = @operationIdentity;"

        command.Parameters.AddWithValue("@key", artifactKey tuple)
        |> ignore

        command.Parameters.AddWithValue("@kind", tuple.Kind)
        |> ignore

        command.Parameters.AddWithValue("@identity", tuple.CanonicalIdentity)
        |> ignore

        command.Parameters.AddWithValue("@directoryVersionId", tuple.DirectoryVersionId)
        |> ignore

        command.Parameters.AddWithValue("@sha256", tuple.ExpectedSha256)
        |> ignore

        command.Parameters.AddWithValue("@size", tuple.ExpectedSize)
        |> ignore

        command.Parameters.AddWithValue("@operationIdentity", operationIdentity)
        |> ignore

        if command.ExecuteNonQuery() <> 1 then
            invalidOp "Exact staging tuple was not current at the Complete transaction."

        transaction.Commit()

    /// Inserts the full exact tuple and generated operation identity as the only durable partial state.
    let private insertStaging (connection: SqliteConnection) tuple operationIdentity =
        use transaction = connection.BeginTransaction()
        use command = connection.CreateCommand()
        command.Transaction <- transaction

        command.CommandText <-
            "INSERT INTO cache_artifact_states (artifact_key, kind, canonical_identity, directory_version_id, expected_sha256, expected_size, state, operation_identity) VALUES (@key, @kind, @identity, @directoryVersionId, @sha256, @size, 'Staging', @operationIdentity);"

        command.Parameters.AddWithValue("@key", artifactKey tuple)
        |> ignore

        command.Parameters.AddWithValue("@kind", tuple.Kind)
        |> ignore

        command.Parameters.AddWithValue("@identity", tuple.CanonicalIdentity)
        |> ignore

        command.Parameters.AddWithValue("@directoryVersionId", tuple.DirectoryVersionId)
        |> ignore

        command.Parameters.AddWithValue("@sha256", tuple.ExpectedSha256)
        |> ignore

        command.Parameters.AddWithValue("@size", tuple.ExpectedSize)
        |> ignore

        command.Parameters.AddWithValue("@operationIdentity", operationIdentity)
        |> ignore

        command.ExecuteNonQuery() |> ignore
        transaction.Commit()

    /// Classifies one tuple's finite residue table while the caller retains the single operation gate.
    let private classifyWithConnection store tuple (connection: SqliteConnection) =
        ensureArtifactSchema connection
        let key = artifactKey tuple

        match readState connection key with
        | DurableAbsent ->
            let final = finalPath store tuple

            if File.Exists final then
                RecoveryRequired "An absent SQLite row has an owned final file; explicit local reset is required."
            else
                clearStaging store
                Absent
        | DurableStaging (current, _) when current <> tuple ->
            Conflict "A staging row already owns this opaque artifact key with a conflicting immutable tuple."
        | DurableStaging (current, operationIdentity) ->
            let final = finalPath store current

            if verifyFile final current then
                markComplete connection current operationIdentity
                clearStaging store
                Hit final
            else
                if File.Exists final then File.Delete(final)

                clearStaging store
                deleteExactStaging connection current
                Absent
        | DurableComplete current when current <> tuple -> Conflict "Complete content already owns this opaque artifact key with a conflicting immutable tuple."
        | DurableComplete current ->
            let final = finalPath store current

            if verifyFile final current then
                Hit final
            else
                RecoveryRequired "Complete SQLite state disagrees with the final file; explicit local reset is required."

    /// Classifies one tuple's finite residue table before a lookup can report local eligibility.
    let private classify store tuple =
        CacheStore.withStoreOperation store.Store (fun databasePath ->
            CacheStore.withBusyRetry (fun () ->
                use connection = CacheStore.openConnection databasePath
                classifyWithConnection store tuple connection))

    /// Interrupts one focused test execution immediately before or after its named effect boundary.
    let private executeEffect failurePoint effect action =
        match failurePoint with
        | Some point when point.Effect = effect && point.Moment = Before -> raise (CacheArtifactInjectedFailure(effect, Before))
        | _ -> ()

        action ()

        match failurePoint with
        | Some point when point.Effect = effect && point.Moment = After -> raise (CacheArtifactInjectedFailure(effect, After))
        | _ -> ()

    /// Streams source bytes into a staged file and returns the independently observed length and SHA-256 digest.
    let private writeAndHash stagedPath (source: Stream) =
        use destination = new FileStream(stagedPath, FileMode.Open, FileAccess.Write, FileShare.None)
        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        let buffer = Array.zeroCreate<byte> 81920
        let mutable length = 0L
        let mutable read = source.Read(buffer, 0, buffer.Length)

        while read > 0 do
            destination.Write(buffer, 0, read)
            hash.AppendData(buffer, 0, read)
            length <- length + int64 read
            read <- source.Read(buffer, 0, buffer.Length)

        destination.Flush(true)

        length,
        Convert
            .ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant()

    /// Opens one managed artifact root after the private storage database has already gained process ownership.
    let create (store: CacheStore) managedRoot =
        if String.IsNullOrWhiteSpace managedRoot then
            invalidArg (nameof managedRoot) "Managed artifact root is required."

        let root =
            Path
                .GetFullPath(managedRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

        CacheStore.withStoreOperation store (fun databasePath ->
            Directory.CreateDirectory(root) |> ignore

            Directory.CreateDirectory(Path.Combine(root, "staging"))
            |> ignore

            Directory.CreateDirectory(Path.Combine(root, "artifacts"))
            |> ignore

            use connection = CacheStore.openConnection databasePath
            ensureArtifactSchema connection)

        { Store = store; ManagedRoot = root }

    /// Returns local eligibility only after restart classification has ruled out staging and verified final bytes.
    let inspect store tuple =
        match validateTuple tuple with
        | Error message -> Rejected message
        | Ok () -> classify store tuple

    /// Commits one streamed immutable artifact in the proven effect order and exposes success only after recheck.
    let private commitInternal store tuple (source: Stream) failurePoint =
        match validateTuple tuple with
        | Error message -> Rejected message
        | Ok () ->
            CacheStore.withStoreOperation store.Store (fun databasePath ->
                CacheStore.withBusyRetry (fun () ->
                    use connection = CacheStore.openConnection databasePath

                    match classifyWithConnection store tuple connection with
                    | Hit path -> Hit path
                    | Conflict message -> Conflict message
                    | RecoveryRequired message -> RecoveryRequired message
                    | Rejected message -> Rejected message
                    | Filled -> invalidOp "Artifact classification cannot report Filled before a write."
                    | Absent ->
                        let key = artifactKey tuple
                        let operationIdentity = Guid.NewGuid().ToString("N")
                        let operationDirectory = Path.Combine(stagingRoot store, operationIdentity)
                        let stagedPath = Path.Combine(operationDirectory, key + "." + operationIdentity + ".part")
                        let final = finalPath store tuple

                        executeEffect failurePoint StagingAllocation (fun () ->
                            Directory.CreateDirectory(operationDirectory)
                            |> ignore)

                        executeEffect failurePoint StagingFileCreation (fun () ->
                            use _stream = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                            ())

                        let mutable observedLength = 0L
                        let mutable observedSha256 = String.Empty

                        executeEffect failurePoint ByteWriteAndClose (fun () ->
                            let length, digest = writeAndHash stagedPath source
                            observedLength <- length
                            observedSha256 <- digest)

                        executeEffect failurePoint SizeAndSha256Verification (fun () ->
                            if
                                observedLength <> tuple.ExpectedSize
                                || observedSha256 <> tuple.ExpectedSha256
                                || not (verifyFile stagedPath tuple)
                            then
                                invalidOp "Staged bytes failed exact size and lowercase SHA-256 verification.")

                        executeEffect failurePoint StagingStateTransaction (fun () -> insertStaging connection tuple operationIdentity)
                        executeEffect failurePoint FinalFilePublication (fun () -> File.Move(stagedPath, final, false))
                        executeEffect failurePoint CompleteStateTransaction (fun () -> markComplete connection tuple operationIdentity)
                        executeEffect failurePoint TerminalSuccessPublication ignore

                        if not (verifyFile final tuple) then
                            invalidOp "Terminal success requires verified final bytes."

                        match readState connection key with
                        | DurableComplete current when current = tuple -> Filled
                        | _ -> invalidOp "Terminal success requires exact Complete SQLite state."))

    /// Commits one immutable stream without exposing the test-only failure injector to production callers.
    let commit store tuple source = commitInternal store tuple source None

    /// Commits one immutable stream with one focused GC-CAL-00 failure injection for production proof.
    let internal commitWithFailure store tuple source failurePoint = commitInternal store tuple source (Some failurePoint)

    /// Returns an opaque managed final path only for focused production tests that validate residue behavior.
    let internal finalPathForTest store tuple = finalPath store tuple

    /// Returns an opaque owned staging path only for focused production tests that create unknown residue.
    let internal unknownStagingPathForTest store tuple = Path.Combine(stagingRoot store, artifactKey tuple + ".unknown.part")
