namespace Grace.Actors

open Azure.Identity
open Azure.Messaging.ServiceBus
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Timing
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Events
open Grace.Types.ManifestContributionWorkflow
open Grace.Types.Reference
open Grace.Types.Reminder
open Grace.Types.RepositoryContentCounter
open Grace.Types.Common
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks

/// Groups Orleans actor helpers for reference keys, proxies, state, or workflow transitions.
module Reference =

    /// Sequences durable Reference Created persistence before broker publication.
    let persistReferenceCreatedThenPublish (persist: unit -> Task) (publish: unit -> Task) =
        task {
            do! persist ()
            do! publish ()
        }

    /// Names producer capability metadata that permits a failed broker send to cross the public operation boundary.
    [<Literal>]
    let internal ReferenceCreatedRequiresBrokerAcceptanceProperty = "ReferenceCreatedRequiresBrokerAcceptance"

    /// Defers Reference publication logging until a broker-bound operation actually needs runtime configuration.
    let private referencePublicationLog = lazy (loggerFactory.CreateLogger("ReferencePublication.Actor"))

    /// Defers managed-identity construction until Azure Service Bus publication is selected.
    let private referencePublicationCredential = lazy (DefaultAzureCredential())

    /// Creates the shared Reference publication client only after broker-bound runtime configuration is available.
    let private referencePublicationClient =
        lazy
            let settings = pubSubSettings.AzureServiceBus.Value

            if settings.UseManagedIdentity then
                let fullyQualifiedNamespace =
                    if not (String.IsNullOrWhiteSpace settings.FullyQualifiedNamespace) then
                        settings.FullyQualifiedNamespace
                    else
                        Grace.Shared.AzureEnvironment.tryGetServiceBusFullyQualifiedNamespace ()
                        |> Option.defaultWith (fun () -> invalidOp "Azure Service Bus namespace is required for managed identity.")

                ServiceBusClient(fullyQualifiedNamespace, referencePublicationCredential.Value)
            else
                ServiceBusClient(settings.ConnectionString)

    /// Reuses one sender for deterministic Reference Created publication on the existing GraceEvent topic.
    let private referencePublicationSender = lazy (referencePublicationClient.Value.CreateSender(pubSubSettings.AzureServiceBus.Value.TopicName))

    /// Reports whether the producer can retry the persisted Reference with its caller-owned identity.
    let internal referenceCreatedRequiresBrokerAcceptance (metadata: EventMetadata) =
        match metadata.Properties.TryGetValue ReferenceCreatedRequiresBrokerAcceptanceProperty with
        | true, value ->
            match Boolean.TryParse value with
            | true, parsed -> parsed
            | _ -> false
        | _ -> false

    /// Creates the deterministic Service Bus envelope for one persisted Reference Created event.
    let internal createReferenceCreatedServiceBusMessage (referenceEvent: ReferenceEvent) =
        let referenceId =
            match referenceEvent.Event with
            | ReferenceEventType.Created (referenceId, _, _, _, _, _, _, _, _, _, _) -> referenceId
            | eventType -> invalidArg (nameof referenceEvent) $"Reference Created publication does not accept {getDiscriminatedUnionCaseName eventType} events."

        let graceEvent = GraceEvent.ReferenceEvent referenceEvent
        let payload = JsonSerializer.SerializeToUtf8Bytes(graceEvent, Constants.JsonSerializerOptions)
        let message = ServiceBusMessage(payload)
        message.ContentType <- "application/json"
        message.Subject <- "GraceEvent"
        message.CorrelationId <- referenceEvent.Metadata.CorrelationId
        message.MessageId <- $"Reference/{referenceId}/Created"
        message.ApplicationProperties[ "graceEventType" ] <- getDiscriminatedUnionFullName graceEvent

        for kvp in referenceEvent.Metadata.Properties do
            message.ApplicationProperties[ kvp.Key ] <- kvp.Value

        message

    /// Publishes one persisted Reference Created event with a deterministic identity.
    let internal publishReferenceCreatedGraceEvent (referenceEvent: ReferenceEvent) =
        task {
            let requiresAcceptance = referenceCreatedRequiresBrokerAcceptance referenceEvent.Metadata

            try
                match pubSubSettings.System, pubSubSettings.AzureServiceBus with
                | GracePubSubSystem.AzureServiceBus, Some _ ->
                    let message = createReferenceCreatedServiceBusMessage referenceEvent
                    do! referencePublicationSender.Value.SendMessageAsync(message)

                    referencePublicationLog.Value.LogInformation(
                        "{CurrentInstant}: Published Reference Created event via Azure Service Bus. CorrelationId: {CorrelationId}; MessageId: {MessageId}.",
                        getCurrentInstantExtended (),
                        referenceEvent.Metadata.CorrelationId,
                        message.MessageId
                    )
                | GracePubSubSystem.AzureServiceBus, None ->
                    invalidOp "Azure Service Bus is selected for Reference Created publication, but its settings are missing."
                | otherSystem, _ ->
                    invalidOp $"Reference Created publication requires Azure Service Bus, but Grace pub-sub is {getDiscriminatedUnionCaseName otherSystem}."
            with
            | ex when not requiresAcceptance ->
                referencePublicationLog.Value.LogError(
                    ex,
                    "{CurrentInstant}: Best-effort Reference Created publication failed before stable identity propagation. CorrelationId: {CorrelationId}.",
                    getCurrentInstantExtended (),
                    referenceEvent.Metadata.CorrelationId
                )
        }
        :> Task

    /// Wraps manifest save contribution plan records exchanged by actor queries or projections.
    type ManifestSaveContributionPlan =
        {
            RepositoryId: RepositoryId
            ReferenceId: ReferenceId
            Manifest: FileManifest
            CounterCommand: RepositoryContentCounterCommand
            WorkflowRanges: ManifestContributionWorkflowRange array
        }

    /// Coordinates manifest contribution operation id logic for the Reference actor.
    let private manifestContributionOperationId (referenceId: ReferenceId) (storagePoolId: StoragePoolId) (manifestAddress: ManifestAddress) =
        RepositoryContentCounterOperationId $"reference:{referenceId:N}:{storagePoolId}:{manifestAddress}"

    /// Coordinates repository content counter primary key logic for the Reference actor.
    let private repositoryContentCounterPrimaryKey (repositoryId: RepositoryId) (storagePoolId: StoragePoolId) (manifestAddress: ManifestAddress) =
        $"{repositoryId:N}|{storagePoolId}|{manifestAddress}"

    /// Coordinates manifest contribution workflow primary key logic for the Reference actor.
    let private manifestContributionWorkflowPrimaryKey (repositoryId: RepositoryId) (storagePoolId: StoragePoolId) (manifestAddress: ManifestAddress) =
        $"{repositoryId:N}|{storagePoolId}|{manifestAddress}"

    /// Coordinates counter command direction logic for the Reference actor.
    let private counterCommandDirection command =
        match command with
        | RepositoryContentCounterCommand.AddReference _ -> ManifestContributionDirection.Increment
        | RepositoryContentCounterCommand.RemoveReference _ -> ManifestContributionDirection.Decrement

    /// Coordinates workflow operation id logic for the Reference actor.
    let private workflowOperationId (referenceId: ReferenceId) (storagePoolId: StoragePoolId) (manifestAddress: ManifestAddress) direction =
        match direction with
        | ManifestContributionDirection.Increment ->
            ManifestContributionWorkflowOperationId $"reference:{referenceId:N}:{storagePoolId}:{manifestAddress}:fanout"
        | ManifestContributionDirection.Decrement ->
            ManifestContributionWorkflowOperationId $"reference-expiry:{referenceId:N}:{storagePoolId}:{manifestAddress}:fanout"

    /// Coordinates workflow ranges for manifest logic for the Reference actor.
    let private workflowRangesForManifest (manifest: FileManifest) =
        let storagePoolId = manifest.StoragePoolId
        let seenContentBlocks = HashSet<ContentBlockAddress>()
        let ranges = ResizeArray<ManifestContributionWorkflowRange>()
        let mutable index = 0

        while index < manifest.Blocks.Count do
            let block = manifest.Blocks[index]

            if seenContentBlocks.Add block.Address then
                ranges.Add({ StoragePoolId = storagePoolId; ContentBlockAddress = block.Address })

            index <- index + 1

        ranges.ToArray()

    /// Coordinates workflow start command for plan logic for the Reference actor.
    let private workflowStartCommandForPlan plan counterRevision =
        let direction = counterCommandDirection plan.CounterCommand

        ManifestContributionWorkflowCommand.Start(
            workflowOperationId plan.ReferenceId plan.Manifest.StoragePoolId plan.Manifest.ManifestAddress direction,
            plan.RepositoryId,
            plan.Manifest.StoragePoolId,
            plan.Manifest.ManifestAddress,
            direction,
            plan.WorkflowRanges,
            counterRevision
        )

    let private planManifestReferences
        repositoryId
        referenceId
        (manifestReferences: DirectoryVersion.ManifestReferenceForSaveBoundary seq)
        : ManifestSaveContributionPlan list
        =
        manifestReferences
        |> Seq.distinctBy (fun manifestReference -> manifestReference.Manifest.StoragePoolId, manifestReference.Manifest.ManifestAddress)
        |> Seq.map (fun manifestReference -> manifestReference.Manifest)
        |> Seq.toList
        |> List.map (fun manifest ->
            let operationId = manifestContributionOperationId referenceId manifest.StoragePoolId manifest.ManifestAddress

            {
                RepositoryId = repositoryId
                ReferenceId = referenceId
                Manifest = manifest
                CounterCommand = RepositoryContentCounterCommand.AddReference(operationId, repositoryId, manifest.StoragePoolId, manifest.ManifestAddress)
                WorkflowRanges = workflowRangesForManifest manifest
            })

    /// Plans plan manifest save boundary for directory versions work for the Reference actor workflow.
    let planManifestSaveBoundaryForDirectoryVersions repositoryId referenceId (directoryVersions: DirectoryVersion seq) correlationId =
        let directoryVersionArray = directoryVersions |> Seq.toArray
        let manifestReferences = ResizeArray<DirectoryVersion.ManifestReferenceForSaveBoundary>()
        let mutable index = 0
        let mutable error: GraceError option = None

        while index < directoryVersionArray.Length
              && error.IsNone do
            match DirectoryVersion.getManifestReferencesForSaveBoundary directoryVersionArray[index] correlationId with
            | Error graceError -> error <- Some graceError
            | Ok directoryManifestReferences -> manifestReferences.AddRange directoryManifestReferences

            index <- index + 1

        match error with
        | Some graceError -> Error graceError
        | None -> Ok(planManifestReferences repositoryId referenceId manifestReferences)

    /// Validates recursive directory versions complete before the operation continues.
    let validateRecursiveDirectoryVersionsComplete rootDirectoryVersionId (directoryVersions: DirectoryVersion seq) correlationId =
        let directoryVersionArray = directoryVersions |> Seq.toArray

        let directoryVersionIds =
            HashSet<DirectoryVersionId>(
                directoryVersionArray
                |> Seq.map (fun directoryVersion -> directoryVersion.DirectoryVersionId)
            )

        if not (directoryVersionIds.Contains rootDirectoryVersionId) then
            Error(
                (GraceError.Create "Recursive directory traversal did not include the root DirectoryVersion." correlationId)
                    .enhance (nameof DirectoryVersionId, rootDirectoryVersionId)
            )
        else
            let missingChild =
                directoryVersionArray
                |> Seq.collect (fun directoryVersion ->
                    directoryVersion.Directories
                    |> Seq.map (fun childDirectoryVersionId -> directoryVersion, childDirectoryVersionId))
                |> Seq.tryFind (fun (_, childDirectoryVersionId) -> not (directoryVersionIds.Contains childDirectoryVersionId))

            match missingChild with
            | Some (parentDirectoryVersion, childDirectoryVersionId) ->
                Error(
                    (GraceError.Create "Recursive directory traversal did not include a declared child DirectoryVersion." correlationId)
                        .enhance(nameof DirectoryVersionId, rootDirectoryVersionId)
                        .enhance("ParentDirectoryVersionId", parentDirectoryVersion.DirectoryVersionId)
                        .enhance ("ChildDirectoryVersionId", childDirectoryVersionId)
                )
            | None -> Ok directoryVersionArray

    /// Plans plan manifest save boundary for recursive directory versions work for the Reference actor workflow.
    let planManifestSaveBoundaryForRecursiveDirectoryVersions repositoryId referenceId rootDirectoryVersionId directoryVersions correlationId =
        match validateRecursiveDirectoryVersionsComplete rootDirectoryVersionId directoryVersions correlationId with
        | Error graceError -> Error graceError
        | Ok completeDirectoryVersions -> planManifestSaveBoundaryForDirectoryVersions repositoryId referenceId completeDirectoryVersions correlationId

    /// Plans plan manifest save boundary work for the Reference actor workflow.
    let planManifestSaveBoundary repositoryId referenceId (directoryVersion: DirectoryVersion) correlationId =
        planManifestSaveBoundaryForDirectoryVersions repositoryId referenceId [ directoryVersion ] correlationId

    /// Completes Reference physical deletion without changing DirectoryVersion-owned manifest retention.
    let internal completeReferencePhysicalDeletion (markBranchForRecompute: unit -> Task) (clearReferenceState: unit -> Task) =
        task {
            do! markBranchForRecompute ()
            do! clearReferenceState ()
        }

    /// Validates reference root directory version hashes before the operation continues.
    let validateReferenceRootDirectoryVersionHashes correlationId repositoryId directoryId sha256Hash blake3Hash (directoryVersion: DirectoryVersion) =
        let rootRelativePath = directoryVersion.RelativePath

        let isRootDirectoryRelativePath =
            rootRelativePath = Constants.RootDirectoryPath
            || rootRelativePath = "/"

        let directoryVersionBlake3IsEmpty = String.IsNullOrWhiteSpace(string directoryVersion.Blake3Hash)
        let commandBlake3IsEmpty = String.IsNullOrWhiteSpace(string blake3Hash)

        if directoryVersion.DirectoryVersionId = DirectoryVersionId.Empty then
            Error(
                (GraceError.Create "Reference root DirectoryVersion does not exist." correlationId)
                    .enhance(nameof RepositoryId, repositoryId)
                    .enhance (nameof DirectoryVersionId, directoryId)
            )
        elif not isRootDirectoryRelativePath then
            Error(
                (GraceError.Create "Reference root DirectoryVersion must use the repository root path." correlationId)
                    .enhance(nameof RepositoryId, repositoryId)
                    .enhance(nameof DirectoryVersionId, directoryId)
                    .enhance (nameof RelativePath, directoryVersion.RelativePath)
            )
        elif directoryVersionBlake3IsEmpty then
            Error(
                (GraceError.Create "Reference root DirectoryVersion must include Blake3Hash before reference creation." correlationId)
                    .enhance(nameof RepositoryId, repositoryId)
                    .enhance (nameof DirectoryVersionId, directoryId)
            )
        elif commandBlake3IsEmpty then
            Error(
                (GraceError.Create "Reference command must include the root DirectoryVersion Blake3Hash." correlationId)
                    .enhance(nameof RepositoryId, repositoryId)
                    .enhance (nameof DirectoryVersionId, directoryId)
            )
        elif directoryVersion.Sha256Hash <> sha256Hash then
            Error(
                (GraceError.Create "Reference command Sha256Hash does not match the root DirectoryVersion Sha256Hash." correlationId)
                    .enhance(nameof RepositoryId, repositoryId)
                    .enhance(nameof DirectoryVersionId, directoryId)
                    .enhance (nameof Sha256Hash, sha256Hash)
            )
        elif directoryVersion.Blake3Hash <> blake3Hash then
            Error(
                (GraceError.Create "Reference command Blake3Hash does not match the root DirectoryVersion Blake3Hash." correlationId)
                    .enhance(nameof RepositoryId, repositoryId)
                    .enhance(nameof DirectoryVersionId, directoryId)
                    .enhance (nameof Blake3Hash, blake3Hash)
            )
        else
            Ok()

    /// Builds command matches reference data needed by the Reference actor.
    let internal createCommandMatchesReference (referenceDto: ReferenceDto) command =
        match command with
        | Create (referenceId, ownerId, organizationId, repositoryId, branchId, directoryId, sha256Hash, blake3Hash, referenceType, referenceText, links) ->
            referenceDto.UpdatedAt.IsSome
            && referenceDto.ReferenceId = referenceId
            && referenceDto.OwnerId = ownerId
            && referenceDto.OrganizationId = organizationId
            && referenceDto.RepositoryId = repositoryId
            && referenceDto.BranchId = branchId
            && referenceDto.DirectoryId = directoryId
            && referenceDto.Sha256Hash = sha256Hash
            && referenceDto.Blake3Hash = blake3Hash
            && referenceDto.ReferenceType = referenceType
            && referenceDto.ReferenceText = referenceText
            && (referenceDto.Links |> Seq.toArray) = (links |> Seq.toArray)
        | _ -> false

    /// Derives the versioned stable reminder identity for one Reference's automatic physical deletion.
    let internal automaticPhysicalDeletionReminderId (repositoryId: RepositoryId) (referenceId: ReferenceId) =
        let seed = $"grace.reference-automatic-physical-deletion.v1|{repositoryId:N}|{referenceId:N}"
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed))
        let guidBytes = hash[0..15]
        guidBytes[7] <- (guidBytes[7] &&& 0x0Fuy) ||| 0x50uy
        guidBytes[8] <- (guidBytes[8] &&& 0x3Fuy) ||| 0x80uy
        ReminderId(guidBytes)

    /// Compares only stable Reference target facts while allowing the first durable reminder metadata to remain unchanged.
    let internal automaticPhysicalDeletionReminderMatches (requested: ReminderDto) (existing: ReminderDto) =
        requested.ReminderId = existing.ReminderId
        && requested.ActorName = existing.ActorName
        && requested.ActorId = existing.ActorId
        && requested.OwnerId = existing.OwnerId
        && requested.OrganizationId = existing.OrganizationId
        && requested.RepositoryId = existing.RepositoryId
        && requested.ReminderType = existing.ReminderType
        && match requested.State, existing.State with
           | ReminderState.ReferencePhysicalDeletion requestedState, ReminderState.ReferencePhysicalDeletion existingState ->
               requestedState.RepositoryId = existingState.RepositoryId
               && requestedState.BranchId = existingState.BranchId
               && requestedState.DirectoryVersionId = existingState.DirectoryVersionId
               && requestedState.Sha256Hash = existingState.Sha256Hash
               && requestedState.Blake3Hash = existingState.Blake3Hash
           | _ -> false

    /// Attempts to create manifest contribution start and returns no value when the required invariant is not met.
    let tryCreateManifestContributionStart plan intent =
        match intent with
        | RepositoryContentCounterIntent.IncrementManifestReferenceCount (repositoryId, storagePoolId, manifestAddress, counterRevision) when
            repositoryId = plan.RepositoryId
            && storagePoolId = plan.Manifest.StoragePoolId
            && manifestAddress = plan.Manifest.ManifestAddress
            && counterCommandDirection plan.CounterCommand = ManifestContributionDirection.Increment
            ->
            Some(workflowStartCommandForPlan plan counterRevision)
        | RepositoryContentCounterIntent.DecrementManifestReferenceCount (repositoryId, storagePoolId, manifestAddress, counterRevision) when
            repositoryId = plan.RepositoryId
            && storagePoolId = plan.Manifest.StoragePoolId
            && manifestAddress = plan.Manifest.ManifestAddress
            && counterCommandDirection plan.CounterCommand = ManifestContributionDirection.Decrement
            ->
            Some(workflowStartCommandForPlan plan counterRevision)
        | _ -> None

    /// Creates workflow work only from the counter decision's revision-bearing zero-crossing intent.
    let tryCreateManifestContributionStartForCounterDecision plan (decision: RepositoryContentCounterDecision) _events =
        decision.Intents
        |> List.tryPick (tryCreateManifestContributionStart plan)

    /// Builds repository content counter actor data needed by the Reference actor.
    let private createRepositoryContentCounterActor repositoryId storagePoolId manifestAddress correlationId =
        let grain =
            orleansClient.CreateActorProxyWithCorrelationId<IRepositoryContentCounterActor>(
                repositoryContentCounterPrimaryKey repositoryId storagePoolId manifestAddress,
                correlationId
            )

        let orleansContext = Dictionary<string, obj>()
        orleansContext.Add(nameof RepositoryId, repositoryId)
        orleansContext.Add(nameof StoragePoolId, storagePoolId)
        orleansContext.Add(Constants.ActorNameProperty, ActorName.RepositoryContentCounter)
        memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
        grain

    /// Builds manifest contribution workflow actor data needed by the Reference actor.
    let private createManifestContributionWorkflowActor repositoryId storagePoolId manifestAddress correlationId =
        let grain =
            orleansClient.CreateActorProxyWithCorrelationId<IManifestContributionWorkflowActor>(
                manifestContributionWorkflowPrimaryKey repositoryId storagePoolId manifestAddress,
                correlationId
            )

        let orleansContext = Dictionary<string, obj>()
        orleansContext.Add(nameof RepositoryId, repositoryId)
        orleansContext.Add(nameof StoragePoolId, storagePoolId)
        orleansContext.Add(Constants.ActorNameProperty, ActorName.ManifestContributionWorkflow)
        memoryCache.CreateOrleansContextEntry(grain.GetGrainId(), orleansContext)
        grain

    /// Applies manifest contribution boundary changes to the Reference actor state.
    let private applyManifestContributionBoundary (plans: ManifestSaveContributionPlan list) (metadata: EventMetadata) =
        task {
            let planArray = plans |> List.toArray
            let mutable planIndex = 0
            let mutable error: GraceError option = None

            while planIndex < planArray.Length && error.IsNone do
                let plan = planArray[planIndex]

                let counterActor =
                    createRepositoryContentCounterActor plan.RepositoryId plan.Manifest.StoragePoolId plan.Manifest.ManifestAddress metadata.CorrelationId

                match! counterActor.Handle plan.CounterCommand metadata with
                | Error graceError -> error <- Some graceError
                | Ok counterReturnValue ->
                    let! counterEvents = counterActor.GetEvents metadata.CorrelationId

                    match tryCreateManifestContributionStartForCounterDecision plan counterReturnValue.ReturnValue counterEvents with
                    | None -> ()
                    | Some startCommand ->
                        let workflowActor =
                            createManifestContributionWorkflowActor
                                plan.RepositoryId
                                plan.Manifest.StoragePoolId
                                plan.Manifest.ManifestAddress
                                metadata.CorrelationId

                        match startCommand with
                        | ManifestContributionWorkflowCommand.Start (operationId,
                                                                     repositoryId,
                                                                     storagePoolId,
                                                                     manifestAddress,
                                                                     direction,
                                                                     ranges,
                                                                     counterRevision) ->
                            match! workflowActor.Start operationId repositoryId storagePoolId manifestAddress direction ranges counterRevision metadata with
                            | Ok _ -> ()
                            | Error graceError -> error <- Some graceError
                        | _ -> error <- Some(GraceError.Create "Manifest contribution save boundary expected a workflow start command." metadata.CorrelationId)

                planIndex <- planIndex + 1

            match error with
            | Some graceError -> return Error graceError
            | None -> return Ok()
        }

    /// Implements the Orleans grain for reference actor.
    type ReferenceActor([<PersistentState(StateName.Reference, Constants.GraceActorStorage)>] state: IPersistentState<List<ReferenceEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Reference

        let log = loggerFactory.CreateLogger("Reference.Actor")

        let mutable currentCommand = String.Empty

        let mutable referenceDto = ReferenceDto.Default

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            task {
                let activateStartTime = getCurrentInstant ()

                logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

                referenceDto <-
                    state.State
                    |> Seq.fold (fun referenceDto event -> ReferenceDto.UpdateDto event referenceDto) referenceDto
            }
            :> Task

        interface IGraceReminderWithGuidKey with
            /// Schedules a Grace reminder.
            member this.ScheduleReminderAsync reminderType delay state correlationId =
                task {
                    let reminderDto =
                        ReminderDto.Create
                            actorName
                            $"{this.IdentityString}"
                            referenceDto.OwnerId
                            referenceDto.OrganizationId
                            referenceDto.RepositoryId
                            reminderType
                            (getFutureInstant delay)
                            state
                            correlationId

                    do! createReminder reminderDto
                }
                :> Task

            /// Receives a Grace reminder.
            member this.ReceiveReminderAsync(reminder: ReminderDto) : Task<Result<unit, GraceError>> =
                task {
                    this.correlationId <- reminder.CorrelationId

                    match reminder.ReminderType, reminder.State with
                    | ReminderTypes.PhysicalDeletion, ReminderState.ReferencePhysicalDeletion physicalDeletionReminderState ->

                        this.correlationId <- physicalDeletionReminderState.CorrelationId

                        let referenceId =
                            if referenceDto.ReferenceId = ReferenceId.Empty then
                                this.GetPrimaryKey()
                            else
                                referenceDto.ReferenceId

                        do!
                            completeReferencePhysicalDeletion
                                (fun () ->
                                    // Mark the branch as needing to update its latest references.
                                    let branchActorProxy =
                                        Branch.CreateActorProxy
                                            physicalDeletionReminderState.BranchId
                                            physicalDeletionReminderState.RepositoryId
                                            this.correlationId

                                    branchActorProxy.MarkForRecompute physicalDeletionReminderState.CorrelationId)
                                (fun () -> state.ClearStateAsync())

                        log.LogInformation(
                            "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Deleted physical state for reference; RepositoryId: {RepositoryId}; BranchId: {BranchId}; ReferenceId: {ReferenceId}; DirectoryVersionId: {DirectoryVersionId}; deleteReason: {deleteReason}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            physicalDeletionReminderState.CorrelationId,
                            physicalDeletionReminderState.RepositoryId,
                            physicalDeletionReminderState.BranchId,
                            referenceId,
                            physicalDeletionReminderState.DirectoryVersionId,
                            physicalDeletionReminderState.DeleteReason
                        )

                        this.DeactivateOnIdle()
                        return Ok()
                    | reminderType, state ->
                        return
                            Error(
                                (GraceError.Create
                                    $"{actorName} does not process reminder type {getDiscriminatedUnionCaseName reminderType} with state {getDiscriminatedUnionCaseName state}."
                                    this.correlationId)
                                    .enhance ("IsRetryable", "false")
                            )
                }

        /// Applies one persisted Reference event to this activation's in-memory state.
        member private this.ApplyEvent(referenceEvent: ReferenceEvent) =
            task {
                let correlationId = referenceEvent.Metadata.CorrelationId

                try
                    /// Persists the event and refreshes this activation before any publication attempt.
                    let persistEvent () =
                        task {
                            state.State.Add(referenceEvent)
                            do! state.WriteStateAsync()

                            referenceDto <-
                                referenceDto
                                |> ReferenceDto.UpdateDto referenceEvent
                        }
                        :> Task

                    match referenceEvent.Event with
                    | ReferenceEventType.Created _ ->
                        do! persistReferenceCreatedThenPublish persistEvent (fun () -> publishReferenceCreatedGraceEvent referenceEvent)
                    | _ ->
                        do! persistEvent ()
                        do! publishGraceEvent (GraceEvent.ReferenceEvent referenceEvent) referenceEvent.Metadata

                    let graceReturnValue =
                        (GraceReturnValue.Create referenceDto correlationId)
                            .enhance(nameof RepositoryId, referenceDto.RepositoryId)
                            .enhance(nameof BranchId, referenceDto.BranchId)
                            .enhance(nameof ReferenceId, referenceDto.ReferenceId)
                            .enhance(nameof DirectoryVersionId, referenceDto.DirectoryId)
                            .enhance(nameof ReferenceType, getDiscriminatedUnionCaseName referenceDto.ReferenceType)
                            .enhance (nameof ReferenceEventType, getDiscriminatedUnionFullName referenceEvent.Event)

                    return Ok graceReturnValue
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Failed to apply event {eventType} for reference {referenceId} in repository {repositoryId} on branch {branchId} with directory version {directoryVersionId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        getDiscriminatedUnionCaseName referenceEvent.Event,
                        referenceDto.ReferenceId,
                        referenceDto.RepositoryId,
                        referenceDto.BranchId,
                        referenceDto.DirectoryId
                    )

                    let graceError =
                        (GraceError.CreateWithException ex (getErrorMessage ReferenceError.FailedWhileApplyingEvent) correlationId)
                            .enhance(nameof RepositoryId, referenceDto.RepositoryId)
                            .enhance(nameof BranchId, referenceDto.BranchId)
                            .enhance(nameof ReferenceId, referenceDto.ReferenceId)
                            .enhance(nameof DirectoryVersionId, referenceDto.DirectoryId)
                            .enhance(nameof ReferenceType, getDiscriminatedUnionCaseName referenceDto.ReferenceType)
                            .enhance (nameof ReferenceEventType, getDiscriminatedUnionFullName referenceEvent.Event)

                    return
                        match referenceEvent.Event with
                        | ReferenceEventType.Created _ -> Error(graceError.enhance ("IsRetryable", "true"))
                        | _ -> Error graceError
            }

        interface IHasRepositoryId with
            /// Returns the repository id recorded in this Reference actor state.
            member this.GetRepositoryId correlationId = referenceDto.RepositoryId |> returnTask

        interface IReferenceActor with
            /// Reports whether this Reference actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not
                <| referenceDto.ReferenceId.Equals(ReferenceDto.Default.ReferenceId)
                |> returnTask

            /// Returns the current Reference actor state snapshot.
            member this.Get correlationId =
                this.correlationId <- correlationId
                referenceDto |> returnTask

            /// Returns reference type data from the Reference actor state or related storage.
            member this.GetReferenceType correlationId =
                this.correlationId <- correlationId
                referenceDto.ReferenceType |> returnTask

            /// Converges the existing Save or Checkpoint automatic-expiry reminder through its stable Reminder actor.
            member this.EnsureAutomaticPhysicalDeletionReminderAsync correlationId =
                task {
                    this.correlationId <- correlationId

                    match referenceDto.ReferenceType with
                    | ReferenceType.Save
                    | ReferenceType.Checkpoint ->
                        let repositoryActor = Repository.CreateActorProxy referenceDto.OrganizationId referenceDto.RepositoryId correlationId

                        let! repositoryDto = repositoryActor.Get correlationId

                        let days, label =
                            match referenceDto.ReferenceType with
                            | ReferenceType.Save -> repositoryDto.SaveDays, "Save"
                            | _ -> repositoryDto.CheckpointDays, "Checkpoint"

                        let reminderState: PhysicalDeletionReminderState =
                            {
                                RepositoryId = referenceDto.RepositoryId
                                BranchId = referenceDto.BranchId
                                DirectoryVersionId = referenceDto.DirectoryId
                                Sha256Hash = referenceDto.Sha256Hash
                                Blake3Hash = referenceDto.Blake3Hash
                                DeleteReason = $"{label}: automatic deletion after {days} days"
                                CorrelationId = correlationId
                            }

                        let reminderId = automaticPhysicalDeletionReminderId referenceDto.RepositoryId referenceDto.ReferenceId

                        let requestedReminder =
                            ReminderDto.CreateWithId
                                reminderId
                                actorName
                                $"{this.IdentityString}"
                                referenceDto.OwnerId
                                referenceDto.OrganizationId
                                referenceDto.RepositoryId
                                ReminderTypes.PhysicalDeletion
                                (getFutureInstant (Duration.FromDays(float days)))
                                (ReminderState.ReferencePhysicalDeletion reminderState)
                                correlationId

                        let reminderActor = Reminder.CreateActorProxy reminderId correlationId
                        let! existingReminder = reminderActor.GetOrAdd requestedReminder correlationId

                        if not (automaticPhysicalDeletionReminderMatches requestedReminder existingReminder) then
                            invalidOp
                                $"Automatic physical-deletion reminder {reminderId} targets different stable Reference data for Reference {referenceDto.ReferenceId}."
                    | _ -> ()
                }
                :> Task

            /// Reports whether this Reference actor state is marked logically deleted.
            member this.IsDeleted correlationId =
                this.correlationId <- correlationId
                referenceDto.DeletedAt.IsSome |> returnTask

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                /// Checks whether command validation succeeded before emitting the domain event.
                let isValid (command: ReferenceCommand) (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            if createCommandMatchesReference referenceDto command then
                                return Ok command
                            else
                                return Error(GraceError.Create (getErrorMessage ReferenceError.DuplicateCorrelationId) metadata.CorrelationId)
                        else
                            match command with
                            | Create (referenceId,
                                      ownerId,
                                      organizationId,
                                      repositoryId,
                                      branchId,
                                      directoryId,
                                      sha256Hash,
                                      blake3Hash,
                                      referenceType,
                                      referenceText,
                                      links) ->
                                match referenceDto.UpdatedAt with
                                | Some _ when createCommandMatchesReference referenceDto command -> return Ok command
                                | Some _ -> return Error(GraceError.Create (getErrorMessage ReferenceError.ReferenceAlreadyExists) metadata.CorrelationId)
                                | None -> return Ok command
                            | _ ->
                                match referenceDto.UpdatedAt with
                                | Some _ -> return Ok command
                                | None -> return Error(GraceError.Create (getErrorMessage ReferenceError.ReferenceIdDoesNotExist) metadata.CorrelationId)
                    }

                /// Runs Reference command decisions, applies emitted events, and persists the result.
                let processCommand (command: ReferenceCommand) (metadata: EventMetadata) =
                    /// Coordinates existing reference return value logic for the Reference actor.
                    let existingReferenceReturnValue () =
                        (GraceReturnValue.Create referenceDto metadata.CorrelationId)
                            .enhance(nameof RepositoryId, referenceDto.RepositoryId)
                            .enhance(nameof BranchId, referenceDto.BranchId)
                            .enhance(nameof ReferenceId, referenceDto.ReferenceId)
                            .enhance(nameof DirectoryVersionId, referenceDto.DirectoryId)
                            .enhance(nameof ReferenceType, getDiscriminatedUnionCaseName referenceDto.ReferenceType)
                            .enhance (
                                nameof ReferenceEventType,
                                getDiscriminatedUnionFullName (
                                    Created(
                                        referenceDto.ReferenceId,
                                        referenceDto.OwnerId,
                                        referenceDto.OrganizationId,
                                        referenceDto.RepositoryId,
                                        referenceDto.BranchId,
                                        referenceDto.DirectoryId,
                                        referenceDto.Sha256Hash,
                                        referenceDto.Blake3Hash,
                                        referenceDto.ReferenceType,
                                        referenceDto.ReferenceText,
                                        referenceDto.Links
                                    )
                                )
                            )

                    /// Reconstructs and republishes the persisted Created event for an exact matching identity retry.
                    let republishSavedCreatedEvent () =
                        task {
                            match state.State
                                  |> Seq.tryFind (fun referenceEvent ->
                                      match referenceEvent.Event with
                                      | ReferenceEventType.Created _ -> true
                                      | _ -> false)
                                with
                            | None ->
                                return
                                    Error(
                                        (GraceError.Create (getErrorMessage ReferenceError.FailedWhileApplyingEvent) metadata.CorrelationId)
                                            .enhance ("IsRetryable", "true")
                                    )
                            | Some createdEvent ->
                                try
                                    do! publishReferenceCreatedGraceEvent createdEvent
                                    return Ok(existingReferenceReturnValue ())
                                with
                                | ex ->
                                    return
                                        Error(
                                            (GraceError.CreateWithException ex (getErrorMessage ReferenceError.FailedWhileApplyingEvent) metadata.CorrelationId)
                                                .enhance(nameof RepositoryId, referenceDto.RepositoryId)
                                                .enhance(nameof BranchId, referenceDto.BranchId)
                                                .enhance(nameof ReferenceId, referenceDto.ReferenceId)
                                                .enhance(nameof DirectoryVersionId, referenceDto.DirectoryId)
                                                .enhance(nameof ReferenceType, getDiscriminatedUnionCaseName referenceDto.ReferenceType)
                                                .enhance ("IsRetryable", "true")
                                        )
                        }

                    /// Validates root directory version hashes before the operation continues.
                    let validateRootDirectoryVersionHashes repositoryId directoryId sha256Hash blake3Hash =
                        task {
                            let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy directoryId repositoryId metadata.CorrelationId
                            let! directoryVersionDto = directoryVersionActorProxy.Get metadata.CorrelationId

                            return
                                validateReferenceRootDirectoryVersionHashes
                                    metadata.CorrelationId
                                    repositoryId
                                    directoryId
                                    sha256Hash
                                    blake3Hash
                                    directoryVersionDto.DirectoryVersion
                        }

                    task {
                        match command with
                        | Create _ when createCommandMatchesReference referenceDto command -> return! republishSavedCreatedEvent ()
                        | _ ->
                            let! (referenceEventTypeResult: Result<ReferenceEventType, GraceError>) =
                                task {
                                    match command with
                                    | Create (referenceId,
                                              ownerId,
                                              organizationId,
                                              repositoryId,
                                              branchId,
                                              directoryId,
                                              sha256Hash,
                                              blake3Hash,
                                              referenceType,
                                              referenceText,
                                              links) ->
                                        match! validateRootDirectoryVersionHashes repositoryId directoryId sha256Hash blake3Hash with
                                        | Ok () ->
                                            return
                                                Ok(
                                                    Created(
                                                        referenceId,
                                                        ownerId,
                                                        organizationId,
                                                        repositoryId,
                                                        branchId,
                                                        directoryId,
                                                        sha256Hash,
                                                        blake3Hash,
                                                        referenceType,
                                                        referenceText,
                                                        links
                                                    )
                                                )
                                        | Error graceError -> return Error graceError
                                    | AddLink link -> return Ok(LinkAdded link)
                                    | RemoveLink link -> return Ok(LinkRemoved link)
                                    | DeleteLogical (force, deleteReason) ->
                                        /// Reads branch logical-delete retention days from event metadata when the caller supplied it.
                                        let tryGetLogicalDeleteDaysFromMetadata () =
                                            match metadata.Properties.TryGetValue("RepositoryLogicalDeleteDays") with
                                            | true, value ->
                                                let mutable parsed = 0.0f

                                                if Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, &parsed) then
                                                    Some parsed
                                                else
                                                    None
                                            | _ -> None

                                        let! logicalDeleteDays =
                                            match tryGetLogicalDeleteDaysFromMetadata () with
                                            | Some days -> Task.FromResult days
                                            | None ->
                                                task {
                                                    let repositoryActorProxy =
                                                        Repository.CreateActorProxy referenceDto.OrganizationId referenceDto.RepositoryId this.correlationId

                                                    let! repositoryDto = repositoryActorProxy.Get this.correlationId
                                                    return repositoryDto.LogicalDeleteDays
                                                }

                                        let reminderState: PhysicalDeletionReminderState =
                                            {
                                                RepositoryId = referenceDto.RepositoryId
                                                BranchId = referenceDto.BranchId
                                                DirectoryVersionId = referenceDto.DirectoryId
                                                Sha256Hash = referenceDto.Sha256Hash
                                                Blake3Hash = referenceDto.Blake3Hash
                                                DeleteReason = deleteReason
                                                CorrelationId = metadata.CorrelationId
                                            }

                                        do!
                                            (this :> IGraceReminderWithGuidKey)
                                                .ScheduleReminderAsync
                                                ReminderTypes.PhysicalDeletion
                                                (Duration.FromDays(float logicalDeleteDays))
                                                (ReminderState.ReferencePhysicalDeletion reminderState)
                                                metadata.CorrelationId

                                        return Ok(LogicalDeleted(force, deleteReason))
                                    | DeletePhysical ->
                                        do! completeReferencePhysicalDeletion (fun () -> Task.CompletedTask) (fun () -> state.ClearStateAsync())

                                        this.DeactivateOnIdle()
                                        return Ok PhysicalDeleted
                                    | Undelete -> return Ok Undeleted
                                }

                            match referenceEventTypeResult with
                            | Ok referenceEventType ->
                                let referenceEvent: ReferenceEvent = { Event = referenceEventType; Metadata = metadata }
                                return! this.ApplyEvent referenceEvent
                            | Error graceError -> return Error graceError
                    }

                task {
                    currentCommand <- $"{getDiscriminatedUnionCaseName command} {getDiscriminatedUnionCaseName referenceDto.ReferenceType}"
                    this.correlationId <- metadata.CorrelationId

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
