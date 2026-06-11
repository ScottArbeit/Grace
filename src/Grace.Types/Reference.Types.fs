namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Reference =

    /// The state held in the database when creating a physical deletion reminder for a reference.
    [<GenerateSerializer>]
    type PhysicalDeletionReminderState =
        {
            RepositoryId: RepositoryId
            BranchId: BranchId
            DirectoryVersionId: DirectoryVersionId
            Sha256Hash: Sha256Hash
            Blake3Hash: Blake3Hash
            DeleteReason: DeleteReason
            CorrelationId: CorrelationId
        }

    [<KnownType("GetKnownTypes")>]
    type ReferenceCommand =
        | Create of
            ReferenceId: ReferenceId *
            OwnerId: OwnerId *
            OrganizationId: OrganizationId *
            RepositoryId: RepositoryId *
            BranchId: BranchId *
            DirectoryId: DirectoryVersionId *
            Sha256Hash: Sha256Hash *
            Blake3Hash: Blake3Hash *
            ReferenceType: ReferenceType *
            ReferenceText: ReferenceText *
            Links: ReferenceLinkType seq
        | AddLink of link: ReferenceLinkType
        | RemoveLink of link: ReferenceLinkType
        | DeleteLogical of force: bool * DeleteReason: DeleteReason
        | DeletePhysical
        | Undelete

        static member GetKnownTypes() = GetKnownTypes<ReferenceCommand>()

    /// Defines the events for the Reference actor.
    [<KnownType("GetKnownTypes")>]
    type ReferenceEventType =
        | Created of
            ReferenceId: ReferenceId *
            OwnerId: OwnerId *
            OrganizationId: OrganizationId *
            RepositoryId: RepositoryId *
            BranchId: BranchId *
            DirectoryId: DirectoryVersionId *
            Sha256Hash: Sha256Hash *
            Blake3Hash: Blake3Hash *
            ReferenceType: ReferenceType *
            ReferenceText: ReferenceText *
            Links: ReferenceLinkType seq
        | LinkAdded of link: ReferenceLinkType
        | LinkRemoved of link: ReferenceLinkType
        | LogicalDeleted of force: bool * DeleteReason: DeleteReason
        | PhysicalDeleted
        | Undeleted

        static member GetKnownTypes() = GetKnownTypes<ReferenceEventType>()

    /// Record that holds the event type and metadata for a Reference event.
    type ReferenceEvent =
        {
            /// The ReferenceEventType case that describes the event.
            Event: ReferenceEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }

    /// The ReferenceDto is a data transfer object that represents a reference in the system.
    type ReferenceDto =
        {
            Class: string
            ReferenceId: ReferenceId
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            BranchId: BranchId
            DirectoryId: DirectoryVersionId
            Sha256Hash: Sha256Hash
            Blake3Hash: Blake3Hash
            ReferenceType: ReferenceType
            ReferenceText: ReferenceText
            Links: ReferenceLinkType seq
            CreatedBy: string option
            CreatedAt: Instant
            UpdatedAt: Instant option
            DeletedAt: Instant option
            DeleteReason: DeleteReason
        }

        static member Default =
            {
                Class = nameof ReferenceDto
                ReferenceId = ReferenceId.Empty
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                BranchId = BranchId.Empty
                DirectoryId = DirectoryVersionId.Empty
                Sha256Hash = Sha256Hash String.Empty
                Blake3Hash = Blake3Hash String.Empty
                ReferenceType = Save
                ReferenceText = ReferenceText String.Empty
                Links = Seq.empty
                CreatedBy = None
                CreatedAt = Constants.DefaultTimestamp
                UpdatedAt = None
                DeletedAt = None
                DeleteReason = String.Empty
            }

        static member TryGetLegacyRootDirectoryHashRepair directoryId sha256Hash blake3Hash (directoryVersion: DirectoryVersion) =
            let rootRelativePath = directoryVersion.RelativePath

            let isRootDirectoryRelativePath =
                rootRelativePath = Constants.RootDirectoryPath
                || rootRelativePath = RelativePath "/"

            let referenceSha256Hash = string sha256Hash
            let rootSha256Hash = string directoryVersion.Sha256Hash

            let referenceSha256MatchesRoot =
                not (String.IsNullOrWhiteSpace referenceSha256Hash)
                && rootSha256Hash.StartsWith(referenceSha256Hash, StringComparison.OrdinalIgnoreCase)

            if
                String.IsNullOrWhiteSpace(string blake3Hash)
                && directoryVersion.DirectoryVersionId = directoryId
                && isRootDirectoryRelativePath
                && referenceSha256MatchesRoot
                && not (String.IsNullOrWhiteSpace(string directoryVersion.Blake3Hash))
            then
                Some(directoryVersion.Sha256Hash, directoryVersion.Blake3Hash)
            else
                None

        static member HydrateLegacyRootDirectoryHash directoryVersion referenceDto =
            match ReferenceDto.TryGetLegacyRootDirectoryHashRepair referenceDto.DirectoryId referenceDto.Sha256Hash referenceDto.Blake3Hash directoryVersion
                with
            | Some (fullSha256Hash, blake3Hash) -> { referenceDto with Sha256Hash = fullSha256Hash; Blake3Hash = blake3Hash }, true
            | None -> referenceDto, false

        /// Updates the ReferenceDto based on the ReferenceEvent.
        static member UpdateDto referenceEvent currentReferenceDto =
            let newReferenceDto =
                match referenceEvent.Event with
                | Created (referenceId,
                           ownerId,
                           organizationId,
                           repositoryId,
                           branchId,
                           directoryId,
                           sha256Hash,
                           blake3Hash,
                           referenceType,
                           referenceText,
                           links) ->
                    { currentReferenceDto with
                        ReferenceId = referenceId
                        OwnerId = ownerId
                        OrganizationId = organizationId
                        RepositoryId = repositoryId
                        BranchId = branchId
                        DirectoryId = directoryId
                        Sha256Hash = sha256Hash
                        Blake3Hash = blake3Hash
                        ReferenceType = referenceType
                        ReferenceText = referenceText
                        Links = links
                        CreatedBy =
                            if String.IsNullOrWhiteSpace referenceEvent.Metadata.Principal then
                                None
                            else
                                Some referenceEvent.Metadata.Principal
                        CreatedAt = referenceEvent.Metadata.Timestamp
                    }
                | LinkAdded link ->
                    { currentReferenceDto with
                        Links =
                            currentReferenceDto.Links
                            |> Seq.append (Seq.singleton link)
                            |> Seq.distinct
                    }
                | LinkRemoved link ->
                    { currentReferenceDto with
                        Links =
                            currentReferenceDto.Links
                            |> Seq.except (Seq.singleton link)
                    }
                | LogicalDeleted (force, deleteReason) -> { currentReferenceDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }
                | PhysicalDeleted -> currentReferenceDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> { currentReferenceDto with DeletedAt = None; DeleteReason = String.Empty }

            { newReferenceDto with UpdatedAt = Some referenceEvent.Metadata.Timestamp }
