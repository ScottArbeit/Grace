namespace Grace.Server.Tests

open System
open System.Net
open System.Text
open Grace.Server.Tests.LibraryHttp
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
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

    /// Creates a repository the test user administers and returns its default branch ID.
    let private administeredRepository (repository: string) =
        task {
            let create = Parameters.Repository.CreateRepositoryParameters(RepositoryName = "LibraryAdmission" + Guid.NewGuid().ToString("N"))
            scope repository create
            let! _, body = post<string> "/repository/create" create
            let created = deserialize<GraceReturnValue<string>> body
            let branchId = Common.requireGuidProperty (nameof BranchId) created.Properties[nameof BranchId]

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
            return $"{branchId}"
        }

    /// Adds one Library at root and returns the new catalog.
    let private addLibrary (repository: string) (root: string) =
        task {
            let get = Parameters.Library.GetLibraryCatalogParameters()
            scope repository get
            let! catalog, _ = post<LibraryCatalogDto> "/libraries/catalog/get" get
            let add = Parameters.Library.AddLibraryParameters(ExpectedVersion = catalog.Version, LibraryPath = root, OperationId = Guid.NewGuid())
            scope repository add
            let! added, _ = post<LibraryCatalogChangeResultDto> "/libraries/add" add
            Assert.That(added.Outcome, Is.EqualTo OutcomeKind.Accepted)
            return added.LibraryCatalog
        }

    /// Creates a repository the test user administers, adds one Library at root, and returns the new catalog.
    let private libraryRepository (repository: string) (root: string) =
        task {
            let! _ = administeredRepository repository
            return! addLibrary repository root
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

    /// A conflict copy follows the same capacity rule as a create: at the slot limit, a copy that needs a new slot record
    /// is refused. The test sets the stored slot count to the limit rather than creating 100,000 records.
    [<Test>]
    let ``a conflict copy at slot capacity is refused`` () =
        task {
            let repository = Guid.NewGuid().ToString("D")
            let repositoryId = Guid.Parse repository
            let! catalog = libraryRepository repository "Shared"
            let! original = createFile repository catalog (rootParent "Shared") "edited.txt" "base"
            let original = acceptedItem original
            let! edited = updateContent repository catalog original original "first edit"
            acceptedItem edited |> ignore

            let! _ =
                LibraryActorSnapshots.rewriteFrom<LibraryControlDocument>
                    HostState.Value
                    "grace-library-control"
                    "Grace.Library.Control.v2"
                    (fun control -> control.Catalog.RepositoryId = repositoryId)
                    (fun control -> { control with SlotRecordCount = Grace.Actors.LibraryDecision.MaximumRecordCount })
                    "Library control at slot capacity"

            let operation = Guid.NewGuid()
            let! prepared = prepare repository operation (Encoding.UTF8.GetBytes "stale edit")

            let stale =
                Parameters.Library.SubmitLibraryChangeParameters(
                    OperationId = operation,
                    LibraryCatalogVersion = catalog.Version,
                    ChangeKind = ChangeKind.UpdateContent,
                    ItemKind = ItemKind.File,
                    ItemId = Nullable original.ItemId,
                    UploadSessionId = Nullable prepared.UploadSessionId,
                    ContentPrecondition =
                        Some
                            {
                                ItemId = original.ItemId
                                ExpectedContentVersionId = original.Content.Value.ContentVersionId
                                ExpectedContentRevision = original.ContentRevision.Value
                            }
                )

            scope repository stale
            use! response = Client.PostAsync("/libraries/changes/submit", createJsonContent stale)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.Not.EqualTo HttpStatusCode.OK, body)
            Assert.That(body, Does.Contain("capacity"))
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

    /// Waits for a change at most 30 seconds, so a change that waited on its wake fails the test instead of hanging it.
    let private withinThirtySeconds (change: Threading.Tasks.Task<LibraryOperationReceiptDto>) =
        task {
            let! finished = Threading.Tasks.Task.WhenAny(change, Threading.Tasks.Task.Delay(TimeSpan.FromSeconds 30.0))
            Assert.That(Object.ReferenceEquals(finished, change), Is.True, "The change waited for its wake.")
            return! change
        }

    /// A change never waits for its wake: the hosted server makes every wake for this one repository never finish, and
    /// each Submit still returns, accepted and committed, and the next change is accepted too.
    [<Test>]
    let ``a change never waits for its wake`` () =
        task {
            let repository = AspireTestHost.libraryWakeFaultRepositoryId.ToString("D")
            let! catalog = libraryRepository repository "Shared"

            let! first =
                createDirectory repository catalog (rootParent "Shared") "first"
                |> withinThirtySeconds

            acceptedItem first |> ignore

            let! second =
                createDirectory repository catalog (rootParent "Shared") "second"
                |> withinThirtySeconds

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

    /// Posts directory versions to save and returns the status and body, so a test can read a rejection.
    let private trySaveDirectoryVersions repository (directoryVersions: Grace.Types.Common.DirectoryVersion list) =
        task {
            let parameters = Parameters.DirectoryVersion.SaveDirectoryVersionsParameters()
            scope repository parameters
            parameters.CorrelationId <- generateCorrelationId ()

            for directoryVersion in directoryVersions do
                parameters.DirectoryVersions.Add directoryVersion

            use! response = Client.PostAsync("/directory/saveDirectoryVersions", createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            return response.StatusCode, body
        }

    /// Version control can't save or reference a path a Library owns, and a sibling whose name only starts with the
    /// Library's name is still version-controlled. Each request checks all of its paths against one catalog read.
    [<Test>]
    let ``version control can't save or reference a Library path`` () =
        task {
            let repository = Guid.NewGuid().ToString("D")
            let! branchId = administeredRepository repository

            let directory path children = BranchServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repository (RelativePath path) children

            // Adding a Library checks only references, so a root saved first can hold paths the Library then owns.
            let docs = directory "Shared/docs" []
            let shared = directory "Shared" [ docs ]
            let ownedRoot = directory Constants.RootDirectoryPath [ shared ]
            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repository [ docs; shared; ownedRoot ]
            let! _ = addLibrary repository "Shared"

            let! savedStatus, savedBody = trySaveDirectoryVersions repository [ directory "Shared/notes" [] ]
            Assert.That(savedStatus, Is.EqualTo HttpStatusCode.BadRequest, savedBody)
            Assert.That(savedBody, Does.Contain "Path 'Shared/notes' belongs to the current Library policy and cannot be saved")

            let old = directory "Shared-old" []
            let siblingRoot = directory Constants.RootDirectoryPath [ old ]
            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repository [ old; siblingRoot ]

            let! branch = BranchServerTestHelpers.getBranchAsync repository branchId
            use! owned = BranchServerTestHelpers.saveReferenceResponseAsync repository branch ownedRoot.DirectoryVersionId ownedRoot.Sha256Hash
            let! ownedBody = owned.Content.ReadAsStringAsync()
            Assert.That(owned.StatusCode, Is.EqualTo HttpStatusCode.BadRequest, ownedBody)
            Assert.That(ownedBody, Does.Contain "belongs to the current Library policy and cannot be referenced")

            use! sibling = BranchServerTestHelpers.saveReferenceResponseAsync repository branch siblingRoot.DirectoryVersionId siblingRoot.Sha256Hash
            let! siblingBody = sibling.Content.ReadAsStringAsync()
            Assert.That(sibling.StatusCode, Is.EqualTo HttpStatusCode.OK, siblingBody)
        }
