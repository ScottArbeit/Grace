namespace Grace.Actors

open Grace.Shared.Types
open System
open System.Collections.Concurrent

module Constants =
    
    module ActorName =
        let Branch = "BranchActor"
        let BranchName = "BranchNameActor"
        let Checkpoint = "CheckpointActor"
        let ContainerName = "ContainerNameActor"
        let Diff = "DiffActor"
        let DirectoryVersion = "DirectoryVersionActor"
        let DirectoryAppearance = "DirectoryAppearanceActor"
        let FileAppearance = "FileAppearanceActor"
        let Organization = "OrganizationActor"
        let OrganizationName = "OrganizationNameActor"
        let Owner = "OwnerActor"
        let OwnerName = "OwnerNameActor"
        let NamedSection = "NamedSectionActor"
        let Reference = "ReferenceActor"
        let Repository = "RepositoryActor"
        let RepositoryName = "RepositoryNameActor"
        let RepositoryPermission = "RepositoryPermissionActor"
        let Save = "SaveActor"
        let Tag = "TagActor"
        let User = "UserActor"

    let defaultObjectStorageProvider = ObjectStorageProvider.AzureBlobStorage
    let defaultObjectStorageAccount = "gracevcsdevelopment"
    let defaultObjectStorageContainerName = "grace-objects"
