namespace Grace.Server.Tests

open Azure.Data.Tables
open Grace.Actors
open Grace.Actors.Interfaces
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types
open Grace.Types.Common
open Grace.Types.ManifestContributionAccounting
open Grace.Types.RepositoryContentCounter
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open NUnit.Framework
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.Serialization
open System
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

/// Proves `GET /usage/storage` end to end on the Aspire host: a real save moves the storage meter, the same content on
/// another branch does not, and releasing the last reference returns the reading to zero.
[<NonParallelizable>]
type StorageUsageAspireTests() =

    /// Reads the manifest-backed bytes for one repository through the public route as the default test caller.
    let readManifestBytes (repositoryId: string) =
        task {
            use! response = Client.GetAsync($"/usage/storage?OwnerId={ownerId}&OrganizationId={organizationId}&RepositoryId={repositoryId}")
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.OK, body)
            use document = JsonDocument.Parse body
            let usage = document.RootElement.GetProperty("ReturnValue")
            Assert.That(usage.GetProperty("Coverage").GetString(), Is.EqualTo "partial")
            let manifestClass = usage.GetProperty("ContentClasses")[0]

            Assert.That(
                manifestClass
                    .GetProperty("ContentClass")
                    .GetString(),
                Is.EqualTo "manifestBackedFiles"
            )

            let bytes = manifestClass.GetProperty("RetainedLogicalBytes")
            Assert.That(bytes.ValueKind, Is.EqualTo JsonValueKind.Number, "A counted class must read as a number, never as missing.")
            return bytes.GetInt64()
        }

    /// Saves one root holding the manifest-backed file at `directoryName` on the branch, and waits for accounting.
    let saveManifestFile
        (state: TestHostState)
        (repositoryId: string)
        (branch: Branch.BranchDto)
        (directoryName: string)
        (fileVersion: FileVersion)
        (manifest: FileManifest)
        =
        task {
            let enableSave = Parameters.Branch.EnableFeatureParameters()
            enableSave.OwnerId <- ownerId
            enableSave.OrganizationId <- organizationId
            enableSave.RepositoryId <- repositoryId
            enableSave.BranchId <- string branch.BranchId
            enableSave.Enabled <- true
            enableSave.CorrelationId <- generateCorrelationId ()
            use! enableResponse = Client.PostAsync("/branch/enableSave", createJsonContent enableSave)
            let! enableBody = enableResponse.Content.ReadAsStringAsync()
            Assert.That(enableResponse.StatusCode, Is.EqualTo HttpStatusCode.OK, enableBody)

            let directory = BranchServerTestHelpers.createDirectoryVersionWithFile repositoryId (RelativePath directoryName) fileVersion
            let root = BranchServerTestHelpers.createDirectoryVersion (Guid.NewGuid()) repositoryId Constants.RootDirectoryPath [ directory ]
            do! BranchServerTestHelpers.saveDirectoryVersionsAsync repositoryId [ directory; root ]
            use! response = BranchServerTestHelpers.saveReferenceResponseAsync repositoryId branch root.DirectoryVersionId root.Sha256Hash
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo HttpStatusCode.OK, body)

            // The relationship is written after the counter applied the add, so its presence means the counter moved.

            do!
                AspireTestHost.waitForExactRelationshipAsync
                    state
                    (ExactRelationship.DirectoryVersionManifest
                        {
                            RepositoryId = Guid.Parse repositoryId
                            StoragePoolId = manifest.StoragePoolId
                            ManifestAddress = manifest.ManifestAddress
                            DirectoryVersionId = directory.DirectoryVersionId
                        })

            return directory
        }

    /// Sends counter commands through a real Orleans client connected to the hosted silo.
    let withCounter (state: TestHostState) (repositoryId: string) (manifest: FileManifest) (send: IRepositoryContentCounterActor -> Task) =
        task {
            let! azureStorageConnectionString, clusterId, serviceId = AspireTestHost.getOrleansClientConfigurationAsync state

            use host =
                Host
                    .CreateDefaultBuilder()
                    .UseOrleansClient(fun clientBuilder ->
                        clientBuilder.Configure<ClusterOptions> (fun (options: ClusterOptions) ->
                            options.ClusterId <- clusterId
                            options.ServiceId <- serviceId)
                        |> ignore

                        clientBuilder.UseAzureStorageClustering (fun (options: Orleans.Clustering.AzureStorage.AzureStorageGatewayOptions) ->
                            options.TableServiceClient <- TableServiceClient(azureStorageConnectionString))
                        |> ignore

                        clientBuilder.Services.AddSerializer (fun serializerBuilder ->
                            serializerBuilder.AddJsonSerializer(
                                isSupported =
                                    (fun valueType ->
                                        not (String.IsNullOrEmpty valueType.Namespace)
                                        && valueType.Namespace.StartsWith("Grace", StringComparison.InvariantCulture)),
                                jsonSerializerOptions = Constants.JsonSerializerOptions
                            )
                            |> ignore)
                        |> ignore)
                    .Build()

            use startTimeout = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(20.0))
            do! host.StartAsync(startTimeout.Token)
            let client = host.Services.GetRequiredService<IClusterClient>()

            let counter =
                client.GetGrain<IRepositoryContentCounterActor>(
                    RepositoryContentCounter.primaryKey (Guid.Parse repositoryId) manifest.StoragePoolId manifest.ManifestAddress
                )

            do! send counter
            do! host.StopAsync()
        }

    /// Done-when checks: a save shows the file's bytes, another branch with the same content leaves them unchanged, and
    /// releasing the last reference reads zero as a number.
    [<Test>]
    member _.``usage follows a manifest-backed save, ignores a second branch, and returns to zero``() =
        task {
            let! state = AspireTestHost.startAsync testUserId
            let! repositoryId = RestartDurabilityHelpers.createRepositoryAsync "storage-usage"
            let! initialBytes = readManifestBytes repositoryId
            Assert.That(initialBytes, Is.EqualTo 0L, "A new repository keeps nothing yet, and reads zero.")

            let! branches = BranchServerTestHelpers.getRepositoryBranchesAsync repositoryId
            let mainBranch = branches |> Seq.head

            let! uploadCorrelationId, uploadSessionId, _, manifest = RestartDurabilityHelpers.createConfirmedUploadSessionAsync repositoryId
            let! _ = RestartDurabilityHelpers.finalizeManifestUploadAsync repositoryId uploadCorrelationId uploadSessionId manifest

            let fileVersion =
                FileVersion.CreateWithHashes
                    (RelativePath(RestartDurabilityHelpers.exactUploadScope uploadSessionId))
                    (Sha256Hash(String.replicate 64 "e"))
                    (Blake3Hash manifest.FileContentHash)
                    String.Empty
                    true
                    manifest.Size

            fileVersion.ContentReference <- FileContentReference.FileManifest manifest

            let! mainDirectory = saveManifestFile state repositoryId mainBranch "kept" fileVersion manifest
            let! afterSave = readManifestBytes repositoryId
            Assert.That(afterSave, Is.EqualTo manifest.Size)

            let! otherBranch = BranchServerTestHelpers.createBranchAsync repositoryId mainBranch $"usage-{Guid.NewGuid():N}"
            let! otherDirectory = saveManifestFile state repositoryId otherBranch "copy" fileVersion manifest
            let! afterSecondBranch = readManifestBytes repositoryId
            Assert.That(afterSecondBranch, Is.EqualTo manifest.Size, "The same content on another branch is kept once.")

            // Nothing on main reclaims a DirectoryVersion yet; these are the RemoveReference commands its physical
            // deletion sends to the counter when it releases each manifest relationship.
            let metadata = EventMetadata.New (generateCorrelationId ()) "storage-usage-test"

            do!
                withCounter state repositoryId manifest (fun counter ->
                    task {
                        /// Releases one DirectoryVersion's manifest relationship, as its physical deletion does.
                        let release (directory: DirectoryVersion) =
                            task {
                                let operationId =
                                    RepositoryContentCounterOperationId
                                        $"directory-version:{directory.DirectoryVersionId:N}:{manifest.StoragePoolId}:{manifest.ManifestAddress}:remove"

                                let command =
                                    RepositoryContentCounterCommand.RemoveReference(
                                        operationId,
                                        Guid.Parse repositoryId,
                                        manifest.StoragePoolId,
                                        manifest.ManifestAddress
                                    )

                                match! counter.Handle command metadata with
                                | Ok _ -> ()
                                | Error error -> Assert.Fail error.Error
                            }

                        do! release mainDirectory
                        do! release otherDirectory
                    })

            let! afterRelease = readManifestBytes repositoryId
            Assert.That(afterRelease, Is.EqualTo 0L, "Releasing the last reference reads zero, not missing.")
        }
