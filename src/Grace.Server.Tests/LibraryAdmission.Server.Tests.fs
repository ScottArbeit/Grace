namespace Grace.Server.Tests

open System
open System.Net
open System.Text
open Grace.Server.Tests.LibraryHttp
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Library
open NUnit.Framework

/// Proves Library admission rules that need the real server: Cosmos queries, uploaded content and the broker.
[<NonParallelizable>]
module LibraryAdmissionHttpTests =

    /// A Library root of 883 UTF-8 bytes, so a short name fits under the 1,024-byte limit and a long one doesn't.
    let private longRoot =
        [ 'a'; 'b'; 'c'; 'd' ]
        |> List.map (fun letter -> String.replicate 220 (string letter))
        |> String.concat "/"

    /// The parent edge for items directly under a root.
    let private rootParent root = { Kind = "root"; LibraryPath = Some root; ItemId = None }

    /// The parent edge for items under a directory item.
    let private itemParent (itemId: Guid) = { Kind = "item"; LibraryPath = None; ItemId = Some itemId }

    /// Creates a repository the test user administers, adds one Library at root, and returns the new catalog.
    let private libraryRepository (repository: string) (root: string) =
        task {
            let create = Parameters.Repository.CreateRepositoryParameters(RepositoryName = "LibraryAdmission" + Guid.NewGuid().ToString("N"))
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
            let add = Parameters.Library.AddLibraryParameters(ExpectedVersion = catalog.Version, LibraryPath = root, OperationId = Guid.NewGuid())
            scope repository add
            let! added, _ = post<LibraryCatalogChangeResultDto> "/libraries/add" add
            Assert.That(added.Outcome, Is.EqualTo OutcomeKind.Accepted)
            return added.LibraryCatalog
        }

    /// Submits one change and returns its receipt.
    let private submit repository (parameters: Parameters.Library.SubmitLibraryChangeParameters) =
        task {
            scope repository parameters
            let! receipt, _ = post<LibraryOperationReceiptDto> "/libraries/changes/submit" parameters
            return receipt
        }

    /// Reads the current version of the slot a create expects to find vacant.
    let private vacancy repository parent (name: string) =
        task {
            let request = Parameters.Library.GetLibraryNamespaceSlotParameters(Parent = Some parent, Name = name)
            scope repository request
            let! slot, _ = post<LibraryNamespaceSlotDto> "/libraries/namespace/get-slot" request
            return { Parent = parent; Name = name; ExpectedSlotVersion = slot.SlotVersion; ExpectedState = "vacant" }
        }

    /// Creates a directory and returns its receipt.
    let private createDirectory repository (catalog: LibraryCatalogDto) parent name =
        task {
            let! expectation = vacancy repository parent name

            return!
                Parameters.Library.SubmitLibraryChangeParameters(
                    OperationId = Guid.NewGuid(),
                    LibraryCatalogVersion = catalog.Version,
                    ChangeKind = ChangeKind.CreateDirectory,
                    ItemKind = ItemKind.Directory,
                    CreationSlotExpectation = Some expectation
                )
                |> submit repository
        }

    /// Uploads bytes and creates a file from them, returning its receipt.
    let private createFile repository (catalog: LibraryCatalogDto) parent name (text: string) =
        task {
            let operation = Guid.NewGuid()
            let! prepared = prepare repository operation (Encoding.UTF8.GetBytes text)
            let! expectation = vacancy repository parent name

            return!
                Parameters.Library.SubmitLibraryChangeParameters(
                    OperationId = operation,
                    LibraryCatalogVersion = catalog.Version,
                    ChangeKind = ChangeKind.CreateFile,
                    ItemKind = ItemKind.File,
                    UploadSessionId = Nullable prepared.UploadSessionId,
                    CreationSlotExpectation = Some expectation
                )
                |> submit repository
        }

    /// Uploads new bytes for a file, claiming the given content as its base, and returns the receipt.
    let private updateContent repository (catalog: LibraryCatalogDto) (item: LibraryItemDto) (baseItem: LibraryItemDto) (text: string) =
        task {
            let operation = Guid.NewGuid()
            let! prepared = prepare repository operation (Encoding.UTF8.GetBytes text)

            return!
                Parameters.Library.SubmitLibraryChangeParameters(
                    OperationId = operation,
                    LibraryCatalogVersion = catalog.Version,
                    ChangeKind = ChangeKind.UpdateContent,
                    ItemKind = ItemKind.File,
                    ItemId = Nullable item.ItemId,
                    UploadSessionId = Nullable prepared.UploadSessionId,
                    ContentPrecondition =
                        Some
                            {
                                ItemId = item.ItemId
                                ExpectedContentVersionId = baseItem.Content.Value.ContentVersionId
                                ExpectedContentRevision = baseItem.ContentRevision.Value
                            }
                )
                |> submit repository
        }

    /// Returns the accepted item from a receipt, failing the test when the change wasn't accepted.
    let private acceptedItem (receipt: LibraryOperationReceiptDto) =
        Assert.That(receipt.Outcome, Is.EqualTo OutcomeKind.Accepted, $"Expected acceptance, got {receipt.Outcome} {receipt.ReasonCode}.")
        receipt.Change.Value.Item

    /// The precondition naming an item's current placement.
    let private placementOf (item: LibraryItemDto) = { ItemId = item.ItemId; ExpectedNamespaceVersion = item.Namespace.Value.NamespaceVersion }

    /// Every change kind that needs the real server rejects a resulting full path over 1,024 UTF-8 bytes: a file
    /// create, a directory rename or move that would push a descendant over, and a conflict copy whose generated name
    /// is longer than the original.
    [<Test>]
    let ``changes whose resulting path passes 1,024 bytes are rejected`` () =
        task {
            let repository = Guid.NewGuid().ToString("D")
            let! catalog = libraryRepository repository longRoot
            let root = rootParent longRoot

            // File create: 883 + 1 + 150 bytes.
            let! longFile = createFile repository catalog root (String.replicate 150 "f") "too long"
            Assert.That(longFile.ReasonCode, Is.EqualTo(Some RejectionReason.PathTooLong), "file create")

            // Directory rename: the directory's own path fits, but its child's would not.
            let! directory = createDirectory repository catalog root "d"
            let directory = acceptedItem directory
            let! child = createFile repository catalog (itemParent directory.ItemId) (String.replicate 100 "c") "child"
            acceptedItem child |> ignore

            let! renamed =
                Parameters.Library.SubmitLibraryChangeParameters(
                    OperationId = Guid.NewGuid(),
                    LibraryCatalogVersion = catalog.Version,
                    ChangeKind = ChangeKind.Rename,
                    ItemKind = ItemKind.Directory,
                    ItemId = Nullable directory.ItemId,
                    NamespacePrecondition = Some(placementOf directory),
                    DestinationName = String.replicate 60 "r"
                )
                |> submit repository

            Assert.That(renamed.ReasonCode, Is.EqualTo(Some RejectionReason.PathTooLong), "directory rename")

            // Directory move: moving "d" under a 100-byte directory pushes its child to 883 + 1 + 100 + 1 + 1 + 1 + 100.
            let! destination = createDirectory repository catalog root (String.replicate 100 "m")
            let destination = acceptedItem destination

            let! moved =
                Parameters.Library.SubmitLibraryChangeParameters(
                    OperationId = Guid.NewGuid(),
                    LibraryCatalogVersion = catalog.Version,
                    ChangeKind = ChangeKind.Move,
                    ItemKind = ItemKind.Directory,
                    ItemId = Nullable directory.ItemId,
                    NamespacePrecondition = Some(placementOf directory),
                    DestinationParent = Some(itemParent destination.ItemId)
                )
                |> submit repository

            Assert.That(moved.ReasonCode, Is.EqualTo(Some RejectionReason.PathTooLong), "directory move")

            // Conflict copy: the original fits at 1,009 bytes; its conflict name adds 19.
            let! original = createFile repository catalog root (String.replicate 121 "o" + ".txt") "base"
            let original = acceptedItem original
            let! edited = updateContent repository catalog original original "first edit"
            let edited = acceptedItem edited
            let! conflict = updateContent repository catalog edited original "stale edit"
            Assert.That(conflict.ReasonCode, Is.EqualTo(Some RejectionReason.PathTooLong), "conflict copy")
        }

    /// Removing a Library is refused while any live item is under its root, and allowed once only tombstones remain.
    [<Test>]
    let ``removing a Library needs every item under it deleted`` () =
        task {
            let repository = Guid.NewGuid().ToString("D")
            let! catalog = libraryRepository repository "Shared"
            let! directory = createDirectory repository catalog (rootParent "Shared") "keep"
            let directory = acceptedItem directory

            let remove version =
                task {
                    let request = Parameters.Library.RemoveLibraryParameters(ExpectedVersion = version, LibraryPath = "Shared", OperationId = Guid.NewGuid())
                    scope repository request
                    let! result, _ = post<LibraryCatalogChangeResultDto> "/libraries/remove" request
                    return result
                }

            let! occupied = remove catalog.Version
            Assert.That(occupied.ReasonCode, Is.EqualTo(Some CatalogRejectionReason.SlotOccupied))

            let! deleted =
                Parameters.Library.SubmitLibraryChangeParameters(
                    OperationId = Guid.NewGuid(),
                    LibraryCatalogVersion = catalog.Version,
                    ChangeKind = ChangeKind.Delete,
                    ItemKind = ItemKind.Directory,
                    ItemId = Nullable directory.ItemId,
                    NamespacePrecondition = Some(placementOf directory)
                )
                |> submit repository

            acceptedItem deleted |> ignore
            let! removed = remove catalog.Version
            Assert.That(removed.Outcome, Is.EqualTo OutcomeKind.Accepted, $"{removed.ReasonCode}")
            Assert.That(removed.LibraryCatalog.Libraries, Is.Empty)
        }

    /// A wake the broker refuses never fails the change: Submit is accepted, the change commits, and the next change
    /// is accepted too. The hosted server makes every wake for this one repository fail.
    [<Test>]
    let ``a failed wake never fails the change`` () =
        task {
            let repository = AspireTestHost.libraryWakeFaultRepositoryId.ToString("D")
            let! catalog = libraryRepository repository "Shared"
            let! first = createDirectory repository catalog (rootParent "Shared") "first"
            acceptedItem first |> ignore
            let! second = createDirectory repository catalog (rootParent "Shared") "second"
            acceptedItem second |> ignore

            let status = Parameters.Library.GetLibraryStatusParameters()
            scope repository status
            let! current, _ = post<LibraryRepositoryStatusDto> "/libraries/status/get" status

            Assert.Multiple(
                Action (fun () ->
                    Assert.That(current.State, Is.EqualTo("ready"))
                    Assert.That(current.PendingOperationCount, Is.Zero)
                    Assert.That(current.LastCompletedAt, Is.EqualTo(Some second.Change.Value.AcceptedAt)))
            )
        }
