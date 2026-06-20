namespace Grace.Server.UnitTests

open Grace.Shared
open Grace.Types.Common
open Grace.Types.Repository
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
type StoragePoolRoutingServerTests() =

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
        Environment.SetEnvironmentVariable(Constants.EnvironmentVariables.DebugEnvironment, "Local")

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
    member _.RepositoryDerivedBridgeNoLongerUsesRepositoryIdAsStoragePoolId() =
        let firstRepositoryId = Guid.Parse("55555555-5555-5555-5555-555555555555")
        let secondRepositoryId = Guid.Parse("66666666-6666-6666-6666-666666666666")

        let firstPool = DedupeIndex.storagePoolIdForRepositoryId firstRepositoryId
        let secondPool = DedupeIndex.storagePoolIdForRepositoryId secondRepositoryId

        Assert.That(firstPool, Is.EqualTo(StoragePoolRouting.defaultStoragePoolId))
        Assert.That(secondPool, Is.EqualTo(firstPool))
        Assert.That(firstPool, Is.Not.EqualTo(StoragePoolId $"{firstRepositoryId}"))
        Assert.That(secondPool, Is.Not.EqualTo(StoragePoolId $"{secondRepositoryId}"))

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
