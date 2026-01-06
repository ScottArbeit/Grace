namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Grace.Types.Events
open Grace.Types.Queue
open Grace.Types.Types
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module ConflictReceipt =

    type ConflictReceiptActor([<PersistentState(StateName.ConflictReceipt, Constants.GraceActorStorage)>] state: IPersistentState<List<ConflictReceiptEvent>>) =
        inherit Grain()

        static let actorName = ActorName.ConflictReceipt

        let log = loggerFactory.CreateLogger("ConflictReceipt.Actor")

        let mutable currentCommand = String.Empty

        let mutable receipt = ConflictReceipt.Default

        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()

            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            receipt <-
                state.State
                |> Seq.fold (fun dto ev -> ConflictReceiptDto.UpdateDto ev dto) receipt

            Task.CompletedTask

        member private this.ApplyEvent(receiptEvent: ConflictReceiptEvent) =
            task {
                let correlationId = receiptEvent.Metadata.CorrelationId

                try
                    state.State.Add(receiptEvent)
                    do! state.WriteStateAsync()

                    receipt <- receipt |> ConflictReceiptDto.UpdateDto receiptEvent

                    let graceEvent = GraceEvent.ConflictReceiptEvent receiptEvent
                    do! publishGraceEvent graceEvent receiptEvent.Metadata

                    let returnValue =
                        (GraceReturnValue.Create "Conflict receipt recorded." correlationId)
                            .enhance(nameof ConflictReceiptId, receipt.ConflictReceiptId)
                            .enhance(nameof CandidateId, receipt.CandidateId)
                            .enhance (nameof ConflictReceiptEventType, getDiscriminatedUnionFullName receiptEvent.Event)

                    return Ok returnValue
                with ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; Failed to apply event {eventType} for conflict receipt {receiptId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        getDiscriminatedUnionCaseName receiptEvent.Event,
                        receipt.ConflictReceiptId
                    )

                    let graceError =
                        (GraceError.CreateWithException ex (ConflictReceiptError.getErrorMessage ConflictReceiptError.FailedWhileApplyingEvent) correlationId)
                            .enhance (nameof ConflictReceiptId, receipt.ConflictReceiptId)

                    return Error graceError
            }

        interface IHasRepositoryId with
            member this.GetRepositoryId correlationId = receipt.RepositoryId |> returnTask

        interface IConflictReceiptActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId
                (receipt.ConflictReceiptId <> ConflictReceiptId.Empty) |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId

                if receipt.ConflictReceiptId = ConflictReceiptId.Empty then
                    None |> returnTask
                else
                    Some receipt |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId
                state.State :> IReadOnlyList<ConflictReceiptEvent> |> returnTask

            member this.Handle command metadata =
                let isValid (command: ConflictReceiptCommand) (metadata: EventMetadata) =
                    task {
                        if state.State.Exists(fun ev -> ev.Metadata.CorrelationId = metadata.CorrelationId) then
                            return
                                Error(
                                    GraceError.Create (ConflictReceiptError.getErrorMessage ConflictReceiptError.DuplicateCorrelationId) metadata.CorrelationId
                                )
                        else
                            match command with
                            | ConflictReceiptCommand.Create _ ->
                                if receipt.ConflictReceiptId <> ConflictReceiptId.Empty then
                                    return
                                        Error(
                                            GraceError.Create
                                                (ConflictReceiptError.getErrorMessage ConflictReceiptError.ConflictReceiptAlreadyExists)
                                                metadata.CorrelationId
                                        )
                                else
                                    return Ok command
                    }

                let processCommand (command: ConflictReceiptCommand) (metadata: EventMetadata) =
                    task {
                        let! (receiptEventType: ConflictReceiptEventType) =
                            task {
                                match command with
                                | ConflictReceiptCommand.Create receipt -> return ConflictReceiptEventType.Created receipt
                            }

                        let receiptEvent: ConflictReceiptEvent = { Event = receiptEventType; Metadata = metadata }
                        return! this.ApplyEvent receiptEvent
                    }

                task {
                    currentCommand <- getDiscriminatedUnionCaseName command
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Constants.CurrentCommandProperty, getDiscriminatedUnionCaseName command)

                    match! isValid command metadata with
                    | Ok command -> return! processCommand command metadata
                    | Error error -> return Error error
                }
