namespace Grace.Server.Middleware

open Dapr.Actors
open Giraffe.HttpStatusCodeHandlers.RequestErrors
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Extensions
open Microsoft.AspNetCore.Http.HttpResults
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Linq
open System.Reflection
open System.Text
open System.Threading.Tasks
open Giraffe.Core

/// Examines the body of the incoming request to validate the Ids and Names in the request, and ensure that we know the right Ids. Having the Ids already figured out saves work for the rest of the pipeline.
///
/// If the Ids are invalid, it returns 400 Bad Request.
///
/// If the Ids and/or Names aren't found, it returns 404 Not Found.
type ValidateIdsMiddleware(next: RequestDelegate) =

    let log = ApplicationContext.loggerFactory.CreateLogger("ValidateIdsMiddleware")

    /// Holds the parameter type for each endpoint.
    let typeLookup = ConcurrentDictionary<String, Type>()

    /// Holds the property info for each parameter type.
    let propertyLookup = ConcurrentDictionary<Type, PropertyInfo[]>()

    /// Gets the parameter type for the endpoint from the endpoint metadata created in Startup.Server.fs.
    let getBodyType (context: HttpContext) = 
        let path = context.Request.Path.ToString()
        if not <| path.StartsWith("/healthz") && not <| path.StartsWith("/actors") && not <| path.StartsWith("/dapr") then
            let endpoint = context.GetEndpoint()
            if isNull(endpoint) then
                log.LogTrace("Path: {context.Request.Path}; Endpoint: null.", context.Request.Path)
                None
            elif endpoint.Metadata.Count > 0 then
                //logToConsole $"Path: {context.Request.Path}; endpoint.Metadata.Count: {endpoint.Metadata.Count}."
                //endpoint.Metadata |> Seq.iter (fun m -> logToConsole (sprintf "%A: %A" m (m.GetType())))
                let requestBodyType = endpoint.Metadata |> Seq.tryFind (fun m -> m.GetType().FullName = "System.RuntimeType") |> Option.map (fun m -> m :?> Type)
                if requestBodyType |> Option.isSome then log.LogTrace("Path: {context.Request.Path}; Endpoint: {endpoint.DisplayName}; RequestBodyType: {requestBodyType.Value.Name}.", context.Request.Path, endpoint.DisplayName, requestBodyType.Value.Name)
                requestBodyType
            else
                log.LogTrace("Path: {context.Request.Path}; endpoint.Metadata.Count = 0.", context.Request.Path)
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

            let path = context.Request.Path
            let mutable requestBodyType: Type = null

            // If we haven't seen this endpoint before, get the parameter type for the endpoint.
            if not <| typeLookup.TryGetValue(path, &requestBodyType) then
                match getBodyType context with
                | Some t -> 
                    requestBodyType <- t
                    typeLookup.TryAdd(path, requestBodyType) |> ignore
                | None ->
                    typeLookup.TryAdd(path, null) |> ignore

            // If we have a parameter type for the endpoint, parse the body of the request to get the Ids and Names.
            // If we don't have a parameter type for the endpoint, it's an endpoint like /healthz or whatever.
            if not <| isNull(requestBodyType) then
                context.Request.EnableBuffering()
                match! context |> parseType requestBodyType with
                | Some requestBody ->
                    let mutable properties = Array.Empty<PropertyInfo>()
                    if not <| propertyLookup.TryGetValue(requestBodyType, &properties) then
                        properties <- requestBodyType.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
                        propertyLookup.TryAdd(requestBodyType, properties) |> ignore

                    let sb = StringBuilder()
                    // properties |> Array.iter (fun p -> sb.Append($"{p.Name}; ") |> ignore)
                    // logToConsole $"Path: {context.Request.Path}; Properties: {sb.ToString()}."
                    let ownerIdProperty = properties.FirstOrDefault(fun p -> p.Name = nameof(OwnerId))
                    let ownerNameProperty = properties.FirstOrDefault(fun p -> p.Name = nameof(OwnerName))
                    let organizationIdProperty = properties.FirstOrDefault(fun p -> p.Name = nameof(OrganizationId))
                    let organizationNameProperty = properties.FirstOrDefault(fun p -> p.Name = nameof(OrganizationName))
                    let repositoryIdProperty = properties.FirstOrDefault(fun p -> p.Name = nameof(RepositoryId))
                    let repositoryNameProperty = properties.FirstOrDefault(fun p -> p.Name = nameof(RepositoryName))
                    let branchIdProperty = properties.FirstOrDefault(fun p -> p.Name = nameof(BranchId))
                    let branchNameProperty = properties.FirstOrDefault(fun p -> p.Name = nameof(BranchName))

                    let mutable ownerId = String.Empty
                    let mutable ownerName = String.Empty
                    let mutable organizationId = String.Empty
                    let mutable organizationName = String.Empty
                    let mutable repositoryId = String.Empty
                    let mutable repositoryName = String.Empty
                    let mutable branchId = String.Empty
                    let mutable branchName = String.Empty
                    
                    if not <| isNull(ownerIdProperty) && not <| isNull(ownerNameProperty) then
                        // Get the values from the request body.
                        ownerId <- ownerIdProperty.GetValue(requestBody) :?> string
                        ownerName <- ownerNameProperty.GetValue(requestBody) :?> string

                        // Resolve the OwnerId based on the provided Id and Name.
                        match! resolveOwnerId ownerId ownerName with
                        | Some resolvedOwnerId ->
                            // Check to see if the Owner exists.
                            match! Owner.ownerExists resolvedOwnerId ownerName Owner.OwnerError.OwnerDoesNotExist with
                            | Ok _ ->
                                context.Items.Add(nameof(OwnerId), resolvedOwnerId)
                                context.Items.Add("HasOwner", true)
                            | Error error ->
                                context.Items.Add("NotFound", true)
                        | None ->
                            context.Items.Add("BadRequest", true)
                    else // These parameters don't have Owner fields.
                        context.Items.Add("HasOwner", false)

                    if not <| isNull(organizationIdProperty) && not <| isNull(organizationNameProperty) then
                        // Get the values from the request body.
                        organizationId <- organizationIdProperty.GetValue(requestBody) :?> string
                        organizationName <- organizationNameProperty.GetValue(requestBody) :?> string

                        // Resolve the OrganizationId based on the provided Id and Name.
                        match! resolveOrganizationId ownerId ownerName organizationId organizationName with
                        | Some resolvedOrganizationId ->
                            // Check to see if the Organization exists.
                            match! Organization.organizationExists ownerId ownerName resolvedOrganizationId organizationName Organization.OrganizationError.OrganizationDoesNotExist with
                            | Ok _ ->
                                context.Items.Add(nameof(OrganizationId), resolvedOrganizationId)
                                context.Items.Add("HasOrganization", true)
                            | Error error ->
                                context.Items.Add("NotFound", true)
                        | None ->
                            context.Items.Add("BadRequest", true)
                    else // These parameters don't have Organization fields.
                        context.Items.Add("HasOrganization", false)

                    if not <| isNull(repositoryIdProperty) && not <| isNull(repositoryNameProperty) then
                        // Get the values from the request body.
                        repositoryId <- repositoryIdProperty.GetValue(requestBody) :?> string
                        repositoryName <- repositoryNameProperty.GetValue(requestBody) :?> string

                        // Resolve the RepositoryId based on the provided Id and Name.
                        match! resolveRepositoryId ownerId ownerName organizationId organizationName repositoryId repositoryName with
                        | Some resolvedRepositoryId ->
                            // Check to see if the Repository exists.
                            match! Repository.repositoryExists ownerId ownerName organizationId organizationName resolvedRepositoryId repositoryName Repository.RepositoryError.RepositoryDoesNotExist with
                            | Ok _ ->
                                context.Items.Add(nameof(RepositoryId), resolvedRepositoryId)
                                context.Items.Add("HasRepository", true)
                            | Error error ->
                                context.Items.Add("NotFound", true)
                        | None ->
                            context.Items.Add("BadRequest", true)
                    else // These parameters don't have Repository fields.
                        context.Items.Add("HasRepository", false)

                    if not <| isNull(branchIdProperty) && not <| isNull(branchNameProperty) then
                        // Get the values from the request body.
                        branchId <- branchIdProperty.GetValue(requestBody) :?> string
                        branchName <- branchNameProperty.GetValue(requestBody) :?> string

                        // Resolve the BranchId based on the provided Id and Name.
                        let repositoryId = context.Items[nameof(RepositoryId)] :?> string
                        match! resolveBranchId repositoryId branchId branchName with
                        | Some resolvedBranchId ->
                            // Check to see if the Branch exists.
                            match! Branch.branchExists ownerId ownerName organizationId organizationName repositoryId repositoryName resolvedBranchId branchName Branch.BranchError.BranchDoesNotExist with
                            | Ok _ ->
                                context.Items.Add(nameof(BranchId), resolvedBranchId)
                                context.Items.Add("HasBranch", true)
                            | Error error ->
                                context.Items.Add("NotFound", true)
                        | None ->
                            context.Items.Add("BadRequest", true)
                    else // These parameters don't have Branch fields.
                        context.Items.Add("HasBranch", false)
                | None ->
                    ()

                // Reset the Body to the beginning so that it can be read again later in the pipeline.
                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin) |> ignore

            if context.Items.ContainsKey("BadRequest") then
                context.Response.StatusCode <- 400
                do! context.Response.WriteAsync("The Id's and/or Names are invalid.")
                do! Task.CompletedTask
            elif context.Items.ContainsKey("NotFound") then
                context.Response.StatusCode <- 404
                do! context.Response.WriteAsync("The Id's and/or Names do not exist.")
                do! Task.CompletedTask
            else
// -----------------------------------------------------------------------------------------------------

                // Pass control to next middleware instance...
                let nextTask = next.Invoke(context);

// -----------------------------------------------------------------------------------------------------
// On the way out...

#if DEBUG
                let middlewareTraceOutHeader = context.Request.Headers["X-MiddlewareTraceOut"];
                context.Request.Headers["X-MiddlewareTraceOut"] <- $"{middlewareTraceOutHeader}{nameof(ValidateIdsMiddleware)} --> ";
                
                let getContextItem key = 
                    let mutable value = null
                    context.Items.TryGetValue(key, &value) |> ignore
                    if not <| isNull(value) then value.ToString() else "Not found"

                let path = context.Request.Path.ToString()
                let elapsed = getCurrentInstant().Minus(startTime).TotalMilliseconds
                if not <| path.StartsWith("/healthz") && not <| path.StartsWith("/actors") && not <| path.StartsWith("/dapr") then
                    log.LogTrace("{currentInstant}: Path: {path}; Elapsed: {elapsed}ms; OwnerId: {OwnerId}; OrganizationId: {OrganizationId}; RepositoryId: {RepositoryId}; BranchId: {BranchId}; HasOwner: {HasOwner}; HasOrganization: {HasOrganization}; HasRepository: {HasRepository}; HasBranch: {HasBranch}",
                        getCurrentInstantExtended(), context.Request.Path, elapsed, getContextItem (nameof(OwnerId)), getContextItem (nameof(OrganizationId)), getContextItem (nameof(RepositoryId)), getContextItem (nameof(BranchId)), getContextItem ("HasOwner"), getContextItem ("HasOrganization"), getContextItem ("HasRepository"), getContextItem ("HasBranch"))
#endif
                do! nextTask
        } :> Task
