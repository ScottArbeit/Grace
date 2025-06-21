namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Reference =

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ReferenceDto =
        { Class: string
          ReferenceId: ReferenceId
          RepositoryId: RepositoryId
          BranchId: BranchId
          DirectoryId: DirectoryVersionId
          Sha256Hash: Sha256Hash
          ReferenceType: ReferenceType
          ReferenceText: ReferenceText
          Links: ReferenceLinkType array
          CreatedAt: Instant
          UpdatedAt: Instant option
          DeletedAt: Instant option
          DeleteReason: DeleteReason }

        static member Default =
            { Class = nameof (ReferenceDto)
              ReferenceId = ReferenceId.Empty
              RepositoryId = RepositoryId.Empty
              BranchId = BranchId.Empty
              DirectoryId = DirectoryVersionId.Empty
              Sha256Hash = Sha256Hash String.Empty
              ReferenceType = Save
              ReferenceText = ReferenceText String.Empty
              Links = Array.empty
              CreatedAt = Constants.DefaultTimestamp
              UpdatedAt = None
              DeletedAt = None
              DeleteReason = String.Empty }

        static member UpdateDto referenceEvent currentReferenceDto =
            let newReferenceDto =
                match referenceEvent.Event with
                | Created createdDto ->
                    { currentReferenceDto with
                        ReferenceId = createdDto.ReferenceId
                        RepositoryId = createdDto.RepositoryId
                        BranchId = createdDto.BranchId
                        DirectoryId = createdDto.DirectoryId
                        Sha256Hash = createdDto.Sha256Hash
                        ReferenceType = createdDto.ReferenceType
                        ReferenceText = createdDto.ReferenceText
                        Links = createdDto.Links
                        CreatedAt = referenceEvent.Metadata.Timestamp }
                | LinkAdded link ->
                    { currentReferenceDto with
                        Links =
                            currentReferenceDto.Links
                            |> Array.append (Array.singleton link)
                            |> Array.distinct }
                | LinkRemoved link -> { currentReferenceDto with Links = currentReferenceDto.Links |> Array.except (Array.singleton link) }
                | LogicalDeleted(force, deleteReason) -> { currentReferenceDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }
                | PhysicalDeleted -> currentReferenceDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> { currentReferenceDto with DeletedAt = None; DeleteReason = String.Empty }

            { newReferenceDto with UpdatedAt = Some referenceEvent.Metadata.Timestamp }

        static member GetKnownTypes() = GetKnownTypes<ReferenceDto>()

    and [<KnownType("GetKnownTypes"); GenerateSerializer>] ReferenceCommand =
        | Create of referenceDto: ReferenceDto
        | AddLink of link: ReferenceLinkType
        | RemoveLink of link: ReferenceLinkType
        | DeleteLogical of force: bool * DeleteReason: DeleteReason
        | DeletePhysical
        | Undelete

        static member GetKnownTypes() = GetKnownTypes<ReferenceCommand>()

    /// Defines the events for the Reference actor.
    and [<KnownType("GetKnownTypes"); GenerateSerializer>] ReferenceEventType =
        | Created of referenceDto: ReferenceDto
        | LinkAdded of link: ReferenceLinkType
        | LinkRemoved of link: ReferenceLinkType
        | LogicalDeleted of force: bool * DeleteReason: DeleteReason
        | PhysicalDeleted
        | Undeleted

        static member GetKnownTypes() = GetKnownTypes<ReferenceEventType>()

    /// Record that holds the event type and metadata for a Reference event.
    and [<GenerateSerializer>] ReferenceEvent =
        {
            /// The ReferenceEventType case that describes the event.
            Event: ReferenceEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }
