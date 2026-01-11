namespace Grace.Server

open FSharpPlus
open Giraffe
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Extensions
open Grace.Shared.Parameters.Reminder
open Grace.Types.Reminder
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Logging
open NodaTime
open NodaTime.Text
open OpenTelemetry.Trace
open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.Linq
open System.Threading.Tasks
open System.Text
open System.Text.Json

module Reminder =

    type Validations<'T when 'T :> ReminderParameters> = 'T -> ValueTask<Result<unit, ReminderError>> array

    let activitySource = new ActivitySource("Reminder")

    let log = ApplicationContext.loggerFactory.CreateLogger("Reminder.Server")

    let processQuery<'T, 'U when 'T :> ReminderParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (query: HttpContext -> 'T -> Task<'U>)
        =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = getParametersAsDictionary parameters

            try
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let! queryResult = query context parameters

                    let graceReturnValue =
                        (GraceReturnValue.Create queryResult correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError

                    let graceError =
                        (GraceError.Create (ReminderError.getErrorMessage error.Value) correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with
            | ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Exception in Reminder.Server.processQuery; Path: {path}; CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    context.Request.Path,
                    correlationId
                )

                let graceError =
                    (GraceError.Create $"{ExceptionResponse.Create ex}" correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    let processCommand<'T when 'T :> ReminderParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (command: HttpContext -> 'T -> Task<Result<string, string>>)
        =
        task {
            use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = getParametersAsDictionary parameters

            try
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let! commandResult = command context parameters

                    match commandResult with
                    | Ok message ->
                        let graceReturnValue =
                            (GraceReturnValue.Create message correlationId)
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance ("Path", context.Request.Path.Value)

                        return! context |> result200Ok graceReturnValue
                    | Error errorMessage ->
                        let graceError =
                            (GraceError.Create errorMessage correlationId)
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance ("Path", context.Request.Path.Value)

                        return! context |> result400BadRequest graceError
                else
                    let! error = validationResults |> getFirstError

                    let graceError =
                        (GraceError.Create (ReminderError.getErrorMessage error.Value) correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with
            | ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Exception in Reminder.Server.processCommand; Path: {path}; CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    context.Request.Path,
                    correlationId
                )

                let graceError =
                    (GraceError.Create $"{ExceptionResponse.Create ex}" correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    /// Lists reminders for a repository with optional filters.
    let List: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: ListRemindersParameters) = [||]

                    let query (context: HttpContext) (parameters: ListRemindersParameters) =
                        task {
                            let correlationId = getCorrelationId context
                            let graceIds = getGraceIds context

                            // Parse optional filters
                            let reminderTypeFilter =
                                if String.IsNullOrEmpty(parameters.ReminderType) then
                                    None
                                else
                                    Some parameters.ReminderType

                            let actorNameFilter =
                                if String.IsNullOrEmpty(parameters.ActorName) then
                                    None
                                else
                                    Some parameters.ActorName

                            let dueAfter =
                                if String.IsNullOrEmpty(parameters.DueAfter) then
                                    None
                                else
                                    let parseResult = InstantPattern.ExtendedIso.Parse(parameters.DueAfter)

                                    if parseResult.Success then Some parseResult.Value else None

                            let dueBefore =
                                if String.IsNullOrEmpty(parameters.DueBefore) then
                                    None
                                else
                                    let parseResult = InstantPattern.ExtendedIso.Parse(parameters.DueBefore)

                                    if parseResult.Success then Some parseResult.Value else None

                            return! getReminders graceIds parameters.MaxCount reminderTypeFilter actorNameFilter dueAfter dueBefore correlationId
                        }

                    let! parameters = context |> parse<ListRemindersParameters>
                    parameters.RepositoryId <- graceIds.RepositoryIdString
                    let! result = processQuery context parameters validations query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Gets a specific reminder by its ID.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetReminderParameters) =
                        [|
                            String.isNotEmpty parameters.ReminderId ReminderIdIsRequired
                        |]

                    let query (context: HttpContext) (parameters: GetReminderParameters) =
                        task {
                            let correlationId = getCorrelationId context
                            let reminderId = Guid.Parse(parameters.ReminderId)
                            let! reminderOption = getReminderById reminderId correlationId

                            return
                                match reminderOption with
                                | Some reminder -> reminder
                                | None -> ReminderDto.Default
                        }

                    let! parameters = context |> parse<GetReminderParameters>
                    let! result = processQuery context parameters validations query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; ReminderId: {reminderId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        parameters.ReminderId
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Deletes a reminder.
    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: DeleteReminderParameters) =
                        [|
                            String.isNotEmpty parameters.ReminderId ReminderIdIsRequired
                        |]

                    let command (context: HttpContext) (parameters: DeleteReminderParameters) =
                        task {
                            let correlationId = getCorrelationId context
                            let reminderId = Guid.Parse(parameters.ReminderId)
                            let! result = deleteReminder reminderId correlationId

                            return
                                match result with
                                | Ok () -> Ok "Reminder deleted successfully."
                                | Error msg -> Error msg
                        }

                    let! parameters = context |> parse<DeleteReminderParameters>
                    let! result = processCommand context parameters validations command

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; ReminderId: {reminderId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        parameters.ReminderId
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Updates the fire time for a reminder.
    let UpdateTime: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: UpdateReminderTimeParameters) =
                        [|
                            String.isNotEmpty parameters.ReminderId ReminderIdIsRequired
                            String.isNotEmpty parameters.FireAt InvalidReminderTime
                        |]

                    let command (context: HttpContext) (parameters: UpdateReminderTimeParameters) =
                        task {
                            let correlationId = getCorrelationId context
                            let reminderId = Guid.Parse(parameters.ReminderId)

                            // Parse the new fire time
                            let parseResult = InstantPattern.ExtendedIso.Parse(parameters.FireAt)

                            if parseResult.Success then
                                let newFireTime = parseResult.Value
                                let! reminderOption = getReminderById reminderId correlationId

                                match reminderOption with
                                | Some existingReminder ->
                                    // Delete the old reminder and create a new one with updated time
                                    let! _ = deleteReminder reminderId correlationId

                                    let newReminder = { existingReminder with ReminderTime = newFireTime; CorrelationId = correlationId }

                                    do! createReminder newReminder
                                    return Ok "Reminder time updated successfully."
                                | None -> return Error "Reminder not found."
                            else
                                return Error $"Invalid fire time format: {parameters.FireAt}. Use ISO8601 format."
                        }

                    let! parameters = context |> parse<UpdateReminderTimeParameters>
                    let! result = processCommand context parameters validations command

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; ReminderId: {reminderId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        parameters.ReminderId
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Parses a duration string like "+15m", "+1h", "+1d" and returns a Duration.
    let parseDuration (durationString: string) =
        if String.IsNullOrEmpty(durationString) then
            None
        else
            try
                let normalized =
                    if durationString.StartsWith("+") then
                        durationString.Substring(1)
                    else
                        durationString

                let lastChar = normalized[normalized.Length - 1]
                let numericPart = normalized.Substring(0, normalized.Length - 1)
                let mutable value = 0.0

                if Double.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, &value) then
                    match lastChar with
                    | 's' -> Some(Duration.FromSeconds(value))
                    | 'm' -> Some(Duration.FromMinutes(value))
                    | 'h' -> Some(Duration.FromHours(value))
                    | 'd' -> Some(Duration.FromDays(value))
                    | _ -> None
                else
                    None
            with
            | _ -> None

    /// Reschedules a reminder relative to now.
    let Reschedule: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: RescheduleReminderParameters) =
                        [|
                            String.isNotEmpty parameters.ReminderId ReminderIdIsRequired
                            String.isNotEmpty parameters.After InvalidReminderDuration
                        |]

                    let command (context: HttpContext) (parameters: RescheduleReminderParameters) =
                        task {
                            let correlationId = getCorrelationId context
                            let reminderId = Guid.Parse(parameters.ReminderId)

                            match parseDuration parameters.After with
                            | Some duration ->
                                let newFireTime = getCurrentInstant () + duration
                                let! reminderOption = getReminderById reminderId correlationId

                                match reminderOption with
                                | Some existingReminder ->
                                    // Delete the old reminder and create a new one with updated time
                                    let! _ = deleteReminder reminderId correlationId

                                    let newReminder = { existingReminder with ReminderTime = newFireTime; CorrelationId = correlationId }

                                    do! createReminder newReminder

                                    return Ok $"Reminder rescheduled to {formatInstantExtended newFireTime}."
                                | None -> return Error "Reminder not found."
                            | None -> return Error $"Invalid duration format: {parameters.After}. Use formats like '+15m', '+1h', '+1d'."
                        }

                    let! parameters = context |> parse<RescheduleReminderParameters>
                    let! result = processCommand context parameters validations command

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; ReminderId: {reminderId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        parameters.ReminderId
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Creates a new manual reminder.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: CreateReminderParameters) =
                        [|
                            String.isNotEmpty parameters.ActorName ReminderActorNameIsRequired
                            String.isNotEmpty parameters.ActorId ReminderActorIdIsRequired
                            String.isNotEmpty parameters.ReminderType InvalidReminderType
                            String.isNotEmpty parameters.FireAt InvalidReminderTime
                        |]

                    let command (context: HttpContext) (parameters: CreateReminderParameters) =
                        task {
                            let correlationId = getCorrelationId context

                            // Parse the fire time
                            let parseResult = InstantPattern.ExtendedIso.Parse(parameters.FireAt)

                            if parseResult.Success then
                                let fireTime = parseResult.Value

                                // Parse the reminder type
                                let reminderTypeOption = discriminatedUnionFromString<ReminderTypes> parameters.ReminderType

                                match reminderTypeOption with
                                | Some reminderType ->
                                    let reminderDto =
                                        ReminderDto.Create
                                            parameters.ActorName
                                            parameters.ActorId
                                            graceIds.OwnerId
                                            graceIds.OrganizationId
                                            graceIds.RepositoryId
                                            reminderType
                                            fireTime
                                            ReminderState.EmptyReminderState
                                            correlationId

                                    do! createReminder reminderDto

                                    return Ok $"Reminder created with ID: {reminderDto.ReminderId}."
                                | None -> return Error $"Invalid reminder type: {parameters.ReminderType}."
                            else
                                return Error $"Invalid fire time format: {parameters.FireAt}. Use ISO8601 format."
                        }

                    let! parameters = context |> parse<CreateReminderParameters>
                    let! result = processCommand context parameters validations command

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }
