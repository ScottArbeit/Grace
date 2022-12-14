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
                EnabledMerge: bool
                EnabledCommit: bool
                EnabledCheckpoint: bool
                EnabledSave: bool
                EnabledTag: bool
                LatestCommit: ReferenceId
                LatestMerge: ReferenceId
                CreatedAt: Instant
                UpdatedAt: Instant option
                DeletedAt: Instant option
            }
            static member Default = {
                Class = "BranchDto"
                BranchId = BranchId.Empty
                BranchName = BranchName String.Empty
                ParentBranchId = Constants.DefaultParentBranchId
                BasedOn = ReferenceId.Empty
                RepositoryId = RepositoryId.Empty
                UserId = UserId String.Empty
                //References = SortedList<Instant, ReferenceDto>()
                EnabledMerge = true
                EnabledCommit = true
                EnabledCheckpoint = true
                EnabledSave = true
                EnabledTag = true
                LatestCommit = ReferenceId.Empty
                LatestMerge = ReferenceId.Empty
                CreatedAt = getCurrentInstant()
                UpdatedAt = None
                DeletedAt = None
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
                Class = "DiffDto"
                HasDifferences = false
                DirectoryId1 = DirectoryId.Empty
                Directory1CreatedAt = Instant.MinValue
                DirectoryId2 = DirectoryId.Empty
                Directory2CreatedAt = Instant.MinValue
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
                Class = "OrganizationDto"
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
                Class = "OwnerDto"
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
                Class = "ReferenceDto"
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
                //Tags: SortedSet<TagName>
                DefaultServerApiVersion: string
                DefaultBranchName: BranchName
                SaveDays: float
                CheckpointDays: float
                EnabledSingleStepMerge: bool
                EnabledComplexMerge: bool
                Description: string
                RecordSaves: bool
                CreatedAt: Instant
                UpdatedAt: Instant option
                DeletedAt: Instant option
                DeleteReason: string
            }

            static member Default = {
                Class = "RepositoryDto"
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
                //Tags = SortedSet<TagName>() 
                DefaultServerApiVersion = "latest"
                DefaultBranchName = BranchName Constants.InitialBranchName
                SaveDays = 7.0
                CheckpointDays = 0.0
                EnabledSingleStepMerge = true
                EnabledComplexMerge = false
                Description = String.Empty
                RecordSaves  = true
                CreatedAt = getCurrentInstant()
                UpdatedAt = None
                DeletedAt = None
                DeleteReason = String.Empty
                }
