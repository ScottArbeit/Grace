namespace Grace.Actors

open Azure.Storage
open Azure.Storage.Blobs
open Azure.Storage.Sas
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Timing
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.Constants
open Grace.Types.Branch
open Grace.Types.DirectoryVersion
open Grace.Types.Reference
open Grace.Types.Reminder
open Grace.Types.Repository
open Grace.Types.Organization
open Grace.Types.Owner
open Grace.Types.Types
open Grace.Shared.Utilities
open Microsoft.Azure.Cosmos
open Microsoft.Azure.Cosmos.Linq
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.Logging
open NodaTime
open Orleans.Runtime
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
open Microsoft.Extensions.DependencyInjection
open System.Runtime.Serialization
open System.Reflection
open System.Text.RegularExpressions

module Services =
    type ServerGraceIndex = Dictionary<RelativePath, DirectoryVersion>
    type OwnerIdRecord = { OwnerId: string }
    type OrganizationIdRecord = { organizationId: string }
    type RepositoryIdRecord = { repositoryId: string }
    type BranchIdRecord = { branchId: string }

    type OrganizationDtoValue() =
        member val public value = OrganizationDto.Default with get, set

    type RepositoryDtoValue() =
        member val public value = RepositoryDto.Default with get, set

    type BranchDtoValue() =
        member val public value = BranchDto.Default with get, set

    type BranchIdValue() =
        member val public branchId = BranchId.Empty with get, set

    type OwnerEventValue() =
        member val public State: OwnerEvent array = Array.Empty<OwnerEvent>() with get, set

    type OrganizationEventValue() =
        member val public State: OrganizationEvent array = Array.Empty<OrganizationEvent>() with get, set

    type RepositoryEventValue() =
        member val public State: RepositoryEvent array = Array.Empty<RepositoryEvent>() with get, set

    type BranchEventValue() =
        member val public State: BranchEvent array = Array.Empty<BranchEvent>() with get, set

    type ReferenceEventValue() =
        member val public State: ReferenceEvent array = Array.Empty<ReferenceEvent>() with get, set

    type DirectoryVersionEventValue() =
        member val public State: DirectoryVersionEvent array = Array.Empty<DirectoryVersionEvent>() with get, set

    type DirectoryVersionValue() =
        member val public value = DirectoryVersion.Default with get, set


    /// Dictionary for caching blob container clients
    let containerClients = new ConcurrentDictionary<string, BlobContainerClient>()

    /// Dapr HTTP endpoint retrieved from environment variables
    //let daprHttpEndpoint =
    //    $"{Environment.GetEnvironmentVariable(EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(EnvironmentVariables.DaprHttpPort)}"

    /// Dapr gRPC endpoint retrieved from environment variables
    //let daprGrpcEndpoint =
    //    $"{Environment.GetEnvironmentVariable(EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(EnvironmentVariables.DaprGrpcPort)}"

    /// Dapr client instance
    //let daprClient =
    //    DaprClientBuilder()
    //        .UseJsonSerializationOptions(JsonSerializerOptions)
    //        .UseHttpEndpoint(daprHttpEndpoint)
    //        .UseGrpcEndpoint(daprGrpcEndpoint)
    //        .Build()

    /// Azure Storage connection string retrieved from environment variables
    let private azureStorageConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariables.AzureStorageConnectionString)

    /// Azure Storage key retrieved from environment variables
    let private azureStorageKey = Environment.GetEnvironmentVariable(EnvironmentVariables.AzureStorageKey)

    /// Shared key credential for Azure Storage
    let private sharedKeyCredential = StorageSharedKeyCredential(DefaultObjectStorageAccount, azureStorageKey)

    /// Logger instance for the Services.Actor module.
    let log = loggerFactory.CreateLogger("Services.Actor")

    let printQueryDefinition (queryDefinition: QueryDefinition) =
        let sb = stringBuilderPool.Get()

        try
            sb.Append(queryDefinition.QueryText) |> ignore

            queryDefinition.GetQueryParameters()
            |> Seq.iter (fun struct (name, value) -> sb.Replace(name, $"\"{value}\"") |> ignore)

            let trimmedSql = Regex.Replace(sb.ToString(), @"(?m)^[ \t]+", "    ").Trim()
            trimmedSql
        finally
            stringBuilderPool.Return sb

    /// Cosmos LINQ serializer options
    let linqSerializerOptions = CosmosLinqSerializerOptions(PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase)

    /// Custom QueryRequestOptions that requests Index Metrics only in DEBUG build.
    let queryRequestOptions = QueryRequestOptions()
    //#if DEBUG
    //    queryRequestOptions.PopulateIndexMetrics <- true
    //#endif

    /// Gets an Azure Blob Storage container client for the container that holds the object files for the given repository.
    let getContainerClient (repositoryDto: RepositoryDto) correlationId =
        task {
            let containerName = $"{repositoryDto.RepositoryId}"
            let key = $"Con:{repositoryDto.StorageAccountName}-{containerName}"

            let! blobContainerClient =
                memoryCache.GetOrCreateAsync(
                    key,
                    fun cacheEntry ->
                        task {
                            let blobContainerClient = BlobContainerClient(azureStorageConnectionString, containerName)
                            let ownerActorProxy = Owner.CreateActorProxy repositoryDto.OwnerId CorrelationId.Empty
                            let! ownerDto = ownerActorProxy.Get correlationId
                            let organizationActorProxy = Organization.CreateActorProxy repositoryDto.OrganizationId CorrelationId.Empty
                            let! organizationDto = organizationActorProxy.Get correlationId
                            let metadata = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) :> IDictionary<string, string>
                            metadata[nameof OwnerId] <- $"{repositoryDto.OwnerId}"
                            metadata[nameof OwnerName] <- $"{ownerDto.OwnerName}"
                            metadata[nameof OrganizationId] <- $"{repositoryDto.OrganizationId}"
                            metadata[nameof OrganizationName] <- $"{organizationDto.OrganizationName}"
                            metadata[nameof RepositoryId] <- $"{repositoryDto.RepositoryId}"
                            metadata[nameof RepositoryName] <- $"{repositoryDto.RepositoryName}"

                            let! azureResponse =
                                blobContainerClient.CreateIfNotExistsAsync(publicAccessType = Models.PublicAccessType.None, metadata = metadata)

                            // This cacheEntry can (and should) last for longer than Grace's default expiration time.
                            // StorageAccountNames and container names are stable.
                            // However, we don't want to clog up memoryCache for too long with each client.
                            // Aiming for a good balance, so keeping it for 10 minutes seems reasonable.
                            cacheEntry.AbsoluteExpiration <- DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(10.0))

                            return blobContainerClient
                        }
                )

            return blobContainerClient
        }

    /// Gets an Azure Blob Storage client instance for the given repository and file version.
    let getAzureBlobClient (repositoryDto: RepositoryDto) (blobName: string) (correlationId: CorrelationId) =
        task {
            //logToConsole $"* In getAzureBlobClient; repositoryId: {repositoryDto.RepositoryId}; fileVersion: {fileVersion.RelativePath}."
            let! containerClient = getContainerClient repositoryDto correlationId

            return containerClient.GetBlobClient(blobName)
        }

    let getAzureBlobClientForFileVersion (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (correlationId: CorrelationId) =
        task {
            let blobName = $"{fileVersion.RelativePath}/{fileVersion.GetObjectFileName}"
            return! getAzureBlobClient repositoryDto blobName correlationId
        }

    /// Creates a full URI for a specific file version.
    let private createAzureBlobSasUri (repositoryDto: RepositoryDto) (blobName: string) (permission: BlobSasPermissions) (correlationId: CorrelationId) =
        task {
            //logToConsole $"In createAzureBlobSasUri; fileVersion.RelativePath: {fileVersion.RelativePath}."
            let! blobContainerClient = getContainerClient repositoryDto correlationId

            let blobSasBuilder =
                BlobSasBuilder(
                    permissions = permission,
                    expiresOn = DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(SharedAccessSignatureExpiration)),
                    StartsOn = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(15.0)),
                    BlobContainerName = blobContainerClient.Name,
                    BlobName = blobName,
                    CorrelationId = correlationId
                )

            let sasUriParameters = blobSasBuilder.ToSasQueryParameters(sharedKeyCredential)

            return UriWithSharedAccessSignature($"{blobContainerClient.Uri}/{blobName}?{sasUriParameters}")
        }

    /// Gets a full Uri, including shared access signature, for reading from the object storage provider.
    let getUriWithReadSharedAccessSignature (repositoryDto: RepositoryDto) (blobName: string) (correlationId: CorrelationId) =
        task {
            match repositoryDto.ObjectStorageProvider with
            | AzureBlobStorage ->
                let permissions = (BlobSasPermissions.Read ||| BlobSasPermissions.List) // These are the minimum permissions needed to read a file.
                let! sas = createAzureBlobSasUri repositoryDto blobName permissions correlationId
                return sas
            | AWSS3 -> return UriWithSharedAccessSignature(String.Empty)
            | GoogleCloudStorage -> return UriWithSharedAccessSignature(String.Empty)
            | ObjectStorageProvider.Unknown -> return UriWithSharedAccessSignature(String.Empty)
        }

    /// Gets a full Uri, including shared access signature, for reading from the object storage provider.
    let getUriWithReadSharedAccessSignatureForFileVersion (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (correlationId: CorrelationId) =
        task {
            let blobName = $"{fileVersion.RelativePath}/{fileVersion.GetObjectFileName}"
            return! getUriWithReadSharedAccessSignature repositoryDto blobName correlationId
        }

    /// Gets a full Uri, including shared access signature, for writing from the object storage provider.
    let getUriWithWriteSharedAccessSignature (repositoryDto: RepositoryDto) (blobName: string) (correlationId: CorrelationId) =
        task {
            match repositoryDto.ObjectStorageProvider with
            | AWSS3 -> return UriWithSharedAccessSignature(String.Empty)
            | AzureBlobStorage ->
                // Adding read permission to allow for calls to .ExistsAsync().
                let permissions =
                    (BlobSasPermissions.Create
                     ||| BlobSasPermissions.Write
                     ||| BlobSasPermissions.Move
                     ||| BlobSasPermissions.Tag
                     ||| BlobSasPermissions.Read)

                let! sas = createAzureBlobSasUri repositoryDto blobName permissions correlationId

                return sas
            | GoogleCloudStorage -> return UriWithSharedAccessSignature(String.Empty)
            | ObjectStorageProvider.Unknown -> return UriWithSharedAccessSignature(String.Empty)
        }

    /// Gets a full Uri, including shared access signature, for writing from the object storage provider.
    let getUriWithWriteSharedAccessSignatureForFileVersion (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (correlationId: CorrelationId) =
        task {
            let blobName = $"{fileVersion.RelativePath}/{fileVersion.GetObjectFileName}"
            return! getUriWithWriteSharedAccessSignature repositoryDto blobName correlationId
        }

    /// Checks whether an owner name exists in the system.
    let ownerNameExists (ownerName: string) cacheResultIfNotFound (correlationId: CorrelationId) =
        task {
            let ownerNameActorProxy = OwnerName.CreateActorProxy ownerName correlationId

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
                    let owners = List<OwnerDto>()

                    let queryDefinition =
                        QueryDefinition(
                            """
                            SELECT c.State
                            FROM c
                                JOIN s IN c.State
                            WHERE (STRINGEQUALS(s.Event.created.ownerName, @ownerName, true)
                                OR STRINGEQUALS(s.Event.setName.ownerName, @ownerName, true))
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            """
                        )
                            .WithParameter("@ownerName", ownerName)
                            .WithParameter("@grainType", StateName.Owner)
                            .WithParameter("@partitionKey", StateName.Owner)

                    //logToConsole $"QueryDefinition in ownerNameExists:{Environment.NewLine}{printQueryDefinition queryDefinition}"

                    let iterator =
                        DefaultRetryPolicy.Execute(fun () ->
                            cosmosContainer.GetItemQueryIterator<OwnerEventValue>(queryDefinition, requestOptions = queryRequestOptions))

                    while iterator.HasMoreResults do
                        let! result = iterator.ReadNextAsync()
                        let ownersThatMatchName = result.Resource

                        ownersThatMatchName
                        |> Seq.iter (fun eventsForOneOwner ->
                            let ownerDto =
                                eventsForOneOwner.State
                                |> Seq.fold (fun ownerDto ownerEvent -> ownerDto |> OwnerDto.UpdateDto ownerEvent) OwnerDto.Default

                            owners.Add(ownerDto))

                    let ownerWithName =
                        owners.FirstOrDefault(
                            (fun owner -> String.Equals(owner.OwnerName, ownerName, StringComparison.InvariantCultureIgnoreCase)),
                            OwnerDto.Default
                        )

                    if String.IsNullOrEmpty(ownerWithName.OwnerName) then
                        logToConsole $"Did not find ownerId using OwnerName {ownerName}. cacheResultIfNotFound: {cacheResultIfNotFound}."
                        // We didn't find the OwnerId, so add this OwnerName to the MemoryCache and indicate that we have already checked.
                        //use newCacheEntry =
                        //    memoryCache.CreateEntry(
                        //        $"OwN:{ownerName}",
                        //        Value = MemoryCache.EntityDoesNotExist,
                        //        AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime
                        //    )
                        return false
                    else
                        // Add this OwnerName and OwnerId to the MemoryCache.
                        memoryCache.CreateOwnerNameEntry ownerName ownerWithName.OwnerId
                        memoryCache.CreateOwnerIdEntry ownerWithName.OwnerId MemoryCache.ExistsValue

                        // Set the OwnerId in the OwnerName actor.
                        do! ownerNameActorProxy.SetOwnerId ownerWithName.OwnerId correlationId
                        return true
                | MongoDB -> return false

        }

    /// Checks whether an organization has been deleted by querying the actor, and updates the MemoryCache with the result.
    let ownerIsDeleted (ownerId: string) correlationId =
        task {
            let ownerGuid = OwnerId.Parse(ownerId)
            let ownerActorProxy = Owner.CreateActorProxy ownerGuid correlationId

            let! isDeleted = ownerActorProxy.IsDeleted correlationId

            if isDeleted then
                memoryCache.CreateDeletedOwnerIdEntry ownerGuid MemoryCache.DoesNotExistValue
                return Some ownerId
            else
                memoryCache.CreateDeletedOwnerIdEntry ownerGuid MemoryCache.ExistsValue
                return None
        }

    /// Checks whether an owner exists by querying the actor, and updates the MemoryCache with the result.
    let ownerExists (ownerId: string) correlationId =
        task {
            // Call the Owner actor to check if the owner exists.
            let ownerGuid = Guid.Parse(ownerId)
            let ownerActorProxy = Owner.CreateActorProxy ownerGuid correlationId

            let! exists = ownerActorProxy.Exists correlationId

            if exists then
                // Add this OwnerId to the MemoryCache.
                memoryCache.CreateOwnerIdEntry ownerGuid MemoryCache.ExistsValue
                return Some ownerId
            else
                return None
        }

    /// Gets the OwnerId by checking for the existence of OwnerId if provided, or searching by OwnerName.
    let resolveOwnerId (ownerId: string) (ownerName: string) (correlationId: CorrelationId) =
        task {
            let mutable ownerGuid = Guid.Empty

            if not <| String.IsNullOrEmpty(ownerId) && Guid.TryParse(ownerId, &ownerGuid) then
                // Check if we have this owner id in MemoryCache.
                match memoryCache.GetOwnerIdEntry ownerGuid with
                | Some value ->
                    match value with
                    | MemoryCache.ExistsValue -> return Some ownerId
                    | MemoryCache.DoesNotExistValue -> return None
                    | _ -> return! ownerExists ownerId correlationId
                | None -> return! ownerExists ownerId correlationId
            elif String.IsNullOrEmpty(ownerName) then
                // We have no OwnerId or OwnerName to resolve.
                return None
            else
                // Check if we have this owner name in MemoryCache.
                match memoryCache.GetOwnerNameEntry ownerName with
                | Some ownerGuid ->
                    // We have already checked and the owner exists.
                    memoryCache.CreateOwnerIdEntry ownerGuid MemoryCache.ExistsValue
                    return Some $"{ownerGuid}"
                | None ->
                    // Check if we have an active OwnerName actor with a cached result.
                    let ownerNameActorProxy = OwnerName.CreateActorProxy ownerName correlationId

                    match! ownerNameActorProxy.GetOwnerId correlationId with
                    | Some ownerId ->
                        // Add this OwnerName and OwnerId to the MemoryCache.
                        memoryCache.CreateOwnerNameEntry ownerName ownerId
                        memoryCache.CreateOwnerIdEntry ownerGuid MemoryCache.ExistsValue

                        return Some $"{ownerId}"
                    | None ->
                        let! nameExists = ownerNameExists ownerName true correlationId

                        if nameExists then
                            // We have already checked and the owner exists.
                            match memoryCache.GetOwnerNameEntry ownerName with
                            | Some ownerGuid -> return Some $"{ownerGuid}"
                            | None -> // This should never happen, because we just populated the cache in ownerNameExists.
                                return None
                        else
                            // The owner name does not exist.
                            return None
        }

    /// Checks whether an organization is deleted by querying the actor, and updates the MemoryCache with the result.
    let organizationIsDeleted (organizationId: string) correlationId =
        task {
            let organizationGuid = Guid.Parse(organizationId)
            let organizationActorProxy = Organization.CreateActorProxy organizationGuid correlationId

            let! isDeleted = organizationActorProxy.IsDeleted correlationId

            let organizationGuid = OrganizationId.Parse(organizationId)

            if isDeleted then
                memoryCache.CreateDeletedOrganizationIdEntry organizationGuid MemoryCache.DoesNotExistValue
                return Some organizationId
            else
                memoryCache.CreateDeletedOrganizationIdEntry organizationGuid MemoryCache.ExistsValue
                return None
        }

    /// Checks whether an organization exists by querying the actor, and updates the MemoryCache with the result.
    let organizationExists (organizationId: string) correlationId =
        task {
            // Call the Organization actor to check if the organization exists.
            let organizationGuid = Guid.Parse(organizationId)
            let organizationActorProxy = Organization.CreateActorProxy organizationGuid correlationId

            let! exists = organizationActorProxy.Exists correlationId

            if exists then
                // Add this OrganizationId to the MemoryCache.
                memoryCache.CreateOrganizationIdEntry (OrganizationId.Parse(organizationId)) MemoryCache.ExistsValue
                return Some organizationId
            else
                return None
        }

    /// Gets the OrganizationId by either returning OrganizationId if provided, or searching by OrganizationName.
    let resolveOrganizationId (ownerId: OwnerId) (organizationId: string) (organizationName: string) (correlationId: CorrelationId) =
        task {
            let mutable organizationGuid = Guid.Empty

            if
                not <| String.IsNullOrEmpty(organizationId)
                && Guid.TryParse(organizationId, &organizationGuid)
            then
                match memoryCache.GetOrganizationIdEntry organizationGuid with
                | Some value ->
                    match value with
                    | MemoryCache.ExistsValue -> return Some organizationId
                    | MemoryCache.DoesNotExistValue -> return None
                    | _ -> return! organizationExists organizationId correlationId
                | None -> return! organizationExists organizationId correlationId
            elif String.IsNullOrEmpty(organizationName) then
                // We have no OrganizationId or OrganizationName to resolve.
                return None
            else
                // Check if we have this organization name in MemoryCache.
                match memoryCache.GetOrganizationNameEntry organizationName with
                | Some organizationGuid ->
                    if organizationGuid.Equals(MemoryCache.EntityDoesNotExistGuid) then
                        // We have already checked and the organization does not exist.
                        return None
                    else
                        memoryCache.CreateOrganizationIdEntry organizationGuid MemoryCache.ExistsValue
                        return Some $"{organizationGuid}"
                | None ->
                    // Check if we have an active OrganizationName actor with a cached result.
                    let organizationNameActorProxy = OrganizationName.CreateActorProxy ownerId organizationName correlationId

                    match! organizationNameActorProxy.GetOrganizationId correlationId with
                    | Some organizationId ->
                        // Add this OrganizationName and OrganizationId to the MemoryCache.
                        memoryCache.CreateOrganizationNameEntry organizationName organizationId
                        memoryCache.CreateOrganizationIdEntry organizationId MemoryCache.ExistsValue
                        return Some $"{organizationId}"
                    | None ->
                        // We have to call into Actor storage to get the OrganizationId.
                        match actorStateStorageProvider with
                        | Unknown -> return None
                        | AzureCosmosDb ->
                            let queryDefinition =
                                QueryDefinition(
                                    """
                                    SELECT s.Event.created.organizationId AS OrganizationId
                                    FROM c JOIN s IN c.State
                                    WHERE STRINGEQUALS(s.Event.created.organizationName, @organizationName, true)
                                        AND STRINGEQUALS(s.Event.created.ownerId, @ownerId, true)
                                        AND c.GrainType = @grainType
                                        AND c.PartitionKey = @partitionKey
                                    """
                                )
                                    .WithParameter("@organizationName", organizationName)
                                    .WithParameter("@ownerId", ownerId)
                                    .WithParameter("@grainType", StateName.Organization)
                                    .WithParameter("@partitionKey", StateName.Organization)

                            let iterator = DefaultRetryPolicy.Execute(fun () -> cosmosContainer.GetItemQueryIterator<OrganizationIdRecord>(queryDefinition))

                            if iterator.HasMoreResults then
                                let! currentResultSet = iterator.ReadNextAsync()

                                let organizationId =
                                    currentResultSet
                                        .FirstOrDefault({ organizationId = String.Empty })
                                        .organizationId

                                if String.IsNullOrEmpty(organizationId) then
                                    // We didn't find the OrganizationId, so add this OrganizationName to the MemoryCache and indicate that we have already checked.
                                    memoryCache.CreateOrganizationNameEntry organizationName MemoryCache.EntityDoesNotExistGuid
                                    return None
                                else
                                    // Add this OrganizationName and OrganizationId to the MemoryCache.
                                    organizationGuid <- Guid.Parse(organizationId)
                                    memoryCache.CreateOrganizationNameEntry organizationName organizationGuid
                                    memoryCache.CreateOrganizationIdEntry organizationGuid MemoryCache.ExistsValue

                                    do! organizationNameActorProxy.SetOrganizationId organizationGuid correlationId
                                    return Some organizationId
                            else
                                return None
                        | MongoDB -> return None
        }

    /// Checks whether a repository has been deleted by querying the actor, and updates the MemoryCache with the result.
    let repositoryIsDeleted organizationId (repositoryId: string) correlationId =
        task {
            // Call the Repository actor to check if the repository is deleted.
            let repositoryGuid = Guid.Parse(repositoryId)
            let repositoryActorProxy = Repository.CreateActorProxy organizationId repositoryGuid correlationId

            let! isDeleted = repositoryActorProxy.IsDeleted correlationId

            if isDeleted then
                memoryCache.CreateDeletedRepositoryIdEntry repositoryGuid MemoryCache.DoesNotExistValue
                return Some repositoryId
            else
                memoryCache.CreateDeletedRepositoryIdEntry repositoryGuid MemoryCache.ExistsValue
                return None
        }

    /// Checks whether a repository exists by querying the actor, and updates the MemoryCache with the result.
    let repositoryExists organizationId (repositoryId: string) correlationId =
        task {
            // Call the Repository actor to check if the repository exists.
            let repositoryGuid = Guid.Parse(repositoryId)
            let repositoryActorProxy = Repository.CreateActorProxy organizationId repositoryGuid correlationId

            let! exists = repositoryActorProxy.Exists correlationId

            if exists then
                // Add this RepositoryId to the MemoryCache.
                memoryCache.CreateRepositoryIdEntry repositoryGuid MemoryCache.ExistsValue
                return Some repositoryGuid
            else
                return None
        }

    /// Gets the RepositoryId by returning RepositoryId if provided, or searching by RepositoryName within the provided owner and organization.
    let resolveRepositoryId (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryId: string) (repositoryName: string) (correlationId: CorrelationId) =
        task {
            let mutable repositoryGuid = Guid.Empty

            if
                not <| String.IsNullOrEmpty(repositoryId)
                && Guid.TryParse(repositoryId, &repositoryGuid)
            then
                match memoryCache.GetRepositoryIdEntry repositoryGuid with
                | Some value ->
                    match value with
                    | MemoryCache.ExistsValue -> return Some repositoryGuid
                    | MemoryCache.DoesNotExistValue -> return None
                    | _ -> return! repositoryExists organizationId repositoryId correlationId
                | None -> return! repositoryExists organizationId repositoryId correlationId
            elif String.IsNullOrEmpty(repositoryName) then
                // We don't have a RepositoryId or RepositoryName, so we can't resolve the RepositoryId.
                return None
            else
                match memoryCache.GetRepositoryNameEntry repositoryName with
                | Some repositoryGuid ->
                    if repositoryGuid.Equals(Constants.MemoryCache.EntityDoesNotExist) then
                        // We have already checked and the repository does not exist.
                        return None
                    else
                        // We have already checked and the repository exists.
                        memoryCache.CreateRepositoryIdEntry repositoryGuid MemoryCache.ExistsValue
                        return Some repositoryGuid
                | None ->
                    // Check if we have an active RepositoryName actor with a cached result.
                    let repositoryNameActorProxy = RepositoryName.CreateActorProxy ownerId organizationId repositoryName correlationId

                    match! repositoryNameActorProxy.GetRepositoryId correlationId with
                    | Some repositoryId ->
                        memoryCache.CreateRepositoryNameEntry repositoryName repositoryId
                        memoryCache.CreateRepositoryIdEntry repositoryId MemoryCache.ExistsValue
                        return Some repositoryId
                    | None ->
                        // We have to call into Actor storage to get the RepositoryId.
                        match actorStateStorageProvider with
                        | Unknown -> return None
                        | AzureCosmosDb ->
                            let queryDefinition =
                                QueryDefinition(
                                    """
                                    SELECT s.Event.created.repositoryId AS RepositoryId
                                    FROM c
                                        JOIN s IN c.State
                                    WHERE STRINGEQUALS(s.Event.created.repositoryName, @repositoryName, true)
                                        AND STRINGEQUALS(s.Event.created.organizationId, @organizationId, true)
                                        AND STRINGEQUALS(s.Event.created.ownerId, @ownerId, true)
                                        AND c.GrainType = @grainType
                                        AND c.PartitionKey = @partitionKey
                                   """
                                )
                                    .WithParameter("@repositoryName", repositoryName)
                                    .WithParameter("@organizationId", organizationId)
                                    .WithParameter("@ownerId", ownerId)
                                    .WithParameter("@grainType", StateName.Repository)
                                    .WithParameter("@partitionKey", organizationId)

                            let iterator = cosmosContainer.GetItemQueryIterator<RepositoryIdRecord>(queryDefinition)

                            if iterator.HasMoreResults then
                                let! currentResultSet = iterator.ReadNextAsync()

                                let repositoryIdString = currentResultSet.FirstOrDefault({ repositoryId = String.Empty }).repositoryId

                                if String.IsNullOrEmpty(repositoryIdString) then
                                    // We didn't find the RepositoryId, so add this RepositoryName to the MemoryCache and indicate that we have already checked.
                                    memoryCache.CreateRepositoryNameEntry repositoryName MemoryCache.EntityDoesNotExistGuid
                                    return None
                                else
                                    // Add this RepositoryName and RepositoryId to the MemoryCache.
                                    let repositoryId = Guid.Parse(repositoryIdString)
                                    memoryCache.CreateRepositoryNameEntry repositoryName repositoryId
                                    memoryCache.CreateRepositoryIdEntry repositoryId MemoryCache.ExistsValue

                                    // Set the RepositoryId in the RepositoryName actor.
                                    do! repositoryNameActorProxy.SetRepositoryId repositoryId correlationId
                                    return Some repositoryId
                            else
                                return None
                        | MongoDB -> return None
        }

    /// Checks whether a branch has been deleted by querying the actor, and updates the MemoryCache with the result.
    let branchIsDeleted (branchId: string) repositoryId correlationId =
        task {
            let branchGuid = Guid.Parse(branchId)
            let branchActorProxy = Branch.CreateActorProxy branchGuid repositoryId correlationId

            let! isDeleted = branchActorProxy.IsDeleted correlationId

            if isDeleted then
                memoryCache.CreateDeletedBranchIdEntry branchGuid MemoryCache.DoesNotExistValue
                return Some branchId
            else
                memoryCache.CreateDeletedBranchIdEntry branchGuid MemoryCache.ExistsValue
                return None
        }

    /// Checks whether a branch exists by querying the actor, and updates the MemoryCache with the result.
    let branchExists (branchId: BranchId) repositoryId correlationId =
        task {
            // Call the Branch actor to check if the branch exists.
            let branchActorProxy = Branch.CreateActorProxy branchId repositoryId correlationId
            let! exists = branchActorProxy.Exists correlationId

            if exists then
                // Add this BranchId to the MemoryCache.
                memoryCache.CreateBranchIdEntry branchId MemoryCache.ExistsValue
                return Some branchId
            else
                return None
        }

    /// Gets the BranchId by returning BranchId if provided, or searching by BranchName within the provided repository.
    let resolveBranchId ownerId organizationId repositoryId branchId branchName (correlationId: CorrelationId) =
        task {
            let mutable branchGuid = Guid.Empty

            if not <| String.IsNullOrEmpty(branchId) && Guid.TryParse(branchId, &branchGuid) then
                match memoryCache.GetBranchIdEntry branchGuid with
                | Some value ->
                    match value with
                    | MemoryCache.ExistsValue -> return Some branchGuid
                    | MemoryCache.DoesNotExistValue -> return None
                    | _ -> return! branchExists branchGuid repositoryId correlationId
                | None -> return! branchExists branchGuid repositoryId correlationId
            elif String.IsNullOrEmpty(branchName) then
                // We don't have a BranchId or BranchName, so we can't resolve the BranchId.
                return None
            else
                // Check if we have an active BranchName actor with a cached result.
                match memoryCache.GetBranchNameEntry(repositoryId, branchName) with
                | Some branchGuid ->
                    if branchGuid.Equals(Constants.MemoryCache.EntityDoesNotExist) then
                        // We have already checked and the branch does not exist.
                        return None
                    else
                        // We have already checked and the branch exists.
                        return Some branchGuid
                | None ->
                    // Check if we have an active BranchName actor with a cached result.
                    let branchNameActorProxy = BranchName.CreateActorProxy repositoryId branchName correlationId

                    match! branchNameActorProxy.GetBranchId correlationId with
                    | Some branchId ->
                        // Add this BranchName and BranchId to the MemoryCache.
                        memoryCache.CreateBranchNameEntry(repositoryId, branchName, branchId)
                        memoryCache.CreateBranchIdEntry branchId MemoryCache.ExistsValue
                        return Some branchGuid
                    | None ->
                        // We have to call into Actor storage to get the BranchId.
                        match actorStateStorageProvider with
                        | Unknown -> return None
                        | AzureCosmosDb ->
                            let queryDefinition =
                                QueryDefinition(
                                    """
                                    SELECT s.Event.created.branchId AS BranchId
                                    FROM c
                                        JOIN s IN c.State
                                    WHERE STRINGEQUALS(s.Event.created.branchName, @branchName, true)
                                        AND STRINGEQUALS(s.Event.created.repositoryId, @repositoryId, true)
                                        AND STRINGEQUALS(s.Event.created.organizationId, @organizationId, true)
                                        AND STRINGEQUALS(s.Event.created.ownerId, @ownerId, true)
                                        AND c.GrainType = @grainType
                                        AND c.PartitionKey = @partitionKey
                                    """
                                )
                                    .WithParameter("@branchName", branchName)
                                    .WithParameter("@repositoryId", repositoryId)
                                    .WithParameter("@organizationId", organizationId)
                                    .WithParameter("@ownerId", ownerId)
                                    .WithParameter("@grainType", StateName.Branch)
                                    .WithParameter("@partitionKey", repositoryId)

                            let iterator = DefaultRetryPolicy.Execute(fun () -> cosmosContainer.GetItemQueryIterator<BranchIdRecord>(queryDefinition))
                            //logToConsole $"QueryDefinition in resolveBranchId:{Environment.NewLine}{printQueryDefinition queryDefinition}"

                            if iterator.HasMoreResults then
                                let! currentResultSet = iterator.ReadNextAsync()
                                let branchId = currentResultSet.FirstOrDefault({ branchId = String.Empty }).branchId

                                if String.IsNullOrEmpty(branchId) then
                                    // We didn't find the BranchId.
                                    //logToConsole $"We didn't find the BranchId. BranchName: {branchName}; BranchId: none."
                                    return None
                                else
                                    // Add this BranchName and BranchId to the MemoryCache.
                                    branchGuid <- Guid.Parse(branchId)

                                    // Add this BranchName and BranchId to the MemoryCache.
                                    memoryCache.CreateBranchNameEntry(repositoryId, branchName, branchGuid)
                                    memoryCache.CreateBranchIdEntry branchGuid MemoryCache.ExistsValue

                                    // Set the BranchId in the BranchName actor.
                                    do! branchNameActorProxy.SetBranchId branchGuid correlationId
                                    //logToConsole $"BranchName actor was not active. BranchName: {branchName}; BranchId: {branchGuid}."
                                    return Some branchGuid
                            else
                                return None
                        | MongoDB -> return None
        }

    /// Creates a CosmosDB SQL WHERE clause that includes or excludes deleted entities.
    let includeDeletedEntitiesClause includeDeletedEntities =
        if includeDeletedEntities then
            // If includeDeletedEntities is true, we don't need to filter out deleted entities.
            String.Empty
        else
            // We're checking to see if:
            //  (count of logicalDeletes) = (count of undeletes)
            //
            //  Usually, of course, both counts are 0, but it's not as simple as checking if there's a delete event.
            //  We can tell if an entity is deleted by checking those counts.
            """
            AND (
            (SELECT VALUE COUNT(1)
                FROM c JOIN subEvent IN c.State
                WHERE IS_DEFINED(subEvent.Event.logicalDeleted)) =
            (SELECT VALUE COUNT(1)
                FROM c JOIN subEvent IN c.State
                WHERE IS_DEFINED(subEvent.Event.undeleted))
            )
            """

    /// Gets a list of organizations for the specified owner.
    let getOrganizations (ownerId: OwnerId) (maxCount: int) includeDeleted =
        task {
            let organizations = List<OrganizationDto>()

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let indexMetrics = stringBuilderPool.Get()
                let requestCharge = stringBuilderPool.Get()

                try
                    try
                        let queryDefinition =
                            QueryDefinition(
                                $"""
                                SELECT TOP @maxCount c.State
                                FROM c
                                    JOIN s IN c.State
                                WHERE STRINGEQUALS(s.Event.created.ownerId, @ownerId, true)
                                    AND c.GrainType = @grainType
                                    AND c.PartitionKey = @partitionKey
                                    {includeDeletedEntitiesClause includeDeleted}
                                """
                            )
                                .WithParameter("@maxCount", maxCount)
                                .WithParameter("@ownerId", ownerId)
                                .WithParameter("@grainType", StateName.Organization)
                                .WithParameter("@partitionKey", StateName.Organization)

                        let iterator = cosmosContainer.GetItemQueryIterator<OrganizationEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                        while iterator.HasMoreResults do
                            let! results = iterator.ReadNextAsync()
                            indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                            requestCharge.Append($"{results.RequestCharge}, ") |> ignore

                            let eventsForAllOrganizations = results.Resource

                            eventsForAllOrganizations
                            |> Seq.iter (fun eventsForOneOrganization ->
                                let organizationDto =
                                    eventsForOneOrganization.State
                                    |> Seq.fold
                                        (fun organizationDto organizationEvent -> organizationDto |> OrganizationDto.UpdateDto organizationEvent)
                                        OrganizationDto.Default

                                organizations.Add(organizationDto))

                        if
                            (indexMetrics.Length >= 2)
                            && (requestCharge.Length >= 2)
                            && Activity.Current <> null
                        then
                            Activity.Current
                                .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                            |> ignore
                    with ex ->
                        logToConsole $"Got an exception."
                        logToConsole $"{ExceptionResponse.Create ex}"
                finally
                    stringBuilderPool.Return(indexMetrics)
                    stringBuilderPool.Return(requestCharge)
            | MongoDB -> ()

            return organizations.OrderBy(fun o -> o.OrganizationName).ToArray()
        }

    /// Checks if the specified organization name is unique for the specified owner.
    let organizationNameIsUnique<'T> (ownerId: string) (organizationName: string) (correlationId: CorrelationId) =
        task {
            match actorStateStorageProvider with
            | Unknown -> return Ok false
            | AzureCosmosDb ->
                try
                    let organizations = List<OrganizationDto>()

                    let queryDefinition =
                        QueryDefinition(
                            """
                            SELECT c.State
                            FROM c
                                JOIN s IN c.State
                            WHERE (STRINGEQUALS(s.Event.created.organizationName, @organizationName, true)
                                OR STRINGEQUALS(s.Event.setName.organizationName, @organizationName, true))
                                AND STRINGEQUALS(s.Event.created.ownerId, @ownerId, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            """
                        )
                            .WithParameter("@organizationName", organizationName)
                            .WithParameter("@ownerId", ownerId)
                            .WithParameter("@grainType", StateName.Organization)
                            .WithParameter("@partitionKey", StateName.Organization)

                    let iterator = cosmosContainer.GetItemQueryIterator<OrganizationEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! result = iterator.ReadNextAsync()
                        let eventsForAllOrganizations = result.Resource

                        eventsForAllOrganizations
                        |> Seq.iter (fun eventsForOneOrganization ->
                            let organizationDto =
                                eventsForOneOrganization.State
                                |> Seq.fold
                                    (fun organizationDto organizationEvent -> organizationDto |> OrganizationDto.UpdateDto organizationEvent)
                                    OrganizationDto.Default

                            organizations.Add(organizationDto))

                    let organizationWithName =
                        organizations.FirstOrDefault(
                            (fun o -> String.Equals(o.OrganizationName, organizationName, StringComparison.OrdinalIgnoreCase)),
                            OrganizationDto.Default
                        )

                    if String.IsNullOrEmpty(organizationWithName.OrganizationName) then
                        // The organization name is unique.
                        return Ok true
                    else
                        // The organization name is not unique.
                        return Ok false
                with ex ->
                    return Error $"{ExceptionResponse.Create ex}"
            | MongoDB -> return Ok false
        }

    /// Checks if the specified repository name is unique for the specified organization.
    let repositoryNameIsUnique<'T> (ownerId: string) (organizationId: string) (repositoryName: string) (correlationId: CorrelationId) =
        task {
            match actorStateStorageProvider with
            | Unknown -> return Ok false
            | AzureCosmosDb ->
                try
                    let repositories = List<RepositoryDto>()

                    let queryDefinition =
                        QueryDefinition(
                            """
                            SELECT c.State
                            FROM c
                                JOIN s IN c.State
                            WHERE (STRINGEQUALS(s.Event.created.repositoryName, @repositoryName, true)
                                OR STRINGEQUALS(s.Event.setName.repositoryName, @repositoryName, true))
                                AND STRINGEQUALS(s.Event.created.organizationId, @organizationId, true)
                                AND STRINGEQUALS(s.Event.created.ownerId, @ownerId, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            """
                        )
                            .WithParameter("@repositoryName", repositoryName)
                            .WithParameter("@organizationId", organizationId)
                            .WithParameter("@ownerId", ownerId)
                            .WithParameter("@grainType", StateName.Repository)
                            .WithParameter("@partitionKey", organizationId)

                    let iterator = cosmosContainer.GetItemQueryIterator<RepositoryEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! result = iterator.ReadNextAsync()
                        let eventsForAllRepositories = result.Resource

                        eventsForAllRepositories
                        |> Seq.iter (fun eventsForOneRepository ->
                            let repositoryDto =
                                eventsForOneRepository.State
                                |> Seq.fold
                                    (fun repositoryDto repositoryEvent -> repositoryDto |> RepositoryDto.UpdateDto repositoryEvent)
                                    RepositoryDto.Default

                            repositories.Add(repositoryDto))

                    let repositoryWithName =
                        repositories.FirstOrDefault(
                            (fun o -> String.Equals(o.RepositoryName, repositoryName, StringComparison.OrdinalIgnoreCase)),
                            RepositoryDto.Default
                        )

                    if String.IsNullOrEmpty(repositoryWithName.RepositoryName) then
                        // The repository name is unique.
                        return Ok true
                    else
                        return Ok true // This else should never be hit.
                with ex ->
                    return Error $"{ExceptionResponse.Create ex}"
            | MongoDB -> return Ok false
        }

    /// Gets a list of repositories for the specified organization.
    let getRepositories (ownerId: OwnerId) (organizationId: OrganizationId) (maxCount: int) includeDeleted =
        task {
            let repositories = List<RepositoryDto>()

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let indexMetrics = stringBuilderPool.Get()
                let requestCharge = stringBuilderPool.Get()

                try
                    try
                        let queryDefinition =
                            QueryDefinition(
                                $"""
                                SELECT TOP @maxCount c.State
                                FROM c
                                    JOIN s IN c.State
                                WHERE STRINGEQUALS(s.Event.created.organizationId, @organizationId, true)
                                    AND STRINGEQUALS(s.Event.created.ownerId, @ownerId, true)
                                    {includeDeletedEntitiesClause includeDeleted}
                                    AND c.GrainType = @grainType
                                    AND c.PartitionKey = @partitionKey
                                """
                            )
                                .WithParameter("@ownerId", ownerId)
                                .WithParameter("@organizationId", organizationId)
                                .WithParameter("@maxCount", maxCount)
                                .WithParameter("@grainType", StateName.Repository)
                                .WithParameter("@partitionKey", organizationId)

                        let iterator = cosmosContainer.GetItemQueryIterator<RepositoryEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                        while iterator.HasMoreResults do
                            let! results = iterator.ReadNextAsync()
                            indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                            requestCharge.Append($"{results.RequestCharge}, ") |> ignore

                            let eventsForAllRepositories = results.Resource

                            eventsForAllRepositories
                            |> Seq.iter (fun eventsForOneRepository ->
                                let repositoryDto =
                                    eventsForOneRepository.State
                                    |> Array.fold
                                        (fun repositoryDto repositoryEvent -> repositoryDto |> RepositoryDto.UpdateDto repositoryEvent)
                                        RepositoryDto.Default

                                repositories.Add(repositoryDto))

                        if
                            (indexMetrics.Length >= 2)
                            && (requestCharge.Length >= 2)
                            && Activity.Current <> null
                        then
                            Activity.Current
                                .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                            |> ignore
                    with ex ->
                        logToConsole $"Got an exception."
                        logToConsole $"{ExceptionResponse.Create ex}"
                finally
                    stringBuilderPool.Return(indexMetrics)
                    stringBuilderPool.Return(requestCharge)
            | MongoDB -> ()

            return repositories.OrderBy(fun r -> r.RepositoryName).ToArray()
        }

    /// Gets a list of branches for a given repository.
    let getBranches (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryId: RepositoryId) (maxCount: int) includeDeleted correlationId =
        task {
            let branches = ConcurrentBag<BranchDto>()

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let indexMetrics = stringBuilderPool.Get()
                let requestCharge = stringBuilderPool.Get()

                try
                    try
                        let queryDefinition =
                            QueryDefinition(
                                $"""
                                SELECT TOP @maxCount c.State
                                FROM c
                                    JOIN s IN c.State
                                WHERE STRINGEQUALS(s.Event.created.repositoryId, @repositoryId, true)
                                    AND STRINGEQUALS(s.Event.created.ownerId, @ownerId, true)
                                    AND STRINGEQUALS(s.Event.created.organizationId, @organizationId, true)
                                    AND LENGTH(s.Event.created.branchName) > 0
                                    {includeDeletedEntitiesClause includeDeleted}
                                    AND c.GrainType = @grainType
                                    AND c.PartitionKey = @partitionKey
                                """
                            )
                                .WithParameter("@maxCount", maxCount)
                                .WithParameter("@ownerId", ownerId)
                                .WithParameter("@organizationId", organizationId)
                                .WithParameter("@repositoryId", repositoryId)
                                .WithParameter("@grainType", StateName.Branch)
                                .WithParameter("@partitionKey", $"{repositoryId}")

                        let iterator = cosmosContainer.GetItemQueryIterator<BranchEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                        while iterator.HasMoreResults do
                            addTiming TimingFlag.BeforeStorageQuery "getBranches" correlationId
                            let! results = iterator.ReadNextAsync()
                            addTiming TimingFlag.AfterStorageQuery "getBranches" correlationId
                            indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                            requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                            let eventsForAllBranches = results.Resource

                            eventsForAllBranches
                            |> Seq.iter (fun eventsForOneBranch ->
                                let branchDto =
                                    eventsForOneBranch.State
                                    |> Array.fold (fun branchDto branchEvent -> branchDto |> BranchDto.UpdateDto branchEvent) BranchDto.Default

                                branches.Add(branchDto))

                        if
                            indexMetrics.Length >= 2
                            && requestCharge.Length >= 2
                            && Activity.Current <> null
                        then
                            Activity.Current
                                .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                            |> ignore
                    with ex ->
                        logToConsole $"Got an exception."
                        logToConsole $"{ExceptionResponse.Create ex}"
                finally
                    stringBuilderPool.Return(indexMetrics)
                    stringBuilderPool.Return(requestCharge)
            | MongoDB -> ()

            return branches.OrderBy(fun b -> b.BranchName).ToArray()
        }

    /// Gets a list of references that match a provided SHA-256 hash.
    let getReferencesBySha256Hash (repositoryId: RepositoryId) (branchId: BranchId) (sha256Hash: Sha256Hash) (maxCount: int) =
        task {
            let references = List<ReferenceDto>()

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let indexMetrics = stringBuilderPool.Get()
                let requestCharge = stringBuilderPool.Get()

                try
                    let queryDefinition =
                        QueryDefinition(
                            """
                            SELECT TOP @maxCount c.State
                            FROM c
                                JOIN s IN c.State
                            WHERE STRINGEQUALS(s.Event.created.BranchId, @branchId, true)
                                AND STRINGEQUALS(s.Event.created.RepositoryId, @repositoryId, true)
                                AND STARTSWITH(s.Event.created.Sha256Hash, @sha256Hash, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            """
                        )
                            .WithParameter("@maxCount", maxCount)
                            .WithParameter("@sha256Hash", sha256Hash)
                            .WithParameter("@branchId", branchId)
                            .WithParameter("@repositoryId", repositoryId)
                            .WithParameter("@grainType", StateName.Reference)
                            .WithParameter("@partitionKey", $"{repositoryId}")

                    let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                        let eventsForAllReferences = results.Resource

                        eventsForAllReferences
                        |> Seq.iter (fun eventsForOneReference ->
                            let referenceDto =
                                eventsForOneReference.State
                                |> Array.fold (fun referenceDto referenceEvent -> referenceDto |> ReferenceDto.UpdateDto referenceEvent) ReferenceDto.Default

                            references.Add(referenceDto))

                    if
                        (indexMetrics.Length >= 2)
                        && (requestCharge.Length >= 2)
                        && Activity.Current <> null
                    then
                        Activity.Current
                            .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                            .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                        |> ignore
                finally
                    stringBuilderPool.Return(indexMetrics)
                    stringBuilderPool.Return(requestCharge)
            | MongoDB -> ()

            return references.OrderBy(fun reference -> reference.CreatedAt).ToArray()
        }

    /// Gets a reference by its SHA-256 hash.
    let getReferenceBySha256Hash (repositoryId: RepositoryId) (branchId: BranchId) (sha256Hash: Sha256Hash) =
        task {
            let! references = getReferencesBySha256Hash repositoryId branchId sha256Hash 1
            if references.Length > 0 then return Some references[0] else return None
        }

    /// Gets a list of references for a given branch.
    let getReferences (repositoryId: RepositoryId) (branchId: BranchId) (maxCount: int) (correlationId: CorrelationId) =
        task {
            let references = List<ReferenceDto>()

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let indexMetrics = stringBuilderPool.Get()
                let requestCharge = stringBuilderPool.Get()

                try
                    let queryDefinition =
                        QueryDefinition(
                            """
                            SELECT TOP @maxCount c.State
                            FROM c
                                JOIN s IN c.State 
                            WHERE STRINGEQUALS(s.Event.created.BranchId, @branchId, true)
                                AND STRINGEQUALS(s.Event.created.RepositoryId, @repositoryId, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            """
                        )
                            .WithParameter("@maxCount", maxCount)
                            .WithParameter("@repositoryId", repositoryId)
                            .WithParameter("@branchId", branchId)
                            .WithParameter("@grainType", StateName.Reference)
                            .WithParameter("@partitionKey", repositoryId)

                    let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        addTiming TimingFlag.BeforeStorageQuery "getReferences" correlationId
                        let! results = iterator.ReadNextAsync()
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                        let eventsForAllReferences = results.Resource

                        eventsForAllReferences
                        |> Seq.iter (fun eventsForOneReference ->
                            let referenceDto =
                                eventsForOneReference.State
                                |> Array.fold (fun referenceDto referenceEvent -> referenceDto |> ReferenceDto.UpdateDto referenceEvent) ReferenceDto.Default

                            references.Add(referenceDto))

                    if
                        indexMetrics.Length >= 2
                        && requestCharge.Length >= 2
                        && Activity.Current <> null
                    then
                        Activity.Current
                            .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                            .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                        |> ignore
                finally
                    stringBuilderPool.Return(indexMetrics)
                    stringBuilderPool.Return(requestCharge)
            | MongoDB -> ()

            return references.OrderBy(fun reference -> reference.CreatedAt).ToArray()
        }

    type DocumentIdentifier() =
        member val id = String.Empty with get, set
        member val PartitionKey = String.Empty with get, set

    type PartitionKeyIdentifier() =
        member val PartitionKey = String.Empty with get, set

    /// Deletes all documents from CosmosDb.
    ///
    /// **** This method is implemented only in Debug configuration. It is a no-op in Release configuration. ****
    let deleteAllFromCosmosDBThatMatch (queryDefinition: QueryDefinition) =
        task {
#if DEBUG
            let failed = List<string>()

            try
                // (MaxDegreeOfParallelism = 3) runs at 700 RU's, so it fits under the free 1,000 RU limit for CosmosDB, without getting throttled.
                let parallelOptions = ParallelOptions(MaxDegreeOfParallelism = 4)

                let itemRequestOptions =
                    ItemRequestOptions(AddRequestHeaders = fun headers -> headers.Add(Constants.CorrelationIdHeaderKey, "deleteAllFromCosmosDBThatMatch"))

                let mutable totalRecordsDeleted = 0
                let overallStartTime = getCurrentInstant ()
                queryRequestOptions.MaxItemCount <- 1000

                logToConsole
                    $"cosmosContainer.Id: {cosmosContainer.Id}; cosmosContainer.Database.Id: {cosmosContainer.Database.Id}; cosmosContainer.Database.Client.Endpoint: {cosmosContainer.Database.Client.Endpoint}."

                let iterator = cosmosContainer.GetItemQueryIterator<PartitionKeyIdentifier>(queryDefinition, requestOptions = queryRequestOptions)

                while iterator.HasMoreResults do
                    let batchStartTime = getCurrentInstant ()
                    let! batchResults = iterator.ReadNextAsync()
                    let mutable totalRequestCharge = 0L

                    //logToConsole $"In Services.deleteAllFromCosmosDB(): Current batch size: {batchResults.Resource.Count()}."

                    do!
                        Parallel.ForEachAsync(
                            batchResults,
                            parallelOptions,
                            (fun document ct ->
                                ValueTask(
                                    task {
                                        //let! deleteResponse =
                                        //    cosmosContainer.DeleteItemAsync(document.id, PartitionKey(document.PartitionKey), itemRequestOptions)

                                        //// Multiplying by 1000 because Interlocked.Add() expects an int64; we'll divide by 1000 when logging.
                                        //totalRequestCharge <- Interlocked.Add(&totalRequestCharge, int64 (deleteResponse.RequestCharge * 1000.0))

                                        //if deleteResponse.StatusCode <> HttpStatusCode.NoContent then
                                        //    failed.Add(document.id)
                                        //    logToConsole $"Failed to delete id {document.id}."

                                        use! deleteResponse =
                                            cosmosContainer.DeleteAllItemsByPartitionKeyStreamAsync(PartitionKey(document.PartitionKey), itemRequestOptions)

                                        if deleteResponse.IsSuccessStatusCode then
                                            logToConsole $"Request succeeded to delete all items with PartitionKey = {document.PartitionKey}."
                                        else
                                            failed.Add(document.PartitionKey)

                                            logToConsole
                                                $"Failed to delete PartitionKey {document.PartitionKey}. StatusCode: {deleteResponse.StatusCode}; Error: {deleteResponse.ErrorMessage}."

                                    }
                                ))
                        )

                //let duration_s = getCurrentInstant().Minus(batchStartTime).TotalSeconds
                //let overall_duration_s = getCurrentInstant().Minus(overallStartTime).TotalSeconds
                //let rps = float (batchResults.Resource.Count()) / duration_s
                //totalRecordsDeleted <- totalRecordsDeleted + batchResults.Resource.Count()
                //let overallRps = float totalRecordsDeleted / overall_duration_s

                //logToConsole
                //    $"In Services.deleteAllFromCosmosDBThatMatch(): batch duration (s): {duration_s:F3}; batch requests/second: {rps:F3}; failed.Count: {failed.Count}; totalRequestCharge: {float totalRequestCharge / 1000.0:F2}; totalRecordsDeleted: {totalRecordsDeleted}; overall duration (m): {overall_duration_s / 60.0:F3}; overall requests/second: {overallRps:F3}."

                return failed
            with ex ->
                failed.Add((ExceptionResponse.Create ex).``exception``)
                return failed
#else
            return List<string>([ "Not implemented" ])
#endif
        }

    /// Deletes all documents from CosmosDB.
    ///
    /// **** This method is implemented only in Debug configuration. It is a no-op in Release configuration. ****
    let deleteAllFromCosmosDb () =
        task {
#if DEBUG
            //let queryDefinition = QueryDefinition("""SELECT c.id, c.PartitionKey FROM c ORDER BY c.PartitionKey""")
            let queryDefinition = QueryDefinition("""SELECT DISTINCT c.PartitionKey FROM c ORDER BY c.PartitionKey""")
            return! deleteAllFromCosmosDBThatMatch queryDefinition
#else
            return List<string>([ "Not implemented" ])
#endif
        }

    /// Deletes all Reminders from CosmosDB.
    ///
    /// **** This method is implemented only in Debug configuration. It is a no-op in Release configuration. ****
    let deleteAllRemindersFromCosmosDb () =
        task {
#if DEBUG
            let queryDefinition = QueryDefinition("""SELECT c.id, c.PartitionKey FROM c WHERE c.GrainType = "Rmd" ORDER BY c.PartitionKey""")
            return! deleteAllFromCosmosDBThatMatch queryDefinition
#else
            return List<string>([ "Not implemented" ])
#endif
        }

    /// Gets a list of references of a given ReferenceType for a branch.
    let getReferencesByType (referenceType: ReferenceType) (repositoryId: RepositoryId) (branchId: BranchId) (maxCount: int) (correlationId: CorrelationId) =
        task {
            let references = List<ReferenceDto>()

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let indexMetrics = stringBuilderPool.Get()
                let requestCharge = stringBuilderPool.Get()

                try
                    let queryDefinition =
                        QueryDefinition(
                            """
                            SELECT TOP @maxCount c.State
                            FROM c
                                JOIN s IN c.State
                            WHERE STRINGEQUALS(s.Event.created.BranchId, @branchId, true)
                                AND STRINGEQUALS(s.Event.created.RepositoryId, @repositoryId, true)
                                AND STRINGEQUALS(s.Event.created.ReferenceType, @referenceType, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            """
                        )
                            .WithParameter("@maxCount", maxCount)
                            .WithParameter("@branchId", branchId)
                            .WithParameter("@repositoryId", repositoryId)
                            .WithParameter("@referenceType", getDiscriminatedUnionCaseName referenceType)
                            .WithParameter("@grainType", StateName.Reference)
                            .WithParameter("@partitionKey", repositoryId)

                    let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        addTiming TimingFlag.BeforeStorageQuery "getReferencesByType" correlationId
                        let! results = iterator.ReadNextAsync()
                        addTiming TimingFlag.AfterStorageQuery "getReferencesByType" correlationId
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                        let eventsForAllReferences = results.Resource

                        eventsForAllReferences
                        |> Seq.iter (fun eventsForOneReference ->
                            let referenceDto =
                                eventsForOneReference.State
                                |> Array.fold (fun referenceDto referenceEvent -> referenceDto |> ReferenceDto.UpdateDto referenceEvent) ReferenceDto.Default

                            references.Add(referenceDto))

                    if
                        indexMetrics.Length >= 2
                        && requestCharge.Length >= 2
                        && Activity.Current <> null
                    then
                        Activity.Current
                            .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                            .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                        |> ignore
                finally
                    stringBuilderPool.Return(indexMetrics)
                    stringBuilderPool.Return(requestCharge)
            | MongoDB -> ()

            return references.OrderBy(fun reference -> reference.CreatedAt).ToArray()
        }

    let getPromotions = getReferencesByType ReferenceType.Promotion
    let getCommits = getReferencesByType ReferenceType.Commit
    let getCheckpoints = getReferencesByType ReferenceType.Checkpoint
    let getSaves = getReferencesByType ReferenceType.Save
    let getTags = getReferencesByType ReferenceType.Tag
    let getExternals = getReferencesByType ReferenceType.External
    let getRebases = getReferencesByType ReferenceType.Rebase

    let getLatestReference repositoryId branchId =
        task {
            match actorStateStorageProvider with
            | Unknown -> return None
            | AzureCosmosDb ->
                let indexMetrics = stringBuilderPool.Get()
                let requestCharge = stringBuilderPool.Get()

                try
                    let references = List<ReferenceDto>()

                    let queryDefinition =
                        QueryDefinition(
                            """
                            SELECT TOP 1 c.State
                            FROM c
                            WHERE STRINGEQUALS(c.State.Event.created.BranchId, @branchId, true)
                                AND STRINGEQUALS(c.State.Event.created.RepositoryId, @repositoryId, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @repositoryId
                            ORDER BY c.State.Event.created.CreatedAt DESC
                            """
                        )
                            .WithParameter("@branchId", branchId)
                            .WithParameter("@repositoryId", repositoryId)
                            .WithParameter("@grainType", StateName.Reference)
                            .WithParameter("@partitionKey", repositoryId)

                    let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                        let eventsForAllReferences = results.Resource

                        eventsForAllReferences
                        |> Seq.iter (fun eventsForOneReference ->
                            let referenceDto =
                                eventsForOneReference.State
                                |> Array.fold (fun referenceDto referenceEvent -> referenceDto |> ReferenceDto.UpdateDto referenceEvent) ReferenceDto.Default

                            references.Add(referenceDto))

                    if
                        (indexMetrics.Length >= 2)
                        && (requestCharge.Length >= 2)
                        && Activity.Current <> null
                    then
                        Activity.Current
                            .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                            .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                        |> ignore

                    if references.Count > 0 then return Some references[0] else return None
                finally
                    stringBuilderPool.Return(indexMetrics)
                    stringBuilderPool.Return(requestCharge)
            | MongoDB -> return None
        }

    /// Gets the latest reference for a given ReferenceType in a branch.
    let getLatestReferenceByReferenceTypes (referenceTypes: ReferenceType array) (repositoryId: RepositoryId) (branchId: BranchId) =
        task {
            let referenceDtos = ConcurrentDictionary<ReferenceType, ReferenceDto>(referenceTypes.Length, referenceTypes.Length)

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let indexMetrics = stringBuilderPool.Get()
                let requestCharge = stringBuilderPool.Get()

                try
                    // CosmosDB SQL doesn't have a UNION clause. That means that the only way to get the latest reference
                    //   for each ReferenceType is to do separate queries for each ReferenceType that gets passed in.
                    //   It's annoying, but at least let's do it in parallel.

                    do!
                        Parallel.ForEachAsync(
                            referenceTypes,
                            Constants.ParallelOptions,
                            (fun referenceType ct ->
                                ValueTask(
                                    task {
                                        let queryDefinition =
                                            QueryDefinition(
                                                """
                                                SELECT TOP 1 c.State
                                                FROM c
                                                WHERE STRINGEQUALS(c.State.Event.created.BranchId, @branchId, true)
                                                    AND STRINGEQUALS(c.State.Event.created.RepositoryId, @repositoryId, true)
                                                    AND STRINGEQUALS(c.State.Event.created.ReferenceType, @referenceType, true)
                                                    AND c.GrainType = @grainType
                                                    AND c.PartitionKey = @partitionKey
                                                ORDER BY c.State.Event.created.CreatedAt DESC
                                                """
                                            )
                                                .WithParameter("@branchId", branchId)
                                                .WithParameter("@repositoryId", repositoryId)
                                                .WithParameter("@referenceType", getDiscriminatedUnionCaseName referenceType)
                                                .WithParameter("@grainType", StateName.Reference)
                                                .WithParameter("@partitionKey", repositoryId)

                                        //logToConsole $"In getLatestReferenceByReferenceTypes(): QueryDefinition:{Environment.NewLine}{printQueryDefinition queryDefinition}."

                                        let iterator =
                                            cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                                        while iterator.HasMoreResults do
                                            let! results = iterator.ReadNextAsync()
                                            //indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                                            //requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                                            let eventsForAllReferences = results.Resource

                                            eventsForAllReferences
                                            |> Seq.iter (fun eventsForOneReference ->
                                                let referenceDto =
                                                    eventsForOneReference.State
                                                    |> Array.fold
                                                        (fun referenceDto referenceEvent -> referenceDto |> ReferenceDto.UpdateDto referenceEvent)
                                                        ReferenceDto.Default

                                                referenceDtos.TryAdd(referenceType, referenceDto) |> ignore)
                                    }
                                ))
                        )

                    if
                        (indexMetrics.Length >= 2)
                        && (requestCharge.Length >= 2)
                        && Activity.Current <> null
                    then
                        Activity.Current
                            .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                            .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                        |> ignore
                finally
                    stringBuilderPool.Return(indexMetrics)
                    stringBuilderPool.Return(requestCharge)
            | MongoDB -> ()

            return referenceDtos :> IReadOnlyDictionary<ReferenceType, ReferenceDto>
        }

    /// Gets the latest reference for a given ReferenceType in a branch.
    let getLatestReferenceByType (referenceType: ReferenceType) (repositoryId: RepositoryId) (branchId: BranchId) =
        task {
            match actorStateStorageProvider with
            | Unknown -> return None
            | AzureCosmosDb ->
                let indexMetrics = stringBuilderPool.Get()
                let requestCharge = stringBuilderPool.Get()

                try
                    let queryDefinition =
                        QueryDefinition(
                            """
                            SELECT TOP 1 c.State
                            FROM c
                            WHERE STRINGEQUALS(c.State.Event.created.BranchId, @branchId, true)
                                AND STRINGEQUALS(c.State.Event.created.RepositoryId, @repositoryId, true)
                                AND STRINGEQUALS(c.State.Event.created.ReferenceType, @referenceType, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            ORDER BY c.State.Event.created.CreatedAt DESC
                            """
                        )
                            .WithParameter("@branchId", branchId)
                            .WithParameter("@repositoryId", repositoryId)
                            .WithParameter("@referenceType", getDiscriminatedUnionCaseName referenceType)
                            .WithParameter("@grainType", StateName.Reference)
                            .WithParameter("@partitionKey", repositoryId)

                    let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    let references = List<ReferenceDto>()

                    while iterator.HasMoreResults do
                        let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                        let eventsForAllReferences = results.Resource

                        eventsForAllReferences
                        |> Seq.iter (fun eventsForOneReference ->
                            let referenceDto =
                                eventsForOneReference.State
                                |> Array.fold (fun referenceDto referenceEvent -> referenceDto |> ReferenceDto.UpdateDto referenceEvent) ReferenceDto.Default

                            references.Add(referenceDto))

                    if
                        (indexMetrics.Length >= 2)
                        && (requestCharge.Length >= 2)
                        && Activity.Current <> null
                    then
                        Activity.Current
                            .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                            .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                        |> ignore

                    if references.Count > 0 then return Some references[0] else return None
                finally
                    stringBuilderPool.Return(indexMetrics)
                    stringBuilderPool.Return(requestCharge)
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
                let indexMetrics = stringBuilderPool.Get()
                let requestCharge = stringBuilderPool.Get()

                try
                    let queryDefinition =
                        QueryDefinition(
                            """
                            SELECT TOP 1 c.State
                            FROM c
                                JOIN s IN c.State
                            WHERE STRINGEQUALS(s.Event.created.RepositoryId, @repositoryId, true)
                                STARTSWITH(s.Event.created.Sha256Hash, @sha256Hash, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            """
                        )
                            .WithParameter("@sha256Hash", sha256Hash)
                            .WithParameter("@repositoryId", repositoryId)
                            .WithParameter("@grainType", StateName.DirectoryVersion)
                            .WithParameter("@partitionKey", repositoryId)

                    try
                        let iterator = cosmosContainer.GetItemQueryIterator<DirectoryVersionEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                        let directoryVersionDtos = List<DirectoryVersionDto>()

                        while iterator.HasMoreResults do
                            let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                            indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                            requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                            let eventsForAllDirectories = results.Resource

                            eventsForAllDirectories
                            |> Seq.iter (fun eventsForOneDirectory ->
                                let directoryVersionDto =
                                    eventsForOneDirectory.State
                                    |> Array.fold
                                        (fun directoryVersionDto directoryEvent -> directoryVersionDto |> DirectoryVersionDto.UpdateDto directoryEvent)
                                        DirectoryVersionDto.Default

                                directoryVersionDtos.Add(directoryVersionDto))

                            if directoryVersionDtos.Count > 0 then
                                directoryVersion <- directoryVersionDtos[0].DirectoryVersion

                        if
                            (indexMetrics.Length >= 2)
                            && (requestCharge.Length >= 2)
                            && Activity.Current <> null
                        then
                            Activity.Current
                                .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                            |> ignore
                    with ex ->
                        log.LogError(
                            ex,
                            "{CurrentInstant}: Exception in Services.getDirectoryBySha256Hash(). QueryDefinition: {queryDefinition}",
                            getCurrentInstantExtended (),
                            (serialize queryDefinition)
                        )
                finally
                    stringBuilderPool.Return(indexMetrics)
                    stringBuilderPool.Return(requestCharge)
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
                let indexMetrics = stringBuilderPool.Get()
                let requestCharge = stringBuilderPool.Get()

                try
                    let queryDefinition =
                        QueryDefinition(
                            $"""
                            SELECT TOP 1 c.State
                            FROM c
                                JOIN s IN c.State
                            WHERE STARTSWITH(s.Event.created.Sha256Hash, @sha256Hash, true)
                                AND STRINGEQUALS(s.Event.created.RepositoryId, @repositoryId, true)
                                AND STRINGEQUALS(s.Event.create.RelativePath, @relativePath, true)                                
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            """
                        )
                            .WithParameter("@sha256Hash", sha256Hash)
                            .WithParameter("@repositoryId", repositoryId)
                            .WithParameter("@relativePath", Constants.RootDirectoryPath)
                            .WithParameter("@grainType", StateName.DirectoryVersion)
                            .WithParameter("@partitionKey", repositoryId)

                    try
                        let iterator = cosmosContainer.GetItemQueryIterator<DirectoryVersionEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                        let directoryVersionDtos = List<DirectoryVersionDto>()

                        while iterator.HasMoreResults do
                            let! results = iterator.ReadNextAsync()
                            indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                            requestCharge.Append($"{results.RequestCharge}, ") |> ignore
                            let eventsForAllDirectories = results.Resource

                            eventsForAllDirectories
                            |> Seq.iter (fun eventsForOneDirectory ->
                                let directoryVersionDto =
                                    eventsForOneDirectory.State
                                    |> Array.fold
                                        (fun directoryVersionDto directoryEvent -> directoryVersionDto |> DirectoryVersionDto.UpdateDto directoryEvent)
                                        DirectoryVersionDto.Default

                                directoryVersionDtos.Add(directoryVersionDto))

                        if
                            (indexMetrics.Length >= 2)
                            && (requestCharge.Length >= 2)
                            && Activity.Current <> null
                        then
                            Activity.Current
                                .SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
                            |> ignore
                    with ex ->
                        let parameters =
                            queryDefinition.GetQueryParameters()
                            |> Seq.fold (fun (state: StringBuilder) (struct (k, v)) -> state.Append($"{k} = {v}; ")) (StringBuilder())

                        log.LogError(
                            ex,
                            "{CurrentInstant}: Exception in Services.getRootDirectoryBySha256Hash(). QueryText: {queryText}. Parameters: {parameters}",
                            getCurrentInstantExtended (),
                            (queryDefinition.QueryText),
                            parameters.ToString()
                        )
                finally
                    stringBuilderPool.Return(indexMetrics)
                    stringBuilderPool.Return(requestCharge)
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
            let referenceActorProxy = Reference.CreateActorProxy referenceId repositoryId correlationId

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
                            """
                            SELECT c.State
                            FROM c
                                JOIN s IN c.State
                            WHERE STRINGEQUALS(s.Event.created.RepositoryId, @repositoryId, true)
                                AND STRINGEQUALS(s.Event.created.DirectoryId, @directoryId, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            """
                        )
                            .WithParameter("@repositoryId", $"{repositoryId}")
                            .WithParameter("@directoryId", $"{directoryId}")
                            .WithParameter("@grainType", StateName.DirectoryVersion)
                            .WithParameter("@partitionKey", repositoryId)

                    let iterator = cosmosContainer.GetItemQueryIterator<DirectoryVersionEventValue>(queryDefinition, requestOptions = queryRequestOptions)

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
    let getReferencesByReferenceId (repositoryId: RepositoryId) (referenceIds: IEnumerable<ReferenceId>) (maxCount: int) (correlationId: CorrelationId) =
        task {
            let referenceDtos = List<ReferenceDto>()

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let mutable requestCharge = 0.0
                let mutable clientElapsedTime = TimeSpan.Zero
                let queryText = stringBuilderPool.Get()

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
                try
                    // In order to build the IN clause, we need to create a parameter for each referenceId.
                    //   (I tried just using string concatenation, it didn't work for some reason. Anyway...)
                    // The query starts with:
                    queryText.Append(
                        @"SELECT TOP @maxCount c.State
                          FROM c
                              JOIN s IN c.State
                          WHERE c.GrainType = @grainType
                              AND c.PartitionKey = @partitionKey
                              AND s.Event.created.ReferenceId IN ("
                    )
                    |> ignore
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
                            .WithParameter("@grainType", StateName.Reference)
                            .WithParameter("@partitionKey", repositoryId)

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
                        addTiming TimingFlag.BeforeStorageQuery "getReferencesByReferenceId" correlationId
                        let! results = iterator.ReadNextAsync()
                        addTiming TimingFlag.AfterStorageQuery "getReferencesByReferenceId" correlationId
                        requestCharge <- requestCharge + results.RequestCharge
                        clientElapsedTime <- clientElapsedTime + results.Diagnostics.GetClientElapsedTime()
                        let eventsForAllReferences = results.Resource

                        eventsForAllReferences
                        |> Seq.iter (fun eventsForOneReference ->
                            let referenceDto =
                                eventsForOneReference.State
                                |> Array.fold (fun referenceDto referenceEvent -> referenceDto |> ReferenceDto.UpdateDto referenceEvent) ReferenceDto.Default

                            queryResults.Add(referenceDto.ReferenceId, referenceDto))

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
                finally
                    stringBuilderPool.Return(queryText)
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

                let branchIdStack = Queue<ReferenceId>(branchIds)

                while branchIdStack.Count > 0 do
                    let branchId = branchIdStack.Dequeue()

                    let queryDefinition =
                        QueryDefinition(
                            $"""
                            SELECT TOP @maxCount c.State
                            FROM c
                                JOIN s IN c.State
                            WHERE STRINGEQUALS(s.Event.created.RepositoryId, @repositoryId, true)
                                AND STRINGEQUALS(s.Event.created.BranchId, @branchId, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                                {includeDeletedEntitiesClause includeDeleted}
                            """
                        )
                            .WithParameter("@maxCount", maxCount)
                            .WithParameter("@repositoryId", $"{repositoryId}")
                            .WithParameter("@branchId", $"{branchId}")
                            .WithParameter("@grainType", StateName.Branch)
                            .WithParameter("@partitionKey", repositoryId)

                    let iterator = cosmosContainer.GetItemQueryIterator<BranchEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        requestCharge <- requestCharge + results.RequestCharge
                        let eventsForAllBranches = results.Resource

                        eventsForAllBranches
                        |> Seq.iter (fun eventsForOneBranch ->
                            let branchDto =
                                eventsForOneBranch.State
                                |> Array.fold (fun branchDto branchEvent -> branchDto |> BranchDto.UpdateDto branchEvent) BranchDto.Default

                            branchDtos.Add(branchDto))

                Activity.Current
                    .SetTag("referenceDtos.Count", $"{branchDtos.Count}")
                    .SetTag("totalRequestCharge", $"{requestCharge}")
                |> ignore
            | MongoDB -> ()

            return branchDtos.OrderBy(fun branchDto -> branchDto.BranchName).ToArray()
        }

    /// Creates a new reminder actor instance.
    let createReminder (reminderDto: ReminderDto) =
        task {
            let reminderActorProxy = Reminder.CreateActorProxy reminderDto.ReminderId reminderDto.CorrelationId
            do! reminderActorProxy.Create reminderDto reminderDto.CorrelationId
        }
        :> Task

    /// Gets the CorrelationId from an Orleans grain's RequestContext.
    let getCorrelationId () =
        match RequestContext.Get(Constants.CorrelationId) with
        | :? string as s -> s
        | _ -> String.Empty

    /// Gets the ActorName from an Orleans grain's RequestContext.
    let getActorName () =
        match RequestContext.Get(Constants.ActorNameProperty) with
        | :? string as s -> s
        | _ -> String.Empty

    /// Gets the CurrentCommand from an Orleans grain's RequestContext.
    let getCurrentCommand () =
        match RequestContext.Get(Constants.CurrentCommandProperty) with
        | :? string as s -> s
        | _ -> String.Empty

    /// Gets the OrganizationId from an Orleans grain's RequestContext.
    let getOrganizationId () =
        match RequestContext.Get(nameof OrganizationId) with
        | :? OrganizationId as organizationId -> organizationId
        | _ -> Guid.Empty

    /// Gets the RepositoryId from an Orleans grain's RequestContext.
    let getRepositoryId () =
        match RequestContext.Get(nameof RepositoryId) with
        | :? RepositoryId as repositoryId -> repositoryId
        | _ -> Guid.Empty

    /// Gets a message that says whether an actor's state was retrieved from the database.
    let getActorActivationMessage recordExists = if recordExists then "Retrieved from database" else "Not found in database"

    /// Logs the activation of an actor.
    let logActorActivation (log: ILogger) (grainIdentity: string) (activationStartTime: Instant) (message: string) =
        log.LogInformation(
            "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Activated {GrainIdentity}. {message}.",
            getCurrentInstantExtended (),
            getMachineName,
            (getDurationRightAligned_ms activationStartTime),
            getCorrelationId (),
            grainIdentity,
            message
        )
