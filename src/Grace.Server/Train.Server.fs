namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Parameters.Train
open Grace.Shared.Utilities
open Grace.Types.Train
open Grace.Types.Types
open Microsoft.AspNetCore.Http
open System

module Train =

    let Create: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<CreateTrainParameters>

                let trainId =
                    if String.IsNullOrEmpty(parameters.PromotionTrainId) then
                        Guid.NewGuid()
                    else
                        Guid.Parse(parameters.PromotionTrainId)

                let command =
                    TrainCommand.Create(
                        trainId,
                        graceIds.RepositoryId,
                        parameters.TargetBranchId,
                        parameters.BaseReferenceId,
                        parameters.ScheduledAt
                    )

                let actorProxy = Train.CreateActorProxy trainId graceIds.RepositoryId parameters.CorrelationId
                let! result = actorProxy.Handle command (createMetadata context)
                return!
                    match result with
                    | Ok returnValue -> context |> result200Ok returnValue
                    | Error error -> context |> result400BadRequest error
            }

    let Enqueue: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<EnqueueTrainParameters>

                match Guid.TryParse(parameters.PromotionTrainId) with
                | true, trainId ->
                    let actorProxy = Train.CreateActorProxy trainId graceIds.RepositoryId parameters.CorrelationId
                    let! result = actorProxy.Handle (TrainCommand.Enqueue parameters.ChangeId) (createMetadata context)
                    return!
                        match result with
                        | Ok returnValue -> context |> result200Ok returnValue
                        | Error error -> context |> result400BadRequest error
                | _ ->
                    return! context |> result400BadRequest (GraceError.Create "Invalid PromotionTrainId." parameters.CorrelationId)
            }

    let Dequeue: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<DequeueTrainParameters>

                match Guid.TryParse(parameters.PromotionTrainId) with
                | true, trainId ->
                    let actorProxy = Train.CreateActorProxy trainId graceIds.RepositoryId parameters.CorrelationId
                    let! result = actorProxy.Handle (TrainCommand.Dequeue parameters.ChangeId) (createMetadata context)
                    return!
                        match result with
                        | Ok returnValue -> context |> result200Ok returnValue
                        | Error error -> context |> result400BadRequest error
                | _ ->
                    return! context |> result400BadRequest (GraceError.Create "Invalid PromotionTrainId." parameters.CorrelationId)
            }

    let Build: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<BuildTrainParameters>

                let operationId = OperationId.NewGuid()
                let operationActor = Operation.CreateActorProxy operationId graceIds.RepositoryId parameters.CorrelationId
                let! _ = operationActor.Handle (Grace.Types.Operation.OperationCommand.Create(operationId, graceIds.RepositoryId)) (createMetadata context)

                let returnValue = GraceReturnValue.Create operationId parameters.CorrelationId
                return! context |> result200Ok returnValue
            }

    let Apply: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<ApplyTrainParameters>

                let operationId = OperationId.NewGuid()
                let operationActor = Operation.CreateActorProxy operationId graceIds.RepositoryId parameters.CorrelationId
                let! _ = operationActor.Handle (Grace.Types.Operation.OperationCommand.Create(operationId, graceIds.RepositoryId)) (createMetadata context)

                let returnValue = GraceReturnValue.Create operationId parameters.CorrelationId
                return! context |> result200Ok returnValue
            }

    let Get: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<GetTrainParameters>

                match Guid.TryParse(parameters.PromotionTrainId) with
                | true, trainId ->
                    let actorProxy = Train.CreateActorProxy trainId graceIds.RepositoryId parameters.CorrelationId
                    let! trainDto = actorProxy.Get parameters.CorrelationId
                    let returnValue = GraceReturnValue.Create trainDto parameters.CorrelationId
                    return! context |> result200Ok returnValue
                | _ ->
                    return! context |> result400BadRequest (GraceError.Create "Invalid PromotionTrainId." parameters.CorrelationId)
            }
