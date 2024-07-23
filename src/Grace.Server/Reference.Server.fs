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

    let getReferenceActorProxy (referenceId: string) =
        let actorId = ActorId(referenceId)
        actorProxyFactory.CreateActorProxy<IReferenceActor>(actorId, ActorName.Reference)

    let processCommand<'T when 'T :> ReferenceParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<ReferenceCommand>) =
        task {
            let commandName = context.Items["Command"] :?> string
            let graceIds = getGraceIds context

            try
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>

                let handleCommand branchId cmd =
                    task {
                        let actorProxy = getReferenceActorProxy branchId

                        match! actorProxy.Handle cmd (createMetadata context) with
                        | Ok graceReturnValue ->
                            graceReturnValue
                                .enhance(nameof(OwnerId), graceIds.OwnerId)
                                .enhance(nameof(OrganizationId), graceIds.OrganizationId)
                                .enhance(nameof(RepositoryId), graceIds.RepositoryId)
                                .enhance(nameof(BranchId), graceIds.BranchId)
                                .enhance(nameof(Parameters), serialize parameters)
                                .enhance("Command", commandName)
                                .enhance("Path", context.Request.Path) |> ignore

                            return! context |> result200Ok graceReturnValue
                        | Error graceError ->
                            graceError
                                .enhance(nameof(OwnerId), graceIds.OwnerId)
                                .enhance(nameof(OrganizationId), graceIds.OrganizationId)
                                .enhance(nameof(RepositoryId), graceIds.RepositoryId)
                                .enhance(nameof(BranchId), graceIds.BranchId)
                                .enhance(nameof(Parameters), serialize parameters)
                                .enhance("Command", commandName)
                                .enhance("Path", context.Request.Path) |> ignore

                            log.LogDebug(
                                "{currentInstant}: In Reference.Server.handleCommand: error from actorProxy.Handle: {error}",
                                getCurrentInstantExtended (),
                                (graceError.ToString())
                            )

                            return!
                                context
                                |> result400BadRequest graceError
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

                    return! handleCommand graceIds.BranchId cmd
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = ReferenceError.getErrorMessage error

                    log.LogDebug("{currentInstant}: error: {error}", getCurrentInstantExtended (), errorMessage)

                    let graceError =
                        (GraceError.CreateWithMetadata errorMessage (getCorrelationId context) (getParametersAsDictionary parameters))
                            .enhance(nameof(OwnerId), graceIds.OwnerId)
                            .enhance(nameof(OrganizationId), graceIds.OrganizationId)
                            .enhance(nameof(RepositoryId), graceIds.RepositoryId)
                            .enhance(nameof(BranchId), graceIds.BranchId)
                            .enhance(nameof(Parameters), serialize parameters)
                            .enhance("Command", commandName)
                            .enhance("Path", context.Request.Path)
                            .enhance("Error", errorMessage)

                    return! context |> result400BadRequest graceError
            with ex ->
                let graceError =
                    (GraceError.Create $"{Utilities.createExceptionResponse ex}" (getCorrelationId context))
                        .enhance(nameof(OwnerId), graceIds.OwnerId)
                        .enhance(nameof(OrganizationId), graceIds.OrganizationId)
                        .enhance(nameof(RepositoryId), graceIds.RepositoryId)
                        .enhance(nameof(BranchId), graceIds.BranchId)
                        .enhance("Command", commandName)
                        .enhance("Path", context.Request.Path)

                return! context |> result500ServerError graceError
        }
