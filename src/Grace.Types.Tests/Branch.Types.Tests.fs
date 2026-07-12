namespace Grace.Types.Tests

open Grace.Shared
open Grace.Types.Branch
open Grace.Types.Common
open Grace.Types.Reference
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic

/// Contains tests covering branch dto hash behavior.
[<Parallelizable(ParallelScope.All)>]
type BranchDtoHashTests() =

    let timestamp = Instant.FromUtc(2026, 6, 11, 9, 0)
    let branchId = Guid.Parse("11111111-aaaa-4444-8888-111111111111")
    let directoryVersionId = Guid.Parse("22222222-aaaa-4444-8888-222222222222")
    let referenceId = Guid.Parse("33333333-aaaa-4444-8888-333333333333")
    let sha256Hash = Sha256Hash "root-sha256"
    let blake3Hash = Blake3Hash "root-blake3"
    let referenceText = ReferenceText "root reference"

    let metadata =
        {
            Timestamp = timestamp
            CorrelationId = "branch-root-hash-tests"
            Principal = "alice@example.test"
            ClientType = Option.None
            Properties = Dictionary<string, string>()
        }

    /// Exercises reference dto coverage for the types branch contract.
    let referenceDto referenceType =
        { ReferenceDto.Default with
            ReferenceId = referenceId
            BranchId = branchId
            DirectoryId = directoryVersionId
            Sha256Hash = sha256Hash
            Blake3Hash = blake3Hash
            ReferenceType = referenceType
            ReferenceText = referenceText
            CreatedAt = timestamp
        }

    /// Exercises branch event coverage for the types branch contract.
    let branchEvent (eventType: BranchEventType) : BranchEvent = { Event = eventType; Metadata = metadata }

    /// Builds isolated branch-event metadata carrying the Reference used by creation replay.
    let branchEventWithBasedOn (eventType: BranchEventType) (basedOnReference: ReferenceDto) : BranchEvent =
        let properties = Dictionary<string, string>()
        properties["basedOnReferenceDto"] <- Utilities.serialize basedOnReference
        { Event = eventType; Metadata = { metadata with Properties = properties } }

    /// Verifies that branch creation keeps only required References real and leaves typed slots at the canonical sentinel.
    [<Test>]
    member _.CreatedProjectionPreservesTypedReferenceSentinels() =
        let initialReference = referenceDto ReferenceType.Rebase

        let created =
            BranchDto.UpdateDto
                (branchEventWithBasedOn
                    (BranchEventType.Created(
                        branchId,
                        BranchName "feature",
                        BranchId.Empty,
                        initialReference.ReferenceId,
                        OwnerId.NewGuid(),
                        OrganizationId.NewGuid(),
                        RepositoryId.NewGuid(),
                        [| ReferenceType.Promotion |]
                    ))
                    initialReference)
                BranchDto.Default

        [|
            created.BasedOn
            created.LatestReference
        |]
        |> Array.iter (fun reference ->
            Assert.That(reference.ReferenceId, Is.EqualTo(initialReference.ReferenceId))
            Assert.That(reference.Sha256Hash, Is.EqualTo(sha256Hash))
            Assert.That(reference.Blake3Hash, Is.EqualTo(blake3Hash)))

        [|
            created.LatestPromotion
            created.LatestCommit
            created.LatestCheckpoint
            created.LatestSave
        |]
        |> Array.iter (fun reference -> Assert.That(reference, Is.EqualTo(ReferenceDto.Default)))

    /// Verifies that an initial promotion updates only promotion and required latest slots.
    [<Test>]
    member _.InitialBranchPromotionReplayKeepsUnseenTypedSlotsDefault() =
        let created =
            BranchDto.UpdateDto
                (branchEventWithBasedOn
                    (BranchEventType.Created(
                        branchId,
                        BranchName Constants.InitialBranchName,
                        BranchId.Empty,
                        ReferenceId.Empty,
                        OwnerId.NewGuid(),
                        OrganizationId.NewGuid(),
                        RepositoryId.NewGuid(),
                        [| ReferenceType.Promotion |]
                    ))
                    ReferenceDto.Default)
                BranchDto.Default

        let initialReference = referenceDto ReferenceType.Promotion

        let promoted =
            BranchDto.UpdateDto (branchEvent (BranchEventType.Promoted(initialReference, directoryVersionId, sha256Hash, blake3Hash, referenceText))) created

        [|
            promoted.BasedOn
            promoted.LatestReference
            promoted.LatestPromotion
        |]
        |> Array.iter (fun reference ->
            Assert.That(reference.ReferenceId, Is.EqualTo(initialReference.ReferenceId))
            Assert.That(reference.Sha256Hash, Is.EqualTo(sha256Hash))
            Assert.That(reference.Blake3Hash, Is.EqualTo(blake3Hash)))

        [|
            promoted.LatestCommit
            promoted.LatestCheckpoint
            promoted.LatestSave
        |]
        |> Array.iter (fun reference -> Assert.That(reference, Is.EqualTo(ReferenceDto.Default)))

        Assert.That(BranchDto.IsValidPublicProjection promoted, Is.True)

    /// Verifies that every typed latest slot accepts only its exact real type or the canonical default sentinel.
    [<Test>]
    member _.PublicProjectionRejectsWrongTypesAndPartialSentinels() =
        let real referenceType = referenceDto referenceType

        let valid =
            { BranchDto.Default with
                BasedOn = real ReferenceType.Rebase
                LatestReference = real ReferenceType.Save
                LatestPromotion = real ReferenceType.Promotion
                LatestCommit = real ReferenceType.Commit
                LatestCheckpoint = real ReferenceType.Checkpoint
                LatestSave = real ReferenceType.Save
            }

        Assert.That(BranchDto.IsValidPublicProjection valid, Is.True)

        let typedSlots =
            [|
                (fun value -> { valid with LatestPromotion = value })
                (fun value -> { valid with LatestCommit = value })
                (fun value -> { valid with LatestCheckpoint = value })
                (fun value -> { valid with LatestSave = value })
            |]

        for setSlot in typedSlots do
            Assert.That(BranchDto.IsValidPublicProjection(setSlot ReferenceDto.Default), Is.True)
            Assert.That(BranchDto.IsValidPublicProjection(setSlot (real ReferenceType.Tag)), Is.False)

            let partialSentinel = { ReferenceDto.Default with Sha256Hash = sha256Hash }
            Assert.That(BranchDto.IsValidPublicProjection(setSlot partialSentinel), Is.False)

    /// Verifies canonical sentinel recognition survives serialization with a newly allocated empty Links sequence.
    [<Test>]
    member _.PublicProjectionRecognizesDeserializedCanonicalSentinels() =
        let deserializedDefault = Utilities.deserialize<ReferenceDto> (Utilities.serialize ReferenceDto.Default)
        let realReference = referenceDto ReferenceType.Promotion

        let branch =
            { BranchDto.Default with
                BasedOn = realReference
                LatestReference = realReference
                LatestPromotion = realReference
                LatestCommit = deserializedDefault
                LatestCheckpoint = deserializedDefault
                LatestSave = deserializedDefault
            }

        Assert.That(BranchDto.IsCanonicalReferenceDefault deserializedDefault, Is.True)
        Assert.That(BranchDto.IsValidPublicProjection branch, Is.True)

    /// Verifies that BasedOn and LatestReference never accept the typed-slot sentinel or partial real References.
    [<Test>]
    member _.PublicProjectionKeepsRequiredReferencesStrict() =
        let realReference = referenceDto ReferenceType.Save

        let valid = { BranchDto.Default with BasedOn = realReference; LatestReference = realReference }

        Assert.That(BranchDto.IsValidPublicProjection valid, Is.True)
        Assert.That(BranchDto.IsValidPublicProjection { valid with BasedOn = ReferenceDto.Default }, Is.False)
        Assert.That(BranchDto.IsValidPublicProjection { valid with LatestReference = ReferenceDto.Default }, Is.False)
        Assert.That(BranchDto.IsValidPublicProjection { valid with LatestReference = { realReference with Blake3Hash = Blake3Hash String.Empty } }, Is.False)

    /// Verifies that reference producing commands carry both root hashes.
    [<Test>]
    member _.ReferenceProducingCommandsCarryBothRootHashes() =
        let commands =
            [
                BranchCommand.Assign(directoryVersionId, sha256Hash, blake3Hash, referenceText)
                BranchCommand.Promote(directoryVersionId, sha256Hash, blake3Hash, referenceText)
                BranchCommand.Commit(directoryVersionId, sha256Hash, blake3Hash, referenceText)
                BranchCommand.Checkpoint(directoryVersionId, sha256Hash, blake3Hash, referenceText)
                BranchCommand.Save(directoryVersionId, sha256Hash, blake3Hash, referenceText)
                BranchCommand.Tag(directoryVersionId, sha256Hash, blake3Hash, referenceText)
                BranchCommand.CreateExternal(directoryVersionId, sha256Hash, blake3Hash, referenceText)
            ]

        for command in commands do
            match command with
            | BranchCommand.Assign (directoryId, sha, blake3, text)
            | BranchCommand.Promote (directoryId, sha, blake3, text)
            | BranchCommand.Commit (directoryId, sha, blake3, text)
            | BranchCommand.Checkpoint (directoryId, sha, blake3, text)
            | BranchCommand.Save (directoryId, sha, blake3, text)
            | BranchCommand.Tag (directoryId, sha, blake3, text)
            | BranchCommand.CreateExternal (directoryId, sha, blake3, text) ->
                Assert.That(directoryId, Is.EqualTo(directoryVersionId))
                Assert.That(sha, Is.EqualTo(sha256Hash))
                Assert.That(blake3, Is.EqualTo(blake3Hash))
                Assert.That(text, Is.EqualTo(referenceText))
            | _ -> Assert.Fail($"Unexpected command case: {command}")

    /// Verifies that reference producing events carry both root hashes.
    [<Test>]
    member _.ReferenceProducingEventsCarryBothRootHashes() =
        let promotionReference = referenceDto ReferenceType.Promotion
        let commitReference = referenceDto ReferenceType.Commit
        let checkpointReference = referenceDto ReferenceType.Checkpoint
        let saveReference = referenceDto ReferenceType.Save
        let tagReference = referenceDto ReferenceType.Tag
        let externalReference = referenceDto ReferenceType.External

        let events =
            [
                BranchEventType.Assigned(promotionReference, directoryVersionId, sha256Hash, blake3Hash, referenceText)
                BranchEventType.Promoted(promotionReference, directoryVersionId, sha256Hash, blake3Hash, referenceText)
                BranchEventType.Committed(commitReference, directoryVersionId, sha256Hash, blake3Hash, referenceText)
                BranchEventType.Checkpointed(checkpointReference, directoryVersionId, sha256Hash, blake3Hash, referenceText)
                BranchEventType.Saved(saveReference, directoryVersionId, sha256Hash, blake3Hash, referenceText)
                BranchEventType.Tagged(tagReference, directoryVersionId, sha256Hash, blake3Hash, referenceText)
                BranchEventType.ExternalCreated(externalReference, directoryVersionId, sha256Hash, blake3Hash, referenceText)
            ]

        for event in events do
            match event with
            | BranchEventType.Assigned (reference, directoryId, sha, blake3, text)
            | BranchEventType.Promoted (reference, directoryId, sha, blake3, text)
            | BranchEventType.Committed (reference, directoryId, sha, blake3, text)
            | BranchEventType.Checkpointed (reference, directoryId, sha, blake3, text)
            | BranchEventType.Saved (reference, directoryId, sha, blake3, text)
            | BranchEventType.Tagged (reference, directoryId, sha, blake3, text)
            | BranchEventType.ExternalCreated (reference, directoryId, sha, blake3, text) ->
                Assert.That(reference.Sha256Hash, Is.EqualTo(sha256Hash))
                Assert.That(reference.Blake3Hash, Is.EqualTo(blake3Hash))
                Assert.That(directoryId, Is.EqualTo(directoryVersionId))
                Assert.That(sha, Is.EqualTo(sha256Hash))
                Assert.That(blake3, Is.EqualTo(blake3Hash))
                Assert.That(text, Is.EqualTo(referenceText))
            | _ -> Assert.Fail($"Unexpected event case: {event}")

    /// Verifies that replay projection keeps latest references with both root hashes.
    [<Test>]
    member _.ReplayProjectionKeepsLatestReferencesWithBothRootHashes() =
        let committed =
            BranchDto.UpdateDto
                (branchEvent (BranchEventType.Committed(referenceDto ReferenceType.Commit, directoryVersionId, sha256Hash, blake3Hash, referenceText)))
                BranchDto.Default

        let saved =
            BranchDto.UpdateDto
                (branchEvent (BranchEventType.Saved(referenceDto ReferenceType.Save, directoryVersionId, sha256Hash, blake3Hash, referenceText)))
                committed

        Assert.That(committed.LatestCommit.Sha256Hash, Is.EqualTo(sha256Hash))
        Assert.That(committed.LatestCommit.Blake3Hash, Is.EqualTo(blake3Hash))
        Assert.That(saved.LatestSave.Sha256Hash, Is.EqualTo(sha256Hash))
        Assert.That(saved.LatestSave.Blake3Hash, Is.EqualTo(blake3Hash))
        Assert.That(saved.LatestCommit.Blake3Hash, Is.EqualTo(blake3Hash))
