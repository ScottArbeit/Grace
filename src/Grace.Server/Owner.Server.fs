namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open Giraffe
open Grace.Actors.Commands.Owner
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Server.ApplicationContext
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Parameters.Owner
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Utilities
open Grace.Shared.Validation.Errors.Owner
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Azure.Cosmos
open NodaTime
open System
open System.Diagnostics
open System.Linq
open System.Threading.Tasks
open Grace.Shared.Utilities

module Owner =

    type Validations<'T when 'T :> OwnerParameters> = 'T -> HttpContext -> Task<Result<unit, OwnerError>> list

    let activitySource = new ActivitySource("Owner")

    let actorProxyFactory = ApplicationContext.ActorProxyFactory()

    let getActorProxy (context: HttpContext) (ownerId: string) =
        let actorId = ActorId(ownerId)
        actorProxyFactory.CreateActorProxy<IOwnerActor>(actorId, ActorName.Owner)

    let processCommand<'T when 'T :> OwnerParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> Task<OwnerCommand>) =
        task {
            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                let validationResults = validations parameters context
                let! validationsPassed = validationResults |> allPass
                if validationsPassed then
                    let! ownerId = resolveOwnerId parameters.OwnerId parameters.OwnerName
                    match ownerId with
                    | Some ownerId ->
                        let actorProxy = getActorProxy context ownerId
                        let! cmd = command parameters
                        match! actorProxy.Handle cmd (Services.createMetadata context) with
                            | Ok graceReturn -> return! context |> result200Ok graceReturn
                            | Error graceError -> return! context |> result400BadRequest graceError
                    | None -> 
                        return! context |> result400BadRequest (GraceError.Create (OwnerError.getErrorMessage OwnerDoesNotExist) (context.Items[Constants.CorrelationId] :?> string))
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (OwnerError.getErrorMessage error) (context.Items[Constants.CorrelationId] :?> string)
                    graceError.Properties.Add("Path", context.Request.Path)
                    return! context |> result400BadRequest graceError
            with ex ->
                return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
        }

    let processQuery<'T, 'U when 'T :> OwnerParameters> (context: HttpContext) (parameters: 'T) (validations: Validations<'T>) (maxCount: int) (query: QueryResult<IOwnerActor, 'U>) =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            try
                let validationResults = validations parameters context
                let! validationsPassed = validationResults |> allPass
                if validationsPassed then
                    let! ownerId = resolveOwnerId parameters.OwnerId parameters.OwnerName
                    match ownerId with
                    | Some ownerId ->
                        let actorProxy = getActorProxy context ownerId
                        let! exists = actorProxy.Exists()
                        if exists then
                            let! queryResult = query context maxCount actorProxy
                            return! context |> result200Ok (GraceReturnValue.Create queryResult (context.Items[Constants.CorrelationId] :?> string))
                        else
                            return! context |> result400BadRequest (GraceError.Create (OwnerError.getErrorMessage OwnerIdDoesNotExist) (context.Items[Constants.CorrelationId] :?> string))
                    | None -> 
                        return! context |> result400BadRequest (GraceError.Create (OwnerError.getErrorMessage OwnerDoesNotExist) (context.Items[Constants.CorrelationId] :?> string))
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (OwnerError.getErrorMessage error) (context.Items[Constants.CorrelationId] :?> string)
                    graceError.Properties.Add("Path", context.Request.Path)
                    return! context |> result400BadRequest graceError
            with ex ->
                return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
        }

    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateParameters) (context: HttpContext) =
                    [ String.isNotEmpty parameters.OwnerId OwnerIdIsRequired
                      Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isNotEmpty parameters.OwnerName OwnerNameIsRequired
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Domain.ownerIdDoesNotExist parameters.OwnerId context OwnerIdAlreadyExists
                      Domain.ownerNameDoesNotExist parameters.OwnerName context OwnerNameAlreadyExists ]

                let command (parameters: CreateParameters) = 
                    let ownerIdGuid = Guid.Parse(parameters.OwnerId)
                    OwnerCommand.Create (ownerIdGuid, OwnerName parameters.OwnerName) |> returnTask

                return! processCommand context validations command
            }

    let SetName: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetNameParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      String.isNotEmpty parameters.NewName OwnerNameIsRequired
                      String.isValidGraceName parameters.NewName InvalidOwnerName
                      Domain.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Domain.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName context OwnerIsDeleted
                      Domain.ownerNameDoesNotExist parameters.NewName context OwnerNameAlreadyExists ]

                let command (parameters: SetNameParameters) = 
                    OwnerCommand.SetName (OwnerName parameters.NewName) |> returnTask

                return! processCommand context validations command
            }

    let SetType: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetTypeParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      String.isNotEmpty parameters.OwnerType OwnerTypeIsRequired
                      DiscriminatedUnion.isMemberOf<OwnerType, OwnerError> parameters.OwnerType InvalidOwnerType
                      Domain.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Domain.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName context OwnerIsDeleted ]

                let command (parameters: SetTypeParameters) = OwnerCommand.SetType (Utilities.discriminatedUnionFromString<OwnerType>(parameters.OwnerType).Value) |> returnTask

                return! processCommand context validations command
            }

    let SetSearchVisibility: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetSearchVisibilityParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      DiscriminatedUnion.isMemberOf<SearchVisibility, OwnerError> parameters.SearchVisibility InvalidSearchVisibility
                      Domain.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Domain.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName context OwnerIsDeleted ]

                let command (parameters: SetSearchVisibilityParameters) = OwnerCommand.SetSearchVisibility (Utilities.discriminatedUnionFromString<SearchVisibility>(parameters.SearchVisibility).Value) |> returnTask

                return! processCommand context validations command
            }

    let SetDescription: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetDescriptionParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      String.isNotEmpty parameters.Description DescriptionIsRequired
                      Domain.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Domain.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName context OwnerIsDeleted ]

                let command (parameters: SetDescriptionParameters) = OwnerCommand.SetDescription (parameters.Description) |> returnTask

                return! processCommand context validations command
            }

    let ListOrganizations: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: ListOrganizationsParameters) (context: HttpContext) =
                        [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId ]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IOwnerActor) =
                        task {
                            let! organizations = actorProxy.ListOrganizations()
                            return! context.WriteJsonAsync(organizations)
                        }

                    let! parameters = context |> parse<ListOrganizationsParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }

    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DeleteParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      String.isNotEmpty parameters.DeleteReason DeleteReasonIsRequired
                      Domain.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Domain.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName context OwnerIsDeleted ]

                let command (parameters: DeleteParameters) = OwnerCommand.DeleteLogical (parameters.Force, parameters.DeleteReason) |> returnTask

                return! processCommand context validations command
            }
            
    let Undelete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: OwnerParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Domain.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      Domain.ownerIsDeleted parameters.OwnerId parameters.OwnerName context OwnerIsNotDeleted ]

                let command (parameters: OwnerParameters) = OwnerCommand.Undelete |> returnTask

                return! processCommand context validations command
            }
            
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetParameters) (context: HttpContext) =
                        [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId ]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IOwnerActor) =
                        task {
                            let! dto = actorProxy.GetDto()
                            return! context.WriteJsonAsync(dto)
                        }

                    let! parameters = context |> parse<GetParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }
