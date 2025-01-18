namespace Grace.Shared

open Grace.Shared.Types
open Grace.Shared.Utilities
open NodaTime
open System
open System.Collections.Generic
open System.Runtime.Serialization

module Dto =

    module Diff =
        [<Serializable>]
        [<KnownType("GetKnownTypes")>]
        type DiffDto =
            { Class: string
              RepositoryId: RepositoryId
              DirectoryVersionId1: DirectoryVersionId
              Directory1CreatedAt: Instant
              DirectoryVersionId2: DirectoryVersionId
              Directory2CreatedAt: Instant
              HasDifferences: bool
              Differences: List<FileSystemDifference>
              FileDiffs: List<FileDiff> }

            static member Default =
                { Class = nameof (DiffDto)
                  RepositoryId = RepositoryId.Empty
                  DirectoryVersionId1 = DirectoryVersionId.Empty
                  Directory1CreatedAt = Constants.DefaultTimestamp
                  DirectoryVersionId2 = DirectoryVersionId.Empty
                  Directory2CreatedAt = Constants.DefaultTimestamp
                  Differences = List<FileSystemDifference>()
                  HasDifferences = false
                  FileDiffs = List<FileDiff>() }

            static member GetKnownTypes() = GetKnownTypes<DiffDto>()

    module Organization =
        [<Serializable>]
        [<KnownType("GetKnownTypes")>]
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

            static member GetKnownTypes() = GetKnownTypes<OrganizationDto>()

    module Owner =
        [<Serializable>]
        [<KnownType("GetKnownTypes")>]
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

            static member GetKnownTypes() = GetKnownTypes<OwnerDto>()

    module Reference =
        [<Serializable>]
        [<KnownType("GetKnownTypes")>]
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

            static member GetKnownTypes() = GetKnownTypes<ReferenceDto>()

    module Repository =
        [<Serializable>]
        [<KnownType("GetKnownTypes")>]
        type RepositoryDto =
            { Class: string
              RepositoryId: RepositoryId
              OwnerId: OwnerId
              OrganizationId: OrganizationId
              RepositoryName: RepositoryName
              ObjectStorageProvider: ObjectStorageProvider
              StorageAccountName: StorageAccountName
              StorageContainerName: StorageContainerName
              RepositoryType: RepositoryType
              RepositoryStatus: RepositoryStatus
              DefaultServerApiVersion: string
              DefaultBranchName: BranchName
              LogicalDeleteDays: single
              SaveDays: single
              CheckpointDays: single
              DirectoryVersionCacheDays: single
              DiffCacheDays: single
              Description: string
              RecordSaves: bool
              CreatedAt: Instant
              InitializedAt: Instant option
              UpdatedAt: Instant option
              DeletedAt: Instant option
              DeleteReason: DeleteReason }

            static member Default =
                { Class = nameof (RepositoryDto)
                  RepositoryId = Guid.Empty
                  OwnerId = OwnerId.Empty
                  OrganizationId = OrganizationId.Empty
                  RepositoryName = RepositoryName String.Empty
                  ObjectStorageProvider = ObjectStorageProvider.Unknown
                  StorageAccountName = String.Empty
                  StorageContainerName = "grace-objects"
                  RepositoryType = RepositoryType.Private
                  RepositoryStatus = RepositoryStatus.Active
                  DefaultServerApiVersion = "latest"
                  DefaultBranchName = BranchName Constants.InitialBranchName
                  LogicalDeleteDays = 30.0f
                  SaveDays = 7.0f
                  CheckpointDays = 365.0f
                  DirectoryVersionCacheDays = 1.0f
                  DiffCacheDays = 1.0f
                  Description = String.Empty
                  RecordSaves = true
                  CreatedAt = Constants.DefaultTimestamp
                  InitializedAt = None
                  UpdatedAt = None
                  DeletedAt = None
                  DeleteReason = String.Empty }

            static member GetKnownTypes() = GetKnownTypes<RepositoryDto>()

    module Branch =
        open Reference

        [<Serializable>]
        [<KnownType("GetKnownTypes")>]
        type BranchDto =
            { Class: string
              BranchId: BranchId
              BranchName: BranchName
              ParentBranchId: BranchId
              BasedOn: ReferenceDto
              RepositoryId: RepositoryId
              UserId: UserId
              AssignEnabled: bool
              PromotionEnabled: bool
              CommitEnabled: bool
              CheckpointEnabled: bool
              SaveEnabled: bool
              TagEnabled: bool
              ExternalEnabled: bool
              AutoRebaseEnabled: bool
              LatestPromotion: ReferenceDto
              LatestCommit: ReferenceDto
              LatestCheckpoint: ReferenceDto
              LatestSave: ReferenceDto
              ShouldRecomputeLatestReferences: bool
              CreatedAt: Instant
              UpdatedAt: Instant option
              DeletedAt: Instant option
              DeleteReason: DeleteReason }

            static member Default =
                { Class = nameof (BranchDto)
                  BranchId = BranchId.Empty
                  BranchName = BranchName "root"
                  ParentBranchId = Constants.DefaultParentBranchId
                  BasedOn = ReferenceDto.Default
                  RepositoryId = RepositoryId.Empty
                  UserId = UserId String.Empty
                  AssignEnabled = false
                  PromotionEnabled = false
                  CommitEnabled = false
                  CheckpointEnabled = false
                  SaveEnabled = false
                  TagEnabled = false
                  ExternalEnabled = false
                  AutoRebaseEnabled = true
                  LatestPromotion = ReferenceDto.Default
                  LatestCommit = ReferenceDto.Default
                  LatestCheckpoint = ReferenceDto.Default
                  LatestSave = ReferenceDto.Default
                  ShouldRecomputeLatestReferences = false
                  CreatedAt = Constants.DefaultTimestamp
                  UpdatedAt = None
                  DeletedAt = None
                  DeleteReason = String.Empty }

            static member GetKnownTypes() = GetKnownTypes<BranchDto>()
