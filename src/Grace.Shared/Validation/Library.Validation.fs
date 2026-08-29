namespace Grace.Shared.Validation

open Grace.Shared.Parameters.Library
open Grace.Types.Library
open NodaTime
open System
open System.Globalization
open System.IO
open System.Text
open System.Text.RegularExpressions

/// Implements the portable path and exact change-shape rules for Library Content.
module Library =

    [<Literal>]
    let MaximumRootCount = 128

    [<Literal>]
    let MaximumSegmentBytes = 255

    [<Literal>]
    let MaximumPathBytes = 1024

    [<Literal>]
    let MaximumOpaqueTokenBytes = 2048

    [<Literal>]
    let MinimumPageSize = 1

    [<Literal>]
    let MaximumPageSize = 2000

    let private invalidCharacters =
        set [ '<'
              '>'
              ':'
              '"'
              '|'
              '?'
              '*' ]

    /// Returns true when a segment names a portable Windows device name.
    let private isDeviceName (segment: string) =
        let stem = segment.Split('.')[0]

        Regex.IsMatch(
            stem,
            "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",
            RegexOptions.IgnoreCase
            ||| RegexOptions.CultureInvariant
        )

    /// Normalizes one repository-relative path and rejects every unsupported Product V1 input.
    let normalizeRepositoryRelativePath (path: string) =
        if String.IsNullOrWhiteSpace path then
            Error "A non-empty repository-relative path is required."
        else
            let normalized =
                path
                    .Normalize(NormalizationForm.FormC)
                    .Replace('\\', '/')

            if
                normalized.StartsWith('/')
                || normalized.EndsWith('/')
                || Path.IsPathRooted normalized
                || Regex.IsMatch(normalized, "^[A-Za-z]:")
                || normalized.Contains("://", StringComparison.Ordinal)
            then
                Error "Absolute, rooted, URI, drive, and trailing-slash paths are unsupported."
            elif Encoding.UTF8.GetByteCount normalized > MaximumPathBytes then
                Error $"A normalized path must not exceed {MaximumPathBytes} UTF-8 bytes."
            else
                let segments = normalized.Split('/')

                let invalidSegment =
                    segments
                    |> Array.tryFind (fun segment ->
                        String.IsNullOrEmpty segment
                        || segment = "."
                        || segment = ".."
                        || segment.EndsWith(' ')
                        || segment.EndsWith('.')
                        || Encoding.UTF8.GetByteCount segment > MaximumSegmentBytes
                        || segment
                           |> Seq.exists (fun value ->
                               Char.IsControl value
                               || value = '\u0000'
                               || invalidCharacters.Contains value)
                        || isDeviceName segment)

                match invalidSegment with
                | Some _ -> Error "The path contains an unsupported portable segment."
                | None when String.Equals(segments[0], ".grace", StringComparison.OrdinalIgnoreCase) ->
                    Error "The .grace directory is reserved for Grace internal state."
                | None -> Ok normalized

    /// Normalizes one namespace segment without accepting a multi-segment path.
    let normalizeName (name: string) =
        normalizeRepositoryRelativePath name
        |> Result.bind (fun normalized ->
            if normalized.Contains('/') then
                Error "A Library name must contain exactly one segment."
            else
                Ok normalized)

    /// Applies case-insensitive portable equality to two normalized paths.
    let pathsEqual left right = String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

    /// Returns true when either root owns the other root's exact path or descendants.
    let librariesOverlap left right =
        pathsEqual left right
        || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase)
        || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase)

    /// Returns true when one normalized repository-relative path belongs to the exact root policy.
    let configurationOwnsPath (configuration: LibraryCatalogDto) (path: string) =
        let normalizedPath = path.Replace('\\', '/').Trim('/')

        configuration.Libraries
        |> Array.exists (fun root ->
            pathsEqual normalizedPath root
            || normalizedPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))

    /// Normalizes and sorts one complete root set while enforcing uniqueness, overlap, and count bounds.
    let normalizeLibraries (libraries: string array) =
        if isNull libraries then
            Error "Libraries are required."
        elif libraries.Length > MaximumRootCount then
            Error $"A repository may configure at most {MaximumRootCount} Libraries."
        else
            libraries
            |> Array.fold
                (fun state root ->
                    state
                    |> Result.bind (fun normalizedLibraries ->
                        normalizeRepositoryRelativePath root
                        |> Result.bind (fun normalizedRoot ->
                            if
                                normalizedLibraries
                                |> Array.exists (librariesOverlap normalizedRoot)
                            then
                                Error "Library libraries must be unique and non-overlapping."
                            else
                                Ok(Array.append normalizedLibraries [| normalizedRoot |]))))
                (Ok Array.empty)
            |> Result.map (Array.sortWith (fun left right -> StringComparer.OrdinalIgnoreCase.Compare(left, right)))

    /// Applies one exact-version root add after the caller has proven outgoing-system emptiness.
    let addLibrary expectedVersion newVersion libraryPath createdAt createdBy (current: LibraryCatalogDto) =
        if expectedVersion <> current.Version then
            Error OutcomeKind.StalePolicy
        elif current.Libraries.Length >= MaximumRootCount then
            Error CatalogRejectionReason.LibraryLimitExceeded
        else
            normalizeRepositoryRelativePath libraryPath
            |> Result.mapError (fun _ -> CatalogRejectionReason.UnsupportedPath)
            |> Result.bind (fun normalizedRoot ->
                if
                    current.Libraries
                    |> Array.exists (librariesOverlap normalizedRoot)
                then
                    Error CatalogRejectionReason.LibraryOverlap
                else
                    normalizeLibraries (Array.append current.Libraries [| normalizedRoot |])
                    |> Result.mapError (fun _ -> CatalogRejectionReason.LibraryOverlap)
                    |> Result.map (fun libraries ->
                        {
                            RepositoryId = current.RepositoryId
                            Version = newVersion
                            Libraries = libraries
                            CreatedAt = createdAt
                            CreatedBy = createdBy
                            PreviousVersion = Some current.Version
                        }))

    /// Applies one exact-version root removal after the caller has proven the Library namespace empty.
    let removeLibrary expectedVersion newVersion libraryPath createdAt createdBy (current: LibraryCatalogDto) =
        if expectedVersion <> current.Version then
            Error OutcomeKind.StalePolicy
        else
            normalizeRepositoryRelativePath libraryPath
            |> Result.mapError (fun _ -> CatalogRejectionReason.UnsupportedPath)
            |> Result.bind (fun normalizedRoot ->
                let retained =
                    current.Libraries
                    |> Array.filter (fun configured -> not (pathsEqual configured normalizedRoot))

                if retained.Length = current.Libraries.Length then
                    Error CatalogRejectionReason.UnsupportedPath
                else
                    Ok
                        {
                            RepositoryId = current.RepositoryId
                            Version = newVersion
                            Libraries = retained
                            CreatedAt = createdAt
                            CreatedBy = createdBy
                            PreviousVersion = Some current.Version
                        })

    /// Checks the exact lowercase 64-character hexadecimal hash contract.
    let isLowercaseHash (value: string) =
        not (String.IsNullOrEmpty value)
        && value.Length = 64
        && value
           |> Seq.forall (fun character ->
               Char.IsAsciiHexDigit character
               && not (Char.IsUpper character))

    /// Checks public opaque-token size without parsing its protected contents.
    let opaqueTokenIsValid (value: string) =
        not (String.IsNullOrEmpty value)
        && Encoding.UTF8.GetByteCount value
           <= MaximumOpaqueTokenBytes

    /// Checks a public bootstrap or change page page size.
    let pageSizeIsValid pageSize =
        pageSize >= MinimumPageSize
        && pageSize <= MaximumPageSize

    /// Validates the exact allowed and forbidden field combination for one change kind.
    let validateChangeShape (parameters: SubmitLibraryChangeParameters) =
        let hasItemId = parameters.ItemId.HasValue
        let hasNamespace = parameters.NamespacePrecondition.IsSome
        let hasContent = parameters.ContentPrecondition.IsSome
        let hasSlot = parameters.CreationSlotExpectation.IsSome
        let hasParent = parameters.DestinationParent.IsSome
        let hasName = not (isNull parameters.DestinationName)
        let hasPrepared = parameters.PreparedContentId.HasValue

        let valid =
            match parameters.ChangeKind with
            | ChangeKind.CreateFile ->
                not hasItemId
                && not hasNamespace
                && not hasContent
                && hasSlot
                && not hasParent
                && not hasName
                && hasPrepared
            | ChangeKind.CreateDirectory ->
                not hasItemId
                && not hasNamespace
                && not hasContent
                && hasSlot
                && not hasParent
                && not hasName
                && not hasPrepared
            | ChangeKind.UpdateContent ->
                hasItemId
                && not hasNamespace
                && hasContent
                && not hasSlot
                && not hasParent
                && not hasName
                && hasPrepared
            | ChangeKind.Rename ->
                hasItemId
                && hasNamespace
                && not hasContent
                && hasSlot
                && not hasParent
                && hasName
                && not hasPrepared
            | ChangeKind.Move ->
                hasItemId
                && hasNamespace
                && not hasContent
                && hasSlot
                && hasParent
                && not hasName
                && not hasPrepared
            | ChangeKind.Delete when parameters.ItemKind = ItemKind.File ->
                hasItemId
                && hasNamespace
                && hasContent
                && not hasSlot
                && not hasParent
                && not hasName
                && not hasPrepared
            | ChangeKind.Delete when parameters.ItemKind = ItemKind.Directory ->
                hasItemId
                && hasNamespace
                && not hasContent
                && not hasSlot
                && not hasParent
                && not hasName
                && not hasPrepared
            | _ -> false

        if valid then
            Ok()
        else
            Error "The change fields do not match the selected change and item kinds."
