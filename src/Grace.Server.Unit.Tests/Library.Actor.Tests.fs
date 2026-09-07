namespace Grace.Server.Tests

open Grace.Actors
open Grace.Shared
open Grace.Shared.Validation
open Grace.Types.Library
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Text
open System.Threading.Tasks

/// Verifies deterministic Library decisions, tokens, and notification effect ordering.
[<Parallelizable(ParallelScope.All)>]
type LibraryActorTests() =

    let repositoryId = Guid.Parse("a819b112-d97c-4813-b647-a02686a0eb33")
    let operationId = Guid.Parse("6aa16ee4-8df0-4aa0-a463-389e36f8e119")
    let itemId = Guid.Parse("06596842-c919-4437-951f-5a9ed9b79465")
    let contentVersionId = Guid.Parse("80faf399-08f5-4768-a0be-2c607f041162")
    let catalogVersion = Guid.Parse("e014a230-2557-45ba-95fa-eb0191dfb657")
    let timestamp = Instant.FromUtc(2026, 9, 7, 8, 9, 10)

    let envelope () =
        let properties = Dictionary<string, string>()
        properties["eventName"] <- "LibraryContentAvailable.v1"

        {
            TopicName = "grace-events"
            MessageId = $"LibraryContentAvailable/{repositoryId:D}/00000000000000017"
            Body = [| 10uy; 20uy; 30uy |]
            ContentType = "application/json"
            Subject = "LibraryContentAvailable.v1"
            CorrelationId = "library-test-correlation"
            ApplicationProperties = properties
        }

    let notificationAttempt sendFails advanceFails clearFails hasRetained =
        let effects = ResizeArray<string>()

        let send _ =
            task {
                effects.Add "send"

                if sendFails then return raise (InvalidOperationException "send failed")
            }

        let advance () =
            task {
                effects.Add "advance"

                if advanceFails then return raise (InvalidOperationException "advance failed")
            }

        let persist _ = task { effects.Add "persist" }

        let clear () =
            task {
                effects.Add "clear"

                if clearFails then return raise (InvalidOperationException "clear failed")
            }

        let run () = LibraryNotifications.attempt send advance persist clear hasRetained (envelope ())
        effects, run

    /// A first successful send advances the durable cursor without creating failure state.
    [<Test>]
    member _.FirstNotificationSuccessAdvancesWithoutRetainingEnvelope() =
        let effects, run = notificationAttempt false false false false
        let sent = run().GetAwaiter().GetResult()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(sent, Is.True)
                Assert.That(effects.ToArray(), Is.EqualTo(box [| "send"; "advance" |])))
        )

    /// A first terminal send failure stores the exact envelope and leaves progress unchanged.
    [<Test>]
    member _.FirstNotificationFailureRetainsEnvelopeWithoutAdvancing() =
        let effects, run = notificationAttempt true false false false
        let sent = run().GetAwaiter().GetResult()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(sent, Is.False)
                Assert.That(effects.ToArray(), Is.EqualTo(box [| "send"; "persist" |])))
        )

    /// A retained envelope is cleared only after its retry sends and advances successfully.
    [<Test>]
    member _.RetainedNotificationSuccessAdvancesBeforeClearingEnvelope() =
        let effects, run = notificationAttempt false false false true
        let sent = run().GetAwaiter().GetResult()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(sent, Is.True)
                Assert.That(effects.ToArray(), Is.EqualTo(box [| "send"; "advance"; "clear" |])))
        )

    /// A retained-envelope retry failure neither rewrites the envelope nor advances progress.
    [<Test>]
    member _.RetainedNotificationFailureLeavesExistingEnvelopeUntouched() =
        let effects, run = notificationAttempt true false false true
        let sent = run().GetAwaiter().GetResult()

        Assert.Multiple(
            Action (fun () ->
                Assert.That(sent, Is.False)
                Assert.That(effects.ToArray(), Is.EqualTo(box [| "send" |])))
        )

    /// Failure to persist progress after sending leaves retained state available for an exact retry.
    [<Test>]
    member _.NotificationAdvanceFailureDoesNotClearRetainedEnvelope() =
        let effects, run = notificationAttempt false true false true

        Assert.That(Action(fun () -> run().GetAwaiter().GetResult() |> ignore), Throws.TypeOf<InvalidOperationException>())
        Assert.That(effects.ToArray(), Is.EqualTo(box [| "send"; "advance" |]))

    /// Conflict names stay portable for Unicode input, huge extensions, and numbered retries.
    [<Test>]
    member _.ConflictNamesRemainBoundedAndDeterministicAcrossRetries() =
        let original =
            String.replicate 100 "😀"
            + "."
            + String.replicate 300 "x"

        let first = LibraryDecision.conflictName original operationId 0
        let second = LibraryDecision.conflictName original operationId 12

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
                HistoryTailSegment = None
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
                Assert.That(LibraryDecision.movedDescendantPathsAreValid catalog "Media" movingId "moved" validDocuments, Is.True)
                Assert.That(LibraryDecision.movedDescendantPathsAreValid catalog "Media" movingId "moved" oversizedDocuments, Is.False))
        )

    /// Signed Library tokens reject a changed purpose, repository, payload, and expiry boundary.
    [<Test>]
    member _.LibraryTokensBindPurposeRepositoryPayloadAndExpiry() =
        let key = Array.init 32 (fun index -> byte (index + 1))
        let epoch = Guid.Parse("c9c8f144-2700-44bd-925a-34325f38572c")
        let cursor = LibraryTokens.cursor key repositoryId epoch 17L
        let page = LibraryTokens.page key "changes" repositoryId "baseline-id" 3 200L
        let content = LibraryTokens.contentRead key repositoryId itemId contentVersionId "cursor-17" 200L

        let tamperedContent =
            content.Substring(0, content.Length - 1)
            + (if content.EndsWith("A", StringComparison.Ordinal) then "B" else "A")

        Assert.Multiple(
            Action (fun () ->
                Assert.That(LibraryTokens.tryCursor key repositoryId cursor, Is.EqualTo(Some(epoch, 17L)))

                Assert.That(
                    (LibraryTokens.tryCursor key (Guid.NewGuid()) cursor)
                        .IsNone,
                    Is.True
                )

                Assert.That(LibraryTokens.tryPage key "changes" repositoryId 199L page, Is.EqualTo(Some("baseline-id", 3)))

                Assert.That(
                    (LibraryTokens.tryPage key "bootstrap" repositoryId 199L page)
                        .IsNone,
                    Is.True
                )

                Assert.That(
                    (LibraryTokens.tryPage key "changes" repositoryId 200L page)
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
                fingerprints.Add(shard.Items.Length, bytes.Length, ContentAddress.computeBlake3Hex bytes)

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
