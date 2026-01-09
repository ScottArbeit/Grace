namespace Grace.Actors

open Grace.Shared
open Grace.Types.Types
open Grace.Shared.Utilities
open NodaTime
open System
open System.Collections.Concurrent

module Constants =

    /// Constants for the names of the actors.
    ///
    /// These names should exactly match the actors' typenames.
    module ActorName =
        [<Literal>]
        let AccessControl = "AccessControlActor"

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
        let IntegrationCandidate = "IntegrationCandidateActor"

        [<Literal>]
        let GateAttestation = "GateAttestationActor"

        [<Literal>]
        let ConflictReceipt = "ConflictReceiptActor"

        [<Literal>]
        let GrainRepository = "GrainRepository"

        [<Literal>]
        let Notification = "NotificationActor"

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
        let PersonalAccessToken = "PersonalAccessTokenActor"

        [<Literal>]
        let PromotionGroup = "PromotionGroupActor"

        [<Literal>]
        let PromotionQueue = "PromotionQueueActor"

        [<Literal>]
        let Policy = "PolicyActor"

        [<Literal>]
        let Review = "ReviewActor"

        [<Literal>]
        let Stage0 = "Stage0Actor"

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

        [<Literal>]
        let WorkItem = "WorkItemActor"

    module StateName =
        [<Literal>]
        let AccessControl = "AccessControl"

        [<Literal>]
        let Branch = "Branch"

        [<Literal>]
        let ConflictReceipt = "ConflictReceipt"

        [<Literal>]
        let Diff = "Diff"

        [<Literal>]
        let DirectoryAppearance = "DirApp"

        [<Literal>]
        let DirectoryVersion = "Dir"

        [<Literal>]
        let FileAppearance = "FileApp"

        [<Literal>]
        let GateAttestation = "GateAttestation"

        [<Literal>]
        let IntegrationCandidate = "IntegrationCandidate"

        [<Literal>]
        let NamedSection = "NamedSection"

        [<Literal>]
        let Organization = "Organization"

        [<Literal>]
        let Owner = "Owner"

        [<Literal>]
        let PersonalAccessToken = "PersonalAccessToken"

        [<Literal>]
        let PromotionGroup = "PromotionGroup"

        [<Literal>]
        let PromotionQueue = "PromotionQueue"

        [<Literal>]
        let Policy = "Policy"

        [<Literal>]
        let Review = "Review"

        [<Literal>]
        let Stage0 = "Stage0"

        [<Literal>]
        let Reference = "Ref"

        [<Literal>]
        let Reminder = "Rmd"

        [<Literal>]
        let Repository = "Repo"

        [<Literal>]
        let RepositoryPermission = "RepoPermission"

        [<Literal>]
        let User = "User"

        [<Literal>]
        let WorkItem = "WorkItem"

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
