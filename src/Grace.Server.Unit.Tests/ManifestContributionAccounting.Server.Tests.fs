namespace Grace.Server.Unit.Tests

open Grace.Actors
open Grace.Server.ManifestContributionAccounting
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.ManifestContributionAccounting
open Grace.Types.Reference
open NodaTime
open NUnit.Framework
open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// Covers the narrow Reference-created manifest contribution tracer without starting Aspire resources.
[<Parallelizable(ParallelScope.All)>]
type ManifestContributionAccountingServerTests() =

    let ownerId = Guid.Parse("11111111-7290-4000-8000-111111111111")
    let organizationId = Guid.Parse("22222222-7290-4000-8000-222222222222")
    let repositoryId = Guid.Parse("33333333-7290-4000-8000-333333333333")
    let branchId = Guid.Parse("44444444-7290-4000-8000-444444444444")
    let referenceId = Guid.Parse("55555555-7290-4000-8000-555555555555")
    let eventDirectoryVersionId = Guid.Parse("66666666-7290-4000-8000-666666666666")
    let currentDirectoryVersionId = Guid.Parse("77777777-7290-4000-8000-777777777777")
    let storagePoolId = StoragePoolId "mca-tracer-pool"
    let fileContentHash = FileContentHash(String.replicate 64 "b")
    let contentBlockAddress = ContentBlockAddress(String.replicate 64 "c")

    let finalizedManifest =
        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                ChunkingSuiteId "fixed-v1",
                fileContentHash,
                4L,
                storagePoolId,
                [
                    ContentBlock.Create(contentBlockAddress, 0L, 4L)
                ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let manifestAddress = finalizedManifest.ManifestAddress

    /// Builds one manifest-backed direct file for current DirectoryVersion state.
    let manifestBackedFile () =
        let file = FileVersion.CreateWithHashes (RelativePath "tracer.bin") (Sha256Hash "sha-tracer") (Blake3Hash fileContentHash) "" true 4L
        file.ContentReference <- FileContentReference.FileManifest finalizedManifest
        file, finalizedManifest

    /// Builds one current root DirectoryVersion actor snapshot.
    let currentDirectoryVersionDto () =
        let file, _ = manifestBackedFile ()
        let files = List<FileVersion>()
        files.Add(file)

        {
            DirectoryVersion =
                DirectoryVersion.CreateWithHashes
                    currentDirectoryVersionId
                    ownerId
                    organizationId
                    repositoryId
                    (RelativePath ".")
                    (Sha256Hash "sha-current")
                    (Blake3Hash "b3-current")
                    (List<DirectoryVersionId>())
                    files
                    4L
            RecursiveSize = 4L
            DeletedAt = None
            DeleteReason = String.Empty
            HashesValidated = true
        }

    /// Builds a stale delivery whose DirectoryVersionId differs from the durable Reference actor state.
    let staleCreatedEvent referenceType =
        {
            Event =
                ReferenceEventType.Created(
                    referenceId,
                    ownerId,
                    organizationId,
                    repositoryId,
                    branchId,
                    eventDirectoryVersionId,
                    Sha256Hash "sha-event",
                    Blake3Hash "b3-event",
                    referenceType,
                    ReferenceText "tracer",
                    []
                )
            Metadata = EventMetadata.New "mca-tracer" "unit-test"
        }

    /// Provides a minimal exact store that records Reference-root mutations.
    let recordingStore (writes: ResizeArray<ExactRelationship>) (effectOrder: ResizeArray<string>) =
        { new IExactRelationshipStore with
            member _.EnsurePresentAsync(relationship, _) =
                effectOrder.Add("exact-root")
                writes.Add(relationship)
                Task.FromResult ExactRelationshipWriteOutcome.Changed

            member _.EnsureAbsentAsync(_, _) = Task.FromResult ExactRelationshipWriteOutcome.AlreadyConverged

            member _.EnumerateAsync(_, _, _, _) = Task.FromResult { Relationships = Array.empty; ContinuationToken = None }

            member _.VerifyAsync(_, _) = Task.FromResult ExactRelationshipPresence.Absent
        }

    /// Verifies Cosmos writes carry its required lowercase id and the existing partition-key property.
    [<Test>]
    member _.ExactRelationshipWriteDocumentUsesCosmosPropertyNames() =
        let document = createExactRelationshipWriteDocument "relationship-id" "relationship-partition"

        Assert.That(document.Keys, Does.Contain("id"))
        Assert.That(document.Keys, Does.Not.Contain("Id"))
        Assert.That(document["id"], Is.EqualTo("relationship-id"))
        Assert.That(document["PartitionKey"], Is.EqualTo("relationship-partition"))

    /// Verifies duplicate subscriber delivery converges through the same exact identities for every ReferenceType.
    [<Test>]
    member _.ReferenceCreatedDuplicateConvergenceIsReferenceTypeNeutral() =
        task {
            let referenceTypes =
                [
                    ReferenceType.Promotion
                    ReferenceType.Commit
                    ReferenceType.Checkpoint
                    ReferenceType.Save
                    ReferenceType.Tag
                    ReferenceType.External
                    ReferenceType.Rebase
                ]

            for referenceType in referenceTypes do
                let storedRelationships = HashSet<ExactRelationship>()
                let mutable lifecycleCount = 0
                let mutable contributionCount = 0

                let store =
                    { new IExactRelationshipStore with
                        member _.EnsurePresentAsync(relationship, _) =
                            Task.FromResult(
                                if storedRelationships.Add relationship then
                                    ExactRelationshipWriteOutcome.Changed
                                else
                                    ExactRelationshipWriteOutcome.AlreadyConverged
                            )

                        member _.EnsureAbsentAsync(relationship, _) =
                            Task.FromResult(
                                if storedRelationships.Remove relationship then
                                    ExactRelationshipWriteOutcome.Changed
                                else
                                    ExactRelationshipWriteOutcome.AlreadyConverged
                            )

                        member _.EnumerateAsync(_, _, _, _) = Task.FromResult { Relationships = storedRelationships |> Seq.toArray; ContinuationToken = None }

                        member _.VerifyAsync(relationship, _) =
                            Task.FromResult(
                                if storedRelationships.Contains relationship then
                                    ExactRelationshipPresence.Present
                                else
                                    ExactRelationshipPresence.Absent
                            )
                    }

                let dependencies: ManifestContributionAccountingDependencies =
                    {
                        GetReference =
                            fun _ _ _ ->
                                Task.FromResult
                                    { ReferenceDto.Default with
                                        ReferenceId = referenceId
                                        RepositoryId = repositoryId
                                        DirectoryId = currentDirectoryVersionId
                                        ReferenceType = referenceType
                                        UpdatedAt = Some(getCurrentInstant ())
                                    }
                        GetDirectoryVersion = fun _ _ _ -> Task.FromResult(currentDirectoryVersionDto ())
                        ExactRelationships = store
                        EnsureAutomaticPhysicalDeletionReminder =
                            fun _ _ _ _ ->
                                lifecycleCount <- lifecycleCount + 1
                                Task.CompletedTask
                        EnsureDirectoryVersionManifest =
                            fun relationship _ _ cancellationToken ->
                                ensureDirectoryVersionManifestWith
                                    (fun exactRelationship cancellationToken -> store.VerifyAsync(exactRelationship, cancellationToken))
                                    (fun () ->
                                        contributionCount <- contributionCount + 1
                                        Task.CompletedTask)
                                    (fun exactRelationship cancellationToken -> store.EnsurePresentAsync(exactRelationship, cancellationToken))
                                    (ExactRelationship.DirectoryVersionManifest relationship)
                                    cancellationToken
                    }

                let delivery = staleCreatedEvent referenceType
                do! handleReferenceCreatedWith dependencies CancellationToken.None delivery
                do! handleReferenceCreatedWith dependencies CancellationToken.None delivery

                Assert.That(lifecycleCount, Is.EqualTo(2), $"Expected {referenceType} lifecycle convergence on each idempotent delivery.")
                Assert.That(contributionCount, Is.EqualTo(1), $"Expected {referenceType} contribution work to converge.")
                Assert.That(storedRelationships.Count, Is.EqualTo(2), $"Expected {referenceType} to retain one root and one manifest relationship.")
        }

    /// Verifies stale event fields cannot override fresh Reference and DirectoryVersion actor state.
    [<Test>]
    member _.ReferenceCreatedConvergenceRereadsCurrentActorStateBeforeExactWrites() =
        task {
            let exactWrites = ResizeArray<ExactRelationship>()
            let manifestWrites = ResizeArray<DirectoryVersionManifestRelationship>()
            let effectOrder = ResizeArray<string>()
            let mutable referenceReads = 0
            let mutable directoryReads = 0
            let _, manifest = manifestBackedFile ()

            let dependencies: ManifestContributionAccountingDependencies =
                {
                    GetReference =
                        fun _ _ _ ->
                            referenceReads <- referenceReads + 1

                            Task.FromResult
                                { ReferenceDto.Default with
                                    ReferenceId = referenceId
                                    RepositoryId = repositoryId
                                    DirectoryId = currentDirectoryVersionId
                                    ReferenceType = ReferenceType.Commit
                                    UpdatedAt = Some(getCurrentInstant ())
                                }
                    GetDirectoryVersion =
                        fun _ _ _ ->
                            directoryReads <- directoryReads + 1
                            Task.FromResult(currentDirectoryVersionDto ())
                    ExactRelationships = recordingStore exactWrites effectOrder
                    EnsureAutomaticPhysicalDeletionReminder =
                        fun _ _ _ _ ->
                            effectOrder.Add("lifecycle")
                            Task.CompletedTask
                    EnsureDirectoryVersionManifest =
                        fun relationship currentManifest _ _ ->
                            Assert.That(currentManifest, Is.EqualTo(manifest))
                            manifestWrites.Add(relationship)
                            Task.CompletedTask
                }

            do! handleReferenceCreatedWith dependencies CancellationToken.None (staleCreatedEvent ReferenceType.Commit)

            Assert.That(referenceReads, Is.EqualTo(1))
            Assert.That(directoryReads, Is.EqualTo(2), "The current root is read to discover candidates and reread immediately before the manifest mutation.")
            Assert.That(exactWrites.Count, Is.EqualTo(1))
            Assert.That(effectOrder |> Seq.toArray, Is.EqualTo<string array>([| "exact-root"; "lifecycle" |]))

            match exactWrites[0] with
            | ExactRelationship.ReferenceRoot relationship ->
                Assert.That(relationship.RootDirectoryVersionId, Is.EqualTo(currentDirectoryVersionId))
                Assert.That(relationship.ReferenceId, Is.EqualTo(referenceId))
            | _ -> Assert.Fail("Expected one current Reference-root relationship.")

            Assert.That(manifestWrites.Count, Is.EqualTo(1))
            Assert.That(manifestWrites[0].DirectoryVersionId, Is.EqualTo(currentDirectoryVersionId))
            Assert.That(manifestWrites[0].StoragePoolId, Is.EqualTo(storagePoolId))
            Assert.That(manifestWrites[0].ManifestAddress, Is.EqualTo(manifestAddress))
        }

    /// Verifies a pending Save lifecycle write cannot delay Reference-wide manifest accounting.
    [<Test>]
    member _.PendingReferenceLifecycleDoesNotBlockManifestAccounting() =
        task {
            let exactWrites = ResizeArray<ExactRelationship>()
            let manifestWrites = ResizeArray<DirectoryVersionManifestRelationship>()
            let effectOrder = ResizeArray<string>()
            let lifecycleStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseLifecycle = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let dependencies: ManifestContributionAccountingDependencies =
                {
                    GetReference =
                        fun _ _ _ ->
                            Task.FromResult
                                { ReferenceDto.Default with
                                    ReferenceId = referenceId
                                    RepositoryId = repositoryId
                                    DirectoryId = currentDirectoryVersionId
                                    ReferenceType = ReferenceType.Save
                                    UpdatedAt = Some(getCurrentInstant ())
                                }
                    GetDirectoryVersion = fun _ _ _ -> Task.FromResult(currentDirectoryVersionDto ())
                    ExactRelationships = recordingStore exactWrites effectOrder
                    EnsureAutomaticPhysicalDeletionReminder =
                        fun _ _ _ _ ->
                            task {
                                effectOrder.Add("lifecycle")
                                lifecycleStarted.SetResult()
                                do! releaseLifecycle.Task
                            }
                            :> Task
                    EnsureDirectoryVersionManifest =
                        fun relationship _ _ _ ->
                            effectOrder.Add("manifest")
                            manifestWrites.Add(relationship)
                            Task.CompletedTask
                }

            let operation = handleReferenceCreatedWith dependencies CancellationToken.None (staleCreatedEvent ReferenceType.Save)
            do! lifecycleStarted.Task.WaitAsync(TimeSpan.FromSeconds(1.0))

            let exactWritesBeforeLifecycleCompleted = exactWrites.Count
            let manifestWritesBeforeLifecycleCompleted = manifestWrites.Count
            let effectOrderBeforeLifecycleCompleted = effectOrder |> Seq.toArray

            releaseLifecycle.SetResult()
            do! operation

            Assert.That(exactWritesBeforeLifecycleCompleted, Is.EqualTo(1))
            Assert.That(manifestWritesBeforeLifecycleCompleted, Is.EqualTo(1))

            Assert.That(
                effectOrderBeforeLifecycleCompleted,
                Is.EqualTo<string array>(
                    [|
                        "exact-root"
                        "manifest"
                        "lifecycle"
                    |]
                )
            )
        }

    /// Verifies a failed lifecycle effect does not roll back already-converged Reference accounting.
    [<Test>]
    member _.ReferenceLifecycleFailurePreservesExactRootWrite() =
        task {
            let exactWrites = ResizeArray<ExactRelationship>()
            let effectOrder = ResizeArray<string>()

            let dependencies: ManifestContributionAccountingDependencies =
                {
                    GetReference =
                        fun _ _ _ ->
                            Task.FromResult
                                { ReferenceDto.Default with
                                    ReferenceId = referenceId
                                    RepositoryId = repositoryId
                                    DirectoryId = currentDirectoryVersionId
                                    ReferenceType = ReferenceType.Save
                                    UpdatedAt = Some(getCurrentInstant ())
                                }
                    GetDirectoryVersion = fun _ _ _ -> Task.FromResult(currentDirectoryVersionDto ())
                    ExactRelationships = recordingStore exactWrites effectOrder
                    EnsureAutomaticPhysicalDeletionReminder =
                        fun _ _ _ _ ->
                            effectOrder.Add("lifecycle")
                            Task.FromException(InvalidOperationException("reminder scheduling failed"))
                    EnsureDirectoryVersionManifest =
                        fun _ _ _ _ ->
                            effectOrder.Add("manifest")
                            Task.CompletedTask
                }

            let operation = handleReferenceCreatedWith dependencies CancellationToken.None (staleCreatedEvent ReferenceType.Commit)
            let _ = Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> operation))

            Assert.That(
                effectOrder |> Seq.toArray,
                Is.EqualTo<string array>(
                    [|
                        "exact-root"
                        "manifest"
                        "lifecycle"
                    |]
                )
            )

            Assert.That(exactWrites.Count, Is.EqualTo(1))
        }

    /// Verifies a conflicting stable reminder target fails delivery after independent Reference accounting converges.
    [<Test>]
    member _.ReferenceLifecycleConflictPreservesExactRootWrite() =
        task {
            let exactWrites = ResizeArray<ExactRelationship>()
            let effectOrder = ResizeArray<string>()

            let dependencies: ManifestContributionAccountingDependencies =
                {
                    GetReference =
                        fun _ _ _ ->
                            Task.FromResult
                                { ReferenceDto.Default with
                                    ReferenceId = referenceId
                                    RepositoryId = repositoryId
                                    DirectoryId = currentDirectoryVersionId
                                    ReferenceType = ReferenceType.Checkpoint
                                    UpdatedAt = Some(getCurrentInstant ())
                                }
                    GetDirectoryVersion = fun _ _ _ -> Task.FromResult(currentDirectoryVersionDto ())
                    ExactRelationships = recordingStore exactWrites effectOrder
                    EnsureAutomaticPhysicalDeletionReminder =
                        fun _ _ _ _ ->
                            effectOrder.Add("lifecycle-conflict")
                            Task.FromException(InvalidOperationException("stable reminder targets different Reference data"))
                    EnsureDirectoryVersionManifest =
                        fun _ _ _ _ ->
                            effectOrder.Add("manifest")
                            Task.CompletedTask
                }

            let operation = handleReferenceCreatedWith dependencies CancellationToken.None (staleCreatedEvent ReferenceType.Checkpoint)
            let error = Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> operation))

            Assert.That(error.Message, Does.Contain("different Reference data"))

            Assert.That(
                effectOrder |> Seq.toArray,
                Is.EqualTo<string array>(
                    [|
                        "exact-root"
                        "manifest"
                        "lifecycle-conflict"
                    |]
                )
            )

            Assert.That(exactWrites.Count, Is.EqualTo(1))
        }

    /// Verifies response loss after stable lifecycle persistence retries once and records one Reference root.
    [<Test>]
    member _.ReferenceLifecycleResponseLossPreservesFirstScheduleAndWritesOneRootOnRetry() =
        task {
            let storedRelationships = HashSet<ExactRelationship>()
            let mutable lifecycleAttempts = 0
            let mutable durableReminder: struct (Guid * Instant * string) option = None
            let firstSchedule = Instant.FromUtc(2026, 8, 1, 8, 0)
            let retrySchedule = Instant.FromUtc(2026, 8, 2, 8, 0)

            let store =
                { new IExactRelationshipStore with
                    member _.EnsurePresentAsync(relationship, _) =
                        Task.FromResult(
                            if storedRelationships.Add relationship then
                                ExactRelationshipWriteOutcome.Changed
                            else
                                ExactRelationshipWriteOutcome.AlreadyConverged
                        )

                    member _.EnsureAbsentAsync(_, _) = Task.FromResult ExactRelationshipWriteOutcome.AlreadyConverged
                    member _.EnumerateAsync(_, _, _, _) = Task.FromResult { Relationships = storedRelationships |> Seq.toArray; ContinuationToken = None }

                    member _.VerifyAsync(relationship, _) =
                        Task.FromResult(
                            if storedRelationships.Contains relationship then
                                ExactRelationshipPresence.Present
                            else
                                ExactRelationshipPresence.Absent
                        )
                }

            let dependencies: ManifestContributionAccountingDependencies =
                {
                    GetReference =
                        fun _ _ _ ->
                            Task.FromResult
                                { ReferenceDto.Default with
                                    ReferenceId = referenceId
                                    RepositoryId = repositoryId
                                    DirectoryId = currentDirectoryVersionId
                                    ReferenceType = ReferenceType.Save
                                    UpdatedAt = Some(getCurrentInstant ())
                                }
                    GetDirectoryVersion = fun _ _ _ -> Task.FromResult { currentDirectoryVersionDto () with DirectoryVersion = DirectoryVersion.Default }
                    ExactRelationships = store
                    EnsureAutomaticPhysicalDeletionReminder =
                        fun currentRepositoryId currentReferenceId correlationId _ ->
                            lifecycleAttempts <- lifecycleAttempts + 1
                            let reminderId = Grace.Actors.Reference.automaticPhysicalDeletionReminderId currentRepositoryId currentReferenceId
                            let schedule = if lifecycleAttempts = 1 then firstSchedule else retrySchedule

                            match durableReminder with
                            | None -> durableReminder <- Some(struct (reminderId, schedule, correlationId))
                            | Some _ -> ()

                            if lifecycleAttempts = 1 then
                                Task.FromException(TimeoutException("reminder response lost after persistence"))
                            else
                                Task.CompletedTask
                    EnsureDirectoryVersionManifest = fun _ _ _ _ -> Task.CompletedTask
                }

            let delivery = staleCreatedEvent ReferenceType.Save
            let retryDelivery = { delivery with Metadata = EventMetadata.New "mca-tracer-retry" "unit-test" }

            let first = handleReferenceCreatedWith dependencies CancellationToken.None delivery
            let _ = Assert.ThrowsAsync<TimeoutException>(Func<Task>(fun () -> first))
            do! handleReferenceCreatedWith dependencies CancellationToken.None retryDelivery

            let expectedReminderId = Grace.Actors.Reference.automaticPhysicalDeletionReminderId repositoryId referenceId

            match durableReminder with
            | Some (struct (reminderId, schedule, correlationId)) ->
                Assert.That(reminderId, Is.EqualTo(expectedReminderId))
                Assert.That(schedule, Is.EqualTo(firstSchedule))
                Assert.That(correlationId, Is.EqualTo(delivery.Metadata.CorrelationId))
            | None -> Assert.Fail("Expected one durable lifecycle reminder.")

            Assert.That(lifecycleAttempts, Is.EqualTo(2))

            let roots =
                storedRelationships
                |> Seq.choose (function
                    | ExactRelationship.ReferenceRoot root -> Some root
                    | _ -> None)
                |> Seq.toArray

            Assert.That(roots.Length, Is.EqualTo(1))
        }

    /// Verifies an already-present exact item prevents duplicate counter or ContentBlock contribution work.
    [<Test>]
    member _.PresentDirectoryVersionManifestSkipsContribution() =
        task {
            let relationship =
                ExactRelationship.DirectoryVersionManifest
                    {
                        RepositoryId = repositoryId
                        StoragePoolId = storagePoolId
                        ManifestAddress = manifestAddress
                        DirectoryVersionId = currentDirectoryVersionId
                    }

            let mutable contributionCalls = 0
            let mutable writeCalls = 0

            let! outcome =
                ensureDirectoryVersionManifestWith
                    (fun _ _ -> Task.FromResult ExactRelationshipPresence.Present)
                    (fun () ->
                        contributionCalls <- contributionCalls + 1
                        Task.CompletedTask)
                    (fun _ _ ->
                        writeCalls <- writeCalls + 1
                        Task.FromResult ExactRelationshipWriteOutcome.Changed)
                    relationship
                    CancellationToken.None

            Assert.That(outcome, Is.EqualTo(ExactRelationshipWriteOutcome.AlreadyConverged))
            Assert.That(contributionCalls, Is.Zero)
            Assert.That(writeCalls, Is.Zero)
        }

    /// Verifies retry after a lost counter response can replay the stable operation before recording the exact item.
    [<Test>]
    member _.CounterResponseLossRetriesBeforeExactRelationshipWrite() =
        task {
            let relationship =
                ExactRelationship.DirectoryVersionManifest
                    {
                        RepositoryId = repositoryId
                        StoragePoolId = storagePoolId
                        ManifestAddress = manifestAddress
                        DirectoryVersionId = currentDirectoryVersionId
                    }

            let mutable durableCount = 0
            let mutable contributionAttempts = 0
            let mutable relationshipPresent = false

            let applyContribution () =
                contributionAttempts <- contributionAttempts + 1

                if durableCount = 0 then durableCount <- 1

                if contributionAttempts = 1 then
                    Task.FromException(TimeoutException("counter response lost"))
                else
                    Task.CompletedTask

            let verify _ _ =
                Task.FromResult(
                    if relationshipPresent then
                        ExactRelationshipPresence.Present
                    else
                        ExactRelationshipPresence.Absent
                )

            let ensurePresent _ _ =
                relationshipPresent <- true
                Task.FromResult ExactRelationshipWriteOutcome.Changed

            let first = ensureDirectoryVersionManifestWith verify applyContribution ensurePresent relationship CancellationToken.None

            let _ = Assert.ThrowsAsync<TimeoutException>(Func<Task>(fun () -> first :> Task))

            let! second = ensureDirectoryVersionManifestWith verify applyContribution ensurePresent relationship CancellationToken.None

            Assert.That(second, Is.EqualTo(ExactRelationshipWriteOutcome.Changed))
            Assert.That(contributionAttempts, Is.EqualTo(2))
            Assert.That(durableCount, Is.EqualTo(1))
            Assert.That(relationshipPresent, Is.True)
        }
