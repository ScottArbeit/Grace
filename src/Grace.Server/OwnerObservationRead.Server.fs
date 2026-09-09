namespace Grace.Server

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Giraffe
open Grace.Operations.Data
open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Parameters.Repository
open Grace.Shared.Utilities
open Grace.Types.Authorization
open Grace.Types.Common
open Grace.Types.Usage
open Grace.Types.UsageObservation
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration

/// Reads retained DirectoryVersion declarations using permission on their recorded owner.
module OwnerObservationRead =
    /// Keeps every quantity digit on this public route without changing shared or admin JSON.
    let internal jsonOptions =
        let options = JsonSerializerOptions(Constants.JsonSerializerOptions)

        options.NumberHandling <-
            options.NumberHandling
            ||| JsonNumberHandling.WriteAsString

        options

    /// Parses historical scope without consulting current entities or resolving names.
    let internal parseRequest (observationIdText: string) (context: HttpContext) =
        let parameters =
            GetRepositoryParameters(
                OwnerId = string context.Request.Query["OwnerId"],
                OrganizationId = string context.Request.Query["OrganizationId"],
                RepositoryId = string context.Request.Query["RepositoryId"],
                OwnerName = string context.Request.Query["OwnerName"],
                OrganizationName = string context.Request.Query["OrganizationName"],
                RepositoryName = string context.Request.Query["RepositoryName"]
            )

        match Guid.TryParse observationIdText, DirectoryVersion.validateSizeDiagnosticParameters parameters with
        | (true, id), Ok scope when id <> Guid.Empty -> Ok(id, scope)
        | _, Error message -> Error message
        | _ -> Error "ObservationId must be a non-empty GUID."

    /// Resolves only explicit IDs after the existing middleware has authenticated the caller.
    let private resolve observationId context =
        parseRequest observationId context
        |> Result.map (fun (_, scope) -> Operation.OwnerAdmin, Resource.Owner scope.OwnerId)
        |> Result.mapError (fun message -> GraceError.Create message (Services.getCorrelationId context))
        |> Task.FromResult

    /// Rechecks permissions after one immutable read, including missing and conflicting rows.
    let internal readWith
        (check: unit -> Task<PermissionCheckResult>)
        (lookup: CancellationToken -> Task<Result<DirectoryVersionSizeObservation option, string>>)
        (observationId: Guid)
        (scope: UsageFactScope)
        (token: CancellationToken)
        =
        task {
            try
                token.ThrowIfCancellationRequested()
                let! stored = lookup token

                let! permission =
                    task {
                        try
                            return! check ()
                        with
                        | _ -> return Denied "Forbidden."
                    }

                match permission with
                | Denied _ -> return Error 403
                | Allowed _ ->
                    token.ThrowIfCancellationRequested()

                    match stored with
                    | Ok (Some observation) when
                        observation.ObservationId = observationId
                        && observation.Scope = scope
                        ->
                        return Ok observation
                    | _ -> return Error 404
            with
            | _ -> return Error 503
        }

    /// Executes the registered read with an injectable immutable lookup for bounded failure tests.
    let internal handleWith
        (lookup: HttpContext -> Guid -> UsageFactScope -> CancellationToken -> Task<Result<DirectoryVersionSizeObservation option, string>>)
        observationId
        : HttpHandler
        =
        AuthorizationMiddleware.requiresPermissionResolved (resolve observationId)
        >=> fun next context ->
                task {
                    let correlationId = Services.getCorrelationId context

                    try
                        match parseRequest observationId context with
                        | Error message -> return! RequestErrors.BAD_REQUEST (GraceError.Create message correlationId) next context
                        | Ok (id, scope) ->
                            use deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted)
                            deadline.CancelAfter(TimeSpan.FromSeconds 30.)
                            let evaluator = context.GetService<IGracePermissionEvaluator>()

                            /// Reloads assignments for the same authenticated request identity and recorded owner.
                            let check () =
                                evaluator.CheckAsync(
                                    PrincipalMapper.getPrincipals context.User,
                                    PrincipalMapper.getEffectiveClaims context.User,
                                    Operation.OwnerAdmin,
                                    Resource.Owner scope.OwnerId
                                )

                            let! result = readWith check (lookup context id scope) id scope deadline.Token

                            match result with
                            | Error status ->
                                let message =
                                    if status = 403 then "Forbidden."
                                    elif status = 404 then "Observation was not found."
                                    else "Observation read failed or was cancelled. Retry the read."

                                return!
                                    (setStatusCode status
                                     >=> json (GraceError.Create message correlationId))
                                        next
                                        context
                            | Ok observation ->
                                let body = JsonSerializer.Serialize(GraceReturnValue.Create observation correlationId, jsonOptions)
                                deadline.Token.ThrowIfCancellationRequested()

                                return!
                                    (setHttpHeader "Content-Type" "application/json; charset=utf-8"
                                     >=> setBodyFromString body)
                                        next
                                        context
                    with
                    | _ ->
                        return!
                            ServerErrors.SERVICE_UNAVAILABLE
                                (GraceError.Create "Observation read failed or was cancelled. Retry the read." correlationId)
                                next
                                context
                }

    /// Reads existing SQL facts without source, provider, capture, or current repository access.
    let Read observationId =
        handleWith
            (fun context id scope token ->
                task {
                    let configuration = context.GetService<IConfiguration>()
                    let connectionString = configuration[getConfigKey "grace__operations__sql__connectionstring"]

                    if String.IsNullOrWhiteSpace connectionString then
                        invalidOp "Operations SQL is not configured."

                    return! DirectoryVersionSizeObservations.lookup connectionString id scope token
                })
            observationId
