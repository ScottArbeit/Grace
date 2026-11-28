namespace Grace.Actors

open Grace.Shared.Validation
open Grace.Types.Common
open Grace.Types.Library
open System
open System.Security.Cryptography
open System.Text
open System.Collections.Generic

/// Makes deterministic Library decisions without reading storage or invoking actors.
module LibraryDecision =

    /// Derives a stable UUID from repository, operation and purpose.
    let deterministicGuid repositoryId operationId purpose =
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"Grace.Library.v1:{repositoryId:D}:{operationId:D}:{purpose}"))
        let value = bytes[0..15]
        value[6] <- (value[6] &&& 0x0Fuy) ||| 0x50uy
        value[8] <- (value[8] &&& 0x3Fuy) ||| 0x80uy
        Guid value

    /// Derives the stable public identity of one immutable byte value.
    let contentVersionId blake3Hash = deterministicGuid Guid.Empty Guid.Empty blake3Hash

    /// Returns the operation identity embedded in a validated command.
    let operationId =
        function
        | LibraryChangeCommand.CreateFile (value, _, _, _, _)
        | LibraryChangeCommand.CreateDirectory (value, _, _, _)
        | LibraryChangeCommand.UpdateContent (value, _, _, _, _, _)
        | LibraryChangeCommand.Rename (value, _, _, _, _)
        | LibraryChangeCommand.Move (value, _, _, _, _)
        | LibraryChangeCommand.Delete (value, _, _, _, _) -> value

    /// Returns the normalized request hash embedded in a validated command.
    let requestHash =
        function
        | LibraryChangeCommand.CreateFile (_, value, _, _, _)
        | LibraryChangeCommand.CreateDirectory (_, value, _, _)
        | LibraryChangeCommand.UpdateContent (_, value, _, _, _, _)
        | LibraryChangeCommand.Rename (_, value, _, _, _)
        | LibraryChangeCommand.Move (_, value, _, _, _)
        | LibraryChangeCommand.Delete (_, value, _, _, _) -> value

    /// Reports whether a change names a Library root, as its parent or destination, that the current catalog no longer
    /// has. #1111 decision 1: that is the only way a catalog change makes a change stale. A change under a live
    /// directory can't reach a removed root, because a Library can't be removed while it holds live items.
    let namesRootOutside (catalog: LibraryCatalogDto) command =
        let parent =
            match command with
            | LibraryChangeCommand.CreateFile (_, _, parent, _, _) -> Some parent
            | LibraryChangeCommand.CreateDirectory (_, _, parent, _) -> Some parent
            | LibraryChangeCommand.Move (_, _, _, _, destination) -> Some destination
            | LibraryChangeCommand.UpdateContent _
            | LibraryChangeCommand.Rename _
            | LibraryChangeCommand.Delete _ -> None

        match parent with
        | Some { Kind = "root"; LibraryPath = Some root } ->
            not (
                catalog.Libraries
                |> Array.exists (Library.pathsEqual root)
            )
        | _ -> false

    /// Returns the public change kind represented by a validated command case.
    let changeKind =
        function
        | LibraryChangeCommand.CreateFile _ -> ChangeKind.CreateFile
        | LibraryChangeCommand.CreateDirectory _ -> ChangeKind.CreateDirectory
        | LibraryChangeCommand.UpdateContent _ -> ChangeKind.UpdateContent
        | LibraryChangeCommand.Rename _ -> ChangeKind.Rename
        | LibraryChangeCommand.Move _ -> ChangeKind.Move
        | LibraryChangeCommand.Delete _ -> ChangeKind.Delete

    /// Normalizes a portable sibling name using the shared Product V1 validator.
    let normalizeName name =
        match Library.normalizeName name with
        | Ok value -> value
        | Error message -> invalidArg (nameof name) message

    /// Compares two parent identities using portable root-path equality.
    let parentsEqual (left: LibraryParentDto) (right: LibraryParentDto) =
        left.Kind = right.Kind
        && left.ItemId = right.ItemId
        && match left.LibraryPath, right.LibraryPath with
           | Some l, Some r -> Library.pathsEqual l r
           | None, None -> true
           | _ -> false

    /// Returns the unambiguous length-delimited identity hashed for one parent/name slot.
    let slotIdentity (parent: LibraryParentDto) name =
        let normalizedName =
            normalizeName name
            |> fun value -> value.ToUpperInvariant()

        let parentIdentity =
            match parent.Kind, parent.LibraryPath, parent.ItemId with
            | "root", Some root, None ->
                "root:"
                + root
                    .Normalize(NormalizationForm.FormC)
                    .ToUpperInvariant()
            | "item", None, Some itemId -> "item:" + itemId.ToString("D")
            | _ -> invalidArg (nameof parent) "A Library parent must identify one configured root or directory item."

        let encode (value: string) = $"{Encoding.UTF8.GetByteCount value}:{value}"
        encode parentIdentity + encode normalizedName

    /// Hashes one full parent/name identity for its stable Cosmos record key.
    let slotKey parent name =
        slotIdentity parent name
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    /// Reports whether a normalized path belongs to a configured Library root.
    let isInLibrary catalog path = Library.configurationOwnsPath catalog path

    /// The most item records, and separately the most slot records, one repository keeps. Tombstones and vacated slots
    /// count, because their records remain.
    [<Literal>]
    let MaximumRecordCount = 100000

    /// The one capacity rule for every change: refuse it when it would add an item record, or a slot record, past the
    /// limit. Creates, conflict copies, renames and moves all use it.
    let exceedsRecordCapacity itemRecordCount slotRecordCount addsItemRecord addsSlotRecord =
        (addsItemRecord
         && itemRecordCount >= MaximumRecordCount)
        || (addsSlotRecord
            && slotRecordCount >= MaximumRecordCount)

    /// Reports whether a complete repository-relative path is over the Product V1 limit of 1,024 UTF-8 bytes.
    let pathTooLong (path: string) = Encoding.UTF8.GetByteCount path > Library.MaximumPathBytes

    /// Returns why a single item placed at this full path can't be accepted, or None when it can.
    let placementRejection catalog (path: string) =
        if pathTooLong path then Some RejectionReason.PathTooLong
        elif not (isInLibrary catalog path) then Some OutcomeKind.StalePolicy
        else None

    /// Returns why a directory can't move or be renamed to destinationPath/movingName, or None when it can. Every live
    /// item under it gets a new path, and each one must stay inside a Library and within the path limit.
    let subtreeRejection catalog destinationPath movingItemId movingName (documents: LibraryCurrentItemDocument array) =
        let children = Dictionary<LibraryItemId, ResizeArray<LibraryItemDto>>()

        documents
        |> Array.choose (fun document ->
            if document.Item.Tombstone.IsNone then
                document.Item.Namespace
                |> Option.map (fun ns -> ns, document.Item)
            else
                None)
        |> Array.iter (fun (ns, item) ->
            match ns.Parent.ItemId with
            | Some parentId ->
                match children.TryGetValue parentId with
                | true, values -> values.Add item
                | false, _ -> children.Add(parentId, ResizeArray [ item ])
            | None -> ())

        let rootPath = destinationPath + "/" + movingName
        let pending = Stack<struct (LibraryItemId * string)>()
        pending.Push(struct (movingItemId, rootPath))
        let mutable rejection = None

        while rejection.IsNone && pending.Count > 0 do
            let struct (parentId, parentPath) = pending.Pop()

            match placementRejection catalog parentPath with
            | Some reason -> rejection <- Some reason
            | None ->
                match children.TryGetValue parentId with
                | true, values ->
                    values
                    |> Seq.iter (fun item ->
                        match item.Namespace with
                        | Some ns -> pending.Push(struct (item.ItemId, parentPath + "/" + ns.Name))
                        | None -> ())
                | false, _ -> ()

        rejection
