namespace Grace.Server.Tests

open Grace.Actors
open Grace.Server
open Grace.Types.Authorization
open Grace.Types.Common
open Grace.Types.Library
open Grace.Types.UploadSession
open Grace.Shared.Validation.Library
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks

/// Stores Library coordinator test state in memory and injects one durable-boundary failure.
type private FailingLibraryStore(initialControl: LibraryControlDocument, failurePoint: string) =

    let mutable control = initialControl
    let mutable etag = 0
    let mutable pendingFailure = Some failurePoint
    let canonicals = Dictionary<int64, LibraryCanonicalChangeDocument>()
    let items = Dictionary<LibraryItemId, LibraryCurrentItemDocument>()
    let slots = Dictionary<string, LibraryCurrentSlotDocument>(StringComparer.OrdinalIgnoreCase)
    let receipts = Dictionary<LibraryOperationId, LibraryReceiptDocument>()
    let catalogOperations = Dictionary<LibraryOperationId, LibraryCatalogOperationDocument>()
    let histories = HashSet<string>(StringComparer.Ordinal)

    /// Raises the configured failure once at its exact durable effect boundary.
    let failOnce point =
        if pendingFailure = Some point then
            pendingFailure <- None
            raise (InvalidOperationException(sprintf "Injected %s failure." point))

    /// Returns the current control snapshot for ordering assertions.
    member _.Control = control

    /// Returns the number of unique durable records retained by the fake store.
    member _.Counts = canonicals.Count, items.Count, slots.Count, receipts.Count, histories.Count

    interface ILibraryStore with

        member _.EnsureControlAsync(_repositoryId, _libraryCatalog, _cancellationToken) = Task.FromResult { Document = control; ETag = string etag }

        member _.ReadControlAsync(_repositoryId, _cancellationToken) = Task.FromResult { Document = control; ETag = string etag }

        member _.ReplaceControlAsync(replacement, expectedEtag, _cancellationToken) =
            task {
                failOnce "control"

                if expectedEtag <> string etag then
                    return PreconditionFailed
                else
                    control <- replacement
                    etag <- etag + 1
                    return Replaced(string etag)
            }

        member _.ReadCatalogOperationAsync(_repositoryId, operationId, _cancellationToken) =
            match catalogOperations.TryGetValue operationId with
            | true, operation -> Task.FromResult(Some operation)
            | false, _ -> Task.FromResult None

        member _.ReplaceControlAndCreateCatalogOperationAsync(replacement, expectedEtag, operation, _cancellationToken) =
            task {
                if expectedEtag <> string etag
                   || catalogOperations.ContainsKey operation.OperationId then
                    return PreconditionFailed
                else
                    control <- replacement
                    catalogOperations.Add(operation.OperationId, operation)
                    etag <- etag + 1
                    return Replaced(string etag)
            }

        member _.CreateCatalogOperationAsync(operation, _cancellationToken) =
            task {
                match catalogOperations.TryGetValue operation.OperationId with
                | true, existing when existing = operation -> ()
                | true, _ -> invalidOp "Catalog operation identity was reused for different content."
                | false, _ -> catalogOperations.Add(operation.OperationId, operation)
            }
            :> Task

        member _.ReadReceiptAsync(_repositoryId, operationId, _cancellationToken) =
            match receipts.TryGetValue operationId with
            | true, receipt -> Task.FromResult(Some receipt)
            | false, _ -> Task.FromResult None

        member _.ReadCanonicalAsync(_repositoryId, cursor, _cancellationToken) =
            match canonicals.TryGetValue cursor with
            | true, change -> Task.FromResult(Some change)
            | false, _ -> Task.FromResult None

        member _.CreateCanonicalAsync(change, _cancellationToken) =
            task {
                failOnce "canonical"

                match canonicals.TryGetValue change.Cursor with
                | true, existing when existing = change -> ()
                | true, _ -> invalidOp "Canonical identity was reused for different content."
                | false, _ -> canonicals.Add(change.Cursor, change)
            }
            :> Task

        member _.ReadItemAsync(_repositoryId, itemId, _cancellationToken) =
            match items.TryGetValue itemId with
            | true, item -> Task.FromResult(Some item)
            | false, _ -> Task.FromResult None

        member _.ReadSlotAsync(_repositoryId, normalizedPath, _cancellationToken) =
            match slots.TryGetValue normalizedPath with
            | true, slot -> Task.FromResult(Some slot)
            | false, _ -> Task.FromResult None

        member _.EnsureCurrentProjectionCapacityAsync(_repositoryId, _itemId, _normalizedPath, _cancellationToken) = task { failOnce "capacity" } :> Task

        member _.UpsertItemAsync(item, _cancellationToken) =
            task {
                failOnce "item"
                items[item.Item.ItemId] <- item
            }
            :> Task

        member _.UpsertSlotAsync(slot, _cancellationToken) =
            task {
                failOnce "slot"
                slots[slot.Slot.NormalizedPath] <- slot
            }
            :> Task

        member _.UpsertReceiptAsync(receipt, _cancellationToken) =
            task {
                failOnce "receipt"
                receipts[receipt.OperationId] <- receipt
            }
            :> Task

        member _.AppendItemHistoryAsync(_repositoryId, itemId, entry, _cancellationToken) =
            task {
                failOnce "history"

                histories.Add(sprintf "item:%O:%d" itemId entry.Cursor)
                |> ignore
            }
            :> Task

        member _.AppendPathHistoryAsync(_repositoryId, normalizedPath, entry, _cancellationToken) =
            task {
                failOnce "history"

                histories.Add(sprintf "path:%s:%d" normalizedPath entry.Cursor)
                |> ignore
            }
            :> Task

        member _.ReadChangesAsync(_repositoryId, afterCursor, maximumCount, _cancellationToken) =
            canonicals.Values
            |> Seq.filter (fun change -> change.Cursor > afterCursor)
            |> Seq.sortBy (fun change -> change.Cursor)
            |> Seq.truncate maximumCount
            |> Seq.toArray
            |> Task.FromResult

        member _.ReadCurrentItemsAsync(_repositoryId, _cancellationToken) = items.Values |> Seq.toArray |> Task.FromResult

        member _.HasLiveDescendantsAsync(_repositoryId, _normalizedDirectoryPath, _cancellationToken) = Task.FromResult false

        member _.EnsureBaselineAsync(_repositoryId, _boundaryCursor, _cursorEpoch, _libraryCatalog, _items, _cancellationToken) =
            Task.FromException<LibraryBaselineManifestDocument>(NotSupportedException())

        member _.ReadBaselineAsync(_repositoryId, _baselineId, _cancellationToken) = Task.FromResult None

/// Covers canonical-first publication and restart repair at every durable effect boundary.
[<Parallelizable(ParallelScope.All)>]
type LibraryCoordinatorTests() =

    /// Builds one live directory item for deterministic baseline tests.
    let baselineItem itemId rootVersion normalizedPath =
        let namespaceValue =
            {
                Parent = { Kind = "root"; LibraryPath = Some "shared"; ItemId = None }
                Name = normalizedPath
                NormalizedPath = normalizedPath
                NamespaceVersion = Guid.NewGuid()
                SlotVersion = Guid.NewGuid()
            }

        {
            ItemId = itemId
            ItemKind = ItemKind.Directory
            State = "live"
            LastChangeCursor = "cursor"
            LibraryCatalogVersion = rootVersion
            Namespace = Some namespaceValue
            Content = None
            Tombstone = None
        }

    /// Builds one completely determined accepted directory creation reservation.
    let pendingFixture () =
        let repositoryId = Guid.Parse "812569f8-705f-5b55-9d9e-1b240724dba4"
        let operationId = Guid.Parse "57d02e0b-ecc7-5dd4-b841-25143d652150"
        let itemId = Guid.Parse "4a946fb8-c26b-5546-a6df-e6fa2ab0ee35"
        let rootVersion = Guid.Parse "51552847-28f7-5264-8147-9f09069df0d0"
        let namespaceVersion = Guid.Parse "8ceaf91d-c112-5623-8fdc-f3aba0bb8576"
        let slotVersion = Guid.Parse "5fd6809e-ed68-5dfa-8390-af55ee63f62e"
        let now = Instant.FromUtc(2026, 8, 27, 12, 0)
        let publicCursor = "protected-cursor-1"

        let libraryCatalog =
            { RepositoryId = repositoryId; Version = rootVersion; Libraries = [| "shared" |]; CreatedAt = now; CreatedBy = "principal"; PreviousVersion = None }

        let namespaceValue =
            {
                Parent = { Kind = "root"; LibraryPath = Some "shared"; ItemId = None }
                Name = "docs"
                NormalizedPath = "shared/docs"
                NamespaceVersion = namespaceVersion
                SlotVersion = slotVersion
            }

        let item =
            {
                ItemId = itemId
                ItemKind = ItemKind.Directory
                State = "live"
                LastChangeCursor = publicCursor
                LibraryCatalogVersion = rootVersion
                Namespace = Some namespaceValue
                Content = None
                Tombstone = None
            }

        let change =
            {
                Cursor = publicCursor
                OperationId = operationId
                ChangeKind = ChangeKind.CreateDirectory
                ItemId = itemId
                ItemKind = ItemKind.Directory
                AcceptedAt = now
                AcceptedBy = "principal"
                LibraryCatalogVersion = rootVersion
                Namespace = Some namespaceValue
                Content = None
                Tombstone = None
                Conflict = None
            }

        let receipt =
            {
                OperationId = operationId
                RequestHash = "request-hash"
                Outcome = OutcomeKind.Accepted
                LibraryCatalogVersion = rootVersion
                RecordedAt = now
                PrincipalId = "principal"
                Change = Some change
                Cursor = Some publicCursor
                Item = Some item
                Conflict = None
                ReasonCode = None
                CurrentLibraryCatalog = None
                Rebaseline = None
            }

        let canonical =
            {
                id = "change:1"
                RepositoryId = repositoryId
                StreamSegment = "segment:0"
                SchemaVersion = 1
                Cursor = 1L
                PublicCursor = publicCursor
                OperationId = operationId
                RequestHash = "request-hash"
                Change = change
                PriorNamespace = None
                PriorContentVersionId = None
                ConsumedNamespaceVersion = None
                ConsumedContentVersionId = None
                ConsumedSlotVersion = Some slotVersion
                CorrelationId = "correlation"
            }

        let pending =
            {
                OperationId = operationId
                RequestHash = "request-hash"
                Cursor = 1L
                Receipt = receipt
                CanonicalChange = canonical
                ExpectedLibraryCatalogVersion = rootVersion
                PrincipalId = "principal"
                CorrelationId = "correlation"
                ReservedAt = now
                TargetItemIds = [| itemId |]
            }

        let control =
            {
                id = "control"
                RepositoryId = repositoryId
                SchemaVersion = 1
                CursorEpoch = Guid.Parse "40a91aa6-40b3-5a19-83cf-c5fe1ca94517"
                NextCursor = 2L
                AppliedThrough = 0L
                ReplayFloor = 0L
                LibraryCatalog = libraryCatalog
                Pending = Some pending
                CurrentBaselineId = None
                CurrentBaselineCursor = None
                ProjectionWatermarks = LibraryProjectionWatermarks.Empty
                UpdatedAt = now
            }

        repositoryId, control

    [<TestCase("canonical")>]
    [<TestCase("item")>]
    [<TestCase("slot")>]
    [<TestCase("history")>]
    [<TestCase("receipt")>]
    [<TestCase("control")>]
    member _.RepairFailureDoesNotAdvanceControlAndConvergesOnRetry failurePoint =
        task {
            let repositoryId, control = pendingFixture ()
            let store = FailingLibraryStore(control, failurePoint)

            let firstAttempt =
                Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> LibraryCoordinator.repair store repositoryId CancellationToken.None :> Task))

            Assert.That(firstAttempt.Message, Does.Contain failurePoint)
            Assert.That(store.Control.Pending.IsSome, Is.True)
            Assert.That(store.Control.AppliedThrough, Is.EqualTo 0L)

            do! LibraryCoordinator.repair store repositoryId CancellationToken.None

            let canonicalCount, itemCount, slotCount, receiptCount, historyCount = store.Counts
            Assert.That(store.Control.Pending.IsNone, Is.True)
            Assert.That(store.Control.AppliedThrough, Is.EqualTo 1L)
            Assert.That(store.Control.ProjectionWatermarks.Current, Is.EqualTo 1L)
            Assert.That(store.Control.ProjectionWatermarks.History, Is.EqualTo 1L)
            Assert.That(store.Control.ProjectionWatermarks.Receipts, Is.EqualTo 1L)
            Assert.That(canonicalCount, Is.EqualTo 1)
            Assert.That(itemCount, Is.EqualTo 1)
            Assert.That(slotCount, Is.EqualTo 1)
            Assert.That(receiptCount, Is.EqualTo 1)
            Assert.That(historyCount, Is.EqualTo 2)
        }

    [<Test>]
    member _.CursorCodecRejectsAValidCursorFromAnotherRepository() =
        let codec = LibraryCoordinator.LibraryCursorCodec(Array.create 32 0x5Auy) :> ILibraryCursorCodec

        let firstRepository = Guid.Parse "f77563ee-2ab2-564f-bd21-2c80a2d945a7"
        let secondRepository = Guid.Parse "cdb8f257-6160-5165-b4de-1394a480c19a"
        let epoch = Guid.Parse "b6c6a22e-430b-5624-bdca-a4d0de57bd13"
        let cursor = codec.Encode(firstRepository, epoch, 42L)

        Assert.That(codec.TryDecode(firstRepository, cursor), Is.EqualTo(Some(epoch, 42L)))
        Assert.That(codec.TryDecode(secondRepository, cursor), Is.EqualTo None)

    [<Test>]
    member _.BaselinePlanIsDeterministicAndPublishesExactShardMetadata() =
        let repositoryId = Guid.Parse "23ae5574-a60c-54f1-bcbc-f4f5515d6cc9"
        let rootVersion = Guid.Parse "34b47a7e-f34b-52aa-8a64-24ae99ff193a"
        let cursorEpoch = Guid.Parse "a3664cc6-9534-54fd-a794-83744886627b"
        let now = Instant.FromUtc(2026, 8, 27, 18, 0)

        let libraryCatalog =
            { RepositoryId = repositoryId; Version = rootVersion; Libraries = [| "shared" |]; CreatedAt = now; CreatedBy = "principal"; PreviousVersion = None }

        let firstId = Guid.Parse "f83b3a34-5897-5cb0-bac0-c186412c4adc"
        let secondId = Guid.Parse "0a3d49d4-071b-505a-809a-b49cd4e445fd"

        let items =
            [|
                baselineItem firstId rootVersion "shared/zeta"
                baselineItem secondId rootVersion "shared/alpha"
            |]

        let firstManifest, firstShards = LibraryPersistence.buildBaselineDocuments repositoryId 23L cursorEpoch libraryCatalog items now

        let secondManifest, secondShards = LibraryPersistence.buildBaselineDocuments repositoryId 23L cursorEpoch libraryCatalog (Array.rev items) now

        Assert.That(secondManifest, Is.EqualTo firstManifest)
        Assert.That(secondShards, Is.EqualTo<LibraryBaselineShardDocument array>(firstShards))

        Assert.That(
            firstShards
            |> Array.collect (fun shard -> shard.Items)
            |> Array.map (fun item -> item.ItemId),
            Is.EqualTo<Guid array>([| secondId; firstId |])
        )

        Assert.That(firstManifest.ShardIds, Is.EqualTo<string array>(firstShards |> Array.map (fun shard -> shard.id)))

        Assert.That(
            firstManifest.ShardHashes,
            Is.EqualTo<string array>(
                firstShards
                |> Array.map LibraryPersistence.documentHash
            )
        )

        Assert.That(
            firstManifest.ShardItemCounts,
            Is.EqualTo<int array>(
                firstShards
                |> Array.map (fun shard -> shard.ItemCount)
            )
        )

        Assert.That(firstManifest.TotalItemCount, Is.EqualTo items.Length)

    [<Test>]
    member _.BaselinePlanKeepsEveryShardWithinTheOneMillionByteLimit() =
        let repositoryId = Guid.Parse "896dcd38-a724-5247-a976-de1776984301"
        let rootVersion = Guid.Parse "807ee08a-bbcf-551b-a684-498ae353d4bf"
        let now = Instant.FromUtc(2026, 8, 27, 18, 30)

        let libraryCatalog =
            { RepositoryId = repositoryId; Version = rootVersion; Libraries = [| "shared" |]; CreatedAt = now; CreatedBy = "principal"; PreviousVersion = None }

        let pathBody = String.replicate 490000 "x"
        let largePath suffix = $"shared/{pathBody}/{suffix}"

        let items =
            [|
                baselineItem (Guid.Parse "079966d6-8de4-501e-8db7-ed1189d55512") rootVersion (largePath "a")
                baselineItem (Guid.Parse "a5888377-934c-56d0-8f57-d38760c03f15") rootVersion (largePath "b")
                baselineItem (Guid.Parse "45f4df4f-aa6b-5f0f-a71c-b5c5a034e531") rootVersion (largePath "c")
            |]

        let manifest, shards = LibraryPersistence.buildBaselineDocuments repositoryId 41L Guid.Empty libraryCatalog items now

        Assert.That(shards.Length, Is.GreaterThan 1)

        Assert.That(
            shards
            |> Array.forall (fun shard ->
                shard.SerializedBytes
                <= LibraryPersistence.BaselineShardByteLimit),
            Is.True
        )

        Assert.That(
            shards
            |> Array.sumBy (fun shard -> shard.ItemCount),
            Is.EqualTo manifest.TotalItemCount
        )

    /// Verifies that root occupancy uses exact path segments across complete DirectoryVersion snapshots.
    [<Test>]
    member _.DirectoryVersionRootOwnershipUsesExactPathSegments() =
        let directoryVersion = DirectoryVersion()
        directoryVersion.RelativePath <- RelativePath "Shared/Docs"

        Assert.That(Library.directoryVersionOwnsRoot "shared" directoryVersion, Is.True)
        Assert.That(Library.directoryVersionOwnsRoot "shared/docs" directoryVersion, Is.True)
        Assert.That(Library.directoryVersionOwnsRoot "share" directoryVersion, Is.False)
        Assert.That(Library.directoryVersionOwnsRoot "shared/documentation" directoryVersion, Is.False)

    /// Verifies only a durable change receipt can produce the coarse content-free wake payload.
    [<Test>]
    member _.LibraryWakeRequiresAcceptedChangeReceipt() =
        let repositoryId, control = pendingFixture ()
        let receipt = control.Pending.Value.Receipt
        let codec = LibraryCoordinator.LibraryCursorCodec(Array.create 32 0x6Buy) :> ILibraryCursorCodec

        let wake =
            Library.libraryAvailableFromReceipt codec control receipt "correlation-id"
            |> Option.get

        Assert.Multiple(
            Action (fun () ->
                Assert.That(wake.EventName, Is.EqualTo("LibraryContentAvailable.v1"))
                Assert.That(wake.RepositoryId, Is.EqualTo(repositoryId))
                Assert.That(codec.TryDecode(repositoryId, wake.CursorEpoch), Is.EqualTo(Some(control.CursorEpoch, 0L)))
                Assert.That(wake.AvailableAfterCursor, Is.EqualTo(receipt.Cursor.Value))
                Assert.That(wake.LibraryCatalogVersion, Is.EqualTo(receipt.LibraryCatalogVersion))
                Assert.That(wake.CorrelationId, Is.EqualTo("correlation-id")))
        )

        let rejectedReceipt = { receipt with Outcome = OutcomeKind.Rejected; Change = None; Cursor = None }

        Assert.That(Library.libraryAvailableFromReceipt codec control rejectedReceipt "correlation-id", Is.EqualTo(None))

    /// Verifies change submission awaits the durable receipt before attempting its best-effort wake and returning success.
    [<Test>]
    member _.LibraryWakeFollowsDurableSubmitAndPrecedesResponse() =
        let sourcePath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Library.Server.fs"))
        let source = File.ReadAllText sourcePath
        let submitIndex = source.IndexOf("actor.Submit", source.IndexOf("let SubmitChange", StringComparison.Ordinal), StringComparison.Ordinal)
        let wakeIndex = source.IndexOf("do! tryNotifyLibraryContentAvailable context ids.RepositoryId receipt", StringComparison.Ordinal)
        let responseIndex = source.IndexOf("return! ok context receipt", wakeIndex, StringComparison.Ordinal)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(submitIndex, Is.GreaterThanOrEqualTo(0))
                Assert.That(wakeIndex, Is.GreaterThan(submitIndex))
                Assert.That(responseIndex, Is.GreaterThan(wakeIndex)))
        )

    /// Verifies that Save and Branch/WDU share exact, separator-bounded Library ownership.
    [<Test>]
    member _.LibraryConfigurationOwnsOnlyExactRootSegments() =
        let configuration =
            { LibraryCatalogDto.CreateInitial(Guid.NewGuid(), Instant.FromUtc(2026, 8, 27, 19, 0), "principal") with Libraries = [| "shared/docs" |] }

        Assert.That(configurationOwnsPath configuration "shared/docs", Is.True)
        Assert.That(configurationOwnsPath configuration "shared/docs/readme.md", Is.True)
        Assert.That(configurationOwnsPath configuration "Shared\\Docs\\readme.md", Is.True)
        Assert.That(configurationOwnsPath configuration "shared/documentation", Is.False)
        Assert.That(configurationOwnsPath configuration "shared", Is.False)

    /// Verifies current actor-owned catalog classification across every accepted path boundary.
    [<Test>]
    member _.IsInLibraryUsesExactCurrentCatalogSnapshot() =
        task {
            let repositoryId, pendingControl = pendingFixture ()

            let initialCatalog = { pendingControl.LibraryCatalog with Libraries = [| "shared/docs" |] }

            let store = FailingLibraryStore({ pendingControl with Pending = None; LibraryCatalog = initialCatalog }, "never")
            let coordinator = LibraryCoordinator.Coordinator(store, LibraryCoordinator.LibraryCursorCodec(Array.create 32 0x44uy)) :> ILibraryCoordinator

            let! exactRoot = coordinator.IsInLibraryAsync(repositoryId, "shared/docs", CancellationToken.None)
            let! descendant = coordinator.IsInLibraryAsync(repositoryId, "shared/docs/readme.md", CancellationToken.None)
            let! siblingPrefix = coordinator.IsInLibraryAsync(repositoryId, "shared/documentation", CancellationToken.None)
            let! slashVariant = coordinator.IsInLibraryAsync(repositoryId, "shared\\docs\\readme.md", CancellationToken.None)
            let! caseVariant = coordinator.IsInLibraryAsync(repositoryId, "SHARED/DOCS/README.MD", CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(exactRoot, Is.True)
                    Assert.That(descendant, Is.True)
                    Assert.That(siblingPrefix, Is.False)
                    Assert.That(slashVariant, Is.True)
                    Assert.That(caseVariant, Is.True))
            )
        }

    /// Verifies empty and changed catalogs are observed on later classification calls without a reservation.
    [<Test>]
    member _.IsInLibraryObservesEmptyAndChangedCatalogs() =
        task {
            let repositoryId, pendingControl = pendingFixture ()
            let emptyCatalog = { pendingControl.LibraryCatalog with Libraries = Array.empty }
            let store = FailingLibraryStore({ pendingControl with Pending = None; LibraryCatalog = emptyCatalog }, "never")
            let coordinator = LibraryCoordinator.Coordinator(store, LibraryCoordinator.LibraryCursorCodec(Array.create 32 0x45uy)) :> ILibraryCoordinator

            let! before = coordinator.IsInLibraryAsync(repositoryId, "shared/docs", CancellationToken.None)

            let changedCatalog =
                { emptyCatalog with
                    Version = Guid.Parse "cc5ba0c2-8fb5-41fd-80d7-86e5f444f95a"
                    Libraries = [| "shared" |]
                    PreviousVersion = Some emptyCatalog.Version
                }

            let operationId = Guid.Parse "54a64a63-14b2-4439-b8c4-b35827dfd299"

            let proposedResult =
                {
                    OperationId = operationId
                    Outcome = OutcomeKind.Accepted
                    LibraryCatalog = changedCatalog
                    ReasonCode = None
                    RecordedAt = changedCatalog.CreatedAt
                }

            let! persisted = coordinator.SetCatalogAsync(repositoryId, "request-hash", proposedResult, CancellationToken.None)
            let! after = coordinator.IsInLibraryAsync(repositoryId, "shared/docs", CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(before, Is.False)
                    Assert.That(persisted.LibraryCatalog, Is.EqualTo changedCatalog)
                    Assert.That(after, Is.True))
            )
        }

    /// Verifies the Library actor has no callback dependency on RepositoryActor.
    [<Test>]
    member _.RepositoryLibraryActorHasOneWayCatalogOwnership() =
        let sourcePath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Actors", "RepositoryLibrary.Actor.fs"))
        let source = File.ReadAllText sourcePath

        Assert.Multiple(
            Action (fun () ->
                Assert.That(source, Does.Contain("member this.IsInLibrary relativePath"))
                Assert.That(source, Does.Not.Contain("IRepositoryActor"))
                Assert.That(source, Does.Not.Contain("IGrainFactory")))
        )

    /// Verifies both current-projection Product V1 bounds are enforced at 100,000 documents.
    [<Test>]
    member _.CurrentProjectionBoundsCoverItemHeadsAndNamespaceSlots() =
        let sourcePath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Library.Persistence.Server.fs"))
        let source = File.ReadAllText sourcePath

        Assert.Multiple(
            Action (fun () ->
                Assert.That(source, Does.Contain("let CurrentProjectionDocumentLimit = 100000"))
                Assert.That(source, Does.Contain("if itemCount >= CurrentProjectionDocumentLimit"))
                Assert.That(source, Does.Contain("Library item-head projection reached its"))
                Assert.That(source, Does.Contain("if slotCount >= CurrentProjectionDocumentLimit"))
                Assert.That(source, Does.Contain("Library namespace-slot projection reached its")))
        )

    /// Verifies every Library container and persistence operation uses its accepted purpose-specific hierarchical key.
    [<Test>]
    member _.LibraryPersistenceUsesAcceptedPurposeSpecificPartitionKeys() =
        let appHostPath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Aspire.AppHost", "Program.Aspire.AppHost.cs"))
        let appHostSource = File.ReadAllText appHostPath
        let persistencePath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Library.Persistence.Server.fs"))
        let persistenceSource = File.ReadAllText persistencePath

        Assert.Multiple(
            Action (fun () ->
                Assert.That(appHostSource, Does.Contain("[\"grace-library-control\"] = [\"/RepositoryId\"]"))
                Assert.That(appHostSource, Does.Contain("[\"grace-library-changes\"] = [\"/RepositoryId\", \"/StreamSegment\"]"))
                Assert.That(appHostSource, Does.Contain("[\"grace-library-current\"] = [\"/RepositoryId\", \"/ProjectionKind\"]"))
                Assert.That(appHostSource, Does.Contain("[\"grace-library-receipts\"] = [\"/RepositoryId\", \"/RecordKind\", \"/RecordKey\"]"))
                Assert.That(appHostSource, Does.Contain("[\"grace-library-history\"] = [\"/RepositoryId\", \"/HistoryKey\", \"/HistorySegment\"]"))
                Assert.That(appHostSource, Does.Contain("[\"grace-library-baselines\"] = [\"/RepositoryId\", \"/BaselineId\", \"/ShardKey\"]"))
                Assert.That(persistenceSource, Does.Contain("controlPartitionKey document.RepositoryId"))
                Assert.That(persistenceSource, Does.Contain("partitionKey2 document.RepositoryId document.StreamSegment"))
                Assert.That(persistenceSource, Does.Contain("partitionKey2 document.RepositoryId document.ProjectionKind"))
                Assert.That(persistenceSource, Does.Contain("partitionKey3 document.RepositoryId document.RecordKind document.RecordKey"))
                Assert.That(persistenceSource, Does.Contain("partitionKey3 repositoryId historyKey historySegment"))
                Assert.That(persistenceSource, Does.Contain("partitionKey3 repositoryId baselineKey shard.ShardKey"))
                Assert.That(persistenceSource, Does.Contain("partitionKey3 repositoryId baselineKey \"manifest\""))
                Assert.That(persistenceSource, Does.Contain("QueryRequestOptions(PartitionKey = Nullable(partitionKey2 repositoryId projectionKind)")))
        )

    /// Verifies capacity failure occurs before a command can reserve the control document.
    [<Test>]
    member _.CurrentProjectionBoundsRunBeforeReservation() =
        task {
            let repositoryId, pendingControl = pendingFixture ()
            let initialControl = { pendingControl with NextCursor = 1L; Pending = None }

            let pending = pendingControl.Pending.Value
            let namespaceValue = pending.Receipt.Item.Value.Namespace.Value
            let store = FailingLibraryStore(initialControl, "capacity")
            let coordinator = LibraryCoordinator.Coordinator(store, LibraryCoordinator.LibraryCursorCodec(Array.create 32 0x46uy)) :> ILibraryCoordinator

            let command =
                {
                    RepositoryId = repositoryId
                    OperationId = pending.OperationId
                    RequestHash = pending.RequestHash
                    LibraryCatalogVersion = pending.ExpectedLibraryCatalogVersion
                    ItemId = None
                    ItemKind = pending.Receipt.Item.Value.ItemKind
                    ChangeKind = pending.Receipt.Change.Value.ChangeKind
                    NamespacePrecondition = None
                    ContentPrecondition = None
                    CreationSlotExpectation =
                        Some
                            {
                                Parent = namespaceValue.Parent
                                Name = namespaceValue.Name
                                ExpectedSlotVersion = LibraryCoordinator.initialSlotVersion repositoryId namespaceValue.NormalizedPath
                                ExpectedState = "vacant"
                            }
                    DestinationParent = None
                    DestinationName = None
                    PreparedContentId = None
                    PreparedContent = None
                    PreparedContentExpiresAt = None
                }

            let failure =
                Assert.ThrowsAsync<InvalidOperationException>(
                    Func<Task>(fun () -> coordinator.SubmitAsync(command, "principal", "correlation", CancellationToken.None) :> Task)
                )

            let canonicalCount, itemCount, slotCount, receiptCount, historyCount = store.Counts

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(failure.Message, Does.Contain("capacity"))
                    Assert.That(store.Control.Pending, Is.EqualTo(None))
                    Assert.That(store.Control.NextCursor, Is.EqualTo(1L))
                    Assert.That(canonicalCount, Is.Zero)
                    Assert.That(itemCount, Is.Zero)
                    Assert.That(slotCount, Is.Zero)
                    Assert.That(receiptCount, Is.Zero)
                    Assert.That(historyCount, Is.Zero))
            )
        }

    /// Verifies a revoked permission gate exits before the coordinator can create any durable Library effect.
    [<Test>]
    member _.RevokedLibraryWritePermissionPreventsReservationAndReceipt() =
        task {
            let _, control = pendingFixture ()
            let mutable submitCount = 0

            let authorize () = Task.FromResult(Denied "revoked")

            let submit () =
                submitCount <- submitCount + 1
                Task.FromResult control.Pending.Value.Receipt

            let! result = RepositoryLibrary.submitWhenAuthorized authorize submit

            Assert.That(result.Receipt, Is.EqualTo(None))
            Assert.That(result.ForbiddenReason, Is.EqualTo(Some "revoked"))

            Assert.That(submitCount, Is.Zero)
        }

    /// Verifies exact repository catalog initialization retries preserve the same empty actor-owned catalog.
    [<Test>]
    member _.InitialCatalogExactRetryIsIdempotent() =
        task {
            let repositoryId, control = pendingFixture ()
            let initialCatalog = LibraryCatalogDto.CreateInitial(repositoryId, control.UpdatedAt, "principal")
            let store = FailingLibraryStore({ control with Pending = None; LibraryCatalog = initialCatalog }, "never")
            let coordinator = LibraryCoordinator.Coordinator(store, LibraryCoordinator.LibraryCursorCodec(Array.create 32 0x49uy)) :> ILibraryCoordinator

            do! coordinator.InitializeAsync(repositoryId, initialCatalog, CancellationToken.None)
            do! coordinator.InitializeAsync(repositoryId, initialCatalog, CancellationToken.None)
            let! actual = coordinator.GetCatalogAsync(repositoryId, CancellationToken.None)

            Assert.That(actual, Is.EqualTo(initialCatalog))
        }

    /// Verifies an accepted catalog operation keeps its original durable result after a later catalog mutation.
    [<Test>]
    member _.CatalogOperationRetryReturnsOriginalResultAfterLaterMutation() =
        task {
            let repositoryId, pendingControl = pendingFixture ()
            let initialControl = { pendingControl with Pending = None; CurrentBaselineId = None; CurrentBaselineCursor = None }
            let store = FailingLibraryStore(initialControl, "never")
            let coordinator = LibraryCoordinator.Coordinator(store, LibraryCoordinator.LibraryCursorCodec(Array.create 32 0x47uy)) :> ILibraryCoordinator
            let now = Instant.FromUtc(2026, 8, 28, 12, 0)
            let operationA = Guid.Parse "9ec3f2d0-a8d8-47c5-9f18-d098c37ab419"
            let operationB = Guid.Parse "bd25030e-d66a-43c4-87c7-ec6535c74469"

            let catalogA =
                { initialControl.LibraryCatalog with
                    Version = Guid.Parse "5e23ea66-c934-4fd8-9310-0f9a4cc75c24"
                    Libraries = [| "shared" |]
                    PreviousVersion = Some initialControl.LibraryCatalog.Version
                    CreatedAt = now
                }

            let catalogB =
                { catalogA with
                    Version = Guid.Parse "518249ae-6adb-41e1-9943-6174384d7c59"
                    Libraries = [| "shared"; "media" |]
                    PreviousVersion = Some catalogA.Version
                    CreatedAt = now + Duration.FromSeconds 1L
                }

            let result operationId catalog =
                { OperationId = operationId; Outcome = OutcomeKind.Accepted; LibraryCatalog = catalog; ReasonCode = None; RecordedAt = catalog.CreatedAt }

            let! acceptedA = coordinator.SetCatalogAsync(repositoryId, "request-a", result operationA catalogA, CancellationToken.None)
            let! acceptedB = coordinator.SetCatalogAsync(repositoryId, "request-b", result operationB catalogB, CancellationToken.None)
            let! replayedA = coordinator.SetCatalogAsync(repositoryId, "request-a", result operationA catalogA, CancellationToken.None)
            let! conflictingA = coordinator.SetCatalogAsync(repositoryId, "different-request", result operationA catalogA, CancellationToken.None)
            let! current = coordinator.GetCatalogAsync(repositoryId, CancellationToken.None)

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(acceptedA, Is.EqualTo(result operationA catalogA))
                    Assert.That(acceptedB, Is.EqualTo(result operationB catalogB))
                    Assert.That(replayedA, Is.EqualTo(acceptedA))
                    Assert.That(current, Is.EqualTo(catalogB))
                    Assert.That(conflictingA.Outcome, Is.EqualTo(OutcomeKind.Rejected))
                    Assert.That(conflictingA.ReasonCode, Is.EqualTo(Some RejectionReason.OperationIdentityMismatch)))
            )
        }

    /// Verifies baseline pages retain the exact catalog snapshot captured with their immutable manifest.
    [<Test>]
    member _.BaselineManifestCapturesCatalogSnapshotForEveryPage() =
        let repositoryId, control = pendingFixture ()
        let item = baselineItem (Guid.NewGuid()) control.LibraryCatalog.Version "shared/docs"
        let manifest, _ = LibraryPersistence.buildBaselineDocuments repositoryId 4L control.CursorEpoch control.LibraryCatalog [| item |] control.UpdatedAt

        let sourcePath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Library.Server.fs"))
        let source = File.ReadAllText sourcePath

        Assert.Multiple(
            Action (fun () ->
                Assert.That(manifest.LibraryCatalog, Is.EqualTo(control.LibraryCatalog))
                Assert.That(source, Does.Contain("LibraryCatalog = manifest.LibraryCatalog"))
                Assert.That(source, Does.Contain("existing.LibraryCatalogVersion = control.Document.LibraryCatalog.Version")))
        )

    /// Verifies projection-backed reads recover canonical-before-projection restarts before returning receipt or item truth.
    [<Test>]
    member _.ProjectionReadsRepairCanonicalBeforeProjectionRestart() =
        task {
            let repositoryId, control = pendingFixture ()
            let store = FailingLibraryStore(control, "receipt")

            Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> LibraryCoordinator.repair store repositoryId CancellationToken.None :> Task))
            |> ignore

            let coordinator = LibraryCoordinator.Coordinator(store, LibraryCoordinator.LibraryCursorCodec(Array.create 32 0x48uy)) :> ILibraryCoordinator
            do! coordinator.RepairAsync(repositoryId, CancellationToken.None)

            let pending = control.Pending.Value

            let! receipt =
                (store :> ILibraryStore)
                    .ReadReceiptAsync(repositoryId, pending.OperationId, CancellationToken.None)

            let! item =
                (store :> ILibraryStore)
                    .ReadItemAsync(repositoryId, pending.TargetItemIds[0], CancellationToken.None)

            let sourcePath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Library.Server.fs"))
            let source = File.ReadAllText sourcePath

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(receipt.IsSome, Is.True)
                    Assert.That(item.IsSome, Is.True)

                    Assert.That(
                        source.IndexOf("let GetOperation", StringComparison.Ordinal),
                        Is.LessThan(
                            source.IndexOf(
                                ".Repair(Services.getCorrelationId context)",
                                source.IndexOf("let GetOperation", StringComparison.Ordinal),
                                StringComparison.Ordinal
                            )
                        )
                    )

                    Assert.That(
                        source.IndexOf("let PrepareContentRead", StringComparison.Ordinal),
                        Is.LessThan(
                            source.IndexOf(
                                ".Repair(Services.getCorrelationId context)",
                                source.IndexOf("let PrepareContentRead", StringComparison.Ordinal),
                                StringComparison.Ordinal
                            )
                        )
                    ))
            )
        }

    /// Verifies an exact prepared-content retry reconstructs and invokes the same upload-session start identity.
    [<Test>]
    member _.PreparedContentRetryResumesExactUploadSession() =
        let repositoryId = Guid.NewGuid()
        let operationId = Guid.NewGuid()
        let preparedId = Guid.NewGuid()
        let now = Instant.FromUtc(2026, 8, 28, 13, 0)

        let document =
            {
                id = $"prepared:{preparedId:D}"
                RepositoryId = repositoryId
                RecordKind = "prepared"
                RecordKey = $"prepared:{preparedId:D}"
                SchemaVersion = 1
                PreparedContentId = preparedId
                OperationId = operationId
                PrincipalId = "principal"
                OwnerId = Guid.NewGuid()
                OrganizationId = Guid.NewGuid()
                Content =
                    {
                        PreparedContentId = preparedId
                        Blake3Hash = String.replicate 64 "a"
                        Sha256Hash = String.replicate 64 "b"
                        Size = 42L
                        UploadRequired = true
                        UploadInstructions = None
                        ExpiresAt = now + Duration.FromMinutes 15L
                    }
                UploadSessionId = preparedId
                AuthorizedScope = $"Library/{preparedId:D}"
                StoragePoolId = StoragePoolId $"pool-{Guid.NewGuid():N}"
                SamplingPolicySnapshot = "{\"minimumSampleCount\":1}"
                FinalizedManifest = None
            }

        let firstAttempt = Library.preparedUploadSessionCommand document
        let exactRetry = Library.preparedUploadSessionCommand document
        let sourcePath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "Library.Server.fs"))
        let source = File.ReadAllText sourcePath

        Assert.That(exactRetry, Is.EqualTo(firstAttempt))
        Assert.That(source, Does.Contain("startPreparedUploadSession context existing.Document"))

        match exactRetry with
        | UploadSessionCommand.Start command ->
            Assert.Multiple(
                Action (fun () ->
                    Assert.That(command.UploadSessionId, Is.EqualTo(preparedId))
                    Assert.That(command.RepositoryId, Is.EqualTo(repositoryId))
                    Assert.That(command.OperationId, Is.EqualTo($"Library-prepare:{operationId:D}"))
                    Assert.That(command.SamplingPolicySnapshot, Is.EqualTo(document.SamplingPolicySnapshot)))
            )
        | _ -> Assert.Fail("Prepared content must reconstruct an upload-session Start command.")
