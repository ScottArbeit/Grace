namespace Grace.Server

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Parameters.Repository
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Usage
open Grace.Types.UsageObservation
open Grace.Operations.Data
open Microsoft.Extensions.Configuration

/// Captures and reads immutable completed declarations through the SystemAdmin routes.
module TextContentSizeObservation =
    /// Reads prior SQL state first, then checks current repository scope on both sides of a caller-cancellable collection.
    let internal captureWith
        (lookup: CancellationToken -> Task<Result<TextContentSizeObservation option, string>>)
        (checkRepository: CancellationToken -> Task<unit>)
        (collect: CancellationToken -> Task<TextContentSizeObservation>)
        (accept: TextContentSizeObservation -> CancellationToken -> Task<Result<TextContentSizeObservation, string>>)
        (token: CancellationToken)
        =
        task {
            token.ThrowIfCancellationRequested()
            let! existing = lookup token

            match existing with
            | Error message -> return Error message
            | Ok (Some stored) -> return Ok stored
            | Ok None ->
                do! checkRepository token
                let! completed = collect token
                do! checkRepository token
                token.ThrowIfCancellationRequested()
                return! accept completed token
        }

    /// Requires an existing nondeleted repository with all requested scope IDs immediately around collection.
    let private checkRepository (scope: UsageFactScope) correlationId (token: CancellationToken) =
        task {
            let repositoryActor = Repository.CreateActorProxy scope.OrganizationId scope.RepositoryId correlationId

            let! repository =
                (repositoryActor.Get correlationId)
                    .WaitAsync(token)

            if repository.RepositoryId <> scope.RepositoryId
               || repository.OwnerId <> scope.OwnerId
               || repository.OrganizationId <> scope.OrganizationId
               || repository.UpdatedAt.IsNone
               || repository.DeletedAt.IsSome then
                raise (InvalidDataException "Repository does not exist, is deleted, or does not match the requested scope.")
        }

    /// Parses explicit scope only after route authorization and maps unavailable outcomes without promising rollback.
    let private handle capture (observationIdText: string) : HttpHandler =
        fun next context ->
            task {
                let correlationId = Grace.Server.Services.getCorrelationId context

                try
                    let observationId =
                        match Guid.TryParse observationIdText with
                        | true, id when id <> Guid.Empty -> id
                        | _ -> raise (InvalidDataException "ObservationId must be a non-empty GUID.")

                    let! parameters =
                        if capture then
                            context.BindJsonAsync<GetRepositoryParameters>()
                        else
                            Task.FromResult(
                                GetRepositoryParameters(
                                    OwnerId = string context.Request.Query["OwnerId"],
                                    OrganizationId = string context.Request.Query["OrganizationId"],
                                    RepositoryId = string context.Request.Query["RepositoryId"],
                                    OwnerName = string context.Request.Query["OwnerName"],
                                    OrganizationName = string context.Request.Query["OrganizationName"],
                                    RepositoryName = string context.Request.Query["RepositoryName"]
                                )
                            )

                    match WorkItem.validateTextContentSizeDiagnosticParameters parameters with
                    | Error message -> return! RequestErrors.BAD_REQUEST (GraceError.Create message correlationId) next context
                    | Ok scope ->
                        let configuration = context.GetService<IConfiguration>()
                        let connectionString = configuration[getConfigKey "grace__operations__sql__connectionstring"]

                        if String.IsNullOrWhiteSpace connectionString then
                            raise (InvalidOperationException "Operations SQL is not configured.")

                        let lookup = TextContentSizeObservations.lookup connectionString observationId scope

                        let! result =
                            if capture then
                                task {
                                    let! accepted =
                                        captureWith
                                            lookup
                                            (checkRepository scope correlationId)
                                            (fun token ->
                                                task {
                                                    let! total, count, started, finished = readTextContentSize scope token

                                                    return
                                                        {
                                                            ObservationId = observationId
                                                            Scope = scope
                                                            DeclaredTextContentUtf8Bytes = total
                                                            DistinctTextContentCount = count
                                                            EnumerationStartedAt = started
                                                            EnumerationFinishedAt = finished
                                                        }
                                                })
                                            (TextContentSizeObservations.accept connectionString)
                                            context.RequestAborted

                                    return Result.map Some accepted
                                }
                            else
                                lookup context.RequestAborted

                        match result with
                        | Error message -> return! RequestErrors.CONFLICT (GraceError.Create message correlationId) next context
                        | Ok None -> return! RequestErrors.NOT_FOUND (GraceError.Create "Observation was not found." correlationId) next context
                        | Ok (Some stored) -> return! json (GraceReturnValue.Create stored correlationId) next context
                with
                | :? System.Text.Json.JsonException ->
                    return! RequestErrors.BAD_REQUEST (GraceError.Create "The request body must be valid repository-scope JSON." correlationId) next context
                | :? InvalidDataException as error -> return! RequestErrors.BAD_REQUEST (GraceError.Create error.Message correlationId) next context
                | _ ->
                    return!
                        ServerErrors.SERVICE_UNAVAILABLE
                            (GraceError.Create
                                "Observation request failed or was cancelled. Retry with the same ObservationId to resolve any uncertain commit."
                                correlationId)
                            next
                            context
            }

    /// Captures a new reading or returns the immutable matching SQL winner.
    let Capture observationId = handle true observationId

    /// Reads historical observations without requiring current repository existence.
    let Read observationId = handle false observationId
