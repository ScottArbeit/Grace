namespace Grace.Actors

open Grace.Shared.Types
open Grace.Shared.Utilities
open NodaTime
open System.Runtime.Serialization

module Events =

    module Branch =
        [<KnownType("GetKnownTypes")>]
        type BranchEventType =
            | Created of branchId: BranchId * branchName: BranchName * parentBranchId: BranchId * repositoryId: RepositoryId
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
            | LogicalDeleted
            | PhysicalDeleted
            | Undeleted
            static member GetKnownTypes() = GetKnownTypes<BranchEventType>()

        type BranchEvent = {
            Event: BranchEventType
            Metadata: EventMetadata
        }

    module Organization =
        [<KnownType("GetKnownTypes")>]
        type OrganizationEventType =
            | Created of organizationId: OrganizationId * organizationName: OrganizationName * ownerId: OwnerId
            | NameSet of organizationName: OrganizationName 
            | TypeSet of organizationType: OrganizationType 
            | SearchVisibilitySet of searchVisibility: SearchVisibility 
            | DescriptionSet of organizationDescription: string 
            | RepositoryAdded of repositoryId: RepositoryId * RepositoryName: RepositoryName 
            | RepositoryDeleted of repositoryId: RepositoryId 
            | LogicalDeleted of force: bool * deleteReason: string
            | PhysicalDeleted
            | Undeleted
            static member GetKnownTypes() = GetKnownTypes<OrganizationEventType>()

        type OrganizationEvent =
            {
                Event: OrganizationEventType
                Metadata: EventMetadata
            }

    module Owner =
        [<KnownType("GetKnownTypes")>]
        type OwnerEventType =
            | Created of ownerId: OwnerId * ownerName: OwnerName
            | NameSet of ownerName: OwnerName 
            | TypeSet of ownerType: OwnerType
            | SearchVisibilitySet of searchVisibility: SearchVisibility
            | DescriptionSet of description: string
            | OrganizationAdded of organizationId: OrganizationId * organizationName: OrganizationName
            | OrganizationDeleted of organizationId: OrganizationId
            | LogicalDeleted of force: bool * deleteReason: string
            | PhysicalDeleted 
            | Undeleted
            static member GetKnownTypes() = GetKnownTypes<OwnerEventType>()

        type OwnerEvent =
            {
                Event: OwnerEventType
                Metadata: EventMetadata
            }

    module Repository =
        [<KnownType("GetKnownTypes")>]
        type RepositoryEventType =
            | Created of repositoryName: RepositoryName * repositoryId: RepositoryId * ownerId: OwnerId * organizationId: OrganizationId
            | ObjectStorageProviderSet of objectStorageProvider: ObjectStorageProvider
            | StorageAccountNameSet of storageAccountName: StorageAccountName
            | StorageContainerNameSet of storageContainerName: StorageContainerName
            | RepositoryVisibilitySet of repositoryVisibility: RepositoryVisibility
            | RepositoryStatusSet of repositoryStatus: RepositoryStatus
            | BranchAdded of branchName: BranchName
            | BranchDeleted of branchName: BranchName
            | RecordSavesSet of recordSaves: bool
            | DefaultServerApiVersionSet of defaultServerApiVersion: string
            | DefaultBranchNameSet of defaultBranchName: BranchName
            | SaveDaysSet of duration: float
            | CheckpointDaysSet of duration: float
            | EnabledSingleStepPromotion of enabled: bool
            | EnabledComplexPromotion of enabled: bool
            | DescriptionSet of description: string
            | LogicalDeleted of force: bool * deleteReason: string
            | PhysicalDeleted
            | Undeleted
            static member GetKnownTypes() = GetKnownTypes<RepositoryEventType>()
        
        type RepositoryEvent =
            {
                Event: RepositoryEventType
                Metadata: EventMetadata
            }

    [<KnownType("GetKnownTypes")>]
    type GraceEvent =
        | BranchEvent of Branch.BranchEvent
        | OrganizationEvent of Organization.OrganizationEvent
        | OwnerEvent of Owner.OwnerEvent
        | RepositoryEvent of Repository.RepositoryEvent
        static member GetKnownTypes() = GetKnownTypes<GraceEvent>()
    