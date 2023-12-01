namespace Grace.Server

open Dapr.Actors
open Dapr.Actors.Client
open Giraffe
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Parameters.Diff
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors.Diff
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
    type Validations<'T when 'T :> DiffParameters> = 'T -> HttpContext -> ValueTask<Result<unit, DiffError>> array

    let activitySource = new ActivitySource("Repository")

    let actorProxyFactory = ApplicationContext.actorProxyFactory

    let getActorProxy directoryId1 directoryId2 (context: HttpContext) =
        let actorId = Diff.GetActorId directoryId1 directoryId2
        actorProxyFactory.CreateActorProxy<IDiffActor>(actorId, ActorName.Diff)

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
    //                graceError.Properties.Add("Path", context.Request.Path)
    //                return! context |> result400BadRequest graceError
    //        with ex ->
    //            return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
    //    }

    let processQuery<'T, 'U when 'T :> DiffParameters> (context: HttpContext) (parameters: 'T) (validations: Validations<'T>) (query: QueryResult<IDiffActor, 'U>) =
        task {
            try
                use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
                //let! parameters = context |> parse<'T>
                let validationResults = validations parameters context
                let! validationsPassed = validationResults |> allPass
                if validationsPassed then
                    let actorProxy = getActorProxy parameters.DirectoryId1 parameters.DirectoryId2 context

                    //// Need to figure this whole part out next. 
                    //// Then add SDK implementation of GetDiff.
                    //// Then add CLI command to get diff.
                    //// Then test diffs end-to-end.
                    //// Then format the CLI output properly.
                    //| Some diff -> 
                    //| None ->

                    let! queryResult = query context 1 actorProxy
                    let returnValue = GraceReturnValue.Create queryResult (getCorrelationId context)
                    returnValue.Properties.Add($"DirectoryId1", $"{parameters.DirectoryId1}")
                    returnValue.Properties.Add($"DirectoryId2", $"{parameters.DirectoryId2}")
                    return! context |> result200Ok returnValue
                else
                    let! error = validationResults |> getFirstError
                    let graceError = GraceError.Create (DiffError.getErrorMessage error) (getCorrelationId context)
                    graceError.Properties.Add("Path", context.Request.Path)
                    graceError.Properties.Add($"DirectoryId1", $"{parameters.DirectoryId1}")
                    graceError.Properties.Add($"DirectoryId2", $"{parameters.DirectoryId2}")
                    return! context |> result400BadRequest graceError
            with ex ->
                return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
        }

    /// Populates the diff actor, without returning the diff. This is meant to be used when generating the diff through reacting to an event.
    let Populate: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: PopulateParameters) (context: HttpContext) =
                        [| Guid.isNotEmpty parameters.DirectoryId1 DiffError.InvalidDirectoryId
                           Guid.isNotEmpty parameters.DirectoryId2 DiffError.InvalidDirectoryId
                           Directory.directoryIdExists parameters.DirectoryId1 DiffError.DirectoryDoesNotExist
                           Directory.directoryIdExists parameters.DirectoryId2 DiffError.DirectoryDoesNotExist |]

                    let query (context: HttpContext) _ (actorProxy: IDiffActor) =
                        task {
                            let! populated = actorProxy.Populate()
                            return populated
                        }

                    let! parameters = context |> parse<PopulateParameters>
                    return! processQuery context parameters validations query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
        }

    /// Retrieves the contents of the diff.
    let GetDiff: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetDiffParameters) (context: HttpContext) =
                        [| Guid.isNotEmpty parameters.DirectoryId1 DiffError.InvalidDirectoryId
                           Guid.isNotEmpty parameters.DirectoryId2 DiffError.InvalidDirectoryId
                           Directory.directoryIdExists parameters.DirectoryId1 DiffError.DirectoryDoesNotExist
                           Directory.directoryIdExists parameters.DirectoryId2 DiffError.DirectoryDoesNotExist |]

                    let query (context: HttpContext) _ (actorProxy: IDiffActor) =
                        task {
                            let! diff = actorProxy.GetDiff()
                            return diff
                        }

                    let! parameters = context |> parse<GetDiffParameters>

                    return! processQuery context parameters validations query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
        }

    /// Retrieves a diff taken by comparing two DirectoryVersions by Sha256Hash.
    let GetDiffBySha256Hash: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let validations (parameters: GetDiffBySha256HashParameters) (context: HttpContext) =
                        [| String.isNotEmpty parameters.Sha256Hash1 DiffError.Sha256HashIsRequired
                           String.isNotEmpty parameters.Sha256Hash2 DiffError.Sha256HashIsRequired
                           String.isValidSha256Hash parameters.Sha256Hash1 DiffError.InvalidSha256Hash
                           String.isValidSha256Hash parameters.Sha256Hash2 DiffError.InvalidSha256Hash |]

                    let query (context: HttpContext) _ (actorProxy: IDiffActor) =
                        task {
                            let! diff = actorProxy.GetDiff()
                            return diff
                        }

                    let! parameters = context |> parse<GetDiffBySha256HashParameters>
                    return! processQuery context parameters validations query
                with ex ->
                    return! context |> result500ServerError (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
        }
