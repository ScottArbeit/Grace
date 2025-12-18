namespace Grace.Server

open Azure.Core
open Azure.Identity
open Azure.Messaging.ServiceBus
open FSharp.Control
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Shared
open Grace.Shared.AzureEnvironment
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Events
open Grace.Types.Types
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open System
open System.Linq
open System.Text.Json
open System.Threading
open System.Threading.Tasks

module Notification =

    let log = loggerFactory.CreateLogger("Notification.Server")
    let private defaultAzureCredential = lazy (DefaultAzureCredential())

    type IGraceClientConnection =
        abstract member RegisterRepository: RepositoryId -> Task
        abstract member RegisterParentBranch: BranchId -> BranchId -> Task
        abstract member NotifyRepository: RepositoryId * ReferenceId -> Task
        abstract member NotifyOnPromotion: BranchId * BranchName * ReferenceId -> Task
        abstract member NotifyOnCommit: BranchName * BranchName * BranchId * ReferenceId -> Task
        abstract member NotifyOnCheckpoint: BranchName * BranchName * BranchId * ReferenceId -> Task
        abstract member NotifyOnSave: BranchName * BranchName * BranchId * ReferenceId -> Task
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

                do! this.Clients.Group($"{repositoryId}").NotifyRepository(repositoryId, referenceId)
            }
            :> Task

        member this.NotifyOnPromotion((branchId: BranchId), (branchName: BranchName), (referenceId: ReferenceId)) =
            task {
                log.LogInformation(
                    "{CurrentInstant}: Node: {HostName}; Notifying clients in Branch: '{BranchName}' ({BranchId}) of promotion ReferenceId: {ReferenceId}.",
                    getCurrentInstantExtended (),
                    getMachineName,
                    branchName,
                    branchId,
                    referenceId
                )

                do! this.Clients.Group($"{branchId}").NotifyOnPromotion(branchId, branchName, referenceId)
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

                do! this.Clients.Group($"{parentBranchId}").NotifyOnSave(branchName, parentBranchName, parentBranchId, referenceId)

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

                do! this.Clients.Group($"{parentBranchId}").NotifyOnCheckpoint(branchName, parentBranchName, parentBranchId, referenceId)
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

                do! this.Clients.Group($"{parentBranchId}").NotifyOnCommit(branchName, parentBranchName, parentBranchId, referenceId)
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

                    match referenceEvent.Event with
                    | Reference.Created(referenceId,
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

                            if not <| isNull hubContext then
                                do! hubContext.Clients.Group($"{branchId}").NotifyOnPromotion(branchId, branchDto.BranchName, referenceId)

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
                                    hubContext.Clients
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
                                    hubContext.Clients
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
                                    hubContext.Clients
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

                        do! hubContext.Clients.Group($"{repositoryId}").NotifyRepository(repositoryId, referenceId)
                    | _ -> ()
                | RepositoryEvent repositoryEvent ->
                    let correlationId = repositoryEvent.Metadata.CorrelationId

                    logToConsole
                        $"Received RepositoryEvent: {getDiscriminatedUnionFullName repositoryEvent.Event} {Environment.NewLine}{repositoryEvent.Metadata}"
                | PromotionGroupEvent promotionGroupEvent ->
                    let correlationId = promotionGroupEvent.Metadata.CorrelationId

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received PromotionGroupEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )

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
                    with ex ->
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

                        while not ready && not cancellationToken.IsCancellationRequested do
                            try
                                let serviceBusClient =
                                    if settings.UseManagedIdentity then
                                        let fullyQualifiedNamespace =
                                            if not (String.IsNullOrWhiteSpace settings.FullyQualifiedNamespace) then
                                                settings.FullyQualifiedNamespace
                                            else
                                                AzureEnvironment.tryGetServiceBusFullyQualifiedNamespace ()
                                                |> Option.defaultWith (fun () ->
                                                    invalidOp
                                                        "Azure Service Bus namespace must be configured when using a managed identity.")

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
                            with ex ->
                                subscriptionLog.LogWarning(ex, "Azure Service Bus not ready; pausing for five seconds to retry.")
                                do! Task.Delay(TimeSpan.FromSeconds(5.0), cancellationToken)
                }

            let stopAzureServiceBusProcessor cancellationToken =
                task {
                    match processor with
                    | Some proc ->
                        try
                            do! proc.StopProcessingAsync(cancellationToken)
                        with ex ->
                            subscriptionLog.LogWarning(ex, "Grace pub-sub processor stop failed; continuing with Dispose().")

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
