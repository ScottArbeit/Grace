namespace Grace.Server.Tests

open Grace.Shared
open Grace.Types.Authorization
open Grace.Types.Common
open Grace.Types.Library
open Microsoft.Extensions.DependencyInjection
open NodaTime
open NUnit.Framework
open Orleans.Serialization
open Orleans.Serialization.NodaTime
open System
open System.Collections.Generic

/// Verifies the populated Library RPC graph through the serializer registration used by Grace.Server.
[<Parallelizable(ParallelScope.All)>]
type LibrarySerializationTests() =

    /// Round-trips one value through Orleans using generated record codecs and the production Grace JSON fallback.
    let roundTrip (serviceProvider: ServiceProvider) (value: 'T) : 'T =
        let serializer = serviceProvider.GetRequiredService<Serializer<'T>>()
        serializer.Deserialize(serializer.SerializeToArray(value))

    /// Verifies material values across the complete final Library actor request and response graph.
    [<Test>]
    member _.PopulatedFinalLibraryRpcGraphRoundTripsThroughProductionOrleansSerialization() =
        let services = ServiceCollection()
        let codeGenerationAssembly = Reflection.Assembly.Load("Grace.Orleans.CodeGen")

        services.AddSerializer (fun builder ->
            builder.AddAssembly(typeof<LibraryItemDto>.Assembly)
            |> ignore

            builder.AddAssembly(codeGenerationAssembly)
            |> ignore

            builder.AddNodaTimeSerializers() |> ignore

            builder.AddJsonSerializer(
                isSupported =
                    (fun candidateType ->
                        not (String.IsNullOrEmpty(candidateType.Namespace))
                        && candidateType.Namespace.StartsWith("Grace", StringComparison.InvariantCulture)),
                jsonSerializerOptions = Constants.JsonSerializerOptions
            )
            |> ignore)
        |> ignore

        use serviceProvider = services.BuildServiceProvider()
        let repositoryId = Guid.Parse("ed650a58-16cb-463d-a181-c853cf2ee7b3")
        let operationId = Guid.Parse("9f493e0c-e0fe-4e1f-9181-c1b84786d750")
        let itemId = Guid.Parse("3af8fc56-f3bf-40aa-ad84-9d166cbc14ca")
        let contentVersionId = Guid.Parse("22883ff6-95d8-47ef-b159-047d025c77a3")
        let uploadSessionId = Guid.Parse("918d5491-fc38-4010-886c-17541171670f")
        let catalogVersion = Guid.Parse("b951c903-6194-4c34-a3ce-c59b9ad7a9ee")
        let namespaceVersion = Guid.Parse("f57f34be-d57f-4987-b7ae-a926799beab4")
        let slotVersion = Guid.Parse("a8bcd35e-e35f-486c-8783-5d763cdb2a83")
        let secondItemId = Guid.Parse("712ee530-5485-4515-a56e-67f068780a3a")
        let priorContentVersionId = Guid.Parse("20fbaf83-ff7e-4531-a267-5766b146ef23")
        let timestamp = Instant.FromUtc(2026, 9, 7, 7, 8, 9)
        let parent = { Kind = "root"; LibraryPath = Some "Media"; ItemId = None }

        let slot = { Parent = parent; Name = "photo.raw"; SlotVersion = slotVersion; OccupantItemId = Some itemId }

        let content =
            {
                ContentVersionId = contentVersionId
                Blake3Hash = String.replicate 64 "a"
                Sha256Hash = String.replicate 64 "b"
                Size = 987654L
                CreatedAt = timestamp
            }

        let item =
            {
                ItemId = itemId
                ItemKind = ItemKind.File
                LastChangeCursor = "cursor-17"
                Namespace = Some { Parent = parent; Name = slot.Name; NamespaceVersion = namespaceVersion }
                Content = Some content
                ContentRevision = Some "revision-17"
                Tombstone = None
            }

        let change =
            {
                OperationId = operationId
                ChangeKind = ChangeKind.CreateFile
                AcceptedAt = timestamp
                AcceptedBy = "user:serializer"
                LibraryCatalogVersion = catalogVersion
                Item = item
                Conflict = None
            }

        let tombstone =
            {
                DeletedAt = timestamp + Duration.FromMinutes(2L)
                DeletedBy = "user:deleter"
                DeleteCursor = "cursor-23"
                LastNamespace = item.Namespace.Value
                LastContentVersionId = Some contentVersionId
            }

        let tombstonedItem =
            { item with LastChangeCursor = tombstone.DeleteCursor; Namespace = None; Content = None; ContentRevision = None; Tombstone = Some tombstone }

        let conflict = { OriginalItemId = itemId; BaseContentVersionId = Some priorContentVersionId; BaseContentRevision = Some "cursor-11" }

        let conflictChange =
            { change with
                OperationId = Guid.Parse("343fdfe4-dd4f-4f30-b16f-04777be4e87f")
                ChangeKind = ChangeKind.UpdateContent
                Item = { item with ItemId = secondItemId; LastChangeCursor = "cursor-18" }
                Conflict = Some conflict
            }

        let receipt =
            {
                OperationId = operationId
                RequestHash = String.replicate 64 "c"
                Outcome = OutcomeKind.Accepted
                Change = Some change
                ReasonCode = None
                CurrentLibraryCatalog = None
                Rebaseline = None
            }

        let rebaseline = { Reason = "cursorExpired"; CurrentEpoch = "epoch-rebaseline"; ServiceFloorCursor = "cursor-9"; RecommendedBootstrap = true }

        let catalog =
            {
                RepositoryId = repositoryId
                Version = catalogVersion
                Libraries = [| "Media" |]
                CreatedAt = timestamp
                CreatedBy = "user:serializer"
                PreviousVersion = Some(Guid.Parse("f1940bd5-8ca2-4760-97fb-a483a51f7fbc"))
            }

        let rejectedReceipt =
            { receipt with
                Outcome = OutcomeKind.RebaselineRequired
                Change = None
                ReasonCode = Some RejectionReason.NamespaceChanged
                CurrentLibraryCatalog = Some catalog
                Rebaseline = Some rebaseline
            }

        let preparation =
            {
                UploadSessionId = uploadSessionId
                Blake3Hash = content.Blake3Hash
                Sha256Hash = content.Sha256Hash
                Size = content.Size
                AuthorizedScope = $"Library/{uploadSessionId:D}"
                StoragePoolId = "pool-serializer"
                ExpiresAt = timestamp + Duration.FromMinutes(15L)
            }

        let read = { DownloadPath = "/libraries/content/download?token=material"; Content = content; ExpiresAt = timestamp + Duration.FromMinutes(5L) }

        let bootstrap =
            {
                BootstrapId = Guid.Parse("3021c4f0-ecaa-4a04-b131-10719a40ec0c")
                BoundaryCursor = "cursor-17"
                CursorEpoch = "epoch-material"
                LibraryCatalog = catalog
                Items = [| item |]
                NextPageToken = Some "bootstrap-page-2"
            }

        let changePage =
            {
                Outcome = OutcomeKind.Accepted
                CursorEpoch = bootstrap.CursorEpoch
                Changes = [| change |]
                LastCursor = item.LastChangeCursor
                HasMore = true
                NextPageToken = Some "change-page-2"
                Rebaseline = None
            }

        let rebaselinePage =
            { changePage with Outcome = OutcomeKind.RebaselineRequired; Changes = [||]; HasMore = false; NextPageToken = None; Rebaseline = Some rebaseline }

        let namespacePrecondition = { ItemId = itemId; ExpectedNamespaceVersion = namespaceVersion }

        let contentPrecondition = { ItemId = itemId; ExpectedContentVersionId = priorContentVersionId; ExpectedContentRevision = "cursor-11" }

        let creationExpectation = { Parent = parent; Name = slot.Name; ExpectedSlotVersion = slotVersion; ExpectedState = "vacant" }

        let commands =
            [|
                LibraryChangeCommand.CreateFile(operationId, receipt.RequestHash, catalogVersion, creationExpectation, uploadSessionId)
                LibraryChangeCommand.CreateDirectory(Guid.NewGuid(), "directory-hash", catalogVersion, creationExpectation)
                LibraryChangeCommand.UpdateContent(
                    Guid.NewGuid(),
                    "update-hash",
                    catalogVersion,
                    itemId,
                    Some namespacePrecondition,
                    contentPrecondition,
                    uploadSessionId
                )
                LibraryChangeCommand.Rename(Guid.NewGuid(), "rename-hash", catalogVersion, itemId, namespacePrecondition, "renamed.raw")
                LibraryChangeCommand.Move(
                    Guid.NewGuid(),
                    "move-hash",
                    catalogVersion,
                    itemId,
                    namespacePrecondition,
                    { Kind = "item"; LibraryPath = None; ItemId = Some secondItemId }
                )
                LibraryChangeCommand.Delete(Guid.NewGuid(), "delete-hash", catalogVersion, itemId, namespacePrecondition, Some contentPrecondition)
            |]

        let catalogResult =
            {
                OperationId = Guid.Parse("9ee2ccee-70fc-4e44-8c47-2d9ce3d48e18")
                Outcome = OutcomeKind.Rejected
                LibraryCatalog = catalog
                ReasonCode = Some CatalogRejectionReason.LibraryOverlap
                RecordedAt = timestamp + Duration.FromMinutes(3L)
            }

        let status =
            {
                State = "blocked"
                RepositoryId = repositoryId
                LibraryCatalogVersion = catalogVersion
                IsCaughtUp = false
                RebaselineRequired = true
                IsBlocked = true
                PendingOperationCount = 2
                OldestPendingAgeMilliseconds = Some 12345L
                ProjectionLagCount = 4L
                LastCompletedAt = Some timestamp
            }

        let authorization =
            {
                OwnerId = Guid.Parse("6e31e97a-a9df-41c7-84f3-38e24d947e02")
                OrganizationId = Guid.Parse("72ed566e-5033-47cf-b1a0-9b31e1c16911")
                Principals =
                    [|
                        { PrincipalType = PrincipalType.User; PrincipalId = "user:serializer" }
                    |]
                EffectiveClaims = [| "library:write" |]
            }

        let acceptedRecord =
            {
                SchemaVersion = 2
                Cursor = 17L
                RequestHash = receipt.RequestHash
                CorrelationId = "correlation-serializer"
                Change = conflictChange
                PriorNamespace = item.Namespace
                PriorContentVersionId = Some priorContentVersionId
                ConsumedNamespaceVersion = Some namespaceVersion
                ConsumedContentVersionId = Some priorContentVersionId
                ConsumedContentRevision = Some "cursor-11"
                ConsumedSlotVersion = Some slotVersion
                AddedItemRecord = true
                AddedSlotRecord = true
            }

        let control =
            {
                SchemaVersion = 2
                Catalog = catalog
                Epoch = Guid.Parse("785fd37c-a0ff-411a-a450-944ace5cc983")
                CommittedCursor = 17L
                ReplayFloor = 3L
                Pending = Some(LibraryPendingDecision.ItemChange acceptedRecord)
                ItemRecordCount = 7
                SlotRecordCount = 8
                HistoryThrough = 16L
                NotifyThrough = 15L
            }

        let currentItem = { SchemaVersion = 2; Item = tombstonedItem; LastCursor = 23L; HistoryTailSegment = Some "history-item-tail" }
        let currentSlot = { SchemaVersion = 2; Slot = slot; LastCursor = 17L; HistoryTailSegment = Some "history-slot-tail" }

        let durableReceipts =
            [|
                { SchemaVersion = 2; OperationId = operationId; RequestHash = receipt.RequestHash; Outcome = LibraryOperationOutcome.AcceptedChange 17L }
                {
                    SchemaVersion = 2
                    OperationId = rejectedReceipt.OperationId
                    RequestHash = rejectedReceipt.RequestHash
                    Outcome = LibraryOperationOutcome.RejectedChange rejectedReceipt
                }
                {
                    SchemaVersion = 2
                    OperationId = catalogResult.OperationId
                    RequestHash = "catalog-hash"
                    Outcome = LibraryOperationOutcome.CatalogResult catalogResult
                }
            |]

        let historySegment = { SchemaVersion = 2; PreviousSegment = Some "history-previous"; Cursors = [| 11L; 17L; 23L |] }
        let baselineShard = { SchemaVersion = 2; Items = [| item; tombstonedItem |] }
        let shardReference = { Ordinal = 4; Blake3Hash = String.replicate 64 "d"; ItemCount = 2 }

        let baselineManifest =
            {
                SchemaVersion = 2
                Epoch = control.Epoch
                BoundaryCursor = control.CommittedCursor
                Catalog = catalog
                CreatedAt = timestamp
                Shards = [| shardReference |]
            }

        let manifest =
            FileManifest.Create(
                "manifest-serializer",
                "chunking-suite",
                content.Blake3Hash,
                content.Size,
                preparation.StoragePoolId,
                [
                    ContentBlock.Create("block-serializer", 0L, content.Size)
                ]
            )

        let contentLocation = { SchemaVersion = 2; Content = content; AuthorizedScope = preparation.AuthorizedScope; Manifest = manifest }

        let contentAvailable =
            LibraryContentAvailable.Create(repositoryId, "epoch-material", item.LastChangeCursor, catalogVersion, timestamp, "correlation-serializer")

        let properties = Dictionary<string, string>()
        properties["eventName"] <- contentAvailable.EventName
        properties["repositoryId"] <- repositoryId.ToString("D")

        let failedEnvelope =
            {
                TopicName = "grace-events"
                MessageId = $"LibraryContentAvailable/{repositoryId:D}/00000000000000017"
                Body = [| 1uy; 2uy; 3uy; 4uy |]
                ContentType = "application/json"
                Subject = contentAvailable.EventName
                CorrelationId = contentAvailable.CorrelationId
                ApplicationProperties = properties
            }

        Assert.Multiple(
            Action (fun () ->
                Assert.That(roundTrip serviceProvider slot, Is.EqualTo(slot))
                Assert.That(roundTrip serviceProvider preparation, Is.EqualTo(preparation))
                Assert.That(roundTrip serviceProvider read, Is.EqualTo(read))
                Assert.That(roundTrip serviceProvider receipt, Is.EqualTo(receipt))
                Assert.That(roundTrip serviceProvider rejectedReceipt, Is.EqualTo(rejectedReceipt))
                Assert.That(roundTrip serviceProvider bootstrap, Is.EqualTo(bootstrap))
                Assert.That(roundTrip serviceProvider changePage, Is.EqualTo(changePage))
                Assert.That(roundTrip serviceProvider rebaselinePage, Is.EqualTo(rebaselinePage))
                Assert.That(roundTrip serviceProvider catalogResult, Is.EqualTo(catalogResult))
                Assert.That(roundTrip serviceProvider status, Is.EqualTo(status))
                Assert.That(roundTrip serviceProvider namespacePrecondition, Is.EqualTo(namespacePrecondition))
                Assert.That(roundTrip serviceProvider contentPrecondition, Is.EqualTo(contentPrecondition))
                Assert.That(roundTrip serviceProvider creationExpectation, Is.EqualTo(creationExpectation))
                Assert.That(roundTrip serviceProvider commands, Is.EqualTo(box commands))
                Assert.That(roundTrip serviceProvider authorization, Is.EqualTo(authorization))
                Assert.That(roundTrip serviceProvider acceptedRecord, Is.EqualTo(acceptedRecord))
                Assert.That(roundTrip serviceProvider control, Is.EqualTo(control))
                Assert.That(roundTrip serviceProvider currentItem, Is.EqualTo(currentItem))
                Assert.That(roundTrip serviceProvider currentSlot, Is.EqualTo(currentSlot))
                Assert.That(roundTrip serviceProvider durableReceipts, Is.EqualTo(box durableReceipts))
                Assert.That(roundTrip serviceProvider historySegment, Is.EqualTo(historySegment))
                Assert.That(roundTrip serviceProvider baselineShard, Is.EqualTo(baselineShard))
                Assert.That(roundTrip serviceProvider baselineManifest, Is.EqualTo(baselineManifest))
                Assert.That(roundTrip serviceProvider contentLocation, Is.EqualTo(contentLocation))
                Assert.That(roundTrip serviceProvider contentAvailable, Is.EqualTo(contentAvailable))

                let actualEnvelope = roundTrip serviceProvider failedEnvelope
                Assert.That(actualEnvelope.TopicName, Is.EqualTo(failedEnvelope.TopicName))
                Assert.That(actualEnvelope.MessageId, Is.EqualTo(failedEnvelope.MessageId))
                Assert.That(actualEnvelope.Body, Is.EqualTo(box failedEnvelope.Body))
                Assert.That(actualEnvelope.ContentType, Is.EqualTo(failedEnvelope.ContentType))
                Assert.That(actualEnvelope.Subject, Is.EqualTo(failedEnvelope.Subject))
                Assert.That(actualEnvelope.CorrelationId, Is.EqualTo(failedEnvelope.CorrelationId))
                Assert.That(actualEnvelope.ApplicationProperties, Has.Count.EqualTo(2))
                Assert.That(actualEnvelope.ApplicationProperties["eventName"], Is.EqualTo(contentAvailable.EventName))
                Assert.That(actualEnvelope.ApplicationProperties["repositoryId"], Is.EqualTo(repositoryId.ToString("D"))))
        )
