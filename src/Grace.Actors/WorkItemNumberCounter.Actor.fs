namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Constants
open Grace.Types.Types
open Grace.Shared.Utilities
open Orleans
open Orleans.Runtime
open System
open System.Threading.Tasks

module WorkItemNumberCounter =

    [<CLIMutable>]
    type WorkItemNumberCounterState = { NextWorkItemNumber: WorkItemNumber }

    type WorkItemNumberCounterActor(
        [<PersistentState(StateName.WorkItemNumberCounter, Grace.Shared.Constants.GraceActorStorage)>]
        state: IPersistentState<WorkItemNumberCounterState>
    ) =
        inherit Grain()

        static let actorName = ActorName.WorkItemNumberCounter

        let log = loggerFactory.CreateLogger("WorkItemNumberCounter.Actor")

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            if not state.RecordExists then
                state.State <- { NextWorkItemNumber = 1L }

            Task.CompletedTask

        interface IWorkItemNumberCounterActor with
            member this.AllocateNext(correlationId: CorrelationId) =
                task {
                    this.correlationId <- correlationId

                    if not state.RecordExists && state.State.NextWorkItemNumber <= 0L then
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
