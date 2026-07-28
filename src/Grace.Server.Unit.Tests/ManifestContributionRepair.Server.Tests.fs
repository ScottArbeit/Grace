namespace Grace.Server.Unit.Tests

open Grace.Server.ManifestContributionDiagnosis
open Grace.Server.ManifestContributionRepair
open Grace.Types.ManifestContributionAccounting
open NUnit.Framework
open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Proves bounded repair planning, revalidation, interruption, and dry-run behavior without starting Grace runtime resources.
[<Parallelizable(ParallelScope.All)>]
type ManifestContributionRepairServerTests() =

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
                            applied.Add action.Kind
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
