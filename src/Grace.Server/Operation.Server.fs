namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Parameters.Operation
open Grace.Shared.Utilities
open Grace.Types.Types
open Microsoft.AspNetCore.Http
open System

module Operation =

    let Get: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<GetOperationParameters>

                match Guid.TryParse(parameters.OperationId) with
                | true, operationId ->
                    let actorProxy = Operation.CreateActorProxy operationId graceIds.RepositoryId parameters.CorrelationId
                    let! operationDto = actorProxy.Get parameters.CorrelationId
                    let returnValue = GraceReturnValue.Create operationDto parameters.CorrelationId
                    return! context |> result200Ok returnValue
                | _ ->
                    return! context |> result400BadRequest (GraceError.Create "Invalid OperationId." parameters.CorrelationId)
            }

    let Cancel: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<CancelOperationParameters>

                match Guid.TryParse(parameters.OperationId) with
                | true, operationId ->
                    let actorProxy = Operation.CreateActorProxy operationId graceIds.RepositoryId parameters.CorrelationId
                    let! result =
                        actorProxy.Handle
                            (Grace.Types.Operation.OperationCommand.Cancel)
                            (createMetadata context)

                    return!
                        match result with
                        | Ok returnValue -> context |> result200Ok returnValue
                        | Error error -> context |> result400BadRequest error
                | _ ->
                    return! context |> result400BadRequest (GraceError.Create "Invalid OperationId." parameters.CorrelationId)
            }

    let ApproveConflict: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<ApproveConflictParameters>

                match Guid.TryParse(parameters.OperationId) with
                | true, operationId ->
                    let actorProxy = Operation.CreateActorProxy operationId graceIds.RepositoryId parameters.CorrelationId
                    let! result =
                        actorProxy.Handle
                            (Grace.Types.Operation.OperationCommand.Start)
                            (createMetadata context)

                    return!
                        match result with
                        | Ok returnValue -> context |> result200Ok returnValue
                        | Error error -> context |> result400BadRequest error
                | _ ->
                    return! context |> result400BadRequest (GraceError.Create "Invalid OperationId." parameters.CorrelationId)
            }
