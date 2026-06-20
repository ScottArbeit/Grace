namespace Grace.Shared

open Grace.Types.Common
open Grace.Types.Repository
open System

module StoragePoolRouting =

    type StorageShard = { StorageAccountName: StorageAccountName; StorageContainerName: StorageContainerName; ObjectKeyPrefix: string }

    type ConfiguredStoragePool = { StoragePoolId: StoragePoolId; Shard: StorageShard }

    type StoragePoolRoute = { RepositoryId: RepositoryId; StoragePoolId: StoragePoolId; Shard: StorageShard }

    let defaultStoragePoolId = StoragePoolId Constants.DefaultStoragePoolId

    let defaultStorageShard () =
        {
            StorageAccountName = AzureEnvironment.storageEndpoints.AccountName
            StorageContainerName = StorageContainerName Constants.DefaultCasStorageContainerName
            ObjectKeyPrefix = Constants.DefaultCasStoragePrefix
        }

    let defaultConfiguredPool () = { StoragePoolId = defaultStoragePoolId; Shard = defaultStorageShard () }

    let private fail correlationId message = Error(GraceError.Create message correlationId)

    let private normalizePrefix (prefix: string) =
        if String.IsNullOrWhiteSpace prefix then
            String.Empty
        else
            prefix.Trim().Trim('/')

    let validateShard correlationId (shard: StorageShard) =
        if isNull (box shard) then
            fail correlationId "StorageShard is required."
        elif String.IsNullOrWhiteSpace shard.StorageAccountName then
            fail correlationId "StorageShard.StorageAccountName is required."
        elif String.IsNullOrWhiteSpace shard.StorageContainerName then
            fail correlationId "StorageShard.StorageContainerName is required."
        else
            Ok()

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

    let storagePlacementForObjectKey (shard: StorageShard) objectKey eTag : Grace.Types.ContentBlockMetadata.ContentBlockStoragePlacement =
        {
            Grace.Types.ContentBlockMetadata.ContentBlockStoragePlacement.StorageAccountName = shard.StorageAccountName
            StorageContainerName = shard.StorageContainerName
            ObjectKey = objectKeyInShard shard objectKey
            ETag = eTag
        }

    let sharedKeyCanSignShard (shard: StorageShard) =
        shard.StorageAccountName.Equals(AzureEnvironment.storageEndpoints.AccountName, StringComparison.OrdinalIgnoreCase)

    let validateShardForSharedKeySas correlationId (shard: StorageShard) =
        match validateShard correlationId shard with
        | Error error -> Error error
        | Ok () when sharedKeyCanSignShard shard -> Ok()
        | Ok () ->
            fail
                correlationId
                $"StorageShard '{shard.StorageAccountName}/{shard.StorageContainerName}' cannot be signed with the configured shared-key account '{AzureEnvironment.storageEndpoints.AccountName}'. Configure managed identity for cross-account CAS SAS."

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

    let resolveConfiguredPoolRouteForStoragePoolId (configuredPools: ConfiguredStoragePool seq) repositoryId storagePoolId correlationId =
        if repositoryId = RepositoryId.Empty then
            fail correlationId "RepositoryId is required before resolving a StoragePool route."
        elif String.IsNullOrWhiteSpace storagePoolId then
            fail correlationId "UploadSession StoragePoolId is not configured."
        else
            let configuredPool =
                configuredPools
                |> Seq.tryFind (fun pool -> pool.StoragePoolId = storagePoolId)

            match configuredPool with
            | Some pool -> Ok { RepositoryId = repositoryId; StoragePoolId = pool.StoragePoolId; Shard = pool.Shard }
            | None -> fail correlationId $"StoragePoolId '{storagePoolId}' is not configured. StoragePool routing fails closed."

    let resolveRepositoryRoute (repository: RepositoryDto) correlationId =
        if isNull (box repository) then
            fail correlationId "Repository state is required before resolving a StoragePool route."
        elif String.IsNullOrWhiteSpace repository.StoragePoolId then
            fail correlationId "Repository StoragePoolId is not configured."
        elif repository.StoragePoolId <> defaultStoragePoolId then
            fail correlationId $"StoragePoolId '{repository.StoragePoolId}' is not configured. StoragePool routing fails closed."
        else
            resolveConfiguredPoolRoute [ defaultConfiguredPool () ] repository correlationId

    let resolveStoragePoolRouteForStoragePoolId repositoryId storagePoolId correlationId =
        resolveConfiguredPoolRouteForStoragePoolId [ defaultConfiguredPool () ] repositoryId storagePoolId correlationId
