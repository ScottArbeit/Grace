namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

/// Defines commands, events, and types for the Owner actor.
module Owner =

    /// Defines the commands for the Owner actor.
    [<KnownType("GetKnownTypes")>]
    type OwnerCommand =
        | Create of ownerId: OwnerId * ownerName: OwnerName
        | SetName of ownerName: OwnerName
        | SetType of ownerType: OwnerType
        | SetSearchVisibility of searchVisibility: SearchVisibility
        | SetDescription of description: string
        | DeleteLogical of force: bool * DeleteReason: DeleteReason
        | DeletePhysical
        | Undelete

        static member GetKnownTypes() = GetKnownTypes<OwnerCommand>()

    /// Defines the events for the Owner actor.
    [<KnownType("GetKnownTypes")>]
    type OwnerEventType =
        | Created of ownerId: OwnerId * ownerName: OwnerName
        | NameSet of ownerName: OwnerName
        | TypeSet of ownerType: OwnerType
        | SearchVisibilitySet of searchVisibility: SearchVisibility
        | DescriptionSet of description: string
        | LogicalDeleted of force: bool * DeleteReason: DeleteReason
        | PhysicalDeleted
        | Undeleted

        static member GetKnownTypes() = GetKnownTypes<OwnerEventType>()

    /// Record that holds the event type and metadata for an Owner event.
    type OwnerEvent =
        {
            /// The OwnerEventType case that describes the event.
            Event: OwnerEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }

    /// The OwnerDto is a data transfer object that represents an owner in the system.
    type OwnerDto =
        { Class: string
          OwnerId: OwnerId
          OwnerName: OwnerName
          OwnerType: OwnerType
          Description: string
          SearchVisibility: SearchVisibility
          CreatedAt: Instant
          UpdatedAt: Instant option
          DeletedAt: Instant option
          DeleteReason: DeleteReason }

        /// Default instance of OwnerDto with empty or default values.
        static member Default =
            { Class = nameof (OwnerDto)
              OwnerId = OwnerId.Empty
              OwnerName = String.Empty
              OwnerType = OwnerType.Public
              Description = String.Empty
              SearchVisibility = Visible
              CreatedAt = Constants.DefaultTimestamp
              UpdatedAt = None
              DeletedAt = None
              DeleteReason = String.Empty }

        /// Updates the OwnerDto based on the OwnerEvent received.
        static member UpdateDto ownerEvent currentOwnerDto =
            let newOwnerDto =
                match ownerEvent.Event with
                | Created(ownerId, ownerName) -> { OwnerDto.Default with OwnerId = ownerId; OwnerName = ownerName; CreatedAt = ownerEvent.Metadata.Timestamp }
                | NameSet(ownerName) -> { currentOwnerDto with OwnerName = ownerName }
                | TypeSet(ownerType) -> { currentOwnerDto with OwnerType = ownerType }
                | SearchVisibilitySet(searchVisibility) -> { currentOwnerDto with SearchVisibility = searchVisibility }
                | DescriptionSet(description) -> { currentOwnerDto with Description = description }
                | LogicalDeleted(_, deleteReason) -> { currentOwnerDto with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }
                | PhysicalDeleted -> currentOwnerDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> { currentOwnerDto with DeletedAt = None; DeleteReason = String.Empty }

            { newOwnerDto with UpdatedAt = Some ownerEvent.Metadata.Timestamp }
