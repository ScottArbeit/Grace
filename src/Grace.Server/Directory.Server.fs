namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open Giraffe
open Grace.Actors.Constants
open Grace.Actors.Directory
open Grace.Actors.Interfaces
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Parameters.Directory
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors.Directory
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open System
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
open System.Threading.Tasks
open System.Collections.Concurrent
open Grace.Shared.Resources.Text
open System.Text

module Directory =

    type Validations<'T when 'T :> DirectoryParameters> = 'T -> HttpContext -> Task<Result<unit, DirectoryError>>[]
    //type QueryResult<'T, 'U when 'T :> DirectoryParameters> = 'T -> int -> IDirectoryActor ->Task<'U>
    
    let activitySource = new ActivitySource("Branch")

    let getActorProxy (context: HttpContext) (directoryId: DirectoryId) =
        let actorProxyFactory = context.GetService<IActorProxyFactory>()
        let actorId = GetActorId directoryId
        actorProxyFactory.CreateActorProxy<IDirectoryActor>(actorId, ActorName.Directory)

    let processCommand<'T when 'T :> DirectoryParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> HttpContext -> Task<GraceResult<string>>) =
        task {
            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>
                let validationResults = validations parameters context
                let! validationsPassed = validationResults |> areValid
                if validationsPassed then
                    let! cmd = command parameters context
                    match cmd with
                    | Ok graceReturn -> 
                        return! context |> result200Ok graceReturn
                    | Error graceError -> return! context |> result400BadRequest graceError
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (DirectoryError.getErrorMessage error) (context.Items[Constants.CorrelationId] :?> string)
                    graceError.Properties.Add("Path", context.Request.Path)                    
                    return! context |> result400BadRequest graceError
            with ex ->
                let graceError = GraceError.Create $"{Utilities.createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string)
                graceError.Properties.Add("Path", context.Request.Path)
                return! context |> result500ServerError graceError
        }

    let processQuery<'T, 'U when 'T :> DirectoryParameters> (context: HttpContext) (parameters: 'T) (validations: Validations<'T>) maxCount (query: QueryResult<IDirectoryActor, 'U>) =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            try
                let validationResults = validations parameters context
                let! validationsPassed = validationResults |> areValid
                if validationsPassed then
                    let actorProxy = getActorProxy context (Guid.Parse(parameters.DirectoryId))
                    let! queryResult = query context maxCount actorProxy
                    let! returnValue = context |> result200Ok (GraceReturnValue.Create queryResult (context.Items[Constants.CorrelationId] :?> string))
                    return returnValue
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (DirectoryError.getErrorMessage error) (context.Items[Constants.CorrelationId] :?> string)
                    graceError.Properties.Add("Path", context.Request.Path)                    
                    return! context |> result400BadRequest graceError
            with ex ->
                return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (context.Items[Constants.CorrelationId] :?> string))
        }

    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: CreateParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty $"{parameters.DirectoryVersion.DirectoryId}" DirectoryError.InvalidDirectoryId |> returnTask
                       guidIsValidAndNotEmpty $"{parameters.DirectoryVersion.RepositoryId}" DirectoryError.InvalidRepositoryId |> returnTask
                       repositoryIdExists $"{parameters.DirectoryVersion.RepositoryId}" context DirectoryError.RepositoryDoesNotExist |]

                let command (parameters: CreateParameters) (context: HttpContext) =
                    task {
                        let actorId = GetActorId parameters.DirectoryVersion.DirectoryId
                        let actorProxy = ApplicationContext.ActorProxyFactory().CreateActorProxy<IDirectoryActor>(actorId, ActorName.Directory)
                        let correlationId = context.Items[Constants.CorrelationIdHeaderKey] :?> string
                        return! actorProxy.Create parameters.DirectoryVersion correlationId
                    }

                return! processCommand context validations command
            }

    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: GetParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty $"{parameters.RepositoryId}" DirectoryError.InvalidRepositoryId |> returnTask
                       guidIsValidAndNotEmpty $"{parameters.DirectoryId}" DirectoryError.InvalidRepositoryId |> returnTask
                       repositoryIdExists $"{parameters.RepositoryId}" context DirectoryError.RepositoryDoesNotExist
                       directoryIdExists (Guid.Parse(parameters.DirectoryId)) context DirectoryError.DirectoryDoesNotExist |]

                let query (context: HttpContext) (maxCount: int) (actorProxy: IDirectoryActor) =
                    task {
                        let! directoryVersion = actorProxy.Get()
                        return directoryVersion
                    }

                let! parameters = context |> parse<GetParameters>
                return! processQuery context parameters validations 1 query
            }

    let GetDirectoryVersionsRecursive: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: GetParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty $"{parameters.RepositoryId}" DirectoryError.InvalidRepositoryId |> returnTask
                       guidIsValidAndNotEmpty $"{parameters.DirectoryId}" DirectoryError.InvalidRepositoryId |> returnTask
                       repositoryIdExists $"{parameters.RepositoryId}" context DirectoryError.RepositoryDoesNotExist
                       directoryIdExists (Guid.Parse(parameters.DirectoryId)) context DirectoryError.DirectoryDoesNotExist |]

                let query (context: HttpContext) (maxCount: int) (actorProxy: IDirectoryActor) =
                    task {
                        let! directoryVersions = actorProxy.GetDirectoryVersionsRecursive()
                        return directoryVersions
                    }

                let! parameters = context |> parse<GetParameters>
                return! processQuery context parameters validations 1 query
            }

    let GetByDirectoryIds: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: GetByDirectoryIdsParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty $"{parameters.RepositoryId}" DirectoryError.InvalidRepositoryId |> returnTask
                       repositoryIdExists $"{parameters.RepositoryId}" context DirectoryError.RepositoryDoesNotExist
                       directoryIdsExist parameters.DirectoryIds context DirectoryError.DirectoryDoesNotExist |]

                let query (context: HttpContext) (maxCount: int) (actorProxy: IDirectoryActor) =
                    task {
                        let directoryVersions = List<DirectoryVersion>()
                        let directoryIds = context.Items[nameof(GetByDirectoryIdsParameters)] :?> List<DirectoryId>
                        for directoryId in directoryIds do
                            let actorProxy = ApplicationContext.ActorProxyFactory().CreateActorProxy<IDirectoryActor>(ActorId($"{directoryId}"), ActorName.Directory)
                            let! directoryVersion = actorProxy.Get()
                            directoryVersions.Add(directoryVersion)
                        return directoryVersions
                    }

                let! parameters = context |> parse<GetByDirectoryIdsParameters>
                context.Items[nameof(GetByDirectoryIdsParameters)] <- parameters.DirectoryIds
                return! processQuery context parameters validations 1 query
            }

    let GetBySha256Hash: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: GetBySha256HashParameters) (context: HttpContext) =
                    [| guidIsValidAndNotEmpty $"{parameters.DirectoryId}" DirectoryError.InvalidDirectoryId |> returnTask
                       guidIsValidAndNotEmpty $"{parameters.RepositoryId}" DirectoryError.InvalidRepositoryId |> returnTask
                       stringIsNotEmpty parameters.Sha256Hash DirectoryError.Sha256HashIsRequired |> returnTask
                       repositoryIdExists $"{parameters.RepositoryId}" context DirectoryError.RepositoryDoesNotExist |]

                let query (context: HttpContext) (maxCount: int) (actorProxy: IDirectoryActor) =
                    task {
                        let parameters = context.Items[nameof(GetBySha256HashParameters)] :?> GetBySha256HashParameters
                        let! directoryVersion = getDirectoryBySha256Hash (Guid.Parse(parameters.RepositoryId)) (Sha256Hash parameters.Sha256Hash)
                        return directoryVersion
                    }

                let! parameters = context |> parse<GetBySha256HashParameters>
                context.Items[nameof(GetBySha256HashParameters)] <- parameters
                return! processQuery context parameters validations 1 query
            }

    let SaveDirectoryVersions: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let validations (parameters: SaveDirectoryVersionsParameters) (context: HttpContext) =
                    let mutable allValidations: Task<Result<unit, DirectoryError>>[] = [||]
                    for directoryVersion in parameters.DirectoryVersions do
                        let validations = 
                            [| guidIsValidAndNotEmpty $"{directoryVersion.DirectoryId}" DirectoryError.InvalidDirectoryId |> returnTask
                               guidIsValidAndNotEmpty $"{directoryVersion.RepositoryId}" DirectoryError.InvalidRepositoryId |> returnTask
                               stringIsNotEmpty $"{directoryVersion.Sha256Hash}" DirectoryError.Sha256HashIsRequired |> returnTask 
                               stringIsNotEmpty $"{directoryVersion.RelativePath}" DirectoryError.RelativePathMustNotBeEmpty |> returnTask
                               repositoryIdExists $"{directoryVersion.RepositoryId}" context DirectoryError.RepositoryDoesNotExist |]
                        allValidations <- Array.append allValidations validations
                    allValidations

                let command (parameters: SaveDirectoryVersionsParameters) (context: HttpContext) =
                    task {
                        let correlationId = context.Items[Constants.CorrelationIdHeaderKey] :?> string
                        let results = ConcurrentQueue<GraceResult<string>>()
                        do! Parallel.ForEachAsync(parameters.DirectoryVersions, Constants.ParallelOptions, (fun dv ct ->
                            ValueTask(task {
                                let actorId = GetActorId dv.DirectoryId
                                let actorProxy = ApplicationContext.ActorProxyFactory().CreateActorProxy<IDirectoryActor>(actorId, ActorName.Directory)
                                let! exists = actorProxy.Exists()
                                if not <| exists then
                                    let! createResult = actorProxy.Create dv correlationId
                                    results.Enqueue(createResult)
                            })
                        ))

                        let firstError = results |> Seq.tryFind (fun result -> match result with | Ok _ -> false | Error _ -> true)

                        match firstError with
                        | None -> return GraceResult.Ok (GraceReturnValue.Create "Uploaded new directory versions." correlationId)
                        | Some error ->
                            let sb = StringBuilder()
                            for result in results do
                                match result with
                                | Ok _ -> ()
                                | Error error -> 
                                    sb.Append($"{error.Error}; ") |> ignore
                            return GraceResult.Error (GraceError.Create (sb.ToString()) correlationId)
                    }

                return! processCommand context validations command
            }
