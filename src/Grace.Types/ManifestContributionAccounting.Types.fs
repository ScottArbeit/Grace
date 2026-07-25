namespace Grace.Types

open Grace.Types.Common
open System
open System.Text

/// Contains exact relationship identities used to rebuild manifest contribution accounting.
module ManifestContributionAccounting =

    /// Identifies a Reference that currently retains one root DirectoryVersion.
    type ReferenceRootRelationship = { RepositoryId: RepositoryId; RootDirectoryVersionId: DirectoryVersionId; ReferenceId: ReferenceId }

    /// Identifies a parent DirectoryVersion that currently retains one direct child DirectoryVersion.
    type ParentChildRelationship = { RepositoryId: RepositoryId; ParentDirectoryVersionId: DirectoryVersionId; ChildDirectoryVersionId: DirectoryVersionId }

    /// Identifies a DirectoryVersion that currently retains one manifest in a StoragePool.
    type DirectoryVersionManifestRelationship =
        {
            RepositoryId: RepositoryId
            StoragePoolId: StoragePoolId
            ManifestAddress: ManifestAddress
            DirectoryVersionId: DirectoryVersionId
        }

    /// Represents one current relationship that can be rebuilt from ReferenceActor or DirectoryVersionActor state.
    type ExactRelationship =
        | ReferenceRoot of ReferenceRootRelationship
        | ParentChild of ParentChildRelationship
        | DirectoryVersionManifest of DirectoryVersionManifestRelationship

    /// Identifies one bounded relationship partition without exposing a storage-provider key type.
    type ExactRelationshipPartition =
        | IncomingDirectoryVersion of repositoryId: RepositoryId * targetDirectoryVersionId: DirectoryVersionId
        | Manifest of repositoryId: RepositoryId * storagePoolId: StoragePoolId * manifestAddress: ManifestAddress

    /// Carries the deterministic partition and item identities for one exact relationship.
    type ExactRelationshipKey = { PartitionKey: string; ItemId: string }

    /// Builds and parses canonical exact-relationship keys without coupling callers to a storage provider.
    module ExactRelationshipKey =

        /// Names partitions that hold current incoming relationships for one target DirectoryVersion.
        [<Literal>]
        let private IncomingPartitionPrefix = "incoming-directory-version"

        /// Names partitions that hold current DirectoryVersion relationships for one manifest.
        [<Literal>]
        let private ManifestPartitionPrefix = "manifest"

        /// Distinguishes Reference identities inside incoming DirectoryVersion partitions.
        [<Literal>]
        let private ReferenceRootItemPrefix = "reference-root"

        /// Distinguishes parent DirectoryVersion identities inside incoming DirectoryVersion partitions.
        [<Literal>]
        let private ParentChildItemPrefix = "parent-child"

        /// Distinguishes source DirectoryVersion identities inside manifest partitions.
        [<Literal>]
        let private DirectoryVersionManifestItemPrefix = "directory-version-manifest"

        /// Rejects malformed UTF-16 instead of replacing distinct identity text with the same UTF-8 bytes.
        let private strictUtf8 = UTF8Encoding(false, true)

        /// Encodes one well-formed variable-length component so delimiters remain data rather than key structure.
        let private tryEncodeComponent (value: string) =
            try
                Convert
                    .ToBase64String(strictUtf8.GetBytes value)
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_')
                |> Some
            with
            | :? EncoderFallbackException -> None

        /// Decodes one canonical variable-length key component.
        let private tryDecodeComponent (value: string) =
            try
                let normalized = value.Replace('-', '+').Replace('_', '/')

                let padded =
                    match normalized.Length % 4 with
                    | 0 -> normalized
                    | 2 -> normalized + "=="
                    | 3 -> normalized + "="
                    | _ -> String.Empty

                if String.IsNullOrEmpty padded then
                    None
                else
                    strictUtf8.GetString(Convert.FromBase64String padded)
                    |> Some
            with
            | :? FormatException
            | :? DecoderFallbackException -> None

        /// Parses a non-empty canonical GUID component.
        let private tryParseGuid value =
            match Guid.TryParseExact(value, "N") with
            | true, parsed when parsed <> Guid.Empty -> Some parsed
            | _ -> None

        /// Returns the partition dimensions used to enumerate incoming relationships for the supplied relationship.
        let partition relationship =
            match relationship with
            | ExactRelationship.ReferenceRoot relationship ->
                ExactRelationshipPartition.IncomingDirectoryVersion(relationship.RepositoryId, relationship.RootDirectoryVersionId)
            | ExactRelationship.ParentChild relationship ->
                ExactRelationshipPartition.IncomingDirectoryVersion(relationship.RepositoryId, relationship.ChildDirectoryVersionId)
            | ExactRelationship.DirectoryVersionManifest relationship ->
                ExactRelationshipPartition.Manifest(relationship.RepositoryId, relationship.StoragePoolId, relationship.ManifestAddress)

        /// Creates the canonical storage partition key shared by relationship writes and bounded enumeration.
        let createPartitionKey partition =
            match partition with
            | ExactRelationshipPartition.IncomingDirectoryVersion (repositoryId, targetDirectoryVersionId) ->
                if repositoryId = Guid.Empty then
                    Error "Incoming DirectoryVersion partition RepositoryId must not be empty."
                elif targetDirectoryVersionId = Guid.Empty then
                    Error "Incoming DirectoryVersion partition target DirectoryVersionId must not be empty."
                else
                    Ok $"{IncomingPartitionPrefix}:{repositoryId:N}:{targetDirectoryVersionId:N}"
            | ExactRelationshipPartition.Manifest (repositoryId, storagePoolId, manifestAddress) ->
                if repositoryId = Guid.Empty then
                    Error "Manifest partition RepositoryId must not be empty."
                elif String.IsNullOrWhiteSpace storagePoolId then
                    Error "Manifest partition StoragePoolId must not be empty."
                elif String.IsNullOrWhiteSpace manifestAddress then
                    Error "Manifest partition ManifestAddress must not be empty."
                else
                    match tryEncodeComponent storagePoolId, tryEncodeComponent manifestAddress with
                    | Some encodedStoragePoolId, Some encodedManifestAddress ->
                        Ok $"{ManifestPartitionPrefix}:{repositoryId:N}:{encodedStoragePoolId}:{encodedManifestAddress}"
                    | _ -> Error "Manifest partition StoragePoolId and ManifestAddress must contain well-formed UTF-16."

        /// Validates an exact relationship before deriving a persistent key.
        let private validate relationship =
            match relationship with
            | ExactRelationship.ReferenceRoot relationship ->
                if relationship.RepositoryId = Guid.Empty then
                    Error "Reference-root RepositoryId must not be empty."
                elif relationship.RootDirectoryVersionId = Guid.Empty then
                    Error "Reference-root RootDirectoryVersionId must not be empty."
                elif relationship.ReferenceId = Guid.Empty then
                    Error "Reference-root ReferenceId must not be empty."
                else
                    Ok()
            | ExactRelationship.ParentChild relationship ->
                if relationship.RepositoryId = Guid.Empty then
                    Error "Parent-child RepositoryId must not be empty."
                elif relationship.ParentDirectoryVersionId = Guid.Empty then
                    Error "Parent-child ParentDirectoryVersionId must not be empty."
                elif relationship.ChildDirectoryVersionId = Guid.Empty then
                    Error "Parent-child ChildDirectoryVersionId must not be empty."
                else
                    Ok()
            | ExactRelationship.DirectoryVersionManifest relationship ->
                if relationship.RepositoryId = Guid.Empty then
                    Error "DirectoryVersion-manifest RepositoryId must not be empty."
                elif String.IsNullOrWhiteSpace relationship.StoragePoolId then
                    Error "DirectoryVersion-manifest StoragePoolId must not be empty."
                elif String.IsNullOrWhiteSpace relationship.ManifestAddress then
                    Error "DirectoryVersion-manifest ManifestAddress must not be empty."
                elif relationship.DirectoryVersionId = Guid.Empty then
                    Error "DirectoryVersion-manifest DirectoryVersionId must not be empty."
                else
                    Ok()

        /// Creates the canonical key for one exact relationship.
        let create relationship =
            match validate relationship with
            | Error error -> Error error
            | Ok _ ->
                match createPartitionKey (partition relationship) with
                | Error error -> Error error
                | Ok partitionKey ->
                    match relationship with
                    | ExactRelationship.ReferenceRoot relationship ->
                        Ok { PartitionKey = partitionKey; ItemId = $"{ReferenceRootItemPrefix}:{relationship.ReferenceId:N}" }
                    | ExactRelationship.ParentChild relationship ->
                        Ok { PartitionKey = partitionKey; ItemId = $"{ParentChildItemPrefix}:{relationship.ParentDirectoryVersionId:N}" }
                    | ExactRelationship.DirectoryVersionManifest relationship ->
                        Ok { PartitionKey = partitionKey; ItemId = $"{DirectoryVersionManifestItemPrefix}:{relationship.DirectoryVersionId:N}" }

        /// Parses a canonical key back into its exact relationship identity.
        let tryParse key =
            let invalid () = Error "Exact relationship key is malformed or non-canonical."

            if isNull (box key)
               || String.IsNullOrWhiteSpace key.PartitionKey
               || String.IsNullOrWhiteSpace key.ItemId then
                invalid ()
            else
                let partitionParts = key.PartitionKey.Split(':')
                let itemParts = key.ItemId.Split(':')

                let parsed =
                    match partitionParts, itemParts with
                    | [| IncomingPartitionPrefix; repositoryId; targetDirectoryVersionId |], [| ReferenceRootItemPrefix; referenceId |] ->
                        match tryParseGuid repositoryId, tryParseGuid targetDirectoryVersionId, tryParseGuid referenceId with
                        | Some repositoryId, Some rootDirectoryVersionId, Some referenceId ->
                            Some(
                                ExactRelationship.ReferenceRoot
                                    { RepositoryId = repositoryId; RootDirectoryVersionId = rootDirectoryVersionId; ReferenceId = referenceId }
                            )
                        | _ -> None
                    | [| IncomingPartitionPrefix; repositoryId; targetDirectoryVersionId |], [| ParentChildItemPrefix; parentDirectoryVersionId |] ->
                        match tryParseGuid repositoryId, tryParseGuid targetDirectoryVersionId, tryParseGuid parentDirectoryVersionId with
                        | Some repositoryId, Some childDirectoryVersionId, Some parentDirectoryVersionId ->
                            Some(
                                ExactRelationship.ParentChild
                                    {
                                        RepositoryId = repositoryId
                                        ParentDirectoryVersionId = parentDirectoryVersionId
                                        ChildDirectoryVersionId = childDirectoryVersionId
                                    }
                            )
                        | _ -> None
                    | [| ManifestPartitionPrefix; repositoryId; storagePoolId; manifestAddress |], [| DirectoryVersionManifestItemPrefix; directoryVersionId |] ->
                        match tryParseGuid repositoryId, tryDecodeComponent storagePoolId, tryDecodeComponent manifestAddress, tryParseGuid directoryVersionId
                            with
                        | Some repositoryId, Some storagePoolId, Some manifestAddress, Some directoryVersionId ->
                            Some(
                                ExactRelationship.DirectoryVersionManifest
                                    {
                                        RepositoryId = repositoryId
                                        StoragePoolId = storagePoolId
                                        ManifestAddress = manifestAddress
                                        DirectoryVersionId = directoryVersionId
                                    }
                            )
                        | _ -> None
                    | _ -> None

                match parsed with
                | None -> invalid ()
                | Some relationship ->
                    match create relationship with
                    | Ok canonical when canonical = key -> Ok relationship
                    | _ -> invalid ()

    /// Carries an explicit finite maximum for one exact-relationship enumeration.
    type ExactRelationshipReadBound = private ExactRelationshipReadBound of int

    /// Validates and unwraps exact-relationship enumeration bounds.
    module ExactRelationshipReadBound =

        /// Caps one bounded diagnostic or convergence read to prevent an accidental repository-wide scan.
        [<Literal>]
        let Maximum = 5000

        /// Creates an explicit positive relationship read bound.
        let create maximumCount =
            if maximumCount <= 0 then
                Error "Exact relationship maximum count must be greater than zero."
            elif maximumCount > Maximum then
                Error $"Exact relationship maximum count must not exceed {Maximum}."
            else
                Ok(ExactRelationshipReadBound maximumCount)

        /// Returns the validated maximum relationship count and rejects a language-default null value.
        let value bound =
            if isNull (box bound) then
                invalidArg (nameof bound) "Exact relationship maximum count must be created explicitly."
            else
                let (ExactRelationshipReadBound maximumCount) = bound
                maximumCount

    /// Reports whether an idempotent exact-relationship write changed current membership.
    type ExactRelationshipWriteOutcome =
        | Changed
        | AlreadyConverged

    /// Reports the current presence of one exact relationship after a direct bounded verification.
    type ExactRelationshipPresence =
        | Present
        | Absent

    /// Carries one bounded page of exact relationships and an optional provider-neutral continuation token.
    type ExactRelationshipPage = { Relationships: ExactRelationship array; ContinuationToken: string option }
