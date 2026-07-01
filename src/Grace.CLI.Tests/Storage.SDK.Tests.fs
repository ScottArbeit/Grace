namespace Grace.CLI.Tests

open Grace.SDK
open NUnit.Framework
open System

/// Exercises storage sdk behavior.
[<Parallelizable(ParallelScope.All)>]
type StorageSdkTests() =

    /// Verifies that content block placement from uri uses configured account for custom blob endpoint.
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

    /// Verifies that content block placement from production upload uri uses shard evidence fragment for custom blob endpoint.
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

    /// Verifies that content block placement from cname endpoint uses shard evidence fragment.
    [<Test>]
    member _.ContentBlockPlacementFromCnameEndpointUsesShardEvidenceFragment() =
        let placement =
            Storage.contentBlockPlacementFromUriUsingConfiguredEndpoint
                (Uri "https://cas.cdn.example.test")
                "configured-account"
                (Uri "https://cas.cdn.example.test/cas-container/cas/content/bbbbbbbb?sig=fake#graceStorageAccount=cas-cname-shard")
                (Some "etag-cname")

        Assert.That(placement.StorageAccountName, Is.EqualTo("cas-cname-shard"))
        Assert.That(string placement.StorageContainerName, Is.EqualTo("cas-container"))
        Assert.That(placement.ObjectKey, Is.EqualTo("cas/content/bbbbbbbb"))
        Assert.That(placement.ETag, Is.EqualTo(Some "etag-cname"))

    /// Verifies that content block placement from private link endpoint keeps private host and shard evidence.
    [<Test>]
    member _.ContentBlockPlacementFromPrivateLinkEndpointKeepsPrivateHostAndShardEvidence() =
        let placement =
            Storage.contentBlockPlacementFromUriUsingConfiguredEndpoint
                (Uri "https://cas-shard-a.privatelink.blob.core.windows.net")
                "configured-account"
                (Uri "https://cas-shard-a.privatelink.blob.core.windows.net/cas-container/cas/content/cccccccc?sig=fake#graceStorageAccount=cas-private-shard")
                None

        Assert.That(placement.StorageAccountName, Is.EqualTo("cas-private-shard"))
        Assert.That(string placement.StorageContainerName, Is.EqualTo("cas-container"))
        Assert.That(placement.ObjectKey, Is.EqualTo("cas/content/cccccccc"))

    /// Verifies that content block placement from ip custom endpoint prefers shard fragment before path style parsing.
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

    /// Verifies that content block placement from azurite upload uri keeps path style parsing with shard fragment.
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

    /// Verifies that content block placement from uri does not infer account from arbitrary custom host.
    [<Test>]
    member _.ContentBlockPlacementFromUriDoesNotInferAccountFromArbitraryCustomHost() =
        let placement =
            Storage.contentBlockPlacementFromUriUsingConfiguredEndpoint
                (Uri "https://configured.example.test")
                "configured-account"
                (Uri "https://tenant-storage.example.test/cas-container/cas/content/dddddddd?sig=fake")
                None

        Assert.That(placement.StorageAccountName, Is.EqualTo(String.Empty))
        Assert.That(string placement.StorageContainerName, Is.EqualTo("cas-container"))
        Assert.That(placement.ObjectKey, Is.EqualTo("cas/content/dddddddd"))
        Assert.That(placement.ETag, Is.EqualTo(None))

    /// Verifies that content block placement from uri keeps azure and azurite account evidence.
    [<Test>]
    member _.ContentBlockPlacementFromUriKeepsAzureAndAzuriteAccountEvidence() =
        let azurePlacement =
            Storage.contentBlockPlacementFromUriUsingConfiguredEndpoint
                (Uri "https://configured.example.test")
                "configured-account"
                (Uri "https://shardaccount.blob.core.windows.net/cas-container/cas/content/eeeeeeee?sig=fake")
                None

        let azuritePlacement =
            Storage.contentBlockPlacementFromUriUsingConfiguredEndpoint
                (Uri "http://127.0.0.1:10000/devstoreaccount1")
                "configured-account"
                (Uri "http://127.0.0.1:10000/devstoreaccount1/cas-container/cas/content/ffffffff?sig=fake")
                None

        Assert.That(azurePlacement.StorageAccountName, Is.EqualTo("shardaccount"))
        Assert.That(string azurePlacement.StorageContainerName, Is.EqualTo("cas-container"))
        Assert.That(azurePlacement.ObjectKey, Is.EqualTo("cas/content/eeeeeeee"))
        Assert.That(azuritePlacement.StorageAccountName, Is.EqualTo("devstoreaccount1"))
        Assert.That(string azuritePlacement.StorageContainerName, Is.EqualTo("cas-container"))
        Assert.That(azuritePlacement.ObjectKey, Is.EqualTo("cas/content/ffffffff"))

    /// Verifies that content block placement from uri keeps standard account evidence without configured endpoint.
    [<Test>]
    member _.ContentBlockPlacementFromUriKeepsStandardAccountEvidenceWithoutConfiguredEndpoint() =
        let placement =
            Storage.contentBlockPlacementFromUriUsingConfiguredEndpoint
                null
                String.Empty
                (Uri "https://shardaccount.blob.core.windows.net/cas-container/cas/content/99999999?sig=fake")
                (Some "etag-standard")

        Assert.That(placement.StorageAccountName, Is.EqualTo("shardaccount"))
        Assert.That(string placement.StorageContainerName, Is.EqualTo("cas-container"))
        Assert.That(placement.ObjectKey, Is.EqualTo("cas/content/99999999"))
        Assert.That(placement.ETag, Is.EqualTo(Some "etag-standard"))
