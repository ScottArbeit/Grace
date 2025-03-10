namespace Grace.Actors

open Grace.Shared.Dto.Reference
open Grace.Shared.Types
open Grace.Shared.Utilities
open NodaTime
open System.Runtime.Serialization

module Commands =

    module Branch =
        [<KnownType("GetKnownTypes")>]
        type BranchCommand =
            | Create of
                branchId: BranchId *
                branchName: BranchName *
                parentBranchId: BranchId *
                basedOn: ReferenceId *
                repositoryId: RepositoryId *
                initialPermissions: ReferenceType[]
            | Rebase of basedOn: ReferenceId
            | SetName of newName: BranchName
            | Assign of directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Promote of directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Commit of directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Checkpoint of directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Save of directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | Tag of directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | CreateExternal of directoryVersionId: DirectoryVersionId * sha256Hash: Sha256Hash * referenceText: ReferenceText
            | EnableAssign of enabled: bool
            | EnablePromotion of enabled: bool
            | EnableCommit of enabled: bool
            | EnableCheckpoint of enabled: bool
            | EnableSave of enabled: bool
            | EnableTag of enabled: bool
            | EnableExternal of enabled: bool
            | EnableAutoRebase of enabled: bool
            | RemoveReference of referenceId: ReferenceId
            | DeleteLogical of force: bool * DeleteReason: DeleteReason
            | DeletePhysical
            | Undelete

            static member GetKnownTypes() = GetKnownTypes<BranchCommand>()

    module DirectoryVersion =
        [<KnownType("GetKnownTypes")>]
        type DirectoryVersionCommand =
            | Create of directoryVersion: DirectoryVersion
            | SetRecursiveSize of recursizeSize: int64
            | DeleteLogical of DeleteReason: DeleteReason
            | DeletePhysical
            | Undelete

            static member GetKnownTypes() = GetKnownTypes<DirectoryVersionCommand>()

    module Organization =
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

    module Owner =
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

    module Reference =
        [<KnownType("GetKnownTypes")>]
        type ReferenceCommand =
            | Create of referenceDto: ReferenceDto
            | AddLink of link: ReferenceLinkType
            | RemoveLink of link: ReferenceLinkType
            | DeleteLogical of force: bool * DeleteReason: DeleteReason
            | DeletePhysical
            | Undelete

            static member GetKnownTypes() = GetKnownTypes<ReferenceCommand>()

    module Repository =
        [<KnownType("GetKnownTypes")>]
        type RepositoryCommand =
            | Create of repositoryName: RepositoryName * repositoryId: RepositoryId * ownerId: OwnerId * organizationId: OrganizationId
            | Initialize
            | SetObjectStorageProvider of objectStorageProvider: ObjectStorageProvider
            | SetStorageAccountName of storageAccountName: StorageAccountName
            | SetStorageContainerName of storageContainerName: StorageContainerName
            | SetRepositoryType of repositoryVisibility: RepositoryType
            | SetRepositoryStatus of repositoryStatus: RepositoryStatus
            | SetRecordSaves of recordSaves: bool
            | SetAllowsLargeFiles of allowsLargeFiles: bool
            | SetAnonymousAccess of anonymousAccess: bool
            | SetDefaultServerApiVersion of defaultServerApiVersion: string
            | SetDefaultBranchName of defaultBranchName: BranchName
            | SetLogicalDeleteDays of duration: single
            | SetSaveDays of duration: single
            | SetCheckpointDays of duration: single
            | SetDirectoryVersionCacheDays of duration: single
            | SetDiffCacheDays of duration: single
            | SetName of repositoryName: RepositoryName
            | SetDescription of description: string
            | DeleteLogical of force: bool * DeleteReason: DeleteReason
            | DeletePhysical
            | Undelete

            static member GetKnownTypes() = GetKnownTypes<RepositoryCommand>()
