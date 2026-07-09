namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.CacheRegistration
open Grace.Types.Common
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Threading.Tasks

/// Contains the Orleans grain that owns server-side Grace Cache service registrations.
module CacheRegistrationActor =

    /// Implements the server-owned registration state for Grace Cache services.
    type CacheRegistrationActor
        (
            loggerFactory: ILoggerFactory,
            [<PersistentState(StateName.CacheRegistration, Constants.GraceActorStorage)>] state: IPersistentState<CacheRegistrationState>
        ) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("CacheRegistration.Actor")
        let mutable currentState = CacheRegistrationState.Empty

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
                    CacheRegistrationState.Empty

            Task.CompletedTask

        /// Persists the next actor state after a registration lifecycle transition.
        member private this.Save(nextState: CacheRegistrationState) =
            task {
                state.State <- nextState
                do! state.WriteStateAsync()
                currentState <- nextState
            }

        /// Wraps a registration lifecycle result in Grace's standard return envelope.
        member private this.ReturnResult(result: CacheRegistrationResult, correlationId: CorrelationId) = GraceReturnValue.Create result correlationId

        interface ICacheRegistrationActor with
            /// Registers or replaces one Cache service using server-approved identity, scopes, and capabilities.
            member this.Register(servicePrincipalId, request, approvedScopes, approvedCapabilities, now, correlationId) =
                task {
                    let nextState, result = Lifecycle.register currentState servicePrincipalId request approvedScopes approvedCapabilities now

                    do! this.Save nextState
                    return Ok(this.ReturnResult(result, correlationId))
                }

            /// Refreshes a current Cache registration while preserving its server-approved boundary.
            member this.Refresh(servicePrincipalId, now, correlationId) =
                task {
                    let nextState, result = Lifecycle.refresh currentState servicePrincipalId now

                    if not (obj.ReferenceEquals(nextState, currentState)) then
                        do! this.Save nextState

                    return Ok(this.ReturnResult(result, correlationId))
                }

            /// Removes expired Cache registrations and returns the current records that remain.
            member this.Expire(now, correlationId) =
                task {
                    let nextState = Lifecycle.expire currentState now
                    do! this.Save nextState
                    return Array.copy nextState.Registrations
                }

            /// Returns current registrations that match the supplied server-side selection query.
            member this.SelectEligible(query, now, correlationId) =
                Lifecycle.selectEligible currentState query now
                |> returnTask

            /// Returns all current Cache registrations.
            member this.Current(now, correlationId) =
                Lifecycle.selectEligible currentState CacheRegistrationSelectionQuery.Current now
                |> returnTask
