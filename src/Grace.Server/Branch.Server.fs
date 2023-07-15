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

    type Validations<'T when 'T :> BranchParameters> = 'T -> HttpContext -> Task<Result<unit, BranchError>> list
    
    let activitySource = new ActivitySource("Branch")

    let actorProxyFactory = ApplicationContext.ActorProxyFactory()

    let getActorProxy (context: HttpContext) (branchId: BranchId) =
        let actorId = ActorId($"{branchId}")
        actorProxyFactory.CreateActorProxy<IBranchActor>(actorId, ActorName.Branch)

    let processCommand<'T when 'T :> BranchParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> Task<BranchCommand>) =
        task {
            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                let validationResults = validations parameters context
                let! validationsPassed = validationResults |> allPass
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
                        | None -> 
                            return! context |> result400BadRequest (GraceError.Create (BranchError.getErrorMessage BranchDoesNotExist) (getCorrelationId context))
                    | None -> 
                        return! context |> result400BadRequest (GraceError.Create (BranchError.getErrorMessage RepositoryDoesNotExist) (getCorrelationId context))
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (BranchError.getErrorMessage error) (getCorrelationId context)
                    graceError.Properties.Add("Path", context.Request.Path)                    
                    return! context |> result400BadRequest graceError
            with ex ->
                let graceError = GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context)
                graceError.Properties.Add("Path", context.Request.Path)
                return! context |> result500ServerError graceError
        }

    let processQuery<'T, 'U when 'T :> BranchParameters> (context: HttpContext) (parameters: 'T) (validations: Validations<'T>) maxCount (query: QueryResult<IBranchActor, 'U>) =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            try
                //let! parameters = context |> parse<'T>
                let validationResults = validations parameters context
                let! validationsPassed = validationResults |> allPass
                if validationsPassed then
                    match! resolveBranchId parameters.RepositoryId parameters.BranchId parameters.BranchName with
                    | Some branchId ->
                        let actorProxy = getActorProxy context (Guid.Parse(branchId))
                        let! queryResult = query context maxCount actorProxy
                        let! returnValue = context |> result200Ok (GraceReturnValue.Create queryResult (getCorrelationId context))
                        return returnValue
                    | None -> return! context |> result400BadRequest (GraceError.Create (BranchError.getErrorMessage BranchDoesNotExist) (getCorrelationId context))
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (BranchError.getErrorMessage error) (getCorrelationId context)
                    graceError.Properties.Add("Path", context.Request.Path)                    
                    return! context |> result400BadRequest graceError
            with ex ->
                return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
        }

    /// Creates a new branch.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateBranchParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                      Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                      Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherBranchIdOrBranchNameRequired
                      String.isNotEmpty parameters.BranchId BranchIdIsRequired
                      Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                      String.isNotEmpty parameters.BranchName BranchNameIsRequired
                      String.isValidGraceName parameters.BranchName InvalidBranchName
                      Guid.isValidAndNotEmpty parameters.ParentBranchId InvalidBranchId
                      String.isValidGraceName parameters.ParentBranchName InvalidBranchName
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                      Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.ParentBranchId parameters.ParentBranchName context ParentBranchDoesNotExist
                      Branch.branchNameDoesNotExist parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchName context BranchNameAlreadyExists ]

                let command (parameters: CreateBranchParameters) = 
                    task {
                        match! (resolveBranchId parameters.RepositoryId parameters.ParentBranchId parameters.ParentBranchName) with
                        | Some parentBranchId -> 
                            let parentBranchActorId = ActorId(parentBranchId)
                            let parentBranchActorProxy = ApplicationContext.ActorProxyFactory().CreateActorProxy<IBranchActor>(parentBranchActorId, ActorName.Branch)
                            let! parentBranch = parentBranchActorProxy.Get()
                            return Create(
                                (Guid.Parse(parameters.BranchId)), 
                                (BranchName parameters.BranchName),
                                (Guid.Parse(parentBranchId)),
                                parentBranch.BasedOn,
                                Guid.Parse(parameters.RepositoryId))
                        | None ->
                            return Create(
                                (Guid.Parse(parameters.BranchId)), 
                                (BranchName parameters.BranchName),
                                Constants.DefaultParentBranchId,
                                ReferenceId.Empty,
                                Guid.Parse(parameters.RepositoryId))
                    }

                return! processCommand context validations command
            }

    /// Rebases a branch on its parent branch.
    let Rebase: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: RebaseParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                      Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                      Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherBranchIdOrBranchNameRequired
                      Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                      String.isValidGraceName parameters.BranchName InvalidBranchName
                      Branch.referenceIdExists parameters.BasedOn context ReferenceIdDoesNotExist
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                      Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist
                      Branch.branchAllowsReferenceType parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName ReferenceType.Commit context CommitIsDisabled ]

                let command (parameters: RebaseParameters) = Rebase(parameters.BasedOn) |> returnTask

                return! processCommand context validations command
            }

    /// Creates a promotion reference in the parent of the specified branch, based on the most-recent commit.
    let Promote: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                      Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                      Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherBranchIdOrBranchNameRequired
                      Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                      String.isValidGraceName parameters.BranchName InvalidBranchName
                      String.isNotEmpty parameters.Message MessageIsRequired
                      String.maxLength parameters.Message 2048 StringIsTooLong
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                      Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist
                      Branch.branchAllowsReferenceType parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName ReferenceType.Promotion context PromotionIsDisabled ]

                let command (parameters: CreateReferenceParameters) = BranchCommand.Promote(parameters.DirectoryId, parameters.Sha256Hash, ReferenceText parameters.Message) |> returnTask

                return! processCommand context validations command
            }

    /// Creates a commit reference pointing to the current root directory version in the branch.            
    let Commit: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                      Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                      Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherBranchIdOrBranchNameRequired
                      Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                      String.isValidGraceName parameters.BranchName InvalidBranchName
                      String.isNotEmpty parameters.Message MessageIsRequired
                      String.maxLength parameters.Message 2048 StringIsTooLong
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                      Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist
                      Branch.branchAllowsReferenceType parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName ReferenceType.Commit context CommitIsDisabled ]

                let command (parameters: CreateReferenceParameters) = BranchCommand.Commit(parameters.DirectoryId, parameters.Sha256Hash, ReferenceText parameters.Message) |> returnTask

                return! processCommand context validations command
            }
            
    /// Creates a checkpoint reference pointing to the current root directory version in the branch.
    let Checkpoint: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                      Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                      Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                      Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                      String.isValidGraceName parameters.BranchName InvalidBranchName
                      Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                      String.maxLength parameters.Message 2048 StringIsTooLong
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                      Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist
                      Branch.branchAllowsReferenceType parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName ReferenceType.Checkpoint context CheckpointIsDisabled ]

                let command (parameters: CreateReferenceParameters) = BranchCommand.Checkpoint(parameters.DirectoryId, parameters.Sha256Hash, ReferenceText parameters.Message) |> returnTask

                return! processCommand context validations command
            }

    /// Creates a save reference pointing to the current root directory version in the branch.        
    let Save: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                      Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                      Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                      Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                      String.isValidGraceName parameters.BranchName InvalidBranchName
                      Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                      String.maxLength parameters.Message 4096 StringIsTooLong
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                      Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist
                      Branch.branchAllowsReferenceType parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName ReferenceType.Save context SaveIsDisabled ]

                let command (parameters: CreateReferenceParameters) = BranchCommand.Save(parameters.DirectoryId, parameters.Sha256Hash, ReferenceText parameters.Message) |> returnTask

                return! processCommand context validations command
            }

    /// Creates a tag reference pointing to the specified root directory version in the branch.        
    let Tag: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateReferenceParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                      Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                      Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                      Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                      String.isValidGraceName parameters.BranchName InvalidBranchName
                      Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                      String.maxLength parameters.Message 2048 StringIsTooLong
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                      Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist
                      Branch.branchAllowsReferenceType parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName ReferenceType.Tag context TagIsDisabled ]

                let command (parameters: CreateReferenceParameters) = BranchCommand.Tag(parameters.DirectoryId, parameters.Sha256Hash, ReferenceText parameters.Message) |> returnTask

                return! processCommand context validations command
            }

    /// Enables and disables promotion references in the provided branch.
    let EnablePromotion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                      Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                      Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                      Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                      String.isValidGraceName parameters.BranchName InvalidBranchName
                      Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                      Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]

                let command (parameters: EnableFeatureParameters) = EnablePromotion(parameters.Enabled) |> returnTask

                return! processCommand context validations command
            }
            
    /// Enables and disables commit references in the provided branch.
    let EnableCommit: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                      Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                      Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                      Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                      String.isValidGraceName parameters.BranchName InvalidBranchName
                      Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                      Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]

                let command (parameters: EnableFeatureParameters) = EnableCommit(parameters.Enabled) |> returnTask

                return! processCommand context validations command
            }

    /// Enables and disables checkpoint references in the provided branch.
    let EnableCheckpoint: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                      Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                      Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                      Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                      String.isValidGraceName parameters.BranchName InvalidBranchName
                      Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                      Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]

                let command (parameters: EnableFeatureParameters) = EnableCheckpoint(parameters.Enabled) |> returnTask

                return! processCommand context validations command
            }

    /// Enables and disables save references in the provided branch.
    let EnableSave: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                      Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                      Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                      Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                      String.isValidGraceName parameters.BranchName InvalidBranchName
                      Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                      Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist ]

                let command (parameters: EnableFeatureParameters) = EnableSave(parameters.Enabled) |> returnTask

                return! processCommand context validations command
            }

    /// Enables and disables tag references in the provided branch.
    let EnableTag: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: EnableFeatureParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                      Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                      Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                      Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                      String.isValidGraceName parameters.BranchName InvalidBranchName
                      Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                      Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]

                let command (parameters: EnableFeatureParameters) = EnableTag(parameters.Enabled) |> returnTask

                return! processCommand context validations command
            }

    /// Deletes the provided branch.
    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DeleteBranchParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                      Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                      Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                      Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                      String.isValidGraceName parameters.BranchName InvalidBranchName
                      Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                      Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]

                let command (parameters: DeleteBranchParameters) = DeleteLogical |> returnTask

                return! processCommand context validations command
            }
            
    /// Gets details about the provided branch.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetBranchParameters) (context: HttpContext) =
                        [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          String.isValidGraceName parameters.OwnerName InvalidOwnerName
                          Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                          Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                          Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                          Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                          String.isValidGraceName parameters.BranchName InvalidBranchName
                          Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                          Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                          Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                          Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                          Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get()
                            return branchDto
                        }

                    let! parameters = context |> parse<GetBranchParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets details about the parent branch of the provided branch.
    let GetParentBranch: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: BranchParameters) (context: HttpContext) =
                        [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          String.isValidGraceName parameters.OwnerName InvalidOwnerName
                          Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                          Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                          Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                          Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                          String.isValidGraceName parameters.BranchName InvalidBranchName
                          Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                          Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                          Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                          Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                          Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context ParentBranchDoesNotExist ]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! parentBranchDto = actorProxy.GetParentBranch()
                            return parentBranchDto
                        }

                    let! parameters = context |> parse<BranchParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets details about the reference with the provided ReferenceId.
    let GetReference: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferenceParameters) (context: HttpContext) =
                        [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          String.isValidGraceName parameters.OwnerName InvalidOwnerName
                          Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                          Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                          Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                          Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                          String.isValidGraceName parameters.BranchName InvalidBranchName
                          Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                          Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                          Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                          Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                          Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]

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
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets details about multiple references in one API call.
    let GetReferences: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferencesParameters) (context: HttpContext) =
                        [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          String.isValidGraceName parameters.OwnerName InvalidOwnerName
                          Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                          Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                          Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                          Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                          String.isValidGraceName parameters.BranchName InvalidBranchName
                          Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                          Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                          Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                          Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                          Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                          Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get()
                            let! results = getReferences branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    return! processQuery context parameters validations (parameters.MaxCount) query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Retrieves the diffs between references in a branch by ReferenceType.
    let GetDiffsForReferenceType: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetDiffsForReferenceTypeParameters) (context: HttpContext) =
                        [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          String.isValidGraceName parameters.OwnerName InvalidOwnerName
                          Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                          Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                          Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                          Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                          String.isValidGraceName parameters.BranchName InvalidBranchName
                          Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                          Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                          String.isNotEmpty parameters.ReferenceType ReferenceTypeMustBeProvided
                          DiscriminatedUnion.isMemberOf<ReferenceType, BranchError> parameters.ReferenceType InvalidReferenceType
                          Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                          Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                          Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                          Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]

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
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets the promotions in a branch.
    let GetPromotions: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferencesParameters) (context: HttpContext) =
                        [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          String.isValidGraceName parameters.OwnerName InvalidOwnerName
                          Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                          Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                          Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                          Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                          String.isValidGraceName parameters.BranchName InvalidBranchName
                          Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                          Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                          Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                          Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                          Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                          Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get()
                            let! results = getPromotions branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    return! processQuery context parameters validations (parameters.MaxCount) query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets the commits in a branch.
    let GetCommits: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferencesParameters) (context: HttpContext) =
                        [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          String.isValidGraceName parameters.OwnerName InvalidOwnerName
                          Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                          Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                          Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                          Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                          String.isValidGraceName parameters.BranchName InvalidBranchName
                          Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                          Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                          Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                          Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                          Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                          Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get()
                            let! results = getCommits branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    return! processQuery context parameters validations (parameters.MaxCount) query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets the checkpoints in a branch.
    let GetCheckpoints: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferencesParameters) (context: HttpContext) =
                        [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          String.isValidGraceName parameters.OwnerName InvalidOwnerName
                          Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                          Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                          Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                          Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                          String.isValidGraceName parameters.BranchName InvalidBranchName
                          Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                          Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                          Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                          Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                          Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                          Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get()
                            let! results = getCheckpoints branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    return! processQuery context parameters validations (parameters.MaxCount) query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets the saves in a branch.
    let GetSaves: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferencesParameters) (context: HttpContext) =
                        [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          String.isValidGraceName parameters.OwnerName InvalidOwnerName
                          Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                          Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                          Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                          Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                          String.isValidGraceName parameters.BranchName InvalidBranchName
                          Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                          Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                          Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                          Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                          Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                          Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]


                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get()
                            let! results = getSaves branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    return! processQuery context parameters validations (parameters.MaxCount) query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets the tags in a branch.
    let GetTags: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferencesParameters) (context: HttpContext) =
                        [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          String.isValidGraceName parameters.OwnerName InvalidOwnerName
                          Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                          Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                          Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                          Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                          String.isValidGraceName parameters.BranchName InvalidBranchName
                          Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                          Number.isPositiveOrZero parameters.MaxCount ValueMustBePositive
                          Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                          Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                          Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                          Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get()
                            let! results = getTags branchDto.BranchId maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    return! processQuery context parameters validations (parameters.MaxCount) query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    let GetVersion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetBranchVersionParameters) (context: HttpContext) =
                        [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          String.isValidGraceName parameters.OwnerName InvalidOwnerName
                          Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                          Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                          Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameIsRequired
                          Guid.isValidAndNotEmpty parameters.BranchId InvalidBranchId
                          String.isValidGraceName parameters.BranchName InvalidBranchName
                          Input.eitherIdOrNameMustBeProvided parameters.BranchId parameters.BranchName EitherBranchIdOrBranchNameRequired
                          String.isEmptyOrValidSha256Hash parameters.Sha256Hash Sha256HashDoesNotExist
                          Guid.isValidAndNotEmpty parameters.ReferenceId ReferenceIdDoesNotExist
                          Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                          Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist
                          Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                          Branch.branchExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName parameters.BranchId parameters.BranchName context BranchDoesNotExist ]

                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let parameters = context.Items["GetVersionParameters"] :?> GetBranchVersionParameters
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
                                let directoryVersionActorId = ActorId($"{rootDirectoryVersion.DirectoryId}")
                                let directoryVersionActorProxy = ApplicationContext.ActorProxyFactory().CreateActorProxy<IDirectoryVersionActor>(directoryVersionActorId, ActorName.DirectoryVersion)
                                let! directoryVersions = directoryVersionActorProxy.GetDirectoryVersionsRecursive()
                                let directoryIds = directoryVersions.Select(fun dv -> dv.DirectoryId).ToList()
                                return directoryIds
                            else
                                return List<DirectoryId>()
                        }

                    let! parameters = context |> parse<GetBranchVersionParameters>
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
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }
