namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Types.Common
open Grace.Shared.Utilities
open Grace.Shared
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for file appearance keys, proxies, state, or workflow transitions.
module FileAppearance =

    let actorName = ActorName.FileAppearance
    //let log = loggerFactory.CreateLogger("FileAppearance.Actor")

    /// Wraps file appearance dto records exchanged by actor queries or projections.
    type FileAppearanceDto() =
        /// Stores the sorted appearance set tracked by this actor state.
        member val public Appearances = SortedSet<Appearance>() with get, set

    /// Implements the Orleans grain for file appearance actor.
    type FileAppearanceActor([<PersistentState(StateName.FileAppearance, Constants.GraceActorStorage)>] state: IPersistentState<SortedSet<Appearance>>) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("FileAppearance.Actor")

        let mutable correlationId = String.Empty

        let dtoStateName = StateName.FileAppearance
        let mutable dto = FileAppearanceDto()

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            Task.CompletedTask

        interface IFileAppearanceActor with
            /// Adds an appearance entry to this FileAppearance actor's sorted set.
            member this.Add appearance correlationId =
                task {
                    let wasAdded = dto.Appearances.Add(appearance)

                    if wasAdded then do! state.WriteStateAsync()
                }
                :> Task

            /// Removes an appearance entry from this FileAppearance actor's sorted set.
            member this.Remove appearance correlationId =
                task {
                    let wasRemoved = dto.Appearances.Remove(appearance)

                    if wasRemoved then
                        if dto.Appearances |> Seq.isEmpty then
                            // TODO: Delete the file from storage
                            do! state.ClearStateAsync()
                        else
                            do! state.WriteStateAsync()

                        ()
                }
                :> Task

            /// Checks whether this FileAppearance actor has recorded a matching appearance.
            member this.Contains appearance correlationId = Task.FromResult(dto.Appearances.Contains(appearance))

            /// Returns the sorted appearances tracked by this FileAppearance actor.
            member this.Appearances correlationId = Task.FromResult(dto.Appearances)
