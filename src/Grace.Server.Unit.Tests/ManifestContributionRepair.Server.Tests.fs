namespace Grace.Server.Unit.Tests

open Grace.Actors
open Grace.Server.ManifestContributionDiagnosis
open Grace.Server.ManifestContributionRepair
open Grace.Shared
open Grace.Types
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.ManifestContributionAccounting
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.RepositoryContentCounter
open NUnit.Framework
open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Proves bounded repair planning, revalidation, interruption, and dry-run behavior without starting Grace runtime resources.
[<Parallelizable(ParallelScope.All)>]
type ManifestContributionRepairServerTests() =

    let ownerId = Guid.Parse("11111111-7350-4000-8000-111111111111")
    let organizationId = Guid.Parse("22222222-7350-4000-8000-222222222222")
    let repositoryId = Guid.Parse("33333333-7350-4000-8000-333333333333")
    let directoryVersionId = Guid.Parse("44444444-7350-4000-8000-444444444444")
    let storagePoolId = StoragePoolId "pool"
    let manifestAddress = ManifestAddress "manifest"
    let reportTarget = DiagnosisTarget.DirectoryVersion "44444444-7350-4000-8000-444444444444"
    let relationshipIdentity = "manifest:33333333735040008000333333333333:cG9vbA:bWFuaWZlc3Q|directory-version-manifest:44444444735040008000444444444444"

    /// Creates a signed, source-backed diagnosis report with caller-selected differences.
    let report outcome (missing: string array) (stale: string array) stored rebuilt (repairTargets: string array) =
        emptyReport "2026-07-28T00:00:00Z" reportTarget 10 outcome Array.empty
        |> fun value ->
            { value with
                ActorFacts =
                    [|
                        {
                            ActorType = "DirectoryVersion"
                            ActorId = "44444444-7350-4000-8000-444444444444"
                            Revision = None
                            SnapshotJson = """{"source":"current"}"""
                        }
                        { ActorType = "RepositoryContentCounter"; ActorId = "counter"; Revision = Some 7L; SnapshotJson = $"{{\"Count\":{stored}}}" }
                        {
                            ActorType = "ManifestContributionWorkflow"
                            ActorId = "workflow"
                            Revision = Some 8L
                            SnapshotJson = """{"LifecycleState":"Completed","CounterRevision":7}"""
                        }
                    |]
                ExpectedRelationships = [| relationshipIdentity |]
                ObservedRelationships = if missing.Length = 0 then [| relationshipIdentity |] else Array.empty
                MissingRelationships = missing
                StaleRelationships = stale
                CountEvidence =
                    [|
                        {
                            RepositoryId = "33333333-7350-4000-8000-333333333333"
                            StoragePoolId = "pool"
                            ManifestAddress = "manifest"
                            StoredCount = Some stored
                            RebuiltCount = Some rebuilt
                            Completeness = "Complete"
                        }
                    |]
                RepairTargets = repairTargets
                EvidenceGaps =
                    if outcome = DiagnosisOutcome.VerifiedComplete then
                        Array.empty
                    else
                        [| "repair required" |]
                Outcome = outcome
            }
        |> signReport

    /// Serializes one diagnosis report using the production JSON contract.
    let serialize value = JsonSerializer.Serialize(value, Grace.Shared.Constants.JsonSerializerOptions)

    /// Supplies a valid explicit relationship bound.
    let bound value =
        match ExactRelationshipReadBound.create value with
        | Ok result -> result
        | Error error -> invalidOp error

    /// Creates readable current DirectoryVersion state that no longer names the stale manifest.
    let sourceWithoutManifest () =
        {
            DirectoryVersion =
                DirectoryVersion.CreateWithHashes
                    directoryVersionId
                    ownerId
                    organizationId
                    repositoryId
                    (RelativePath ".")
                    (Sha256Hash "sha-stale")
                    (Blake3Hash "b3-stale")
                    (List<DirectoryVersionId>())
                    (List<FileVersion>())
                    0L
            RecursiveSize = 0L
            DeletedAt = None
            DeleteReason = String.Empty
            HashesValidated = true
        }

    /// Creates coherent completed workflow evidence for the focused repository-manifest tuple.
    let completedWorkflow () =
        let operationId = ManifestContributionWorkflowOperationId "repair-workflow"
        let range = { StoragePoolId = storagePoolId; ContentBlockAddress = ContentBlockAddress "block" }

        { ManifestContributionWorkflowDto.Default with
            RepositoryId = repositoryId
            StoragePoolId = storagePoolId
            ManifestAddress = manifestAddress
            Direction = ManifestContributionDirection.Increment
            Ranges = [| range |]
            CompletedRanges =
                [|
                    { OperationId = operationId; RepositoryId = repositoryId; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress; Range = range }
                |]
            FailedRanges = Array.empty
            LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
            StartOperationId = Some operationId
            LastOperationId = Some operationId
            CounterRevision = 7L
            Revision = 8L
        }

    /// Replaces synthetic actor facts with exact snapshots consumed by destructive repair helpers.
    let withCurrentActorFacts (source: DirectoryVersionDto) (counter: RepositoryContentCounterDto) (workflow: ManifestContributionWorkflowDto) value =
        { value with
            ActorFacts =
                [|
                    { ActorType = "DirectoryVersion"; ActorId = $"{directoryVersionId:D}"; Revision = None; SnapshotJson = actorSnapshotJson source }
                    {
                        ActorType = "RepositoryContentCounter"
                        ActorId = RepositoryContentCounter.primaryKey repositoryId storagePoolId manifestAddress
                        Revision = Some counter.Revision
                        SnapshotJson = actorSnapshotJson counter
                    }
                    {
                        ActorType = "ManifestContributionWorkflow"
                        ActorId = ManifestContributionWorkflow.primaryKey repositoryId storagePoolId manifestAddress
                        Revision = Some workflow.Revision
                        SnapshotJson = workflowSnapshotJson workflow
                    }
                |]
        }
        |> signReport

    /// Verifies request validation requires the diagnosis schema and both matching SHA checks.
    [<Test>]
    member _.RejectsWrongExpectedHashAndTamperedReport() =
        let current = report DiagnosisOutcome.VerifiedComplete Array.empty Array.empty 1L 1L Array.empty
        let serialized = serialize current

        match validateRequest serialized (String.replicate 64 "0") false with
        | Error error -> Assert.That(error, Does.Contain("ExpectedReportSha256"))
        | Ok _ -> Assert.Fail("A mismatched expected report hash must be rejected.")

        let tampered = serialized.Replace("\"StoredCount\": 1", "\"StoredCount\": 2")
        Assert.That(tampered, Is.Not.EqualTo(serialized), "The fixture must mutate hash-covered diagnosis evidence.")

        match validateRequest tampered current.ReportSha256 false with
        | Error error -> Assert.That(error, Does.Contain("SHA-256"))
        | Ok _ -> Assert.Fail("Tampered diagnosis content must be rejected.")

    /// Verifies dry run returns the exact ordered plan without invoking any mutation callback.
    [<Test>]
    member _.DryRunDerivesOrderedPlanAndPerformsZeroWrites() =
        task {
            let current =
                report
                    DiagnosisOutcome.IncompleteRetain
                    [| relationshipIdentity |]
                    Array.empty
                    0L
                    1L
                    [|
                        $"ReconcileCounter:33333333-7350-4000-8000-333333333333|pool|manifest"
                        $"GetOrAddExactRelationship:{relationshipIdentity}"
                    |]

            let mutable mutationCalls = 0

            let dependencies =
                {
                    DiagnoseCurrent = fun _ _ _ -> Task.FromResult current
                    ApplyAction =
                        fun _ _ _ ->
                            mutationCalls <- mutationCalls + 1
                            Task.CompletedTask
                }

            let! result = repairWith dependencies "2026-07-28T00:01:00Z" "repair-test" CancellationToken.None (bound 10) current false

            Assert.That(result.Outcome, Is.EqualTo(RepairOutcome.IncompleteRetain))

            let proposedKinds =
                result.ProposedActions
                |> Array.map (fun action -> action.Kind)

            let expectedKinds =
                [|
                    "ResendDeterministicEvent"
                    "ReconcileCounter"
                |]

            Assert.That((proposedKinds = expectedKinds), Is.True)

            Assert.That(result.AppliedActions, Is.Empty)
            Assert.That(mutationCalls, Is.Zero)
        }

    /// Verifies execute rereads before each mutation, tolerates only planned convergence, and verifies final state.
    [<Test>]
    member _.ExecuteRevalidatesEveryMutationAndVerifiesCompletion() =
        task {
            let initial =
                report
                    DiagnosisOutcome.IncompleteRetain
                    [| relationshipIdentity |]
                    Array.empty
                    0L
                    1L
                    [|
                        $"GetOrAddExactRelationship:{relationshipIdentity}"
                        $"ReconcileCounter:33333333-7350-4000-8000-333333333333|pool|manifest"
                    |]

            let afterResend = report DiagnosisOutcome.VerifiedComplete Array.empty Array.empty 1L 1L Array.empty

            let reads = Queue<ManifestContributionDiagnosisReport>()
            reads.Enqueue initial
            reads.Enqueue initial
            reads.Enqueue afterResend
            let applied = ResizeArray<string>()

            let dependencies =
                {
                    DiagnoseCurrent = fun _ _ _ -> Task.FromResult(reads.Dequeue())
                    ApplyAction =
                        fun _ action _ ->
                            applied.Add action.Action.Kind
                            Task.CompletedTask
                }

            let! result = repairWith dependencies "2026-07-28T00:01:00Z" "repair-test" CancellationToken.None (bound 10) initial true

            Assert.That(result.Outcome, Is.EqualTo(RepairOutcome.VerifiedComplete))
            Assert.That((applied.ToArray() = [| "ResendDeterministicEvent" |]), Is.True)

            let appliedKinds =
                result.AppliedActions
                |> Array.map (fun action -> action.Kind)

            Assert.That((appliedKinds = [| "ResendDeterministicEvent" |]), Is.True)

            Assert.That(reads, Is.Empty)
        }

    /// Verifies changed source evidence retains without applying the stale report plan.
    [<Test>]
    member _.ChangedSourceStateRetainsWithoutMutation() =
        task {
            let initial =
                report
                    DiagnosisOutcome.IncompleteRetain
                    [| relationshipIdentity |]
                    Array.empty
                    1L
                    1L
                    [|
                        $"GetOrAddExactRelationship:{relationshipIdentity}"
                    |]

            let changed =
                { initial with
                    ActorFacts =
                        initial.ActorFacts
                        |> Array.map (fun fact ->
                            if fact.ActorType = "DirectoryVersion" then
                                { fact with SnapshotJson = """{"source":"changed"}""" }
                            else
                                fact)
                }
                |> signReport

            let mutable mutationCalls = 0

            let dependencies =
                {
                    DiagnoseCurrent = fun _ _ _ -> Task.FromResult changed
                    ApplyAction =
                        fun _ _ _ ->
                            mutationCalls <- mutationCalls + 1
                            Task.CompletedTask
                }

            let! result = repairWith dependencies "2026-07-28T00:01:00Z" "repair-test" CancellationToken.None (bound 10) initial true

            Assert.That(result.Outcome, Is.EqualTo(RepairOutcome.IncompleteRetain))
            Assert.That(result.Message, Does.Contain("changed"))
            Assert.That(mutationCalls, Is.Zero)
        }

    /// Verifies a dependency failure retains partial progress and a retry can converge from current exact evidence.
    [<Test>]
    member _.DependencyFailureReturnsFailedRetainAndRetryUsesCurrentPlan() =
        task {
            let initial =
                report
                    DiagnosisOutcome.IncompleteRetain
                    [| relationshipIdentity |]
                    Array.empty
                    1L
                    1L
                    [|
                        $"GetOrAddExactRelationship:{relationshipIdentity}"
                    |]

            let mutable failureCalls = 0

            let failing =
                {
                    DiagnoseCurrent = fun _ _ _ -> Task.FromResult initial
                    ApplyAction =
                        fun _ _ _ ->
                            failureCalls <- failureCalls + 1
                            Task.FromException(InvalidOperationException "focused dependency failure")
                }

            let! failed = repairWith failing "2026-07-28T00:01:00Z" "repair-test" CancellationToken.None (bound 10) initial true

            Assert.That(failed.Outcome, Is.EqualTo(RepairOutcome.FailedRetain))
            Assert.That(failureCalls, Is.EqualTo(1))
            Assert.That(failed.Message, Does.Contain("focused dependency failure"))

            let complete = report DiagnosisOutcome.VerifiedComplete Array.empty Array.empty 1L 1L Array.empty
            let retryReads = Queue<ManifestContributionDiagnosisReport>()
            retryReads.Enqueue initial
            retryReads.Enqueue initial
            retryReads.Enqueue complete

            let diagnoseCurrent _ _ _ = retryReads.Dequeue() |> Task.FromResult

            let applyAction _ _ _ = Task.CompletedTask

            let retry = { DiagnoseCurrent = diagnoseCurrent; ApplyAction = applyAction }

            let! recovered = repairWith retry "2026-07-28T00:02:00Z" "repair-test" CancellationToken.None (bound 10) initial true

            Assert.That(recovered.Outcome, Is.EqualTo(RepairOutcome.VerifiedComplete))
        }

    /// Verifies the operator-facing action contract contains only stable strings and no internal mutation target.
    [<Test>]
    member _.SerializedRepairActionsDoNotLeakInternalTargets() =
        let current =
            report
                DiagnosisOutcome.IncompleteRetain
                [| relationshipIdentity |]
                Array.empty
                1L
                1L
                [|
                    $"GetOrAddExactRelationship:{relationshipIdentity}"
                |]

        let proposed =
            match buildPlan current with
            | Ok actions ->
                actions
                |> Array.map (fun mutation -> mutation.Action)
            | Error error ->
                Assert.Fail error
                Array.empty

        let json = serialize proposed

        Assert.That(json, Does.Contain("\"Kind\""))
        Assert.That(json, Does.Contain("\"Identity\""))
        Assert.That(json, Does.Not.Contain("Target"))
        Assert.That(json, Does.Not.Contain("DirectoryVersionManifest"))

    /// Verifies stale removal rereads every destructive prerequisite and retains when any snapshot changed.
    [<Test>]
    member _.StaleRemovalRequiresImmediateReadableSourceAbsenceAndCoherentSnapshots() =
        task {
            let source = sourceWithoutManifest ()

            let counter =
                { RepositoryContentCounterDto.Default with
                    RepositoryId = repositoryId
                    StoragePoolId = storagePoolId
                    ManifestAddress = manifestAddress
                    Count = 1L
                    Revision = 7L
                }

            let workflow = completedWorkflow ()

            let current =
                report
                    DiagnosisOutcome.IncompleteRetain
                    Array.empty
                    [| relationshipIdentity |]
                    1L
                    1L
                    [|
                        $"RemoveStaleExactRelationship:{relationshipIdentity}"
                    |]
                |> withCurrentActorFacts source counter workflow

            let relationship =
                { RepositoryId = repositoryId; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress; DirectoryVersionId = directoryVersionId }

            let mutable removals = 0

            let valid =
                {
                    GetDirectoryVersion = fun _ -> Task.FromResult source
                    GetCounter = fun _ -> Task.FromResult counter
                    GetWorkflow = fun _ -> Task.FromResult workflow
                    EnsureAbsent =
                        fun _ _ ->
                            removals <- removals + 1
                            Task.FromResult ExactRelationshipWriteOutcome.Changed
                }

            do! removeStaleRelationshipWith valid current relationshipIdentity relationship CancellationToken.None

            Assert.That(removals, Is.EqualTo(1))

            let changedSource = { source with RecursiveSize = 1L }
            let changed = { valid with GetDirectoryVersion = fun _ -> Task.FromResult changedSource }

            Assert.ThrowsAsync<InvalidOperationException>(
                Func<Task>(fun () -> removeStaleRelationshipWith changed current relationshipIdentity relationship CancellationToken.None)
            )
            |> ignore

            Assert.That(removals, Is.EqualTo(1), "Changed source evidence must retain the exact relationship.")
        }

    /// Verifies one counter step uses the fresh revision and preserves the actor's intrinsic zero-crossing workflow.
    [<Test>]
    member _.CounterReconciliationAppliesOneFreshDeterministicCommandAndZeroCrossingWorkflow() =
        task {
            let source = sourceWithoutManifest ()

            let counter =
                { RepositoryContentCounterDto.Default with
                    RepositoryId = repositoryId
                    StoragePoolId = storagePoolId
                    ManifestAddress = manifestAddress
                    Count = 0L
                    Revision = 7L
                }

            let workflow = completedWorkflow ()

            let current =
                report
                    DiagnosisOutcome.IncompleteRetain
                    Array.empty
                    Array.empty
                    0L
                    2L
                    [|
                        $"ReconcileCounter:{repositoryId:D}|{storagePoolId}|{manifestAddress}"
                    |]
                |> withCurrentActorFacts source counter workflow

            let target = { RepositoryId = repositoryId; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress }
            let commands = ResizeArray<RepositoryContentCounterCommand>()
            let workflows = ResizeArray<ManifestContributionDirection * int64>()

            let dependencies =
                {
                    GetCounter = fun _ -> Task.FromResult counter
                    GetWorkflow = fun _ -> Task.FromResult workflow
                    HandleCounter =
                        fun command ->
                            commands.Add command

                            let operationId =
                                match command with
                                | RepositoryContentCounterCommand.AddReference (value, _, _, _)
                                | RepositoryContentCounterCommand.RemoveReference (value, _, _, _) -> value

                            Task.FromResult
                                {
                                    Counter = { counter with Count = 1L; Revision = 8L }
                                    OperationId = operationId
                                    Events = []
                                    Intents =
                                        [
                                            RepositoryContentCounterIntent.IncrementManifestReferenceCount(repositoryId, storagePoolId, manifestAddress, 8L)
                                        ]
                                    WasIdempotentReplay = false
                                    Message = "repair step"
                                }
                    StartWorkflow =
                        fun _ _ direction _ revision ->
                            workflows.Add(direction, revision)
                            Task.CompletedTask
                }

            do! reconcileCounterStepWith dependencies current target 2L

            Assert.That(commands.Count, Is.EqualTo(1))

            Assert.That(
                workflows.ToArray() =
                    [|
                        ManifestContributionDirection.Increment, 8L
                    |],
                Is.True
            )

            match commands[0] with
            | RepositoryContentCounterCommand.AddReference (operationId, _, _, _) -> Assert.That($"{operationId}", Does.Contain("target:2:revision:7:add"))
            | _ -> Assert.Fail("A zero-to-one reconciliation step must use AddReference.")
        }

    /// Verifies a retry resumes only the deterministic workflow left by the repair's completed zero-crossing command.
    [<Test>]
    member _.CounterReconciliationRetryResumesItsIncompleteWorkflowWithoutAnotherCounterWrite() =
        task {
            let source = sourceWithoutManifest ()
            let target = { RepositoryId = repositoryId; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress }
            let counterOperationId = counterRepairOperationId target 1L 7L "add"
            let workflowOperationId = ManifestContributionWorkflowOperationId $"{counterOperationId}:workflow"

            let counter =
                { RepositoryContentCounterDto.Default with
                    RepositoryId = repositoryId
                    StoragePoolId = storagePoolId
                    ManifestAddress = manifestAddress
                    Count = 1L
                    Revision = 8L
                    LastCompletedChange =
                        Some
                            {
                                OperationId = counterOperationId
                                Operation = RepositoryContentCounterChangeOperation.Added
                                PreviousCount = 0L
                                CurrentCount = 1L
                                Revision = 8L
                            }
                }

            let workflow =
                { completedWorkflow () with
                    StartOperationId = Some workflowOperationId
                    LastOperationId = Some workflowOperationId
                    CompletedRanges = Array.empty
                    LifecycleState = ManifestContributionWorkflowLifecycleState.InProgress
                    CounterRevision = 8L
                    Revision = 9L
                }

            let current =
                report DiagnosisOutcome.IncompleteRetain Array.empty Array.empty 1L 1L Array.empty
                |> withCurrentActorFacts source counter workflow

            let mutable counterWrites = 0
            let mutable workflowStarts = 0

            let dependencies =
                {
                    GetCounter = fun _ -> Task.FromResult counter
                    GetWorkflow = fun _ -> Task.FromResult workflow
                    HandleCounter =
                        fun _ ->
                            counterWrites <- counterWrites + 1
                            Task.FromException<RepositoryContentCounterDecision>(InvalidOperationException "unexpected counter write")
                    StartWorkflow =
                        fun operationId _ direction _ revision ->
                            Assert.That(operationId, Is.EqualTo(workflowOperationId))
                            Assert.That(direction, Is.EqualTo(ManifestContributionDirection.Increment))
                            Assert.That(revision, Is.EqualTo(8L))
                            workflowStarts <- workflowStarts + 1
                            Task.CompletedTask
                }

            do! reconcileCounterStepWith dependencies current target 1L

            Assert.That(counterWrites, Is.Zero)
            Assert.That(workflowStarts, Is.EqualTo(1))
        }

    /// Verifies unchanged partial-workflow evidence stops one repair request after one deterministic replay.
    [<Test>]
    member _.RepairStopsWhenDeterministicWorkflowReplayDoesNotChangeEvidence() =
        task {
            let source = sourceWithoutManifest ()
            let target = { RepositoryId = repositoryId; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress }
            let counterOperationId = counterRepairOperationId target 1L 7L "add"
            let workflowOperationId = ManifestContributionWorkflowOperationId $"{counterOperationId}:workflow"

            let originalCounter =
                { RepositoryContentCounterDto.Default with
                    RepositoryId = repositoryId
                    StoragePoolId = storagePoolId
                    ManifestAddress = manifestAddress
                    Count = 0L
                    Revision = 7L
                }

            let partialCounter =
                { originalCounter with
                    Count = 1L
                    Revision = 8L
                    LastCompletedChange =
                        Some
                            {
                                OperationId = counterOperationId
                                Operation = RepositoryContentCounterChangeOperation.Added
                                PreviousCount = 0L
                                CurrentCount = 1L
                                Revision = 8L
                            }
                }

            let completed = completedWorkflow ()

            let partialWorkflow =
                { completed with
                    StartOperationId = Some workflowOperationId
                    LastOperationId = Some workflowOperationId
                    CompletedRanges = Array.empty
                    LifecycleState = ManifestContributionWorkflowLifecycleState.InProgress
                    CounterRevision = 8L
                    Revision = 9L
                }

            let identity = $"{repositoryId:D}|{storagePoolId}|{manifestAddress}"

            let original =
                report DiagnosisOutcome.IncompleteRetain Array.empty Array.empty 0L 1L [| $"ReconcileCounter:{identity}" |]
                |> withCurrentActorFacts source originalCounter completed

            let unchangedPartial =
                report DiagnosisOutcome.IncompleteRetain Array.empty Array.empty 1L 1L Array.empty
                |> withCurrentActorFacts source partialCounter partialWorkflow

            let mutable diagnosisCalls = 0
            let mutable replayCalls = 0

            let dependencies =
                {
                    DiagnoseCurrent =
                        fun _ _ _ ->
                            diagnosisCalls <- diagnosisCalls + 1

                            if diagnosisCalls <= 3 then
                                Task.FromResult unchangedPartial
                            else
                                Task.FromException<ManifestContributionDiagnosisReport>(InvalidOperationException "unbounded replay")
                    ApplyAction =
                        fun _ _ _ ->
                            replayCalls <- replayCalls + 1
                            Task.CompletedTask
                }

            let! result = repairWith dependencies "2026-07-28T00:03:00Z" "repair-test" CancellationToken.None (bound 10) original true

            Assert.That(result.Outcome, Is.EqualTo(RepairOutcome.FailedRetain))
            Assert.That(result.Message, Does.Contain("without changing"))
            Assert.That(replayCalls, Is.EqualTo(1))
        }
