namespace Grace.Server.UnitTests

open Grace.Shared
open Grace.Types.ContentBlockMetadata
open Grace.Types.Common
open Grace.Types.Repository
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
type StoragePoolRoutingServerTests() =

    do Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment, "Local")

    let repositoryWithId repositoryId =
        { RepositoryDto.Default with
            RepositoryId = repositoryId
            OwnerId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
            OrganizationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")
            RepositoryName = RepositoryName $"repo-{repositoryId:N}"
            StoragePoolId = StoragePoolRouting.defaultStoragePoolId
            StorageAccountName = "whole-file-account"
            StorageContainerName = StorageContainerName $"{repositoryId}"
        }

    [<Test>]
    member _.DefaultRepositoriesResolveToSharedConfiguredPoolAndShard() =
        let firstRepository = repositoryWithId (Guid.Parse("11111111-1111-1111-1111-111111111111"))
        let secondRepository = repositoryWithId (Guid.Parse("22222222-2222-2222-2222-222222222222"))

        let firstRoute = StoragePoolRouting.resolveRepositoryRoute firstRepository "corr-route-first"
        let secondRoute = StoragePoolRouting.resolveRepositoryRoute secondRepository "corr-route-second"

        match firstRoute, secondRoute with
        | Ok first, Ok second ->
            Assert.That(first.RepositoryId, Is.EqualTo(firstRepository.RepositoryId))
            Assert.That(second.RepositoryId, Is.EqualTo(secondRepository.RepositoryId))
            Assert.That(first.StoragePoolId, Is.EqualTo(StoragePoolRouting.defaultStoragePoolId))
            Assert.That(second.StoragePoolId, Is.EqualTo(first.StoragePoolId))
            Assert.That(first.Shard, Is.EqualTo(second.Shard))
            Assert.That(first.Shard.StorageAccountName, Is.EqualTo(AzureEnvironment.storageEndpoints.AccountName))
            Assert.That(first.Shard.StorageContainerName, Is.EqualTo(StorageContainerName Constants.DefaultCasStorageContainerName))
            Assert.That(first.Shard.StorageContainerName, Is.Not.EqualTo(firstRepository.StorageContainerName))
            Assert.That(second.Shard.StorageContainerName, Is.Not.EqualTo(secondRepository.StorageContainerName))
        | Error error, _ -> Assert.Fail($"Expected first repository route to resolve, got {error.Error}.")
        | _, Error error -> Assert.Fail($"Expected second repository route to resolve, got {error.Error}.")

    [<Test>]
    member _.BlankRepositoryStoragePoolFailsClosed() =
        let repository = { repositoryWithId (Guid.Parse("33333333-3333-3333-3333-333333333333")) with StoragePoolId = StoragePoolId " " }

        match StoragePoolRouting.resolveRepositoryRoute repository "corr-blank-pool" with
        | Ok route -> Assert.Fail($"Expected blank StoragePoolId to fail closed, got {route.StoragePoolId}.")
        | Error error ->
            Assert.That(error.Error, Does.Contain("Repository StoragePoolId is not configured."))
            Assert.That(error.CorrelationId, Is.EqualTo("corr-blank-pool"))

    [<Test>]
    member _.UnconfiguredRepositoryStoragePoolFailsClosed() =
        let repository = { repositoryWithId (Guid.Parse("44444444-4444-4444-4444-444444444444")) with StoragePoolId = StoragePoolId "pool-a" }

        match StoragePoolRouting.resolveRepositoryRoute repository "corr-missing-pool" with
        | Ok route -> Assert.Fail($"Expected unconfigured StoragePoolId to fail closed, got {route.StoragePoolId}.")
        | Error error ->
            Assert.That(error.Error, Does.Contain("StoragePoolId 'pool-a' is not configured."))
            Assert.That(error.CorrelationId, Is.EqualTo("corr-missing-pool"))

    [<Test>]
    member _.UploadSessionRecordedStoragePoolRouteDoesNotFollowRepositoryRouteDrift() =
        let repositoryId = Guid.Parse("45454545-4545-4545-4545-454545454545")
        let recordedPoolId = StoragePoolId "pool-recorded"
        let currentRepositoryPoolId = StoragePoolId "pool-current"

        let configuredPools: StoragePoolRouting.ConfiguredStoragePool array =
            [|
                {
                    StoragePoolId = recordedPoolId
                    Shard = { StorageAccountName = "cas-recorded"; StorageContainerName = StorageContainerName "cas-recorded"; ObjectKeyPrefix = "recorded" }
                }
                {
                    StoragePoolId = currentRepositoryPoolId
                    Shard = { StorageAccountName = "cas-current"; StorageContainerName = StorageContainerName "cas-current"; ObjectKeyPrefix = "current" }
                }
            |]

        let route = StoragePoolRouting.resolveConfiguredPoolRouteForStoragePoolId configuredPools repositoryId recordedPoolId "corr-recorded-pool"

        match route with
        | Error error -> Assert.Fail($"Expected recorded upload-session pool to resolve, got {error.Error}.")
        | Ok route ->
            Assert.That(route.RepositoryId, Is.EqualTo(repositoryId))
            Assert.That(route.StoragePoolId, Is.EqualTo(recordedPoolId))
            Assert.That(route.StoragePoolId, Is.Not.EqualTo(currentRepositoryPoolId))
            Assert.That(route.Shard.StorageAccountName, Is.EqualTo("cas-recorded"))
            Assert.That(route.Shard.StorageContainerName, Is.EqualTo(StorageContainerName "cas-recorded"))
            Assert.That(route.Shard.ObjectKeyPrefix, Is.EqualTo("recorded"))

    [<Test>]
    member _.UploadSessionRepositoryScopedDedupePoolResolvesToDefaultShardWithoutLosingRecordedPoolId() =
        let repositoryId = Guid.Parse("4b01c5b6-a12e-4f53-b7df-77034fa6c30f")
        let recordedPoolId = DedupeIndex.storagePoolIdForRepositoryId repositoryId

        match StoragePoolRouting.resolveStoragePoolRouteForStoragePoolId repositoryId recordedPoolId "corr-repository-dedupe-pool" with
        | Error error -> Assert.Fail($"Expected repository-scoped dedupe pool to resolve to the default shard, got {error.Error}.")
        | Ok route ->
            Assert.That(route.RepositoryId, Is.EqualTo(repositoryId))
            Assert.That(route.StoragePoolId, Is.EqualTo(recordedPoolId))
            Assert.That(route.StoragePoolId, Is.Not.EqualTo(StoragePoolRouting.defaultStoragePoolId))
            Assert.That(route.Shard.StorageAccountName, Is.EqualTo(AzureEnvironment.storageEndpoints.AccountName))
            Assert.That(route.Shard.StorageContainerName, Is.EqualTo(StorageContainerName Constants.DefaultCasStorageContainerName))
            Assert.That(route.Shard.ObjectKeyPrefix, Is.EqualTo(Constants.DefaultCasStoragePrefix))

    [<Test>]
    member _.ShardPlacementUsesSelectedAccountContainerAndPrefix() =
        let shard: StoragePoolRouting.StorageShard =
            { StorageAccountName = "cas-shard-a"; StorageContainerName = StorageContainerName "cas-a"; ObjectKeyPrefix = "/pool-a/" }

        let placement = StoragePoolRouting.storagePlacementForObjectKey shard "blocks/aa" (Some "etag-a")

        Assert.That(placement.StorageAccountName, Is.EqualTo("cas-shard-a"))
        Assert.That(placement.StorageContainerName, Is.EqualTo(StorageContainerName "cas-a"))
        Assert.That(placement.ObjectKey, Is.EqualTo("pool-a/blocks/aa"))
        Assert.That(placement.ETag, Is.EqualTo(Some "etag-a"))

    [<Test>]
    member _.SameObjectKeyInTwoStoragePoolsHasDistinctPlacementEvidence() =
        let objectKey = "blocks/same-address"

        let first =
            StoragePoolRouting.storagePlacementForObjectKey
                { StorageAccountName = "cas-shard-a"; StorageContainerName = StorageContainerName "cas-a"; ObjectKeyPrefix = "pool-a" }
                objectKey
                None

        let second =
            StoragePoolRouting.storagePlacementForObjectKey
                { StorageAccountName = "cas-shard-b"; StorageContainerName = StorageContainerName "cas-b"; ObjectKeyPrefix = "pool-b" }
                objectKey
                None

        Assert.That(first.ObjectKey.EndsWith(objectKey, StringComparison.Ordinal), Is.True)
        Assert.That(second.ObjectKey.EndsWith(objectKey, StringComparison.Ordinal), Is.True)
        Assert.That(first, Is.Not.EqualTo(second))

    [<Test>]
    member _.MissingShardContainerFailsClosed() =
        let shard: StoragePoolRouting.StorageShard =
            { StorageAccountName = "cas-shard-a"; StorageContainerName = StorageContainerName " "; ObjectKeyPrefix = String.Empty }

        match StoragePoolRouting.validateShard "corr-missing-shard-container" shard with
        | Ok () -> Assert.Fail("Expected missing shard container to fail closed.")
        | Error error ->
            Assert.That(error.Error, Is.EqualTo("StorageShard.StorageContainerName is required."))
            Assert.That(error.CorrelationId, Is.EqualTo("corr-missing-shard-container"))

    [<Test>]
    member _.SameAccountSharedKeyShardCanBeSigned() =
        let shard: StoragePoolRouting.StorageShard =
            {
                StorageAccountName = AzureEnvironment.storageEndpoints.AccountName
                StorageContainerName = StorageContainerName "cas-a"
                ObjectKeyPrefix = String.Empty
            }

        match StoragePoolRouting.validateShardForSharedKeySas "corr-same-account-shared-key" shard with
        | Ok () -> Assert.Pass()
        | Error error -> Assert.Fail($"Expected same-account shared-key shard to be accepted, got {error.Error}.")

    [<Test>]
    member _.CrossAccountSharedKeyShardFailsBeforeSasMinting() =
        let shard: StoragePoolRouting.StorageShard =
            {
                StorageAccountName = $"{AzureEnvironment.storageEndpoints.AccountName}-other"
                StorageContainerName = StorageContainerName "cas-a"
                ObjectKeyPrefix = String.Empty
            }

        match StoragePoolRouting.validateShardForSharedKeySas "corr-cross-account-shared-key" shard with
        | Ok () -> Assert.Fail("Expected cross-account shared-key shard to fail before SAS minting.")
        | Error error ->
            Assert.That(error.Error, Does.Contain("cannot be signed with the configured shared-key account"))
            Assert.That(error.CorrelationId, Is.EqualTo("corr-cross-account-shared-key"))

    [<Test>]
    member _.ManagedIdentityCasUserDelegationSasUsesSelectedShardAccountName() =
        let route: StoragePoolRouting.StoragePoolRoute =
            {
                RepositoryId = Guid.Parse("99999999-1111-2222-3333-444444444444")
                StoragePoolId = StoragePoolId "pool-custom-cname"
                Shard =
                    { StorageAccountName = "cas-shard-authority"; StorageContainerName = StorageContainerName "cas-a"; ObjectKeyPrefix = "pool-custom-cname" }
            }

        let signingAccount = Grace.Actors.Services.casUserDelegationSasSigningAccountName route

        Assert.That(signingAccount, Is.EqualTo(route.Shard.StorageAccountName))
        Assert.That(signingAccount, Is.Not.EqualTo(AzureEnvironment.storageEndpoints.AccountName))

    [<Test>]
    member _.ShardBlobHostReplacementPreservesPrivateLinkSuffix() =
        let host = Grace.Actors.Services.shardBlobHostForConfiguredHost "cas-shard-private" "primaryacct.privatelink.blob.core.windows.net"

        Assert.That(host, Is.EqualTo("cas-shard-private.privatelink.blob.core.windows.net"))

    [<Test>]
    member _.ShardBlobHostReplacementPreservesStandardBlobSuffix() =
        let host = Grace.Actors.Services.shardBlobHostForConfiguredHost "cas-shard-standard" "primaryacct.blob.core.windows.net"

        Assert.That(host, Is.EqualTo("cas-shard-standard.blob.core.windows.net"))

    [<Test>]
    member _.RepositoryDerivedBridgeRemainsRepositoryScopedUntilContentBlockPlacementUsesStoragePoolShard() =
        let firstRepositoryId = Guid.Parse("55555555-5555-5555-5555-555555555555")
        let secondRepositoryId = Guid.Parse("66666666-6666-6666-6666-666666666666")

        let firstPool = DedupeIndex.storagePoolIdForRepositoryId firstRepositoryId
        let secondPool = DedupeIndex.storagePoolIdForRepositoryId secondRepositoryId

        Assert.That(firstPool, Is.EqualTo(StoragePoolId $"{firstRepositoryId}"))
        Assert.That(secondPool, Is.EqualTo(StoragePoolId $"{secondRepositoryId}"))
        Assert.That(firstPool, Is.Not.EqualTo(secondPool))
        Assert.That(firstPool, Is.Not.EqualTo(StoragePoolRouting.defaultStoragePoolId))

    [<Test>]
    member _.RepositoryCreationKeepsWholeFileStorageContainerRepositoryScoped() =
        let repositoryId = Guid.Parse("77777777-7777-7777-7777-777777777777")

        let event =
            {
                Event =
                    RepositoryEventType.Created(
                        RepositoryName "whole-file-repo",
                        repositoryId,
                        Guid.Parse("88888888-8888-8888-8888-888888888888"),
                        Guid.Parse("99999999-9999-9999-9999-999999999999"),
                        ObjectStorageProvider.AzureBlobStorage
                    )
                Metadata = EventMetadata.New "corr-created" Constants.GraceSystemUser
            }

        let repository = RepositoryDto.UpdateDto event RepositoryDto.Default

        Assert.That(repository.StoragePoolId, Is.EqualTo(StoragePoolRouting.defaultStoragePoolId))
        Assert.That(repository.StorageContainerName, Is.EqualTo(StorageContainerName $"{repositoryId}"))
        Assert.That(repository.StorageContainerName, Is.Not.EqualTo(StorageContainerName Constants.DefaultCasStorageContainerName))
