namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Operations.Data.Migrations
open Grace.Operations.Worker
open Grace.Shared
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Migrations
open Microsoft.EntityFrameworkCore.Metadata
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Linq
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Provides deterministic raw fact archive candidates for hot/cold archive tests.
module OperationsUsageArchiveTestData =

    /// Provides the owner used by archive usage facts.
    let ownerId = OwnerId.Parse("11111111-1111-1111-1111-111111111111")

    /// Provides the organization used by archive usage facts.
    let organizationId = OrganizationId.Parse("22222222-2222-2222-2222-222222222222")

    /// Provides the repository used by archive usage facts.
    let repositoryId = RepositoryId.Parse("33333333-3333-3333-3333-333333333333")

    /// Provides the storage pool used by archive usage facts.
    let storagePoolId = StoragePoolId "storage-pool-main"

    /// Creates the accepted raw payload bytes that later replay leaves will consume.
    let payload usageFactId = Encoding.UTF8.GetBytes($"{{\"usageFactId\":\"{usageFactId:D}\",\"payload\":\"exact broker bytes\"}}")

    /// Creates a valid usage fact whose raw JSON payload can be validated during archive replay.
    let usageFact usageFactId =
        UsageFact.RepositoryStorageBytesMinute(
            usageFactId,
            CorrelationId $"corr-{usageFactId}",
            ownerId,
            organizationId,
            repositoryId,
            storagePoolId,
            4096L,
            Instant.FromUtc(2026, 7, 4, 12, 34, 0)
        )

    /// Serializes a usage fact with the production JSON settings preserved in archive blobs.
    let usageFactPayload fact = JsonSerializer.SerializeToUtf8Bytes(fact, Constants.JsonSerializerOptions)

    /// Creates a raw usage fact archive candidate with deterministic scope and observed time.
    let candidate usageFactId archiveState rawPayload pointer =
        {
            UsageFactId = usageFactId
            RawPayload = rawPayload
            CorrelationId = CorrelationId $"corr-{usageFactId}"
            FactKind = UsageFactKind.RepositoryStorageBytesMinute
            OwnerId = ownerId
            OrganizationId = organizationId
            RepositoryId = repositoryId
            StoragePoolId = storagePoolId
            Quantity = 4096L
            ObservedAt = Instant.FromUtc(2026, 7, 4, 12, 34, 0)
            ArchiveState = archiveState
            ArchiveBlobName =
                pointer
                |> Option.map (fun value -> value.BlobName)
            ArchiveChecksumSha256Hex =
                pointer
                |> Option.map (fun value -> value.ChecksumSha256Hex)
            ArchiveByteLength =
                pointer
                |> Option.map (fun value -> value.ByteLength)
        }

/// Records archive store interactions without requiring SQL Server.
type private RecordingArchiveStore
    (
        candidates: RawUsageFactArchiveCandidate list,
        events: List<string>,
        ?rehydrateResults: Result<UsageFactId list, exn> list,
        ?afterRehydrate: RawUsageFactRehydrationItem -> bool -> unit,
        ?expiredCleanupResults: int list,
        ?completeResults: bool list,
        ?archivedCountOverride: int64
    ) =
    let markedPointers = ResizeArray<RawUsageFactArchivePointer>()
    let completedPointers = ResizeArray<RawUsageFactArchivePointer>()
    let recordedFailures = ResizeArray<UsageFactId * string>()
    let rehydratedPointers = ResizeArray<RawUsageFactArchivePointer>()
    let rehydrationExpiresAtValues = ResizeArray<Instant>()
    let rehydrateCancellationCanBeCanceled = ResizeArray<bool>()
    let rehydrateResults = Queue<Result<UsageFactId list, exn>>(defaultArg rehydrateResults [])
    let expiredCleanupResults = Queue<int>(defaultArg expiredCleanupResults [])
    let completeResults = Queue<bool>(defaultArg completeResults [])
    let expiredCleanupBatchSizes = ResizeArray<int>()
    let archivedPageBatchSizes = ResizeArray<int>()
    let afterRehydrate = defaultArg afterRehydrate (fun _ _ -> ())
    let mutable expiredCleanupCount = 0

    /// Returns Blob pointers recorded as verified in SQL.
    member _.MarkedPointers = markedPointers |> Seq.toList

    /// Returns Blob pointers completed by clearing the hot SQL payload.
    member _.CompletedPointers = completedPointers |> Seq.toList

    /// Returns row-scoped archive failures recorded for operator repair.
    member _.RecordedFailures = recordedFailures |> Seq.toList

    /// Returns Blob pointers restored for temporary support rehydration.
    member _.RehydratedPointers = rehydratedPointers |> Seq.toList

    /// Returns durable expiry values used to restore temporary payloads.
    member _.RehydrationExpiresAtValues = rehydrationExpiresAtValues |> Seq.toList

    /// Returns the number of expired lease cleanup passes requested by the archive processor.
    member _.ExpiredCleanupCount = expiredCleanupCount

    /// Returns bounded cleanup batch sizes requested from the archive store.
    member _.ExpiredCleanupBatchSizes = expiredCleanupBatchSizes |> Seq.toList

    /// Returns whether each rehydration SQL mutation received a cancelable caller token.
    member _.RehydrateCancellationCanBeCanceled = rehydrateCancellationCanBeCanceled |> Seq.toList

    /// Returns page sizes used while exploring archived rows.
    member _.ArchivedPageBatchSizes = archivedPageBatchSizes |> Seq.toList

    /// Returns archived candidates that match the support or replay query without reading Blob payloads.
    member _.MatchingArchivedCandidates(query: RawUsageFactArchiveQuery) =
        let matchesScope (candidate: RawUsageFactArchiveCandidate) =
            match query.Scope with
            | None -> true
            | Some scope ->
                (scope.OwnerId
                 |> Option.forall ((=) candidate.OwnerId))
                && (scope.OrganizationId
                    |> Option.forall ((=) candidate.OrganizationId))
                && (scope.RepositoryId
                    |> Option.forall ((=) candidate.RepositoryId))

        let matchesBounds (candidate: RawUsageFactArchiveCandidate) =
            (query.ObservedAfterUtc
             |> Option.forall (fun lowerBound -> candidate.ObservedAt >= lowerBound))
            && (query.ObservedBeforeUtc
                |> Option.forall (fun upperBound -> candidate.ObservedAt <= upperBound))

        let matchesCorrelationId (candidate: RawUsageFactArchiveCandidate) =
            query.CorrelationIds.IsEmpty
            || query.CorrelationIds
               |> List.exists ((=) candidate.CorrelationId)

        let matchesUsageFactId (candidate: RawUsageFactArchiveCandidate) =
            query.UsageFactIds.IsEmpty
            || query.UsageFactIds
               |> List.exists ((=) candidate.UsageFactId)

        candidates
        |> List.filter matchesScope
        |> List.filter matchesBounds
        |> List.filter matchesCorrelationId
        |> List.filter matchesUsageFactId

    interface IOperationsUsageArchiveStore with

        member _.ListArchiveCandidatesAsync(_observedBefore, _batchSize, _cancellationToken) =
            events.Add("list-candidates")
            Task.FromResult candidates

        member _.MarkArchiveVerifiedAsync(pointer, _cancellationToken) =
            events.Add("mark-verified")
            markedPointers.Add pointer
            Task.FromResult true

        member _.CompleteArchiveAsync(pointer, _cancellationToken) =
            events.Add("complete-archive")
            completedPointers.Add pointer
            let changed = if completeResults.Count > 0 then completeResults.Dequeue() else true
            Task.FromResult changed

        member _.RecordArchiveFailureAsync(usageFactId, reason, _cancellationToken) =
            events.Add("record-archive-failure")
            recordedFailures.Add((usageFactId, reason))
            Task.CompletedTask

        member this.ListArchivedUsageFactsAsync(query, after, batchSize, _cancellationToken) =
            events.Add("list-archived")
            archivedPageBatchSizes.Add batchSize

            let isAfterCursor (candidate: RawUsageFactArchiveCandidate) =
                match after with
                | None -> true
                | Some cursor ->
                    candidate.ObservedAt > cursor.ObservedAt
                    || (candidate.ObservedAt = cursor.ObservedAt
                        && candidate.UsageFactId > cursor.UsageFactId)

            let replayItems =
                this.MatchingArchivedCandidates query
                |> List.filter isAfterCursor
                |> List.sortBy (fun candidate -> candidate.ObservedAt, candidate.UsageFactId)
                |> List.truncate batchSize
                |> List.map (fun candidate ->
                    match candidate.ArchiveBlobName, candidate.ArchiveChecksumSha256Hex, candidate.ArchiveByteLength with
                    | Some blobName, Some checksum, Some byteLength ->
                        let pointer = { UsageFactId = candidate.UsageFactId; BlobName = blobName; ChecksumSha256Hex = checksum; ByteLength = byteLength }

                        {
                            UsageFactId = candidate.UsageFactId
                            CorrelationId = candidate.CorrelationId
                            FactKind = candidate.FactKind
                            OwnerId = candidate.OwnerId
                            OrganizationId = candidate.OrganizationId
                            RepositoryId = candidate.RepositoryId
                            StoragePoolId = candidate.StoragePoolId
                            Quantity = candidate.Quantity
                            ObservedAt = candidate.ObservedAt
                            Pointer = pointer
                        }
                    | _ -> failwith "Replay candidates require complete archive pointer authority.")

            Task.FromResult replayItems

        member this.CountArchivedUsageFactsAsync(query, _cancellationToken) =
            events.Add("count-archived")

            match archivedCountOverride with
            | Some value -> Task.FromResult value
            | None ->
                Task.FromResult(
                    this.MatchingArchivedCandidates(query)
                    |> List.length
                    |> int64
                )

        member _.RehydrateArchivedPayloadsAsync(items, expiresAt, cancellationToken) =
            events.Add("rehydrate")
            let itemArray = items |> List.toArray

            itemArray
            |> Array.iter (fun item -> Assert.That(item.RawPayload, Is.Not.Empty))

            rehydrationExpiresAtValues.Add expiresAt
            rehydrateCancellationCanBeCanceled.Add cancellationToken.CanBeCanceled

            let result =
                if rehydrateResults.Count > 0 then
                    rehydrateResults.Dequeue()
                else
                    Ok(
                        itemArray
                        |> Array.map (fun item -> item.Pointer.UsageFactId)
                        |> Array.toList
                    )

            match result with
            | Ok changedUsageFactIds ->
                let changedSet = HashSet<UsageFactId>(changedUsageFactIds)

                itemArray
                |> Array.iter (fun item ->
                    let changed = changedSet.Contains item.Pointer.UsageFactId

                    if changed then rehydratedPointers.Add item.Pointer

                    afterRehydrate item changed)

                Task.FromResult changedUsageFactIds
            | Error ex -> Task.FromException<UsageFactId list> ex

        member _.CleanupExpiredRehydratedPayloadsAsync(_expiresBefore, batchSize, _cancellationToken) =
            events.Add("cleanup-expired-rehydrated")
            expiredCleanupCount <- expiredCleanupCount + 1
            expiredCleanupBatchSizes.Add batchSize
            let cleaned = if expiredCleanupResults.Count > 0 then expiredCleanupResults.Dequeue() else 0
            Task.FromResult cleaned

/// Stores archive blobs in memory while preserving checksum and length verification semantics.
type private RecordingArchiveBlobStore(events: List<string>) =
    let blobs = Dictionary<string, byte array>()

    /// Inserts an existing blob for resume and corruption tests.
    member _.Put(blobName, content: byte array) = blobs[blobName] <- Array.copy content

    /// Returns whether a deterministic Blob name has been written.
    member _.Contains(blobName) = blobs.ContainsKey blobName

    /// Returns the stored bytes for a deterministic Blob name.
    member _.Content(blobName) = blobs[blobName] |> Array.copy

    /// Throws when stored bytes do not match a SQL archive pointer.
    member _.VerifyPointer(pointer: RawUsageFactArchivePointer) =
        match blobs.TryGetValue pointer.BlobName with
        | false, _ -> invalidOp $"Archive Blob '{pointer.BlobName}' is missing."
        | true, content ->
            if int64 content.Length <> pointer.ByteLength then
                invalidOp
                    $"Archive Blob '{pointer.BlobName}' length mismatch for UsageFactId '{pointer.UsageFactId}'. Expected {pointer.ByteLength}; actual {content.Length}."

            let checksum = OperationsUsageArchiveFormat.checksumSha256Hex content

            if checksum <> pointer.ChecksumSha256Hex then
                invalidOp $"Archive Blob '{pointer.BlobName}' checksum mismatch for UsageFactId '{pointer.UsageFactId}'."

    interface IOperationsUsageArchiveBlobStore with

        member this.WriteAndVerifyAsync(archiveBlob, _cancellationToken) =
            events.Add("write-verify-blob")

            match blobs.TryGetValue archiveBlob.Pointer.BlobName with
            | true, existing when
                Convert.ToBase64String(existing)
                <> Convert.ToBase64String(archiveBlob.Content)
                ->
                Task.FromException(InvalidOperationException("Archive Blob already exists with different content."))
            | _ ->
                blobs[archiveBlob.Pointer.BlobName] <- Array.copy archiveBlob.Content
                this.VerifyPointer archiveBlob.Pointer
                Task.CompletedTask

        member this.VerifyAsync(pointer, _cancellationToken) =
            events.Add("verify-blob")

            try
                this.VerifyPointer pointer
                Task.CompletedTask
            with
            | ex -> Task.FromException ex

        member this.DownloadVerifiedAsync(pointer, _cancellationToken) =
            events.Add("download-verified")

            try
                this.VerifyPointer pointer
                Task.FromResult(this.Content pointer.BlobName)
            with
            | ex -> Task.FromException<byte array> ex

/// Records replay persistence calls and simulates UsageFactId idempotency.
type private RecordingArchiveReplayStore(events: List<string>) =
    let replayed = HashSet<UsageFactId>()

    /// Returns the number of unique usage facts accepted by replay.
    member _.AcceptedCount = replayed.Count

    interface IOperationsUsageArchiveReplayStore with

        member _.ReplayArchivedUsageFactAsync(fact, rawPayload, pointer, _cancellationToken) =
            events.Add("replay-store")
            Assert.That(rawPayload, Is.Not.Empty)
            Assert.That(pointer.UsageFactId, Is.EqualTo(fact.UsageFactId))

            let status =
                if replayed.Add fact.UsageFactId then
                    UsageFactPersistenceStatus.Accepted
                else
                    UsageFactPersistenceStatus.AlreadyProcessed

            let result = { Status = status; UsageFactId = fact.UsageFactId; Aggregate = None }

            Task.FromResult(Ok result)

/// Provides deterministic time for rehydration expiry tests.
type private FixedClock(now: Instant) =

    interface IClock with

        member _.GetCurrentInstant() = now

/// Captures structured log messages so redaction tests can inspect rendered and property values.
type private ArchiveRecordingLogger<'T>() =
    let entries = ResizeArray<string>()

    /// Returns formatted log messages and structured property values captured by this logger.
    member _.Entries = entries |> Seq.toList

    interface ILogger<'T> with

        member _.BeginScope<'TState>(_state: 'TState) =
            { new IDisposable with
                member _.Dispose() = ()
            }

        member _.IsEnabled(_logLevel: LogLevel) = true

        member _.Log<'TState>(logLevel: LogLevel, _eventId: EventId, state: 'TState, ``exception``: exn, formatter: Func<'TState, exn, string>) =
            let formatted = formatter.Invoke(state, ``exception``)
            entries.Add($"{logLevel}: {formatted}")

            match box state with
            | :? IEnumerable<KeyValuePair<string, obj>> as properties ->
                for property in properties do
                    if property.Key <> "{OriginalFormat}" then
                        entries.Add($"{property.Key}={property.Value}")
            | _ -> ()

/// Records archive schema initialization attempts without opening SQL connections.
type private RecordingSchemaInitializer(events: List<string>, ?failure: exn) =
    let invoked = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    /// Completes when the archive worker reaches the schema gate.
    member _.Invoked = invoked.Task

    interface IOperationsUsageSchemaInitializer with

        member _.EnsureCreatedAsync(_cancellationToken) =
            events.Add("schema")
            invoked.TrySetResult() |> ignore

            match failure with
            | Some ex -> Task.FromException ex
            | None -> Task.CompletedTask

/// Covers hot/cold archive layout, Blob verification, SQL state ordering, and migration shape.
[<TestFixture>]
type OperationsUsageArchiveTests() =

    /// Creates the archive processor with fake SQL and Blob dependencies.
    let createProcessor archiveStore blobStore =
        OperationsUsageArchiveProcessor(
            archiveStore,
            blobStore,
            NullLogger<OperationsUsageArchiveProcessor>
                .Instance
        )

    /// Creates the replay processor with fake SQL, Blob, and replay dependencies.
    let createReplayProcessor archiveStore blobStore replayStore =
        OperationsUsageArchiveReplayProcessor(
            archiveStore,
            blobStore,
            replayStore,
            NullLogger<OperationsUsageArchiveReplayProcessor>
                .Instance
        )

    /// Creates the scoped rehydration processor with deterministic time.
    let createRehydrationProcessor archiveStore blobStore clock =
        OperationsUsageRehydrationProcessor(
            archiveStore,
            blobStore,
            clock,
            NullLogger<OperationsUsageRehydrationProcessor>
                .Instance
        )

    /// Provides the fully scoped archive query required for support rehydration.
    let rehydrationQuery observedAfter observedBefore =
        {
            Scope =
                Some
                    {
                        OwnerId = Some OperationsUsageArchiveTestData.ownerId
                        OrganizationId = Some OperationsUsageArchiveTestData.organizationId
                        RepositoryId = Some OperationsUsageArchiveTestData.repositoryId
                    }
            ObservedAfterUtc = observedAfter
            ObservedBeforeUtc = observedBefore
            CorrelationIds = []
            UsageFactIds = []
        }

    /// Creates a valid support rehydration request with the approved predicate contract.
    let rehydrationRequest (now: Instant) =
        {
            Query = rehydrationQuery (Some(Instant.FromUtc(2026, 7, 4, 0, 0, 0))) None
            DryRun = false
            RequestedBy = "support@example.test"
            Reason = "Investigate archived customer support case"
            ExpiresAt = Some(now.Plus(Duration.FromHours 1.0))
        }

    /// Creates an archived replay candidate with a matching Blob payload for rehydration tests.
    let archivedCandidate usageFactId (observedAt: Instant) =
        let fact = { OperationsUsageArchiveTestData.usageFact usageFactId with ObservedAt = observedAt }

        let rawPayload = OperationsUsageArchiveTestData.usageFactPayload fact

        let hotCandidate =
            { OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None with ObservedAt = observedAt }

        let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob hotCandidate rawPayload

        { hotCandidate with
            ArchiveState = RawUsageFactArchiveState.Archived
            RawPayload = None
            ArchiveBlobName = Some archiveBlob.Pointer.BlobName
            ArchiveChecksumSha256Hex = Some archiveBlob.Pointer.ChecksumSha256Hex
            ArchiveByteLength = Some archiveBlob.Pointer.ByteLength
        },
        archiveBlob

    /// Reads repository source text for last-resort SQL-shape regression tests.
    let sourceText relativePath =
        let rec findRepoRoot (directory: DirectoryInfo) =
            if isNull directory then
                failwith "Could not locate Grace repository root for source-shape test."
            elif
                File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(directory.FullName, "src"))
            then
                directory.FullName
            else
                findRepoRoot directory.Parent

        let root = findRepoRoot (DirectoryInfo(TestContext.CurrentContext.TestDirectory))
        File.ReadAllText(Path.Combine(root, relativePath))

    /// Creates archive worker configuration from exact setting names so startup validation paths can be tested.
    let archiveConfiguration (settings: KeyValuePair<string, string> seq) =
        ConfigurationBuilder()
            .AddInMemoryCollection(Dictionary<string, string>(settings))
            .Build()

    /// Decompresses gzip JSONL bytes for deterministic archive content assertions.
    let decompressGzip (content: byte array) =
        use input = new MemoryStream(content)
        use gzip = new GZipStream(input, CompressionMode.Decompress)
        use output = new MemoryStream()
        gzip.CopyTo output
        output.ToArray()

    /// Generates the Operations migration script without connecting to SQL Server.
    let migrationScript () =
        use context =
            OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsArchiveMigrationScript;Integrated Security=true;"

        let migrator = context.GetService<IMigrator>()
        migrator.GenerateScript(options = MigrationsSqlGenerationOptions.Idempotent)

    /// Reads the EF entity metadata that future migrations use for raw fact archive schema drift.
    let rawFactEntityType () : IEntityType =
        use context = OperationsDbContextFactory.create "Server=(localdb)\\MSSQLLocalDB;Database=GraceOperationsArchiveMigrationModel;Integrated Security=true;"

        context.Model.FindEntityType(typeof<RawUsageFactEntity>)

    /// Creates archive worker settings for hosted-service ordering tests without opening Blob storage.
    let archiveWorkerSettings () =
        {
            BlobConnectionString = Some "UseDevelopmentStorage=true"
            BlobServiceUri = None
            BlobContainerName = "usage-facts"
            HotRetentionDays = 90
            BatchSize = 10
            PollInterval = TimeSpan.FromSeconds(30.0)
        }

    /// Creates ingestion worker settings for service-registration tests without opening external resources.
    let operationsWorkerSettings () =
        {
            TopicName = "operational-facts"
            SubscriptionName = OperationsWorkerSettings.DefaultProcessorSubscriptionName
            SqlConnectionString = "Server=tcp:sql.example.net;Database=GraceOperations;Authentication=Active Directory Default;"
            ServiceBusConnectionString = Some "Endpoint=sb://operations.example.test/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=fake"
            ServiceBusFullyQualifiedNamespace = None
            SchemaBootstrapMode = OperationsUsageSchemaBootstrapMode.TargetDatabaseOnly
            MaxConcurrentCalls = 1
            PrefetchCount = 0
        }

    /// Verifies the cleanup processor drains expired temporary rehydration payloads in bounded batches.
    [<Test>]
    member _.TemporaryHotCleanupProcessorDrainsExpiredRehydrationsInBoundedBatches() =
        task {
            let events = List<string>()
            let archiveStore = RecordingArchiveStore([], events, expiredCleanupResults = [ 1000; 1000; 5 ])

            let processor =
                OperationsUsageTemporaryHotCleanupProcessor(
                    archiveStore,
                    FixedClock(Instant.FromUtc(2026, 7, 7, 11, 0, 0)),
                    NullLogger<OperationsUsageTemporaryHotCleanupProcessor>
                        .Instance
                )

            let! cleaned = processor.CleanupExpiredAsync(OperationsUsageSql.TemporaryHotCleanupBatchSize, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(cleaned, Is.EqualTo(2005))
                    Assert.That(archiveStore.ExpiredCleanupCount, Is.EqualTo(3))
                    Assert.That(String.Join(",", archiveStore.ExpiredCleanupBatchSizes), Is.EqualTo("1000,1000,1000"))
                    Assert.That(String.Join("|", events), Is.EqualTo("cleanup-expired-rehydrated|cleanup-expired-rehydrated|cleanup-expired-rehydrated")))
            )
        }

    /// Verifies SQL-owned temporary-hot cleanup is registered even when Blob archive settings are absent.
    [<Test>]
    member _.TemporaryHotCleanupRegistrationDoesNotRequireArchiveBlobSettings() =
        let services = ServiceCollection()

        Grace.Operations.Worker.Program.configureServices services (operationsWorkerSettings ()) None

        let hostedServiceTypes =
            services
                .Where(fun descriptor -> descriptor.ServiceType = typeof<IHostedService>)
                .Select(fun descriptor -> descriptor.ImplementationType)
            |> Seq.toList

        Assert.Multiple(
            Action (fun () ->
                Assert.That(hostedServiceTypes, Does.Contain(typeof<OperationsUsageTemporaryHotCleanupWorkerService>))

                Assert.That(hostedServiceTypes, Does.Not.Contain(typeof<OperationsUsageArchiveWorkerService>)))
        )

    /// Verifies the archive hosted service does not query archive state before schema migrations succeed.
    [<Test>]
    member _.ArchiveWorkerSchemaFailureDoesNotQueryArchiveState() =
        task {
            let events = List<string>()
            let schema = RecordingSchemaInitializer(events, InvalidOperationException("schema unavailable"))
            let archiveStore = RecordingArchiveStore([], events)
            let blobStore = RecordingArchiveBlobStore(events)
            let processor = createProcessor archiveStore blobStore
            let readiness = OperationsUsageReadinessState()

            let service =
                new OperationsUsageArchiveWorkerService(
                    archiveWorkerSettings (),
                    schema,
                    processor,
                    readiness :> IOperationsUsageReadinessRecorder,
                    NullLogger<OperationsUsageArchiveWorkerService>
                        .Instance
                )

            do!
                (service :> IHostedService)
                    .StartAsync(CancellationToken.None)

            do! schema.Invoked.WaitAsync(TimeSpan.FromSeconds(5.0))

            use stopCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5.0))

            do!
                (service :> IHostedService)
                    .StopAsync(stopCancellation.Token)

            let snapshot =
                (readiness :> IOperationsUsageReadinessProbe)
                    .GetSnapshot()

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(String.Join("|", events), Is.EqualTo("schema"))
                    Assert.That(archiveStore.ExpiredCleanupCount, Is.EqualTo(0))
                    Assert.That(snapshot.Status, Is.EqualTo(OperationsUsageReadinessStatus.NotReady))
                    Assert.That(snapshot.DependencyFailure.Value, Does.Contain("Operations usage schema initialization failed (InvalidOperationException).")))
            )
        }

    /// Verifies deterministic gzip JSONL output and pointer authority for a hot raw usage fact.
    [<Test>]
    member _.ArchiveFormatIsDeterministicCompressedJsonlWithChecksumAuthority() =
        let usageFactId = Guid.Parse("40404040-4040-4040-8040-404040404040")
        let rawPayload = OperationsUsageArchiveTestData.payload usageFactId
        let candidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None

        let first = OperationsUsageArchiveFormat.createArchiveBlob candidate rawPayload
        let second = OperationsUsageArchiveFormat.createArchiveBlob candidate rawPayload

        let jsonl =
            first.Content
            |> decompressGzip
            |> Encoding.UTF8.GetString

        using (JsonDocument.Parse(jsonl)) (fun document ->
            let root = document.RootElement

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(first.Pointer.BlobName, Is.EqualTo(second.Pointer.BlobName))
                    Assert.That(Convert.ToBase64String(first.Content), Is.EqualTo(Convert.ToBase64String(second.Content)))
                    Assert.That(first.Pointer.ChecksumSha256Hex, Is.EqualTo(second.Pointer.ChecksumSha256Hex))
                    Assert.That(first.Pointer.ByteLength, Is.EqualTo(int64 first.Content.Length))
                    Assert.That(first.Pointer.BlobName, Does.Contain("usage-facts/v1/observedYear=2026/observedMonth=07"))
                    Assert.That(first.Pointer.BlobName, Does.Contain($"usageFactId={usageFactId:D}.jsonl.gz"))

                    Assert.That(
                        root
                            .GetProperty("archiveSchemaVersion")
                            .GetInt32(),
                        Is.EqualTo(OperationsUsageArchiveFormat.ArchiveSchemaVersion)
                    )

                    Assert.That(root.GetProperty("usageFactId").GetString(), Is.EqualTo(string usageFactId))

                    Assert.That(
                        Convert.ToBase64String(
                            root
                                .GetProperty("rawPayloadBase64")
                                .GetBytesFromBase64()
                        ),
                        Is.EqualTo(Convert.ToBase64String(rawPayload))
                    ))
            ))

    /// Verifies hot payload cleanup happens after Blob write/verify and SQL pointer verification.
    [<Test>]
    member _.ArchiveBatchWritesBlobRecordsPointerThenClearsHotPayload() =
        task {
            let events = List<string>()
            let usageFactId = Guid.Parse("41414141-4141-4141-8141-414141414141")
            let rawPayload = OperationsUsageArchiveTestData.payload usageFactId
            let candidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None
            let archiveStore = RecordingArchiveStore([ candidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            let processor = createProcessor archiveStore blobStore

            let! archived = processor.ArchiveBatchAsync(Instant.FromUtc(2026, 8, 1, 0, 0), 10, CancellationToken.None)
            let pointer = archiveStore.CompletedPointers[0]

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(archived, Is.EqualTo(1))

                    Assert.That(String.Join("|", events), Is.EqualTo("list-candidates|write-verify-blob|mark-verified|verify-blob|complete-archive"))

                    Assert.That(archiveStore.MarkedPointers[0], Is.EqualTo(pointer))
                    Assert.That(blobStore.Contains(pointer.BlobName), Is.True))
            )
        }

    /// Verifies a retry can resume after Blob authority was recorded but before hot SQL payload cleanup.
    [<Test>]
    member _.ArchiveBatchResumesVerifiedPointerWithoutRewritingBlob() =
        task {
            let events = List<string>()
            let usageFactId = Guid.Parse("42424242-4242-4242-8242-424242424242")
            let rawPayload = OperationsUsageArchiveTestData.payload usageFactId
            let hotCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None
            let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob hotCandidate rawPayload

            let verifiedCandidate =
                OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.ArchiveVerified None (Some archiveBlob.Pointer)

            let archiveStore = RecordingArchiveStore([ verifiedCandidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(archiveBlob.Pointer.BlobName, archiveBlob.Content)
            let processor = createProcessor archiveStore blobStore

            let! archived = processor.ArchiveBatchAsync(Instant.FromUtc(2026, 8, 1, 0, 0), 10, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(archived, Is.EqualTo(1))

                    Assert.That(String.Join("|", events), Is.EqualTo("list-candidates|verify-blob|complete-archive"))

                    Assert.That(archiveStore.MarkedPointers, Is.Empty)
                    Assert.That(archiveStore.CompletedPointers[0], Is.EqualTo(archiveBlob.Pointer)))
            )
        }

    /// Verifies a temporary-hot archived row is treated as a harmless completed archive race.
    [<Test>]
    member _.ArchiveBatchAcceptsAlreadyCompletedArchivedPointerWithoutThrowing() =
        task {
            let events = List<string>()
            let usageFactId = Guid.Parse("62626262-6262-6262-8626-626262626262")
            let rawPayload = OperationsUsageArchiveTestData.payload usageFactId
            let hotCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None
            let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob hotCandidate rawPayload

            let archivedCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Archived None (Some archiveBlob.Pointer)

            let archiveStore = RecordingArchiveStore([ archivedCandidate ], events, completeResults = [ false ])
            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(archiveBlob.Pointer.BlobName, archiveBlob.Content)
            let processor = createProcessor archiveStore blobStore

            let! archived = processor.ArchiveBatchAsync(Instant.FromUtc(2026, 8, 1, 0, 0), 10, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(archived, Is.EqualTo(1))
                    Assert.That(String.Join("|", events), Is.EqualTo("list-candidates|verify-blob|complete-archive"))
                    Assert.That(archiveStore.MarkedPointers, Is.Empty)
                    Assert.That(archiveStore.CompletedPointers[0], Is.EqualTo(archiveBlob.Pointer)))
            )
        }

    /// Verifies a missing verified Blob records row-scoped evidence and does not block later safe candidates.
    [<Test>]
    member _.ArchiveBatchRecordsVerifiedBlobFailureAndContinuesPastBadRow() =
        task {
            let events = List<string>()
            let badUsageFactId = Guid.Parse("43434343-4343-4343-8343-434343434343")
            let safeUsageFactId = Guid.Parse("43434343-4343-4343-8343-434343434344")
            let badRawPayload = OperationsUsageArchiveTestData.payload badUsageFactId
            let badHotCandidate = OperationsUsageArchiveTestData.candidate badUsageFactId RawUsageFactArchiveState.Hot (Some badRawPayload) None
            let badArchiveBlob = OperationsUsageArchiveFormat.createArchiveBlob badHotCandidate badRawPayload

            let verifiedCandidate =
                OperationsUsageArchiveTestData.candidate badUsageFactId RawUsageFactArchiveState.ArchiveVerified None (Some badArchiveBlob.Pointer)

            let safeRawPayload = OperationsUsageArchiveTestData.payload safeUsageFactId
            let safeCandidate = OperationsUsageArchiveTestData.candidate safeUsageFactId RawUsageFactArchiveState.Hot (Some safeRawPayload) None
            let safeArchiveBlob = OperationsUsageArchiveFormat.createArchiveBlob safeCandidate safeRawPayload
            let archiveStore = RecordingArchiveStore([ verifiedCandidate; safeCandidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(safeArchiveBlob.Pointer.BlobName, safeArchiveBlob.Content)
            let processor = createProcessor archiveStore blobStore

            let! archived = processor.ArchiveBatchAsync(Instant.FromUtc(2026, 8, 1, 0, 0), 10, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(archived, Is.EqualTo(1))

                    Assert.That(
                        String.Join("|", events),
                        Is.EqualTo("list-candidates|verify-blob|record-archive-failure|write-verify-blob|mark-verified|verify-blob|complete-archive")
                    )

                    Assert.That(archiveStore.RecordedFailures |> List.length, Is.EqualTo(1))
                    Assert.That(fst archiveStore.RecordedFailures[0], Is.EqualTo(badUsageFactId))
                    Assert.That(snd archiveStore.RecordedFailures[0], Does.Contain("is missing"))
                    Assert.That(archiveStore.CompletedPointers |> List.length, Is.EqualTo(1))
                    Assert.That(archiveStore.CompletedPointers[0], Is.EqualTo(safeArchiveBlob.Pointer)))
            )
        }

    /// Verifies a hot row without payload bytes records failure evidence and does not block later safe candidates.
    [<Test>]
    member _.ArchiveBatchRecordsMissingPayloadFailureAndContinuesPastBadRow() =
        task {
            let events = List<string>()
            let badUsageFactId = Guid.Parse("44444444-4444-4444-8444-444444444444")
            let safeUsageFactId = Guid.Parse("44444444-4444-4444-8444-444444444445")
            let badCandidate = OperationsUsageArchiveTestData.candidate badUsageFactId RawUsageFactArchiveState.Hot None None
            let safeRawPayload = OperationsUsageArchiveTestData.payload safeUsageFactId
            let safeCandidate = OperationsUsageArchiveTestData.candidate safeUsageFactId RawUsageFactArchiveState.Hot (Some safeRawPayload) None
            let safeArchiveBlob = OperationsUsageArchiveFormat.createArchiveBlob safeCandidate safeRawPayload
            let archiveStore = RecordingArchiveStore([ badCandidate; safeCandidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(safeArchiveBlob.Pointer.BlobName, safeArchiveBlob.Content)
            let processor = createProcessor archiveStore blobStore

            let! archived = processor.ArchiveBatchAsync(Instant.FromUtc(2026, 8, 1, 0, 0), 10, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(archived, Is.EqualTo(1))

                    Assert.That(
                        String.Join("|", events),
                        Is.EqualTo("list-candidates|record-archive-failure|write-verify-blob|mark-verified|verify-blob|complete-archive")
                    )

                    Assert.That(archiveStore.RecordedFailures |> List.length, Is.EqualTo(1))
                    Assert.That(fst archiveStore.RecordedFailures[0], Is.EqualTo(badUsageFactId))
                    Assert.That(snd archiveStore.RecordedFailures[0], Does.Contain("has no raw payload"))
                    Assert.That(archiveStore.CompletedPointers |> List.length, Is.EqualTo(1))
                    Assert.That(archiveStore.CompletedPointers[0], Is.EqualTo(safeArchiveBlob.Pointer)))
            )
        }

    /// Verifies archive replay validates Blob authority and remains idempotent by UsageFactId.
    [<Test>]
    member _.ArchiveReplayValidatesBlobAuthorityAndIsIdempotent() =
        task {
            let events = List<string>()
            let usageFactId = Guid.Parse("45454545-4545-4545-8545-454545454545")
            let fact = OperationsUsageArchiveTestData.usageFact usageFactId
            let rawPayload = OperationsUsageArchiveTestData.usageFactPayload fact
            let hotCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None
            let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob hotCandidate rawPayload

            let replayCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Archived None (Some archiveBlob.Pointer)

            let archiveStore = RecordingArchiveStore([ replayCandidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(archiveBlob.Pointer.BlobName, archiveBlob.Content)
            let replayStore = RecordingArchiveReplayStore(events)
            let processor = createReplayProcessor archiveStore blobStore replayStore

            let! first = processor.ReplayBatchAsync(None, 10, CancellationToken.None)
            let! second = processor.ReplayBatchAsync(None, 10, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(first.Examined, Is.EqualTo(1))
                    Assert.That(first.Accepted, Is.EqualTo(1))
                    Assert.That(first.AlreadyProcessed, Is.EqualTo(0))
                    Assert.That(second.Examined, Is.EqualTo(1))
                    Assert.That(second.Accepted, Is.EqualTo(0))
                    Assert.That(second.AlreadyProcessed, Is.EqualTo(1))
                    Assert.That(replayStore.AcceptedCount, Is.EqualTo(1)))
            )
        }

    /// Verifies archive replay advances its SQL cursor instead of rereading the same first page.
    [<Test>]
    member _.ArchiveReplayPagesPastFirstSqlBatch() =
        task {
            let events = List<string>()

            let usageFactIds =
                [
                    Guid.Parse("45454545-4545-4545-8545-454545454546")
                    Guid.Parse("45454545-4545-4545-8545-454545454547")
                    Guid.Parse("45454545-4545-4545-8545-454545454548")
                ]

            let createReplayCandidate usageFactId =
                let fact = OperationsUsageArchiveTestData.usageFact usageFactId
                let rawPayload = OperationsUsageArchiveTestData.usageFactPayload fact
                let hotCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None
                let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob hotCandidate rawPayload
                OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Archived None (Some archiveBlob.Pointer), archiveBlob

            let replayCandidates, archiveBlobs =
                usageFactIds
                |> List.map createReplayCandidate
                |> List.unzip

            let archiveStore = RecordingArchiveStore(replayCandidates, events)
            let blobStore = RecordingArchiveBlobStore(events)

            archiveBlobs
            |> List.iter (fun archiveBlob -> blobStore.Put(archiveBlob.Pointer.BlobName, archiveBlob.Content))

            let replayStore = RecordingArchiveReplayStore(events)
            let processor = createReplayProcessor archiveStore blobStore replayStore

            let! result = processor.ReplayBatchAsync(None, 1, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(result.Examined, Is.EqualTo(3))
                    Assert.That(result.Accepted, Is.EqualTo(3))
                    Assert.That(result.AlreadyProcessed, Is.EqualTo(0))
                    Assert.That(replayStore.AcceptedCount, Is.EqualTo(3))

                    Assert.That(
                        events
                        |> Seq.filter ((=) "list-archived")
                        |> Seq.length,
                        Is.EqualTo(4)
                    ))
            )
        }

    /// Verifies replay fails loudly when Blob checksum, byte length, or UsageFactId authority disagrees.
    [<Test>]
    member _.ArchiveReplayRejectsCorruptArchiveAuthority() =
        task {
            let usageFactId = Guid.Parse("46464646-4646-4646-8646-464646464646")
            let fact = OperationsUsageArchiveTestData.usageFact usageFactId
            let rawPayload = OperationsUsageArchiveTestData.usageFactPayload fact
            let hotCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None
            let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob hotCandidate rawPayload

            let runReplayWithPointer (pointer: RawUsageFactArchivePointer) =
                task {
                    let events = List<string>()
                    let replayCandidate = OperationsUsageArchiveTestData.candidate pointer.UsageFactId RawUsageFactArchiveState.Archived None (Some pointer)
                    let archiveStore = RecordingArchiveStore([ replayCandidate ], events)
                    let blobStore = RecordingArchiveBlobStore(events)
                    blobStore.Put(pointer.BlobName, archiveBlob.Content)
                    let replayStore = RecordingArchiveReplayStore(events)
                    let processor = createReplayProcessor archiveStore blobStore replayStore
                    let mutable message = None

                    try
                        let! _ = processor.ReplayBatchAsync(None, 10, CancellationToken.None)
                        Assert.Fail("Corrupt archive authority should stop replay.")
                    with
                    | :? InvalidOperationException as ex -> message <- Some ex.Message

                    return message.Value
                }

            let checksumPointer: RawUsageFactArchivePointer =
                { archiveBlob.Pointer with ChecksumSha256Hex = String.replicate OperationsUsageSql.ArchiveChecksumSha256HexLength "0" }

            let lengthPointer: RawUsageFactArchivePointer = { archiveBlob.Pointer with ByteLength = archiveBlob.Pointer.ByteLength + 1L }

            let usageFactIdPointer: RawUsageFactArchivePointer = { archiveBlob.Pointer with UsageFactId = Guid.Parse("47474747-4747-4747-8747-474747474747") }

            let! checksumMessage = runReplayWithPointer checksumPointer
            let! lengthMessage = runReplayWithPointer lengthPointer
            let! usageFactIdMessage = runReplayWithPointer usageFactIdPointer

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(checksumMessage, Does.Contain("checksum mismatch"))
                    Assert.That(lengthMessage, Does.Contain("length mismatch"))
                    Assert.That(usageFactIdMessage, Does.Contain("UsageFactId mismatch")))
            )
        }

    /// Verifies replay SQL never receives raw payload bytes for an insert that stores the archived pointer only.
    [<Test>]
    member _.ReplayInsertSqlDoesNotReferenceRawPayloadParameter() =
        Assert.That(OperationsUsageSql.TryInsertReplayedArchivedRawUsageFact, Does.Not.Contain("@RawPayload"))

    /// Verifies replay scans block instead of advancing the cursor past a locked lower-key row.
    [<Test>]
    member _.ReplayScanSqlDoesNotUseReadPast() =
        Assert.Multiple(
            Action (fun () ->
                Assert.That(OperationsUsageSql.SelectArchivedRawUsageFactsForReplay, Does.Not.Contain("READPAST"))
                Assert.That(OperationsUsageSql.SelectArchivedRawUsageFactsForReplay, Does.Contain("WITH (READCOMMITTEDLOCK)")))
        )

    /// Verifies support rehydration stores only durable expiry state and cleanup runs in bounded batches.
    [<Test>]
    member _.RehydrationSqlUsesExpiryOnlyRestoreAndBoundedCleanup() =
        Assert.Multiple(
            Action (fun () ->
                Assert.That(OperationsUsageSql.DeclareRehydratedRawUsageFactBatch, Does.Contain("@RehydrationRows table"))
                Assert.That(OperationsUsageSql.RehydrateArchivedRawUsageFactPayloadBatch, Does.Contain("RehydrationExpiresAtUtc ="))

                Assert.That(OperationsUsageSql.RehydrateArchivedRawUsageFactPayloadBatch, Does.Contain("RehydrationExpiresAtUtc = @RehydrationExpiresAtUtc"))

                Assert.That(OperationsUsageSql.RehydrateArchivedRawUsageFactPayloadBatch, Does.Not.Contain("THEN target.RehydrationExpiresAtUtc"))
                Assert.That(OperationsUsageSql.RehydrateArchivedRawUsageFactPayloadBatch, Does.Contain("OUTPUT inserted.UsageFactId"))
                Assert.That(OperationsUsageSql.RehydrateArchivedRawUsageFactPayloadBatch, Does.Contain("INNER JOIN @RehydrationRows AS source"))
                Assert.That(OperationsUsageSql.RehydrateArchivedRawUsageFactPayloadBatch, Does.Not.Contain("RehydrationLeaseId"))
                Assert.That(OperationsUsageSql.RehydrateArchivedRawUsageFactPayloadBatch, Does.Not.Contain("RehydrationRequestedBy"))
                Assert.That(OperationsUsageSql.CleanupExpiredRehydratedRawUsageFactPayloads, Does.Contain("UPDATE TOP (@BatchSize)"))
                Assert.That(OperationsUsageSql.CleanupExpiredRehydratedRawUsageFactPayloads, Does.Contain("RehydrationExpiresAtUtc = NULL"))
                Assert.That(OperationsUsageSql.CleanupExpiredRehydratedRawUsageFactPayloads, Does.Contain("RehydrationExpiresAtUtc <= @ExpiresBeforeUtc"))
                Assert.That(OperationsUsageSql.CompleteRawUsageFactArchive, Does.Contain("RawPayload IS NULL OR RehydrationExpiresAtUtc IS NOT NULL")))
        )

    /// Verifies archive failure state is durable on failure and cleared by later successful retry transitions.
    [<Test>]
    member _.ArchiveFailureSqlRecordsRetirementEvidenceAndSuccessfulRetryClearsIt() =
        Assert.Multiple(
            Action (fun () ->
                Assert.That(OperationsUsageSql.RecordRawUsageFactArchiveFailure, Does.Contain("LastArchiveFailureReason = @LastArchiveFailureReason"))
                Assert.That(OperationsUsageSql.RecordRawUsageFactArchiveFailure, Does.Contain("ArchiveFailureCount = ArchiveFailureCount + 1"))
                Assert.That(OperationsUsageSql.RecordRawUsageFactArchiveFailure, Does.Contain("ArchiveRetiredAtUtc"))
                Assert.That(OperationsUsageSql.SelectRawUsageFactsForArchive, Does.Contain("AND ArchiveRetiredAtUtc IS NULL"))
                Assert.That(OperationsUsageSql.MarkRawUsageFactArchiveVerified, Does.Contain("LastArchiveFailureReason = NULL"))
                Assert.That(OperationsUsageSql.MarkRawUsageFactArchiveVerified, Does.Contain("ArchiveFailureCount = 0"))
                Assert.That(OperationsUsageSql.CompleteRawUsageFactArchive, Does.Contain("LastArchiveFailureReason = NULL"))
                Assert.That(OperationsUsageSql.CompleteRawUsageFactArchive, Does.Contain("ArchiveFailureCount = 0")))
        )

    /// Verifies chunked rehydration uses one SQL transaction instead of committing each batch independently.
    [<Test>]
    member _.RehydrationDataLayerRunsChunkedBatchesInOneTransaction() =
        let source = sourceText "src/Grace.Operations/Grace.Operations.Data/OperationsData.fs"
        let rehydrateIndex = source.IndexOf("member _.RehydrateArchivedPayloadsAsync", StringComparison.Ordinal)
        let cleanupIndex = source.IndexOf("member _.CleanupExpiredRehydratedPayloadsAsync", StringComparison.Ordinal)

        Assert.That(rehydrateIndex, Is.GreaterThanOrEqualTo(0))
        Assert.That(cleanupIndex, Is.GreaterThan(rehydrateIndex))

        let rehydrateSection = source.Substring(rehydrateIndex, cleanupIndex - rehydrateIndex)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(rehydrateSection, Does.Contain("BeginTransactionAsync"))
                Assert.That(rehydrateSection, Does.Contain("CommitAsync"))
                Assert.That(rehydrateSection, Does.Contain("rollbackRehydrationIgnoringFailuresAsync transaction"))
                Assert.That(rehydrateSection, Does.Contain("executeRehydrationPayloadBatchAsync connection transaction"))
                Assert.That(source, Does.Contain("do! transaction.RollbackAsync CancellationToken.None")))
        )

    /// Verifies support rehydration requires scope, observed bounds, local audit proof, and durable expiry persistence.
    [<Test>]
    member _.RehydrationIsScopedByPredicateAuditedAndPersistsExpiry() =
        task {
            let events = List<string>()
            let firstUsageFactId = Guid.Parse("48484848-4848-4848-8848-484848484848")
            let secondUsageFactId = Guid.Parse("49494949-4949-4949-8949-494949494949")

            let createReplayCandidate usageFactId =
                let fact = OperationsUsageArchiveTestData.usageFact usageFactId
                let rawPayload = OperationsUsageArchiveTestData.usageFactPayload fact
                let hotCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None
                let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob hotCandidate rawPayload
                OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Archived None (Some archiveBlob.Pointer), archiveBlob

            let firstCandidate, firstBlob = createReplayCandidate firstUsageFactId
            let secondCandidate, secondBlob = createReplayCandidate secondUsageFactId
            let archiveStore = RecordingArchiveStore([ firstCandidate; secondCandidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(firstBlob.Pointer.BlobName, firstBlob.Content)
            blobStore.Put(secondBlob.Pointer.BlobName, secondBlob.Content)

            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)

            let request =
                { rehydrationRequest now with
                    Query = { rehydrationQuery (Some firstCandidate.ObservedAt) (Some firstCandidate.ObservedAt) with UsageFactIds = [ firstUsageFactId ] }
                }

            let! rehydrated = processor.RehydrateAsync(request, CancellationToken.None)

            let result =
                match rehydrated with
                | Ok value -> value
                | Error errors -> failwith (String.Join("; ", errors))

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(result.AuditEntries |> List.length, Is.EqualTo(1))
                    Assert.That(result.AuditEntries[0].UsageFactId, Is.EqualTo(firstUsageFactId))
                    Assert.That(result.AuditEntries[0].RequestedBy, Is.EqualTo(request.RequestedBy))
                    Assert.That(result.AuditEntries[0].Reason, Is.EqualTo(request.Reason))
                    Assert.That(result.AuditEntries[0].ExpiresAt, Is.EqualTo(request.ExpiresAt.Value))
                    Assert.That(result.MatchingArchivedFactCount, Is.EqualTo(1))
                    Assert.That(archiveStore.RehydrationExpiresAtValues[0], Is.EqualTo(request.ExpiresAt.Value))
                    Assert.That(result.AuditEntries[0].RestoredByteLength, Is.GreaterThan(0))
                    Assert.That(result.AuditEntries[0].ChangedSqlState, Is.True)
                    Assert.That(archiveStore.RehydratedPointers |> List.length, Is.EqualTo(1)))
            )
        }

    /// Verifies support rehydration accepts lower, upper, or combined observed-time predicates.
    [<Test>]
    member _.RehydrationObservedBoundsMayUseAfterBeforeOrBoth() =
        task {
            let events = List<string>()
            let firstCandidate, _firstBlob = archivedCandidate (Guid.Parse("48484848-4848-4848-8848-484848484860")) (Instant.FromUtc(2026, 7, 4, 10, 0, 0))
            let secondCandidate, _secondBlob = archivedCandidate (Guid.Parse("48484848-4848-4848-8848-484848484861")) (Instant.FromUtc(2026, 7, 4, 11, 0, 0))
            let thirdCandidate, _thirdBlob = archivedCandidate (Guid.Parse("48484848-4848-4848-8848-484848484862")) (Instant.FromUtc(2026, 7, 4, 12, 0, 0))

            let archiveStore =
                RecordingArchiveStore(
                    [
                        firstCandidate
                        secondCandidate
                        thirdCandidate
                    ],
                    events
                )

            let blobStore = RecordingArchiveBlobStore(events)
            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)

            let countFor query =
                task {
                    let request = { rehydrationRequest now with Query = query; DryRun = true }
                    let! result = processor.RehydrateAsync(request, CancellationToken.None)

                    return
                        match result with
                        | Ok value -> value.MatchingArchivedFactCount
                        | Error errors -> failwith (String.Join("; ", errors))
                }

            let! afterOnly = countFor (rehydrationQuery (Some secondCandidate.ObservedAt) None)
            let! beforeOnly = countFor (rehydrationQuery None (Some secondCandidate.ObservedAt))
            let! both = countFor (rehydrationQuery (Some secondCandidate.ObservedAt) (Some secondCandidate.ObservedAt))

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(afterOnly, Is.EqualTo(2))
                    Assert.That(beforeOnly, Is.EqualTo(2))
                    Assert.That(both, Is.EqualTo(1))
                    Assert.That(events, Does.Not.Contain("download-verified"))
                    Assert.That(events, Does.Not.Contain("rehydrate")))
            )
        }

    /// Verifies optional CorrelationId filters are applied during support rehydration exploration.
    [<Test>]
    member _.RehydrationCorrelationIdFilterLimitsMatches() =
        task {
            let events = List<string>()
            let firstCandidate, _firstBlob = archivedCandidate (Guid.Parse("48484848-4848-4848-8848-484848484863")) (Instant.FromUtc(2026, 7, 4, 10, 0, 0))
            let secondCandidate, _secondBlob = archivedCandidate (Guid.Parse("48484848-4848-4848-8848-484848484864")) (Instant.FromUtc(2026, 7, 4, 11, 0, 0))
            let archiveStore = RecordingArchiveStore([ firstCandidate; secondCandidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)

            let request =
                { rehydrationRequest now with
                    Query = { rehydrationQuery (Some firstCandidate.ObservedAt) None with CorrelationIds = [ secondCandidate.CorrelationId ] }
                    DryRun = true
                }

            let! result = processor.RehydrateAsync(request, CancellationToken.None)

            match result with
            | Error errors -> Assert.Fail(String.Join("; ", errors))
            | Ok value -> Assert.That(value.MatchingArchivedFactCount, Is.EqualTo(1))
        }

    /// Verifies overlong CorrelationId filters fail validation before archive exploration can broaden the query.
    [<Test>]
    member _.RehydrationRejectsOverlongCorrelationIdFiltersBeforeArchiveExploration() =
        task {
            let overlongCorrelationId = CorrelationId(String.replicate (OperationsUsageSql.CorrelationIdMaxLength + 1) "x")

            let runRequest dryRun includeValidFilter =
                task {
                    let events = List<string>()
                    let candidate, archiveBlob = archivedCandidate (Guid.Parse("48484848-4848-4848-8848-484848484869")) (Instant.FromUtc(2026, 7, 4, 10, 0, 0))

                    let archiveStore = RecordingArchiveStore([ candidate ], events)
                    let blobStore = RecordingArchiveBlobStore(events)
                    blobStore.Put(archiveBlob.Pointer.BlobName, archiveBlob.Content)
                    let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
                    let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)

                    let request =
                        { rehydrationRequest now with
                            Query = { rehydrationQuery (Some candidate.ObservedAt) (Some candidate.ObservedAt) with CorrelationIds = [ overlongCorrelationId ] }
                            DryRun = dryRun
                        }

                    let request =
                        if includeValidFilter then
                            { request with
                                Query =
                                    { request.Query with
                                        CorrelationIds =
                                            [
                                                candidate.CorrelationId
                                                overlongCorrelationId
                                            ]
                                    }
                            }
                        else
                            request

                    let! result = processor.RehydrateAsync(request, CancellationToken.None)
                    return result, events
                }

            let! dryRunResult, dryRunEvents = runRequest true false
            let! rehydrateResult, rehydrateEvents = runRequest false false
            let! mixedDryRunResult, mixedDryRunEvents = runRequest true true
            let! mixedRehydrateResult, mixedRehydrateEvents = runRequest false true

            let assertValidationFailure (result: Result<OperationsUsageRehydrationResult, string list>) (events: List<string>) =
                match result with
                | Ok value -> Assert.Fail($"Overlong CorrelationId should fail validation before matching {value.MatchingArchivedFactCount} archived facts.")
                | Error errors ->
                    Assert.Multiple(
                        Action (fun () ->
                            Assert.That(
                                String.Join("|", errors),
                                Does.Contain($"CorrelationId filters must be {OperationsUsageSql.CorrelationIdMaxLength} characters or fewer")
                            )

                            Assert.That(events, Is.Empty))
                    )

            assertValidationFailure dryRunResult dryRunEvents
            assertValidationFailure rehydrateResult rehydrateEvents
            assertValidationFailure mixedDryRunResult mixedDryRunEvents
            assertValidationFailure mixedRehydrateResult mixedRehydrateEvents
        }

    /// Verifies exact-width CorrelationId filters still work in dry-run and temporary rehydration paths.
    [<Test>]
    member _.RehydrationAcceptsMaximumLengthCorrelationIdFilters() =
        task {
            let events = List<string>()
            let usageFactId = Guid.Parse("48484848-4848-4848-8848-484848484870")
            let observedAt = Instant.FromUtc(2026, 7, 4, 10, 0, 0)
            let maxLengthCorrelationId = CorrelationId(String.replicate OperationsUsageSql.CorrelationIdMaxLength "m")

            let fact = { OperationsUsageArchiveTestData.usageFact usageFactId with CorrelationId = maxLengthCorrelationId; ObservedAt = observedAt }

            let rawPayload = OperationsUsageArchiveTestData.usageFactPayload fact

            let hotCandidate =
                { OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None with
                    CorrelationId = maxLengthCorrelationId
                    ObservedAt = observedAt
                }

            let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob hotCandidate rawPayload

            let candidate =
                { hotCandidate with
                    ArchiveState = RawUsageFactArchiveState.Archived
                    RawPayload = None
                    ArchiveBlobName = Some archiveBlob.Pointer.BlobName
                    ArchiveChecksumSha256Hex = Some archiveBlob.Pointer.ChecksumSha256Hex
                    ArchiveByteLength = Some archiveBlob.Pointer.ByteLength
                }

            let archiveStore = RecordingArchiveStore([ candidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(archiveBlob.Pointer.BlobName, archiveBlob.Content)
            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)

            let query = { rehydrationQuery (Some observedAt) (Some observedAt) with CorrelationIds = [ maxLengthCorrelationId ] }

            let! dryRun = processor.RehydrateAsync({ rehydrationRequest now with Query = query; DryRun = true }, CancellationToken.None)
            let! rehydrated = processor.RehydrateAsync({ rehydrationRequest now with Query = query; DryRun = false }, CancellationToken.None)

            match dryRun, rehydrated with
            | Ok dryRunResult, Ok rehydratedResult ->
                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(dryRunResult.MatchingArchivedFactCount, Is.EqualTo(1))
                        Assert.That(rehydratedResult.MatchingArchivedFactCount, Is.EqualTo(1))
                        Assert.That(rehydratedResult.AuditEntries |> List.length, Is.EqualTo(1))
                        Assert.That(rehydratedResult.AuditEntries[0].UsageFactId, Is.EqualTo(usageFactId))
                        Assert.That(String.Join("|", events), Is.EqualTo("count-archived|count-archived|list-archived|download-verified|rehydrate")))
                )
            | Error errors, _
            | _, Error errors -> Assert.Fail(String.Join("; ", errors))
        }

    /// Verifies large optional filter lists have identical dry-run and mutation semantics without per-value SQL parameters.
    [<Test>]
    member _.RehydrationLargeFilterListsUseBoundedSqlShapeAndConsistentSemantics() =
        task {
            let source = sourceText "src/Grace.Operations/Grace.Operations.Data/OperationsData.fs"

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(source, Does.Contain("@UsageFactIdsJson"))
                    Assert.That(source, Does.Contain("@CorrelationIdsJson"))
                    Assert.That(source, Does.Contain("OPENJSON(@UsageFactIdsJson)"))
                    Assert.That(source, Does.Contain("OPENJSON(@CorrelationIdsJson)"))
                    Assert.That(source, Does.Not.Contain("@UsageFactIdFilter{"))
                    Assert.That(source, Does.Not.Contain("@CorrelationIdFilter{")))
            )

            let events = List<string>()
            let matchingUsageFactId = Guid.Parse("48484848-4848-4848-8848-484848484866")
            let candidate, archiveBlob = archivedCandidate matchingUsageFactId (Instant.FromUtc(2026, 7, 4, 10, 0, 0))
            let archiveStore = RecordingArchiveStore([ candidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(archiveBlob.Pointer.BlobName, archiveBlob.Content)
            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)

            let manyUsageFactIds =
                [
                    for index in 0..2105 do
                        let suffix = sprintf "%012i" (index + 1)
                        Guid.Parse($"99999999-9999-4999-8999-{suffix}")
                ]
                @ [ matchingUsageFactId ]

            let manyCorrelationIds =
                [
                    for index in 0..2105 do
                        CorrelationId $"nonmatching-correlation-{index}"
                ]
                @ [ candidate.CorrelationId ]

            let query =
                { rehydrationQuery (Some candidate.ObservedAt) (Some candidate.ObservedAt) with
                    UsageFactIds = manyUsageFactIds
                    CorrelationIds = manyCorrelationIds
                }

            let dryRunRequest = { rehydrationRequest now with Query = query; DryRun = true }
            let rehydrateRequest = { rehydrationRequest now with Query = query; DryRun = false }

            let! dryRun = processor.RehydrateAsync(dryRunRequest, CancellationToken.None)
            let! rehydrated = processor.RehydrateAsync(rehydrateRequest, CancellationToken.None)

            match dryRun, rehydrated with
            | Ok dryRunResult, Ok rehydratedResult ->
                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(dryRunResult.MatchingArchivedFactCount, Is.EqualTo(1))
                        Assert.That(rehydratedResult.MatchingArchivedFactCount, Is.EqualTo(1))
                        Assert.That(rehydratedResult.AuditEntries |> List.length, Is.EqualTo(1))
                        Assert.That(rehydratedResult.AuditEntries[0].UsageFactId, Is.EqualTo(matchingUsageFactId)))
                )
            | Error errors, _
            | _, Error errors -> Assert.Fail(String.Join("; ", errors))
        }

    /// Verifies dry run reports count and warnings without Blob download or SQL rehydration.
    [<Test>]
    member _.RehydrationDryRunCountsMatchesAndSkipsBlobAndSqlMutation() =
        task {
            let events = List<string>()
            let candidate, _archiveBlob = archivedCandidate (Guid.Parse("48484848-4848-4848-8848-484848484865")) (Instant.FromUtc(2026, 7, 4, 10, 0, 0))
            let archiveStore = RecordingArchiveStore([ candidate ], events, archivedCountOverride = 10001L)
            let blobStore = RecordingArchiveBlobStore(events)
            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)
            let request = { rehydrationRequest now with DryRun = true }

            let! result = processor.RehydrateAsync(request, CancellationToken.None)

            match result with
            | Error errors -> Assert.Fail(String.Join("; ", errors))
            | Ok value ->
                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(value.DryRun, Is.True)
                        Assert.That(value.MatchingArchivedFactCount, Is.EqualTo(10001L))
                        Assert.That(value.Warnings |> String.concat "|", Does.Contain("above the 10000-row operator warning threshold"))
                        Assert.That(value.AuditEntries, Is.Empty)
                        Assert.That(String.Join("|", events), Is.EqualTo("count-archived")))
                )
        }

    /// Verifies internal SQL batch size pages a full request without capping total rehydration.
    [<Test>]
    member _.RehydrationProcessesAllMatchesAcrossMultipleSqlBatches() =
        task {
            let events = List<string>()
            let candidates = ResizeArray<RawUsageFactArchiveCandidate>()
            let blobs = ResizeArray<OperationsUsageArchiveBlob>()
            let mutable index = 0

            while index
                  <= OperationsUsageSql.RehydrationPayloadBatchSize do
                let suffix = sprintf "%012i" (index + 4866)
                let usageFactId = Guid.Parse($"48484848-4848-4848-8848-{suffix}")

                let candidate, archiveBlob =
                    archivedCandidate
                        usageFactId
                        (Instant
                            .FromUtc(2026, 7, 4, 10, 0, 0)
                            .Plus(Duration.FromMinutes(float index)))

                candidates.Add candidate
                blobs.Add archiveBlob
                index <- index + 1

            let archiveStore = RecordingArchiveStore(candidates |> Seq.toList, events)
            let blobStore = RecordingArchiveBlobStore(events)
            let mutable blobIndex = 0

            while blobIndex < blobs.Count do
                blobStore.Put(blobs[blobIndex].Pointer.BlobName, blobs[blobIndex].Content)
                blobIndex <- blobIndex + 1

            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)
            let! result = processor.RehydrateAsync(rehydrationRequest now, CancellationToken.None)

            match result with
            | Error errors -> Assert.Fail(String.Join("; ", errors))
            | Ok value ->
                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(value.MatchingArchivedFactCount, Is.EqualTo(int64 (OperationsUsageSql.RehydrationPayloadBatchSize + 1)))
                        Assert.That(value.AuditEntries |> List.length, Is.EqualTo(OperationsUsageSql.RehydrationPayloadBatchSize + 1))

                        Assert.That(
                            events
                            |> Seq.filter ((=) "rehydrate")
                            |> Seq.length,
                            Is.EqualTo(2)
                        )

                        Assert.That(archiveStore.ArchivedPageBatchSizes, Does.Contain(OperationsUsageSql.RehydrationPayloadBatchSize)))
                )
        }

    /// Verifies a failed SQL batch receives three retries before the operator request fails.
    [<Test>]
    member _.RehydrationRetriesFailedBatchUpToThreeTimes() =
        task {
            let events = List<string>()
            let usageFactId = Guid.Parse("48484848-4848-4848-8848-484848484867")
            let candidate, archiveBlob = archivedCandidate usageFactId (Instant.FromUtc(2026, 7, 4, 10, 0, 0))

            let archiveStore =
                RecordingArchiveStore(
                    [ candidate ],
                    events,
                    rehydrateResults =
                        [
                            Error(InvalidOperationException("transient one"))
                            Error(InvalidOperationException("transient two"))
                            Error(InvalidOperationException("transient three"))
                            Ok [ usageFactId ]
                        ]
                )

            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(archiveBlob.Pointer.BlobName, archiveBlob.Content)
            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)
            let! result = processor.RehydrateAsync(rehydrationRequest now, CancellationToken.None)

            match result with
            | Error errors -> Assert.Fail(String.Join("; ", errors))
            | Ok value ->
                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(value.AuditEntries |> List.length, Is.EqualTo(1))

                        Assert.That(
                            events
                            |> Seq.filter ((=) "rehydrate")
                            |> Seq.length,
                            Is.EqualTo(4)
                        ))
                )
        }

    /// Verifies retry exhaustion fails the request while leaving earlier successful batches for expiry cleanup.
    [<Test>]
    member _.RehydrationRetryExhaustionFailsAndLeavesPriorCommittedBatches() =
        task {
            let events = List<string>()
            let candidates = ResizeArray<RawUsageFactArchiveCandidate>()
            let blobs = ResizeArray<OperationsUsageArchiveBlob>()
            let firstBatchUsageFactIds = ResizeArray<UsageFactId>()
            let mutable index = 0

            while index
                  <= OperationsUsageSql.RehydrationPayloadBatchSize do
                let suffix = sprintf "%012i" (index + 4900)
                let usageFactId = Guid.Parse($"48484848-4848-4848-8848-{suffix}")

                let candidate, archiveBlob =
                    archivedCandidate
                        usageFactId
                        (Instant
                            .FromUtc(2026, 7, 4, 10, 0, 0)
                            .Plus(Duration.FromMinutes(float index)))

                candidates.Add candidate
                blobs.Add archiveBlob

                if index < OperationsUsageSql.RehydrationPayloadBatchSize then
                    firstBatchUsageFactIds.Add usageFactId

                index <- index + 1

            let archiveStore =
                RecordingArchiveStore(
                    candidates |> Seq.toList,
                    events,
                    rehydrateResults =
                        [
                            Ok(firstBatchUsageFactIds |> Seq.toList)
                            Error(InvalidOperationException("sql unavailable"))
                            Error(InvalidOperationException("sql unavailable"))
                            Error(InvalidOperationException("sql unavailable"))
                            Error(InvalidOperationException("sql unavailable"))
                        ]
                )

            let blobStore = RecordingArchiveBlobStore(events)
            let mutable blobIndex = 0

            while blobIndex < blobs.Count do
                blobStore.Put(blobs[blobIndex].Pointer.BlobName, blobs[blobIndex].Content)
                blobIndex <- blobIndex + 1

            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)
            let mutable message = None

            try
                let! _ = processor.RehydrateAsync(rehydrationRequest now, CancellationToken.None)
                Assert.Fail("Retry exhaustion should fail the operator request.")
            with
            | :? InvalidOperationException as ex -> message <- Some ex.Message

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(message.Value, Does.Contain("failed after 3 retries"))
                    Assert.That(message.Value, Does.Contain("expiry cleanup will remove prior temporary-hot rows"))
                    Assert.That(archiveStore.RehydratedPointers |> List.length, Is.EqualTo(OperationsUsageSql.RehydrationPayloadBatchSize))

                    Assert.That(
                        events
                        |> Seq.filter ((=) "rehydrate")
                        |> Seq.length,
                        Is.EqualTo(5)
                    ))
            )
        }

    /// Verifies default and maximum support rehydration expiry are enforced before archive exploration.
    [<Test>]
    member _.RehydrationExpiryDefaultsToEightHoursAndRejectsAboveSevenDaysBeforeExploration() =
        task {
            let events = List<string>()
            let usageFactId = Guid.Parse("48484848-4848-4848-8848-484848484868")
            let candidate, archiveBlob = archivedCandidate usageFactId (Instant.FromUtc(2026, 7, 4, 10, 0, 0))
            let archiveStore = RecordingArchiveStore([ candidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(archiveBlob.Pointer.BlobName, archiveBlob.Content)
            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)

            let! defaultResult = processor.RehydrateAsync({ rehydrationRequest now with ExpiresAt = None }, CancellationToken.None)
            let! maxResult = processor.RehydrateAsync({ rehydrationRequest now with ExpiresAt = Some(now.Plus(Duration.FromDays 7.0)) }, CancellationToken.None)
            let eventCountAfterValidRequests = events.Count

            let! rejected =
                processor.RehydrateAsync(
                    { rehydrationRequest now with
                        ExpiresAt =
                            Some(
                                now
                                    .Plus(Duration.FromDays 7.0)
                                    .Plus(Duration.FromTicks 1L)
                            )
                    },
                    CancellationToken.None
                )

            match defaultResult, maxResult, rejected with
            | Ok _, Ok _, Error errors ->
                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(archiveStore.RehydrationExpiresAtValues[0], Is.EqualTo(now.Plus(Duration.FromHours 8.0)))
                        Assert.That(archiveStore.RehydrationExpiresAtValues[1], Is.EqualTo(now.Plus(Duration.FromDays 7.0)))
                        Assert.That(String.Join("|", errors), Does.Contain("no more than 7 days"))
                        Assert.That(events.Count, Is.EqualTo(eventCountAfterValidRequests)))
                )
            | _ -> Assert.Fail("Expiry default/max validation did not return the expected results.")
        }

    /// Verifies general rehydration logs omit human requester identity while local audit evidence keeps it.
    [<Test>]
    member _.RehydrationLogsRedactRequestedByButAuditKeepsRequester() =
        task {
            let events = List<string>()
            let usageFactId = Guid.Parse("48484848-4848-4848-8848-484848484856")
            let fact = OperationsUsageArchiveTestData.usageFact usageFactId
            let rawPayload = OperationsUsageArchiveTestData.usageFactPayload fact
            let hotCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None
            let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob hotCandidate rawPayload
            let replayCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Archived None (Some archiveBlob.Pointer)
            let archiveStore = RecordingArchiveStore([ replayCandidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(archiveBlob.Pointer.BlobName, archiveBlob.Content)
            let logger = ArchiveRecordingLogger<OperationsUsageRehydrationProcessor>()

            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)

            let processor = OperationsUsageRehydrationProcessor(archiveStore, blobStore, FixedClock now, logger)

            let request = rehydrationRequest now

            let! rehydrated = processor.RehydrateAsync(request, CancellationToken.None)

            let result =
                match rehydrated with
                | Ok value -> value
                | Error errors -> failwith (String.Join("; ", errors))

            let logText = String.Join("\n", logger.Entries)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(result.AuditEntries[0].RequestedBy, Is.EqualTo(request.RequestedBy))
                    Assert.That(logText, Does.Not.Contain(request.RequestedBy))
                    Assert.That(logText, Does.Not.Contain("@example.test"))
                    Assert.That(logText, Does.Not.Contain("RequestedBy")))
            )
        }

    /// Verifies archive payload validation fails before SQL expiry state is changed.
    [<Test>]
    member _.RehydrationDoesNotPersistExpiryWhenBlobValidationFails() =
        task {
            let events = List<string>()
            let firstUsageFactId = Guid.Parse("48484848-4848-4848-8848-484848484849")
            let secondUsageFactId = Guid.Parse("49494949-4949-4949-8949-494949494950")

            let createReplayCandidate usageFactId =
                let fact = OperationsUsageArchiveTestData.usageFact usageFactId
                let rawPayload = OperationsUsageArchiveTestData.usageFactPayload fact
                let hotCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None
                let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob hotCandidate rawPayload
                OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Archived None (Some archiveBlob.Pointer), archiveBlob

            let firstCandidate, firstBlob = createReplayCandidate firstUsageFactId
            let secondCandidate, _secondBlob = createReplayCandidate secondUsageFactId
            let archiveStore = RecordingArchiveStore([ firstCandidate; secondCandidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(firstBlob.Pointer.BlobName, firstBlob.Content)

            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)

            let request = rehydrationRequest now

            let mutable message = None

            try
                let! _ = processor.RehydrateAsync(request, CancellationToken.None)
                Assert.Fail("Missing archive Blob should be surfaced before SQL expiry persistence.")
            with
            | :? InvalidOperationException as ex -> message <- Some ex.Message

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(message.Value, Does.Contain("is missing"))
                    Assert.That(events, Does.Not.Contain("rehydrate"))
                    Assert.That(archiveStore.RehydratedPointers, Is.Empty))
            )
        }

    /// Verifies caller cancellation after SQL persistence does not prevent durable expiry evidence.
    [<Test>]
    member _.RehydrationPersistsExpiryBeforeCancellationAfterSqlMutation() =
        task {
            let events = List<string>()
            let firstUsageFactId = Guid.Parse("48484848-4848-4848-8848-484848484852")
            let secondUsageFactId = Guid.Parse("49494949-4949-4949-8949-494949494953")

            let createReplayCandidate usageFactId =
                let fact = OperationsUsageArchiveTestData.usageFact usageFactId
                let rawPayload = OperationsUsageArchiveTestData.usageFactPayload fact
                let hotCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None
                let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob hotCandidate rawPayload
                OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Archived None (Some archiveBlob.Pointer), archiveBlob

            let firstCandidate, firstBlob = createReplayCandidate firstUsageFactId
            let secondCandidate, secondBlob = createReplayCandidate secondUsageFactId
            use cancellationSource = new CancellationTokenSource()

            let archiveStore =
                RecordingArchiveStore(
                    [ firstCandidate; secondCandidate ],
                    events,
                    afterRehydrate = (fun _ changed -> if changed then cancellationSource.Cancel())
                )

            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(firstBlob.Pointer.BlobName, firstBlob.Content)
            blobStore.Put(secondBlob.Pointer.BlobName, secondBlob.Content)

            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)

            let request = rehydrationRequest now

            try
                let! _ = processor.RehydrateAsync(request, cancellationSource.Token)
                Assert.Fail("Cancellation after SQL expiry persistence should be surfaced.")
            with
            | :? OperationCanceledException -> ()

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(archiveStore.RehydratedPointers |> List.length, Is.EqualTo(2))
                    Assert.That(archiveStore.RehydrationExpiresAtValues[0], Is.EqualTo(request.ExpiresAt.Value))
                    Assert.That(archiveStore.RehydrateCancellationCanBeCanceled[0], Is.False))
            )
        }

    /// Verifies overlapping requests refresh durable expiry instead of conflicting on request-owned state.
    [<Test>]
    member _.OverlappingRehydrationRefreshesExpiresAt() =
        task {
            let events = List<string>()
            let firstUsageFactId = Guid.Parse("48484848-4848-4848-8848-484848484850")
            let secondUsageFactId = Guid.Parse("49494949-4949-4949-8949-494949494951")

            let createReplayCandidate usageFactId =
                let fact = OperationsUsageArchiveTestData.usageFact usageFactId
                let rawPayload = OperationsUsageArchiveTestData.usageFactPayload fact
                let hotCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None
                let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob hotCandidate rawPayload
                OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Archived None (Some archiveBlob.Pointer), archiveBlob

            let firstCandidate, firstBlob = createReplayCandidate firstUsageFactId
            let secondCandidate, secondBlob = createReplayCandidate secondUsageFactId
            let archiveStore = RecordingArchiveStore([ firstCandidate; secondCandidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(firstBlob.Pointer.BlobName, firstBlob.Content)
            blobStore.Put(secondBlob.Pointer.BlobName, secondBlob.Content)

            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)

            let request = rehydrationRequest now

            let refreshedRequest =
                { request with Reason = "Second support request for the same archived rows"; ExpiresAt = Some(now.Plus(Duration.FromHours 2.0)) }

            let! firstResult = processor.RehydrateAsync(request, CancellationToken.None)
            let! secondResult = processor.RehydrateAsync(refreshedRequest, CancellationToken.None)

            let firstAudit =
                match firstResult with
                | Ok value -> value.AuditEntries
                | Error errors -> failwith (String.Join("; ", errors))

            let secondAudit =
                match secondResult with
                | Ok value -> value.AuditEntries
                | Error errors -> failwith (String.Join("; ", errors))

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(firstAudit |> List.length, Is.EqualTo(2))
                    Assert.That(secondAudit |> List.length, Is.EqualTo(2))
                    Assert.That(archiveStore.RehydrationExpiresAtValues[0], Is.EqualTo(request.ExpiresAt.Value))
                    Assert.That(archiveStore.RehydrationExpiresAtValues[1], Is.EqualTo(refreshedRequest.ExpiresAt.Value))

                    Assert.That(
                        secondAudit
                        |> List.forall (fun entry -> entry.ChangedSqlState),
                        Is.True
                    ))
            )
        }

    /// Verifies a request fails closed when SQL cannot persist expiry for every verified archive pointer.
    [<Test>]
    member _.RehydrationFailsWhenExpiryPersistenceMissesVerifiedPointer() =
        task {
            let events = List<string>()
            let firstUsageFactId = Guid.Parse("48484848-4848-4848-8848-484848484854")
            let secondUsageFactId = Guid.Parse("49494949-4949-4949-8949-494949494955")

            let createReplayCandidate usageFactId =
                let fact = OperationsUsageArchiveTestData.usageFact usageFactId
                let rawPayload = OperationsUsageArchiveTestData.usageFactPayload fact
                let hotCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None
                let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob hotCandidate rawPayload
                OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Archived None (Some archiveBlob.Pointer), archiveBlob

            let firstCandidate, firstBlob = createReplayCandidate firstUsageFactId
            let secondCandidate, secondBlob = createReplayCandidate secondUsageFactId

            let archiveStore =
                RecordingArchiveStore(
                    [ firstCandidate; secondCandidate ],
                    events,
                    [
                        Ok [ secondUsageFactId ]
                        Ok [ secondUsageFactId ]
                        Ok [ secondUsageFactId ]
                        Ok [ secondUsageFactId ]
                    ]
                )

            let blobStore = RecordingArchiveBlobStore(events)
            blobStore.Put(firstBlob.Pointer.BlobName, firstBlob.Content)
            blobStore.Put(secondBlob.Pointer.BlobName, secondBlob.Content)

            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)

            let request = rehydrationRequest now

            let mutable message = None

            try
                let! _ = processor.RehydrateAsync(request, CancellationToken.None)
                Assert.Fail("Rehydration must not return success when SQL did not persist every expiry.")
            with
            | :? InvalidOperationException as ex -> message <- Some ex.Message

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(message.Value, Does.Contain("failed after 3 retries"))
                    Assert.That(message.Value, Does.Contain("did not verify and rehydrate every selected archive pointer"))
                    Assert.That(message.Value, Does.Contain(string firstUsageFactId))
                    Assert.That(archiveStore.RehydratedPointers |> List.length, Is.EqualTo(4))
                    Assert.That(archiveStore.RehydratedPointers[0], Is.EqualTo(secondBlob.Pointer)))
            )
        }

    /// Verifies support rehydration rejects missing scope and observed bounds before reading Blob payloads.
    [<Test>]
    member _.RehydrationRejectsMissingScopeAndObservedBounds() =
        task {
            let events = List<string>()
            let archiveStore = RecordingArchiveStore([], events)
            let blobStore = RecordingArchiveBlobStore(events)
            let now = Instant.FromUtc(2026, 7, 7, 10, 0, 0)
            let processor = createRehydrationProcessor archiveStore blobStore (FixedClock now)

            let request =
                {
                    Query =
                        {
                            Scope = Some { OwnerId = None; OrganizationId = None; RepositoryId = None }
                            ObservedAfterUtc = None
                            ObservedBeforeUtc = None
                            CorrelationIds = []
                            UsageFactIds = []
                        }
                    DryRun = false
                    RequestedBy = ""
                    Reason = ""
                    ExpiresAt = Some(now - Duration.FromMinutes 1.0)
                }

            let! result = processor.RehydrateAsync(request, CancellationToken.None)

            match result with
            | Ok _ -> Assert.Fail("Global unbounded rehydration should be rejected.")
            | Error errors ->
                let errorText = String.Join("|", errors)

                Assert.Multiple(
                    Action (fun () ->
                        Assert.That(errorText, Does.Contain("OwnerId scope"))
                        Assert.That(errorText, Does.Contain("OrganizationId scope"))
                        Assert.That(errorText, Does.Contain("RepositoryId scope"))
                        Assert.That(errorText, Does.Contain("ObservedAfterUtc or ObservedBeforeUtc"))
                        Assert.That(events, Is.Empty))
                )
        }

    /// Verifies archive settings expose missing Blob dependencies as startup validation errors.
    [<Test>]
    member _.ArchiveSettingsRequireBlobAuthorityConfiguration() =
        let configuration =
            ConfigurationBuilder()
                .AddInMemoryCollection(Dictionary<string, string>())
                .Build()

        match OperationsUsageArchiveSettings.fromConfiguration configuration with
        | Ok _ -> Assert.Fail("Archive settings should reject missing Blob dependencies.")
        | Error errors ->
            let errorText = String.Join("|", errors)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(errorText, Does.Contain(OperationsUsageArchiveSettings.BlobConnectionStringEnvironmentVariable))
                    Assert.That(errorText, Does.Contain(OperationsUsageArchiveSettings.BlobContainerNameEnvironmentVariable)))
            )

    /// Verifies the worker can keep ingestion-only startup when no archive settings are supplied by the host.
    [<Test>]
    member _.ArchiveSettingsAreOptionalUntilArchiveConfigurationIsPresent() =
        let configuration =
            ConfigurationBuilder()
                .AddInMemoryCollection(Dictionary<string, string>())
                .Build()

        match OperationsUsageArchiveSettings.tryFromConfiguration configuration with
        | Ok None -> Assert.Pass()
        | Ok (Some _) -> Assert.Fail("No archive settings should leave the archive worker unregistered.")
        | Error errors ->
            let errorText = String.Join("; ", errors)
            Assert.Fail($"No archive settings should not fail ingestion-only startup: {errorText}")

    /// Verifies malformed archive tuning values fail startup instead of silently reverting to defaults.
    [<Test>]
    member _.ArchiveSettingsRejectMalformedTuningValuesWhenArchiveConfigurationIsPresent() =
        let configuration =
            archiveConfiguration [ KeyValuePair(OperationsUsageArchiveSettings.BlobConnectionStringEnvironmentVariable, "UseDevelopmentStorage=true")
                                   KeyValuePair(OperationsUsageArchiveSettings.BlobContainerNameEnvironmentVariable, "usage-archives")
                                   KeyValuePair(OperationsUsageArchiveSettings.HotRetentionDaysEnvironmentVariable, "7d")
                                   KeyValuePair(OperationsUsageArchiveSettings.BatchSizeEnvironmentVariable, "abc")
                                   KeyValuePair(OperationsUsageArchiveSettings.PollIntervalSecondsEnvironmentVariable, "0") ]

        match OperationsUsageArchiveSettings.fromConfiguration configuration with
        | Ok _ -> Assert.Fail("Archive settings should reject malformed or non-positive tuning values.")
        | Error errors ->
            let errorText = String.Join("|", errors)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(errorText, Does.Contain(OperationsUsageArchiveSettings.HotRetentionDaysEnvironmentVariable))
                    Assert.That(errorText, Does.Contain(OperationsUsageArchiveSettings.BatchSizeEnvironmentVariable))
                    Assert.That(errorText, Does.Contain(OperationsUsageArchiveSettings.PollIntervalSecondsEnvironmentVariable)))
            )

    /// Verifies archive retention batch size is capped before raw payload batches can be materialized.
    [<Test>]
    member _.ArchiveSettingsRejectBatchSizeAboveRetentionCap() =
        let configuration =
            archiveConfiguration [ KeyValuePair(OperationsUsageArchiveSettings.BlobConnectionStringEnvironmentVariable, "UseDevelopmentStorage=true")
                                   KeyValuePair(OperationsUsageArchiveSettings.BlobContainerNameEnvironmentVariable, "usage-archives")
                                   KeyValuePair(
                                       OperationsUsageArchiveSettings.BatchSizeEnvironmentVariable,
                                       string (OperationsUsageArchiveSettings.MaxBatchSize + 1)
                                   ) ]

        match OperationsUsageArchiveSettings.fromConfiguration configuration with
        | Ok _ -> Assert.Fail("Archive settings should reject over-cap retention batch sizes.")
        | Error errors ->
            let errorText = String.Join("|", errors)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(errorText, Does.Contain(OperationsUsageArchiveSettings.BatchSizeEnvironmentVariable))
                    Assert.That(errorText, Does.Contain(string OperationsUsageArchiveSettings.MaxBatchSize)))
            )

    /// Verifies archive Blob container names follow Azure's lowercase DNS-style naming rules.
    [<TestCase("UsageArchives")>]
    [<TestCase("archive--hot")>]
    [<TestCase("-archive-hot")>]
    [<TestCase("archive-hot-")>]
    [<TestCase("ar")>]
    [<TestCase("archive_hot")>]
    member _.ArchiveSettingsRejectInvalidBlobContainerNames(containerName: string) =
        let configuration =
            archiveConfiguration [ KeyValuePair(OperationsUsageArchiveSettings.BlobConnectionStringEnvironmentVariable, "UseDevelopmentStorage=true")
                                   KeyValuePair(OperationsUsageArchiveSettings.BlobContainerNameEnvironmentVariable, containerName) ]

        match OperationsUsageArchiveSettings.fromConfiguration configuration with
        | Ok _ -> Assert.Fail($"Archive settings should reject invalid Blob container name '{containerName}'.")
        | Error errors ->
            let errorText = String.Join("|", errors)
            Assert.That(errorText, Does.Contain(OperationsUsageArchiveSettings.BlobContainerNameEnvironmentVariable))

    /// Verifies a valid archive configuration still applies default worker tuning values.
    [<Test>]
    member _.ArchiveSettingsAcceptValidConfigurationWithDefaultTuning() =
        let configuration =
            archiveConfiguration [ KeyValuePair(OperationsUsageArchiveSettings.BlobConnectionStringEnvironmentVariable, "UseDevelopmentStorage=true")
                                   KeyValuePair(OperationsUsageArchiveSettings.BlobContainerNameEnvironmentVariable, "usage-archives") ]

        match OperationsUsageArchiveSettings.fromConfiguration configuration with
        | Error errors ->
            let errorText = String.Join("; ", errors)
            Assert.Fail($"Archive settings should accept valid Blob configuration: {errorText}")
        | Ok settings ->
            Assert.Multiple(
                Action (fun () ->
                    Assert.That(settings.BlobContainerName, Is.EqualTo("usage-archives"))
                    Assert.That(settings.HotRetentionDays, Is.EqualTo(90))
                    Assert.That(settings.BatchSize, Is.EqualTo(100))
                    Assert.That(settings.PollInterval, Is.EqualTo(TimeSpan.FromSeconds 300.0)))
            )

    /// Verifies EF model metadata includes the persisted archive authority surface.
    [<Test>]
    member _.OperationsEfModelCarriesRawFactArchiveAuthorityColumns() =
        let rawFact = rawFactEntityType ()

        let hasArchiveIndex =
            rawFact.GetIndexes()
            |> Seq.exists (fun index -> index.GetDatabaseName() = "IX_ops_RawUsageFact_ArchiveStateObservedAt")

        Assert.Multiple(
            Action (fun () ->
                Assert.That(rawFact.FindProperty("RawPayload").IsNullable, Is.True)
                Assert.That(rawFact.FindProperty("ArchiveState").IsNullable, Is.False)

                Assert.That(
                    rawFact
                        .FindProperty("ArchiveBlobName")
                        .GetMaxLength(),
                    Is.EqualTo(Nullable OperationsUsageSql.ArchiveBlobNameMaxLength)
                )

                Assert.That(
                    rawFact
                        .FindProperty("ArchiveChecksumSha256Hex")
                        .GetMaxLength(),
                    Is.EqualTo(Nullable OperationsUsageSql.ArchiveChecksumSha256HexLength)
                )

                Assert.That(
                    rawFact
                        .FindProperty("ArchiveByteLength")
                        .GetColumnType(),
                    Is.EqualTo("bigint")
                )

                Assert.That(
                    rawFact
                        .FindProperty("ArchiveVerifiedAtUtc")
                        .GetColumnType(),
                    Is.EqualTo("datetime2(7)")
                )

                Assert.That(
                    rawFact
                        .FindProperty("ArchivedAtUtc")
                        .GetColumnType(),
                    Is.EqualTo("datetime2(7)")
                )

                Assert.That(rawFact.FindProperty("RehydrationLeaseId"), Is.Null)
                Assert.That(rawFact.FindProperty("RehydrationRequestedBy"), Is.Null)
                Assert.That(rawFact.FindProperty("RehydrationReason"), Is.Null)

                Assert.That(
                    rawFact
                        .FindProperty("RehydrationExpiresAtUtc")
                        .GetColumnType(),
                    Is.EqualTo("datetime2(7)")
                )

                Assert.That(
                    rawFact
                        .FindProperty("LastArchiveFailureReason")
                        .GetMaxLength(),
                    Is.EqualTo(Nullable OperationsUsageSql.ArchiveFailureReasonMaxLength)
                )

                Assert.That(
                    rawFact
                        .FindProperty("LastArchiveFailureAtUtc")
                        .GetColumnType(),
                    Is.EqualTo("datetime2(7)")
                )

                Assert.That(
                    rawFact
                        .FindProperty(
                            "ArchiveFailureCount"
                        )
                        .IsNullable,
                    Is.False
                )

                Assert.That(
                    rawFact
                        .FindProperty("ArchiveRetiredAtUtc")
                        .GetColumnType(),
                    Is.EqualTo("datetime2(7)")
                )

                Assert.That(rawFact.FindProperty("RehydratedAtUtc"), Is.Null)

                Assert.That(hasArchiveIndex, Is.True))
        )

    /// Verifies the archive migration emits guarded SQL for authority columns, nullable payloads, and archive scan index.
    [<Test>]
    member _.ArchiveMigrationScriptContainsAuthorityAndCleanupGuards() =
        let script = migrationScript ()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(script, Does.Contain("ArchiveState int NOT NULL"))
                Assert.That(script, Does.Contain("CONSTRAINT DF_ops_RawUsageFact_ArchiveState DEFAULT (0) WITH VALUES"))
                Assert.That(script, Does.Contain("ArchiveBlobName nvarchar(512) NULL"))
                Assert.That(script, Does.Contain("ArchiveChecksumSha256Hex char(64) NULL"))
                Assert.That(script, Does.Contain("ArchiveByteLength bigint NULL"))
                Assert.That(script, Does.Contain("RehydrationExpiresAtUtc datetime2(7) NULL"))
                Assert.That(script, Does.Contain("LastArchiveFailureReason nvarchar(400) NULL"))
                Assert.That(script, Does.Contain("LastArchiveFailureAtUtc datetime2(7) NULL"))
                Assert.That(script, Does.Contain("ArchiveFailureCount int NOT NULL"))
                Assert.That(script, Does.Contain("CONSTRAINT DF_ops_RawUsageFact_ArchiveFailureCount DEFAULT (0) WITH VALUES"))
                Assert.That(script, Does.Contain("ArchiveRetiredAtUtc datetime2(7) NULL"))
                Assert.That(script, Does.Not.Contain("RehydrationLeaseId uniqueidentifier NULL"))
                Assert.That(script, Does.Not.Contain("RehydrationRequestedBy nvarchar(200) NULL"))
                Assert.That(script, Does.Not.Contain("RehydrationReason nvarchar(512) NULL"))
                Assert.That(script, Does.Not.Contain("RehydratedAtUtc datetime2(7) NULL"))
                Assert.That(script, Does.Contain("ALTER COLUMN RawPayload varbinary(max) NULL"))
                Assert.That(script, Does.Contain("CREATE INDEX IX_ops_RawUsageFact_ArchiveStateObservedAt"))
                Assert.That(script, Does.Contain("ON PS_ops_OperationsUsageMonthUtc(ObservedAtUtc)")))
        )
