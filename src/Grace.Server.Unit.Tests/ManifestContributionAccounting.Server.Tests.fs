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

    /// Builds one current DirectoryVersion actor snapshot for a nested traversal witness.
    let nestedDirectoryVersionDto directoryVersionId relativePath directories files =
        {
            DirectoryVersion =
                DirectoryVersion.CreateWithHashes
                    directoryVersionId
                    ownerId
                    organizationId
                    repositoryId
                    relativePath
                    (Sha256Hash $"sha-{directoryVersionId:N}")
                    (Blake3Hash $"b3-{directoryVersionId:N}")
                    directories
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

            Assert.That(referenceReads, Is.EqualTo(4), "The Reference is reread before the root write, manifest mutation, and lifecycle convergence.")
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

    /// Verifies a diamond DAG retains one shared manifest after traversing each newly retained parent.
    [<Test>]
    member _.ReferenceCreatedTraversesDiamondAndAttributesSharedManifestToDirectOwnerOnce() =
        task {
            let leftDirectoryVersionId = Guid.Parse("88888888-7290-4000-8000-888888888888")
            let rightDirectoryVersionId = Guid.Parse("89898989-7290-4000-8000-898989898989")
            let sharedDirectoryVersionId = Guid.Parse("90909090-7290-4000-8000-909090909090")
            let rootDirectories = List<DirectoryVersionId>()
            rootDirectories.Add(leftDirectoryVersionId)
            rootDirectories.Add(rightDirectoryVersionId)
            let sharedDirectories = List<DirectoryVersionId>()
            sharedDirectories.Add(sharedDirectoryVersionId)
            let sharedFiles = List<FileVersion>()
            let file, manifest = manifestBackedFile ()
            sharedFiles.Add(file)

            let root = nestedDirectoryVersionDto currentDirectoryVersionId (RelativePath ".") rootDirectories (List<FileVersion>())

            let left = nestedDirectoryVersionDto leftDirectoryVersionId (RelativePath "left") sharedDirectories (List<FileVersion>())
            let right = nestedDirectoryVersionDto rightDirectoryVersionId (RelativePath "right") sharedDirectories (List<FileVersion>())

            let shared = nestedDirectoryVersionDto sharedDirectoryVersionId (RelativePath "shared") (List<DirectoryVersionId>()) sharedFiles

            let storedRelationships = HashSet<ExactRelationship>()
            let manifestWrites = ResizeArray<DirectoryVersionManifestRelationship>()
            let effectOrder = ResizeArray<string>()

            let store =
                { new IExactRelationshipStore with
                    member _.EnsurePresentAsync(relationship, _) =
                        let outcome =
                            if storedRelationships.Add relationship then
                                effectOrder.Add(
                                    match relationship with
                                    | ExactRelationship.ReferenceRoot _ -> "reference-root"
                                    | ExactRelationship.ParentChild _ -> "parent-child"
                                    | ExactRelationship.DirectoryVersionManifest _ -> "directory-version-manifest"
                                )

                                ExactRelationshipWriteOutcome.Changed
                            else
                                ExactRelationshipWriteOutcome.AlreadyConverged

                        Task.FromResult outcome

                    member _.EnsureAbsentAsync(_, _) = Task.FromResult ExactRelationshipWriteOutcome.AlreadyConverged

                    member _.EnumerateAsync(partition, bound, _, _) =
                        let maximumCount = ExactRelationshipReadBound.value bound

                        Task.FromResult
                            {
                                Relationships =
                                    storedRelationships
                                    |> Seq.filter (fun relationship -> ExactRelationshipKey.partition relationship = Ok partition)
                                    |> Seq.truncate maximumCount
                                    |> Seq.toArray
                                ContinuationToken = None
                            }

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
                    GetDirectoryVersion =
                        fun _ directoryVersionId _ ->
                            Task.FromResult(
                                if directoryVersionId = currentDirectoryVersionId then root
                                elif directoryVersionId = leftDirectoryVersionId then left
                                elif directoryVersionId = rightDirectoryVersionId then right
                                elif directoryVersionId = sharedDirectoryVersionId then shared
                                else DirectoryVersionDto.Default
                            )
                    ExactRelationships = store
                    EnsureAutomaticPhysicalDeletionReminder = fun _ _ _ _ -> Task.CompletedTask
                    EnsureDirectoryVersionManifest =
                        fun relationship currentManifest _ cancellationToken ->
                            task {
                                Assert.That(currentManifest, Is.EqualTo(manifest))
                                manifestWrites.Add(relationship)
                                effectOrder.Add("manifest-effect")

                                let! _ = store.EnsurePresentAsync(ExactRelationship.DirectoryVersionManifest relationship, cancellationToken)

                                return ()
                            }
                            :> Task
                }

            do! handleReferenceCreatedWith dependencies CancellationToken.None (staleCreatedEvent ReferenceType.Save)

            let expectedEdges =
                [|
                    currentDirectoryVersionId, leftDirectoryVersionId
                    currentDirectoryVersionId, rightDirectoryVersionId
                    leftDirectoryVersionId, sharedDirectoryVersionId
                    rightDirectoryVersionId, sharedDirectoryVersionId
                |]

            expectedEdges
            |> Array.iter (fun (parentDirectoryVersionId, childDirectoryVersionId) ->
                Assert.That(
                    storedRelationships,
                    Does.Contain(
                        ExactRelationship.ParentChild
                            {
                                RepositoryId = repositoryId
                                ParentDirectoryVersionId = parentDirectoryVersionId
                                ChildDirectoryVersionId = childDirectoryVersionId
                            }
                    )
                ))

            Assert.That(manifestWrites.Count, Is.EqualTo(1))
            Assert.That(manifestWrites[0].DirectoryVersionId, Is.EqualTo(sharedDirectoryVersionId))
            Assert.That(manifestWrites[0].StoragePoolId, Is.EqualTo(storagePoolId))
            Assert.That(manifestWrites[0].ManifestAddress, Is.EqualTo(manifestAddress))

            Assert.That(effectOrder[0], Is.EqualTo("reference-root"))
            Assert.That(effectOrder[1], Is.EqualTo("manifest-effect"))
            Assert.That(effectOrder[2], Is.EqualTo("directory-version-manifest"))
            Assert.That(effectOrder[3], Is.EqualTo("parent-child"), "Shared child contents must converge before its first incoming edge.")

            Assert.That(
                effectOrder
                |> Seq.filter ((=) "parent-child")
                |> Seq.length,
                Is.EqualTo(4)
            )
        }

    /// Verifies an already-retained shared child gains the new edge without walking its subtree again.
    [<Test>]
    member _.ReferenceCreatedStopsAtSharedChildWithExistingIncomingRelationship() =
        task {
            let childDirectoryVersionId = Guid.Parse("aaaaaaaa-7290-4000-8000-aaaaaaaaaaaa")
            let existingReferenceId = Guid.Parse("bbbbbbbb-7290-4000-8000-bbbbbbbbbbbb")
            let rootDirectories = List<DirectoryVersionId>()
            rootDirectories.Add(childDirectoryVersionId)

            let root = nestedDirectoryVersionDto currentDirectoryVersionId (RelativePath ".") rootDirectories (List<FileVersion>())

            let child =
                let childFiles = List<FileVersion>()
                let file, _ = manifestBackedFile ()
                childFiles.Add(file)

                nestedDirectoryVersionDto childDirectoryVersionId (RelativePath "shared") (List<DirectoryVersionId>()) childFiles

            let storedRelationships =
                HashSet<ExactRelationship>(
                    [
                        ExactRelationship.ReferenceRoot
                            { RepositoryId = repositoryId; RootDirectoryVersionId = childDirectoryVersionId; ReferenceId = existingReferenceId }
                    ]
                )

            let mutable childReads = 0
            let mutable manifestWrites = 0

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

                    member _.EnumerateAsync(partition, bound, _, _) =
                        let maximumCount = ExactRelationshipReadBound.value bound

                        Task.FromResult
                            {
                                Relationships =
                                    storedRelationships
                                    |> Seq.filter (fun relationship -> ExactRelationshipKey.partition relationship = Ok partition)
                                    |> Seq.truncate maximumCount
                                    |> Seq.toArray
                                ContinuationToken = None
                            }

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
                                    ReferenceType = ReferenceType.Tag
                                    UpdatedAt = Some(getCurrentInstant ())
                                }
                    GetDirectoryVersion =
                        fun _ directoryVersionId _ ->
                            if directoryVersionId = currentDirectoryVersionId then
                                Task.FromResult root
                            else
                                childReads <- childReads + 1
                                Task.FromResult child
                    ExactRelationships = store
                    EnsureAutomaticPhysicalDeletionReminder = fun _ _ _ _ -> Task.CompletedTask
                    EnsureDirectoryVersionManifest =
                        fun _ _ _ _ ->
                            manifestWrites <- manifestWrites + 1
                            Task.CompletedTask
                }

            do! handleReferenceCreatedWith dependencies CancellationToken.None (staleCreatedEvent ReferenceType.Tag)

            Assert.That(
                storedRelationships,
                Does.Contain(
                    ExactRelationship.ParentChild
                        { RepositoryId = repositoryId; ParentDirectoryVersionId = currentDirectoryVersionId; ChildDirectoryVersionId = childDirectoryVersionId }
                )
            )

            Assert.That(childReads, Is.EqualTo(1), "The child is reread only to validate the exact edge before mutation.")
            Assert.That(manifestWrites, Is.Zero, "An existing incoming edge proves the shared child's contents were already converged.")
        }

    /// Verifies a child removed from current parent state cannot create stale child or manifest relationships.
    [<Test>]
    member _.ReferenceCreatedRevalidatesNestedDirectoryVersionBeforeRelationshipMutations() =
        task {
            let childDirectoryVersionId = Guid.Parse("99999999-7290-4000-8000-999999999999")
            let candidateRootDirectories = List<DirectoryVersionId>()
            candidateRootDirectories.Add(childDirectoryVersionId)
            let currentRootDirectories = List<DirectoryVersionId>()
            let childFiles = List<FileVersion>()
            let file, _ = manifestBackedFile ()
            childFiles.Add(file)

            let candidateRoot = nestedDirectoryVersionDto currentDirectoryVersionId (RelativePath ".") candidateRootDirectories (List<FileVersion>())

            let currentRoot = nestedDirectoryVersionDto currentDirectoryVersionId (RelativePath ".") currentRootDirectories (List<FileVersion>())

            let child = nestedDirectoryVersionDto childDirectoryVersionId (RelativePath "nested") (List<DirectoryVersionId>()) childFiles

            let exactWrites = ResizeArray<ExactRelationship>()
            let manifestWrites = ResizeArray<DirectoryVersionManifestRelationship>()
            let mutable rootReads = 0

            let dependencies: ManifestContributionAccountingDependencies =
                {
                    GetReference =
                        fun _ _ _ ->
                            Task.FromResult
                                { ReferenceDto.Default with
                                    ReferenceId = referenceId
                                    RepositoryId = repositoryId
                                    DirectoryId = currentDirectoryVersionId
                                    ReferenceType = ReferenceType.Commit
                                    UpdatedAt = Some(getCurrentInstant ())
                                }
                    GetDirectoryVersion =
                        fun _ directoryVersionId _ ->
                            if directoryVersionId = currentDirectoryVersionId then
                                rootReads <- rootReads + 1
                                Task.FromResult(if rootReads = 1 then candidateRoot else currentRoot)
                            else
                                Task.FromResult child
                    ExactRelationships = recordingStore exactWrites (ResizeArray<string>())
                    EnsureAutomaticPhysicalDeletionReminder = fun _ _ _ _ -> Task.CompletedTask
                    EnsureDirectoryVersionManifest =
                        fun relationship _ _ _ ->
                            manifestWrites.Add(relationship)
                            Task.CompletedTask
                }

            do! handleReferenceCreatedWith dependencies CancellationToken.None (staleCreatedEvent ReferenceType.Commit)

            Assert.That(
                exactWrites
                |> Seq.exists (function
                    | ExactRelationship.ParentChild _ -> true
                    | _ -> false),
                Is.False
            )

            Assert.That(manifestWrites, Is.Empty)
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

    /// Verifies a current incoming relationship blocks physical deletion before any outgoing mutation.
    [<Test>]
    member _.DirectoryVersionPhysicalDeletionStopsBeforeMutationWhenIncomingRelationshipExists() =
        task {
            let dto = currentDirectoryVersionDto ()

            let incoming =
                ExactRelationship.ReferenceRoot { RepositoryId = repositoryId; RootDirectoryVersionId = currentDirectoryVersionId; ReferenceId = referenceId }

            let mutable releaseCalls = 0
            let mutable removeCalls = 0

            let dependencies: Grace.Actors.DirectoryVersion.PhysicalDeletionDependencies =
                {
                    EnumerateIncoming = fun _ _ _ -> Task.FromResult [| incoming |]
                    Verify = fun _ _ -> Task.FromResult ExactRelationshipPresence.Present
                    EnsureAbsent =
                        fun _ _ ->
                            removeCalls <- removeCalls + 1
                            Task.FromResult ExactRelationshipWriteOutcome.Changed
                    ReleaseManifest =
                        fun _ _ _ _ ->
                            releaseCalls <- releaseCalls + 1
                            Task.CompletedTask
                }

            let! disposition =
                Grace.Actors.DirectoryVersion.convergePhysicalDeletionWith
                    dependencies
                    dto
                    (EventMetadata.New "directory-delete-blocked" "unit-test")
                    CancellationToken.None

            Assert.That(disposition, Is.EqualTo(Grace.Actors.DirectoryVersion.PhysicalDeletionDisposition.BlockedByIncomingRelationship))
            Assert.That(releaseCalls, Is.Zero)
            Assert.That(removeCalls, Is.Zero)
        }

    /// Verifies restart resumes from exact outgoing evidence after manifest release returns unknown.
    [<Test>]
    member _.DirectoryVersionPhysicalDeletionRetainsExactEvidenceUntilReleaseCompletes() =
        task {
            let dto = currentDirectoryVersionDto ()
            let childDirectoryVersionId = Guid.Parse("88888888-7290-4000-8000-888888888888")
            dto.DirectoryVersion.Directories.Add(childDirectoryVersionId)

            let manifestRelationship =
                ExactRelationship.DirectoryVersionManifest
                    {
                        RepositoryId = repositoryId
                        StoragePoolId = storagePoolId
                        ManifestAddress = manifestAddress
                        DirectoryVersionId = currentDirectoryVersionId
                    }

            let childRelationship =
                ExactRelationship.ParentChild
                    { RepositoryId = repositoryId; ParentDirectoryVersionId = currentDirectoryVersionId; ChildDirectoryVersionId = childDirectoryVersionId }

            let present =
                HashSet<ExactRelationship>(
                    [|
                        manifestRelationship
                        childRelationship
                    |]
                )

            let calls = ResizeArray<string>()
            let mutable releaseAttempts = 0

            let dependencies: Grace.Actors.DirectoryVersion.PhysicalDeletionDependencies =
                {
                    EnumerateIncoming = fun _ _ _ -> Task.FromResult Array.empty
                    Verify =
                        fun relationship _ ->
                            Task.FromResult(
                                if present.Contains relationship then
                                    ExactRelationshipPresence.Present
                                else
                                    ExactRelationshipPresence.Absent
                            )
                    EnsureAbsent =
                        fun relationship _ ->
                            calls.Add($"remove:{relationship}")
                            let changed = present.Remove relationship

                            Task.FromResult(
                                if changed then
                                    ExactRelationshipWriteOutcome.Changed
                                else
                                    ExactRelationshipWriteOutcome.AlreadyConverged
                            )
                    ReleaseManifest =
                        fun relationship _ _ _ ->
                            releaseAttempts <- releaseAttempts + 1
                            calls.Add($"release:{relationship.ManifestAddress}")

                            if releaseAttempts = 1 then
                                Task.FromException(TimeoutException("counter response lost"))
                            else
                                Task.CompletedTask
                }

            let metadata = EventMetadata.New "directory-delete-restart" "unit-test"
            let first = Grace.Actors.DirectoryVersion.convergePhysicalDeletionWith dependencies dto metadata CancellationToken.None
            let _ = Assert.ThrowsAsync<TimeoutException>(Func<Task>(fun () -> first :> Task))

            Assert.That(present.Contains manifestRelationship, Is.True)
            Assert.That(present.Contains childRelationship, Is.True)
            Assert.That(calls, Is.EqualTo<string array>([| $"release:{manifestAddress}" |]))

            let! disposition = Grace.Actors.DirectoryVersion.convergePhysicalDeletionWith dependencies dto metadata CancellationToken.None

            Assert.That(disposition, Is.EqualTo(Grace.Actors.DirectoryVersion.PhysicalDeletionDisposition.ReadyToClear))
            Assert.That(present, Is.Empty)
            Assert.That(releaseAttempts, Is.EqualTo(2))
            Assert.That(calls[1], Is.EqualTo($"release:{manifestAddress}"))
            Assert.That(calls[2], Does.StartWith("remove:DirectoryVersionManifest"))
            Assert.That(calls[3], Does.StartWith("remove:ParentChild"))
        }

    /// Verifies final exact verification prevents actor-state clear when a removal has not converged.
    [<Test>]
    member _.DirectoryVersionPhysicalDeletionRequiresFinalOutgoingAbsence() =
        task {
            let dto = currentDirectoryVersionDto ()
            let mutable verifyCalls = 0

            let dependencies: Grace.Actors.DirectoryVersion.PhysicalDeletionDependencies =
                {
                    EnumerateIncoming = fun _ _ _ -> Task.FromResult Array.empty
                    Verify =
                        fun _ _ ->
                            verifyCalls <- verifyCalls + 1
                            Task.FromResult ExactRelationshipPresence.Present
                    EnsureAbsent = fun _ _ -> Task.FromResult ExactRelationshipWriteOutcome.Changed
                    ReleaseManifest = fun _ _ _ _ -> Task.CompletedTask
                }

            let! disposition =
                Grace.Actors.DirectoryVersion.convergePhysicalDeletionWith
                    dependencies
                    dto
                    (EventMetadata.New "directory-delete-final-verify" "unit-test")
                    CancellationToken.None

            Assert.That(disposition, Is.EqualTo(Grace.Actors.DirectoryVersion.PhysicalDeletionDisposition.OutgoingRelationshipStillPresent))
            Assert.That(verifyCalls, Is.EqualTo(2))
        }

    /// Verifies an unknown exact-removal outcome stops deletion before later outgoing evidence is removed.
    [<Test>]
    member _.DirectoryVersionPhysicalDeletionStopsAfterUnknownExactRemoval() =
        task {
            let dto = currentDirectoryVersionDto ()
            let childDirectoryVersionId = Guid.Parse("99999999-7290-4000-8000-999999999999")
            dto.DirectoryVersion.Directories.Add(childDirectoryVersionId)

            let mutable releaseCalls = 0
            let mutable removeCalls = 0

            let dependencies: Grace.Actors.DirectoryVersion.PhysicalDeletionDependencies =
                {
                    EnumerateIncoming = fun _ _ _ -> Task.FromResult Array.empty
                    Verify = fun _ _ -> Task.FromResult ExactRelationshipPresence.Present
                    EnsureAbsent =
                        fun _ _ ->
                            removeCalls <- removeCalls + 1
                            Task.FromException<ExactRelationshipWriteOutcome>(TimeoutException("exact removal response lost"))
                    ReleaseManifest =
                        fun _ _ _ _ ->
                            releaseCalls <- releaseCalls + 1
                            Task.CompletedTask
                }

            let deletion =
                Grace.Actors.DirectoryVersion.convergePhysicalDeletionWith
                    dependencies
                    dto
                    (EventMetadata.New "directory-delete-exact-unknown" "unit-test")
                    CancellationToken.None

            let _ = Assert.ThrowsAsync<TimeoutException>(Func<Task>(fun () -> deletion :> Task))

            Assert.That(releaseCalls, Is.EqualTo(1))
            Assert.That(removeCalls, Is.EqualTo(1), "The child relationship must remain untouched after the manifest removal is unknown.")
        }

    /// Verifies a DirectoryVersion with no outgoing evidence clears after exactly one incoming check.
    [<Test>]
    member _.DirectoryVersionPhysicalDeletionConvergesWithoutOutgoingEvidence() =
        task {
            let dto = currentDirectoryVersionDto ()
            dto.DirectoryVersion.Files.Clear()
            let mutable incomingChecks = 0
            let mutable outgoingCalls = 0

            let dependencies: Grace.Actors.DirectoryVersion.PhysicalDeletionDependencies =
                {
                    EnumerateIncoming =
                        fun _ _ _ ->
                            incomingChecks <- incomingChecks + 1
                            Task.FromResult Array.empty
                    Verify =
                        fun _ _ ->
                            outgoingCalls <- outgoingCalls + 1
                            Task.FromResult ExactRelationshipPresence.Absent
                    EnsureAbsent =
                        fun _ _ ->
                            outgoingCalls <- outgoingCalls + 1
                            Task.FromResult ExactRelationshipWriteOutcome.AlreadyConverged
                    ReleaseManifest =
                        fun _ _ _ _ ->
                            outgoingCalls <- outgoingCalls + 1
                            Task.CompletedTask
                }

            let! disposition =
                Grace.Actors.DirectoryVersion.convergePhysicalDeletionWith
                    dependencies
                    dto
                    (EventMetadata.New "directory-delete-no-outgoing" "unit-test")
                    CancellationToken.None

            Assert.That(disposition, Is.EqualTo(Grace.Actors.DirectoryVersion.PhysicalDeletionDisposition.ReadyToClear))
            Assert.That(incomingChecks, Is.EqualTo(1))
            Assert.That(outgoingCalls, Is.Zero)
        }
