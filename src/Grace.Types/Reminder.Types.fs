namespace Grace.Types

open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open System
open Orleans

module Reminder =

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
          State: obj }

        static member Default =
            { Class = nameof (ReminderDto)
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
              State = obj () }

        /// Creates a ReminderDto.
        static member Create actorName actorId ownerId organizationId repositoryId reminderType reminderTime state correlationId =
            { Class = nameof (ReminderDto)
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
