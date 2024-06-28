namespace Grace.Server

open Dapr.Actors
open Giraffe
open Grace.Actors
open Grace.Actors.Commands.Reference
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Parameters.Reference
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Utilities
open Grace.Shared.Validation.Errors.Reference
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open System
open System.Diagnostics
open System.Threading.Tasks

module Reference =

    type Validations<'T when 'T :> ReferenceParameters> = 'T -> ValueTask<Result<unit, ReferenceError>>[]

    let activitySource = new ActivitySource("Reference")

    let log = ApplicationContext.loggerFactory.CreateLogger("Branch.Server")

    let actorProxyFactory = ApplicationContext.actorProxyFactory

    let getActorProxy (context: HttpContext) (referenceId: string) =
        let actorId = ActorId($"{referenceId}")
        actorProxyFactory.CreateActorProxy<IReferenceActor>(actorId, ActorName.Reference)

    let processCommand<'T when 'T :> ReferenceParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<ReferenceCommand>) =
        task {
            try
                let commandName = context.Items["Command"] :?> string
                let graceIds = context.Items[nameof (GraceIds)] :?> GraceIds
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>

                let handleCommand branchId cmd =
                    task {
                        let actorProxy = getActorProxy context branchId

                        match! actorProxy.Handle cmd (createMetadata context) with
                        | Ok graceReturnValue ->
                            let graceIds = getGraceIds context
                            graceReturnValue.Properties[nameof (OwnerId)] <- graceIds.OwnerId
                            graceReturnValue.Properties[nameof (OrganizationId)] <- graceIds.OrganizationId
                            graceReturnValue.Properties[nameof (RepositoryId)] <- graceIds.RepositoryId
                            graceReturnValue.Properties[nameof (BranchId)] <- graceIds.BranchId

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            log.LogDebug(
                                "{currentInstant}: In Reference.Server.handleCommand: error from actorProxy.Handle: {error}",
                                getCurrentInstantExtended (),
                                (graceError.ToString())
                            )

                            return!
                                context
                                |> result400BadRequest { graceError with Properties = getPropertiesAsDictionary parameters }
                    }

                let validationResults = validations parameters

                let! validationsPassed = validationResults |> allPass

                log.LogDebug(
                    "{currentInstant}: In Reference.Server.processCommand: validationsPassed: {validationsPassed}.",
                    getCurrentInstantExtended (),
                    validationsPassed
                )

                if validationsPassed then
                    let! cmd = command parameters

                    let! branchId = resolveBranchId graceIds.RepositoryId parameters.BranchId parameters.BranchName parameters.CorrelationId

                    match branchId, commandName = nameof (Create) with
                    | Some branchId, _ ->
                        // If Id is Some, then we know we have a valid Id.
                        if String.IsNullOrEmpty(parameters.BranchId) then
                            parameters.BranchId <- branchId

                        return! handleCommand branchId cmd
                    | None, true ->
                        // If it's None, but this is a Create command, still valid, just use the Id from the parameters.
                        return! handleCommand parameters.BranchId cmd
                    | None, false ->
                        // If it's None, and this is not a Create command, then we have a bad request.
                        log.LogDebug(
                            "{currentInstant}: In Branch.Server.processCommand: resolveBranchId failed. Branch does not exist. repositoryId: {repositoryId}; repositoryName: {repositoryName}.",
                            getCurrentInstantExtended (),
                            parameters.RepositoryId,
                            parameters.RepositoryName
                        )

                        return!
                            context
                            |> result400BadRequest (GraceError.Create (ReferenceError.getErrorMessage RepositoryDoesNotExist) (getCorrelationId context))
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = ReferenceError.getErrorMessage error
                    log.LogDebug("{currentInstant}: error: {error}", getCurrentInstantExtended (), errorMessage)

                    let graceError = GraceError.CreateWithMetadata errorMessage (getCorrelationId context) (getPropertiesAsDictionary parameters)

                    graceError.Properties.Add("Path", context.Request.Path)
                    graceError.Properties.Add("Error", errorMessage)
                    return! context |> result400BadRequest graceError
            with ex ->
                let graceError = GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context)

                graceError.Properties.Add("Path", context.Request.Path)
                return! context |> result500ServerError graceError
        }
