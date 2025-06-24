namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

/// Defines commands, events, and types for Organizations.
module Organization =

    /// Defines the commands for the Organization actor.
    [<KnownType("GetKnownTypes")>]
    type OrganizationCommand =
        | Create of organizationId: OrganizationId * organizationName: OrganizationName * ownerId: OwnerId
        | SetName of organizationName: OrganizationName
        | SetType of organizationType: OrganizationType
        | SetSearchVisibility of searchVisibility: SearchVisibility
        | SetDescription of description: string
        | DeleteLogical of force: bool * DeleteReason: DeleteReason
        | DeletePhysical
        | Undelete

        static member GetKnownTypes() = GetKnownTypes<OrganizationCommand>()

    /// Defines the events for the Organization actor.
    [<KnownType("GetKnownTypes")>]
    type OrganizationEventType =
        | Created of organizationId: OrganizationId * organizationName: OrganizationName * ownerId: OwnerId
        | NameSet of organizationName: OrganizationName
        | TypeSet of organizationType: OrganizationType
        | SearchVisibilitySet of searchVisibility: SearchVisibility
        | DescriptionSet of organizationDescription: string
        | LogicalDeleted of force: bool * DeleteReason: DeleteReason
        | PhysicalDeleted
        | Undeleted

        static member GetKnownTypes() = GetKnownTypes<OrganizationEventType>()

    /// Record that holds the event type and metadata for an Organization event.
    type OrganizationEvent =
        {
            /// The OrganizationEventType case that describes the event.
            Event: OrganizationEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }

    type OrganizationDto =
        { Class: string
          OrganizationId: OrganizationId
          OrganizationName: OrganizationName
          OwnerId: OwnerId
          OrganizationType: OrganizationType
          Description: string
          SearchVisibility: SearchVisibility
          CreatedAt: Instant
          UpdatedAt: Instant option
          DeletedAt: Instant option
          DeleteReason: DeleteReason }

        static member Default =
            { Class = nameof (OrganizationDto)
              OrganizationId = OrganizationId.Empty
              OrganizationName = String.Empty
              OwnerId = OwnerId.Empty
              OrganizationType = OrganizationType.Public
              Description = String.Empty
              SearchVisibility = Visible
              CreatedAt = Constants.DefaultTimestamp
              UpdatedAt = None
              DeletedAt = None
              DeleteReason = String.Empty }

        /// Updates the OrganizationDto based on the OrganizationEvent.
        static member UpdateDto organizationEvent currentOrganizationDto =
            let newOrganizationDto =
                match organizationEvent.Event with
                | Created(organizationId, organizationName, ownerId) ->
                    { OrganizationDto.Default with
                        OrganizationId = organizationId
                        OrganizationName = organizationName
                        OwnerId = ownerId
                        CreatedAt = organizationEvent.Metadata.Timestamp }
                | NameSet(organizationName) -> { currentOrganizationDto with OrganizationName = organizationName }
                | TypeSet(organizationType) -> { currentOrganizationDto with OrganizationType = organizationType }
                | SearchVisibilitySet(searchVisibility) -> { currentOrganizationDto with SearchVisibility = searchVisibility }
                | DescriptionSet(description) -> { currentOrganizationDto with Description = description }
                | LogicalDeleted(_, deleteReason) -> { currentOrganizationDto with DeleteReason = deleteReason; DeletedAt = Some(getCurrentInstant ()) }
                | PhysicalDeleted -> currentOrganizationDto // Do nothing because it's about to be deleted anyway.
                | Undeleted -> { currentOrganizationDto with DeletedAt = None; DeleteReason = String.Empty }

            { newOrganizationDto with UpdatedAt = Some organizationEvent.Metadata.Timestamp }
