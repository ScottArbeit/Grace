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

/// Contains the Orleans grain that owns durable Grace Cache enrollment records.
module CacheRegistrationActor =

    /// Implements serialized Cache lifecycle transitions so proofs and mutations use one current durable record.
    type CacheRegistrationActor
        (
            loggerFactory: ILoggerFactory,
            [<PersistentState(StateName.CacheRegistration, Constants.GraceActorStorage)>] state: IPersistentState<CacheRegistrationState>
        ) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("CacheRegistration.Actor")
        let mutable currentState = CacheRegistrationState.Empty

        override this.OnActivateAsync(_ct) =
            let activatedAt = getCurrentInstant ()
            logActorActivation log this.IdentityString activatedAt (getActorActivationMessage state.RecordExists)

            currentState <-
                if
                    state.RecordExists
                    && not (isNull (box state.State))
                then
                    state.State
                else
                    CacheRegistrationState.Empty

            Task.CompletedTask

        /// Persists one accepted lifecycle transition and advances the in-memory authoritative record only after write success.
        member private this.Save(nextState: CacheRegistrationState) =
            task {
                state.State <- nextState
                do! state.WriteStateAsync()
                currentState <- nextState
            }

        /// Wraps Cache lifecycle results in Grace's standard correlation envelope.
        member private _.ReturnResult(result: CacheRegistrationResult, correlationId: CorrelationId) = GraceReturnValue.Create result correlationId

        /// Finds the authoritative durable record used for proof validation and administrator preflight.
        member private _.TryGet(cacheId: Guid) =
            currentState.Registrations
            |> Array.tryFind (fun registration -> registration.CacheId = cacheId)

        interface ICacheRegistrationActor with
            /// Enrolls a Cache only after its public key and administrator-approved boundary have reached this singleton actor.
            member this.Enroll(cacheId, request, enrolledBy, now, correlationId) =
                task {
                    let nextState, result = Lifecycle.enroll currentState cacheId request enrolledBy now

                    if result.Status = CacheRegistrationRefreshStatus.Enrolled then
                        do! this.Save nextState

                    return Ok(this.ReturnResult(result, correlationId))
                }

            /// Validates the current key proof and then refreshes permitted operational facts atomically in actor order.
            member this.Refresh(request, now, correlationId) =
                task {
                    match this.TryGet(request.CacheId) with
                    | None ->
                        let _, result = Lifecycle.refresh currentState request now
                        return Ok(this.ReturnResult(result, correlationId))
                    | Some registration ->
                        let digest = CacheRegistrationProof.refreshRequestDigest request

                        let isActiveProof =
                            CacheRegistrationProof.validate
                                now
                                registration.ActivePublicKey
                                request.CacheId
                                CacheRegistrationProof.RefreshOperation
                                digest
                                request.Proof

                        let isCandidateProof =
                            registration.CandidatePublicKey
                            |> Option.exists (fun candidate ->
                                CacheRegistrationProof.validate now candidate request.CacheId CacheRegistrationProof.RefreshOperation digest request.Proof)

                        if not isActiveProof && not isCandidateProof then
                            return
                                Error(
                                    GraceError.Create
                                        "Cache refresh proof is invalid, stale, or does not match the active or pending identity key."
                                        correlationId
                                )
                        else
                            let nextState, result =
                                if isCandidateProof then
                                    Lifecycle.promoteCandidate currentState request now
                                else
                                    Lifecycle.refresh currentState request now

                            if nextState <> currentState then do! this.Save nextState

                            return Ok(this.ReturnResult(result, correlationId))
                }

            /// Applies an already-authorized explicit repository assignment replacement without altering Cache identity facts.
            member this.UpdateAssignments(request, correlationId) =
                task {
                    let nextState, result = Lifecycle.updateAssignments currentState request.CacheId request.RepositoryScopes

                    if result.Status = CacheRegistrationRefreshStatus.Updated then
                        do! this.Save nextState

                    return Ok(this.ReturnResult(result, correlationId))
                }

            /// Marks a Cache revoked so new selection and grants fail closed immediately.
            member this.Revoke(request, now, correlationId) =
                task {
                    let nextState, result = Lifecycle.revoke currentState request.CacheId now

                    if result.Status = CacheRegistrationRefreshStatus.Revoked
                       && nextState <> currentState then
                        do! this.Save nextState

                    return Ok(this.ReturnResult(result, correlationId))
                }

            /// Accepts one canonical candidate only after a current active-key proof and preserves it durably until candidate-key promotion.
            member this.SubmitCandidate(request, now, correlationId) =
                task {
                    let requestDigest = CacheRegistrationProof.candidateRequestDigest request

                    match this.TryGet(request.CacheId) with
                    | None ->
                        let _, result = Lifecycle.submitCandidate currentState request now
                        return Ok(this.ReturnResult(result, correlationId))
                    | Some registration ->
                        match
                            CacheRegistrationProof.classify
                                now
                                registration.ActivePublicKey
                                request.CacheId
                                CacheRegistrationProof.SubmitCandidateOperation
                                requestDigest
                                request.Proof
                            with
                        | Error TimestampOutsideTolerance -> return Error(CacheRegistrationProof.candidateProofTimestampStaleError correlationId)
                        | Error _ ->
                            return
                                Error(GraceError.Create "Cache candidate-submission proof is invalid or does not match the active identity key." correlationId)
                        | Ok () ->
                            if not (CacheRegistrationProof.isValidPublicKey request.CandidatePublicKey) then
                                return Error(GraceError.Create "Cache identity candidate requires a canonical P-256 public key." correlationId)
                            elif registration.ActivePublicKey = request.CandidatePublicKey then
                                return Error(GraceError.Create "Cache identity candidate must differ from the active identity key." correlationId)
                            elif registration.CandidatePublicKey
                                 |> Option.exists (fun candidate -> candidate <> request.CandidatePublicKey) then
                                return Error(GraceError.Create "Cache identity already has a different unresolved candidate." correlationId)
                            else
                                let nextState, result = Lifecycle.submitCandidate currentState request now

                                if nextState <> currentState then do! this.Save nextState

                                return Ok(this.ReturnResult(result, correlationId))
                }

            /// Returns the authoritative stored record without applying selection eligibility filters.
            member this.Get(cacheId, _correlationId) = this.TryGet(cacheId) |> returnTask

            /// Returns current registrations that match exact durable selection requirements.
            member _.SelectEligible(query, now, _correlationId) =
                Lifecycle.selectEligible currentState query now
                |> returnTask

            /// Returns all current Cache registrations without granting artifact access or generating a plan.
            member _.Current(now, _correlationId) =
                Lifecycle.selectEligible currentState CacheRegistrationSelectionQuery.Current now
                |> returnTask
