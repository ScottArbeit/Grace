namespace Grace.Server

open Grace.Actors
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.ApplicationContext
open Grace.Shared
open Grace.Types
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.ManifestContributionAccounting
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Reference
open Grace.Types.Reminder
open Grace.Types.Repository
open Grace.Types.RepositoryContentCounter
open Microsoft.Azure.Cosmos
open NodaTime
open System
open System.Collections.Generic
open System.Net
open System.Threading
open System.Threading.Tasks

/// Coordinates durable background manifest contribution accounting from persisted Reference events.
module ManifestContributionAccounting =

    /// Stores the provider-neutral exact relationship key in the existing Cosmos container.
    type internal ExactRelationshipDocument() =
        member val id = String.Empty with get, set
        member val PartitionKey = String.Empty with get, set

    /// Creates the exact JSON property names required by the shared Cosmos container.
    let internal createExactRelationshipWriteDocument itemId partitionKey =
        let document = Dictionary<string, obj>()
        document["id"] <- itemId
        document["PartitionKey"] <- partitionKey
        document

    /// Implements conflict-safe exact relationship operations in the existing Grace Cosmos container.
    type CosmosExactRelationshipStore(container: Container) =

        /// Resolves the canonical provider-neutral key or rejects the invalid relationship.
        let key relationship =
            match ExactRelationshipKey.create relationship with
            | Ok key -> key
            | Error error -> invalidArg (nameof relationship) error

        interface IExactRelationshipStore with
            member _.EnsurePresentAsync(relationship, cancellationToken) =
                task {
                    let key = key relationship
                    let document = createExactRelationshipWriteDocument key.ItemId key.PartitionKey

                    try
                        let! _ = container.CreateItemAsync(document, PartitionKey key.PartitionKey, cancellationToken = cancellationToken)

                        return ExactRelationshipWriteOutcome.Changed
                    with
                    | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.Conflict -> return ExactRelationshipWriteOutcome.AlreadyConverged
                }

            member _.EnsureAbsentAsync(relationship, cancellationToken) =
                task {
                    let key = key relationship

                    try
                        let! _ =
                            container.DeleteItemAsync<ExactRelationshipDocument>(
                                key.ItemId,
                                PartitionKey key.PartitionKey,
                                cancellationToken = cancellationToken
                            )

                        return ExactRelationshipWriteOutcome.Changed
                    with
                    | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.NotFound -> return ExactRelationshipWriteOutcome.AlreadyConverged
                }

            member _.EnumerateAsync(partition, bound, continuationToken, cancellationToken) =
                task {
                    let partitionKey =
                        match ExactRelationshipKey.createPartitionKey partition with
                        | Ok partitionKey -> partitionKey
                        | Error error -> invalidArg (nameof partition) error

                    let maximumCount = ExactRelationshipReadBound.value bound

                    let requestOptions = QueryRequestOptions(PartitionKey = Nullable(PartitionKey partitionKey), MaxItemCount = Nullable maximumCount)

                    use iterator =
                        container.GetItemQueryIterator<ExactRelationshipDocument>(
                            QueryDefinition("SELECT c.id, c.PartitionKey FROM c"),
                            continuationToken |> Option.toObj,
                            requestOptions
                        )

                    if not iterator.HasMoreResults then
                        return { Relationships = Array.empty; ContinuationToken = None }
                    else
                        let! response = iterator.ReadNextAsync(cancellationToken)

                        let relationships =
                            response
                            |> Seq.choose (fun document ->
                                ExactRelationshipKey.tryParse { PartitionKey = document.PartitionKey; ItemId = document.id }
                                |> Result.toOption)
                            |> Seq.truncate maximumCount
                            |> Seq.toArray

                        return
                            {
                                Relationships = relationships
                                ContinuationToken =
                                    if String.IsNullOrWhiteSpace response.ContinuationToken then
                                        None
                                    else
                                        Some response.ContinuationToken
                            }
                }

            member _.VerifyAsync(relationship, cancellationToken) =
                task {
                    let key = key relationship

                    try
                        let! _ =
                            container.ReadItemAsync<ExactRelationshipDocument>(key.ItemId, PartitionKey key.PartitionKey, cancellationToken = cancellationToken)

                        return ExactRelationshipPresence.Present
                    with
                    | :? CosmosException as ex when ex.StatusCode = HttpStatusCode.NotFound -> return ExactRelationshipPresence.Absent
                }

    /// Provides current actor reads and exact mutation effects to the provider-neutral convergence loop.
    type ManifestContributionAccountingDependencies =
        {
            GetReference: RepositoryId -> ReferenceId -> CorrelationId -> Task<ReferenceDto>
            GetDirectoryVersion: RepositoryId -> DirectoryVersionId -> CorrelationId -> Task<DirectoryVersionDto>
            ExactRelationships: IExactRelationshipStore
            EnsureAutomaticPhysicalDeletionReminder: RepositoryId -> ReferenceId -> CorrelationId -> CancellationToken -> Task
            EnsureDirectoryVersionManifest: DirectoryVersionManifestRelationship -> FileManifest -> EventMetadata -> CancellationToken -> Task
        }

    /// Runs contribution effects before recording the exact relationship so an unknown counter outcome remains repairable.
    let ensureDirectoryVersionManifestWith
        (verify: ExactRelationship -> CancellationToken -> Task<ExactRelationshipPresence>)
        (applyContribution: unit -> Task)
        (ensurePresent: ExactRelationship -> CancellationToken -> Task<ExactRelationshipWriteOutcome>)
        relationship
        cancellationToken
        =
        task {
            match! verify relationship cancellationToken with
            | ExactRelationshipPresence.Present -> return ExactRelationshipWriteOutcome.AlreadyConverged
            | ExactRelationshipPresence.Absent ->
                do! applyContribution ()
                return! ensurePresent relationship cancellationToken
        }

    /// Returns the direct finalized manifests currently recorded by one DirectoryVersion actor snapshot.
    let private directManifests correlationId (directoryVersionDto: DirectoryVersionDto) =
        match DirectoryVersion.getManifestReferencesForSaveBoundary directoryVersionDto.DirectoryVersion correlationId with
        | Ok manifests ->
            manifests
            |> Seq.map (fun reference -> reference.Manifest)
            |> Seq.distinctBy (fun manifest -> manifest.StoragePoolId, manifest.ManifestAddress)
            |> Seq.toArray
        | Error graceError -> invalidOp graceError.Error

    /// Represents one bounded iterative step while converging a retained DirectoryVersion DAG.
    type private DirectoryTraversalStep =
        | VisitDirectory of directoryVersionId: DirectoryVersionId
        | VisitChild of parentDirectoryVersionId: DirectoryVersionId * childDirectoryVersionId: DirectoryVersionId
        | ConvergeParentChild of parentDirectoryVersionId: DirectoryVersionId * childDirectoryVersionId: DirectoryVersionId

    /// Caps the retained-child probe at the first exact incoming relationship.
    let private oneIncomingRelationshipBound =
        match ExactRelationshipReadBound.create 1 with
        | Ok bound -> bound
        | Error error -> invalidOp error

    /// Confirms the Reference actor still retains the same root named by this delivery.
    let private isCurrentLiveReference
        (dependencies: ManifestContributionAccountingDependencies)
        repositoryId
        referenceId
        rootDirectoryVersionId
        correlationId
        =
        task {
            let! currentReference = dependencies.GetReference repositoryId referenceId correlationId

            return
                currentReference.ReferenceId = referenceId
                && currentReference.RepositoryId = repositoryId
                && currentReference.DirectoryId = rootDirectoryVersionId
                && currentReference.DeletedAt.IsNone
        }

    /// Confirms a DirectoryVersion actor read belongs to the requested immutable repository node.
    let private isCurrentDirectoryVersion repositoryId directoryVersionId (directoryVersionDto: DirectoryVersionDto) =
        directoryVersionDto.DirectoryVersion.RepositoryId = repositoryId
        && directoryVersionDto.DirectoryVersion.DirectoryVersionId = directoryVersionId

    /// Confirms the current immutable parent still directly names the candidate child.
    let private isCurrentDirectChild repositoryId parentDirectoryVersionId childDirectoryVersionId (parentDto: DirectoryVersionDto) =
        isCurrentDirectoryVersion repositoryId parentDirectoryVersionId parentDto
        && parentDto.DirectoryVersion.Directories.Contains childDirectoryVersionId

    /// Reconciles one Reference Created event from fresh actor state before converging its independent lifecycle effect.
    let handleReferenceCreatedWith (dependencies: ManifestContributionAccountingDependencies) cancellationToken (referenceEvent: ReferenceEvent) =
        task {
            match referenceEvent.Event with
            | ReferenceEventType.Created (eventReferenceId, _, _, eventRepositoryId, _, _, _, _, _, _, _) ->
                let correlationId = referenceEvent.Metadata.CorrelationId
                let! currentReference = dependencies.GetReference eventRepositoryId eventReferenceId correlationId

                if currentReference.ReferenceId = eventReferenceId
                   && currentReference.RepositoryId = eventRepositoryId
                   && currentReference.DirectoryId
                      <> DirectoryVersionId.Empty
                   && currentReference.DeletedAt.IsNone then
                    let referenceRoot =
                        ExactRelationship.ReferenceRoot
                            {
                                RepositoryId = currentReference.RepositoryId
                                RootDirectoryVersionId = currentReference.DirectoryId
                                ReferenceId = currentReference.ReferenceId
                            }

                    match! dependencies.ExactRelationships.VerifyAsync(referenceRoot, cancellationToken) with
                    | ExactRelationshipPresence.Present -> ()
                    | ExactRelationshipPresence.Absent ->
                        let! stillLive =
                            isCurrentLiveReference
                                dependencies
                                currentReference.RepositoryId
                                currentReference.ReferenceId
                                currentReference.DirectoryId
                                correlationId

                        if stillLive then
                            let! _ = dependencies.ExactRelationships.EnsurePresentAsync(referenceRoot, cancellationToken)
                            ()

                    let pending = Stack<DirectoryTraversalStep>()
                    pending.Push(VisitDirectory currentReference.DirectoryId)

                    while pending.Count > 0 do
                        cancellationToken.ThrowIfCancellationRequested()

                        match pending.Pop() with
                        | VisitDirectory directoryVersionId ->
                            let! candidateState = dependencies.GetDirectoryVersion currentReference.RepositoryId directoryVersionId correlationId

                            if isCurrentDirectoryVersion currentReference.RepositoryId directoryVersionId candidateState then
                                let manifestCandidates = directManifests correlationId candidateState
                                let mutable manifestIndex = 0

                                while manifestIndex < manifestCandidates.Length do
                                    cancellationToken.ThrowIfCancellationRequested()
                                    let candidate = manifestCandidates[manifestIndex]

                                    let! referenceStillLive =
                                        isCurrentLiveReference
                                            dependencies
                                            currentReference.RepositoryId
                                            currentReference.ReferenceId
                                            currentReference.DirectoryId
                                            correlationId

                                    let! currentDirectoryVersion =
                                        dependencies.GetDirectoryVersion currentReference.RepositoryId directoryVersionId correlationId

                                    let currentManifest =
                                        if referenceStillLive
                                           && isCurrentDirectoryVersion currentReference.RepositoryId directoryVersionId currentDirectoryVersion then
                                            directManifests correlationId currentDirectoryVersion
                                            |> Array.tryFind (fun manifest ->
                                                manifest.StoragePoolId = candidate.StoragePoolId
                                                && manifest.ManifestAddress = candidate.ManifestAddress)
                                        else
                                            None

                                    match currentManifest with
                                    | None -> ()
                                    | Some manifest ->
                                        let relationship =
                                            {
                                                RepositoryId = currentReference.RepositoryId
                                                StoragePoolId = manifest.StoragePoolId
                                                ManifestAddress = manifest.ManifestAddress
                                                DirectoryVersionId = directoryVersionId
                                            }

                                        do! dependencies.EnsureDirectoryVersionManifest relationship manifest referenceEvent.Metadata cancellationToken

                                    manifestIndex <- manifestIndex + 1

                                let childCandidates =
                                    candidateState.DirectoryVersion.Directories
                                    |> Seq.distinct
                                    |> Seq.toArray

                                let mutable childIndex = childCandidates.Length - 1

                                while childIndex >= 0 do
                                    pending.Push(VisitChild(directoryVersionId, childCandidates[childIndex]))
                                    childIndex <- childIndex - 1
                        | VisitChild (parentDirectoryVersionId, childDirectoryVersionId) ->
                            let! referenceStillLive =
                                isCurrentLiveReference
                                    dependencies
                                    currentReference.RepositoryId
                                    currentReference.ReferenceId
                                    currentReference.DirectoryId
                                    correlationId

                            let! currentParent = dependencies.GetDirectoryVersion currentReference.RepositoryId parentDirectoryVersionId correlationId

                            if referenceStillLive
                               && isCurrentDirectChild currentReference.RepositoryId parentDirectoryVersionId childDirectoryVersionId currentParent then
                                let relationship =
                                    ExactRelationship.ParentChild
                                        {
                                            RepositoryId = currentReference.RepositoryId
                                            ParentDirectoryVersionId = parentDirectoryVersionId
                                            ChildDirectoryVersionId = childDirectoryVersionId
                                        }

                                match! dependencies.ExactRelationships.VerifyAsync(relationship, cancellationToken) with
                                | ExactRelationshipPresence.Present -> ()
                                | ExactRelationshipPresence.Absent ->
                                    let! incoming =
                                        dependencies.ExactRelationships.EnumerateAsync(
                                            ExactRelationshipPartition.IncomingDirectoryVersion(currentReference.RepositoryId, childDirectoryVersionId),
                                            oneIncomingRelationshipBound,
                                            None,
                                            cancellationToken
                                        )

                                    if incoming.Relationships.Length > 0 then
                                        let! latestReferenceStillLive =
                                            isCurrentLiveReference
                                                dependencies
                                                currentReference.RepositoryId
                                                currentReference.ReferenceId
                                                currentReference.DirectoryId
                                                correlationId

                                        let! latestParent =
                                            dependencies.GetDirectoryVersion currentReference.RepositoryId parentDirectoryVersionId correlationId

                                        let! latestChild = dependencies.GetDirectoryVersion currentReference.RepositoryId childDirectoryVersionId correlationId

                                        if latestReferenceStillLive
                                           && isCurrentDirectChild currentReference.RepositoryId parentDirectoryVersionId childDirectoryVersionId latestParent
                                           && isCurrentDirectoryVersion currentReference.RepositoryId childDirectoryVersionId latestChild then
                                            let! _ = dependencies.ExactRelationships.EnsurePresentAsync(relationship, cancellationToken)
                                            ()
                                    else
                                        pending.Push(ConvergeParentChild(parentDirectoryVersionId, childDirectoryVersionId))
                                        pending.Push(VisitDirectory childDirectoryVersionId)
                        | ConvergeParentChild (parentDirectoryVersionId, childDirectoryVersionId) ->
                            let! referenceStillLive =
                                isCurrentLiveReference
                                    dependencies
                                    currentReference.RepositoryId
                                    currentReference.ReferenceId
                                    currentReference.DirectoryId
                                    correlationId

                            let! currentParent = dependencies.GetDirectoryVersion currentReference.RepositoryId parentDirectoryVersionId correlationId

                            let! currentChild = dependencies.GetDirectoryVersion currentReference.RepositoryId childDirectoryVersionId correlationId

                            if referenceStillLive
                               && isCurrentDirectChild currentReference.RepositoryId parentDirectoryVersionId childDirectoryVersionId currentParent
                               && isCurrentDirectoryVersion currentReference.RepositoryId childDirectoryVersionId currentChild then
                                let relationship =
                                    ExactRelationship.ParentChild
                                        {
                                            RepositoryId = currentReference.RepositoryId
                                            ParentDirectoryVersionId = parentDirectoryVersionId
                                            ChildDirectoryVersionId = childDirectoryVersionId
                                        }

                                let! _ = dependencies.ExactRelationships.EnsurePresentAsync(relationship, cancellationToken)
                                ()

                    let! referenceStillLive =
                        isCurrentLiveReference
                            dependencies
                            currentReference.RepositoryId
                            currentReference.ReferenceId
                            currentReference.DirectoryId
                            correlationId

                    if referenceStillLive then
                        do!
                            dependencies.EnsureAutomaticPhysicalDeletionReminder
                                currentReference.RepositoryId
                                currentReference.ReferenceId
                                correlationId
                                cancellationToken
            | _ -> ()
        }
        :> Task

    /// Builds the deterministic counter operation for one exact DirectoryVersion-manifest relationship.
    let internal directoryVersionManifestOperationId (relationship: DirectoryVersionManifestRelationship) =
        RepositoryContentCounterOperationId
            $"directory-version:{relationship.DirectoryVersionId:N}:{relationship.StoragePoolId}:{relationship.ManifestAddress}:add"

    /// Builds the deterministic ContentBlock fan-out operation for one exact DirectoryVersion-manifest relationship.
    let internal directoryVersionManifestWorkflowOperationId (relationship: DirectoryVersionManifestRelationship) =
        ManifestContributionWorkflowOperationId
            $"directory-version:{relationship.DirectoryVersionId:N}:{relationship.StoragePoolId}:{relationship.ManifestAddress}:fanout"

    /// Applies the durable repository counter and zero-to-one ContentBlock retention transition.
    let private applyManifestContribution (relationship: DirectoryVersionManifestRelationship) (manifest: FileManifest) (metadata: EventMetadata) =
        task {
            let counterActor =
                grainFactory.CreateActorProxyWithCorrelationId<IRepositoryContentCounterActor>(
                    RepositoryContentCounter.primaryKey relationship.RepositoryId relationship.StoragePoolId relationship.ManifestAddress,
                    metadata.CorrelationId
                )

            let counterCommand =
                RepositoryContentCounterCommand.AddReference(
                    directoryVersionManifestOperationId relationship,
                    relationship.RepositoryId,
                    relationship.StoragePoolId,
                    relationship.ManifestAddress
                )

            match! counterActor.Handle counterCommand metadata with
            | Error graceError -> invalidOp graceError.Error
            | Ok counterReturnValue ->
                let! counterEvents = counterActor.GetEvents metadata.CorrelationId

                let plan: Reference.ManifestSaveContributionPlan =
                    {
                        RepositoryId = relationship.RepositoryId
                        ReferenceId = relationship.DirectoryVersionId
                        Manifest = manifest
                        CounterCommand = counterCommand
                        WorkflowRanges =
                            manifest.Blocks
                            |> Seq.distinctBy (fun block -> block.Address)
                            |> Seq.map (fun block -> { StoragePoolId = manifest.StoragePoolId; ContentBlockAddress = block.Address })
                            |> Seq.toArray
                    }

                match Reference.tryCreateManifestContributionStartForCounterDecision plan counterReturnValue.ReturnValue counterEvents with
                | None -> ()
                | Some startCommand ->
                    let workflowActor =
                        grainFactory.CreateActorProxyWithCorrelationId<IManifestContributionWorkflowActor>(
                            ManifestContributionWorkflow.primaryKey relationship.RepositoryId relationship.StoragePoolId relationship.ManifestAddress,
                            metadata.CorrelationId
                        )

                    match startCommand with
                    | ManifestContributionWorkflowCommand.Start (operationId, repositoryId, storagePoolId, manifestAddress, direction, ranges, counterRevision) ->
                        match! workflowActor.Start operationId repositoryId storagePoolId manifestAddress direction ranges counterRevision metadata with
                        | Ok _ -> ()
                        | Error graceError -> invalidOp graceError.Error
                    | _ -> invalidOp "Manifest contribution accounting expected a workflow start command."
        }
        :> Task

    /// Ensures one direct manifest relationship using current durable counter and exact relationship state.
    let internal ensureDirectoryVersionManifest
        (store: IExactRelationshipStore)
        (relationship: DirectoryVersionManifestRelationship)
        (manifest: FileManifest)
        metadata
        cancellationToken
        =
        let exactRelationship = ExactRelationship.DirectoryVersionManifest relationship

        ensureDirectoryVersionManifestWith
            (fun relationship cancellationToken -> store.VerifyAsync(relationship, cancellationToken))
            (fun () -> applyManifestContribution relationship manifest metadata)
            (fun relationship cancellationToken -> store.EnsurePresentAsync(relationship, cancellationToken))
            exactRelationship
            cancellationToken
        :> Task

    /// Handles Reference events on the existing subscriber without adding a second queue or dispatcher.
    let handleReferenceEvent referenceEvent =
        let store = CosmosExactRelationshipStore(cosmosContainer) :> IExactRelationshipStore

        let dependencies =
            {
                GetReference =
                    fun repositoryId referenceId correlationId ->
                        let actor = Reference.CreateActorProxy referenceId repositoryId correlationId
                        actor.Get correlationId
                GetDirectoryVersion =
                    fun repositoryId directoryVersionId correlationId ->
                        let actor = DirectoryVersion.CreateActorProxy directoryVersionId repositoryId correlationId
                        actor.Get correlationId
                ExactRelationships = store
                EnsureAutomaticPhysicalDeletionReminder =
                    fun repositoryId referenceId correlationId cancellationToken ->
                        cancellationToken.ThrowIfCancellationRequested()
                        let actor = Reference.CreateActorProxy referenceId repositoryId correlationId
                        actor.EnsureAutomaticPhysicalDeletionReminderAsync correlationId
                EnsureDirectoryVersionManifest =
                    fun relationship manifest metadata cancellationToken ->
                        ensureDirectoryVersionManifest store relationship manifest metadata cancellationToken
            }

        handleReferenceCreatedWith dependencies CancellationToken.None referenceEvent
