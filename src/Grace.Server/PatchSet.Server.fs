namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Parameters.PatchSet
open Grace.Shared.Utilities
open Grace.Types.PatchSet
open Grace.Types.Types
open Microsoft.AspNetCore.Http
open System

module PatchSet =

    let Create: HttpHandler =
        fun _ context ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<CreatePatchSetParameters>

                let! dto =
                    PatchSetService.createPatchSet
                        graceIds.RepositoryId
                        parameters.BaseDirectoryId
                        parameters.HeadDirectoryId
                        graceIds.OwnerId
                        parameters.CorrelationId

                let actorProxy = PatchSet.CreateActorProxy dto.PatchSetId graceIds.RepositoryId parameters.CorrelationId

                let command =
                    PatchSetCommand.Create(
                        dto.PatchSetId,
                        dto.RepositoryId,
                        dto.BaseDirectoryId,
                        dto.HeadDirectoryId,
                        dto.UpdatedPaths,
                        dto.Files,
                        dto.CreatedBy
                    )

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
                let! parameters = context |> parse<GetPatchSetParameters>

                match Guid.TryParse(parameters.PatchSetId) with
                | true, patchSetId ->
                    let actorProxy = PatchSet.CreateActorProxy patchSetId graceIds.RepositoryId parameters.CorrelationId
                    let! patchSetDto = actorProxy.Get parameters.CorrelationId
                    let returnValue = GraceReturnValue.Create patchSetDto parameters.CorrelationId
                    return! context |> result200Ok returnValue
                | _ ->
                    return! context |> result400BadRequest (GraceError.Create "Invalid PatchSetId." parameters.CorrelationId)
            }
