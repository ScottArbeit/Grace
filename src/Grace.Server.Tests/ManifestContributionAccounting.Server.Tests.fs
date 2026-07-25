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
open System.Threading
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

/// Proves the public Commit tracer across the real Aspire Service Bus and Cosmos resources.
[<NonParallelizable>]
type ManifestContributionAccountingAspireTests() =

    /// Waits for one canonical exact relationship item in the Aspire Cosmos container.
    let waitForExactRelationshipAsync (state: TestHostState) relationship =
        task {
            let key =
                match ExactRelationshipKey.create relationship with
                | Ok key -> key
                | Error error -> failwith error

            use client = AspireTestHost.createCosmosClient state
            let container = client.GetContainer(state.CosmosDatabaseName, state.CosmosContainerName)
            let timeoutAt = DateTime.UtcNow.AddSeconds(30.0)
            let mutable found = false

            while not found && DateTime.UtcNow < timeoutAt do
                try
                    let! _ = container.ReadItemAsync<JsonElement>(key.ItemId, PartitionKey key.PartitionKey, cancellationToken = CancellationToken.None)

                    found <- true
                with
                | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.NotFound -> do! Task.Delay(TimeSpan.FromMilliseconds(250.0))

            if not found then
                let! logs = AspireTestHost.getGraceServerLogsAsync state
                let! fileLog = AspireTestHost.getGraceServerFileLogAsync state
                let! subscription = AspireTestHost.describeGraceServerSubscriptionAsync state

                Assert.Fail(
                    $"Timed out waiting for exact relationship {key.PartitionKey}/{key.ItemId}.{Environment.NewLine}Grace.Server logs:{Environment.NewLine}{logs}{Environment.NewLine}Grace.Server file log:{Environment.NewLine}{fileLog}{Environment.NewLine}{subscription}"
                )
        }

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

            let root = BranchServerTestHelpers.createRootDirectoryVersion repositoryId fileVersion
            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ root ]

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
                        | ReferenceEventType.Created (createdReferenceId, _, _, _, _, _, _, _, _, _, _) -> createdReferenceId = referenceId
                        | _ -> false
                    | _ -> false)

            Assert.That(envelope.MessageId, Is.EqualTo($"Reference/{referenceId}/Created"))
            Assert.That(envelope.Subject, Is.EqualTo("GraceEvent"))

            let persistedCreatedAt =
                match graceEvent with
                | GraceEvent.ReferenceEvent referenceEvent ->
                    Assert.That(envelope.CorrelationId, Is.EqualTo(referenceEvent.Metadata.CorrelationId))

                    match referenceEvent.Event with
                    | ReferenceEventType.Created (_, _, _, _, _, _, _, _, referenceType, _, _) ->
                        Assert.That(referenceType, Is.EqualTo(ReferenceType.Commit))
                        referenceEvent.Metadata.Timestamp
                    | _ ->
                        Assert.Fail("Expected Reference Created.")
                        Constants.DefaultTimestamp
                | _ ->
                    Assert.Fail("Expected Reference event.")
                    Constants.DefaultTimestamp

            do!
                waitForExactRelationshipAsync
                    state
                    (ExactRelationship.ReferenceRoot
                        { RepositoryId = Guid.Parse repositoryId; RootDirectoryVersionId = root.DirectoryVersionId; ReferenceId = referenceId })

            do!
                waitForExactRelationshipAsync
                    state
                    (ExactRelationship.DirectoryVersionManifest
                        {
                            RepositoryId = Guid.Parse repositoryId
                            StoragePoolId = manifest.StoragePoolId
                            ManifestAddress = manifest.ManifestAddress
                            DirectoryVersionId = root.DirectoryVersionId
                        })

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

            Assert.That(matchingValidationResultEvents.Length, Is.EqualTo(1), "Duplicate Commit delivery must retain one durable quick-scan result.")

            let validationResult =
                matchingValidationResultEvents
                |> Seq.fold (fun current validationResultEvent -> ValidationResultDto.UpdateDto validationResultEvent current) ValidationResultDto.Default

            Assert.That(validationResult.CreatedAt, Is.EqualTo(persistedCreatedAt))
            Assert.That(validationResult.ValidationName, Is.EqualTo("quick-scan"))
            Assert.That(validationResult.ValidationVersion, Is.EqualTo("1.0"))

            let! counterEventStreams =
                ManifestContributionAccountingAspireTestHelpers.readActorEventStreamsAsync<RepositoryContentCounterEvent> state "RepoContentCounter"

            let counter =
                counterEventStreams
                |> Seq.map (fun counterEvents ->
                    counterEvents
                    |> Seq.fold (fun current counterEvent -> RepositoryContentCounterDto.UpdateDto counterEvent current) RepositoryContentCounterDto.Default)
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

            Assert.That(
                retainedMetadata.Ranges
                |> Array.exists (fun range -> range.ActiveManifestCount = 1),
                Is.True,
                "The retained ContentBlock range must be active for the first manifest contribution."
            )

            let! subscription = AspireTestHost.describeGraceServerSubscriptionAsync state
            Assert.That(subscription, Does.Not.Contain($"Reference/{referenceId}/Created delivery="))
        }
