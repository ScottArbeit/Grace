namespace Grace.Server.Unit.Tests

open Grace.Actors.BillingAccount
open Grace.Shared
open Grace.Types.BillingAccount
open Grace.Types.Common
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

[<TestFixture>]
type BillingAccountActorTests() =
    let ownerId = Guid.Parse "11111111-1111-1111-1111-111111111111"
    let repositoryId = Guid.Parse "22222222-2222-2222-2222-222222222222"
    let contributorId = "user-33333333"
    let branchId = Guid.Parse "44444444-4444-4444-4444-444444444444"

    let metadata correlationId =
        {
            Timestamp = Instant.FromUtc(2026, 7, 10, 0, 0)
            CorrelationId = correlationId
            Principal = String.Empty
            ClientType = None
            Properties = Dictionary<string, string>()
        }

    let decide events account command = decideCommand events account command (metadata $"corr-{operationId command}")

    let success =
        function
        | Ok value -> value
        | Error error ->
            Assert.Fail error.Error
            Unchecked.defaultof<_>

    [<Test>]
    member _.ExternalReservationAtExactBudgetSucceedsAndOneByteMoreFails() =
        let bytes = IncludedStorageBytes / 10L
        let command = Reserve("reserve", ownerId, repositoryId, contributorId, bytes, ExternalContribution)

        let decision =
            decide [] BillingAccountDto.Default command
            |> success

        Assert.That(decision.Account.UsedBytes, Is.EqualTo bytes)
        Assert.That(decision.Account.ExternalBytes, Is.EqualTo bytes)

        let rejected = decide decision.Events decision.Account (Reserve("reserve-2", ownerId, repositoryId, contributorId, 1L, ExternalContribution))

        Assert.That(rejected.IsError, Is.True)

    [<Test>]
    member _.CompetingFinalCapacityReservationHasOneWinner() =
        let account = { BillingAccountDto.Default with CapacityBytes = 10L; ExternalPercent = 50 }

        let first =
            decide [] account (Reserve("first", ownerId, repositoryId, contributorId, 10L, Repository))
            |> success

        let second = decide first.Events first.Account (Reserve("second", ownerId, repositoryId, contributorId, 1L, Repository))
        Assert.That(second.IsError, Is.True)

    [<Test>]
    member _.ReserveReplayIsIdempotentAndMismatchFailsClosed() =
        let command = Reserve("same", ownerId, repositoryId, contributorId, 10L, ExternalContribution)

        let first =
            decide [] BillingAccountDto.Default command
            |> success

        let replay =
            decide first.Events first.Account command
            |> success

        Assert.That(replay.WasIdempotentReplay, Is.True)
        Assert.That(replay.Account.UsedBytes, Is.EqualTo 10L)
        let mismatch = decide first.Events first.Account (Reserve("same", ownerId, repositoryId, contributorId, 11L, ExternalContribution))
        Assert.That(mismatch.IsError, Is.True)

    [<Test>]
    member _.SettlePromoteAndReleasePreserveCounterInvariants() =
        let reserve = Reserve("reserve", ownerId, repositoryId, contributorId, 20L, ExternalContribution)

        let reserved =
            decide [] BillingAccountDto.Default reserve
            |> success

        let settled =
            decide reserved.Events reserved.Account (Settle("settle", "reserve", repositoryId, branchId))
            |> success

        Assert.That(settled.Account.ActiveExternalBranches, Is.EqualTo 1)

        let promoted =
            decide (reserved.Events @ settled.Events) settled.Account (Promote("promote", "reserve", repositoryId, branchId))
            |> success

        Assert.That(promoted.Account.UsedBytes, Is.EqualTo 20L)
        Assert.That(promoted.Account.ExternalBytes, Is.Zero)
        Assert.That(promoted.Account.ActiveExternalBranches, Is.Zero)

        let released =
            decide (reserved.Events @ settled.Events @ promoted.Events) promoted.Account (Release("release", "reserve", repositoryId, branchId))
            |> success

        Assert.That(released.Account.UsedBytes, Is.Zero)
        Assert.That(released.Account.ExternalBytes, Is.Zero)
        Assert.That(released.Account.ActiveExternalBranches, Is.Zero)

    [<Test>]
    member _.MultipleReservationsOnOneExternalBranchConsumeOneBranchSlot() =
        let reserveOne = Reserve("reserve-1", ownerId, repositoryId, contributorId, 20L, ExternalContribution)

        let first =
            decide [] BillingAccountDto.Default reserveOne
            |> success

        let reserveTwo = Reserve("reserve-2", ownerId, repositoryId, contributorId, 20L, ExternalContribution)

        let second =
            decide first.Events first.Account reserveTwo
            |> success

        let firstSettle =
            decide (first.Events @ second.Events) second.Account (Settle("settle-1", "reserve-1", repositoryId, branchId))
            |> success

        let secondSettle =
            decide (first.Events @ second.Events @ firstSettle.Events) firstSettle.Account (Settle("settle-2", "reserve-2", repositoryId, branchId))
            |> success

        Assert.That(secondSettle.Account.ActiveExternalBranches, Is.EqualTo 1)
        Assert.That(secondSettle.Account.ExternalBranchReservationCounts[branchId], Is.EqualTo 2)

        let firstRelease =
            decide
                (first.Events
                 @ second.Events
                   @ firstSettle.Events @ secondSettle.Events)
                secondSettle.Account
                (Release("release-1", "reserve-1", repositoryId, branchId))
            |> success

        Assert.That(firstRelease.Account.ActiveExternalBranches, Is.EqualTo 1)

        let secondRelease =
            decide
                (first.Events
                 @ second.Events
                   @ firstSettle.Events
                     @ secondSettle.Events @ firstRelease.Events)
                firstRelease.Account
                (Release("release-2", "reserve-2", repositoryId, branchId))
            |> success

        Assert.That(secondRelease.Account.ActiveExternalBranches, Is.Zero)
        Assert.That(secondRelease.Account.ExternalBranchReservationCounts, Is.Empty)

    [<Test>]
    member _.ReleaseWinsAgainstLaterSettlementAndDoesNotDoubleRelease() =
        let reserve = Reserve("reserve", ownerId, repositoryId, contributorId, 20L, ExternalContribution)

        let reserved =
            decide [] BillingAccountDto.Default reserve
            |> success

        let released =
            decide reserved.Events reserved.Account (Release("release", "reserve", repositoryId, branchId))
            |> success

        Assert.That(released.Account.UsedBytes, Is.Zero)
        Assert.That(released.Account.ExternalBytes, Is.Zero)
        let settleLate = decide (reserved.Events @ released.Events) released.Account (Settle("settle", "reserve", repositoryId, branchId))
        Assert.That(settleLate.IsError, Is.True)
        let duplicateDifferentId = decide (reserved.Events @ released.Events) released.Account (Release("release-2", "reserve", repositoryId, branchId))
        Assert.That(duplicateDifferentId.IsError, Is.True)
