namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open Giraffe
open Grace.Actors.Commands.Organization
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
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
open Microsoft.Extensions.Logging
open System
open System.Threading.Tasks
open System.Diagnostics
open Grace.Shared.Services

module Organization =

    type Validations<'T when 'T :> OrganizationParameters> = 'T -> ValueTask<Result<unit, OrganizationError>> array

    let activitySource = new ActivitySource("Organization")

    let log = ApplicationContext.loggerFactory.CreateLogger("Organization.Server")

    let actorProxyFactory = ApplicationContext.actorProxyFactory

    let getActorProxy (context: HttpContext) (organizationId: string) =
        let actorId = ActorId(organizationId)
        actorProxyFactory.CreateActorProxy<IOrganizationActor>(actorId, ActorName.Organization)

    let processCommand<'T when 'T :> OrganizationParameters>
        (context: HttpContext)
        (validations: Validations<'T>)
        (command: 'T -> ValueTask<OrganizationCommand>)
        =
        task {
            try
                let commandName = context.Items["Command"] :?> string
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>

                let handleCommand organizationId cmd =
                    task {
                        let actorProxy = getActorProxy context organizationId

                        match! actorProxy.Handle cmd (createMetadata context) with
                        | Ok graceReturnValue ->
                            match getGraceIds context with
                            | Some graceIds ->
                                graceReturnValue.Properties[nameof (OwnerId)] <- graceIds.OwnerId
                                graceReturnValue.Properties[nameof (OrganizationId)] <- graceIds.OrganizationId
                            | None -> ()

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            log.LogDebug(
                                "{currentInstant}: In Branch.Server.handleCommand: error from actorProxy.Handle: {error}",
                                getCurrentInstantExtended (),
                                (graceError.ToString())
                            )

                            return!
                                context
                                |> result400BadRequest
                                    { graceError with
                                        Properties = getPropertiesAsDictionary parameters }
                    }

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                log.LogDebug(
                    "{currentInstant}: In Organization.Server.processCommand: validationsPassed: {validationsPassed}.",
                    getCurrentInstantExtended (),
                    validationsPassed
                )

                if validationsPassed then
                    let! cmd = command parameters

                    let! organizationId =
                        resolveOrganizationId
                            parameters.OwnerId
                            parameters.OwnerName
                            parameters.OrganizationId
                            parameters.OrganizationName
                            parameters.CorrelationId

                    match organizationId, commandName = nameof (Create) with
                    | Some organizationId, _ ->
                        // If Id is Some, then we know we have a valid Id.
                        if String.IsNullOrEmpty(parameters.OrganizationId) then
                            parameters.OrganizationId <- organizationId

                        return! handleCommand organizationId cmd
                    | None, true ->
                        // If it's None, but this is a Create command, still valid, just use the Id from the parameters.
                        return! handleCommand parameters.OrganizationId cmd
                    | None, false ->
                        // If it's None, and this is not a Create command, then we have a bad request.
                        log.LogDebug(
                            "{currentInstant}: In Organization.Server.processCommand: resolveOrganizationId failed. Organization does not exist. organizationId: {organizationId}; organizationName: {organizationName}.",
                            getCurrentInstantExtended (),
                            parameters.OrganizationId,
                            parameters.OrganizationName
                        )

                        return!
                            context
                            |> result400BadRequest (
                                GraceError.CreateWithMetadata
                                    (OrganizationError.getErrorMessage OrganizationDoesNotExist)
                                    (getCorrelationId context)
                                    (getPropertiesAsDictionary parameters)
                            )
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = OrganizationError.getErrorMessage error
                    log.LogDebug("{currentInstant}: error: {error}", getCurrentInstantExtended (), errorMessage)

                    let graceError =
                        GraceError.CreateWithMetadata errorMessage (getCorrelationId context) (getPropertiesAsDictionary parameters)

                    graceError.Properties.Add("Path", context.Request.Path)
                    graceError.Properties.Add("Error", errorMessage)
                    return! context |> result400BadRequest graceError
            with ex ->
                log.LogError(
                    ex,
                    "{currentInstant}: Exception in Organization.Server.processCommand. CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    (getCorrelationId context)
                )

                return!
                    context
                    |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
        }

    let processQuery<'T, 'U when 'T :> OrganizationParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (maxCount: int)
        (query: QueryResult<IOrganizationActor, 'U>)
        =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)

            try
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    match!
                        resolveOrganizationId
                            parameters.OwnerId
                            parameters.OwnerName
                            parameters.OrganizationId
                            parameters.OrganizationName
                            parameters.CorrelationId
                    with
                    | Some organizationId ->
                        let actorProxy = getActorProxy context organizationId
                        let! queryResult = query context maxCount actorProxy

                        let graceReturnValue =
                            GraceReturnValue.Create queryResult (getCorrelationId context)

                        match getGraceIds context with
                        | Some graceIds ->
                            graceReturnValue.Properties[nameof (OwnerId)] <- graceIds.OwnerId
                            graceReturnValue.Properties[nameof (OrganizationId)] <- graceIds.OrganizationId
                        | None -> ()

                        return! context |> result200Ok graceReturnValue
                    | None ->
                        return!
                            context
                            |> result400BadRequest (GraceError.Create (OrganizationError.getErrorMessage OrganizationDoesNotExist) (getCorrelationId context))
                else
                    let! error = validationResults |> getFirstError

                    let graceError =
                        GraceError.Create (OrganizationError.getErrorMessage error) (getCorrelationId context)

                    graceError.Properties.Add("Path", context.Request.Path)
                    return! context |> result400BadRequest graceError
            with ex ->
                return!
                    context
                    |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
        }

    /// Create an organization.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateOrganizationParameters) =
                    [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                       String.isNotEmpty parameters.OrganizationId OrganizationIdIsRequired
                       Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                       String.isNotEmpty parameters.OrganizationName OrganizationNameIsRequired
                       String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationDoesNotExist
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationIdAlreadyExists
                       Organization.organizationNameIsUnique
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationNameAlreadyExists |]

                let command (parameters: CreateOrganizationParameters) =
                    task {
                        let! ownerId = resolveOwnerId parameters.OwnerId parameters.OwnerName parameters.CorrelationId
                        let ownerIdGuid = Guid.Parse(ownerId.Value)
                        let organizationIdGuid = Guid.Parse(parameters.OrganizationId)
                        return Create(organizationIdGuid, OrganizationName parameters.OrganizationName, ownerIdGuid)
                    }
                    |> ValueTask<OrganizationCommand>

                log.LogDebug("{currentInstant}: In Grace.Server.Create.", getCurrentInstantExtended ())
                context.Items.Add("Command", nameof (Create))
                return! processCommand context validations command
            }

    /// Set the name of an organization.
    let SetName: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOrganizationNameParameters) =
                    [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                       Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                       String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                       Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                       String.isNotEmpty parameters.NewName OrganizationNameIsRequired
                       String.isValidGraceName parameters.NewName InvalidOrganizationName
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Organization.organizationIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationIsDeleted |]

                let command (parameters: SetOrganizationNameParameters) =
                    SetName(OrganizationName parameters.NewName) |> returnValueTask

                context.Items.Add("Command", nameof (SetName))
                return! processCommand context validations command
            }

    /// Set the type of an organization (Public, Private).
    let SetType: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOrganizationTypeParameters) =
                    [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                       Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                       String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                       Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                       DiscriminatedUnion.isMemberOf<OrganizationType, OrganizationError> parameters.OrganizationType InvalidOrganizationType
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Organization.organizationIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationIsDeleted |]

                let command (parameters: SetOrganizationTypeParameters) =
                    SetType(
                        discriminatedUnionFromString<OrganizationType>(parameters.OrganizationType)
                            .Value
                    )
                    |> returnValueTask

                context.Items.Add("Command", nameof (SetType))
                return! processCommand context validations command
            }

    /// Set the search visibility of an organization (Visible, Hidden).
    let SetSearchVisibility: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOrganizationSearchVisibilityParameters) =
                    [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                       Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                       String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                       Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                       String.isNotEmpty parameters.SearchVisibility SearchVisibilityIsRequired
                       DiscriminatedUnion.isMemberOf<SearchVisibility, OrganizationError> parameters.SearchVisibility InvalidSearchVisibility
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Organization.organizationIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationIsDeleted |]

                let command (parameters: SetOrganizationSearchVisibilityParameters) =
                    SetSearchVisibility(
                        discriminatedUnionFromString<SearchVisibility>(parameters.SearchVisibility)
                            .Value
                    )
                    |> returnValueTask

                context.Items.Add("Command", nameof (SetSearchVisibility))
                return! processCommand context validations command
            }

    /// Set the description of an organization.
    let SetDescription: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOrganizationDescriptionParameters) =
                    [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                       Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                       String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                       Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                       String.isNotEmpty parameters.Description OrganizationDescriptionIsRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Organization.organizationIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationIsDeleted |]

                let command (parameters: SetOrganizationDescriptionParameters) =
                    SetDescription(parameters.Description) |> returnValueTask

                context.Items.Add("Command", nameof (SetDescription))
                return! processCommand context validations command
            }

    /// List the repositories of an organization.
    let ListRepositories: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: ListRepositoriesParameters) =
                        [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                           String.isValidGraceName parameters.OwnerName InvalidOwnerName
                           Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                           Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                           String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                           Input.eitherIdOrNameMustBeProvided
                               parameters.OrganizationId
                               parameters.OrganizationName
                               EitherOrganizationIdOrOrganizationNameRequired
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Organization.organizationIsNotDeleted
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationIsDeleted |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IOrganizationActor) =
                        task {
                            let! repositories = actorProxy.ListRepositories(getCorrelationId context)
                            return! context.WriteJsonAsync(repositories)
                        }

                    let! parameters = context |> parse<ListRepositoriesParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }

    /// Delete an organization.
    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DeleteOrganizationParameters) =
                    [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                       Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                       String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                       Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                       String.isNotEmpty parameters.DeleteReason DeleteReasonIsRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Organization.organizationIsNotDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationIsDeleted |]

                let command (parameters: DeleteOrganizationParameters) =
                    DeleteLogical(parameters.Force, parameters.DeleteReason) |> returnValueTask

                context.Items.Add("Command", nameof (DeleteLogical))
                return! processCommand context validations command
            }

    /// Undelete a previous-deleted organization.
    let Undelete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: OrganizationParameters) =
                    [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                       String.isValidGraceName parameters.OwnerName InvalidOwnerName
                       Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                       Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                       String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                       Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                       Input.eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                       Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                       Organization.organizationExists
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationDoesNotExist
                       Organization.organizationIsDeleted
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationIsNotDeleted |]

                let command (parameters: OrganizationParameters) = Undelete |> returnValueTask

                context.Items.Add("Command", nameof (Undelete))
                return! processCommand context validations command
            }

    /// Get an organization.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetOrganizationParameters) =
                        [| Guid.isValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                           String.isValidGraceName parameters.OwnerName InvalidOwnerName
                           Input.eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                           Guid.isValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                           String.isValidGraceName parameters.OrganizationName InvalidOrganizationName
                           Input.eitherIdOrNameMustBeProvided
                               parameters.OrganizationId
                               parameters.OrganizationName
                               EitherOrganizationIdOrOrganizationNameRequired
                           Owner.ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                           Organization.organizationExists
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationDoesNotExist
                           Organization.organizationIsNotDeleted
                               parameters.OwnerId
                               parameters.OwnerName
                               parameters.OrganizationId
                               parameters.OrganizationName
                               parameters.CorrelationId
                               OrganizationIsDeleted |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IOrganizationActor) =
                        task { return! actorProxy.Get(getCorrelationId context) }

                    let! parameters = context |> parse<GetOrganizationParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
            }
