namespace Grace.Shared

open Grace.Shared.Types
open Grace.Shared.Utilities
open NodaTime
open System
open System.Collections.Generic

module Dto =

    module Branch =
        [<Serializable>]
        [<CLIMutable>]
        type BranchDto = 
            {
                Class: string
                BranchId: BranchId
                BranchName: BranchName
                ParentBranchId: BranchId
                BasedOn: ReferenceId
                RepositoryId: RepositoryId
                UserId: UserId
                //References: SortedList<Instant, ReferenceDto>
                PromotionEnabled: bool
                CommitEnabled: bool
                CheckpointEnabled: bool
                SaveEnabled: bool
                TagEnabled: bool
                LatestPromotion: ReferenceId
                LatestCommit: ReferenceId
                LatestCheckpoint: ReferenceId
                LatestSave: ReferenceId
                CreatedAt: Instant
                UpdatedAt: Instant option
                DeletedAt: Instant option
                DeleteReason: string
            }
            static member Default = {
                Class = nameof(BranchDto)
                BranchId = BranchId.Empty
                BranchName = BranchName String.Empty
                ParentBranchId = Constants.DefaultParentBranchId
                BasedOn = ReferenceId.Empty
                RepositoryId = RepositoryId.Empty
                UserId = UserId String.Empty
                //References = SortedList<Instant, ReferenceDto>()
                PromotionEnabled = false
                CommitEnabled = false
                CheckpointEnabled = false
                SaveEnabled = false
                TagEnabled = false
                LatestPromotion = ReferenceId.Empty
                LatestCommit = ReferenceId.Empty
                LatestCheckpoint = ReferenceId.Empty
                LatestSave = ReferenceId.Empty
                CreatedAt = getCurrentInstant()
                UpdatedAt = None
                DeletedAt = None
                DeleteReason = String.Empty
            }

    module Diff =
        [<Serializable>]
        type DiffDto =
            {
                Class: string
                HasDifferences: bool
                DirectoryId1: DirectoryId
                Directory1CreatedAt: Instant
                DirectoryId2: DirectoryId
                Directory2CreatedAt: Instant
                Differences: List<FileSystemDifference>
                FileDiffs: List<FileDiff>
            }

            static member Default = {
                Class = nameof(DiffDto)
                HasDifferences = false
                DirectoryId1 = DirectoryId.Empty
                Directory1CreatedAt = Instant.FromUnixTimeTicks 0L
                DirectoryId2 = DirectoryId.Empty
                Directory2CreatedAt = Instant.FromUnixTimeTicks 0L
                Differences = List<FileSystemDifference>()
                FileDiffs = List<FileDiff>()
            }

    module Organization =
        [<Serializable>]
        type OrganizationDto = 
            {
                Class: string
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
                DeleteReason: string
            }
            static member Default = {
                Class = nameof(OrganizationDto)
                OrganizationId = OrganizationId.Empty
                OrganizationName = String.Empty
                OwnerId = OwnerId.Empty
                OrganizationType = OrganizationType.Public
                Description = String.Empty
                SearchVisibility = Visible
                Repositories = new Dictionary<RepositoryId, RepositoryName>()
                CreatedAt = getCurrentInstant()
                UpdatedAt = None
                DeletedAt = None
                DeleteReason = String.Empty
            }

    module Owner =
        [<Serializable>]
        type OwnerDto = 
            {
                Class: string
                OwnerId: OwnerId
                OwnerName: OwnerName
                OwnerType: OwnerType
                Description: string
                SearchVisibility: SearchVisibility
                Organizations: Dictionary<OrganizationId, OrganizationName>
                CreatedAt: Instant
                UpdatedAt: Instant option
                DeletedAt: Instant option
                DeleteReason: string
            }
            static member Default = {
                Class = nameof(OwnerDto)
                OwnerId = OwnerId.Empty
                OwnerName = String.Empty
                OwnerType = OwnerType.Public
                Description = String.Empty
                SearchVisibility = Visible
                Organizations = new Dictionary<OrganizationId, OrganizationName>()
                CreatedAt = getCurrentInstant()
                UpdatedAt = None
                DeletedAt = None
                DeleteReason = String.Empty
            }

    module Reference =
        [<Serializable>]
        type ReferenceDto =
            {
                Class: string
                ReferenceId: ReferenceId
                BranchId: BranchId
                DirectoryId: DirectoryId
                Sha256Hash: Sha256Hash
                ReferenceType: ReferenceType
                ReferenceText: ReferenceText
                CreatedAt: Instant
            }
            static member Default = {
                Class = nameof(ReferenceDto)
                ReferenceId = ReferenceId.Empty
                BranchId = BranchId.Empty
                DirectoryId = DirectoryId.Empty
                Sha256Hash = Sha256Hash String.Empty
                ReferenceType = Save 
                ReferenceText = ReferenceText String.Empty
                CreatedAt = getCurrentInstant()
            }

    module Repository =
        [<Serializable>]
        type RepositoryDto = 
            {
                Class: string
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
                SaveDays: double
                CheckpointDays: double
                Description: string
                RecordSaves: bool
                CreatedAt: Instant
                InitializedAt: Instant option
                UpdatedAt: Instant option
                DeletedAt: Instant option
                DeleteReason: string
            }

            static member Default = {
                Class = nameof(RepositoryDto)
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
                SaveDays = 7.0
                CheckpointDays = 365.0
                Description = String.Empty
                RecordSaves = true
                CreatedAt = getCurrentInstant()
                InitializedAt = None
                UpdatedAt = None
                DeletedAt = None
                DeleteReason = String.Empty
                }
