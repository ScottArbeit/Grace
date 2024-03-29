﻿namespace Grace.Server.Middleware

open Grace.Actors.Services
open Grace.Server
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Linq
open System.Reflection
open System.Text
open System.Threading.Tasks

/// Holds the PropertyInfo for each Entity Id and Name property.
type EntityProperties =
    {
        OwnerId: PropertyInfo option
        OwnerName: PropertyInfo option
        OrganizationId: PropertyInfo option
        OrganizationName: PropertyInfo option
        RepositoryId: PropertyInfo option
        RepositoryName: PropertyInfo option
        BranchId: PropertyInfo option
        BranchName: PropertyInfo option
        CorrelationId: PropertyInfo option
    }

    static member Default =
        {
            OwnerId = None
            OwnerName = None
            OrganizationId = None
            OrganizationName = None
            RepositoryId = None
            RepositoryName = None
            BranchId = None
            BranchName = None
            CorrelationId = None
        }

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
    let ignorePaths = ["/healthz"; "/actors"; "/dapr"; "/notifications"]

    /// Gets the parameter type for the endpoint from the endpoint metadata created in Startup.Server.fs.
    let getBodyType (context: HttpContext) = 
        let path = context.Request.Path.ToString()
        if not <| (ignorePaths |> Seq.exists(fun ignorePath -> path.StartsWith(ignorePath, StringComparison.InvariantCultureIgnoreCase))) then
            let endpoint = context.GetEndpoint()
            if isNull(endpoint) then
                log.LogDebug("{currentInstant}: Path: {path}; Endpoint: null.", getCurrentInstantExtended(), path)
                None
            elif endpoint.Metadata.Count > 0 then
                let requestBodyType = endpoint.Metadata 
                                        |> Seq.tryFind (fun metadataItem -> metadataItem.GetType().FullName = "System.RuntimeType") // The types that we add in Startup.Server.fs show up here as "System.RuntimeType".
                                        |> Option.map (fun metadataItem -> metadataItem :?> Type)                                   // Convert the metadata item to a Type.
                if requestBodyType |> Option.isSome then 
                    log.LogDebug("{currentInstant}: Path: {path}; Endpoint: {endpoint.DisplayName}; RequestBodyType: {requestBodyType.Value.Name}.", getCurrentInstantExtended(), path, endpoint.DisplayName, requestBodyType.Value.Name)
                requestBodyType
            else
                log.LogDebug("{currentInstant}: Path: {path}; endpoint.Metadata.Count = 0.", getCurrentInstantExtended(), path)
                None            
        else
            None

    member this.Invoke(context: HttpContext) =
        task {

    // -----------------------------------------------------------------------------------------------------
    // On the way in...
#if DEBUG
            let startTime = getCurrentInstant()
            let middlewareTraceHeader = context.Request.Headers["X-MiddlewareTraceIn"];
            context.Request.Headers["X-MiddlewareTraceIn"] <- $"{middlewareTraceHeader}{nameof(ValidateIdsMiddleware)} --> ";
#endif

            try
                let path = context.Request.Path.ToString()
                let correlationId = getCorrelationId context
                let mutable requestBodyType: Type = null
                let mutable graceIds = GraceIds.Default
                let mutable notFound = false
                let mutable badRequest = false

                // If we haven't seen this endpoint before, get the parameter type for the endpoint.
                if not <| typeLookup.TryGetValue(path, &requestBodyType) then
                    match getBodyType context with
                    | Some t -> 
                        requestBodyType <- t
                        typeLookup.TryAdd(path, requestBodyType) |> ignore
                    | None ->
                        typeLookup.TryAdd(path, null) |> ignore

                // If we have a request body type for the endpoint, parse the body of the request to get the Ids and Names.
                // If the request body type is null, it's an endpoint (like /healthz) where we don't take these parameters.
                if not <| isNull(requestBodyType) then
                    context.Request.EnableBuffering()
                    match! context |> parseType requestBodyType with
                    | Some requestBody ->
                        // Get the available entity properties for this endpoint from the dictionary.
                        //   If we don't already have them, figure out which properties are available for this type, and cache that.
                        let mutable entityProperties: EntityProperties = EntityProperties.Default
                        if not <| propertyLookup.TryGetValue(requestBodyType, &entityProperties) then
                            // We haven't seen this request body type before, so we need to figure out which properties are available.

                            // Get all of the properties on the request body type.
                            let properties = requestBodyType.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
                            let findProperty name = properties |> Seq.tryFind(fun p -> p.Name = name)

                            // Check if these indivudal properties exist on the request body type.
                            entityProperties <-
                                {
                                    OwnerId =           findProperty (nameof(OwnerId))
                                    OwnerName =         findProperty (nameof(OwnerName))
                                    OrganizationId =    findProperty (nameof(OrganizationId))
                                    OrganizationName =  findProperty (nameof(OrganizationName))
                                    RepositoryId =      findProperty (nameof(RepositoryId))
                                    RepositoryName =    findProperty (nameof(RepositoryName))
                                    BranchId =          findProperty (nameof(BranchId))
                                    BranchName =        findProperty (nameof(BranchName))
                                    CorrelationId =     findProperty (nameof(CorrelationId))
                                }

                            // Cache the property list for this request body type.
                            propertyLookup.TryAdd(requestBodyType, entityProperties) |> ignore

                        // let sb = StringBuilder()
                        // properties |> Array.iter (fun p -> sb.Append($"{p.Name}; ") |> ignore)
                        // logToConsole $"Path: {context.Request.Path}; Properties: {sb.ToString()}."

                        let mutable ownerId = String.Empty
                        let mutable ownerName = String.Empty
                        let mutable organizationId = String.Empty
                        let mutable organizationName = String.Empty
                        let mutable repositoryId = String.Empty
                        let mutable repositoryName = String.Empty
                        let mutable branchId = String.Empty
                        let mutable branchName = String.Empty

                        log.LogDebug("{currentInstant}: requestBodyType: {requestBodyType}; Request body: {requestBody}", getCurrentInstantExtended(), requestBodyType.Name, serialize requestBody)

                        // Get Owner information.
                        if Option.isSome entityProperties.OwnerId && Option.isSome entityProperties.OwnerName then
                            // Get the values from the request body.
                            ownerId <- entityProperties.OwnerId.Value.GetValue(requestBody) :?> string
                            ownerName <- entityProperties.OwnerName.Value.GetValue(requestBody) :?> string

                            if path.EndsWith("/create", StringComparison.InvariantCultureIgnoreCase) then
                                // If we're creating a new Owner, we don't need to resolve the Id.
                                graceIds <- {graceIds with OwnerId = ownerId; HasOwner = true}
                            else
                                // Resolve the OwnerId based on the provided Id and Name.
                                match! resolveOwnerId ownerId ownerName correlationId with
                                | Some resolvedOwnerId ->
                                    graceIds <- {graceIds with OwnerId = resolvedOwnerId; HasOwner = true}
                                | None ->
                                    badRequest <- true

                        // Get Organization information.
                        if Option.isSome entityProperties.OrganizationId && Option.isSome entityProperties.OrganizationName then
                            // Get the values from the request body.
                            organizationId <- entityProperties.OrganizationId.Value.GetValue(requestBody) :?> string
                            organizationName <- entityProperties.OrganizationName.Value.GetValue(requestBody) :?> string

                            if path.EndsWith("/create", StringComparison.InvariantCultureIgnoreCase) then
                                // If we're creating a new Organization, we don't need to resolve the Id.
                                graceIds <- {graceIds with OrganizationId = organizationId; HasOrganization = true}
                            else
                                // Resolve the OrganizationId based on the provided Id and Name.
                                match! resolveOrganizationId ownerId ownerName organizationId organizationName correlationId with
                                | Some resolvedOrganizationId ->
                                    graceIds <- {graceIds with OrganizationId = resolvedOrganizationId; HasOrganization = true}
                                | None -> 
                                    badRequest <- true

                        // Get repository information.
                        if Option.isSome entityProperties.RepositoryId && Option.isSome entityProperties.RepositoryName then
                            // Get the values from the request body.
                            repositoryId <- entityProperties.RepositoryId.Value.GetValue(requestBody) :?> string
                            repositoryName <- entityProperties.RepositoryName.Value.GetValue(requestBody) :?> string

                            if path.EndsWith("/create", StringComparison.InvariantCultureIgnoreCase) then
                                // If we're creating a new Repository, we don't need to resolve the Id.
                                graceIds <- {graceIds with RepositoryId = repositoryId; HasRepository = true}
                            else
                                // Resolve the RepositoryId based on the provided Id and Name.
                                match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName correlationId with
                                | Some resolvedRepositoryId ->
                                    graceIds <- {graceIds with RepositoryId = resolvedRepositoryId; HasRepository = true}
                                | None ->
                                    badRequest <- true

                        // Get branch information.
                        if Option.isSome entityProperties.BranchId && Option.isSome entityProperties.BranchName then
                            // Get the values from the request body.
                            branchId <- entityProperties.BranchId.Value.GetValue(requestBody) :?> string
                            branchName <- entityProperties.BranchName.Value.GetValue(requestBody) :?> string

                            if path.EndsWith("/create", StringComparison.InvariantCultureIgnoreCase) then
                                // If we're creating a new Branch, we don't need to resolve the Id.
                                graceIds <- {graceIds with BranchId = branchId; HasBranch = true}
                            else
                                // Resolve the BranchId based on the provided Id and Name.
                                match! resolveBranchId graceIds.RepositoryId branchId branchName correlationId with
                                | Some resolvedBranchId ->
                                    // Check to see if the Branch exists.
                                    match! Branch.branchExists ownerId ownerName organizationId organizationName repositoryId repositoryName resolvedBranchId branchName correlationId Branch.BranchError.BranchDoesNotExist with
                                    | Ok _ ->
                                        graceIds <- {graceIds with BranchId = resolvedBranchId; HasBranch = true}
                                    | Error error ->
                                        notFound <- true
                                | None ->
                                    badRequest <- true

                    | None ->
                        ()

                    // Add the parsed Id's and Names to the HttpContext.
                    context.Items.Add(nameof(GraceIds), graceIds)

                    // Reset the Body to the beginning so that it can be read again later in the pipeline.
                    context.Request.Body.Seek(0L, IO.SeekOrigin.Begin) |> ignore

                if badRequest then
                    log.LogDebug("{currentInstant}: Bad request. CorrelationId: {correlationId}", getCurrentInstantExtended(), correlationId)
                    context.Items.Add("BadRequest", true)
                elif notFound then
                    log.LogDebug("{currentInstant}: The provided entity Id's and/or Names were not found in the database. This is normal for Create commands. CorrelationId: {correlationId}", getCurrentInstantExtended(), correlationId)
                    context.Items.Add("EntitiesNotFound", true)
    // -----------------------------------------------------------------------------------------------------

                // Pass control to next middleware instance...
                let nextTask = next.Invoke(context);

    // -----------------------------------------------------------------------------------------------------
    // On the way out...

    #if DEBUG
                let middlewareTraceOutHeader = context.Request.Headers["X-MiddlewareTraceOut"];
                context.Request.Headers["X-MiddlewareTraceOut"] <- $"{middlewareTraceOutHeader}{nameof(ValidateIdsMiddleware)} --> ";

                let elapsed = getCurrentInstant().Minus(startTime).TotalMilliseconds
                if not <| (ignorePaths |> Seq.exists(fun ignorePath -> path.StartsWith(ignorePath, StringComparison.InvariantCultureIgnoreCase))) then
                    log.LogDebug("{currentInstant}: Path: {path}; Elapsed: {elapsed}ms; Status code: {statusCode}; graceIds: {graceIds}",
                        getCurrentInstantExtended(), context.Request.Path, elapsed, context.Response.StatusCode, serialize graceIds)
#endif
                do! nextTask
            with ex ->
                log.LogError(ex, "{currentInstant}: An unhandled exception occurred in the {middlewareName} middleware.", getCurrentInstantExtended(), nameof(ValidateIdsMiddleware))
                context.Response.StatusCode <- 500
                do! context.Response.WriteAsync($"{getCurrentInstantExtended()}: An unhandled exception occurred in the ValidateIdsMiddleware middleware.")
                do! Task.CompletedTask
        } :> Task
