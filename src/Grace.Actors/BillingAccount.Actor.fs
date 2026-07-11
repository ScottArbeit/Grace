namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.BillingAccount
open Grace.Types.Common
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Implements constant-time BillingAccount storage admission decisions.
module BillingAccount =

    /// Returns the durable operation identity carried by a command.
    let operationId =
        function
        | Reserve (id, _, _, _, _, _)
        | Settle (id, _, _, _)
        | Promote (id, _, _, _)
        | Release (id, _, _, _) -> id

    /// Creates a domain rejection with the caller's correlation id.
    let private error (metadata: EventMetadata) message = Error(GraceError.Create message metadata.CorrelationId)

    /// Compares the complete durable command payload used by replay detection.
    let private payloadMatches left right = left = right

    /// Applies one validated command without scanning repository content or history.
    let decideCommand (events: seq<BillingAccountEvent>) account command (metadata: EventMetadata) =
        let id = operationId command

        match events
              |> Seq.tryFind (fun event -> event.OperationId = id)
            with
        | Some event when payloadMatches event.Command command -> Ok { Account = account; OperationId = id; Events = []; WasIdempotentReplay = true }
        | Some _ -> error metadata "BillingAccount operation id was reused with a different payload."
        | None when String.IsNullOrWhiteSpace id -> error metadata "BillingAccount operation id is required."
        | None ->
            let transition =
                match command with
                | Reserve (_, ownerId, repositoryId, contributorId, bytes, storageClass) ->
                    let externalLimit =
                        account.CapacityBytes
                        * int64 account.ExternalPercent
                        / 100L

                    if ownerId = OwnerId.Empty
                       || repositoryId = RepositoryId.Empty
                       || bytes <= 0L then
                        Error "BillingAccount reservation payload is invalid."
                    elif account.OwnerId <> OwnerId.Empty
                         && account.OwnerId <> ownerId then
                        Error "BillingAccount owner does not match the aggregate key."
                    elif bytes > account.CapacityBytes - account.UsedBytes then
                        Error "BillingAccount storage capacity is exhausted."
                    elif storageClass = ExternalContribution
                         && bytes > externalLimit - account.ExternalBytes then
                        Error "External contribution storage capacity is exhausted."
                    elif storageClass = ExternalContribution
                         && account.ActiveExternalBranches
                            >= account.ExternalBranchLimit then
                        Error "External contribution branch limit is exhausted."
                    else
                        let reservation =
                            {
                                OperationId = id
                                RepositoryId = repositoryId
                                ContributorId = contributorId
                                RetainingBranchId = None
                                Bytes = bytes
                                StorageClass = storageClass
                                Settled = false
                                Promoted = false
                                Released = false
                            }

                        Ok
                            { account with
                                OwnerId = ownerId
                                UsedBytes = account.UsedBytes + bytes
                                ExternalBytes =
                                    account.ExternalBytes
                                    + (if storageClass = ExternalContribution then bytes else 0L)
                                Reservations = account.Reservations.Add(id, reservation)
                            }
                | Settle (_, reservationId, repositoryId, branchId) ->
                    match account.Reservations.TryFind reservationId with
                    | None -> Error "BillingAccount reservation was not found."
                    | Some reservation when
                        reservation.RepositoryId <> repositoryId
                        || (reservation.RetainingBranchId.IsSome
                            && reservation.RetainingBranchId <> Some branchId)
                        ->
                        Error "BillingAccount settlement payload does not match the reservation."
                    | Some reservation when reservation.Released -> Error "BillingAccount cannot settle a released reservation."
                    | Some reservation when reservation.Settled -> Error "BillingAccount reservation is already settled under another operation id."
                    | Some reservation when
                        reservation.StorageClass = ExternalContribution
                        && not (account.ExternalBranchReservationCounts.ContainsKey branchId)
                        && account.ActiveExternalBranches
                           >= account.ExternalBranchLimit
                        ->
                        Error "External contribution branch limit is exhausted."
                    | Some reservation ->
                        let next = { reservation with Settled = true; RetainingBranchId = Some branchId }

                        let branchCounts =
                            if reservation.StorageClass = ExternalContribution then
                                let currentCount =
                                    account.ExternalBranchReservationCounts.TryFind branchId
                                    |> Option.defaultValue 0

                                account.ExternalBranchReservationCounts.Add(branchId, currentCount + 1)
                            else
                                account.ExternalBranchReservationCounts

                        Ok
                            { account with
                                ActiveExternalBranches =
                                    account.ActiveExternalBranches
                                    + (if
                                           reservation.StorageClass = ExternalContribution
                                           && not (account.ExternalBranchReservationCounts.ContainsKey branchId)
                                       then
                                           1
                                       else
                                           0)
                                ExternalBranchReservationCounts = branchCounts
                                Reservations = account.Reservations.Add(reservationId, next)
                            }
                | Promote (_, reservationId, repositoryId, branchId) ->
                    match account.Reservations.TryFind reservationId with
                    | Some reservation when
                        reservation.RepositoryId = repositoryId
                        && reservation.RetainingBranchId = Some branchId
                        && reservation.Settled
                        && not reservation.Released
                        && not reservation.Promoted
                        ->
                        let currentBranchCount =
                            account.ExternalBranchReservationCounts.TryFind branchId
                            |> Option.defaultValue 0

                        let branchCounts =
                            if reservation.StorageClass <> ExternalContribution
                               || currentBranchCount <= 0 then
                                account.ExternalBranchReservationCounts
                            elif currentBranchCount = 1 then
                                account.ExternalBranchReservationCounts.Remove branchId
                            else
                                account.ExternalBranchReservationCounts.Add(branchId, currentBranchCount - 1)

                        Ok
                            { account with
                                ExternalBytes =
                                    account.ExternalBytes
                                    - (if reservation.StorageClass = ExternalContribution then
                                           reservation.Bytes
                                       else
                                           0L)
                                ActiveExternalBranches =
                                    account.ActiveExternalBranches
                                    - (if reservation.StorageClass = ExternalContribution
                                          && currentBranchCount = 1 then
                                           1
                                       else
                                           0)
                                ExternalBranchReservationCounts = branchCounts
                                Reservations = account.Reservations.Add(reservationId, { reservation with Promoted = true })
                            }
                    | _ -> Error "BillingAccount promotion does not match active settled external usage."
                | Release (_, reservationId, repositoryId, branchId) ->
                    match account.Reservations.TryFind reservationId with
                    | Some reservation when
                        reservation.RepositoryId = repositoryId
                        && (reservation.RetainingBranchId.IsNone
                            || reservation.RetainingBranchId = Some branchId)
                        && not reservation.Released
                        ->
                        let retainedExternal =
                            reservation.StorageClass = ExternalContribution
                            && not reservation.Promoted

                        let currentBranchCount =
                            reservation.RetainingBranchId
                            |> Option.bind account.ExternalBranchReservationCounts.TryFind
                            |> Option.defaultValue 0

                        let branchCounts =
                            match reservation.RetainingBranchId with
                            | Some retainedBranchId when retainedExternal && currentBranchCount = 1 ->
                                account.ExternalBranchReservationCounts.Remove retainedBranchId
                            | Some retainedBranchId when retainedExternal && currentBranchCount > 1 ->
                                account.ExternalBranchReservationCounts.Add(retainedBranchId, currentBranchCount - 1)
                            | _ -> account.ExternalBranchReservationCounts

                        Ok
                            { account with
                                UsedBytes = account.UsedBytes - reservation.Bytes
                                ExternalBytes =
                                    account.ExternalBytes
                                    - (if retainedExternal then reservation.Bytes else 0L)
                                ActiveExternalBranches =
                                    account.ActiveExternalBranches
                                    - (if retainedExternal
                                          && reservation.Settled
                                          && currentBranchCount = 1 then
                                           1
                                       else
                                           0)
                                ExternalBranchReservationCounts = branchCounts
                                Reservations = account.Reservations.Add(reservationId, { reservation with Released = true })
                            }
                    | _ -> Error "BillingAccount release does not match releasable usage."

            match transition with
            | Error message -> error metadata message
            | Ok next ->
                let event = { OperationId = id; Command = command; State = next; Metadata = metadata }
                Ok { Account = next; OperationId = id; Events = [ event ]; WasIdempotentReplay = false }

    /// Implements the persistent Orleans aggregate for one owner BillingAccount.
    type BillingAccountActor
        (
            [<PersistentState(StateName.BillingAccount, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<List<BillingAccountEvent>>
        ) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("BillingAccount.Actor")
        let mutable account = BillingAccountDto.Default

        /// Stores the correlation id used while reporting actor diagnostics.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            account <-
                state.State
                |> Seq.fold (fun _ accountEvent -> accountEvent.State) BillingAccountDto.Default

            Task.CompletedTask

        /// Persists accepted events before publishing the new in-memory snapshot.
        member private this.ApplyEvents(events: BillingAccountEvent list) =
            task {
                state.State.AddRange events
                do! state.WriteStateAsync()

                account <-
                    events
                    |> List.fold (fun _ accountEvent -> accountEvent.State) account
            }

        interface IBillingAccountActor with
            member this.Get correlationId =
                this.correlationId <- correlationId
                account |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                (state.State :> IReadOnlyList<BillingAccountEvent>)
                |> returnTask

            member this.Handle command metadata =
                task {
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Grace.Shared.Constants.CurrentCommandProperty, operationId command)

                    let ownerId = this.GetPrimaryKey()

                    let targetsOwner =
                        match command with
                        | Reserve (_, commandOwnerId, _, _, _, _) -> commandOwnerId = ownerId
                        | Settle _
                        | Promote _
                        | Release _ -> account.OwnerId = ownerId

                    if not targetsOwner then
                        return Error(GraceError.Create "BillingAccount command does not match the grain key." metadata.CorrelationId)
                    else
                        match decideCommand state.State account command metadata with
                        | Error error ->
                            log.LogWarning(
                                "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Rejected BillingAccount command. Error: {Error}",
                                getCurrentInstantExtended (),
                                getMachineName,
                                metadata.CorrelationId,
                                error.Error
                            )

                            return Error error
                        | Ok decision ->
                            if not decision.Events.IsEmpty then do! this.ApplyEvents decision.Events

                            return Ok(GraceReturnValue.Create decision metadata.CorrelationId)
                }
