namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.Types
open NodaTime
open System
open System.Collections.Generic

module Reminder =

    /// Base parameters for reminder admin endpoints - scoped by repository.
    type ReminderParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set

    /// Parameters for listing reminders.
    type ListRemindersParameters() =
        inherit ReminderParameters()
        /// Maximum number of reminders to return.
        member val public MaxCount: int = 100 with get, set
        /// Filter by reminder type (e.g., Maintenance, PhysicalDeletion).
        member val public ReminderType = String.Empty with get, set
        /// Filter by actor name (e.g., Branch, Repository).
        member val public ActorName = String.Empty with get, set
        /// Filter by status (pending, dispatched, failed).
        member val public Status = String.Empty with get, set
        /// Filter by reminders due after this time (ISO8601).
        member val public DueAfter = String.Empty with get, set
        /// Filter by reminders due before this time (ISO8601).
        member val public DueBefore = String.Empty with get, set

    /// Parameters for getting a single reminder.
    type GetReminderParameters() =
        inherit ReminderParameters()
        /// The unique ID of the reminder to get.
        member val public ReminderId = String.Empty with get, set

    /// Parameters for deleting a reminder.
    type DeleteReminderParameters() =
        inherit ReminderParameters()
        /// The unique ID of the reminder to delete.
        member val public ReminderId = String.Empty with get, set

    /// Parameters for updating a reminder's fire time.
    type UpdateReminderTimeParameters() =
        inherit ReminderParameters()
        /// The unique ID of the reminder to update.
        member val public ReminderId = String.Empty with get, set
        /// The new fire time in ISO8601 format.
        member val public FireAt = String.Empty with get, set

    /// Parameters for rescheduling a reminder relative to now or original time.
    type RescheduleReminderParameters() =
        inherit ReminderParameters()
        /// The unique ID of the reminder to reschedule.
        member val public ReminderId = String.Empty with get, set
        /// The duration to add (e.g., +15m, +1h, +1d). Relative to now.
        member val public After = String.Empty with get, set

    /// Parameters for creating a manual reminder for testing/tooling.
    type CreateReminderParameters() =
        inherit ReminderParameters()
        /// The target actor name (e.g., Branch, Repository, Owner).
        member val public ActorName = String.Empty with get, set
        /// The target actor ID.
        member val public ActorId = String.Empty with get, set
        /// The type of reminder (Maintenance, PhysicalDeletion, DeleteCachedState, DeleteZipFile).
        member val public ReminderType = String.Empty with get, set
        /// When the reminder should fire (ISO8601 format).
        member val public FireAt = String.Empty with get, set
        /// Optional JSON payload for the reminder state.
        member val public StateJson = String.Empty with get, set
