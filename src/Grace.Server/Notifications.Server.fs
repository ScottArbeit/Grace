namespace Grace.Server

open Dapr
open Dapr.Actors.Client
open Giraffe
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Events
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Dto
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.SignalR
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
            let referenceActorId = Reference.GetActorId referenceId

            let referenceActorProxy = actorProxyFactory.CreateActorProxy<IReferenceActor>(referenceActorId, ActorName.Reference)

            return! referenceActorProxy.Get correlationId
        }

    /// Gets the BranchDto for the given BranchId.
    let getBranchDto branchId correlationId =
        task {
            let branchActorId = Branch.GetActorId branchId

            let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(branchActorId, ActorName.Branch)

            return! branchActorProxy.Get correlationId
        }

    [<Topic("graceevents", "graceeventstream")>]
    let Post: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                logToConsole $"In Notifications.Post."

                let hubContext = context.GetService<IHubContext<NotificationHub, IGraceClientConnection>>()

                let! graceEvent = context.BindJsonAsync<GraceEvent>()
                //logToConsole $"{serialize graceEvent}"


                let diffTwoDirectoryVersions directoryId1 directoryId2 =
                    task {
                        let diffActorId = Diff.GetActorId directoryId1 directoryId2

                        let diffActorProxy = actorProxyFactory.CreateActorProxy<IDiffActor>(diffActorId, ActorName.Diff)

                        let! x = diffActorProxy.Compute(getCorrelationId context)
                        ()
                    }

                match graceEvent with
                | BranchEvent branchEvent ->
                    let correlationId = branchEvent.Metadata.CorrelationId

                    logToConsole $"Received BranchEvent: {getDiscriminatedUnionFullName branchEvent.Event} {Environment.NewLine}{branchEvent.Metadata}"

                    match branchEvent.Event with
                    | Branch.Promoted(referenceDto, directoryId, sha256Hash, referenceText) ->
                        let! branchDto = getBranchDto referenceDto.BranchId correlationId

                        do!
                            hubContext.Clients
                                .Group($"{branchDto.BranchId}")
                                .NotifyOnPromotion(branchDto.BranchId, branchDto.BranchName, referenceDto.ReferenceId)

                        // Create the diff between the new promotion and previous promotion.
                        let! latestTwoPromotions = getPromotions referenceDto.BranchId 2

                        if latestTwoPromotions.Count = 2 then
                            do! diffTwoDirectoryVersions latestTwoPromotions[0].DirectoryId latestTwoPromotions[1].DirectoryId

                    | Branch.Committed(referenceDto, directoryId, sha256Hash, referenceText) ->
                        let! branchDto = getBranchDto referenceDto.BranchId correlationId
                        let! parentBranchDto = getBranchDto branchDto.ParentBranchId correlationId

                        do!
                            hubContext.Clients
                                .Group($"{branchDto.ParentBranchId}")
                                .NotifyOnCommit(branchDto.BranchName, parentBranchDto.BranchName, parentBranchDto.ParentBranchId, referenceDto.ReferenceId)

                        // Create the diff between the new commit and the previous commit.
                        let! latestTwoCommits = getCommits referenceDto.BranchId 2

                        if latestTwoCommits.Count = 2 then
                            do! diffTwoDirectoryVersions latestTwoCommits[0].DirectoryId latestTwoCommits[1].DirectoryId

                        // Create the diff between the commit and the parent branch's most recent promotion.
                        match! getLatestPromotion branchDto.ParentBranchId with
                        | Some latestPromotion -> do! diffTwoDirectoryVersions referenceDto.DirectoryId latestPromotion.DirectoryId
                        | None -> ()

                    | Branch.Checkpointed(referenceDto, directoryId, sha256Hash, referenceText) ->
                        let! branchDto = getBranchDto referenceDto.BranchId correlationId
                        let! parentBranchDto = getBranchDto branchDto.ParentBranchId correlationId

                        do!
                            hubContext.Clients
                                .Group($"{branchDto.ParentBranchId}")
                                .NotifyOnCheckpoint(branchDto.BranchName, parentBranchDto.BranchName, parentBranchDto.ParentBranchId, referenceDto.ReferenceId)

                        // Create the diff between the two most recent checkpoints.
                        let! checkpoints = getCheckpoints branchDto.BranchId 2

                        if checkpoints.Count = 2 then
                            do! diffTwoDirectoryVersions checkpoints[0].DirectoryId checkpoints[1].DirectoryId

                        // Create a diff between the checkpoint and the most recent commit.
                        match! getLatestCommit branchDto.BranchId with
                        | Some latestCommit -> do! diffTwoDirectoryVersions referenceDto.DirectoryId latestCommit.DirectoryId
                        | None -> ()

                    | Branch.Saved(referenceDto, directoryId, sha256Hash, referenceText) ->
                        let! branchDto = getBranchDto referenceDto.BranchId correlationId
                        let! parentBranchDto = getBranchDto branchDto.ParentBranchId correlationId

                        do!
                            hubContext.Clients
                                .Group($"{branchDto.ParentBranchId}")
                                .NotifyOnSave(branchDto.BranchName, parentBranchDto.BranchName, parentBranchDto.ParentBranchId, referenceDto.ReferenceId)

                        // Create the diff between the new save and the previous save.
                        let! latestTwoSaves = getSaves referenceDto.BranchId 2

                        if latestTwoSaves.Count = 2 then
                            do! diffTwoDirectoryVersions latestTwoSaves[0].DirectoryId latestTwoSaves[1].DirectoryId

                        // Create the diff between the new save and the most recent commit.
                        let mutable latestCommit = Reference.ReferenceDto.Default

                        match! getLatestCommit branchDto.BranchId with
                        | Some latest ->
                            latestCommit <- latest
                            do! diffTwoDirectoryVersions latestCommit.DirectoryId referenceDto.DirectoryId
                        | None -> ()

                        // Create the diff between the new save and the most recent checkpoint,
                        //   if the checkpoint is newer than the most recent commit.
                        match! getLatestCheckpoint branchDto.BranchId with
                        | Some latestCheckpoint ->
                            if latestCheckpoint.CreatedAt > latestCommit.CreatedAt then
                                do! diffTwoDirectoryVersions latestCheckpoint.DirectoryId referenceDto.DirectoryId
                        | None -> ()
                    | Branch.Tagged(referenceId, directoryId, sha256Hash, referenceText) -> ()
                    | _ -> ()
                | DirectoryVersionEvent directoryVersionEvent ->
                    logToConsole
                        $"Received DirectoryVersionEvent: {getDiscriminatedUnionFullName directoryVersionEvent.Event} {Environment.NewLine}{directoryVersionEvent.Metadata}"
                | OrganizationEvent organizationEvent ->
                    logToConsole
                        $"Received OrganizationEvent: {getDiscriminatedUnionFullName organizationEvent.Event} {Environment.NewLine}{organizationEvent.Metadata}"
                | OwnerEvent ownerEvent ->
                    logToConsole $"Received OwnerEvent: {getDiscriminatedUnionFullName ownerEvent.Event} {Environment.NewLine}{ownerEvent.Metadata}"
                | ReferenceEvent referenceEvent ->
                    logToConsole
                        $"Received ReferenceEvent: {getDiscriminatedUnionFullName referenceEvent.Event} {Environment.NewLine}{referenceEvent.Metadata}"
                | RepositoryEvent repositoryEvent ->
                    logToConsole
                        $"Received RepositoryEvent: {getDiscriminatedUnionFullName repositoryEvent.Event} {Environment.NewLine}{repositoryEvent.Metadata}"

                return! setStatusCode StatusCodes.Status204NoContent next context
            }
