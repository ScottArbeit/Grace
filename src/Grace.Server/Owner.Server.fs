namespace Grace.Server

open Giraffe
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Commands.Owner
open Grace.Shared.Extensions
open Grace.Shared.Parameters.Owner
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Utilities
open Grace.Shared.Validation.Errors.Owner
open Grace.Types.Types
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

    /// Generic processor for all Owner commands.
    let processCommand<'T when 'T :> OwnerParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<OwnerCommand>) =
        task {
            let commandName = context.Items["Command"] :?> string
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = Dictionary<string, string>()

            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                parameterDictionary.AddRange(getParametersAsDictionary parameters)

                // We know these Id's from ValidateIds.Middleware, so let's set them so we never have to resolve them again.
                parameters.OwnerId <- graceIds.OwnerId

                let handleCommand (ownerId: string) cmd =
                    task {
                        let t = cmd.GetType()

                        let isSupported =
                            not <| (isNull t.Namespace)
                            && t.Namespace.StartsWith("Grace", StringComparison.InvariantCulture)

                        logToConsole
                            $"In Owner.Server.processCommand: handleCommand: t.Namespace: {t.Namespace}; t.FullName: {t.FullName}; isSupported (for JSON serialization): {isSupported}."

                        let ownerGuid = Guid.Parse(ownerId)
                        let actorProxy = Owner.CreateActorProxy ownerGuid (getCorrelationId context)
                        let metadata = createMetadata context

                        logToConsole
                            $"In Owner.Server.handleCommand: context.Items: {serialize context.Items}; metadata.AssemblyQualifiedName: {metadata.GetType().AssemblyQualifiedName}; metadata.Assembly.Location: {metadata.GetType().Assembly.Location}; metadata.GetType().Attributes: {metadata.GetType().Attributes}; metadata: {serialize metadata}."

                        match! actorProxy.Handle cmd metadata with
                        | Ok graceReturnValue ->
                            logToConsole $"In Owner.Server.processCommand: graceReturnValue.ReturnValue: {graceReturnValue.ReturnValue}."
                            logToConsole $"In Owner.Server.processCommand: graceReturnValue.CorrelationId: {graceReturnValue.CorrelationId}."
                            logToConsole $"In Owner.Server.processCommand: graceReturnValue.EventTime: {graceReturnValue.EventTime}."
                            logToConsole $"In Owner.Server.processCommand: graceReturnValue.Properties: {serialize graceReturnValue.Properties}."

                            logToConsole
                                $"In Owner.Server.processCommand: parameterDictionary: {serialize parameterDictionary}; graceIds: {serialize graceIds}; commandName: {commandName}; path: {context.Request.Path}."

                            graceReturnValue.enhance (parameterDictionary :> IReadOnlyDictionary<string, string>)
                            |> ignore

                            graceReturnValue.enhance (nameof (OwnerId), graceIds.OwnerId) |> ignore
                            graceReturnValue.enhance ("Command", commandName) |> ignore
                            graceReturnValue.enhance ("Path", context.Request.Path) |> ignore

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            graceError
                                .enhance(parameterDictionary)
                                .enhance(nameof (OwnerId), graceIds.OwnerId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path)
                            |> ignore

                            log.LogDebug(
                                "{CurrentInstant}: In Branch.Server.handleCommand: error from actorProxy.Handle: {error}",
                                getCurrentInstantExtended (),
                                (graceError.ToString())
                            )

                            return! context |> result400BadRequest graceError
                    }


                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                log.LogDebug(
                    "{CurrentInstant}: In Owner.Server.processCommand: validationsPassed: {validationsPassed}.",
                    getCurrentInstantExtended (),
                    validationsPassed
                )

                if validationsPassed then
                    let! cmd = command parameters

                    logToConsole
                        $"In Owner.Server.processCommand: cmd: {serialize cmd}; AssemblyQualifiedName: {cmd.GetType().AssemblyQualifiedName}; Assembly.Location: {command.GetType().Assembly.Location}; parameters: {serialize parameters}."

                    let! result = handleCommand graceIds.OwnerId cmd

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {correlationId}; Finished {path}; Status code: {statusCode}; OwnerId: {ownerId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        context.Request.Path,
                        context.Response.StatusCode,
                        graceIds.OwnerId
                    )

                    return result
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = OwnerError.getErrorMessage error
                    log.LogDebug("{CurrentInstant}: error: {error}", getCurrentInstantExtended (), errorMessage)

                    let graceError =
                        (GraceError.Create errorMessage (getCorrelationId context))
                            .enhance(parameterDictionary)
                            .enhance(nameof (OwnerId), graceIds.OwnerId)
                            .enhance("Command", commandName)
                            .enhance("Path", context.Request.Path)
                            .enhance ("Error", errorMessage)

                    return! context |> result400BadRequest graceError
            with ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Exception in Owner.Server.processCommand. CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    (getCorrelationId context)
                )

                let graceError =
                    (GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (getCorrelationId context))
                        .enhance(parameterDictionary)
                        .enhance(nameof (OwnerId), graceIds.OwnerId)
                        .enhance ("Path", context.Request.Path)

                return! context |> result500ServerError graceError
        }

    /// Generic processor for all Owner queries.
    let processQuery<'T, 'U when 'T :> OwnerParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (maxCount: int)
        (query: QueryResult<IOwnerActor, 'U>)
        =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = getParametersAsDictionary parameters

            try
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    // Get the actor proxy for this owner.
                    let ownerGuid = Guid.Parse(graceIds.OwnerId)
                    let actorProxy = Owner.CreateActorProxy ownerGuid correlationId

                    // Execute the query.
                    let! queryResult = query context maxCount actorProxy

                    // Wrap the result in a GraceReturnValue.
                    let graceReturnValue =
                        (GraceReturnValue.Create queryResult correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof (OwnerId), graceIds.OwnerId)
                            .enhance ("Path", context.Request.Path)

                    //logToConsole $"In Owner.Server.processQuery: graceReturnValue: {graceReturnValue}"
                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError

                    let graceError =
                        (GraceError.Create (OwnerError.getErrorMessage error) correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof (OwnerId), graceIds.OwnerId)
                            .enhance ("Path", context.Request.Path)

                    return! context |> result400BadRequest graceError
            with ex ->
                let graceError =
                    (GraceError.Create $"{ExceptionResponse.Create ex}" correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof (OwnerId), graceIds.OwnerId)
                        .enhance ("Path", context.Request.Path)

                return! context |> result500ServerError graceError
        }

    /// Create an owner.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateOwnerParameters) =
                    [| Owner.ownerIdDoesNotExist parameters.OwnerId parameters.CorrelationId OwnerIdAlreadyExists
                       Owner.ownerNameDoesNotExist parameters.OwnerName parameters.CorrelationId OwnerNameAlreadyExists |]

                let command (parameters: CreateOwnerParameters) =
                    let ownerIdGuid = Guid.Parse(parameters.OwnerId)
                    Create(ownerIdGuid, OwnerName parameters.OwnerName) |> returnValueTask

                context.Items.Add("Command", nameof (Create))
                return! processCommand context validations command
            }

    /// Set the name of an owner.
    let SetName: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOwnerNameParameters) =
                    [| String.isNotEmpty parameters.NewName OwnerNameIsRequired
                       String.isValidGraceName parameters.NewName InvalidOwnerName
                       Owner.ownerIsNotDeleted context parameters.CorrelationId OwnerIsDeleted
                       Owner.ownerNameDoesNotExist parameters.NewName parameters.CorrelationId OwnerNameAlreadyExists |]

                let command (parameters: SetOwnerNameParameters) = SetName(OwnerName parameters.NewName) |> returnValueTask

                context.Items.Add("Command", nameof (SetName))
                return! processCommand context validations command
            }

    /// Set the owner type (Public, Private).
    let SetType: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOwnerTypeParameters) =
                    [| String.isNotEmpty parameters.OwnerType OwnerTypeIsRequired
                       DiscriminatedUnion.isMemberOf<OwnerType, OwnerError> parameters.OwnerType InvalidOwnerType
                       Owner.ownerIsNotDeleted context parameters.CorrelationId OwnerIsDeleted |]

                let command (parameters: SetOwnerTypeParameters) =
                    OwnerCommand.SetType(discriminatedUnionFromString<OwnerType>(parameters.OwnerType).Value)
                    |> returnValueTask

                context.Items.Add("Command", nameof (SetType))
                return! processCommand context validations command
            }

    /// Set the owner search visibility (Visible, NotVisible).
    let SetSearchVisibility: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOwnerSearchVisibilityParameters) =
                    [| String.isNotEmpty parameters.SearchVisibility SearchVisibilityIsRequired
                       DiscriminatedUnion.isMemberOf<SearchVisibility, OwnerError> parameters.SearchVisibility InvalidSearchVisibility
                       Owner.ownerIsNotDeleted context parameters.CorrelationId OwnerIsDeleted |]

                let command (parameters: SetOwnerSearchVisibilityParameters) =
                    OwnerCommand.SetSearchVisibility(
                        Utilities
                            .discriminatedUnionFromString<SearchVisibility>(parameters.SearchVisibility)
                            .Value
                    )
                    |> returnValueTask

                context.Items.Add("Command", nameof (SetSearchVisibility))
                return! processCommand context validations command
            }

    /// Set the owner's description.
    let SetDescription: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SetOwnerDescriptionParameters) =
                    [| String.isNotEmpty parameters.Description DescriptionIsRequired
                       String.maxLength parameters.Description 2048 DescriptionIsTooLong
                       Owner.ownerIsNotDeleted context parameters.CorrelationId OwnerIsDeleted |]

                let command (parameters: SetOwnerDescriptionParameters) = OwnerCommand.SetDescription(parameters.Description) |> returnValueTask

                context.Items.Add("Command", nameof (SetDescription))
                return! processCommand context validations command
            }

    /// List the organizations for an owner.
    let ListOrganizations: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: ListOrganizationsParameters) = [| Guid.isValidAndNotEmptyGuid parameters.OwnerId InvalidOwnerId |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IOwnerActor) =
                        task {
                            let! organizations = actorProxy.ListOrganizations(getCorrelationId context)
                            return! context.WriteJsonAsync(organizations)
                        }

                    let! parameters = context |> parse<ListOrganizationsParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Delete an owner.
    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DeleteOwnerParameters) =
                    [| String.isNotEmpty parameters.DeleteReason DeleteReasonIsRequired
                       Owner.ownerIsNotDeleted context parameters.CorrelationId OwnerIsDeleted |]

                let command (parameters: DeleteOwnerParameters) =
                    OwnerCommand.DeleteLogical(parameters.Force, parameters.DeleteReason)
                    |> returnValueTask

                context.Items.Add("Command", nameof (Delete))
                return! processCommand context validations command
            }

    /// Undelete a previously-deleted owner.
    let Undelete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: OwnerParameters) = [| Owner.ownerIsDeleted context parameters.CorrelationId OwnerIsNotDeleted |]

                let command (parameters: OwnerParameters) = OwnerCommand.Undelete |> returnValueTask

                context.Items.Add("Command", nameof (Undelete))
                return! processCommand context validations command
            }

    /// Get an owner.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let validations (parameters: GetOwnerParameters) = [| Owner.ownerIsNotDeleted context parameters.CorrelationId OwnerIsDeleted |]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IOwnerActor) = task { return! actorProxy.Get(getCorrelationId context) }

                    let! parameters = context |> parse<GetOwnerParameters>

                    let! result = processQuery context parameters validations 1 query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; OwnerId: {ownerId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.OwnerId
                    )

                    return result
                with ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; OwnerId: {ownerId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.OwnerId
                    )

                    let graceError =
                        (GraceError.Create $"{ExceptionResponse.Create ex}" (getCorrelationId context))
                            .enhance(nameof (OwnerId), graceIds.OwnerId)
                            .enhance ("Path", context.Request.Path)

                    return! context |> result500ServerError graceError
            }
