namespace Grace.Server.Tests

open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Common
open Grace.Types.ContentBlockMetadata
open Grace.Types.Events
open Grace.Types.ManifestContributionAccounting
open Grace.Types.Reference
open Grace.Types.RepositoryContentCounter
open Grace.Types.Validation
open Microsoft.Azure.Cosmos
open NUnit.Framework
open System
open System.Collections.Generic
open System.Net
open System.Text.Json
open System.Threading.Tasks

/// Provides typed access to raw actor persistence for the focused Aspire tracer.
module private ManifestContributionAccountingAspireTestHelpers =
    /// Reads persisted actor event streams of one grain type from the shared Aspire Cosmos container.
    let readActorEventStreamsAsync<'T> (state: TestHostState) (grainType: string) =
        task {
            use client = AspireTestHost.createCosmosClient state
            let container = client.GetContainer(state.CosmosDatabaseName, state.CosmosContainerName)
            use iterator = container.GetItemQueryIterator<Dictionary<string, obj>>(QueryDefinition("SELECT * FROM c"))
            let actorEventStreams = ResizeArray<List<'T>>()

            while iterator.HasMoreResults do
                let! page = iterator.ReadNextAsync()

                for document in page do
                    let tryGetJsonElement name =
                        match document.TryGetValue name with
                        | true, (:? JsonElement as value) -> Some value
                        | _ -> None

                    let documentGrainType =
                        tryGetJsonElement "GrainType"
                        |> Option.bind (fun value -> if value.ValueKind = JsonValueKind.String then Some(value.GetString()) else None)
                        |> Option.defaultValue String.Empty

                    if documentGrainType.Equals(grainType, StringComparison.Ordinal) then
                        match tryGetJsonElement "State" with
                        | Some stateValue ->
                            let events = JsonSerializer.Deserialize<List<'T>>(stateValue.GetRawText(), Constants.JsonSerializerOptions)

                            if not (isNull events) then actorEventStreams.Add(events)
                        | None -> ()

            return actorEventStreams
        }

    /// Reads bounded actor snapshots of one grain type from the shared Aspire Cosmos container.
    let readActorSnapshotsAsync<'T> (state: TestHostState) (grainType: string) =
        task {
            use client = AspireTestHost.createCosmosClient state
            let container = client.GetContainer(state.CosmosDatabaseName, state.CosmosContainerName)
            use iterator = container.GetItemQueryIterator<Dictionary<string, obj>>(QueryDefinition("SELECT * FROM c"))
            let actorSnapshots = ResizeArray<'T>()

            while iterator.HasMoreResults do
                let! page = iterator.ReadNextAsync()

                for document in page do
                    let tryGetJsonElement name =
                        match document.TryGetValue name with
                        | true, (:? JsonElement as value) -> Some value
                        | _ -> None

                    let documentGrainType =
                        tryGetJsonElement "GrainType"
                        |> Option.bind (fun value -> if value.ValueKind = JsonValueKind.String then Some(value.GetString()) else None)
                        |> Option.defaultValue String.Empty

                    if documentGrainType.Equals(grainType, StringComparison.Ordinal) then
                        match tryGetJsonElement "State" with
                        | Some stateValue -> actorSnapshots.Add(JsonSerializer.Deserialize<'T>(stateValue.GetRawText(), Constants.JsonSerializerOptions))
                        | None -> ()

            return actorSnapshots
        }

/// Proves the internal bounded repair route against one shared Aspire host.
[<NonParallelizable>]
type ManifestContributionRepairAspireTests() =

    /// Verifies the internal repair route rejects a wrong digest and keeps dry-run and empty execute plans wire-identical.
    [<Test>]
    member _.``manifest contribution repair validates hash and converges an empty bounded plan``() =
        task {
            let! state = AspireTestHost.startAsync testUserId
            let repositoryId = repositoryIds[2]
            let branchId = repositoryDefaultBranchIds[2]
            let! branch = BranchServerTestHelpers.getBranchAsync repositoryId branchId

            let diagnosisRequest =
                {| ReferenceId = String.Empty
                   DirectoryVersionId = $"{branch.LatestReference.DirectoryId}"
                   RepositoryId = repositoryId
                   StoragePoolId = String.Empty
                   ManifestAddress = String.Empty
                   RepositoryContentCounterOperationId = String.Empty
                   MaxRelationships = 100 |}

            let! diagnosisResponse = state.Client.PostAsync("/admin/manifest-contribution/diagnose", createJsonContent diagnosisRequest)

            let! diagnosisJson = diagnosisResponse.Content.ReadAsStringAsync()
            Assert.That(diagnosisResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), diagnosisJson)
            use diagnosisDocument = JsonDocument.Parse diagnosisJson
            let diagnosisSha = diagnosisDocument.RootElement.GetProperty("ReportSha256").GetString()

            let wrongHashRequest = {| ReportJson = diagnosisJson; ExpectedReportSha256 = String.replicate 64 "0"; Execute = false |}

            let! wrongHashResponse = state.Client.PostAsync("/admin/manifest-contribution/repair", createJsonContent wrongHashRequest)

            Assert.That(wrongHashResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))

            let repair execute =
                task {
                    let request = {| ReportJson = diagnosisJson; ExpectedReportSha256 = diagnosisSha; Execute = execute |}

                    let! response = state.Client.PostAsync("/admin/manifest-contribution/repair", createJsonContent request)

                    let! json = response.Content.ReadAsStringAsync()
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), json)
                    return JsonDocument.Parse json
                }

            use! dryRun = repair false
            use! execute = repair true

            let dryRoot = dryRun.RootElement
            let executeRoot = execute.RootElement
            Assert.That(dryRoot.GetProperty("Execute").GetBoolean(), Is.False)
            Assert.That(executeRoot.GetProperty("Execute").GetBoolean(), Is.True)
            Assert.That(dryRoot.GetProperty("ProposedActions").GetArrayLength(), Is.Zero)
            Assert.That(executeRoot.GetProperty("ProposedActions").GetArrayLength(), Is.Zero)
            Assert.That(dryRoot.GetProperty("AppliedActions").GetArrayLength(), Is.Zero)
            Assert.That(executeRoot.GetProperty("AppliedActions").GetArrayLength(), Is.Zero)
            Assert.That(dryRoot.GetProperty("Outcome").GetString(), Is.EqualTo("verifiedComplete"))
            Assert.That(executeRoot.GetProperty("Outcome").GetString(), Is.EqualTo("verifiedComplete"))
        }

/// Proves the public Commit tracer across the real Aspire Service Bus and Cosmos resources.
[<NonParallelizable>]
type ManifestContributionAccountingAspireTests() =

    /// Verifies Commit returns after durable save plus broker acceptance while the existing subscriber converges retained content.
    [<Test>]
    member _.``public Commit publishes deterministic envelope and converges exact manifest relationships``() =
        task {
            let! state = AspireTestHost.startAsync testUserId
            let repositoryId = repositoryIds[2]
            let branchId = repositoryDefaultBranchIds[2]
            let! branch = BranchServerTestHelpers.getBranchAsync repositoryId branchId

            let enableCommit = Parameters.Branch.EnableFeatureParameters()
            enableCommit.OwnerId <- ownerId
            enableCommit.OrganizationId <- organizationId
            enableCommit.RepositoryId <- repositoryId
            enableCommit.BranchId <- branchId
            enableCommit.Enabled <- true
            enableCommit.CorrelationId <- generateCorrelationId ()
            let! enableResponse = state.Client.PostAsync("/branch/enableCommit", createJsonContent enableCommit)
            let! enableBody = enableResponse.Content.ReadAsStringAsync()
            Assert.That(enableResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), enableBody)

            let! uploadCorrelationId, uploadSessionId, block, manifest = RestartDurabilityHelpers.createConfirmedUploadSessionAsync repositoryId

            let! finalizedSession = RestartDurabilityHelpers.finalizeManifestUploadAsync repositoryId uploadCorrelationId uploadSessionId manifest

            Assert.That(finalizedSession.FinalizedManifestAddress, Is.EqualTo(Some manifest.ManifestAddress))

            let fileVersion =
                FileVersion.CreateWithHashes
                    (RelativePath(RestartDurabilityHelpers.exactUploadScope uploadSessionId))
                    (Sha256Hash(String.replicate 64 "d"))
                    (Blake3Hash manifest.FileContentHash)
                    String.Empty
                    true
                    manifest.Size

            fileVersion.ContentReference <- FileContentReference.FileManifest manifest

            let shared = BranchServerTestHelpers.createDirectoryVersionWithFile repositoryId (RelativePath "shared") fileVersion
            let left = BranchServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId (RelativePath "left") [ shared ]
            let right = BranchServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId (RelativePath "right") [ shared ]

            let root = BranchServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId Constants.RootDirectoryPath [ left; right ]

            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ shared; left; right; root ]

            let! _ = AspireTestHost.drainServiceBusAsync state
            let referenceId = Guid.NewGuid()
            let parameters = Parameters.Branch.CommitReferenceParameters()
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- organizationId
            parameters.RepositoryId <- repositoryId
            parameters.BranchId <- string branch.BranchId
            parameters.ReferenceId <- referenceId
            parameters.DirectoryVersionId <- root.DirectoryVersionId
            parameters.Sha256Hash <- root.Sha256Hash
            parameters.Blake3Hash <- root.Blake3Hash
            parameters.Message <- "MCA-01 public Commit tracer"
            parameters.CorrelationId <- generateCorrelationId ()

            let! response = state.Client.PostAsync("/branch/commit", createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)

            let! graceEvent, envelope =
                AspireTestHost.waitForGraceEventMessageAsync state (TimeSpan.FromSeconds(30.0)) "MCA-01 Reference Created event" (fun graceEvent ->
                    match graceEvent with
                    | GraceEvent.ReferenceEvent referenceEvent ->
                        match referenceEvent.Event with
                        | ReferenceEventType.Created(createdReferenceId, _, _, _, _, _, _, _, _, _, _) -> createdReferenceId = referenceId
                        | _ -> false
                    | _ -> false)

            Assert.That(envelope.MessageId, Is.EqualTo($"Reference/{referenceId}/Created"))
            Assert.That(envelope.Subject, Is.EqualTo("GraceEvent"))

            let persistedCreatedAt =
                match graceEvent with
                | GraceEvent.ReferenceEvent referenceEvent ->
                    Assert.That(envelope.CorrelationId, Is.EqualTo(referenceEvent.Metadata.CorrelationId))

                    match referenceEvent.Event with
                    | ReferenceEventType.Created(_, _, _, _, _, _, _, _, referenceType, _, _) ->
                        Assert.That(referenceType, Is.EqualTo(ReferenceType.Commit))
                        referenceEvent.Metadata.Timestamp
                    | _ ->
                        Assert.Fail("Expected Reference Created.")
                        Constants.DefaultTimestamp
                | _ ->
                    Assert.Fail("Expected Reference event.")
                    Constants.DefaultTimestamp

            do!
                AspireTestHost.waitForExactRelationshipAsync
                    state
                    (ExactRelationship.ReferenceRoot
                        { RepositoryId = Guid.Parse repositoryId; RootDirectoryVersionId = root.DirectoryVersionId; ReferenceId = referenceId })

            do!
                AspireTestHost.waitForExactRelationshipAsync
                    state
                    (ExactRelationship.DirectoryVersionManifest
                        { RepositoryId = Guid.Parse repositoryId
                          StoragePoolId = manifest.StoragePoolId
                          ManifestAddress = manifest.ManifestAddress
                          DirectoryVersionId = shared.DirectoryVersionId })

            let expectedEdges =
                [| root.DirectoryVersionId, left.DirectoryVersionId
                   root.DirectoryVersionId, right.DirectoryVersionId
                   left.DirectoryVersionId, shared.DirectoryVersionId
                   right.DirectoryVersionId, shared.DirectoryVersionId |]

            let mutable edgeIndex = 0

            while edgeIndex < expectedEdges.Length do
                let parentDirectoryVersionId, childDirectoryVersionId = expectedEdges[edgeIndex]

                do!
                    AspireTestHost.waitForExactRelationshipAsync
                        state
                        (ExactRelationship.ParentChild
                            { RepositoryId = Guid.Parse repositoryId
                              ParentDirectoryVersionId = parentDirectoryVersionId
                              ChildDirectoryVersionId = childDirectoryVersionId })

                edgeIndex <- edgeIndex + 1

            parameters.CorrelationId <- generateCorrelationId ()
            let! retryResponse = state.Client.PostAsync("/branch/commit", createJsonContent parameters)
            let! retryBody = retryResponse.Content.ReadAsStringAsync()
            Assert.That(retryResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK), retryBody)

            do! Task.Delay(TimeSpan.FromMilliseconds(500.0))

            let! recoveredBranch = BranchServerTestHelpers.getBranchAsync repositoryId branchId
            Assert.That(recoveredBranch.LatestCommit.ReferenceId, Is.EqualTo(referenceId))
            Assert.That(recoveredBranch.LatestReference.ReferenceId, Is.EqualTo(referenceId))

            let! validationResultEventStreams =
                ManifestContributionAccountingAspireTestHelpers.readActorEventStreamsAsync<ValidationResultEvent> state "ValidationResult"

            let expectedValidationResultId = Grace.Server.DerivedComputation.buildQuickScanValidationResultId (Guid.Parse repositoryId) referenceId

            let matchingValidationResultEvents =
                validationResultEventStreams
                |> Seq.collect id
                |> Seq.filter (fun validationResultEvent ->
                    match validationResultEvent.Event with
                    | ValidationResultEventType.Recorded validationResult -> validationResult.ValidationResultId = expectedValidationResultId)
                |> Seq.toArray

            Assert.That(
                matchingValidationResultEvents.Length,
                Is.EqualTo(1),
                "Duplicate Reference delivery through the Commit witness must retain one durable quick-scan result."
            )

            let validationResult =
                matchingValidationResultEvents
                |> Seq.fold (fun current validationResultEvent -> ValidationResultDto.UpdateDto validationResultEvent current) ValidationResultDto.Default

            Assert.That(validationResult.CreatedAt, Is.EqualTo(persistedCreatedAt))
            Assert.That(validationResult.ValidationName, Is.EqualTo("quick-scan"))
            Assert.That(validationResult.ValidationVersion, Is.EqualTo("1.0"))

            let! counterSnapshots =
                ManifestContributionAccountingAspireTestHelpers.readActorSnapshotsAsync<RepositoryContentCounterDto> state "RepoContentCounter"

            let counter =
                counterSnapshots
                |> Seq.tryFind (fun candidate ->
                    candidate.RepositoryId = Guid.Parse(repositoryId)
                    && candidate.StoragePoolId = manifest.StoragePoolId
                    && candidate.ManifestAddress = manifest.ManifestAddress)
                |> Option.defaultWith (fun () -> failwith "The tracer RepositoryContentCounter state was not found.")

            Assert.That(counter.ReferenceCount, Is.EqualTo(1L), "The first exact manifest relationship must contribute exactly once.")

            let! metadataEvents =
                ManifestContributionAccountingAspireTestHelpers.readActorEventStreamsAsync<ContentBlockMetadataEvent> state "ContentBlockMetadata"

            let metadataDto =
                metadataEvents
                |> Seq.map (fun events ->
                    events
                    |> Seq.fold (fun current metadataEvent -> ContentBlockMetadataDto.UpdateDto metadataEvent current) ContentBlockMetadataDto.Empty)
                |> Seq.tryFind (fun candidate ->
                    candidate.Metadata
                    |> Option.exists (fun metadata ->
                        metadata.StoragePoolId = manifest.StoragePoolId
                        && metadata.ContentBlockAddress = block.Address))
                |> Option.defaultWith (fun () -> failwith "The tracer ContentBlockMetadata state was not found.")

            let retainedMetadata =
                metadataDto.Metadata
                |> Option.defaultWith (fun () -> failwith "ContentBlock metadata was not retained.")

            Assert.That(retainedMetadata.ContentBlockAddress, Is.EqualTo(block.Address))
            Assert.That(retainedMetadata.Ranges, Is.Not.Empty, "The retained ContentBlock must contain authoritative physical ranges.")

            Assert.That(
                retainedMetadata.Ranges
                |> Array.forall (fun range -> range.ActiveManifestCount = 1),
                Is.True,
                "Every retained ContentBlock physical range must be active exactly once for the first repository manifest contribution."
            )

            let! subscription = AspireTestHost.describeGraceServerSubscriptionAsync state
            Assert.That(subscription, Does.Not.Contain($"Reference/{referenceId}/Created delivery="))
        }
