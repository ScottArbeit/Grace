namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Parameters.Stack
open Grace.Shared.Utilities
open Grace.Types.Stack
open Grace.Types.Types
open Microsoft.AspNetCore.Http
open System

module Stack =

    let Create: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<CreateStackParameters>

                let stackId =
                    if String.IsNullOrEmpty(parameters.StackId) then
                        Guid.NewGuid()
                    else
                        Guid.Parse(parameters.StackId)

                let command = StackCommand.Create(stackId, graceIds.RepositoryId, parameters.BaseBranchId, Array.empty)
                let actorProxy = Stack.CreateActorProxy stackId graceIds.RepositoryId parameters.CorrelationId
                let! result = actorProxy.Handle command (createMetadata context)
                return!
                    match result with
                    | Ok returnValue -> context |> result200Ok returnValue
                    | Error error -> context |> result400BadRequest error
            }

    let AddLayer: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<AddStackLayerParameters>

                let layer =
                    { StackLayer.ChangeId = parameters.ChangeId
                      BranchId = parameters.BranchId
                      Order = parameters.Order }

                match Guid.TryParse(parameters.StackId) with
                | true, stackId ->
                    let actorProxy = Stack.CreateActorProxy stackId graceIds.RepositoryId parameters.CorrelationId
                    let! result = actorProxy.Handle (StackCommand.AddLayer layer) (createMetadata context)
                    return!
                        match result with
                        | Ok returnValue -> context |> result200Ok returnValue
                        | Error error -> context |> result400BadRequest error
                | _ ->
                    return! context |> result400BadRequest (GraceError.Create "Invalid StackId." parameters.CorrelationId)
            }

    let Get: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<GetStackParameters>

                match Guid.TryParse(parameters.StackId) with
                | true, stackId ->
                    let actorProxy = Stack.CreateActorProxy stackId graceIds.RepositoryId parameters.CorrelationId
                    let! stackDto = actorProxy.Get parameters.CorrelationId
                    let returnValue = GraceReturnValue.Create stackDto parameters.CorrelationId
                    return! context |> result200Ok returnValue
                | _ ->
                    return! context |> result400BadRequest (GraceError.Create "Invalid StackId." parameters.CorrelationId)
            }

    let Restack: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<RestackParameters>

                let operationId = OperationId.NewGuid()
                let operationActor = Operation.CreateActorProxy operationId graceIds.RepositoryId parameters.CorrelationId
                let! _ = operationActor.Handle (Grace.Types.Operation.OperationCommand.Create(operationId, graceIds.RepositoryId)) (createMetadata context)

                let returnValue = GraceReturnValue.Create operationId parameters.CorrelationId
                return! context |> result200Ok returnValue
            }

    let ReparentAfterPromotion: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<ReparentAfterPromotionParameters>

                let operationId = OperationId.NewGuid()
                let operationActor = Operation.CreateActorProxy operationId graceIds.RepositoryId parameters.CorrelationId
                let! _ = operationActor.Handle (Grace.Types.Operation.OperationCommand.Create(operationId, graceIds.RepositoryId)) (createMetadata context)

                let returnValue = GraceReturnValue.Create operationId parameters.CorrelationId
                return! context |> result200Ok returnValue
            }
