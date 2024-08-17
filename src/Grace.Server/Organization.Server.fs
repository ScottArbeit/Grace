namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open Giraffe
open Grace.Actors.Commands.Organization
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
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

    let processCommand<'T when 'T :> OrganizationParameters>
        (context: HttpContext)
        (validations: Validations<'T>)
        (command: 'T -> ValueTask<OrganizationCommand>)
        =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context

            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let commandName = context.Items["Command"] :?> string
                let! parameters = context |> parse<'T>

                // We know these Id's from ValidateIds.Middleware, so let's set them so we never have to resolve them again.
                parameters.OwnerId <- graceIds.OwnerId
                parameters.OrganizationId <- graceIds.OrganizationId

                let handleCommand (organizationId: string) cmd =
                    task {
                        let organizationGuid = Guid.Parse(organizationId)
                        let actorProxy = Organization.CreateActorProxy organizationGuid correlationId

                        match! actorProxy.Handle cmd (createMetadata context) with
                        | Ok graceReturnValue ->
                            (graceReturnValue |> addParametersToGraceReturnValue parameters)
                                .enhance(nameof(OwnerId), graceIds.OwnerId)
                                .enhance(nameof(OrganizationId), graceIds.OrganizationId)
                                .enhance("Command", commandName)
                                .enhance("Path", context.Request.Path) |> ignore

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            (graceError |> addParametersToGraceError parameters)
                                .enhance(nameof(OwnerId), graceIds.OwnerId)
                                .enhance(nameof(OrganizationId), graceIds.OrganizationId)
                                .enhance("Command", commandName)
                                .enhance("Path", context.Request.Path) |> ignore

                            log.LogDebug(
                                "{currentInstant}: In Branch.Server.handleCommand: error from actorProxy.Handle: {error}",
                                getCurrentInstantExtended (),
                                (graceError.ToString())
                            )

                            return! context |> result400BadRequest graceError
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
                    return! handleCommand graceIds.OrganizationId cmd
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = OrganizationError.getErrorMessage error
                    log.LogDebug("{currentInstant}: error: {error}", getCurrentInstantExtended (), errorMessage)

                    let graceError =
                        (GraceError.CreateWithMetadata errorMessage (getCorrelationId context) (getParametersAsDictionary parameters))
                            .enhance(nameof(OwnerId), graceIds.OwnerId)
                            .enhance(nameof(OrganizationId), graceIds.OrganizationId)
                            .enhance("Command", commandName)
                            .enhance("Path", context.Request.Path)
                            .enhance("Error", errorMessage)

                    return! context |> result400BadRequest graceError
            with ex ->
                log.LogError(
                    ex,
                    "{currentInstant}: Exception in Organization.Server.processCommand. CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    (getCorrelationId context)
                )

                let graceError =
                    (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
                        .enhance(nameof (OwnerId), graceIds.OwnerId)
                        .enhance(nameof (OrganizationId), graceIds.OrganizationId)
                        .enhance("Path", context.Request.Path)

                return! context |> result500ServerError graceError
        }

    /// Generic processor for all Organization queries.
    let processQuery<'T, 'U when 'T :> OrganizationParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (maxCount: int)
        (query: QueryResult<IOrganizationActor, 'U>)
        =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context

            try
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    // Get the actor proxy for this organization.
                    let organizationGuid = Guid.Parse(graceIds.OrganizationId)
                    let actorProxy = Organization.CreateActorProxy organizationGuid correlationId

                    // Execute the query.
                    let! queryResult = query context maxCount actorProxy

                    // Wrap the result in a GraceReturnValue.
                    let graceReturnValue =
                        (GraceReturnValue.CreateWithMetadata queryResult correlationId (getParametersAsDictionary parameters))
                            .enhance(nameof (OwnerId), graceIds.OwnerId)
                            .enhance(nameof (OrganizationId), graceIds.OrganizationId)
                            .enhance("Path", context.Request.Path)

                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError

                    let graceError =
                        (GraceError.CreateWithMetadata (OrganizationError.getErrorMessage error) correlationId (getParametersAsDictionary parameters))
                            .enhance(nameof (OwnerId), graceIds.OwnerId)
                            .enhance(nameof (OrganizationId), graceIds.OrganizationId)
                            .enhance("Path", context.Request.Path)

                    return! context |> result400BadRequest graceError
            with ex ->
                let graceError =
                    (GraceError.Create $"{createExceptionResponse ex}" correlationId)
                        .enhance(nameof (OwnerId), graceIds.OwnerId)
                        .enhance(nameof (OrganizationId), graceIds.OrganizationId)
                        .enhance("Path", context.Request.Path)

                return! context |> result500ServerError graceError
        }

    /// Create an organization.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateOrganizationParameters) =
                    [| Organization.organizationDoesNotExist
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationId
                           parameters.OrganizationName
                           parameters.CorrelationId
                           OrganizationIdAlreadyExists
                       Organization.organizationNameIsUniqueWithinOwner
                           parameters.OwnerId
                           parameters.OwnerName
                           parameters.OrganizationName
                           context
                           parameters.CorrelationId
                           OrganizationNameAlreadyExists |]

                let command (parameters: CreateOrganizationParameters) =
                    task {
                        let ownerIdGuid = Guid.Parse(parameters.OwnerId)
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
                    [| String.isNotEmpty parameters.NewName OrganizationNameIsRequired
                       String.isValidGraceName parameters.NewName InvalidOrganizationName
                       Organization.organizationIsNotDeleted
                           context
                           parameters.CorrelationId
                           OrganizationIsDeleted |]

                let command (parameters: SetOrganizationNameParameters) = SetName(OrganizationName parameters.NewName) |> returnValueTask

                context.Items.Add("Command", nameof (SetName))
                return! processCommand context validations command
            }

    /// Set the type of an organization (Public, Private).
    let SetType: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOrganizationTypeParameters) =
                    [| DiscriminatedUnion.isMemberOf<OrganizationType, OrganizationError> parameters.OrganizationType InvalidOrganizationType
                       Organization.organizationIsNotDeleted
                           context
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
                    [| String.isNotEmpty parameters.SearchVisibility SearchVisibilityIsRequired
                       DiscriminatedUnion.isMemberOf<SearchVisibility, OrganizationError> parameters.SearchVisibility InvalidSearchVisibility
                       Organization.organizationIsNotDeleted
                           context
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
                    [| String.isNotEmpty parameters.Description DescriptionIsRequired
                       String.maxLength parameters.Description 2048 DescriptionIsTooLong
                       Organization.organizationIsNotDeleted
                           context
                           parameters.CorrelationId
                           OrganizationIsDeleted |]

                let command (parameters: SetOrganizationDescriptionParameters) = SetDescription(parameters.Description) |> returnValueTask

                context.Items.Add("Command", nameof (SetDescription))
                return! processCommand context validations command
            }

    /// List the repositories of an organization.
    let ListRepositories: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: ListRepositoriesParameters) =
                        [| Organization.organizationIsNotDeleted
                               context
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
                    [| String.isNotEmpty parameters.DeleteReason DeleteReasonIsRequired
                       Organization.organizationIsNotDeleted
                           context
                           parameters.CorrelationId
                           OrganizationIsDeleted |]

                let command (parameters: DeleteOrganizationParameters) = DeleteLogical(parameters.Force, parameters.DeleteReason) |> returnValueTask

                context.Items.Add("Command", nameof (DeleteLogical))
                return! processCommand context validations command
            }

    /// Undelete a previous-deleted organization.
    let Undelete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: OrganizationParameters) =
                    [| Organization.organizationIsDeleted
                           context
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
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetOrganizationParameters) =
                        [| Organization.organizationIsNotDeleted
                               context
                               parameters.CorrelationId
                               OrganizationIsDeleted |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IOrganizationActor) =
                        task { return! actorProxy.Get(getCorrelationId context) }

                    let! parameters = context |> parse<GetOrganizationParameters>
                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getPaddedDuration_ms startTime

                    log.LogInformation(
                        "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; OwnerId: {ownerId}; OrganizationId: {organizationId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.OwnerId,
                        graceIds.OrganizationId
                    )

                    return result
                with ex ->
                    let duration_ms = getPaddedDuration_ms startTime
                    let graceError =
                        (GraceError.Create $"{createExceptionResponse ex}" (getCorrelationId context))
                            .enhance(nameof (OwnerId), graceIds.OwnerId)
                            .enhance(nameof (OrganizationId), graceIds.OrganizationId)
                            .enhance("Path", context.Request.Path)
                    return! context |> result500ServerError graceError
            }
