namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Extensions
open Grace.Shared.Parameters.PromotionSet
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.Types
open Microsoft.AspNetCore.Http
open System

module PromotionSet =
    /// Gets a promotion set.
    let Get: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                let! parameters = context |> parse<GetPromotionSetParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validations (_: GetPromotionSetParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.PromotionSetId QueueError.InvalidPromotionSetId
                    |]

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let promotionSetId = Guid.Parse(parameters.PromotionSetId)
                    let actorProxy = PromotionSet.CreateActorProxy promotionSetId graceIds.RepositoryId correlationId
                    let! promotionSet = actorProxy.Get correlationId

                    let graceReturnValue =
                        (GraceReturnValue.Create promotionSet correlationId)
                            .enhance(getParametersAsDictionary parameters)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof PromotionSetId, promotionSetId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result200Ok graceReturnValue
                else
                    let! validationError = validationResults |> getFirstError

                    return!
                        context
                        |> result400BadRequest (GraceError.Create (QueueError.getErrorMessage validationError) correlationId)
            }
