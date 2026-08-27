namespace Grace.Server.Tests

open Grace.Server
open Grace.Types.Common
open Grace.Types.SynchronizedContent
open Grace.Shared.Validation.SynchronizedContent
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Threading.Tasks

/// Stores synchronized coordinator test state in memory and injects one durable-boundary failure.
type private FailingSynchronizedStore(initialControl: SynchronizedControlDocument, failurePoint: string) =

    let mutable control = initialControl
    let mutable etag = 0
    let mutable pendingFailure = Some failurePoint
    let canonicals = Dictionary<int64, SynchronizedCanonicalMutationDocument>()
    let items = Dictionary<SynchronizedItemId, SynchronizedCurrentItemDocument>()
    let slots = Dictionary<string, SynchronizedCurrentSlotDocument>(StringComparer.OrdinalIgnoreCase)
    let receipts = Dictionary<SynchronizedOperationId, SynchronizedReceiptDocument>()
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

    interface ISynchronizedContentStore with

        member _.EnsureControlAsync(_repositoryId, _rootConfiguration, _cancellationToken) = Task.FromResult { Document = control; ETag = string etag }

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

        member _.ReadReceiptAsync(_repositoryId, operationId, _cancellationToken) =
            match receipts.TryGetValue operationId with
            | true, receipt -> Task.FromResult(Some receipt)
            | false, _ -> Task.FromResult None

        member _.ReadCanonicalAsync(_repositoryId, cursor, _cancellationToken) =
            match canonicals.TryGetValue cursor with
            | true, mutation -> Task.FromResult(Some mutation)
            | false, _ -> Task.FromResult None

        member _.CreateCanonicalAsync(mutation, _cancellationToken) =
            task {
                failOnce "canonical"

                match canonicals.TryGetValue mutation.Cursor with
                | true, existing when existing = mutation -> ()
                | true, _ -> invalidOp "Canonical identity was reused for different content."
                | false, _ -> canonicals.Add(mutation.Cursor, mutation)
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

        member _.ReadDeltasAsync(_repositoryId, afterCursor, maximumCount, _cancellationToken) =
            canonicals.Values
            |> Seq.filter (fun mutation -> mutation.Cursor > afterCursor)
            |> Seq.sortBy (fun mutation -> mutation.Cursor)
            |> Seq.truncate maximumCount
            |> Seq.toArray
            |> Task.FromResult

        member _.ReadCurrentItemsAsync(_repositoryId, _cancellationToken) = items.Values |> Seq.toArray |> Task.FromResult

        member _.HasLiveDescendantsAsync(_repositoryId, _normalizedDirectoryPath, _cancellationToken) = Task.FromResult false

        member _.EnsureBaselineAsync(_repositoryId, _boundaryCursor, _cursorEpoch, _rootConfiguration, _items, _cancellationToken) =
            Task.FromException<SynchronizedBaselineManifestDocument>(NotSupportedException())

        member _.ReadBaselineAsync(_repositoryId, _baselineId, _cancellationToken) = Task.FromResult None

/// Covers canonical-first publication and restart repair at every durable effect boundary.
[<Parallelizable(ParallelScope.All)>]
type SynchronizedContentCoordinatorTests() =

    /// Builds one live directory item for deterministic baseline tests.
    let baselineItem itemId rootVersion normalizedPath =
        let namespaceValue =
            {
                Parent = { Kind = "root"; RootPath = Some "shared"; ItemId = None }
                Name = normalizedPath
                NormalizedPath = normalizedPath
                NamespaceVersion = Guid.NewGuid()
                SlotVersion = Guid.NewGuid()
            }

        {
            ItemId = itemId
            ItemKind = ItemKind.Directory
            State = "live"
            LastMutationCursor = "cursor"
            RootConfigurationVersion = rootVersion
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

        let rootConfiguration =
            { RepositoryId = repositoryId; Version = rootVersion; Roots = [| "shared" |]; CreatedAt = now; CreatedBy = "principal"; PreviousVersion = None }

        let namespaceValue =
            {
                Parent = { Kind = "root"; RootPath = Some "shared"; ItemId = None }
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
                LastMutationCursor = publicCursor
                RootConfigurationVersion = rootVersion
                Namespace = Some namespaceValue
                Content = None
                Tombstone = None
            }

        let mutation =
            {
                Cursor = publicCursor
                OperationId = operationId
                MutationKind = MutationKind.CreateDirectory
                ItemId = itemId
                ItemKind = ItemKind.Directory
                AcceptedAt = now
                AcceptedBy = "principal"
                RootConfigurationVersion = rootVersion
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
                RootConfigurationVersion = rootVersion
                RecordedAt = now
                PrincipalId = "principal"
                Mutation = Some mutation
                Cursor = Some publicCursor
                Item = Some item
                Conflict = None
                ReasonCode = None
                CurrentRootConfiguration = None
                Rebaseline = None
            }

        let canonical =
            {
                id = "mutation:1"
                RepositoryId = repositoryId
                Scope = "segment:0"
                SchemaVersion = 1
                Cursor = 1L
                PublicCursor = publicCursor
                OperationId = operationId
                RequestHash = "request-hash"
                Mutation = mutation
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
                CanonicalMutation = canonical
                ExpectedRootConfigurationVersion = rootVersion
                PrincipalId = "principal"
                CorrelationId = "correlation"
                ReservedAt = now
                TargetItemIds = [| itemId |]
            }

        let control =
            {
                id = "control"
                RepositoryId = repositoryId
                Scope = "control"
                SchemaVersion = 1
                CursorEpoch = Guid.Parse "40a91aa6-40b3-5a19-83cf-c5fe1ca94517"
                NextCursor = 2L
                AppliedThrough = 0L
                ReplayFloor = 0L
                RootConfiguration = rootConfiguration
                Pending = Some pending
                CurrentBaselineId = None
                CurrentBaselineCursor = None
                ProjectionWatermarks = SynchronizedProjectionWatermarks.Empty
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
            let store = FailingSynchronizedStore(control, failurePoint)

            let firstAttempt =
                Assert.ThrowsAsync<InvalidOperationException>(
                    Func<Task>(fun () -> SynchronizedContentCoordinator.repair store repositoryId CancellationToken.None :> Task)
                )

            Assert.That(firstAttempt.Message, Does.Contain failurePoint)
            Assert.That(store.Control.Pending.IsSome, Is.True)
            Assert.That(store.Control.AppliedThrough, Is.EqualTo 0L)

            do! SynchronizedContentCoordinator.repair store repositoryId CancellationToken.None

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
        let codec = SynchronizedContentCoordinator.SynchronizedCursorCodec(Array.create 32 0x5Auy) :> ISynchronizedCursorCodec

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

        let rootConfiguration =
            { RepositoryId = repositoryId; Version = rootVersion; Roots = [| "shared" |]; CreatedAt = now; CreatedBy = "principal"; PreviousVersion = None }

        let firstId = Guid.Parse "f83b3a34-5897-5cb0-bac0-c186412c4adc"
        let secondId = Guid.Parse "0a3d49d4-071b-505a-809a-b49cd4e445fd"

        let items =
            [|
                baselineItem firstId rootVersion "shared/zeta"
                baselineItem secondId rootVersion "shared/alpha"
            |]

        let firstManifest, firstShards = SynchronizedContentPersistence.buildBaselineDocuments repositoryId 23L cursorEpoch rootConfiguration items now

        let secondManifest, secondShards =
            SynchronizedContentPersistence.buildBaselineDocuments repositoryId 23L cursorEpoch rootConfiguration (Array.rev items) now

        Assert.That(secondManifest, Is.EqualTo firstManifest)
        Assert.That(secondShards, Is.EqualTo<SynchronizedBaselineShardDocument array>(firstShards))

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
                |> Array.map SynchronizedContentPersistence.documentHash
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

        let rootConfiguration =
            { RepositoryId = repositoryId; Version = rootVersion; Roots = [| "shared" |]; CreatedAt = now; CreatedBy = "principal"; PreviousVersion = None }

        let pathBody = String.replicate 490000 "x"
        let largePath suffix = $"shared/{pathBody}/{suffix}"

        let items =
            [|
                baselineItem (Guid.Parse "079966d6-8de4-501e-8db7-ed1189d55512") rootVersion (largePath "a")
                baselineItem (Guid.Parse "a5888377-934c-56d0-8f57-d38760c03f15") rootVersion (largePath "b")
                baselineItem (Guid.Parse "45f4df4f-aa6b-5f0f-a71c-b5c5a034e531") rootVersion (largePath "c")
            |]

        let manifest, shards = SynchronizedContentPersistence.buildBaselineDocuments repositoryId 41L Guid.Empty rootConfiguration items now

        Assert.That(shards.Length, Is.GreaterThan 1)

        Assert.That(
            shards
            |> Array.forall (fun shard ->
                shard.SerializedBytes
                <= SynchronizedContentPersistence.BaselineShardByteLimit),
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

        Assert.That(SynchronizedContent.directoryVersionOwnsRoot "shared" directoryVersion, Is.True)
        Assert.That(SynchronizedContent.directoryVersionOwnsRoot "shared/docs" directoryVersion, Is.True)
        Assert.That(SynchronizedContent.directoryVersionOwnsRoot "share" directoryVersion, Is.False)
        Assert.That(SynchronizedContent.directoryVersionOwnsRoot "shared/documentation" directoryVersion, Is.False)

    /// Verifies only a durable mutation receipt can produce the coarse content-free wake payload.
    [<Test>]
    member _.SynchronizedWakeRequiresAcceptedMutationReceipt() =
        let repositoryId, control = pendingFixture ()
        let receipt = control.Pending.Value.Receipt
        let codec = SynchronizedContentCoordinator.SynchronizedCursorCodec(Array.create 32 0x6Buy) :> ISynchronizedCursorCodec

        let wake =
            SynchronizedContent.synchronizedContentAvailableFromReceipt codec control receipt "correlation-id"
            |> Option.get

        Assert.Multiple(
            Action (fun () ->
                Assert.That(wake.EventName, Is.EqualTo("SynchronizedContentAvailable.v1"))
                Assert.That(wake.RepositoryId, Is.EqualTo(repositoryId))
                Assert.That(codec.TryDecode(repositoryId, wake.CursorEpoch), Is.EqualTo(Some(control.CursorEpoch, 0L)))
                Assert.That(wake.AvailableAfterCursor, Is.EqualTo(receipt.Cursor.Value))
                Assert.That(wake.RootConfigurationVersion, Is.EqualTo(receipt.RootConfigurationVersion))
                Assert.That(wake.CorrelationId, Is.EqualTo("correlation-id")))
        )

        let rejectedReceipt = { receipt with Outcome = OutcomeKind.Rejected; Mutation = None; Cursor = None }

        Assert.That(SynchronizedContent.synchronizedContentAvailableFromReceipt codec control rejectedReceipt "correlation-id", Is.EqualTo(None))

    /// Verifies mutation submission awaits the durable receipt before attempting its best-effort wake and returning success.
    [<Test>]
    member _.SynchronizedWakeFollowsDurableSubmitAndPrecedesResponse() =
        let sourcePath = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Grace.Server", "SynchronizedContent.Server.fs"))
        let source = File.ReadAllText sourcePath
        let submitIndex = source.IndexOf("let! receipt = actor.Submit command", StringComparison.Ordinal)
        let wakeIndex = source.IndexOf("do! tryNotifySynchronizedContentAvailable context ids.RepositoryId receipt", StringComparison.Ordinal)
        let responseIndex = source.IndexOf("return! ok context receipt", wakeIndex, StringComparison.Ordinal)

        Assert.Multiple(
            Action (fun () ->
                Assert.That(submitIndex, Is.GreaterThanOrEqualTo(0))
                Assert.That(wakeIndex, Is.GreaterThan(submitIndex))
                Assert.That(responseIndex, Is.GreaterThan(wakeIndex)))
        )

    /// Verifies that Save and Branch/WDU share exact, separator-bounded synchronized-root ownership.
    [<Test>]
    member _.SynchronizedConfigurationOwnsOnlyExactRootSegments() =
        let configuration =
            { SynchronizedRootConfigurationDto.CreateInitial(Guid.NewGuid(), Instant.FromUtc(2026, 8, 27, 19, 0), "principal") with
                Roots = [| "shared/docs" |]
            }

        Assert.That(configurationOwnsPath configuration "shared/docs", Is.True)
        Assert.That(configurationOwnsPath configuration "shared/docs/readme.md", Is.True)
        Assert.That(configurationOwnsPath configuration "Shared\\Docs\\readme.md", Is.True)
        Assert.That(configurationOwnsPath configuration "shared/documentation", Is.False)
        Assert.That(configurationOwnsPath configuration "shared", Is.False)

    /// Verifies that Repository root mutation admission requires the exact current predecessor version.
    [<Test>]
    member _.RepositoryRootConfigurationRequiresExactCurrentPredecessor() =
        let repositoryId = Guid.Parse "3c982ea8-f104-41e9-821a-e2613035268d"
        let current = SynchronizedRootConfigurationDto.CreateInitial(repositoryId, Instant.FromUtc(2026, 8, 27, 19, 0), "principal")

        let matching = { current with Version = Guid.Parse "cc5ba0c2-8fb5-41fd-80d7-86e5f444f95a"; PreviousVersion = Some current.Version }

        let stale = { matching with PreviousVersion = Some(Guid.Parse "e6f33d06-5cef-4c35-99c8-3211fba6d776") }

        Assert.That(Grace.Actors.Repository.synchronizedRootConfigurationMatchesCurrent current matching, Is.True)
        Assert.That(Grace.Actors.Repository.synchronizedRootConfigurationMatchesCurrent current stale, Is.False)
