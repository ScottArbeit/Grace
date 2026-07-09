namespace Grace.Server.Tests

open Grace.Types.Common
open Grace.Types.ContentOwnershipLedger
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

module ContentOwnershipLedgerActor = Grace.Actors.ContentOwnershipLedger

/// Covers manifest-backed content ownership ledger behavior in no-Aspire server unit tests.
[<Parallelizable(ParallelScope.All)>]
type ContentOwnershipLedgerActorTests() =

    let timestamp = Instant.FromUtc(2026, 7, 9, 12, 0)
    let repositoryId = Guid.Parse("720c96d1-9647-48fb-9d05-469eb4ef4bd5")
    let otherRepositoryId = Guid.Parse("0db55478-4d76-44cb-a77f-c3a3c40a29d8")
    let promotionSetId = Guid.Parse("ae0d1ae6-0202-49a2-8a79-61140792ddbf")
    let stepId = Guid.Parse("f39047f7-3783-4081-8d32-c39f8d25fcc7")
    let appliedDirectoryVersionId = Guid.Parse("72bbaedb-e40a-44e4-b2f4-2f38f7f4c831")
    let storagePoolId = StoragePoolId "pool-main"
    let otherStoragePoolId = StoragePoolId "pool-archive"
    let manifestAddress = ManifestAddress "manifest:blake3:ownership-alpha"
    let otherManifestAddress = ManifestAddress "manifest:blake3:ownership-beta"
    let creatorUserId = UserId "creator@example.test"
    let otherCreatorUserId = UserId "other@example.test"
    let contributorScope = ContentOwnershipOwnerScope.ContributorOwned(repositoryId, creatorUserId)
    let otherContributorScope = ContentOwnershipOwnerScope.ContributorOwned(repositoryId, otherCreatorUserId)
    let repositoryScope = ContentOwnershipOwnerScope.RepositoryOwned repositoryId
    let activeUsageId = ContentOwnershipLedgerActiveUsageId "reference:private-save:pool-main:ownership-alpha"
    let secondActiveUsageId = ContentOwnershipLedgerActiveUsageId "reference:accepted-promotion:pool-main:ownership-alpha"
    let thirdActiveUsageId = ContentOwnershipLedgerActiveUsageId "reference:private-reuse:pool-main:ownership-alpha"

    /// Constructs metadata fixtures used by content ownership ledger assertions.
    let metadata correlationId =
        {
            Timestamp = timestamp
            CorrelationId = correlationId
            Principal = "tester"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    /// Applies ledger events to the DTO snapshot used by the next command.
    let applyEvents events dto =
        events
        |> List.fold (fun current event -> ContentOwnershipLedgerDto.UpdateDto event current) dto

    /// Extracts successful ledger decisions while keeping assertion failures readable.
    let expectOk result =
        match result with
        | Ok decision -> decision
        | Error error ->
            Assert.Fail($"Expected content ownership ledger command to succeed, got {error.Error}.")
            Unchecked.defaultof<ContentOwnershipLedgerDecision>

    /// Builds a contributor-owned add command for the manifest-backed content proof target.
    let addContributor operationId usageId =
        ContentOwnershipLedgerCommand.AddActiveUsage(operationId, repositoryId, storagePoolId, manifestAddress, usageId, contributorScope)

    /// Builds a repository-owned add command for the manifest-backed content proof target.
    let addRepository operationId usageId =
        ContentOwnershipLedgerCommand.AddActiveUsage(operationId, repositoryId, storagePoolId, manifestAddress, usageId, repositoryScope)

    /// Builds a remove command for the manifest-backed content proof target.
    let remove operationId usageId = ContentOwnershipLedgerCommand.RemoveActiveUsage(operationId, repositoryId, storagePoolId, manifestAddress, usageId)

    /// Builds accepted promotion evidence for the manifest-backed content proof target.
    let acceptedEvidence =
        { PromotionSetId = promotionSetId; PromotionSetStepId = stepId; StepsComputationAttempt = 1; AppliedDirectoryVersionId = appliedDirectoryVersionId }

    /// Builds an accepted transfer command for the manifest-backed content proof target.
    let transfer operationId sourceScope =
        ContentOwnershipLedgerCommand.TransferAcceptedContent(
            operationId,
            repositoryId,
            storagePoolId,
            manifestAddress,
            sourceScope,
            repositoryScope,
            acceptedEvidence
        )

    /// Verifies private contributor-owned manifest content records contributor active usage before acceptance.
    [<Test>]
    member _.ContributorOwnedActiveUsageIsCountedBeforeAcceptance() =
        let decision =
            ContentOwnershipLedgerActor.decideCommand
                []
                ContentOwnershipLedgerDto.Default
                (addContributor "reference:add-private" activeUsageId)
                (metadata "corr-add-private")
            |> expectOk

        Assert.That(decision.Ledger.ActiveUsageOwners[activeUsageId], Is.EqualTo(contributorScope))
        Assert.That(decision.Ledger.ActiveUsageByOwner[contributorScope], Is.EqualTo(1L))
        Assert.That(decision.Ledger.ActiveUsageByOwner.ContainsKey repositoryScope, Is.False)

    /// Verifies accepted transfer moves active contributor usage to repository-owned usage exactly once.
    [<Test>]
    member _.AcceptedTransferMovesContributorUsageToRepositoryOwnerExactlyOnce() =
        let first =
            ContentOwnershipLedgerActor.decideCommand
                []
                ContentOwnershipLedgerDto.Default
                (addContributor "reference:add-private" activeUsageId)
                (metadata "corr-add-private")
            |> expectOk

        let afterFirst = applyEvents first.Events ContentOwnershipLedgerDto.Default

        let second =
            ContentOwnershipLedgerActor.decideCommand
                first.Events
                afterFirst
                (addContributor "reference:add-promotion" secondActiveUsageId)
                (metadata "corr-add-promotion")
            |> expectOk

        let events = first.Events @ second.Events
        let beforeTransfer = applyEvents events ContentOwnershipLedgerDto.Default

        let transferred =
            ContentOwnershipLedgerActor.decideCommand events beforeTransfer (transfer "promotion-transfer:accepted" contributorScope) (metadata "corr-transfer")
            |> expectOk

        Assert.That(transferred.Events, Has.Length.EqualTo(1))
        Assert.That(transferred.Ledger.ActiveUsageOwners[activeUsageId], Is.EqualTo(repositoryScope))
        Assert.That(transferred.Ledger.ActiveUsageOwners[secondActiveUsageId], Is.EqualTo(repositoryScope))
        Assert.That(transferred.Ledger.ActiveUsageByOwner.ContainsKey contributorScope, Is.False)
        Assert.That(transferred.Ledger.ActiveUsageByOwner[repositoryScope], Is.EqualTo(2L))

        let replay =
            ContentOwnershipLedgerActor.decideCommand
                (events @ transferred.Events)
                transferred.Ledger
                (transfer "promotion-transfer:accepted" contributorScope)
                (metadata "corr-transfer-replay")
            |> expectOk

        Assert.That(replay.WasIdempotentReplay, Is.True)
        Assert.That(replay.Events, Is.Empty)
        Assert.That(replay.Ledger.ActiveUsageByOwner[repositoryScope], Is.EqualTo(2L))

    /// Verifies replay identity reuse with a different owner, repository, storage pool, manifest, or evidence is rejected.
    [<Test>]
    member _.AcceptedTransferRejectsMismatchedOperationPayloadReuse() =
        let added =
            ContentOwnershipLedgerActor.decideCommand
                []
                ContentOwnershipLedgerDto.Default
                (addContributor "reference:add-private" activeUsageId)
                (metadata "corr-add-private")
            |> expectOk

        let beforeTransfer = applyEvents added.Events ContentOwnershipLedgerDto.Default

        let transferred =
            ContentOwnershipLedgerActor.decideCommand
                added.Events
                beforeTransfer
                (transfer "promotion-transfer:accepted" contributorScope)
                (metadata "corr-transfer")
            |> expectOk

        let events = added.Events @ transferred.Events

        let mismatchedPayloads =
            [
                ContentOwnershipLedgerCommand.TransferAcceptedContent(
                    "promotion-transfer:accepted",
                    otherRepositoryId,
                    storagePoolId,
                    manifestAddress,
                    ContentOwnershipOwnerScope.ContributorOwned(otherRepositoryId, creatorUserId),
                    ContentOwnershipOwnerScope.RepositoryOwned otherRepositoryId,
                    acceptedEvidence
                )
                ContentOwnershipLedgerCommand.TransferAcceptedContent(
                    "promotion-transfer:accepted",
                    repositoryId,
                    otherStoragePoolId,
                    manifestAddress,
                    contributorScope,
                    repositoryScope,
                    acceptedEvidence
                )
                ContentOwnershipLedgerCommand.TransferAcceptedContent(
                    "promotion-transfer:accepted",
                    repositoryId,
                    storagePoolId,
                    otherManifestAddress,
                    contributorScope,
                    repositoryScope,
                    acceptedEvidence
                )
                ContentOwnershipLedgerCommand.TransferAcceptedContent(
                    "promotion-transfer:accepted",
                    repositoryId,
                    storagePoolId,
                    manifestAddress,
                    otherContributorScope,
                    repositoryScope,
                    acceptedEvidence
                )
                ContentOwnershipLedgerCommand.TransferAcceptedContent(
                    "promotion-transfer:accepted",
                    repositoryId,
                    storagePoolId,
                    manifestAddress,
                    contributorScope,
                    repositoryScope,
                    { acceptedEvidence with StepsComputationAttempt = 2 }
                )
            ]

        for mismatchedPayload in mismatchedPayloads do
            match ContentOwnershipLedgerActor.decideCommand events transferred.Ledger mismatchedPayload (metadata "corr-mismatch") with
            | Ok _ -> Assert.Fail("Expected reused accepted transfer operation id with different payload to reject.")
            | Error error -> Assert.That(error.Error, Is.EqualTo("ContentOwnershipLedger operation id was already used with a different payload."))

    /// Verifies repository-owned usage replay does not double-charge when public or repository content is reused.
    [<Test>]
    member _.RepositoryOwnedUsageReplayDoesNotDoubleCharge() =
        let first =
            ContentOwnershipLedgerActor.decideCommand
                []
                ContentOwnershipLedgerDto.Default
                (addRepository "reference:add-public" activeUsageId)
                (metadata "corr-add-public")
            |> expectOk

        let replay =
            ContentOwnershipLedgerActor.decideCommand
                first.Events
                (applyEvents first.Events ContentOwnershipLedgerDto.Default)
                (addRepository "reference:add-public" activeUsageId)
                (metadata "corr-add-public-replay")
            |> expectOk

        Assert.That(replay.WasIdempotentReplay, Is.True)
        Assert.That(replay.Events, Is.Empty)
        Assert.That(replay.Ledger.ActiveUsageByOwner[repositoryScope], Is.EqualTo(1L))

    /// Verifies active usage duplicate commands with new operation ids are rejected rather than persisted as replay.
    [<Test>]
    member _.AlreadyPresentAddWithNewOperationIdIsRejected() =
        let first =
            ContentOwnershipLedgerActor.decideCommand
                []
                ContentOwnershipLedgerDto.Default
                (addContributor "reference:add-private" activeUsageId)
                (metadata "corr-add-private")
            |> expectOk

        match
            ContentOwnershipLedgerActor.decideCommand
                first.Events
                (applyEvents first.Events ContentOwnershipLedgerDto.Default)
                (addContributor "reference:add-private-duplicate" activeUsageId)
                (metadata "corr-add-private-duplicate")
            with
        | Ok _ -> Assert.Fail("Expected duplicate active usage with a new operation id to reject.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("ContentOwnershipLedger active usage id is already present for a different operation id."))

    /// Verifies absent active usage removes are rejected rather than accepted as replay without durable evidence.
    [<Test>]
    member _.AbsentRemoveWithNewOperationIdIsRejected() =
        match
            ContentOwnershipLedgerActor.decideCommand
                []
                ContentOwnershipLedgerDto.Default
                (remove "reference-expiry:absent" activeUsageId)
                (metadata "corr-remove-absent")
            with
        | Ok _ -> Assert.Fail("Expected absent active usage remove to reject.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("ContentOwnershipLedger active usage id is not present."))

    /// Verifies contributor reuse after accepted repository ownership remains repository-owned for accounting.
    [<Test>]
    member _.ContributorReuseAfterAcceptedTransferKeepsRepositoryOwnership() =
        let added =
            ContentOwnershipLedgerActor.decideCommand
                []
                ContentOwnershipLedgerDto.Default
                (addContributor "reference:add-private" activeUsageId)
                (metadata "corr-add-private")
            |> expectOk

        let transferred =
            ContentOwnershipLedgerActor.decideCommand
                added.Events
                (applyEvents added.Events ContentOwnershipLedgerDto.Default)
                (transfer "promotion-transfer:accepted" contributorScope)
                (metadata "corr-transfer")
            |> expectOk

        let events = added.Events @ transferred.Events

        let reused =
            ContentOwnershipLedgerActor.decideCommand
                events
                transferred.Ledger
                (addContributor "reference:add-reuse" thirdActiveUsageId)
                (metadata "corr-add-reuse")
            |> expectOk

        Assert.That(reused.Ledger.ActiveUsageOwners[activeUsageId], Is.EqualTo(repositoryScope))
        Assert.That(reused.Ledger.ActiveUsageOwners[thirdActiveUsageId], Is.EqualTo(repositoryScope))
        Assert.That(reused.Ledger.ActiveUsageByOwner[repositoryScope], Is.EqualTo(2L))
        Assert.That(reused.Ledger.ActiveUsageByOwner.ContainsKey contributorScope, Is.False)

    /// Verifies accepted repository ownership survives removal of every active usage for the manifest.
    [<Test>]
    member _.ContributorReuseAfterAcceptedTransferAndRemovalKeepsRepositoryOwnership() =
        let added =
            ContentOwnershipLedgerActor.decideCommand
                []
                ContentOwnershipLedgerDto.Default
                (addContributor "reference:add-private" activeUsageId)
                (metadata "corr-add-private")
            |> expectOk

        let transferred =
            ContentOwnershipLedgerActor.decideCommand
                added.Events
                (applyEvents added.Events ContentOwnershipLedgerDto.Default)
                (transfer "promotion-transfer:accepted" contributorScope)
                (metadata "corr-transfer")
            |> expectOk

        let removed =
            ContentOwnershipLedgerActor.decideCommand
                (added.Events @ transferred.Events)
                transferred.Ledger
                (remove "reference-expiry:private" activeUsageId)
                (metadata "corr-remove")
            |> expectOk

        Assert.That(removed.Ledger.ActiveUsageOwners, Is.Empty)
        Assert.That(removed.Ledger.ActiveUsageByOwner, Is.Empty)

        let events = added.Events @ transferred.Events @ removed.Events

        let reused =
            ContentOwnershipLedgerActor.decideCommand
                events
                removed.Ledger
                (addContributor "reference:add-reuse-after-removal" thirdActiveUsageId)
                (metadata "corr-add-reuse-after-removal")
            |> expectOk

        Assert.That(reused.Ledger.ActiveUsageOwners[thirdActiveUsageId], Is.EqualTo(repositoryScope))
        Assert.That(reused.Ledger.ActiveUsageByOwner[repositoryScope], Is.EqualTo(1L))
        Assert.That(reused.Ledger.ActiveUsageByOwner.ContainsKey contributorScope, Is.False)

        let replay =
            ContentOwnershipLedgerActor.decideCommand
                (events @ reused.Events)
                reused.Ledger
                (addContributor "reference:add-reuse-after-removal" thirdActiveUsageId)
                (metadata "corr-add-reuse-after-removal-replay")
            |> expectOk

        Assert.That(replay.WasIdempotentReplay, Is.True)
        Assert.That(replay.Events, Is.Empty)

    /// Verifies rejected or abandoned private work has no repository transfer without accepted evidence.
    [<Test>]
    member _.RejectedOrAbandonedWorkDoesNotCreateRepositoryOwnedTransfer() =
        let added =
            ContentOwnershipLedgerActor.decideCommand
                []
                ContentOwnershipLedgerDto.Default
                (addContributor "reference:add-private" activeUsageId)
                (metadata "corr-add-private")
            |> expectOk

        Assert.That(added.Ledger.ActiveUsageByOwner[contributorScope], Is.EqualTo(1L))
        Assert.That(added.Ledger.ActiveUsageByOwner.ContainsKey repositoryScope, Is.False)

        Assert.That(
            added.Events
            |> List.exists (fun event ->
                match event.Event with
                | ContentOwnershipLedgerEventType.AcceptedContentTransferred _ -> true
                | _ -> false),
            Is.False
        )

    /// Verifies deletion removes the active usage from whichever owner currently holds it.
    [<Test>]
    member _.DeletionRemovesCurrentOwnerUsageAfterTransfer() =
        let added =
            ContentOwnershipLedgerActor.decideCommand
                []
                ContentOwnershipLedgerDto.Default
                (addContributor "reference:add-private" activeUsageId)
                (metadata "corr-add-private")
            |> expectOk

        let transferred =
            ContentOwnershipLedgerActor.decideCommand
                added.Events
                (applyEvents added.Events ContentOwnershipLedgerDto.Default)
                (transfer "promotion-transfer:accepted" contributorScope)
                (metadata "corr-transfer")
            |> expectOk

        let removed =
            ContentOwnershipLedgerActor.decideCommand
                (added.Events @ transferred.Events)
                transferred.Ledger
                (remove "reference-expiry:private" activeUsageId)
                (metadata "corr-remove")
            |> expectOk

        Assert.That(removed.Ledger.ActiveUsageOwners.ContainsKey activeUsageId, Is.False)
        Assert.That(removed.Ledger.ActiveUsageByOwner.ContainsKey repositoryScope, Is.False)
        Assert.That(removed.Ledger.ActiveUsageByOwner.ContainsKey contributorScope, Is.False)
