namespace Grace.CLI.Command

open Grace.Shared.Services
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Common
open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks

/// Defines the private immutable contracts that precede every Working Directory Update mutation stage.
module internal WorkingDirectoryUpdateContracts =
    /// Identifies the caller policy that owns progress after local content is proven.
    type CallerKind =
        | Watch
        | Branch
        | Connect

    /// Represents a complete immutable selected update target.
    type Target =
        private | Target of
            repositoryId: RepositoryId *
            branchId: BranchId *
            rootDirectoryVersionId: DirectoryVersionId *
            sha256Hash: Sha256Hash *
            blake3Hash: Blake3Hash *
            canonical: string

    /// Represents the path-derived Connect scope that separates local working roots.
    type LocalRootScope = private LocalRootScope of string

    /// States whether a Branch update may publish a selected Reference or must preserve the current branch identity.
    type BranchSelection =
        | Reference of ReferenceId
        | DirectoryVersion

    /// Represents one deterministic caller-specific logical update identity.
    type Operation = private Operation of callerKind: CallerKind * target: Target option * repositoryId: RepositoryId * branchId: BranchId * value: string

    /// Represents one random marker token that never participates in logical operation identity.
    type AttemptToken = private AttemptToken of string

    /// Names a declared directory or a dual-hashed file in a prepared-content manifest.
    type PreparedManifestEntry =
        | Directory of RelativePath
        | File of RelativePath * Sha256Hash * Blake3Hash

    /// Represents a structurally valid immutable prepared-content manifest.
    type PreparedManifest = private PreparedManifest of PreparedManifestEntry array * PreparedFile array

    /// Holds one canonical file declaration used during actual-byte validation.
    and private PreparedFile = { RelativePath: RelativePath; Sha256Hash: Sha256Hash; Blake3Hash: Blake3Hash }

    /// Supplies only declared uncompressed paths and readable bytes to preparation.
    type IPreparedContentReader =
        inherit IDisposable

        /// Lists all non-directory paths that can be opened by this reader.
        abstract member FilePaths: seq<string>

        /// Opens uncompressed bytes for a normalized manifest file path.
        abstract member OpenReadAsync: RelativePath * CancellationToken -> Task<Stream>

    /// Represents immutable dual-hash-verified bytes for a future update engine.
    type PreparedContent = private PreparedContent of PreparedManifest * Dictionary<string, byte array> * disposed: bool ref

    /// Holds validated update inputs without exposing a mutation plan, writer, or transaction callback.
    type Request = private Request of Target * Operation * PreparedContent * CorrelationId

    /// Holds the target and operation facts proven by a completed update attempt.
    type Receipt = private Receipt of Target * Operation * bytesChanged: bool

    /// Names coarse reporting stages that cannot influence update ordering.
    type Progress =
        | Preparing
        | Waiting
        | Applying
        | Verifying
        | Committing
        | Finalizing

    /// Holds one classified reason for a rejected or incomplete update.
    type Failure = private Failure of string

    /// Names every accepted Working Directory Update terminal outcome.
    type Outcome =
        | Unchanged of Receipt
        | Updated of Receipt
        | Rejected of Failure
        | UpdateIncomplete of Failure
        | FinalizationIncomplete of Receipt * Failure

    /// Encodes one canonical text field without delimiter ambiguity.
    let private canonicalField name (value: string) = $"{name}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}\n"

    /// Renders a Guid in stable lower-case compact form.
    let private guidText (value: Guid) = value.ToString("N")

    /// Requires an identifier to be present before it enters an update tuple.
    let private requireId name (value: Guid) = if value = Guid.Empty then Error $"{name} must not be empty." else Ok value

    /// Requires a full lower-case SHA-256 value.
    let private requireSha256 (value: Sha256Hash) =
        if Grace.Shared.Constants.Sha256FullHashRegex.IsMatch(string value) then
            Ok value
        else
            Error "SHA-256 must be a complete lowercase hexadecimal hash."

    /// Requires a full lower-case BLAKE3 value.
    let private requireBlake3 (value: Blake3Hash) =
        if Grace.Shared.Constants.Blake3FullHashRegex.IsMatch(string value) then
            Ok value
        else
            Error "BLAKE3 must be a complete lowercase hexadecimal hash."

    /// Requires an exact opaque server cursor without trimming or rewriting it.
    let private requireCursor name (value: string) =
        if
            String.IsNullOrWhiteSpace(value)
            || value <> value.Trim()
        then
            Error $"{name} must be present and canonical."
        else
            Ok value

    /// Computes the canonical SHA-256 operation identity from the accepted tuple encoding.
    let private operationValue (canonical: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes(canonical))
        |> Convert.ToHexString
        |> fun hash -> $"sha256:{hash.ToLowerInvariant()}"

    /// Identifies DOS device basenames that Windows reserves even when an extension follows.
    let private isReservedWindowsDeviceName (segment: string) =
        let extensionIndex = segment.IndexOf('.')

        let baseName =
            (if extensionIndex < 0 then segment else segment.Substring(0, extensionIndex))
            |> fun value -> value.ToUpperInvariant()

        baseName = "CON"
        || baseName = "PRN"
        || baseName = "AUX"
        || baseName = "NUL"
        || (baseName.Length = 4
            && (baseName.StartsWith("COM", StringComparison.Ordinal)
                || baseName.StartsWith("LPT", StringComparison.Ordinal))
            && ((baseName[3] >= '1' && baseName[3] <= '9')
                || baseName[3] = '¹'
                || baseName[3] = '²'
                || baseName[3] = '³'))

    /// Validates a relative path and converts Windows separators to the canonical slash form.
    let private normalizeRelativePath (path: RelativePath) =
        let value = string path

        if String.IsNullOrWhiteSpace(value) then
            Error "Prepared-content paths must not be empty."
        elif value.IndexOf(char 0) >= 0 then
            Error "Prepared-content paths must not contain NUL."
        elif
            Path.IsPathRooted(value)
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("\\", StringComparison.Ordinal)
        then
            Error "Prepared-content paths must be relative."
        else
            let normalized = normalizeFilePath value
            let segments = normalized.Split('/', StringSplitOptions.None)

            let invalid =
                segments
                |> Array.tryPick (fun segment ->
                    if String.IsNullOrWhiteSpace(segment) then
                        Some "Prepared-content paths must not contain empty segments."
                    elif segment = "." || segment = ".." then
                        Some "Prepared-content paths must not contain traversal segments."
                    elif segment
                         |> Seq.exists (fun character -> int character < 32) then
                        Some "Prepared-content paths must not contain Windows control characters."
                    elif
                        segment.IndexOfAny([| '<'; '>'; ':'; '"'; '|'; '?'; '*' |])
                        >= 0
                    then
                        Some "Prepared-content paths must be representable on Windows."
                    elif segment.EndsWith('.') || segment.EndsWith(' ') then
                        Some "Prepared-content paths must not end a Windows segment with a dot or space."
                    elif isReservedWindowsDeviceName segment then
                        Some "Prepared-content paths must not use a reserved Windows device name."
                    else
                        None)

            match invalid with
            | Some error -> Error error
            | None -> Ok(RelativePath normalized)

    /// Produces the canonical Windows comparison key used for manifest, reader, and verified-byte lookup.
    let private windowsPathKey (path: RelativePath) =
        string path
        |> fun value -> value.ToUpperInvariant()

    /// Supplies construction and access functions for complete selected targets.
    module Target =
        /// Creates a selected target only when every identifier and dual hash is complete and canonical.
        let create repositoryId branchId rootDirectoryVersionId sha256Hash blake3Hash =
            match requireId "RepositoryId" repositoryId,
                  requireId "BranchId" branchId,
                  requireId "RootDirectoryVersionId" rootDirectoryVersionId,
                  requireSha256 sha256Hash,
                  requireBlake3 blake3Hash
                with
            | Ok repositoryId, Ok branchId, Ok rootDirectoryVersionId, Ok sha256Hash, Ok blake3Hash ->
                let canonical =
                    "grace.working-directory-update.target.v1\n"
                    + canonicalField "repository" (guidText repositoryId)
                    + canonicalField "branch" (guidText branchId)
                    + canonicalField "root-directory-version" (guidText rootDirectoryVersionId)
                    + canonicalField "sha256" (string sha256Hash)
                    + canonicalField "blake3" (string blake3Hash)

                Ok(Target(repositoryId, branchId, rootDirectoryVersionId, sha256Hash, blake3Hash, canonical))
            | Error error, _, _, _, _ -> Error error
            | _, Error error, _, _, _ -> Error error
            | _, _, Error error, _, _ -> Error error
            | _, _, _, Error error, _ -> Error error
            | _, _, _, _, Error error -> Error error

        /// Returns the repository selected by this target.
        let repositoryId (Target (repositoryId, _, _, _, _, _)) = repositoryId

        /// Returns the branch selected by this target.
        let branchId (Target (_, branchId, _, _, _, _)) = branchId

        /// Returns the root DirectoryVersion selected by this target.
        let rootDirectoryVersionId (Target (_, _, rootDirectoryVersionId, _, _, _)) = rootDirectoryVersionId

        /// Returns the complete SHA-256 root selected by this target.
        let sha256Hash (Target (_, _, _, sha256Hash, _, _)) = sha256Hash

        /// Returns the complete BLAKE3 root selected by this target.
        let blake3Hash (Target (_, _, _, _, blake3Hash, _)) = blake3Hash

        /// Returns the canonical target encoding included in caller operation tuples.
        let canonical (Target (_, _, _, _, _, canonical)) = canonical

    /// Supplies construction and access functions for local-root scopes.
    module LocalRootScope =
        /// Builds a stable scope from an absolute local root without opening or mutating the filesystem.
        let create (localRoot: string) =
            if
                String.IsNullOrWhiteSpace(localRoot)
                || not (Path.IsPathFullyQualified(localRoot))
            then
                Error "Local root scope requires a full absolute path."
            else
                let fullPath = Path.GetFullPath(localRoot)
                let root = Path.GetPathRoot(fullPath)

                let trimmed =
                    if String.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) then
                        fullPath
                    else
                        fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

                let canonicalPath =
                    normalizeFilePath trimmed
                    |> fun path -> path.ToLowerInvariant()

                SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath))
                |> Convert.ToHexString
                |> fun hash -> hash.ToLowerInvariant()
                |> LocalRootScope
                |> Ok

        /// Returns the canonical path-derived scope value.
        let value (LocalRootScope scope) = scope

    /// Supplies construction and access functions for deterministic operation identities.
    module Operation =
        /// Constructs an operation from a complete caller tuple after repository and branch validation.
        let private create callerKind target repositoryId branchId canonical =
            match requireId "RepositoryId" repositoryId, requireId "BranchId" branchId with
            | Ok repositoryId, Ok branchId -> Ok(Operation(callerKind, target, repositoryId, branchId, operationValue canonical))
            | Error error, _ -> Error error
            | _, Error error -> Error error

        /// Creates the Watch identity from its exact opaque replay cursor.
        let watchReplay repositoryId branchId eventCursor =
            match requireCursor "Watch event cursor" eventCursor with
            | Error error -> Error error
            | Ok eventCursor ->
                let canonical =
                    "grace.working-directory-update.operation.v1\n"
                    + canonicalField "caller" "watch"
                    + canonicalField "repository" (guidText repositoryId)
                    + canonicalField "branch" (guidText branchId)
                    + canonicalField "event-cursor" eventCursor

                create Watch None repositoryId branchId canonical

        /// Creates the Branch identity from an exact transition Reference or an exact hash-selected target root.
        let branchSwitchWithSelection previousBranchId selection target =
            match requireId "PreviousBranchId" previousBranchId with
            | Ok previousBranchId ->
                let repositoryId = Target.repositoryId target
                let branchId = Target.branchId target

                let selectionCanonical =
                    match selection with
                    | Reference selectedReferenceId ->
                        match requireId "SelectedReferenceId" selectedReferenceId with
                        | Ok selectedReferenceId ->
                            Ok(
                                canonicalField "branch-selection" "reference"
                                + canonicalField "selected-reference" (guidText selectedReferenceId)
                            )
                        | Error error -> Error error
                    | DirectoryVersion when branchId = previousBranchId -> Ok(canonicalField "branch-selection" "directory-version")
                    | DirectoryVersion -> Error "DirectoryVersion Branch selection must retain the current Branch."

                match selectionCanonical with
                | Error error -> Error error
                | Ok selectionCanonical ->
                    let canonical =
                        "grace.working-directory-update.operation.v1\n"
                        + canonicalField "caller" "branch"
                        + canonicalField "repository" (guidText repositoryId)
                        + canonicalField "previous-branch" (guidText previousBranchId)
                        + canonicalField "selected-branch" (guidText branchId)
                        + selectionCanonical
                        + canonicalField "target" (Target.canonical target)

                    create Branch (Some target) repositoryId branchId canonical
            | Error error -> Error error

        /// Retains the existing private Reference-selected construction shorthand while callers migrate to typed selection.
        let branchSwitch previousBranchId selectedReferenceId target = branchSwitchWithSelection previousBranchId (Reference selectedReferenceId) target

        /// Creates the Connect identity from target, initial cursor, and local-root scope.
        let connectBootstrap target initialCursor localRootScope =
            match requireCursor "Connect initial cursor" initialCursor with
            | Error error -> Error error
            | Ok initialCursor ->
                let repositoryId = Target.repositoryId target
                let branchId = Target.branchId target

                let canonical =
                    "grace.working-directory-update.operation.v1\n"
                    + canonicalField "caller" "connect"
                    + canonicalField "repository" (guidText repositoryId)
                    + canonicalField "selected-branch" (guidText branchId)
                    + canonicalField "target" (Target.canonical target)
                    + canonicalField "initial-cursor" initialCursor
                    + canonicalField "local-root-scope" (LocalRootScope.value localRootScope)

                create Connect (Some target) repositoryId branchId canonical

        /// Returns the operation identity independently of diagnostic correlation.
        let value (Operation (_, _, _, _, value)) = value

        /// Returns the caller kind that owns completion progress.
        let callerKind (Operation (callerKind, _, _, _, _)) = callerKind

        /// Verifies Watch scope or the complete Branch and Connect target embedded by the operation.
        let matchesTarget target (Operation (callerKind, operationTarget, repositoryId, branchId, _)) =
            match callerKind, operationTarget with
            | Watch, None ->
                repositoryId = Target.repositoryId target
                && branchId = Target.branchId target
            | (Branch
              | Connect),
              Some operationTarget -> operationTarget = target
            | _ -> false

    /// Supplies construction and access functions for marker attempt tokens.
    module AttemptToken =
        /// Creates a fresh random marker token without changing logical retry identity.
        let create () = AttemptToken(Guid.NewGuid().ToString("N"))

        /// Returns the marker attempt token value.
        let value (AttemptToken token) = token

    /// Supplies structural validation and access functions for exact prepared manifests.
    module PreparedManifest =
        /// Creates an immutable manifest after validating paths, hashes, collisions, and file-directory conflicts.
        let create (entries: PreparedManifestEntry seq) =
            if isNull (box entries) then
                Error "Prepared-content manifest entries must not be null."
            else
                let paths = Dictionary<string, PreparedManifestEntry>(StringComparer.Ordinal)
                let canonicalEntries = ResizeArray<PreparedManifestEntry>()
                let files = ResizeArray<PreparedFile>()
                let mutable error = None

                use enumerator = entries.GetEnumerator()

                while enumerator.MoveNext() && Option.isNone error do
                    match enumerator.Current with
                    | Directory path ->
                        match normalizeRelativePath path with
                        | Error pathError -> error <- Some pathError
                        | Ok normalizedPath ->
                            let key = windowsPathKey normalizedPath

                            if paths.ContainsKey(key) then
                                error <- Some $"Prepared-content manifest contains a duplicate or case-colliding path '{key}'."
                            else
                                let entry = Directory normalizedPath
                                paths[key] <- entry
                                canonicalEntries.Add(entry)
                    | File (path, sha256Hash, blake3Hash) ->
                        match normalizeRelativePath path, requireSha256 sha256Hash, requireBlake3 blake3Hash with
                        | Error pathError, _, _ -> error <- Some pathError
                        | _, Error hashError, _ -> error <- Some hashError
                        | _, _, Error hashError -> error <- Some hashError
                        | Ok normalizedPath, Ok sha256Hash, Ok blake3Hash ->
                            let key = windowsPathKey normalizedPath

                            if paths.ContainsKey(key) then
                                error <- Some $"Prepared-content manifest contains a duplicate or case-colliding path '{key}'."
                            else
                                let entry = File(normalizedPath, sha256Hash, blake3Hash)
                                paths[key] <- entry
                                canonicalEntries.Add(entry)
                                files.Add({ RelativePath = normalizedPath; Sha256Hash = sha256Hash; Blake3Hash = blake3Hash })

                match error with
                | Some error -> Error error
                | None ->
                    let keys = paths.Keys |> Seq.toArray

                    let conflict =
                        keys
                        |> Array.tryPick (fun path ->
                            match paths[path] with
                            | File _ when
                                keys
                                |> Array.exists (fun candidate -> candidate.StartsWith(path + "/", StringComparison.Ordinal))
                                ->
                                Some $"Prepared-content file '{path}' conflicts with a contained path."
                            | _ -> None)

                    match conflict with
                    | Some error -> Error error
                    | None -> Ok(PreparedManifest(canonicalEntries.ToArray(), files.ToArray()))

        /// Returns the canonical file paths that must have readable bytes.
        let filePaths (PreparedManifest (_, files)) = files |> Seq.map (fun file -> file.RelativePath)

        /// Returns the immutable canonical manifest entries.
        let entries (PreparedManifest (entries, _)) = entries :> seq<PreparedManifestEntry>

    /// Supplies actual-byte validation and lifetime functions for prepared content.
    module PreparedContent =
        /// Validates exact reader coverage and actual dual-hash bytes before a future lease can be acquired.
        let create manifest (reader: IPreparedContentReader) (cancellationToken: CancellationToken) =
            task {
                if isNull (box reader) then
                    return Error "Prepared-content reader must not be null."
                else
                    try
                        let files = PreparedManifest.filePaths manifest |> Seq.toArray
                        let declaredHashes = Dictionary<string, Sha256Hash * Blake3Hash>(StringComparer.Ordinal)

                        PreparedManifest.entries manifest
                        |> Seq.iter (function
                            | File (path, sha256Hash, blake3Hash) -> declaredHashes[windowsPathKey path] <- (sha256Hash, blake3Hash)
                            | Directory _ -> ())

                        let readerPaths = reader.FilePaths |> Seq.toArray
                        let readerKeys = HashSet<string>(StringComparer.Ordinal)
                        let mutable error = None
                        let mutable readerIndex = 0

                        while readerIndex < readerPaths.Length
                              && Option.isNone error do
                            match normalizeRelativePath (RelativePath readerPaths[readerIndex]) with
                            | Error pathError -> error <- Some pathError
                            | Ok path when not (readerKeys.Add(windowsPathKey path)) ->
                                error <- Some $"Prepared-content reader contains a duplicate or case-colliding path '{path}'."
                            | Ok _ -> ()

                            readerIndex <- readerIndex + 1

                        let expected = HashSet<string>(files |> Seq.map windowsPathKey, StringComparer.Ordinal)

                        if
                            Option.isNone error
                            && not (readerKeys.SetEquals(expected))
                        then
                            error <- Some "Prepared-content reader paths do not exactly match the declared manifest files."

                        match error with
                        | Some error -> return Error error
                        | None ->
                            let bytesByPath = Dictionary<string, byte array>(StringComparer.Ordinal)
                            let mutable fileIndex = 0
                            let mutable byteError = None

                            while fileIndex < files.Length
                                  && Option.isNone byteError do
                                cancellationToken.ThrowIfCancellationRequested()
                                let path = files[fileIndex]

                                try
                                    let! stream = reader.OpenReadAsync(path, cancellationToken)

                                    if isNull stream then
                                        byteError <- Some $"Prepared-content reader returned no stream for '{path}'."
                                    else
                                        use stream = stream
                                        use copy = new MemoryStream()
                                        do! stream.CopyToAsync(copy, cancellationToken)
                                        let bytes = copy.ToArray()
                                        use hashStream = new MemoryStream(bytes, writable = false)
                                        let! sha256Hash, blake3Hash = computeHashesForFile hashStream path
                                        let expectedSha256Hash, expectedBlake3Hash = declaredHashes[windowsPathKey path]

                                        if sha256Hash <> expectedSha256Hash then
                                            byteError <- Some $"Prepared-content bytes do not match declared SHA-256 for '{path}'."
                                        elif blake3Hash <> expectedBlake3Hash then
                                            byteError <- Some $"Prepared-content bytes do not match declared BLAKE3 for '{path}'."
                                        else
                                            bytesByPath[windowsPathKey path] <- bytes
                                with
                                | ex -> byteError <- Some $"Prepared-content reader failed for '{path}': {ex.Message}"

                                fileIndex <- fileIndex + 1

                            match byteError with
                            | Some error -> return Error error
                            | None -> return Ok(PreparedContent(manifest, bytesByPath, ref false))
                    finally
                        reader.Dispose()
            }

        /// Opens a read-only stream over verified bytes for one declared file.
        let openRead (PreparedContent (_, bytesByPath, disposed)) path =
            match normalizeRelativePath path with
            | Error error -> Error error
            | Ok path when !disposed -> Error "Prepared-content has already been disposed."
            | Ok path ->
                match bytesByPath.TryGetValue(windowsPathKey path) with
                | true, bytes -> Ok(new MemoryStream(bytes, writable = false) :> Stream)
                | false, _ -> Error $"Prepared-content has no declared file '{path}'."

        /// Clears verified bytes when the owning update operation reaches a terminal path.
        let dispose (PreparedContent (_, bytesByPath, disposed)) =
            if not !disposed then
                bytesByPath.Values
                |> Seq.iter (fun bytes -> Array.Clear(bytes, 0, bytes.Length))

                bytesByPath.Clear()
                disposed := true

    /// Supplies construction and access functions for private update requests.
    module Request =
        /// Creates a request only when its deterministic operation belongs to the selected target scope.
        let create target operation preparedContent correlationId =
            if isNull (box preparedContent) then
                Error "Working Directory Update requires prepared content."
            elif Operation.matchesTarget target operation then
                Ok(Request(target, operation, preparedContent, correlationId))
            else
                Error "Working Directory Update operation does not match the selected target."

        /// Returns the logical operation independently of diagnostic correlation.
        let operation (Request (_, operation, _, _)) = operation

        /// Returns the exact selected target admitted by this request.
        let target (Request (target, _, _, _)) = target

        /// Returns the dual-hash-verified prepared bytes owned by this request.
        let preparedContent (Request (_, _, preparedContent, _)) = preparedContent

        /// Returns the diagnostic correlation without making it part of replay identity.
        let correlationId (Request (_, _, _, correlationId)) = correlationId

    /// Supplies construction and access functions for completed update receipts.
    module Receipt =
        /// Creates a receipt only when the operation belongs to the selected target scope.
        let create target operation bytesChanged =
            if Operation.matchesTarget target operation then
                Ok(Receipt(target, operation, bytesChanged))
            else
                Error "Working Directory Update receipt operation does not match the selected target."

        /// Returns whether the receipt records a completed byte mutation.
        let bytesChanged (Receipt (_, _, bytesChanged)) = bytesChanged

    /// Supplies construction functions for classified private update failures.
    module Failure =
        /// Creates a failure only when it supplies a non-empty reason for a terminal outcome.
        let create reason =
            if String.IsNullOrWhiteSpace(reason) then
                Error "Working Directory Update failure reason must not be empty."
            else
                Ok(Failure reason)

        /// Returns the classified terminal reason for truthful CLI projection.
        let reason (Failure reason) = reason
