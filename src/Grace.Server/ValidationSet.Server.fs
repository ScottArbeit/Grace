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
open NodaTime
open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

module ValidationSet =
    type Validations<'T when 'T :> ValidationParameters> = 'T -> ValueTask<Result<unit, ValidationSetError>> array

    let activitySource = new ActivitySource("ValidationSet")

    let private getPrincipal (context: HttpContext) =
        if
            isNull context.User
            || isNull context.User.Identity
            || String.IsNullOrWhiteSpace(context.User.Identity.Name)
        then
            Grace.Shared.Constants.GraceSystemUser
        else
            context.User.Identity.Name

    let private parseValidationSetIdOrNew (rawValidationSetId: string) =
        if String.IsNullOrWhiteSpace(rawValidationSetId) then
            Ok(Guid.NewGuid())
        else
            let mutable parsed = Guid.Empty

            if
                Guid.TryParse(rawValidationSetId, &parsed)
                && parsed <> Guid.Empty
            then
                Ok parsed
            else
                Error ValidationSetError.InvalidValidationSetId

    let private processCommand<'T when 'T :> ValidationParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (validationSetId: ValidationSetId)
        (command: ValidationSetCommand)
        =
        task {
            use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let metadata = createMetadata context
            let parameterDictionary = getParametersAsDictionary parameters

            let validationResults = validations parameters
            let! validationsPassed = validationResults |> allPass

            if validationsPassed then
                let actorProxy = ValidationSet.CreateActorProxy validationSetId graceIds.RepositoryId correlationId

                match! actorProxy.Handle command metadata with
                | Ok graceReturnValue ->
                    graceReturnValue
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance(nameof ValidationSetId, validationSetId)
                        .enhance ("Path", context.Request.Path.Value)
                    |> ignore

                    return! context |> result200Ok graceReturnValue
                | Error graceError ->
                    graceError
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance(nameof ValidationSetId, validationSetId)
                        .enhance ("Path", context.Request.Path.Value)
                    |> ignore

                    return! context |> result400BadRequest graceError
            else
                let! validationError = validationResults |> getFirstError
                let graceError = GraceError.Create (ValidationSetError.getErrorMessage validationError) correlationId
                return! context |> result400BadRequest graceError
        }

    let private processGet (context: HttpContext) (parameters: GetValidationSetParameters) (validationSetId: ValidationSetId) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let actorProxy = ValidationSet.CreateActorProxy validationSetId graceIds.RepositoryId correlationId

            match! actorProxy.Get correlationId with
            | Some validationSet ->
                let graceReturnValue =
                    (GraceReturnValue.Create validationSet correlationId)
                        .enhance(getParametersAsDictionary parameters)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance(nameof ValidationSetId, validationSetId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result200Ok graceReturnValue
            | None ->
                let graceError = GraceError.Create (ValidationSetError.getErrorMessage ValidationSetError.ValidationSetDoesNotExist) correlationId
                return! context |> result400BadRequest graceError
        }

    /// Creates a validation set.
    let Create: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let! parameters = context |> parse<CreateValidationSetParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                match parseValidationSetIdOrNew parameters.ValidationSetId with
                | Error validationSetError ->
                    return!
                        context
                        |> result400BadRequest (GraceError.Create (ValidationSetError.getErrorMessage validationSetError) (getCorrelationId context))
                | Ok validationSetId ->
                    let validations (_: CreateValidationSetParameters) =
                        [|
                            Guid.isValidAndNotEmptyGuid parameters.TargetBranchId ValidationSetError.InvalidTargetBranchId
                            if parameters.Rules.IsEmpty then
                                Error ValidationSetError.ValidationSetRulesRequired
                            else
                                Ok()
                            |> returnValueTask
                            if parameters.Validations.IsEmpty then
                                Error ValidationSetError.ValidationDefinitionsRequired
                            else
                                Ok()
                            |> returnValueTask
                        |]

                    let validationSetDto =
                        { ValidationSetDto.Default with
                            ValidationSetId = validationSetId
                            OwnerId = graceIds.OwnerId
                            OrganizationId = graceIds.OrganizationId
                            RepositoryId = graceIds.RepositoryId
                            TargetBranchId = Guid.Parse(parameters.TargetBranchId)
                            Rules = parameters.Rules
                            Validations = parameters.Validations
                            CreatedBy = UserId(getPrincipal context)
                            CreatedAt = getCurrentInstant ()
                        }

                    let command = ValidationSetCommand.Create validationSetDto
                    return! processCommand context parameters validations validationSetId command
            }

    /// Gets a validation set.
    let Get: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                let! parameters = context |> parse<GetValidationSetParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validations (_: GetValidationSetParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.ValidationSetId ValidationSetError.InvalidValidationSetId
                    |]

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let validationSetId = Guid.Parse(parameters.ValidationSetId)
                    return! processGet context parameters validationSetId
                else
                    let! validationError = validationResults |> getFirstError

                    return!
                        context
                        |> result400BadRequest (GraceError.Create (ValidationSetError.getErrorMessage validationError) correlationId)
            }

    /// Updates a validation set.
    let Update: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                let! parameters = context |> parse<UpdateValidationSetParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validations (_: UpdateValidationSetParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.ValidationSetId ValidationSetError.InvalidValidationSetId
                        Guid.isValidAndNotEmptyGuid parameters.TargetBranchId ValidationSetError.InvalidTargetBranchId
                        if parameters.Rules.IsEmpty then
                            Error ValidationSetError.ValidationSetRulesRequired
                        else
                            Ok()
                        |> returnValueTask
                        if parameters.Validations.IsEmpty then
                            Error ValidationSetError.ValidationDefinitionsRequired
                        else
                            Ok()
                        |> returnValueTask
                    |]

                let validationSetId = Guid.Parse(parameters.ValidationSetId)
                let actorProxy = ValidationSet.CreateActorProxy validationSetId graceIds.RepositoryId correlationId
                let! currentValidationSet = actorProxy.Get correlationId

                match currentValidationSet with
                | None ->
                    return!
                        context
                        |> result400BadRequest (
                            GraceError.Create (ValidationSetError.getErrorMessage ValidationSetError.ValidationSetDoesNotExist) correlationId
                        )
                | Some currentValidationSet ->
                    let updatedValidationSet =
                        { currentValidationSet with
                            TargetBranchId = Guid.Parse(parameters.TargetBranchId)
                            Rules = parameters.Rules
                            Validations = parameters.Validations
                        }

                    let command = ValidationSetCommand.Update updatedValidationSet
                    return! processCommand context parameters validations validationSetId command
            }

    /// Logically deletes a validation set.
    let Delete: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                let! parameters = context |> parse<DeleteValidationSetParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validations (_: DeleteValidationSetParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.ValidationSetId ValidationSetError.InvalidValidationSetId
                    |]

                let validationSetId = Guid.Parse(parameters.ValidationSetId)

                let command = ValidationSetCommand.DeleteLogical(parameters.Force, DeleteReason parameters.DeleteReason)

                return! processCommand context parameters validations validationSetId command
            }
