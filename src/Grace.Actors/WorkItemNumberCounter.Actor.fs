namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Constants
open Grace.Types.Common
open Grace.Shared.Utilities
open Orleans
open Orleans.Runtime
open System
open System.Threading.Tasks

/// Groups Orleans actor helpers for work item number counter keys, proxies, state, or workflow transitions.
module WorkItemNumberCounter =

    /// Stores durable state for work item number counter state.
    [<CLIMutable>]
    type WorkItemNumberCounterState = { NextWorkItemNumber: WorkItemNumber }

    /// Implements the Orleans grain for work item number counter actor.
    type WorkItemNumberCounterActor
        (
            [<PersistentState(StateName.WorkItemNumberCounter, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<WorkItemNumberCounterState>
        ) =
        inherit Grain()

        static let actorName = ActorName.WorkItemNumberCounter

        let log = loggerFactory.CreateLogger("WorkItemNumberCounter.Actor")

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            if not state.RecordExists then state.State <- { NextWorkItemNumber = 1L }

            Task.CompletedTask

        interface IWorkItemNumberCounterActor with
            /// Allocates and persists the next repository-local work item number.
            member this.AllocateNext(correlationId: CorrelationId) =
                task {
                    this.correlationId <- correlationId

                    if not state.RecordExists
                       && state.State.NextWorkItemNumber <= 0L then
                        state.State <- { NextWorkItemNumber = 1L }

                    let nextWorkItemNumber =
                        if state.State.NextWorkItemNumber > 0L then
                            state.State.NextWorkItemNumber
                        else
                            1L

                    state.State <- { NextWorkItemNumber = nextWorkItemNumber + 1L }
                    do! state.WriteStateAsync()

                    return nextWorkItemNumber
                }
