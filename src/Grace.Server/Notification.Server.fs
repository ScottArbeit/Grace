namespace Grace.Server

open Azure.Core
open Azure.Identity
open Azure.Messaging.ServiceBus
open FSharp.Control
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Server.DerivedComputation
open Grace.Shared
open Grace.Shared.AzureEnvironment
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Automation
open Grace.Types.Events
open Grace.Types.Queue
open Grace.Types.Reference
open Grace.Types.Types
open Grace.Types.Validation
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open System
open System.Linq
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

module Notification =

    let log = loggerFactory.CreateLogger("Notification.Server")
    let private defaultAzureCredential = lazy (DefaultAzureCredential())

    type IGraceClientConnection =
        abstract member RegisterRepository: RepositoryId -> Task
        abstract member RegisterParentBranch: BranchId -> BranchId -> Task
        abstract member NotifyRepository: RepositoryId * ReferenceId -> Task
        abstract member NotifyOnCommit: BranchName * BranchName * BranchId * ReferenceId -> Task
        abstract member NotifyOnCheckpoint: BranchName * BranchName * BranchId * ReferenceId -> Task
        abstract member NotifyOnSave: BranchName * BranchName * BranchId * ReferenceId -> Task
        abstract member NotifyAutomationEvent: AutomationEventEnvelope -> Task
        abstract member ServerToClientMessage: string -> Task

    type NotificationHub() =
        inherit Hub<IGraceClientConnection>()

        override this.OnConnectedAsync() =
            task {
                log.LogInformation(
                    "{CurrentInstant}: Node: {HostName}; ConnectionId: {ConnectionId} established.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    this.Context.ConnectionId
                )
            }

        member this.RegisterRepository(repositoryId: RepositoryId) =
            task {
                log.LogInformation(
                    "{CurrentInstant}: Node: {HostName}; ConnectionId: {ConnectionId} registering for RepositoryId: {RepositoryId}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    this.Context.ConnectionId,
                    repositoryId
                )

                do! this.Groups.AddToGroupAsync(this.Context.ConnectionId, $"{repositoryId}")
            }

        member this.RegisterParentBranch(branchId: BranchId, parentBranchId: BranchId) =
            task {
                log.LogInformation(
                    "{CurrentInstant}: Node: {HostName}; ConnectionId: {ConnectionId} registering for ParentBranchId: {ParentBranchId}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    this.Context.ConnectionId,
                    parentBranchId
                )

                do! this.Groups.AddToGroupAsync(this.Context.ConnectionId, $"{parentBranchId}")
            }

        member this.NotifyRepository((repositoryId: RepositoryId), (referenceId: ReferenceId)) =
            task {
                log.LogInformation(
                    "{CurrentInstant}: Node: {HostName}; Notifying clients in RepositoryId group: {RepositoryId} of ReferenceId: {ReferenceId}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    repositoryId,
                    referenceId
                )

                do!
                    this
                        .Clients
                        .Group($"{repositoryId}")
                        .NotifyRepository(repositoryId, referenceId)
            }
            :> Task

        member this.NotifyOnSave((branchName: BranchName), (parentBranchName: BranchName), (parentBranchId: BranchId), (referenceId: ReferenceId)) =
            task {
                log.LogInformation(
                    "{CurrentInstant}: Node: {HostName}; Notifying clients with ParentBranch '{ParentBranchName}' ({ParentBranchId}) of save ReferenceId: {ReferenceId} in branch '{BranchName}'.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    parentBranchName,
                    parentBranchId,
                    referenceId,
                    branchName
                )

                do!
                    this
                        .Clients
                        .Group($"{parentBranchId}")
                        .NotifyOnSave(branchName, parentBranchName, parentBranchId, referenceId)

                ()
            }
            :> Task

        member this.NotifyOnCheckpoint((branchName: BranchName), (parentBranchName: BranchName), (parentBranchId: BranchId), (referenceId: ReferenceId)) =
            task {
                log.LogInformation(
                    "{CurrentInstant}: Node: {HostName}; Notifying clients with ParentBranch '{ParentBranchName}' ({ParentBranchId}) of checkpoint ReferenceId: {ReferenceId} in branch '{branchName}'.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    parentBranchName,
                    parentBranchId,
                    referenceId,
                    branchName
                )

                do!
                    this
                        .Clients
                        .Group($"{parentBranchId}")
                        .NotifyOnCheckpoint(branchName, parentBranchName, parentBranchId, referenceId)
            }
            :> Task

        member this.NotifyOnCommit((branchName: BranchName), (parentBranchName: BranchName), (parentBranchId: BranchId), (referenceId: ReferenceId)) =
            task {
                log.LogInformation(
                    "{CurrentInstant}: Node: {HostName}; Notifying clients with ParentBranch '{ParentBranchName}' ({ParentBranchId}) of commit ReferenceId: {ReferenceId} in branch '{branchName}'.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    parentBranchName,
                    parentBranchId,
                    referenceId,
                    branchName
                )

                do!
                    this
                        .Clients
                        .Group($"{parentBranchId}")
                        .NotifyOnCommit(branchName, parentBranchName, parentBranchId, referenceId)
            }
            :> Task

        member this.ServerToClientMessage(message: string) =
            task {
                if not <| isNull (this.Clients) then
                    do! this.Clients.All.ServerToClientMessage(message)
                else
                    logToConsole $"No SignalR clients connected."
            }
            :> Task

        member this.NotifyAutomationEvent(envelope: AutomationEventEnvelope) =
            task {
                if not <| isNull (this.Clients) then
                    let groupKey =
                        if envelope.RepositoryId = RepositoryId.Empty then
                            String.Empty
                        else
                            $"{envelope.RepositoryId}"

                    if String.IsNullOrWhiteSpace groupKey then
                        do! this.Clients.All.NotifyAutomationEvent(envelope)
                    else
                        do!
                            this
                                .Clients
                                .Group(groupKey)
                                .NotifyAutomationEvent(envelope)
                else
                    logToConsole $"No SignalR clients connected."
            }
            :> Task

    module Subscriber =
        /// Gets the ReferenceDto for the given ReferenceId.
        let getReferenceDto referenceId repositoryId correlationId =
            task {
                let referenceActorProxy = Reference.CreateActorProxy referenceId repositoryId correlationId

                return! referenceActorProxy.Get correlationId
            }

        /// Gets the BranchDto for the given BranchId.
        let getBranchDto branchId repositoryId correlationId =
            task {
                let branchActorProxy = Branch.CreateActorProxy branchId repositoryId correlationId

                return! branchActorProxy.Get correlationId
            }

        let diffTwoDirectoryVersions directoryVersionId1 directoryVersionId2 ownerId organizationId repositoryId correlationId =
            task {
                let diffActorProxy = Diff.CreateActorProxy directoryVersionId1 directoryVersionId2 ownerId organizationId repositoryId correlationId

                match! diffActorProxy.Compute correlationId with
                | Ok result -> return ()
                | Error graceError ->
                    log.LogError(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; In Notification.Server.diffTwoDirectoryVersions: Error computing diff between DirectoryVersionId {DirectoryVersionId1} and {DirectoryVersionId2}:\n{GraceError}",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        directoryVersionId1,
                        directoryVersionId2,
                        graceError
                    )

                    return ()
            }

        let tryGetRepositoryIdFromMetadata (metadata: EventMetadata) =
            match metadata.Properties.TryGetValue(nameof RepositoryId) with
            | true, value ->
                match Guid.TryParse(value) with
                | true, repositoryId -> Some repositoryId
                | _ -> None
            | _ -> None

        let triggerPromotionSetRecompute (repositoryId: RepositoryId) (promotionSetId: PromotionSetId) (reason: string) (correlationId: CorrelationId) =
            task {
                try
                    let promotionSetActorProxy = PromotionSet.CreateActorProxy promotionSetId repositoryId correlationId
                    let! exists = promotionSetActorProxy.Exists correlationId

                    if exists then
                        let recomputeCorrelationId = $"{correlationId}-recompute-{promotionSetId:N}"
                        let metadata = EventMetadata.New recomputeCorrelationId GraceSystemUser
                        metadata.Properties[ nameof RepositoryId ] <- $"{repositoryId}"
                        metadata.Properties[ "ActorId" ] <- $"{promotionSetId}"

                        match! promotionSetActorProxy.Handle (Grace.Types.PromotionSet.PromotionSetCommand.RecomputeStepsIfStale(Some reason)) metadata with
                        | Ok _ -> ()
                        | Error graceError ->
                            log.LogWarning(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed to recompute PromotionSetId {PromotionSetId}: {GraceError}",
                                getCurrentInstantExtended (),
                                getMachineName,
                                recomputeCorrelationId,
                                promotionSetId,
                                graceError
                            )
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Exception while triggering recompute for PromotionSetId {PromotionSetId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        promotionSetId
                    )
            }

        let triggerQueuedPromotionSetRecompute (repositoryId: RepositoryId) (targetBranchId: BranchId) (reason: string) (correlationId: CorrelationId) =
            task {
                try
                    let queueActorProxy = PromotionQueue.CreateActorProxy targetBranchId repositoryId correlationId
                    let! queueExists = queueActorProxy.Exists correlationId

                    if queueExists then
                        let! queue = queueActorProxy.Get correlationId

                        let queuedPromotionSetIds =
                            queue.PromotionSetIds
                            |> Seq.distinct
                            |> Seq.toArray

                        let mutable index = 0

                        while index < queuedPromotionSetIds.Length do
                            do! triggerPromotionSetRecompute repositoryId queuedPromotionSetIds[index] reason correlationId
                            index <- index + 1
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Exception while scheduling queue recompute for target branch {BranchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        targetBranchId
                    )
            }

        let private parseGuid (value: string) =
            let mutable parsed = Guid.Empty

            if String.IsNullOrWhiteSpace value |> not
               && Guid.TryParse(value, &parsed)
               && parsed <> Guid.Empty then
                Some parsed
            else
                None

        let internal matchesBranchGlob (branchName: BranchName) (branchNameGlob: string) =
            let normalizedPattern = if String.IsNullOrWhiteSpace branchNameGlob then "*" else branchNameGlob.Trim()

            let regexPattern =
                "^"
                + Regex
                    .Escape(normalizedPattern)
                    .Replace("\\*", ".*")
                + "$"

            Regex.IsMatch(
                $"{branchName}",
                regexPattern,
                RegexOptions.IgnoreCase
                ||| RegexOptions.CultureInvariant
            )

        let private tryGetPromotionSetIdFromMetadata (metadata: EventMetadata) =
            match metadata.Properties.TryGetValue("ActorId") with
            | true, actorId -> parseGuid actorId
            | _ -> None

        let private tryGetTerminalPromotionSetId (links: ReferenceLinkType seq) =
            links
            |> Seq.tryPick (fun link ->
                match link with
                | ReferenceLinkType.PromotionSetTerminal promotionSetId -> Some promotionSetId
                | _ -> None)

        let private emitAutomationEvent (hubContext: IHubContext<NotificationHub, IGraceClientConnection>) (envelope: AutomationEventEnvelope) =
            task {
                if not <| isNull hubContext then
                    let groupKey =
                        if envelope.RepositoryId = RepositoryId.Empty then
                            String.Empty
                        else
                            $"{envelope.RepositoryId}"

                    if String.IsNullOrWhiteSpace groupKey then
                        do! hubContext.Clients.All.NotifyAutomationEvent(envelope)
                    else
                        do!
                            hubContext
                                .Clients
                                .Group(groupKey)
                                .NotifyAutomationEvent(envelope)
            }

        let private getPromotionSetContext (repositoryId: RepositoryId) (promotionSetId: PromotionSetId) (correlationId: CorrelationId) =
            task {
                let promotionSetActorProxy = PromotionSet.CreateActorProxy promotionSetId repositoryId correlationId
                let! exists = promotionSetActorProxy.Exists correlationId

                if exists then
                    let! promotionSet = promotionSetActorProxy.Get correlationId
                    let! branch = getBranchDto promotionSet.TargetBranchId repositoryId correlationId
                    return Some(promotionSet, branch)
                else
                    return None
            }

        let private tryResolveAutomationBranchContext (graceEvent: GraceEvent) (correlationId: CorrelationId) =
            task {
                match graceEvent with
                | QueueEvent queueEvent ->
                    match queueEvent.Event with
                    | PromotionQueueEventType.PromotionSetEnqueued promotionSetId
                    | PromotionQueueEventType.PromotionSetDequeued promotionSetId ->
                        match tryGetRepositoryIdFromMetadata queueEvent.Metadata with
                        | Some repositoryId ->
                            let! promotionSetContext = getPromotionSetContext repositoryId promotionSetId correlationId

                            match promotionSetContext with
                            | Some (promotionSet, branch) ->
                                return
                                    Some(
                                        repositoryId,
                                        branch.BranchId,
                                        branch.BranchName,
                                        Some promotionSet.PromotionSetId,
                                        Some promotionSet.StepsComputationAttempt
                                    )
                            | None -> return None
                        | None -> return None
                    | _ -> return None
                | PromotionSetEvent promotionSetEvent ->
                    match tryGetPromotionSetIdFromMetadata promotionSetEvent.Metadata, tryGetRepositoryIdFromMetadata promotionSetEvent.Metadata with
                    | Some promotionSetId, Some repositoryId ->
                        let! promotionSetContext = getPromotionSetContext repositoryId promotionSetId correlationId

                        match promotionSetContext with
                        | Some (promotionSet, branch) ->
                            return
                                Some(
                                    repositoryId,
                                    branch.BranchId,
                                    branch.BranchName,
                                    Some promotionSet.PromotionSetId,
                                    Some promotionSet.StepsComputationAttempt
                                )
                        | None -> return None
                    | _ -> return None
                | ReferenceEvent referenceEvent ->
                    match referenceEvent.Event with
                    | ReferenceEventType.Created (_, _, _, repositoryId, branchId, _, _, referenceType, _, links) when referenceType = ReferenceType.Promotion ->
                        match tryGetTerminalPromotionSetId links with
                        | Some promotionSetId ->
                            let! branchDto = getBranchDto branchId repositoryId correlationId
                            let! promotionSetContext = getPromotionSetContext repositoryId promotionSetId correlationId

                            let stepsComputationAttempt =
                                promotionSetContext
                                |> Option.map (fun (promotionSet, _) -> promotionSet.StepsComputationAttempt)

                            return Some(repositoryId, branchId, branchDto.BranchName, Some promotionSetId, stepsComputationAttempt)
                        | None -> return None
                    | _ -> return None
                | _ -> return None
            }

        let private emitValidationRequestedEvents
            (hubContext: IHubContext<NotificationHub, IGraceClientConnection>)
            (sourceEnvelope: AutomationEventEnvelope)
            (graceEvent: GraceEvent)
            =
            task {
                let correlationId = sourceEnvelope.CorrelationId

                let! context = tryResolveAutomationBranchContext graceEvent correlationId

                match context with
                | None -> ()
                | Some (repositoryId, branchId, branchName, promotionSetId, stepsComputationAttempt) ->
                    let! validationSets = getValidationSets repositoryId 500 false correlationId

                    let matchingValidationSets =
                        validationSets
                        |> List.filter (fun validationSet ->
                            validationSet.Rules
                            |> List.exists (fun rule ->
                                rule.EventTypes
                                |> List.contains sourceEnvelope.EventType
                                && matchesBranchGlob branchName rule.BranchNameGlob))

                    let mutable index = 0

                    while index < matchingValidationSets.Length do
                        let validationSet = matchingValidationSets[index]
                        let mutable validationIndex = 0

                        while validationIndex < validationSet.Validations.Length do
                            let validation = validationSet.Validations[validationIndex]

                            match validation.ExecutionMode with
                            | ValidationExecutionMode.AsyncCallback ->
                                let payload =
                                    {|
                                        validationSetId = validationSet.ValidationSetId
                                        promotionSetId = promotionSetId
                                        stepsComputationAttempt = stepsComputationAttempt
                                        targetBranchId = branchId
                                        targetBranchName = branchName
                                        validationName = validation.Name
                                        validationVersion = validation.Version
                                        sourceEventType = sourceEnvelope.EventType
                                    |}

                                let validationRequestedEnvelope =
                                    AutomationEventEnvelope.Create
                                        AutomationEventType.ValidationRequested
                                        (getCurrentInstant ())
                                        correlationId
                                        validationSet.OwnerId
                                        validationSet.OrganizationId
                                        validationSet.RepositoryId
                                        $"{validationSet.ValidationSetId}"
                                        (serialize payload)

                                do! emitAutomationEvent hubContext validationRequestedEnvelope
                            | ValidationExecutionMode.Synchronous ->
                                let validationResultId = Guid.NewGuid()
                                let validationResultActorProxy = ValidationResult.CreateActorProxy validationResultId repositoryId correlationId
                                let metadata = EventMetadata.New correlationId GraceSystemUser
                                metadata.Properties[ nameof RepositoryId ] <- $"{repositoryId}"
                                metadata.Properties[ "ActorId" ] <- $"{validationResultId}"

                                let validationResultDto =
                                    { ValidationResultDto.Default with
                                        ValidationResultId = validationResultId
                                        OwnerId = validationSet.OwnerId
                                        OrganizationId = validationSet.OrganizationId
                                        RepositoryId = repositoryId
                                        ValidationSetId = Some validationSet.ValidationSetId
                                        PromotionSetId = promotionSetId
                                        StepsComputationAttempt = stepsComputationAttempt
                                        ValidationName = validation.Name
                                        ValidationVersion = validation.Version
                                        Output =
                                            {
                                                Status = ValidationStatus.Pass
                                                Summary = $"Synchronous validation '{validation.Name}' recorded automatically from {sourceEnvelope.EventType}."
                                                ArtifactIds = []
                                            }
                                        OnBehalfOf = [ UserId GraceSystemUser ]
                                        CreatedAt = getCurrentInstant ()
                                    }

                                match! validationResultActorProxy.Handle (ValidationResultCommand.Record validationResultDto) metadata with
                                | Ok _ -> ()
                                | Error graceError ->
                                    log.LogWarning(
                                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed recording synchronous validation result for ValidationSetId {ValidationSetId}. Error: {GraceError}",
                                        getCurrentInstantExtended (),
                                        getMachineName,
                                        correlationId,
                                        validationSet.ValidationSetId,
                                        graceError
                                    )

                            validationIndex <- validationIndex + 1

                        index <- index + 1
            }

        let hubContext = lazy (serviceProvider.GetService<IHubContext<NotificationHub, IGraceClientConnection>>())

        //let private getHubContextOld () =
        //    if isNull hubContext then
        //        if isNull serviceProvider then
        //            log.LogWarning("NotificationHub context requested before the service provider was initialized.")
        //        else
        //            hubContext <- serviceProvider.GetService<IHubContext<NotificationHub, IGraceClientConnection>>()

        //            if isNull hubContext then
        //                log.LogWarning("NotificationHub context could not be resolved from the service provider.")

        //    hubContext

        //let private getHubContext () =
        //    if isNull hubContext then
        //        hubContext <- serviceProvider.GetService<IHubContext<NotificationHub, IGraceClientConnection>>()

        //        if isNull hubContext then
        //            log.LogWarning("NotificationHub context could not be resolved from the service provider.")

        //    hubContext

        /// Main processing for asynchronous event notifications received from the pub-sub system.
        let handleEvent (graceEvent: GraceEvent) =
            task {
                let hubContext = hubContext.Value

                match graceEvent with
                | BranchEvent branchEvent ->
                    let correlationId = branchEvent.Metadata.CorrelationId

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received BranchEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )
                | DirectoryVersionEvent directoryVersionEvent ->
                    let correlationId = directoryVersionEvent.Metadata.CorrelationId

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received DirectoryVersionEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )
                | OrganizationEvent organizationEvent ->
                    let correlationId = organizationEvent.Metadata.CorrelationId

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received OrganizationEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )
                | OwnerEvent ownerEvent ->
                    let correlationId = ownerEvent.Metadata.CorrelationId

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received OwnerEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )
                | ReferenceEvent referenceEvent ->
                    let correlationId = referenceEvent.Metadata.CorrelationId
                    let repositoryId = Guid.Parse($"{referenceEvent.Metadata.Properties[nameof RepositoryId]}")

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received ReferenceEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )

                    do! DerivedComputation.handleReferenceEvent referenceEvent

                    match referenceEvent.Event with
                    | ReferenceEventType.Created (referenceId,
                                                  ownerId,
                                                  organizationId,
                                                  repositoryId,
                                                  branchId,
                                                  directoryId,
                                                  sha256Hash,
                                                  referenceType,
                                                  referenceText,
                                                  links) ->
                        match referenceType with
                        | ReferenceType.Promotion ->
                            let! branchDto = getBranchDto branchId repositoryId correlationId

                            let isTerminalPromotion =
                                links
                                |> Seq.exists (fun link ->
                                    match link with
                                    | ReferenceLinkType.PromotionSetTerminal _ -> true
                                    | _ -> false)

                            if isTerminalPromotion then
                                do!
                                    triggerQueuedPromotionSetRecompute
                                        repositoryId
                                        branchId
                                        $"Target branch advanced to terminal promotion {referenceId}."
                                        correlationId

                            // Create the diff between the new promotion and previous promotion.
                            let! latestTwoPromotions = getPromotions repositoryId branchId 2 correlationId

                            if latestTwoPromotions.Length = 2 then
                                do!
                                    diffTwoDirectoryVersions
                                        latestTwoPromotions[0].DirectoryId
                                        latestTwoPromotions[1].DirectoryId
                                        branchDto.OwnerId
                                        branchDto.OrganizationId
                                        branchDto.RepositoryId
                                        correlationId

                        | ReferenceType.Commit ->
                            let! branchDto = getBranchDto branchId repositoryId correlationId
                            let! parentBranchDto = getBranchDto branchDto.ParentBranchId repositoryId correlationId

                            if not <| isNull hubContext then
                                do!
                                    hubContext
                                        .Clients
                                        .Group($"{branchDto.ParentBranchId}")
                                        .NotifyOnCommit(branchDto.BranchName, parentBranchDto.BranchName, parentBranchDto.ParentBranchId, referenceId)
                            else
                                log.LogWarning("No SignalR hub context available; cannot notify clients of commit.")

                            let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy directoryId repositoryId correlationId
                            let! exists = directoryVersionActorProxy.Exists correlationId

                            if exists then
                                // Create the zip file for this directory version.
                                let! zipFileUri = directoryVersionActorProxy.GetZipFileUri correlationId

                                // Create the diff between the new commit and the previous commit.
                                let! latestTwoCommits = getCommits repositoryId branchId 2 correlationId

                                if latestTwoCommits.Length = 2 then
                                    do!
                                        diffTwoDirectoryVersions
                                            latestTwoCommits[0].DirectoryId
                                            latestTwoCommits[1].DirectoryId
                                            branchDto.OwnerId
                                            branchDto.OrganizationId
                                            branchDto.RepositoryId
                                            correlationId

                                // Create the diff between the commit and the parent branch's most recent promotion.
                                match! getLatestPromotion branchDto.RepositoryId branchDto.ParentBranchId with
                                | Some latestPromotion ->
                                    do!
                                        diffTwoDirectoryVersions
                                            directoryId
                                            latestPromotion.DirectoryId
                                            branchDto.OwnerId
                                            branchDto.OrganizationId
                                            branchDto.RepositoryId
                                            correlationId
                                | None -> ()
                        | ReferenceType.Checkpoint ->
                            let! branchDto = getBranchDto branchId repositoryId correlationId
                            let! parentBranchDto = getBranchDto branchDto.ParentBranchId repositoryId correlationId

                            if not <| isNull hubContext then
                                do!
                                    hubContext
                                        .Clients
                                        .Group($"{branchDto.ParentBranchId}")
                                        .NotifyOnCheckpoint(branchDto.BranchName, parentBranchDto.BranchName, parentBranchDto.ParentBranchId, referenceId)

                            // Create the diff between the two most recent checkpoints.
                            let! checkpoints = getCheckpoints repositoryId branchId 2 correlationId

                            if checkpoints.Length = 2 then
                                do!
                                    diffTwoDirectoryVersions
                                        checkpoints[0].DirectoryId
                                        checkpoints[1].DirectoryId
                                        branchDto.OwnerId
                                        branchDto.OrganizationId
                                        branchDto.RepositoryId
                                        correlationId

                            // Create a diff between the checkpoint and the most recent commit.
                            match! getLatestCommit repositoryId branchId with
                            | Some latestCommit ->
                                do!
                                    diffTwoDirectoryVersions
                                        directoryId
                                        latestCommit.DirectoryId
                                        branchDto.OwnerId
                                        branchDto.OrganizationId
                                        branchDto.RepositoryId
                                        correlationId
                            | None -> ()

                        | ReferenceType.Save ->
                            let! branchDto = getBranchDto branchId repositoryId correlationId
                            let! parentBranchDto = getBranchDto branchDto.ParentBranchId repositoryId correlationId

                            if not <| isNull hubContext then
                                do!
                                    hubContext
                                        .Clients
                                        .Group($"{branchDto.ParentBranchId}")
                                        .NotifyOnSave(branchDto.BranchName, parentBranchDto.BranchName, parentBranchDto.ParentBranchId, referenceId)
                            else
                                log.LogWarning("No SignalR hub context available; cannot notify clients of save.")

                            // Create the diff between the new save and the previous save.
                            let! latestTwoSaves = getSaves branchDto.RepositoryId branchId 2 correlationId

                            if latestTwoSaves.Length = 2 then
                                do!
                                    diffTwoDirectoryVersions
                                        latestTwoSaves[0].DirectoryId
                                        latestTwoSaves[1].DirectoryId
                                        branchDto.OwnerId
                                        branchDto.OrganizationId
                                        branchDto.RepositoryId
                                        correlationId

                            // Create the diff between the new save and the most recent commit.
                            let mutable latestCommit = Reference.ReferenceDto.Default

                            match! getLatestCommit branchDto.RepositoryId branchDto.BranchId with
                            | Some latest ->
                                latestCommit <- latest

                                do!
                                    diffTwoDirectoryVersions
                                        latestCommit.DirectoryId
                                        directoryId
                                        branchDto.OwnerId
                                        branchDto.OrganizationId
                                        branchDto.RepositoryId
                                        correlationId
                            | None -> ()

                            // Create the diff between the new save and the most recent checkpoint,
                            //   if the checkpoint is newer than the most recent commit.
                            match! getLatestCheckpoint branchDto.RepositoryId branchDto.BranchId with
                            | Some latestCheckpoint ->
                                if latestCheckpoint.CreatedAt > latestCommit.CreatedAt then
                                    do!
                                        diffTwoDirectoryVersions
                                            latestCheckpoint.DirectoryId
                                            directoryId
                                            branchDto.OwnerId
                                            branchDto.OrganizationId
                                            branchDto.RepositoryId
                                            correlationId
                            | None -> ()

                        | ReferenceType.Tag
                        | ReferenceType.Rebase
                        | ReferenceType.External -> ()

                        do!
                            hubContext
                                .Clients
                                .Group($"{repositoryId}")
                                .NotifyRepository(repositoryId, referenceId)
                    | _ -> ()
                | RepositoryEvent repositoryEvent ->
                    let correlationId = repositoryEvent.Metadata.CorrelationId

                    logToConsole
                        $"Received RepositoryEvent: {getDiscriminatedUnionFullName repositoryEvent.Event} {Environment.NewLine}{repositoryEvent.Metadata}"
                | PolicyEvent policyEvent ->
                    let correlationId = policyEvent.Metadata.CorrelationId

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received PolicyEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )

                    do! DerivedComputation.handlePolicyEvent policyEvent
                | WorkItemEvent workItemEvent ->
                    let correlationId = workItemEvent.Metadata.CorrelationId

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received WorkItemEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )
                | ReviewEvent reviewEvent ->
                    let correlationId = reviewEvent.Metadata.CorrelationId

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received ReviewEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )
                | QueueEvent queueEvent ->
                    let correlationId = queueEvent.Metadata.CorrelationId

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received QueueEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )

                    match queueEvent.Event with
                    | Grace.Types.Queue.PromotionQueueEventType.PromotionSetEnqueued promotionSetId ->
                        match tryGetRepositoryIdFromMetadata queueEvent.Metadata with
                        | Some repositoryId -> do! triggerPromotionSetRecompute repositoryId promotionSetId "PromotionSet enqueued." correlationId
                        | None -> ()
                    | _ -> ()
                | PromotionSetEvent promotionSetEvent ->
                    let correlationId = promotionSetEvent.Metadata.CorrelationId

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received PromotionSetEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )
                | ValidationSetEvent validationSetEvent ->
                    let correlationId = validationSetEvent.Metadata.CorrelationId

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received ValidationSetEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )
                | ValidationResultEvent validationResultEvent ->
                    let correlationId = validationResultEvent.Metadata.CorrelationId

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received ValidationResultEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )
                | ArtifactEvent artifactEvent ->
                    let correlationId = artifactEvent.Metadata.CorrelationId

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received ArtifactEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )

                match EventingPublisher.tryCreateEnvelope graceEvent with
                | Some envelope ->
                    do! emitAutomationEvent hubContext envelope

                    if envelope.EventType = AutomationEventType.PromotionSetStepsUpdated then
                        let recomputeSucceededEnvelope =
                            { envelope with
                                EventId = Guid.NewGuid()
                                EventType = AutomationEventType.PromotionSetRecomputeSucceeded
                                EventTime = getCurrentInstant ()
                            }

                        do! emitAutomationEvent hubContext recomputeSucceededEnvelope

                    do! emitValidationRequestedEvents hubContext envelope graceEvent
                | None -> ()

            //return! setStatusCode StatusCodes.Status204NoContent next context
            }

        type GraceEventSubscriptionService(loggerFactory: ILoggerFactory) =
            let subscriptionLog = loggerFactory.CreateLogger("Notification.Server.Subscription")
            let credential = lazy (DefaultAzureCredential())
            let mutable client: ServiceBusClient option = None
            let mutable processor: ServiceBusProcessor option = None

            let handleProcessorError (args: ProcessErrorEventArgs) =
                task {
                    //subscriptionLog.LogError(
                    //    args.Exception,
                    //    "Grace pub-sub processor fault. ErrorSource: {ErrorSource}; EntityPath: {EntityPath}.",
                    //    args.ErrorSource,
                    //    args.EntityPath
                    //)

                    subscriptionLog.LogWarning("Azure Service Bus not ready; pausing for five seconds to retry.")
                    do! Task.Delay(TimeSpan.FromSeconds(5.0))
                }
                :> Task

            let processGraceEvent (args: ProcessMessageEventArgs) =
                task {
                    try
                        use bodyStream = args.Message.Body.ToStream()
                        let graceEvent = JsonSerializer.Deserialize<GraceEvent>(bodyStream, options = Constants.JsonSerializerOptions)

                        do! handleEvent graceEvent
                        do! args.CompleteMessageAsync(args.Message, args.CancellationToken)
                    with
                    | ex ->
                        subscriptionLog.LogError(
                            ex,
                            "Failed to process GraceEvent message {MessageId} (CorrelationId: {CorrelationId}).",
                            args.Message.MessageId,
                            args.Message.CorrelationId
                        )

                        do! args.AbandonMessageAsync(args.Message, cancellationToken = args.CancellationToken)
                }

            let startAzureServiceBusProcessor (settings: AzureServiceBusPubSubSettings) (cancellationToken: CancellationToken) =
                task {
                    if processor.IsSome then
                        subscriptionLog.LogDebug("Grace pub-sub listener already running; skipping duplicate startup.")
                    else
                        let mutable ready = false

                        while not ready
                              && not cancellationToken.IsCancellationRequested do
                            try
                                let serviceBusClient =
                                    if settings.UseManagedIdentity then
                                        let fullyQualifiedNamespace =
                                            if not (String.IsNullOrWhiteSpace settings.FullyQualifiedNamespace) then
                                                settings.FullyQualifiedNamespace
                                            else
                                                AzureEnvironment.tryGetServiceBusFullyQualifiedNamespace ()
                                                |> Option.defaultWith (fun () ->
                                                    invalidOp "Azure Service Bus namespace must be configured when using a managed identity.")

                                        ServiceBusClient(fullyQualifiedNamespace, defaultAzureCredential.Value)
                                    else
                                        ServiceBusClient(settings.ConnectionString)

                                let serviceBusProcessorOptions =
                                    ServiceBusProcessorOptions(
                                        AutoCompleteMessages = false,
                                        MaxConcurrentCalls = 4,
                                        PrefetchCount = 16,
                                        Identifier = Environment.MachineName
                                    )

                                let serviceBusProcessor =
                                    serviceBusClient.CreateProcessor(settings.TopicName, settings.SubscriptionName, serviceBusProcessorOptions)

                                serviceBusProcessor.add_ProcessMessageAsync (Func<ProcessMessageEventArgs, Task>(fun args -> processGraceEvent args))
                                serviceBusProcessor.add_ProcessErrorAsync (Func<ProcessErrorEventArgs, Task>(fun args -> handleProcessorError args))

                                do! serviceBusProcessor.StartProcessingAsync(cancellationToken)

                                client <- Some serviceBusClient
                                processor <- Some serviceBusProcessor

                                subscriptionLog.LogInformation(
                                    "Started Grace pub-sub listener for topic {TopicName} / subscription {SubscriptionName}.",
                                    settings.TopicName,
                                    settings.SubscriptionName
                                )

                                ready <- true
                            with
                            | ex ->
                                subscriptionLog.LogWarning(ex, "Azure Service Bus not ready; pausing for five seconds to retry.")
                                do! Task.Delay(TimeSpan.FromSeconds(5.0), cancellationToken)
                }

            let stopAzureServiceBusProcessor cancellationToken =
                task {
                    match processor with
                    | Some proc ->
                        try
                            do! proc.StopProcessingAsync(cancellationToken)
                        with
                        | ex -> subscriptionLog.LogWarning(ex, "Grace pub-sub processor stop failed; continuing with Dispose().")

                        do! proc.DisposeAsync()
                        processor <- None
                    | None -> ()

                    match client with
                    | Some clientInstance ->
                        do! clientInstance.DisposeAsync()
                        client <- None
                    | None -> ()
                }

            let startSubscriber (cancellationToken: CancellationToken) : Task =
                match pubSubSettings with
                | { System = GracePubSubSystem.AzureServiceBus; AzureServiceBus = Some settings } -> startAzureServiceBusProcessor settings cancellationToken
                | { System = GracePubSubSystem.AzureServiceBus; AzureServiceBus = None } ->
                    subscriptionLog.LogWarning("Azure Service Bus pub-sub selected but settings were missing; skipping notification subscriber startup.")

                    Task.CompletedTask
                | { System = GracePubSubSystem.UnknownPubSubProvider } ->
                    subscriptionLog.LogInformation("Grace pub-sub disabled; notification subscriber will not start.")
                    Task.CompletedTask
                | otherSettings ->
                    subscriptionLog.LogWarning("Grace pub-sub system {System} is not supported for the notification subscriber.", otherSettings.System)

                    Task.CompletedTask

            interface IHostedService with
                member _.StartAsync(cancellationToken: CancellationToken) = startSubscriber cancellationToken

                member _.StopAsync(cancellationToken: CancellationToken) = task { do! stopAzureServiceBusProcessor cancellationToken }
