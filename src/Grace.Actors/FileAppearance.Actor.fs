namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module FileAppearance =

    let actorName = ActorName.FileAppearance
    //let log = loggerFactory.CreateLogger("FileAppearance.Actor")

    type FileAppearanceDto() =
        member val public Appearances = SortedSet<Appearance>() with get, set

    type FileAppearanceActor([<PersistentState(StateName.FileAppearance, Constants.GraceActorStorage)>] state: IPersistentState<SortedSet<Appearance>>, log: ILogger<FileAppearanceActor>) =
        inherit Grain()

        let mutable correlationId = String.Empty

        let dtoStateName = StateName.FileAppearance
        let mutable dto = FileAppearanceDto()

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            logActorActivation log this.IdentityString (getActorActivationMessage state.RecordExists)

            Task.CompletedTask

        interface IFileAppearanceActor with
            member this.Add appearance correlationId =
                task {
                    let wasAdded = dto.Appearances.Add(appearance)

                    if wasAdded then
                        do! state.WriteStateAsync()
                }
                :> Task

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

            member this.Contains appearance correlationId = Task.FromResult(dto.Appearances.Contains(appearance))

            member this.Appearances correlationId = Task.FromResult(dto.Appearances)
