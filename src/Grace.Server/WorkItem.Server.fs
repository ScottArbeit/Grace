namespace Grace.Server

open Giraffe
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Server.Services
open Grace.Shared
open Grace.Shared.Extensions
open Grace.Shared.Parameters.WorkItem
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Grace.Types.WorkItem
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks

module WorkItem =
    type Validations<'T when 'T :> WorkItemParameters> = 'T -> ValueTask<Result<unit, WorkItemError>> array

    let log = ApplicationContext.loggerFactory.CreateLogger("WorkItem.Server")

    let activitySource = new ActivitySource("WorkItem")

    let processCommand<'T when 'T :> WorkItemParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<WorkItemCommand>) =
        task {
            let commandName = context.Items["Command"] :?> string
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = Dictionary<string, obj>()

            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                parameterDictionary.AddRange(getParametersAsDictionary parameters)

                // Use IDs from middleware.
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let handleCommand workItemId cmd =
                    task {
                        let actorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                        let metadata = createMetadata context

                        match! actorProxy.Handle cmd metadata with
                        | Ok graceReturnValue ->
                            graceReturnValue
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof WorkItemId, workItemId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            graceError
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof WorkItemId, workItemId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result400BadRequest graceError
                    }

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let! cmd = command parameters
                    let workItemId = Guid.Parse(parameters.WorkItemId)
                    return! handleCommand workItemId cmd
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = WorkItemError.getErrorMessage error

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance("Command", commandName)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Exception in WorkItem.Server.processCommand. CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    correlationId
                )

                let graceError =
                    (GraceError.CreateWithException ex String.Empty correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    let processQuery<'T, 'U when 'T :> WorkItemParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (query: QueryResult<IWorkItemActor, 'U>)
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
                    let workItemId = Guid.Parse(parameters.WorkItemId)
                    let actorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                    let! queryResult = query context 0 actorProxy

                    let graceReturnValue =
                        (GraceReturnValue.Create queryResult correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof WorkItemId, workItemId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = WorkItemError.getErrorMessage error

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with ex ->
                let graceError =
                    (GraceError.CreateWithException ex String.Empty correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    let internal buildUpdateCommands (parameters: UpdateWorkItemParameters) =
        [ if not <| String.IsNullOrEmpty(parameters.Title) then
              WorkItemCommand.SetTitle parameters.Title
          if not <| String.IsNullOrEmpty(parameters.Description) then
              WorkItemCommand.SetDescription parameters.Description
          if not <| String.IsNullOrEmpty(parameters.Status) then
              let status = discriminatedUnionFromString<WorkItemStatus> parameters.Status |> Option.get
              WorkItemCommand.SetStatus status
          if not <| String.IsNullOrEmpty(parameters.Constraints) then
              WorkItemCommand.SetConstraints parameters.Constraints
          if not <| String.IsNullOrEmpty(parameters.Notes) then
              WorkItemCommand.SetNotes parameters.Notes
          if not <| String.IsNullOrEmpty(parameters.ArchitecturalNotes) then
              WorkItemCommand.SetArchitecturalNotes parameters.ArchitecturalNotes
          if not <| String.IsNullOrEmpty(parameters.MigrationNotes) then
              WorkItemCommand.SetMigrationNotes parameters.MigrationNotes ]

    let internal validateLinkReferenceParameters (parameters: LinkReferenceParameters) =
        [| Guid.isValidAndNotEmptyGuid parameters.WorkItemId WorkItemError.InvalidWorkItemId
           Guid.isValidAndNotEmptyGuid parameters.ReferenceId WorkItemError.InvalidReferenceId |]

    let internal validateLinkPromotionGroupParameters (parameters: LinkPromotionGroupParameters) =
        [| Guid.isValidAndNotEmptyGuid parameters.WorkItemId WorkItemError.InvalidWorkItemId
           Guid.isValidAndNotEmptyGuid parameters.PromotionGroupId WorkItemError.InvalidPromotionGroupId |]

    /// Creates a new work item.
    let Create: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateWorkItemParameters) = [| Guid.isValidAndNotEmptyGuid parameters.WorkItemId WorkItemError.InvalidWorkItemId |]

                let command (parameters: CreateWorkItemParameters) =
                    let workItemId = Guid.Parse(parameters.WorkItemId)

                    WorkItemCommand.Create(
                        workItemId,
                        Guid.Parse(parameters.OwnerId),
                        Guid.Parse(parameters.OrganizationId),
                        Guid.Parse(parameters.RepositoryId),
                        parameters.Title,
                        parameters.Description
                    )
                    |> returnValueTask

                context.Items["Command"] <- nameof Create
                return! processCommand context validations command
            }

    /// Gets a work item.
    let Get: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                let validations (parameters: GetWorkItemParameters) = [| Guid.isValidAndNotEmptyGuid parameters.WorkItemId WorkItemError.InvalidWorkItemId |]

                let query (context: HttpContext) _ (actorProxy: IWorkItemActor) = actorProxy.Get(getCorrelationId context)

                let! parameters = context |> parse<GetWorkItemParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString
                context.Items["Command"] <- "Get"
                return! processQuery context parameters validations query
            }

    /// Updates a work item.
    let Update: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                let validations (parameters: UpdateWorkItemParameters) =
                    [| Guid.isValidAndNotEmptyGuid parameters.WorkItemId WorkItemError.InvalidWorkItemId
                       (if String.IsNullOrEmpty(parameters.Status) then
                            Ok() |> returnValueTask
                        else
                            DiscriminatedUnion.isMemberOf<WorkItemStatus, WorkItemError> parameters.Status WorkItemError.InvalidStatus) |]

                let! parameters = context |> parse<UpdateWorkItemParameters>
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString

                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let workItemId = Guid.Parse(parameters.WorkItemId)
                    let actorProxy = WorkItem.CreateActorProxy workItemId graceIds.RepositoryId correlationId
                    let metadata = createMetadata context

                    let commands = buildUpdateCommands parameters

                    if commands.IsEmpty then
                        let graceError =
                            (GraceError.Create "No updates were provided." correlationId)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof WorkItemId, workItemId)
                                .enhance ("Path", context.Request.Path.Value)

                        return! context |> result400BadRequest graceError
                    else
                        let mutable result: GraceResult<string> option = None

                        for cmd in commands do
                            match result with
                            | Some(Error _) -> ()
                            | _ ->
                                let! handleResult = actorProxy.Handle cmd metadata
                                result <- Some handleResult

                        match result with
                        | Some(Ok graceReturnValue) ->
                            graceReturnValue
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof WorkItemId, workItemId)
                                .enhance("Command", "Update")
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result200Ok graceReturnValue
                        | Some(Error graceError) ->
                            graceError
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof WorkItemId, workItemId)
                                .enhance("Command", "Update")
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result400BadRequest graceError
                        | None ->
                            return!
                                context
                                |> result400BadRequest (GraceError.Create "No updates were applied." correlationId)
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = WorkItemError.getErrorMessage error

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            }

    /// Links a reference to a work item.
    let LinkReference: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: LinkReferenceParameters) = validateLinkReferenceParameters parameters

                let command (parameters: LinkReferenceParameters) =
                    WorkItemCommand.LinkReference(Guid.Parse(parameters.ReferenceId))
                    |> returnValueTask

                context.Items["Command"] <- nameof LinkReference
                return! processCommand context validations command
            }

    /// Links a promotion group to a work item.
    let LinkPromotionGroup: HttpHandler =
        fun (_next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: LinkPromotionGroupParameters) = validateLinkPromotionGroupParameters parameters

                let command (parameters: LinkPromotionGroupParameters) =
                    WorkItemCommand.LinkPromotionGroup(Guid.Parse(parameters.PromotionGroupId))
                    |> returnValueTask

                context.Items["Command"] <- nameof LinkPromotionGroup
                return! processCommand context validations command
            }
