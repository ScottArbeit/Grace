namespace Grace.Server.Middleware

open Grace.Actors.Services
open Grace.Server
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Types.Common
open Grace.Shared.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Net
open System.Threading.Tasks
open System.Text
open System.Text.Json
open Grace.Actors.Constants.ActorName

/// Examines the body of the incoming request to validate the Ids and Names in the request, and ensure that we know the right Ids. Having the Ids already figured out saves work for the rest of the pipeline.
///
/// If the Ids are invalid, it returns 400 Bad Request.
///
/// If the Ids and/or Names aren't found, it returns 404 Not Found.
type ValidateIdsMiddleware(next: RequestDelegate) =

    let log = ApplicationContext.loggerFactory.CreateLogger($"{nameof ValidateIdsMiddleware}.Server")

    /// Holds the request body type for each endpoint.
    let typeLookup = ConcurrentDictionary<String, Type>()

    /// Holds the property info for each request body type.
    let propertyLookup = ConcurrentDictionary<Type, EntityProperties>()

    /// Gets the parameter type for the endpoint from the endpoint metadata created in Startup.Server.fs.
    let getBodyType (context: HttpContext) =
        let path = context.Request.Path.ToString()

        if not <| ValidateIdsDecisions.isIgnoredPath path then
            let endpoint = context.GetEndpoint()

            if isNull (endpoint) then
                log.LogDebug("{CurrentInstant}: Path: {path}; Endpoint: null.", getCurrentInstantExtended (), path)
                None
            elif endpoint.Metadata.Count > 0 then
                let requestBodyType = ValidateIdsDecisions.tryGetBodyType path endpoint

                if requestBodyType |> Option.isSome then
                    log.LogDebug(
                        "{CurrentInstant}: Path: {path}; Endpoint: {endpoint.DisplayName}; RequestBodyType: {requestBodyType.Value.Name}.",
                        getCurrentInstantExtended (),
                        path,
                        endpoint.DisplayName,
                        requestBodyType.Value.Name
                    )

                requestBodyType
            else
                log.LogDebug("{CurrentInstant}: Path: {path}; endpoint.Metadata.Count = 0.", getCurrentInstantExtended (), path)

                None
        else
            None

    member this.Invoke(context: HttpContext) =
        task {

            // -----------------------------------------------------------------------------------------------------
            // On the way in...
            let startTime = getCurrentInstant ()

#if DEBUG
            let middlewareTraceHeader = context.Request.Headers["X-MiddlewareTraceIn"]

            context.Request.Headers[ "X-MiddlewareTraceIn" ] <- $"{middlewareTraceHeader}{nameof ValidateIdsMiddleware} --> "
#endif

            try
                /// The path of the current request.
                let path = context.Request.Path.ToString()

                let correlationId = getCorrelationId context
                let mutable requestBodyType: Type = null
                let mutable graceIds = GraceIds.Default
                let mutable (badRequest: GraceError option) = None

                // Get the parameter type for the endpoint from the cache.
                // If we don't already have it, get it, and add it to the cache.
                if
                    not
                    <| typeLookup.TryGetValue(path, &requestBodyType)
                then
                    match getBodyType context with
                    | Some t ->
                        requestBodyType <- t
                        typeLookup.TryAdd(path, requestBodyType) |> ignore
                    | None -> typeLookup.TryAdd(path, null) |> ignore

                // If we have a request body type for the endpoint, parse the body of the request to get the Ids and Names.
                // If the request body type is null, it's an endpoint (like /healthz) where we don't take these parameters.
                if not <| isNull (requestBodyType) then
                    // This allows us to read the request body multiple times.
                    context.Request.EnableBuffering()

                    // Deserialize the request body to the type for this endpoint.
                    match! deserializeToType requestBodyType context with
                    | None -> ()
                    | Some requestBody ->
                        let mutable entityProperties = EntityProperties.Default

                        // Get the available entity properties for this endpoint from the cache.
                        //   If we don't already have them, figure out which properties are available for this type, and cache that.
                        if
                            not
                            <| propertyLookup.TryGetValue(requestBodyType, &entityProperties)
                        then
                            entityProperties <- ValidateIdsDecisions.discoverEntityProperties requestBodyType

                            // Cache the property list for this request body type.
                            propertyLookup.TryAdd(requestBodyType, entityProperties)
                            |> ignore

                        // Get Owner information.
                        if ValidateIdsDecisions.shouldValidateEntity None entityProperties.OwnerId entityProperties.OwnerName then
                            // Get the values from the request body.
                            let ownerIdString = entityProperties.OwnerId.Value.GetValue(requestBody) :?> string
                            let ownerName = entityProperties.OwnerName.Value.GetValue(requestBody) :?> string

                            let validationMode = ValidateIdsDecisions.validationModeForPath ValidateIdsDecisions.EntityKind.Owner path

                            match!
                                ValidateIdsDecisions.getEntityValidationErrorMessage
                                    ValidateIdsDecisions.EntityKind.Owner
                                    validationMode
                                    ownerIdString
                                    ownerName
                                with
                            | Some error -> badRequest <- Some(GraceError.Create error correlationId)
                            | None ->
                                if validationMode = ValidateIdsDecisions.EntityValidationMode.Create then
                                    // If we're creating a new Owner, we don't need to resolve the Id.
                                    graceIds <- ValidateIdsDecisions.withEntityId ValidateIdsDecisions.EntityKind.Owner ownerIdString graceIds
                                else
                                    // Resolve the OwnerId based on the provided Id and Name.
                                    match! resolveOwnerId ownerIdString ownerName correlationId with
                                    | Some resolvedOwnerId ->
                                        graceIds <- ValidateIdsDecisions.withEntityId ValidateIdsDecisions.EntityKind.Owner resolvedOwnerId graceIds
                                    | None ->
                                        let error = ValidateIdsDecisions.notFoundErrorMessage ValidateIdsDecisions.EntityKind.Owner ownerIdString

                                        badRequest <- Some(GraceError.Create error correlationId)

                        // Get Organization information.
                        if ValidateIdsDecisions.shouldValidateEntity badRequest entityProperties.OrganizationId entityProperties.OrganizationName then
                            // Get the values from the request body.
                            let organizationIdString = entityProperties.OrganizationId.Value.GetValue(requestBody) :?> string
                            let organizationName = entityProperties.OrganizationName.Value.GetValue(requestBody) :?> string

                            let validationMode = ValidateIdsDecisions.validationModeForPath ValidateIdsDecisions.EntityKind.Organization path

                            match!
                                ValidateIdsDecisions.getEntityValidationErrorMessage
                                    ValidateIdsDecisions.EntityKind.Organization
                                    validationMode
                                    organizationIdString
                                    organizationName
                                with
                            | Some error -> badRequest <- Some(GraceError.Create error correlationId)
                            | None ->
                                if validationMode = ValidateIdsDecisions.EntityValidationMode.Create then
                                    // If we're creating a new Organization, we don't need to resolve the Id.
                                    graceIds <- ValidateIdsDecisions.withEntityId ValidateIdsDecisions.EntityKind.Organization organizationIdString graceIds
                                else
                                    // Resolve the OrganizationId based on the provided Id and Name.
                                    match! resolveOrganizationId graceIds.OwnerId organizationIdString organizationName correlationId with
                                    | Some resolvedOrganizationId ->
                                        graceIds <-
                                            ValidateIdsDecisions.withEntityId ValidateIdsDecisions.EntityKind.Organization resolvedOrganizationId graceIds
                                    | None ->
                                        let error = ValidateIdsDecisions.notFoundErrorMessage ValidateIdsDecisions.EntityKind.Organization organizationIdString

                                        badRequest <- Some(GraceError.Create error correlationId)

                        // Get repository information.
                        if ValidateIdsDecisions.shouldValidateEntity badRequest entityProperties.RepositoryId entityProperties.RepositoryName then
                            // Get the values from the request body.
                            let repositoryIdString = entityProperties.RepositoryId.Value.GetValue(requestBody) :?> string
                            let repositoryName = entityProperties.RepositoryName.Value.GetValue(requestBody) :?> string

                            let validationMode = ValidateIdsDecisions.validationModeForPath ValidateIdsDecisions.EntityKind.Repository path

                            match!
                                ValidateIdsDecisions.getEntityValidationErrorMessage
                                    ValidateIdsDecisions.EntityKind.Repository
                                    validationMode
                                    repositoryIdString
                                    repositoryName
                                with
                            | Some error -> badRequest <- Some(GraceError.Create error correlationId)
                            | None ->
                                if validationMode = ValidateIdsDecisions.EntityValidationMode.Create then
                                    // If we're creating a new Repository, we don't need to resolve the Id.
                                    graceIds <- ValidateIdsDecisions.withEntityId ValidateIdsDecisions.EntityKind.Repository repositoryIdString graceIds
                                else
                                    // Resolve the RepositoryId based on the provided Id and Name.
                                    match! resolveRepositoryId graceIds.OwnerId graceIds.OrganizationId repositoryIdString repositoryName correlationId with
                                    | Some resolvedRepositoryId ->
                                        graceIds <-
                                            ValidateIdsDecisions.withEntityId ValidateIdsDecisions.EntityKind.Repository $"{resolvedRepositoryId}" graceIds
                                    | None ->
                                        let error = ValidateIdsDecisions.notFoundErrorMessage ValidateIdsDecisions.EntityKind.Repository repositoryIdString

                                        badRequest <- Some(GraceError.Create error correlationId)

                        // Get branch information.
                        if ValidateIdsDecisions.shouldValidateEntity badRequest entityProperties.BranchId entityProperties.BranchName then
                            // Get the values from the request body.
                            let branchIdString = entityProperties.BranchId.Value.GetValue(requestBody) :?> string
                            let branchName = entityProperties.BranchName.Value.GetValue(requestBody) :?> string

                            let validationMode = ValidateIdsDecisions.validationModeForPath ValidateIdsDecisions.EntityKind.Branch path

                            match!
                                ValidateIdsDecisions.getEntityValidationErrorMessage
                                    ValidateIdsDecisions.EntityKind.Branch
                                    validationMode
                                    branchIdString
                                    branchName
                                with
                            | Some error -> badRequest <- Some(GraceError.Create error correlationId)
                            | None ->
                                // If we're creating a new Branch, we don't need to resolve the Id.
                                if validationMode = ValidateIdsDecisions.EntityValidationMode.Create then
                                    graceIds <- ValidateIdsDecisions.withEntityId ValidateIdsDecisions.EntityKind.Branch branchIdString graceIds
                                else
                                    // Resolve the BranchId based on the provided Id and Name.
                                    match!
                                        resolveBranchId graceIds.OwnerId graceIds.OrganizationId graceIds.RepositoryId branchIdString branchName correlationId
                                        with
                                    | Some resolvedBranchId ->
                                        graceIds <- ValidateIdsDecisions.withEntityId ValidateIdsDecisions.EntityKind.Branch $"{resolvedBranchId}" graceIds
                                    | None ->
                                        let error = ValidateIdsDecisions.notFoundErrorMessage ValidateIdsDecisions.EntityKind.Branch branchIdString

                                        badRequest <- Some(GraceError.Create error correlationId)

                    // Add the parsed Id's and Names to the HttpContext.
                    context.Items.Add(nameof GraceIds, graceIds)

                    // Reset Request.Body to position 0 so it can be read again by endpoints.
                    context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                    |> ignore

                let duration_ms = getDurationRightAligned_ms startTime

                if Option.isSome badRequest then
                    let contract = ValidateIdsDecisions.badRequestResponse badRequest.Value
                    let error = contract.Error
                    context.Items.Add("BadRequest", contract.Error.Error)

                    log.LogWarning(
                        "{CurrentInstant}: Node: {hostName}; CorrelationId: {correlationId}; {currentFunction}: Path: {path}; {message}; Duration: {duration_ms}ms.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        correlationId,
                        nameof ValidateIdsMiddleware,
                        path,
                        error.Error,
                        duration_ms
                    )

                    let! _ = (context |> result400BadRequest contract.Error)
                    ()
                else
                    if graceIds.HasBranch then
                        log.LogInformation(
                            "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; ValidateIds.Middleware: Path: {path}; OwnerId: {OwnerId}; OrganizationId: {OrganizationId}; RepositoryId: {RepositoryId}; BranchId: {BranchId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            duration_ms,
                            correlationId,
                            path,
                            graceIds.OwnerIdString,
                            graceIds.OrganizationIdString,
                            graceIds.RepositoryIdString,
                            graceIds.BranchIdString
                        )
                    elif graceIds.HasRepository then
                        log.LogInformation(
                            "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; ValidateIds.Middleware: Path: {path}; OwnerId: {OwnerId}; OrganizationId: {OrganizationId}; RepositoryId: {RepositoryId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            duration_ms,
                            correlationId,
                            path,
                            graceIds.OwnerIdString,
                            graceIds.OrganizationIdString,
                            graceIds.RepositoryIdString
                        )
                    elif graceIds.HasOrganization then
                        log.LogInformation(
                            "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; ValidateIds.Middleware: Path: {path}; OwnerId: {OwnerId}; OrganizationId: {OrganizationId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            duration_ms,
                            correlationId,
                            path,
                            graceIds.OwnerIdString,
                            graceIds.OrganizationIdString
                        )
                    elif graceIds.HasOwner then
                        log.LogInformation(
                            "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; ValidateIds.Middleware: Path: {path}; OwnerId: {OwnerId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            duration_ms,
                            correlationId,
                            path,
                            graceIds.OwnerIdString
                        )

                    // -----------------------------------------------------------------------------------------------------

                    // Pass control to next middleware instance...
                    //logToConsole
                    //    $"********About to call next.Invoke(context) in ValidateIds.Middleware.fs. CorrelationId: {correlationId}. Path: {context.Request.Path}; RepositoryId: {graceIds.RepositoryId}."

                    let nextTask = next.Invoke(context)

                    //logToConsole
                    //    $"********After call to next.Invoke(context) in ValidateIds.Middleware.fs. CorrelationId: {correlationId}. Path: {context.Request.Path}; RepositoryId: {graceIds.RepositoryId}."

                    // -----------------------------------------------------------------------------------------------------
                    // On the way out...

#if DEBUG
                    let middlewareTraceOutHeader = context.Request.Headers["X-MiddlewareTraceOut"]

                    context.Request.Headers[ "X-MiddlewareTraceOut" ] <- $"{middlewareTraceOutHeader}{nameof ValidateIdsMiddleware} --> "

                    if not <| ValidateIdsDecisions.isIgnoredPath path then
                        log.LogDebug(
                            "{CurrentInstant}: Path: {path}; Elapsed: {elapsed}ms; Status code: {statusCode}; graceIds: {graceIds}",
                            getCurrentInstantExtended (),
                            context.Request.Path,
                            duration_ms,
                            context.Response.StatusCode,
                            serialize graceIds
                        )
#endif
                    do! nextTask
            with
            | ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: An unhandled exception occurred in the {middlewareName} middleware.",
                    getCurrentInstantExtended (),
                    nameof ValidateIdsMiddleware
                )

                context.Response.StatusCode <- 500

                do! context.Response.WriteAsync($"{getCurrentInstantExtended ()}: An unhandled exception occurred in the ValidateIdsMiddleware middleware.")

        }
        :> Task
