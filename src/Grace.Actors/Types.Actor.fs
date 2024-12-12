namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Shared.Types
open Grace.Shared.Utilities
open NodaTime
open System
open System.Runtime.Serialization

module Types =

    /// Defines all reminders used in Grace.
    [<Serializable>]
    [<KnownType("GetKnownTypes")>]
    type ReminderDto =
        { Class: string
          ReminderId: ReminderId
          ActorName: string
          ActorId: string
          ReminderType: ReminderTypes
          CreatedAt: Instant
          ReminderTime: Instant
          CorrelationId: CorrelationId
          State: string }

        static member Default =
            { Class = nameof (ReminderDto)
              ReminderId = ReminderId.Empty
              ActorName = String.Empty
              ActorId = String.Empty
              ReminderType = ReminderTypes.Maintenance // This is the default because something has to be; there's no significance to it.
              CreatedAt = Instant.MinValue
              ReminderTime = Instant.MinValue
              CorrelationId = String.Empty
              State = String.Empty }

        /// Creates a ReminderDto.
        static member Create actorName actorId reminderType reminderTime state correlationId =
            { Class = nameof (ReminderDto)
              ReminderId = ReminderId.NewGuid()
              ActorName = actorName
              ActorId = actorId
              ReminderType = reminderType
              CreatedAt = getCurrentInstant ()
              ReminderTime = reminderTime
              CorrelationId = correlationId
              State = state }

        override this.ToString() = serialize this
        static member GetKnownTypes() = GetKnownTypes<ReminderDto>()

    type TimingFlag =
        | Initial
        | BeforeRetrieveState
        | AfterRetrieveState
        | BeforeSaveState
        | AfterSaveState
        | BeforeStorageQuery
        | AfterStorageQuery
        | BeforeGettingCorrelationIdFromMemoryCache
        | AfterGettingCorrelationIdFromMemoryCache
        | BeforeSettingCorrelationIdInMemoryCache
        | AfterSettingCorrelationIdInMemoryCache
        | Final

    type Timing =
        { Time: Instant
          ActorStateName: string
          Flag: TimingFlag }

        static member Create (flag: TimingFlag) actorStateName = { Time = getCurrentInstant (); ActorStateName = actorStateName; Flag = flag }
