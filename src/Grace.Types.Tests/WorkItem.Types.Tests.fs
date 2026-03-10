namespace Grace.Types.Tests

open Grace.Types.Types
open Grace.Types.WorkItem
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

[<Parallelizable(ParallelScope.All)>]
type WorkItemTypesTests() =
    let metadata timestamp = { Timestamp = timestamp; CorrelationId = "corr-work-item"; Principal = "tester"; Properties = Dictionary<string, string>() }

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
    member _.CreatedEventSetsWorkItemNumber() =
        let createdAt = Instant.FromUtc(2025, 2, 1, 0, 0)
        let workItemId = Guid.NewGuid()
        let workItemNumber = 42L

        let createdEvent =
            {
                Event = WorkItemEventType.Created(workItemId, workItemNumber, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Title", "Description")
                Metadata = metadata createdAt
            }

        let dto = WorkItemDto.UpdateDto createdEvent WorkItemDto.Default

        Assert.That(dto.WorkItemId, Is.EqualTo(workItemId))
        Assert.That(dto.WorkItemNumber, Is.EqualTo(workItemNumber))
