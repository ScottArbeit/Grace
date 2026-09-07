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

        let catalog =
            {
                RepositoryId = repositoryId
                Version = catalogVersion
                Libraries = [| "Media" |]
                CreatedAt = timestamp
                CreatedBy = "user:serializer"
                PreviousVersion = Some(Guid.Parse("f1940bd5-8ca2-4760-97fb-a483a51f7fbc"))
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

        let command =
            LibraryChangeCommand.CreateFile(
                operationId,
                receipt.RequestHash,
                catalogVersion,
                { Parent = parent; Name = slot.Name; ExpectedSlotVersion = slotVersion; ExpectedState = "vacant" },
                uploadSessionId
            )

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

        Assert.Multiple(
            Action (fun () ->
                Assert.That(roundTrip serviceProvider slot, Is.EqualTo(slot))
                Assert.That(roundTrip serviceProvider preparation, Is.EqualTo(preparation))
                Assert.That(roundTrip serviceProvider read, Is.EqualTo(read))
                Assert.That(roundTrip serviceProvider receipt, Is.EqualTo(receipt))
                Assert.That(roundTrip serviceProvider bootstrap, Is.EqualTo(bootstrap))
                Assert.That(roundTrip serviceProvider changePage, Is.EqualTo(changePage))
                Assert.That(roundTrip serviceProvider command, Is.EqualTo(command))
                Assert.That(roundTrip serviceProvider authorization, Is.EqualTo(authorization)))
        )
