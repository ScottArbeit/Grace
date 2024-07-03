namespace Grace.Shared

open Grace.Shared.Types
open Grace.Shared.Utilities
open NodaTime
open System
open System.Collections.Generic
open System.Runtime.Serialization

module Dto =

    module Branch =
        [<Serializable>]
        [<KnownType("GetKnownTypes")>]
        type BranchDto =
            { Class: string
              BranchId: BranchId
              BranchName: BranchName
              ParentBranchId: BranchId
              BasedOn: ReferenceId
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
              LatestPromotion: ReferenceId
              LatestCommit: ReferenceId
              LatestCheckpoint: ReferenceId
              LatestSave: ReferenceId
              CreatedAt: Instant
              UpdatedAt: Instant option
              DeletedAt: Instant option
              DeleteReason: DeleteReason }

            static member Default =
                { Class = nameof (BranchDto)
                  BranchId = BranchId.Empty
                  BranchName = BranchName String.Empty
                  ParentBranchId = Constants.DefaultParentBranchId
                  BasedOn = ReferenceId.Empty
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
                  LatestPromotion = ReferenceId.Empty
                  LatestCommit = ReferenceId.Empty
                  LatestCheckpoint = ReferenceId.Empty
                  LatestSave = ReferenceId.Empty
                  CreatedAt = getCurrentInstant ()
                  UpdatedAt = None
                  DeletedAt = None
                  DeleteReason = String.Empty
                }

            static member GetKnownTypes() = GetKnownTypes<BranchDto>()

    module Diff =
        [<Serializable>]
        [<KnownType("GetKnownTypes")>]
        type DiffDto =
            { Class: string
              HasDifferences: bool
              DirectoryId1: DirectoryVersionId
              Directory1CreatedAt: Instant
              DirectoryId2: DirectoryVersionId
              Directory2CreatedAt: Instant
              Differences: List<FileSystemDifference>
              FileDiffs: List<FileDiff> }

            static member Default =
                { Class = nameof (DiffDto)
                  HasDifferences = false
                  DirectoryId1 = DirectoryVersionId.Empty
                  Directory1CreatedAt = Instant.FromUnixTimeTicks 0L
                  DirectoryId2 = DirectoryVersionId.Empty
                  Directory2CreatedAt = Instant.FromUnixTimeTicks 0L
                  Differences = List<FileSystemDifference>()
                  FileDiffs = List<FileDiff>()
                }

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
              Repositories: Dictionary<RepositoryId, RepositoryName>
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
                  Repositories = new Dictionary<RepositoryId, RepositoryName>()
                  CreatedAt = getCurrentInstant ()
                  UpdatedAt = None
                  DeletedAt = None
                  DeleteReason = String.Empty
                }

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
              Organizations: Dictionary<OrganizationId, OrganizationName>
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
                  Organizations = new Dictionary<OrganizationId, OrganizationName>()
                  CreatedAt = getCurrentInstant ()
                  UpdatedAt = None
                  DeletedAt = None
                  DeleteReason = String.Empty
                }

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
              CreatedAt: Instant
              UpdatedAt: Instant option
              DeletedAt: Instant option
              DeleteReason: DeleteReason
            }

            static member Default =
                { Class = nameof (ReferenceDto)
                  ReferenceId = ReferenceId.Empty
                  RepositoryId = RepositoryId.Empty
                  BranchId = BranchId.Empty
                  DirectoryId = DirectoryVersionId.Empty
                  Sha256Hash = Sha256Hash String.Empty
                  ReferenceType = Save
                  ReferenceText = ReferenceText String.Empty
                  CreatedAt = getCurrentInstant ()
                  UpdatedAt = None
                  DeletedAt = None
                  DeleteReason = String.Empty
                }

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
              RepositoryVisibility: RepositoryVisibility
              RepositoryStatus: RepositoryStatus
              Branches: SortedSet<BranchName>
              DefaultServerApiVersion: string
              DefaultBranchName: BranchName
              LogicalDeleteDays: double
              SaveDays: double
              CheckpointDays: double
              DirectoryVersionCacheDays: double
              DiffCacheDays: double
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
                  RepositoryVisibility = RepositoryVisibility.Private
                  RepositoryStatus = RepositoryStatus.Active
                  Branches = SortedSet<BranchName>()
                  DefaultServerApiVersion = "latest"
                  DefaultBranchName = BranchName Constants.InitialBranchName
                  LogicalDeleteDays = 30.0
                  SaveDays = 7.0
                  CheckpointDays = 365.0
                  DirectoryVersionCacheDays = 1.0
                  DiffCacheDays = 1.0
                  Description = String.Empty
                  RecordSaves = true
                  CreatedAt = getCurrentInstant ()
                  InitializedAt = None
                  UpdatedAt = None
                  DeletedAt = None
                  DeleteReason = String.Empty
                }

            static member GetKnownTypes() = GetKnownTypes<RepositoryDto>()
