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
open Grace.Shared.Constants
open Grace.Shared.Parameters.Owner
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Utilities
open Grace.Shared.Validation.Errors.Owner
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Azure.Cosmos
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Threading.Tasks

module Owner =

    type Validations<'T when 'T :> OwnerParameters> = 'T -> ValueTask<Result<unit, OwnerError>> array

    let log = ApplicationContext.loggerFactory.CreateLogger("Owner.Server")

    let activitySource = new ActivitySource("Owner")

    let actorProxyFactory = ApplicationContext.actorProxyFactory

    let getActorProxy (context: HttpContext) (ownerId: string) =
        let actorId = ActorId(ownerId)
        actorProxyFactory.CreateActorProxy<IOwnerActor>(actorId, ActorName.Owner)

    /// Generic processor for all Owner commands.
    let processCommand<'T when 'T :> OwnerParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<OwnerCommand>) =
        task {
            try
                let commandName = context.Items["Command"] :?> string 
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>

                let handleCommand ownerId cmd  =
                    task {
                        let actorProxy = getActorProxy context ownerId
                        match! actorProxy.Handle cmd (createMetadata context) with
                        | Ok graceReturn ->
                            match getGraceIds context with
                            | Some graceIds ->
                                graceReturn.Properties.Add(nameof(OwnerId), graceIds.OwnerId)
                            | None -> ()
                            return! context |> result200Ok graceReturn
                        | Error graceError ->
                            log.LogDebug("{currentInstant}: In Branch.Server.handleCommand: error from actorProxy.Handle: {error}", getCurrentInstantExtended(), (graceError.ToString()))
                            return! context |> result400BadRequest {graceError with Properties = getPropertiesAsDictionary parameters}
                    }

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass
                log.LogDebug("{currentInstant}: In Owner.Server.processCommand: validationsPassed: {validationsPassed}.", getCurrentInstantExtended(), validationsPassed)

                if validationsPassed then
                    let! cmd = command parameters
                    let! ownerId = resolveOwnerId parameters.OwnerId parameters.OwnerName (getCorrelationId context)

                    match ownerId, commandName = nameof(Create) with
                    | Some ownerId, _ ->
                        // If ownerId is Some, then we have a valid ownerId.
                        if String.IsNullOrEmpty(parameters.OwnerId) then parameters.OwnerId<- ownerId
                        return! handleCommand ownerId cmd
                    | None, true ->
                        // If it's None, but this is a Create command, still valid, just use the ownerId from the parameters.
                        return! handleCommand parameters.OwnerId cmd
                    | None, false ->
                        // If it's None, and this is not a Create command, then we have a bad request.
                        log.Log(LogLevel.Information, "{currentInstant}: Error: OwnerNotFound; OwnerId: {ownerId}; OwnerName: {ownerName}; command {commandName}.", getCurrentInstantExtended(), parameters.OwnerId, parameters.OwnerName, commandName)
                        return! context |> result400BadRequest (GraceError.CreateWithMetadata (OwnerError.getErrorMessage OwnerDoesNotExist) (getCorrelationId context) (getPropertiesAsDictionary parameters))
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = OwnerError.getErrorMessage error
                    log.LogDebug("{currentInstant}: error: {error}", getCurrentInstantExtended(), errorMessage)
                    let graceError = GraceError.CreateWithMetadata errorMessage (getCorrelationId context) (getPropertiesAsDictionary parameters)
                    graceError.Properties.Add("Path", context.Request.Path)
                    graceError.Properties.Add("Error", errorMessage)
                    return! context |> result400BadRequest graceError
            with ex ->
                log.LogError(ex, "{currentInstant}: Exception in Organization.Server.processCommand. CorrelationId: {correlationId}.", getCurrentInstantExtended(), (getCorrelationId context))
                return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
        }

    /// Generic processor for all Owner queries.
    let processQuery<'T, 'U when 'T :> OwnerParameters> (context: HttpContext) (parameters: 'T) (validations: Validations<'T>) (maxCount: int) (query: QueryResult<IOwnerActor, 'U>) =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            try
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass
                if validationsPassed then
                    match! resolveOwnerId parameters.OwnerId parameters.OwnerName (getCorrelationId context) with
                    | Some ownerId ->
                        let actorProxy = getActorProxy context ownerId
                        let! queryResult = query context maxCount actorProxy
                        let graceReturnValue = GraceReturnValue.Create queryResult (getCorrelationId context)
                        match getGraceIds context with
                        | Some graceIds ->
                            graceReturnValue.Properties.Add(nameof(OwnerId), graceIds.OwnerId)
                        | None -> ()
                        return! context |> result200Ok graceReturnValue
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
                let validations (parameters: CreateOwnerParameters) =
                    [| String.isNotEmpty parameters.OwnerId OwnerIdIsRequired
                       Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isNotEmpty parameters.OwnerName OwnerNameIsRequired
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Owner.ownerIdDoesNotExist parameters.OwnerId parameters.CorrelationId OwnerIdAlreadyExists
                       Owner.ownerNameDoesNotExist parameters.OwnerName parameters.CorrelationId OwnerNameAlreadyExists |]

                let command (parameters: CreateOwnerParameters) = 
                    let ownerIdGuid = Guid.Parse(parameters.OwnerId)
                    Create (ownerIdGuid, OwnerName parameters.OwnerName) |> returnValueTask

                context.Items.Add("Command", nameof(Create))
                return! processCommand context validations command
            }

    /// Set the name of an owner.
    let SetName: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOwnerNameParameters) =
                    [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                       String.isNotEmpty parameters.NewName OwnerNameIsRequired
                       String.isValidGraceName parameters.NewName InvalidOwnerName
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName parameters.CorrelationId OwnerDoesNotExist
                       Owner.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.CorrelationId OwnerIsDeleted
                       Owner.ownerNameDoesNotExist parameters.NewName parameters.CorrelationId OwnerNameAlreadyExists |]

                let command (parameters: SetOwnerNameParameters) = 
                    SetName (OwnerName parameters.NewName) |> returnValueTask

                context.Items.Add("Command", nameof(SetName))
                return! processCommand context validations command
            }

    /// Set the owner type (Public, Private).
    let SetType: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOwnerTypeParameters) =
                    [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                       String.isNotEmpty parameters.OwnerType OwnerTypeIsRequired
                       DiscriminatedUnion.isMemberOf<OwnerType, OwnerError> parameters.OwnerType InvalidOwnerType
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName parameters.CorrelationId OwnerDoesNotExist
                       Owner.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.CorrelationId OwnerIsDeleted |]

                let command (parameters: SetOwnerTypeParameters) = OwnerCommand.SetType (Utilities.discriminatedUnionFromString<OwnerType>(parameters.OwnerType).Value) |> returnValueTask

                context.Items.Add("Command", nameof(SetType))
                return! processCommand context validations command
            }

    /// Set the owner search visibility (Visible, NotVisible).
    let SetSearchVisibility: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOwnerSearchVisibilityParameters) =
                    [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                       DiscriminatedUnion.isMemberOf<SearchVisibility, OwnerError> parameters.SearchVisibility InvalidSearchVisibility
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName parameters.CorrelationId OwnerDoesNotExist
                       Owner.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.CorrelationId OwnerIsDeleted |]

                let command (parameters: SetOwnerSearchVisibilityParameters) = OwnerCommand.SetSearchVisibility (Utilities.discriminatedUnionFromString<SearchVisibility>(parameters.SearchVisibility).Value) |> returnValueTask

                context.Items.Add("Command", nameof(SetSearchVisibility))
                return! processCommand context validations command
            }

    /// Set the owner's description.
    let SetDescription: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOwnerDescriptionParameters) =
                    [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                       String.isNotEmpty parameters.Description DescriptionIsRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName parameters.CorrelationId OwnerDoesNotExist
                       Owner.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.CorrelationId OwnerIsDeleted |]

                let command (parameters: SetOwnerDescriptionParameters) = OwnerCommand.SetDescription (parameters.Description) |> returnValueTask

                context.Items.Add("Command", nameof(SetDescription))
                return! processCommand context validations command
            }

    /// List the organizations for an owner.
    let ListOrganizations: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: ListOrganizationsParameters) =
                        [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IOwnerActor) =
                        task {
                            let! organizations = actorProxy.ListOrganizations (getCorrelationId context)
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
                let validations (parameters: DeleteOwnerParameters) =
                    [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                       String.isNotEmpty parameters.DeleteReason DeleteReasonIsRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName parameters.CorrelationId OwnerDoesNotExist
                       Owner.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.CorrelationId OwnerIsDeleted |]

                let command (parameters: DeleteOwnerParameters) = OwnerCommand.DeleteLogical (parameters.Force, parameters.DeleteReason) |> returnValueTask

                context.Items.Add("Command", nameof(Delete))
                return! processCommand context validations command
            }
            
    /// Undelete a previously-deleted owner.
    let Undelete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: OwnerParameters) =
                    [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName parameters.CorrelationId OwnerDoesNotExist
                       Owner.ownerIsDeleted parameters.OwnerId parameters.OwnerName parameters.CorrelationId OwnerIsNotDeleted |]

                let command (parameters: OwnerParameters) = OwnerCommand.Undelete |> returnValueTask

                context.Items.Add("Command", nameof(Undelete))
                return! processCommand context validations command
            }
            
    /// Get an owner.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetOwnerParameters) =
                        [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                           String.isValidGraceName parameters.OwnerName InvalidOwnerName
                           Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName parameters.CorrelationId OwnerDoesNotExist
                           Owner.ownerIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.CorrelationId OwnerIsDeleted |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IOwnerActor) =
                        task {
                            return! actorProxy.Get (getCorrelationId context)
                        }

                    let! parameters = context |> parse<GetOwnerParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
            }
