namespace Grace.Server.Middleware

open Grace.Actors.Services
open Grace.Server
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Errors.Branch
open Grace.Shared.Validation.Errors.Repository
open Grace.Shared.Validation.Errors.Organization
open Grace.Shared.Validation.Errors.Owner
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Linq
open System.Reflection
open System.Text
open System.Threading.Tasks
open Grace.Actors.Constants.ActorName

/// Holds the PropertyInfo for each Entity Id and Name property.
type EntityProperties =
    { OwnerId: PropertyInfo option
      OwnerName: PropertyInfo option
      OrganizationId: PropertyInfo option
      OrganizationName: PropertyInfo option
      RepositoryId: PropertyInfo option
      RepositoryName: PropertyInfo option
      BranchId: PropertyInfo option
      BranchName: PropertyInfo option }

    static member Default =
        { OwnerId = None
          OwnerName = None
          OrganizationId = None
          OrganizationName = None
          RepositoryId = None
          RepositoryName = None
          BranchId = None
          BranchName = None }

/// Examines the body of the incoming request to validate the Ids and Names in the request, and ensure that we know the right Ids. Having the Ids already figured out saves work for the rest of the pipeline.
///
/// If the Ids are invalid, it returns 400 Bad Request.
///
/// If the Ids and/or Names aren't found, it returns 404 Not Found.
type ValidateIdsMiddleware(next: RequestDelegate) =

    let log = ApplicationContext.loggerFactory.CreateLogger("ValidateIds.Middleware")

    /// Holds the request body type for each endpoint.
    let typeLookup = ConcurrentDictionary<String, Type>()

    /// Holds the property info for each request body type.
    let propertyLookup = ConcurrentDictionary<Type, EntityProperties>()

    /// Paths that we want to ignore, because they won't have Ids and Names in the body.
    let ignorePaths = [ "/healthz"; "/actors"; "/dapr"; "/notifications" ]

    /// Gets the parameter type for the endpoint from the endpoint metadata created in Startup.Server.fs.
    let getBodyType (context: HttpContext) =
        let path = context.Request.Path.ToString()

        if
            not
            <| (ignorePaths
                |> Seq.exists (fun ignorePath -> path.StartsWith(ignorePath, StringComparison.InvariantCultureIgnoreCase)))
        then
            let endpoint = context.GetEndpoint()

            if isNull (endpoint) then
                log.LogDebug("{currentInstant}: Path: {path}; Endpoint: null.", getCurrentInstantExtended (), path)
                None
            elif endpoint.Metadata.Count > 0 then
                let requestBodyType =
                    endpoint.Metadata
                    |> Seq.tryFind (fun metadataItem -> metadataItem.GetType().FullName = "System.RuntimeType") // The types that we add in Startup.Server.fs show up here as "System.RuntimeType".
                    |> Option.map (fun metadataItem -> metadataItem :?> Type) // Convert the metadata item to a Type.

                if requestBodyType |> Option.isSome then
                    log.LogDebug(
                        "{currentInstant}: Path: {path}; Endpoint: {endpoint.DisplayName}; RequestBodyType: {requestBodyType.Value.Name}.",
                        getCurrentInstantExtended (),
                        path,
                        endpoint.DisplayName,
                        requestBodyType.Value.Name
                    )

                requestBodyType
            else
                log.LogDebug("{currentInstant}: Path: {path}; endpoint.Metadata.Count = 0.", getCurrentInstantExtended (), path)

                None
        else
            None

    member this.Invoke(context: HttpContext) =
        task {

            // -----------------------------------------------------------------------------------------------------
            // On the way in...
#if DEBUG
            let startTime = getCurrentInstant ()
            let middlewareTraceHeader = context.Request.Headers["X-MiddlewareTraceIn"]

            context.Request.Headers["X-MiddlewareTraceIn"] <- $"{middlewareTraceHeader}{nameof (ValidateIdsMiddleware)} --> "
#endif

            try
                /// The path of the current request.
                let path = context.Request.Path.ToString()

                let correlationId = getCorrelationId context
                let mutable requestBodyType: Type = null
                let mutable graceIds = GraceIds.Default
                let mutable badRequest = String.Empty

                // Get the parameter type for the endpoint from the cache.
                // If we don't already have it, get it, and add it to the cache.
                if not <| typeLookup.TryGetValue(path, &requestBodyType) then
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
                        let mutable entityProperties: EntityProperties = EntityProperties.Default

                        // Get the available entity properties for this endpoint from the cache.
                        //   If we don't already have them, figure out which properties are available for this type, and cache that.
                        if not <| propertyLookup.TryGetValue(requestBodyType, &entityProperties) then

                            // Get all of the properties on the request body type.
                            let properties = requestBodyType.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)

                            let findProperty name = properties |> Seq.tryFind (fun p -> p.Name = name)

                            // Check if these indivudal properties exist on the request body type.
                            entityProperties <-
                                { OwnerId = findProperty (nameof (OwnerId))
                                  OwnerName = findProperty (nameof (OwnerName))
                                  OrganizationId = findProperty (nameof (OrganizationId))
                                  OrganizationName = findProperty (nameof (OrganizationName))
                                  RepositoryId = findProperty (nameof (RepositoryId))
                                  RepositoryName = findProperty (nameof (RepositoryName))
                                  BranchId = findProperty (nameof (BranchId))
                                  BranchName = findProperty (nameof (BranchName)) }

                            // Cache the property list for this request body type.
                            propertyLookup.TryAdd(requestBodyType, entityProperties) |> ignore

                        // let sb = StringBuilder()
                        // properties |> Array.iter (fun p -> sb.Append($"{p.Name}; ") |> ignore)
                        // logToConsole $"Path: {context.Request.Path}; Properties: {sb.ToString()}."

                        log.LogDebug(
                            "{currentInstant}: requestBodyType: {requestBodyType}; Request body: {requestBody}",
                            getCurrentInstantExtended (),
                            requestBodyType.Name,
                            serialize requestBody
                        )

                        // Get Owner information.
                        if
                            Option.isSome entityProperties.OwnerId
                            && Option.isSome entityProperties.OwnerName
                        then
                            // Get the values from the request body.
                            let ownerId = entityProperties.OwnerId.Value.GetValue(requestBody) :?> string
                            let ownerName = entityProperties.OwnerName.Value.GetValue(requestBody) :?> string

                            let validations =
                                if path.Equals("/owner/create", StringComparison.InvariantCultureIgnoreCase) then
                                    [| Common.String.isNotEmpty ownerId OwnerIdIsRequired
                                       Common.Guid.isValidAndNotEmpty ownerId InvalidOwnerId
                                       Common.String.isNotEmpty ownerName OwnerNameIsRequired
                                       Common.String.isValidGraceName ownerName InvalidOwnerName
                                       Common.Input.eitherIdOrNameMustBeProvided ownerId ownerName EitherOwnerIdOrOwnerNameRequired |]
                                else
                                    [| Common.Guid.isValidAndNotEmpty ownerId InvalidOwnerId
                                       Common.String.isValidGraceName ownerName InvalidOwnerName
                                       Common.Input.eitherIdOrNameMustBeProvided ownerId ownerName EitherOwnerIdOrOwnerNameRequired |]

                            match! getFirstError validations with
                            | Some error -> badRequest <- OwnerError.getErrorMessage error
                            | None ->
                                if path.Equals("/owner/create", StringComparison.InvariantCultureIgnoreCase) then
                                    // If we're creating a new Owner, we don't need to resolve the Id.
                                    graceIds <- { graceIds with OwnerId = ownerId; HasOwner = true }
                                else
                                    // Resolve the OwnerId based on the provided Id and Name.
                                    match! resolveOwnerId ownerId ownerName correlationId with
                                    | Some resolvedOwnerId -> graceIds <- { graceIds with OwnerId = resolvedOwnerId; HasOwner = true }
                                    | None ->
                                        badRequest <-
                                            if not <| String.IsNullOrEmpty(ownerId) then
                                                OwnerError.getErrorMessage OwnerIdDoesNotExist
                                            else
                                                OwnerError.getErrorMessage OwnerDoesNotExist

                        // Get Organization information.
                        if
                            String.IsNullOrEmpty(badRequest)
                            && Option.isSome entityProperties.OrganizationId
                            && Option.isSome entityProperties.OrganizationName
                        then
                            // Get the values from the request body.
                            let organizationId = entityProperties.OrganizationId.Value.GetValue(requestBody) :?> string
                            let organizationName = entityProperties.OrganizationName.Value.GetValue(requestBody) :?> string

                            let validations =
                                if path.Equals("/organization/create", StringComparison.InvariantCultureIgnoreCase) then
                                    [| Common.String.isNotEmpty organizationId OrganizationIdIsRequired
                                       Common.Guid.isValidAndNotEmpty organizationId InvalidOrganizationId
                                       Common.String.isNotEmpty organizationName OrganizationNameIsRequired
                                       Common.String.isValidGraceName organizationName InvalidOrganizationName
                                       Common.Input.eitherIdOrNameMustBeProvided organizationId organizationName EitherOrganizationIdOrOrganizationNameRequired |]
                                else
                                    [| Common.Guid.isValidAndNotEmpty organizationId InvalidOrganizationId
                                       Common.String.isValidGraceName organizationName InvalidOrganizationName
                                       Common.Input.eitherIdOrNameMustBeProvided organizationId organizationName EitherOrganizationIdOrOrganizationNameRequired |]

                            match! getFirstError validations with
                            | Some error -> badRequest <- OrganizationError.getErrorMessage error
                            | None ->
                                if path.Equals("/organization/create", StringComparison.InvariantCultureIgnoreCase) then
                                    // If we're creating a new Organization, we don't need to resolve the Id.
                                    graceIds <- { graceIds with OrganizationId = organizationId; HasOrganization = true }
                                else
                                    // Resolve the OrganizationId based on the provided Id and Name.
                                    match! resolveOrganizationId graceIds.OwnerId String.Empty organizationId organizationName correlationId with
                                    | Some resolvedOrganizationId ->
                                        graceIds <- { graceIds with OrganizationId = resolvedOrganizationId; HasOrganization = true }
                                    | None ->
                                        badRequest <-
                                            if not <| String.IsNullOrEmpty(organizationId) then
                                                OrganizationError.getErrorMessage OrganizationIdDoesNotExist
                                            else
                                                OrganizationError.getErrorMessage OrganizationDoesNotExist

                        // Get repository information.
                        if
                            String.IsNullOrEmpty(badRequest)
                            && Option.isSome entityProperties.RepositoryId
                            && Option.isSome entityProperties.RepositoryName
                        then
                            // Get the values from the request body.
                            let repositoryId = entityProperties.RepositoryId.Value.GetValue(requestBody) :?> string
                            let repositoryName = entityProperties.RepositoryName.Value.GetValue(requestBody) :?> string

                            let validations =
                                if path.Equals("/repository/create", StringComparison.InvariantCultureIgnoreCase) then
                                    [| Common.String.isNotEmpty repositoryId RepositoryError.RepositoryIdIsRequired
                                       Common.Guid.isValidAndNotEmpty repositoryId RepositoryError.InvalidRepositoryId
                                       Common.String.isNotEmpty repositoryName RepositoryError.RepositoryNameIsRequired
                                       Common.String.isValidGraceName repositoryName RepositoryError.InvalidRepositoryName
                                       Common.Input.eitherIdOrNameMustBeProvided repositoryId repositoryName EitherRepositoryIdOrRepositoryNameRequired |]
                                else
                                    [| Common.Guid.isValidAndNotEmpty repositoryId RepositoryError.InvalidRepositoryId
                                       Common.String.isValidGraceName repositoryName RepositoryError.InvalidRepositoryName
                                       Common.Input.eitherIdOrNameMustBeProvided repositoryId repositoryName EitherRepositoryIdOrRepositoryNameRequired |]

                            match! getFirstError validations with
                            | Some error -> badRequest <- RepositoryError.getErrorMessage error
                            | None ->
                                if path.Equals("/repository/create", StringComparison.InvariantCultureIgnoreCase) then
                                    // If we're creating a new Repository, we don't need to resolve the Id.
                                    graceIds <- { graceIds with RepositoryId = repositoryId; HasRepository = true }
                                else
                                    logToConsole $"In ValidateIdsMiddleware: About to call resolveRepositoryId with {graceIds.OwnerId}; {graceIds.OrganizationId}; {repositoryId}; {repositoryName}; {correlationId}."
                                    // Resolve the RepositoryId based on the provided Id and Name.
                                    match!
                                        resolveRepositoryId
                                            graceIds.OwnerId
                                            String.Empty
                                            graceIds.OrganizationId
                                            String.Empty
                                            repositoryId
                                            repositoryName
                                            correlationId
                                    with
                                    | Some resolvedRepositoryId -> graceIds <- { graceIds with RepositoryId = resolvedRepositoryId; HasRepository = true }
                                    | None ->
                                        badRequest <-
                                            if not <| String.IsNullOrEmpty(repositoryId) then
                                                RepositoryError.getErrorMessage RepositoryIdDoesNotExist
                                            else
                                                RepositoryError.getErrorMessage RepositoryDoesNotExist

                        // Get branch information.
                        if
                            String.IsNullOrEmpty(badRequest)
                            && Option.isSome entityProperties.BranchId
                            && Option.isSome entityProperties.BranchName
                        then
                            // Get the values from the request body.
                            let branchId = entityProperties.BranchId.Value.GetValue(requestBody) :?> string
                            let branchName = entityProperties.BranchName.Value.GetValue(requestBody) :?> string

                            let validations =
                                if path.Equals("/branch/create", StringComparison.InvariantCultureIgnoreCase) then
                                    [| Common.String.isNotEmpty branchId BranchIdIsRequired
                                       Common.Guid.isValidAndNotEmpty branchId InvalidBranchId
                                       Common.String.isNotEmpty branchName BranchNameIsRequired
                                       Common.String.isValidGraceName branchName InvalidBranchName
                                       Common.Input.eitherIdOrNameMustBeProvided branchId branchName EitherBranchIdOrBranchNameRequired |]
                                else
                                    [| Common.Guid.isValidAndNotEmpty branchId InvalidBranchId
                                       Common.String.isValidGraceName branchName InvalidBranchName
                                       Common.Input.eitherIdOrNameMustBeProvided branchId branchName EitherBranchIdOrBranchNameRequired |]

                            match! getFirstError validations with
                            | Some error -> badRequest <- BranchError.getErrorMessage error
                            | None ->
                                if path.Equals("/branch/create", StringComparison.InvariantCultureIgnoreCase) then
                                    // If we're creating a new Branch, we don't need to resolve the Id.
                                    graceIds <- { graceIds with BranchId = branchId; HasBranch = true }
                                else
                                    // Resolve the BranchId based on the provided Id and Name.
                                    match! resolveBranchId graceIds.RepositoryId branchId branchName correlationId with
                                    | Some resolvedBranchId -> graceIds <- { graceIds with BranchId = resolvedBranchId; HasBranch = true }
                                    | None ->
                                        badRequest <-
                                            if not <| String.IsNullOrEmpty(branchId) then
                                                BranchError.getErrorMessage BranchIdDoesNotExist
                                            else
                                                BranchError.getErrorMessage BranchDoesNotExist

                    // Add the parsed Id's and Names to the HttpContext.
                    context.Items.Add(nameof (GraceIds), graceIds)

                    // Reset the Body to the beginning so that it can be read again later in the pipeline.
                    context.Request.Body.Seek(0L, IO.SeekOrigin.Begin) |> ignore

                let duration_ms = getPaddedDuration_ms startTime

                if not <| String.IsNullOrEmpty(badRequest) then
                    context.Items.Add("BadRequest", badRequest)

                    log.LogWarning(
                        "{currentInstant}: CorrelationId: {correlationId}; {currentFunction}: Path: {path}; {message}; Duration: {duration_ms}ms.",
                        getCurrentInstantExtended (),
                        correlationId,
                        nameof (ValidateIdsMiddleware),
                        path,
                        badRequest,
                        duration_ms
                    )

                    context.Response.StatusCode <- 400
                    do! context.Response.WriteAsync($"{badRequest}")
                else
                    if graceIds.HasBranch then
                        log.LogInformation(
                            "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; ValidateIds.Middleware: Path: {path}; OwnerId: {OwnerId}; OrganizationId: {OrganizationId}; RepositoryId: {RepositoryId}; BranchId: {BranchId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            duration_ms,
                            correlationId,
                            path,
                            graceIds.OwnerId,
                            graceIds.OrganizationId,
                            graceIds.RepositoryId,
                            graceIds.BranchId
                        )
                    elif graceIds.HasRepository then
                        log.LogInformation(
                            "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; ValidateIds.Middleware: Path: {path}; OwnerId: {OwnerId}; OrganizationId: {OrganizationId}; RepositoryId: {RepositoryId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            duration_ms,
                            correlationId,
                            path,
                            graceIds.OwnerId,
                            graceIds.OrganizationId,
                            graceIds.RepositoryId
                        )
                    elif graceIds.HasOrganization then
                        log.LogInformation(
                            "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; ValidateIds.Middleware: Path: {path}; OwnerId: {OwnerId}; OrganizationId: {OrganizationId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            duration_ms,
                            correlationId,
                            path,
                            graceIds.OwnerId,
                            graceIds.OrganizationId
                        )
                    elif graceIds.HasOwner then
                        log.LogInformation(
                            "{currentInstant}: Node: {hostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; ValidateIds.Middleware: Path: {path}; OwnerId: {OwnerId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            duration_ms,
                            correlationId,
                            path,
                            graceIds.OwnerId
                        )
                    // -----------------------------------------------------------------------------------------------------

                    // Pass control to next middleware instance...
                    let nextTask = next.Invoke(context)

                    // -----------------------------------------------------------------------------------------------------
                    // On the way out...

#if DEBUG
                    let middlewareTraceOutHeader = context.Request.Headers["X-MiddlewareTraceOut"]

                    context.Request.Headers["X-MiddlewareTraceOut"] <- $"{middlewareTraceOutHeader}{nameof (ValidateIdsMiddleware)} --> "

                    if
                        not
                        <| (ignorePaths
                            |> Seq.exists (fun ignorePath -> path.StartsWith(ignorePath, StringComparison.InvariantCultureIgnoreCase)))
                    then
                        log.LogDebug(
                            "{currentInstant}: Path: {path}; Elapsed: {elapsed}ms; Status code: {statusCode}; graceIds: {graceIds}",
                            getCurrentInstantExtended (),
                            context.Request.Path,
                            duration_ms,
                            context.Response.StatusCode,
                            serialize graceIds
                        )
#endif
                    do! nextTask
            with ex ->
                log.LogError(
                    ex,
                    "{currentInstant}: An unhandled exception occurred in the {middlewareName} middleware.",
                    getCurrentInstantExtended (),
                    nameof (ValidateIdsMiddleware)
                )

                context.Response.StatusCode <- 500

                do! context.Response.WriteAsync($"{getCurrentInstantExtended ()}: An unhandled exception occurred in the ValidateIdsMiddleware middleware.")

        }
        :> Task
