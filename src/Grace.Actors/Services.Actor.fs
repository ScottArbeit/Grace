namespace Grace.Actors

open Azure.Core
open Azure.Identity
open Azure.Messaging.ServiceBus
open Azure.Storage
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Specialized
open Azure.Storage.Sas
open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Extensions.MemoryCache
open Grace.Actors.Interfaces
open Grace.Actors.Timing
open Grace.Actors.Types
open Grace.Shared
open Grace.Shared.AzureEnvironment
open Grace.Shared.Constants
open Grace.Types.Branch
open Grace.Types.DirectoryVersion
open Grace.Types.Events
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
open System.Text.Json
open System.Threading.Tasks
open System.Threading
open System
open Microsoft.Extensions.DependencyInjection
open System.Runtime.Serialization
open System.Reflection
open System.Text.RegularExpressions
open Microsoft.Azure.Amqp
open Azure.Core.Amqp

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

    type ReminderValue() =
        member val public Reminder: ReminderDto = ReminderDto.Default with get, set

    /// Dictionary for caching blob container clients
    let containerClients = new ConcurrentDictionary<string, BlobContainerClient>()

    /// Azure Storage key retrieved from environment variables
    let private azureStorageKey = Environment.GetEnvironmentVariable(EnvironmentVariables.AzureStorageKey)

    /// Shared key credential for Azure Storage
    let private sharedKeyCredential = lazy (StorageSharedKeyCredential(DefaultObjectStorageAccount, azureStorageKey))

    /// Logger instance for the Services.Actor module.
    let private log = loggerFactory.CreateLogger("Services.Actor")

    let private defaultAzureCredential = lazy (DefaultAzureCredential())

    let private serviceBusClient =
        lazy
            let settings = pubSubSettings.AzureServiceBus.Value

            if settings.UseManagedIdentity then
                let fullyQualifiedNamespace =
                    if not (String.IsNullOrWhiteSpace settings.FullyQualifiedNamespace) then
                        settings.FullyQualifiedNamespace
                    else
                        AzureEnvironment.tryGetServiceBusFullyQualifiedNamespace ()
                        |> Option.defaultWith (fun () -> invalidOp "Azure Service Bus namespace is required for managed identity.")

                logToConsole $"Creating ServiceBusClient with Managed Identity for namespace: {fullyQualifiedNamespace}."
                ServiceBusClient(fullyQualifiedNamespace, defaultAzureCredential.Value)
            else
                logToConsole "Creating ServiceBusClient with connection string."
                ServiceBusClient(settings.ConnectionString)

    let private serviceBusSender = lazy (serviceBusClient.Value.CreateSender(pubSubSettings.AzureServiceBus.Value.TopicName))

    /// Publishes a GraceEvent to the configured pub-sub system.
    let publishGraceEvent (graceEvent: GraceEvent) (metadata: EventMetadata) =
        task {
            match pubSubSettings.System with
            | GracePubSubSystem.AzureServiceBus ->
                match pubSubSettings.AzureServiceBus with
                | Some sbSettings ->
                    try
                        let payload = JsonSerializer.SerializeToUtf8Bytes(graceEvent, Constants.JsonSerializerOptions)
                        let message = ServiceBusMessage(payload)
                        message.ContentType <- "application/json"
                        message.Subject <- "GraceEvent"
                        message.CorrelationId <- metadata.CorrelationId
                        message.MessageId <- $"{metadata.CorrelationId}-{getCurrentInstant().ToUnixTimeMilliseconds}" //Guid.NewGuid().ToString("N")
                        message.ApplicationProperties["graceEventType"] <- getDiscriminatedUnionFullName graceEvent

                        for kvp in metadata.Properties do
                            message.ApplicationProperties[kvp.Key] <- kvp.Value

                        do! serviceBusSender.Value.SendMessageAsync(message)

                        log.LogInformation(
                            "{CurrentInstant}: Published GraceEvent via Azure Service Bus. CorrelationId: {CorrelationId}; EventType: {EventType}.",
                            getCurrentInstantExtended (),
                            metadata.CorrelationId,
                            getDiscriminatedUnionCaseName graceEvent
                        )
                    with ex ->
                        log.LogError(
                            ex,
                            "{CurrentInstant}: Failed publishing GraceEvent via Azure Service Bus. CorrelationId: {CorrelationId}; EventType: {EventType}.",
                            getCurrentInstantExtended (),
                            metadata.CorrelationId,
                            getDiscriminatedUnionCaseName graceEvent
                        )
                | None ->
                    log.LogWarning(
                        "Azure Service Bus selected but settings were not provided; dropping GraceEvent {EventType}.",
                        getDiscriminatedUnionCaseName graceEvent
                    )
            | GracePubSubSystem.UnknownPubSubProvider ->
                log.LogDebug(
                    "Pub-sub system disabled; dropping GraceEvent {EventType} with CorrelationId: {CorrelationId}.",
                    getDiscriminatedUnionCaseName graceEvent,
                    metadata.CorrelationId
                )
            | otherSystem ->
                log.LogWarning(
                    "Grace pub-sub system {System} not yet implemented; dropping GraceEvent {EventType} with CorrelationId: {CorrelationId}.",
                    getDiscriminatedUnionCaseName otherSystem,
                    getDiscriminatedUnionCaseName graceEvent,
                    metadata.CorrelationId
                )
        }
        :> Task

    /// Prints a Cosmos DB QueryDefinition with parameters replaced for easier debugging.
    let printQueryDefinition (queryDefinition: QueryDefinition) =
        let sb = stringBuilderPool.Get()

        try
            sb.Append(queryDefinition.QueryText) |> ignore

            queryDefinition.GetQueryParameters()
            |> Seq.iter (fun struct (name, value: obj) ->
                match value with
                | :? int64 as intValue -> sb.Replace(name, $"{intValue}")
                | :? int as intValue -> sb.Replace(name, $"{intValue}")
                | :? double as doubleValue -> sb.Replace(name, $"{doubleValue}")
                | :? bool as boolValue -> sb.Replace(name, $"{boolValue.ToString().ToLower()}")
                | :? single as floatValue -> sb.Replace(name, $"{floatValue}")
                | :? Guid as guidValue -> sb.Replace(name, $"\"{guidValue}\"")
                | _ -> sb.Replace(name, $"\"{value}\"")
                |> ignore)

            // Replaces any leading spaces or tabs at the start of each line with four spaces (multiline) and then trims leading/trailing whitespace from the entire multi-line string.
            let trimmedSql = Regex.Replace(sb.ToString(), @"(?m)^[ \t]+", "    ").Trim()
            trimmedSql
        finally
            stringBuilderPool.Return sb

    /// Custom QueryRequestOptions that requests Index Metrics only in DEBUG build.
    let queryRequestOptions = QueryRequestOptions()
#if DEBUG
    queryRequestOptions.PopulateIndexMetrics <- true
#endif

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
                            let blobContainerClient = Context.blobServiceClient.GetBlobContainerClient(containerName)
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

            return containerClient.GetBlockBlobClient blobName
        }

    let getAzureBlobClientForFileVersion (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (correlationId: CorrelationId) =
        task {
            let blobName = $"{fileVersion.RelativePath}/{fileVersion.GetObjectFileName}"
            return! getAzureBlobClient repositoryDto blobName correlationId
        }

    /// Creates a full URI for a specific file version.
    let private createAzureBlobSasUri (repositoryDto: RepositoryDto) (blobName: string) (permission: BlobSasPermissions) (correlationId: CorrelationId) =
        task {
            let! blobContainerClient = getContainerClient repositoryDto correlationId

            let blobSasBuilder =
                BlobSasBuilder(
                    permissions = permission,
                    expiresOn = DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(SharedAccessSignatureExpiration)),
                    StartsOn = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(15.0)),
                    BlobContainerName = blobContainerClient.Name,
                    BlobName = blobName
                )

            let! sasUriParameters =
                if AzureEnvironment.useManagedIdentity then
                    task {
                        // For managed identity, we need to get a user delegation key first
                        let blobServiceClient = blobContainerClient.GetParentBlobServiceClient()

                        let! userDelegationKey =
                            blobServiceClient.GetUserDelegationKeyAsync(
                                DateTimeOffset.UtcNow,
                                DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(SharedAccessSignatureExpiration))
                            )

                        let sasQueryParameters = blobSasBuilder.ToSasQueryParameters(userDelegationKey.Value, blobServiceClient.AccountName)

                        return sasQueryParameters
                    }
                else
                    task { return blobSasBuilder.ToSasQueryParameters(sharedKeyCredential.Value) }

            return UriWithSharedAccessSignature($"{blobContainerClient.Uri}/{blobName}?{sasUriParameters}")
        }

    let azureBlobReadPermissions = (BlobSasPermissions.Read ||| BlobSasPermissions.List) // These are the minimum permissions needed to read a file.

    /// Gets a full Uri, including shared access signature, for reading from the object storage provider.
    let getUriWithReadSharedAccessSignature (repositoryDto: RepositoryDto) (blobName: string) (correlationId: CorrelationId) =
        task {
            match repositoryDto.ObjectStorageProvider with
            | AzureBlobStorage ->
                let! sas = createAzureBlobSasUri repositoryDto blobName azureBlobReadPermissions correlationId
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

    /// The permissions we need to create, write, or tag blobs. Includes read permission to allow for calls to .ExistsAsync().
    let azureBlobWritePermissions =
        (BlobSasPermissions.Create
         ||| BlobSasPermissions.Write
         ||| BlobSasPermissions.Tag
         ||| BlobSasPermissions.Read)

    /// Gets a full Uri, including shared access signature, for writing from the object storage provider.
    let getUriWithWriteSharedAccessSignature (repositoryDto: RepositoryDto) (blobName: string) (correlationId: CorrelationId) =
        task {
            match repositoryDto.ObjectStorageProvider with
            | AWSS3 -> return UriWithSharedAccessSignature(String.Empty)
            | AzureBlobStorage ->
                let! sas = createAzureBlobSasUri repositoryDto blobName azureBlobWritePermissions correlationId
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
                            WHERE (STRINGEQUALS(c.State[0].Event.created.ownerName, @ownerName, true)
                                OR STRINGEQUALS(c.State[0].Event.setName.ownerName, @ownerName, true))
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
                        memoryCache.CreateOwnerIdEntry ownerWithName.OwnerId MemoryCache.Exists

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
                memoryCache.CreateDeletedOwnerIdEntry ownerGuid MemoryCache.DoesNotExist
                return Some ownerId
            else
                memoryCache.CreateDeletedOwnerIdEntry ownerGuid MemoryCache.Exists
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
                memoryCache.CreateOwnerIdEntry ownerGuid MemoryCache.Exists
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
                    | MemoryCache.Exists -> return Some ownerId
                    | MemoryCache.DoesNotExist -> return None
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
                    memoryCache.CreateOwnerIdEntry ownerGuid MemoryCache.Exists
                    return Some $"{ownerGuid}"
                | None ->
                    // Check if we have an active OwnerName actor with a cached result.
                    let ownerNameActorProxy = OwnerName.CreateActorProxy ownerName correlationId

                    match! ownerNameActorProxy.GetOwnerId correlationId with
                    | Some ownerId ->
                        // Add this OwnerName and OwnerId to the MemoryCache.
                        memoryCache.CreateOwnerNameEntry ownerName ownerId
                        memoryCache.CreateOwnerIdEntry ownerGuid MemoryCache.Exists

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
                memoryCache.CreateDeletedOrganizationIdEntry organizationGuid MemoryCache.DoesNotExist
                return Some organizationId
            else
                memoryCache.CreateDeletedOrganizationIdEntry organizationGuid MemoryCache.Exists
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
                memoryCache.CreateOrganizationIdEntry (OrganizationId.Parse(organizationId)) MemoryCache.Exists
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
                    | MemoryCache.Exists -> return Some organizationId
                    | MemoryCache.DoesNotExist -> return None
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
                        memoryCache.CreateOrganizationIdEntry organizationGuid MemoryCache.Exists
                        return Some $"{organizationGuid}"
                | None ->
                    // Check if we have an active OrganizationName actor with a cached result.
                    let organizationNameActorProxy = OrganizationName.CreateActorProxy ownerId organizationName correlationId

                    match! organizationNameActorProxy.GetOrganizationId correlationId with
                    | Some organizationId ->
                        // Add this OrganizationName and OrganizationId to the MemoryCache.
                        memoryCache.CreateOrganizationNameEntry organizationName organizationId
                        memoryCache.CreateOrganizationIdEntry organizationId MemoryCache.Exists
                        return Some $"{organizationId}"
                    | None ->
                        // We have to call into Actor storage to get the OrganizationId.
                        match actorStateStorageProvider with
                        | Unknown -> return None
                        | AzureCosmosDb ->
                            let queryDefinition =
                                QueryDefinition(
                                    """
                                    SELECT c.State[0].Event.created.organizationId AS OrganizationId
                                    FROM c
                                    WHERE STRINGEQUALS(c.State[0].Event.created.organizationName, @organizationName, true)
                                        AND STRINGEQUALS(c.State[0].Event.created.ownerId, @ownerId, true)
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

                                let organizationId = currentResultSet.FirstOrDefault({ organizationId = String.Empty }).organizationId

                                if String.IsNullOrEmpty(organizationId) then
                                    // We didn't find the OrganizationId, so add this OrganizationName to the MemoryCache and indicate that we have already checked.
                                    memoryCache.CreateOrganizationNameEntry organizationName MemoryCache.EntityDoesNotExistGuid
                                    return None
                                else
                                    // Add this OrganizationName and OrganizationId to the MemoryCache.
                                    organizationGuid <- Guid.Parse(organizationId)
                                    memoryCache.CreateOrganizationNameEntry organizationName organizationGuid
                                    memoryCache.CreateOrganizationIdEntry organizationGuid MemoryCache.Exists

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
                memoryCache.CreateDeletedRepositoryIdEntry repositoryGuid MemoryCache.DoesNotExist
                return Some repositoryId
            else
                memoryCache.CreateDeletedRepositoryIdEntry repositoryGuid MemoryCache.Exists
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
                memoryCache.CreateRepositoryIdEntry repositoryGuid MemoryCache.Exists
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
                    | MemoryCache.Exists -> return Some repositoryGuid
                    | MemoryCache.DoesNotExist -> return None
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
                        memoryCache.CreateRepositoryIdEntry repositoryGuid MemoryCache.Exists
                        return Some repositoryGuid
                | None ->
                    // Check if we have an active RepositoryName actor with a cached result.
                    let repositoryNameActorProxy = RepositoryName.CreateActorProxy ownerId organizationId repositoryName correlationId

                    match! repositoryNameActorProxy.GetRepositoryId correlationId with
                    | Some repositoryId ->
                        memoryCache.CreateRepositoryNameEntry repositoryName repositoryId
                        memoryCache.CreateRepositoryIdEntry repositoryId MemoryCache.Exists
                        return Some repositoryId
                    | None ->
                        // We have to call into Actor storage to get the RepositoryId.
                        match actorStateStorageProvider with
                        | Unknown -> return None
                        | AzureCosmosDb ->
                            let queryDefinition =
                                QueryDefinition(
                                    """
                                    SELECT c.State[0].Event.created.repositoryId AS RepositoryId
                                    FROM c
                                    WHERE STRINGEQUALS(c.State[0].Event.created.repositoryName, @repositoryName, true)
                                        AND STRINGEQUALS(c.State[0].Event.created.organizationId, @organizationId, true)
                                        AND STRINGEQUALS(c.State[0].Event.created.ownerId, @ownerId, true)
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
                                    memoryCache.CreateRepositoryIdEntry repositoryId MemoryCache.Exists

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
                memoryCache.CreateDeletedBranchIdEntry branchGuid MemoryCache.DoesNotExist
                return Some branchId
            else
                memoryCache.CreateDeletedBranchIdEntry branchGuid MemoryCache.Exists
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
                memoryCache.CreateBranchIdEntry branchId MemoryCache.Exists
                return Some branchId
            else
                return None
        }

    /// Gets the BranchId by returning BranchId if provided, or searching by BranchName within the provided repository.
    let resolveBranchId ownerId organizationId (repositoryId: RepositoryId) branchIdString branchName (correlationId: CorrelationId) =
        task {
            let mutable branchGuid = Guid.Empty

            if
                not <| String.IsNullOrEmpty(branchIdString)
                && Guid.TryParse(branchIdString, &branchGuid)
            then
                // We have a BranchId, so check if it exists.
                match memoryCache.GetBranchIdEntry branchGuid with
                | Some value ->
                    match value with
                    | MemoryCache.Exists -> return Some branchGuid
                    | MemoryCache.DoesNotExist -> return None
                    | _ -> return! branchExists branchGuid repositoryId correlationId
                | None -> return! branchExists branchGuid repositoryId correlationId
            elif String.IsNullOrEmpty(branchName) then
                // We don't have a BranchId or BranchName, so we can't resolve the BranchId.
                return None
            else
                // We have no BranchId, but we do have a BranchName.
                // Check if we have an active BranchName actor with a cached result.
                match memoryCache.GetBranchNameEntry(repositoryId, branchName) with
                | Some branchGuid ->
                    // We have a cached result.
                    if branchGuid.Equals(Constants.MemoryCache.EntityDoesNotExist) then
                        // We have already checked and the branch does not exist.
                        return None
                    else
                        // We have already checked and the branch exists.
                        return Some branchGuid
                | None ->
                    // The BranchName was not in the MemoryCache on this node, but we may have it in a BranchName actor.
                    let branchNameActorProxy = BranchName.CreateActorProxy repositoryId branchName correlationId

                    match! branchNameActorProxy.GetBranchId correlationId with
                    | Some branchId ->
                        // We have an active BranchName actor with the BranchId cached.
                        memoryCache.CreateBranchNameEntry(repositoryId, branchName, branchId)
                        memoryCache.CreateBranchIdEntry branchId MemoryCache.Exists
                        return Some branchId
                    | None ->
                        // The BranchName actor was not active, so we have to search the database.
                        match actorStateStorageProvider with
                        | Unknown -> return None
                        | AzureCosmosDb ->
                            let queryDefinition =
                                QueryDefinition(
                                    """
                                    SELECT c.State[0].Event.created.branchId AS BranchId
                                    FROM c
                                    WHERE STRINGEQUALS(c.State[0].Event.created.branchName, @branchName, true)
                                        AND STRINGEQUALS(c.State[0].Event.created.organizationId, @organizationId, true)
                                        AND STRINGEQUALS(c.State[0].Event.created.ownerId, @ownerId, true)
                                        AND c.GrainType = @grainType
                                        AND c.PartitionKey = @partitionKey
                                    """
                                )
                                    .WithParameter("@branchName", branchName)
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
                                    return None
                                else
                                    // Add this BranchName and BranchId to the MemoryCache.
                                    branchGuid <- Guid.Parse(branchId)

                                    // Add this BranchName and BranchId to the MemoryCache.
                                    memoryCache.CreateBranchNameEntry(repositoryId, branchName, branchGuid)
                                    memoryCache.CreateBranchIdEntry branchGuid MemoryCache.Exists

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
                FROM c JOIN e in c.State
                WHERE IS_DEFINED(e.Event.logicalDeleted)) <=
            (SELECT VALUE COUNT(1)
                FROM c JOIN e in c.State
                WHERE IS_DEFINED(e.Event.undeleted))
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
                                WHERE STRINGEQUALS(c.State[0].Event.created.ownerId, @ownerId, true)
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
                            requestCharge.Append($"{results.RequestCharge:F3}, ") |> ignore

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
                            WHERE (STRINGEQUALS(c.State[0].Event.created.organizationName, @organizationName, true)
                                OR STRINGEQUALS(c.State[0].Event.setName.organizationName, @organizationName, true))
                                AND STRINGEQUALS(c.State[0].Event.created.ownerId, @ownerId, true)
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
                            WHERE (STRINGEQUALS(c.State[0].Event.created.repositoryName, @repositoryName, true)
                                OR STRINGEQUALS(c.State[0].Event.setName.repositoryName, @repositoryName, true))
                                AND STRINGEQUALS(c.State[0].Event.created.organizationId, @organizationId, true)
                                AND STRINGEQUALS(c.State[0].Event.created.ownerId, @ownerId, true)
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
                                WHERE STRINGEQUALS(c.State[0].Event.created.organizationId, @organizationId, true)
                                    AND STRINGEQUALS(c.State[0].Event.created.ownerId, @ownerId, true)
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
                            requestCharge.Append($"{results.RequestCharge:F3}, ") |> ignore

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
                            WHERE STRINGEQUALS(c.State[0].Event.created.BranchId, @branchId, true)
                                AND STARTSWITH(c.State[0].Event.created.Sha256Hash, @sha256Hash, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            ORDER BY c.State[0].Event.created.CreatedAt DESC
                            """
                        )
                            .WithParameter("@maxCount", maxCount)
                            .WithParameter("@sha256Hash", sha256Hash)
                            .WithParameter("@branchId", branchId)
                            .WithParameter("@grainType", StateName.Reference)
                            .WithParameter("@partitionKey", repositoryId)

                    let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge:F3}, ") |> ignore
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
                            WHERE STRINGEQUALS(c.State[0].Event.created.BranchId, @branchId, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            ORDER BY c.State[0].Event.created.CreatedAt DESC
                            """
                        )
                            .WithParameter("@maxCount", maxCount)
                            .WithParameter("@branchId", branchId)
                            .WithParameter("@grainType", StateName.Reference)
                            .WithParameter("@partitionKey", repositoryId)

                    let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        addTiming TimingFlag.BeforeStorageQuery "getReferences" correlationId
                        let! results = iterator.ReadNextAsync()
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge:F3}, ") |> ignore
                        let eventsForAllReferences = results.Resource

                        eventsForAllReferences
                        |> Seq.iter (fun eventsForOneReference ->
                            let referenceDto =
                                eventsForOneReference.State
                                |> Array.fold (fun referenceDto referenceEvent -> referenceDto |> ReferenceDto.UpdateDto referenceEvent) ReferenceDto.Default

                            references.Add(referenceDto))

                    //logToConsole
                    //    $"In Services.Actor.getReferences: BranchId: {branchId}; RepositoryId: {repositoryId}; Retrieved {references.Count} references.{Environment.NewLine}{printQueryDefinition queryDefinition}{Environment.NewLine}{serialize references}"

                    if
                        indexMetrics.Length >= 2
                        && requestCharge.Length >= 2
                        && Activity.Current <> null
                    then
                        Activity.Current.SetTag("indexMetrics", $"{indexMetrics.Remove(indexMetrics.Length - 2, 2)}")
                        |> ignore

                        Activity.Current.SetTag("requestCharge", $"{requestCharge.Remove(requestCharge.Length - 2, 2)}")
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
                let itemRequestOptions =
                    ItemRequestOptions(AddRequestHeaders = fun headers -> headers.Add(Constants.CorrelationIdHeaderKey, "deleteAllFromCosmosDBThatMatch"))

                let mutable totalRecordsDeleted = 0
                let overallStartTime = getCurrentInstant ()
                let deleteQueryRequestOptions = queryRequestOptions.ShallowCopy() :?> QueryRequestOptions
                deleteQueryRequestOptions.MaxItemCount <- 1000

                logToConsole
                    $"cosmosContainer.Id: {cosmosContainer.Id}; cosmosContainer.Database.Id: {cosmosContainer.Database.Id}; cosmosContainer.Database.Client.Endpoint: {cosmosContainer.Database.Client.Endpoint}."

                let iterator = cosmosContainer.GetItemQueryIterator<PartitionKeyIdentifier>(queryDefinition, requestOptions = deleteQueryRequestOptions)

                while iterator.HasMoreResults do
                    let batchStartTime = getCurrentInstant ()
                    let! batchResults = iterator.ReadNextAsync()
                    let mutable totalRequestCharge = 0L

                    //logToConsole $"In Services.deleteAllFromCosmosDB(): Current batch size: {batchResults.Resource.Count()}."

                    do!
                        Parallel.ForEachAsync(
                            batchResults,
                            (fun document ct ->
                                ValueTask(
                                    task {
                                        use! deleteResponse =
                                            cosmosContainer.DeleteAllItemsByPartitionKeyStreamAsync(PartitionKey(document.PartitionKey), itemRequestOptions)

                                        if deleteResponse.IsSuccessStatusCode then
                                            log.LogInformation(
                                                "Succeeded to delete PartitionKey {PartitionKey}. StatusCode: {statusCode}.",
                                                document.PartitionKey,
                                                deleteResponse.StatusCode
                                            )
                                        else
                                            failed.Add(document.PartitionKey)

                                            log.LogError(
                                                "Failed to delete PartitionKey {PartitionKey}. StatusCode: {statusCode}; Error: {ErrorMessage}.",
                                                document.PartitionKey,
                                                deleteResponse.StatusCode,
                                                deleteResponse.ErrorMessage
                                            )

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
                            WHERE STRINGEQUALS(c.State[0].Event.created.BranchId, @branchId, true)
                                AND STRINGEQUALS(c.State[0].Event.created.ReferenceType, @referenceType, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            ORDER BY c.State[0].Event.created.CreatedAt DESC
                            """
                        )
                            .WithParameter("@maxCount", maxCount)
                            .WithParameter("@branchId", branchId)
                            .WithParameter("@referenceType", getDiscriminatedUnionCaseName referenceType)
                            .WithParameter("@grainType", StateName.Reference)
                            .WithParameter("@partitionKey", repositoryId)

                    let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        addTiming TimingFlag.BeforeStorageQuery "getReferencesByType" correlationId
                        let! results = iterator.ReadNextAsync()
                        addTiming TimingFlag.AfterStorageQuery "getReferencesByType" correlationId
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge:F3}, ") |> ignore
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
                            WHERE STRINGEQUALS(c.State[0].Event.created.BranchId, @branchId, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            ORDER BY c.State[0].Event.created.CreatedAt DESC
                            """
                        )
                            .WithParameter("@branchId", branchId)
                            .WithParameter("@grainType", StateName.Reference)
                            .WithParameter("@partitionKey", repositoryId)

                    let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge:F3}, ") |> ignore
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
                // Collect per-task metrics in thread-safe bags to avoid concurrent mutation of a single StringBuilder.
                let indexMetricsBag = ConcurrentBag<string>()
                let requestChargeBag = ConcurrentBag<string>()

                try
                    // Run queries in parallel; each task adds metrics to the concurrent bags.
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
                                                WHERE STRINGEQUALS(c.State[0].Event.created.BranchId, @branchId, true)
                                                    AND STRINGEQUALS(c.State[0].Event.created.ReferenceType, @referenceType, true)
                                                    AND c.GrainType = @grainType
                                                    AND c.PartitionKey = @partitionKey
                                                ORDER BY c.State[0].Event.created.CreatedAt DESC
                                                """
                                            )
                                                .WithParameter("@branchId", branchId)
                                                .WithParameter("@referenceType", getDiscriminatedUnionCaseName referenceType)
                                                .WithParameter("@grainType", StateName.Reference)
                                                .WithParameter("@partitionKey", repositoryId)

                                        let iterator =
                                            cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                                        while iterator.HasMoreResults do
                                            let! results = iterator.ReadNextAsync()
                                            // Save metrics into concurrent bags (thread-safe).
                                            indexMetricsBag.Add(results.IndexMetrics)
                                            requestChargeBag.Add($"{results.RequestCharge:F3}")

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

                    // Merge collected metrics after parallel work and set Activity tags.
                    let indexMetricsSb = stringBuilderPool.Get()
                    let requestChargeSb = stringBuilderPool.Get()

                    try
                        for m in indexMetricsBag do
                            indexMetricsSb.Append($"{m}, ") |> ignore

                        for r in requestChargeBag do
                            requestChargeSb.Append($"{r}, ") |> ignore

                        if
                            (indexMetricsSb.Length >= 2)
                            && (requestChargeSb.Length >= 2)
                            && Activity.Current <> null
                        then
                            Activity.Current
                                .SetTag("indexMetrics", $"{indexMetricsSb.Remove(indexMetricsSb.Length - 2, 2)}")
                                .SetTag("requestCharge", $"{requestChargeSb.Remove(requestChargeSb.Length - 2, 2)}")
                            |> ignore
                    finally
                        stringBuilderPool.Return(indexMetricsSb)
                        stringBuilderPool.Return(requestChargeSb)
                finally
                    ()
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
                            WHERE STRINGEQUALS(c.State[0].Event.created.BranchId, @branchId, true)
                                AND STRINGEQUALS(c.State[0].Event.created.ReferenceType, @referenceType, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            ORDER BY c.State[0].Event.created.CreatedAt DESC
                            """
                        )
                            .WithParameter("@branchId", branchId)
                            .WithParameter("@referenceType", getDiscriminatedUnionCaseName referenceType)
                            .WithParameter("@grainType", StateName.Reference)
                            .WithParameter("@partitionKey", repositoryId)

                    let iterator = cosmosContainer.GetItemQueryIterator<ReferenceEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    let references = List<ReferenceDto>()

                    while iterator.HasMoreResults do
                        let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge:F3}, ") |> ignore
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

    /// Gets a list of branches for a given repository.
    let getBranches (ownerId: OwnerId) (organizationId: OrganizationId) (repositoryId: RepositoryId) (maxCount: int) includeDeleted correlationId =
        task {
            let branches = ConcurrentDictionary<BranchId, BranchDto>()
            let branchIds = List<BranchId>()

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let indexMetrics = stringBuilderPool.Get()
                let requestCharge = stringBuilderPool.Get()

                try
                    try
                        // First, get all of the branches for the repository.
                        let queryDefinition =
                            QueryDefinition(
                                $"""
                                SELECT TOP @maxCount c.State[0].Event.created.branchId
                                FROM c
                                WHERE STRINGEQUALS(c.State[0].Event.created.ownerId, @ownerId, true)
                                    AND STRINGEQUALS(c.State[0].Event.created.organizationId, @organizationId, true)
                                    AND LENGTH(c.State[0].Event.created.branchName) > 0
                                    {includeDeletedEntitiesClause includeDeleted}
                                    AND c.GrainType = @grainType
                                    AND c.PartitionKey = @partitionKey
                                """
                            )
                                .WithParameter("@maxCount", maxCount)
                                .WithParameter("@ownerId", ownerId)
                                .WithParameter("@organizationId", organizationId)
                                .WithParameter("@grainType", StateName.Branch)
                                .WithParameter("@partitionKey", repositoryId)

                        let iterator = cosmosContainer.GetItemQueryIterator<BranchIdValue>(queryDefinition, requestOptions = queryRequestOptions)

                        while iterator.HasMoreResults do
                            addTiming TimingFlag.BeforeStorageQuery "getBranches" correlationId
                            let! results = iterator.ReadNextAsync()
                            addTiming TimingFlag.AfterStorageQuery "getBranches" correlationId
                            indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                            requestCharge.Append($"{results.RequestCharge:F3}, ") |> ignore

                            let branchIdValues = results.Resource

                            for branchIdValue in branchIdValues do
                                branchIds.Add(branchIdValue.branchId)

                        do!
                            Parallel.ForEachAsync(
                                branchIds,
                                (fun branchId ct ->
                                    ValueTask(
                                        task {
                                            let actorProxy = Branch.CreateActorProxy branchId repositoryId correlationId
                                            let! branchDto = actorProxy.Get correlationId
                                            branches[branchDto.BranchId] <- branchDto
                                        }
                                    ))
                            )

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

            return branches.Values.OrderBy(fun branchDto -> branchDto.UpdatedAt).ToArray()
        }

    /// Gets a DirectoryVersion by searching using a Sha256Hash value.
    let getDirectoryVersionBySha256Hash (repositoryId: RepositoryId) (sha256Hash: Sha256Hash) correlationId =
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
                            WHERE STARTSWITH(c.State[0].Event.created.Sha256Hash, @sha256Hash, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            ORDER BY c.State[0].Event.created.CreatedAt DESC
                            """
                        )
                            .WithParameter("@sha256Hash", sha256Hash)
                            .WithParameter("@grainType", StateName.DirectoryVersion)
                            .WithParameter("@partitionKey", repositoryId)

                    try
                        let iterator = cosmosContainer.GetItemQueryIterator<DirectoryVersionEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                        let directoryVersionDtos = List<DirectoryVersionDto>()

                        while iterator.HasMoreResults do
                            let! results = DefaultAsyncRetryPolicy.ExecuteAsync(fun () -> iterator.ReadNextAsync())
                            indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                            requestCharge.Append($"{results.RequestCharge:F3}, ") |> ignore
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

    /// Gets the most recent DirectoryVersion with HashesValidated = true by RelativePath.
    let getMostRecentDirectoryVersionByRelativePath (repositoryId: RepositoryId) (relativePath: RelativePath) correlationId =
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
                            WHERE STRINGEQUALS(c.State[0].Event.created.RelativePath, @relativePath, true)
                                AND c.State[0].Event.created.HashesValidated = true
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            ORDER BY c.State[0].Event.created.CreatedAt DESC
                            """
                        )
                            .WithParameter("@relativePath", relativePath)
                            .WithParameter("@grainType", StateName.DirectoryVersion)
                            .WithParameter("@partitionKey", repositoryId)

                    let iterator = cosmosContainer.GetItemQueryIterator<DirectoryVersionEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                        requestCharge.Append($"{results.RequestCharge:F3}, ") |> ignore
                        let eventsForAllDirectories = results.Resource

                        eventsForAllDirectories
                        |> Seq.iter (fun eventsForOneDirectory ->
                            let directoryVersionDto =
                                eventsForOneDirectory.State
                                |> Array.fold
                                    (fun directoryVersionDto directoryEvent -> directoryVersionDto |> DirectoryVersionDto.UpdateDto directoryEvent)
                                    DirectoryVersionDto.Default

                            directoryVersion <- directoryVersionDto.DirectoryVersion)

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

            if
                directoryVersion.DirectoryVersionId
                <> DirectoryVersion.Default.DirectoryVersionId
            then
                return Some directoryVersion
            else
                return None
        }

    /// Gets a Root DirectoryVersion by searching using a Sha256Hash value.
    let getRootDirectoryVersionBySha256Hash (repositoryId: RepositoryId) (sha256Hash: Sha256Hash) correlationId =
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
                            WHERE STARTSWITH(c.State[0].Event.created.Sha256Hash, @sha256Hash, true)
                                AND STRINGEQUALS(c.State[0].Event.created.RelativePath, @relativePath, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            ORDER BY c.State[0].Event.created.CreatedAt DESC
                            """
                        )
                            .WithParameter("@sha256Hash", sha256Hash)
                            .WithParameter("@relativePath", Constants.RootDirectoryPath)
                            .WithParameter("@grainType", StateName.DirectoryVersion)
                            .WithParameter("@partitionKey", repositoryId)

                    try
                        let iterator = cosmosContainer.GetItemQueryIterator<DirectoryVersionEventValue>(queryDefinition, requestOptions = queryRequestOptions)
                        let directoryVersionDtos = List<DirectoryVersionDto>()

                        while iterator.HasMoreResults do
                            let! results = iterator.ReadNextAsync()
                            indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                            requestCharge.Append($"{results.RequestCharge:F3}, ") |> ignore
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
    let getRootDirectoryVersionByReferenceId (repositoryId: RepositoryId) (referenceId: ReferenceId) correlationId =
        task {
            let referenceActorProxy = Reference.CreateActorProxy referenceId repositoryId correlationId

            let! referenceDto = referenceActorProxy.Get correlationId

            return! getRootDirectoryVersionBySha256Hash repositoryId referenceDto.Sha256Hash correlationId
        }

    /// Checks if all of the supplied DirectoryVersionIds exist.
    let directoryVersionIdsExist (repositoryId: RepositoryId) (directoryVersionIds: IEnumerable<DirectoryVersionId>) correlationId =
        task {
            match actorStateStorageProvider with
            | Unknown -> return false
            | AzureCosmosDb ->
                let mutable requestCharge = 0.0
                let mutable allExist = true
                let directoryVersionIdQueue = Queue<DirectoryVersionId>(directoryVersionIds)

                while directoryVersionIdQueue.Count > 0 && allExist do
                    let directoryVersionId = directoryVersionIdQueue.Dequeue()

                    let queryDefinition =
                        QueryDefinition(
                            """
                            SELECT c.State
                            FROM c
                            WHERE STRINGEQUALS(c.State[0].Event.created.DirectoryVersionId, @directoryVersionId, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            """
                        )
                            .WithParameter("@directoryVersionId", $"{directoryVersionId}")
                            .WithParameter("@grainType", StateName.DirectoryVersion)
                            .WithParameter("@partitionKey", repositoryId)

                    let iterator = cosmosContainer.GetItemQueryIterator<DirectoryVersionEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        let! results = iterator.ReadNextAsync()
                        requestCharge <- requestCharge + results.RequestCharge

                        if not <| results.Resource.Any() then allExist <- false

                Activity.Current.SetTag("allExist", $"{allExist}").SetTag("totalRequestCharge", $"{requestCharge}")
                |> ignore

                return allExist
            | MongoDB -> return false
        }

    /// Gets a list of ReferenceDtos based on ReferenceIds. The list is returned in the same order as the supplied ReferenceIds.
    let getReferencesByReferenceId (repositoryId: RepositoryId) (referenceIds: IEnumerable<ReferenceId>) (maxCount: int) (correlationId: CorrelationId) =
        task {
            let referenceDtos = List<ReferenceDto>()

            if referenceIds.Count() > 0 then
                match actorStateStorageProvider with
                | Unknown -> ()
                | AzureCosmosDb ->
                    let mutable requestCharge = 0.0
                    let mutable clientElapsedTime = TimeSpan.Zero
                    let queryText = stringBuilderPool.Get()

                    try
                        // In order to build the IN clause, we need to create a parameter for each referenceId.
                        //   (I tried just using string concatenation, it didn't work for some reason. Anyway...)
                        // The query starts with:
                        queryText.Append(
                            @"SELECT TOP @maxCount c.State
                              FROM c
                              WHERE c.GrainType = @grainType
                                  AND c.PartitionKey = @partitionKey
                                  AND c.State[0].Event.created.ReferenceId IN ("
                        )
                        |> ignore
                        // Then we add a parameter for each referenceId.
                        referenceIds.Where(fun referenceId -> not <| referenceId.Equals(ReferenceId.Empty)).Distinct()
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
                        referenceIds.Where(fun referenceId -> not <| referenceId.Equals(ReferenceId.Empty)).Distinct()
                        |> Seq.iteri (fun i referenceId -> queryDefinition.WithParameter($"@referenceId{i}", $"{referenceId}") |> ignore)

                        //logToConsole $"In getReferencesByReferenceId(): QueryText:{Environment.NewLine}{printQueryDefinition queryDefinition}."

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
                                    |> Array.fold
                                        (fun referenceDto referenceEvent -> referenceDto |> ReferenceDto.UpdateDto referenceEvent)
                                        ReferenceDto.Default

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
                            WHERE STRINGEQUALS(c.State[0].Event.created.BranchId, @branchId, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                                {includeDeletedEntitiesClause includeDeleted}
                            """
                        )
                            .WithParameter("@maxCount", maxCount)
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

                if Activity.Current <> null then
                    Activity.Current.SetTag("referenceDtos.Count", $"{branchDtos.Count}").SetTag("totalRequestCharge", $"{requestCharge}")
                    |> ignore
            | MongoDB -> ()

            return branchDtos.OrderBy(fun branchDto -> branchDto.BranchName).ToArray()
        }

    /// Gets a list of child BranchDtos for a given parent branch.
    let getChildBranches (repositoryId: RepositoryId) (parentBranchId: BranchId) (maxCount: int) includeDeleted correlationId =
        task {
            let childBranches = List<BranchDto>()

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let mutable requestCharge = 0.0

                try
                    let queryDefinition =
                        QueryDefinition(
                            $"""
                            SELECT TOP @maxCount c.State
                            FROM c
                            WHERE STRINGEQUALS(c.State[0].Event.created.parentBranchId, @parentBranchId, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                                {includeDeletedEntitiesClause includeDeleted}
                            """
                        )
                            .WithParameter("@maxCount", maxCount)
                            .WithParameter("@parentBranchId", $"{parentBranchId}")
                            .WithParameter("@grainType", StateName.Branch)
                            .WithParameter("@partitionKey", repositoryId)

                    let iterator = cosmosContainer.GetItemQueryIterator<BranchEventValue>(queryDefinition, requestOptions = queryRequestOptions)

                    while iterator.HasMoreResults do
                        addTiming TimingFlag.BeforeStorageQuery "getChildBranches" correlationId
                        let! results = iterator.ReadNextAsync()
                        addTiming TimingFlag.AfterStorageQuery "getChildBranches" correlationId
                        requestCharge <- requestCharge + results.RequestCharge
                        let eventsForAllBranches = results.Resource

                        eventsForAllBranches
                        |> Seq.iter (fun eventsForOneBranch ->
                            let branchDto =
                                eventsForOneBranch.State
                                |> Array.fold (fun branchDto branchEvent -> branchDto |> BranchDto.UpdateDto branchEvent) BranchDto.Default

                            childBranches.Add(branchDto))

                    if (Activity.Current <> null) then
                        Activity.Current.SetTag("childBranches.Count", $"{childBranches.Count}").SetTag("totalRequestCharge", $"{requestCharge}")
                        |> ignore
                with ex ->
                    log.LogError(
                        ex,
                        "{CurrentInstant}: Error in getChildBranches. CorrelationId: {correlationId}.",
                        getCurrentInstantExtended (),
                        correlationId
                    )
            | MongoDB -> ()

            return childBranches.OrderBy(fun branchDto -> branchDto.BranchName).ToArray()
        }

    /// Gets a list of reminders for a repository, with optional filtering.
    let getReminders
        (graceIds: GraceIds)
        (maxCount: int)
        (reminderTypeFilter: string option)
        (actorNameFilter: string option)
        (dueAfter: Instant option)
        (dueBefore: Instant option)
        (correlationId: CorrelationId)
        =
        task {
            let reminders = List<ReminderDto>()

            match actorStateStorageProvider with
            | Unknown -> ()
            | AzureCosmosDb ->
                let indexMetrics = stringBuilderPool.Get()
                let requestCharge = stringBuilderPool.Get()

                try
                    try
                        // Build the query dynamically based on provided filters
                        let queryBuilder = StringBuilder()

                        queryBuilder.Append(
                            """
                            SELECT TOP @maxCount c.State.Reminder
                            FROM c
                            WHERE STRINGEQUALS(c.State.Reminder.OwnerId, @ownerId, true)
                                AND STRINGEQUALS(c.State.Reminder.OrganizationId, @organizationId, true)
                                AND STRINGEQUALS(c.State.Reminder.RepositoryId, @repositoryId, true)
                                AND c.GrainType = @grainType
                                AND c.PartitionKey = @partitionKey
                            """
                        )
                        |> ignore

                        // Add optional filters
                        if
                            reminderTypeFilter.IsSome
                            && not (String.IsNullOrEmpty(reminderTypeFilter.Value))
                        then
                            queryBuilder.Append(" AND STRINGEQUALS(c.State.Reminder.ReminderType, @reminderType, true)")
                            |> ignore

                        if actorNameFilter.IsSome && not (String.IsNullOrEmpty(actorNameFilter.Value)) then
                            queryBuilder.Append(" AND STRINGEQUALS(c.State.Reminder.ActorName, @actorName, true)")
                            |> ignore

                        if dueAfter.IsSome then
                            queryBuilder.Append(" AND c.State.Reminder.ReminderTime >= @dueAfter") |> ignore

                        if dueBefore.IsSome then
                            queryBuilder.Append(" AND c.State.Reminder.ReminderTime <= @dueBefore")
                            |> ignore

                        queryBuilder.Append(" ORDER BY c.State.Reminder.ReminderTime ASC") |> ignore

                        let queryDefinition =
                            QueryDefinition(queryBuilder.ToString())
                                .WithParameter("@maxCount", maxCount)
                                .WithParameter("@ownerId", graceIds.OwnerIdString)
                                .WithParameter("@organizationId", graceIds.OrganizationIdString)
                                .WithParameter("@repositoryId", graceIds.RepositoryIdString)
                                .WithParameter("@grainType", StateName.Reminder)
                                .WithParameter("@partitionKey", StateName.Reminder)

                        // Add optional parameters
                        if
                            reminderTypeFilter.IsSome
                            && not (String.IsNullOrEmpty(reminderTypeFilter.Value))
                        then
                            queryDefinition.WithParameter("@reminderType", reminderTypeFilter.Value)
                            |> ignore

                        if actorNameFilter.IsSome && not (String.IsNullOrEmpty(actorNameFilter.Value)) then
                            queryDefinition.WithParameter("@actorName", actorNameFilter.Value) |> ignore

                        if dueAfter.IsSome then
                            queryDefinition.WithParameter("@dueAfter", dueAfter.Value.ToUnixTimeTicks())
                            |> ignore

                        if dueBefore.IsSome then
                            queryDefinition.WithParameter("@dueBefore", dueBefore.Value.ToUnixTimeTicks())
                            |> ignore

                        let iterator = cosmosContainer.GetItemQueryIterator<ReminderValue>(queryDefinition, requestOptions = queryRequestOptions)

                        while iterator.HasMoreResults do
                            let! results = iterator.ReadNextAsync()
                            indexMetrics.Append($"{results.IndexMetrics}, ") |> ignore
                            requestCharge.Append($"{results.RequestCharge:F3}, ") |> ignore

                            let reminderValues = results.Resource

                            reminderValues
                            |> Seq.iter (fun reminderValue -> reminders.Add(reminderValue.Reminder))

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
                            "{CurrentInstant}: Error in getReminders. CorrelationId: {correlationId}.",
                            getCurrentInstantExtended (),
                            correlationId
                        )
                finally
                    stringBuilderPool.Return(indexMetrics)
                    stringBuilderPool.Return(requestCharge)
            | MongoDB -> ()

            logToConsole $"Found {reminders.Count} reminders for RepositoryId {graceIds.RepositoryIdString}."
            logToConsole $"Reminders: {serialize reminders}."
            return reminders.ToArray()
        }

    /// Gets a single reminder by its ID.
    let getReminderById (reminderId: ReminderId) (correlationId: CorrelationId) =
        task {
            let reminderActorProxy = Reminder.CreateActorProxy reminderId correlationId
            let! exists = reminderActorProxy.Exists correlationId

            if exists then
                let! reminderDto = reminderActorProxy.Get correlationId
                return Some reminderDto
            else
                return None
        }

    /// Deletes a reminder by its ID.
    let deleteReminder (reminderId: ReminderId) (correlationId: CorrelationId) =
        task {
            let reminderActorProxy = Reminder.CreateActorProxy reminderId correlationId
            let! exists = reminderActorProxy.Exists correlationId

            if exists then
                do! reminderActorProxy.Delete correlationId
                return Ok()
            else
                return Error "Reminder not found."
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
