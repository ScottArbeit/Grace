namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Reminder
open Grace.Shared.Utilities
open Grace.Types.Reminder
open Grace.Types.Types
open System
open System.Collections.Generic
open System.Threading.Tasks

type Reminder() =

    /// <summary>
    /// Lists reminders for a repository with optional filters.
    /// </summary>
    /// <param name="parameters">Values to use when listing reminders.</param>
    static member public List(parameters: ListRemindersParameters) =
        postServer<ListRemindersParameters, IEnumerable<ReminderDto>> (parameters |> ensureCorrelationIdIsSet, $"reminder/{nameof (Reminder.List)}")

    /// <summary>
    /// Gets a specific reminder by its ID.
    /// </summary>
    /// <param name="parameters">Values to use when getting the reminder.</param>
    static member public Get(parameters: GetReminderParameters) =
        postServer<GetReminderParameters, ReminderDto> (parameters |> ensureCorrelationIdIsSet, $"reminder/{nameof (Reminder.Get)}")

    /// <summary>
    /// Deletes a reminder.
    /// </summary>
    /// <param name="parameters">Values to use when deleting the reminder.</param>
    static member public Delete(parameters: DeleteReminderParameters) =
        postServer<DeleteReminderParameters, String> (parameters |> ensureCorrelationIdIsSet, $"reminder/{nameof (Reminder.Delete)}")

    /// <summary>
    /// Updates the fire time for a reminder.
    /// </summary>
    /// <param name="parameters">Values to use when updating the reminder.</param>
    static member public UpdateTime(parameters: UpdateReminderTimeParameters) =
        postServer<UpdateReminderTimeParameters, String> (parameters |> ensureCorrelationIdIsSet, $"reminder/{nameof (Reminder.UpdateTime)}")

    /// <summary>
    /// Reschedules a reminder relative to now.
    /// </summary>
    /// <param name="parameters">Values to use when rescheduling the reminder.</param>
    static member public Reschedule(parameters: RescheduleReminderParameters) =
        postServer<RescheduleReminderParameters, String> (parameters |> ensureCorrelationIdIsSet, $"reminder/{nameof (Reminder.Reschedule)}")

    /// <summary>
    /// Creates a new manual reminder.
    /// </summary>
    /// <param name="parameters">Values to use when creating the reminder.</param>
    static member public Create(parameters: CreateReminderParameters) =
        postServer<CreateReminderParameters, String> (parameters |> ensureCorrelationIdIsSet, $"reminder/{nameof (Reminder.Create)}")
