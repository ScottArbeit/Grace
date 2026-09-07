namespace Grace.Actors

open Grace.Shared.Validation
open Grace.Types.Common
open Grace.Types.Library
open System
open System.Security.Cryptography
open System.Text

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
        | LibraryChangeCommand.UpdateContent (value, _, _, _, _, _, _)
        | LibraryChangeCommand.Rename (value, _, _, _, _, _)
        | LibraryChangeCommand.Move (value, _, _, _, _, _)
        | LibraryChangeCommand.Delete (value, _, _, _, _, _) -> value

    /// Returns the normalized request hash embedded in a validated command.
    let requestHash =
        function
        | LibraryChangeCommand.CreateFile (_, value, _, _, _)
        | LibraryChangeCommand.CreateDirectory (_, value, _, _)
        | LibraryChangeCommand.UpdateContent (_, value, _, _, _, _, _)
        | LibraryChangeCommand.Rename (_, value, _, _, _, _)
        | LibraryChangeCommand.Move (_, value, _, _, _, _)
        | LibraryChangeCommand.Delete (_, value, _, _, _, _) -> value

    /// Returns the catalog version observed by the command caller.
    let catalogVersion =
        function
        | LibraryChangeCommand.CreateFile (_, _, value, _, _)
        | LibraryChangeCommand.CreateDirectory (_, _, value, _)
        | LibraryChangeCommand.UpdateContent (_, _, value, _, _, _, _)
        | LibraryChangeCommand.Rename (_, _, value, _, _, _)
        | LibraryChangeCommand.Move (_, _, value, _, _, _)
        | LibraryChangeCommand.Delete (_, _, value, _, _, _) -> value

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

    /// Derives the initial generation observed for a previously unseen slot.
    let initialSlotVersion repositoryId parent name = deterministicGuid repositoryId Guid.Empty ("slot:" + slotIdentity parent name)

    /// Reports whether a normalized path belongs to a configured Library root.
    let isInLibrary catalog path = Library.configurationOwnsPath catalog path

    /// Allocates the deterministic portable sibling used for a stale-content conflict copy.
    let conflictName (name: string) operationId =
        let extension = IO.Path.GetExtension name
        let stem = IO.Path.GetFileNameWithoutExtension name
        let suffix = ($".conflict-{operationId:N}")[0..18]

        let maximumStemBytes =
            Library.MaximumSegmentBytes
            - Encoding.UTF8.GetByteCount(suffix + extension)

        let mutable retained = stem

        while Encoding.UTF8.GetByteCount retained > maximumStemBytes do
            retained <- retained.Substring(0, retained.Length - 1)

        retained + suffix + extension
