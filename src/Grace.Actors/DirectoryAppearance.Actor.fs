namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module DirectoryAppearance =

    type DirectoryAppearanceDto() =
        member val public Appearances = SortedSet<Appearance>() with get, set
        member val public RepositoryId: RepositoryId = RepositoryId.Empty with get, set

    type DirectoryAppearanceActor
        (
            [<PersistentState(StateName.DirectoryAppearance, Constants.GraceActorStorage)>] state: IPersistentState<SortedSet<Appearance>>,
            log: ILogger<DirectoryAppearanceDto>
        ) =
        inherit Grain()

        let actorName = ActorName.DirectoryAppearance

        let directoryAppearanceDto = DirectoryAppearanceDto()

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            directoryAppearanceDto.Appearances <- state.State
            Task.CompletedTask

        interface IDirectoryAppearanceActor with

            member this.Add appearance correlationId =
                task {
                    let wasAdded = directoryAppearanceDto.Appearances.Add(appearance)

                    if wasAdded then do! state.WriteStateAsync()
                }
                :> Task

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

            member this.Contains appearance correlationId = directoryAppearanceDto.Appearances.Contains(appearance) |> returnTask

            member this.Appearances correlationId = directoryAppearanceDto.Appearances |> returnTask
