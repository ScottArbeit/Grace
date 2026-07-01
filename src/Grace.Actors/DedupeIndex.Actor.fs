namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.ContentBlockMetadata
open Grace.Types.Common
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Threading.Tasks

/// Groups Orleans actor helpers for dedupe index actor keys, proxies, state, or workflow transitions.
module DedupeIndexActor =

    [<Literal>]
    let GrainKey = "dedupe-index:v1"

    /// Creates the typed Orleans proxy for the DedupeIndex actor key.
    let CreateActorProxy correlationId = Context.orleansClient.CreateActorProxyWithCorrelationId<IDedupeIndexActor>(GrainKey, correlationId)

    /// Implements the Orleans grain for dedupe index actor.
    type DedupeIndexActor
        (
            loggerFactory: ILoggerFactory,
            [<PersistentState(StateName.DedupeIndex, Constants.GraceActorStorage)>] state: IPersistentState<DedupeIndex.DedupeIndexState>
        ) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("DedupeIndex.Actor")
        let mutable currentState = DedupeIndex.DedupeIndexState.Empty
        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            currentState <-
                if
                    state.RecordExists
                    && not (isNull (box state.State))
                then
                    state.State
                else
                    DedupeIndex.DedupeIndexState.Empty

            Task.CompletedTask

        /// Writes the dedupe index state only when the registration changed persisted records.
        member private this.Save(nextState: DedupeIndex.DedupeIndexState) =
            task {
                state.State <- nextState
                do! state.WriteStateAsync()
                currentState <- nextState
            }

        interface IDedupeIndexActor with
            /// Adds finalized manifest block records to the dedupe index for future reuse discovery.
            member this.RegisterFinalizedManifest registration correlationId =
                task {
                    this.correlationId <- correlationId
                    /// Coordinates next state logic for the DedupeIndex actor.
                    let nextState, newRecords = DedupeIndex.registerFinalizedManifestInState currentState registration
                    do! this.Save nextState
                    return newRecords
                }

            /// Updates dedupe records after authoritative content-block metadata is loaded.
            member this.WriteAfterAuthoritativeMetadata metadata correlationId =
                task {
                    this.correlationId <- correlationId
                    /// Coordinates next state logic for the DedupeIndex actor.
                    let nextState, newRecords = DedupeIndex.writeAfterAuthoritativeMetadataInState currentState metadata
                    do! this.Save nextState
                    return newRecords
                }

            /// Returns the dedupe index records used for diagnostics and reuse planning.
            member this.Snapshot correlationId =
                this.correlationId <- correlationId

                if
                    isNull (box currentState)
                    || isNull currentState.Records
                then
                    Array.empty
                else
                    Array.copy currentState.Records
                |> returnTask

            /// Returns the full persisted dedupe index state for tests and diagnostics.
            member this.SnapshotState correlationId =
                this.correlationId <- correlationId

                if isNull (box currentState) then
                    DedupeIndex.DedupeIndexState.Empty
                else
                    {
                        Records =
                            if isNull currentState.Records then
                                Array.empty
                            else
                                Array.copy currentState.Records
                        FinalizedManifests =
                            if isNull currentState.FinalizedManifests then
                                Array.empty
                            else
                                Array.copy currentState.FinalizedManifests
                        MetadataRecords =
                            if isNull currentState.MetadataRecords then
                                Array.empty
                            else
                                Array.copy currentState.MetadataRecords
                    }
                |> returnTask

            /// Looks up finalized metadata for a repository-scoped content block address.
            member this.TryGetFinalizedScopedContentBlockMetadata
                (
                    storagePoolId,
                    repositoryId,
                    authorizedScope,
                    manifestAddress,
                    contentBlockAddress,
                    correlationId
                ) =
                this.correlationId <- correlationId

                DedupeIndex.tryFindFinalizedScopedContentBlockMetadata
                    storagePoolId
                    repositoryId
                    authorizedScope
                    manifestAddress
                    contentBlockAddress
                    currentState
                |> returnTask
