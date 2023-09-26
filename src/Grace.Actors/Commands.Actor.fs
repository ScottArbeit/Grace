namespace Grace.Actors

open Grace.Shared.Types
open Grace.Shared.Utilities
open System.Runtime.Serialization

module Commands =

    module Branch =
        [<KnownType("GetKnownTypes")>]
        type BranchCommand =
            | Create of branchId: BranchId * branchName: BranchName * parentBranchId: BranchId * basedOn: ReferenceId * repositoryId: RepositoryId * initialPermissions: ReferenceType[]
            | Rebase of basedOn: ReferenceId
            | SetName of newName: BranchName
            | Promote of directoryId: DirectoryId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Commit of directoryId: DirectoryId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Checkpoint of directoryId: DirectoryId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Save of directoryId: DirectoryId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Tag of directoryId: DirectoryId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | EnablePromotion of enabled: bool
            | EnableCommit of enabled: bool
            | EnableCheckpoint of enabled: bool
            | EnableSave of enabled: bool
            | EnableTag of enabled: bool
            | RemoveReference of referenceId: ReferenceId
            | DeleteLogical of force: bool * deleteReason: string
            | DeletePhysical
            | Undelete
            static member GetKnownTypes() = GetKnownTypes<BranchCommand>()

    module Organization =
        [<KnownType("GetKnownTypes")>]
        type OrganizationCommand =
            | Create of organizationId: OrganizationId * organizationName: OrganizationName * ownerId: OwnerId
            | SetName of organizationName: OrganizationName
            | SetType of organizationType: OrganizationType
            | SetSearchVisibility of searchVisibility: SearchVisibility
            | SetDescription of description: string
            | DeleteLogical of force: bool * deleteReason: string
            | DeletePhysical
            | Undelete
            static member GetKnownTypes() = GetKnownTypes<OrganizationCommand>()

    module Owner =
        [<KnownType("GetKnownTypes")>]
        type OwnerCommand =
            | Create of ownerId: OwnerId * ownerName: OwnerName
            | SetName of ownerName: OwnerName
            | SetType of ownerType: OwnerType
            | SetSearchVisibility of searchVisibility: SearchVisibility
            | SetDescription of description: string
            | DeleteLogical of force: bool * deleteReason: string
            | DeletePhysical
            | Undelete
            static member GetKnownTypes() = GetKnownTypes<OwnerCommand>()

    module Repository =
        [<KnownType("GetKnownTypes")>]
        type RepositoryCommand =
            | Create of repositoryName: RepositoryName * repositoryId: RepositoryId * ownerId: OwnerId * organizationId: OrganizationId
            | Initialize
            | SetObjectStorageProvider of objectStorageProvider: ObjectStorageProvider
            | SetStorageAccountName of storageAccountName: StorageAccountName
            | SetStorageContainerName of storageContainerName: StorageContainerName
            | SetVisibility of repositoryVisibility: RepositoryVisibility
            | SetRepositoryStatus of repositoryStatus: RepositoryStatus
            | SetRecordSaves of recordSaves: bool
            | SetDefaultServerApiVersion of defaultServerApiVersion: string
            | SetDefaultBranchName of defaultBranchName: BranchName
            | SetSaveDays of duration: double
            | SetCheckpointDays of duration: double
            | SetDescription of description: string
            | EnableSingleStepPromotion of enabled: bool
            | EnableComplexPromotion of enabled: bool
            | DeleteLogical of force: bool * deleteReason: string
            | DeletePhysical
            | Undelete
            static member GetKnownTypes() = GetKnownTypes<RepositoryCommand>()
