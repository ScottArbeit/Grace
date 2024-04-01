namespace Grace.Actors

open Azure.Storage
open Azure.Storage.Blobs
open Azure.Storage.Sas
open Dapr.Actors
open Dapr.Actors.Runtime
open Dapr.Client
open FSharp.Control.Tasks.V2.ContextInsensitive
open Grace.Actors.Types
open Grace.Shared
open System
open System.Threading.Tasks
open System.Text.Json
open Grace.Shared.Configuration
open System.Collections.Concurrent

module StorageAccount =

    let GetActorId (storageProvider: ObjectStorageProvider) (name: StorageAccountName) =
        ActorId($"{storageProvider}{Constants.GraceNameDelimiter}{name}")

    type IStorageAccount =
        inherit IActor
        //abstract member GetBlobClient: string -> string -> Task<BlobClient>
        //abstract member GetConnectionString: unit -> Task<StorageConnectionString>
        //abstract member GetContainerClient: string -> Task<BlobContainerClient>
        //abstract member GetReadOnlyConnectionString: unit -> Task<StorageConnectionString>
        abstract member GetReadSharedAccessSignature:
            containerName: StorageContainerName -> relativePath: RelativePath -> Task<UriWithSharedAccessSignature>

        abstract member GetWriteSharedAccessSignature:
            containerName: StorageContainerName -> relativePath: RelativePath -> Task<UriWithSharedAccessSignature>

    type StorageAccount
        (host: ActorHost, name: StorageAccountName, storageProvider: ObjectStorageProvider, daprClient: DaprClient) =
        inherit Actor(host)

        //static let jsonSerializerOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        //static let daprClient = Dapr.Client.DaprClientBuilder().UseGrpcEndpoint().UseHttpEndpoint().UseJsonSerializationOptions(jsonSerializerOptions).Build()

        let containerClients = new ConcurrentDictionary<string, BlobContainerClient>()

        let retrieveSecret secretName =
            let secrets =
                daprClient
                    .GetSecretAsync(Constants.GraceSecretStoreName, secretName)
                    .GetAwaiter()
                    .GetResult()

            secrets.Item secretName

        let connectionString = retrieveSecret $"ConnectionString-{name}"
        let readOnlyConnectionString = retrieveSecret $"ReadOnlyConnectionString-{name}"

        let getContainerClient (containerName: string) =
            if containerClients.ContainsKey(containerName) then
                containerClients.[containerName]
            else
                let blobContainerClient = BlobContainerClient(connectionString, containerName)
                containerClients.[containerName] <- blobContainerClient
                blobContainerClient

        interface IStorageAccount with
            //member this.GetBlobClient(blobName: string, containerName: string) =
            //    let x = BlobClient(Uri(blobName),
            //    Task.FromResult(x)

            //member this.GetConnectionString() = Task.FromResult(connectionString)

            //member this.GetContainerClient (containerName: string) =
            //    Task.FromResult(getContainerClient containerName)

            //member this.GetReadOnlyConnectionString() = Task.FromResult(readOnlyConnectionString)

            member this.GetReadSharedAccessSignature
                (containerName: StorageContainerName)
                (relativePath: RelativePath)
                =
                match storageProvider with
                | AzureBlobStorage ->
                    Storage.AzureBlobStorage.GetReadSharedAccessSignature
                    let blobContainerClient = getContainerClient containerName

                    let blobSasBuilder =
                        BlobSasBuilder(
                            BlobSasPermissions.Read,
                            DateTimeOffset.UtcNow.AddMinutes(Constants.SharedAccessSignatureExpiration)
                        )

                    blobSasBuilder.BlobName <- relativePath.ToString()
                    Task.FromResult(blobContainerClient.GenerateSasUri(blobSasBuilder).ToString())
                | AWSS3 -> Task.FromResult(String.Empty)
                | GoogleCloudStorage -> Task.FromResult(String.Empty)
                | Nul -> Task.FromResult(String.Empty)

            member this.GetWriteSharedAccessSignature
                (containerName: StorageContainerName)
                (relativePath: RelativePath)
                =
                match storageProvider with
                | AzureBlobStorage ->
                    let blobContainerClient = getContainerClient containerName

                    let blobSasBuilder =
                        BlobSasBuilder(
                            BlobSasPermissions.Create ||| BlobSasPermissions.Write,
                            DateTimeOffset.UtcNow.AddMinutes(Constants.SharedAccessSignatureExpiration)
                        )

                    blobSasBuilder.BlobName <- $"{relativePath}"
                    Task.FromResult(blobContainerClient.GenerateSasUri(blobSasBuilder).ToString())
                | AWSS3 -> Task.FromResult(String.Empty)
                | GoogleCloudStorage -> Task.FromResult(String.Empty)
                | Nul -> Task.FromResult(String.Empty)
