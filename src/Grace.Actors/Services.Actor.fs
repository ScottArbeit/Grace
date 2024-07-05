namespace Grace.Actors

open Azure.Storage
open Azure.Storage.Blobs
open Azure.Storage.Sas
open Dapr.Actors
open Dapr.Actors.Client
open Dapr.Client
open Grace.Actors.Constants
open Grace.Actors.Events
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
open System.Net.Http
open System.Net.Http.Json
open System.Net.Security
open System.Text
open System.Threading.Tasks
open System.Threading
open System

module Services =
    type ServerGraceIndex = Dictionary<RelativePath, DirectoryVersion>
    type OwnerIdRecord = { ownerId: string }
    type OrganizationIdRecord = { organizationId: string }
    type RepositoryIdRecord = { repositoryId: string }
    type BranchIdRecord = { branchId: string }

    /// Dictionary for caching blob container clients
    let containerClients = new ConcurrentDictionary<string, BlobContainerClient>()

    /// Dapr HTTP endpoint retrieved from environment variables
    let daprHttpEndpoint =
        $"{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprHttpPort)}"

    /// Dapr gRPC endpoint retrieved from environment variables
    let daprGrpcEndpoint =
        $"{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprGrpcPort)}"

    /// Dapr client instance
    let daprClient =
        DaprClientBuilder()
            .UseJsonSerializationOptions(Constants.JsonSerializerOptions)
            .UseHttpEndpoint(daprHttpEndpoint)
            .UseGrpcEndpoint(daprGrpcEndpoint)
            .Build()

    /// Azure Storage connection string retrieved from environment variables
    let private azureStorageConnectionString = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageConnectionString)

    /// Azure Storage key retrieved from environment variables
    let private azureStorageKey = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.AzureStorageKey)

    /// Shared key credential for Azure Storage
    let private sharedKeyCredential = StorageSharedKeyCredential(DefaultObjectStorageAccount, azureStorageKey)

    /// Actor proxy factory instance
    let mutable actorProxyFactory: IActorProxyFactory = null

    /// Setter for actor proxy factory
    let setActorProxyFactory proxyFactory = actorProxyFactory <- proxyFactory

    /// Getter for actor proxy factory
    let ActorProxyFactory () = actorProxyFactory

    /// Actor state storage provider instance
    let mutable actorStateStorageProvider: ActorStateStorageProvider = ActorStateStorageProvider.Unknown

    /// Setter for actor state storage provider
    let setActorStateStorageProvider storageProvider = actorStateStorageProvider <- storageProvider

    /// Cosmos client instance
    let mutable private cosmosClient: CosmosClient = null

    /// Setter for Cosmos client
    let setCosmosClient (client: CosmosClient) = cosmosClient <- client

    /// Cosmos container instance
    let mutable private cosmosContainer: Container = null

    /// Setter for Cosmos container
    let setCosmosContainer (container: Container) = cosmosContainer <- container

    /// Logger factory instance
    let mutable internal loggerFactory: ILoggerFactory = null

    /// Setter for logger factory
    let setLoggerFactory (factory: ILoggerFactory) = loggerFactory <- factory

    /// Grace Server's universal .NET memory cache
    let mutable internal memoryCache: IMemoryCache = null

    /// Setter for memory cache
    let setMemoryCache (cache: IMemoryCache) = memoryCache <- cache

    /// Logger instance for the Services.Actor module.
    let log = Lazy<ILogger>(fun () -> loggerFactory.CreateLogger("Services.Actor"))

    /// Cosmos LINQ serializer options
    let linqSerializerOptions = CosmosLinqSerializerOptions(PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase)

    /// Gets the ActorId for an OwnerName actor.
    let getOwnerNameActorId ownerName = ActorId(ownerName)

    /// Gets the ActorId for an OrganizationName actor.
    let getOrganizationNameActorId ownerId organizationName = ActorId($"{organizationName}|{ownerId}")

    /// Gets the ActorId for a RepositoryName actor.
    let getRepositoryNameActorId ownerId organizationId repositoryName = ActorId($"{repositoryName}|{ownerId}|{organizationId}")

    /// Gets the ActorId for a BranchName actor.
    let getBranchNameActorId repositoryId branchName = ActorId($"{branchName}|{repositoryId}")

    /// Custom QueryRequestOptions that requests Index Metrics only in DEBUG build.
    let queryRequestOptions = QueryRequestOptions()
#if DEBUG
    queryRequestOptions.PopulateIndexMetrics <- true
#endif

    /// Gets a CosmosDB container client for the given container.
    let getContainerClient (storageAccountName: StorageAccountName) (containerName: StorageContainerName) =
        task {
            let key = $"{storageAccountName}-{containerName}"

            let mutable blobContainerClient: BlobContainerClient = null

            if memoryCache.TryGetValue(key, &blobContainerClient) then
                return blobContainerClient
            else
                let blobContainerClient = BlobContainerClient(azureStorageConnectionString, $"{containerName}")
                let! azureResponse = blobContainerClient.CreateIfNotExistsAsync(publicAccessType = Models.PublicAccessType.None)
                let memoryCacheEntryOptions = MemoryCacheEntryOptions(SlidingExpiration = TimeSpan.FromMinutes(15.0))
                return memoryCache.Set(key, blobContainerClient, memoryCacheEntryOptions)
        }

    /// Gets an Azure Blob Storage client instance for the given repository and file version.
    let getAzureBlobClient (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (correlationId: CorrelationId) =
        task {
            //logToConsole $"* In getAzureBlobClient; repositoryId: {repositoryDto.RepositoryId}; fileVersion: {fileVersion.RelativePath}."
            let containerNameActorId = ActorId($"{repositoryDto.RepositoryId}")

            let containerNameActorProxy = actorProxyFactory.CreateActorProxy<IContainerNameActor>(containerNameActorId, ActorName.ContainerName)

            let! containerName = containerNameActorProxy.GetContainerName correlationId

            match containerName with
            | Ok containerName ->
                let! containerClient = getContainerClient repositoryDto.StorageAccountName containerName

                let blobClient = containerClient.GetBlobClient($"{fileVersion.RelativePath}/{fileVersion.GetObjectFileName}")

                return Ok blobClient
            | Error error -> return Error error
        }

    /// Creates a full URI for a specific file version.
    let private createAzureBlobSasUri
        (repositoryDto: RepositoryDto)
        (fileVersion: FileVersion)
        (permission: BlobSasPermissions)
        (correlationId: CorrelationId)
        =
        task {
            //logToConsole $"In createAzureBlobSasUri; fileVersion.RelativePath: {fileVersion.RelativePath}."
            let containerNameActorId = ActorId($"{repositoryDto.RepositoryId}")

            let containerNameActorProxy = actorProxyFactory.CreateActorProxy<IContainerNameActor>(containerNameActorId, ActorName.ContainerName)

            let! containerName = containerNameActorProxy.GetContainerName correlationId
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
    let getReadSharedAccessSignature (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (correlationId: CorrelationId) =
        task {
            match repositoryDto.ObjectStorageProvider with
            | AzureBlobStorage ->
                let! sas = createAzureBlobSasUri repositoryDto fileVersion (BlobSasPermissions.Read ||| BlobSasPermissions.List) correlationId

                match sas with
                | Ok sas -> return Ok(sas.ToString())
                | Error error -> return Error error
            | AWSS3 -> return Error "Not implemented"
            | GoogleCloudStorage -> return Error "Not implemented"
            | ObjectStorageProvider.Unknown -> return Error "Not implemented"
        }

    /// Gets a shared access signature for writing to the object storage provider.
    let getWriteSharedAccessSignature (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (correlationId: CorrelationId) =
        task {
            match repositoryDto.ObjectStorageProvider with
            | AWSS3 -> return Uri("http://localhost:3500")
            | AzureBlobStorage ->
                // Adding read permission to allow for calls to .ExistsAsync().
                let! sas =
                    createAzureBlobSasUri
                        repositoryDto
                        fileVersion
                        (BlobSasPermissions.Create
                         ||| BlobSasPermissions.Write
                         ||| BlobSasPermissions.Move
                         ||| BlobSasPermissions.Tag
                         ||| BlobSasPermissions.Read)
                        correlationId

                match sas with
                | Ok sas ->
                    //logToConsole $"In Actor.Services.getWriteSharedAccessSignature; {sas}"
                    return Uri(sas)
                | Error error ->
                    //logToConsole $"In Actor.Services.getWriteSharedAccessSignature; {error}"
                    return Uri("http://localhost")
            | GoogleCloudStorage -> return Uri("http://localhost:3500")
            | ObjectStorageProvider.Unknown -> return Uri("http://localhost:3500")
        }

    /// Checks whether an owner name exists in the system.
    let ownerNameExists (ownerName: string) cacheResultIfNotFound (correlationId: CorrelationId) =
        task {
            let ownerNameActorId = getOwnerNameActorId ownerName
            let ownerNameActorProxy = actorProxyFactory.CreateActorProxy<IOwnerNameActor>(ownerNameActorId, ActorName.OwnerName)

            match! ownerNameActorProxy.GetOwnerId correlationId with
            | Some ownerId -> return true
            | None ->
                // Check if the owner name exists in the database.
                // If it does, we need to add it to the memoryCache, and store the ownerId in the OwnerNameActor.
                // If it does not, we need to add it to the memoryCache with a false value.
                // We have to call into Actor storage to get the OwnerId.
                match actorStateStorageProvider with
                | Unknown -> return false
                | AzureCosmosDb ->
                    let queryDefinition =
                        QueryDefinition(
                            """SELECT events.Event.created.ownerId
                            FROM c
                            JOIN events IN c["value"]
                            WHERE ENDSWITH(c.id, @stateStorageName) AND STRINGEQUALS(events.Event.created.ownerName, @ownerName, true)"""
                        )
                            .WithParameter("@ownerName", ownerName)
                            .WithParameter("@stateStorageName", StateName.Owner)

                    let iterator = DefaultRetryPolicy.Execute(fun () -> cosmosContainer.GetItemQueryIterator<OwnerIdRecord>(queryDefinition))
                    let mutable ownerGuid = OwnerId.Empty

                    if iterator.HasMoreResults then
                        let! currentResultSet = iterator.ReadNextAsync()
                        let ownerId = currentResultSet.FirstOrDefault({ ownerId = String.Empty }).ownerId

                        if String.IsNullOrEmpty(ownerId) && cacheResultIfNotFound then
                            // We didn't find the OwnerId, so add this OwnerName to the MemoryCache and indicate that we have already checked.
                            use newCacheEntry =
                                memoryCache.CreateEntry(
                                    $"OwN:{ownerName}",
                                    Value = Constants.MemoryCacheValueGuid,
                                    AbsoluteExpirationRelativeToNow = DefaultExpirationTime
                                )

                            return false
                        elif String.IsNullOrEmpty(ownerId) then
                            // We didn't find the OwnerId, so return false.
                            return false
                        else
                            // Add this OwnerName and OwnerId to the MemoryCache.
                            ownerGuid <- Guid.Parse(ownerId)

                            use newCacheEntry1 =
                                memoryCache.CreateEntry($"OwN:{ownerName}", Value = ownerGuid, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)

                            use newCacheEntry2 = memoryCache.CreateEntry(ownerGuid, Value = true, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)

                            // Set the OwnerId in the OwnerName actor.
                            do! ownerNameActorProxy.SetOwnerId ownerId correlationId
                            return true
                    else
                        return false
                | MongoDB -> return false

        }

    /// Gets the OwnerId by checking for the existence of OwnerId if provided, or searching by OwnerName.
    let resolveOwnerId (ownerId: string) (ownerName: string) (correlationId: CorrelationId) =
        task {
            //logToConsole $"***In resolveOwnerId: Stack Trace: {Environment.StackTrace}"
            let mutable ownerGuid = Guid.Empty

            if not <| String.IsNullOrEmpty(ownerId) && Guid.TryParse(ownerId, &ownerGuid) then
                // Check if we have this owner id in MemoryCache.
                let exists = memoryCache.Get<string>(ownerGuid)

                match exists with
                | MemoryCacheExistsValue -> return Some ownerId
                | MemoryCacheDoesNotExistValue -> return None
                | _ ->
                    // Call the Owner actor to check if the owner exists.
                    let ownerActorProxy = actorProxyFactory.CreateActorProxy<IOwnerActor>(ActorId(ownerId), ActorName.Owner)

                    let! exists = ownerActorProxy.Exists correlationId

                    if exists then
                        // Add this OwnerId to the MemoryCache.
                        use newCacheEntry = memoryCache.CreateEntry(ownerGuid, Value = MemoryCacheExistsValue, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)
                        return Some ownerId
                    else
                        return None
            elif String.IsNullOrEmpty(ownerName) then
                // We have no OwnerId or OwnerName to resolve.
                return None
            else
                // Check if we have this owner name in MemoryCache.
                let cached = memoryCache.TryGetValue($"OwN:{ownerName}", &ownerGuid)

                if ownerGuid.Equals(Constants.MemoryCacheValueGuid) then
                    // We have already checked and the owner does not exist.
                    return None
                elif cached then
                    // We have already checked and the owner exists.
                    return Some(ownerGuid.ToString())
                else
                    // Check if we have an active OwnerName actor with a cached result.
                    let ownerNameActorProxy = actorProxyFactory.CreateActorProxy<IOwnerNameActor>((getOwnerNameActorId ownerName), ActorName.OwnerName)

                    match! ownerNameActorProxy.GetOwnerId correlationId with
                    | Some ownerId ->
                        // Add this OwnerName to the MemoryCache.
                        use newCacheEntry =
                            memoryCache.CreateEntry($"OwN:{ownerName}", Value = ownerGuid, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)

                        // Add the OwnerId to the MemoryCache.
                        use newCacheEntry2 = memoryCache.CreateEntry(ownerGuid, Value = MemoryCacheExistsValue, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)

                        return Some $"{ownerId}"
                    | None ->
                        let! nameExists = ownerNameExists ownerName true correlationId

                        if nameExists then
                            // We have already checked and the owner exists.
                            let cached = memoryCache.TryGetValue($"OwN:{ownerName}", &ownerGuid)
                            return Some $"{ownerGuid}"
                        else
                            // The owner name does not exist.
                            return None
        }

    /// Gets the OrganizationId by either returning OrganizationId if provided, or searching by OrganizationName.
    let resolveOrganizationId (ownerId: string) (ownerName: string) (organizationId: string) (organizationName: string) (correlationId: CorrelationId) =
        task {
            let mutable organizationGuid = Guid.Empty

            if not <| String.IsNullOrEmpty(organizationId)
                && Guid.TryParse(organizationId, &organizationGuid)
            then
                let exists = memoryCache.Get<string>(organizationGuid)
                match exists with
                | MemoryCacheExistsValue -> return Some organizationId
                | MemoryCacheDoesNotExistValue -> return None
                | _ ->
                    // Call the Organization actor to check if the organization exists.
                    let actorProxy = actorProxyFactory.CreateActorProxy<IOrganizationActor>(ActorId(organizationId), ActorName.Organization)

                    let! exists = actorProxy.Exists correlationId

                    if exists then
                        // Add this OrganizationId to the MemoryCache.
                        use newCacheEntry = memoryCache.CreateEntry(organizationGuid, Value = MemoryCacheExistsValue, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)
                        ()

                    if exists then return Some organizationId else return None
            elif String.IsNullOrEmpty(organizationName) then
                // We have no OrganizationId or OrganizationName to resolve.
                return None
            else
                // Check if we have this organization name in MemoryCache.
                let cached = memoryCache.TryGetValue($"OrN:{organizationName}", &organizationGuid)

                if organizationGuid.Equals(Constants.MemoryCacheValueGuid) then
                    // We have already checked and the organization does not exist.
                    return None
                elif cached then
                    // We have already checked and the organization exists.
                    return Some $"{organizationGuid}"
                else
                    // Check if we have an active OrganizationName actor with a cached result.
                    let organizationNameActorProxy =
                        actorProxyFactory.CreateActorProxy<IOrganizationNameActor>(
                            (getOrganizationNameActorId ownerId organizationName),
                            ActorName.OrganizationName
                        )

                    match! organizationNameActorProxy.GetOrganizationId correlationId with
                    | Some ownerId -> return Some $"{ownerId}"
                    | None ->
                        // We have to call into Actor storage to get the OrganizationId.
                        match actorStateStorageProvider with
                        | Unknown -> return None
                        | AzureCosmosDb ->
                            let queryDefinition =
                                QueryDefinition(
                                    """SELECT c["value"].OrganizationId FROM c WHERE STRINGEQUALS(c["value"].OrganizationName, @organizationName, true) AND c["value"].OwnerId = @ownerId AND c["value"].Class = @class"""
                                )
                                    .WithParameter("@organizationName", organizationName)
                                    .WithParameter("@ownerId", ownerId)
                                    .WithParameter("@class", nameof (OrganizationDto))

                            let iterator = DefaultRetryPolicy.Execute(fun () -> cosmosContainer.GetItemQueryIterator<OrganizationIdRecord>(queryDefinition))

                            if iterator.HasMoreResults then
                                let! currentResultSet = iterator.ReadNextAsync()

                                let organizationId =
                                    currentResultSet
                                        .FirstOrDefault({ organizationId = String.Empty })
                                        .organizationId

                                if String.IsNullOrEmpty(organizationId) then
                                    // We didn't find the OrganizationId, so add this OrganizationName to the MemoryCache and indicate that we have already checked.
                                    use newCacheEntry =
                                        memoryCache.CreateEntry(
                                            $"OrN:{organizationName}",
                                            Value = Constants.MemoryCacheValueGuid,
                                            AbsoluteExpirationRelativeToNow = DefaultExpirationTime
                                        )

                                    return None
                                else
                                    // Add this OrganizationName and OrganizationId to the MemoryCache.
                                    organizationGuid <- Guid.Parse(organizationId)

                                    use newCacheEntry1 =
                                        memoryCache.CreateEntry(
                                            $"OrN:{organizationName}",
                                            Value = organizationGuid,
                                            AbsoluteExpirationRelativeToNow = DefaultExpirationTime
                                        )

                                    use newCacheEntry2 =
                                        memoryCache.CreateEntry(organizationGuid, Value = MemoryCacheExistsValue, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)

                                    do! organizationNameActorProxy.SetOrganizationId organizationId correlationId
                                    return Some organizationId
                            else
                                return None
                        | MongoDB -> return None
        }

    /// Gets the RepositoryId by returning RepositoryId if provided, or searching by RepositoryName within the provided owner and organization.
    let resolveRepositoryId
        (ownerId: string)
        (ownerName: string)
        (organizationId: string)
        (organizationName: string)
        (repositoryId: string)
        (repositoryName: string)
        (correlationId: CorrelationId)
        =
        task {
            let mutable repositoryGuid = Guid.Empty

            if not <| String.IsNullOrEmpty(repositoryId)
                && Guid.TryParse(repositoryId, &repositoryGuid)
            then
                let exists = memoryCache.Get<string>(repositoryGuid)
                //logToConsole $"In resolveRepositoryId: correlationId: {correlationId}; repositoryId: {repositoryGuid}; exists: {exists}."

                match exists with
                | MemoryCacheExistsValue -> return Some repositoryId
                | MemoryCacheDoesNotExistValue -> return None
                | _ ->
                    // Call the Repository actor to check if the repository exists.
                    let actorProxy = actorProxyFactory.CreateActorProxy<IRepositoryActor>(ActorId(repositoryId), ActorName.Repository)

                    let! exists = actorProxy.Exists correlationId

                    if exists then
                        // Add this RepositoryId to the MemoryCache.
                        //logToConsole $"In resolveRepositoryId: creating cache entry; correlationId: {correlationId}; repositoryId: {repositoryGuid}; exists: {MemoryCacheExistsValue}."
                        use newCacheEntry = memoryCache.CreateEntry(repositoryGuid, Value = MemoryCacheExistsValue, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)
                        return Some repositoryId
                    else
                        return None
            elif String.IsNullOrEmpty(repositoryName) then
                // We don't have a RepositoryId or RepositoryName, so we can't resolve the RepositoryId.
                return None
            else
                let cached = memoryCache.TryGetValue($"ReN:{repositoryName}", &repositoryGuid)

                if repositoryGuid.Equals(Constants.MemoryCacheValueGuid) then
                    // We have already checked and the repository does not exist.
                    return None
                elif cached then
                    // We have already checked and the repository exists.
                    return Some(repositoryGuid.ToString())
                else
                    // Check if we have an active RepositoryName actor with a cached result.
                    let repositoryNameActorProxy =
                        actorProxyFactory.CreateActorProxy<IRepositoryNameActor>(
                            (getRepositoryNameActorId ownerId organizationId repositoryName),
                            ActorName.RepositoryName
                        )

                    match! repositoryNameActorProxy.GetRepositoryId correlationId with
                    | Some repositoryId -> return Some repositoryId
                    | None ->
                        // We have to call into Actor storage to get the RepositoryId.
                        match actorStateStorageProvider with
                        | Unknown -> return None
                        | AzureCosmosDb ->
                            let queryDefinition =
                                QueryDefinition(
                                    """SELECT c["value"].RepositoryId FROM c WHERE STRINGEQUALS(c["value"].RepositoryName, @repositoryName) AND c["value"].OwnerId = @ownerId AND c["value"].OrganizationId = @organizationId AND c["value"].Class = @class"""
                                )
                                    .WithParameter("@repositoryName", repositoryName)
                                    .WithParameter("@organizationId", organizationId)
                                    .WithParameter("@ownerId", ownerId)
                                    .WithParameter("@class", nameof (RepositoryDto))

                            let iterator = cosmosContainer.GetItemQueryIterator<RepositoryIdRecord>(queryDefinition)

                            if iterator.HasMoreResults then
                                let! currentResultSet = iterator.ReadNextAsync()

                                let repositoryId = currentResultSet.FirstOrDefault({ repositoryId = String.Empty }).repositoryId

                                if String.IsNullOrEmpty(repositoryId) then
                                    // We didn't find the RepositoryId, so add this RepositoryName to the MemoryCache and indicate that we have already checked.
                                    use newCacheEntry =
                                        memoryCache.CreateEntry(
                                            $"ReN:{repositoryName}",
                                            Value = Constants.MemoryCacheValueGuid,
                                            AbsoluteExpirationRelativeToNow = DefaultExpirationTime
                                        )

                                    return None
                                else
                                    // Add this RepositoryName and RepositoryId to the MemoryCache.
                                    repositoryGuid <- Guid.Parse(repositoryId)

                                    use newCacheEntry1 =
                                        memoryCache.CreateEntry(
                                            $"ReN:{repositoryName}",
                                            Value = repositoryGuid,
                                            AbsoluteExpirationRelativeToNow = DefaultExpirationTime
                                        )

                                    use newCacheEntry2 =
                                        memoryCache.CreateEntry(repositoryGuid, Value = MemoryCacheExistsValue, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)
                                    // Set the RepositoryId in the RepositoryName actor.
                                    do! repositoryNameActorProxy.SetRepositoryId repositoryId correlationId
                                    return Some repositoryId
                            else
                                return None
                        | MongoDB -> return None
        }

    /// Gets the BranchId by returning BranchId if provided, or searching by BranchName within the provided repository.
    let resolveBranchId repositoryId branchId branchName (correlationId: CorrelationId) =
        task {
            let mutable branchGuid = Guid.Empty

            if not <| String.IsNullOrEmpty(branchId) && Guid.TryParse(branchId, &branchGuid) then
                let exists = memoryCache.Get<string>(branchGuid)

                match exists with
                | MemoryCacheExistsValue -> return Some branchId
                | MemoryCacheDoesNotExistValue -> return None
                | _ ->
                    // Call the Branch actor to check if the branch exists.
                    let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(ActorId(branchId), ActorName.Branch)
                    let! exists = branchActorProxy.Exists correlationId

                    if exists then
                        // Add this BranchId to the MemoryCache.
                        use newCacheEntry = memoryCache.CreateEntry(branchGuid, Value = MemoryCacheExistsValue, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)
                        return Some branchId
                    else
                        return None
            elif String.IsNullOrEmpty(branchName) then
                // We don't have a BranchId or BranchName, so we can't resolve the BranchId.
                return None
            else
                // Check if we have an active BranchName actor with a cached result.
                let cached = memoryCache.TryGetValue($"BrN:{branchName}", &branchGuid)

                if branchGuid.Equals(Constants.MemoryCacheValueGuid) then
                    // We have already checked and the branch does not exist.
                    logToConsole $"We have already checked and the branch does not exist. BranchName: {branchName}; BranchId: none."
                    return None
                elif cached then
                    // We have already checked and the branch exists.
                    logToConsole $"We have already checked and the branch exists. BranchId: {branchGuid}."
                    return Some $"{branchGuid}"
                else
                    // Check if we have an active BranchName actor with a cached result.
                    let branchNameActorProxy =
                        actorProxyFactory.CreateActorProxy<IBranchNameActor>(getBranchNameActorId repositoryId branchName, ActorName.BranchName)

                    match! branchNameActorProxy.GetBranchId correlationId with
                    | Some branchId ->
                        // Add this BranchName to the MemoryCache.
                        use newCacheEntry =
                            memoryCache.CreateEntry($"BrN:{branchName}", Value = branchId, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)

                        logToConsole $"BranchName actor was already active. BranchId: {branchId}."
                        return Some $"{branchId}"
                    | None ->
                        // We have to call into Actor storage to get the BranchId.
                        match actorStateStorageProvider with
                        | Unknown -> return None
                        | AzureCosmosDb ->
                            let queryDefinition =
                                QueryDefinition(
                                    """SELECT events.Event.created.branchId
                            	       FROM c
                            	       JOIN events IN c["value"]
                            	       WHERE ENDSWITH(c.id, @stateName, true)
                                           AND STRINGEQUALS(events.Event.created.branchName, @branchName, true)
                                           AND STRINGEQUALS(events.Event.created.repositoryId, @repositoryId, true)
                                    """
                                )
                                    .WithParameter("@repositoryId", repositoryId)
                                    .WithParameter("@branchName", branchName)
                                    .WithParameter("@stateName", StateName.Branch)

                            let iterator = DefaultRetryPolicy.Execute(fun () -> cosmosContainer.GetItemQueryIterator<BranchIdRecord>(queryDefinition))

                            if iterator.HasMoreResults then
                                let! currentResultSet = iterator.ReadNextAsync()
                                let branchId = currentResultSet.FirstOrDefault({ branchId = String.Empty }).branchId

                                if String.IsNullOrEmpty(branchId) then
                                    // We didn't find the BranchId.
                                    logToConsole $"We didn't find the BranchId. BranchName: {branchName}; BranchId: none."
                                    return None
                                else
                                    // Add this BranchName and BranchId to the MemoryCache.
                                    branchGuid <- Guid.Parse(branchId)

                                    use newCacheEntry1 =
                                        memoryCache.CreateEntry(
                                            $"BrN:{branchName}",
                                            Value = branchGuid,
                                            AbsoluteExpirationRelativeToNow = DefaultExpirationTime
                                        )

                                    use newCacheEntry2 =
                                        memoryCache.CreateEntry(branchGuid, Value = MemoryCacheExistsValue, AbsoluteExpirationRelativeToNow = DefaultExpirationTime)

                                    // Set the BranchId in the BranchName actor.
                                    do! branchNameActorProxy.SetBranchId branchGuid correlationId
                                    logToConsole $"BranchName actor was not active. BranchName: {branchName}; BranchId: {branchGuid}."
                                    return Some branchId
                            else
                                return None
                        | MongoDB -> return None
        }

    type OrganizationDtoValue() =
        member val public value = OrganizationDto.Default with get, set

    type RepositoryDtoValue() =
        member val public value = RepositoryDto.Default with get, set

    type BranchDtoValue() =
        member val public value = BranchDto.Default with get, set

    type BranchIdValue() =
        member val public branchId = BranchId.Empty with get, set

    type BranchEventValue() =
        member val public event: Branch.BranchEvent =
            { Event = Branch.BranchEventType.PhysicalDeleted; Metadata = (EventMetadata.New String.Empty String.Empty) } with get, set

    type ReferenceEventValue() =
        member val public event: Reference.ReferenceEvent =
            { Event = Reference.ReferenceEventType.PhysicalDeleted; Metadata = (EventMetadata.New String.Empty String.Empty) } with get, set

    type DirectoryVersionEventValue() =
        member val public event: DirectoryVersion.DirectoryVersionEvent =
            { Event = DirectoryVersion.DirectoryVersionEventType.PhysicalDeleted; Metadata = (EventMetadata.New String.Empty String.Empty) } with get, set

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

                    let includeDeletedClause =
                        if includeDeleted then
                            String.Empty
                        else
                            """ AND IS_NULL(c["value"].DeletedAt)"""

                    let queryDefinition =
                        QueryDefinition(
                            $"""SELECT TOP @maxCount c["value"] FROM c WHERE c["value"].OwnerId = @ownerId AND c["value"].Class = @class {includeDeletedClause} ORDER BY c["value"].CreatedAt DESC"""
                        )
                            .WithParameter("@ownerId", ownerId)
                            .WithParameter("@maxCount", maxCount)
                            .WithParameter("@class", nameof (OrganizationDto))

                    let iterator = cosmosContainer.GetItemQueryIterator<OrganizationDtoValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                        repositories.AddRange(results.Resource.Select(fun v -> v.value))

                    Activity.Current
                        .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                        .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                    |> ignore
                with ex ->
                    logToConsole $"Got an exception."
                    logToConsole $"{createExceptionResponse ex}"
            | MongoDB -> ()

            return repositories
        }

    /// Checks if the specified organization name is unique for the specified owner.
    let organizationNameIsUnique<'T> (ownerId: string) (organizationName: string) (correlationId: CorrelationId) =
        task {
            match actorStateStorageProvider with
            | Unknown -> return Ok false
            | AzureCosmosDb ->
                try
                    let queryDefinition =
                        QueryDefinition(
                            """SELECT c["value"].OrganizationId FROM c WHERE c["value"].OwnerId = @ownerId AND c["value"].OrganizationName = @organizationName AND c["value"].Class = @class"""
                        )
                            .WithParameter("@ownerId", ownerId)
                            .WithParameter("@organizationName", organizationName)
                            .WithParameter("@class", nameof (OrganizationDto))

                    //logToConsole (
                    //    queryDefinition.QueryText
                    //        .Replace("@ownerId", $"\"{ownerId}\"")
                    //        .Replace("@organizationName", $"\"{organizationName}\"")
                    //        .Replace("@class", "\"OrganizationDto\"")
                    //)

                    let iterator = cosmosContainer.GetItemQueryIterator<OrganizationIdRecord>(queryDefinition, requestOptions = queryRequestOptions)

                    if iterator.HasMoreResults then
                        let! currentResultSet = iterator.ReadNextAsync()
                        // If a row is returned, and organizationId gets a value, then the organization name is not unique.
                        let organizationId =
                            currentResultSet
                                .FirstOrDefault({ organizationId = String.Empty })
                                .organizationId

                        if String.IsNullOrEmpty(organizationId) then
                            // The organization name is unique.
                            return Ok true
                        else
                            // The organization name is not unique.
                            return Ok false
                    else
                        return Ok true // This else should never be hit.
                with ex ->
                    return Error $"{createExceptionResponse ex}"
            | MongoDB -> return Ok false
        }

    /// Checks if the specified repository name is unique for the specified organization.
    let repositoryNameIsUnique<'T>
        (ownerId: string)
        (ownerName: string)
        (organizationId: string)
        (organizationName: string)
        (repositoryName: string)
        (correlationId: CorrelationId)
        =
        task {
            match actorStateStorageProvider with
            | Unknown -> return Ok false
            | AzureCosmosDb ->
                try
                    match! resolveOrganizationId ownerId ownerName organizationId organizationName correlationId with
                    | Some organizationId ->
                        let queryDefinition =
                            QueryDefinition(
                                """SELECT c["value"].RepositoryId FROM c WHERE c["value"].OrganizationId = @organizationId AND c["value"].RepositoryName = @repositoryName AND c["value"].Class = @class"""
                            )
                                .WithParameter("@organizationId", organizationId)
                                .WithParameter("@repositoryName", repositoryName)
                                .WithParameter("@class", nameof (RepositoryDto))
                        //logToConsole (queryDefinition.QueryText.Replace("@organizationId", $"\"{organizationId}\"").Replace("@repositoryName", $"\"{repositoryName}\""))
                        let iterator = cosmosContainer.GetItemQueryIterator<RepositoryIdRecord>(queryDefinition, requestOptions = queryRequestOptions)

                        if iterator.HasMoreResults then
                            let! currentResultSet = iterator.ReadNextAsync()
                            // If a row is returned, and repositoryId gets a value, then the repository name is not unique.
                            let repositoryId = currentResultSet.FirstOrDefault({ repositoryId = String.Empty }).repositoryId

                            if String.IsNullOrEmpty(repositoryId) then
                                // The repository name is unique.
                                return Ok true
                            else
                                // The repository name is not unique.
                                return Ok false
                        else
                            return Ok true // This else should never be hit.
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

                    let includeDeletedClause =
                        if includeDeleted then
                            String.Empty
                        else
                            """ AND IS_NULL(c["value"].DeletedAt)"""

                    let queryDefinition =
                        QueryDefinition(
                            $"""SELECT TOP @maxCount c["value"] FROM c WHERE c["value"].OrganizationId = @organizationId AND c["value"].Class = @class {includeDeletedClause} ORDER BY c["value"].CreatedAt DESC"""
                        )
                            .WithParameter("@organizationId", organizationId)
                            .WithParameter("@maxCount", maxCount)
                            .WithParameter("@class", nameof (RepositoryDto))

                    let iterator = cosmosContainer.GetItemQueryIterator<RepositoryDtoValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                        repositories.AddRange(results.Resource.Select(fun v -> v.value))

                    Activity.Current
                        .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                        .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                    |> ignore
                with ex ->
                    logToConsole $"Got an exception."
                    logToConsole $"{createExceptionResponse ex}"
            | MongoDB -> ()

            return repositories
        }

    /// Gets a list of branches for a given repository.
    let getBranches (repositoryId: RepositoryId) (maxCount: int) includeDeleted correlationId =
        task {
            let branches = ConcurrentBag<BranchDto>()
            let branchIds = List<BranchId>()

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                try
                    let indexMetrics = StringBuilder()
                    let requestCharge = StringBuilder()

                    let includeDeletedClause =
                        if includeDeleted then
                            String.Empty
                        else
                            """ AND IS_NULL(c["value"].DeletedAt)"""

                    let queryDefinition =
                        QueryDefinition(
                            """SELECT TOP @maxCount event.Event.created.branchId
                                FROM c JOIN event IN c["value"] 
                                WHERE event.Event.created.repositoryId = @repositoryId
                                    AND LENGTH(event.Event.created.branchName) > 0
                                ORDER BY c["value"].CreatedAt DESC"""
                        )
                            .WithParameter("@maxCount", maxCount)
                            .WithParameter("@repositoryId", $"{repositoryId}")

                    let iterator = cosmosContainer.GetItemQueryIterator<BranchIdValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                        results.Resource |> Seq.iter (fun r -> branchIds.Add(r.branchId))

                    Activity.Current
                        .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                        .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                    |> ignore

                    // Now we have the branchIds, let's get the BranchDto's. Right now the best way is just to get them as individual actors.
                    do!
                        Parallel.ForEachAsync(
                            branchIds,
                            Constants.ParallelOptions,
                            (fun branchId ct ->
                                ValueTask(
                                    task {
                                        let branchActorProxy = actorProxyFactory.CreateActorProxy<IBranchActor>(ActorId($"{branchId}"), ActorName.Branch)
                                        let! branchDto = branchActorProxy.Get correlationId
                                        branches.Add(branchDto)
                                    }
                                ))
                        )
                with ex ->
                    logToConsole $"Got an exception."
                    logToConsole $"{createExceptionResponse ex}"
            | MongoDB -> ()

            return branches.ToArray()
        }

    /// Gets a list of references that match a provided SHA-256 hash.
    let getReferencesBySha256Hash (branchId: BranchId) (sha256Hash: Sha256Hash) (maxCount: int) =
        task {
            let references = List<ReferenceDto>()

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()

                let queryDefinition =
                    QueryDefinition(
                        """SELECT TOP @maxCount event FROM c JOIN event IN c["value"] 
                            WHERE event.Event.created.BranchId = @branchId
                                AND STARTSWITH(event.Event.created.Sha256Hash, @sha256Hash, true)
                                AND event.Event.created.Class = @class
                            ORDER BY c["value"].CreatedAt DESC"""
                    )
                        .WithParameter("@maxCount", maxCount)
                        .WithParameter("@sha256Hash", sha256Hash)
                        .WithParameter("@branchId", branchId)
                        .WithParameter("@class", nameof (ReferenceDto))

                let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore

                    results.Resource
                    |> Seq.iter (fun r ->
                        match r.event.Event with
                        | Reference.ReferenceEventType.Created refDto -> references.Add(refDto)
                        | _ -> ())

                Activity.Current
                    .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                    .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                |> ignore
            | MongoDB -> ()

            return references :> IReadOnlyList<ReferenceDto>
        }

    /// Gets a reference by its SHA-256 hash.
    let getReferenceBySha256Hash (branchId: BranchId) (sha256Hash: Sha256Hash) =
        task {
            let! references = getReferencesBySha256Hash branchId sha256Hash 1
            if references.Count > 0 then return Some references[0] else return None
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

                let queryDefinition =
                    QueryDefinition(
                        """SELECT TOP @maxCount event FROM c JOIN event IN c["value"] 
                            WHERE event.Event.created.BranchId = @branchId
                                AND event.Event.created.Class = @class
                            ORDER BY c["value"].CreatedAt DESC"""
                    )
                        .WithParameter("@maxCount", maxCount)
                        .WithParameter("@branchId", branchId)
                        .WithParameter("@class", nameof (ReferenceDto))

                let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    //let! results = iterator.ReadNextAsync()
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore

                    results.Resource
                    |> Seq.iter (fun r ->
                        match r.event.Event with
                        | Reference.ReferenceEventType.Created refDto -> references.Add(refDto)
                        | _ -> ())

                Activity.Current
                    .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                    .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                |> ignore
            | MongoDB -> ()

            return references :> IReadOnlyList<ReferenceDto>
        }

    type DocumentIdentifier() =
        member val id = String.Empty with get, set
        member val partitionKey = String.Empty with get, set

    /// Deletes all documents from CosmosDb.
    ///
    /// **** This method is implemented only in Debug configuration. It is a no-op in Release configuration. ****
    let deleteAllFromCosmosDB () =
        task {
#if DEBUG
            let failed = List<string>()

            try
                // (MaxDegreeOfParallelism = 3) runs at 700 RU's, so it fits under the free 1,000 RU limit for CosmosDB, without getting throttled.
                let parallelOptions = ParallelOptions(MaxDegreeOfParallelism = 3)

                let itemRequestOptions = ItemRequestOptions()

                itemRequestOptions.AddRequestHeaders <- fun headers -> headers.Add(Constants.CorrelationIdHeaderKey, "Deleting all records from CosmosDB")

                let queryDefinition = QueryDefinition("SELECT c.id, c.partitionKey FROM c ORDER BY c.partitionKey")

                let mutable totalRecordsDeleted = 0
                let overallStartTime = getCurrentInstant ()

                let iterator = cosmosContainer.GetItemQueryIterator<DocumentIdentifier>(queryDefinition, requestOptions = queryRequestOptions)

                while iterator.HasMoreResults do
                    let batchStartTime = getCurrentInstant ()
                    let! batchResults = iterator.ReadNextAsync()
                    let mutable totalRequestCharge = 0L

                    logToConsole $"In Services.deleteAllFromCosmosDB(): Current batch size: {batchResults.Resource.Count()}."

                    do!
                        Parallel.ForEachAsync(
                            batchResults.Resource,
                            parallelOptions,
                            (fun document ct ->
                                ValueTask(
                                    task {
                                        let! deleteResponse =
                                            cosmosContainer.DeleteItemAsync(document.id, PartitionKey(document.partitionKey), itemRequestOptions)

                                        // Multiplying by 1000 because Interlocked.Add() expects an int64; we'll divide by 1000 when logging.
                                        totalRequestCharge <- Interlocked.Add(&totalRequestCharge, int64 (deleteResponse.RequestCharge * 1000.0))

                                        if deleteResponse.StatusCode <> HttpStatusCode.NoContent then
                                            failed.Add(document.id)
                                            logToConsole $"Failed to delete id {document.id}."
                                    }
                                ))
                        )

                    let duration_s = getCurrentInstant().Minus(batchStartTime).TotalSeconds
                    let overall_duration_s = getCurrentInstant().Minus(overallStartTime).TotalSeconds
                    let rps = float (batchResults.Resource.Count()) / duration_s
                    totalRecordsDeleted <- totalRecordsDeleted + batchResults.Resource.Count()
                    let overallRps = float totalRecordsDeleted / overall_duration_s

                    logToConsole
                        $"In Services.deleteAllFromCosmosDB(): batch duration (s): {duration_s:F3}; batch requests/second: {rps:F3}; failed.Count: {failed.Count}; totalRequestCharge: {float totalRequestCharge / 1000.0:F2}; totalRecordsDeleted: {totalRecordsDeleted}; overall duration (m): {overall_duration_s / 60.0:F3}; overall requests/second: {overallRps:F3}."

                return failed
            with ex ->
                failed.Add((createExceptionResponse ex).``exception``)
                return failed
#else
            return List<string>([ "Not implemented" ])
#endif
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

                let queryDefinition =
                    QueryDefinition(
                        """SELECT TOP @maxCount event
                            FROM c JOIN event IN c["value"] 
                            WHERE event.Event.created.BranchId = @branchId
                                AND event.Event.created.Class = @class
                                AND STRINGEQUALS(event.Event.created.ReferenceType, @referenceType, true)
                            ORDER BY c["value"].CreatedAt DESC"""
                    )
                        .WithParameter("@maxCount", maxCount)
                        .WithParameter("@branchId", branchId)
                        .WithParameter("@referenceType", getDiscriminatedUnionCaseName referenceType)
                        .WithParameter("@class", nameof (ReferenceDto))

                let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore

                    results.Resource
                    |> Seq.iter (fun r ->
                        match r.event.Event with
                        | Reference.ReferenceEventType.Created refDto -> references.Add(refDto)
                        | _ -> ())

                Activity.Current
                    .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                    .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                |> ignore
            | MongoDB -> ()

            return references :> IReadOnlyList<ReferenceDto>
        }

    let getPromotions = getReferencesByType ReferenceType.Promotion
    let getCommits = getReferencesByType ReferenceType.Commit
    let getCheckpoints = getReferencesByType ReferenceType.Checkpoint
    let getSaves = getReferencesByType ReferenceType.Save
    let getTags = getReferencesByType ReferenceType.Tag
    let getExternals = getReferencesByType ReferenceType.External
    let getRebases = getReferencesByType ReferenceType.Rebase

    let getLatestReference branchId =
        task {
            match actorStateStorageProvider with
            | Unknown -> return None
            | AzureCosmosDb ->
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()

                let queryDefinition =
                    QueryDefinition(
                        //"""SELECT TOP 1 c["value"] FROM c WHERE c["value"].BranchId = @branchId AND c["value"].Class = @class AND STRINGEQUALS(c["value"].ReferenceType, @referenceType, true) ORDER BY c["value"].CreatedAt DESC"""
                        """SELECT TOP 1 event FROM c JOIN event IN c["value"] 
                                    WHERE event.Event.created.BranchId = @branchId
                                        AND event.Event.created.Class = @class
                                        ORDER BY c["value"].CreatedAt DESC"""
                    )
                        .WithParameter("@branchId", $"{branchId}")
                        .WithParameter("@class", nameof (ReferenceDto))

                let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                let mutable referenceDto = ReferenceDto.Default

                while iterator.HasMoreResults do
                    let! results = iterator.ReadNextAsync()
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore

                    if results.Count > 0 then
                        referenceDto <-
                            match results.Resource.FirstOrDefault().event.Event with
                            | Reference.ReferenceEventType.Created refDto -> refDto
                            | _ -> ReferenceDto.Default

                Activity.Current
                    .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                    .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                |> ignore

                if referenceDto.ReferenceId <> ReferenceDto.Default.ReferenceId then
                    return Some referenceDto
                else
                    return None
            | MongoDB -> return None
        }

    /// Gets the latest reference for a given ReferenceType in a branch.
    let getLatestReferenceByReferenceTypes (referenceTypes: ReferenceType array) (branchId: BranchId) =
        task {
            let referenceDtos = ConcurrentDictionary<ReferenceType, ReferenceDto>(referenceTypes.Length, referenceTypes.Length)

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()

                // CosmosDb SQL doesn't have a UNION clause. That means that the only way to get the latest reference for each ReferenceType is to do separate queries for each ReferenceType that gets passed in.
                // It's annoying, but at least let's do it in parallel.

                do! Parallel.ForEachAsync(referenceTypes, Constants.ParallelOptions,
                    (fun referenceType ct ->
                        ValueTask(
                            task {
                                let queryDefinition =
                                    QueryDefinition(
                                        //"""SELECT TOP 1 c["value"] FROM c WHERE c["value"].BranchId = @branchId AND c["value"].Class = @class AND STRINGEQUALS(c["value"].ReferenceType, @referenceType, true) ORDER BY c["value"].CreatedAt DESC"""
                                        """SELECT TOP 1 event FROM c JOIN event IN c["value"] 
                                                    WHERE event.Event.created.BranchId = @branchId
                                                        AND event.Event.created.Class = @class
                                                        AND STRINGEQUALS(event.Event.created.ReferenceType, @referenceType, true)
                                                        ORDER BY c["value"].CreatedAt DESC"""
                                    )
                                        .WithParameter("@branchId", branchId)
                                        .WithParameter("@referenceType", getDiscriminatedUnionCaseName referenceType)
                                        .WithParameter("@class", nameof (ReferenceDto))

                                let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                                let mutable referenceDto = ReferenceDto.Default

                                while iterator.HasMoreResults do
                                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore

                                    if results.Count > 0 then
                                        referenceDto <-
                                            match results.Resource.FirstOrDefault().event.Event with
                                            | Reference.ReferenceEventType.Created refDto -> refDto
                                            | _ -> ReferenceDto.Default

                                if referenceDto.ReferenceId <> ReferenceDto.Default.ReferenceId then
                                    referenceDtos.TryAdd(referenceType, referenceDto) |> ignore

                                //logToConsole $"In getLatestReferenceByReferenceTypes:{Environment.NewLine}{referenceDto}."
                            }
                        ))
                )

                Activity.Current
                    .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                    .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                |> ignore
            | MongoDB -> ()

            return referenceDtos :> IReadOnlyDictionary<ReferenceType, ReferenceDto>
        }

    /// Gets the latest reference for a given ReferenceType in a branch.
    let getLatestReferenceByType referenceType (branchId: BranchId) =
        task {
            match actorStateStorageProvider with
            | Unknown -> return None
            | AzureCosmosDb ->
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()

                let queryDefinition =
                    QueryDefinition(
                        //"""SELECT TOP 1 c["value"] FROM c WHERE c["value"].BranchId = @branchId AND c["value"].Class = @class AND STRINGEQUALS(c["value"].ReferenceType, @referenceType, true) ORDER BY c["value"].CreatedAt DESC"""
                        """SELECT TOP 1 event FROM c JOIN event IN c["value"] 
                                    WHERE event.Event.created.BranchId = @branchId
                                        AND event.Event.created.Class = @class
                                        AND STRINGEQUALS(event.Event.created.ReferenceType, @referenceType, true)
                                        ORDER BY c["value"].CreatedAt DESC"""
                    )
                        .WithParameter("@branchId", branchId)
                        .WithParameter("@referenceType", getDiscriminatedUnionCaseName referenceType)
                        .WithParameter("@class", nameof (ReferenceDto))

                let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                let mutable referenceDto = ReferenceDto.Default

                while iterator.HasMoreResults do
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                    requestCharge.Append($"{results.RequestCharge}, ") |> ignore

                    if results.Count > 0 then
                        referenceDto <-
                            match results.Resource.FirstOrDefault().event.Event with
                            | Reference.ReferenceEventType.Created refDto -> refDto
                            | _ -> ReferenceDto.Default

                Activity.Current
                    .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                    .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                |> ignore

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

    /// Gets the latest external from a branch.
    let getLatestExternal = getLatestReferenceByType ReferenceType.External

    /// Gets the latest rebase from a branch.
    let getLatestRebase = getLatestReferenceByType ReferenceType.Rebase

    /// Gets a DirectoryVersion by searching using a Sha256Hash value.
    let getDirectoryBySha256Hash (repositoryId: RepositoryId) (sha256Hash: Sha256Hash) correlationId =
        task {
            let mutable directoryVersion = DirectoryVersion.Default

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()

                let queryDefinition =
                    QueryDefinition(
                        """SELECT TOP 1 event FROM c JOIN event IN c["value"] 
                            WHERE STARTSWITH(event.Event.created.Sha256Hash, @sha256Hash, true)
                                AND event.Event.created.RepositoryId = @repositoryId
                                AND event.Event.created.Class = @class"""
                    )
                        //let queryDefinition = QueryDefinition("""SELECT TOP 1 c["value"] FROM c WHERE c["value"].RepositoryId = @repositoryId AND STARTSWITH(c["value"].Sha256Hash, @sha256Hash, true) AND c["value"].Class = @class""")
                        .WithParameter("@sha256Hash", sha256Hash)
                        .WithParameter("@repositoryId", repositoryId)
                        .WithParameter("@class", nameof (DirectoryVersion))

                try
                    let iterator = cosmosContainer.GetItemQueryIterator<DirectoryVersionEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge}, ") |> ignore

                        if results.Resource.Count() > 0 then
                            directoryVersion <-
                                match results.Resource.FirstOrDefault().event.Event with
                                | DirectoryVersion.DirectoryVersionEventType.Created directoryVersion -> directoryVersion
                                | _ -> DirectoryVersion.Default

                    Activity.Current
                        .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                        .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                    |> ignore
                with ex ->
                    log.Value.LogError(
                        ex,
                        "{currentInstant}: Exception in Services.getDirectoryBySha256Hash(). QueryDefinition: {queryDefinition}",
                        getCurrentInstantExtended (),
                        (serialize queryDefinition)
                    )
            | MongoDB -> ()

            if
                directoryVersion.DirectoryVersionId
                <> DirectoryVersion.Default.DirectoryVersionId
            then
                return Some directoryVersion
            else
                return None
        }

    /// Gets a Root DirectoryVersion by searching using a Sha256Hash value.
    let getRootDirectoryBySha256Hash (repositoryId: RepositoryId) (sha256Hash: Sha256Hash) correlationId =
        task {
            let mutable directoryVersion = DirectoryVersion.Default

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let indexMetrics = StringBuilder()
                let requestCharge = StringBuilder()

                let queryDefinition =
                    QueryDefinition(
                        $"""SELECT TOP 1 event FROM c JOIN event in c["value"]
                                                            WHERE STARTSWITH(event.Event.created.Sha256Hash, @sha256Hash, true)
                                                                AND event.Event.created.RepositoryId = @repositoryId
                                                                AND event.Event.created.RelativePath = @relativePath
                                                                AND event.Event.created.Class = @class"""
                    )
                        .WithParameter("@sha256Hash", sha256Hash)
                        .WithParameter("@repositoryId", repositoryId)
                        .WithParameter("@relativePath", Constants.RootDirectoryPath)
                        .WithParameter("@class", nameof (DirectoryVersion))

                try
                    let iterator = cosmosContainer.GetItemQueryIterator<DirectoryVersionEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge}, ") |> ignore

                        if results.Resource.Count() > 0 then
                            directoryVersion <-
                                match results.Resource.FirstOrDefault().event.Event with
                                | DirectoryVersion.DirectoryVersionEventType.Created directoryVersion -> directoryVersion
                                | _ -> DirectoryVersion.Default

                    Activity.Current
                        .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                        .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                    |> ignore
                with ex ->
                    let parameters =
                        queryDefinition.GetQueryParameters()
                        |> Seq.fold (fun (state: StringBuilder) (struct (k, v)) -> state.Append($"{k} = {v}; ")) (StringBuilder())

                    log.Value.LogError(
                        ex,
                        "{currentInstant}: Exception in Services.getRootDirectoryBySha256Hash(). QueryText: {queryText}. Parameters: {parameters}",
                        getCurrentInstantExtended (),
                        (queryDefinition.QueryText),
                        parameters.ToString()
                    )
            | MongoDB -> ()

            if
                directoryVersion.DirectoryVersionId
                <> DirectoryVersion.Default.DirectoryVersionId
            then
                return Some directoryVersion
            else
                return None
        }

    /// Gets a Root DirectoryVersion by searching using a Sha256Hash value.
    let getRootDirectoryByReferenceId (repositoryId: RepositoryId) (referenceId: ReferenceId) correlationId =
        task {
            let referenceActorId = ActorId($"{referenceId}")

            let referenceActorProxy = actorProxyFactory.CreateActorProxy<IReferenceActor>(referenceActorId, ActorName.Reference)

            let! referenceDto = referenceActorProxy.Get correlationId

            return! getRootDirectoryBySha256Hash repositoryId referenceDto.Sha256Hash correlationId
        }

    /// Checks if all of the supplied DirectoryIds exist.
    let directoryIdsExist (repositoryId: RepositoryId) (directoryIds: IEnumerable<DirectoryVersionId>) correlationId =
        task {
            match actorStateStorageProvider with
            | Unknown -> return false
            | AzureCosmosDb ->
                let mutable requestCharge = 0.0
                let mutable allExist = true
                let directoryIdQueue = Queue<DirectoryVersionId>(directoryIds)

                while directoryIdQueue.Count > 0 && allExist do
                    let directoryId = directoryIdQueue.Dequeue()

                    let queryDefinition =
                        QueryDefinition(
                            """SELECT c FROM c 
                                            WHERE c["value"].RepositoryId = @repositoryId 
                                                AND c["value"].DirectoryId = @directoryId
                                                AND c["value"].Class = @class"""
                        )
                            .WithParameter("@repositoryId", $"{repositoryId}")
                            .WithParameter("@directoryId", $"{directoryId}")
                            .WithParameter("@class", nameof (DirectoryVersion))

                    let iterator = cosmosContainer.GetItemQueryIterator<DirectoryVersion>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        requestCharge <- requestCharge + results.RequestCharge

                        if not <| results.Resource.Any() then allExist <- false

                Activity.Current
                    .SetTag("allExist", $"{allExist}")
                    .SetTag("totalRequestCharge", $"{requestCharge}")
                |> ignore

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

                (*("""SELECT TOP 1 event FROM c JOIN event IN c["value"] 
                    WHERE event.Event.created.BranchId = @branchId
                        AND event.Event.created.Class = @class
                        AND STRINGEQUALS(event.Event.created.ReferenceType, @referenceType, true)
                        ORDER BY c["value"].CreatedAt DESC"""
            )
        .WithParameter("@branchId", $"{branchId}")
        .WithParameter("@referenceType", $"{referenceType}")
        .WithParameter("@class", "Reference")
        *)

                // In order to build the IN clause, we need to create a parameter for each referenceId. (I tried just using string concatenation, it didn't work for some reason. Anyway...)
                // The query starts with:
                let queryText =
                    StringBuilder(
                        @"SELECT TOP @maxCount event
                                                FROM c JOIN event IN c[""value""]
                                                WHERE event.Event.created.Class = @class
                                                AND event.Event.created.ReferenceId IN ("
                    )
                // Then we add a parameter for each referenceId.
                referenceIds
                    .Where(fun referenceId -> not <| referenceId.Equals(ReferenceId.Empty))
                    .Distinct()
                |> Seq.iteri (fun i referenceId -> queryText.Append($"@referenceId{i},") |> ignore)
                // Then we remove the last comma and close the parenthesis.
                queryText.Remove(queryText.Length - 1, 1).Append(")") |> ignore

                // Create the query definition.
                let queryDefinition =
                    QueryDefinition(queryText.ToString())
                        .WithParameter("@maxCount", referenceIds.Count())
                        .WithParameter("@class", nameof (ReferenceDto))

                // Add a .WithParameter for each referenceId.
                referenceIds
                    .Where(fun referenceId -> not <| referenceId.Equals(ReferenceId.Empty))
                    .Distinct()
                |> Seq.iteri (fun i referenceId -> queryDefinition.WithParameter($"@referenceId{i}", $"{referenceId}") |> ignore)

                // Execute the query.
                let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                // The query will return fewer results than the number of referenceIds if the supplied referenceIds have duplicates.
                //   This is normal for `grace status` (BasedOn and Latest Promotion are likely to be the same, for instance).
                //   We need to gather the query results, and then iterate through the referenceId's to return the dto's in the same order.
                let queryResults = Dictionary<ReferenceId, ReferenceDto>()

                while iterator.HasMoreResults do
                    let! results = iterator.ReadNextAsync()
                    requestCharge <- requestCharge + results.RequestCharge
                    clientElapsedTime <- clientElapsedTime + results.Diagnostics.GetClientElapsedTime()

                    results.Resource
                    |> Seq.iter (fun ev ->
                        match ev.event.Event with
                        | Reference.ReferenceEventType.Created refDto -> queryResults.Add(refDto.ReferenceId, refDto)
                        | _ -> ())

                // Add the results to the list in the same order as the supplied referenceIds.
                referenceIds
                |> Seq.iter (fun referenceId ->
                    if referenceId <> ReferenceId.Empty then
                        if queryResults.ContainsKey(referenceId) then
                            referenceDtos.Add(queryResults[referenceId])
                    else
                        // In case the caller supplied an empty referenceId, add a default ReferenceDto.
                        referenceDtos.Add(ReferenceDto.Default))

                Activity.Current
                    .SetTag("referenceDtos.Count", $"{referenceDtos.Count}")
                    .SetTag("clientElapsedTime", $"{clientElapsedTime}")
                    .SetTag("totalRequestCharge", $"{requestCharge}")
                |> ignore
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

                let includeDeletedClause =
                    if includeDeleted then
                        String.Empty
                    else
                        """ AND IS_NULL(c["value"].DeletedAt)"""

                let branchIdStack = Queue<ReferenceId>(branchIds)

                while branchIdStack.Count > 0 do
                    let branchId = branchIdStack.Dequeue()

                    let queryDefinition =
                        QueryDefinition(
                            $"""SELECT TOP @maxCount c["value"] FROM c 
                                            WHERE c["value"].RepositoryId = @repositoryId 
                                                AND c["value"].BranchId = @branchId
                                                AND c["value"].Class = @class
                                                {includeDeletedClause}"""
                        )
                            .WithParameter("@maxCount", maxCount)
                            .WithParameter("@repositoryId", $"{repositoryId}")
                            .WithParameter("@branchId", $"{branchId}")
                            .WithParameter("@class", nameof (BranchDto))

                    let iterator = cosmosContainer.GetItemQueryIterator<BranchDtoValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        requestCharge <- requestCharge + results.RequestCharge

                        if results.Resource.Count() > 0 then
                            branchDtos.Add(results.Resource.First().value)

                Activity.Current
                    .SetTag("referenceDtos.Count", $"{branchDtos.Count}")
                    .SetTag("totalRequestCharge", $"{requestCharge}")
                |> ignore
            | MongoDB -> ()

            return branchDtos
        }
