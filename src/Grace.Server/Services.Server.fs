namespace Grace.Server

open Dapr.Actors
open Giraffe
open Grace.Actors
open Grace.Actors.BranchName
open Grace.Actors.Organization
open Grace.Actors.OrganizationName
open Grace.Actors.Owner
open Grace.Actors.OwnerName
open Grace.Actors.Repository
open Grace.Actors.RepositoryName
open Grace.Actors.Constants
open Grace.Server.ApplicationContext
open Grace.Actors.Reference
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Reference
open Grace.Shared.Dto.Repository
open Grace.Shared.Parameters.Common
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Azure.Cosmos
open Microsoft.Azure.Cosmos.Linq
open NodaTime
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Net
open System.Threading.Tasks
open System.Text
open Grace.Actors.Interfaces

module Services =   //.

    /// Defines the type of all server queries in Grace.
    ///
    /// Takes an HttpContext, the MaxCount of results to return, and the ActorProxy to use for the query, and returns a Task containing the return value.
    type QueryResult<'T, 'U when 'T :> IActor> = HttpContext -> int -> 'T -> Task<'U>

    /// <summary>
    /// Creates common metadata for Grace events.
    /// </summary>
    /// <param name="context">The current HttpContext.</param>
    let createMetadata (context: HttpContext): EventMetadata = 
        {
            Timestamp = getCurrentInstant();
            CorrelationId = context.Items[Constants.CorrelationId].ToString()
            Principal = context.User.Identity.Name;
            Properties = new Dictionary<string, string>()
        }

    /// <summary>
    /// Parses the incoming request body into the specified type.
    /// </summary>
    /// <param name="context">The current HttpContext.</param>
    let parse<'T when 'T :> CommonParameters> (context: HttpContext) = 
        task {
            let! parameters = context.BindJsonAsync<'T>()
            if String.IsNullOrEmpty(parameters.CorrelationId) then
                parameters.CorrelationId <- context.Items[Constants.CorrelationId] :?> String
            return parameters
        }

    /// <summary>
    /// Adds common attributes to the current OpenTelemetry activity, and returns the result.
    /// </summary>
    /// <param name="statusCode">The HTTP status code to return to the user.</param>
    /// <param name="result">The result value to serialize into JSON.</param>
    /// <param name="context">The current HttpContext.</param>
    let returnResult<'T> (statusCode: int) (result: 'T) (context: HttpContext) =
        task {
            Activity.Current.AddTag("correlation_id", context.Items[Constants.CorrelationId] :?> string)
                            .AddTag("http.status_code", statusCode) |> ignore
            context.SetStatusCode(statusCode)
            return! context.WriteJsonAsync(result)
        }

    /// <summary>
    /// Adds common attributes to the current OpenTelemetry activity, and returns the result with a 200 Ok status.
    /// </summary>
    /// <param name="result">The result value to serialize into JSON.</param>
    /// <param name="context">The current HttpContext.</param>
    let result200Ok<'T> = returnResult<'T> StatusCodes.Status200OK

    /// <summary>
    /// Adds common attributes to the current OpenTelemetry activity, and returns the result with a 400 Bad request status.
    /// </summary>
    /// <param name="result">The result value to serialize into JSON.</param>
    /// <param name="context">The current HttpContext.</param>
    let result400BadRequest<'T> = returnResult<'T> StatusCodes.Status400BadRequest

    /// <summary>
    /// Adds common attributes to the current OpenTelemetry activity, and returns the result with a 400 Bad request status.
    /// </summary>
    /// <param name="result">The result value to serialize into JSON.</param>
    /// <param name="context">The current HttpContext.</param>
    let result404NotFound<'T> = returnResult<'T> StatusCodes.Status404NotFound

    /// <summary>
    /// Adds common attributes to the current OpenTelemetry activity, and returns the result with a 500 Internal server error status.
    /// </summary>
    /// <param name="result">The result value to serialize into JSON.</param>
    /// <param name="context">The current HttpContext.</param>
    let result500ServerError<'T> = returnResult<'T> StatusCodes.Status500InternalServerError

    type ownerIdRecord = {ownerId: string}
    type organizationIdRecord = {organizationId: string}
    type repositoryIdRecord = {repositoryId: string}
    type branchIdRecord = {branchId: string}

    let linqSerializerOptions = CosmosLinqSerializerOptions(PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase)
    let queryRequestOptions = QueryRequestOptions()
#if DEBUG
    queryRequestOptions.PopulateIndexMetrics <- true
#endif

    /// <summary>
    /// Gets the OwnerId by returning OwnerId if provided, or searching by OwnerName.
    /// </summary>
    /// <param name="ownerId">The OwnerId provided by the user</param>
    /// <param name="ownerName">The OwnerName provided by the user</param>
    let resolveOwnerId (ownerId: string) (ownerName: string) =
        task {
            if not <| String.IsNullOrEmpty(ownerId) then
                return Some ownerId
            elif String.IsNullOrEmpty(ownerName) then
                return None
            else
                let ownerNameActorProxy = ActorProxyFactory().CreateActorProxy<IOwnerNameActor>(OwnerName.GetActorId(ownerName), ActorName.OwnerName)
                match! ownerNameActorProxy.GetOwnerId() with
                | Some ownerId -> return Some ownerId
                | None ->
                    match ApplicationContext.actorStateStorageProvider with
                    | Unknown -> return None
                    | AzureCosmosDb -> 
                        let queryDefinition = QueryDefinition("""SELECT c["value"].OwnerId FROM c WHERE STRINGEQUALS(c["value"].OwnerName, @ownerName, true) AND c["value"].Class = @class""")
                                                .WithParameter("@ownerName", ownerName)
                                                .WithParameter("@class", "OwnerDto")
                        let iterator = DefaultRetryPolicy.Execute(fun () -> CosmosContainer().GetItemQueryIterator<ownerIdRecord>(queryDefinition))
                        if iterator.HasMoreResults then
                            let! currentResultSet = iterator.ReadNextAsync()
                            let ownerId = currentResultSet.FirstOrDefault({ownerId = String.Empty}).ownerId
                            if String.IsNullOrEmpty(ownerId) then
                                return None
                            else
                                do! ownerNameActorProxy.SetOwnerId(ownerId)
                                return Some ownerId
                        else return None
                    | Cassandra -> return None
        }

    /// <summary>
    /// Gets the OrganizationId by either returning OrganizationId if provided, or searching by OrganizationName.
    /// </summary>
    /// <param name="organizationId">The OrganizationId to find</param>
    /// <param name="organizationName">The OrganizationName to find</param>
    let resolveOrganizationId (ownerId: string) (ownerName: string) (organizationId: string) (organizationName: string) =
        task {
            match! resolveOwnerId ownerId ownerName with
            | Some ownerId ->
                if not <| String.IsNullOrEmpty(organizationId) then
                    return Some organizationId
                elif String.IsNullOrEmpty(organizationName) then
                    return None
                else
                    let organizationNameActorProxy = ApplicationContext.ActorProxyFactory().CreateActorProxy<IOrganizationNameActor>(OrganizationName.GetActorId(organizationName), ActorName.OrganizationName)
                    match! organizationNameActorProxy.GetOrganizationId() with
                    | Some ownerId -> return Some ownerId
                    | None ->
                        match ApplicationContext.actorStateStorageProvider with
                        | Unknown -> return None
                        | AzureCosmosDb -> 
                            let queryDefinition = QueryDefinition("""SELECT c["value"].OrganizationId FROM c WHERE STRINGEQUALS(c["value"].OrganizationName, @organizationName, true) AND c["value"].OwnerId = @ownerId AND c["value"].Class = @class""")
                                                    .WithParameter("@organizationName", organizationName)
                                                    .WithParameter("@ownerId", ownerId)
                                                    .WithParameter("@class", "OrganizationDto")
                            let iterator = DefaultRetryPolicy.Execute(fun () -> CosmosContainer().GetItemQueryIterator<organizationIdRecord>(queryDefinition))
                            if iterator.HasMoreResults then
                                let! currentResultSet = iterator.ReadNextAsync()
                                let organizationId = currentResultSet.FirstOrDefault({organizationId = String.Empty}).organizationId
                                if String.IsNullOrEmpty(organizationId) then
                                    return None
                                else
                                    do! organizationNameActorProxy.SetOrganizationId(organizationId)
                                    return Some organizationId
                            else return None
                        | Cassandra -> return None
            | None -> return None
        }

    /// Gets the RepositoryId by returning RepositoryId if provided, or searching by RepositoryName within the provided owner and organization.
    let resolveRepositoryId (ownerId: string) (ownerName: string) (organizationId: string) (organizationName: string) (repositoryId: string) (repositoryName: string) =
        task {
            match! resolveOwnerId ownerId ownerName with
            | Some ownerId ->
                match! resolveOrganizationId ownerId String.Empty organizationId organizationName with
                | Some organizationId ->
                    if not <| String.IsNullOrEmpty(repositoryId) then
                        return Some repositoryId
                    elif String.IsNullOrEmpty(repositoryName) then
                        return None
                    else
                        let repositoryNameActorProxy = ApplicationContext.ActorProxyFactory().CreateActorProxy<IRepositoryNameActor>(RepositoryName.GetActorId(repositoryName), ActorName.RepositoryName)
                        match! repositoryNameActorProxy.GetRepositoryId() with
                        | Some repositoryId -> return Some repositoryId
                        | None ->
                            match ApplicationContext.actorStateStorageProvider with
                            | Unknown -> return None
                            | AzureCosmosDb -> 
                                let queryDefinition = QueryDefinition("""SELECT c["value"].RepositoryId FROM c WHERE STRINGEQUALS(c["value"].RepositoryName, @repositoryName) AND c["value"].OwnerId = @ownerId AND c["value"].OrganizationId = @organizationId AND c["value"].Class = @class""")
                                                        .WithParameter("@repositoryName", repositoryName)
                                                        .WithParameter("@organizationId", organizationId)
                                                        .WithParameter("@ownerId", ownerId)
                                                        .WithParameter("@class", "RepositoryDto")
                                let iterator = DefaultRetryPolicy.Execute(fun () -> CosmosContainer().GetItemQueryIterator<repositoryIdRecord>(queryDefinition))
                                if iterator.HasMoreResults then
                                    let! currentResultSet = iterator.ReadNextAsync()
                                    let repositoryId = currentResultSet.FirstOrDefault({repositoryId = String.Empty}).repositoryId
                                    if String.IsNullOrEmpty(repositoryId) then
                                        return None
                                    else
                                        do! repositoryNameActorProxy.SetRepositoryId(repositoryId)
                                        return Some repositoryId
                                else return None
                            | Cassandra -> return None
                | None -> return None
            | None -> return None
        }

    let resolveRepositoryId_Linq (ownerId: string) (ownerName: string) (organizationId: string) (organizationName: string) (repositoryId: string) (repositoryName: string) =
        task {
            match! resolveOwnerId ownerId ownerName with
            | Some ownerId ->
                match! resolveOrganizationId ownerId String.Empty organizationId organizationName with
                | Some organizationId ->
                    if not <| String.IsNullOrEmpty(repositoryId) then
                        return Some repositoryId
                    elif String.IsNullOrEmpty(repositoryName) then
                        return None
                    else
                        let actorProxy = ApplicationContext.ActorProxyFactory().CreateActorProxy<IRepositoryNameActor>(RepositoryName.GetActorId(repositoryName), ActorName.RepositoryName)
                        match! actorProxy.GetRepositoryId() with
                        | Some ownerId -> return Some ownerId
                        | None ->
                            match ApplicationContext.actorStateStorageProvider with
                            | Unknown -> return None
                            | AzureCosmosDb -> 
                                let indexMetrics = StringBuilder()
                                let requestCharge = StringBuilder()
                                let query = CosmosContainer().GetItemLinqQueryable<RepositoryDto>(linqSerializerOptions = linqSerializerOptions, requestOptions = queryRequestOptions)
                                                .Where(fun repo -> repo.RepositoryName = (RepositoryName repositoryName) &&
                                                                   repo.OrganizationId = Guid.Parse(organizationId) && 
                                                                   repo.OwnerId = Guid.Parse(ownerId) &&
                                                                   repo.Class = "RepositoryDto")
                                                .Select(fun repo -> $"{repo.RepositoryId}")
                                                .Take(1)
                                                .ToFeedIterator()
                                let mutable retrievedRepositoryId = String.Empty
                                while query.HasMoreResults do
                                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> query.ReadNextAsync())
                                    retrievedRepositoryId <- results.Resource.FirstOrDefault(String.Empty)
                                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
                                if String.IsNullOrEmpty(retrievedRepositoryId) then
                                    return None
                                else
                                    do! actorProxy.SetRepositoryId(retrievedRepositoryId)
                                    return Some retrievedRepositoryId
                            | Cassandra -> return None
                | None -> return None
            | None -> return None
        }

    let resolveBranchId repositoryId branchId branchName =
        task {            
            if not <| String.IsNullOrEmpty(branchId) then
                return Some branchId
            elif String.IsNullOrEmpty(branchName) then
                return None
            else
                let branchNameActorProxy = ApplicationContext.ActorProxyFactory().CreateActorProxy<IBranchNameActor>(BranchName.GetActorId repositoryId branchName, ActorName.BranchName)
                match! branchNameActorProxy.GetBranchId() with
                | Some branchId -> return Some branchId
                | None ->
                    match ApplicationContext.actorStateStorageProvider with
                    | Unknown -> return None
                    | AzureCosmosDb -> 
                        let queryDefinition = QueryDefinition("""SELECT c["value"].BranchId FROM c WHERE STRINGEQUALS(c["value"].BranchName, @branchName, true) AND c["value"].RepositoryId = @repositoryId AND c["value"].Class = @class""")
                                                .WithParameter("@repositoryId", repositoryId)
                                                .WithParameter("@branchName", branchName)
                                                .WithParameter("@class", "BranchDto")
                        let iterator = DefaultRetryPolicy.Execute(fun () -> CosmosContainer().GetItemQueryIterator<branchIdRecord>(queryDefinition))
                        if iterator.HasMoreResults then
                            let! currentResultSet = iterator.ReadNextAsync()
                            let branchId = currentResultSet.FirstOrDefault({branchId = String.Empty}).branchId
                            if String.IsNullOrEmpty(branchId) then
                                return None
                            else
                                do! branchNameActorProxy.SetBranchId(branchId)
                                return Some branchId
                        else return None
                    | Cassandra -> return None
        }
        
    let resolveBranchIdLinq (repositoryId: string) (branchId: string) (branchName: string) =
        task {
            if not <| String.IsNullOrEmpty(branchId) then
                return Some branchId
            elif String.IsNullOrEmpty(branchName) then
                return None
            else
                match ApplicationContext.actorStateStorageProvider with
                | Unknown -> return None
                | AzureCosmosDb -> 
                    let indexMetrics = StringBuilder()
                    let requestCharge = StringBuilder()
                    let query = CosmosContainer().GetItemLinqQueryable<BranchDto>(linqSerializerOptions = linqSerializerOptions, requestOptions = queryRequestOptions)
                                    //.Where(fun branch -> branch.RepositoryId = repositoryId &&
                                    //                     branch.BranchName = branchName &&
                                    //                     branch.Class = "BranchDto")
                                    .Where(fun branch -> branch.RepositoryId = Guid.Parse(repositoryId) &&
                                                         branch.BranchName = (BranchName branchName) &&
                                                         branch.Class = "BranchDto")
                                    .Select(fun branch -> $"{branch.BranchId}")
                                    .Take(1)
                                    .ToFeedIterator()
                    let mutable retrievedBranchId = String.Empty
                    while query.HasMoreResults do
                        let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> query.ReadNextAsync())
                        retrievedBranchId <- results.Resource.FirstOrDefault(String.Empty)
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                    .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
                    if String.IsNullOrEmpty(retrievedBranchId) then
                        return None
                    else
                        return Some retrievedBranchId
                | Cassandra -> return None
        }

    type BranchDtoValue() =
        member val public value = BranchDto.Default with get, set
    type ReferenceDtoValue() =
        member val public value = ReferenceDto.Default with get, set
    type DirectoryVersionValue() =
        member val public value = DirectoryVersion.Default with get, set

    let getBranches (repositoryId: RepositoryId) (maxCount: int) includeDeleted = 
        task {
            let branches = List<BranchDto>()
            match ApplicationContext.actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                try
                    let indexMetrics = StringBuilder()
                    let requestCharge = StringBuilder()
                    let includeDeletedClause = if includeDeleted then String.Empty else """ AND IS_NULL(c["value"].DeletedAt)"""
                    let queryDefinition = QueryDefinition($"""SELECT TOP @maxCount c["value"] FROM c WHERE c["value"].RepositoryId = @repositoryId AND c["value"].Class = @class {includeDeletedClause} ORDER BY c["value"].CreatedAt DESC""")
                                            .WithParameter("@repositoryId", repositoryId)
                                            .WithParameter("@maxCount", maxCount)
                                            .WithParameter("@class", "BranchDto")
                    let iterator = CosmosContainer().GetItemQueryIterator<BranchDtoValue>(queryDefinition, requestOptions = queryRequestOptions)
                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                        branches.AddRange(results.Resource.Select(fun v -> v.value))
                    Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                    .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
                with ex ->
                    logToConsole $"Got an exception."
                    logToConsole $"{createExceptionResponse ex}"
            | Cassandra -> ()
            return branches
        }

    let getReference (referenceId: ReferenceId) = 
        task {
            let mutable referenceDto = ReferenceDto.Default
            match ApplicationContext.actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition("""SELECT * FROM c WHERE c["value"].Class = @class AND c["value"].ReferenceId = @referenceId""")
                                        .WithParameter("@referenceId", $"{referenceId}")
                                        .WithParameter("@class", "ReferenceDto")
                let iterator = CosmosContainer().GetItemQueryIterator<ReferenceDto>(queryDefinition, requestOptions = queryRequestOptions)
                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    if results.Count = 1 then
                        referenceDto <- results.Resource.First()
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
            | Cassandra -> ()
            return referenceDto
        }

    let getReferenceBySha256Hash (sha256Hash: Sha256Hash) = 
        task {
            let mutable referenceDto = ReferenceDto.Default
            match ApplicationContext.actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition("""SELECT * FROM c["value"] c WHERE c.Class = @class AND STARTSWITH(c.Sha256Hash, @sha256Hash, true)""")
                                        .WithParameter("@sha256Hash", $"{sha256Hash}")
                                        .WithParameter("@class", "ReferenceDto")
                let iterator = CosmosContainer().GetItemQueryIterator<ReferenceDto>(queryDefinition, requestOptions = queryRequestOptions)
                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    if results.Count = 1 then
                        referenceDto <- results.Resource.First()
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
            | Cassandra -> ()
            return referenceDto
        }

    let getReferences (branchId: BranchId) (maxCount: int) = 
        task {
            let references = List<ReferenceDto>()
            match ApplicationContext.actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition("""SELECT TOP @maxCount c["value"].Class, c["value"].ReferenceId, c["value"].BranchId, c["value"].DirectoryId, c["value"].Sha256Hash, c["value"].ReferenceType, c["value"].ReferenceText, c["value"].CreatedAt FROM c WHERE c["value"].BranchId = @branchId AND c["value"].Class = @class ORDER BY c["value"].CreatedAt DESC""")
                                        .WithParameter("@branchId", $"{branchId}")
                                        .WithParameter("@maxCount", maxCount)
                                        .WithParameter("@class", "ReferenceDto")
                let iterator = CosmosContainer().GetItemQueryIterator<ReferenceDto>(queryDefinition, requestOptions = queryRequestOptions)
                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    //let! results = iterator.ReadNextAsync()
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    references.AddRange(results.Resource)
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
            | Cassandra -> ()

            return references :> IReadOnlyList<ReferenceDto>
        }

    type DocumentIdentifier() =
        member val id = String.Empty with get, set
        member val partitionKey = String.Empty with get, set

    /// Deletes all documents from CosmosDb.
    ///
    /// **** This method is implemented only in Debug configuration. It is a no-op in Release configuration. ****
    let deleteAllFromCosmosDB() =
        task {
        #if DEBUG
            let failed = List<string>()
            try
                let queryDefinition = QueryDefinition("SELECT c.id, c.partitionKey FROM c")
                let iterator = CosmosContainer().GetItemQueryIterator<DocumentIdentifier>(queryDefinition, requestOptions = queryRequestOptions)
                while iterator.HasMoreResults do
                    let! results = iterator.ReadNextAsync()
                    for document in results.Resource do
                        let! deleteResponse = CosmosContainer().DeleteItemAsync(document.id, PartitionKey(document.partitionKey))
                        if deleteResponse.StatusCode <> HttpStatusCode.NoContent then
                            failed.Add(document.id)
                            logToConsole $"Failed to delete id {document.id}."
                return failed
            with ex ->
                failed.Add((createExceptionResponse ex).``exception``)
                return failed
        #else
            return List<string>(["Not implemented"])
        #endif
        }

    let getReferencesLinq branchId = 
        task {
            let references = List<ReferenceDto>()
            match ApplicationContext.actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let query = CosmosContainer().GetItemLinqQueryable<ReferenceDto>(requestOptions = queryRequestOptions)
                                .Where(fun ref -> ref.BranchId = branchId && ref.Class = "ReferenceDto")
                                //.OrderByDescending(fun ref -> ref.CreatedAt)
                                .ToFeedIterator()
                while query.HasMoreResults do
                    let! results = query.ReadNextAsync()
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    references.AddRange(results.Resource)
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
            | Cassandra -> ()

            return references :> IReadOnlyList<ReferenceDto>
        }

    let getReferencesByType (referenceType: ReferenceType) (branchId: BranchId) (maxCount: int) = 
        task {
            let references = List<ReferenceDto>()
            match ApplicationContext.actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition("""SELECT TOP @maxCount c["value"].Class, c["value"].ReferenceId, c["value"].BranchId, c["value"].DirectoryId, c["value"].Sha256Hash, c["value"].ReferenceType, c["value"].ReferenceText, c["value"].CreatedAt FROM c WHERE c["value"].BranchId = @branchId AND STRINGEQUALS(c["value"].ReferenceType, @referenceType, true) AND c["value"].Class = @class ORDER BY c["value"].CreatedAt DESC""")
                                        .WithParameter("@maxCount", maxCount)
                                        .WithParameter("@branchId", $"{branchId}")
                                        .WithParameter("@referenceType", discriminatedUnionCaseNameToString referenceType)
                                        .WithParameter("@class", "ReferenceDto")
                let iterator = CosmosContainer().GetItemQueryIterator<ReferenceDto>(queryDefinition, requestOptions = queryRequestOptions)
                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    references.AddRange(results.Resource)
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
            | Cassandra -> ()

            return references :> IReadOnlyList<ReferenceDto>
        }

    let getMerges = getReferencesByType ReferenceType.Merge
    let getCommits = getReferencesByType ReferenceType.Commit
    let getCheckpoints = getReferencesByType ReferenceType.Checkpoint
    let getSaves = getReferencesByType ReferenceType.Save
    let getTags = getReferencesByType ReferenceType.Tag

    let getLatestReference branchId =
        task {
            match ApplicationContext.actorStateStorageProvider with
            | Unknown -> return None
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition("""SELECT TOP 1 c["value"] FROM c WHERE c["value"].BranchId = @branchId AND c["value"].Class = @class ORDER BY c["value"].CreatedAt DESC""")
                                        .WithParameter("@branchId", $"{branchId}")
                                        .WithParameter("@class", "ReferenceDto")
                let iterator = CosmosContainer().GetItemQueryIterator<ReferenceDtoValue>(queryDefinition, requestOptions = queryRequestOptions)
                let mutable referenceDto = ReferenceDto.Default
                while iterator.HasMoreResults do
                    let! results = iterator.ReadNextAsync()
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    if results.Count > 0 then
                        referenceDto <- results.Resource.First().value
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
                if referenceDto.ReferenceId <> ReferenceDto.Default.ReferenceId then
                    return Some referenceDto
                else
                    return None
            | Cassandra -> return None
        }

    let getLatestReferenceByType referenceType (branchId: BranchId) =
        task {
            match ApplicationContext.actorStateStorageProvider with
            | Unknown -> return None
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition("""SELECT TOP 1 c["value"] FROM c WHERE c["value"].BranchId = @branchId AND c["value"].Class = @class AND STRINGEQUALS(c["value"].ReferenceType, @referenceType, true) ORDER BY c["value"].CreatedAt DESC""")
                                        .WithParameter("@branchId", $"{branchId}")
                                        .WithParameter("@referenceType", discriminatedUnionCaseNameToString referenceType)
                                        .WithParameter("@class", "ReferenceDto")
                let iterator = CosmosContainer().GetItemQueryIterator<ReferenceDtoValue>(queryDefinition, requestOptions = queryRequestOptions)
                let mutable referenceDto = ReferenceDto.Default
                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    if results.Count > 0 then
                        referenceDto <- results.Resource.First().value
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
                if referenceDto.ReferenceId <> ReferenceDto.Default.ReferenceId then
                    return Some referenceDto
                else
                    return None
            | Cassandra -> return None
        }

    let getLatestMerge = getLatestReferenceByType ReferenceType.Merge
    let getLatestCommit = getLatestReferenceByType ReferenceType.Commit
    let getLatestCheckpoint = getLatestReferenceByType ReferenceType.Checkpoint
    let getLatestSave = getLatestReferenceByType ReferenceType.Save
    let getLatestTag = getLatestReferenceByType ReferenceType.Tag

    /// Gets a DirectoryVersion by searching using a Sha256Hash value.
    let getDirectoryBySha256Hash (repositoryId: RepositoryId) (sha256Hash: Sha256Hash) = 
        task {
            let mutable directoryVersion = DirectoryVersion.Default
            match ApplicationContext.actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition("""SELECT TOP 1 c["value"] FROM c WHERE c["value"].RepositoryId = @repositoryId AND STARTSWITH(c["value"].Sha256Hash, @sha256Hash, true) AND c["value"].Class = @class""")
                                        .WithParameter("@sha256Hash", $"{sha256Hash}")
                                        .WithParameter("@repositoryId", $"{repositoryId}")
                                        .WithParameter("@class", "DirectoryVersion")
                let iterator = CosmosContainer().GetItemQueryIterator<DirectoryVersionValue>(queryDefinition, requestOptions = queryRequestOptions)
                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    if results.Count > 0 then
                        directoryVersion <- results.Resource.FirstOrDefault().value
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
            | Cassandra -> ()

            return directoryVersion
        }

    /// Gets a Root DirectoryVersion by searching using a Sha256Hash value.
    let getRootDirectoryBySha256Hash (repositoryId: RepositoryId) (sha256Hash: Sha256Hash) = 
        task {
            let mutable directoryVersion = DirectoryVersion.Default
            match ApplicationContext.actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition($"""SELECT TOP 1 c["value"] FROM c WHERE c["value"].RepositoryId = @repositoryId AND STARTSWITH(c["value"].Sha256Hash, @sha256Hash, true) AND c["value"].RelativePath = @relativePath AND c["value"].Class = @class""")
                                        .WithParameter("@sha256Hash", $"{sha256Hash}")
                                        .WithParameter("@repositoryId", $"{repositoryId}")
                                        .WithParameter("@relativePath", $"{Constants.RootDirectoryPath}")
                                        .WithParameter("@class", "DirectoryVersion")
                logToConsole $"{queryDefinition.QueryText}"
                for (s, o) in queryDefinition.GetQueryParameters() do
                    logToConsole $"{s}: {o}"
                let iterator = CosmosContainer().GetItemQueryIterator<DirectoryVersionValue>(queryDefinition, requestOptions = queryRequestOptions)
                while iterator.HasMoreResults do
                    let! results = iterator.ReadNextAsync()
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    if results.Count > 0 then
                        directoryVersion <- results.Resource.FirstOrDefault().value
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
            | Cassandra -> ()

            return directoryVersion
        }

    /// Gets a Root DirectoryVersion by searching using a Sha256Hash value.
    let getRootDirectoryByReferenceId (repositoryId: RepositoryId) (referenceId: ReferenceId) = 
        task {
            let referenceActorId = Reference.GetActorId(referenceId)
            let referenceActorProxy = ApplicationContext.ActorProxyFactory().CreateActorProxy<IReferenceActor>(referenceActorId, ActorName.Reference)
            let! referenceDto = referenceActorProxy.Get()

            return! getRootDirectoryBySha256Hash repositoryId referenceDto.Sha256Hash
        }

    /// Checks if all of the supplied DirectoryIds exist.
    let directoryIdsExist (repositoryId: RepositoryId) (directoryIds: IEnumerable<DirectoryId>) = 
        task {
            match ApplicationContext.actorStateStorageProvider with
            | Unknown -> return false
            | AzureCosmosDb -> 
                let mutable requestCharge = 0.0
                let mutable allExist = true
                let directoryIdQueue = Queue<DirectoryId>(directoryIds)
                while directoryIdQueue.Count > 0 && allExist do
                    let directoryId = directoryIdQueue.Dequeue()
                    let queryDefinition = QueryDefinition("""SELECT c FROM c 
                                            WHERE c["value"].RepositoryId = @repositoryId 
                                                AND c["value"].DirectoryId = @directoryId
                                                AND c["value"].Class = @class""")
                                            .WithParameter("@repositoryId", $"{repositoryId}")
                                            .WithParameter("@directoryId", $"{directoryId}")
                                            .WithParameter("@class", "DirectoryVersion")
                    let iterator = CosmosContainer().GetItemQueryIterator<DirectoryVersion>(queryDefinition, requestOptions = queryRequestOptions)
                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        requestCharge <- requestCharge + results.RequestCharge
                        if not <| results.Resource.Any() then allExist <- false
                Activity.Current.SetTag("allExist", $"{allExist}")
                                .SetTag("totalRequestCharge", $"{requestCharge}") |> ignore
                return allExist
            | Cassandra -> return false
        }

    /// Gets a list of ReferenceDtos based on ReferenceIds.
    let getReferencesByReferenceId (referenceIds: IEnumerable<ReferenceId>) (maxCount: int) =
        task {
            let referenceDtos = List<ReferenceDto>()
            match ApplicationContext.actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let mutable requestCharge = 0.0
                for referenceId in referenceIds do
                    let queryDefinition = QueryDefinition("""SELECT TOP 1 c["value"] FROM c 
                                            WHERE c["value"].ReferenceId = @referenceId
                                                AND c["value"].Class = @class""")
                                            .WithParameter("@referenceId", $"{referenceId}")
                                            .WithParameter("@class", "ReferenceDto")
                    let iterator = CosmosContainer().GetItemQueryIterator<ReferenceDtoValue>(queryDefinition, requestOptions = queryRequestOptions)
                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        requestCharge <- requestCharge + results.RequestCharge
                        if results.Resource.Count() > 0 then referenceDtos.Add(results.Resource.First().value)
                
                Activity.Current.SetTag("referenceDtos.Count", $"{referenceDtos.Count}")
                                .SetTag("totalRequestCharge", $"{requestCharge}") |> ignore
            | Cassandra -> ()

            return referenceDtos 
        }

    /// Gets a list of BranchDtos based on BranchIds.
    let getBranchesByBranchId (repositoryId: RepositoryId) (branchIds: IEnumerable<BranchId>) (maxCount: int) includeDeleted =
        task {
            let branchDtos = List<BranchDto>()
            match ApplicationContext.actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let mutable requestCharge = 0.0
                let includeDeletedClause = if includeDeleted then String.Empty else """ AND IS_NULL(c["value"].DeletedAt)"""
                let branchIdStack = Queue<ReferenceId>(branchIds)
                while branchIdStack.Count > 0 do 
                    let branchId = branchIdStack.Dequeue()
                    let queryDefinition = QueryDefinition($"""SELECT TOP 1 c["value"] FROM c 
                                            WHERE c["value"].RepositoryId = @repositoryId 
                                                AND c["value"].BranchId = @branchId
                                                AND c["value"].Class = @class
                                                {includeDeletedClause}""")
                                            .WithParameter("@repositoryId", $"{repositoryId}")
                                            .WithParameter("@branchId", $"{branchId}")
                                            .WithParameter("@class", "BranchDto")
                    let iterator = CosmosContainer().GetItemQueryIterator<BranchDtoValue>(queryDefinition, requestOptions = queryRequestOptions)
                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        requestCharge <- requestCharge + results.RequestCharge
                        if results.Resource.Count() > 0 then branchDtos.Add(results.Resource.First().value)
                
                Activity.Current.SetTag("referenceDtos.Count", $"{branchDtos.Count}")
                                .SetTag("totalRequestCharge", $"{requestCharge}") |> ignore
            | Cassandra -> ()
            
            return branchDtos
        }

    /// Gets the CorrelationId from HttpContext.Items.
    let getCorrelationId (context: HttpContext) = (context.Items[Constants.CorrelationId] :?> string)
