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
                    ContentBlock.Create(ContentBlockAddress(String.replicate 64 "c"), 0L, 4L)
                ]
            )

        { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }

    let manifestAddress = finalizedManifest.ManifestAddress

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
            GetWorkflow =
                fun target _ ->
                    Task.FromResult
                        { ManifestContributionWorkflowDto.Default with
                            RepositoryId = target.RepositoryId
                            StoragePoolId = target.StoragePoolId
                            ManifestAddress = target.ManifestAddress
                            Revision = 8L
                        }
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

        Assert.That(signed.ReportSha256, Has.Length.EqualTo(64))
        Assert.That(verifySerializedReportSha256 serialized, Is.True)

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
            Assert.That(report.ReclamationPermitted, Is.False)
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
                dependencies Map.empty (fun () -> { Relationships = Array.empty; ContinuationToken = None }) (fun _ -> ExactRelationshipPresence.Absent) None 0L

            let deps = { baseDependencies with GetWorkflow = fun _ _ -> Task.FromResult absentWorkflow }

            let! report =
                diagnoseWith deps "2026-07-27T00:00:00Z" "diagnosis-test" CancellationToken.None (readBound 10) (DiagnosisSelector.CounterTuple target)

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
            Assert.That(report.ReclamationPermitted, Is.False)
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
