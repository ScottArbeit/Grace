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

    let resolveRepositoryRoute (repository: RepositoryDto) correlationId =
        if isNull (box repository) then
            fail correlationId "Repository state is required before resolving a StoragePool route."
        elif String.IsNullOrWhiteSpace repository.StoragePoolId then
            fail correlationId "Repository StoragePoolId is not configured."
        elif repository.StoragePoolId <> defaultStoragePoolId then
            fail correlationId $"StoragePoolId '{repository.StoragePoolId}' is not configured. StoragePool routing fails closed."
        else
            resolveConfiguredPoolRoute [ defaultConfiguredPool () ] repository correlationId
