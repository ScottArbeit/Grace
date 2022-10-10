namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open Giraffe
open Grace.Actors.Commands.Organization
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Parameters.Organization
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors.Organization
open Grace.Shared.Validation.Utilities
open Grace.Shared.Types
open Microsoft.AspNetCore.Http
open System
open System.Threading.Tasks
open System.Diagnostics

module Organization =

    type Validations<'T when 'T :> OrganizationParameters> = 'T -> HttpContext -> Task<Result<unit, OrganizationError>>[]

    let activitySource = new ActivitySource("Organization")

    let getActorProxy (context: HttpContext) (organizationId: string) =
        let actorProxyFactory = context.GetService<IActorProxyFactory>()
        let actorId = ActorId(organizationId)
        actorProxyFactory.CreateActorProxy<IOrganizationActor>(actorId, ActorName.Organization)

    let processCommand<'T when 'T :> OrganizationParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> Task<OrganizationCommand>) =
        task {
            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                let validationResults = validations parameters context
                let! validationsPassed = validationResults |> areValid
                if validationsPassed then
                    let! organizationId = resolveOrganizationId parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName
                    match organizationId with
                    | Some organizationId ->
                        let actorProxy = getActorProxy context organizationId
                        let! cmd = command parameters
                        let! result = actorProxy.Handle cmd (Services.createMetadata context)
                        match result with
                            | Ok graceReturn -> return! context |> result200Ok graceReturn 
                            | Error graceError -> return! context |> result400BadRequest graceError
                    | None -> return! context |> result400BadRequest (GraceError.Create (OrganizationError.getErrorMessage OrganizationDoesNotExist) (context.Items[Constants.CorrelationId] :?> string))
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (OrganizationError.getErrorMessage error) (context.Items[Constants.CorrelationId] :?> string)
                    graceError.Properties.Add("Path", context.Request.Path)
                    return! context |> result400BadRequest graceError
            with ex ->
                return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
        }
    
    let processQuery<'T, 'U when 'T :> OrganizationParameters> (context: HttpContext) (parameters: 'T) (validations: Validations<'T>) (maxCount: int) (query: QueryResult<IOrganizationActor, 'U>) =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            try
                let validationResults = validations parameters context
                let! validationsPassed = validationResults |> areValid
                if validationsPassed then
                    let actorProxy = getActorProxy context parameters.OrganizationId
                    let! exists = actorProxy.Exists()
                    if exists then
                        let! queryResult = query context maxCount actorProxy
                        return! context |> result200Ok (GraceReturnValue.Create queryResult (context.Items[Constants.CorrelationId] :?> string))
                    else
                        return! context |> result400BadRequest (GraceError.Create (OrganizationError.getErrorMessage OrganizationIdDoesNotExist) (context.Items[Constants.CorrelationId] :?> string))
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (OrganizationError.getErrorMessage error) (context.Items[Constants.CorrelationId] :?> string)
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
                       stringIsNotEmpty parameters.OwnerName OwnerIdIsRequired |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired |> returnTask
                       stringIsNotEmpty parameters.OrganizationId OrganizationIdIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsNotEmpty parameters.OrganizationName OrganizationNameIsRequired |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       organizationIdDoesNotExist parameters.OrganizationId context OrganizationAlreadyExists |]

                let command (parameters: CreateParameters) = 
                    task {
                        let! ownerId = resolveOwnerId parameters.OwnerId parameters.OwnerName
                        let ownerIdGuid = Guid.Parse(ownerId.Value)
                        let organizationIdGuid = Guid.Parse(parameters.OrganizationId)
                        return OrganizationCommand.Create (organizationIdGuid, OrganizationName parameters.OrganizationName, ownerIdGuid)
                    }
                    
                return! processCommand context validations command
            }

    let SetName: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: NameParameters) (context: HttpContext) =
                    [| stringIsNotEmpty parameters.OrganizationId OrganizationIdIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       stringIsNotEmpty parameters.NewName OrganizationNameIsRequired |> returnTask
                       stringIsValidGraceName parameters.NewName InvalidOrganizationName |> returnTask
                       organizationIdExists parameters.OrganizationId context OrganizationIdDoesNotExist
                       organizationIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationIsDeleted |]

                let command (parameters: NameParameters) = 
                    OrganizationCommand.SetName (OrganizationName parameters.NewName) |> returnTask

                return! processCommand context validations command
            }

    let SetType: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: TypeParameters) (context: HttpContext) =
                    [| stringIsNotEmpty parameters.OrganizationId OrganizationIdIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       stringIsNotEmpty parameters.OrganizationType OrganizationTypeIsRequired |> returnTask
                       isMemberOfDiscriminatedUnion<OrganizationType, OrganizationError> parameters.OrganizationType InvalidOrganizationType |> returnTask
                       organizationIdExists parameters.OrganizationId context OrganizationIdDoesNotExist
                       organizationIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationIsDeleted |]

                let command (parameters: TypeParameters) = OrganizationCommand.SetType (Utilities.discriminatedUnionFromString<OrganizationType>(parameters.OrganizationType).Value) |> returnTask
                
                return! processCommand context validations command
            }

    let SetSearchVisibility: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SearchVisibilityParameters) (context: HttpContext) =
                    [| stringIsNotEmpty parameters.OrganizationId OrganizationIdIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       stringIsNotEmpty parameters.SearchVisibility SearchVisibilityIsRequired |> returnTask
                       isMemberOfDiscriminatedUnion<SearchVisibility, OrganizationError> parameters.SearchVisibility InvalidSearchVisibility |> returnTask
                       organizationIdExists parameters.OrganizationId context OrganizationIdDoesNotExist
                       organizationIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationIsDeleted |]

                let command (parameters: SearchVisibilityParameters) =
                    OrganizationCommand.SetSearchVisibility (Utilities.discriminatedUnionFromString<SearchVisibility>(parameters.SearchVisibility).Value) |> returnTask
                
                return! processCommand context validations command
            }
            
    let SetDescription: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DescriptionParameters) (context: HttpContext) =
                    [| stringIsNotEmpty parameters.OrganizationId OrganizationIdIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsNotEmpty parameters.Description OrganizationDescriptionIsRequired |> returnTask
                       organizationIdExists parameters.OrganizationId context OrganizationIdDoesNotExist
                       organizationIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationIsDeleted |]

                let command (parameters: DescriptionParameters) = OrganizationCommand.SetDescription(parameters.Description) |> returnTask

                return! processCommand context validations command
            }

    let ListRepositories: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: ListRepositoriesParameters) (context: HttpContext) =
                        [| stringIsNotEmpty parameters.OrganizationId OrganizationIdIsRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                           stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                           organizationIdExists parameters.OrganizationId context OrganizationIdDoesNotExist
                           organizationIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationIsDeleted |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IOrganizationActor) =
                        task {
                            let! repositories = actorProxy.ListRepositories()
                            return! context.WriteJsonAsync(repositories)
                        }

                    let! parameters = context |> parse<ListRepositoriesParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }

    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DeleteParameters) (context: HttpContext) =
                    [| stringIsNotEmpty parameters.OrganizationId OrganizationIdIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       stringIsNotEmpty parameters.DeleteReason DeleteReasonIsRequired |> returnTask
                       organizationIdExists parameters.OrganizationId context OrganizationIdDoesNotExist
                       organizationIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationIsDeleted |]

                let command (parameters: DeleteParameters) = OrganizationCommand.DeleteLogical (parameters.Force, parameters.DeleteReason) |> returnTask

                return! processCommand context validations command
            }

    let Undelete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: OrganizationParameters) (context: HttpContext) =
                    [| stringIsNotEmpty parameters.OrganizationId OrganizationIdIsRequired |> returnTask
                       guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                       stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                       eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired |> returnTask
                       organizationIdExists parameters.OrganizationId context OrganizationIdDoesNotExist
                       organizationIsDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationIsNotDeleted |]

                let command (parameters: OrganizationParameters) = OrganizationCommand.Undelete |> returnTask

                return! processCommand context validations command
            }

    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetParameters) (context: HttpContext) =
                        [| stringIsNotEmpty parameters.OrganizationId OrganizationIdIsRequired |> returnTask
                           guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId |> returnTask
                           stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName |> returnTask
                           organizationIdExists parameters.OrganizationId context OrganizationIdDoesNotExist
                           organizationIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationIsDeleted |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IOrganizationActor) =
                        task {
                            let! dto = actorProxy.GetDto()
                            return! context.WriteJsonAsync(dto)
                        }

                    let! parameters = context |> parse<GetParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }
