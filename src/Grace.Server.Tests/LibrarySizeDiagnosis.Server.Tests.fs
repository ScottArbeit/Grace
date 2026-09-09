namespace Grace.Server.Tests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Collections.Generic
open Azure.Storage.Blobs.Specialized
open Grace.Actors
open Grace.Server.Library
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Library
open Grace.Types.ContentBlockMetadata
open Grace.Types.UploadSession
open Microsoft.Azure.Cosmos
open NUnit.Framework

/// Uses accepted Library uploads and namespace operations to prove lifetime declaration membership over HTTP.
[<NonParallelizable>]
module LibrarySizeDiagnosisHttpTests =
    let private route = "/admin/library-content-size/diagnose"

    /// Requires the real HTTP success envelope while retaining its original bytes for operator replay.
    let private post<'T> (path: string) (parameters: obj) =
        task {
            use! response = Client.PostAsync(path, createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)

            return
                (deserialize<GraceReturnValue<'T>> body)
                    .ReturnValue,
                body
        }

    /// Sets the explicit repository scope used by every Library operation.
    let inline private scope repository (parameters: ^T) =
        (^T: (member set_OwnerId: string -> unit) (parameters, ownerId))
        (^T: (member set_OrganizationId: string -> unit) (parameters, organizationId))
        (^T: (member set_RepositoryId: string -> unit) (parameters, repository))

    /// Recovers the exact storage placement supplied in the server's upload URI.
    let private placement (uri: Uri) etag : ContentBlockStoragePlacement =
        let segments =
            uri.AbsolutePath.Trim('/').Split('/')
            |> Array.map Uri.UnescapeDataString

        let pathStyle =
            uri.Host = "localhost"
            || (IPAddress.TryParse(uri.Host) |> fst)

        let account =
            uri.Fragment.TrimStart('#').Split('&')
            |> Array.tryPick (fun part ->
                let pair = part.Split('=', 2) in

                if pair.Length = 2 && pair[0] = "graceStorageAccount" then
                    Some(Uri.UnescapeDataString pair[1])
                else
                    None)
            |> Option.defaultWith (fun () -> if pathStyle then segments[0] else uri.Host.Split('.')[0])

        let index = if pathStyle && segments[0] = account then 1 else 0

        {
            StorageAccountName = account
            StorageContainerName = segments[index]
            ObjectKey = String.Join('/', segments |> Array.skip (index + 1))
            ETag = Some etag
        }

    /// Uploads real encoded bytes and finalizes their manifest through the ordinary Library and Storage routes.
    let private prepare repository operation (payload: byte array) =
        task {
            let input = Parameters.Library.PrepareLibraryContentParameters()
            scope repository input
            input.OperationId <- operation
            input.Blake3Hash <- ContentAddress.computeBlake3Hex payload
            input.Sha256Hash <- Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData payload)
            input.Size <- int64 payload.Length
            let! prepared, _ = post<LibraryContentPreparationDto> "/libraries/content/prepare" input

            let block =
                match ContentBlockFormat.encode [ { PhysicalOffset = 0L; Bytes = payload } ] with
                | Ok block -> block
                | Error error -> failwithf "%A" error

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            scope repository register
            register.UploadSessionId <- prepared.UploadSessionId
            register.AuthorizedScope <- prepared.AuthorizedScope
            register.OperationId <- string (Guid.NewGuid())
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length
            let! _, _ = post<UploadSessionDecision> "/storage/registerContentBlockUpload" register
            let upload = Parameters.Storage.GetContentBlockUploadUriParameters()
            scope repository upload
            upload.UploadSessionId <- prepared.UploadSessionId
            upload.AuthorizedScope <- prepared.AuthorizedScope
            upload.ContentBlockAddress <- block.Address
            use! response = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent upload)
            let! uriText = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), uriText)
            let uri = Uri uriText
            use stream = new MemoryStream(block.Payload, false)
            let! uploaded = BlockBlobClient(uri).UploadAsync(stream)
            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            scope repository confirm
            confirm.UploadSessionId <- prepared.UploadSessionId
            confirm.AuthorizedScope <- prepared.AuthorizedScope
            confirm.OperationId <- string (Guid.NewGuid())
            confirm.ContentBlockAddress <- block.Address
            confirm.Payload <- block.Payload
            confirm.StoragePlacement <- placement uri (uploaded.Value.ETag.ToString())
            let! _, _ = post<UploadSessionDecision> "/storage/confirmContentBlockUpload" confirm

            let blocks =
                [
                    ContentBlock.Create(block.Address, 0L, int64 payload.Length)
                ]

            let manifest = FileManifest.Create("", RabinChunking.SuiteName, input.Blake3Hash, int64 payload.Length, prepared.StoragePoolId, blocks)
            let manifest = { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }
            let finalize = Parameters.Storage.FinalizeManifestUploadParameters()
            scope repository finalize
            finalize.UploadSessionId <- prepared.UploadSessionId
            finalize.AuthorizedScope <- prepared.AuthorizedScope
            finalize.OperationId <- string (Guid.NewGuid())
            finalize.Manifest <- manifest
            let! _, _ = post<UploadSessionDecision> "/storage/finalizeManifestUpload" finalize
            return prepared
        }

    /// Requires denied callers to fail before malformed JSON can reach parsing or provider access.
    [<Test>]
    let ``Library diagnostic authorizes before parsing and requires explicit live scope`` () =
        task {
            use denied = new HttpClient(BaseAddress = Client.BaseAddress)
            denied.DefaultRequestHeaders.Add("x-grace-user-id", string (Guid.NewGuid()))
            use! response = denied.PostAsync(route, new StringContent("{", Encoding.UTF8, "application/json"))
            Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.Forbidden)
            let parameters = Parameters.Repository.GetRepositoryParameters(OwnerId = ownerId, OrganizationId = organizationId, RepositoryId = repositoryIds[0])
            parameters.RepositoryName <- "unsupported"
            use! invalid = Client.PostAsync(route, createJsonContent parameters)
            Assert.That(invalid.StatusCode, Is.EqualTo HttpStatusCode.BadRequest)
            parameters.RepositoryName <- ""
            parameters.OwnerId <- string (Guid.NewGuid())
            use! foreign = Client.PostAsync(route, createJsonContent parameters)
            let! body = foreign.Content.ReadAsStringAsync()
            Assert.That(foreign.StatusCode, Is.EqualTo HttpStatusCode.BadRequest, body)
            Assert.That(body, Does.Not.Contain "DeclaredLogicalBytes")
        }

    /// Proves actual admission across cursor 200, superseded content, repeated content, rename and delete.
    [<Test>]
    let ``accepted Library history survives edits repeats rename deletion and cursor segments`` () =
        task {
            let repository = Guid.NewGuid().ToString("D")
            let create = Parameters.Repository.CreateRepositoryParameters(RepositoryName = "LibrarySize" + Guid.NewGuid().ToString("N"))
            scope repository create
            let! _, _ = post<string> "/repository/create" create

            let grant =
                Parameters.Access.GrantRoleParameters(
                    PrincipalType = "User",
                    PrincipalId = testUserId,
                    ScopeKind = "repo",
                    RoleId = "RepositoryAdmin",
                    Source = "test"
                )

            scope repository grant
            use! granted = Client.PostAsync("/authorize/grant-role", createJsonContent grant)
            Assert.That(granted.StatusCode, Is.EqualTo HttpStatusCode.OK)
            let get = Parameters.Library.GetLibraryCatalogParameters()
            scope repository get
            let! catalog, _ = post<LibraryCatalogDto> "/libraries/catalog/get" get
            let parameters = Parameters.Repository.GetRepositoryParameters()
            scope repository parameters
            let! empty, emptyBody = post<LibraryContentSizeDiagnostic> route parameters
            Assert.That(empty.DeclaredLogicalBytes, Is.Zero)
            Assert.That(empty.DistinctManifestCount, Is.Zero)
            Assert.That(empty.CommittedCursor, Is.Zero)
            use emptyJson = JsonDocument.Parse(emptyBody)
            let emptyValue = emptyJson.RootElement.GetProperty("ReturnValue")

            Assert.That(
                emptyValue
                    .GetProperty("DeclaredLogicalBytes")
                    .GetInt64(),
                Is.EqualTo 0L
            )

            Assert.That(
                emptyValue
                    .GetProperty("DistinctManifestCount")
                    .GetInt64(),
                Is.EqualTo 0L
            )

            Assert.That(
                emptyValue
                    .GetProperty("CommittedCursor")
                    .GetInt64(),
                Is.EqualTo 0L
            )

            TestContext.Error.WriteLine(
                "LIBRARY_SIZE_DIAGNOSTIC_HOSTED_ZERO_JSON_BASE64:"
                + Convert.ToBase64String(Encoding.UTF8.GetBytes emptyBody)
            )

            let zeroOutput = Path.Combine(TestContext.CurrentContext.WorkDirectory, "library-size-diagnostic-hosted-zero-response.json")
            File.WriteAllText(zeroOutput, emptyBody)
            TestContext.AddTestAttachment(zeroOutput, "Exact hosted zero Library declaration envelope for Windows operator replay.")
            let add = Parameters.Library.AddLibraryParameters(ExpectedVersion = catalog.Version, LibraryPath = "Library", OperationId = Guid.NewGuid())
            scope repository add
            let! configured, _ = post<LibraryCatalogChangeResultDto> "/libraries/add" add
            let parent = { Kind = "root"; LibraryPath = Some "Library"; ItemId = None }
            let slotRequest = Parameters.Library.GetLibraryNamespaceSlotParameters(Parent = Some parent, Name = "sample.bin")
            scope repository slotRequest
            let! slot, _ = post<LibraryNamespaceSlotDto> "/libraries/namespace/get-slot" slotRequest
            let payloadA = Encoding.UTF8.GetBytes("Library-size-A-" + Guid.NewGuid().ToString("N"))

            let payloadB =
                Encoding.UTF8.GetBytes(
                    "Library-size-B-longer-"
                    + Guid.NewGuid().ToString("N")
                )

            let operation = Guid.NewGuid()
            let! prepared = prepare repository operation payloadA

            let submit =
                Parameters.Library.SubmitLibraryChangeParameters(
                    OperationId = operation,
                    LibraryCatalogVersion = configured.LibraryCatalog.Version,
                    ChangeKind = ChangeKind.CreateFile,
                    ItemKind = ItemKind.File,
                    UploadSessionId = Nullable prepared.UploadSessionId,
                    CreationSlotExpectation = Some { Parent = parent; Name = "sample.bin"; ExpectedSlotVersion = slot.SlotVersion; ExpectedState = "vacant" }
                )

            scope repository submit
            let! accepted, _ = post<LibraryOperationReceiptDto> "/libraries/changes/submit" submit
            Assert.That(accepted.Outcome, Is.EqualTo OutcomeKind.Accepted)
            let mutable item = accepted.Change.Value.Item
            let mutable stage = 0

            while stage < 2 do
                let operation = Guid.NewGuid()
                let! uploaded = prepare repository operation (if stage = 0 then payloadB else payloadA)

                let update =
                    Parameters.Library.SubmitLibraryChangeParameters(
                        OperationId = operation,
                        LibraryCatalogVersion = configured.LibraryCatalog.Version,
                        ChangeKind = ChangeKind.UpdateContent,
                        ItemKind = ItemKind.File,
                        ItemId = Nullable item.ItemId,
                        UploadSessionId = Nullable uploaded.UploadSessionId
                    )

                scope repository update
                update.NamespacePrecondition <- Some { ItemId = item.ItemId; ExpectedNamespaceVersion = item.Namespace.Value.NamespaceVersion }

                update.ContentPrecondition <-
                    Some
                        {
                            ItemId = item.ItemId
                            ExpectedContentVersionId = item.Content.Value.ContentVersionId
                            ExpectedContentRevision = item.ContentRevision.Value
                        }

                let! changed, _ = post<LibraryOperationReceiptDto> "/libraries/changes/submit" update
                Assert.That(changed.Outcome, Is.EqualTo OutcomeKind.Accepted)
                item <- changed.Change.Value.Item
                stage <- stage + 1

            let duplicateSlotRequest = Parameters.Library.GetLibraryNamespaceSlotParameters(Parent = Some parent, Name = "same-content.bin")
            scope repository duplicateSlotRequest
            let! duplicateSlot, _ = post<LibraryNamespaceSlotDto> "/libraries/namespace/get-slot" duplicateSlotRequest
            let duplicateOperation = Guid.NewGuid()
            let! duplicateUpload = prepare repository duplicateOperation payloadA

            let duplicate =
                Parameters.Library.SubmitLibraryChangeParameters(
                    OperationId = duplicateOperation,
                    LibraryCatalogVersion = configured.LibraryCatalog.Version,
                    ChangeKind = ChangeKind.CreateFile,
                    ItemKind = ItemKind.File,
                    UploadSessionId = Nullable duplicateUpload.UploadSessionId,
                    CreationSlotExpectation =
                        Some { Parent = parent; Name = "same-content.bin"; ExpectedSlotVersion = duplicateSlot.SlotVersion; ExpectedState = "vacant" }
                )

            scope repository duplicate
            let! duplicateReceipt, _ = post<LibraryOperationReceiptDto> "/libraries/changes/submit" duplicate
            Assert.That(duplicateReceipt.Outcome, Is.EqualTo OutcomeKind.Accepted)
            Assert.That(duplicateReceipt.Change.Value.Item.ItemId, Is.Not.EqualTo item.ItemId)
            Assert.That(duplicateReceipt.Change.Value.Item.Content.Value.ContentVersionId, Is.EqualTo item.Content.Value.ContentVersionId)
            let mutable cursor = 4

            while cursor < 204 do
                let rename =
                    Parameters.Library.SubmitLibraryChangeParameters(
                        OperationId = Guid.NewGuid(),
                        LibraryCatalogVersion = configured.LibraryCatalog.Version,
                        ChangeKind = ChangeKind.Rename,
                        ItemKind = ItemKind.File,
                        ItemId = Nullable item.ItemId,
                        DestinationName = $"renamed-{cursor}.bin",
                        NamespacePrecondition = Some { ItemId = item.ItemId; ExpectedNamespaceVersion = item.Namespace.Value.NamespaceVersion }
                    )

                scope repository rename
                let! changed, _ = post<LibraryOperationReceiptDto> "/libraries/changes/submit" rename
                Assert.That(changed.Outcome, Is.EqualTo OutcomeKind.Accepted)
                item <- changed.Change.Value.Item
                cursor <- cursor + 1

            let delete =
                Parameters.Library.SubmitLibraryChangeParameters(
                    OperationId = Guid.NewGuid(),
                    LibraryCatalogVersion = configured.LibraryCatalog.Version,
                    ChangeKind = ChangeKind.Delete,
                    ItemKind = ItemKind.File,
                    ItemId = Nullable item.ItemId,
                    NamespacePrecondition = Some { ItemId = item.ItemId; ExpectedNamespaceVersion = item.Namespace.Value.NamespaceVersion },
                    ContentPrecondition =
                        Some
                            {
                                ItemId = item.ItemId
                                ExpectedContentVersionId = item.Content.Value.ContentVersionId
                                ExpectedContentRevision = item.ContentRevision.Value
                            }
                )

            scope repository delete
            let! deleted, _ = post<LibraryOperationReceiptDto> "/libraries/changes/submit" delete
            Assert.That(deleted.Outcome, Is.EqualTo OutcomeKind.Accepted)
            Assert.That(deleted.Change.Value.Item.Tombstone.IsSome, Is.True)
            let! result, body = post<LibraryContentSizeDiagnostic> route parameters
            Assert.That(result.DeclaredLogicalBytes, Is.EqualTo(int64 (payloadA.Length + payloadB.Length)))
            Assert.That(result.DistinctManifestCount, Is.EqualTo 2L)
            Assert.That(result.CommittedCursor, Is.EqualTo 205L)
            Assert.That(result.Epoch, Is.EqualTo empty.Epoch)

            TestContext.Error.WriteLine(
                "LIBRARY_SIZE_DIAGNOSTIC_HOSTED_POSITIVE_JSON_BASE64:"
                + Convert.ToBase64String(Encoding.UTF8.GetBytes body)
            )

            let positiveOutput = Path.Combine(TestContext.CurrentContext.WorkDirectory, "library-size-diagnostic-hosted-response.json")
            File.WriteAllText(positiveOutput, body)
            TestContext.AddTestAttachment(positiveOutput, "Exact hosted positive Library declaration envelope for Windows operator replay.")
        }
