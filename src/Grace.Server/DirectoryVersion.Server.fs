namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open Giraffe
open Grace.Actors.Commands
open Grace.Actors.Constants
open Grace.Actors.DirectoryVersion
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Parameters.Directory
open Grace.Shared.Resources.Text
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors.DirectoryVersion
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Giraffe.ViewEngine.HtmlElements

module DirectoryVersion =

    type Validations<'T when 'T :> DirectoryParameters> = 'T -> ValueTask<Result<unit, DirectoryVersionError>> array
    //type QueryResult<'T, 'U when 'T :> DirectoryParameters> = 'T -> int -> IDirectoryVersionActor ->Task<'U>

    let activitySource = new ActivitySource("Branch")

    let processCommand<'T when 'T :> DirectoryParameters>
        (context: HttpContext)
        (validations: Validations<'T>)
        (command: 'T -> HttpContext -> Task<GraceResult<string>>)
        =
        task {
            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let! cmd = command parameters context

                    match cmd with
                    | Ok graceReturnValue -> return! context |> result200Ok graceReturnValue
                    | Error graceError -> return! context |> result400BadRequest graceError
                else
                    let! error = validationResults |> getFirstError

                    let graceError = GraceError.Create (DirectoryVersionError.getErrorMessage error) (getCorrelationId context)

                    graceError.Properties.Add("Path", context.Request.Path)
                    return! context |> result400BadRequest graceError
            with ex ->
                let graceError = GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (getCorrelationId context)

                graceError.Properties.Add("Path", context.Request.Path)
                return! context |> result500ServerError graceError
        }

    let processQuery<'T, 'U when 'T :> DirectoryParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        maxCount
        (query: QueryResult<IDirectoryVersionActor, 'U>)
        =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            let correlationId = getCorrelationId context

            try
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let directoryVersionGuid = Guid.Parse(parameters.DirectoryId)
                    let actorProxy = DirectoryVersion.CreateActorProxy directoryVersionGuid correlationId

                    let! queryResult = query context maxCount actorProxy

                    let graceReturnValue = GraceReturnValue.Create queryResult correlationId

                    let graceIds = getGraceIds context
                    graceReturnValue.Properties[nameof (OwnerId)] <- graceIds.OwnerId
                    graceReturnValue.Properties[nameof (OrganizationId)] <- graceIds.OrganizationId
                    graceReturnValue.Properties[nameof (RepositoryId)] <- graceIds.RepositoryId
                    graceReturnValue.Properties[nameof (BranchId)] <- graceIds.BranchId

                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError

                    let graceError = GraceError.Create (DirectoryVersionError.getErrorMessage error) correlationId

                    graceError.Properties.Add("Path", context.Request.Path)
                    return! context |> result400BadRequest graceError
            with ex ->
                return!
                    context
                    |> result500ServerError (GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" correlationId)
        }

    /// Create a new directory version.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateParameters) =
                    [| String.isNotEmpty $"{parameters.DirectoryVersion.DirectoryVersionId}" DirectoryVersionError.InvalidDirectoryId
                       Guid.isValidAndNotEmptyGuid $"{parameters.DirectoryVersion.DirectoryVersionId}" DirectoryVersionError.InvalidDirectoryId
                       String.isNotEmpty $"{parameters.DirectoryVersion.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                       Guid.isValidAndNotEmptyGuid $"{parameters.DirectoryVersion.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                       String.isNotEmpty $"{parameters.DirectoryVersion.RelativePath}" DirectoryVersionError.RelativePathMustNotBeEmpty
                       String.isNotEmpty $"{parameters.DirectoryVersion.Sha256Hash}" DirectoryVersionError.Sha256HashIsRequired
                       String.isValidSha256Hash $"{parameters.DirectoryVersion.Sha256Hash}" DirectoryVersionError.InvalidSha256Hash
                       Repository.repositoryIdExists
                           $"{parameters.DirectoryVersion.RepositoryId}"
                           parameters.CorrelationId
                           DirectoryVersionError.RepositoryDoesNotExist |]

                let command (parameters: CreateParameters) (context: HttpContext) =
                    task {
                        let actorProxy = DirectoryVersion.CreateActorProxy parameters.DirectoryVersion.DirectoryVersionId (getCorrelationId context)

                        return! actorProxy.Handle (DirectoryVersion.Create parameters.DirectoryVersion) (Services.createMetadata context)
                    }

                return! processCommand context validations command
            }

    /// Get a directory version.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: GetParameters) =
                    [| Guid.isValidAndNotEmptyGuid $"{parameters.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                       Guid.isValidAndNotEmptyGuid $"{parameters.DirectoryId}" DirectoryVersionError.InvalidDirectoryId
                       Repository.repositoryIdExists $"{parameters.RepositoryId}" parameters.CorrelationId DirectoryVersionError.RepositoryDoesNotExist
                       DirectoryVersion.directoryIdExists
                           (Guid.Parse(parameters.DirectoryId))
                           parameters.CorrelationId
                           DirectoryVersionError.DirectoryDoesNotExist |]

                let query (context: HttpContext) (maxCount: int) (actorProxy: IDirectoryVersionActor) =
                    task {
                        let! directoryVersion = actorProxy.Get(getCorrelationId context)
                        return directoryVersion
                    }

                let! parameters = context |> parse<GetParameters>
                return! processQuery context parameters validations 1 query
            }

    /// Get a directory version and all of its children.
    let GetDirectoryVersionsRecursive: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: GetParameters) =
                    [| Guid.isValidAndNotEmptyGuid $"{parameters.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                       Guid.isValidAndNotEmptyGuid $"{parameters.DirectoryId}" DirectoryVersionError.InvalidDirectoryId
                       Repository.repositoryIdExists $"{parameters.RepositoryId}" parameters.CorrelationId DirectoryVersionError.RepositoryDoesNotExist
                       DirectoryVersion.directoryIdExists
                           (Guid.Parse(parameters.DirectoryId))
                           parameters.CorrelationId
                           DirectoryVersionError.DirectoryDoesNotExist |]

                let query (context: HttpContext) (maxCount: int) (actorProxy: IDirectoryVersionActor) =
                    task {
                        let! directoryVersions = actorProxy.GetRecursiveDirectoryVersions false (getCorrelationId context)

                        return directoryVersions
                    }

                let! parameters = context |> parse<GetParameters>
                return! processQuery context parameters validations 1 query
            }

    /// Get a list of directory versions by directory ids.
    let GetByDirectoryIds: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: GetByDirectoryIdsParameters) =
                    [| Guid.isValidAndNotEmptyGuid $"{parameters.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                       Repository.repositoryIdExists $"{parameters.RepositoryId}" parameters.CorrelationId DirectoryVersionError.RepositoryDoesNotExist
                       DirectoryVersion.directoryIdsExist parameters.DirectoryIds parameters.CorrelationId DirectoryVersionError.DirectoryDoesNotExist |]

                let query (context: HttpContext) (maxCount: int) (actorProxy: IDirectoryVersionActor) =
                    task {
                        let directoryVersions = List<DirectoryVersion>()

                        let directoryIds = context.Items[nameof (GetByDirectoryIdsParameters)] :?> List<DirectoryVersionId>

                        for directoryId in directoryIds do
                            let actorProxy = DirectoryVersion.CreateActorProxy directoryId (getCorrelationId context)

                            let! directoryVersion = actorProxy.Get(getCorrelationId context)
                            directoryVersions.Add(directoryVersion)

                        return directoryVersions
                    }

                let! parameters = context |> parse<GetByDirectoryIdsParameters>
                context.Items[nameof (GetByDirectoryIdsParameters)] <- parameters.DirectoryIds
                return! processQuery context parameters validations 1 query
            }

    /// Get a directory version by its SHA256 hash.
    let GetBySha256Hash: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: GetBySha256HashParameters) =
                    [| Guid.isValidAndNotEmptyGuid $"{parameters.DirectoryId}" DirectoryVersionError.InvalidDirectoryId
                       Guid.isValidAndNotEmptyGuid $"{parameters.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                       String.isNotEmpty parameters.Sha256Hash DirectoryVersionError.Sha256HashIsRequired
                       Repository.repositoryIdExists $"{parameters.RepositoryId}" parameters.CorrelationId DirectoryVersionError.RepositoryDoesNotExist |]

                let query (context: HttpContext) (maxCount: int) (actorProxy: IDirectoryVersionActor) =
                    task {
                        let parameters = context.Items[nameof (GetBySha256HashParameters)] :?> GetBySha256HashParameters

                        match! getDirectoryBySha256Hash (Guid.Parse(parameters.RepositoryId)) (Sha256Hash parameters.Sha256Hash) (getCorrelationId context) with
                        | Some directoryVersion -> return directoryVersion
                        | None -> return DirectoryVersion.Default
                    }

                let! parameters = context |> parse<GetBySha256HashParameters>
                context.Items[nameof (GetBySha256HashParameters)] <- parameters
                return! processQuery context parameters validations 1 query
            }

    /// Save a list of directory versions.
    let SaveDirectoryVersions: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SaveDirectoryVersionsParameters) =
                    let mutable allValidations: ValueTask<Result<unit, DirectoryVersionError>> array = Array.Empty()

                    for directoryVersion in parameters.DirectoryVersions do
                        let validations =
                            [| String.isNotEmpty $"{directoryVersion.DirectoryVersionId}" DirectoryVersionError.InvalidDirectoryId
                               Guid.isValidAndNotEmptyGuid $"{directoryVersion.DirectoryVersionId}" DirectoryVersionError.InvalidDirectoryId
                               String.isNotEmpty $"{directoryVersion.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                               Guid.isValidAndNotEmptyGuid $"{directoryVersion.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                               String.isNotEmpty $"{directoryVersion.Sha256Hash}" DirectoryVersionError.Sha256HashIsRequired
                               String.isValidSha256Hash $"{directoryVersion.Sha256Hash}" DirectoryVersionError.InvalidSha256Hash
                               String.isNotEmpty $"{directoryVersion.RelativePath}" DirectoryVersionError.RelativePathMustNotBeEmpty
                               Repository.repositoryIdExists
                                   $"{directoryVersion.RepositoryId}"
                                   parameters.CorrelationId
                                   DirectoryVersionError.RepositoryDoesNotExist |]

                        allValidations <- Array.append allValidations validations

                    allValidations

                let command (parameters: SaveDirectoryVersionsParameters) (context: HttpContext) =
                    task {
                        let correlationId = getCorrelationId context
                        let results = ConcurrentQueue<GraceResult<string>>()

                        do!
                            Parallel.ForEachAsync(
                                parameters.DirectoryVersions,
                                Constants.ParallelOptions,
                                (fun dv ct ->
                                    ValueTask(
                                        task {
                                            // Check if the directory version exists. If it doesn't, create it.
                                            let directoryVersionActor = DirectoryVersion.CreateActorProxy dv.DirectoryVersionId correlationId

                                            let! exists = directoryVersionActor.Exists parameters.CorrelationId
                                            //logToConsole $"In SaveDirectoryVersions: {dv.DirectoryId} exists: {exists}"
                                            if not <| exists then
                                                let! createResult = directoryVersionActor.Handle (DirectoryVersion.Create dv) (createMetadata context)

                                                results.Enqueue(createResult)
                                        }
                                    ))
                            )

                        let firstError =
                            results
                            |> Seq.tryFind (fun result ->
                                match result with
                                | Ok _ -> false
                                | Error _ -> true)

                        match firstError with
                        | None -> return GraceResult.Ok(GraceReturnValue.Create "Uploaded new directory versions." correlationId)
                        | Some error ->
                            let sb = StringBuilder()

                            for result in results do
                                match result with
                                | Ok _ -> ()
                                | Error error -> sb.Append($"{error.Error}; ") |> ignore

                            return GraceResult.Error(GraceError.Create (sb.ToString()) correlationId)
                    }

                return! processCommand context validations command
            }
