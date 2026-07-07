namespace Grace.Operations.Tests

open Grace.Operations.Data
open Grace.Operations.Data.Migrations
open Grace.Operations.Worker
open Grace.Types.Common
open Grace.Types.Usage
open Microsoft.EntityFrameworkCore
open Microsoft.EntityFrameworkCore.Infrastructure
open Microsoft.EntityFrameworkCore.Migrations
open Microsoft.EntityFrameworkCore.Metadata
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging.Abstractions
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
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
type private RecordingArchiveStore(candidates: RawUsageFactArchiveCandidate list, events: List<string>) =
    let markedPointers = ResizeArray<RawUsageFactArchivePointer>()
    let completedPointers = ResizeArray<RawUsageFactArchivePointer>()

    /// Returns Blob pointers recorded as verified in SQL.
    member _.MarkedPointers = markedPointers |> Seq.toList

    /// Returns Blob pointers completed by clearing the hot SQL payload.
    member _.CompletedPointers = completedPointers |> Seq.toList

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
            Task.FromResult true

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
            Assert.That(int64 content.Length, Is.EqualTo(pointer.ByteLength))

            let checksum = OperationsUsageArchiveFormat.checksumSha256Hex content
            Assert.That(checksum, Is.EqualTo(pointer.ChecksumSha256Hex))

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

    /// Verifies missing or corrupt verified blobs fail before SQL cleanup can clear hot payload authority.
    [<Test>]
    member _.ArchiveBatchFailsBeforeCleanupWhenVerifiedBlobIsMissingOrCorrupt() =
        task {
            let events = List<string>()
            let usageFactId = Guid.Parse("43434343-4343-4343-8343-434343434343")
            let rawPayload = OperationsUsageArchiveTestData.payload usageFactId
            let hotCandidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot (Some rawPayload) None
            let archiveBlob = OperationsUsageArchiveFormat.createArchiveBlob hotCandidate rawPayload

            let verifiedCandidate =
                OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.ArchiveVerified None (Some archiveBlob.Pointer)

            let archiveStore = RecordingArchiveStore([ verifiedCandidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            let processor = createProcessor archiveStore blobStore

            let mutable message = None

            try
                let! _ = processor.ArchiveBatchAsync(Instant.FromUtc(2026, 8, 1, 0, 0), 10, CancellationToken.None)
                Assert.Fail("Missing verified archive Blob should fail before SQL cleanup.")
            with
            | :? InvalidOperationException as ex -> message <- Some ex.Message

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(message.Value, Does.Contain("is missing"))
                    Assert.That(String.Join("|", events), Is.EqualTo("list-candidates|verify-blob"))
                    Assert.That(archiveStore.CompletedPointers, Is.Empty))
            )
        }

    /// Verifies a hot row without payload bytes fails loudly instead of recording fake Blob authority.
    [<Test>]
    member _.ArchiveBatchRejectsHotCandidateWithoutRawPayload() =
        task {
            let events = List<string>()
            let usageFactId = Guid.Parse("44444444-4444-4444-8444-444444444444")
            let candidate = OperationsUsageArchiveTestData.candidate usageFactId RawUsageFactArchiveState.Hot None None
            let archiveStore = RecordingArchiveStore([ candidate ], events)
            let blobStore = RecordingArchiveBlobStore(events)
            let processor = createProcessor archiveStore blobStore
            let mutable message = None

            try
                let! _ = processor.ArchiveBatchAsync(Instant.FromUtc(2026, 8, 1, 0, 0), 10, CancellationToken.None)
                Assert.Fail("Hot rows without raw payload bytes should fail before Blob writes.")
            with
            | :? InvalidOperationException as ex -> message <- Some ex.Message

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(message.Value, Does.Contain("has no raw payload"))
                    Assert.That(String.Join("|", events), Is.EqualTo("list-candidates"))
                    Assert.That(archiveStore.MarkedPointers, Is.Empty)
                    Assert.That(archiveStore.CompletedPointers, Is.Empty))
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
                Assert.That(script, Does.Contain("ALTER COLUMN RawPayload varbinary(max) NULL"))
                Assert.That(script, Does.Contain("CREATE INDEX IX_ops_RawUsageFact_ArchiveStateObservedAt"))
                Assert.That(script, Does.Contain("ON PS_ops_OperationsUsageMonthUtc(ObservedAtUtc)")))
        )
