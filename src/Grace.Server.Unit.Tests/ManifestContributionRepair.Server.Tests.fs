namespace Grace.Server.Unit.Tests

open Giraffe
open Grace.Actors
open Grace.Server.ManifestContributionDiagnosis
open Grace.Server.ManifestContributionRepair
open Grace.Shared
open Grace.Types
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.ManifestContributionAccounting
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Reference
open Grace.Types.RepositoryContentCounter
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open NUnit.Framework
open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

module RepositoryContentCounterActor = Grace.Actors.RepositoryContentCounter
module ManifestContributionDiagnosis = Grace.Server.ManifestContributionDiagnosis
module ManifestContributionRepair = Grace.Server.ManifestContributionRepair

/// Proves finite manifest repair from production-reachable bounded diagnosis reports without starting Aspire.
[<Parallelizable(ParallelScope.All)>]
type ManifestContributionRepairServerTests() =

    let ownerId = Guid.Parse("11111111-7350-4000-8000-111111111111")

    let organizationId = Guid.Parse("22222222-7350-4000-8000-222222222222")

    let repositoryId = Guid.Parse("33333333-7350-4000-8000-333333333333")

    let rootDirectoryVersionId = Guid.Parse("44444444-7350-4000-8000-444444444444")

    let childDirectoryVersionId = Guid.Parse("55555555-7350-4000-8000-555555555555")

    let staleDirectoryVersionId = Guid.Parse("66666666-7350-4000-8000-666666666666")

    let referenceId = Guid.Parse("77777777-7350-4000-8000-777777777777")

    let storagePoolId = StoragePoolId "repair:pool"
    let fileContentHash = FileContentHash(String.replicate 64 "b")

    let finalizedManifest =
        let manifest =
            FileManifest.Create(
                ManifestAddress String.Empty,
                ChunkingSuiteId "fixed-v1",
                fileContentHash,
                4L,
                storagePoolId,
                [
                    ContentBlock.Create(ContentBlockAddress(String.replicate 64 "c"), 0L, 2L)
                    ContentBlock.Create(ContentBlockAddress(String.replicate 64 "d"), 2L, 2L)
                ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let manifestAddress = finalizedManifest.ManifestAddress

    let workflowRanges =
        finalizedManifest.Blocks
        |> Seq.distinctBy (fun block -> block.Address)
        |> Seq.map (fun block -> { StoragePoolId = storagePoolId; ContentBlockAddress = block.Address })
        |> Seq.toArray

    /// Creates one DirectoryVersion source with optional children and direct manifest content.
    let directoryVersionDto id children includesManifest =
        let files = List<FileVersion>()

        if includesManifest then
            let file = FileVersion.CreateWithHashes (RelativePath "repair.bin") (Sha256Hash "sha-repair") (Blake3Hash fileContentHash) "" true 4L

            file.ContentReference <- FileContentReference.FileManifest finalizedManifest

            files.Add file

        let directories = List<DirectoryVersionId>()
        children |> Seq.iter directories.Add

        {
            DirectoryVersion =
                DirectoryVersion.CreateWithHashes
                    id
                    ownerId
                    organizationId
                    repositoryId
                    (RelativePath ".")
                    (Sha256Hash $"sha-{id:N}")
                    (Blake3Hash $"b3-{id:N}")
                    directories
                    files
                    (if includesManifest then 4L else 0L)
            RecursiveSize = if includesManifest then 4L else 0L
            DeletedAt = None
            DeleteReason = String.Empty
            HashesValidated = true
        }

    /// Creates one live Reference source for any supported ReferenceType.
    let referenceDto referenceType =
        { ReferenceDto.Default with
            ReferenceId = referenceId
            OwnerId = ownerId
            OrganizationId = organizationId
            RepositoryId = repositoryId
            BranchId = Guid.Parse("88888888-7350-4000-8000-888888888888")
            DirectoryId = rootDirectoryVersionId
            Sha256Hash = Sha256Hash "sha-root"
            Blake3Hash = Blake3Hash "b3-root"
            ReferenceType = referenceType
            CreatedAt = NodaTime.Instant.FromUtc(2026, 7, 28, 12, 0)
        }

    /// Creates the exact manifest relationship named by one DirectoryVersion source.
    let manifestRelationship directoryVersionId =
        ExactRelationship.DirectoryVersionManifest
            { RepositoryId = repositoryId; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress; DirectoryVersionId = directoryVersionId }

    /// Creates the exact parent relationship named by the root DirectoryVersion source.
    let parentRelationship =
        ExactRelationship.ParentChild
            { RepositoryId = repositoryId; ParentDirectoryVersionId = rootDirectoryVersionId; ChildDirectoryVersionId = childDirectoryVersionId }

    /// Creates the exact Reference-root relationship named by the live Reference source.
    let referenceRelationship =
        ExactRelationship.ReferenceRoot { RepositoryId = repositoryId; RootDirectoryVersionId = rootDirectoryVersionId; ReferenceId = referenceId }

    /// Creates the shared logical counter tuple used by diagnosis and repair.
    let counterTuple () = { RepositoryId = repositoryId; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress }

    /// Creates a completed physical contribution workflow for the source manifest ranges.
    let completedWorkflow counterRevision =
        let operationId = ManifestContributionWorkflowOperationId "repair-completed"

        { ManifestContributionWorkflowDto.Default with
            RepositoryId = repositoryId
            StoragePoolId = storagePoolId
            ManifestAddress = manifestAddress
            Direction = ManifestContributionDirection.Increment
            LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
            StartOperationId = Some operationId
            LastOperationId = Some operationId
            Ranges = workflowRanges
            CompletedRanges =
                workflowRanges
                |> Array.map (fun range ->
                    { OperationId = operationId; RepositoryId = repositoryId; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress; Range = range })
            FailedRanges = Array.empty
            CounterRevision = counterRevision
            Revision = 8L
        }

    /// Creates a valid finite exact-relationship read bound.
    let readBound value =
        match ExactRelationshipReadBound.create value with
        | Ok bound -> bound
        | Error error -> invalidOp error

    /// Creates production-shaped diagnosis dependencies over mutable actor, workflow, and projection state.
    let diagnosisDependenciesWithWorkflow
        currentReference
        (directoryVersions: Map<DirectoryVersionId, DirectoryVersionDto>)
        (relationships: HashSet<ExactRelationship>)
        (counter: unit -> RepositoryContentCounterDto)
        (workflow: unit -> ManifestContributionWorkflowDto)
        =
        {
            GetReference = fun _ _ -> Task.FromResult currentReference
            GetDirectoryVersion =
                fun id _ ->
                    Task.FromResult(
                        directoryVersions
                        |> Map.tryFind id
                        |> Option.defaultValue DirectoryVersionDto.Default
                    )
            EnumerateRelationships =
                fun partition _ continuationToken _ ->
                    if continuationToken.IsSome then
                        Task.FromResult { Relationships = Array.empty; ContinuationToken = None }
                    else
                        let matching =
                            relationships
                            |> Seq.filter (fun relationship -> ExactRelationshipKey.partition relationship = Ok partition)
                            |> Seq.toArray

                        Task.FromResult { Relationships = matching; ContinuationToken = None }
            VerifyRelationship =
                fun relationship _ ->
                    Task.FromResult(
                        if relationships.Contains relationship then
                            ExactRelationshipPresence.Present
                        else
                            ExactRelationshipPresence.Absent
                    )
            GetCounter = fun _ _ -> Task.FromResult(counter ())
            GetWorkflow = fun _ _ -> Task.FromResult(workflow ())
            GetRecentResult = fun _ _ _ -> Task.FromResult None
        }

    /// Creates production-shaped diagnosis dependencies with completed Increment accounting.
    let diagnosisDependencies
        currentReference
        (directoryVersions: Map<DirectoryVersionId, DirectoryVersionDto>)
        (relationships: HashSet<ExactRelationship>)
        (counter: unit -> RepositoryContentCounterDto)
        =
        diagnosisDependenciesWithWorkflow currentReference directoryVersions relationships counter (fun () -> completedWorkflow (counter ()).Revision)

    /// Runs the production diagnosis engine for one selector and mutable test state.
    let diagnose dependencies selector = diagnoseWith dependencies "2026-07-28T12:00:00Z" "corr-repair" CancellationToken.None (readBound 20) selector

    /// Returns a fully initialized logical counter snapshot.
    let logicalCounter count revision =
        { RepositoryContentCounterDto.Default with
            RepositoryId = repositoryId
            StoragePoolId = storagePoolId
            ManifestAddress = manifestAddress
            Count = count
            Revision = revision
        }

    /// Extracts a successful repair plan with assertion-friendly failure output.
    let expectPlan report =
        match buildPlan report with
        | Ok plan -> plan
        | Error error ->
            Assert.Fail($"Expected a valid repair plan, got {error}.")
            Array.empty

    /// Verifies report digest validation rejects mismatches, tampering, and JSON literal null.
    [<Test>]
    member _.RequestValidationRejectsWrongHashTamperingAndNullReport() =
        task {
            let relationships =
                HashSet<ExactRelationship>(
                    [
                        manifestRelationship rootDirectoryVersionId
                    ]
                )

            let counter = logicalCounter 1L 7L

            let dependencies =
                diagnosisDependencies
                    ReferenceDto.Default
                    (Map.ofList [ rootDirectoryVersionId, directoryVersionDto rootDirectoryVersionId Array.empty true ])
                    relationships
                    (fun () -> counter)

            let! report = diagnose dependencies (DiagnosisSelector.DirectoryVersionId(rootDirectoryVersionId, None))

            let serialized = JsonSerializer.Serialize(report, Grace.Shared.Constants.JsonSerializerOptions)

            match validateRequest serialized (String.replicate 64 "0") false with
            | Ok _ -> Assert.Fail("Expected the wrong digest to reject.")
            | Error error -> Assert.That(error, Does.Contain("does not match"))

            let tampered = serialized.Replace("\"StoredCount\": 1", "\"StoredCount\": 2")

            match validateRequest tampered report.ReportSha256 false with
            | Ok _ -> Assert.Fail("Expected tampered report JSON to reject.")
            | Error error -> Assert.That(error, Does.Contain("SHA-256"))

            match validateRequest "null" report.ReportSha256 false with
            | Ok _ -> Assert.Fail("Expected JSON literal null to reject.")
            | Error error -> Assert.That(error, Does.Contain("diagnosis JSON"))
        }

    /// Verifies the HTTP route handles a JSON literal null body without dereferencing request parameters.
    [<Test>]
    member _.NullRequestBodyReturnsBadRequest() =
        task {
            let context = DefaultHttpContext()
            let bytes = Encoding.UTF8.GetBytes("null")
            context.Request.Body <- new MemoryStream(bytes)
            context.Request.ContentType <- "application/json"
            context.Response.Body <- new MemoryStream()

            context.RequestServices <-
                ServiceCollection()
                    .AddGiraffe()
                    .AddSingleton<Json.ISerializer>(Json.Serializer(Constants.JsonSerializerOptions))
                    .BuildServiceProvider()

            let next: HttpFunc = fun httpContext -> Task.FromResult(Some httpContext)

            let! _ = ManifestContributionRepair.Repair next context

            Assert.That(context.Response.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest))
        }

    /// Supplies every ReferenceType to the uniform original-event republication proof.
    static member ReferenceTypes =
        [|
            ReferenceType.Promotion
            ReferenceType.Commit
            ReferenceType.Checkpoint
            ReferenceType.Save
            ReferenceType.Tag
            ReferenceType.External
            ReferenceType.Rebase
        |]

    /// Verifies every ReferenceType uses the same original Created-event republication action.
    [<TestCaseSource("ReferenceTypes")>]
    member _.MissingReferenceRootRepublishesOriginalCreatedEvent(referenceType) =
        task {
            let reference = referenceDto referenceType

            let relationships =
                HashSet<ExactRelationship>(
                    [
                        manifestRelationship rootDirectoryVersionId
                    ]
                )

            let counter = logicalCounter 1L 7L

            let dependencies =
                diagnosisDependencies
                    reference
                    (Map.ofList [ rootDirectoryVersionId, directoryVersionDto rootDirectoryVersionId Array.empty true ])
                    relationships
                    (fun () -> counter)

            let! report = diagnose dependencies (DiagnosisSelector.ReferenceId(referenceId, None))

            let action =
                expectPlan report
                |> Array.find (fun mutation -> mutation.Action.Kind = "RepublishReferenceCreated")

            let mutable reads = 0
            let mutable republishes = 0

            let repairDependencies =
                {
                    GetReference =
                        fun id ->
                            reads <- reads + 1
                            Assert.That(id, Is.EqualTo(referenceId))
                            Task.FromResult reference
                    RepublishReferenceCreated =
                        fun id ->
                            republishes <- republishes + 1
                            Assert.That(id, Is.EqualTo(referenceId))
                            Task.CompletedTask
                    GetDirectoryVersion =
                        fun _ ->
                            Task.FromException<DirectoryVersionDto>(InvalidOperationException("Reference repair must not use DirectoryVersion accounting."))
                    GetOrAdd =
                        fun _ _ ->
                            Task.FromException<ExactRelationshipWriteOutcome>(
                                InvalidOperationException("Reference-root repair must flow through event republication.")
                            )
                }

            do! repairMissingRelationshipWith repairDependencies report action CancellationToken.None

            Assert.That(reads, Is.EqualTo(1))
            Assert.That(republishes, Is.EqualTo(1))
        }

    /// Verifies a missing manifest projection performs one immediate source reread and one direct GetOrAdd.
    [<Test>]
    member _.MissingManifestRelationshipUsesGetOrAddAfterImmediateSourceReread() =
        task {
            let source = directoryVersionDto rootDirectoryVersionId Array.empty true

            let relationships = HashSet<ExactRelationship>()
            let counter = logicalCounter 1L 7L

            let dependencies = diagnosisDependencies ReferenceDto.Default (Map.ofList [ rootDirectoryVersionId, source ]) relationships (fun () -> counter)

            let! report = diagnose dependencies (DiagnosisSelector.DirectoryVersionId(rootDirectoryVersionId, None))

            let action = expectPlan report |> Array.exactlyOne
            let calls = ResizeArray<string>()

            let repairDependencies =
                {
                    GetReference = fun _ -> Task.FromException<ReferenceDto>(InvalidOperationException())
                    RepublishReferenceCreated = fun _ -> Task.FromException(InvalidOperationException())
                    GetDirectoryVersion =
                        fun id ->
                            calls.Add $"read:{id:D}"
                            Task.FromResult source
                    GetOrAdd =
                        fun relationship _ ->
                            calls.Add $"write:{ManifestContributionDiagnosis.relationshipIdentity relationship}"

                            Task.FromResult ExactRelationshipWriteOutcome.Changed
                }

            do! repairMissingRelationshipWith repairDependencies report action CancellationToken.None

            Assert.That(
                calls.ToArray(),
                Is.EqualTo(
                    [|
                        $"read:{rootDirectoryVersionId:D}"
                        $"write:{action.Action.Identity}"
                    |]
                    :> obj
                )
            )
        }

    /// Verifies incomplete physical evidence retains a missing manifest relationship for normal accounting replay.
    [<Test>]
    member _.MissingManifestRelationshipWithIncompleteEvidenceProducesNoRepairMutation() =
        task {
            let source = directoryVersionDto rootDirectoryVersionId Array.empty true
            let relationship = manifestRelationship rootDirectoryVersionId
            let relationships = HashSet<ExactRelationship>()
            let counter = logicalCounter 0L 7L

            let incompleteWorkflow =
                { completedWorkflow counter.Revision with
                    LifecycleState = ManifestContributionWorkflowLifecycleState.InProgress
                    CompletedRanges = Array.empty
                }

            let diagnosisDeps =
                diagnosisDependenciesWithWorkflow
                    ReferenceDto.Default
                    (Map.ofList [ rootDirectoryVersionId, source ])
                    relationships
                    (fun () -> counter)
                    (fun () -> incompleteWorkflow)

            let selector = DiagnosisSelector.DirectoryVersionId(rootDirectoryVersionId, None)
            let! report = diagnose diagnosisDeps selector

            Assert.That(report.MissingRelationships, Does.Contain(ManifestContributionDiagnosis.relationshipIdentity relationship))

            Assert.That(
                report.CountEvidence,
                Has
                    .Exactly(1)
                    .Matches<ManifestCountEvidence>(fun evidence -> evidence.Completeness = "IncompleteRetain")
            )

            Assert.That(report.RepairTargets, Has.None.StartsWith("GetOrAddExactRelationship:"))

            let plan = expectPlan report
            Assert.That(plan, Is.Empty)

            let mutable writes = 0

            let repairDependencies =
                {
                    DiagnoseCurrent = fun _ _ _ -> diagnose diagnosisDeps selector
                    ApplyAction =
                        fun _ _ _ _ ->
                            writes <- writes + 1
                            Task.CompletedTask
                }

            let! repaired = repairWith repairDependencies "2026-07-28T12:00:30Z" "corr-incomplete" CancellationToken.None (readBound 20) report true

            Assert.That(repaired.Outcome, Is.EqualTo(RepairOutcome.IncompleteRetain))
            Assert.That(repaired.ProposedActions, Is.Empty)
            Assert.That(repaired.AppliedActions, Is.Empty)
            Assert.That(writes, Is.Zero)
            Assert.That(relationships.Contains relationship, Is.False)
        }

    /// Verifies complete signed evidence that becomes incomplete is rejected before the manifest projection write.
    [<Test>]
    member _.MissingManifestRelationshipRejectsEvidenceDowngradeBeforeExecution() =
        task {
            let source = directoryVersionDto rootDirectoryVersionId Array.empty true
            let relationships = HashSet<ExactRelationship>()
            let counter = logicalCounter 1L 7L
            let mutable workflow = completedWorkflow counter.Revision

            let diagnosisDeps =
                diagnosisDependenciesWithWorkflow
                    ReferenceDto.Default
                    (Map.ofList [ rootDirectoryVersionId, source ])
                    relationships
                    (fun () -> counter)
                    (fun () -> workflow)

            let selector = DiagnosisSelector.DirectoryVersionId(rootDirectoryVersionId, None)
            let! signed = diagnose diagnosisDeps selector
            Assert.That(expectPlan signed, Has.Length.EqualTo(1))

            let mutable diagnosisCalls = 0
            let mutable writes = 0

            let repairDependencies =
                {
                    DiagnoseCurrent =
                        fun _ _ _ ->
                            diagnosisCalls <- diagnosisCalls + 1

                            if diagnosisCalls = 2 then
                                workflow <-
                                    { workflow with LifecycleState = ManifestContributionWorkflowLifecycleState.InProgress; CompletedRanges = Array.empty }

                            diagnose diagnosisDeps selector
                    ApplyAction =
                        fun _ _ _ _ ->
                            writes <- writes + 1
                            Task.CompletedTask
                }

            let! repaired = repairWith repairDependencies "2026-07-28T12:00:45Z" "corr-downgrade" CancellationToken.None (readBound 20) signed true

            Assert.That(repaired.Outcome, Is.EqualTo(RepairOutcome.IncompleteRetain))
            Assert.That(repaired.AppliedActions, Is.Empty)
            Assert.That(writes, Is.Zero)
            Assert.That(diagnosisCalls, Is.EqualTo(2))
        }

    /// Verifies a missing parent projection is written only after both current endpoints still establish the exact child edge.
    [<Test>]
    member _.MissingParentRelationshipUsesGetOrAddAfterImmediateEndpointRereads() =
        task {
            let root = directoryVersionDto rootDirectoryVersionId [ childDirectoryVersionId ] false

            let child = directoryVersionDto childDirectoryVersionId Array.empty true

            let relationships =
                HashSet<ExactRelationship>(
                    [
                        manifestRelationship childDirectoryVersionId
                    ]
                )

            let counter = logicalCounter 1L 7L

            let dependencies =
                diagnosisDependencies
                    ReferenceDto.Default
                    (Map.ofList [ rootDirectoryVersionId, root
                                  childDirectoryVersionId, child ])
                    relationships
                    (fun () -> counter)

            let! report = diagnose dependencies (DiagnosisSelector.DirectoryVersionId(rootDirectoryVersionId, None))

            let action =
                expectPlan report
                |> Array.find (fun mutation -> mutation.Target = RepairMutationTarget.Relationship parentRelationship)

            let calls = ResizeArray<string>()

            let repairDependencies =
                {
                    GetReference = fun _ -> Task.FromException<ReferenceDto>(InvalidOperationException())
                    RepublishReferenceCreated = fun _ -> Task.FromException(InvalidOperationException())
                    GetDirectoryVersion =
                        fun id ->
                            calls.Add $"read:{id:D}"

                            if id = rootDirectoryVersionId then
                                Task.FromResult root
                            elif id = childDirectoryVersionId then
                                Task.FromResult child
                            else
                                Task.FromException<DirectoryVersionDto>(InvalidOperationException($"Unexpected DirectoryVersion read: {id:D}"))
                    GetOrAdd =
                        fun exact _ ->
                            calls.Add "write"
                            Assert.That(exact, Is.EqualTo(parentRelationship))
                            Task.FromResult ExactRelationshipWriteOutcome.Changed
                }

            do! repairMissingRelationshipWith repairDependencies report action CancellationToken.None

            Assert.That(
                calls.ToArray(),
                Is.EqualTo(
                    [|
                        $"read:{rootDirectoryVersionId:D}"
                        $"read:{childDirectoryVersionId:D}"
                        "write"
                    |]
                    :> obj
                )
            )
        }

    /// Verifies either endpoint changing after diagnosis retains ParentChild repair without a projection write.
    [<Test>]
    member _.MissingParentRelationshipRejectsChangedEndpointsBeforeGetOrAdd() =
        task {
            let root = directoryVersionDto rootDirectoryVersionId [ childDirectoryVersionId ] false

            let child = directoryVersionDto childDirectoryVersionId Array.empty true

            let relationships =
                HashSet<ExactRelationship>(
                    [
                        manifestRelationship childDirectoryVersionId
                    ]
                )

            let counter = logicalCounter 1L 7L

            let diagnosisDependencies =
                diagnosisDependencies
                    ReferenceDto.Default
                    (Map.ofList [ rootDirectoryVersionId, root
                                  childDirectoryVersionId, child ])
                    relationships
                    (fun () -> counter)

            let! report = diagnose diagnosisDependencies (DiagnosisSelector.DirectoryVersionId(rootDirectoryVersionId, None))

            let wrongRepository = directoryVersionDto childDirectoryVersionId Array.empty true
            wrongRepository.DirectoryVersion.RepositoryId <- Guid.Parse("99999999-7350-4000-8000-999999999999")

            let wrongId = directoryVersionDto staleDirectoryVersionId Array.empty true
            let parentWithoutChild = directoryVersionDto rootDirectoryVersionId Array.empty false

            let changedEndpoints: (string * DirectoryVersionDto * (unit -> Task<DirectoryVersionDto>) * int) array =
                [|
                    "parent source changed", parentWithoutChild, (fun () -> Task.FromResult child), 0
                    "child cleared", root, (fun () -> Task.FromResult DirectoryVersionDto.Default), 1
                    "child unreadable", root, (fun () -> Task.FromException<DirectoryVersionDto>(InvalidOperationException("cleared"))), 1
                    "child wrong repository", root, (fun () -> Task.FromResult wrongRepository), 1
                    "child wrong id", root, (fun () -> Task.FromResult wrongId), 1
                |]

            let mutable scenarioIndex = 0

            while scenarioIndex < changedEndpoints.Length do
                let label, currentParent, readChild, expectedChildReads = changedEndpoints[scenarioIndex]
                let mutable writes = 0
                let mutable childReads = 0

                let missingDependencies =
                    {
                        GetReference = fun _ -> Task.FromException<ReferenceDto>(InvalidOperationException())
                        RepublishReferenceCreated = fun _ -> Task.FromException(InvalidOperationException())
                        GetDirectoryVersion =
                            fun id ->
                                if id = rootDirectoryVersionId then
                                    Task.FromResult currentParent
                                elif id = childDirectoryVersionId then
                                    childReads <- childReads + 1
                                    readChild ()
                                else
                                    Task.FromException<DirectoryVersionDto>(InvalidOperationException($"Unexpected DirectoryVersion read: {id:D}"))
                        GetOrAdd =
                            fun _ _ ->
                                writes <- writes + 1
                                Task.FromResult ExactRelationshipWriteOutcome.Changed
                    }

                let repairDependencies =
                    {
                        DiagnoseCurrent = fun _ _ _ -> Task.FromResult report
                        ApplyAction =
                            fun _ currentReport mutation cancellationToken ->
                                repairMissingRelationshipWith missingDependencies currentReport mutation cancellationToken
                    }

                let! repaired =
                    repairWith repairDependencies "2026-07-28T12:00:40Z" $"corr-endpoint-{scenarioIndex}" CancellationToken.None (readBound 20) report true

                Assert.That(repaired.Outcome, Is.EqualTo(RepairOutcome.FailedRetain), label)
                Assert.That(repaired.AppliedActions, Is.Empty, label)
                Assert.That(childReads, Is.EqualTo(expectedChildReads), label)
                Assert.That(writes, Is.Zero, label)
                scenarioIndex <- scenarioIndex + 1
        }

    /// Verifies stale removal requires immediate source absence and unchanged signed counter/workflow facts.
    [<Test>]
    member _.StaleRemovalRequiresCurrentAbsenceAndUnchangedPhysicalEvidence() =
        task {
            let currentSource = directoryVersionDto rootDirectoryVersionId Array.empty true

            let staleSource = directoryVersionDto staleDirectoryVersionId Array.empty false

            let expected = manifestRelationship rootDirectoryVersionId

            let stale = manifestRelationship staleDirectoryVersionId

            let relationships = HashSet<ExactRelationship>([ expected; stale ])

            let counter = logicalCounter 1L 7L

            let dependencies =
                diagnosisDependencies
                    ReferenceDto.Default
                    (Map.ofList [ rootDirectoryVersionId, currentSource
                                  staleDirectoryVersionId, staleSource ])
                    relationships
                    (fun () -> counter)

            let! report = diagnose dependencies (DiagnosisSelector.DirectoryVersionId(rootDirectoryVersionId, None))

            let staleIdentity = ManifestContributionDiagnosis.relationshipIdentity stale

            let staleRelationship =
                match stale with
                | ExactRelationship.DirectoryVersionManifest relationship -> relationship
                | _ -> failwith "unreachable"

            let mutable removals = 0

            let valid =
                {
                    GetDirectoryVersion = fun _ -> Task.FromResult staleSource
                    GetCounter = fun _ -> Task.FromResult counter
                    GetWorkflow = fun _ -> Task.FromResult(completedWorkflow counter.Revision)
                    EnsureAbsent =
                        fun _ _ ->
                            removals <- removals + 1
                            Task.FromResult ExactRelationshipWriteOutcome.Changed
                }

            do! removeStaleRelationshipWith valid report report staleIdentity staleRelationship CancellationToken.None

            Assert.That(removals, Is.EqualTo(1))

            let changedSource = directoryVersionDto staleDirectoryVersionId Array.empty true

            let changed = { valid with GetDirectoryVersion = fun _ -> Task.FromResult changedSource }

            let mutable rejected = false

            try
                do! removeStaleRelationshipWith changed report report staleIdentity staleRelationship CancellationToken.None
            with
            | :? InvalidOperationException -> rejected <- true

            Assert.That(rejected, Is.True)
            Assert.That(removals, Is.EqualTo(1))
        }

    /// Verifies positive logical reconciliation changes count and revision once and emits no physical contribution work.
    [<Test>]
    member _.PositiveLogicalCountRepairIsAtomicAndPhysicalContributionFree() =
        task {
            let source = directoryVersionDto rootDirectoryVersionId Array.empty true

            let relationship = manifestRelationship rootDirectoryVersionId

            let relationships = HashSet<ExactRelationship>([ relationship ])

            let counter = logicalCounter 3L 7L

            let diagnosisDeps = diagnosisDependencies ReferenceDto.Default (Map.ofList [ rootDirectoryVersionId, source ]) relationships (fun () -> counter)

            let! report = diagnose diagnosisDeps (DiagnosisSelector.DirectoryVersionId(rootDirectoryVersionId, None))

            let action = expectPlan report |> Array.exactlyOne

            let target, rebuiltCount =
                match action.Target with
                | RepairMutationTarget.Counter (target, rebuiltCount) -> target, rebuiltCount
                | _ -> failwith "Expected a logical counter repair."

            let mutable commands = Array.empty<RepositoryContentCounterRepairCommand>

            let reconciliation =
                {
                    GetCounter = fun _ -> Task.FromResult counter
                    GetWorkflow = fun _ -> Task.FromResult(completedWorkflow counter.Revision)
                    Reconcile =
                        fun command ->
                            commands <- Array.append commands [| command |]

                            RepositoryContentCounterActor.decideRepairForKey None counter command (EventMetadata.New "corr-counter" "GraceSystem")
                            |> function
                                | Ok decision -> Task.FromResult decision
                                | Error error -> Task.FromException<RepositoryContentCounterDecision>(InvalidOperationException error.Error)
                }

            do! reconcileCounterWith reconciliation report report (readBound 20) target rebuiltCount

            Assert.That(commands, Has.Length.EqualTo(1))
            Assert.That(commands[0].ExpectedRevision, Is.EqualTo(7L))
            Assert.That(commands[0].RebuiltCount, Is.EqualTo(1L))
        }

    /// Verifies duplicate actions, oversized plans, and rebuilt-zero counter repair are rejected before execution.
    [<Test>]
    member _.PlanValidationRejectsDuplicatesOversizeAndRebuiltZero() =
        task {
            let source = directoryVersionDto rootDirectoryVersionId Array.empty true

            let relationship = manifestRelationship rootDirectoryVersionId

            let relationships = HashSet<ExactRelationship>()
            let counter = logicalCounter 1L 7L

            let dependencies = diagnosisDependencies ReferenceDto.Default (Map.ofList [ rootDirectoryVersionId, source ]) relationships (fun () -> counter)

            let! missingReport = diagnose dependencies (DiagnosisSelector.DirectoryVersionId(rootDirectoryVersionId, None))

            let identity = ManifestContributionDiagnosis.relationshipIdentity relationship

            let duplicate =
                { missingReport with
                    MissingRelationships = [| identity; identity |]
                    RepairTargets =
                        [|
                            $"GetOrAddExactRelationship:{identity}"
                            $"GetOrAddExactRelationship:{identity}"
                        |]
                }

            match buildPlan duplicate with
            | Ok _ -> Assert.Fail("Expected duplicate actions to reject.")
            | Error error -> Assert.That(error, Does.Contain("duplicate"))

            let oversizedRelationships =
                [|
                    referenceRelationship
                    parentRelationship
                    ExactRelationship.ParentChild
                        { RepositoryId = repositoryId; ParentDirectoryVersionId = rootDirectoryVersionId; ChildDirectoryVersionId = staleDirectoryVersionId }
                |]

            let oversized =
                { missingReport with
                    MaxRelationships = 1
                    MissingRelationships =
                        oversizedRelationships
                        |> Array.map ManifestContributionDiagnosis.relationshipIdentity
                    RepairTargets =
                        [|
                            $"RepublishReferenceCreated:{ManifestContributionDiagnosis.relationshipIdentity referenceRelationship}"
                            $"GetOrAddExactRelationship:{ManifestContributionDiagnosis.relationshipIdentity parentRelationship}"
                            $"GetOrAddExactRelationship:{ManifestContributionDiagnosis.relationshipIdentity oversizedRelationships[2]}"
                        |]
                    CountEvidence = Array.empty
                }

            match buildPlan oversized with
            | Ok _ -> Assert.Fail("Expected the oversized plan to reject.")
            | Error error -> Assert.That(error, Does.Contain("2 * MaxRelationships"))

            let counterIdentity = $"{repositoryId:D}|{storagePoolId}|{manifestAddress}"

            let zero =
                { missingReport with
                    MissingRelationships = Array.empty
                    CountEvidence =
                        [|
                            {
                                RepositoryId = $"{repositoryId:D}"
                                StoragePoolId = storagePoolId
                                ManifestAddress = manifestAddress
                                StoredCount = Some 1L
                                RebuiltCount = Some 0L
                                Completeness = "Complete"
                            }
                        |]
                    RepairTargets =
                        [|
                            $"ReconcileRepositoryContentCount:{counterIdentity}"
                        |]
                }

            match buildPlan zero with
            | Ok _ -> Assert.Fail("Expected rebuilt-zero repair to reject.")
            | Error error -> Assert.That(error, Does.Contain("positive"))
        }

    /// Runs a two-action production diagnosis and fails or cancels the second action after confirming the first.
    member private _.ProveAppliedPrefixPreserved cancel =
        task {
            let root = directoryVersionDto rootDirectoryVersionId [ childDirectoryVersionId ] false

            let child = directoryVersionDto childDirectoryVersionId Array.empty true

            let relationships = HashSet<ExactRelationship>()
            let mutable counter = logicalCounter 1L 7L

            let diagnosisDeps =
                diagnosisDependencies
                    ReferenceDto.Default
                    (Map.ofList [ rootDirectoryVersionId, root
                                  childDirectoryVersionId, child ])
                    relationships
                    (fun () -> counter)

            let selector = DiagnosisSelector.DirectoryVersionId(rootDirectoryVersionId, None)

            let! signed = diagnose diagnosisDeps selector
            let attempts = ResizeArray<string>()

            let dependencies =
                {
                    DiagnoseCurrent =
                        fun currentSelector _ _ ->
                            Assert.That(currentSelector, Is.EqualTo(selector))
                            diagnose diagnosisDeps selector
                    ApplyAction =
                        fun _ _ action _ ->
                            task {
                                attempts.Add action.Action.Identity

                                if attempts.Count = 1 then
                                    match action.Target with
                                    | RepairMutationTarget.Relationship relationship -> relationships.Add relationship |> ignore
                                    | _ -> failwith "Expected relationship action."
                                elif cancel then
                                    raise (OperationCanceledException("cancelled after first action"))
                                else
                                    invalidOp "failed after first action"
                            }
                            :> Task
                }

            let! repaired = repairWith dependencies "2026-07-28T12:01:00Z" "corr-prefix" CancellationToken.None (readBound 20) signed true

            Assert.That(repaired.Outcome, Is.EqualTo(RepairOutcome.FailedRetain))

            Assert.That(repaired.AppliedActions, Has.Length.EqualTo(1))
            Assert.That(attempts, Has.Count.EqualTo(2))
            Assert.That(attempts |> Seq.distinct |> Seq.length, Is.EqualTo(2))
            Assert.That(repaired.Message, Does.Contain("fresh diagnosis"))
        }

    /// Verifies dependency exceptions preserve the confirmed applied prefix and never retry the failed action.
    [<Test>]
    member this.ExceptionPreservesAppliedPrefix() = this.ProveAppliedPrefixPreserved false

    /// Verifies cancellation preserves the confirmed applied prefix and never retries the cancelled action.
    [<Test>]
    member this.CancellationPreservesAppliedPrefix() = this.ProveAppliedPrefixPreserved true

    /// Verifies one finite execution applies each signed action once and completes with one final bounded diagnosis.
    [<Test>]
    member _.FiniteExecutionRepairsProjectionThenLogicalCountOnce() =
        task {
            let source = directoryVersionDto rootDirectoryVersionId Array.empty true

            let relationship = manifestRelationship rootDirectoryVersionId

            let relationships = HashSet<ExactRelationship>()
            let mutable counter = logicalCounter 2L 7L

            let diagnosisDeps = diagnosisDependencies ReferenceDto.Default (Map.ofList [ rootDirectoryVersionId, source ]) relationships (fun () -> counter)

            let selector = DiagnosisSelector.DirectoryVersionId(rootDirectoryVersionId, None)

            let! signed = diagnose diagnosisDeps selector

            Assert.That(
                signed.CountEvidence,
                Has
                    .Exactly(1)
                    .Matches<ManifestCountEvidence>(fun evidence ->
                        evidence.Completeness = "Complete"
                        && evidence.StoredCount = Some 2L
                        && evidence.RebuiltCount = Some 1L)
            )

            let attempts = ResizeArray<string>()
            let mutable diagnosisCalls = 0

            let dependencies =
                {
                    DiagnoseCurrent =
                        fun _ _ _ ->
                            diagnosisCalls <- diagnosisCalls + 1
                            diagnose diagnosisDeps selector
                    ApplyAction =
                        fun _ _ action _ ->
                            task {
                                attempts.Add action.Action.Kind

                                match action.Target with
                                | RepairMutationTarget.Relationship exact -> relationships.Add exact |> ignore
                                | RepairMutationTarget.Counter (_, rebuiltCount) ->
                                    let command =
                                        {
                                            OperationId =
                                                RepositoryContentCounterActor.repairOperationId
                                                    repositoryId
                                                    storagePoolId
                                                    manifestAddress
                                                    counter.Revision
                                                    rebuiltCount
                                            RepositoryId = repositoryId
                                            StoragePoolId = storagePoolId
                                            ManifestAddress = manifestAddress
                                            ExpectedRevision = counter.Revision
                                            RebuiltCount = rebuiltCount
                                        }

                                    let decision =
                                        RepositoryContentCounterActor.decideRepairForKey None counter command (EventMetadata.New "corr-finite" "GraceSystem")
                                        |> function
                                            | Ok value -> value
                                            | Error error -> invalidOp error.Error

                                    Assert.That(decision.Intents, Is.Empty)
                                    counter <- decision.Counter
                            }
                            :> Task
                }

            let! repaired = repairWith dependencies "2026-07-28T12:02:00Z" "corr-finite" CancellationToken.None (readBound 20) signed true

            Assert.That(repaired.Outcome, Is.EqualTo(RepairOutcome.VerifiedComplete))

            Assert.That(
                attempts.ToArray(),
                Is.EqualTo(
                    [|
                        "GetOrAddExactRelationship"
                        "ReconcileRepositoryContentCount"
                    |]
                    :> obj
                )
            )

            Assert.That(repaired.AppliedActions, Has.Length.EqualTo(2))
            Assert.That(diagnosisCalls, Is.EqualTo(4))
            Assert.That(counter.Count, Is.EqualTo(1L))
            Assert.That(counter.Revision, Is.EqualTo(8L))
        }

    /// Verifies an expected-revision conflict is retained as unknown and requires a fresh diagnosis.
    [<Test>]
    member _.ExpectedRevisionConflictRequiresFreshDiagnosis() =
        task {
            let source = directoryVersionDto rootDirectoryVersionId Array.empty true

            let relationships =
                HashSet<ExactRelationship>(
                    [
                        manifestRelationship rootDirectoryVersionId
                    ]
                )

            let counter = logicalCounter 2L 7L

            let diagnosisDeps = diagnosisDependencies ReferenceDto.Default (Map.ofList [ rootDirectoryVersionId, source ]) relationships (fun () -> counter)

            let selector = DiagnosisSelector.DirectoryVersionId(rootDirectoryVersionId, None)

            let! signed = diagnose diagnosisDeps selector

            let dependencies =
                {
                    DiagnoseCurrent = fun _ _ _ -> diagnose diagnosisDeps selector
                    ApplyAction =
                        fun _ _ _ _ ->
                            Task.FromException(InvalidOperationException("RepositoryContentCounter repair expected revision 7, but current revision is 8."))
                }

            let! repaired = repairWith dependencies "2026-07-28T12:03:00Z" "corr-conflict" CancellationToken.None (readBound 20) signed true

            Assert.That(repaired.Outcome, Is.EqualTo(RepairOutcome.FailedRetain))

            Assert.That(repaired.AppliedActions, Is.Empty)
            Assert.That(repaired.Message, Does.Contain("fresh diagnosis"))
        }
