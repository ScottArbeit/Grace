namespace Grace.Server

open Giraffe
open Grace.Actors.Constants
open Grace.Actors.DirectoryVersion
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Parameters.DirectoryVersion
open Grace.Shared.Resources.Text
open Grace.Types.DirectoryVersion
open Grace.Types.Common
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
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
open System.IO

/// Contains Grace Server directory version behavior and supporting helpers.
module DirectoryVersion =

    /// Represents validations used by Grace Server APIs and background services.
    type Validations<'T when 'T :> DirectoryVersionParameters> = 'T -> ValueTask<Result<unit, DirectoryVersionError>> array
    //type QueryResult<'T, 'U when 'T :> DirectoryParameters> = 'T -> int -> IDirectoryVersionActor ->Task<'U>

    let activitySource = new ActivitySource("Branch")

    /// Implements directory save depth for the server request pipeline.
    let private directorySaveDepth (relativePath: RelativePath) =
        let trimmedPath = $"{relativePath}".Trim().TrimEnd('/', '\\')

        if String.IsNullOrWhiteSpace trimmedPath
           || trimmedPath = "." then
            0
        else
            trimmedPath
                .Split(
                    [| '/'; '\\' |],
                    StringSplitOptions.RemoveEmptyEntries
                )
                .Length

    /// Coordinates process command processing for Grace Server.
    let processCommand<'T when 'T :> DirectoryVersionParameters>
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

                    graceError.Properties.Add("Path", context.Request.Path.Value)
                    return! context |> result400BadRequest graceError
            with
            | ex ->
                let graceError = GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (getCorrelationId context)

                graceError.Properties.Add("Path", context.Request.Path.Value)
                return! context |> result500ServerError graceError
        }

    /// Coordinates process query processing for Grace Server.
    let processQuery<'T, 'U when 'T :> DirectoryVersionParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        maxCount
        (query: QueryResult<IDirectoryVersionActor, 'U>)
        =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            let correlationId = getCorrelationId context
            let repositoryId = Guid.Parse(parameters.RepositoryId)

            try
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let directoryVersionGuid = Guid.Parse(parameters.DirectoryVersionId)
                    let actorProxy = DirectoryVersion.CreateActorProxy directoryVersionGuid repositoryId correlationId

                    let! queryResult = query context maxCount actorProxy

                    let graceReturnValue = GraceReturnValue.Create queryResult correlationId

                    let graceIds = getGraceIds context
                    graceReturnValue.Properties[ nameof OwnerId ] <- graceIds.OwnerId
                    graceReturnValue.Properties[ nameof OrganizationId ] <- graceIds.OrganizationId
                    graceReturnValue.Properties[ nameof RepositoryId ] <- graceIds.RepositoryId
                    graceReturnValue.Properties[ nameof BranchId ] <- graceIds.BranchId

                    return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError

                    let graceError = GraceError.Create (DirectoryVersionError.getErrorMessage error) correlationId

                    graceError.Properties.Add("Path", context.Request.Path.Value)
                    return! context |> result400BadRequest graceError
            with
            | ex ->
                return!
                    context
                    |> result500ServerError (GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" correlationId)
        }

    /// Create a new directory version.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                /// Implements validations for the server request pipeline.
                let validations (parameters: CreateParameters) =
                    [|
                        String.isNotEmpty $"{parameters.DirectoryVersion.DirectoryVersionId}" DirectoryVersionError.InvalidDirectoryVersionId
                        Guid.isValidAndNotEmptyGuid $"{parameters.DirectoryVersion.DirectoryVersionId}" DirectoryVersionError.InvalidDirectoryVersionId
                        String.isNotEmpty $"{parameters.DirectoryVersion.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                        Guid.isValidAndNotEmptyGuid $"{parameters.DirectoryVersion.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                        String.isNotEmpty $"{parameters.DirectoryVersion.RelativePath}" DirectoryVersionError.RelativePathMustNotBeEmpty
                        String.isNotEmpty $"{parameters.DirectoryVersion.Sha256Hash}" DirectoryVersionError.Sha256HashIsRequired
                        String.isValidSha256Hash $"{parameters.DirectoryVersion.Sha256Hash}" DirectoryVersionError.InvalidSha256Hash
                        Repository.repositoryIdExists
                            graceIds.OrganizationId
                            $"{parameters.DirectoryVersion.RepositoryId}"
                            parameters.CorrelationId
                            DirectoryVersionError.RepositoryDoesNotExist
                    |]

                /// Implements command for the server request pipeline.
                let command (parameters: CreateParameters) (context: HttpContext) =
                    task {
                        let repositoryActorProxy = Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId (getCorrelationId context)

                        let! repositoryDto = repositoryActorProxy.Get(getCorrelationId context)

                        let actorProxy =
                            DirectoryVersion.CreateActorProxy parameters.DirectoryVersion.DirectoryVersionId graceIds.RepositoryId (getCorrelationId context)

                        return! actorProxy.Handle (DirectoryVersionCommand.Create(parameters.DirectoryVersion, repositoryDto)) (Services.createMetadata context)
                    }

                return! processCommand context validations command
            }

    /// Get a directory version.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let repositoryId = Guid.Parse(graceIds.RepositoryIdString)

                /// Implements validations for the server request pipeline.
                let validations (parameters: GetParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid $"{parameters.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                        Guid.isValidAndNotEmptyGuid $"{parameters.DirectoryVersionId}" DirectoryVersionError.InvalidDirectoryVersionId
                        Repository.repositoryIdExists
                            graceIds.OrganizationId
                            $"{parameters.RepositoryId}"
                            parameters.CorrelationId
                            DirectoryVersionError.RepositoryDoesNotExist
                        DirectoryVersion.directoryIdExists
                            (Guid.Parse(parameters.DirectoryVersionId))
                            repositoryId
                            parameters.CorrelationId
                            DirectoryVersionError.DirectoryDoesNotExist
                    |]

                /// Implements query for the server request pipeline.
                let query (context: HttpContext) (maxCount: int) (actorProxy: IDirectoryVersionActor) =
                    task {
                        let! directoryVersionDto = actorProxy.Get(getCorrelationId context)
                        return directoryVersionDto
                    }

                let! parameters = context |> parse<GetParameters>
                return! processQuery context parameters validations 1 query
            }

    /// Get a directory version and all of its children.
    let GetDirectoryVersionsRecursive: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let repositoryId = Guid.Parse(graceIds.RepositoryIdString)

                /// Implements validations for the server request pipeline.
                let validations (parameters: GetParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid $"{parameters.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                        Guid.isValidAndNotEmptyGuid $"{parameters.DirectoryVersionId}" DirectoryVersionError.InvalidDirectoryVersionId
                        Repository.repositoryIdExists
                            graceIds.OrganizationId
                            $"{parameters.RepositoryId}"
                            parameters.CorrelationId
                            DirectoryVersionError.RepositoryDoesNotExist
                        DirectoryVersion.directoryIdExists
                            (Guid.Parse(parameters.DirectoryVersionId))
                            repositoryId
                            parameters.CorrelationId
                            DirectoryVersionError.DirectoryDoesNotExist
                    |]

                /// Implements query for the server request pipeline.
                let query (context: HttpContext) (maxCount: int) (actorProxy: IDirectoryVersionActor) =
                    task {
                        let! directoryVersionDtos = actorProxy.GetRecursiveDirectoryVersions false (getCorrelationId context)

                        return directoryVersionDtos :> IEnumerable<DirectoryVersionDto>
                    }

                let! parameters = context |> parse<GetParameters>
                return! processQuery context parameters validations 1 query
            }

    /// Get a list of directory versions by directory ids.
    let GetByDirectoryIds: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let repositoryId = Guid.Parse(graceIds.RepositoryIdString)

                /// Implements validations for the server request pipeline.
                let validations (parameters: GetByDirectoryIdsParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid $"{parameters.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                        Repository.repositoryIdExists
                            graceIds.OrganizationId
                            $"{parameters.RepositoryId}"
                            parameters.CorrelationId
                            DirectoryVersionError.RepositoryDoesNotExist
                        DirectoryVersion.directoryIdsExist
                            parameters.DirectoryIds
                            repositoryId
                            parameters.CorrelationId
                            DirectoryVersionError.DirectoryDoesNotExist
                    |]

                /// Implements query for the server request pipeline.
                let query (context: HttpContext) (maxCount: int) (actorProxy: IDirectoryVersionActor) =
                    task {
                        let directoryVersionDtos = List<DirectoryVersionDto>()

                        let directoryIds = context.Items[nameof GetByDirectoryIdsParameters] :?> List<DirectoryVersionId>

                        for directoryId in directoryIds do
                            let actorProxy = DirectoryVersion.CreateActorProxy directoryId repositoryId (getCorrelationId context)

                            let! directoryVersionDto = actorProxy.Get(getCorrelationId context)
                            directoryVersionDtos.Add(directoryVersionDto)

                        return directoryVersionDtos :> IEnumerable<DirectoryVersionDto>
                    }

                let! parameters = context |> parse<GetByDirectoryIdsParameters>
                context.Items[ nameof GetByDirectoryIdsParameters ] <- parameters.DirectoryIds
                return! processQuery context parameters validations 1 query
            }

    /// Get a directory version by its SHA256 hash.
    let GetBySha256Hash: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                try
                    let! parameters = context |> parse<GetBySha256HashParameters>

                    let validations =
                        [|
                            Guid.isValidAndNotEmptyGuid $"{parameters.DirectoryVersionId}" DirectoryVersionError.InvalidDirectoryVersionId
                            Guid.isValidAndNotEmptyGuid $"{parameters.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                            String.isNotEmpty parameters.Sha256Hash DirectoryVersionError.Sha256HashIsRequired
                            String.isValidSha256HashPrefix parameters.Sha256Hash DirectoryVersionError.InvalidSha256Hash
                            Repository.repositoryIdExists
                                graceIds.OrganizationId
                                $"{parameters.RepositoryId}"
                                parameters.CorrelationId
                                DirectoryVersionError.RepositoryDoesNotExist
                        |]

                    let! validationsPassed = validations |> allPass

                    if validationsPassed then
                        let! resolution =
                            getDirectoryVersionResolutionBySha256Hash (Guid.Parse(parameters.RepositoryId)) (Sha256Hash parameters.Sha256Hash) correlationId

                        match resolution with
                        | NoMatches ->
                            let graceReturnValue = GraceReturnValue.Create DirectoryVersion.Default correlationId

                            graceReturnValue.Properties[ nameof OwnerId ] <- graceIds.OwnerId
                            graceReturnValue.Properties[ nameof OrganizationId ] <- graceIds.OrganizationId
                            graceReturnValue.Properties[ nameof RepositoryId ] <- graceIds.RepositoryId
                            graceReturnValue.Properties[ nameof BranchId ] <- graceIds.BranchId

                            return! context |> result200Ok graceReturnValue
                        | UniqueMatch directoryVersion ->
                            let graceReturnValue = GraceReturnValue.Create directoryVersion correlationId

                            graceReturnValue.Properties[ nameof OwnerId ] <- graceIds.OwnerId
                            graceReturnValue.Properties[ nameof OrganizationId ] <- graceIds.OrganizationId
                            graceReturnValue.Properties[ nameof RepositoryId ] <- graceIds.RepositoryId
                            graceReturnValue.Properties[ nameof BranchId ] <- graceIds.BranchId

                            return! context |> result200Ok graceReturnValue
                        | AmbiguousMatches _ ->
                            let graceError =
                                GraceError.Create $"The supplied SHA-256 hash prefix '{parameters.Sha256Hash}' is ambiguous in repository scope." correlationId

                            graceError.Properties.Add("Path", context.Request.Path.Value)
                            return! context |> result400BadRequest graceError
                    else
                        let! error = validations |> getFirstError
                        let graceError = GraceError.Create (DirectoryVersionError.getErrorMessage error) correlationId

                        graceError.Properties.Add("Path", context.Request.Path.Value)
                        return! context |> result400BadRequest graceError
                with
                | ex ->
                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" correlationId)
            }

    /// Coordinates add grace ids processing for Grace Server.
    let private addGraceIds (context: HttpContext) (graceReturnValue: GraceReturnValue<'T>) =
        let graceIds = getGraceIds context
        graceReturnValue.Properties[ nameof OwnerId ] <- graceIds.OwnerId
        graceReturnValue.Properties[ nameof OrganizationId ] <- graceIds.OrganizationId
        graceReturnValue.Properties[ nameof RepositoryId ] <- graceIds.RepositoryId
        graceReturnValue.Properties[ nameof BranchId ] <- graceIds.BranchId
        graceReturnValue

    /// Implements directory version hash error for the server request pipeline.
    let private directoryVersionHashError (context: HttpContext) message =
        let graceError = GraceError.Create message (getCorrelationId context)
        graceError.Properties.Add("Path", context.Request.Path.Value)
        graceError

    /// Get a directory version by its BLAKE3 hash or unique BLAKE3 prefix.
    let GetByBlake3Hash: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                try
                    let! parameters = context |> parse<GetByBlake3HashParameters>

                    let validations =
                        [|
                            Guid.isValidAndNotEmptyGuid $"{parameters.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                            String.isNotEmpty parameters.Blake3Hash DirectoryVersionError.Blake3HashIsRequired
                            String.isValidBlake3HashPrefix parameters.Blake3Hash DirectoryVersionError.InvalidBlake3Hash
                            Repository.repositoryIdExists
                                graceIds.OrganizationId
                                $"{parameters.RepositoryId}"
                                parameters.CorrelationId
                                DirectoryVersionError.RepositoryDoesNotExist
                        |]

                    let! validationsPassed = validations |> allPass

                    if validationsPassed then
                        let! resolution =
                            getDirectoryVersionResolutionByBlake3Hash (Guid.Parse(parameters.RepositoryId)) (Blake3Hash parameters.Blake3Hash) correlationId

                        match resolution with
                        | NoMatches ->
                            return!
                                context
                                |> result400BadRequest (
                                    directoryVersionHashError
                                        context
                                        $"No DirectoryVersion matched the supplied BLAKE3 hash prefix '{parameters.Blake3Hash}' in repository scope."
                                )
                        | UniqueMatch directoryVersion ->
                            let graceReturnValue =
                                GraceReturnValue.Create directoryVersion correlationId
                                |> addGraceIds context

                            return! context |> result200Ok graceReturnValue
                        | AmbiguousMatches _ ->
                            return!
                                context
                                |> result400BadRequest (
                                    directoryVersionHashError
                                        context
                                        $"The supplied BLAKE3 hash prefix '{parameters.Blake3Hash}' is ambiguous in repository scope."
                                )
                    else
                        let! error = validations |> getFirstError

                        return!
                            context
                            |> result400BadRequest (directoryVersionHashError context (DirectoryVersionError.getErrorMessage error))
                with
                | ex ->
                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" correlationId)
            }

    /// Get the Uri of the zip file for a directory version.
    let GetZipFile: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let repositoryId = Guid.Parse(graceIds.RepositoryIdString)

                /// Implements validations for the server request pipeline.
                let validations (parameters: GetZipFileParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid $"{parameters.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                        Guid.isValidAndNotEmptyGuid $"{parameters.DirectoryVersionId}" DirectoryVersionError.InvalidDirectoryVersionId
                        Repository.repositoryIdExists
                            graceIds.OrganizationId
                            $"{parameters.RepositoryId}"
                            parameters.CorrelationId
                            DirectoryVersionError.RepositoryDoesNotExist
                        DirectoryVersion.directoryIdExists
                            (Guid.Parse(parameters.DirectoryVersionId))
                            repositoryId
                            parameters.CorrelationId
                            DirectoryVersionError.DirectoryDoesNotExist
                    |]

                /// Implements query for the server request pipeline.
                let query (context: HttpContext) (maxCount: int) (actorProxy: IDirectoryVersionActor) =
                    task {
                        let! zipFile = actorProxy.GetZipFileUri(getCorrelationId context)
                        logToConsole $"In DirectoryVersion.GetZipFile: zipFile: {zipFile}."
                        return zipFile
                    }

                let! parameters = context |> parse<GetZipFileParameters>
                context.Items[ nameof GetZipFileParameters ] <- parameters
                return! processQuery context parameters validations 1 query
            }

    /// Save a list of directory versions.
    let SaveDirectoryVersions: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let repositoryId = Guid.Parse(graceIds.RepositoryIdString)

                /// Implements validations for the server request pipeline.
                let validations (parameters: SaveDirectoryVersionsParameters) =
                    let mutable allValidations: ValueTask<Result<unit, DirectoryVersionError>> array = Array.Empty()

                    for directoryVersion in parameters.DirectoryVersions do
                        let validations =
                            [|
                                String.isNotEmpty $"{directoryVersion.DirectoryVersionId}" DirectoryVersionError.InvalidDirectoryVersionId
                                Guid.isValidAndNotEmptyGuid $"{directoryVersion.DirectoryVersionId}" DirectoryVersionError.InvalidDirectoryVersionId
                                String.isNotEmpty $"{directoryVersion.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                                Guid.isValidAndNotEmptyGuid $"{directoryVersion.RepositoryId}" DirectoryVersionError.InvalidRepositoryId
                                String.isNotEmpty $"{directoryVersion.Sha256Hash}" DirectoryVersionError.Sha256HashIsRequired
                                String.isValidSha256Hash $"{directoryVersion.Sha256Hash}" DirectoryVersionError.InvalidSha256Hash
                                String.isNotEmpty $"{directoryVersion.RelativePath}" DirectoryVersionError.RelativePathMustNotBeEmpty
                                Repository.repositoryIdExists
                                    graceIds.OrganizationId
                                    $"{directoryVersion.RepositoryId}"
                                    parameters.CorrelationId
                                    DirectoryVersionError.RepositoryDoesNotExist
                            |]

                        allValidations <- Array.append allValidations validations

                    allValidations

                /// Implements command for the server request pipeline.
                let command (parameters: SaveDirectoryVersionsParameters) (context: HttpContext) =
                    task {
                        let correlationId = getCorrelationId context
                        let results = ConcurrentQueue<GraceResult<string>>()

                        let repositoryActorProxy = Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId correlationId
                        let! repositoryDto = repositoryActorProxy.Get(correlationId)

                        let orderedDirectoryVersions =
                            parameters.DirectoryVersions
                            |> Seq.sortByDescending (fun directoryVersion -> directorySaveDepth directoryVersion.RelativePath)
                            |> Seq.toArray

                        for directoryVersion in orderedDirectoryVersions do
                            try
                                // Check if the directory version exists. If it doesn't, create it.
                                let directoryVersionActor = DirectoryVersion.CreateActorProxy directoryVersion.DirectoryVersionId repositoryId correlationId

                                let! exists = directoryVersionActor.Exists parameters.CorrelationId
                                //logToConsole $"In SaveDirectoryVersions: {dv.DirectoryId} exists: {exists}"
                                if not <| exists then
                                    if String.IsNullOrWhiteSpace $"{directoryVersion.Blake3Hash}" then
                                        results.Enqueue(
                                            Error(
                                                GraceError.Create
                                                    $"DirectoryVersion '{directoryVersion.RelativePath}' must include DirectoryVersion.Blake3Hash before Save."
                                                    correlationId
                                            )
                                        )
                                    else
                                        let! createResult =
                                            directoryVersionActor.Handle
                                                (DirectoryVersionCommand.Create(directoryVersion, repositoryDto))
                                                (createMetadata context)

                                        results.Enqueue(createResult)
                            with
                            | ex ->
                                let exceptionResponse = Utilities.ExceptionResponse.Create ex

                                logToConsole
                                    $"****Error in SaveDirectoryVersions: directoryVersion.Directories.Count: {directoryVersion.Directories.Count}; directoryVersion.Files.Count: {directoryVersion.Files.Count}."

                                logToConsole $"{exceptionResponse}"

                        let firstError =
                            results
                            |> Seq.tryFind (fun result ->
                                match result with
                                | Ok _ -> false
                                | Error _ -> true)

                        match firstError with
                        | None -> return Ok(GraceReturnValue.Create "Uploaded new directory versions." correlationId)
                        | Some error ->
                            let sb = stringBuilderPool.Get()

                            try
                                for result in results do
                                    match result with
                                    | Ok _ -> ()
                                    | Error error -> sb.Append($"{error.Error}; ") |> ignore

                                return Error(GraceError.Create (sb.ToString()) correlationId)
                            finally
                                stringBuilderPool.Return(sb)
                    }

                return! processCommand context validations command
            }
