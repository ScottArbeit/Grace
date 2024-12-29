namespace Grace.Actors

open Grace.Shared
open Grace.Shared.Types
open Grace.Shared.Utilities
open NodaTime
open System
open System.Collections.Concurrent
open System.Runtime.Serialization

module Constants =

    /// Constants for the names of the actors.
    ///
    /// These names should exactly match the actors' typenames.
    module ActorName =
        [<Literal>]
        let Branch = "BranchActor"

        [<Literal>]
        let BranchName = "BranchNameActor"

        [<Literal>]
        let Checkpoint = "CheckpointActor"

        [<Literal>]
        let Diff = "DiffActor"

        [<Literal>]
        let DirectoryVersion = "DirectoryVersionActor"

        [<Literal>]
        let DirectoryAppearance = "DirectoryAppearanceActor"

        [<Literal>]
        let FileAppearance = "FileAppearanceActor"

        [<Literal>]
        let GlobalLock = "GlobalLockActor"

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
        let Reminder = "ReminderActor"

        [<Literal>]
        let Repository = "RepositoryActor"

        [<Literal>]
        let RepositoryName = "RepositoryNameActor"

        [<Literal>]
        let RepositoryPermission = "RepositoryPermissionActor"

        [<Literal>]
        let User = "UserActor"

    module StateName =
        [<Literal>]
        let Branch = "Branch"

        [<Literal>]
        let ContainerName = "ContainerName"

        [<Literal>]
        let Diff = "Diff"

        [<Literal>]
        let DirectoryAppearance = "DirApp"

        [<Literal>]
        let DirectoryVersionCache = "DirCache"

        [<Literal>]
        let DirectoryVersion = "Dir"

        [<Literal>]
        let FileAppearance = "FileApp"

        [<Literal>]
        let NamedSection = "NamedSection"

        [<Literal>]
        let Organization = "Organization"

        [<Literal>]
        let OrganizationDto = "OrganizationDto"

        [<Literal>]
        let Owner = "Owner"

        [<Literal>]
        let OwnerDto = "OwnerDto"

        [<Literal>]
        let Reference = "Ref"

        [<Literal>]
        let Reminder = "Rmd"

        [<Literal>]
        let Repository = "Repo"

        [<Literal>]
        let RepositoryDto = "RepoDto"

        [<Literal>]
        let RepositoryPermission = "RepoPermission"

        [<Literal>]
        let User = "User"

    /// Constants for the different types of reminders.
    module ReminderType =
        [<Literal>]
        let Maintenance = "Maintenance"

        [<Literal>]
        let PhysicalDeletion = "PhysicalDeletion"

        [<Literal>]
        let DeleteCachedState = "DeleteCachedState"

    module LockName =
        [<Literal>]
        let ReminderLock = "ReminderLock"

    let DefaultObjectStorageProvider = ObjectStorageProvider.AzureBlobStorage
    let DefaultObjectStorageAccount = "gracevcsdevelopment"
    let DefaultObjectStorageContainerName = "grace-objects"

    /// The time to wait between logical and physical deletion of an actor's state.
    ///
    /// In Release builds, this is TimeSpan.FromDays(7.0). In Debug builds, it's TimeSpan.FromSeconds(300.0).
#if DEBUG
    let DefaultPhysicalDeletionReminderDuration = Duration.FromSeconds(300.0)
#else
    let DefaultPhysicalDeletionReminderDuration = Duration.FromDays(7.0)
#endif

    /// The time to wait between logical and physical deletion of an actor's state, as a TimeSpan.
    let DefaultPhysicalDeletionReminderTimeSpan = DefaultPhysicalDeletionReminderDuration.ToTimeSpan()
