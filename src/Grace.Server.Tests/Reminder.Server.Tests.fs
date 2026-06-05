namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types
open Grace.Types.Common
open Grace.Types.Reminder
open NodaTime
open NodaTime.Text
open NUnit.Framework
open System
open System.Net
open System.Net.Http
open System.Text.RegularExpressions

module ReminderServerTestHelpers =
    let formatInstant (instant: Instant) = InstantPattern.ExtendedIso.Format instant

    let createParameters (actorName: string) (actorId: string) (reminderType: string) (fireAt: Instant) =
        let parameters = Parameters.Reminder.CreateReminderParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryIds[0]
        parameters.ActorName <- actorName
        parameters.ActorId <- actorId
        parameters.ReminderType <- reminderType
        parameters.FireAt <- formatInstant fireAt
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let listParameters (actorName: string) (reminderType: string) =
        let parameters = Parameters.Reminder.ListRemindersParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryIds[0]
        parameters.ActorName <- actorName
        parameters.ReminderType <- reminderType
        parameters.MaxCount <- 100
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let getParameters (reminderId: string) =
        let parameters = Parameters.Reminder.GetReminderParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryIds[0]
        parameters.ReminderId <- $"{reminderId}"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let updateTimeParameters (reminderId: ReminderId) (fireAt: Instant) =
        let parameters = Parameters.Reminder.UpdateReminderTimeParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryIds[0]
        parameters.ReminderId <- $"{reminderId}"
        parameters.FireAt <- formatInstant fireAt
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let rescheduleParameters (reminderId: ReminderId) (after: string) =
        let parameters = Parameters.Reminder.RescheduleReminderParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryIds[0]
        parameters.ReminderId <- $"{reminderId}"
        parameters.After <- after
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let deleteParameters (reminderId: ReminderId) =
        let parameters = Parameters.Reminder.DeleteReminderParameters()
        parameters.OwnerId <- ownerId
        parameters.OrganizationId <- organizationId
        parameters.RepositoryId <- repositoryIds[0]
        parameters.ReminderId <- $"{reminderId}"
        parameters.CorrelationId <- generateCorrelationId ()
        parameters

    let assertOk (response: HttpResponseMessage) =
        task {
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)
            Assert.That(response.Content.Headers.ContentType.MediaType, Is.EqualTo("application/json"))
        }

    let extractReminderId (message: string) : ReminderId =
        let matchResult = Regex.Match(message, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")

        if matchResult.Success then
            Guid.Parse(matchResult.Value)
        else
            Assert.Fail($"Could not find a reminder id in message: {message}")
            Guid.Empty

    let createReminderAsync (actorName: string) (actorId: string) (reminderType: string) (fireAt: Instant) =
        task {
            let! response = Client.PostAsync("/reminder/create", createJsonContent (createParameters actorName actorId reminderType fireAt))
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<string>> response
            return extractReminderId returnValue.ReturnValue
        }

    let getReminderAsync (reminderId: ReminderId) =
        task {
            let! response = Client.PostAsync("/reminder/get", createJsonContent (getParameters $"{reminderId}"))
            do! assertOk response
            let! returnValue = deserializeContent<GraceReturnValue<ReminderDto>> response
            return returnValue.ReturnValue
        }

    let assertReminder
        (expectedReminderId: ReminderId)
        (expectedActorName: string)
        (expectedActorId: string)
        (expectedReminderType: ReminderTypes)
        (expectedTime: Instant)
        (reminder: ReminderDto)
        =
        Assert.That(reminder.ReminderId, Is.EqualTo(expectedReminderId))
        Assert.That(reminder.ActorName, Is.EqualTo(expectedActorName))
        Assert.That(reminder.ActorId, Is.EqualTo(expectedActorId))
        Assert.That(reminder.OwnerId, Is.EqualTo(Guid.Parse(ownerId)))
        Assert.That(reminder.OrganizationId, Is.EqualTo(Guid.Parse(organizationId)))
        Assert.That(reminder.RepositoryId, Is.EqualTo(Guid.Parse(repositoryIds[0])))
        Assert.That(reminder.ReminderType, Is.EqualTo(expectedReminderType))
        Assert.That(reminder.ReminderTime, Is.EqualTo(expectedTime))
        Assert.That(reminder.State, Is.EqualTo(ReminderState.EmptyReminderState))

[<NonParallelizable>]
type ReminderServer() =

    [<Test>]
    member _.CreateListGetUpdateRescheduleAndDeletePreserveExplicitSchedulingState() =
        task {
            let actorName = $"DirectoryVersionActor"
            let actorId = $"{Guid.NewGuid()}"
            let initialFireAt = Instant.FromUtc(2035, 1, 2, 3, 4, 5)
            let updatedFireAt = Instant.FromUtc(2035, 2, 3, 4, 5, 6)

            let! reminderId = ReminderServerTestHelpers.createReminderAsync actorName actorId "Maintenance" initialFireAt

            let! createdReminder = ReminderServerTestHelpers.getReminderAsync reminderId
            ReminderServerTestHelpers.assertReminder reminderId actorName actorId ReminderTypes.Maintenance initialFireAt createdReminder

            let! listResponse = Client.PostAsync("/reminder/list", createJsonContent (ReminderServerTestHelpers.listParameters actorName "Maintenance"))

            do! ReminderServerTestHelpers.assertOk listResponse
            let! listed = deserializeContent<GraceReturnValue<ReminderDto seq>> listResponse

            Assert.That(
                listed.ReturnValue
                |> Seq.exists (fun reminder ->
                    reminder.ReminderId = reminderId
                    && reminder.ReminderTime = initialFireAt),
                Is.True
            )

            let! updateResponse =
                Client.PostAsync("/reminder/updateTime", createJsonContent (ReminderServerTestHelpers.updateTimeParameters reminderId updatedFireAt))

            do! ReminderServerTestHelpers.assertOk updateResponse
            let! updateReturnValue = deserializeContent<GraceReturnValue<string>> updateResponse
            Assert.That(updateReturnValue.ReturnValue, Is.EqualTo("Reminder time updated successfully."))

            let! updatedReminder = ReminderServerTestHelpers.getReminderAsync reminderId
            ReminderServerTestHelpers.assertReminder reminderId actorName actorId ReminderTypes.Maintenance updatedFireAt updatedReminder

            let lowerBound = getCurrentInstant () + Duration.FromHours(2.0)

            let! rescheduleResponse =
                Client.PostAsync("/reminder/reschedule", createJsonContent (ReminderServerTestHelpers.rescheduleParameters reminderId "+2h"))

            let upperBound =
                getCurrentInstant ()
                + Duration.FromHours(2.0)
                + Duration.FromSeconds(5.0)

            do! ReminderServerTestHelpers.assertOk rescheduleResponse
            let! rescheduleReturnValue = deserializeContent<GraceReturnValue<string>> rescheduleResponse
            Assert.That(rescheduleReturnValue.ReturnValue, Does.Contain("Reminder rescheduled to"))

            let! rescheduledReminder = ReminderServerTestHelpers.getReminderAsync reminderId
            Assert.That(rescheduledReminder.ReminderId, Is.EqualTo(reminderId))
            Assert.That(rescheduledReminder.ReminderTime >= lowerBound, Is.True)
            Assert.That(rescheduledReminder.ReminderTime <= upperBound, Is.True)

            let! deleteResponse = Client.PostAsync("/reminder/delete", createJsonContent (ReminderServerTestHelpers.deleteParameters reminderId))

            do! ReminderServerTestHelpers.assertOk deleteResponse
            let! deleteReturnValue = deserializeContent<GraceReturnValue<string>> deleteResponse
            Assert.That(deleteReturnValue.ReturnValue, Is.EqualTo("Reminder deleted successfully."))

            let! deletedReminder = ReminderServerTestHelpers.getReminderAsync reminderId
            Assert.That(deletedReminder.ReminderId, Is.EqualTo(Guid.Empty))
        }

    [<Test>]
    member _.ReminderRoutesRejectInvalidCreateAndMissingGetInputsAsGraceErrors() =
        task {
            let invalidCreate = ReminderServerTestHelpers.createParameters String.Empty $"{Guid.NewGuid()}" "Maintenance" (Instant.FromUtc(2035, 1, 1, 0, 0))

            let! invalidCreateResponse = Client.PostAsync("/reminder/create", createJsonContent invalidCreate)
            let! invalidCreateBody = invalidCreateResponse.Content.ReadAsStringAsync()
            Assert.That(invalidCreateResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), invalidCreateBody)
            let invalidCreateError = deserialize<GraceError> invalidCreateBody
            Assert.That(invalidCreateError.Error, Is.EqualTo(ReminderError.getErrorMessage ReminderError.ReminderActorNameIsRequired))
            Assert.That(invalidCreateError.CorrelationId, Is.Not.Empty)

            let missingGet = ReminderServerTestHelpers.getParameters String.Empty
            let! missingGetResponse = Client.PostAsync("/reminder/get", createJsonContent missingGet)
            let! missingGetBody = missingGetResponse.Content.ReadAsStringAsync()
            Assert.That(missingGetResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), missingGetBody)
            let missingGetError = deserialize<GraceError> missingGetBody
            Assert.That(missingGetError.Error, Is.EqualTo(ReminderError.getErrorMessage ReminderError.ReminderIdIsRequired))
            Assert.That(missingGetError.CorrelationId, Is.Not.Empty)
        }
