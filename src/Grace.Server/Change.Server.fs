namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Parameters.Change
open Grace.Shared.Utilities
open Grace.Types.Change
open Grace.Types.Types
open Microsoft.AspNetCore.Http
open System
open System.Collections.Generic
open System.Threading.Tasks

module Change =

    let private getChangeId (value: string) =
        match Guid.TryParse(value) with
        | true, parsed -> Ok parsed
        | _ -> Error "Invalid ChangeId."

    let Create: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<CreateChangeParameters>

                let changeId =
                    if String.IsNullOrEmpty(parameters.ChangeId) then
                        Guid.NewGuid()
                    else
                        Guid.Parse(parameters.ChangeId)

                let command =
                    ChangeCommand.Create(
                        changeId,
                        graceIds.RepositoryId,
                        parameters.Title,
                        parameters.Description,
                        None,
                        None
                    )

                let actorProxy = Change.CreateActorProxy changeId graceIds.RepositoryId parameters.CorrelationId
                let! result = actorProxy.Handle command (createMetadata context)
                return!
                    match result with
                    | Ok returnValue -> context |> result200Ok returnValue
                    | Error error -> context |> result400BadRequest error
            }

    let UpdateMetadata: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<UpdateChangeMetadataParameters>

                match getChangeId parameters.ChangeId with
                | Error message -> return! context |> result400BadRequest (GraceError.Create message parameters.CorrelationId)
                | Ok changeId ->
                    let command = ChangeCommand.UpdateMetadata(parameters.Title, parameters.Description)
                    let actorProxy = Change.CreateActorProxy changeId graceIds.RepositoryId parameters.CorrelationId
                    let! result = actorProxy.Handle command (createMetadata context)
                    return!
                        match result with
                        | Ok returnValue -> context |> result200Ok returnValue
                        | Error error -> context |> result400BadRequest error
            }

    let Get: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<GetChangeParameters>

                match getChangeId parameters.ChangeId with
                | Error message -> return! context |> result400BadRequest (GraceError.Create message parameters.CorrelationId)
                | Ok changeId ->
                    let actorProxy = Change.CreateActorProxy changeId graceIds.RepositoryId parameters.CorrelationId
                    let! changeDto = actorProxy.Get parameters.CorrelationId
                    let returnValue = GraceReturnValue.Create changeDto parameters.CorrelationId
                    return! context |> result200Ok returnValue
            }

    let List: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<ListChangesParameters>
                // Listing requires a separate index; v1 returns empty until list support is added.
                let returnValue = GraceReturnValue.Create (Array.empty<ChangeDto>) parameters.CorrelationId
                return! context |> result200Ok returnValue
            }
