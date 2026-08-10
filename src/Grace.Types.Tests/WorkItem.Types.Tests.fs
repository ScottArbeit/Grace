namespace Grace.Types.Tests

open Grace.Types.Common
open Grace.Types.TextContent
open Grace.Types.WorkItem
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

/// Contains tests covering work item types behavior.
[<Parallelizable(ParallelScope.All)>]
type WorkItemTypesTests() =
    /// Exercises metadata coverage for the types work Item contract.
    let metadata timestamp =
        {
            Timestamp = timestamp
            CorrelationId = "corr-work-item"
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    /// Verifies that update dto preserves created fields.
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

    /// Verifies that created event sets work item number.
    [<Test>]
    member _.CreatedEventSetsWorkItemNumber() =
        let createdAt = Instant.FromUtc(2025, 2, 1, 0, 0)
        let workItemId = Guid.NewGuid()
        let workItemNumber = 42L

        let createdEvent =
            {
                Event = WorkItemEventType.Created(workItemId, workItemNumber, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Title", None)
                Metadata = metadata createdAt
            }

        let dto = WorkItemDto.UpdateDto createdEvent WorkItemDto.Default

        Assert.That(dto.WorkItemId, Is.EqualTo(workItemId))
        Assert.That(dto.WorkItemNumber, Is.EqualTo(workItemNumber))

    /// Verifies that the actor-only state retains a description reference while the public projection remains text-free.
    [<Test>]
    member _.DescriptionStateDoesNotExposeStorageFactsThroughPublicProjection() =
        let description =
            {
                DescriptionId = Guid.Parse("11111111-1111-1111-1111-111111111111")
                TextContent =
                    Some { TextContentId = Guid.Parse("22222222-2222-2222-2222-222222222222"); Blake3Hash = String.replicate 64 "a"; Utf8ByteLength = 12L }
            }

        let updated =
            WorkItemState.UpdateState
                { Event = WorkItemEventType.DescriptionSet description; Metadata = metadata (Instant.FromUtc(2025, 3, 1, 0, 0)) }
                WorkItemState.Default

        Assert.That(updated.Description, Is.EqualTo(Some description))
        Assert.That(updated.WorkItem.Description, Is.EqualTo(String.Empty))

    /// Verifies that the later accepted description event is the current immutable reference.
    [<Test>]
    member _.DescriptionStateUsesLastAppendedDescription() =
        let first = { DescriptionId = Guid.NewGuid(); TextContent = None }
        let second = { DescriptionId = Guid.NewGuid(); TextContent = None }
        let firstEvent = { Event = WorkItemEventType.DescriptionSet first; Metadata = metadata (Instant.FromUtc(2025, 3, 1, 0, 0)) }
        let secondEvent = { Event = WorkItemEventType.DescriptionSet second; Metadata = metadata (Instant.FromUtc(2025, 3, 2, 0, 0)) }

        let state =
            WorkItemState.Default
            |> WorkItemState.UpdateState firstEvent
            |> WorkItemState.UpdateState secondEvent

        Assert.That(state.Description, Is.EqualTo(Some second))
