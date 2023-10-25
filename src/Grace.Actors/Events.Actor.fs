namespace Grace.Actors

open Grace.Shared.Types
open Grace.Shared.Utilities
open NodaTime
open System.Runtime.Serialization

module Events =

    /// Defines the events for the Branch actor.
    module Branch =
        /// Defines the events for the Branch actor.
        [<KnownType("GetKnownTypes")>]
        type BranchEventType =
            | Created of branchId: BranchId * branchName: BranchName * parentBranchId: BranchId * basedOn: ReferenceId * repositoryId: RepositoryId * initialPermissions: ReferenceType[]
            | Rebased of basedOn: ReferenceId
            | NameSet of newName: BranchName
            | Promoted of referenceId: ReferenceId * directoryId: DirectoryId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Committed of referenceId: ReferenceId * directoryId: DirectoryId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Checkpointed of referenceId: ReferenceId * directoryId: DirectoryId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Saved of referenceId: ReferenceId * directoryId: DirectoryId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Tagged of referenceId: ReferenceId * directoryId: DirectoryId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | EnabledPromotion of allowed: bool
            | EnabledCommit of allowed: bool
            | EnabledCheckpoint of allowed: bool
            | EnabledSave of allowed: bool
            | EnabledTag of allowed: bool
            | ReferenceRemoved of referenceId: ReferenceId
            | LogicalDeleted of force: bool * deleteReason: string
            | PhysicalDeleted
            | Undeleted
            static member GetKnownTypes() = GetKnownTypes<BranchEventType>()

        /// Record that holds the event type and metadata for a Branch event.
        type BranchEvent = {
            /// The BranchEventType case that describes the event.
            Event: BranchEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }

    /// Defines the events for the Organization actor.
    module Organization =
        /// Defines the events for the Organization actor.
        [<KnownType("GetKnownTypes")>]
        type OrganizationEventType =
            | Created of organizationId: OrganizationId * organizationName: OrganizationName * ownerId: OwnerId
            | NameSet of organizationName: OrganizationName 
            | TypeSet of organizationType: OrganizationType 
            | SearchVisibilitySet of searchVisibility: SearchVisibility 
            | DescriptionSet of organizationDescription: string 
            | LogicalDeleted of force: bool * deleteReason: string
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

    /// Defines the events for the Owner actor.
    module Owner =
        /// Defines the events for the Owner actor.
        [<KnownType("GetKnownTypes")>]
        type OwnerEventType =
            | Created of ownerId: OwnerId * ownerName: OwnerName
            | NameSet of ownerName: OwnerName 
            | TypeSet of ownerType: OwnerType
            | SearchVisibilitySet of searchVisibility: SearchVisibility
            | DescriptionSet of description: string
            | LogicalDeleted of force: bool * deleteReason: string
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

    /// Defines the events for the Repository actor.
    module Repository =
        /// Defines the events for the Repository actor.
        [<KnownType("GetKnownTypes")>]
        type RepositoryEventType =
            | Created of repositoryName: RepositoryName * repositoryId: RepositoryId * ownerId: OwnerId * organizationId: OrganizationId
            | Initialized
            | ObjectStorageProviderSet of objectStorageProvider: ObjectStorageProvider
            | StorageAccountNameSet of storageAccountName: StorageAccountName
            | StorageContainerNameSet of storageContainerName: StorageContainerName
            | RepositoryVisibilitySet of repositoryVisibility: RepositoryVisibility
            | RepositoryStatusSet of repositoryStatus: RepositoryStatus
            | RecordSavesSet of recordSaves: bool
            | DefaultServerApiVersionSet of defaultServerApiVersion: string
            | DefaultBranchNameSet of defaultBranchName: BranchName
            | SaveDaysSet of duration: double
            | CheckpointDaysSet of duration: double
            | EnabledSingleStepPromotion of enabled: bool
            | EnabledComplexPromotion of enabled: bool
            | DescriptionSet of description: string
            | LogicalDeleted of force: bool * deleteReason: string
            | PhysicalDeleted
            | Undeleted
            static member GetKnownTypes() = GetKnownTypes<RepositoryEventType>()
        
        /// Record that holds the event type and metadata for a Repository event.
        type RepositoryEvent =
            {
                /// The RepositoryEventType case that describes the event.
                Event: RepositoryEventType
                /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
                Metadata: EventMetadata
            }

    /// A discriminated union that holds all of the possible events for Grace. Used for publishing events to graceEventStream.
    [<KnownType("GetKnownTypes")>]
    type GraceEvent =
        | BranchEvent of Branch.BranchEvent
        | OrganizationEvent of Organization.OrganizationEvent
        | OwnerEvent of Owner.OwnerEvent
        | RepositoryEvent of Repository.RepositoryEvent
        static member GetKnownTypes() = GetKnownTypes<GraceEvent>()
    