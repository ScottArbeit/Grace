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
open Grace.Types.Common
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for artifact keys, proxies, state, or workflow transitions.
module Artifact =

    /// Implements the Orleans grain for artifact actor.
    type ArtifactActor([<PersistentState(StateName.Artifact, Constants.GraceActorStorage)>] eventState: IPersistentState<List<ArtifactEvent>>) =
        inherit Grain()

        static let actorName = ActorName.Artifact
        let log = loggerFactory.CreateLogger("Artifact.Actor")

        let mutable currentCommand = String.Empty
        let mutable artifact = ArtifactMetadata.Default

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage eventState.RecordExists)

            artifact <-
                eventState.State
                |> Seq.fold (fun dto event -> ArtifactMetadata.UpdateDto event dto) artifact

            Task.CompletedTask

        /// Applies one persisted Artifact event to this activation's in-memory state.
        member private this.ApplyEvent(artifactEvent: ArtifactEvent) =
            task {
                let correlationId = artifactEvent.Metadata.CorrelationId

                try
                    let updatedArtifact =
                        artifact
                        |> ArtifactMetadata.UpdateDto artifactEvent

                    eventState.State.Add(artifactEvent)
                    do! eventState.WriteStateAsync()

                    artifact <- updatedArtifact

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
            /// Returns the repository id recorded in this Artifact actor state.
            member this.GetRepositoryId correlationId = artifact.RepositoryId |> returnTask

        interface IArtifactActor with
            /// Reports whether this Artifact actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId

                not
                <| artifact.ArtifactId.Equals(ArtifactId.Empty)
                |> returnTask

            /// Returns the current Artifact actor state snapshot.
            member this.Get correlationId =
                this.correlationId <- correlationId

                if artifact.ArtifactId = ArtifactId.Empty then Option.None else Some artifact
                |> returnTask

            /// Returns the persisted Artifact event stream for replay or audit.
            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                eventState.State :> IReadOnlyList<ArtifactEvent>
                |> returnTask

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                /// Checks whether command validation succeeded before emitting the domain event.
                let isValid (artifactCommand: ArtifactCommand) (eventMetadata: EventMetadata) =
                    task {
                        if eventState.State.Exists(fun ev -> ev.Metadata.CorrelationId = eventMetadata.CorrelationId) then
                            return Error(GraceError.Create "Duplicate correlation ID for Artifact command." eventMetadata.CorrelationId)
                        else
                            match artifactCommand.Command with
                            | command when not (String.Equals(command, ArtifactCommandNames.Create, StringComparison.OrdinalIgnoreCase)) ->
                                return
                                    Error(
                                        (GraceError.Create "Unsupported Artifact command." eventMetadata.CorrelationId)
                                            .enhance ("Command", artifactCommand.Command)
                                    )
                            | command when
                                String.Equals(command, ArtifactCommandNames.Create, StringComparison.OrdinalIgnoreCase)
                                && artifact.ArtifactId <> ArtifactId.Empty
                                ->
                                return Error(GraceError.Create "Artifact already exists." eventMetadata.CorrelationId)
                            | _ -> return Ok artifactCommand
                    }

                /// Runs Artifact command decisions, applies emitted events, and persists the result.
                let processCommand (artifactCommand: ArtifactCommand) (eventMetadata: EventMetadata) =
                    task {
                        let artifact = artifactCommand.ToCreated()

                        let artifactEvent = ArtifactEvent.FromCreated(ArtifactEventNames.Created, artifact, eventMetadata)

                        return! this.ApplyEvent artifactEvent
                    }

                task {
                    currentCommand <- command.Command
                    this.correlationId <- metadata.CorrelationId

                    match! isValid command metadata with
                    | Ok validCommand -> return! processCommand validCommand metadata
                    | Error validationError -> return Error validationError
                }
