namespace Grace.CLI.Tests

open Grace.SDK
open NUnit.Framework
open System

[<Parallelizable(ParallelScope.All)>]
type StorageSdkTests() =

    [<Test>]
    member _.ContentBlockPlacementFromUriUsesConfiguredAccountForCustomBlobEndpoint() =
        let placement =
            Storage.contentBlockPlacementFromUriUsingConfiguredEndpoint
                (Uri "https://cas.example.test")
                "configured-account"
                (Uri "https://cas.example.test/cas-container/cas/content/aaaaaaaa?sig=fake")
                (Some "etag-custom")

        Assert.That(placement.StorageAccountName, Is.EqualTo("configured-account"))
        Assert.That(string placement.StorageContainerName, Is.EqualTo("cas-container"))
        Assert.That(placement.ObjectKey, Is.EqualTo("cas/content/aaaaaaaa"))
        Assert.That(placement.ETag, Is.EqualTo(Some "etag-custom"))

    [<Test>]
    member _.ContentBlockPlacementFromProductionUploadUriUsesShardEvidenceFragmentForCustomBlobEndpoint() =
        let placement =
            Storage.contentBlockPlacementFromUri
                (Uri "https://cas.example.test/cas-container/staging/upload-sessions/session/content-blocks/aaaaaaaa?sig=fake#graceStorageAccount=cas-shard-a")
                (Some "etag-staged")

        Assert.That(placement.StorageAccountName, Is.EqualTo("cas-shard-a"))
        Assert.That(string placement.StorageContainerName, Is.EqualTo("cas-container"))
        Assert.That(placement.ObjectKey, Is.EqualTo("staging/upload-sessions/session/content-blocks/aaaaaaaa"))
        Assert.That(placement.ETag, Is.EqualTo(Some "etag-staged"))

    [<Test>]
    member _.ContentBlockPlacementFromIpCustomEndpointPrefersShardFragmentBeforePathStyleParsing() =
        let placement =
            Storage.contentBlockPlacementFromUriUsingConfiguredEndpoint
                (Uri "https://10.10.0.15")
                "configured-account"
                (Uri "https://10.10.0.15/cas-container/staging/upload-sessions/session/content-blocks/aaaaaaaa?sig=fake#graceStorageAccount=cas-shard-ip")
                (Some "etag-ip")

        Assert.That(placement.StorageAccountName, Is.EqualTo("cas-shard-ip"))
        Assert.That(string placement.StorageContainerName, Is.EqualTo("cas-container"))
        Assert.That(placement.ObjectKey, Is.EqualTo("staging/upload-sessions/session/content-blocks/aaaaaaaa"))
        Assert.That(placement.ETag, Is.EqualTo(Some "etag-ip"))

    [<Test>]
    member _.ContentBlockPlacementFromAzuriteUploadUriKeepsPathStyleParsingWithShardFragment() =
        let placement =
            Storage.contentBlockPlacementFromUriUsingConfiguredEndpoint
                (Uri "http://127.0.0.1:10000/devstoreaccount1")
                "configured-account"
                (Uri
                    "http://127.0.0.1:10000/devstoreaccount1/cas-container/staging/upload-sessions/session/content-blocks/aaaaaaaa?sig=fake#graceStorageAccount=devstoreaccount1")
                None

        Assert.That(placement.StorageAccountName, Is.EqualTo("devstoreaccount1"))
        Assert.That(string placement.StorageContainerName, Is.EqualTo("cas-container"))
        Assert.That(placement.ObjectKey, Is.EqualTo("staging/upload-sessions/session/content-blocks/aaaaaaaa"))

    [<Test>]
    member _.ContentBlockPlacementFromUriDoesNotInferAccountFromArbitraryCustomHost() =
        let placement =
            Storage.contentBlockPlacementFromUriUsingConfiguredEndpoint
                (Uri "https://configured.example.test")
                "configured-account"
                (Uri "https://tenant-storage.example.test/cas-container/cas/content/bbbbbbbb?sig=fake")
                None

        Assert.That(placement.StorageAccountName, Is.EqualTo(String.Empty))
        Assert.That(string placement.StorageContainerName, Is.EqualTo("cas-container"))
        Assert.That(placement.ObjectKey, Is.EqualTo("cas/content/bbbbbbbb"))
        Assert.That(placement.ETag, Is.EqualTo(None))

    [<Test>]
    member _.ContentBlockPlacementFromUriKeepsAzureAndAzuriteAccountEvidence() =
        let azurePlacement =
            Storage.contentBlockPlacementFromUriUsingConfiguredEndpoint
                (Uri "https://configured.example.test")
                "configured-account"
                (Uri "https://shardaccount.blob.core.windows.net/cas-container/cas/content/cccccccc?sig=fake")
                None

        let azuritePlacement =
            Storage.contentBlockPlacementFromUriUsingConfiguredEndpoint
                (Uri "http://127.0.0.1:10000/devstoreaccount1")
                "configured-account"
                (Uri "http://127.0.0.1:10000/devstoreaccount1/cas-container/cas/content/dddddddd?sig=fake")
                None

        Assert.That(azurePlacement.StorageAccountName, Is.EqualTo("shardaccount"))
        Assert.That(string azurePlacement.StorageContainerName, Is.EqualTo("cas-container"))
        Assert.That(azurePlacement.ObjectKey, Is.EqualTo("cas/content/cccccccc"))
        Assert.That(azuritePlacement.StorageAccountName, Is.EqualTo("devstoreaccount1"))
        Assert.That(string azuritePlacement.StorageContainerName, Is.EqualTo("cas-container"))
        Assert.That(azuritePlacement.ObjectKey, Is.EqualTo("cas/content/dddddddd"))

    [<Test>]
    member _.ContentBlockPlacementFromUriKeepsStandardAccountEvidenceWithoutConfiguredEndpoint() =
        let placement =
            Storage.contentBlockPlacementFromUriUsingConfiguredEndpoint
                null
                String.Empty
                (Uri "https://shardaccount.blob.core.windows.net/cas-container/cas/content/eeeeeeee?sig=fake")
                (Some "etag-standard")

        Assert.That(placement.StorageAccountName, Is.EqualTo("shardaccount"))
        Assert.That(string placement.StorageContainerName, Is.EqualTo("cas-container"))
        Assert.That(placement.ObjectKey, Is.EqualTo("cas/content/eeeeeeee"))
        Assert.That(placement.ETag, Is.EqualTo(Some "etag-standard"))
