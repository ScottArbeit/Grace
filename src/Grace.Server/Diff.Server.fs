namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Parameters.Diff
open Grace.Types.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Repository
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Logging
open NodaTime
open System
open OpenTelemetry.Trace
open System.Globalization
open System.Diagnostics
open System.Threading.Tasks
open System.Text.Json

module Diff =
    type Validations<'T when 'T :> DiffParameters> = 'T -> ValueTask<Result<unit, DiffError>> array

    let activitySource = new ActivitySource("Diff")

    ///let processCommand<'T when 'T :> DiffParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> Task<DiffCommand>) =
    //    task {
    //        try
    //            use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
    //            let! parameters = context |> parse<'T>
    //            let validationResults = validations parameters context
    //            let! validationsPassed = validationResults |> areValid
    //            if validationsPassed then
    //                let! repositoryId = resolveRepositoryId parameters.OwnerId parameters.OwnerName parameters.OrganizationId parameters.OrganizationName parameters.RepositoryId parameters.RepositoryName
    //                match repositoryId with
    //                | Some repositoryId ->
    //                    if String.IsNullOrEmpty(parameters.RepositoryId) then parameters.RepositoryId <- repositoryId
    //                    let actorProxy = getActorProxy repositoryId context
    //                    let! cmd = command parameters
    //                    let! result = actorProxy.Handle cmd (Services.createMetadata context)
    //                    match result with
    //                        | Ok graceReturn ->
    //                            match cmd with
    //                            | Create _ ->
    //                                let branchId = (BranchId (Guid.NewGuid()))
    //                                let branchActorId = Branch.GetActorId branchId
    //                                let branchActor = context.GetService<IActorProxyFactory>().CreateActorProxy<IBranchActor>(branchActorId, ActorName.Branch)
    //                                let! result = branchActor.Handle (Branch.BranchCommand.Create (branchId, (BranchName Constants.InitialBranchName), (BranchId.Root), (Guid.Parse(parameters.RepositoryId)))) (Services.createMetadata context)
    //                                match result with
    //                                | Ok branchGraceReturn ->
    //                                    do graceReturn.Properties.Add(nameof(BranchId), $"{branchId}")
    //                                    do graceReturn.Properties.Add(nameof(BranchName), Constants.InitialBranchName)
    //                                    return! context |> result200Ok graceReturn
    //                                | Error graceError -> return! context |> result400BadRequest graceError
    //                            | _ ->
    //                                return! context |> result200Ok graceReturn
    //                        | Error graceError -> return! context |> result400BadRequest graceError
    //                | None -> return! context |> result400BadRequest (GraceError.Create (RepositoryError.getErrorMessage RepositoryError.RepositoryDoesNotExist) (getCorrelationId context))
    //            else
    //                let! error = validationResults |> getFirstError
    //                let graceError = GraceError.Create (RepositoryError.getErrorMessage error) (getCorrelationId context)
    //                graceError.Properties.Add("Path", context.Request.Path.Value)
    //                return! context |> result400BadRequest graceError
    //        with ex ->
    //            return! context |> result500ServerError (GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (getCorrelationId context))
    //    }

    let processQuery<'T, 'U when 'T :> DiffParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        (query: QueryResult<IDiffActor, 'U>)
        =
        task {
            let correlationId = getCorrelationId context
            let graceIds = getGraceIds context

            try
                use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
                //let! parameters = context |> parse<'T>
                let validationResults = validations parameters
                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    let actorProxy =
                        Diff.CreateActorProxy
                            parameters.DirectoryVersionId1
                            parameters.DirectoryVersionId2
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            correlationId

                    //// Need to figure this whole part out next.
                    //// Then add SDK implementation of GetDiff.
                    //// Then add CLI command to get diff.
                    //// Then test diffs end-to-end.
                    //// Then format the CLI output properly.
                    //| Some diff ->
                    //| None ->

                    let! queryResult = query context 1 actorProxy
                    let returnValue = GraceReturnValue.Create queryResult (getCorrelationId context)
                    returnValue.Properties.Add($"DirectoryVersionId1", $"{parameters.DirectoryVersionId1}")
                    returnValue.Properties.Add($"DirectoryVersionId2", $"{parameters.DirectoryVersionId2}")
                    return! context |> result200Ok returnValue
                else
                    let! error = validationResults |> getFirstError

                    let graceError = GraceError.Create (getErrorOptionMessage error) (getCorrelationId context)

                    graceError.Properties.Add("Path", context.Request.Path.Value)
                    graceError.Properties.Add($"DirectoryVersionId1", $"{parameters.DirectoryVersionId1}")
                    graceError.Properties.Add($"DirectoryVersionId2", $"{parameters.DirectoryVersionId2}")
                    return! context |> result400BadRequest graceError
            with
            | ex ->
                return!
                    context
                    |> result500ServerError (GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (getCorrelationId context))
        }

    /// Populates the diff actor, without returning the diff. This is meant to be used when generating the diff through reacting to an event.
    let Populate: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let graceIds = getGraceIds context
                    let repositoryId = Guid.Parse(graceIds.RepositoryIdString)

                    let validations (parameters: PopulateParameters) =
                        [|
                            Guid.isNotEmpty parameters.DirectoryVersionId1 DiffError.InvalidDirectoryVersionId
                            Guid.isNotEmpty parameters.DirectoryVersionId2 DiffError.InvalidDirectoryVersionId
                            DirectoryVersion.directoryIdExists
                                parameters.DirectoryVersionId1
                                repositoryId
                                parameters.CorrelationId
                                DiffError.DirectoryDoesNotExist
                            DirectoryVersion.directoryIdExists
                                parameters.DirectoryVersionId2
                                repositoryId
                                parameters.CorrelationId
                                DiffError.DirectoryDoesNotExist
                        |]

                    let query (context: HttpContext) _ (actorProxy: IDiffActor) =
                        task {
                            let! populated = actorProxy.Compute(getCorrelationId context)
                            return populated
                        }

                    let! parameters = context |> parse<PopulateParameters>
                    return! processQuery context parameters validations query
                with
                | ex ->
                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Retrieves the contents of the diff.
    let GetDiff: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let graceIds = getGraceIds context
                    let repositoryId = Guid.Parse(graceIds.RepositoryIdString)

                    let validations (parameters: GetDiffParameters) =
                        [|
                            Guid.isNotEmpty parameters.DirectoryVersionId1 DiffError.InvalidDirectoryVersionId
                            Guid.isNotEmpty parameters.DirectoryVersionId2 DiffError.InvalidDirectoryVersionId
                            DirectoryVersion.directoryIdExists
                                parameters.DirectoryVersionId1
                                repositoryId
                                parameters.CorrelationId
                                DiffError.DirectoryDoesNotExist
                            DirectoryVersion.directoryIdExists
                                parameters.DirectoryVersionId2
                                repositoryId
                                parameters.CorrelationId
                                DiffError.DirectoryDoesNotExist
                        |]

                    let query (context: HttpContext) _ (actorProxy: IDiffActor) =
                        task {
                            logToConsole $"About to call DiffActor.GetDiff()."
                            let! diff = actorProxy.GetDiff(getCorrelationId context)
                            logToConsole $"After calling DiffActor.GetDiff()."
                            return diff
                        }

                    let! parameters = context |> parse<GetDiffParameters>

                    return! processQuery context parameters validations query
                with
                | ex ->
                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (getCorrelationId context))
            }

    /// Retrieves a diff taken by comparing two DirectoryVersions by Sha256Hash.
    let GetDiffBySha256Hash: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetDiffBySha256HashParameters) =
                        [|
                            String.isNotEmpty parameters.Sha256Hash1 DiffError.Sha256HashIsRequired
                            String.isNotEmpty parameters.Sha256Hash2 DiffError.Sha256HashIsRequired
                            String.isValidSha256Hash parameters.Sha256Hash1 DiffError.InvalidSha256Hash
                            String.isValidSha256Hash parameters.Sha256Hash2 DiffError.InvalidSha256Hash
                        |]

                    let query (context: HttpContext) _ (actorProxy: IDiffActor) =
                        task {
                            let! diff = actorProxy.GetDiff(getCorrelationId context)
                            return diff
                        }

                    let! parameters = context |> parse<GetDiffBySha256HashParameters>

                    let graceIds = getGraceIds context
                    let repositoryId = Guid.Parse(graceIds.RepositoryIdString)
                    let! directoryVersionId1 = getDirectoryVersionBySha256Hash repositoryId parameters.Sha256Hash1 (getCorrelationId context)
                    let! directoryVersionId2 = getDirectoryVersionBySha256Hash repositoryId parameters.Sha256Hash2 (getCorrelationId context)

                    match directoryVersionId1, directoryVersionId2 with
                    | Some directoryVersionId1, Some directoryVersionId2 ->
                        parameters.DirectoryVersionId1 <- directoryVersionId1.DirectoryVersionId
                        parameters.DirectoryVersionId2 <- directoryVersionId2.DirectoryVersionId
                    | _ -> ()

                    return! processQuery context parameters validations query
                with
                | ex ->
                    return!
                        context
                        |> result500ServerError (GraceError.Create $"{Utilities.ExceptionResponse.Create ex}" (getCorrelationId context))
            }
