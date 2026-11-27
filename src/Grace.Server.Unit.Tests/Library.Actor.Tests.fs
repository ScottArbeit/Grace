namespace Grace.Server.Tests

open Grace.Actors
open Grace.Shared
open Grace.Shared.Validation
open Grace.Types.Authorization
open Grace.Types.Common
open Grace.Types.Library
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.UploadSession
open NodaTime
open Microsoft.Extensions.DependencyInjection
open NUnit.Framework
open Orleans
open Orleans.Runtime
open Orleans.Storage
open System
open System.Collections.Generic
open System.Reflection
open System.Text
open System.Threading
open System.Threading.Tasks

/// Supplies the one real grain identity needed to invoke RepositoryLibraryActor methods in a focused harness.
type LibraryTestGrainContextProxy() =
    inherit DispatchProxy()

    member val GrainId = GrainId.Create(GrainType.Create("Grace.RepositoryLibraryActor"), GrainIdKeyExtensions.CreateGuidKey(Guid.Empty)) with get, set

    override this.Invoke(methodInfo, _arguments) =
        match methodInfo.Name with
        | "get_GrainId" -> box this.GrainId
        | _ when methodInfo.ReturnType = typeof<Void> -> null
        | _ when methodInfo.ReturnType.IsValueType -> Activator.CreateInstance(methodInfo.ReturnType)
        | _ -> null

/// Stores exact Library records so authorization ordering can be exercised through the real actor methods.
type LibraryReplayTestStorage() =
    let records = Dictionary<string, obj>(StringComparer.Ordinal)
    let mutable readCount = 0

    let storageKey (grainType: string) (grainId: GrainId) = $"{grainType}|{grainId}"

    member _.ReadCount = readCount

    member _.Store<'T>(grainType, recordKey, value: 'T) = records[storageKey grainType (GrainId.Create(grainType, recordKey))] <- box value

    interface IGrainStorage with
        member _.ReadStateAsync<'T>(grainType, grainId, state: IGrainState<'T>) =
            readCount <- readCount + 1

            match records.TryGetValue(storageKey grainType grainId) with
            | true, value ->
                state.State <- unbox<'T> value
                state.ETag <- "library-replay-test"
                state.RecordExists <- true
            | _ -> state.RecordExists <- false

            Task.CompletedTask

        member _.WriteStateAsync<'T>(_, _, _: IGrainState<'T>) =
            Task.FromException(InvalidOperationException("The authorization replay harness must not write Library records."))

        member _.ClearStateAsync<'T>(_, _, _: IGrainState<'T>) =
            Task.FromException(InvalidOperationException("The authorization replay harness must not clear Library records."))

/// Stores Library records in memory with ETags, so admission runs through the real actor without Cosmos. Any path that
/// needs a Cosmos query or an upload session fails here, which is how these tests prove a path doesn't take one.
type LibraryMemoryTestStorage() =
    let records = Dictionary<string, obj * string>(StringComparer.Ordinal)
    let mutable writeCount = 0
    let mutable nextETag = 0
    let readCounts = Dictionary<string, int>(StringComparer.Ordinal)

    let storageKey (grainType: string) (grainId: GrainId) = $"{grainType}|{grainId}"

    /// Counts every write and clear the actor made.
    member _.WriteCount = writeCount

    /// Counts the actor's reads of one record type.
    member _.ReadCount(grainType: string) =
        match readCounts.TryGetValue grainType with
        | true, count -> count
        | _ -> 0

    /// Seeds one record as if an earlier turn had written it.
    member _.Store<'T>(grainType, recordKey, value: 'T) =
        nextETag <- nextETag + 1
        records[storageKey grainType (GrainId.Create(grainType, recordKey))] <- (box value, $"etag-{nextETag}")

    /// Reads one record back for assertions.
    member _.TryGet<'T>(grainType, recordKey) =
        match records.TryGetValue(storageKey grainType (GrainId.Create(grainType, recordKey))) with
        | true, (value, _) -> Some(unbox<'T> value)
        | _ -> None

    interface IGrainStorage with
        member _.ReadStateAsync<'T>(grainType, grainId, state: IGrainState<'T>) =
            let count =
                match readCounts.TryGetValue grainType with
                | true, count -> count
                | _ -> 0

            readCounts[grainType] <- count + 1

            match records.TryGetValue(storageKey grainType grainId) with
            | true, (value, etag) ->
                state.State <- unbox<'T> value
                state.ETag <- etag
                state.RecordExists <- true
            | _ ->
                state.ETag <- null
                state.RecordExists <- false

            Task.CompletedTask

        member _.WriteStateAsync<'T>(grainType, grainId, state: IGrainState<'T>) =
            let key = storageKey grainType grainId

            let stored =
                match records.TryGetValue key with
                | true, (_, etag) -> etag
                | _ -> null

            if stored <> state.ETag then
                Task.FromException(InvalidOperationException($"ETag mismatch for {key}."))
            else
                writeCount <- writeCount + 1
                nextETag <- nextETag + 1
                let etag = $"etag-{nextETag}"
                records[key] <- (box state.State, etag)
                state.ETag <- etag
                state.RecordExists <- true
                Task.CompletedTask

        member _.ClearStateAsync<'T>(grainType, grainId, _: IGrainState<'T>) =
            writeCount <- writeCount + 1

            records.Remove(storageKey grainType grainId)
            |> ignore

            Task.CompletedTask

/// Verifies deterministic Library decisions, tokens, and stored-result replay.
[<Parallelizable(ParallelScope.All)>]
type LibraryActorTests() =

    let repositoryId = Guid.Parse("a819b112-d97c-4813-b647-a02686a0eb33")
    let operationId = Guid.Parse("6aa16ee4-8df0-4aa0-a463-389e36f8e119")
    let itemId = Guid.Parse("06596842-c919-4437-951f-5a9ed9b79465")
    let contentVersionId = Guid.Parse("80faf399-08f5-4768-a0be-2c607f041162")
    let catalogVersion = Guid.Parse("e014a230-2557-45ba-95fa-eb0191dfb657")
    let timestamp = Instant.FromUtc(2026, 9, 7, 8, 9, 10)

    let acceptedRecord cursor =
        {
            SchemaVersion = 1
            Cursor = cursor
            RequestHash = $"request-{cursor}"
            CorrelationId = $"correlation-{cursor}"
            Change = Unchecked.defaultof<LibraryChangeDto>
            PriorNamespace = None
            AddedItemRecord = false
            AddedSlotRecord = false
        }

    let preparedManifest () =
        let bytes = Encoding.UTF8.GetBytes("prepared Library content")
        let blockAddress = ContentBlockAddress(ContentAddress.computeBlake3Hex bytes)

        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                RabinChunking.SuiteName,
                FileContentHash(ContentAddress.computeBlake3Hex bytes),
                int64 bytes.Length,
                StoragePoolId "pool-library",
                [
                    ContentBlock.Create(blockAddress, 0L, int64 bytes.Length)
                ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let preparedUpload expiresAt =
        let manifest = preparedManifest ()

        { UploadSessionDto.Default with
            UploadSessionId = Guid.Parse("918d5491-fc38-4010-886c-17541171670f")
            RepositoryId = repositoryId
            StoragePoolId = manifest.StoragePoolId
            AuthorizedScope = RelativePath "Library/918d5491-fc38-4010-886c-17541171670f"
            FileContentHash = manifest.FileContentHash
            ExpectedSize = manifest.Size
            LifecycleState = UploadSessionLifecycleState.RetentionPending
            FinalizedManifestAddress = Some manifest.ManifestAddress
            FinalizedManifest = Some manifest
            LibraryPreparation = Some { OperationId = operationId; PrincipalId = "user:test"; ExpectedSha256 = String.replicate 64 "a"; ExpiresAt = expiresAt }
        }

    /// Builds a real RepositoryLibraryActor with exact keyed records and a controllable current permission result.
    let replayActor permission (receipt: LibraryReceiptDocument) =
        let catalog = LibraryCatalogDto.CreateInitial(repositoryId, timestamp, "user:test")

        let control = { SchemaVersion = 1; Catalog = catalog; CommittedCursor = 0L; Pending = None; ItemRecordCount = 0; SlotRecordCount = 0 }

        let storage = LibraryReplayTestStorage()
        storage.Store("Grace.Library.Control.v2", repositoryId.ToString("D"), control)

        storage.Store(
            "Grace.Library.Receipt.v2",
            LibraryRecords.key [ repositoryId.ToString("D")
                                 "operation"
                                 receipt.OperationId.ToString("D") ],
            receipt
        )

        let services = ServiceCollection()

        for storageName in
            [|
                LibraryRecords.ControlStorageName
                LibraryRecords.ChangesStorageName
                LibraryRecords.CurrentStorageName
                LibraryRecords.ReceiptsStorageName
                LibraryRecords.BaselinesStorageName
            |] do
            services.AddKeyedSingleton<IGrainStorage>(storageName, storage)
            |> ignore

        let provider = services.BuildServiceProvider()
        let mutable authorizationCalls = 0

        let authorize =
            Func<RepositoryId, LibraryWriteAuthorization, CancellationToken, Task<PermissionCheckResult>> (fun _ _ _ ->
                authorizationCalls <- authorizationCalls + 1
                Task.FromResult permission)

        let actor = RepositoryLibraryActor(provider, null, authorize, Array.create 32 7uy)
        let context = DispatchProxy.Create<IGrainContext, LibraryTestGrainContextProxy>()

        (context :?> LibraryTestGrainContextProxy).GrainId <- GrainId.Create(
            GrainType.Create("Grace.RepositoryLibraryActor"),
            GrainIdKeyExtensions.CreateGuidKey(repositoryId)
        )

        typeof<Grain>
            .GetProperty(
                "GrainContext",
                BindingFlags.Instance
                ||| BindingFlags.Public
                ||| BindingFlags.NonPublic
            )
            .SetValue(actor, context)

        actor :> Grace.Actors.Interfaces.IRepositoryLibraryActor, storage, provider, (fun () -> authorizationCalls), catalog

    let authorization = { OwnerId = Guid.Empty; OrganizationId = Guid.Empty; Principals = Array.empty; EffectiveClaims = Array.empty }

    /// The token key the memory-backed actor signs cursors with.
    let tokenKey = Array.create 32 7uy

    /// A Library root of 883 UTF-8 bytes, so a short name fits under the 1,024-byte limit and a long one doesn't.
    let longRoot =
        [ 'a'; 'b'; 'c'; 'd' ]
        |> List.map (fun letter -> String.replicate 220 (string letter))
        |> String.concat "/"

    /// The parent edge for items directly under longRoot.
    let longRootParent = { Kind = "root"; LibraryPath = Some longRoot; ItemId = None }

    /// A catalog whose only Library is longRoot.
    let longRootCatalog =
        {
            RepositoryId = repositoryId
            Version = catalogVersion
            Libraries = [| longRoot |]
            CreatedAt = timestamp
            CreatedBy = "user:test"
            PreviousVersion = None
        }

    /// Builds control for a catalog at a committed cursor, with no saved decision.
    let controlWith catalog committed items slots =
        { SchemaVersion = 1; Catalog = catalog; CommittedCursor = committed; Pending = None; ItemRecordCount = items; SlotRecordCount = slots }

    /// Builds a real RepositoryLibraryActor over writable in-memory storage seeded with control and any other records.
    let memoryActor (control: LibraryControlDocument) (seed: LibraryMemoryTestStorage -> unit) =
        let storage = LibraryMemoryTestStorage()
        storage.Store("Grace.Library.Control.v2", repositoryId.ToString("D"), control)
        seed storage
        let services = ServiceCollection()

        for storageName in
            [|
                LibraryRecords.ControlStorageName
                LibraryRecords.ChangesStorageName
                LibraryRecords.CurrentStorageName
                LibraryRecords.ReceiptsStorageName
                LibraryRecords.BaselinesStorageName
            |] do
            services.AddKeyedSingleton<IGrainStorage>(storageName, storage)
            |> ignore

        let provider = services.BuildServiceProvider()

        let authorize =
            Func<RepositoryId, LibraryWriteAuthorization, CancellationToken, Task<PermissionCheckResult>>(fun _ _ _ -> Task.FromResult(Allowed "test"))

        let actor = RepositoryLibraryActor(provider, null, authorize, tokenKey)
        let context = DispatchProxy.Create<IGrainContext, LibraryTestGrainContextProxy>()

        (context :?> LibraryTestGrainContextProxy).GrainId <- GrainId.Create(
            GrainType.Create("Grace.RepositoryLibraryActor"),
            GrainIdKeyExtensions.CreateGuidKey(repositoryId)
        )

        typeof<Grain>
            .GetProperty(
                "GrainContext",
                BindingFlags.Instance
                ||| BindingFlags.Public
                ||| BindingFlags.NonPublic
            )
            .SetValue(actor, context)

        actor :> Grace.Actors.Interfaces.IRepositoryLibraryActor, storage, provider

    /// Seeds one live current item.
    let seedItem (storage: LibraryMemoryTestStorage) (item: LibraryItemDto) =
        storage.Store(
            "Grace.Library.Item.v2",
            LibraryRecords.key [ repositoryId.ToString("D")
                                 "item"
                                 item.ItemId.ToString("D") ],
            { SchemaVersion = 1; Item = item; LastCursor = 1L }
        )

    /// Seeds one current slot record.
    let seedSlot (storage: LibraryMemoryTestStorage) (slot: LibraryNamespaceSlotDto) =
        storage.Store(
            "Grace.Library.Slot.v2",
            LibraryRecords.key [ repositoryId.ToString("D")
                                 "slot"
                                 LibraryDecision.slotKey slot.Parent slot.Name ],
            { SchemaVersion = 1; Slot = slot; LastCursor = 1L }
        )

    /// Builds a live item directly under a parent.
    let liveItem itemId kind parent name namespaceVersion =
        {
            ItemId = itemId
            ItemKind = kind
            LastChangeCursor = "cursor-1"
            Namespace = Some { Parent = parent; Name = name; NamespaceVersion = namespaceVersion }
            Content = None
            ContentRevision = None
            Tombstone = None
        }

    /// Builds a create-directory command that expects the slot's first vacancy.
    let createDirectory operation (name: string) =
        LibraryChangeCommand.CreateDirectory(
            operation,
            $"create-{name.Length}-{operation:N}",
            {
                Parent = longRootParent
                Name = name
                ExpectedSlotVersion = LibraryDecision.initialSlotVersion repositoryId longRootParent name
                ExpectedState = "vacant"
            }
        )

    /// Returns a successful receipt or fails the test with the actor's error.
    let receiptOf (result: Result<LibraryOperationReceiptDto, string>) =
        match result with
        | Ok receipt -> receipt
        | Error reason -> failwith $"Submit failed: {reason}"

    /// Verifies current denial prevents both permanent catalog and item receipt disclosure inside the actor turn.
    [<Test>]
    member _.CurrentPermissionDenialPrecedesStoredResultReplay() =
        task {
            let catalogResult =
                {
                    OperationId = operationId
                    Outcome = OutcomeKind.Accepted
                    LibraryCatalog = LibraryCatalogDto.CreateInitial(repositoryId, timestamp, "user:test")
                    ReasonCode = None
                    RecordedAt = timestamp
                }

            let catalogReceipt =
                { SchemaVersion = 1; OperationId = operationId; RequestHash = "catalog-request"; Outcome = LibraryOperationOutcome.CatalogResult catalogResult }

            let deniedReason = "Library permission was revoked before the actor turn."
            let catalogActor, catalogStorage, catalogServices, catalogAuthorizationCalls, catalog = replayActor (Denied deniedReason) catalogReceipt
            use _catalogServices = catalogServices

            let! catalogReplay =
                catalogActor.ChangeCatalog
                    true
                    catalog.Version
                    "Library"
                    operationId
                    catalogReceipt.RequestHash
                    "user:test"
                    authorization
                    true
                    "corr-catalog-denied"

            match catalogReplay with
            | Error reason -> Assert.That(reason, Is.EqualTo(deniedReason))
            | Ok _ -> Assert.Fail("Denied catalog replay disclosed its stored result.")

            Assert.That(catalogAuthorizationCalls (), Is.EqualTo(1))
            Assert.That(catalogStorage.ReadCount, Is.EqualTo(0))

            let rejectedReceipt =
                {
                    OperationId = operationId
                    RequestHash = "submit-request"
                    Outcome = OutcomeKind.Rejected
                    Change = None
                    ReasonCode = Some "preparedContentExpired"
                    CurrentLibraryCatalog = Some catalog
                }

            let submitReceipt =
                {
                    SchemaVersion = 1
                    OperationId = operationId
                    RequestHash = rejectedReceipt.RequestHash
                    Outcome = LibraryOperationOutcome.RejectedChange rejectedReceipt
                }

            let command =
                LibraryChangeCommand.CreateDirectory(
                    operationId,
                    rejectedReceipt.RequestHash,
                    {
                        Parent = { Kind = "root"; LibraryPath = Some "Library"; ItemId = None }
                        Name = "denied"
                        ExpectedSlotVersion = Guid.Empty
                        ExpectedState = "vacant"
                    }
                )

            let submitActor, submitStorage, submitServices, submitAuthorizationCalls, _ = replayActor (Denied deniedReason) submitReceipt
            use _submitServices = submitServices
            let! submitReplay = submitActor.Submit command "user:test" authorization "corr-submit-denied"

            match submitReplay with
            | Error reason -> Assert.That(reason, Is.EqualTo(deniedReason))
            | Ok _ -> Assert.Fail("Denied item replay disclosed its stored receipt.")

            Assert.That(submitAuthorizationCalls (), Is.EqualTo(1))
            Assert.That(submitStorage.ReadCount, Is.EqualTo(0))
        }

    /// Verifies current permission allows exact catalog and item receipt replay through the same actor boundary.
    [<Test>]
    member _.CurrentPermissionAllowsExactStoredResultReplay() =
        task {
            let catalogResult =
                {
                    OperationId = operationId
                    Outcome = OutcomeKind.Accepted
                    LibraryCatalog = LibraryCatalogDto.CreateInitial(repositoryId, timestamp, "user:test")
                    ReasonCode = None
                    RecordedAt = timestamp
                }

            let catalogReceipt =
                { SchemaVersion = 1; OperationId = operationId; RequestHash = "catalog-request"; Outcome = LibraryOperationOutcome.CatalogResult catalogResult }

            let catalogActor, catalogStorage, catalogServices, catalogAuthorizationCalls, catalog = replayActor (Allowed "current") catalogReceipt
            use _catalogServices = catalogServices

            let! catalogReplay =
                catalogActor.ChangeCatalog
                    true
                    catalog.Version
                    "Library"
                    operationId
                    catalogReceipt.RequestHash
                    "user:test"
                    authorization
                    true
                    "corr-catalog-allowed"

            match catalogReplay with
            | Ok result -> Assert.That(result, Is.EqualTo(catalogResult))
            | Error reason -> Assert.Fail($"Allowed catalog replay failed: {reason}")

            Assert.That(catalogAuthorizationCalls (), Is.EqualTo(1))
            Assert.That(catalogStorage.ReadCount, Is.GreaterThan(0))

            let rejectedReceipt =
                {
                    OperationId = operationId
                    RequestHash = "submit-request"
                    Outcome = OutcomeKind.Rejected
                    Change = None
                    ReasonCode = Some "preparedContentExpired"
                    CurrentLibraryCatalog = Some catalog
                }

            let submitReceipt =
                {
                    SchemaVersion = 1
                    OperationId = operationId
                    RequestHash = rejectedReceipt.RequestHash
                    Outcome = LibraryOperationOutcome.RejectedChange rejectedReceipt
                }

            let command =
                LibraryChangeCommand.CreateDirectory(
                    operationId,
                    rejectedReceipt.RequestHash,
                    {
                        Parent = { Kind = "root"; LibraryPath = Some "Library"; ItemId = None }
                        Name = "allowed"
                        ExpectedSlotVersion = Guid.Empty
                        ExpectedState = "vacant"
                    }
                )

            let submitActor, submitStorage, submitServices, submitAuthorizationCalls, _ = replayActor (Allowed "current") submitReceipt
            use _submitServices = submitServices
            let! submitReplay = submitActor.Submit command "user:test" authorization "corr-submit-allowed"

            match submitReplay with
            | Ok receipt -> Assert.That(receipt, Is.EqualTo(rejectedReceipt))
            | Error reason -> Assert.Fail($"Allowed item replay failed: {reason}")

            Assert.That(submitAuthorizationCalls (), Is.EqualTo(1))
            Assert.That(submitStorage.ReadCount, Is.GreaterThan(0))
        }

    /// Conflict names stay portable for Unicode input, huge extensions, and numbered retries.
    [<Test>]
    member _.ConflictNamesRemainBoundedAndDeterministicAcrossRetries() =
        let original =
            String.replicate 100 "😀"
            + "."
            + String.replicate 300 "x"

        let first = Grace.Shared.Validation.Library.conflictName original operationId 0
        let second = Grace.Shared.Validation.Library.conflictName original operationId 12

        Assert.Multiple(
            Action (fun () ->
                Assert.That(Encoding.UTF8.GetByteCount first, Is.LessThanOrEqualTo(Library.MaximumSegmentBytes))
                Assert.That(Encoding.UTF8.GetByteCount second, Is.LessThanOrEqualTo(Library.MaximumSegmentBytes))
                Assert.That(first, Does.EndWith(".conflict-6aa16ee48"))
                Assert.That(second, Does.EndWith(".conflict-6aa16ee48.12"))
                Assert.That(first, Is.Not.EqualTo(second))
                Assert.That(first.Contains("\uFFFD", StringComparison.Ordinal), Is.False))
        )

    /// Directory moves reject a descendant path that exceeds the repository-relative UTF-8 limit.
    [<Test>]
    member _.MovedDescendantsMustStayWithinOwnedPortablePaths() =
        let root = { Kind = "root"; LibraryPath = Some "Media"; ItemId = None }
        let movingId = Guid.Parse("66d071fb-435f-4d67-ad42-67a602691868")

        let catalog =
            {
                RepositoryId = repositoryId
                Version = catalogVersion
                Libraries = [| "Media" |]
                CreatedAt = timestamp
                CreatedBy = "user:test"
                PreviousVersion = None
            }

        let makeDirectory id parent name =
            {
                SchemaVersion = 2
                LastCursor = 1L
                Item =
                    {
                        ItemId = id
                        ItemKind = ItemKind.Directory
                        LastChangeCursor = "cursor"
                        Namespace = Some { Parent = parent; Name = name; NamespaceVersion = Guid.NewGuid() }
                        Content = None
                        ContentRevision = None
                        Tombstone = None
                    }
            }

        let child1 = Guid.Parse("6d6f23dc-87f8-4f60-9ed6-49fd1f6f5371")
        let child2 = Guid.Parse("8d4f627a-e209-4b59-82f8-103932c95017")
        let child3 = Guid.Parse("14cd1da7-2020-4dc2-9995-517c71c4ec02")
        let child4 = Guid.Parse("ed7465d8-a3e8-419b-b7dc-716f4d9fccd7")
        let longName = String.replicate 250 "x"

        let validDocuments =
            [|
                makeDirectory child1 { Kind = "item"; LibraryPath = None; ItemId = Some movingId } "child"
                makeDirectory child2 { Kind = "item"; LibraryPath = None; ItemId = Some child1 } "grandchild"
            |]

        let oversizedDocuments =
            [|
                makeDirectory child1 { Kind = "item"; LibraryPath = None; ItemId = Some movingId } longName
                makeDirectory child2 { Kind = "item"; LibraryPath = None; ItemId = Some child1 } longName
                makeDirectory child3 { Kind = "item"; LibraryPath = None; ItemId = Some child2 } longName
                makeDirectory child4 { Kind = "item"; LibraryPath = None; ItemId = Some child3 } longName
                makeDirectory (Guid.NewGuid()) { Kind = "item"; LibraryPath = None; ItemId = Some child4 } longName
            |]

        Assert.Multiple(
            Action (fun () ->
                Assert.That(LibraryDecision.subtreeRejection catalog "Media" movingId "moved" validDocuments, Is.EqualTo(None))

                Assert.That(LibraryDecision.subtreeRejection catalog "Media" movingId "moved" oversizedDocuments, Is.EqualTo(Some RejectionReason.PathTooLong))

                Assert.That(LibraryDecision.subtreeRejection catalog "Elsewhere" movingId "moved" validDocuments, Is.EqualTo(Some OutcomeKind.StalePolicy)))
        )

    /// A wake that throws, even before its first await, goes to the failure handler and never to the caller.
    [<Test>]
    member _.WakeFailureIsReportedAndNeverThrown() =
        let failures = Collections.Concurrent.ConcurrentQueue<exn>()

        LibraryWake.start (fun () -> Task.FromException(InvalidOperationException "broker down")) failures.Enqueue
        LibraryWake.start (fun () -> raise (InvalidOperationException "thrown before await")) failures.Enqueue
        LibraryWake.start (fun () -> raise (InvalidOperationException "and the logger fails too")) (fun _ -> failwith "logger down")

        // Failures are reported from the thread pool, after start has already returned.
        SpinWait.SpinUntil((fun () -> failures.Count >= 2), TimeSpan.FromSeconds 10.0)
        |> ignore

        Assert.That(failures |> Seq.map (fun ex -> ex.Message), Is.EquivalentTo([ "broker down"; "thrown before await" ]))

    /// A wake that never finishes doesn't hold up the caller.
    [<Test>]
    member _.WakeThatNeverFinishesDoesNotBlock() =
        let never = TaskCompletionSource()
        let started = Diagnostics.Stopwatch.StartNew()
        LibraryWake.start (fun () -> never.Task) (fun _ -> Assert.Fail("A pending wake isn't a failure."))

        // A send that blocks before it even returns a task, like a slow client start, still can't hold up the caller.
        LibraryWake.start
            (fun () ->
                Thread.Sleep(TimeSpan.FromSeconds 5.0)
                Task.CompletedTask)
            ignore

        Assert.That(started.Elapsed, Is.LessThan(TimeSpan.FromSeconds 1.0))

    /// Status is read from storage without activating the actor, because activation repairs a saved decision. It reports the decision as stored.
    [<Test>]
    member _.StatusReadsSavedDecisionWithoutRepairingIt() =
        task {
            let control = { controlWith longRootCatalog 0L 0 0 with Pending = Some(LibraryPendingDecision.ItemChange(acceptedRecord 1L)) }
            let _, storage, provider = memoryActor control ignore
            use _provider = provider

            let! status = LibraryQueries.readStatus provider repositoryId

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(status.State, Is.EqualTo("repairing"))
                    Assert.That(status.PendingOperationCount, Is.EqualTo(1))
                    Assert.That(storage.WriteCount, Is.Zero, "Reading status must not write anything."))
            )
        }

    /// Every save and reference request reads the catalog, so with no saved decision the read costs one control read.
    [<Test>]
    member _.CatalogReadWithNoSavedDecisionReadsControlOnce() =
        task {
            let actor, storage, provider = memoryActor (controlWith longRootCatalog 0L 0 0) ignore
            use _provider = provider

            let! catalog = actor.GetCatalog "catalog-read"

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(catalog, Is.EqualTo(longRootCatalog))
                    Assert.That(storage.ReadCount "Grace.Library.Control.v2", Is.EqualTo(1)))
            )
        }

    /// Reading the catalog finishes a saved catalog decision first, so the caller never sees the catalog from before it.
    [<Test>]
    member _.CatalogReadFinishesSavedCatalogDecisionFirst() =
        task {
            let initial = LibraryCatalogDto.CreateInitial(repositoryId, timestamp, "user:test")
            let chosenVersion = Guid.Parse("5b0f3c43-7f3c-4f39-9d1e-0d7f7d1b7d2a")

            let pending =
                LibraryPendingDecision.CatalogChange(operationId, "request-add", initial.Version, chosenVersion, true, "Shared", timestamp, "user:test")

            let actor, storage, provider = memoryActor { controlWith initial 0L 0 0 with Pending = Some pending } ignore
            use _provider = provider

            let! catalog = actor.GetCatalog "catalog-read"

            let stored = storage.TryGet<LibraryControlDocument>("Grace.Library.Control.v2", repositoryId.ToString("D"))

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(catalog.Libraries, Is.EqualTo([| "Shared" |]))
                    Assert.That(catalog.Version, Is.EqualTo(chosenVersion))
                    Assert.That(storage.ReadCount "Grace.Library.Control.v2", Is.EqualTo(2), "One read to find the decision, one after finishing it.")

                    Assert.That(
                        stored
                        |> Option.exists (fun control -> control.Pending.IsNone),
                        Is.True,
                        "The saved decision is finished."
                    ))
            )
        }

    /// A cursor ahead of the committed cursor, or one this repository didn't issue, is an error rather than "current".
    [<Test>]
    member _.ChangesRefuseCursorsTheServerDidNotIssueOrHasNotReached() =
        task {
            let actor, _, provider = memoryActor (controlWith longRootCatalog 3L 0 0) ignore
            use _provider = provider

            let! ahead = actor.GetChanges (LibraryTokens.cursor tokenKey repositoryId 5L) 100 "corr-ahead"
            let! foreign = actor.GetChanges (LibraryTokens.cursor tokenKey (Guid.NewGuid()) 1L) 100 "corr-foreign"
            let! unreadable = actor.GetChanges "not-a-cursor" 100 "corr-unreadable"
            let! missing = actor.GetChanges null 100 "corr-null"

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(ahead, Is.EqualTo(Error LibraryCursorError.AheadOfServer: Result<LibraryChangePageDto, string>))
                    Assert.That(foreign, Is.EqualTo(Error LibraryCursorError.NotIssuedHere: Result<LibraryChangePageDto, string>))
                    Assert.That(unreadable, Is.EqualTo(Error LibraryCursorError.NotIssuedHere: Result<LibraryChangePageDto, string>))
                    Assert.That(missing, Is.EqualTo(Error LibraryCursorError.NotIssuedHere: Result<LibraryChangePageDto, string>)))
            )
        }

    /// Adding a Library is decided without loading any items; only removal needs to look at what's under a root.
    [<Test>]
    member _.AddingLibraryLoadsNoItems() =
        task {
            let catalog = LibraryCatalogDto.CreateInitial(repositoryId, timestamp, "user:test")
            let actor, storage, provider = memoryActor (controlWith catalog 0L 0 0) ignore
            use _provider = provider

            let! result = actor.ChangeCatalog true catalog.Version "Shared" operationId "add-shared" "user:test" authorization true "corr-add"

            match result with
            | Ok added ->
                Assert.That(added.Outcome, Is.EqualTo(OutcomeKind.Accepted))
                Assert.That(added.LibraryCatalog.Libraries, Is.EqualTo([| "Shared" |]))

                let stored = storage.TryGet<LibraryControlDocument>("Grace.Library.Control.v2", repositoryId.ToString("D"))

                Assert.That(
                    stored
                    |> Option.map (fun control -> control.Catalog),
                    Is.EqualTo(Some added.LibraryCatalog)
                )
            | Error reason -> Assert.Fail($"Adding a Library failed: {reason}")
        }

    /// A created directory whose full path would pass 1,024 bytes is rejected; a shorter one is accepted.
    [<Test>]
    member _.CreateRejectsResultingPathOverLimit() =
        task {
            let actor, _, provider = memoryActor (controlWith longRootCatalog 0L 0 0) ignore
            use _provider = provider

            let! tooLong = actor.Submit (createDirectory (Guid.NewGuid()) (String.replicate 150 "n")) "user:test" authorization "corr-long"
            let! fits = actor.Submit (createDirectory (Guid.NewGuid()) (String.replicate 100 "n")) "user:test" authorization "corr-fits"

            Assert.Multiple(
                Action (fun () ->
                    Assert.That((receiptOf tooLong).ReasonCode, Is.EqualTo(Some RejectionReason.PathTooLong))
                    Assert.That((receiptOf fits).Outcome, Is.EqualTo(OutcomeKind.Accepted)))
            )
        }

    /// Paths are measured and stored with the catalog's spelling of a root, even when the caller's spelling matches it
    /// case-insensitively but is shorter in UTF-8. Here the catalog root uses "ⱥ" (3 bytes) and the caller sends "Ⱥ"
    /// (2 bytes), so a 70-byte name fits under the caller's spelling but not under the catalog's.
    [<Test>]
    member _.RootSpellingFromCatalogDecidesPathLimitAndStoredParent() =
        task {
            let segment letter = String.replicate 80 letter

            let catalogRoot =
                [
                    segment "ⱥ"
                    segment "ⱥ"
                    segment "ⱥ"
                    segment "ⱥ"
                ]
                |> String.concat "/"

            let callerRoot =
                [
                    segment "Ⱥ"
                    segment "Ⱥ"
                    segment "Ⱥ"
                    segment "Ⱥ"
                ]
                |> String.concat "/"

            Assume.That(String.Equals(catalogRoot, callerRoot, StringComparison.OrdinalIgnoreCase), Is.True)

            let catalog = { longRootCatalog with Libraries = [| catalogRoot |] }
            let actor, _, provider = memoryActor (controlWith catalog 0L 0 0) ignore
            use _provider = provider
            let callerParent = { Kind = "root"; LibraryPath = Some callerRoot; ItemId = None }
            let storedParent = { Kind = "root"; LibraryPath = Some catalogRoot; ItemId = None }

            let create (name: string) =
                LibraryChangeCommand.CreateDirectory(
                    Guid.NewGuid(),
                    $"create-{name}",
                    {
                        Parent = callerParent
                        Name = name
                        ExpectedSlotVersion = LibraryDecision.initialSlotVersion repositoryId storedParent name
                        ExpectedState = "vacant"
                    }
                )

            let! tooLong = actor.Submit (create (String.replicate 70 "n")) "user:test" authorization "corr-spelling-long"
            let! fits = actor.Submit (create "short") "user:test" authorization "corr-spelling-fits"
            let accepted = receiptOf fits

            Assert.Multiple(
                Action (fun () ->
                    Assert.That((receiptOf tooLong).ReasonCode, Is.EqualTo(Some RejectionReason.PathTooLong))
                    Assert.That(accepted.Outcome, Is.EqualTo(OutcomeKind.Accepted))
                    Assert.That(accepted.Change.Value.Item.Namespace.Value.Parent, Is.EqualTo(storedParent)))
            )
        }

    /// #1111 decision 1: a catalog change rejects a change only when it affects that change's own root. A save the
    /// client captured before another Library was added is still accepted; a change outside every current Library isn't.
    [<Test>]
    member _.CatalogChangeRejectsOnlyChangesWhoseOwnRootIsGone() =
        task {
            let added = { longRootCatalog with Version = Guid.NewGuid(); Libraries = [| longRoot; "Added" |]; PreviousVersion = Some catalogVersion }

            let actor, _, provider = memoryActor (controlWith added 0L 0 0) ignore
            use _provider = provider

            let create parent (name: string) =
                LibraryChangeCommand.CreateDirectory(
                    Guid.NewGuid(),
                    $"create-{name}",
                    {
                        Parent = parent
                        Name = name
                        ExpectedSlotVersion = LibraryDecision.initialSlotVersion repositoryId parent name
                        ExpectedState = "vacant"
                    }
                )

            let gone = { Kind = "root"; LibraryPath = Some "Gone"; ItemId = None }
            let! kept = actor.Submit (create longRootParent "captured-before-addition") "user:test" authorization "corr-kept"
            let! outside = actor.Submit (create gone "outside") "user:test" authorization "corr-outside"

            Assert.Multiple(
                Action (fun () ->
                    Assert.That((receiptOf kept).Outcome, Is.EqualTo(OutcomeKind.Accepted))
                    Assert.That((receiptOf outside).ReasonCode, Is.EqualTo(Some OutcomeKind.StalePolicy)))
            )
        }

    /// Renaming a file to a name that makes its full path pass 1,024 bytes is rejected.
    [<Test>]
    member _.RenameRejectsResultingPathOverLimit() =
        task {
            let fileId = Guid.NewGuid()
            let namespaceVersion = Guid.NewGuid()
            let file = liveItem fileId ItemKind.File longRootParent "short.txt" namespaceVersion

            let seed storage =
                seedItem storage file
                seedSlot storage { Parent = longRootParent; Name = "short.txt"; SlotVersion = Guid.NewGuid(); OccupantItemId = Some fileId }

            let actor, _, provider = memoryActor (controlWith longRootCatalog 1L 1 1) seed
            use _provider = provider

            let rename =
                LibraryChangeCommand.Rename(
                    Guid.NewGuid(),
                    "rename-long",
                    fileId,
                    { ItemId = fileId; ExpectedNamespaceVersion = namespaceVersion },
                    String.replicate 150 "r"
                )

            let! receipt = actor.Submit rename "user:test" authorization "corr-rename"
            Assert.That((receiptOf receipt).ReasonCode, Is.EqualTo(Some RejectionReason.PathTooLong))
        }

    /// Moving a file into a directory whose path leaves no room for its name is rejected.
    [<Test>]
    member _.MoveRejectsResultingPathOverLimit() =
        task {
            let directoryId = Guid.NewGuid()
            let fileId = Guid.NewGuid()
            let fileNamespace = Guid.NewGuid()
            let directory = liveItem directoryId ItemKind.Directory longRootParent (String.replicate 120 "d") (Guid.NewGuid())
            let file = liveItem fileId ItemKind.File longRootParent (String.replicate 30 "f") fileNamespace

            let seed storage =
                seedItem storage directory
                seedItem storage file

            let actor, _, provider = memoryActor (controlWith longRootCatalog 2L 2 2) seed
            use _provider = provider

            let move =
                LibraryChangeCommand.Move(
                    Guid.NewGuid(),
                    "move-long",
                    fileId,
                    { ItemId = fileId; ExpectedNamespaceVersion = fileNamespace },
                    { Kind = "item"; LibraryPath = None; ItemId = Some directoryId }
                )

            let! receipt = actor.Submit move "user:test" authorization "corr-move"
            Assert.That((receiptOf receipt).ReasonCode, Is.EqualTo(Some RejectionReason.PathTooLong))
        }

    /// One capacity rule: a change is refused only when it would add an item record or a slot record past the limit.
    [<Test>]
    member _.RecordCapacityCountsOnlyRecordsAChangeAdds() =
        let full = LibraryDecision.MaximumRecordCount

        Assert.Multiple(
            Action (fun () ->
                Assert.That(LibraryDecision.exceedsRecordCapacity full 0 true false, Is.True)
                Assert.That(LibraryDecision.exceedsRecordCapacity 0 full false true, Is.True)
                Assert.That(LibraryDecision.exceedsRecordCapacity 0 full true false, Is.False)
                Assert.That(LibraryDecision.exceedsRecordCapacity full full false false, Is.False)
                Assert.That(LibraryDecision.exceedsRecordCapacity (full - 1) (full - 1) true true, Is.False))
        )

    /// At slot capacity, a create that needs a new slot record is refused, and one that reuses a vacated slot is not.
    [<Test>]
    member _.CreateAtSlotCapacityFollowsSharedRule() =
        task {
            let reusedName = "reused"
            let reusedVersion = Guid.NewGuid()

            let seed storage = seedSlot storage { Parent = longRootParent; Name = reusedName; SlotVersion = reusedVersion; OccupantItemId = None }

            let actor, _, provider = memoryActor (controlWith longRootCatalog 0L 0 LibraryDecision.MaximumRecordCount) seed
            use _provider = provider

            let! newSlot = actor.Submit (createDirectory (Guid.NewGuid()) "fresh") "user:test" authorization "corr-fresh"

            let reuse =
                LibraryChangeCommand.CreateDirectory(
                    Guid.NewGuid(),
                    "create-reused",
                    { Parent = longRootParent; Name = reusedName; ExpectedSlotVersion = reusedVersion; ExpectedState = "vacant" }
                )

            let! reused = actor.Submit reuse "user:test" authorization "corr-reused"

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(Result.isError newSlot, Is.True, "A new slot record past the limit must be refused.")
                    Assert.That((receiptOf reused).Outcome, Is.EqualTo(OutcomeKind.Accepted)))
            )
        }

    /// Waiting for a committed item to become visible backs off on a fixed schedule, then gives up.
    [<Test>]
    member _.VisibilityWaitBacksOffOnTheFixedScheduleThenFails() =
        task {
            let waits = ResizeArray<TimeSpan>()
            let checks = ref 0

            let! visible =
                LibraryQueries.retryUntil
                    LibraryQueries.VisibilityRetryDelays
                    (fun delay ->
                        waits.Add delay
                        Task.CompletedTask)
                    (fun () ->
                        checks.Value <- checks.Value + 1
                        Task.FromResult false)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(visible, Is.False)
                    Assert.That(checks.Value, Is.EqualTo(7), "One check before the first wait and one after each wait.")

                    Assert.That(
                        waits
                        |> Seq.map (fun value -> value.TotalMilliseconds),
                        Is.EqualTo(
                            box [| 250.0
                                   500.0
                                   750.0
                                   1000.0
                                   1500.0
                                   2000.0 |]
                        )
                    ))
            )
        }

    /// A visibility wait stops as soon as the committed item is visible, without waiting again.
    [<Test>]
    member _.VisibilityWaitStopsAtTheFirstVisibleCheck() =
        task {
            let waits = ResizeArray<TimeSpan>()
            let checks = ref 0

            let! visible =
                LibraryQueries.retryUntil
                    LibraryQueries.VisibilityRetryDelays
                    (fun delay ->
                        waits.Add delay
                        Task.CompletedTask)
                    (fun () ->
                        checks.Value <- checks.Value + 1
                        Task.FromResult(checks.Value = 3))

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(visible, Is.True)
                    Assert.That(checks.Value, Is.EqualTo(3))

                    Assert.That(
                        waits
                        |> Seq.map (fun value -> value.TotalMilliseconds),
                        Is.EqualTo(box [| 250.0; 500.0 |])
                    ))
            )
        }

    /// A temporarily visible later segment cannot move a change page beyond a missing committed cursor.
    [<Test>]
    member _.ChangePagesStopAtVisibilityGapAndResumeContiguously() =
        let first = ResizeArray<LibraryAcceptedChangeRecord>()
        let firstCursor, firstGap = LibraryQueries.appendContiguousChanges 200L 10 first [| acceptedRecord 201L |]
        let initialPage, initialPosition, initialHasMore = LibraryQueries.changePageWindow 199L 201L 10 (first.ToArray())
        let retry = ResizeArray<LibraryAcceptedChangeRecord>()

        let retryCursor, retryGap =
            LibraryQueries.appendContiguousChanges
                200L
                10
                retry
                [|
                    acceptedRecord 200L
                    acceptedRecord 201L
                |]

        let retryPage, retryPosition, retryHasMore = LibraryQueries.changePageWindow 199L 201L 10 (retry.ToArray())

        Assert.Multiple(
            Action (fun () ->
                Assert.That(first, Is.Empty)
                Assert.That(firstCursor, Is.EqualTo(200L))
                Assert.That(firstGap, Is.True)
                Assert.That(initialPage, Is.Empty)
                Assert.That(initialPosition, Is.EqualTo(199L))
                Assert.That(initialHasMore, Is.True)
                Assert.That(retry |> Seq.map (fun value -> value.Cursor), Is.EqualTo(box [| 200L; 201L |]))
                Assert.That(retryCursor, Is.EqualTo(202L))
                Assert.That(retryGap, Is.False)
                Assert.That(retryPage |> Array.map (fun value -> value.Cursor), Is.EqualTo(box [| 200L; 201L |]))
                Assert.That(retryPosition, Is.EqualTo(201L))
                Assert.That(retryHasMore, Is.False))
        )

    /// A valid preparation stops being consumable at its declared expiry boundary.
    [<Test>]
    member _.PreparedUploadExpiresAtThePersistedBoundary() =
        let upload = preparedUpload timestamp

        let before = LibraryTransfer.validatePreparedUpload (timestamp - Duration.FromTicks(1L)) repositoryId operationId "user:test" upload

        let atBoundary = LibraryTransfer.validatePreparedUpload timestamp repositoryId operationId "user:test" upload

        Assert.Multiple(
            Action (fun () ->
                match before with
                | Error reason -> Assert.Fail($"Expected valid preparation before expiry, got {reason}.")
                | Ok (binding, manifest) ->
                    Assert.That(binding.ExpiresAt, Is.EqualTo(timestamp))
                    Assert.That(manifest, Is.EqualTo(upload.FinalizedManifest.Value))

                match atBoundary with
                | Error reason -> Assert.That(reason, Is.EqualTo(RejectionReason.PreparedContentExpired))
                | Ok _ -> Assert.Fail("Expected the preparation to expire at its persisted boundary."))
        )

    /// An upload that expires during its awaited actor read is validated against the later observation time.
    [<Test>]
    member _.PreparedUploadExpiryIsObservedAfterAwaitedRead() =
        task {
            let upload = preparedUpload timestamp
            let readStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseRead = TaskCompletionSource<UploadSessionDto>(TaskCreationOptions.RunContinuationsAsynchronously)
            let mutable currentInstant = timestamp - Duration.FromTicks(1L)
            let mutable clockCalls = 0

            let validation =
                LibraryTransfer.readAndValidatePreparedUpload
                    (fun () ->
                        task {
                            readStarted.TrySetResult() |> ignore
                            return! releaseRead.Task
                        })
                    (fun () ->
                        clockCalls <- clockCalls + 1
                        currentInstant)
                    repositoryId
                    operationId
                    "user:test"

            do! readStarted.Task
            Assert.That(clockCalls, Is.Zero)
            currentInstant <- timestamp
            releaseRead.SetResult upload
            let! observedAt, returnedUpload, result = validation

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(clockCalls, Is.EqualTo(1))
                    Assert.That(observedAt, Is.EqualTo(timestamp))
                    Assert.That(returnedUpload, Is.EqualTo(upload))

                    match result with
                    | Error reason -> Assert.That(reason, Is.EqualTo(RejectionReason.PreparedContentExpired))
                    | Ok _ -> Assert.Fail("Expected the upload to expire while its actor read was awaiting."))
            )
        }

    /// Only the exact completed workflow can suppress a repeated tracked-manifest activation.
    [<Test>]
    member _.CompletedTrackedWorkflowMatchesExactOperationAndCounterRevision() =
        let manifest = preparedManifest ()
        let ranges = LibraryTransfer.workflowRanges manifest
        let counterOperationId = LibraryTransfer.counterOperationId operationId contentVersionId

        let completedRanges =
            ranges
            |> Array.mapi (fun index range ->
                {
                    OperationId = $"{counterOperationId}:fanout:revision:7:range:{index}:completed"
                    RepositoryId = repositoryId
                    StoragePoolId = manifest.StoragePoolId
                    ManifestAddress = manifest.ManifestAddress
                    Range = range
                })

        let completed =
            { ManifestContributionWorkflowDto.Default with
                RepositoryId = repositoryId
                StoragePoolId = manifest.StoragePoolId
                ManifestAddress = manifest.ManifestAddress
                Direction = ManifestContributionDirection.Increment
                Ranges = ranges
                CompletedRanges = completedRanges
                LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
                StartOperationId = Some $"{counterOperationId}:fanout"
                LastOperationId =
                    completedRanges
                    |> Array.tryLast
                    |> Option.map (fun value -> value.OperationId)
                CounterRevision = 7L
                Revision = 2L
            }

        Assert.Multiple(
            Action (fun () ->
                Assert.That(LibraryTransfer.workflowCompletedForTrackedManifest repositoryId counterOperationId manifest ranges completed, Is.True)

                Assert.That(LibraryTransfer.workflowCompletedForTrackedManifest repositoryId "different-operation" manifest ranges completed, Is.False)

                Assert.That(
                    LibraryTransfer.workflowCompletedForTrackedManifest repositoryId counterOperationId manifest ranges { completed with CounterRevision = 0L },
                    Is.False
                )

                Assert.That(
                    LibraryTransfer.workflowCompletedForTrackedManifest
                        repositoryId
                        counterOperationId
                        manifest
                        ranges
                        { completed with LifecycleState = ManifestContributionWorkflowLifecycleState.InProgress },
                    Is.False
                ))
        )

    /// Signed Library tokens reject a changed purpose, repository, payload, and expiry boundary.
    [<Test>]
    member _.LibraryTokensBindPurposeRepositoryPayloadAndExpiry() =
        let key = Array.init 32 (fun index -> byte (index + 1))
        let cursor = LibraryTokens.cursor key repositoryId 17L
        let page = LibraryTokens.page key "baseline" repositoryId "baseline-id" 3 200L
        let content = LibraryTokens.contentRead key repositoryId itemId contentVersionId "cursor-17" 200L

        let tamperedContent =
            content.Substring(0, content.Length - 1)
            + (if content.EndsWith("A", StringComparison.Ordinal) then "B" else "A")

        Assert.Multiple(
            Action (fun () ->
                Assert.That(LibraryTokens.tryCursor key repositoryId cursor, Is.EqualTo(Some 17L))

                Assert.That(
                    (LibraryTokens.tryCursor key (Guid.NewGuid()) cursor)
                        .IsNone,
                    Is.True
                )

                Assert.That(LibraryTokens.tryPage key "baseline" repositoryId 199L page, Is.EqualTo(Some("baseline-id", 3)))

                Assert.That(
                    (LibraryTokens.tryPage key "bootstrap" repositoryId 199L page)
                        .IsNone,
                    Is.True
                )

                Assert.That(
                    (LibraryTokens.tryPage key "baseline" repositoryId 200L page)
                        .IsNone,
                    Is.True
                )

                Assert.That(LibraryTokens.tryContentRead key 199L content, Is.EqualTo(Some(repositoryId, itemId, contentVersionId, "cursor-17")))

                Assert.That(
                    (LibraryTokens.tryContentRead key 200L content)
                        .IsNone,
                    Is.True
                )

                Assert.That(
                    (LibraryTokens.tryContentRead key 199L tamperedContent)
                        .IsNone,
                    Is.True
                ))
        )

    /// The production baseline packer streams the full item limit into deterministic byte-bounded shards, including tombstones.
    [<Test>]
    member _.BaselinePackerBoundsOneHundredThousandItemsAndReplaysExactly() =
        let namespaceVersion = Guid.Parse("1a591698-8f6f-40c1-9b6c-c2c7763b6df7")
        let parent = { Kind = "root"; LibraryPath = Some "Media"; ItemId = None }

        let item index =
            let idBytes = Array.zeroCreate<byte> 16

            BitConverter
                .GetBytes(index + 1)
                .CopyTo(idBytes, 0)

            let id = Guid idBytes

            let ns = { Parent = parent; Name = $"file-{index:D6}.bin"; NamespaceVersion = namespaceVersion }

            if index % 1000 = 0 then
                {
                    ItemId = id
                    ItemKind = ItemKind.File
                    LastChangeCursor = $"cursor-{index:D6}"
                    Namespace = None
                    Content = None
                    ContentRevision = None
                    Tombstone =
                        Some
                            {
                                DeletedAt = timestamp
                                DeletedBy = "user:baseline"
                                DeleteCursor = $"cursor-{index:D6}"
                                LastNamespace = ns
                                LastContentVersionId = Some contentVersionId
                            }
                }
            else
                {
                    ItemId = id
                    ItemKind = ItemKind.Directory
                    LastChangeCursor = $"cursor-{index:D6}"
                    Namespace = Some ns
                    Content = None
                    ContentRevision = None
                    Tombstone = None
                }

        let pack () =
            let current = ResizeArray<LibraryItemDto>()
            let fingerprints = ResizeArray<int * int * string>()
            let mutable itemCount = 0
            let mutable tombstoneCount = 0
            let mutable currentBytes = LibraryQueries.emptyBaselineShardBytes
            let mutable maximumPendingBytes = currentBytes

            let recordShard shard =
                let bytes = LibraryQueries.serializeBaselineShard shard
                fingerprints.Add((shard.Items.Length, bytes.Length, ContentAddress.computeBlake3Hex bytes))

                Assert.That(bytes.Length, Is.LessThanOrEqualTo(LibraryQueries.BaselineShardMaximumBytes))

            for index in 0..99_999 do
                let value = item index
                itemCount <- itemCount + 1
                if value.Tombstone.IsSome then tombstoneCount <- tombstoneCount + 1

                let nextBytes, completed = LibraryQueries.appendBaselineItem current currentBytes value
                currentBytes <- nextBytes
                maximumPendingBytes <- Math.Max(maximumPendingBytes, currentBytes)

                match completed with
                | Some shard -> recordShard shard
                | None -> ()

            match LibraryQueries.finishBaselineShard current currentBytes with
            | Some shard -> recordShard shard
            | None -> ()

            itemCount, tombstoneCount, maximumPendingBytes, fingerprints.ToArray()

        let firstCount, firstTombstones, firstMaximumPendingBytes, first = pack ()
        let secondCount, secondTombstones, secondMaximumPendingBytes, second = pack ()
        let totalSerializedBytes = first |> Array.sumBy (fun (_, bytes, _) -> bytes)

        TestContext.Out.WriteLine(
            $"Library baseline packer: items={firstCount}; tombstones={firstTombstones}; shards={first.Length}; largestPendingShardBytes={firstMaximumPendingBytes}; totalSerializedShardBytes={totalSerializedBytes}"
        )

        Assert.Multiple(
            Action (fun () ->
                Assert.That(firstCount, Is.EqualTo(100_000))
                Assert.That(firstTombstones, Is.EqualTo(100))
                Assert.That(first.Length, Is.GreaterThan(1))
                Assert.That(first |> Array.sumBy (fun (count, _, _) -> count), Is.EqualTo(100_000))
                Assert.That(firstMaximumPendingBytes, Is.LessThanOrEqualTo(LibraryQueries.BaselineShardMaximumBytes))
                Assert.That(secondCount, Is.EqualTo(firstCount))
                Assert.That(secondTombstones, Is.EqualTo(firstTombstones))
                Assert.That(secondMaximumPendingBytes, Is.EqualTo(firstMaximumPendingBytes))
                Assert.That(second, Is.EqualTo(box first)))
        )
