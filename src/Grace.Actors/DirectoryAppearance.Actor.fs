namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Types.Common
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for directory appearance keys, proxies, state, or workflow transitions.
module DirectoryAppearance =

    /// Wraps directory appearance dto records exchanged by actor queries or projections.
    type DirectoryAppearanceDto() =
        /// Stores the sorted appearance set tracked by this actor state.
        member val public Appearances = SortedSet<Appearance>() with get, set
        /// Stores the repository id that scopes this actor state.
        member val public RepositoryId: RepositoryId = RepositoryId.Empty with get, set

    /// Implements the Orleans grain for directory appearance actor.
    type DirectoryAppearanceActor
        (
            [<PersistentState(StateName.DirectoryAppearance, Constants.GraceActorStorage)>] state: IPersistentState<SortedSet<Appearance>>
        ) =
        inherit Grain()

        let actorName = ActorName.DirectoryAppearance

        let log = loggerFactory.CreateLogger("DirectoryAppearance.Actor")

        let directoryAppearanceDto = DirectoryAppearanceDto()

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            directoryAppearanceDto.Appearances <- state.State
            Task.CompletedTask

        interface IDirectoryAppearanceActor with

            /// Adds an appearance entry to this DirectoryAppearance actor's sorted set.
            member this.Add appearance correlationId =
                task {
                    let wasAdded = directoryAppearanceDto.Appearances.Add(appearance)

                    if wasAdded then do! state.WriteStateAsync()
                }
                :> Task

            /// Removes an appearance entry from this DirectoryAppearance actor's sorted set.
            member this.Remove appearance correlationId =
                task {
                    let wasRemoved = directoryAppearanceDto.Appearances.Remove(appearance)

                    if wasRemoved then
                        if directoryAppearanceDto.Appearances |> Seq.isEmpty then
                            do! state.ClearStateAsync()

                            let directoryVersionGuid = this.GetGrainId().GetGuidKey()

                            let directoryVersionActorProxy =
                                DirectoryVersion.CreateActorProxy directoryVersionGuid directoryAppearanceDto.RepositoryId correlationId

                            let! result = directoryVersionActorProxy.Delete(correlationId)

                            match result with
                            | Ok returnValue -> ()
                            | Error error -> ()

                            ()
                        else
                            do! state.WriteStateAsync()
                }
                :> Task

            /// Checks whether this DirectoryAppearance actor has recorded a matching appearance.
            member this.Contains appearance correlationId =
                directoryAppearanceDto.Appearances.Contains(appearance)
                |> returnTask

            /// Returns the sorted appearances tracked by this DirectoryAppearance actor.
            member this.Appearances correlationId = directoryAppearanceDto.Appearances |> returnTask
