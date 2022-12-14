namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open Giraffe
open Grace.Actors
open Grace.Actors.Commands.Branch
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Dto.Diff
open Grace.Shared.Parameters.Branch
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors.Branch
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Text.Json
open System.Threading.Tasks

module Branch =

    type Validations<'T when 'T :> BranchParameters> = 'T -> HttpContext -> Task<Result<unit, BranchError>>[]
    
    let activitySource = new ActivitySource("Branch")

    let getActorProxy (context: HttpContext) (branchId: BranchId) =
        let actorProxyFactory = context.GetService<IActorProxyFactory>()
        let actorId = ActorId($"{branchId}")
        actorProxyFactory.CreateActorProxy<IBranchActor>(actorId, ActorName.Branch)

    let processCommand<'T when 'T :> BranchParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> Task<BranchCommand>) =
        task {
            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                let validationResults = validations parameters context
                let! validationsPassed = validationResults |> areValid
                if validationsPassed then
                    let! repositoryId = resolveRepositoryId parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName
                    match repositoryId with
                    | Some repositoryId ->
                        parameters.RepositoryId <- repositoryId
                        match! resolveBranchId repositoryId parameters.BranchId parameters.BranchName with
                        | Some branchId ->
                            let actorProxy = getActorProxy context (Guid.Parse(branchId))
                            let! cmd = command parameters
                            match! actorProxy.Handle cmd (Services.createMetadata context) with
                                | Ok graceReturn -> return! context |> result200Ok graceReturn
                                | Error graceError -> return! context |> result400BadRequest graceError
                        | None -> return! context |> result400BadRequest (GraceError.Create (BranchError.getErrorMessage BranchDoesNotExist) (context.Items[Constants.CorrelationId] :?> string))
                    | None -> return! context |> result400BadRequest (GraceError.Create (BranchError.getErrorMessage RepositoryDoesNotExist) (context.Items[Constants.CorrelationId] :?> string))
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (BranchError.getErrorMessage error) (context.Items[Constants.CorrelationId] :?> string)
                    graceError.Properties.Add("Path", context.Request.Path)                    
                    return! context |> result400BadRequest graceError
            with ex ->
                let graceError = GraceError.Create $"{Utilities.createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string)
                graceError.Properties.Add("Path", context.Request.Path)
                return! context |> result500ServerError graceError
        }

    let processQuery<'T, 'U when 'T :> BranchParameters> (context: HttpContext) (parameters: 'T) (validations: Validations<'T>) maxCount (query: QueryResult<IBranchActor, 'U>) =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            try
                //let! parameters = context |> parse<'T>
                let validationResults = validations parameters context
                let! validationsPassed = validationResults |> areValid
                if validationsPassed then
                    match! resolveBranchId parameters.RepositoryId parameters.BranchId parameters.BranchName with
                    | Some branchId ->
                        let actorProxy = getActorProxy context (Guid.Parse(branchId))
                        let! queryResult = query context maxCount actorProxy
                        let! returnValue = context |> result200Ok (GraceReturnValue.Create queryResult (context.Items[Constants.CorrelationId] :?> string))
                        return returnValue
                    | None -> return! context |> result400BadRequest (GraceError.Create (BranchError.getErrorMessage BranchDoesNotExist) (context.Items[Constants.CorrelationId] :?> string))
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (BranchError.getErrorMessage error) (context.Items[Constants.CorrelationId] :?> string)
                    graceError.Properties.Add("Path", context.Request.Path)                    
                    return! context |> result400BadRequest graceError
            with ex ->
                return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
        }

    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                       stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                       stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherBranchIdOrBranchNameRequired |> returnTask
                       stringIsNotEmpty parameters.BranchId BranchIdIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                       stringIsNotEmpty parameters.BranchName BranchNameIsRequired |> returnTask
                       stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                       guidIsValidAndNotEmpty parameters.ParentBranchId InvalidBranchId |> returnTask
                       stringIsValidGraceName parameters.ParentBranchName InvalidBranchName |> returnTask
                       ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                       repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                       branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.ParentBranchId parameters.ParentBranchName context ParentBranchDoesNotExist
                       branchNameDoesNotExist parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchName context BranchNameAlreadyExists |]

                let command (parameters: CreateParameters) = 
                    task {
                        match! (resolveBranchId parameters.RepositoryId parameters.ParentBranchId parameters.ParentBranchName) with
                        | Some parentBranchId -> 
                            return Create(
                                (Guid.Parse(parameters.BranchId)), 
                                (BranchName parameters.BranchName),
                                (Guid.Parse(parentBranchId)),
                                Guid.Parse(parameters.RepositoryId))
                        | None ->
                            return Create(
                                (Guid.Parse(parameters.BranchId)), 
                                (BranchName parameters.BranchName),
                                Constants.DefaultParentBranchId,
                                Guid.Parse(parameters.RepositoryId))
                    }

                return! processCommand context validations command
            }

    let Rebase: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: RebaseParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                       stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                       stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherBranchIdOrBranchNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                       stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                       referenceIdExists parameters.BasedOn context ReferenceIdDoesNotExist
                       ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                       repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                       branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist
                       branchAllowsReferenceType parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName ReferenceType.Commit context CommitIsDisabled |]

                let command (parameters: RebaseParameters) = Rebase(parameters.BasedOn) |> returnTask

                return! processCommand context validations command
            }

    let Merge: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                       stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                       stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherBranchIdOrBranchNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                       stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                       stringIsNotEmpty parameters.Message MessageIsRequired |> returnTask
                       stringMaxLength parameters.Message 2048 StringIsTooLong |> returnTask
                       ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                       repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                       branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist
                       branchAllowsReferenceType parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName ReferenceType.Merge context MergeIsDisabled |]

                let command (parameters: CreateReferenceParameters) = BranchCommand.Merge(parameters.DirectoryId, parameters.Sha256Hash, ReferenceText parameters.Message) |> returnTask

                return! processCommand context validations command
            }
            
    let Commit: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                       stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                       stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherBranchIdOrBranchNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                       stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                       stringIsNotEmpty parameters.Message MessageIsRequired |> returnTask
                       stringMaxLength parameters.Message 2048 StringIsTooLong |> returnTask
                       ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                       repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                       branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist
                       branchAllowsReferenceType parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName ReferenceType.Commit context CommitIsDisabled |]

                let command (parameters: CreateReferenceParameters) = BranchCommand.Commit(parameters.DirectoryId, parameters.Sha256Hash, ReferenceText parameters.Message) |> returnTask

                return! processCommand context validations command
            }
            
    let Checkpoint: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                       stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                       stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                       stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                       stringMaxLength parameters.Message 2048 StringIsTooLong |> returnTask
                       ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                       repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                       branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist
                       branchAllowsReferenceType parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName ReferenceType.Checkpoint context CheckpointIsDisabled |]

                let command (parameters: CreateReferenceParameters) = BranchCommand.Checkpoint(parameters.DirectoryId, parameters.Sha256Hash, ReferenceText parameters.Message) |> returnTask

                return! processCommand context validations command
            }
            
    let Save: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                       stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                       stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                       stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                       stringMaxLength parameters.Message 4096 StringIsTooLong |> returnTask
                       ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                       repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                       branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist
                       branchAllowsReferenceType parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName ReferenceType.Save context SaveIsDisabled |]

                let command (parameters: CreateReferenceParameters) = BranchCommand.Save(parameters.DirectoryId, parameters.Sha256Hash, ReferenceText parameters.Message) |> returnTask

                return! processCommand context validations command
            }
            
    let Tag: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                       stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                       stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                       stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                       stringMaxLength parameters.Message 2048 StringIsTooLong |> returnTask
                       ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                       repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                       branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist
                       branchAllowsReferenceType parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName ReferenceType.Tag context TagIsDisabled |]

                let command (parameters: CreateReferenceParameters) = BranchCommand.Tag(parameters.DirectoryId, parameters.Sha256Hash, ReferenceText parameters.Message) |> returnTask

                return! processCommand context validations command
            }

    let EnableMerge: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                       stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                       stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                       stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                       ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                       repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                       branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist |]

                let command (parameters: EnableFeatureParameters) = EnableMerge(parameters.Enabled) |> returnTask

                return! processCommand context validations command
            }
            
    let EnableCommit: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                       stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                       stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                       stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                       ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                       repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                       branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist |]

                let command (parameters: EnableFeatureParameters) = EnableCommit(parameters.Enabled) |> returnTask

                return! processCommand context validations command
            }

    let EnableCheckpoint: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                       stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                       stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                       stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                       ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                       repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                       branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist |]

                let command (parameters: EnableFeatureParameters) = EnableCheckpoint(parameters.Enabled) |> returnTask

                return! processCommand context validations command
            }

    let EnableSave: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                       stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                       stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                       stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                       ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                       repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                       branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist |]

                let command (parameters: EnableFeatureParameters) = EnableSave(parameters.Enabled) |> returnTask

                return! processCommand context validations command
            }
            
    let EnableTag: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                       stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                       stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                       stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                       ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                       repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                       branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist |]

                let command (parameters: EnableFeatureParameters) = EnableTag(parameters.Enabled) |> returnTask

                return! processCommand context validations command
            }

    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DeleteParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                       stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                       stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                       stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                       ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                       repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                       branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist |]

                let command (parameters: DeleteParameters) = DeleteLogical |> returnTask

                return! processCommand context validations command
            }
            
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetParameters) (context: HttpContext) =
                        [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                           stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                           stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                           stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                           stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                           ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                           repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                           branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get()
                            return branchDto
                        }

                    let! parameters = context |> parse<GetParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }

    let GetParentBranch: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: BranchParameters) (context: HttpContext) =
                        [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                           stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                           stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                           stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                           stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                           ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                           repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                           branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! parentBranchDto = actorProxy.GetParentBranch()
                            return parentBranchDto
                        }

                    let! parameters = context |> parse<BranchParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }

    let GetReference: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferenceParameters) (context: HttpContext) =
                        [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                           stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                           stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                           stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                           stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                           ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                           repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                           branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let referenceActorId = ActorId(context.Items["ReferenceId"] :?> string)
                            let referenceActorProxy = ApplicationContext.ActorProxyFactory().CreateActorProxy<IReferenceActor>(referenceActorId, ActorName.Reference)
                            let! referenceDto = referenceActorProxy.Get()
                            return referenceDto
                        }

                    let! parameters = context |> parse<GetReferenceParameters>
                    context.Items.Add("ReferenceId", parameters.ReferenceId)
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }

    let GetReferences: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferencesParameters) (context: HttpContext) =
                        [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                           stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                           stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                           stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                           stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                           numberIsPositive parameters.MaxCount ValueMustBePositive |> returnTask
                           ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                           repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                           branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get()
                            let! results = getReferences branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    return! processQuery context parameters validations (parameters.MaxCount) query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }
            
    let GetDiffsForReferenceType: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetDiffsForReferenceTypeParameters) (context: HttpContext) =
                        [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                           stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                           stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                           stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                           stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                           numberIsPositive parameters.MaxCount ValueMustBePositive |> returnTask
                           stringIsNotEmpty parameters.ReferenceType ReferenceTypeMustBeProvided |> returnTask
                           isMemberOfDiscriminatedUnion<ReferenceType, BranchError> parameters.ReferenceType InvalidReferenceType |> returnTask
                           ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                           repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                           branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IBranchActor) =
                        task {
                            let diffDtos = ConcurrentBag<DiffDto>()
                            let! branchDto = actorProxy.Get()
                            let referenceType = (context.Items[nameof(ReferenceType)] :?> String) |> discriminatedUnionFromString<ReferenceType>
                            let! references = getReferencesByType referenceType.Value branchDto.BranchId maxCount

                            let sortedRefs = references.OrderByDescending(fun referenceDto -> referenceDto.CreatedAt).ToList();
                            
                            // Take pairs of references and get the diffs between them.
                            do! Parallel.ForEachAsync([0..sortedRefs.Count - 2], Constants.ParallelOptions, (fun i ct ->
                                ValueTask(task {
                                    let diffActorId = Diff.GetActorId sortedRefs[i].DirectoryId sortedRefs[i + 1].DirectoryId
                                    let diffActorProxy = ApplicationContext.ActorProxyFactory().CreateActorProxy<IDiffActor>(diffActorId, ActorName.Diff)
                                    let! diffDto = diffActorProxy.GetDiff()
                                    diffDtos.Add(diffDto)
                                })
                            ))

                            return (sortedRefs, diffDtos.OrderByDescending(fun diffDto -> Math.Max(diffDto.Directory1CreatedAt.ToUnixTimeMilliseconds(), diffDto.Directory2CreatedAt.ToUnixTimeMilliseconds())).ToList())
                        }

                    let! parameters = context |> parse<GetDiffsForReferenceTypeParameters>
                    context.Items.Add(nameof(ReferenceType), parameters.ReferenceType)
                    return! processQuery context parameters validations (parameters.MaxCount) query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }
        
    let GetMerges: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferencesParameters) (context: HttpContext) =
                        [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                           stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                           stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                           stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                           stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                           numberIsPositive parameters.MaxCount ValueMustBePositive |> returnTask
                           ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                           repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                           branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get()
                            let! results = getMerges branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    return! processQuery context parameters validations (parameters.MaxCount) query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }

    let GetCommits: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferencesParameters) (context: HttpContext) =
                        [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                           stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                           stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                           stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                           stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                           numberIsPositive parameters.MaxCount ValueMustBePositive |> returnTask
                           ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                           repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                           branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get()
                            let! results = getCommits branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    return! processQuery context parameters validations (parameters.MaxCount) query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }


    let GetCheckpoints: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferencesParameters) (context: HttpContext) =
                        [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                           stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                           stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                           stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                           stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                           numberIsPositive parameters.MaxCount ValueMustBePositive |> returnTask
                           ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                           repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                           branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get()
                            let! results = getCheckpoints branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    return! processQuery context parameters validations (parameters.MaxCount) query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }

    let GetSaves: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferencesParameters) (context: HttpContext) =
                        [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                           stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                           stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                           stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                           stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                           numberIsPositive parameters.MaxCount ValueMustBePositive |> returnTask
                           ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                           repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                           branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist |]


                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get()
                            let! results = getSaves branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    return! processQuery context parameters validations (parameters.MaxCount) query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }

    let GetTags: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferencesParameters) (context: HttpContext) =
                        [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                           stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                           stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                           stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                           stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                           numberIsPositive parameters.MaxCount ValueMustBePositive |> returnTask
                           ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                           repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                           branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist |]


                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get()
                            let! results = getTags branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    return! processQuery context parameters validations (parameters.MaxCount) query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }

    let GetVersion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetVersionParameters) (context: HttpContext) =
                        [| guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId |> returnTask
                           stringIsValidGraceName parameters.OwnerName InvalidOwnerName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                           stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId |> returnTask
                           stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.BranchId InvalidBranchId |> returnTask
                           stringIsValidGraceName parameters.BranchName InvalidBranchName |> returnTask
                           eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired |> returnTask
                           stringIsEmptyOrValidSha256Hash parameters.Sha256Hash Sha256HashDoesNotExist |> returnTask
                           guidIsValidAndNotEmpty parameters.ReferenceId ReferenceIdDoesNotExist |> returnTask
                           ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                           repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                           branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist |]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let parameters = context.Items["GetVersionParameters"] :?> GetVersionParameters
                            let repositoryId = Guid.Parse(parameters.RepositoryId)
                            let! rootDirectoryVersion =
                                task {
                                    if not <| String.IsNullOrEmpty(parameters.Sha256Hash) then
                                        return! getRootDirectoryBySha256Hash repositoryId parameters.Sha256Hash
                                    elif not <| String.IsNullOrEmpty(parameters.ReferenceId) then
                                        return! getRootDirectoryByReferenceId repositoryId (Guid.Parse(parameters.ReferenceId))
                                    else 
                                        let! branchDto = actorProxy.Get()
                                        let! latestReference = getLatestReference branchDto.BranchId
                                        match latestReference with
                                        | Some referenceDto -> 
                                            return! getRootDirectoryBySha256Hash repositoryId referenceDto.Sha256Hash
                                        | None ->
                                            return DirectoryVersion.Default
                                }

                            if rootDirectoryVersion.DirectoryId <> DirectoryVersion.Default.DirectoryId then
                                let directoryActorId = ActorId($"{rootDirectoryVersion.DirectoryId}")
                                let directoryActorProxy = ApplicationContext.ActorProxyFactory().CreateActorProxy<IDirectoryActor>(directoryActorId, ActorName.Directory)
                                let! directoryVersions = directoryActorProxy.GetDirectoryVersionsRecursive()
                                let directoryIds = directoryVersions.Select(fun dv -> dv.DirectoryId).ToList()
                                return directoryIds
                            else
                                return List<DirectoryId>()
                        }

                    let! parameters = context |> parse<GetVersionParameters>
                    let! repositoryId = resolveRepositoryId parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName
                    match repositoryId with
                    | Some repositoryId ->
                        parameters.RepositoryId <- repositoryId                            
                        if not <| String.IsNullOrEmpty(parameters.BranchId) || not <| String.IsNullOrEmpty(parameters.BranchName) then
                            logToConsole $"In Branch.GetVersion: parameters.BranchId: {parameters.BranchId}; parameters.BranchName: {parameters.BranchName}"
                            match! resolveBranchId repositoryId parameters.BranchId parameters.BranchName with
                            | Some branchId -> parameters.BranchId <- branchId
                            | None -> () // This should never happen because it would get caught in validations.
                        elif not <| String.IsNullOrEmpty(parameters.ReferenceId) then
                            logToConsole $"In Branch.GetVersion: parameters.ReferenceId: {parameters.ReferenceId}"
                            let referenceActorId = Reference.GetActorId(parameters.ReferenceId)
                            let referenceActorProxy = ApplicationContext.ActorProxyFactory().CreateActorProxy<IReferenceActor>(referenceActorId, ActorName.Reference)
                            let! referenceDto = referenceActorProxy.Get()
                            logToConsole $"referenceDto.ReferenceId: {referenceDto.ReferenceId}"
                            parameters.BranchId <- $"{referenceDto.BranchId}"
                        elif not <| String.IsNullOrEmpty(parameters.Sha256Hash) then
                            logToConsole $"In Branch.GetVersion: parameters.Sha256Hash: {parameters.Sha256Hash}"
                            let! referenceDto = getReferenceBySha256Hash parameters.Sha256Hash
                            logToConsole $"referenceDto.ReferenceId: {referenceDto.ReferenceId}"
                            parameters.BranchId <- $"{referenceDto.BranchId}"
                        else
                            ()  // This should never happen because it would get caught in validations.
                    | None ->
                        ()  // This should never happen because it would get caught in validations.
                    
                    // Now that we've populated BranchId for sure...
                    context.Items.Add("GetVersionParameters", parameters)
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }
