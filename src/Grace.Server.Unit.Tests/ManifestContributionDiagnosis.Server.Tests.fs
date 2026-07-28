namespace Grace.Server.Unit.Tests

open Grace.Actors
open Grace.Server.ManifestContributionDiagnosis
open Grace.Shared
open Grace.Types
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.ManifestContributionAccounting
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Reference
open Grace.Types.RepositoryContentCounter
open NUnit.Framework
open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Text.Json

/// Proves the bounded diagnosis request and report contracts without starting Grace runtime resources.
[<Parallelizable(ParallelScope.All)>]
type ManifestContributionDiagnosisServerTests() =

    let ownerId = Guid.Parse("11111111-7340-4000-8000-111111111111")
    let organizationId = Guid.Parse("22222222-7340-4000-8000-222222222222")
    let repositoryId = Guid.Parse("33333333-7340-4000-8000-333333333333")
    let directoryVersionId = Guid.Parse("44444444-7340-4000-8000-444444444444")
    let otherDirectoryVersionId = Guid.Parse("55555555-7340-4000-8000-555555555555")
    let referenceId = Guid.Parse("77777777-7340-4000-8000-777777777777")
    let storagePoolId = StoragePoolId "diagnosis:pool"
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

    /// Builds current DirectoryVersion state that directly names the test manifest.
    let directoryVersionDto id =
        let file = FileVersion.CreateWithHashes (RelativePath "diagnose.bin") (Sha256Hash "sha-diagnose") (Blake3Hash fileContentHash) "" true 4L
        file.ContentReference <- FileContentReference.FileManifest finalizedManifest
        let files = List<FileVersion>()
        files.Add file

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
                    (List<DirectoryVersionId>())
                    files
                    4L
            RecursiveSize = 4L
            DeletedAt = None
            DeleteReason = String.Empty
            HashesValidated = true
        }

    /// Creates the shared counter tuple used by focused diagnosis tests.
    let counterTuple () = { RepositoryId = repositoryId; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress }

    /// Creates a completed workflow snapshot that can support complete diagnosis evidence.
    let completedWorkflow (target: CounterTuple) =
        let operationId = ManifestContributionWorkflowOperationId "diagnosis-completed"

        { ManifestContributionWorkflowDto.Default with
            RepositoryId = target.RepositoryId
            StoragePoolId = target.StoragePoolId
            ManifestAddress = target.ManifestAddress
            LifecycleState = ManifestContributionWorkflowLifecycleState.Completed
            StartOperationId = Some operationId
            LastOperationId = Some operationId
            Ranges = workflowRanges
            CompletedRanges =
                workflowRanges
                |> Array.map (fun range ->
                    {
                        OperationId = operationId
                        RepositoryId = target.RepositoryId
                        StoragePoolId = target.StoragePoolId
                        ManifestAddress = target.ManifestAddress
                        Range = range
                    })
            CounterRevision = 7L
            Revision = 8L
        }

    /// Creates the exact manifest relationship named by the focused DirectoryVersion source.
    let manifestRelationship id =
        ExactRelationship.DirectoryVersionManifest
            { RepositoryId = repositoryId; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress; DirectoryVersionId = id }

    /// Creates a valid exact read bound for focused tests.
    let readBound value =
        match ExactRelationshipReadBound.create value with
        | Ok bound -> bound
        | Error error -> invalidOp error

    /// Supplies read-only fakes with per-test relationship behavior.
    let dependencies directoryVersions enumerate verify recentResult storedCount =
        {
            GetReference = fun _ _ -> Task.FromResult ReferenceDto.Default
            GetDirectoryVersion =
                fun id _ ->
                    Task.FromResult(
                        directoryVersions
                        |> Map.tryFind id
                        |> Option.defaultValue DirectoryVersionDto.Default
                    )
            EnumerateRelationships =
                fun _ _ continuationToken _ ->
                    if continuationToken.IsSome then
                        Task.FromResult { Relationships = Array.empty; ContinuationToken = None }
                    else
                        Task.FromResult(enumerate ())
            VerifyRelationship = fun relationship _ -> Task.FromResult(verify relationship)
            GetCounter =
                fun target _ ->
                    Task.FromResult
                        { RepositoryContentCounterDto.Default with
                            RepositoryId = target.RepositoryId
                            StoragePoolId = target.StoragePoolId
                            ManifestAddress = target.ManifestAddress
                            Count = storedCount
                            Revision = 7L
                        }
            GetWorkflow = fun target _ -> Task.FromResult(completedWorkflow target)
            GetRecentResult = fun _ _ _ -> Task.FromResult recentResult
        }

    /// Builds the smallest valid Reference selector.
    let referenceParameters () =
        let parameters = DiagnoseManifestContributionParameters()
        parameters.ReferenceId <- "11111111-1111-1111-1111-111111111111"
        parameters.MaxRelationships <- 1
        parameters

    /// Verifies the selector gate rejects an accidental broad diagnosis.
    [<Test>]
    member _.RequiresExactlyOneSelector() =
        let empty = DiagnoseManifestContributionParameters()
        empty.MaxRelationships <- 1

        match validateParameters empty with
        | Error message -> Assert.That(message, Does.Contain("exactly one selector"))
        | Ok _ -> Assert.Fail("An empty selector must not produce a diagnosis target.")

        let multiple = referenceParameters ()
        multiple.DirectoryVersionId <- "22222222-2222-2222-2222-222222222222"

        match validateParameters multiple with
        | Error message -> Assert.That(message, Does.Contain("exactly one selector"))
        | Ok _ -> Assert.Fail("Multiple selector forms must not produce a diagnosis target.")

    /// Verifies the exact relationship maximum is enforced before any read dependency can run.
    [<Test>]
    member _.RejectsRelationshipBoundsOutsideTheSharedLimit() =
        let parameters = referenceParameters ()
        parameters.MaxRelationships <- ExactRelationshipReadBound.Maximum + 1

        match validateParameters parameters with
        | Error message -> Assert.That(message, Does.Contain(string ExactRelationshipReadBound.Maximum))
        | Ok _ -> Assert.Fail("A relationship bound above the shared maximum must be rejected.")

    /// Verifies a counter tuple is one selector and not three independent selector choices.
    [<Test>]
    member _.AcceptsOnlyCompleteCounterTuple() =
        let parameters = DiagnoseManifestContributionParameters()
        parameters.RepositoryId <- "33333333-3333-3333-3333-333333333333"
        parameters.StoragePoolId <- "pool-main"
        parameters.ManifestAddress <- String.replicate 64 "a"
        parameters.MaxRelationships <- 10

        match validateParameters parameters with
        | Ok (DiagnosisSelector.CounterTuple target) ->
            Assert.That(target.RepositoryId, Is.EqualTo(Guid.Parse parameters.RepositoryId))
            Assert.That(target.StoragePoolId, Is.EqualTo(parameters.StoragePoolId))
            Assert.That(target.ManifestAddress, Is.EqualTo(parameters.ManifestAddress))
        | Ok other -> Assert.Fail($"Expected the counter tuple selector, got {other}.")
        | Error message -> Assert.Fail(message)

        parameters.ManifestAddress <- String.Empty

        match validateParameters parameters with
        | Error message -> Assert.That(message, Does.Contain("complete counter tuple"))
        | Ok _ -> Assert.Fail("A partial counter tuple must be rejected.")

    /// Verifies the report digest covers the compact report with only its digest value blanked.
    [<Test>]
    member _.ReportSha256CanBeVerifiedFromTheSerializedReport() =
        let unsigned =
            emptyReport
                "2026-07-27T00:00:00Z"
                (DiagnosisTarget.Reference "11111111-1111-1111-1111-111111111111")
                10
                DiagnosisOutcome.IncompleteRetain
                [| "RebuiltCount" |]

        let signed = signReport unsigned
        let serialized = JsonSerializer.Serialize(signed, Grace.Shared.Constants.JsonSerializerOptions)
        use document = JsonDocument.Parse serialized

        Assert.That(signed.ReportSha256, Has.Length.EqualTo(64))
        Assert.That(verifySerializedReportSha256 serialized, Is.True)

        Assert.That(
            document.RootElement.TryGetProperty("ReclamationPermitted")
            |> fst,
            Is.False
        )

    /// Verifies Decision A preserves bounded evidence but never invents completeness for a source-less tuple.
    [<Test>]
    member _.CounterTupleWithoutReadableSourceReturnsIncompleteRetain() =
        task {
            let deps =
                dependencies Map.empty (fun () -> { Relationships = Array.empty; ContinuationToken = None }) (fun _ -> ExactRelationshipPresence.Absent) None 0L

            let! report =
                diagnoseWith
                    deps
                    "2026-07-27T00:00:00Z"
                    "diagnosis-test"
                    CancellationToken.None
                    (readBound 10)
                    (DiagnosisSelector.CounterTuple(counterTuple ()))

            Assert.That(report.Outcome, Is.EqualTo(DiagnosisOutcome.IncompleteRetain))
            Assert.That(report.UnknownFields, Does.Contain("MissingRelationships"))
            Assert.That(report.UnknownFields, Does.Contain("RebuiltCount"))

            Assert.That(
                report.ActorFacts
                |> Array.map (fun fact -> fact.ActorType),
                Does.Contain("RepositoryContentCounter")
            )

            Assert.That(
                report.ActorFacts
                |> Array.map (fun fact -> fact.ActorType),
                Does.Contain("ManifestContributionWorkflow")
            )

            Assert.That(report.RedisEvidence, Does.Contain("NotRequested"))
            Assert.That(verifySerializedReportSha256 (JsonSerializer.Serialize(report, Grace.Shared.Constants.JsonSerializerOptions)), Is.True)
        }

    /// Verifies an absent workflow actor remains diagnosable when Orleans returns null enum-like union fields.
    [<Test>]
    member _.AbsentWorkflowSnapshotUsesExplicitNullEvidence() =
        task {
            let target = counterTuple ()

            let absentWorkflow =
                { ManifestContributionWorkflowDto.Default with
                    RepositoryId = target.RepositoryId
                    StoragePoolId = target.StoragePoolId
                    ManifestAddress = target.ManifestAddress
                    Direction = Unchecked.defaultof<ManifestContributionDirection>
                    Ranges = null
                    CompletedRanges = null
                    FailedRanges = null
                    LifecycleState = Unchecked.defaultof<ManifestContributionWorkflowLifecycleState>
                }

            let baseDependencies =
                dependencies
                    (Map.ofList [ directoryVersionId, directoryVersionDto directoryVersionId ])
                    (fun () ->
                        {
                            Relationships =
                                [|
                                    manifestRelationship directoryVersionId
                                |]
                            ContinuationToken = None
                        })
                    (fun _ -> ExactRelationshipPresence.Present)
                    None
                    1L

            let deps = { baseDependencies with GetWorkflow = fun _ _ -> Task.FromResult absentWorkflow }

            let! report =
                diagnoseWith
                    deps
                    "2026-07-27T00:00:00Z"
                    "diagnosis-test"
                    CancellationToken.None
                    (readBound 10)
                    (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))

            let workflowFact =
                report.ActorFacts
                |> Array.find (fun fact -> fact.ActorType = "ManifestContributionWorkflow")

            use snapshot = JsonDocument.Parse(workflowFact.SnapshotJson)

            Assert.That(
                snapshot
                    .RootElement
                    .GetProperty(
                        "Direction"
                    )
                    .ValueKind,
                Is.EqualTo(JsonValueKind.Null)
            )

            Assert.That(
                snapshot
                    .RootElement
                    .GetProperty(
                        "LifecycleState"
                    )
                    .ValueKind,
                Is.EqualTo(JsonValueKind.Null)
            )

            Assert.That(
                snapshot
                    .RootElement
                    .GetProperty(
                        "FailedRanges"
                    )
                    .ValueKind,
                Is.EqualTo(JsonValueKind.Null)
            )

            Assert.That(report.Outcome, Is.EqualTo(DiagnosisOutcome.IncompleteRetain))
            Assert.That(report.CountEvidence[0].RebuiltCount, Is.EqualTo(None))
            Assert.That(report.RepairTargets, Has.None.StartsWith("ReconcileCounter:"))
            Assert.That(report.EvidenceGaps, Has.Some.Contains("absent or unreadable"))
        }

    /// Verifies a readable source produces a concrete missing relationship and counter reconciliation target.
    [<Test>]
    member _.DirectoryVersionSourceProducesConcreteMissingRelationshipEvidence() =
        task {
            let deps =
                dependencies
                    (Map.ofList [ directoryVersionId, directoryVersionDto directoryVersionId ])
                    (fun () -> { Relationships = Array.empty; ContinuationToken = None })
                    (fun _ -> ExactRelationshipPresence.Absent)
                    None
                    0L

            let! report =
                diagnoseWith
                    deps
                    "2026-07-27T00:00:00Z"
                    "diagnosis-test"
                    CancellationToken.None
                    (readBound 10)
                    (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))

            Assert.That(report.Outcome, Is.EqualTo(DiagnosisOutcome.IncompleteRetain))
            Assert.That(report.MissingRelationships, Has.Length.EqualTo(1))
            Assert.That(report.RepairTargets, Has.Some.StartsWith("GetOrAddExactRelationship:"))
            Assert.That(report.RepairTargets, Has.Some.StartsWith("ReconcileCounter:"))
            Assert.That(report.CountEvidence, Has.Length.EqualTo(1))
            Assert.That(report.CountEvidence[0].StoredCount, Is.EqualTo(Some 0L))
            Assert.That(report.CountEvidence[0].RebuiltCount, Is.EqualTo(Some 1L))
        }

    /// Verifies only a readable, target-matching completed workflow can support complete diagnosis evidence.
    [<Test>]
    member _.WorkflowEvidenceMustBeReadableTargetMatchingAndCompleted() =
        task {
            let target = counterTuple ()
            let relationship = manifestRelationship directoryVersionId
            let range = { StoragePoolId = storagePoolId; ContentBlockAddress = finalizedManifest.Blocks[0].Address }
            let operationId = ManifestContributionWorkflowOperationId "diagnosis-incomplete"

            let unfinishedWorkflow =
                { completedWorkflow target with
                    Ranges = [| range |]
                    CompletedRanges = Array.empty
                    LifecycleState = ManifestContributionWorkflowLifecycleState.InProgress
                }

            let failedWorkflow =
                { completedWorkflow target with
                    Ranges = [| range |]
                    CompletedRanges = Array.empty
                    FailedRanges =
                        [|
                            {
                                OperationId = operationId
                                RepositoryId = repositoryId
                                StoragePoolId = storagePoolId
                                ManifestAddress = manifestAddress
                                Range = range
                                Message = "focused failure"
                            }
                        |]
                    LifecycleState = ManifestContributionWorkflowLifecycleState.InProgress
                }

            let mismatchedWorkflow = { completedWorkflow target with RepositoryId = Guid.Parse("66666666-7340-4000-8000-666666666666") }
            let classIncompatibleWorkflow = { completedWorkflow target with Class = "UnexpectedWorkflow" }

            let directionIncompatibleWorkflow = { completedWorkflow target with Direction = ManifestContributionDirection.Decrement }
            let revisionIncompatibleWorkflow = { completedWorkflow target with CounterRevision = 0L }

            let baseDependencies =
                dependencies
                    (Map.ofList [ directoryVersionId, directoryVersionDto directoryVersionId ])
                    (fun () -> { Relationships = [| relationship |]; ContinuationToken = None })
                    (fun _ -> ExactRelationshipPresence.Present)
                    None
                    1L

            let! completeReport =
                diagnoseWith
                    baseDependencies
                    "2026-07-27T00:00:00Z"
                    "diagnosis-test"
                    CancellationToken.None
                    (readBound 10)
                    (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))

            Assert.That(completeReport.Outcome, Is.EqualTo(DiagnosisOutcome.VerifiedComplete))
            Assert.That(completeReport.CountEvidence[0].RebuiltCount, Is.EqualTo(Some 1L))

            let verifyIncompleteWorkflow workflow =
                task {
                    let deps = { baseDependencies with GetWorkflow = fun _ _ -> Task.FromResult workflow }

                    let! report =
                        diagnoseWith
                            deps
                            "2026-07-27T00:00:00Z"
                            "diagnosis-test"
                            CancellationToken.None
                            (readBound 10)
                            (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))

                    Assert.That(report.Outcome, Is.EqualTo(DiagnosisOutcome.IncompleteRetain))
                    Assert.That(report.CountEvidence[0].RebuiltCount, Is.EqualTo(None))
                    Assert.That(report.CountEvidence[0].Completeness, Is.EqualTo("IncompleteRetain"))
                    Assert.That(report.RepairTargets, Has.None.StartsWith("ReconcileCounter:"))
                    Assert.That(report.RepairTargets, Has.None.StartsWith("ResumeManifestContributionWorkflow:"))
                }

            do! verifyIncompleteWorkflow unfinishedWorkflow
            do! verifyIncompleteWorkflow failedWorkflow
            do! verifyIncompleteWorkflow mismatchedWorkflow
            do! verifyIncompleteWorkflow classIncompatibleWorkflow
            do! verifyIncompleteWorkflow directionIncompatibleWorkflow
            do! verifyIncompleteWorkflow revisionIncompatibleWorkflow
        }

    /// Verifies stale relationship removal is advice only when the selected target's workflow and counter evidence are complete.
    [<Test>]
    member _.StaleRelationshipRemovalRequiresCompleteTargetEvidence() =
        task {
            let target = counterTuple ()
            let expectedRelationship = manifestRelationship directoryVersionId
            let staleRelationship = manifestRelationship otherDirectoryVersionId
            let staleSource = directoryVersionDto otherDirectoryVersionId
            staleSource.DirectoryVersion.Files.Clear()

            let baseDependencies =
                dependencies
                    (Map.ofList [ directoryVersionId, directoryVersionDto directoryVersionId
                                  otherDirectoryVersionId, staleSource ])
                    (fun () ->
                        {
                            Relationships =
                                [|
                                    expectedRelationship
                                    staleRelationship
                                |]
                            ContinuationToken = None
                        })
                    (fun _ -> ExactRelationshipPresence.Present)
                    None
                    1L

            let diagnose dependencies =
                diagnoseWith
                    dependencies
                    "2026-07-27T00:00:00Z"
                    "diagnosis-test"
                    CancellationToken.None
                    (readBound 10)
                    (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))

            let! completeEvidenceReport = diagnose baseDependencies
            Assert.That(completeEvidenceReport.StaleRelationships, Has.Length.EqualTo(1))
            Assert.That(completeEvidenceReport.RepairTargets, Has.Some.StartsWith("RemoveStaleExactRelationship:"))

            let incompleteWorkflow = { completedWorkflow target with Ranges = Array.empty; CompletedRanges = Array.empty }

            let invalidCounter =
                { RepositoryContentCounterDto.Default with
                    RepositoryId = target.RepositoryId
                    StoragePoolId = target.StoragePoolId
                    ManifestAddress = target.ManifestAddress
                    Count = 1L
                    Revision = 0L
                }

            let! incompleteWorkflowReport = diagnose { baseDependencies with GetWorkflow = fun _ _ -> Task.FromResult incompleteWorkflow }

            let! invalidCounterReport = diagnose { baseDependencies with GetCounter = fun _ _ -> Task.FromResult invalidCounter }

            Assert.That(incompleteWorkflowReport.RepairTargets, Has.None.StartsWith("RemoveStaleExactRelationship:"))
            Assert.That(invalidCounterReport.RepairTargets, Has.None.StartsWith("RemoveStaleExactRelationship:"))
        }

    /// Verifies completed workflow evidence exactly covers the distinct ContentBlocks in the current source manifest.
    [<Test>]
    member _.CompletedWorkflowMustExactlyCoverCurrentSourceManifestRanges() =
        task {
            let target = counterTuple ()
            let relationship = manifestRelationship directoryVersionId
            let operationId = ManifestContributionWorkflowOperationId "diagnosis-invalid-coverage"

            let progress range =
                {
                    OperationId = operationId
                    RepositoryId = target.RepositoryId
                    StoragePoolId = target.StoragePoolId
                    ManifestAddress = target.ManifestAddress
                    Range = range
                }

            let unexpectedRange = { StoragePoolId = storagePoolId; ContentBlockAddress = ContentBlockAddress(String.replicate 64 "e") }

            let mismatchedRange = { StoragePoolId = storagePoolId; ContentBlockAddress = ContentBlockAddress(String.replicate 64 "f") }

            let completedWith ranges = { completedWorkflow target with Ranges = ranges; CompletedRanges = ranges |> Array.map progress }

            let nullProgressWorkflow =
                { completedWorkflow target with
                    CompletedRanges = Array.create workflowRanges.Length (Unchecked.defaultof<ManifestContributionWorkflowRangeProgress>)
                }

            let invalidWorkflows =
                [|
                    completedWith Array.empty
                    completedWith [| workflowRanges[0] |]
                    completedWith [| workflowRanges[0]
                                     workflowRanges[1]
                                     unexpectedRange |]
                    completedWith [| workflowRanges[0]
                                     mismatchedRange |]
                    completedWith [| workflowRanges[0]
                                     workflowRanges[0] |]
                    nullProgressWorkflow
                |]

            let baseDependencies =
                dependencies
                    (Map.ofList [ directoryVersionId, directoryVersionDto directoryVersionId ])
                    (fun () -> { Relationships = [| relationship |]; ContinuationToken = None })
                    (fun _ -> ExactRelationshipPresence.Present)
                    None
                    1L

            let! completeReport =
                diagnoseWith
                    baseDependencies
                    "2026-07-27T00:00:00Z"
                    "diagnosis-test"
                    CancellationToken.None
                    (readBound 10)
                    (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))

            Assert.That(completeReport.Outcome, Is.EqualTo(DiagnosisOutcome.VerifiedComplete))

            for invalidWorkflow in invalidWorkflows do
                let deps = { baseDependencies with GetWorkflow = fun _ _ -> Task.FromResult invalidWorkflow }

                let! report =
                    diagnoseWith
                        deps
                        "2026-07-27T00:00:00Z"
                        "diagnosis-test"
                        CancellationToken.None
                        (readBound 10)
                        (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))

                Assert.That(report.Outcome, Is.EqualTo(DiagnosisOutcome.IncompleteRetain))
                Assert.That(report.CountEvidence[0].RebuiltCount, Is.EqualTo(None))
                Assert.That(report.RepairTargets, Has.None.StartsWith("ReconcileCounter:"))
                Assert.That(report.RepairTargets, Has.None.StartsWith("ResumeManifestContributionWorkflow:"))
        }

    /// Verifies only an initialized, class-correct counter snapshot for the selected tuple contributes stored count evidence.
    [<Test>]
    member _.CounterSnapshotMustBeInitializedClassCorrectAndTargetMatching() =
        task {
            let target = counterTuple ()
            let relationship = manifestRelationship directoryVersionId

            let validCounter =
                { RepositoryContentCounterDto.Default with
                    RepositoryId = target.RepositoryId
                    StoragePoolId = target.StoragePoolId
                    ManifestAddress = target.ManifestAddress
                    Count = 1L
                    Revision = 7L
                }

            let baseDependencies =
                dependencies
                    (Map.ofList [ directoryVersionId, directoryVersionDto directoryVersionId ])
                    (fun () -> { Relationships = [| relationship |]; ContinuationToken = None })
                    (fun _ -> ExactRelationshipPresence.Present)
                    None
                    1L

            let completeDependencies = { baseDependencies with GetCounter = fun _ _ -> Task.FromResult validCounter }

            let! completeReport =
                diagnoseWith
                    completeDependencies
                    "2026-07-27T00:00:00Z"
                    "diagnosis-test"
                    CancellationToken.None
                    (readBound 10)
                    (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))

            Assert.That(completeReport.Outcome, Is.EqualTo(DiagnosisOutcome.VerifiedComplete))

            let invalidCounters =
                [|
                    { validCounter with Revision = 0L; Count = 0L }
                    { validCounter with Class = "UnexpectedCounter"; Count = 0L }
                    { validCounter with RepositoryId = Guid.Parse("66666666-7340-4000-8000-666666666666"); Count = 0L }
                    { validCounter with StoragePoolId = StoragePoolId "diagnosis:other"; Count = 0L }
                    { validCounter with ManifestAddress = ManifestAddress(String.replicate 64 "a"); Count = 0L }
                |]

            for invalidCounter in invalidCounters do
                let deps = { baseDependencies with GetCounter = fun _ _ -> Task.FromResult invalidCounter }

                let! report =
                    diagnoseWith
                        deps
                        "2026-07-27T00:00:00Z"
                        "diagnosis-test"
                        CancellationToken.None
                        (readBound 10)
                        (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))

                Assert.That(report.Outcome, Is.EqualTo(DiagnosisOutcome.IncompleteRetain))
                Assert.That(report.CountEvidence[0].StoredCount, Is.EqualTo(None))
                Assert.That(report.CountEvidence[0].RebuiltCount, Is.EqualTo(Some 1L))
                Assert.That(report.CountEvidence[0].Completeness, Is.EqualTo("IncompleteRetain"))
                Assert.That(report.RepairTargets, Has.None.StartsWith("ReconcileCounter:"))
                Assert.That(report.EvidenceGaps, Has.Some.Contains("counter"))
        }

    /// Verifies unreadable current source state cannot turn an observed relationship into stale-removal advice.
    [<Test>]
    member _.UnreadableObservedRelationshipRemainsUnknownInsteadOfStale() =
        task {
            let expectedRelationship = manifestRelationship directoryVersionId
            let unreadableRelationship = manifestRelationship otherDirectoryVersionId

            let deps =
                dependencies
                    (Map.ofList [ directoryVersionId, directoryVersionDto directoryVersionId ])
                    (fun () ->
                        {
                            Relationships =
                                [|
                                    expectedRelationship
                                    unreadableRelationship
                                |]
                            ContinuationToken = None
                        })
                    (fun _ -> ExactRelationshipPresence.Present)
                    None
                    1L

            let! report =
                diagnoseWith
                    deps
                    "2026-07-27T00:00:00Z"
                    "diagnosis-test"
                    CancellationToken.None
                    (readBound 10)
                    (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))

            Assert.That(report.Outcome, Is.EqualTo(DiagnosisOutcome.IncompleteRetain))
            Assert.That(report.StaleRelationships, Is.Empty)
            Assert.That(report.RepairTargets, Has.None.StartsWith("RemoveStaleExactRelationship:"))
            Assert.That(report.RepairTargets, Has.None.StartsWith("ReconcileCounter:"))
            Assert.That(report.CountEvidence[0].RebuiltCount, Is.EqualTo(None))
            Assert.That(report.EvidenceGaps, Has.Some.Contains("did not return readable current state"))
        }

    /// Verifies an incomplete exact-partition continuation chain cannot support counter reconciliation.
    [<Test>]
    member _.RepeatedContinuationKeepsRebuiltCountIncomplete() =
        task {
            let relationship = manifestRelationship directoryVersionId

            let baseDependencies =
                dependencies
                    (Map.ofList [ directoryVersionId, directoryVersionDto directoryVersionId ])
                    (fun () -> { Relationships = [| relationship |]; ContinuationToken = None })
                    (fun _ -> ExactRelationshipPresence.Present)
                    None
                    0L

            let deps =
                { baseDependencies with
                    EnumerateRelationships = fun _ _ _ _ -> Task.FromResult { Relationships = [| relationship |]; ContinuationToken = Some "repeated-token" }
                }

            let! report =
                diagnoseWith
                    deps
                    "2026-07-27T00:00:00Z"
                    "diagnosis-test"
                    CancellationToken.None
                    (readBound 10)
                    (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))

            Assert.That(report.Outcome, Is.EqualTo(DiagnosisOutcome.IncompleteRetain))
            Assert.That(report.CountEvidence[0].RebuiltCount, Is.EqualTo(None))
            Assert.That(report.CountEvidence[0].Completeness, Is.EqualTo("IncompleteRetain"))
            Assert.That(report.RepairTargets, Has.None.StartsWith("ReconcileCounter:"))
            Assert.That(report.EvidenceGaps, Has.Some.Contains("repeated a continuation token"))
        }

    /// Verifies DirectoryVersion discovery charges distinct expected relationships before reading a later child actor.
    [<Test>]
    member _.DirectoryVersionDiscoveryStopsBeforeReadingPastTheRelationshipBound() =
        task {
            let root = directoryVersionDto directoryVersionId
            root.DirectoryVersion.Directories.Add otherDirectoryVersionId
            root.DirectoryVersion.Directories.Add otherDirectoryVersionId

            let child = directoryVersionDto otherDirectoryVersionId
            child.DirectoryVersion.Files.Clear()

            let directoryVersions =
                Map.ofList [ directoryVersionId, root
                             otherDirectoryVersionId, child ]

            let createDependencies (reads: ResizeArray<DirectoryVersionId>) =
                let baseDependencies =
                    dependencies
                        directoryVersions
                        (fun () ->
                            {
                                Relationships =
                                    [|
                                        manifestRelationship directoryVersionId
                                    |]
                                ContinuationToken = None
                            })
                        (fun _ -> ExactRelationshipPresence.Present)
                        None
                        1L

                { baseDependencies with
                    GetDirectoryVersion =
                        fun id _ ->
                            reads.Add id
                            Task.FromResult directoryVersions[id]
                }

            let exactReads = ResizeArray<DirectoryVersionId>()

            let! exactReport =
                diagnoseWith
                    (createDependencies exactReads)
                    "2026-07-27T00:00:00Z"
                    "diagnosis-test"
                    CancellationToken.None
                    (readBound 2)
                    (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))

            Assert.That(exactReport.ExpectedRelationships, Has.Length.EqualTo(2))

            Assert.That(
                exactReads.ToArray() =
                    [|
                        directoryVersionId
                        otherDirectoryVersionId
                    |],
                Is.True
            )

            let exceededReads = ResizeArray<DirectoryVersionId>()

            Assert.ThrowsAsync<RelationshipBoundExceeded>(
                Func<Task> (fun () ->
                    diagnoseWith
                        (createDependencies exceededReads)
                        "2026-07-27T00:00:00Z"
                        "diagnosis-test"
                        CancellationToken.None
                        (readBound 1)
                        (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))
                    :> Task)
            )
            |> ignore

            Assert.That(exceededReads.ToArray() = [| directoryVersionId |], Is.True)
        }

    /// Verifies Reference discovery charges its root relationship and stops before reading a child beyond the bound.
    [<Test>]
    member _.ReferenceDiscoveryStopsBeforeReadingPastTheRelationshipBound() =
        task {
            let root = directoryVersionDto directoryVersionId
            root.DirectoryVersion.Directories.Add otherDirectoryVersionId
            root.DirectoryVersion.Directories.Add otherDirectoryVersionId

            let child = directoryVersionDto otherDirectoryVersionId
            child.DirectoryVersion.Files.Clear()

            let directoryVersions =
                Map.ofList [ directoryVersionId, root
                             otherDirectoryVersionId, child ]

            let reference = { ReferenceDto.Default with ReferenceId = referenceId; RepositoryId = repositoryId; DirectoryId = directoryVersionId }

            let createDependencies (reads: ResizeArray<DirectoryVersionId>) =
                let baseDependencies =
                    dependencies
                        directoryVersions
                        (fun () ->
                            {
                                Relationships =
                                    [|
                                        manifestRelationship directoryVersionId
                                    |]
                                ContinuationToken = None
                            })
                        (fun _ -> ExactRelationshipPresence.Present)
                        None
                        1L

                { baseDependencies with
                    GetReference = fun _ _ -> Task.FromResult reference
                    GetDirectoryVersion =
                        fun id _ ->
                            reads.Add id
                            Task.FromResult directoryVersions[id]
                }

            let exactReads = ResizeArray<DirectoryVersionId>()

            let! exactReport =
                diagnoseWith
                    (createDependencies exactReads)
                    "2026-07-27T00:00:00Z"
                    "diagnosis-test"
                    CancellationToken.None
                    (readBound 3)
                    (DiagnosisSelector.ReferenceId(referenceId, Some repositoryId))

            Assert.That(exactReport.ExpectedRelationships, Has.Length.EqualTo(3))

            Assert.That(
                exactReads.ToArray() =
                    [|
                        directoryVersionId
                        otherDirectoryVersionId
                    |],
                Is.True
            )

            let exceededReads = ResizeArray<DirectoryVersionId>()

            Assert.ThrowsAsync<RelationshipBoundExceeded>(
                Func<Task> (fun () ->
                    diagnoseWith
                        (createDependencies exceededReads)
                        "2026-07-27T00:00:00Z"
                        "diagnosis-test"
                        CancellationToken.None
                        (readBound 2)
                        (DiagnosisSelector.ReferenceId(referenceId, Some repositoryId))
                    :> Task)
            )
            |> ignore

            Assert.That(exceededReads.ToArray() = [| directoryVersionId |], Is.True)
        }

    /// Verifies the cumulative relationship bound stops a report before it can imply complete evidence.
    [<Test>]
    member _.RelationshipBoundCountsDistinctEnumeratedIdentities() =
        task {
            let first =
                ExactRelationship.DirectoryVersionManifest
                    { RepositoryId = repositoryId; StoragePoolId = storagePoolId; ManifestAddress = manifestAddress; DirectoryVersionId = directoryVersionId }

            let second =
                ExactRelationship.DirectoryVersionManifest
                    {
                        RepositoryId = repositoryId
                        StoragePoolId = storagePoolId
                        ManifestAddress = manifestAddress
                        DirectoryVersionId = otherDirectoryVersionId
                    }

            let deps =
                dependencies
                    Map.empty
                    (fun () -> { Relationships = [| first; second |]; ContinuationToken = None })
                    (fun _ -> ExactRelationshipPresence.Present)
                    None
                    2L

            Assert.ThrowsAsync<RelationshipBoundExceeded>(
                Func<Task> (fun () ->
                    diagnoseWith
                        deps
                        "2026-07-27T00:00:00Z"
                        "diagnosis-test"
                        CancellationToken.None
                        (readBound 1)
                        (DiagnosisSelector.CounterTuple(counterTuple ()))
                    :> Task)
            )
            |> ignore
        }

    /// Verifies each exact-relationship page receives only the distinct allowance left after discovery and earlier pages.
    [<Test>]
    member _.EnumerationUsesRemainingRelationshipAllowanceAcrossPages() =
        task {
            let expectedRelationship = manifestRelationship directoryVersionId
            let secondRelationship = manifestRelationship otherDirectoryVersionId
            let thirdDirectoryVersionId = Guid.Parse("66666666-7340-4000-8000-666666666666")
            let thirdRelationship = manifestRelationship thirdDirectoryVersionId
            let requestedBounds = ResizeArray<int>()

            let baseDependencies =
                dependencies
                    (Map.ofList [ directoryVersionId, directoryVersionDto directoryVersionId ])
                    (fun () -> { Relationships = Array.empty; ContinuationToken = None })
                    (fun _ -> ExactRelationshipPresence.Present)
                    None
                    1L

            let deps =
                { baseDependencies with
                    EnumerateRelationships =
                        fun _ pageBound continuationToken _ ->
                            requestedBounds.Add(ExactRelationshipReadBound.value pageBound)

                            match continuationToken with
                            | None ->
                                Task.FromResult
                                    {
                                        Relationships =
                                            [|
                                                expectedRelationship
                                                secondRelationship
                                            |]
                                        ContinuationToken = Some "next-page"
                                    }
                            | Some _ -> Task.FromResult { Relationships = [| thirdRelationship |]; ContinuationToken = None }
                }

            let! report =
                diagnoseWith
                    deps
                    "2026-07-27T00:00:00Z"
                    "diagnosis-test"
                    CancellationToken.None
                    (readBound 3)
                    (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))

            Assert.That(requestedBounds.ToArray() = [| 2; 1 |], Is.True)
            Assert.That(report.RelationshipsRead, Is.EqualTo(3))
        }

    /// Verifies an exact discovery bound is not exceeded by an unbudgeted enumeration probe.
    [<Test>]
    member _.EnumerationRetainsWithoutReadingWhenDiscoveryConsumesTheBound() =
        task {
            let mutable enumerationCalls = 0

            let baseDependencies =
                dependencies
                    (Map.ofList [ directoryVersionId, directoryVersionDto directoryVersionId ])
                    (fun () -> { Relationships = Array.empty; ContinuationToken = None })
                    (fun _ -> ExactRelationshipPresence.Present)
                    None
                    1L

            let deps =
                { baseDependencies with
                    EnumerateRelationships =
                        fun _ _ _ _ ->
                            enumerationCalls <- enumerationCalls + 1
                            Task.FromResult { Relationships = Array.empty; ContinuationToken = None }
                }

            let! report =
                diagnoseWith
                    deps
                    "2026-07-27T00:00:00Z"
                    "diagnosis-test"
                    CancellationToken.None
                    (readBound 1)
                    (DiagnosisSelector.DirectoryVersionId(directoryVersionId, Some repositoryId))

            Assert.That(enumerationCalls, Is.Zero)
            Assert.That(report.Outcome, Is.EqualTo(DiagnosisOutcome.IncompleteRetain))
            Assert.That(report.CountEvidence[0].RebuiltCount, Is.EqualTo(None))
            Assert.That(report.EvidenceGaps, Has.Some.Contains("no remaining relationship allowance"))
        }

    /// Verifies ephemeral Redis absence is explicit and does not remove durable actor evidence.
    [<Test>]
    member _.OperationDiagnosisKeepsDurableEvidenceWhenRedisIsAbsent() =
        task {
            let source = directoryVersionDto directoryVersionId

            let operationId = RepositoryContentCounterOperationId $"directory-version:{directoryVersionId:N}:{storagePoolId}:{manifestAddress}:add"

            let deps =
                dependencies
                    (Map.ofList [ directoryVersionId, source ])
                    (fun () -> { Relationships = Array.empty; ContinuationToken = None })
                    (fun _ -> ExactRelationshipPresence.Absent)
                    None
                    0L

            let! report =
                diagnoseWith deps "2026-07-27T00:00:00Z" "diagnosis-test" CancellationToken.None (readBound 10) (DiagnosisSelector.OperationId operationId)

            Assert.That(report.Outcome, Is.EqualTo(DiagnosisOutcome.IncompleteRetain))
            Assert.That(report.RedisEvidence, Does.Contain("AbsentOrUnavailable"))

            Assert.That(
                report.ActorFacts
                |> Array.map (fun fact -> fact.ActorType),
                Does.Contain("DirectoryVersion")
            )

            Assert.That(
                report.ActorFacts
                |> Array.map (fun fact -> fact.ActorType),
                Does.Contain("RepositoryContentCounter")
            )

            Assert.That(
                report.ActorFacts
                |> Array.map (fun fact -> fact.ActorType),
                Does.Contain("ManifestContributionWorkflow")
            )
        }
