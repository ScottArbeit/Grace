namespace Grace.Shared

open Grace.Types.Common
open Grace.Types.Repository
open System

/// Contains storage pool routing helpers.
module StoragePoolRouting =

    /// Represents the storage shard contract.
    type StorageShard = { StorageAccountName: StorageAccountName; StorageContainerName: StorageContainerName; ObjectKeyPrefix: string }

    /// Represents the configured storage pool contract.
    type ConfiguredStoragePool = { StoragePoolId: StoragePoolId; Shard: StorageShard }

    /// Represents the storage pool route contract.
    type StoragePoolRoute = { RepositoryId: RepositoryId; StoragePoolId: StoragePoolId; Shard: StorageShard }

    let defaultStoragePoolId = StoragePoolId Constants.DefaultStoragePoolId

    /// Derives the only supported repository-scoped dedupe pool id.
    let repositoryDedupeStoragePoolId (repositoryId: RepositoryId) = StoragePoolId $"{repositoryId}"

    /// Builds the default shard configuration used when no external routing is configured.
    let defaultStorageShard () =
        {
            StorageAccountName = AzureEnvironment.storageEndpoints.AccountName
            StorageContainerName = StorageContainerName Constants.DefaultCasStorageContainerName
            ObjectKeyPrefix = Constants.DefaultCasStoragePrefix
        }

    /// Builds the default configured pool with the default shard as both primary and replica.
    let defaultConfiguredPool () = { StoragePoolId = defaultStoragePoolId; Shard = defaultStorageShard () }

    /// Creates a routing failure that explains why storage placement could not be resolved.
    let private fail correlationId message = Error(GraceError.Create message correlationId)

    /// Normalizes prefix.
    let private normalizePrefix (prefix: string) =
        if String.IsNullOrWhiteSpace prefix then
            String.Empty
        else
            prefix.Trim().Trim('/')

    /// Validates shard.
    let validateShard correlationId (shard: StorageShard) =
        if isNull (box shard) then
            fail correlationId "StorageShard is required."
        elif String.IsNullOrWhiteSpace shard.StorageAccountName then
            fail correlationId "StorageShard.StorageAccountName is required."
        elif String.IsNullOrWhiteSpace shard.StorageContainerName then
            fail correlationId "StorageShard.StorageContainerName is required."
        else
            Ok()

    /// Combines a shard prefix and object key into the physical key stored in that shard.
    let objectKeyInShard (shard: StorageShard) objectKey =
        let prefix = normalizePrefix shard.ObjectKeyPrefix

        let normalizedObjectKey =
            if String.IsNullOrWhiteSpace objectKey then
                String.Empty
            else
                objectKey.TrimStart('/')

        if String.IsNullOrWhiteSpace prefix then
            normalizedObjectKey
        else
            $"{prefix}/{normalizedObjectKey}"

    /// Creates primary and replica object placements for a resolved object key.
    let storagePlacementForObjectKey (shard: StorageShard) objectKey eTag : Grace.Types.ContentBlockMetadata.ContentBlockStoragePlacement =
        {
            Grace.Types.ContentBlockMetadata.ContentBlockStoragePlacement.StorageAccountName = shard.StorageAccountName
            StorageContainerName = shard.StorageContainerName
            ObjectKey = objectKeyInShard shard objectKey
            ETag = eTag
        }

    /// Checks whether a shared-key credential can sign URLs for a storage shard.
    let sharedKeyCanSignShard (shard: StorageShard) =
        shard.StorageAccountName.Equals(AzureEnvironment.storageEndpoints.AccountName, StringComparison.OrdinalIgnoreCase)

    /// Validates shard for shared key sas.
    let validateShardForSharedKeySas correlationId (shard: StorageShard) =
        match validateShard correlationId shard with
        | Error error -> Error error
        | Ok () when sharedKeyCanSignShard shard -> Ok()
        | Ok () ->
            fail
                correlationId
                $"StorageShard '{shard.StorageAccountName}/{shard.StorageContainerName}' cannot be signed with the configured shared-key account '{AzureEnvironment.storageEndpoints.AccountName}'. Configure managed identity for cross-account CAS SAS."

    /// Resolves the configured pool route or returns a closed failure for unknown pools.
    let resolveConfiguredPoolRoute (configuredPools: ConfiguredStoragePool seq) (repository: RepositoryDto) correlationId =
        if isNull (box repository) then
            fail correlationId "Repository state is required before resolving a StoragePool route."
        elif String.IsNullOrWhiteSpace repository.StoragePoolId then
            fail correlationId "Repository StoragePoolId is not configured."
        else
            let configuredPool =
                configuredPools
                |> Seq.tryFind (fun pool -> pool.StoragePoolId = repository.StoragePoolId)

            match configuredPool with
            | Some pool -> Ok { RepositoryId = repository.RepositoryId; StoragePoolId = pool.StoragePoolId; Shard = pool.Shard }
            | None -> fail correlationId $"StoragePoolId '{repository.StoragePoolId}' is not configured. StoragePool routing fails closed."

    /// Resolves the configured pool route or returns a closed failure for unknown pools.
    let resolveConfiguredPoolRouteForStoragePoolId (configuredPools: ConfiguredStoragePool seq) repositoryId storagePoolId correlationId =
        if repositoryId = RepositoryId.Empty then
            fail correlationId "RepositoryId is required before resolving a StoragePool route."
        elif String.IsNullOrWhiteSpace storagePoolId then
            fail correlationId "UploadSession StoragePoolId is not configured."
        else
            let configuredPools = configuredPools |> Seq.toArray

            let configuredPool =
                configuredPools
                |> Seq.tryFind (fun pool -> pool.StoragePoolId = storagePoolId)

            match configuredPool with
            | Some pool -> Ok { RepositoryId = repositoryId; StoragePoolId = pool.StoragePoolId; Shard = pool.Shard }
            | None when storagePoolId = repositoryDedupeStoragePoolId repositoryId ->
                match configuredPools
                      |> Seq.tryFind (fun pool -> pool.StoragePoolId = defaultStoragePoolId)
                    with
                | Some defaultPool -> Ok { RepositoryId = repositoryId; StoragePoolId = storagePoolId; Shard = defaultPool.Shard }
                | None -> fail correlationId $"StoragePoolId '{defaultStoragePoolId}' is not configured. StoragePool routing fails closed."
            | None -> fail correlationId $"StoragePoolId '{storagePoolId}' is not configured. StoragePool routing fails closed."

    /// Resolves the repository storage route used for repository-scoped objects.
    let resolveRepositoryRoute (repository: RepositoryDto) correlationId =
        if isNull (box repository) then
            fail correlationId "Repository state is required before resolving a StoragePool route."
        elif String.IsNullOrWhiteSpace repository.StoragePoolId then
            fail correlationId "Repository StoragePoolId is not configured."
        elif repository.StoragePoolId <> defaultStoragePoolId then
            fail correlationId $"StoragePoolId '{repository.StoragePoolId}' is not configured. StoragePool routing fails closed."
        else
            resolveConfiguredPoolRoute [ defaultConfiguredPool () ] repository correlationId

    /// Resolves a storage-pool route for callers that already selected the pool id.
    let resolveStoragePoolRouteForStoragePoolId repositoryId storagePoolId correlationId =
        resolveConfiguredPoolRouteForStoragePoolId [ defaultConfiguredPool () ] repositoryId storagePoolId correlationId
