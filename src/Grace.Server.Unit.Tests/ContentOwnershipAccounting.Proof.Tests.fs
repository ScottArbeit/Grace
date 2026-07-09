namespace Grace.Server.Tests

open Grace.Types.Common
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.RepositoryContentCounter
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

module ContentOwnershipCounterActor = Grace.Actors.RepositoryContentCounter
module ContentOwnershipWorkflowActor = Grace.Actors.ManifestContributionWorkflow

/// Proves the contributor-owned accounting design assumptions that GV-14 must build on without implementing transfer.
[<Parallelizable(ParallelScope.All)>]
type ContentOwnershipAccountingProofTests() =

    let timestamp = Instant.FromUtc(2026, 7, 9, 9, 0)
    let repositoryId = Guid.Parse("4fa17137-1a45-4a0f-9ccb-638ca926cff1")
    let otherRepositoryId = Guid.Parse("3b870a65-3791-4d05-81b8-110fe13f74e3")
    let promotionSetId = Guid.Parse("a49646da-9002-4599-a8d1-3d23be3127f9")
    let storagePoolId = StoragePoolId "pool-main"
    let manifestAddress = ManifestAddress "manifest:blake3:owner-proof"

    let range = { StoragePoolId = storagePoolId; ContentBlockAddress = ContentBlockAddress "block-owner-proof"; OrdinalStart = 0; OrdinalCount = 1 }

    /// Creates deterministic metadata so proof failures identify the exact accounting operation under test.
    let metadata correlationId =
        {
            Timestamp = timestamp
            CorrelationId = correlationId
            Principal = "proof-worker"
            ClientType = Microsoft.FSharp.Core.Option.None
            Properties = Dictionary<string, string>()
        }

    /// Applies repository counter events to the DTO snapshot used by the next proof command.
    let applyCounterEvents events dto =
        events
        |> List.fold (fun current event -> RepositoryContentCounterDto.UpdateDto event current) dto

    /// Applies manifest workflow events to the DTO snapshot used by the next proof command.
    let applyWorkflowEvents events dto =
        events
        |> List.fold (fun current event -> ManifestContributionWorkflowDto.UpdateDto event current) dto

    /// Extracts successful repository counter decisions while keeping assertion failures readable.
    let expectCounterOk result =
        match result with
        | Ok decision -> decision
        | Error error ->
            Assert.Fail($"Expected repository content counter command to succeed, got {error.Error}.")
            Unchecked.defaultof<RepositoryContentCounterDecision>

    /// Extracts successful manifest workflow decisions while keeping assertion failures readable.
    let expectWorkflowOk result =
        match result with
        | Ok decision -> decision
        | Error error ->
            Assert.Fail($"Expected manifest contribution workflow command to succeed, got {error.Error}.")
            Unchecked.defaultof<ManifestContributionWorkflowDecision>

    /// Builds a repository counter add command for the manifest-backed content proof target.
    let add operationId repositoryId = RepositoryContentCounterCommand.AddReference(operationId, repositoryId, storagePoolId, manifestAddress)

    /// Builds a manifest workflow start command for an accepted transfer proof target.
    let startTransfer operationId direction =
        ManifestContributionWorkflowCommand.Start(operationId, repositoryId, storagePoolId, manifestAddress, direction, [| range |])

    /// Proves repeated contributor saves in one repository cannot be separated by owner using the current counter.
    [<Test>]
    member _.RepositoryContentCounterHasNoOwnerDimensionForSameRepositoryManifest() =
        Assert.That(typeof<RepositoryContentCounterDto>.GetProperty ("Ownership"), Is.Null)
        Assert.That(typeof<RepositoryContentCounterDto>.GetProperty ("CreatorUserId"), Is.Null)

        let first =
            ContentOwnershipCounterActor.decideCommand
                []
                RepositoryContentCounterDto.Default
                (add "private-save:contributor-a" repositoryId)
                (metadata "corr-contributor-a")
            |> expectCounterOk

        Assert.That(first.Counter.ReferenceCount, Is.EqualTo(1L))
        Assert.That(first.Intents, Has.Length.EqualTo(1))

        let afterFirst = applyCounterEvents first.Events RepositoryContentCounterDto.Default

        let second =
            ContentOwnershipCounterActor.decideCommand first.Events afterFirst (add "private-save:contributor-b" repositoryId) (metadata "corr-contributor-b")
            |> expectCounterOk

        Assert.That(second.Counter.ReferenceCount, Is.EqualTo(2L))
        Assert.That(second.Intents, Is.Empty)

    /// Proves repository content counters distinguish repositories even when the manifest-backed content matches.
    [<Test>]
    member _.SameManifestAddressInDifferentRepositoriesUsesDistinctRepositoryCounters() =
        let firstKey = ContentOwnershipCounterActor.primaryKey repositoryId storagePoolId manifestAddress
        let secondKey = ContentOwnershipCounterActor.primaryKey otherRepositoryId storagePoolId manifestAddress

        Assert.That(secondKey, Is.Not.EqualTo(firstKey))

        let firstRepository =
            ContentOwnershipCounterActor.decideCommandForKey
                (Some firstKey)
                []
                RepositoryContentCounterDto.Default
                (add "repository-a:first-reference" repositoryId)
                (metadata "corr-repository-a")
            |> expectCounterOk

        let secondRepository =
            ContentOwnershipCounterActor.decideCommandForKey
                (Some secondKey)
                []
                RepositoryContentCounterDto.Default
                (add "repository-b:first-reference" otherRepositoryId)
                (metadata "corr-repository-b")
            |> expectCounterOk

        Assert.That(firstRepository.Intents, Has.Length.EqualTo(1))
        Assert.That(secondRepository.Intents, Has.Length.EqualTo(1))

    /// Proves a replayed accepted transfer operation identity does not emit a second workflow delta.
    [<Test>]
    member _.StableTransferOperationIdentityReplaysWithoutSecondWorkflowDelta() =
        let operationId = ManifestContributionWorkflowOperationId $"promotion-transfer:{promotionSetId:N}:{storagePoolId}:{manifestAddress}"

        let first =
            ContentOwnershipWorkflowActor.decideCommand
                []
                ManifestContributionWorkflowDto.Default
                (startTransfer operationId ManifestContributionDirection.Increment)
                (metadata "corr-transfer-start")
            |> expectWorkflowOk

        let afterFirst = applyWorkflowEvents first.Events ManifestContributionWorkflowDto.Default

        let replay =
            ContentOwnershipWorkflowActor.decideCommand
                first.Events
                afterFirst
                (startTransfer operationId ManifestContributionDirection.Increment)
                (metadata "corr-transfer-replay")
            |> expectWorkflowOk

        Assert.That(replay.WasIdempotentReplay, Is.True)
        Assert.That(replay.Events, Is.Empty)
        Assert.That(replay.Intents, Is.Empty)

    /// Proves the same accepted transfer identity cannot be reused for a different manifest workflow payload.
    [<Test>]
    member _.StableTransferOperationIdentityRejectsDifferentPayloadReuse() =
        let operationId = ManifestContributionWorkflowOperationId $"promotion-transfer:{promotionSetId:N}:{storagePoolId}:{manifestAddress}"

        let first =
            ContentOwnershipWorkflowActor.decideCommand
                []
                ManifestContributionWorkflowDto.Default
                (startTransfer operationId ManifestContributionDirection.Increment)
                (metadata "corr-transfer-start")
            |> expectWorkflowOk

        let afterFirst = applyWorkflowEvents first.Events ManifestContributionWorkflowDto.Default

        match
            ContentOwnershipWorkflowActor.decideCommand
                first.Events
                afterFirst
                (startTransfer operationId ManifestContributionDirection.Decrement)
                (metadata "corr-transfer-mismatched")
            with
        | Ok _ -> Assert.Fail("Expected reused transfer operation identity with a different payload to reject.")
        | Error error -> Assert.That(error.Error, Is.EqualTo("ManifestContributionWorkflow operation id was already used with a different payload."))

    /// Proves range active counts model retention progress and do not carry owner-specific usage fields.
    [<Test>]
    member _.RangeActiveCountWorkflowIsRetentionProgressNotOwnerSpecificUsage() =
        Assert.That(typeof<ManifestContributionWorkflowDto>.GetProperty ("Ownership"), Is.Null)
        Assert.That(typeof<ManifestContributionWorkflowDto>.GetProperty ("CreatorUserId"), Is.Null)

        let started =
            ContentOwnershipWorkflowActor.decideCommand
                []
                ManifestContributionWorkflowDto.Default
                (startTransfer "retention-proof-start" ManifestContributionDirection.Increment)
                (metadata "corr-retention-start")
            |> expectWorkflowOk

        let afterStart = applyWorkflowEvents started.Events ManifestContributionWorkflowDto.Default

        let succeeded =
            ContentOwnershipWorkflowActor.decideCommand
                started.Events
                afterStart
                (ManifestContributionWorkflowCommand.RecordRangeSucceeded("retention-proof-range", repositoryId, storagePoolId, manifestAddress, range))
                (metadata "corr-retention-range")
            |> expectWorkflowOk

        Assert.That(succeeded.Intents, Has.Length.EqualTo(1))
        Assert.That(succeeded.Intents[0], Is.EqualTo(ManifestContributionWorkflowIntent.AdjustRangeActiveManifestCount(range, 1)))
        Assert.That(succeeded.Workflow.CompletedRanges.ContainsKey range, Is.True)
