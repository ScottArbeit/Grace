namespace Grace.Server.Tests

open Grace.Server
open Grace.Shared.Validation.Utilities
open Grace.Shared.Parameters.WorkItem
open Grace.Shared.Validation.Errors
open Grace.Types.Types
open Grace.Types.WorkItem
open NUnit.Framework
open NodaTime
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type WorkItemUpdateTests() =
    let metadata timestamp = { Timestamp = timestamp; CorrelationId = "corr-work-item"; Principal = "tester"; Properties = Dictionary<string, string>() }

    [<Test>]
    member _.UpdateCommandsEmptyWhenNoFieldsProvided() =
        let parameters = UpdateWorkItemParameters(WorkItemId = Guid.NewGuid().ToString())
        let commands = WorkItem.buildUpdateCommands parameters
        Assert.That(commands, Is.Empty)

    [<Test>]
    member _.UpdateCommandsOrderedForMultipleFields() =
        let parameters =
            UpdateWorkItemParameters(
                WorkItemId = Guid.NewGuid().ToString(),
                Title = "Title",
                Description = "Description",
                Status = WorkItemStatus.Active.ToString(),
                Constraints = "Constraints",
                Notes = "Notes",
                ArchitecturalNotes = "Architecture",
                MigrationNotes = "Migration"
            )

        let commands = WorkItem.buildUpdateCommands parameters

        let expected: WorkItemCommand list =
            [
                WorkItemCommand.SetTitle "Title"
                WorkItemCommand.SetDescription "Description"
                WorkItemCommand.SetStatus WorkItemStatus.Active
                WorkItemCommand.SetConstraints "Constraints"
                WorkItemCommand.SetNotes "Notes"
                WorkItemCommand.SetArchitecturalNotes "Architecture"
                WorkItemCommand.SetMigrationNotes "Migration"
            ]

        let matches = commands = expected
        Assert.That(matches, Is.True)

    [<Test>]
    member _.LinkReferenceValidationRejectsInvalidReferenceId() =
        let parameters = LinkReferenceParameters(WorkItemId = Guid.NewGuid().ToString(), ReferenceId = "not-a-guid")

        let validations = WorkItem.validateLinkReferenceParameters parameters

        let error =
            validations
            |> getFirstError
            |> Async.AwaitTask
            |> Async.RunSynchronously

        Assert.That(error, Is.EqualTo(Some WorkItemError.InvalidReferenceId))

    [<Test>]
    member _.LinkPromotionGroupValidationRejectsInvalidPromotionGroupId() =
        let parameters = LinkPromotionGroupParameters(WorkItemId = Guid.NewGuid().ToString(), PromotionGroupId = "not-a-guid")

        let validations = WorkItem.validateLinkPromotionGroupParameters parameters

        let error =
            validations
            |> getFirstError
            |> Async.AwaitTask
            |> Async.RunSynchronously

        Assert.That(error, Is.EqualTo(Some WorkItemError.InvalidPromotionGroupId))

    [<Test>]
    member _.UpdateDtoPreservesCreatedFields() =
        let createdAt = Instant.FromUtc(2025, 1, 1, 0, 0)
        let updatedAt = Instant.FromUtc(2025, 1, 2, 0, 0)
        let createdBy = UserId "creator"

        let dto = { WorkItemDto.Default with WorkItemId = Guid.NewGuid(); Title = "Before"; CreatedAt = createdAt; CreatedBy = createdBy }

        let workItemEvent = { Event = WorkItemEventType.TitleSet "After"; Metadata = metadata updatedAt }

        let updated = WorkItemDto.UpdateDto workItemEvent dto

        Assert.That(updated.Title, Is.EqualTo("After"))
        Assert.That(updated.CreatedAt, Is.EqualTo(createdAt))
        Assert.That(updated.CreatedBy, Is.EqualTo(createdBy))
        Assert.That(updated.UpdatedAt, Is.EqualTo(Some updatedAt))

    [<Test>]
    member _.DuplicateCorrelationDetectionFindsMatches() =
        let timestamp = Instant.FromUtc(2025, 1, 1, 0, 0)
        let eventMetadata = metadata timestamp
        let workItemEvent = { Event = WorkItemEventType.TitleSet "Title"; Metadata = eventMetadata }

        let duplicate = Grace.Actors.WorkItem.hasDuplicateCorrelationId [ workItemEvent ] eventMetadata
        let different = Grace.Actors.WorkItem.hasDuplicateCorrelationId [ workItemEvent ] { eventMetadata with CorrelationId = "corr-other" }

        Assert.That(duplicate, Is.True)
        Assert.That(different, Is.False)
