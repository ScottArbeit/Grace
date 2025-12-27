namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Shared.Constants
open Grace.Types.Types
open Orleans
open Orleans.Runtime
open System.Threading.Tasks

module ExternalIdentityIndex =

    type ExternalIdentityIndexActor([<PersistentState(StateName.ExternalIdentityIndex, Constants.GraceActorStorage)>] state: IPersistentState<UserId>) =
        inherit Grain()

        interface IExternalIdentityIndexActor with
            member this.TryGetUserId correlationId =
                if state.RecordExists && not <| System.String.IsNullOrWhiteSpace state.State then
                    Task.FromResult(Some state.State)
                else
                    Task.FromResult(None)

            member this.TrySetUserId userId correlationId =
                task {
                    if state.RecordExists && not <| System.String.IsNullOrWhiteSpace state.State then
                        if state.State = userId then return true else return false
                    else
                        state.State <- userId
                        do! state.WriteStateAsync()
                        return true
                }
