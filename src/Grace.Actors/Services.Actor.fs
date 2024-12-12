namespace Grace.Actors

open Azure.Storage
open Azure.Storage.Blobs
open Azure.Storage.Sas
open Dapr.Actors
open Dapr.Actors.Client
open Dapr.Client
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Events
open Grace.Actors.Interfaces
open Grace.Actors.Timing
open Grace.Actors.Types
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
open NodaTime
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

module Services =
    type ServerGraceIndex = Dictionary<RelativePath, DirectoryVersion>
    type OwnerIdRecord = { OwnerId: string }
    type OrganizationIdRecord = { organizationId: string }
    type RepositoryIdRecord = { repositoryId: string }
    type BranchIdRecord = { branchId: string }

    /// Dictionary for caching blob container clients
    let containerClients = new ConcurrentDictionary<string, BlobContainerClient>()

    /// Dapr HTTP endpoint retrieved from environment variables
    let daprHttpEndpoint =
        $"{Environment.GetEnvironmentVariable(EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(EnvironmentVariables.DaprHttpPort)}"

    /// Dapr gRPC endpoint retrieved from environment variables
    let daprGrpcEndpoint =
        $"{Environment.GetEnvironmentVariable(EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(EnvironmentVariables.DaprGrpcPort)}"

    /// Dapr client instance
    let daprClient =
        DaprClientBuilder()
            .UseJsonSerializationOptions(JsonSerializerOptions)
            .UseHttpEndpoint(daprHttpEndpoint)
            .UseGrpcEndpoint(daprGrpcEndpoint)
            .Build()

    /// Azure Storage connection string retrieved from environment variables
    let private azureStorageConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariables.AzureStorageConnectionString)

    /// Azure Storage key retrieved from environment variables
    let private azureStorageKey = Environment.GetEnvironmentVariable(EnvironmentVariables.AzureStorageKey)

    /// Shared key credential for Azure Storage
    let private sharedKeyCredential = StorageSharedKeyCredential(DefaultObjectStorageAccount, azureStorageKey)

    /// Logger instance for the Services.Actor module.
    let log = loggerFactory.CreateLogger("Services.Actor")

    /// Cosmos LINQ serializer options
    let linqSerializerOptions = CosmosLinqSerializerOptions(PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase)

    /// Custom QueryRequestOptions that requests Index Metrics only in DEBUG build.
    let queryRequestOptions = QueryRequestOptions()
    //#if DEBUG
    //    queryRequestOptions.PopulateIndexMetrics <- true
    //#endif

    /// Adds the parameters from the API call to a GraceReturnValue instance.
    let addParametersToGraceReturnValue<'T> parameters (graceReturnValue: GraceReturnValue<'T>) =
        (getParametersAsDictionary parameters)
        |> Seq.iter (fun kvp -> graceReturnValue.enhance (kvp.Key, kvp.Value) |> ignore)

        graceReturnValue

    /// Adds the parameters from the API call to a GraceError instance.
    let addParametersToGraceError parameters (graceError: GraceError) =
        (getParametersAsDictionary parameters)
        |> Seq.iter (fun kvp -> graceError.enhance (kvp.Key, kvp.Value) |> ignore)

        graceError

    /// Gets a CosmosDB container client for the given container.
    let getContainerClient (repositoryDto: RepositoryDto) =
        task {
            let containerName = $"{repositoryDto.RepositoryId}"
            let key = $"Con:{repositoryDto.StorageAccountName}-{containerName}"

            let! blobContainerClient =
                memoryCache.GetOrCreateAsync(
                    key,
                    fun cacheEntry ->
                        task {
                            // This can and should last in cache for longer than Grace's default expiration time.
                            // StorageAccountNames and container names are stable.
                            // However, we don't want to clog up memoryCache for too long with each client.
                            // Not sure what the right balance is, but 10 minutes seems reasonable.
                            cacheEntry.AbsoluteExpiration <- DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(10.0))

                            let blobContainerClient = BlobContainerClient(azureStorageConnectionString, containerName)

                            // Make sure the container exists before returning the client.
                            let metadata = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) :> IDictionary<string, string>
                            metadata[nameof (OwnerId)] <- $"{repositoryDto.OwnerId}"
                            metadata[nameof (OrganizationId)] <- $"{repositoryDto.OrganizationId}"
                            metadata[nameof (RepositoryId)] <- $"{repositoryDto.RepositoryId}"

                            let! azureResponse =
                                blobContainerClient.CreateIfNotExistsAsync(publicAccessType = Models.PublicAccessType.None, metadata = metadata)

                            return blobContainerClient
                        }
                )

            return blobContainerClient
        }

    /// Gets an Azure Blob Storage client instance for the given repository and file version.
    let getAzureBlobClient (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (correlationId: CorrelationId) =
        task {
            //logToConsole $"* In getAzureBlobClient; repositoryId: {repositoryDto.RepositoryId}; fileVersion: {fileVersion.RelativePath}."
            let! containerClient = getContainerClient repositoryDto

            let blobClient = containerClient.GetBlobClient($"{fileVersion.RelativePath}/{fileVersion.GetObjectFileName}")

            return Ok blobClient
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
            let! blobContainerClient = getContainerClient repositoryDto

            let blobSasBuilder =
                BlobSasBuilder(
                    permissions = permission,
                    expiresOn = DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(Constants.SharedAccessSignatureExpiration)),
                    StartsOn = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(15.0)),
                    BlobContainerName = blobContainerClient.Name,
                    BlobName = Path.Combine($"{fileVersion.RelativePath}", fileVersion.GetObjectFileName),
                    CorrelationId = correlationId
                )

            let sasUriParameters = blobSasBuilder.ToSasQueryParameters(sharedKeyCredential)

            return Ok $"{blobContainerClient.Uri}/{fileVersion.RelativePath}/{fileVersion.GetObjectFileName}?{sasUriParameters}"
        }

    /// Gets a shared access signature for reading from the object storage provider.
    let getReadSharedAccessSignature (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (correlationId: CorrelationId) =
        task {
            match repositoryDto.ObjectStorageProvider with
            | AzureBlobStorage ->
                let permissions = (BlobSasPermissions.Read ||| BlobSasPermissions.List) // These are the minimum permissions needed to read a file.
                let! sas = createAzureBlobSasUri repositoryDto fileVersion permissions correlationId

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
                    let queryDefinition =
                        QueryDefinition(
                            """SELECT c["value"].OwnerId
                            FROM c
                            WHERE c["value"].Class = @stateStorageName
                                AND STRINGEQUALS(c["value"].OwnerName, @ownerName, true)"""
                        )
                            .WithParameter("@ownerName", ownerName)
                            .WithParameter("@stateStorageName", StateName.OwnerDto)

                    let iterator = DefaultRetryPolicy.Execute(fun () -> cosmosContainer.GetItemQueryIterator<OwnerIdRecord>(queryDefinition))
                    let mutable ownerGuid = OwnerId.Empty

                    if iterator.HasMoreResults then
                        let! currentResultSet = iterator.ReadNextAsync()
                        currentResultSet |> Seq.iter (fun owner -> logToConsole $"Owner: {owner}")
                        let ownerId = currentResultSet.FirstOrDefault({ OwnerId = String.Empty }).OwnerId

                        if String.IsNullOrEmpty(ownerId) && cacheResultIfNotFound then
                            logToConsole $"Did not find ownerId using OwnerName {ownerName}. cacheResultIfNotFound: {cacheResultIfNotFound}."
                            // We didn't find the OwnerId, so add this OwnerName to the MemoryCache and indicate that we have already checked.
                            //use newCacheEntry =
                            //    memoryCache.CreateEntry(
                            //        $"OwN:{ownerName}",
                            //        Value = MemoryCache.EntityDoesNotExist,
                            //        AbsoluteExpirationRelativeToNow = MemoryCache.DefaultExpirationTime
                            //    )

                            return false
                        elif String.IsNullOrEmpty(ownerId) then
                            // We didn't find the OwnerId, so return false.
                            return false
                        else
                            // Add this OwnerName and OwnerId to the MemoryCache.
                            ownerGuid <- Guid.Parse(ownerId)
                            memoryCache.CreateOwnerNameEntry ownerName ownerGuid
                            memoryCache.CreateOwnerIdEntry ownerGuid MemoryCache.ExistsValue

                            // Set the OwnerId in the OwnerName actor.
                            do! ownerNameActorProxy.SetOwnerId ownerGuid correlationId
                            return true
                    else
                        return false
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
    let resolveOrganizationId (ownerId: string) (ownerName: string) (organizationId: string) (organizationName: string) (correlationId: CorrelationId) =
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
                    let ownerGuid = Guid.Parse(ownerId)
                    let organizationNameActorProxy = OrganizationName.CreateActorProxy ownerGuid organizationName correlationId

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
                                    memoryCache.CreateOrganizationNameEntry organizationName MemoryCache.EntityDoesNotExistGuid
                                    return None
                                else
                                    // Add this OrganizationName and OrganizationId to the MemoryCache.
                                    organizationGuid <- Guid.Parse(organizationId)
                                    memoryCache.CreateOrganizationNameEntry organizationName organizationGuid
                                    memoryCache.CreateOrganizationIdEntry organizationGuid MemoryCache.ExistsValue

                                    do! organizationNameActorProxy.SetOrganizationId organizationId correlationId
                                    return Some organizationId
                            else
                                return None
                        | MongoDB -> return None
        }

    /// Checks whether a repository has been deleted by querying the actor, and updates the MemoryCache with the result.
    let repositoryIsDeleted (repositoryId: string) correlationId =
        task {
            // Call the Repository actor to check if the repository is deleted.
            let repositoryGuid = Guid.Parse(repositoryId)
            let repositoryActorProxy = Repository.CreateActorProxy repositoryGuid correlationId

            let! isDeleted = repositoryActorProxy.IsDeleted correlationId

            if isDeleted then
                memoryCache.CreateDeletedRepositoryIdEntry repositoryGuid MemoryCache.DoesNotExistValue
                return Some repositoryId
            else
                memoryCache.CreateDeletedRepositoryIdEntry repositoryGuid MemoryCache.ExistsValue
                return None
        }

    /// Checks whether a repository exists by querying the actor, and updates the MemoryCache with the result.
    let repositoryExists (repositoryId: string) correlationId =
        task {
            // Call the Repository actor to check if the repository exists.
            let repositoryGuid = Guid.Parse(repositoryId)
            let repositoryActorProxy = Repository.CreateActorProxy repositoryGuid correlationId

            let! exists = repositoryActorProxy.Exists correlationId

            if exists then
                // Add this RepositoryId to the MemoryCache.
                memoryCache.CreateRepositoryIdEntry repositoryGuid MemoryCache.ExistsValue
                return Some repositoryId
            else
                return None
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

            if
                not <| String.IsNullOrEmpty(repositoryId)
                && Guid.TryParse(repositoryId, &repositoryGuid)
            then
                match memoryCache.GetRepositoryIdEntry repositoryGuid with
                | Some value ->
                    match value with
                    | MemoryCache.ExistsValue -> return Some repositoryId
                    | MemoryCache.DoesNotExistValue -> return None
                    | _ -> return! repositoryExists repositoryId correlationId
                | None -> return! repositoryExists repositoryId correlationId
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
                        return Some $"{repositoryGuid}"
                | None ->
                    // Check if we have an active RepositoryName actor with a cached result.
                    let ownerGuid = Guid.Parse(ownerId)
                    let organizationGuid = Guid.Parse(organizationId)
                    let repositoryNameActorProxy = RepositoryName.CreateActorProxy ownerGuid organizationGuid repositoryName correlationId

                    match! repositoryNameActorProxy.GetRepositoryId correlationId with
                    | Some repositoryId ->
                        repositoryGuid <- Guid.Parse(repositoryId)
                        memoryCache.CreateRepositoryNameEntry repositoryName repositoryGuid
                        memoryCache.CreateRepositoryIdEntry repositoryGuid MemoryCache.ExistsValue
                        return Some repositoryId
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
                                    memoryCache.CreateRepositoryNameEntry repositoryName MemoryCache.EntityDoesNotExistGuid
                                    return None
                                else
                                    // Add this RepositoryName and RepositoryId to the MemoryCache.
                                    repositoryGuid <- Guid.Parse(repositoryId)
                                    memoryCache.CreateRepositoryNameEntry repositoryName repositoryGuid
                                    memoryCache.CreateRepositoryIdEntry repositoryGuid MemoryCache.ExistsValue

                                    // Set the RepositoryId in the RepositoryName actor.
                                    do! repositoryNameActorProxy.SetRepositoryId repositoryId correlationId
                                    return Some repositoryId
                            else
                                return None
                        | MongoDB -> return None
        }

    /// Checks whether a branch has been deleted by querying the actor, and updates the MemoryCache with the result.
    let branchIsDeleted (branchId: string) correlationId =
        task {
            let branchGuid = Guid.Parse(branchId)
            let branchActorProxy = Branch.CreateActorProxy branchGuid correlationId

            let! isDeleted = branchActorProxy.IsDeleted correlationId

            if isDeleted then
                memoryCache.CreateDeletedBranchIdEntry branchGuid MemoryCache.DoesNotExistValue
                return Some branchId
            else
                memoryCache.CreateDeletedBranchIdEntry branchGuid MemoryCache.ExistsValue
                return None
        }

    /// Checks whether a branch exists by querying the actor, and updates the MemoryCache with the result.
    let branchExists (branchId: string) correlationId =
        task {
            // Call the Branch actor to check if the branch exists.
            let branchGuid = Guid.Parse(branchId)
            let branchActorProxy = Branch.CreateActorProxy branchGuid correlationId
            let! exists = branchActorProxy.Exists correlationId

            if exists then
                // Add this BranchId to the MemoryCache.
                memoryCache.CreateBranchIdEntry (BranchId.Parse(branchId)) MemoryCache.ExistsValue
                return Some branchId
            else
                return None
        }

    /// Gets the BranchId by returning BranchId if provided, or searching by BranchName within the provided repository.
    let resolveBranchId (repositoryId: string) branchId branchName (correlationId: CorrelationId) =
        task {
            let mutable branchGuid = Guid.Empty

            if not <| String.IsNullOrEmpty(branchId) && Guid.TryParse(branchId, &branchGuid) then
                match memoryCache.GetBranchIdEntry branchGuid with
                | Some value ->
                    match value with
                    | MemoryCache.ExistsValue -> return Some branchId
                    | MemoryCache.DoesNotExistValue -> return None
                    | _ -> return! branchExists branchId correlationId
                | None -> return! branchExists branchId correlationId
            elif String.IsNullOrEmpty(branchName) then
                // We don't have a BranchId or BranchName, so we can't resolve the BranchId.
                return None
            else
                // Check if we have an active BranchName actor with a cached result.
                match memoryCache.GetBranchNameEntry branchName with
                | Some branchGuid ->
                    if branchGuid.Equals(Constants.MemoryCache.EntityDoesNotExist) then
                        // We have already checked and the branch does not exist.
                        return None
                    else
                        // We have already checked and the branch exists.
                        return Some $"{branchGuid}"
                | None ->
                    // Check if we have an active BranchName actor with a cached result.
                    let repositoryGuid = Guid.Parse(repositoryId)
                    let branchNameActorProxy = BranchName.CreateActorProxy repositoryGuid branchName correlationId

                    match! branchNameActorProxy.GetBranchId correlationId with
                    | Some branchId ->
                        // Add this BranchName and BranchId to the MemoryCache.
                        memoryCache.CreateBranchNameEntry branchName branchId
                        memoryCache.CreateBranchIdEntry branchId MemoryCache.ExistsValue
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

                                    // Add this BranchName and BranchId to the MemoryCache.
                                    memoryCache.CreateBranchNameEntry branchName branchGuid
                                    memoryCache.CreateBranchIdEntry branchGuid MemoryCache.ExistsValue

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
                    logToConsole $"{ExceptionResponse.Create ex}"
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
                    let queryDefinition =
                        QueryDefinition(
                            """SELECT c["value"].RepositoryId FROM c WHERE c["value"].OwnerId = @ownerId AND c["value"].OrganizationId = @organizationId AND c["value"].RepositoryName = @repositoryName AND c["value"].Class = @class"""
                        )
                            .WithParameter("@ownerId", ownerId)
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
                with ex ->
                    return Error $"{ExceptionResponse.Create ex}"
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
                    logToConsole $"{ExceptionResponse.Create ex}"
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
                    let requestCharge2 = StringBuilder()

                    let includeDeletedClause =
                        if includeDeleted then
                            String.Empty
                        else
                            """ AND (
                                (SELECT VALUE COUNT(1)
                                 FROM c JOIN subEvent IN c["value"]
                                 WHERE IS_DEFINED(subEvent.Event.logicalDeleted)) =
                                (SELECT VALUE COUNT(1)
                                 FROM c JOIN subEvent IN c["value"]
                                 WHERE IS_DEFINED(subEvent.Event.undeleted))
                            ) """

                    let queryDefinition =
                        QueryDefinition(
                            $"""SELECT TOP @maxCount event.Event.created.branchId
                                FROM c JOIN event IN c["value"] 
                                WHERE event.Event.created.repositoryId = @repositoryId
                                    AND LENGTH(event.Event.created.branchName) > 0 {includeDeletedClause}"""
                        )
                            .WithParameter("@maxCount", maxCount)
                            .WithParameter("@repositoryId", $"{repositoryId}")

                    let iterator = cosmosContainer.GetItemQueryIterator<BranchIdValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        addTiming TimingFlag.BeforeStorageQuery "getBranches" correlationId
                        let! results = iterator.ReadNextAsync()
                        addTiming TimingFlag.AfterStorageQuery "getBranches" correlationId
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
                                        let branchActorProxy = Branch.CreateActorProxy branchId correlationId
                                        let! branchDto = branchActorProxy.Get correlationId
                                        branches.Add(branchDto)
                                    }
                                ))
                        )
                with ex ->
                    logToConsole $"Got an exception."
                    logToConsole $"{ExceptionResponse.Create ex}"
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
    let getReferences (branchId: BranchId) (maxCount: int) (correlationId: CorrelationId) =
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
                    addTiming TimingFlag.BeforeStorageQuery "getReferences" correlationId
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

                let itemRequestOptions =
                    ItemRequestOptions(AddRequestHeaders = fun headers -> headers.Add(Constants.CorrelationIdHeaderKey, "Deleting all records from CosmosDB"))

                let queryDefinition = QueryDefinition("""SELECT c.id, c.partitionKey FROM c ORDER BY c.partitionKey""")
                //let queryDefinition = QueryDefinition("""SELECT c.id, c.partitionKey FROM c WHERE ENDSWITH(c.id, "||Rmd") ORDER BY c.partitionKey""")

                let mutable totalRecordsDeleted = 0
                let overallStartTime = getCurrentInstant ()
                queryRequestOptions.MaxItemCount <- 1000

                logToConsole
                    $"cosmosContainer.Id: {cosmosContainer.Id}; cosmosContainer.Database.Id: {cosmosContainer.Database.Id}; cosmosContainer.Database.Client.Endpoint: {cosmosContainer.Database.Client.Endpoint}."

                let iterator = cosmosContainer.GetItemQueryIterator<DocumentIdentifier>(queryDefinition, requestOptions = queryRequestOptions)

                while iterator.HasMoreResults do
                    let batchStartTime = getCurrentInstant ()
                    let! batchResults = iterator.ReadNextAsync()
                    let mutable totalRequestCharge = 0L

                    logToConsole $"In Services.deleteAllFromCosmosDB(): Current batch size: {batchResults.Resource.Count()}."

                    do!
                        Parallel.ForEachAsync(
                            batchResults,
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
                failed.Add((ExceptionResponse.Create ex).``exception``)
                return failed
#else
            return List<string>([ "Not implemented" ])
#endif
        }

    /// Gets a list of references of a given ReferenceType for a branch.
    let getReferencesByType (referenceType: ReferenceType) (branchId: BranchId) (maxCount: int) (correlationId: CorrelationId) =
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
                    addTiming TimingFlag.BeforeStorageQuery "getReferencesByType" correlationId
                    let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                    addTiming TimingFlag.AfterStorageQuery "getReferencesByType" correlationId
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

                do!
                    Parallel.ForEachAsync(
                        referenceTypes,
                        Constants.ParallelOptions,
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

                                    let iterator =
                                        cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

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
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Exception in Services.getDirectoryBySha256Hash(). QueryDefinition: {queryDefinition}",
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

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Exception in Services.getRootDirectoryBySha256Hash(). QueryText: {queryText}. Parameters: {parameters}",
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
            let referenceActorProxy = Reference.CreateActorProxy referenceId correlationId

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
    let getReferencesByReferenceId (referenceIds: IEnumerable<ReferenceId>) (maxCount: int) (correlationId: CorrelationId) =
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
                    addTiming TimingFlag.BeforeStorageQuery "getReferencesByReferenceId" correlationId
                    let! results = iterator.ReadNextAsync()
                    addTiming TimingFlag.AfterStorageQuery "getReferencesByReferenceId" correlationId
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

    /// Gets a message that says whether an actor's state was retrieved from the database.
    let getActorActivationMessage retrievedItem =
        match retrievedItem with
        | Some item -> "Retrieved from database"
        | None -> "Not found in database"

    /// Logs the activation of an actor.
    let logActorActivation (log: ILogger) activateStartTime (correlationId: string) (actorName: string) (actorId: ActorId) (message: string) =
        log.LogInformation(
            "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Activated {ActorType} {ActorId}. {message}.",
            getCurrentInstantExtended (),
            getMachineName,
            (getPaddedDuration_ms activateStartTime),
            correlationId,
            actorName,
            actorId,
            message
        )

    /// Creates a new reminder actor instance.
    let createReminder (reminder: ReminderDto) =
        task {
            let reminderActorProxy = Reminder.CreateActorProxy reminder.ReminderId reminder.CorrelationId

            do! reminderActorProxy.Create reminder reminder.CorrelationId
        }
        :> Task
