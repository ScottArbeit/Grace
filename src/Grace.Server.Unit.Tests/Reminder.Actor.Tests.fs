namespace Grace.Server.Unit.Tests

open Grace.Actors.Reminder
open Grace.Shared
open Grace.Types
open Grace.Types.Common
open Grace.Types.Reminder
open NodaTime
open NUnit.Framework
open System
open System.Threading.Tasks

/// Covers ReminderActor persistence semantics without starting Orleans or Aspire resources.
[<Parallelizable(ParallelScope.All)>]
type ReminderActorTests() =

    let firstReminderId = Guid.Parse("11111111-7291-4000-8000-111111111111")
    let secondReminderId = Guid.Parse("22222222-7291-4000-8000-222222222222")
    let repositoryId = Guid.Parse("33333333-7291-4000-8000-333333333333")

    /// Builds one reminder with fixed durable facts for persistence tests.
    let reminder reminderId correlationId reminderTime =
        { ReminderDto.Default with
            ReminderId = reminderId
            ActorName = "ReferenceActor"
            ActorId = "referenceactor/44444444729140008000444444444444"
            RepositoryId = repositoryId
            ReminderType = ReminderTypes.PhysicalDeletion
            CreatedAt = Instant.FromUtc(2026, 7, 26, 8, 0)
            ReminderTime = reminderTime
            CorrelationId = correlationId
        }

    /// Verifies the explicit-id constructor keeps the caller's stable reminder identity.
    [<Test>]
    member _.CreateWithIdPreservesRequestedIdentity() =
        let created =
            ReminderDto.CreateWithId
                firstReminderId
                "ReferenceActor"
                "referenceactor/44444444729140008000444444444444"
                Guid.Empty
                Guid.Empty
                repositoryId
                ReminderTypes.PhysicalDeletion
                (Instant.FromUtc(2026, 8, 1, 8, 0))
                ReminderState.EmptyReminderState
                "corr-create-with-id"

        Assert.That(created.ReminderId, Is.EqualTo(firstReminderId))

    /// Verifies ordinary Create still allocates a fresh identity through the shared constructor path.
    [<Test>]
    member _.CreateAllocatesFreshReminderIdentity() =
        let create () =
            ReminderDto.Create
                "ReferenceActor"
                "referenceactor/44444444729140008000444444444444"
                Guid.Empty
                Guid.Empty
                repositoryId
                ReminderTypes.PhysicalDeletion
                (Instant.FromUtc(2026, 8, 1, 8, 0))
                ReminderState.EmptyReminderState
                "corr-create"

        let first = create ()
        let second = create ()

        Assert.That(first.ReminderId, Is.Not.EqualTo(ReminderId.Empty))
        Assert.That(second.ReminderId, Is.Not.EqualTo(first.ReminderId))

    /// Verifies a missing reminder is persisted and returned with the requested identity.
    [<Test>]
    member _.GetOrAddPersistsRequestedReminderWhenDurableRecordIsAbsent() =
        task {
            let wrapper = ReminderWrapper()
            let requested = reminder firstReminderId "corr-first" (Instant.FromUtc(2026, 8, 1, 8, 0))
            let mutable writes = 0

            let! returned =
                persistGetOrAddWith
                    false
                    (fun () ->
                        writes <- writes + 1
                        Task.CompletedTask)
                    wrapper
                    requested

            Assert.That(returned, Is.EqualTo(requested))
            Assert.That(wrapper.Reminder, Is.EqualTo(requested))
            Assert.That(writes, Is.EqualTo(1))
        }

    /// Verifies GetOrAdd preserves the first durable reminder instead of overwriting its schedule or metadata.
    [<Test>]
    member _.GetOrAddReturnsExistingDurableReminderUnchanged() =
        task {
            let wrapper = ReminderWrapper()
            let existing = reminder firstReminderId "corr-first" (Instant.FromUtc(2026, 8, 1, 8, 0))
            let requested = reminder firstReminderId "corr-retry" (Instant.FromUtc(2026, 8, 2, 8, 0))
            wrapper.Reminder <- existing
            let mutable writes = 0

            let! returned =
                persistGetOrAddWith
                    true
                    (fun () ->
                        writes <- writes + 1
                        Task.CompletedTask)
                    wrapper
                    requested

            Assert.That(returned, Is.EqualTo(existing))
            Assert.That(wrapper.Reminder, Is.EqualTo(existing))
            Assert.That(writes, Is.Zero)
        }

    /// Verifies a conflicting durable Reminder actor record is returned unchanged for Reference-level validation.
    [<Test>]
    member _.GetOrAddReturnsConflictingDurableReminderUnchanged() =
        task {
            let wrapper = ReminderWrapper()
            let existing = reminder secondReminderId "corr-existing" (Instant.FromUtc(2026, 8, 1, 8, 0))
            let requested = reminder firstReminderId "corr-requested" (Instant.FromUtc(2026, 8, 2, 8, 0))
            wrapper.Reminder <- existing
            let mutable writes = 0

            let! returned =
                persistGetOrAddWith
                    true
                    (fun () ->
                        writes <- writes + 1
                        Task.CompletedTask)
                    wrapper
                    requested

            Assert.That(returned, Is.EqualTo(existing))
            Assert.That(wrapper.Reminder, Is.EqualTo(existing))
            Assert.That(writes, Is.Zero)
        }

    /// Verifies a failed unknown write is retried with the same in-memory reminder rather than a later request snapshot.
    [<Test>]
    member _.GetOrAddRetriesPendingReminderAfterUnknownWriteOutcome() =
        task {
            let wrapper = ReminderWrapper()
            let first = reminder firstReminderId "corr-first" (Instant.FromUtc(2026, 8, 1, 8, 0))
            let retry = reminder firstReminderId "corr-retry" (Instant.FromUtc(2026, 8, 2, 8, 0))
            let failure = TimeoutException("reminder persistence outcome unknown")

            let firstWrite = persistGetOrAddWith false (fun () -> Task.FromException failure) wrapper first

            let escaped = Assert.ThrowsAsync<TimeoutException>(Func<Task>(fun () -> firstWrite :> Task))
            Assert.That(escaped, Is.SameAs(failure))
            Assert.That(wrapper.Reminder, Is.EqualTo(first))

            let mutable persisted = ReminderDto.Default

            let! returned =
                persistGetOrAddWith
                    false
                    (fun () ->
                        persisted <- wrapper.Reminder
                        Task.CompletedTask)
                    wrapper
                    retry

            Assert.That(returned, Is.EqualTo(first))
            Assert.That(persisted, Is.EqualTo(first))
        }

    /// Verifies Create keeps overwrite semantics while propagating the original persistence exception.
    [<Test>]
    member _.CreateOverwritesInMemoryStateAndPropagatesPersistenceFailure() =
        task {
            let wrapper = ReminderWrapper()
            let existing = reminder firstReminderId "corr-first" (Instant.FromUtc(2026, 8, 1, 8, 0))
            let replacement = reminder secondReminderId "corr-replacement" (Instant.FromUtc(2026, 8, 2, 8, 0))
            let failure = InvalidOperationException("create persistence failed")
            wrapper.Reminder <- existing

            let write = persistCreateWith (fun () -> Task.FromException failure) wrapper replacement

            let escaped = Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> write :> Task))
            Assert.That(escaped, Is.SameAs(failure))
            Assert.That(wrapper.Reminder, Is.EqualTo(replacement))
        }
