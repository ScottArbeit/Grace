namespace Grace.Server

open Dapr
open Dapr.Actors.Client
open Giraffe
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Events
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Dto
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Logging
open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Dapr.Actors
open Services
open Microsoft.AspNetCore.Http.Features

module Notifications =

    let actorProxyFactory = ApplicationContext.actorProxyFactory

    type IGraceClientConnection =
        abstract member RegisterParentBranch: BranchId -> BranchId -> Task
        abstract member NotifyOnPromotion: BranchId * BranchName * ReferenceId -> Task
        abstract member NotifyOnCommit: BranchName * BranchName * BranchId * ReferenceId -> Task
        abstract member NotifyOnCheckpoint: BranchName * BranchName * BranchId * ReferenceId -> Task
        abstract member NotifyOnSave: BranchName * BranchName * BranchId * ReferenceId -> Task
        abstract member ServerToClientMessage: string -> Task

    type NotificationHub() =
        inherit Hub<IGraceClientConnection>()

        override this.OnConnectedAsync() = task { logToConsole $"NotificationHub ConnectionId {this.Context.ConnectionId} established." }

        //override this.OnDisconnectedAsync(ex: Exception) =
        //    task {
        //        ()
        //    }

        member this.RegisterParentBranch(branchId: BranchId, parentBranchId: BranchId) =
            task {
                logToConsole
                    $"In NotificationHub.RegisterParentBranch; branchId: {branchId}; parentBranchId: {parentBranchId}; ConnectionId: {this.Context.ConnectionId}."

                do! this.Groups.AddToGroupAsync(this.Context.ConnectionId, $"{parentBranchId}")
            }

        member this.NotifyOnPromotion((branchId: BranchId), (branchName: BranchName), (referenceId: ReferenceId)) =
            task {
                logToConsole $"In NotifyOnPromotion. branchName: {branchName}; referenceId: {referenceId}."

                do!
                    this.Clients
                        .Group($"{branchId}")
                        .NotifyOnPromotion(branchId, branchName, referenceId)
            }
            :> Task

        member this.NotifyOnSave((branchName: BranchName), (parentBranchName: BranchName), (parentBranchId: BranchId), (referenceId: ReferenceId)) =
            task {
                logToConsole
                    $"In NotifyOnSave. branchName: {branchName}, parentBranchName: {parentBranchName}. parentBranchId: {parentBranchId}; referenceId: {referenceId}."

                do!
                    this.Clients
                        .Group($"{parentBranchId}")
                        .NotifyOnSave(branchName, parentBranchName, parentBranchId, referenceId)

                ()
            }
            :> Task

        member this.NotifyOnCheckpoint((branchName: BranchName), (parentBranchName: BranchName), (parentBranchId: BranchId), (referenceId: ReferenceId)) =
            task {
                logToConsole
                    $"In NotifyOnCheckpoint. branchName: {branchName}, parentBranchName: {parentBranchName}. parentBranchId: {parentBranchId}; referenceId: {referenceId}."

                do!
                    this.Clients
                        .Group($"{parentBranchId}")
                        .NotifyOnCheckpoint(branchName, parentBranchName, parentBranchId, referenceId)
            }
            :> Task

        member this.NotifyOnCommit((branchName: BranchName), (parentBranchName: BranchName), (parentBranchId: BranchId), (referenceId: ReferenceId)) =
            task {
                logToConsole
                    $"In NotifyOnCommit. branchName: {branchName}, parentBranchName: {parentBranchName}. parentBranchId: {parentBranchId}; referenceId: {referenceId}."

                do!
                    this.Clients
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

    /// Gets the ReferenceDto for the given ReferenceId.
    let getReferenceDto referenceId correlationId =
        task {
            let referenceActorProxy = Reference.CreateActorProxy referenceId correlationId

            return! referenceActorProxy.Get correlationId
        }

    /// Gets the BranchDto for the given BranchId.
    let getBranchDto branchId correlationId =
        task {
            let branchActorProxy = Branch.CreateActorProxy branchId correlationId

            return! branchActorProxy.Get correlationId
        }

    /// This is the path called by the Dapr `graceevents` Pub/Sub component when a message is received.
    [<Topic("graceevents", "graceeventstream")>]
    let ReceiveGraceEventStream: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let hubContext = context.GetService<IHubContext<NotificationHub, IGraceClientConnection>>()

                let! graceEvent = context.BindJsonAsync<GraceEvent>()
                //logToConsole $"{serialize graceEvent}"

                let diffTwoDirectoryVersions directoryVersionId1 directoryVersionId2 correlationId =
                    task {
                        let diffActorProxy = Diff.CreateActorProxy directoryVersionId1 directoryVersionId2 correlationId

                        let! x = diffActorProxy.Compute correlationId
                        ()
                    }

                match graceEvent with
                | BranchEvent branchEvent ->
                    let correlationId = branchEvent.Metadata.CorrelationId

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received BranchEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )

                    match branchEvent.Event with
                    | Branch.Promoted(referenceDto, directoryVersionId, sha256Hash, referenceText) ->
                        let! branchDto = getBranchDto referenceDto.BranchId correlationId

                        do!
                            hubContext.Clients
                                .Group($"{branchDto.BranchId}")
                                .NotifyOnPromotion(branchDto.BranchId, branchDto.BranchName, referenceDto.ReferenceId)

                        // Create the diff between the new promotion and previous promotion.
                        let! latestTwoPromotions = getPromotions referenceDto.BranchId 2 (getCorrelationId context)

                        if latestTwoPromotions.Count = 2 then
                            do! diffTwoDirectoryVersions latestTwoPromotions[0].DirectoryId latestTwoPromotions[1].DirectoryId correlationId

                    | Branch.Committed(referenceDto, directoryVersionId, sha256Hash, referenceText) ->
                        let! branchDto = getBranchDto referenceDto.BranchId correlationId
                        let! parentBranchDto = getBranchDto branchDto.ParentBranchId correlationId

                        do!
                            hubContext.Clients
                                .Group($"{branchDto.ParentBranchId}")
                                .NotifyOnCommit(branchDto.BranchName, parentBranchDto.BranchName, parentBranchDto.ParentBranchId, referenceDto.ReferenceId)

                        // Create the diff between the new commit and the previous commit.
                        let! latestTwoCommits = getCommits referenceDto.BranchId 2 (getCorrelationId context)

                        if latestTwoCommits.Count = 2 then
                            do! diffTwoDirectoryVersions latestTwoCommits[0].DirectoryId latestTwoCommits[1].DirectoryId correlationId

                        // Create the diff between the commit and the parent branch's most recent promotion.
                        match! getLatestPromotion branchDto.ParentBranchId with
                        | Some latestPromotion -> do! diffTwoDirectoryVersions referenceDto.DirectoryId latestPromotion.DirectoryId correlationId
                        | None -> ()
                    | Branch.Checkpointed(referenceDto, directoryVersionId, sha256Hash, referenceText) ->
                        let! branchDto = getBranchDto referenceDto.BranchId correlationId
                        let! parentBranchDto = getBranchDto branchDto.ParentBranchId correlationId

                        do!
                            hubContext.Clients
                                .Group($"{branchDto.ParentBranchId}")
                                .NotifyOnCheckpoint(branchDto.BranchName, parentBranchDto.BranchName, parentBranchDto.ParentBranchId, referenceDto.ReferenceId)

                        // Create the diff between the two most recent checkpoints.
                        let! checkpoints = getCheckpoints branchDto.BranchId 2 (getCorrelationId context)

                        if checkpoints.Count = 2 then
                            do! diffTwoDirectoryVersions checkpoints[0].DirectoryId checkpoints[1].DirectoryId correlationId

                        // Create a diff between the checkpoint and the most recent commit.
                        match! getLatestCommit branchDto.BranchId with
                        | Some latestCommit -> do! diffTwoDirectoryVersions referenceDto.DirectoryId latestCommit.DirectoryId correlationId
                        | None -> ()

                    | Branch.Saved(referenceDto, directoryVersionId, sha256Hash, referenceText) ->
                        let! branchDto = getBranchDto referenceDto.BranchId correlationId
                        let! parentBranchDto = getBranchDto branchDto.ParentBranchId correlationId

                        do!
                            hubContext.Clients
                                .Group($"{branchDto.ParentBranchId}")
                                .NotifyOnSave(branchDto.BranchName, parentBranchDto.BranchName, parentBranchDto.ParentBranchId, referenceDto.ReferenceId)

                        // Create the diff between the new save and the previous save.
                        let! latestTwoSaves = getSaves referenceDto.BranchId 2 (getCorrelationId context)

                        if latestTwoSaves.Count = 2 then
                            do! diffTwoDirectoryVersions latestTwoSaves[0].DirectoryId latestTwoSaves[1].DirectoryId correlationId

                        // Create the diff between the new save and the most recent commit.
                        let mutable latestCommit = Reference.ReferenceDto.Default

                        match! getLatestCommit branchDto.BranchId with
                        | Some latest ->
                            latestCommit <- latest
                            do! diffTwoDirectoryVersions latestCommit.DirectoryId referenceDto.DirectoryId correlationId
                        | None -> ()

                        // Create the diff between the new save and the most recent checkpoint,
                        //   if the checkpoint is newer than the most recent commit.
                        match! getLatestCheckpoint branchDto.BranchId with
                        | Some latestCheckpoint ->
                            if latestCheckpoint.CreatedAt > latestCommit.CreatedAt then
                                do! diffTwoDirectoryVersions latestCheckpoint.DirectoryId referenceDto.DirectoryId correlationId
                        | None -> ()
                    | Branch.Tagged(referenceId, directoryVersionId, sha256Hash, referenceText) -> ()
                    | _ -> ()
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

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Received ReferenceEvent notification.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId
                    )

                    match referenceEvent.Event with
                    | Reference.Created(referenceDto) ->
                        // If the reference is a commit, we're going to pre-compute the directory version contents .zip file.
                        if referenceDto.ReferenceType = ReferenceType.Commit then
                            let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy referenceDto.DirectoryId correlationId
                            let! zipFileUri = directoryVersionActorProxy.GetZipFileUri correlationId
                            ()
                    | _ -> ()
                | RepositoryEvent repositoryEvent ->
                    let correlationId = repositoryEvent.Metadata.CorrelationId

                    logToConsole
                        $"Received RepositoryEvent: {getDiscriminatedUnionFullName repositoryEvent.Event} {Environment.NewLine}{repositoryEvent.Metadata}"

                return! setStatusCode StatusCodes.Status204NoContent next context
            }
