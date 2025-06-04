namespace Grace.Shared

open Grace.Shared.Dto.Reference
open Grace.Types.Types
open Grace.Shared.Utilities
open NodaTime
open Orleans
open System.Runtime.Serialization

module Events =

    /// Defines the events for the Branch actor.
    module Branch =
        /// Defines the events for the Branch actor.
        [<KnownType("GetKnownTypes"); GenerateSerializer>]
        type BranchEventType =
            | Created of
                branchId: BranchId *
                branchName: BranchName *
                parentBranchId: BranchId *
                basedOn: ReferenceId *
                repositoryId: RepositoryId *
                initialPermissions: ReferenceType[]
            | Rebased of basedOn: ReferenceId
            | NameSet of newName: BranchName
            | Assigned of referenceDto: ReferenceDto * directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Promoted of referenceDto: ReferenceDto * directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Committed of referenceDto: ReferenceDto * directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Checkpointed of referenceDto: ReferenceDto * directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Saved of referenceDto: ReferenceDto * directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Tagged of referenceDto: ReferenceDto * directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | ExternalCreated of referenceDto: ReferenceDto * directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | EnabledAssign of enabled: bool
            | EnabledPromotion of enabled: bool
            | EnabledCommit of enabled: bool
            | EnabledCheckpoint of enabled: bool
            | EnabledSave of enabled: bool
            | EnabledTag of enabled: bool
            | EnabledExternal of enabled: bool
            | EnabledAutoRebase of enabled: bool
            | ReferenceRemoved of referenceId: ReferenceId
            | LogicalDeleted of force: bool * DeleteReason: DeleteReason
            | PhysicalDeleted
            | Undeleted

            static member GetKnownTypes() = GetKnownTypes<BranchEventType>()

        /// Record that holds the event type and metadata for a Branch event.
        [<GenerateSerializer>]
        type BranchEvent =
            {
                /// The BranchEventType case that describes the event.
                Event: BranchEventType
                /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
                Metadata: EventMetadata
            }

    /// Defines the events for the DirectoryVersion actor.
    module DirectoryVersion =
        /// Defines the events for the DirectoryVersion actor.
        [<KnownType("GetKnownTypes"); GenerateSerializer>]
        type DirectoryVersionEventType =
            | Created of directoryVersion: DirectoryVersion
            | RecursiveSizeSet of recursiveSize: int64
            | LogicalDeleted of DeleteReason: DeleteReason
            | PhysicalDeleted
            | Undeleted

        /// Record that holds the event type and metadata for a DirectoryVersion event.
        [<GenerateSerializer>]
        type DirectoryVersionEvent =
            {
                /// The DirectoryVersionEventType case that describes the event.
                Event: DirectoryVersionEventType
                /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
                Metadata: EventMetadata
            }

    /// Defines the events for the Organization actor.
    module Organization =
        /// Defines the events for the Organization actor.
        [<KnownType("GetKnownTypes"); GenerateSerializer>]
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
        [<GenerateSerializer>]
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
        [<KnownType("GetKnownTypes"); GenerateSerializer>]
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
        [<GenerateSerializer>]
        type OwnerEvent =
            {
                /// The OwnerEventType case that describes the event.
                Event: OwnerEventType
                /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
                Metadata: EventMetadata
            }

    /// Defines the events for the Reference actor.
    module Reference =
        /// Defines the events for the Reference actor.
        [<KnownType("GetKnownTypes"); GenerateSerializer>]
        type ReferenceEventType =
            | Created of referenceDto: ReferenceDto
            | LinkAdded of link: ReferenceLinkType
            | LinkRemoved of link: ReferenceLinkType
            | LogicalDeleted of force: bool * DeleteReason: DeleteReason
            | PhysicalDeleted
            | Undeleted

            static member GetKnownTypes() = GetKnownTypes<ReferenceEventType>()

        /// Record that holds the event type and metadata for a Reference event.
        [<GenerateSerializer>]
        type ReferenceEvent =
            {
                /// The ReferenceEventType case that describes the event.
                Event: ReferenceEventType
                /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
                Metadata: EventMetadata
            }

    /// Defines the events for the Repository actor.
    module Repository =
        /// Defines the events for the Repository actor.
        [<KnownType("GetKnownTypes"); GenerateSerializer>]
        type RepositoryEventType =
            | Created of repositoryName: RepositoryName * repositoryId: RepositoryId * ownerId: OwnerId * organizationId: OrganizationId
            | Initialized
            | ObjectStorageProviderSet of objectStorageProvider: ObjectStorageProvider
            | StorageAccountNameSet of storageAccountName: StorageAccountName
            | StorageContainerNameSet of storageContainerName: StorageContainerName
            | RepositoryTypeSet of repositoryVisibility: RepositoryType
            | RepositoryStatusSet of repositoryStatus: RepositoryStatus
            | AllowsLargeFilesSet of allowsLargeFiles: bool
            | AnonymousAccessSet of anonymousAccess: bool
            | RecordSavesSet of recordSaves: bool
            | DefaultServerApiVersionSet of defaultServerApiVersion: string
            | DefaultBranchNameSet of defaultBranchName: BranchName
            | LogicalDeleteDaysSet of duration: single
            | SaveDaysSet of duration: single
            | CheckpointDaysSet of duration: single
            | DirectoryVersionCacheDaysSet of duration: single
            | DiffCacheDaysSet of duration: single
            | NameSet of repositoryName: RepositoryName
            | DescriptionSet of description: string
            | LogicalDeleted of force: bool * DeleteReason: DeleteReason
            | PhysicalDeleted
            | Undeleted

            static member GetKnownTypes() = GetKnownTypes<RepositoryEventType>()

        /// Record that holds the event type and metadata for a Repository event.
        [<GenerateSerializer>]
        type RepositoryEvent =
            {
                /// The RepositoryEventType case that describes the event.
                Event: RepositoryEventType
                /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
                Metadata: EventMetadata
            }

    /// A discriminated union that holds all of the possible events for Grace. Used for publishing events to graceEventStream.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type GraceEvent =
        | BranchEvent of Branch.BranchEvent
        | DirectoryVersionEvent of DirectoryVersion.DirectoryVersionEvent
        | OrganizationEvent of Organization.OrganizationEvent
        | OwnerEvent of Owner.OwnerEvent
        | ReferenceEvent of Reference.ReferenceEvent
        | RepositoryEvent of Repository.RepositoryEvent

        static member GetKnownTypes() = GetKnownTypes<GraceEvent>()
