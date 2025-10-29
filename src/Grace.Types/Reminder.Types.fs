namespace Grace.Types

open Grace.Shared.Utilities
open Grace.Types.Branch
open Grace.Types.Diff
open Grace.Types.DirectoryVersion
open Grace.Types.Organization
open Grace.Types.Owner
open Grace.Types.Reference
open Grace.Types.Repository
open Grace.Types.Types
open NodaTime
open Orleans
open System

module Reminder =

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

    /// Defines all reminders used in Grace.
    type ReminderDto =
        { Class: string
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
          State: ReminderState }

        static member Default =
            { Class = nameof ReminderDto
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
              State = ReminderState.EmptyReminderState }

        /// Creates a ReminderDto.
        static member Create actorName actorId ownerId organizationId repositoryId reminderType reminderTime state correlationId =
            { Class = nameof ReminderDto
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
              State = state }

        override this.ToString() = serialize this

    type ReminderWrapper() =
        member val public Reminder: ReminderDto = ReminderDto.Default with get, set
        override this.ToString() = serialize this
