namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Types.Common
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open Orleans
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for work item number keys, proxies, state, or workflow transitions.
module WorkItemNumber =

    /// Implements the Orleans grain for work item number actor.
    type WorkItemNumberActor() =
        inherit Grain()

        static let actorName = ActorName.WorkItemNumber

        let log = loggerFactory.CreateLogger("WorkItemNumber.Actor")

        let cachedWorkItemIds = Dictionary<WorkItemNumber, WorkItemId>()

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime "In-memory only"
            Task.CompletedTask

        interface IWorkItemNumberActor with
            /// Returns the work item id mapped to a repository-local work item number.
            member this.GetWorkItemId (workItemNumber: WorkItemNumber) correlationId =
                this.correlationId <- correlationId

                match cachedWorkItemIds.TryGetValue workItemNumber with
                | true, workItemId -> Some workItemId |> returnTask
                | false, _ -> None |> returnTask

            /// Stores the work item id mapped to a repository-local work item number.
            member this.SetWorkItemId (workItemNumber: WorkItemNumber) (workItemId: WorkItemId) correlationId =
                this.correlationId <- correlationId

                if workItemNumber > 0L && workItemId <> Guid.Empty then
                    cachedWorkItemIds[workItemNumber] <- workItemId

                Task.CompletedTask
