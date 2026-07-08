namespace Grace.Operations.Worker

open Azure
open Azure.Core
open Azure.Identity
open Azure.Messaging.ServiceBus
open Azure.Storage.Blobs
open Grace.Operations.Data
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Usage
open Microsoft.Data.SqlClient
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.IO.Compression
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Identifies the Grace operations worker assembly before ingestion hosting is wired by a later slice.
[<AbstractClass; Sealed>]
type internal OperationsWorkerAssembly =
    class
    end

/// Holds the operational fact envelope constants shared with the publisher contract without depending on Grace.Actors.
[<RequireQualifiedAccess>]
module internal OperationalFactEnvelope =

    /// Identifies the Service Bus subject used for immutable usage fact messages.
    [<Literal>]
    let UsageFactSubject = "GraceOperationalUsageFact"

    /// Names the application property that classifies operational fact messages.
    [<Literal>]
    let UsageFactMessageTypeProperty = "graceMessageType"

    /// Identifies the only operational fact message type consumed by this worker slice.
    [<Literal>]
    let UsageFactMessageType = "UsageFact"

    /// Names the application property that records the usage fact kind for diagnostics and routing.
    [<Literal>]
    let UsageFactKindProperty = "usageFactKind"

/// Carries the safe metadata and body bytes the ingestion processor needs from a Service Bus message.
type OperationsUsageMessage =
    {
        MessageId: string
        CorrelationId: string
        DeliveryCount: int
        Subject: string
        ApplicationProperties: IReadOnlyDictionary<string, obj>
        Body: byte array
    }

/// Settles an operational usage message only after the processor decides the durable outcome.
type IOperationsUsageMessageActions =

    /// Completes a message after durable SQL processing succeeds or proves the fact was already processed.
    abstract CompleteAsync: cancellationToken: CancellationToken -> Task

    /// Abandons a message when a transient failure should allow Service Bus retry delivery.
    abstract AbandonAsync: cancellationToken: CancellationToken -> Task

    /// Dead-letters a message that cannot become a valid usage fact through retry.
    abstract DeadLetterAsync: reason: string * description: string * cancellationToken: CancellationToken -> Task

/// Stores one usage fact through the operations data layer.
type IOperationsUsageFactStore =

    /// Persists the fact idempotently and returns the operations data-layer result.
    abstract StoreUsageFactAsync:
        fact: UsageFact * rawPayload: byte array * cancellationToken: CancellationToken -> Task<Result<UsageFactPersistenceResult, string list>>

/// Adapts the concrete operations data store to the worker's fakeable ingestion dependency.
type OperationsUsageFactStoreAdapter(store: OperationsUsageStore) =

    interface IOperationsUsageFactStore with

        member _.StoreUsageFactAsync(fact, rawPayload, cancellationToken) = store.StoreUsageFactAsync(fact, rawPayload, cancellationToken)

/// Carries deterministic compressed JSONL bytes and the Blob authority they must verify against.
type OperationsUsageArchiveBlob = { Pointer: RawUsageFactArchivePointer; Content: byte array }

/// Writes and verifies archive Blob content before SQL clears hot payload bytes.
type IOperationsUsageArchiveBlobStore =

    /// Writes archive content if absent, then verifies checksum and byte length from Blob storage.
    abstract WriteAndVerifyAsync: archiveBlob: OperationsUsageArchiveBlob * cancellationToken: CancellationToken -> Task

    /// Verifies an already-recorded Blob pointer before SQL cleanup resumes.
    abstract VerifyAsync: pointer: RawUsageFactArchivePointer * cancellationToken: CancellationToken -> Task

    /// Downloads archived Blob bytes after verifying checksum and byte length against SQL pointer authority.
    abstract DownloadVerifiedAsync: pointer: RawUsageFactArchivePointer * cancellationToken: CancellationToken -> Task<byte array>

/// Replays archived usage facts without restoring hot SQL payload authority.
type IOperationsUsageArchiveReplayStore =

    /// Persists an archived fact idempotently while keeping `ops.RawUsageFact.RawPayload` empty.
    abstract ReplayArchivedUsageFactAsync:
        fact: UsageFact * rawPayload: byte array * pointer: RawUsageFactArchivePointer * cancellationToken: CancellationToken ->
            Task<Result<UsageFactPersistenceResult, string list>>

/// Adapts the concrete operations usage store to archive replay.
type OperationsUsageArchiveReplayStoreAdapter(store: OperationsUsageStore) =

    interface IOperationsUsageArchiveReplayStore with

        member _.ReplayArchivedUsageFactAsync(fact, rawPayload, pointer, cancellationToken) =
            store.ReplayArchivedUsageFactAsync(fact, rawPayload, pointer, cancellationToken)

/// Builds deterministic archive names, JSONL payloads, compression, and checksums for raw usage facts.
[<RequireQualifiedAccess>]
module OperationsUsageArchiveFormat =

    /// Identifies the archive record schema written into compressed JSONL blobs.
    [<Literal>]
    let ArchiveSchemaVersion = 1

    /// Converts binary data into lowercase hexadecimal for persisted SHA-256 checksums.
    let private toLowerHex (bytes: byte array) =
        let builder = StringBuilder(bytes.Length * 2)
        let mutable index = 0

        while index < bytes.Length do
            builder.Append(
                bytes[index]
                    .ToString("x2", CultureInfo.InvariantCulture)
            )
            |> ignore

            index <- index + 1

        builder.ToString()

    /// Computes the lowercase SHA-256 checksum used by SQL archive authority.
    let checksumSha256Hex (content: byte array) = SHA256.HashData content |> toLowerHex

    /// Builds the deterministic Blob name for one archived usage fact.
    let blobName (candidate: RawUsageFactArchiveCandidate) =
        let observedUtc = candidate.ObservedAt.ToDateTimeUtc()
        let observedYear = observedUtc.Year.ToString("0000", CultureInfo.InvariantCulture)
        let observedMonth = observedUtc.Month.ToString("00", CultureInfo.InvariantCulture)

        String.Join(
            "/",
            [|
                "usage-facts"
                "v1"
                $"observedYear={observedYear}"
                $"observedMonth={observedMonth}"
                $"ownerId={candidate.OwnerId:D}"
                $"organizationId={candidate.OrganizationId:D}"
                $"repositoryId={candidate.RepositoryId:D}"
                $"usageFactId={candidate.UsageFactId:D}.jsonl.gz"
            |]
        )

    /// Writes one JSONL record with a stable property order and the exact accepted raw payload bytes.
    let jsonLineBytes (candidate: RawUsageFactArchiveCandidate) (rawPayload: byte array) =
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))

        writer.WriteStartObject()
        writer.WriteNumber("archiveSchemaVersion", ArchiveSchemaVersion)
        writer.WriteString("usageFactId", string candidate.UsageFactId)
        writer.WriteString("correlationId", string candidate.CorrelationId)
        writer.WriteString("factKind", candidate.FactKind.ToString())
        writer.WriteString("ownerId", string candidate.OwnerId)
        writer.WriteString("organizationId", string candidate.OrganizationId)
        writer.WriteString("repositoryId", string candidate.RepositoryId)
        writer.WriteString("storagePoolId", string candidate.StoragePoolId)
        writer.WriteNumber("quantity", candidate.Quantity)

        writer.WriteString(
            "observedAtUtc",
            candidate
                .ObservedAt
                .ToDateTimeUtc()
                .ToString("O", CultureInfo.InvariantCulture)
        )

        writer.WriteBase64String("rawPayloadBase64", ReadOnlySpan<byte>(rawPayload))
        writer.WriteEndObject()
        writer.Flush()
        stream.WriteByte(byte '\n')
        stream.ToArray()

    /// Compresses JSONL bytes with gzip so archive content remains compact and byte-for-byte repeatable.
    let gzip (jsonl: byte array) =
        use output = new MemoryStream()

        use gzipStream = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen = true)

        gzipStream.Write(jsonl, 0, jsonl.Length)
        gzipStream.Dispose()
        output.ToArray()

    /// Builds the compressed archive blob and SQL pointer for one hot raw usage fact.
    let createArchiveBlob (candidate: RawUsageFactArchiveCandidate) (rawPayload: byte array) =
        let content = rawPayload |> jsonLineBytes candidate |> gzip

        let pointer =
            {
                UsageFactId = candidate.UsageFactId
                BlobName = blobName candidate
                ChecksumSha256Hex = checksumSha256Hex content
                ByteLength = int64 content.Length
            }

        { Pointer = pointer; Content = content }

    /// Decompresses archived gzip JSONL bytes for replay validation.
    let gunzip (content: byte array) =
        use input = new MemoryStream(content)
        use gzipStream = new GZipStream(input, CompressionMode.Decompress)
        use output = new MemoryStream()
        gzipStream.CopyTo output
        output.ToArray()

    /// Reads a required string property from the archive JSON record.
    let private readString (root: JsonElement) (name: string) = root.GetProperty(name).GetString()

    /// Fails replay when the archive record and compact SQL index disagree.
    let private requireMatch description expected actual =
        if not (String.Equals(expected, actual, StringComparison.Ordinal)) then
            invalidOp $"Archive replay {description} mismatch. Expected '{expected}'; actual '{actual}'."

    /// Extracts and validates the raw UsageFact payload from an archived Blob against the compact SQL index.
    let readValidatedUsageFact (item: RawUsageFactArchiveReplayItem) (content: byte array) =
        let jsonl = gunzip content
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(jsonl))
        let root = document.RootElement

        let archiveSchemaVersion =
            root
                .GetProperty("archiveSchemaVersion")
                .GetInt32()

        if archiveSchemaVersion <> ArchiveSchemaVersion then
            invalidOp
                $"Archive replay schema version mismatch for UsageFactId '{item.UsageFactId}'. Expected {ArchiveSchemaVersion}; actual {archiveSchemaVersion}."

        requireMatch "UsageFactId" (string item.UsageFactId) (readString root "usageFactId")
        requireMatch "CorrelationId" (string item.CorrelationId) (readString root "correlationId")
        requireMatch "FactKind" (item.FactKind.ToString()) (readString root "factKind")
        requireMatch "OwnerId" (string item.OwnerId) (readString root "ownerId")
        requireMatch "OrganizationId" (string item.OrganizationId) (readString root "organizationId")
        requireMatch "RepositoryId" (string item.RepositoryId) (readString root "repositoryId")
        requireMatch "StoragePoolId" (string item.StoragePoolId) (readString root "storagePoolId")

        requireMatch
            "Quantity"
            (item.Quantity.ToString(CultureInfo.InvariantCulture))
            (root
                .GetProperty("quantity")
                .GetInt64()
                .ToString(CultureInfo.InvariantCulture))

        requireMatch
            "ObservedAtUtc"
            (item
                .ObservedAt
                .ToDateTimeUtc()
                .ToString("O", CultureInfo.InvariantCulture))
            (readString root "observedAtUtc")

        let rawPayload =
            root
                .GetProperty("rawPayloadBase64")
                .GetBytesFromBase64()

        let usageFact =
            use stream = new MemoryStream(rawPayload, writable = false)
            JsonSerializer.Deserialize<UsageFact>(stream, Constants.JsonSerializerOptions)

        if isNull (box usageFact) then
            invalidOp $"Archive replay payload for UsageFactId '{item.UsageFactId}' did not contain a UsageFact."

        match UsageFact.Validate usageFact with
        | Error errors ->
            let errorText = String.Join("; ", errors)
            invalidOp $"Archive replay payload for UsageFactId '{item.UsageFactId}' is invalid: {errorText}"
        | Ok () -> ()

        requireMatch "payload UsageFactId" (string item.UsageFactId) (string usageFact.UsageFactId)
        requireMatch "payload CorrelationId" (string item.CorrelationId) (string usageFact.CorrelationId)
        requireMatch "payload FactKind" (item.FactKind.ToString()) (usageFact.FactKind.ToString())
        requireMatch "payload OwnerId" (string item.OwnerId) (string usageFact.Scope.OwnerId)
        requireMatch "payload OrganizationId" (string item.OrganizationId) (string usageFact.Scope.OrganizationId)
        requireMatch "payload RepositoryId" (string item.RepositoryId) (string usageFact.Scope.RepositoryId)
        requireMatch "payload StoragePoolId" (string item.StoragePoolId) (string usageFact.Resource.StoragePoolId)
        requireMatch "payload Quantity" (item.Quantity.ToString(CultureInfo.InvariantCulture)) (usageFact.Quantity.ToString(CultureInfo.InvariantCulture))

        requireMatch
            "payload ObservedAtUtc"
            (item
                .ObservedAt
                .ToDateTimeUtc()
                .ToString("O", CultureInfo.InvariantCulture))
            (usageFact
                .ObservedAt
                .ToDateTimeUtc()
                .ToString("O", CultureInfo.InvariantCulture))

        usageFact, rawPayload

/// Stores archive blobs in Azure Blob Storage and reads them back for authority verification.
type AzureOperationsUsageArchiveBlobStore(containerClient: BlobContainerClient) =

    /// Downloads Blob content so checksum and byte length are verified from durable storage, not local memory.
    let downloadContentAsync blobName cancellationToken =
        task {
            let blobClient = containerClient.GetBlobClient blobName
            let! response = blobClient.DownloadContentAsync cancellationToken
            return response.Value.Content.ToArray()
        }

    /// Throws when durable Blob bytes do not match the SQL archive authority.
    let verifyContent (pointer: RawUsageFactArchivePointer) (content: byte array) =
        let actualByteLength = int64 content.Length

        if actualByteLength <> pointer.ByteLength then
            invalidOp
                $"Archive Blob '{pointer.BlobName}' length mismatch for UsageFactId '{pointer.UsageFactId}'. Expected {pointer.ByteLength}; actual {actualByteLength}."

        let actualChecksum = OperationsUsageArchiveFormat.checksumSha256Hex content

        if not (String.Equals(actualChecksum, pointer.ChecksumSha256Hex, StringComparison.Ordinal)) then
            invalidOp $"Archive Blob '{pointer.BlobName}' checksum mismatch for UsageFactId '{pointer.UsageFactId}'."

    /// Uploads content only when the deterministic Blob does not already exist.
    let uploadIfMissingAsync (archiveBlob: OperationsUsageArchiveBlob) cancellationToken =
        task {
            let blobClient = containerClient.GetBlobClient archiveBlob.Pointer.BlobName

            try
                use stream = new MemoryStream(archiveBlob.Content, writable = false)
                let! _ = blobClient.UploadAsync(stream, overwrite = false, cancellationToken = cancellationToken)
                return ()
            with
            | :? RequestFailedException as ex when String.Equals(ex.ErrorCode, "BlobAlreadyExists", StringComparison.OrdinalIgnoreCase) -> return ()
        }

    interface IOperationsUsageArchiveBlobStore with

        member _.WriteAndVerifyAsync(archiveBlob, cancellationToken) =
            task {
                let! _ = containerClient.CreateIfNotExistsAsync(cancellationToken = cancellationToken)
                do! uploadIfMissingAsync archiveBlob cancellationToken
                let! storedContent = downloadContentAsync archiveBlob.Pointer.BlobName cancellationToken
                verifyContent archiveBlob.Pointer storedContent
            }

        member _.VerifyAsync(pointer, cancellationToken) =
            task {
                let! storedContent = downloadContentAsync pointer.BlobName cancellationToken
                verifyContent pointer storedContent
            }

        member _.DownloadVerifiedAsync(pointer, cancellationToken) =
            task {
                let! storedContent = downloadContentAsync pointer.BlobName cancellationToken
                verifyContent pointer storedContent
                return storedContent
            }

/// Archives old hot raw payloads after Blob authority is verified and persisted in SQL.
type OperationsUsageArchiveProcessor
    (
        archiveStore: IOperationsUsageArchiveStore,
        blobStore: IOperationsUsageArchiveBlobStore,
        logger: ILogger<OperationsUsageArchiveProcessor>
    ) =

    /// Rebuilds a verified SQL pointer from a partially archived row before retrying hot-payload cleanup.
    let pointerFromVerifiedCandidate (candidate: RawUsageFactArchiveCandidate) =
        match candidate.ArchiveBlobName, candidate.ArchiveChecksumSha256Hex, candidate.ArchiveByteLength with
        | Some blobName, Some checksum, Some byteLength ->
            { UsageFactId = candidate.UsageFactId; BlobName = blobName; ChecksumSha256Hex = checksum; ByteLength = byteLength }
        | _ -> invalidOp $"UsageFactId '{candidate.UsageFactId}' is marked archive-verified without a complete Blob pointer."

    /// Archives one hot candidate or resumes cleanup for an already verified candidate.
    let archiveCandidateAsync (candidate: RawUsageFactArchiveCandidate) cancellationToken =
        task {
            let! pointer =
                task {
                    match candidate.ArchiveState with
                    | RawUsageFactArchiveState.Hot ->
                        match candidate.RawPayload with
                        | Some rawPayload ->
                            let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob candidate rawPayload
                            do! blobStore.WriteAndVerifyAsync(archiveBlob, cancellationToken)
                            let! _ = archiveStore.MarkArchiveVerifiedAsync(archiveBlob.Pointer, cancellationToken)
                            return archiveBlob.Pointer
                        | None -> return invalidOp $"Hot UsageFactId '{candidate.UsageFactId}' has no raw payload to archive."
                    | RawUsageFactArchiveState.ArchiveVerified -> return pointerFromVerifiedCandidate candidate
                    | RawUsageFactArchiveState.Archived -> return pointerFromVerifiedCandidate candidate
                    | state -> return invalidOp $"UsageFactId '{candidate.UsageFactId}' has unsupported archive state '{state}'."
                }

            do! blobStore.VerifyAsync(pointer, cancellationToken)
            let! completed = archiveStore.CompleteArchiveAsync(pointer, cancellationToken)

            logger.LogInformation(
                "Archived operational UsageFact hot payload. UsageFactId: {UsageFactId}; BlobName: {BlobName}; ByteLength: {ByteLength}; CompletedStateChange: {CompletedStateChange}.",
                pointer.UsageFactId,
                pointer.BlobName,
                pointer.ByteLength,
                completed
            )
        }

    /// Archives at most one batch of candidates older than the supplied hot-retention cutoff.
    member _.ArchiveBatchAsync(observedBefore: Instant, batchSize: int, cancellationToken: CancellationToken) =
        task {
            let! candidates = archiveStore.ListArchiveCandidatesAsync(observedBefore, batchSize, cancellationToken)
            let mutable index = 0
            let mutable archived = 0
            let candidateArray = candidates |> List.toArray

            while index < candidateArray.Length do
                cancellationToken.ThrowIfCancellationRequested()
                do! archiveCandidateAsync candidateArray[index] cancellationToken
                archived <- archived + 1
                index <- index + 1

            return archived
        }

/// Clears expired temporary-hot archived payloads without deleting compact replay authority.
type OperationsUsageTemporaryHotCleanupProcessor
    (
        archiveStore: IOperationsUsageArchiveStore,
        clock: IClock,
        logger: ILogger<OperationsUsageTemporaryHotCleanupProcessor>
    ) =

    /// Stops one cleanup pass if SQL keeps returning full batches beyond the reviewed safety bound.
    [<Literal>]
    let MaxCleanupBatchesPerPass = 100

    /// Clears expired temporary-hot payloads in bounded SQL batches until the current pass drains.
    member _.CleanupExpiredAsync(batchSize: int, cancellationToken: CancellationToken) =
        task {
            if batchSize <= 0 then
                invalidArg (nameof batchSize) "Temporary-hot cleanup batch size must be greater than zero."

            let expiresBefore = clock.GetCurrentInstant()
            let mutable totalCleaned = 0
            let mutable batches = 0
            let mutable keepCleaning = true

            while keepCleaning do
                cancellationToken.ThrowIfCancellationRequested()
                let! cleaned = archiveStore.CleanupExpiredRehydratedPayloadsAsync(expiresBefore, batchSize, cancellationToken)
                totalCleaned <- totalCleaned + cleaned
                batches <- batches + 1

                keepCleaning <-
                    cleaned = batchSize
                    && batches < MaxCleanupBatchesPerPass

            logger.LogInformation(
                "Cleaned expired operations UsageFact temporary-hot payloads. CleanedFacts: {CleanedFacts}; Batches: {Batches}; ExpiresBeforeUtc: {ExpiresBeforeUtc}.",
                totalCleaned,
                batches,
                expiresBefore
            )

            return totalCleaned
        }

/// Summarizes one archive replay batch.
type OperationsUsageArchiveReplayBatchResult = { Examined: int; Accepted: int; AlreadyProcessed: int }

/// Replays archived UsageFact payloads through SQL compact-index and Blob authority checks.
type OperationsUsageArchiveReplayProcessor
    (
        archiveStore: IOperationsUsageArchiveStore,
        blobStore: IOperationsUsageArchiveBlobStore,
        replayStore: IOperationsUsageArchiveReplayStore,
        logger: ILogger<OperationsUsageArchiveReplayProcessor>
    ) =

    /// Replays one archived item after Blob checksum, byte length, and payload/index validation.
    let replayItemAsync (item: RawUsageFactArchiveReplayItem) cancellationToken =
        task {
            let! content = blobStore.DownloadVerifiedAsync(item.Pointer, cancellationToken)
            let usageFact, rawPayload = OperationsUsageArchiveFormat.readValidatedUsageFact item content
            let! replayed = replayStore.ReplayArchivedUsageFactAsync(usageFact, rawPayload, item.Pointer, cancellationToken)

            match replayed with
            | Error errors ->
                let errorText = String.Join("; ", errors)
                return invalidOp $"Archive replay rejected UsageFactId '{item.UsageFactId}': {errorText}"
            | Ok result ->
                logger.LogInformation(
                    "Replayed archived operational UsageFact. UsageFactId: {UsageFactId}; BlobName: {BlobName}; Status: {Status}.",
                    item.UsageFactId,
                    item.Pointer.BlobName,
                    result.Status
                )

                return result.Status
        }

    /// Advances the archive replay cursor from the last row processed in SQL's stable archive ordering.
    let nextReplayCursor (item: RawUsageFactArchiveReplayItem) = { ObservedAt = item.ObservedAt; UsageFactId = item.UsageFactId }

    /// Replays archived rows in SQL-index pages, using UsageFactId idempotency to avoid duplicate aggregate projection.
    member _.ReplayBatchAsync(scope: RawUsageFactArchiveScope option, batchSize: int, cancellationToken: CancellationToken) =
        task {
            let query = { Scope = scope; ObservedAfterUtc = None; ObservedBeforeUtc = None; CorrelationIds = []; UsageFactIds = [] }

            let mutable cursor = None
            let mutable keepReading = true
            let mutable examined = 0
            let mutable accepted = 0
            let mutable alreadyProcessed = 0

            while keepReading do
                let! items = archiveStore.ListArchivedUsageFactsAsync(query, cursor, batchSize, cancellationToken)
                let itemArray = items |> List.toArray
                let mutable index = 0

                while index < itemArray.Length do
                    cancellationToken.ThrowIfCancellationRequested()
                    let item = itemArray[index]
                    let! status = replayItemAsync item cancellationToken

                    match status with
                    | UsageFactPersistenceStatus.Accepted -> accepted <- accepted + 1
                    | UsageFactPersistenceStatus.AlreadyProcessed -> alreadyProcessed <- alreadyProcessed + 1
                    | _ -> invalidOp $"Archive replay returned unsupported persistence status '{status}'."

                    examined <- examined + 1
                    cursor <- Some(nextReplayCursor item)
                    index <- index + 1

                keepReading <- itemArray.Length = batchSize

            return { Examined = examined; Accepted = accepted; AlreadyProcessed = alreadyProcessed }
        }

/// Requests temporary support access to archived payloads for one Grace repository scope.
type OperationsUsageRehydrationRequest = { Query: RawUsageFactArchiveQuery; DryRun: bool; RequestedBy: string; Reason: string; ExpiresAt: Instant option }

/// Reports temporary support rehydration audit evidence and cleanup targets.
type OperationsUsageRehydrationResult =
    {
        MatchingArchivedFactCount: int64
        DryRun: bool
        Warnings: string list
        AuditEntries: RawUsageFactRehydrationAuditEntry list
    }

/// Restores archived payloads temporarily for scoped support workflows and provides cleanup.
type OperationsUsageRehydrationProcessor
    (
        archiveStore: IOperationsUsageArchiveStore,
        blobStore: IOperationsUsageArchiveBlobStore,
        clock: IClock,
        logger: ILogger<OperationsUsageRehydrationProcessor>
    ) =

    /// Provides the default support rehydration lease duration when the operator does not supply one.
    [<Literal>]
    let DefaultExpiryHours = 8.0

    /// Bounds temporary support rehydration leases so archived payloads cannot remain hot indefinitely.
    [<Literal>]
    let MaximumExpiryDays = 7.0

    /// Names the match-count threshold where operators receive an explicit high-volume warning.
    [<Literal>]
    let RehydrationWarningThreshold = 10000L

    /// Uses the SQL payload batch size as the archive exploration page size for support rehydration.
    let RehydrationArchivePageSize = OperationsUsageSql.RehydrationPayloadBatchSize

    /// Retries each verified SQL rehydration batch after transient failures before failing the operator request.
    [<Literal>]
    let RehydrationBatchRetryLimit = 3

    /// Rejects global or unbounded rehydration requests before any Blob reads occur.
    let validateRequest (request: OperationsUsageRehydrationRequest) =
        let errors = ResizeArray<string>()
        let requestedAt = clock.GetCurrentInstant()

        let scope =
            match request.Query.Scope with
            | Some scope ->
                if scope.OwnerId.IsNone then errors.Add("Rehydration requires OwnerId scope.")

                if scope.OrganizationId.IsNone then
                    errors.Add("Rehydration requires OrganizationId scope.")

                if scope.RepositoryId.IsNone then
                    errors.Add("Rehydration requires RepositoryId scope.")

                Some scope
            | None ->
                errors.Add("Rehydration requires OwnerId scope.")
                errors.Add("Rehydration requires OrganizationId scope.")
                errors.Add("Rehydration requires RepositoryId scope.")
                None

        if request.Query.ObservedAfterUtc.IsNone
           && request.Query.ObservedBeforeUtc.IsNone then
            errors.Add("Rehydration requires ObservedAfterUtc or ObservedBeforeUtc.")

        if String.IsNullOrWhiteSpace request.RequestedBy then
            errors.Add("Rehydration RequestedBy audit value is required.")

        if String.IsNullOrWhiteSpace request.Reason then
            errors.Add("Rehydration Reason audit value is required.")

        let expiresAt =
            request.ExpiresAt
            |> Option.defaultValue (requestedAt.Plus(Duration.FromHours DefaultExpiryHours))

        if expiresAt <= requestedAt then
            errors.Add("Rehydration ExpiresAt must be in the future.")

        if expiresAt > requestedAt.Plus(Duration.FromDays MaximumExpiryDays) then
            errors.Add("Rehydration ExpiresAt must be no more than 7 days after request time.")

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            match scope with
            | Some scope -> Ok(requestedAt, expiresAt, scope)
            | None -> Error(List.ofSeq errors)

    /// Downloads and validates one archived payload before batched SQL rehydration mutates durable state.
    let buildRehydrationItemAsync (item: RawUsageFactArchiveReplayItem) (cancellationToken: CancellationToken) =
        task {
            let! content = blobStore.DownloadVerifiedAsync(item.Pointer, cancellationToken)
            let _, rawPayload = OperationsUsageArchiveFormat.readValidatedUsageFact item content
            return { Pointer = item.Pointer; RawPayload = rawPayload }
        }

    /// Raises a clear operator-facing failure when SQL did not commit every selected pointer in a batch.
    let raiseIncompleteBatchFailure (rehydrationItems: RawUsageFactRehydrationItem array) (changedUsageFactIds: UsageFactId list) =
        let changedSet = HashSet<UsageFactId>(changedUsageFactIds)

        if changedSet.Count <> rehydrationItems.Length then
            let missingIds =
                rehydrationItems
                |> Array.map (fun item -> item.Pointer.UsageFactId)
                |> Array.filter (fun usageFactId -> not (changedSet.Contains usageFactId))
                |> Array.map string
                |> fun values -> String.Join(", ", values)

            invalidOp
                $"Grace Operator support rehydration failed because SQL did not verify and rehydrate every selected archive pointer in the batch. MissingUsageFactIds: {missingIds}."

        changedSet

    /// Retries a verified support rehydration batch without rolling back earlier committed batches.
    let rehydrateBatchWithRetryAsync (items: RawUsageFactArchiveReplayItem array) expiresAt (cancellationToken: CancellationToken) =
        task {
            let rehydrationItems = ResizeArray<RawUsageFactRehydrationItem>()
            let mutable index = 0

            while index < items.Length do
                cancellationToken.ThrowIfCancellationRequested()
                let! rehydrationItem = buildRehydrationItemAsync items[index] cancellationToken
                rehydrationItems.Add rehydrationItem
                index <- index + 1

            let rehydrationArray = rehydrationItems.ToArray()
            let mutable attempt = 0
            let mutable completed = false
            let mutable changedSet = HashSet<UsageFactId>()

            while not completed
                  && attempt <= RehydrationBatchRetryLimit do
                try
                    let! changedUsageFactIds = archiveStore.RehydrateArchivedPayloadsAsync(rehydrationArray |> Array.toList, expiresAt, CancellationToken.None)

                    changedSet <- raiseIncompleteBatchFailure rehydrationArray changedUsageFactIds
                    completed <- true
                with
                | ex when attempt < RehydrationBatchRetryLimit ->
                    logger.LogWarning(
                        ex,
                        "Retrying temporary support rehydration SQL batch after failure. Attempt: {Attempt}; RetryLimit: {RetryLimit}.",
                        attempt + 1,
                        RehydrationBatchRetryLimit
                    )

                    attempt <- attempt + 1
                | ex ->
                    invalidOp
                        $"Grace Operator support rehydration failed after {attempt} retries for a verified archive batch. No compensation was attempted for earlier successful batches; expiry cleanup will remove prior temporary-hot rows. Failure: {ex.Message}"

            return rehydrationArray, changedSet
        }

    /// Rehydrates all rows matching the scoped support predicate and returns local audit proof.
    member _.RehydrateAsync(request: OperationsUsageRehydrationRequest, cancellationToken: CancellationToken) =
        task {
            match validateRequest request with
            | Error errors -> return Error errors
            | Ok (_requestedAt, expiresAt, scope) ->
                let warnings = ResizeArray<string>()
                let! matchingCount = archiveStore.CountArchivedUsageFactsAsync(request.Query, cancellationToken)

                logger.LogInformation(
                    "Support rehydration archive exploration matched {MatchingArchivedFactCount} archived operational UsageFacts. DryRun: {DryRun}.",
                    matchingCount,
                    request.DryRun
                )

                if matchingCount > RehydrationWarningThreshold then
                    let warning = $"Support rehydration matched {matchingCount} archived UsageFacts, which is above the 10000-row operator warning threshold."

                    warnings.Add warning
                    logger.LogWarning("{Warning}", warning)

                if request.DryRun then
                    return Ok { MatchingArchivedFactCount = matchingCount; DryRun = true; Warnings = warnings |> Seq.toList; AuditEntries = [] }
                else
                    let entries = ResizeArray<RawUsageFactRehydrationAuditEntry>()
                    let mutable cursor = None
                    let mutable keepReading = true

                    while keepReading do
                        let! items = archiveStore.ListArchivedUsageFactsAsync(request.Query, cursor, RehydrationArchivePageSize, cancellationToken)
                        let itemArray = items |> List.toArray

                        if itemArray.Length = 0 then
                            keepReading <- false
                        else
                            let! rehydrationItems, changedSet = rehydrateBatchWithRetryAsync itemArray expiresAt cancellationToken
                            let rehydratedAt = clock.GetCurrentInstant()
                            let mutable auditIndex = 0

                            while auditIndex < itemArray.Length do
                                cancellationToken.ThrowIfCancellationRequested()
                                let item = itemArray[auditIndex]
                                let rehydrationItem = rehydrationItems[auditIndex]

                                entries.Add
                                    {
                                        UsageFactId = item.UsageFactId
                                        Scope = scope
                                        Pointer = item.Pointer
                                        RequestedBy = request.RequestedBy
                                        Reason = request.Reason
                                        RehydratedAt = rehydratedAt
                                        ExpiresAt = expiresAt
                                        RestoredByteLength = rehydrationItem.RawPayload.Length
                                        ChangedSqlState = changedSet.Contains item.UsageFactId
                                    }

                                logger.LogInformation(
                                    "Temporarily rehydrated archived operational UsageFact. UsageFactId: {UsageFactId}; BlobName: {BlobName}; ExpiresAtUtc: {ExpiresAtUtc}; ExpiryPersisted: {ExpiryPersisted}.",
                                    item.UsageFactId,
                                    item.Pointer.BlobName,
                                    expiresAt,
                                    changedSet.Contains item.UsageFactId
                                )

                                auditIndex <- auditIndex + 1

                            cursor <-
                                Some { ObservedAt = itemArray[itemArray.Length - 1].ObservedAt; UsageFactId = itemArray[itemArray.Length - 1].UsageFactId }

                            keepReading <- itemArray.Length = RehydrationArchivePageSize

                    return
                        Ok
                            {
                                MatchingArchivedFactCount = matchingCount
                                DryRun = false
                                Warnings = warnings |> Seq.toList
                                AuditEntries = entries |> Seq.toList
                            }
        }

/// Reads Blob archive settings required by the hot/cold usage fact archive worker.
type OperationsUsageArchiveSettings =
    {
        BlobConnectionString: string option
        BlobServiceUri: string option
        BlobContainerName: string
        HotRetentionDays: int
        BatchSize: int
        PollInterval: TimeSpan
    }

/// Resolves hot/cold archive settings from host configuration and environment variables.
[<RequireQualifiedAccess>]
module OperationsUsageArchiveSettings =

    /// The environment variable that contains an Azure Storage connection string for archive Blob writes.
    [<Literal>]
    let BlobConnectionStringEnvironmentVariable = "grace__operations__archive__blob_connectionstring"

    /// The environment variable that contains a Blob service URI for managed-identity archive writes.
    [<Literal>]
    let BlobServiceUriEnvironmentVariable = "grace__operations__archive__blob_service_uri"

    /// The environment variable that names the container used for compressed usage fact archives.
    [<Literal>]
    let BlobContainerNameEnvironmentVariable = "grace__operations__archive__blob_container"

    /// The environment variable that controls how long raw payloads remain hot in SQL.
    [<Literal>]
    let HotRetentionDaysEnvironmentVariable = "grace__operations__archive__hot_retention_days"

    /// The environment variable that controls the maximum number of raw facts archived per batch.
    [<Literal>]
    let BatchSizeEnvironmentVariable = "grace__operations__archive__batch_size"

    /// The environment variable that controls the delay between archive worker batches.
    [<Literal>]
    let PollIntervalSecondsEnvironmentVariable = "grace__operations__archive__poll_interval_seconds"

    /// Returns a trimmed setting value when configuration contains a non-empty entry.
    let private optionalSetting (configuration: IConfiguration) name =
        [
            configuration[getConfigKey name]
            configuration[name]
            Environment.GetEnvironmentVariable name
        ]
        |> List.tryPick (fun value -> if String.IsNullOrWhiteSpace value then None else Some(value.Trim()))

    /// Reads a positive integer setting, rejecting malformed values once archive configuration is present.
    let private positiveIntSetting configuration name defaultValue =
        match optionalSetting configuration name with
        | Some value ->
            match Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, parsed when parsed > 0 -> Ok parsed
            | _ -> Error $"{name} must be a positive integer."
        | None -> Ok defaultValue

    /// Validates the lowercase DNS-style Azure Blob container name used for usage fact archives.
    let private validateContainerName (name: string) =
        let isLowercaseLetterOrDigit (value: char) =
            (value >= 'a' && value <= 'z')
            || (value >= '0' && value <= '9')

        let isValidCharacter (value: char) = isLowercaseLetterOrDigit value || value = '-'

        let hasValidLength = name.Length >= 3 && name.Length <= 63

        let hasValidBoundary =
            hasValidLength
            && isLowercaseLetterOrDigit name[0]
            && isLowercaseLetterOrDigit name[name.Length - 1]

        let hasValidCharacters = name |> Seq.forall isValidCharacter

        if
            hasValidLength
            && hasValidBoundary
            && hasValidCharacters
            && not (name.Contains("--", StringComparison.Ordinal))
        then
            Ok()
        else
            Error
                $"{BlobContainerNameEnvironmentVariable} must be 3-63 lowercase letters, numbers, or hyphens; start and end with a letter or number; and not contain consecutive hyphens."

    /// Builds validated archive settings from configuration.
    let fromConfiguration (configuration: IConfiguration) =
        let blobConnectionString = optionalSetting configuration BlobConnectionStringEnvironmentVariable
        let blobServiceUri = optionalSetting configuration BlobServiceUriEnvironmentVariable
        let blobContainerName = optionalSetting configuration BlobContainerNameEnvironmentVariable
        let hotRetentionDays = positiveIntSetting configuration HotRetentionDaysEnvironmentVariable 90
        let batchSize = positiveIntSetting configuration BatchSizeEnvironmentVariable 100
        let pollIntervalSeconds = positiveIntSetting configuration PollIntervalSecondsEnvironmentVariable 300
        let errors = ResizeArray<string>()

        if blobConnectionString.IsNone
           && blobServiceUri.IsNone then
            errors.Add($"{BlobConnectionStringEnvironmentVariable} or {BlobServiceUriEnvironmentVariable} is required.")

        if blobContainerName.IsNone then
            errors.Add($"{BlobContainerNameEnvironmentVariable} is required.")
        else
            match validateContainerName blobContainerName.Value with
            | Ok () -> ()
            | Error error -> errors.Add error

        match hotRetentionDays with
        | Ok _ -> ()
        | Error error -> errors.Add error

        match batchSize with
        | Ok _ -> ()
        | Error error -> errors.Add error

        match pollIntervalSeconds with
        | Ok _ -> ()
        | Error error -> errors.Add error

        match blobServiceUri with
        | Some uri ->
            match Uri.TryCreate(uri, UriKind.Absolute) with
            | true, parsed when parsed.Scheme = Uri.UriSchemeHttps -> ()
            | _ -> errors.Add($"{BlobServiceUriEnvironmentVariable} must be an absolute HTTPS URI.")
        | None -> ()

        match hotRetentionDays, batchSize, pollIntervalSeconds with
        | Ok hotRetentionDaysValue, Ok batchSizeValue, Ok pollIntervalSecondsValue when errors.Count = 0 ->
            Ok
                {
                    BlobConnectionString = blobConnectionString
                    BlobServiceUri = blobServiceUri
                    BlobContainerName = blobContainerName.Value
                    HotRetentionDays = hotRetentionDaysValue
                    BatchSize = batchSizeValue
                    PollInterval = TimeSpan.FromSeconds(float pollIntervalSecondsValue)
                }
        | _ -> Error(List.ofSeq errors)

    /// Enables the archive worker only when archive storage settings are present, while rejecting partial configuration.
    let tryFromConfiguration (configuration: IConfiguration) =
        let hasArchiveSetting =
            [
                BlobConnectionStringEnvironmentVariable
                BlobServiceUriEnvironmentVariable
                BlobContainerNameEnvironmentVariable
                HotRetentionDaysEnvironmentVariable
                BatchSizeEnvironmentVariable
                PollIntervalSecondsEnvironmentVariable
            ]
            |> List.exists (fun name ->
                optionalSetting configuration name
                |> Option.isSome)

        if hasArchiveSetting then
            fromConfiguration configuration |> Result.map Some
        else
            Ok None

/// Reports whether the operations usage worker is ready to consume durable ingestion messages.
type OperationsUsageReadinessStatus =

    /// Indicates the worker has started its ingestion dependencies successfully.
    | Ready = 1

    /// Indicates the worker has not yet proven that ingestion dependencies are available.
    | NotReady = 2

/// Describes the current ingestion readiness state without exposing secrets or message payloads.
type OperationsUsageReadinessSnapshot =
    {
        Status: OperationsUsageReadinessStatus
        SupportedUsageFactSchemaVersion: int
        DependencyFailure: string option
        LastUnsupportedContract: string option
    }

/// Identifies which dependency surface currently owns an Operations ingestion readiness failure.
type internal OperationsUsageReadinessFailureSource =

    /// Startup dependency failures are cleared only by a later successful startup pass.
    | StartupDependency = 1

    /// Runtime storage or processing failures are cleared by a later fresh durable message success.
    | RuntimeProcessingDependency = 2

    /// Service Bus receive/link failures are cleared only by receive-side recovery proof.
    | ServiceBusReceiveLink = 3

    /// Service Bus processor callback, settlement, or lock failures remain visible until their owner proves recovery.
    | ServiceBusProcessorRuntime = 4

    /// Archive retention failures remain visible until a later configured archive batch succeeds.
    | ArchiveRetentionRuntime = 5

    /// Temporary-hot cleanup failures remain visible until a later cleanup pass succeeds.
    | TemporaryHotCleanupRuntime = 6

/// Tracks one active readiness failure and the ordering point needed for source-owned recovery.
type internal OperationsUsageReadinessFailure =
    {
        Source: OperationsUsageReadinessFailureSource
        Description: string
        Version: int64
        RecoveryKey: string option
    }

/// Identifies one independently recoverable readiness failure within a dependency surface.
type internal OperationsUsageReadinessFailureKey = { Source: OperationsUsageReadinessFailureSource; RecoveryKey: string option }

/// Captures the current readiness ordering point for one in-flight message.
type OperationsUsageReadinessAttempt = { FailureVersion: int64 }

/// Exposes a snapshot of the Operations ingestion readiness state.
type IOperationsUsageReadinessProbe =

    /// Returns the latest readiness state recorded by the worker process.
    abstract GetSnapshot: unit -> OperationsUsageReadinessSnapshot

/// Records readiness signals from dependency startup and message contract classification.
type IOperationsUsageReadinessRecorder =

    /// Marks ingestion dependencies as ready after SQL initialization and Service Bus processor startup succeed.
    abstract MarkReady: unit -> unit

    /// Captures the readiness failure version visible when one message starts processing.
    abstract BeginProcessingAttempt: unit -> OperationsUsageReadinessAttempt

    /// Records a redacted dependency failure that prevents the worker from being ready.
    abstract MarkDependencyFailure: description: string -> unit

    /// Records a redacted runtime storage or processing failure that prevents the worker from being ready.
    abstract MarkRuntimeProcessingFailure: description: string -> unit

    /// Records a redacted Service Bus receive or session-link failure that prevents the worker from being ready.
    abstract MarkServiceBusReceiveFailure: description: string -> unit

    /// Records a redacted Service Bus callback, settlement, or lock failure that prevents the worker from being ready.
    abstract MarkServiceBusProcessorFailure: description: string * recoveryKey: string option -> unit

    /// Clears a Service Bus receive or link failure only when a later received message proves the link recovered.
    abstract MarkServiceBusReceiveSuccess: attempt: OperationsUsageReadinessAttempt -> unit

    /// Clears runtime-owned processing failures only when this success started after that failure.
    abstract MarkRuntimeProcessingSuccess: attempt: OperationsUsageReadinessAttempt -> unit

    /// Clears one Service Bus processor-owned failure after the matching processor proof succeeds later.
    abstract MarkServiceBusProcessorSuccess: attempt: OperationsUsageReadinessAttempt * recoveryKey: string -> unit

    /// Records a redacted archive retention failure that prevents configured retention from being ready.
    abstract MarkArchiveProcessingFailure: description: string -> unit

    /// Clears archive-owned retention failures after a later archive batch succeeds.
    abstract MarkArchiveProcessingSuccess: unit -> unit

    /// Records a redacted temporary-hot cleanup failure that prevents configured retention from being ready.
    abstract MarkTemporaryHotCleanupFailure: description: string -> unit

    /// Clears cleanup-owned failures after a later cleanup pass succeeds.
    abstract MarkTemporaryHotCleanupSuccess: unit -> unit

    /// Records the latest unsupported ingestion contract observed at the broker boundary.
    abstract MarkUnsupportedContract: description: string -> unit

/// Maintains the redacted readiness snapshot exposed by the Operations worker process.
type OperationsUsageReadinessState() =
    let gate = obj ()

    let mutable failureVersion = 0L

    let dependencyFailures = Dictionary<OperationsUsageReadinessFailureKey, OperationsUsageReadinessFailure>()

    let failureKey source recoveryKey = { Source = source; RecoveryKey = recoveryKey }

    do
        let startupKey = failureKey OperationsUsageReadinessFailureSource.StartupDependency None

        dependencyFailures[startupKey] <- {
                                              Source = OperationsUsageReadinessFailureSource.StartupDependency
                                              Description = "Operations usage schema initialization is pending."
                                              Version = failureVersion
                                              RecoveryKey = None
                                          }

    let mutable lastUnsupportedContract: string option = None

    let readinessStatus () =
        if dependencyFailures.Count = 0 then
            OperationsUsageReadinessStatus.Ready
        else
            OperationsUsageReadinessStatus.NotReady

    let dependencyFailureDescription () =
        if dependencyFailures.Count = 0 then
            None
        else
            dependencyFailures.Values
            |> Seq.sortBy (fun failure ->
                int failure.Source,
                failure.RecoveryKey
                |> Option.defaultValue String.Empty)
            |> Seq.map (fun failure -> failure.Description)
            |> fun descriptions -> String.Join("; ", descriptions)
            |> Some

    let recordFailure source description recoveryKey =
        failureVersion <- failureVersion + 1L

        dependencyFailures[failureKey source recoveryKey] <- { Source = source; Description = description; Version = failureVersion; RecoveryKey = recoveryKey }

    let clearFailure source recoveryKey =
        dependencyFailures.Remove(failureKey source recoveryKey)
        |> ignore

    let clearFailureWhenFresh source attempt =
        let key = failureKey source None

        match dependencyFailures.TryGetValue key with
        | true, failure when failure.Version <= attempt.FailureVersion -> clearFailure source None
        | _ -> ()

    let clearServiceBusProcessorFailureWhenFresh attempt recoveryKey =
        let key = failureKey OperationsUsageReadinessFailureSource.ServiceBusProcessorRuntime (Some recoveryKey)

        match dependencyFailures.TryGetValue key with
        | true, failure when failure.Version <= attempt.FailureVersion ->
            clearFailure OperationsUsageReadinessFailureSource.ServiceBusProcessorRuntime (Some recoveryKey)
        | _ -> ()

    let snapshot () =
        lock gate (fun () ->
            {
                Status = readinessStatus ()
                SupportedUsageFactSchemaVersion = UsageFactSchemaVersion
                DependencyFailure = dependencyFailureDescription ()
                LastUnsupportedContract = lastUnsupportedContract
            })

    interface IOperationsUsageReadinessProbe with

        member _.GetSnapshot() = snapshot ()

    interface IOperationsUsageReadinessRecorder with

        member _.MarkReady() = lock gate (fun () -> clearFailure OperationsUsageReadinessFailureSource.StartupDependency None)

        member _.BeginProcessingAttempt() = lock gate (fun () -> { FailureVersion = failureVersion })

        member _.MarkDependencyFailure(description) =
            lock gate (fun () -> recordFailure OperationsUsageReadinessFailureSource.StartupDependency description None)

        member _.MarkRuntimeProcessingFailure(description) =
            lock gate (fun () -> recordFailure OperationsUsageReadinessFailureSource.RuntimeProcessingDependency description None)

        member _.MarkServiceBusReceiveFailure(description) =
            lock gate (fun () -> recordFailure OperationsUsageReadinessFailureSource.ServiceBusReceiveLink description None)

        member _.MarkServiceBusProcessorFailure(description, recoveryKey) =
            lock gate (fun () -> recordFailure OperationsUsageReadinessFailureSource.ServiceBusProcessorRuntime description recoveryKey)

        member _.MarkArchiveProcessingFailure(description) =
            lock gate (fun () -> recordFailure OperationsUsageReadinessFailureSource.ArchiveRetentionRuntime description None)

        member _.MarkTemporaryHotCleanupFailure(description) =
            lock gate (fun () -> recordFailure OperationsUsageReadinessFailureSource.TemporaryHotCleanupRuntime description None)

        member _.MarkServiceBusReceiveSuccess(attempt) =
            lock gate (fun () -> clearFailureWhenFresh OperationsUsageReadinessFailureSource.ServiceBusReceiveLink attempt)

        member _.MarkRuntimeProcessingSuccess(attempt) =
            lock gate (fun () -> clearFailureWhenFresh OperationsUsageReadinessFailureSource.RuntimeProcessingDependency attempt)

        member _.MarkServiceBusProcessorSuccess(attempt, recoveryKey) = lock gate (fun () -> clearServiceBusProcessorFailureWhenFresh attempt recoveryKey)

        member _.MarkArchiveProcessingSuccess() = lock gate (fun () -> clearFailure OperationsUsageReadinessFailureSource.ArchiveRetentionRuntime None)

        member _.MarkTemporaryHotCleanupSuccess() = lock gate (fun () -> clearFailure OperationsUsageReadinessFailureSource.TemporaryHotCleanupRuntime None)

        member _.MarkUnsupportedContract(description) = lock gate (fun () -> lastUnsupportedContract <- Some description)

/// Records runtime readiness transitions that must stay visible through the shared health-check state.
[<RequireQualifiedAccess>]
module internal OperationsUsageReadinessTransitions =

    /// Identifies the Service Bus settlement operation that proves a matching processor failure recovered.
    [<Literal>]
    let CompleteSettlementRecoveryKey = "complete"

    /// Identifies the Service Bus abandon operation that proves a matching processor failure recovered.
    [<Literal>]
    let AbandonSettlementRecoveryKey = "abandon"

    /// Identifies the Service Bus dead-letter operation that proves a matching processor failure recovered.
    [<Literal>]
    let DeadLetterSettlementRecoveryKey = "dead-letter"

    /// Identifies callback invocation failures that recover after any later explicit settlement succeeds.
    [<Literal>]
    let CallbackRuntimeRecoveryKey = "callback"

    /// Identifies lock-renewal failures that require renewal-side recovery proof instead of ordinary message completion.
    [<Literal>]
    let RenewLockRecoveryKey = "renew-lock"

    /// Identifies processor sources where a later receive proves broker receive/session-link recovery.
    let private isReceiveRecoverySource errorSource =
        match errorSource with
        | ServiceBusErrorSource.Receive
        | ServiceBusErrorSource.AcceptSession
        | ServiceBusErrorSource.CloseSession -> true
        | ServiceBusErrorSource.Complete
        | ServiceBusErrorSource.Abandon
        | ServiceBusErrorSource.ProcessMessageCallback
        | ServiceBusErrorSource.RenewLock -> false
        | _ -> false

    /// Maps Azure Service Bus error sources to the concrete settlement proof that may recover them later.
    let private serviceBusProcessorRecoveryKey errorSource =
        match errorSource with
        | ServiceBusErrorSource.Complete -> Some CompleteSettlementRecoveryKey
        | ServiceBusErrorSource.ProcessMessageCallback -> Some CallbackRuntimeRecoveryKey
        | ServiceBusErrorSource.Abandon -> Some AbandonSettlementRecoveryKey
        | ServiceBusErrorSource.RenewLock -> Some RenewLockRecoveryKey
        | _ -> None

    /// Records a redacted Service Bus processor fault observed after startup.
    let recordServiceBusProcessorFault (readiness: IOperationsUsageReadinessRecorder) (errorSource: ServiceBusErrorSource) (ex: exn) =
        let description = $"Service Bus processor fault ({errorSource}, {ex.GetType().Name})."

        if isReceiveRecoverySource errorSource then
            readiness.MarkServiceBusReceiveFailure(description)
        else
            readiness.MarkServiceBusProcessorFailure(description, serviceBusProcessorRecoveryKey errorSource)

    /// Records a redacted Service Bus settlement failure observed inside explicit worker settlement calls.
    let recordServiceBusSettlementFailure (readiness: IOperationsUsageReadinessRecorder) recoveryKey (ex: exn) =
        let description = $"Service Bus settlement failed ({recoveryKey}, {ex.GetType().Name})."
        readiness.MarkServiceBusProcessorFailure(description, Some recoveryKey)

    /// Records a later receive-side proof that a Service Bus processor receive or link fault recovered.
    let recordServiceBusReceiveSuccess (readiness: IOperationsUsageReadinessRecorder) (attempt: OperationsUsageReadinessAttempt) =
        readiness.MarkServiceBusReceiveSuccess(attempt)

    /// Records a later settlement-side proof that a Service Bus processor settlement fault recovered.
    let recordServiceBusProcessorSuccess (readiness: IOperationsUsageReadinessRecorder) (attempt: OperationsUsageReadinessAttempt) recoveryKey =
        readiness.MarkServiceBusProcessorSuccess(attempt, recoveryKey)

    /// Records that a later callback reached explicit settlement, proving callback invocation recovered.
    let recordServiceBusCallbackSuccess (readiness: IOperationsUsageReadinessRecorder) (attempt: OperationsUsageReadinessAttempt) =
        readiness.MarkServiceBusProcessorSuccess(attempt, CallbackRuntimeRecoveryKey)

    /// Records a redacted runtime processing dependency failure that should recover after a later successful message.
    let recordRuntimeProcessingFailure (readiness: IOperationsUsageReadinessRecorder) (ex: exn) =
        readiness.MarkRuntimeProcessingFailure($"Runtime processing dependency failed ({ex.GetType().Name}).")

/// Adapts Operations ingestion readiness to the standard .NET health-check surface.
type OperationsUsageReadinessHealthCheck(readiness: IOperationsUsageReadinessProbe) =

    /// Builds redacted health-check data that operators can inspect without seeing payloads or secrets.
    let healthData (snapshot: OperationsUsageReadinessSnapshot) =
        let data = Dictionary<string, obj>()
        data["supportedUsageFactSchemaVersion"] <- box snapshot.SupportedUsageFactSchemaVersion

        match snapshot.DependencyFailure with
        | Some dependencyFailure -> data["dependencyFailure"] <- box dependencyFailure
        | None -> ()

        match snapshot.LastUnsupportedContract with
        | Some unsupportedContract -> data["lastUnsupportedContract"] <- box unsupportedContract
        | None -> ()

        data :> IReadOnlyDictionary<string, obj>

    /// Builds the unhealthy readiness description without repeating sensitive dependency configuration.
    let unhealthyDescription (snapshot: OperationsUsageReadinessSnapshot) =
        snapshot.DependencyFailure
        |> Option.defaultValue "Operations usage ingestion dependencies have not reported ready."

    interface IHealthCheck with

        /// Reports healthy only after the worker records successful SQL and Service Bus startup.
        member _.CheckHealthAsync(_context, _cancellationToken) =
            let snapshot = readiness.GetSnapshot()
            let data = healthData snapshot

            let result =
                match snapshot.Status with
                | OperationsUsageReadinessStatus.Ready -> HealthCheckResult(HealthStatus.Healthy, "Operations usage ingestion is ready.", null, data)
                | _ -> HealthCheckResult(HealthStatus.Unhealthy, unhealthyDescription snapshot, null, data)

            Task.FromResult result

/// Publishes the Operations ingestion readiness check through worker-host health publishing.
type OperationsUsageReadinessHealthCheckPublisher(logger: ILogger<OperationsUsageReadinessHealthCheckPublisher>) =

    /// Reads one redacted health-check data field for structured publishing.
    let tryReadDataField name (data: IReadOnlyDictionary<string, obj>) =
        match data.TryGetValue name with
        | true, value when not (isNull value) -> Some(string value)
        | _ -> None

    interface IHealthCheckPublisher with

        /// Logs the readiness health-check result so worker hosts expose readiness without HTTP endpoints.
        member _.PublishAsync(report, cancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()

            match report.Entries.TryGetValue "operations-usage-ingestion" with
            | true, entry ->
                let supportedSchemaVersion =
                    tryReadDataField "supportedUsageFactSchemaVersion" entry.Data
                    |> Option.defaultValue "<unknown>"

                let dependencyFailure =
                    tryReadDataField "dependencyFailure" entry.Data
                    |> Option.defaultValue "<none>"

                let lastUnsupportedContract =
                    tryReadDataField "lastUnsupportedContract" entry.Data
                    |> Option.defaultValue "<none>"

                logger.LogInformation(
                    "Operations usage readiness published. Status: {HealthStatus}; SupportedUsageFactSchemaVersion: {SupportedUsageFactSchemaVersion}; DependencyFailure: {DependencyFailure}; LastUnsupportedContract: {LastUnsupportedContract}.",
                    entry.Status,
                    supportedSchemaVersion,
                    dependencyFailure,
                    lastUnsupportedContract
                )
            | false, _ ->
                logger.LogWarning(
                    "Operations usage readiness health check was not present in the published worker health report. OverallStatus: {HealthStatus}.",
                    report.Status
                )

            Task.CompletedTask

/// Reads Service Bus settings required by the operational usage ingestion worker.
type OperationsWorkerSettings =
    {
        TopicName: string
        SubscriptionName: string
        SqlConnectionString: string
        ServiceBusConnectionString: string option
        ServiceBusFullyQualifiedNamespace: string option
        SchemaBootstrapMode: OperationsUsageSchemaBootstrapMode
        MaxConcurrentCalls: int
        PrefetchCount: int
    }

/// Resolves worker settings from host configuration and environment variables.
[<RequireQualifiedAccess>]
module OperationsWorkerSettings =

    /// The durable subscription used by the operations worker for the operational facts topic.
    [<Literal>]
    let DefaultProcessorSubscriptionName = "operational-facts-processor"

    /// The environment variable that confirms the durable operational facts processor subscription name.
    [<Literal>]
    let ProcessorSubscriptionEnvironmentVariable = "grace__azure_service_bus__operational_facts_processor_subscription"

    /// The environment variable that contains the SQL connection string for operations usage storage.
    [<Literal>]
    let SqlConnectionStringEnvironmentVariable = "grace__operations__sql__connectionstring"

    /// The environment variable that controls Service Bus processor concurrency for usage fact ingestion.
    [<Literal>]
    let MaxConcurrentCallsEnvironmentVariable = "grace__operations_worker__max_concurrent_calls"

    /// The environment variable that controls Service Bus prefetch for usage fact ingestion.
    [<Literal>]
    let PrefetchCountEnvironmentVariable = "grace__operations_worker__prefetch_count"

    /// Converts short Service Bus namespace settings into the fully qualified host expected by Azure.Messaging.ServiceBus.
    let private normalizeServiceBusNamespace (value: string) =
        let trimmed = value.Trim()

        let withoutScheme =
            if trimmed.StartsWith("sb://", StringComparison.OrdinalIgnoreCase) then
                trimmed.Substring(5)
            else
                trimmed

        let normalizedNamespace = withoutScheme.Trim().TrimEnd('/')

        if normalizedNamespace.Contains "." then
            normalizedNamespace
        else
            $"{normalizedNamespace}.servicebus.windows.net"

    /// Returns a trimmed setting value when configuration contains a non-empty entry.
    let private optionalSetting (configuration: IConfiguration) name =
        [
            configuration[getConfigKey name]
            configuration[name]
            Environment.GetEnvironmentVariable name
        ]
        |> List.tryPick (fun value -> if String.IsNullOrWhiteSpace value then None else Some(value.Trim()))

    /// Reads a setting or returns the supplied default.
    let private settingOrDefault configuration name defaultValue =
        optionalSetting configuration name
        |> Option.defaultValue defaultValue

    /// Reads a positive integer setting or returns the supplied default.
    let private positiveIntSetting configuration name defaultValue =
        match optionalSetting configuration name with
        | Some value ->
            match Int32.TryParse value with
            | true, parsed when parsed > 0 -> parsed
            | _ -> defaultValue
        | None -> defaultValue

    /// Reads a non-negative integer setting or returns the supplied default.
    let private nonNegativeIntSetting configuration name defaultValue =
        match optionalSetting configuration name with
        | Some value ->
            match Int32.TryParse value with
            | true, parsed when parsed >= 0 -> parsed
            | _ -> defaultValue
        | None -> defaultValue

    /// Allows local SQL emulator runs to create the operations database while keeping Azure connections least-privilege.
    let private schemaBootstrapMode configuration =
        match optionalSetting configuration Constants.EnvironmentVariables.DebugEnvironment with
        | Some value when value.Equals("Local", StringComparison.OrdinalIgnoreCase) -> OperationsUsageSchemaBootstrapMode.CreateDatabaseIfMissing
        | _ -> OperationsUsageSchemaBootstrapMode.TargetDatabaseOnly

    /// Attempts managed-identity Service Bus discovery without letting unrelated Azure environment gaps abort validation.
    let private serviceBusNamespaceFromAzureEnvironment () =
        try
            AzureEnvironment.tryGetServiceBusFullyQualifiedNamespace ()
        with
        | :? InvalidOperationException
        | :? TypeInitializationException -> None

    /// Adds a validation error when the worker is configured for a receive mode that cannot prove recovery safely.
    let private validateReceiveProofMode maxConcurrentCalls prefetchCount (errors: ResizeArray<string>) =
        if maxConcurrentCalls <> 1 then
            errors.Add(
                $"{MaxConcurrentCallsEnvironmentVariable} must be '1' because callback start ordering cannot prove Service Bus receive/link recovery when concurrent callbacks are enabled."
            )

        if prefetchCount <> 0 then
            errors.Add(
                $"{PrefetchCountEnvironmentVariable} must be '0' because prefetched callbacks cannot prove Service Bus receive/link recovery after a later receive fault."
            )

    /// Builds validated worker settings from configuration.
    let fromConfiguration (configuration: IConfiguration) =
        let topicName = settingOrDefault configuration Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic Constants.GraceOperationalFactsTopic

        let subscriptionName = settingOrDefault configuration ProcessorSubscriptionEnvironmentVariable DefaultProcessorSubscriptionName

        let sqlConnectionString = optionalSetting configuration SqlConnectionStringEnvironmentVariable

        let serviceBusConnectionString = optionalSetting configuration Constants.EnvironmentVariables.AzureServiceBusConnectionString

        let serviceBusNamespace =
            optionalSetting configuration Constants.EnvironmentVariables.AzureServiceBusNamespace
            |> Option.map normalizeServiceBusNamespace

        let errors = ResizeArray<string>()

        if String.IsNullOrWhiteSpace topicName then
            errors.Add($"{Constants.EnvironmentVariables.AzureServiceBusOperationalFactsTopic} is required.")

        if subscriptionName
           <> DefaultProcessorSubscriptionName then
            errors.Add(
                $"{ProcessorSubscriptionEnvironmentVariable} must be '{DefaultProcessorSubscriptionName}' so the worker uses the durable operational facts processor subscription."
            )

        if sqlConnectionString.IsNone then
            errors.Add($"{SqlConnectionStringEnvironmentVariable} is required.")

        if serviceBusConnectionString.IsNone
           && serviceBusNamespace.IsNone
           && serviceBusNamespaceFromAzureEnvironment().IsNone then
            errors.Add(
                $"{Constants.EnvironmentVariables.AzureServiceBusConnectionString} or {Constants.EnvironmentVariables.AzureServiceBusNamespace} is required."
            )

        let maxConcurrentCalls = positiveIntSetting configuration MaxConcurrentCallsEnvironmentVariable 1
        let prefetchCount = nonNegativeIntSetting configuration PrefetchCountEnvironmentVariable 0

        validateReceiveProofMode maxConcurrentCalls prefetchCount errors

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            Ok
                {
                    TopicName = topicName
                    SubscriptionName = subscriptionName
                    SqlConnectionString = sqlConnectionString.Value
                    ServiceBusConnectionString = serviceBusConnectionString
                    ServiceBusFullyQualifiedNamespace =
                        match serviceBusConnectionString with
                        | Some _ -> serviceBusNamespace
                        | None ->
                            serviceBusNamespace
                            |> Option.orElseWith serviceBusNamespaceFromAzureEnvironment
                    SchemaBootstrapMode = schemaBootstrapMode configuration
                    MaxConcurrentCalls = maxConcurrentCalls
                    PrefetchCount = prefetchCount
                }

/// Handles one usage fact message through validation, SQL persistence, and explicit Service Bus settlement.
type OperationsUsageIngestionProcessor
    (
        store: IOperationsUsageFactStore,
        logger: ILogger<OperationsUsageIngestionProcessor>,
        readiness: IOperationsUsageReadinessRecorder
    ) =

    /// Reads a string application property without exposing untrusted payload values in logs.
    let tryGetStringProperty propertyName (properties: IReadOnlyDictionary<string, obj>) =
        match properties.TryGetValue propertyName with
        | true, (:? string as value) when not (String.IsNullOrWhiteSpace value) -> Some value
        | true, value when not (isNull value) -> Some(string value)
        | _ -> None

    /// Builds a bounded diagnostic description without including the message body.
    let describeErrors (errors: string list) = String.Join("; ", errors)

    /// Runs one explicit Service Bus settlement call and records matching settlement and callback recovery proofs.
    let settleAsync readinessAttempt recoveryKey settlementName (settle: unit -> Task) (cancellationToken: CancellationToken) =
        task {
            try
                do! settle ()
                OperationsUsageReadinessTransitions.recordServiceBusProcessorSuccess readiness readinessAttempt recoveryKey
                OperationsUsageReadinessTransitions.recordServiceBusCallbackSuccess readiness readinessAttempt
                return true
            with
            | :? OperationCanceledException when cancellationToken.IsCancellationRequested -> return false
            | ex ->
                OperationsUsageReadinessTransitions.recordServiceBusSettlementFailure readiness recoveryKey ex

                logger.LogError(
                    ex,
                    "Service Bus settlement failed during operations UsageFact processing. Settlement: {Settlement}; RecoveryKey: {RecoveryKey}.",
                    settlementName,
                    recoveryKey
                )

                return false
        }

    /// Dead-letters an invalid usage message with deterministic reason metadata.
    let deadLetterAsync readinessAttempt reason description (actions: IOperationsUsageMessageActions) cancellationToken =
        settleAsync
            readinessAttempt
            OperationsUsageReadinessTransitions.DeadLetterSettlementRecoveryKey
            "dead-letter"
            (fun () -> actions.DeadLetterAsync(reason, description, cancellationToken))
            cancellationToken

    /// Configures lightweight schema pre-parsing to match Grace's shared JSON reader leniency.
    let usageFactSchemaDocumentOptions =
        JsonDocumentOptions(
            AllowTrailingCommas = Constants.JsonSerializerOptions.AllowTrailingCommas,
            CommentHandling = Constants.JsonSerializerOptions.ReadCommentHandling,
            MaxDepth = Constants.JsonSerializerOptions.MaxDepth
        )

    /// Reads the schema version before binding the body to the v1 UsageFact enum contract.
    let tryReadUsageFactSchemaVersion (body: byte array) =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(body), usageFactSchemaDocumentOptions)
        let root = document.RootElement

        let tryGetProperty (name: string) =
            let mutable property = Unchecked.defaultof<JsonElement>

            if
                root.ValueKind = JsonValueKind.Object
                && root.TryGetProperty(name, &property)
            then
                Some property
            else
                None

        let tryGetPropertyCaseInsensitive (name: string) =
            if root.ValueKind = JsonValueKind.Object then
                root.EnumerateObject()
                |> Seq.tryFind (fun property -> String.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                |> Option.map (fun property -> property.Value)
            else
                None

        match tryGetProperty "schemaVersion"
              |> Option.orElseWith (fun () -> tryGetProperty "SchemaVersion")
              |> Option.orElseWith (fun () -> tryGetPropertyCaseInsensitive "schemaVersion")
            with
        | Some property when property.ValueKind = JsonValueKind.Number ->
            match property.TryGetInt32() with
            | true, value -> Some value
            | false, _ -> None
        | Some property when property.ValueKind = JsonValueKind.String ->
            match Int32.TryParse(property.GetString()) with
            | true, value -> Some value
            | false, _ -> None
        | _ -> None

    /// Deserializes a usage fact using the shared Grace JSON contract options.
    let deserializeUsageFact (body: byte array) =
        use stream = new MemoryStream(body)
        JsonSerializer.Deserialize<UsageFact>(stream, Constants.JsonSerializerOptions)

    /// Builds the deterministic unsupported-schema description used for settlement and readiness.
    let unsupportedSchemaDescription schemaVersion = $"UsageFact SchemaVersion '{schemaVersion}' is not supported. Expected '{UsageFactSchemaVersion}'."

    /// Records unsupported schema readiness and dead-letters without exposing the message body.
    let deadLetterUnsupportedSchemaAsync readinessAttempt schemaVersion (message: OperationsUsageMessage) actions cancellationToken =
        let description = unsupportedSchemaDescription schemaVersion

        readiness.MarkUnsupportedContract(description)

        logger.LogWarning(
            "Dead-lettering unsupported UsageFact schema. MessageId: {MessageId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}; SchemaVersion: {SchemaVersion}; SupportedSchemaVersion: {SupportedSchemaVersion}.",
            message.MessageId,
            message.CorrelationId,
            message.DeliveryCount,
            schemaVersion,
            UsageFactSchemaVersion
        )

        deadLetterAsync readinessAttempt "UnsupportedUsageFactSchema" description actions cancellationToken

    /// Builds a processor with an isolated readiness state for tests that only inspect settlement behavior.
    new(store, logger) = OperationsUsageIngestionProcessor(store, logger, OperationsUsageReadinessState() :> IOperationsUsageReadinessRecorder)

    /// Processes a usage fact message and settles it only after the durable outcome is known.
    member _.ProcessMessageAsync(message: OperationsUsageMessage, actions: IOperationsUsageMessageActions, cancellationToken: CancellationToken) =
        task {
            let readinessAttempt = readiness.BeginProcessingAttempt()

            try
                cancellationToken.ThrowIfCancellationRequested()

                let messageType = tryGetStringProperty OperationalFactEnvelope.UsageFactMessageTypeProperty message.ApplicationProperties

                if message.Subject
                   <> OperationalFactEnvelope.UsageFactSubject
                   || messageType
                      <> Some OperationalFactEnvelope.UsageFactMessageType then
                    readiness.MarkUnsupportedContract("Unsupported Service Bus envelope for operations UsageFact ingestion.")

                    logger.LogWarning(
                        "Dead-lettering unsupported operational fact envelope. MessageId: {MessageId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}; Subject: {Subject}; MessageType: {MessageType}.",
                        message.MessageId,
                        message.CorrelationId,
                        message.DeliveryCount,
                        message.Subject,
                        messageType |> Option.defaultValue "<missing>"
                    )

                    let! _ =
                        deadLetterAsync
                            readinessAttempt
                            "UnsupportedOperationalFactEnvelope"
                            "Message subject or graceMessageType is not supported by the operations usage worker."
                            actions
                            cancellationToken

                    ()
                else
                    match tryReadUsageFactSchemaVersion message.Body with
                    | Some schemaVersion when schemaVersion <> UsageFactSchemaVersion ->
                        let! _ = deadLetterUnsupportedSchemaAsync readinessAttempt schemaVersion message actions cancellationToken
                        ()
                    | _ ->
                        let usageFact = deserializeUsageFact message.Body

                        if isNull (box usageFact) then
                            let errors = [ "UsageFact is required." ]

                            logger.LogWarning(
                                "Dead-lettering invalid UsageFact. MessageId: {MessageId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}; Errors: {ValidationErrors}.",
                                message.MessageId,
                                message.CorrelationId,
                                message.DeliveryCount,
                                describeErrors errors
                            )

                            let! _ = deadLetterAsync readinessAttempt "InvalidUsageFact" (describeErrors errors) actions cancellationToken
                            ()
                        elif usageFact.SchemaVersion <> UsageFactSchemaVersion then
                            let! _ = deadLetterUnsupportedSchemaAsync readinessAttempt usageFact.SchemaVersion message actions cancellationToken
                            ()
                        else
                            match UsageFact.Validate usageFact with
                            | Error errors ->
                                logger.LogWarning(
                                    "Dead-lettering invalid UsageFact. MessageId: {MessageId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}; Errors: {ValidationErrors}.",
                                    message.MessageId,
                                    message.CorrelationId,
                                    message.DeliveryCount,
                                    describeErrors errors
                                )

                                let! _ = deadLetterAsync readinessAttempt "InvalidUsageFact" (describeErrors errors) actions cancellationToken
                                ()
                            | Ok () ->
                                let! stored = store.StoreUsageFactAsync(usageFact, message.Body, cancellationToken)

                                match stored with
                                | Error errors ->
                                    logger.LogWarning(
                                        "Dead-lettering UsageFact rejected by operations storage validation. UsageFactId: {UsageFactId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}; Errors: {ValidationErrors}.",
                                        usageFact.UsageFactId,
                                        usageFact.CorrelationId,
                                        message.DeliveryCount,
                                        describeErrors errors
                                    )

                                    let! _ = deadLetterAsync readinessAttempt "InvalidUsageFact" (describeErrors errors) actions cancellationToken
                                    ()
                                | Ok result ->
                                    logger.LogInformation(
                                        "Processed operational UsageFact. UsageFactId: {UsageFactId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}; Status: {Status}; BucketStart: {BucketStart}.",
                                        result.UsageFactId,
                                        usageFact.CorrelationId,
                                        message.DeliveryCount,
                                        result.Status,
                                        result.Aggregate
                                        |> Option.map (fun aggregate -> aggregate.Key.BucketStart)
                                        |> Option.defaultValue Instant.MinValue
                                    )

                                    readiness.MarkRuntimeProcessingSuccess(readinessAttempt)

                                    let! _ =
                                        settleAsync
                                            readinessAttempt
                                            OperationsUsageReadinessTransitions.CompleteSettlementRecoveryKey
                                            "complete"
                                            (fun () -> actions.CompleteAsync cancellationToken)
                                            cancellationToken

                                    ()
            with
            | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                logger.LogWarning(
                    "Operational UsageFact processing was cancelled before message settlement. MessageId: {MessageId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}.",
                    message.MessageId,
                    message.CorrelationId,
                    message.DeliveryCount
                )
            | :? JsonException as ex ->
                logger.LogWarning(
                    ex,
                    "Dead-lettering malformed UsageFact JSON. MessageId: {MessageId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}.",
                    message.MessageId,
                    message.CorrelationId,
                    message.DeliveryCount
                )

                let! _ = deadLetterAsync readinessAttempt "MalformedUsageFactJson" "UsageFact JSON could not be deserialized." actions cancellationToken
                ()
            | ex ->
                OperationsUsageReadinessTransitions.recordRuntimeProcessingFailure readiness ex

                logger.LogError(
                    ex,
                    "Abandoning operational UsageFact message after transient processing failure. MessageId: {MessageId}; CorrelationId: {CorrelationId}; DeliveryCount: {DeliveryCount}.",
                    message.MessageId,
                    message.CorrelationId,
                    message.DeliveryCount
                )

                let! _ =
                    settleAsync
                        readinessAttempt
                        OperationsUsageReadinessTransitions.AbandonSettlementRecoveryKey
                        "abandon"
                        (fun () -> actions.AbandonAsync cancellationToken)
                        cancellationToken

                ()
        }

/// Adapts Azure Service Bus message settlement to the worker's fakeable action interface.
type internal ServiceBusOperationsUsageMessageActions(args: ProcessMessageEventArgs) =

    interface IOperationsUsageMessageActions with

        member _.CompleteAsync(cancellationToken) = args.CompleteMessageAsync(args.Message, cancellationToken)

        member _.AbandonAsync(cancellationToken) = args.AbandonMessageAsync(args.Message, cancellationToken = cancellationToken)

        member _.DeadLetterAsync(reason, description, cancellationToken) =
            args.DeadLetterMessageAsync(
                args.Message,
                deadLetterReason = reason,
                deadLetterErrorDescription = description,
                cancellationToken = cancellationToken
            )

/// Converts Service Bus messages into the processor's redacted, deterministic input shape.
[<RequireQualifiedAccess>]
module internal OperationsUsageServiceBusMessage =

    /// Creates the ingestion processor message model from a received Service Bus message.
    let fromReceivedMessage (message: ServiceBusReceivedMessage) =
        {
            MessageId = message.MessageId
            CorrelationId = message.CorrelationId
            DeliveryCount = message.DeliveryCount
            Subject = message.Subject
            ApplicationProperties = message.ApplicationProperties
            Body = message.Body.ToArray()
        }

    /// Handles a delivered Azure Service Bus message after the processor proves receive-side delivery.
    let processReceivedMessageAsync
        (readiness: IOperationsUsageReadinessRecorder)
        (processor: OperationsUsageIngestionProcessor)
        canProveReceiveRecovery
        (message: ServiceBusReceivedMessage)
        (actions: IOperationsUsageMessageActions)
        cancellationToken
        =
        if canProveReceiveRecovery then
            let receiveAttempt = readiness.BeginProcessingAttempt()
            OperationsUsageReadinessTransitions.recordServiceBusReceiveSuccess readiness receiveAttempt

        processor.ProcessMessageAsync(fromReceivedMessage message, actions, cancellationToken)

/// Runs hot/cold archive batches for raw usage fact payload retention.
type OperationsUsageArchiveWorkerService
    (
        settings: OperationsUsageArchiveSettings,
        schema: IOperationsUsageSchemaInitializer,
        processor: OperationsUsageArchiveProcessor,
        readiness: IOperationsUsageReadinessRecorder,
        logger: ILogger<OperationsUsageArchiveWorkerService>
    ) =
    inherit BackgroundService()

    /// Computes the exclusive hot-retention cutoff for the next archive batch.
    let observedBefore () =
        SystemClock.Instance.GetCurrentInstant()
        - Duration.FromDays(settings.HotRetentionDays)

    /// Runs one archive pass and records redacted operational evidence.
    let runBatchAsync cancellationToken =
        task {
            let cutoff = observedBefore ()
            let! archived = processor.ArchiveBatchAsync(cutoff, settings.BatchSize, cancellationToken)
            readiness.MarkArchiveProcessingSuccess()

            logger.LogInformation(
                "Completed operations usage archive batch. ArchivedFacts: {ArchivedFacts}; HotRetentionDays: {HotRetentionDays}; CutoffUtc: {CutoffUtc}.",
                archived,
                settings.HotRetentionDays,
                cutoff
            )
        }

    /// Waits for schema readiness without misclassifying migration startup as archive dependency failure.
    let tryEnsureSchemaReadyAsync cancellationToken =
        task {
            try
                do! schema.EnsureCreatedAsync cancellationToken
                return true
            with
            | :? OperationCanceledException when cancellationToken.IsCancellationRequested -> return false
            | ex ->
                readiness.MarkDependencyFailure($"Operations usage schema initialization failed ({ex.GetType().Name}).")

                logger.LogWarning(ex, "Operations usage archive worker is waiting for schema initialization before querying archive state.")

                return false
        }

    /// Delays between archive passes while allowing prompt host shutdown.
    let delayUntilNextBatchAsync (cancellationToken: CancellationToken) = Task.Delay(settings.PollInterval, cancellationToken)

    /// Runs archive batches until the worker host shuts down.
    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            logger.LogInformation(
                "Started operations usage archive worker for container {ContainerName}; HotRetentionDays: {HotRetentionDays}; BatchSize: {BatchSize}.",
                settings.BlobContainerName,
                settings.HotRetentionDays,
                settings.BatchSize
            )

            let mutable running = true

            while running
                  && not stoppingToken.IsCancellationRequested do
                try
                    let! schemaReady = tryEnsureSchemaReadyAsync stoppingToken

                    if schemaReady then do! runBatchAsync stoppingToken

                    do! delayUntilNextBatchAsync stoppingToken
                with
                | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> running <- false
                | ex ->
                    readiness.MarkArchiveProcessingFailure($"Archive retention failed ({ex.GetType().Name}).")
                    logger.LogError(ex, "Operations usage archive batch failed; retrying after the configured poll interval.")
                    do! delayUntilNextBatchAsync stoppingToken
        }

/// Runs periodic cleanup for expired temporary-hot archived payloads.
type OperationsUsageTemporaryHotCleanupWorkerService
    (
        schema: IOperationsUsageSchemaInitializer,
        processor: OperationsUsageTemporaryHotCleanupProcessor,
        readiness: IOperationsUsageReadinessRecorder,
        logger: ILogger<OperationsUsageTemporaryHotCleanupWorkerService>
    ) =
    inherit BackgroundService()

    /// Uses the operator-approved cadence for temporary-hot expiry cleanup.
    let cleanupInterval: TimeSpan = TimeSpan.FromMinutes(30.0)

    /// Runs one cleanup pass after schema initialization succeeds.
    let runCleanupAsync cancellationToken =
        task {
            let! cleaned = processor.CleanupExpiredAsync(OperationsUsageSql.TemporaryHotCleanupBatchSize, cancellationToken)
            readiness.MarkTemporaryHotCleanupSuccess()

            logger.LogInformation(
                "Completed operations usage temporary-hot cleanup pass. CleanedFacts: {CleanedFacts}; BatchSize: {BatchSize}.",
                cleaned,
                OperationsUsageSql.TemporaryHotCleanupBatchSize
            )
        }

    /// Waits for schema readiness without misclassifying migration startup as cleanup failure.
    let tryEnsureSchemaReadyAsync cancellationToken =
        task {
            try
                do! schema.EnsureCreatedAsync cancellationToken
                return true
            with
            | :? OperationCanceledException when cancellationToken.IsCancellationRequested -> return false
            | ex ->
                readiness.MarkDependencyFailure($"Operations usage schema initialization failed ({ex.GetType().Name}).")

                logger.LogWarning(ex, "Operations usage temporary-hot cleanup worker is waiting for schema initialization before querying archive state.")

                return false
        }

    /// Delays between cleanup passes while allowing prompt host shutdown.
    let delayUntilNextCleanupAsync (cancellationToken: CancellationToken) = Task.Delay(cleanupInterval, cancellationToken)

    /// Runs cleanup passes until the worker host shuts down.
    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            logger.LogInformation(
                "Started operations usage temporary-hot cleanup worker. PollInterval: {PollInterval}; BatchSize: {BatchSize}.",
                cleanupInterval,
                OperationsUsageSql.TemporaryHotCleanupBatchSize
            )

            let mutable running = true

            while running
                  && not stoppingToken.IsCancellationRequested do
                try
                    let! schemaReady = tryEnsureSchemaReadyAsync stoppingToken

                    if schemaReady then do! runCleanupAsync stoppingToken

                    do! delayUntilNextCleanupAsync stoppingToken
                with
                | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> running <- false
                | ex ->
                    readiness.MarkTemporaryHotCleanupFailure($"Temporary-hot cleanup failed ({ex.GetType().Name}).")
                    logger.LogError(ex, "Operations usage temporary-hot cleanup failed; retrying after the configured poll interval.")
                    do! delayUntilNextCleanupAsync stoppingToken
        }

/// Runs the operational fact Service Bus processor for the Grace operations worker process.
type OperationsUsageWorkerService
    (
        settings: OperationsWorkerSettings,
        schema: IOperationsUsageSchemaInitializer,
        processor: OperationsUsageIngestionProcessor,
        readiness: IOperationsUsageReadinessRecorder,
        logger: ILogger<OperationsUsageWorkerService>
    ) =
    let credential = lazy (DefaultAzureCredential() :> TokenCredential)
    let mutable serviceBusClient: ServiceBusClient option = None
    let mutable serviceBusProcessor: ServiceBusProcessor option = None
    let mutable processingCancellation: CancellationTokenSource option = None
    let mutable processingTask: Task option = None

    /// Creates a Service Bus client from connection string or managed identity settings.
    let createClient () =
        match settings.ServiceBusConnectionString, settings.ServiceBusFullyQualifiedNamespace with
        | Some connectionString, _ -> ServiceBusClient(connectionString)
        | None, Some fullyQualifiedNamespace -> ServiceBusClient(fullyQualifiedNamespace, credential.Value)
        | None, None -> invalidOp "Azure Service Bus connection string or namespace must be configured."

    /// Extracts the non-secret SQL data source for dependency diagnostics without logging credentials.
    let sqlDataSource () =
        try
            let builder = SqlConnectionStringBuilder(settings.SqlConnectionString)

            if String.IsNullOrWhiteSpace builder.DataSource then
                "<missing>"
            else
                builder.DataSource
        with
        | _ -> "<unavailable>"

    /// Starts the Azure Service Bus processor after SQL schema initialization succeeds.
    let startProcessingAsync (cancellationToken: CancellationToken) =
        task {
            if serviceBusProcessor.IsSome then
                logger.LogDebug("Operations usage worker already running; skipping duplicate startup.")
            else
                let mutable ready = false

                while not ready
                      && not cancellationToken.IsCancellationRequested do
                    let mutable createdClient = None
                    let mutable createdProcessor = None

                    try
                        do! schema.EnsureCreatedAsync cancellationToken

                        let client = createClient ()
                        createdClient <- Some client

                        let processorOptions =
                            ServiceBusProcessorOptions(
                                AutoCompleteMessages = false,
                                MaxConcurrentCalls = settings.MaxConcurrentCalls,
                                PrefetchCount = settings.PrefetchCount,
                                Identifier = Environment.MachineName
                            )

                        let azureProcessor = client.CreateProcessor(settings.TopicName, settings.SubscriptionName, processorOptions)

                        createdProcessor <- Some azureProcessor

                        azureProcessor.add_ProcessMessageAsync (
                            Func<ProcessMessageEventArgs, Task> (fun args ->
                                let actions = ServiceBusOperationsUsageMessageActions args

                                OperationsUsageServiceBusMessage.processReceivedMessageAsync
                                    readiness
                                    processor
                                    (settings.MaxConcurrentCalls = 1
                                     && settings.PrefetchCount = 0)
                                    args.Message
                                    actions
                                    args.CancellationToken)
                        )

                        azureProcessor.add_ProcessErrorAsync (
                            Func<ProcessErrorEventArgs, Task> (fun args ->
                                OperationsUsageReadinessTransitions.recordServiceBusProcessorFault readiness args.ErrorSource args.Exception

                                logger.LogError(
                                    args.Exception,
                                    "Operations usage Service Bus processor fault. ErrorSource: {ErrorSource}; EntityPath: {EntityPath}; FullyQualifiedNamespace: {FullyQualifiedNamespace}.",
                                    args.ErrorSource,
                                    args.EntityPath,
                                    args.FullyQualifiedNamespace
                                )

                                Task.CompletedTask)
                        )

                        do! azureProcessor.StartProcessingAsync cancellationToken
                        serviceBusClient <- Some client
                        serviceBusProcessor <- Some azureProcessor
                        createdClient <- None
                        createdProcessor <- None

                        logger.LogInformation(
                            "Started operations usage worker for topic {TopicName} / subscription {SubscriptionName}.",
                            settings.TopicName,
                            settings.SubscriptionName
                        )

                        readiness.MarkReady()
                        ready <- true
                    with
                    | :? OperationCanceledException when cancellationToken.IsCancellationRequested -> ()
                    | ex ->
                        match createdProcessor with
                        | Some processor -> do! processor.DisposeAsync()
                        | None -> ()

                        match createdClient with
                        | Some client -> do! client.DisposeAsync()
                        | None -> ()

                        readiness.MarkDependencyFailure($"Dependency startup failed ({ex.GetType().Name}).")

                        logger.LogWarning(
                            ex,
                            "Operations usage worker dependencies are not ready for SQL data source {SqlDataSource}; pausing for five seconds before retrying.",
                            sqlDataSource ()
                        )

                        do! Task.Delay(TimeSpan.FromSeconds(5.0), cancellationToken)
        }

    /// Stops the Azure Service Bus processor and releases its client.
    let stopProcessingAsync cancellationToken =
        task {
            match serviceBusProcessor with
            | Some processor ->
                try
                    do! processor.StopProcessingAsync cancellationToken
                with
                | ex -> logger.LogWarning(ex, "Operations usage processor stop failed; continuing with Dispose().")

                do! processor.DisposeAsync()
                serviceBusProcessor <- None
            | None -> ()

            match serviceBusClient with
            | Some client ->
                do! client.DisposeAsync()
                serviceBusClient <- None
            | None -> ()
        }

    /// Starts ingestion dependency retries in the background so later hosted services can start independently.
    let startProcessingInBackground (cancellationToken: CancellationToken) =
        if cancellationToken.IsCancellationRequested then
            Task.FromCanceled cancellationToken
        else
            let hasActiveStartup =
                processingTask
                |> Option.exists (fun task -> not task.IsCompleted)

            if serviceBusProcessor.IsSome || hasActiveStartup then
                logger.LogDebug("Operations usage worker startup is already active; skipping duplicate hosted-service start.")
            else
                match processingCancellation with
                | Some cancellation -> cancellation.Dispose()
                | None -> ()

                let cancellation = new CancellationTokenSource()
                processingCancellation <- Some cancellation
                processingTask <- Some(Task.Run(Func<Task>(fun () -> startProcessingAsync cancellation.Token)))

            Task.CompletedTask

    /// Cancels the background startup loop before stopping any active Service Bus processor.
    let stopBackgroundProcessingAsync (cancellationToken: CancellationToken) =
        task {
            match processingCancellation with
            | Some cancellation -> cancellation.Cancel()
            | None -> ()

            match processingTask with
            | Some task ->
                try
                    do! task.WaitAsync(cancellationToken)
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested -> ()
                | :? OperationCanceledException -> ()
                | ex -> logger.LogWarning(ex, "Operations usage startup retry loop failed while the host was stopping.")
            | None -> ()

            match processingCancellation with
            | Some cancellation -> cancellation.Dispose()
            | None -> ()

            processingCancellation <- None
            processingTask <- None
            do! stopProcessingAsync cancellationToken
        }

    interface IHostedService with

        /// Starts operational usage ingestion.
        member _.StartAsync(cancellationToken: CancellationToken) = startProcessingInBackground cancellationToken

        /// Stops operational usage ingestion.
        member _.StopAsync(cancellationToken: CancellationToken) = stopBackgroundProcessingAsync cancellationToken
