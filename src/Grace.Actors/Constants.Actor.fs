namespace Grace.Actors

open Grace.Shared.Types
open System
open System.Collections.Concurrent

module Constants =

    /// Constants for the names of the actors.
    module ActorName =
        [<Literal>]
        let Branch = "BranchActor"

        [<Literal>]
        let BranchName = "BranchNameActor"

        [<Literal>]
        let Checkpoint = "CheckpointActor"

        [<Literal>]
        let ContainerName = "ContainerNameActor"

        [<Literal>]
        let Diff = "DiffActor"

        [<Literal>]
        let DirectoryVersion = "DirectoryVersionActor"

        [<Literal>]
        let DirectoryAppearance = "DirectoryAppearanceActor"

        [<Literal>]
        let FileAppearance = "FileAppearanceActor"

        [<Literal>]
        let Organization = "OrganizationActor"

        [<Literal>]
        let OrganizationName = "OrganizationNameActor"

        [<Literal>]
        let Owner = "OwnerActor"

        [<Literal>]
        let OwnerName = "OwnerNameActor"

        [<Literal>]
        let NamedSection = "NamedSectionActor"

        [<Literal>]
        let Reference = "ReferenceActor"

        [<Literal>]
        let Repository = "RepositoryActor"

        [<Literal>]
        let RepositoryName = "RepositoryNameActor"

        [<Literal>]
        let RepositoryPermission = "RepositoryPermissionActor"

        [<Literal>]
        let User = "UserActor"

    /// Constants for the different types of reminders.
    module ReminderType =
        [<Literal>]
        let Maintenance = "Maintenance"

        [<Literal>]
        let PhysicalDeletion = "PhysicalDeletion"

        [<Literal>]
        let DeleteCachedState = "DeleteCachedState"

    let DefaultObjectStorageProvider = ObjectStorageProvider.AzureBlobStorage
    let DefaultObjectStorageAccount = "gracevcsdevelopment"
    let DefaultObjectStorageContainerName = "grace-objects"

    /// The time to wait between logical and physical deletion of an actor's state.
    ///
    /// In Release builds, this is TimeSpan.FromDays(7.0). In Debug builds, it's TimeSpan.FromSeconds(30.0).
#if DEBUG
    let DefaultPhysicalDeletionReminderTime = TimeSpan.FromSeconds(30.0)
#else
    let DefaultPhysicalDeletionReminderTime = TimeSpan.FromDays(7.0)
#endif
