namespace Grace.Actors

open Azure.Storage
open Azure.Storage.Blobs
open Azure.Storage.Sas
open Dapr.Actors.Runtime
open Grace.Shared.Constants
open Grace.Shared.Utilities
open NodaTime
open System
open System.Collections.Generic
open System.Threading.Tasks

module Storage =

    /// Retrieves the actor's state from storage.
    let RetrieveState<'T> (stateManager: IActorStateManager) (actorStateName: string) =
        task {
            return!
                DefaultAsyncRetryPolicy.ExecuteAsync(fun () ->
                    task {
                        try
                            let! conditionalValue = stateManager.TryGetStateAsync<'T>(actorStateName)

                            return if conditionalValue.HasValue then Some conditionalValue.Value else None
                        with ex ->
                            logToConsole $"{createExceptionResponse ex}"
                            raise ex
                            return None
                    })
        }

    /// Saves the actor's state to storage.
    let SaveState<'T> (stateManager: IActorStateManager) actorStateName actorState =
        task { do! DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> stateManager.SetStateAsync<'T>(actorStateName, actorState)) } :> Task

    /// Deletes the actor's state from storage.
    let DeleteState (stateManager: IActorStateManager) actorStateName = task { return! stateManager.TryRemoveStateAsync(actorStateName) }
