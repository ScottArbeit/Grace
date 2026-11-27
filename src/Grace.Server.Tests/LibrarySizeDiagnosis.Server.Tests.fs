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
open Grace.Server.Tests.LibraryHttp
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

            TestContext.Error.WriteLine(
                "LIBRARY_SIZE_DIAGNOSTIC_HOSTED_POSITIVE_JSON_BASE64:"
                + Convert.ToBase64String(Encoding.UTF8.GetBytes body)
            )

            let positiveOutput = Path.Combine(TestContext.CurrentContext.WorkDirectory, "library-size-diagnostic-hosted-response.json")
            File.WriteAllText(positiveOutput, body)
            TestContext.AddTestAttachment(positiveOutput, "Exact hosted positive Library declaration envelope for Windows operator replay.")
        }
