namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open Giraffe
open Grace.Actors
open Grace.Actors.Commands.Branch
open Grace.Actors.Commands.Repository
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Parameters.Repository
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors.Repository
open Grace.Shared.Validation.Repository
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Logging
open NodaTime
open System
open OpenTelemetry.Trace
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.Threading.Tasks
open System.Text.Json
open Repository

module Repository =

    type Validations<'T when 'T :> RepositoryParameters> = 'T -> HttpContext -> Task<Result<unit, RepositoryError>> list

    let activitySource = new ActivitySource("Repository")

    let actorProxyFactory = ApplicationContext.ActorProxyFactory()

    let getActorProxy (repositoryId: string) (context: HttpContext) =
        let actorId = ActorId(repositoryId)
        actorProxyFactory.CreateActorProxy<IRepositoryActor>(actorId, ActorName.Repository)

    let processCommand<'T when 'T :> RepositoryParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> Task<RepositoryCommand>) = 
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
                        if String.IsNullOrEmpty(parameters.RepositoryId) then parameters.RepositoryId <- repositoryId
                        let actorProxy = getActorProxy repositoryId context
                        let! cmd = command parameters
                        let! result = actorProxy.Handle cmd (Services.createMetadata context)
                        match result with
                            | Ok graceReturn -> 
                                match cmd with
                                | Create _ ->
                                    let branchId = (Guid.NewGuid())
                                    let branchActorId = ActorId($"{branchId}")
                                    let branchActor = context.GetService<IActorProxyFactory>().CreateActorProxy<IBranchActor>(branchActorId, ActorName.Branch)
                                    let! result = branchActor.Handle (BranchCommand.Create (branchId, (BranchName Constants.InitialBranchName), 
                                                    Constants.DefaultParentBranchId, (Guid.Parse(parameters.RepositoryId)))) (createMetadata context)
                                    match result with
                                    | Ok branchGraceReturn ->
                                        do graceReturn.Properties.Add(nameof(BranchId), $"{branchId}")
                                        do graceReturn.Properties.Add(nameof(BranchName), Constants.InitialBranchName)
                                        return! context |> result200Ok graceReturn
                                    | Error graceError -> 
                                        return! context |> result400BadRequest graceError
                                | _ ->
                                    return! context |> result200Ok graceReturn
                            | Error graceError -> 
                                return! context |> result400BadRequest graceError
                    | None -> 
                        return! context |> result400BadRequest (GraceError.Create (RepositoryError.getErrorMessage RepositoryDoesNotExist) (context.Items[Constants.CorrelationId] :?> string))
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (RepositoryError.getErrorMessage error) (context.Items[Constants.CorrelationId] :?> string)
                    graceError.Properties.Add("Path", context.Request.Path)
                    return! context |> result400BadRequest graceError
            with ex ->
                return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
        }

    let processQuery<'T, 'U when 'T :> RepositoryParameters> (context: HttpContext) (parameters: 'T) (validations: Validations<'T>) (maxCount: int) (query: QueryResult<IRepositoryActor, 'U>) =
        task {
            try
                use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
                //let! parameters = context |> parse<'T>
                let validationResults = validations parameters context
                let! validationsPassed = validationResults |> allPass
                if validationsPassed then
                    context.Items.Add(nameof(RepositoryId), RepositoryId parameters.RepositoryId)
                    let actorProxy = getActorProxy parameters.RepositoryId context
                    let! exists = actorProxy.Exists()
                    if exists then
                        let! queryResult = query context maxCount actorProxy
                        return! context |> result200Ok (GraceReturnValue.Create queryResult (context.Items[Constants.CorrelationId] :?> string))
                    else
                        return! context |> result400BadRequest (GraceError.Create (RepositoryError.getErrorMessage RepositoryIdDoesNotExist) (context.Items[Constants.CorrelationId] :?> string))
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (RepositoryError.getErrorMessage error) (context.Items[Constants.CorrelationId] :?> string)
                    graceError.Properties.Add("Path", context.Request.Path)
                    return! context |> result400BadRequest graceError
            with ex ->
                return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string)) 
        }

    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                //let! parameters = context |> parse<CreateParameters>
                //logToConsole $"parameters.ObjectStorageProvider: {parameters.ObjectStorageProvider}"
                let validations (parameters: CreateParameters) (context: HttpContext) =
                    [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                      eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                      eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      stringIsNotEmpty parameters.RepositoryId RepositoryIdIsRequired
                      guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      stringIsNotEmpty parameters.RepositoryName RepositoryNameIsRequired
                      stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName
                      ownerExists parameters.OwnerId parameters.OwnerName context OwnerDoesNotExist
                      organizationExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName context OrganizationDoesNotExist ]
                
                let command (parameters: CreateParameters) = 
                    task {
                        let! ownerId = resolveOwnerId parameters.OwnerId parameters.OwnerName
                        let! organizationId = resolveOrganizationId ownerId.Value parameters.OwnerName parameters.OrganizationId parameters.OrganizationName
                        return Create (RepositoryName parameters.RepositoryName, (Guid.Parse(parameters.RepositoryId)),
                            (Guid.Parse(ownerId.Value)), (Guid.Parse(organizationId.Value)))
                    }

                return! processCommand context validations command
            }

    /// <summary>
    /// Sets the search visibility of the repository.
    /// </summary>
    /// <param name="next">The next middleware to call in the pipeline.</param>
    /// <param name="context">The current HttpContext.</param>
    let SetVisibility: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: VisibilityParameters) (context: HttpContext) =
                    [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                      eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                      eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName
                      eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                      visibilityIsValid parameters.Visibility InvalidVisibilityValue
                      repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryIsDeleted ]
  
                let command (parameters: VisibilityParameters) = 
                    SetVisibility(discriminatedUnionFromString<RepositoryVisibility>(parameters.Visibility).Value) |> returnTask
                
                return! processCommand context validations command
            }

    let SetSaveDays: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) -> 
            task {
                let validations (parameters: SaveDaysParameters) (context: HttpContext) =
                    [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                      eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                      eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName
                      eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                      daysIsValid parameters.SaveDays InvalidSaveDaysValue
                      repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryIsDeleted ]
                
                let command (parameters: SaveDaysParameters) = SetSaveDays(parameters.SaveDays) |> returnTask
                
                return! processCommand context validations command
            }

    let SetCheckpointDays: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) -> 
            task {
                let validations (parameters: CheckpointDaysParameters) (context: HttpContext) =
                    [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                      eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                      eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName
                      eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                      daysIsValid parameters.CheckpointDays InvalidCheckpointDaysValue
                      repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryIsDeleted ]
                
                let command (parameters: CheckpointDaysParameters) = SetCheckpointDays(parameters.CheckpointDays) |> returnTask
                
                return! processCommand context validations command
            }

    let EnableSingleStepMerge: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) -> 
            task {
                let validations (parameters: EnableMergeTypeParameters) (context: HttpContext) =
                    [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                      eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                      eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName
                      eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                      repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryIsDeleted ]
                
                let command (parameters: EnableMergeTypeParameters) = EnableSingleStepMerge (parameters.Enabled) |> returnTask
                
                return! processCommand context validations command
            }

    let EnableComplexMerge: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) -> 
            task {
                let validations (parameters: EnableMergeTypeParameters) (context: HttpContext) =
                    [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                      eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                      eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName
                      eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                      repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryIsDeleted ]
                
                let command (parameters: EnableMergeTypeParameters) = EnableComplexMerge (parameters.Enabled) |> returnTask
                
                return! processCommand context validations command
            }

    let SetStatus: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) -> 
            task {
                let validations (parameters: StatusParameters) (context: HttpContext) =
                    [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                      eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                      eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName
                      eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                      isMemberOfDiscriminatedUnion<RepositoryStatus, RepositoryError> parameters.Status InvalidRepositoryStatus
                      repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryIsDeleted ]
                
                let command (parameters: StatusParameters) =
                    SetRepositoryStatus(discriminatedUnionFromString<RepositoryStatus>(parameters.Status).Value) |> returnTask
                
                return! processCommand context validations command
            }

    let SetDefaultServerApiVersion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) -> 
            task {
                let validations (parameters: DefaultServerApiVersionParameters) (context: HttpContext) =
                    [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                      eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                      eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName
                      eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                      stringIsNotEmpty parameters.DefaultServerApiVersion InvalidServerApiVersion
                      repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryIsDeleted ]
                
                let command (parameters: DefaultServerApiVersionParameters) = SetDefaultServerApiVersion(parameters.DefaultServerApiVersion) |> returnTask
                
                return! processCommand context validations command
            }

    let SetRecordSaves: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: RecordSavesParameters) (context: HttpContext) =
                    [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                      eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                      eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName
                      eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                      repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryIsDeleted ]
                
                let command (parameters: RecordSavesParameters) = SetRecordSaves (parameters.RecordSaves) |> returnTask
                
                return! processCommand context validations command
            }

    let SetDescription: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DescriptionParameters) (context: HttpContext) =
                    [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                      eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                      eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName
                      stringIsNotEmpty parameters.Description DescriptionIsRequired
                      eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                      repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryIsDeleted ]

                let command (parameters: DescriptionParameters) = SetDescription (parameters.Description) |> returnTask

                return! processCommand context validations command
            }
            
    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: DeleteParameters) (context: HttpContext) =
                    [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                      eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                      eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName
                      stringIsNotEmpty parameters.DeleteReason DeleteReasonIsRequired
                      eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                      repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      repositoryIsNotDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryIsDeleted ]

                let command (parameters: DeleteParameters) = DeleteLogical (parameters.Force, parameters.DeleteReason) |> returnTask

                return! processCommand context validations command
            }

    let Undelete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: RepositoryParameters) (context: HttpContext) =
                    [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                      stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                      eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                      guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                      stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                      eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                      guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                      stringIsValidGraceName parameters.RepositoryName InvalidRepositoryName
                      eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                      repositoryExists parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryDoesNotExist
                      repositoryIsDeleted parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName context RepositoryIsNotDeleted ]

                let command (parameters: RepositoryParameters) = Undelete |> returnTask

                return! processCommand context validations command
            }

    let Exists: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: RepositoryParameters) (context: HttpContext) =
                        [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                          eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                          eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          repositoryIdExists parameters.RepositoryId context RepositoryIdDoesNotExist ]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            return! actorProxy.Exists()
                        }

                    let! parameters = context |> parse<RepositoryParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
        }

    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: RepositoryParameters) (context: HttpContext) =
                        [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                          eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                          eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          stringIsNotEmpty parameters.RepositoryName RepositoryNameIsRequired
                          repositoryIdExists parameters.RepositoryId context RepositoryIdDoesNotExist ]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            return! actorProxy.Get()
                        }

                    let! parameters = context |> parse<RepositoryParameters>
                    return! processQuery context parameters validations 1 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }

    let GetBranches: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetBranchesParameters) (context: HttpContext) =
                        [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                          eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                          eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                          repositoryIdExists parameters.RepositoryId context RepositoryIdDoesNotExist ]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            let repositoryId = context.Items[nameof(RepositoryId)] :?> RepositoryId
                            let includeDeleted = context.Items["IncludeDeleted"] :?> bool
                            return! getBranches repositoryId maxCount includeDeleted
                        }

                    let! parameters = context |> parse<GetBranchesParameters>
                    context.Items.Add("IncludeDeleted", parameters.IncludeDeleted)
                    return! processQuery context parameters validations 1000 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }

    let GetReferencesByReferenceId: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetReferencesByReferenceIdParameters) (context: HttpContext) =
                        [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                          eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                          eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                          listIsNonEmpty parameters.ReferenceIds ReferenceIdsAreRequired
                          repositoryIdExists parameters.RepositoryId context RepositoryIdDoesNotExist ]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            let referenceIds = context.Items["ReferenceIds"] :?> IEnumerable<ReferenceId>
                            return! getReferencesByReferenceId referenceIds maxCount
                        }

                    let! parameters = context |> parse<GetReferencesByReferenceIdParameters>
                    context.Items.Add("ReferenceIds", parameters.ReferenceIds)
                    return! processQuery context parameters validations 1000 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }

    let GetBranchesByBranchId: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetBranchesByBranchIdParameters) (context: HttpContext) =
                        [ guidIsValidAndNotEmpty parameters.OwnerId InvalidOwnerId
                          stringIsValidGraceName parameters.OwnerName InvalidOwnerName
                          eitherIdOrNameMustBeProvided parameters.OwnerId parameters.OwnerName EitherOwnerIdOrOwnerNameRequired
                          guidIsValidAndNotEmpty parameters.OrganizationId InvalidOrganizationId
                          stringIsValidGraceName parameters.OrganizationName InvalidOrganizationName
                          eitherIdOrNameMustBeProvided parameters.OrganizationId parameters.OrganizationName EitherOrganizationIdOrOrganizationNameRequired
                          guidIsValidAndNotEmpty parameters.RepositoryId InvalidRepositoryId
                          eitherIdOrNameMustBeProvided parameters.RepositoryId parameters.RepositoryName EitherRepositoryIdOrRepositoryNameRequired
                          listIsNonEmpty parameters.BranchIds BranchIdsAreRequired
                          repositoryIdExists parameters.RepositoryId context RepositoryIdDoesNotExist ]

                    let query (context: HttpContext) (maxCount: int) (actorProxy: IRepositoryActor) =
                        task {
                            let repositoryId = context.Items[nameof(RepositoryId)] :?> RepositoryId
                            let branchIds = context.Items["BranchIds"] :?> IEnumerable<ReferenceId>
                            let includeDeleted = context.Items["IncludeDeleted"] :?> bool
                            return! getBranchesByBranchId repositoryId branchIds maxCount includeDeleted
                        }

                    let! parameters = context |> parse<GetBranchesByBranchIdParameters>
                    context.Items.Add("BranchIds", parameters.BranchIds)
                    context.Items.Add("IncludeDeleted", parameters.IncludeDeleted)
                    return! processQuery context parameters validations 1000 query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
            }
