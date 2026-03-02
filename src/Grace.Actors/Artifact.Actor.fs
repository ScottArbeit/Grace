namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Artifact
open Grace.Types.Events
open Grace.Types.Types
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module Artifact =

    type ArtifactActor([<PersistentState(StateName.Artifact, Constants.GraceActorStorage)>] state: IPersistentState<List<ArtifactEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Artifact
        let log = loggerFactory.CreateLogger("Artifact.Actor")

        let mutable currentCommand = String.Empty
        let mutable artifact = ArtifactMetadata.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            artifact <-
                state.State
                |> Seq.fold (fun dto event -> ArtifactMetadata.UpdateDto event dto) artifact

            Task.CompletedTask

        member private this.ApplyEvent(artifactEvent: ArtifactEvent) =
            task {
                let correlationId = artifactEvent.Metadata.CorrelationId

                try
                    state.State.Add(artifactEvent)
                    do! state.WriteStateAsync()

                    artifact <-
                        artifact
                        |> ArtifactMetadata.UpdateDto artifactEvent

                    let graceEvent = GraceEvent.ArtifactEvent artifactEvent
                    do! publishGraceEvent graceEvent artifactEvent.Metadata

                    let graceReturnValue: GraceReturnValue<string> =
                        (GraceReturnValue.Create "Artifact command succeeded." correlationId)
                            .enhance(nameof RepositoryId, artifact.RepositoryId)
                            .enhance (nameof ArtifactId, artifact.ArtifactId)

                    return Ok graceReturnValue
                with
                | ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Failed to apply event for ArtifactId: {ArtifactId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        artifact.ArtifactId
                    )

                    return
                        Error(
                            (GraceError.CreateWithException ex "Failed while applying Artifact event." correlationId)
                                .enhance(nameof RepositoryId, artifact.RepositoryId)
                                .enhance (nameof ArtifactId, artifact.ArtifactId)
                        )
            }

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId = artifact.RepositoryId |> returnTask

        interface IArtifactActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not
                <| artifact.ArtifactId.Equals(ArtifactId.Empty)
                |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId

                if artifact.ArtifactId = ArtifactId.Empty then Option.None else Some artifact
                |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                state.State :> IReadOnlyList<ArtifactEvent>
                |> returnTask

            member this.Handle command metadata =
                let isValid (artifactCommand: ArtifactCommand) (eventMetadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = eventMetadata.CorrelationId) then
                            return Error(GraceError.Create "Duplicate correlation ID for Artifact command." eventMetadata.CorrelationId)
                        else
                            match artifactCommand with
                            | ArtifactCommand.Create _ when artifact.ArtifactId <> ArtifactId.Empty ->
                                return Error(GraceError.Create "Artifact already exists." eventMetadata.CorrelationId)
                            | _ -> return Ok artifactCommand
                    }

                let processCommand (artifactCommand: ArtifactCommand) (eventMetadata: EventMetadata) =
                    task {
                        let eventType =
                            match artifactCommand with
                            | ArtifactCommand.Create artifactDto -> ArtifactEventType.Created artifactDto

                        let artifactEvent: ArtifactEvent = { Event = eventType; Metadata = eventMetadata }
                        return! this.ApplyEvent artifactEvent
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId

                    match! isValid command metadata with
                    | Ok validCommand -> return! processCommand validCommand metadata
                    | Error validationError -> return Error validationError
                }
