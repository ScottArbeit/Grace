namespace Grace.Actors

open Azure.Storage
open Azure.Storage.Blobs
open Azure.Storage.Sas
open Dapr.Actors
open Dapr.Actors.Client
open Dapr.Client
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Organization
open Grace.Shared.Dto.Reference
open Grace.Shared.Dto.Repository
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Azure.Cosmos
open Microsoft.Azure.Cosmos.Linq
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Linq
open System.Net
open System.Text
open System.Threading.Tasks

module Services =

    type ServerGraceIndex = Dictionary<RelativePath, DirectoryVersion>
    type ownerIdRecord = {ownerId: string}
    type organizationIdRecord = {organizationId: string}
    type repositoryIdRecord = {repositoryId: string}
    type branchIdRecord = {branchId: string}

    let repositoryContainerNameCache = ConcurrentDictionary<Guid, string>()
    let containerClients = new ConcurrentDictionary<string, BlobContainerClient>()
    
    let daprHttpEndpoint = $"{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprHttpPort)}"
    let daprGrpcEndpoint = $"{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprGrpcPort)}"
    let daprClient = DaprClientBuilder().UseJsonSerializationOptions(Constants.JsonSerializerOptions).UseHttpEndpoint(daprHttpEndpoint).UseGrpcEndpoint(daprGrpcEndpoint).Build()
    let private azureStorageConnectionString = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageConnectionString)
    let private storageKey = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageKey)
    let private sharedKeyCredential = StorageSharedKeyCredential(DefaultObjectStorageAccount, storageKey)

    //let actorProxyOptions = ActorProxyOptions(JsonSerializerOptions = Constants.JsonSerializerOptions, HttpEndpoint = daprEndpoint)
    //let ActorProxyFactory = ActorProxyFactory(actorProxyOptions)

    let GetBranchNameActorId repositoryId branchName = ActorId($"{repositoryId}-{branchName}")

    let mutable actorProxyFactory: IActorProxyFactory = null
    let setActorProxyFactory proxyFactory =
        actorProxyFactory <- proxyFactory
    let ActorProxyFactory() = actorProxyFactory

    let mutable actorStateStorageProvider: ActorStateStorageProvider = ActorStateStorageProvider.Unknown
    let setActorStateStorageProvider storageProvider =
        actorStateStorageProvider <- storageProvider

    let mutable private cosmosClient: CosmosClient = null
    let setCosmosClient (client: CosmosClient) =
        cosmosClient <- client
    
    let mutable private cosmosContainer: Container = null
    let setCosmosContainer (container: Container) =
        cosmosContainer <- container

    let mutable internal loggerFactory: ILoggerFactory = null
    let setLoggerFactory (factory: ILoggerFactory) =
        loggerFactory <- factory

    let mutable internal memoryCache: IMemoryCache = null
    let setMemoryCache (cache: IMemoryCache) =
        memoryCache <- cache

    let linqSerializerOptions = CosmosLinqSerializerOptions(PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase)

    /// Custom QueryRequestOptions that requests Index Metrics only in DEBUG build.
    let queryRequestOptions = QueryRequestOptions()
#if DEBUG
    queryRequestOptions.PopulateIndexMetrics <- true
#endif

    /// Gets a CosmosDB container client for the given container.
    let getContainerClient (storageAccountName: StorageAccountName) (containerName: StorageContainerName) =
        task {
            let key = $"{storageAccountName}-{containerName}"
            if containerClients.ContainsKey(key) then
                return containerClients[key]
            else
                let blobContainerClient = BlobContainerClient(azureStorageConnectionString, $"{containerName}")
                let! azureResponse = blobContainerClient.CreateIfNotExistsAsync(publicAccessType = Models.PublicAccessType.None)
                containerClients[key] <- blobContainerClient
                return blobContainerClient
        }

    /// Gets an Azure Blob Storage client instance for the given repository and file version.
    let getAzureBlobClient (repositoryDto: RepositoryDto) (fileVersion: FileVersion) = 
        task {
            //logToConsole $"* In getAzureBlobClient; repositoryId: {repositoryDto.RepositoryId}; fileVersion: {fileVersion.RelativePath}."
            let containerNameActorId = ActorId($"{repositoryDto.RepositoryId}")
            let containerNameActorProxy = actorProxyFactory.CreateActorProxy<IContainerNameActor>(containerNameActorId, ActorName.ContainerName)
            let! containerName = containerNameActorProxy.GetContainerName()
            match containerName with
            | Ok containerName ->
                let! containerClient = getContainerClient repositoryDto.StorageAccountName containerName
                let blobClient = containerClient.GetBlobClient($"{fileVersion.RelativePath}/{fileVersion.GetObjectFileName}")
                return Ok blobClient
            | Error error ->
                return Error error
        }

    /// Creates a full URI for a specific file version.
    let private createAzureBlobSasUri (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (permission: BlobSasPermissions) =
        task {
            //logToConsole $"In createAzureBlobSasUri; fileVersion.RelativePath: {fileVersion.RelativePath}."
            let containerNameActorId = ActorId($"{repositoryDto.RepositoryId}")
            let containerNameActorProxy = actorProxyFactory.CreateActorProxy<IContainerNameActor>(containerNameActorId, ActorName.ContainerName)
            let! containerName = containerNameActorProxy.GetContainerName()
            //logToConsole $"containerName: {containerName}."
            match containerName with
            | Ok containerName ->
                let! blobContainerClient = getContainerClient repositoryDto.StorageAccountName containerName
            
                let blobSasBuilder = BlobSasBuilder(permission, DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(Constants.SharedAccessSignatureExpiration)))
                blobSasBuilder.BlobName <- Path.Combine($"{fileVersion.RelativePath}", fileVersion.GetObjectFileName)
                blobSasBuilder.BlobContainerName <- containerName
                blobSasBuilder.StartsOn <- DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(15.0))
                let sasUriParameters = blobSasBuilder.ToSasQueryParameters(sharedKeyCredential)
                return Ok $"{blobContainerClient.Uri}/{fileVersion.RelativePath}/{fileVersion.GetObjectFileName}?{sasUriParameters}"
            | Error error -> return Error error
        }

    /// Gets a shared access signature for reading from the object storage provider.
    let getReadSharedAccessSignature (repositoryDto: RepositoryDto) (fileVersion: FileVersion) =
        task {
            match repositoryDto.ObjectStorageProvider with
                | AzureBlobStorage ->
                    let! sas = createAzureBlobSasUri repositoryDto fileVersion (BlobSasPermissions.Read ||| BlobSasPermissions.List)
                    match sas with
                    | Ok sas -> return Ok (sas.ToString())
                    | Error error -> return Error error
                | AWSS3 ->
                    return Error "Not implemented"
                | GoogleCloudStorage ->
                    return Error "Not implemented"
                | ObjectStorageProvider.Unknown ->
                    return Error "Not implemented"
        }

    /// Gets a shared access signature for writing to the object storage provider.
    let getWriteSharedAccessSignature (repositoryDto: RepositoryDto) (fileVersion: FileVersion) =
        task {
            match repositoryDto.ObjectStorageProvider with
                | AWSS3 ->
                    return Uri("http://localhost:3500")
                | AzureBlobStorage ->
                    // Adding read permission to allow for calls to .ExistsAsync().
                    let! sas = createAzureBlobSasUri repositoryDto fileVersion (BlobSasPermissions.Create ||| BlobSasPermissions.Write ||| BlobSasPermissions.Move  ||| BlobSasPermissions.Tag ||| BlobSasPermissions.Read)
                    match sas with 
                    | Ok sas -> 
                        //logToConsole $"In Actor.Services.getWriteSharedAccessSignature; {sas}"
                        return Uri(sas)
                    | Error error -> 
                        //logToConsole $"In Actor.Services.getWriteSharedAccessSignature; {error}"
                        return Uri("http://localhost")
                | GoogleCloudStorage ->
                    return Uri("http://localhost:3500")
                | ObjectStorageProvider.Unknown ->
                    return Uri("http://localhost:3500")
        }

    /// Gets the OwnerId by checking for the existence of OwnerId if provided, or searching by OwnerName.
    let resolveOwnerId (ownerId: string) (ownerName: string) =
        task {
            let mutable ownerGuid = Guid.Empty
            if not <| String.IsNullOrEmpty(ownerId) && Guid.TryParse(ownerId, &ownerGuid) then
                let mutable x = obj
                let cached = memoryCache.TryGetValue(ownerGuid, &x)
                if cached then
                    return Some ownerId
                else
                    let actorProxy = actorProxyFactory.CreateActorProxy<IOwnerActor>(ActorId(ownerId), ActorName.Owner)
                    let! exists = actorProxy.Exists()
                    if exists then
                        use newCacheEntry = memoryCache.CreateEntry(ownerGuid, Value = null, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)
                        return Some ownerId
                    else
                        return None
            elif String.IsNullOrEmpty(ownerName) then
                return None
            else
                let ownerNameActorProxy = actorProxyFactory.CreateActorProxy<IOwnerNameActor>(ActorId(ownerName), ActorName.OwnerName)
                match! ownerNameActorProxy.GetOwnerId() with
                | Some ownerId -> return Some ownerId
                | None ->
                    match actorStateStorageProvider with
                    | Unknown -> return None
                    | AzureCosmosDb -> 
                        let queryDefinition = QueryDefinition("""SELECT c["value"].OwnerId FROM c WHERE STRINGEQUALS(c["value"].OwnerName, @ownerName, true) AND c["value"].Class = @class""")
                                                .WithParameter("@ownerName", ownerName)
                                                .WithParameter("@class", "OwnerDto")
                        let iterator = DefaultRetryPolicy.Execute(fun () -> cosmosContainer.GetItemQueryIterator<ownerIdRecord>(queryDefinition))
                        if iterator.HasMoreResults then
                            let! currentResultSet = iterator.ReadNextAsync()
                            let ownerId = currentResultSet.FirstOrDefault({ownerId = String.Empty}).ownerId
                            if String.IsNullOrEmpty(ownerId) then
                                return None
                            else
                                do! ownerNameActorProxy.SetOwnerId(ownerId)
                                ownerGuid <- Guid.Parse(ownerId)
                                use newCacheEntry = memoryCache.CreateEntry(ownerGuid, Value = null, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)
                                return Some ownerId
                        else return None
                    | MongoDB -> return None
        }

    /// Gets the OrganizationId by either returning OrganizationId if provided, or searching by OrganizationName.
    let resolveOrganizationId (ownerId: string) (ownerName: string) (organizationId: string) (organizationName: string) =
        task {
            let mutable organizationGuid = Guid.Empty
            match! resolveOwnerId ownerId ownerName with
            | Some ownerId ->
                if not <| String.IsNullOrEmpty(organizationId) && Guid.TryParse(organizationId, &organizationGuid) then
                    let mutable x = obj
                    let cached = memoryCache.TryGetValue(organizationGuid, &x)
                    if cached then
                        return Some organizationId
                    else
                        let actorProxy = actorProxyFactory.CreateActorProxy<IOrganizationActor>(ActorId(organizationId), ActorName.Organization)
                        let! exists = actorProxy.Exists()
                        if exists then
                            use newCacheEntry = memoryCache.CreateEntry(organizationGuid, Value = null, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)
                            return Some organizationId
                        else
                            return None
                elif String.IsNullOrEmpty(organizationName) then
                    return None
                else
                    let organizationNameActorProxy = actorProxyFactory.CreateActorProxy<IOrganizationNameActor>(ActorId(organizationName), ActorName.OrganizationName)
                    match! organizationNameActorProxy.GetOrganizationId() with
                    | Some ownerId -> return Some ownerId
                    | None ->
                        match actorStateStorageProvider with
                        | Unknown -> return None
                        | AzureCosmosDb -> 
                            let queryDefinition = QueryDefinition("""SELECT c["value"].OrganizationId FROM c WHERE STRINGEQUALS(c["value"].OrganizationName, @organizationName, true) AND c["value"].OwnerId = @ownerId AND c["value"].Class = @class""")
                                                    .WithParameter("@organizationName", organizationName)
                                                    .WithParameter("@ownerId", ownerId)
                                                    .WithParameter("@class", "OrganizationDto")
                            let iterator = DefaultRetryPolicy.Execute(fun () -> cosmosContainer.GetItemQueryIterator<organizationIdRecord>(queryDefinition))
                            if iterator.HasMoreResults then
                                let! currentResultSet = iterator.ReadNextAsync()
                                let organizationId = currentResultSet.FirstOrDefault({organizationId = String.Empty}).organizationId
                                if String.IsNullOrEmpty(organizationId) then
                                    return None
                                else
                                    do! organizationNameActorProxy.SetOrganizationId(organizationId)
                                    organizationGuid <- Guid.Parse(organizationId)
                                    use newCacheEntry = memoryCache.CreateEntry(organizationGuid, Value = null, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)
                                    return Some organizationId
                            else return None
                        | MongoDB -> return None
            | None -> return None
        }

    /// Gets the RepositoryId by returning RepositoryId if provided, or searching by RepositoryName within the provided owner and organization.
    let resolveRepositoryId (ownerId: string) (ownerName: string) (organizationId: string) (organizationName: string) (repositoryId: string) (repositoryName: string) =
        task {
            let mutable repositoryGuid = Guid.Empty
            match! resolveOwnerId ownerId ownerName with
            | Some ownerId ->
                match! resolveOrganizationId ownerId String.Empty organizationId organizationName with
                | Some organizationId ->
                    if not <| String.IsNullOrEmpty(repositoryId) && Guid.TryParse(repositoryId, &repositoryGuid) then
                        let mutable x = obj
                        let cached = memoryCache.TryGetValue(repositoryGuid, &x)
                        if cached then
                            return Some repositoryId
                        else
                            let actorProxy = actorProxyFactory.CreateActorProxy<IRepositoryActor>(ActorId(repositoryId), ActorName.Repository)
                            let! exists = actorProxy.Exists()
                            if exists then
                                use newCacheEntry = memoryCache.CreateEntry(repositoryGuid, Value = null, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)
                                return Some repositoryId
                            else
                                return None
                    elif String.IsNullOrEmpty(repositoryName) then
                        return None
                    else
                        let repositoryNameActorProxy = actorProxyFactory.CreateActorProxy<IRepositoryNameActor>(ActorId(repositoryName), ActorName.RepositoryName)
                        match! repositoryNameActorProxy.GetRepositoryId() with
                        | Some repositoryId -> return Some repositoryId
                        | None ->
                            match actorStateStorageProvider with
                            | Unknown -> return None
                            | AzureCosmosDb -> 
                                let queryDefinition = QueryDefinition("""SELECT c["value"].RepositoryId FROM c WHERE STRINGEQUALS(c["value"].RepositoryName, @repositoryName) AND c["value"].OwnerId = @ownerId AND c["value"].OrganizationId = @organizationId AND c["value"].Class = @class""")
                                                        .WithParameter("@repositoryName", repositoryName)
                                                        .WithParameter("@organizationId", organizationId)
                                                        .WithParameter("@ownerId", ownerId)
                                                        .WithParameter("@class", "RepositoryDto")
                                let iterator = cosmosContainer.GetItemQueryIterator<repositoryIdRecord>(queryDefinition)
                                if iterator.HasMoreResults then
                                    let! currentResultSet = iterator.ReadNextAsync()
                                    let repositoryId = currentResultSet.FirstOrDefault({repositoryId = String.Empty}).repositoryId
                                    if String.IsNullOrEmpty(repositoryId) then
                                        return None
                                    else
                                        do! repositoryNameActorProxy.SetRepositoryId(repositoryId)
                                        repositoryGuid <- Guid.Parse(repositoryId)
                                        use newCacheEntry = memoryCache.CreateEntry(repositoryGuid, Value = null, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)
                                        return Some repositoryId
                                else return None
                            | MongoDB -> return None
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
                        let actorProxy = actorProxyFactory.CreateActorProxy<IRepositoryNameActor>(ActorId(repositoryName), ActorName.RepositoryName)
                        match! actorProxy.GetRepositoryId() with
                        | Some ownerId -> return Some ownerId
                        | None ->
                            match actorStateStorageProvider with
                            | Unknown -> return None
                            | AzureCosmosDb -> 
                                let indexMetrics = StringBuilder()
                                let requestCharge = StringBuilder()
                                let query = cosmosContainer.GetItemLinqQueryable<RepositoryDto>(linqSerializerOptions = linqSerializerOptions, requestOptions = queryRequestOptions)
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
                            | MongoDB -> return None
                | None -> return None
            | None -> return None
        }

    /// Gets the BranchId by returning BranchId if provided, or searching by BranchName within the provided repository.
    let resolveBranchId repositoryId branchId branchName =
        task {            
            let mutable branchGuid = Guid.Empty
            if not <| String.IsNullOrEmpty(branchId) then
                let mutable x = obj
                let cached = memoryCache.TryGetValue(branchGuid, &x)
                if cached then
                    return Some branchId
                else
                    let actorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(ActorId(branchId), ActorName.Branch)
                    let! exists = actorProxy.Exists()
                    if exists then
                        use newCacheEntry = memoryCache.CreateEntry(branchGuid, Value = null, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)
                        return Some branchId
                    else
                        return None
            elif String.IsNullOrEmpty(branchName) then
                return None
            else
                let branchNameActorProxy = actorProxyFactory.CreateActorProxy<IBranchNameActor>(GetBranchNameActorId repositoryId branchName, ActorName.BranchName)
                match! branchNameActorProxy.GetBranchId() with
                | Some branchId -> return Some branchId
                | None ->
                    match actorStateStorageProvider with
                    | Unknown -> return None
                    | AzureCosmosDb -> 
                        let queryDefinition = QueryDefinition("""SELECT c["value"].BranchId FROM c WHERE STRINGEQUALS(c["value"].BranchName, @branchName, true) AND c["value"].RepositoryId = @repositoryId AND c["value"].Class = @class""")
                                                .WithParameter("@repositoryId", repositoryId)
                                                .WithParameter("@branchName", branchName)
                                                .WithParameter("@class", "BranchDto")
                        let iterator = DefaultRetryPolicy.Execute(fun () -> cosmosContainer.GetItemQueryIterator<branchIdRecord>(queryDefinition))
                        if iterator.HasMoreResults then
                            let! currentResultSet = iterator.ReadNextAsync()
                            let branchId = currentResultSet.FirstOrDefault({branchId = String.Empty}).branchId
                            if String.IsNullOrEmpty(branchId) then
                                return None
                            else
                                do! branchNameActorProxy.SetBranchId(branchId)
                                branchGuid <- Guid.Parse(branchId)
                                use newCacheEntry = memoryCache.CreateEntry(branchGuid, Value = null, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)
                                return Some branchId
                        else return None
                    | MongoDB -> return None
        }
        
    let resolveBranchIdLinq (repositoryId: string) (branchId: string) (branchName: string) =
        task {
            if not <| String.IsNullOrEmpty(branchId) then
                return Some branchId
            elif String.IsNullOrEmpty(branchName) then
                return None
            else
                match actorStateStorageProvider with
                | Unknown -> return None
                | AzureCosmosDb -> 
                    let indexMetrics = StringBuilder()
                    let requestCharge = StringBuilder()
                    let query = cosmosContainer.GetItemLinqQueryable<BranchDto>(linqSerializerOptions = linqSerializerOptions, requestOptions = queryRequestOptions)
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
                | MongoDB -> return None
        }

    type OrganizationDtoValue() =
        member val public value = OrganizationDto.Default with get, set
    type RepositoryDtoValue() =
        member val public value = RepositoryDto.Default with get, set
    type BranchDtoValue() =
        member val public value = BranchDto.Default with get, set
    type ReferenceDtoValue() =
        member val public value = ReferenceDto.Default with get, set
    type DirectoryVersionValue() =
        member val public value = DirectoryVersion.Default with get, set

    /// Gets a list of organizations for the specified owner.
    let getOrganizations (ownerId: OwnerId) (maxCount: int) includeDeleted = 
        task {
            let repositories = List<OrganizationDto>()
            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                try
                    let indexMetrics = StringBuilder()
                    let requestCharge = StringBuilder()
                    let includeDeletedClause = if includeDeleted then String.Empty else """ AND IS_NULL(c["value"].DeletedAt)"""
                    let queryDefinition = QueryDefinition($"""SELECT TOP @maxCount c["value"] FROM c WHERE c["value"].OwnerId = @ownerId AND c["value"].Class = @class {includeDeletedClause} ORDER BY c["value"].CreatedAt DESC""")
                                            .WithParameter("@ownerId", ownerId)
                                            .WithParameter("@maxCount", maxCount)
                                            .WithParameter("@class", "OrganizationDto")
                    let iterator = cosmosContainer.GetItemQueryIterator<OrganizationDtoValue>(queryDefinition, requestOptions = queryRequestOptions)
                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                        repositories.AddRange(results.Resource.Select(fun v -> v.value))
                    Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                    .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
                with ex ->
                    logToConsole $"Got an exception."
                    logToConsole $"{createExceptionResponse ex}"
            | MongoDB -> ()
            return repositories
        }

    /// Checks if the specified organization name is unique for the specified owner.
    let organizationNameIsUnique<'T> (ownerId: string) (ownerName: string) (organizationName: string) =
        task {
            match actorStateStorageProvider with
            | Unknown -> return Ok false
            | AzureCosmosDb -> 
                try
                    match! resolveOwnerId ownerId ownerName with
                    | Some ownerId ->
                        let queryDefinition = QueryDefinition("""SELECT c["value"].OrganizationId FROM c WHERE c["value"].OwnerId = @ownerId AND c["value"].OrganizationName = @organizationName AND c["value"].Class = @class""")
                                                .WithParameter("@ownerId", ownerId)
                                                .WithParameter("@organizationName", organizationName)
                                                .WithParameter("@class", "OrganizationDto")
                        //logToConsole (queryDefinition.QueryText.Replace("@ownerId", $"\"{ownerId}\"").Replace("@organizationName", $"\"{organizationName}\"").Replace("@class", "\"OrganizationDto\""))
                        let iterator = cosmosContainer.GetItemQueryIterator<organizationIdRecord>(queryDefinition, requestOptions = queryRequestOptions)
                        if iterator.HasMoreResults then
                            let! currentResultSet = iterator.ReadNextAsync()
                            // If a row is returned, and organizationId gets a value, then the organization name is not unique.
                            let organizationId = currentResultSet.FirstOrDefault({organizationId = String.Empty}).organizationId
                            if String.IsNullOrEmpty(organizationId) then
                                // The organization name is unique.
                                return Ok true
                            else
                                // The organization name is not unique.
                                return Ok false
                        else return Ok true     // This else should never be hit.
                    | None -> return Ok false
                with ex ->
                    return Error $"{createExceptionResponse ex}"
            | MongoDB -> return Ok false
        }

    /// Checks if the specified repository name is unique for the specified organization.
    let repositoryNameIsUnique<'T> (ownerId: string) (ownerName: string) (organizationId: string) (organizationName: string) (repositoryName: string) =
        task {
            match actorStateStorageProvider with
            | Unknown -> return Ok false
            | AzureCosmosDb -> 
                try
                    match! resolveOrganizationId ownerId ownerName organizationId organizationName with
                    | Some organizationId ->
                        let queryDefinition = QueryDefinition("""SELECT c["value"].RepositoryId FROM c WHERE c["value"].OrganizationId = @organizationId AND c["value"].RepositoryName = @repositoryName AND c["value"].Class = @class""")
                                                .WithParameter("@organizationId", organizationId)
                                                .WithParameter("@repositoryName", repositoryName)
                                                .WithParameter("@class", "RepositoryDto")
                        //logToConsole (queryDefinition.QueryText.Replace("@organizationId", $"\"{organizationId}\"").Replace("@repositoryName", $"\"{repositoryName}\""))
                        let iterator = cosmosContainer.GetItemQueryIterator<repositoryIdRecord>(queryDefinition, requestOptions = queryRequestOptions)
                        if iterator.HasMoreResults then
                            let! currentResultSet = iterator.ReadNextAsync()
                            // If a row is returned, and repositoryId gets a value, then the repository name is not unique.
                            let repositoryId = currentResultSet.FirstOrDefault({repositoryId = String.Empty}).repositoryId
                            if String.IsNullOrEmpty(repositoryId) then
                                // The repository name is unique.
                                return Ok true
                            else
                                // The repository name is not unique.
                                return Ok false
                        else return Ok true     // This else should never be hit.
                    | None -> return Ok false
                with ex ->
                    return Error $"{createExceptionResponse ex}"
            | MongoDB -> return Ok false
        }

    /// Gets a list of repositories for the specified organization.
    let getRepositories (organizationId: OrganizationId) (maxCount: int) includeDeleted = 
        task {
            let repositories = List<RepositoryDto>()
            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                try
                    let indexMetrics = StringBuilder()
                    let requestCharge = StringBuilder()
                    let includeDeletedClause = if includeDeleted then String.Empty else """ AND IS_NULL(c["value"].DeletedAt)"""
                    let queryDefinition = QueryDefinition($"""SELECT TOP @maxCount c["value"] FROM c WHERE c["value"].OrganizationId = @organizationId AND c["value"].Class = @class {includeDeletedClause} ORDER BY c["value"].CreatedAt DESC""")
                                            .WithParameter("@organizationId", organizationId)
                                            .WithParameter("@maxCount", maxCount)
                                            .WithParameter("@class", "RepositoryDto")
                    let iterator = cosmosContainer.GetItemQueryIterator<RepositoryDtoValue>(queryDefinition, requestOptions = queryRequestOptions)
                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                        repositories.AddRange(results.Resource.Select(fun v -> v.value))
                    Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                    .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
                with ex ->
                    logToConsole $"Got an exception."
                    logToConsole $"{createExceptionResponse ex}"
            | MongoDB -> ()
            return repositories
        }

    /// Gets a list of branches for a given repository.
    let getBranches (repositoryId: RepositoryId) (maxCount: int) includeDeleted = 
        task {
            let branches = List<BranchDto>()
            match actorStateStorageProvider with
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
                    let iterator = cosmosContainer.GetItemQueryIterator<BranchDtoValue>(queryDefinition, requestOptions = queryRequestOptions)
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
            | MongoDB -> ()
            return branches
        }

    /// Gets a reference by its id.
    let getReference (referenceId: ReferenceId) = 
        task {
            let mutable referenceDto = ReferenceDto.Default
            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition("""SELECT * FROM c WHERE c["value"].Class = @class AND c["value"].ReferenceId = @referenceId""")
                                        .WithParameter("@referenceId", $"{referenceId}")
                                        .WithParameter("@class", "ReferenceDto")
                let iterator = cosmosContainer.GetItemQueryIterator<ReferenceDto>(queryDefinition, requestOptions = queryRequestOptions)
                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    if results.Count = 1 then
                        referenceDto <- results.Resource.First()
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
            | MongoDB -> ()
            return referenceDto
        }

    /// Gets a reference by its SHA-256 hash.
    let getReferenceBySha256Hash (sha256Hash: Sha256Hash) = 
        task {
            let mutable referenceDto = ReferenceDto.Default
            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition("""SELECT * FROM c["value"] c WHERE c.Class = @class AND STARTSWITH(c.Sha256Hash, @sha256Hash, true)""")
                                        .WithParameter("@sha256Hash", $"{sha256Hash}")
                                        .WithParameter("@class", "ReferenceDto")
                let iterator = cosmosContainer.GetItemQueryIterator<ReferenceDto>(queryDefinition, requestOptions = queryRequestOptions)
                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    if results.Count = 1 then
                        referenceDto <- results.Resource.First()
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
            | MongoDB -> ()
            return referenceDto
        }

    /// Gets a list of references for a given branch.
    let getReferences (branchId: BranchId) (maxCount: int) = 
        task {
            let references = List<ReferenceDto>()
            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition("""SELECT TOP @maxCount c["value"].Class, c["value"].ReferenceId, c["value"].BranchId, c["value"].DirectoryId, c["value"].Sha256Hash, c["value"].ReferenceType, c["value"].ReferenceText, c["value"].CreatedAt FROM c WHERE c["value"].BranchId = @branchId AND c["value"].Class = @class ORDER BY c["value"].CreatedAt DESC""")
                                        .WithParameter("@branchId", $"{branchId}")
                                        .WithParameter("@maxCount", maxCount)
                                        .WithParameter("@class", "ReferenceDto")
                let iterator = cosmosContainer.GetItemQueryIterator<ReferenceDto>(queryDefinition, requestOptions = queryRequestOptions)
                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    //let! results = iterator.ReadNextAsync()
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    references.AddRange(results.Resource)
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
            | MongoDB -> ()

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
                let queryDefinition = QueryDefinition("SELECT c.id, c.partitionKey FROM c ORDER BY c.partitionKey")
                let iterator = cosmosContainer.GetItemQueryIterator<DocumentIdentifier>(queryDefinition, requestOptions = queryRequestOptions)
                while iterator.HasMoreResults do
                    let! results = iterator.ReadNextAsync()
                    for document in results.Resource do
                        let! deleteResponse = cosmosContainer.DeleteItemAsync(document.id, PartitionKey(document.partitionKey))
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
            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let query = cosmosContainer.GetItemLinqQueryable<ReferenceDto>(requestOptions = queryRequestOptions)
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
            | MongoDB -> ()

            return references :> IReadOnlyList<ReferenceDto>
        }

    /// Gets a list of references of a given ReferenceType for a branch.
    let getReferencesByType (referenceType: ReferenceType) (branchId: BranchId) (maxCount: int) = 
        task {
            let references = List<ReferenceDto>()
            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition("""SELECT TOP @maxCount c["value"].Class, c["value"].ReferenceId, c["value"].BranchId, c["value"].DirectoryId, c["value"].Sha256Hash, c["value"].ReferenceType, c["value"].ReferenceText, c["value"].CreatedAt FROM c WHERE c["value"].BranchId = @branchId AND STRINGEQUALS(c["value"].ReferenceType, @referenceType, true) AND c["value"].Class = @class ORDER BY c["value"].CreatedAt DESC""")
                                        .WithParameter("@maxCount", maxCount)
                                        .WithParameter("@branchId", $"{branchId}")
                                        .WithParameter("@referenceType", getDiscriminatedUnionCaseName referenceType)
                                        .WithParameter("@class", "ReferenceDto")
                let iterator = cosmosContainer.GetItemQueryIterator<ReferenceDto>(queryDefinition, requestOptions = queryRequestOptions)
                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    references.AddRange(results.Resource)
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
            | MongoDB -> ()

            return references :> IReadOnlyList<ReferenceDto>
        }

    let getPromotions = getReferencesByType ReferenceType.Promotion
    let getCommits = getReferencesByType ReferenceType.Commit
    let getCheckpoints = getReferencesByType ReferenceType.Checkpoint
    let getSaves = getReferencesByType ReferenceType.Save
    let getTags = getReferencesByType ReferenceType.Tag

    let getLatestReference branchId =
        task {
            match actorStateStorageProvider with
            | Unknown -> return None
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition("""SELECT TOP 1 c["value"] FROM c WHERE c["value"].BranchId = @branchId AND c["value"].Class = @class ORDER BY c["value"].CreatedAt DESC""")
                                        .WithParameter("@branchId", $"{branchId}")
                                        .WithParameter("@class", nameof(ReferenceDto))
                let iterator = cosmosContainer.GetItemQueryIterator<ReferenceDtoValue>(queryDefinition, requestOptions = queryRequestOptions)
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
            | MongoDB -> return None
        }

    /// Gets the latest reference for a given ReferenceType in a branch.
    let getLatestReferenceByType referenceType (branchId: BranchId) =
        task {
            match actorStateStorageProvider with
            | Unknown -> return None
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition("""SELECT TOP 1 c["value"] FROM c WHERE c["value"].BranchId = @branchId AND c["value"].Class = @class AND STRINGEQUALS(c["value"].ReferenceType, @referenceType, true) ORDER BY c["value"].CreatedAt DESC""")
                                        .WithParameter("@branchId", $"{branchId}")
                                        .WithParameter("@referenceType", getDiscriminatedUnionCaseName referenceType)
                                        .WithParameter("@class", nameof(ReferenceDto))
                let iterator = cosmosContainer.GetItemQueryIterator<ReferenceDtoValue>(queryDefinition, requestOptions = queryRequestOptions)
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
            | MongoDB -> return None
        }

    /// Gets the latest promotion from a branch.
    let getLatestPromotion = getLatestReferenceByType ReferenceType.Promotion
    
    /// Gets the latest commit from a branch.
    let getLatestCommit = getLatestReferenceByType ReferenceType.Commit
    
    /// Gets the latest checkpoint from a branch.
    let getLatestCheckpoint = getLatestReferenceByType ReferenceType.Checkpoint
    
    /// Gets the latest save from a branch.
    let getLatestSave = getLatestReferenceByType ReferenceType.Save
    
    /// Gets the latest tag from a branch.
    let getLatestTag = getLatestReferenceByType ReferenceType.Tag

    /// Gets a DirectoryVersion by searching using a Sha256Hash value.
    let getDirectoryBySha256Hash (repositoryId: RepositoryId) (sha256Hash: Sha256Hash) = 
        task {
            let mutable directoryVersion = DirectoryVersion.Default
            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()
                let queryDefinition = QueryDefinition("""SELECT TOP 1 c["value"] FROM c WHERE c["value"].RepositoryId = @repositoryId AND STARTSWITH(c["value"].Sha256Hash, @sha256Hash, true) AND c["value"].Class = @class""")
                                        .WithParameter("@sha256Hash", $"{sha256Hash}")
                                        .WithParameter("@repositoryId", $"{repositoryId}")
                                        .WithParameter("@class", "DirectoryVersion")
                let iterator = cosmosContainer.GetItemQueryIterator<DirectoryVersionValue>(queryDefinition, requestOptions = queryRequestOptions)
                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    if results.Count > 0 then
                        directoryVersion <- results.Resource.FirstOrDefault().value
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
            | MongoDB -> ()

            return directoryVersion
        }

    /// Gets a Root DirectoryVersion by searching using a Sha256Hash value.
    let getRootDirectoryBySha256Hash (repositoryId: RepositoryId) (sha256Hash: Sha256Hash) = 
        task {
            let mutable directoryVersion = DirectoryVersion.Default
            match actorStateStorageProvider with
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
                let iterator = cosmosContainer.GetItemQueryIterator<DirectoryVersionValue>(queryDefinition, requestOptions = queryRequestOptions)
                while iterator.HasMoreResults do
                    let! results = iterator.ReadNextAsync()
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                    if results.Count > 0 then
                        directoryVersion <- results.Resource.FirstOrDefault().value
                Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}") |> ignore
            | MongoDB -> ()

            return directoryVersion
        }

    /// Gets a Root DirectoryVersion by searching using a Sha256Hash value.
    let getRootDirectoryByReferenceId (repositoryId: RepositoryId) (referenceId: ReferenceId) = 
        task {
            let referenceActorId = ActorId($"{referenceId}")
            let referenceActorProxy = actorProxyFactory.CreateActorProxy<IReferenceActor>(referenceActorId, ActorName.Reference)
            let! referenceDto = referenceActorProxy.Get()

            return! getRootDirectoryBySha256Hash repositoryId referenceDto.Sha256Hash
        }

    /// Checks if all of the supplied DirectoryIds exist.
    let directoryIdsExist (repositoryId: RepositoryId) (directoryIds: IEnumerable<DirectoryId>) = 
        task {
            match actorStateStorageProvider with
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
                    let iterator = cosmosContainer.GetItemQueryIterator<DirectoryVersion>(queryDefinition, requestOptions = queryRequestOptions)
                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        requestCharge <- requestCharge + results.RequestCharge
                        if not <| results.Resource.Any() then allExist <- false
                Activity.Current.SetTag("allExist", $"{allExist}")
                                .SetTag("totalRequestCharge", $"{requestCharge}") |> ignore
                return allExist
            | MongoDB -> return false
        }

    /// Gets a list of ReferenceDtos based on ReferenceIds. The list is returned in the same order as the supplied ReferenceIds.
    let getReferencesByReferenceId (referenceIds: IEnumerable<ReferenceId>) (maxCount: int) =
        task {
            let referenceDtos = List<ReferenceDto>()
            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let mutable requestCharge = 0.0
                let mutable clientElapsedTime = TimeSpan.Zero

                // In order to build the IN clause, we need to create a parameter for each referenceId. (I tried just using string concatenation, it didn't work for some reason. Anyway...)
                // The query starts with:
                let queryText = StringBuilder(@"SELECT TOP @maxCount c[""value""] FROM c WHERE c[""value""].Class = @class and c[""value""].ReferenceId IN (")
                // Then we add a parameter for each referenceId.
                referenceIds |> Seq.iteri (fun i referenceId -> queryText.Append($"@referenceId{i},") |> ignore)
                // Then we remove the last comma and close the parenthesis.
                queryText.Remove(queryText.Length - 1, 1).Append(")") |> ignore

                // Create the query definition.
                let queryDefinition = QueryDefinition(queryText.ToString())
                                        .WithParameter("@maxCount", referenceIds.Count())
                                        .WithParameter("@class", nameof(ReferenceDto))

                // Add a .WithParameter for each referenceId.
                referenceIds |> Seq.iteri (fun i referenceId -> queryDefinition.WithParameter($"@referenceId{i}", $"{referenceId}") |> ignore)

                // Execute the query.
                let iterator = cosmosContainer.GetItemQueryIterator<ReferenceDtoValue>(queryDefinition, requestOptions = queryRequestOptions)

                // The query will return fewer results than the number of referenceIds if the supplied referenceIds have duplicates.
                //   This is normal for `grace status` (BasedOn and Latest Promotion are likely to be the same, for instance).
                //   We need to gather the query results, and then iterate through the referenceId's to return the dto's in the same order.
                let queryResults = Dictionary<ReferenceId, ReferenceDto>()
                while iterator.HasMoreResults do
                    let! results = iterator.ReadNextAsync()
                    requestCharge <- requestCharge + results.RequestCharge
                    clientElapsedTime <- clientElapsedTime + results.Diagnostics.GetClientElapsedTime()
                    results.Resource |> Seq.iter (fun refDto -> queryResults.Add(refDto.value.ReferenceId, refDto.value))
                
                // Add the results to the list in the same order as the supplied referenceIds.
                referenceIds |> Seq.iter (fun referenceId -> 
                    if referenceId <> ReferenceId.Empty then 
                        if queryResults.ContainsKey(referenceId) then
                            referenceDtos.Add(queryResults[referenceId]) 
                    else
                        // In case the caller supplied an empty referenceId, add a default ReferenceDto.
                        referenceDtos.Add(ReferenceDto.Default))

                Activity.Current.SetTag("referenceDtos.Count", $"{referenceDtos.Count}")
                                .SetTag("clientElapsedTime", $"{clientElapsedTime}")
                                .SetTag("totalRequestCharge", $"{requestCharge}") |> ignore
            | MongoDB -> ()

            return referenceDtos
        }

    /// Gets a list of BranchDtos based on BranchIds.
    let getBranchesByBranchId (repositoryId: RepositoryId) (branchIds: IEnumerable<BranchId>) (maxCount: int) includeDeleted =
        task {
            let branchDtos = List<BranchDto>()
            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb -> 
                let mutable requestCharge = 0.0
                let includeDeletedClause = if includeDeleted then String.Empty else """ AND IS_NULL(c["value"].DeletedAt)"""
                let branchIdStack = Queue<ReferenceId>(branchIds)
                while branchIdStack.Count > 0 do 
                    let branchId = branchIdStack.Dequeue()
                    let queryDefinition = QueryDefinition($"""SELECT TOP @maxCount c["value"] FROM c 
                                            WHERE c["value"].RepositoryId = @repositoryId 
                                                AND c["value"].BranchId = @branchId
                                                AND c["value"].Class = @class
                                                {includeDeletedClause}""")
                                            .WithParameter("@maxCount", maxCount)
                                            .WithParameter("@repositoryId", $"{repositoryId}")
                                            .WithParameter("@branchId", $"{branchId}")
                                            .WithParameter("@class", "BranchDto")
                    let iterator = cosmosContainer.GetItemQueryIterator<BranchDtoValue>(queryDefinition, requestOptions = queryRequestOptions)
                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        requestCharge <- requestCharge + results.RequestCharge
                        if results.Resource.Count() > 0 then branchDtos.Add(results.Resource.First().value)
                
                Activity.Current.SetTag("referenceDtos.Count", $"{branchDtos.Count}")
                                .SetTag("totalRequestCharge", $"{requestCharge}") |> ignore
            | MongoDB -> ()
            
            return branchDtos
        }
