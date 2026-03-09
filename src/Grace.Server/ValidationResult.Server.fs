namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Extensions
open Grace.Shared.Parameters.Validation
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.Types
open Grace.Types.Validation
open Microsoft.AspNetCore.Http
open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

module ValidationResult =
    let activitySource = new ActivitySource("ValidationResult")

    let internal hasPromotionSetScope (promotionSetId: string) = not (String.IsNullOrWhiteSpace promotionSetId)

    let internal isValidStepsComputationAttempt (stepsComputationAttempt: int) = stepsComputationAttempt > 0

    let private getPrincipal (context: HttpContext) =
        if
            isNull context.User
            || isNull context.User.Identity
            || String.IsNullOrWhiteSpace(context.User.Identity.Name)
        then
            Grace.Shared.Constants.GraceSystemUser
        else
            context.User.Identity.Name

    let private parseOptionalGuid (rawValue: string) = if String.IsNullOrWhiteSpace(rawValue) then None else Some(Guid.Parse(rawValue))

    let internal validationsForRecord (parameters: RecordValidationResultParameters) =
        [|
            (if String.IsNullOrWhiteSpace(parameters.ValidationResultId) then
                 Ok() |> returnValueTask
             else
                 Guid.isValidAndNotEmptyGuid parameters.ValidationResultId ValidationResultError.InvalidValidationResultId)
            (if String.IsNullOrWhiteSpace(parameters.ValidationSetId) then
                 Ok() |> returnValueTask
             else
                 Guid.isValidAndNotEmptyGuid parameters.ValidationSetId ValidationResultError.InvalidValidationSetId)
            (if String.IsNullOrWhiteSpace(parameters.PromotionSetId) then
                 Ok() |> returnValueTask
             else
                 Guid.isValidAndNotEmptyGuid parameters.PromotionSetId ValidationResultError.InvalidPromotionSetId)
            (if String.IsNullOrWhiteSpace(parameters.PromotionSetStepId) then
                 Ok() |> returnValueTask
             else
                 Guid.isValidAndNotEmptyGuid parameters.PromotionSetStepId ValidationResultError.InvalidPromotionSetStepId)
            String.isNotEmpty parameters.ValidationName ValidationResultError.ValidationNameRequired
            String.isNotEmpty parameters.ValidationVersion ValidationResultError.ValidationVersionRequired
            DiscriminatedUnion.isMemberOf<ValidationStatus, ValidationResultError> parameters.Status ValidationResultError.InvalidValidationStatus
            (if parameters.ArtifactIds
                |> Seq.forall (fun artifactId ->
                    if String.IsNullOrWhiteSpace artifactId then
                        false
                    else
                        let mutable parsed = Guid.Empty

                        Guid.TryParse(artifactId, &parsed)
                        && parsed <> Guid.Empty) then
                 Ok()
             else
                 Error ValidationResultError.InvalidArtifactId)
            |> returnValueTask
            (if hasPromotionSetScope parameters.PromotionSetId then
                 if parameters.StepsComputationAttempt < 0 then
                     Error ValidationResultError.StepsComputationAttemptRequired
                 elif isValidStepsComputationAttempt parameters.StepsComputationAttempt then
                     Ok()
                 else
                     Error ValidationResultError.InvalidStepsComputationAttempt
             else
                 Ok())
            |> returnValueTask
        |]

    /// Records a validation result.
    let Record: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            async {
                use activity = activitySource.StartActivity("Record", ActivityKind.Server)
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let! parameters =
                    context
                    |> parse<RecordValidationResultParameters>
                    |> Async.AwaitTask

                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validationResults = validationsForRecord parameters
                let! validationsPassed = validationResults |> allPass |> Async.AwaitTask

                if validationsPassed then
                    let validationStatus =
                        discriminatedUnionFromString<ValidationStatus> parameters.Status
                        |> Option.get

                    let artifactIds =
                        parameters.ArtifactIds
                        |> Seq.map Guid.Parse
                        |> Seq.toList

                    let validationResultId =
                        if String.IsNullOrWhiteSpace(parameters.ValidationResultId) then
                            Guid.NewGuid()
                        else
                            Guid.Parse(parameters.ValidationResultId)

                    let validationSetId = parseOptionalGuid parameters.ValidationSetId

                    let promotionSetId = parseOptionalGuid parameters.PromotionSetId

                    let promotionSetStepId = parseOptionalGuid parameters.PromotionSetStepId

                    let stepsComputationAttempt =
                        if hasPromotionSetScope parameters.PromotionSetId then
                            Some parameters.StepsComputationAttempt
                        else
                            None

                    let validationResultDto: ValidationResultDto =
                        { ValidationResultDto.Default with
                            ValidationResultId = validationResultId
                            OwnerId = graceIds.OwnerId
                            OrganizationId = graceIds.OrganizationId
                            RepositoryId = graceIds.RepositoryId
                            ValidationSetId = validationSetId
                            PromotionSetId = promotionSetId
                            PromotionSetStepId = promotionSetStepId
                            StepsComputationAttempt = stepsComputationAttempt
                            ValidationName = parameters.ValidationName
                            ValidationVersion = parameters.ValidationVersion
                            Output = { Status = validationStatus; Summary = parameters.Summary; ArtifactIds = artifactIds }
                            OnBehalfOf = [ UserId(getPrincipal context) ]
                            CreatedAt = getCurrentInstant ()
                        }

                    let actorProxy = ValidationResult.CreateActorProxy validationResultId graceIds.RepositoryId correlationId
                    let metadata = createMetadata context
                    metadata.Properties[ nameof ValidationResultId ] <- $"{validationResultId}"
                    metadata.Properties[ "ActorId" ] <- $"{validationResultId}"

                    validationSetId
                    |> Option.iter (fun value -> metadata.Properties[ nameof ValidationSetId ] <- $"{value}")

                    promotionSetId
                    |> Option.iter (fun value -> metadata.Properties[ nameof PromotionSetId ] <- $"{value}")

                    promotionSetStepId
                    |> Option.iter (fun value -> metadata.Properties[ nameof PromotionSetStepId ] <- $"{value}")

                    let! handleResult =
                        actorProxy.Handle (ValidationResultCommand.Record validationResultDto) metadata
                        |> Async.AwaitTask

                    match handleResult with
                    | Error graceError ->
                        return!
                            context
                            |> result400BadRequest graceError
                            |> Async.AwaitTask
                    | Ok _ ->
                        let! savedResult = actorProxy.Get correlationId |> Async.AwaitTask

                        if savedResult.IsNone then
                            let graceError =
                                GraceError.Create (ValidationResultError.getErrorMessage ValidationResultError.FailedWhileApplyingEvent) correlationId

                            return!
                                context
                                |> result500ServerError graceError
                                |> Async.AwaitTask
                        else
                            let graceReturnValue =
                                (GraceReturnValue.Create savedResult.Value correlationId)
                                    .enhance(getParametersAsDictionary parameters)
                                    .enhance(nameof OwnerId, graceIds.OwnerId)
                                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                    .enhance(nameof ValidationResultId, validationResultId)
                                    .enhance ("Path", context.Request.Path.Value)

                            return!
                                context
                                |> result200Ok graceReturnValue
                                |> Async.AwaitTask
                else
                    let! validationError =
                        validationResults
                        |> getFirstError
                        |> Async.AwaitTask

                    let errorMessage = ValidationResultError.getErrorMessage (validationError: ValidationResultError option)

                    return!
                        context
                        |> result400BadRequest (GraceError.Create errorMessage correlationId)
                        |> Async.AwaitTask
            }
            |> Async.StartAsTask
