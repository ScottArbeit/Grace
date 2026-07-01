namespace Grace.Types

open Grace.Shared.Utilities
open Grace.Types.Branch
open Grace.Types.Diff
open Grace.Types.DirectoryVersion
open Grace.Types.Organization
open Grace.Types.Owner
open Grace.Types.Reference
open Grace.Types.Repository
open Grace.Types.Common
open Grace.Types.UploadSession
open NodaTime
open Orleans
open System

/// Contains reminder helpers.
module Reminder =

    /// Represents reminder state.
    [<GenerateSerializer>]
    type ReminderState =
        | EmptyReminderState
        | OwnerPhysicalDeletion of Owner.PhysicalDeletionReminderState
        | OrganizationPhysicalDeletion of Organization.PhysicalDeletionReminderState
        | RepositoryPhysicalDeletion of Repository.PhysicalDeletionReminderState
        | BranchPhysicalDeletion of Branch.PhysicalDeletionReminderState
        | ReferencePhysicalDeletion of Reference.PhysicalDeletionReminderState
        | DirectoryVersionPhysicalDeletion of DirectoryVersion.PhysicalDeletionReminderState
        | DirectoryVersionDeleteCachedState of DirectoryVersion.PhysicalDeletionReminderState
        | DirectoryVersionDeleteZipFile of DirectoryVersion.PhysicalDeletionReminderState
        | DiffDeleteCachedState of Diff.DeleteCachedStateReminderState
        | UploadSessionPhysicalDeletion of UploadSession.PhysicalDeletionReminderState

    /// Defines all reminders used in Grace.
    type ReminderDto =
        {
            Class: string
            ReminderId: ReminderId
            ActorName: string
            ActorId: string
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            ReminderType: ReminderTypes
            CreatedAt: Instant
            ReminderTime: Instant
            CorrelationId: CorrelationId
            State: ReminderState
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof ReminderDto
                ReminderId = ReminderId.Empty
                ActorName = String.Empty
                ActorId = String.Empty
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                ReminderType = ReminderTypes.Maintenance // This is the default because something has to be; there's no significance to it.
                CreatedAt = Instant.MinValue
                ReminderTime = Instant.MinValue
                CorrelationId = String.Empty
                State = ReminderState.EmptyReminderState
            }

        /// Builds a ReminderDto from the validated inputs used by this contract.
        static member Create actorName actorId ownerId organizationId repositoryId reminderType reminderTime state correlationId =
            {
                Class = nameof ReminderDto
                ReminderId = ReminderId.NewGuid()
                ActorName = actorName
                ActorId = actorId
                OwnerId = ownerId
                OrganizationId = organizationId
                RepositoryId = repositoryId
                ReminderType = reminderType
                CreatedAt = getCurrentInstant ()
                ReminderTime = reminderTime
                CorrelationId = correlationId
                State = state
            }

        /// Returns the display representation for this value.
        override this.ToString() = serialize this

    /// Represents reminder wrapper.
    type ReminderWrapper() =
        member val public Reminder: ReminderDto = ReminderDto.Default with get, set
        /// Returns the display representation for this value.
        override this.ToString() = serialize this
