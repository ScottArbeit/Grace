namespace Grace.Server

open Dapr
open Dapr.Actors.Client
open Giraffe
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Events
open Grace.Actors.Interfaces
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

module Notifications =

    type IGraceClientConnection =
        abstract member RegisterParentBranch: BranchId -> BranchId -> Task
        abstract member NotifyOnPromotion: BranchId * BranchName * ReferenceId -> Task
        abstract member NotifyOnCommit: BranchName * BranchName * BranchId * ReferenceId -> Task
        abstract member NotifyOnCheckpoint: BranchName * BranchName * BranchId * ReferenceId -> Task
        abstract member NotifyOnSave: BranchName * BranchName * BranchId * ReferenceId -> Task
        abstract member ServerToClientMessage: string -> Task

    type NotificationHub() =
        inherit Hub<IGraceClientConnection>()

        //override this.OnConnectedAsync() =
        //    task {
        //        ()
        //    }

        //override this.OnDisconnectedAsync(ex: Exception) =
        //    task {
        //        ()
        //    }

        member this.RegisterParentBranch(branchId: BranchId, parentBranchId: BranchId) =
            task {
                logToConsole $"In NotificationHub.RegisterParentBranch; branchId: {branchId}; parentBranchId: {parentBranchId}; ConnectionId: {this.Context.ConnectionId}."
                do! this.Groups.AddToGroupAsync(this.Context.ConnectionId, $"{parentBranchId}")
            }

        member this.NotifyOnPromotion((branchId: BranchId), (branchName: BranchName), (referenceId: ReferenceId)) =
            task {
                logToConsole $"In NotifyOnPromotion. branchName: {branchName}; referenceId: {referenceId}."
                do! this.Clients.Group($"{branchId}").NotifyOnPromotion(branchId, branchName, referenceId)
            } :> Task

        member this.NotifyOnSave((branchName: BranchName), (parentBranchName: BranchName), (parentBranchId: BranchId), (referenceId: ReferenceId)) =
            task {
                logToConsole $"In NotifyOnSave. branchName: {branchName}, parentBranchName: {parentBranchName}. parentBranchId: {parentBranchId}; referenceId: {referenceId}."
                do! this.Clients.Group($"{parentBranchId}").NotifyOnSave(branchName, parentBranchName, parentBranchId, referenceId)
                ()
            } :> Task

        member this.NotifyOnCheckpoint((branchName: BranchName), (parentBranchName: BranchName), (parentBranchId: BranchId), (referenceId: ReferenceId)) =
            task {
                logToConsole $"In NotifyOnCheckpoint. branchName: {branchName}, parentBranchName: {parentBranchName}. parentBranchId: {parentBranchId}; referenceId: {referenceId}."
                do! this.Clients.Group($"{parentBranchId}").NotifyOnCheckpoint(branchName, parentBranchName, parentBranchId, referenceId)
            } :> Task

        member this.NotifyOnCommit((branchName: BranchName), (parentBranchName: BranchName), (parentBranchId: BranchId), (referenceId: ReferenceId)) =
            task {
                logToConsole $"In NotifyOnCommit. branchName: {branchName}, parentBranchName: {parentBranchName}. parentBranchId: {parentBranchId}; referenceId: {referenceId}."
                do! this.Clients.Group($"{parentBranchId}").NotifyOnCommit(branchName, parentBranchName, parentBranchId, referenceId)
            } :> Task

        member this.ServerToClientMessage (message: string) =
            task {
                if not <| isNull(this.Clients) then
                    do! this.Clients.All.ServerToClientMessage(message)
                else
                    logToConsole $"No SignalR clients connected."
            } :> Task

    let Post: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                logToConsole $"In Notifications.Post."
                
                let hubContext = context.GetService<IHubContext<NotificationHub, IGraceClientConnection>>()
                let actorProxyFactory = context.GetService<IActorProxyFactory>()
                let actorProxyOptions = context.GetService<ActorProxyOptions>()

                let! body = context.ReadBodyFromRequestAsync()
                //logToConsole $"{body}"

                let diffTwoDirectoryVersions directoryId1 directoryId2 =
                    task {
                        let diffActorId = Diff.GetActorId directoryId1 directoryId2
                        let diffActorProxy = actorProxyFactory.CreateActorProxy<IDiffActor>(diffActorId, ActorName.Diff, actorProxyOptions)
                        let! x = diffActorProxy.Populate()
                        ()
                    }
            
                //let cloudEvent = deserialize<CloudEvent<string>> body
                let graceEvent = deserialize<GraceEvent> body
            
                match graceEvent with
                | BranchEvent branchEvent ->
                    logToConsole $"Received BranchEvent: {discriminatedUnionFullNameToString branchEvent.Event} {Environment.NewLine}{branchEvent.Metadata}"
                    match branchEvent.Event with
                    | Branch.Promoted (referenceId, directoryId, sha256Hash, referenceText) -> 
                        logToConsole $"Received Branch.Promoted; referenceId: {referenceId}, directoryId: {directoryId}, sha256Hash: {sha256Hash}, referenceText: {referenceText}"
                        let referenceActorId = Reference.GetActorId referenceId
                        let referenceActorProxy = actorProxyFactory.CreateActorProxy<IReferenceActor>(referenceActorId, ActorName.Reference, actorProxyOptions)
                        let! referenceDto = referenceActorProxy.Get()

                        let branchActorId = ActorId($"{referenceDto.BranchId}")
                        let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(branchActorId, ActorName.Branch, actorProxyOptions)
                        let! branchDto = branchActorProxy.Get()

                        logToConsole $"About to send signalR message to clients for group {branchDto.BranchId}."                                
                        do! hubContext.Clients.Group($"{branchDto.BranchId}").NotifyOnPromotion(branchDto.BranchId, branchDto.BranchName, referenceId)

                        // Create the diff between the new promotion and previous promotion.
                        let! latestTwoPromotions = getPromotions referenceDto.BranchId 2
                        if latestTwoPromotions.Count = 2 then
                            do! diffTwoDirectoryVersions latestTwoPromotions[0].DirectoryId latestTwoPromotions[1].DirectoryId
                    
                    | Branch.Committed (referenceId, directoryId, sha256Hash, referenceText) ->
                        let referenceActorId = Reference.GetActorId referenceId
                        let referenceActorProxy = actorProxyFactory.CreateActorProxy<IReferenceActor>(referenceActorId, ActorName.Reference, actorProxyOptions)
                        let! referenceDto = referenceActorProxy.Get()

                        let branchActorId = ActorId($"{referenceDto.BranchId}")
                        let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(branchActorId, ActorName.Branch, actorProxyOptions)
                        let! branchDto = branchActorProxy.Get()
                        let! parentBranchDto = branchActorProxy.GetParentBranch()

                        do! hubContext.Clients.Group($"{branchDto.ParentBranchId}").NotifyOnCommit(branchDto.BranchName, parentBranchDto.BranchName, parentBranchDto.ParentBranchId, referenceId)

                        // Create the diff between the new commit and the previous commit.
                        let! latestTwoCommits = getCommits referenceDto.BranchId 2
                        if latestTwoCommits.Count = 2 then
                            do! diffTwoDirectoryVersions latestTwoCommits[0].DirectoryId latestTwoCommits[1].DirectoryId

                        // Create the diff between the commit and the parent branch's most recent promotion.
                        match! getLatestPromotion branchDto.ParentBranchId with
                        | Some latestPromotion -> do! diffTwoDirectoryVersions referenceDto.DirectoryId latestPromotion.DirectoryId
                        | None -> ()
                        
                    | Branch.Checkpointed (referenceId, directoryId, sha256Hash, referenceText) ->
                        let referenceActorId = Reference.GetActorId referenceId
                        let referenceActorProxy = actorProxyFactory.CreateActorProxy<IReferenceActor>(referenceActorId, ActorName.Reference, actorProxyOptions)
                        let! referenceDto = referenceActorProxy.Get()

                        let branchActorId = ActorId($"{referenceDto.BranchId}")
                        let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(branchActorId, ActorName.Branch, actorProxyOptions)
                        let! branchDto = branchActorProxy.Get()
                        let! parentBranchDto = branchActorProxy.GetParentBranch()

                        do! hubContext.Clients.Group($"{branchDto.ParentBranchId}").NotifyOnCheckpoint(branchDto.BranchName, parentBranchDto.BranchName, parentBranchDto.ParentBranchId, referenceId)

                        // Create the diff between the two most recent checkpoints.
                        let! checkpoints = getCheckpoints branchDto.BranchId 2
                        if checkpoints.Count = 2 then
                            do! diffTwoDirectoryVersions checkpoints[0].DirectoryId checkpoints[1].DirectoryId

                        // Create a diff between the checkpoint and the most recent commit.
                        match! getLatestCommit branchDto.BranchId with
                        | Some latestCommit -> do! diffTwoDirectoryVersions referenceDto.DirectoryId latestCommit.DirectoryId
                        | None -> ()

                    | Branch.Saved (referenceId, directoryId, sha256Hash, referenceText) ->
                        logToConsole $"In Notifications.Post : Branch.Saved."
                        let actorId = Reference.GetActorId referenceId
                        let referenceActorProxy = actorProxyFactory.CreateActorProxy<IReferenceActor>(actorId, ActorName.Reference, actorProxyOptions)
                        let! referenceDto = referenceActorProxy.Get()

                        let branchActorId = ActorId($"{referenceDto.BranchId}")
                        let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(branchActorId, ActorName.Branch, actorProxyOptions)
                        let! branchDto = branchActorProxy.Get()
                        let! parentBranchDto = branchActorProxy.GetParentBranch()
                        
                        logToConsole $"About to send Saved message to SignalR group {branchDto.ParentBranchId} for child branches of {parentBranchDto.BranchName}."
                        do! hubContext.Clients.Group($"{branchDto.ParentBranchId}").NotifyOnSave(branchDto.BranchName, parentBranchDto.BranchName, parentBranchDto.ParentBranchId, referenceId)
                        
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
                    | Branch.Tagged (referenceId, directoryId, sha256Hash, referenceText) -> ()
                    | _ -> ()
                | OrganizationEvent organizationEvent -> logToConsole $"Received OrganizationEvent: {discriminatedUnionFullNameToString organizationEvent.Event} {Environment.NewLine}{organizationEvent.Metadata}"
                | OwnerEvent ownerEvent -> logToConsole $"Received OwnerEvent: {discriminatedUnionFullNameToString ownerEvent.Event} {Environment.NewLine}{ownerEvent.Metadata}"
                | RepositoryEvent repositoryEvent -> logToConsole $"Received RepositoryEvent: {discriminatedUnionFullNameToString repositoryEvent.Event} {Environment.NewLine}{repositoryEvent.Metadata}"

                return! setStatusCode StatusCodes.Status204NoContent next context
            }
