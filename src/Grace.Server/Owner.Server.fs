namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open Giraffe
open Grace.Actors.Commands.Owner
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
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

    /// Generic processor for all Owner commands.
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
                            | Error graceError -> return! context |> result400BadRequest {graceError with Properties = (getPropertiesAsDictionary parameters)}
                    | None -> 
                        return! context |> result400BadRequest (GraceError.CreateWithMetadata (OwnerError.getErrorMessage OwnerDoesNotExist) (getCorrelationId context) (getPropertiesAsDictionary parameters))
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.CreateWithMetadata (OwnerError.getErrorMessage error) (getCorrelationId context) (getPropertiesAsDictionary parameters)
                    graceError.Properties.Add("Path", context.Request.Path)
                    return! context |> result400BadRequest graceError
            with ex ->
                return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
        }

    /// Generic processor for all Owner queries.
    let processQuery<'T, 'U when 'T :> OwnerParameters> (context: HttpContext) (parameters: 'T) (validations: Validations<'T>) (maxCount: int) (query: QueryResult<IOwnerActor, 'U>) =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            try
                let validationResults = validations parameters context
                let! validationsPassed = validationResults |> allPass
                if validationsPassed then
                    match! resolveOwnerId parameters.OwnerId parameters.OwnerName with
                    | Some ownerId ->
                        context.Items.Add(nameof(OwnerId), OwnerId ownerId)
                        let actorProxy = getActorProxy context ownerId
                        let! queryResult = query context maxCount actorProxy
                        return! context |> result200Ok (GraceReturnValue.Create queryResult (getCorrelationId context))
                    | None -> 
                        return! context |> result400BadRequest (GraceError.Create (OwnerError.getErrorMessage OwnerDoesNotExist) (getCorrelationId context))
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (OwnerError.getErrorMessage error) (getCorrelationId context)
                    graceError.Properties.Add("Path", context.Request.Path)
                    return! context |> result400BadRequest graceError
            with ex ->
                return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
        }

    /// Create an owner.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateOwnerParameters) (context: HttpContext) =
                    [ String.isNotEmpty parameters.OwnerId OwnerIdIsRequired
                      Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isNotEmpty parameters.OwnerName OwnerNameIsRequired
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Owner.ownerIdDoesNotExist parameters.OwnerId OwnerIdAlreadyExists
                      Owner.ownerNameDoesNotExist parameters.OwnerName OwnerNameAlreadyExists ]

                let command (parameters: CreateOwnerParameters) = 
                    let ownerIdGuid = Guid.Parse(parameters.OwnerId)
                    OwnerCommand.Create (ownerIdGuid, OwnerName parameters.OwnerName) |> returnTask

                return! processCommand context validations command
            }

    /// Set the name of an owner.
    let SetName: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOwnerNameParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      String.isNotEmpty parameters.NewName OwnerNameIsRequired
                      String.isValidGraceName parameters.NewName InvalidOwnerName
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName OwnerDoesNotExist
                      Owner.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName OwnerIsDeleted
                      Owner.ownerNameDoesNotExist parameters.NewName OwnerNameAlreadyExists ]

                let command (parameters: SetOwnerNameParameters) = 
                    OwnerCommand.SetName (OwnerName parameters.NewName) |> returnTask

                return! processCommand context validations command
            }

    /// Set the owner type (Public, Private).
    let SetType: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOwnerTypeParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      String.isNotEmpty parameters.OwnerType OwnerTypeIsRequired
                      DiscriminatedUnion.isMemberOf<OwnerType, OwnerError> parameters.OwnerType InvalidOwnerType
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName OwnerDoesNotExist
                      Owner.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName OwnerIsDeleted ]

                let command (parameters: SetOwnerTypeParameters) = OwnerCommand.SetType (Utilities.discriminatedUnionFromString<OwnerType>(parameters.OwnerType).Value) |> returnTask

                return! processCommand context validations command
            }

    /// Set the owner search visibility (Visible, NotVisible).
    let SetSearchVisibility: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOwnerSearchVisibilityParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      DiscriminatedUnion.isMemberOf<SearchVisibility, OwnerError> parameters.SearchVisibility InvalidSearchVisibility
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName OwnerDoesNotExist
                      Owner.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName OwnerIsDeleted ]

                let command (parameters: SetOwnerSearchVisibilityParameters) = OwnerCommand.SetSearchVisibility (Utilities.discriminatedUnionFromString<SearchVisibility>(parameters.SearchVisibility).Value) |> returnTask

                return! processCommand context validations command
            }

    /// Set the owner's description.
    let SetDescription: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOwnerDescriptionParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      String.isNotEmpty parameters.Description DescriptionIsRequired
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName OwnerDoesNotExist
                      Owner.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName OwnerIsDeleted ]

                let command (parameters: SetOwnerDescriptionParameters) = OwnerCommand.SetDescription (parameters.Description) |> returnTask

                return! processCommand context validations command
            }

    /// List the organizations for an owner.
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
                    return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Delete an owner.
    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DeleteOwnerParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      String.isNotEmpty parameters.DeleteReason DeleteReasonIsRequired
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName OwnerDoesNotExist
                      Owner.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName OwnerIsDeleted ]

                let command (parameters: DeleteOwnerParameters) = OwnerCommand.DeleteLogical (parameters.Force, parameters.DeleteReason) |> returnTask

                return! processCommand context validations command
            }
            
    /// Undelete a previously-deleted owner.
    let Undelete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: OwnerParameters) (context: HttpContext) =
                    [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      String.isValidGraceName parameters.OwnerName InvalidOwnerName
                      Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      Owner.ownerExists parameters.OwnerId parameters.OwnerName OwnerDoesNotExist
                      Owner.ownerIsDeleted parameters.OwnerId parameters.OwnerName OwnerIsNotDeleted ]

                let command (parameters: OwnerParameters) = OwnerCommand.Undelete |> returnTask

                return! processCommand context validations command
            }
            
    /// Get an owner.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetOwnerParameters) (context: HttpContext) =
                        [ Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          String.isValidGraceName parameters.OwnerName InvalidOwnerName
                          Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          Owner.ownerExists parameters.OwnerId parameters.OwnerName OwnerDoesNotExist
                          Owner.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName OwnerIsDeleted ]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IOwnerActor) =
                        task {
                            return! actorProxy.Get()
                        }

                    let! parameters = context |> parse<GetOwnerParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
            }
