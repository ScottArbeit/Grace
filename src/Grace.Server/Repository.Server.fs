namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open FSharpPlus
open Giraffe
open Grace.Actors
open Grace.Actors.Commands.Branch
open Grace.Actors.Commands.Repository
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Parameters.Repository
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors.Repository
open Grace.Shared.Validation
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open NodaTime
open OpenTelemetry.Trace
open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.Linq
open System.Threading.Tasks
open System.Text
open System.Text.Json
open Repository
open FSharpPlus.Data
open Giraffe.ViewEngine.HtmlElements

module Repository =

    type Validations<'T when 'T :> RepositoryParameters> = 'T -> HttpContext -> Task<Result<unit, RepositoryError>> array

    let activitySource = new ActivitySource("Repository")

    let actorProxyFactory = ApplicationContext.actorProxyFactory

    let getActorProxy (context: HttpContext) (repositoryId: string) =
        let actorId = ActorId(repositoryId)
        actorProxyFactory.CreateActorProxy<IRepositoryActor>(actorId, ActorName.Repository)

    let commonValidations<'T when 'T :> RepositoryParameters> (parameters: 'T) (context: HttpContext) =
        [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
           String.isValidGraceName parameters.OwnerName InvalidOwnerName
           Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
           Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
           String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
           Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
           Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
           String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
           Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName RepositoryError.EitherRepositoryIdOrRepositoryNameRequired |]

    let processCommand<'T when 'T :> RepositoryParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> Task<RepositoryCommand>) = 
        task {
            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                let validationResults = Array.append (commonValidations parameters context) (validations parameters context)
                let! validationsPassed = validationResults |> allPass
                if validationsPassed then
                    let! repositoryId = resolveRepositoryId parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName
                    match repositoryId with
                    | Some repositoryId ->
                        if String.IsNullOrEmpty(parameters.RepositoryId) then parameters.RepositoryId <- repositoryId
                        let actorProxy = getActorProxy context repositoryId
                        let! cmd = command parameters
                        let! result = actorProxy.Handle cmd (Services.createMetadata context)
                        match result with
                            | Ok graceReturn -> 
                                return! context |> result200Ok graceReturn
                            | Error graceError -> 
                                return! context |> result400BadRequest {graceError with Properties = getPropertiesAsDictionary parameters}
                    | None -> 
                        return! context |> result400BadRequest (GraceError.CreateWithMetadata (RepositoryError.getErrorMessage RepositoryDoesNotExist) (getCorrelationId context) (getPropertiesAsDictionary parameters))
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.CreateWithMetadata (RepositoryError.getErrorMessage error) (getCorrelationId context) (getPropertiesAsDictionary parameters)
                    graceError.Properties.Add("Path", context.Request.Path)
                    return! context |> result400BadRequest graceError
            with ex ->
                return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
        }

    let processQuery<'T, 'U when 'T :> RepositoryParameters> (context: HttpContext) (parameters: 'T) (validations: Validations<'T>) (maxCount: int) (query: QueryResult<IRepositoryActor, 'U>) =
        task {
            try
                use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
                //let! parameters = context |> parse<'T>              
                let validationResults = Array.append (commonValidations parameters context) (validations parameters context)
                //let validationResults = validations parameters context
                let! validationsPassed = validationResults |> allPass
                if validationsPassed then
                    match! resolveRepositoryId parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName with
                    | Some repositoryId ->
                        let actorProxy = getActorProxy context repositoryId
                        let! queryResult = query context maxCount actorProxy
                        return! context |> result200Ok (GraceReturnValue.Create queryResult (getCorrelationId context))
                    | None ->
                        return! context |> result400BadRequest (GraceError.Create (RepositoryError.getErrorMessage RepositoryIdDoesNotExist) (getCorrelationId context))
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (RepositoryError.getErrorMessage error) (getCorrelationId context)
                    graceError.Properties.Add("Path", context.Request.Path)
                    return! context |> result400BadRequest graceError
            with ex ->
                return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context)) 
        }

    /// Create a new repository.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                //let! parameters = context |> parse<CreateParameters>
                //logToConsole $"parameters.ObjectStorageProvider: {parameters.ObjectStorageProvider}"
                let validations (parameters: CreateRepositoryParameters) (context: HttpContext) =
                    [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                       Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                       String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                       Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                       String.isNotEmpty parameters.RepositoryId RepositoryIdIsRequired
                       Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                       String.isNotEmpty parameters.RepositoryName RepositoryNameIsRequired
                       String.isValidGraceName parameters.RepositoryName InvalidRepositoryName
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName OwnerDoesNotExist
                       Organization.organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName OrganizationDoesNotExist |]
                
                let command (parameters: CreateRepositoryParameters) = 
                    task {
                        let! ownerId = resolveOwnerId parameters.OwnerId parameters.OwnerName
                        let! organizationId = resolveOrganizationId ownerId.Value parameters.OwnerName parameters.OrganizationId parameters.OrganizationName
                        return Create (RepositoryName parameters.RepositoryName, (Guid.Parse(parameters.RepositoryId)),
                            (Guid.Parse(ownerId.Value)), (Guid.Parse(organizationId.Value)))
                    }

                return! processCommand context validations command
            }

    /// Sets the search visibility of the repository.
    let SetVisibility: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetRepositoryVisibilityParameters) (context: HttpContext) =
                    [| Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                       Repository.visibilityIsValid parameters.Visibility InvalidVisibilityValue
                       Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryDoesNotExist
                       Repository.repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryIsDeleted |]
  
                let command (parameters: SetRepositoryVisibilityParameters) = 
                    SetVisibility(discriminatedUnionFromString<RepositoryVisibility>(parameters.Visibility).Value) |> returnTask
                
                return! processCommand context validations command
            }

    /// Sets the number of days to keep saves in the repository.
    let SetSaveDays: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) -> 
            task {
                let validations (parameters: SetSaveDaysParameters) (context: HttpContext) =
                    [| Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                       Repository.daysIsValid parameters.SaveDays InvalidSaveDaysValue
                       Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryDoesNotExist
                       Repository.repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryIsDeleted |]
                
                let command (parameters: SetSaveDaysParameters) = SetSaveDays(parameters.SaveDays) |> returnTask
                
                return! processCommand context validations command
            }

    /// Sets the number of days to keep checkpoints in the repository.
    let SetCheckpointDays: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetCheckpointDaysParameters) (context: HttpContext) =
                    [| Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                       Repository.daysIsValid parameters.CheckpointDays InvalidCheckpointDaysValue
                       Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryDoesNotExist
                       Repository.repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryIsDeleted |]
                
                let command (parameters: SetCheckpointDaysParameters) = SetCheckpointDays(parameters.CheckpointDays) |> returnTask
                
                return! processCommand context validations command
            }

    /// Enables single-step promotion for the repository. (Currently unused, the only branching model is single-step.)
    let EnableSingleStepPromotion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) -> 
            task {
                let validations (parameters: EnablePromotionTypeParameters) (context: HttpContext) =
                    [| Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                       Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryDoesNotExist
                       Repository.repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryIsDeleted |]
                
                let command (parameters: EnablePromotionTypeParameters) = EnableSingleStepPromotion (parameters.Enabled) |> returnTask
                
                return! processCommand context validations command
            }

    /// Enables complex promotion for the repository. (Currently unused.)
    let EnableComplexPromotion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) -> 
            task {
                let validations (parameters: EnablePromotionTypeParameters) (context: HttpContext) =
                    [| Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                       Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryDoesNotExist
                       Repository.repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryIsDeleted |]
                
                let command (parameters: EnablePromotionTypeParameters) = EnableComplexPromotion (parameters.Enabled) |> returnTask
                
                return! processCommand context validations command
            }

    /// Sets the status of the repository (Public, Private).
    let SetStatus: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) -> 
            task {
                let validations (parameters: SetRepositoryStatusParameters) (context: HttpContext) =
                    [| Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                       DiscriminatedUnion.isMemberOf<RepositoryStatus, RepositoryError> parameters.Status InvalidRepositoryStatus
                       Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryDoesNotExist
                       Repository.repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryIsDeleted |]
                
                let command (parameters: SetRepositoryStatusParameters) =
                    SetRepositoryStatus(discriminatedUnionFromString<RepositoryStatus>(parameters.Status).Value) |> returnTask
                
                return! processCommand context validations command
            }

    /// Sets the default server API version for the repository.
    let SetDefaultServerApiVersion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) -> 
            task {
                let validations (parameters: SetDefaultServerApiVersionParameters) (context: HttpContext) =
                    [| Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                       String.isNotEmpty parameters.DefaultServerApiVersion InvalidServerApiVersion
                       Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryDoesNotExist
                       Repository.repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryIsDeleted |]
                
                let command (parameters: SetDefaultServerApiVersionParameters) = SetDefaultServerApiVersion(parameters.DefaultServerApiVersion) |> returnTask
                
                return! processCommand context validations command
            }

    /// Sets whether or not to keep saves in the repository.
    let SetRecordSaves: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: RecordSavesParameters) (context: HttpContext) =
                    [| Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                       Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryDoesNotExist
                       Repository.repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryIsDeleted |]
                
                let command (parameters: RecordSavesParameters) = SetRecordSaves (parameters.RecordSaves) |> returnTask
                
                return! processCommand context validations command
            }

    /// Sets the description of the repository.
    let SetDescription: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetRepositoryDescriptionParameters) (context: HttpContext) =
                    [| Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                       String.isNotEmpty parameters.Description DescriptionIsRequired
                       Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryDoesNotExist
                       Repository.repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryIsDeleted |]

                let command (parameters: SetRepositoryDescriptionParameters) = SetDescription (parameters.Description) |> returnTask

                return! processCommand context validations command
            }
            
    /// Deletes the repository.
    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DeleteRepositoryParameters) (context: HttpContext) =
                    [| Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                       String.isNotEmpty parameters.DeleteReason DeleteReasonIsRequired
                       Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryDoesNotExist
                       Repository.repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryIsDeleted |]

                let command (parameters: DeleteRepositoryParameters) = DeleteLogical (parameters.Force, parameters.DeleteReason) |> returnTask

                return! processCommand context validations command
            }

    /// Undeletes a previously-deleted repository.
    let Undelete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: RepositoryParameters) (context: HttpContext) =
                    [| Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                       Repository.repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryDoesNotExist
                       Repository.repositoryIsDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName RepositoryIsNotDeleted |]

                let command (parameters: RepositoryParameters) = Undelete |> returnTask

                return! processCommand context validations command
            }

    /// Checks if a repository exists with the given parameters.
    let Exists: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: RepositoryParameters) (context: HttpContext) =
                        [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                           String.isValidGraceName parameters.OwnerName InvalidOwnerName
                           Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                           Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                           String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                           Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                           Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                           Repository.repositoryIdExists parameters.RepositoryId RepositoryIdDoesNotExist |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            return! actorProxy.Exists()
                        }

                    let! parameters = context |> parse<RepositoryParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
        }

    /// Checks if a repository is empty - that is, just created - or not.
    let IsEmpty: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: RepositoryParameters) (context: HttpContext) =
                        [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                           String.isValidGraceName parameters.OwnerName InvalidOwnerName
                           Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                           Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                           String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                           Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                           Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                           Repository.repositoryIdExists parameters.RepositoryId RepositoryIdDoesNotExist |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            return! actorProxy.IsEmpty()
                        }

                    let! parameters = context |> parse<RepositoryParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
        }

    /// Gets a repository.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: RepositoryParameters) (context: HttpContext) =
                        [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                           String.isValidGraceName parameters.OwnerName InvalidOwnerName
                           Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                           Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                           String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                           Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                           Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                           String.isNotEmpty parameters.RepositoryName RepositoryNameIsRequired
                           Repository.repositoryIdExists parameters.RepositoryId RepositoryIdDoesNotExist |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            return! actorProxy.Get()
                        }

                    let! parameters = context |> parse<RepositoryParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets a repository's branches.
    let GetBranches: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetBranchesParameters) (context: HttpContext) =
                        [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                           String.isValidGraceName parameters.OwnerName InvalidOwnerName
                           Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                           Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                           String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                           Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                           Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                           Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                           Number.isWithinRange parameters.MaxCount 1 1000 InvalidMaxCountValue
                           Repository.repositoryIdExists parameters.RepositoryId RepositoryIdDoesNotExist |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            let repositoryId = RepositoryId(context.Items[nameof(RepositoryId)] :?> String)
                            let includeDeleted = context.Items["IncludeDeleted"] :?> bool
                            return! getBranches repositoryId maxCount includeDeleted
                        }

                    let! parameters = context |> parse<GetBranchesParameters>
                    context.Items.Add("IncludeDeleted", parameters.IncludeDeleted)
                    return! processQuery context parameters validations 1000 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets a list of references, given a list of reference IDs.
    let GetReferencesByReferenceId: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferencesByReferenceIdParameters) (context: HttpContext) =
                        [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                           String.isValidGraceName parameters.OwnerName InvalidOwnerName
                           Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                           Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                           String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                           Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                           Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                           Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                           Input.listIsNonEmpty parameters.ReferenceIds ReferenceIdsAreRequired
                           Number.isWithinRange parameters.MaxCount 1 1000 InvalidMaxCountValue
                           Repository.repositoryIdExists parameters.RepositoryId RepositoryIdDoesNotExist |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            let referenceIds = context.Items["ReferenceIds"] :?> IEnumerable<ReferenceId>
                            return! getReferencesByReferenceId referenceIds maxCount
                        }

                    let! parameters = context |> parse<GetReferencesByReferenceIdParameters>
                    context.Items.Add("ReferenceIds", parameters.ReferenceIds)
                    return! processQuery context parameters validations parameters.MaxCount query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Gets a list of branches, given a list of branch IDs.
    let GetBranchesByBranchId: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetBranchesByBranchIdParameters) (context: HttpContext) =
                        [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                           String.isValidGraceName parameters.OwnerName InvalidOwnerName
                           Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                           Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                           String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                           Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                           Guid.isValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                           Input.eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                           Input.listIsNonEmpty parameters.BranchIds BranchIdsAreRequired
                           Number.isWithinRange parameters.MaxCount 1 1000 InvalidMaxCountValue
                           Repository.repositoryIdExists parameters.RepositoryId RepositoryIdDoesNotExist |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            let repositoryId = context.Items[nameof(RepositoryId)] :?> RepositoryId
                            let branchIds = context.Items["BranchIds"] :?> IEnumerable<ReferenceId>
                            let includeDeleted = context.Items["IncludeDeleted"] :?> bool
                            return! getBranchesByBranchId repositoryId branchIds maxCount includeDeleted
                        }

                    let! parameters = context |> parse<GetBranchesByBranchIdParameters>
                    let branchIdList = parameters.BranchIds.ToList()    // We need .Count below, so may as well materialize it once here.
                    let branchIds = branchIdList.Aggregate(StringBuilder(), (fun state branchId -> 
                        state.Append($"{branchId}, ")))
                    context.Items.Add("BranchIds", (branchIds.ToString())[0..-2])
                    context.Items.Add("IncludeDeleted", parameters.IncludeDeleted)
                    return! processQuery context parameters validations (branchIdList.Count) query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }
