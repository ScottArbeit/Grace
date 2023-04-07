namespace Grace.Actors

open Azure.Storage
open Azure.Storage.Blobs
open Azure.Storage.Sas
open Dapr.Actors
open Dapr.Actors.Client
open Dapr.Client
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Shared.Dto.Repository
open Grace.Shared.Types
open Grace.Shared.Utilities
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Linq
open System.Threading.Tasks
open Services

module Services =

    type ServerGraceIndex = Dictionary<RelativePath, DirectoryVersion>

    let repositoryContainerNameCache = ConcurrentDictionary<Guid, string>()
    let containerClients = new ConcurrentDictionary<string, BlobContainerClient>()
    
    let daprHttpEndpoint = $"{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprHttpPort)}"
    let daprGrpcEndpoint = $"{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprServerUri)}:{Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.DaprGrpcPort)}"
    let daprClient = DaprClientBuilder().UseJsonSerializationOptions(Constants.JsonSerializerOptions).UseHttpEndpoint(daprHttpEndpoint).UseGrpcEndpoint(daprGrpcEndpoint).Build()
    let azureStorageConnectionString =
        (task {
            let! secret = daprClient.GetSecretAsync(Constants.GraceSecretStoreName, "AzureStorageConnectionString")
            return secret.First().Value
        }).Result

    let private storageKey = 
        (task {
            let! secret = daprClient.GetSecretAsync(Constants.GraceSecretStoreName, "AzureStorageKey")
            return secret.First().Value
         }).Result
    let private sharedKeyCredential = StorageSharedKeyCredential(defaultObjectStorageAccount, storageKey)

    //let actorProxyOptions = ActorProxyOptions(JsonSerializerOptions = Constants.JsonSerializerOptions, HttpEndpoint = daprEndpoint)
    //let ActorProxyFactory = ActorProxyFactory(actorProxyOptions)

    let mutable ActorProxyFactory: IActorProxyFactory = null
    let setActorProxyFactory proxyFactory =
        ActorProxyFactory <- proxyFactory

    let getContainerClient (storageAccountName: StorageAccountName) (containerName: StorageContainerName) =
        task {
            let key = $"{storageAccountName}-{containerName}"
            if containerClients.ContainsKey(key) then
                return containerClients[key]
            else
                let blobContainerClient = BlobContainerClient(azureStorageConnectionString, $"{containerName}")
                let! azureResponse = blobContainerClient.CreateIfNotExistsAsync(publicAccessType = Models.PublicAccessType.None)
                containerClients[key] <- blobContainerClient
                return blobContainerClient
        }

    let getAzureBlobClient (repositoryDto: RepositoryDto) (fileVersion: FileVersion) = 
        task {
            //logToConsole $"* In getAzureBlobClient; repositoryId: {repositoryDto.RepositoryId}; fileVersion: {fileVersion.RelativePath}."
            let containerNameActorId = ActorId($"{repositoryDto.RepositoryId}")
            let containerNameActorProxy = ActorProxyFactory.CreateActorProxy<IContainerNameActor>(containerNameActorId, ActorName.ContainerName)
            let! containerName = containerNameActorProxy.GetContainerName()
            match containerName with
            | Ok containerName ->
                let! containerClient = getContainerClient repositoryDto.StorageAccountName containerName
                let blobClient = containerClient.GetBlobClient($"{fileVersion.RelativePath}/{fileVersion.GetObjectFileName}")
                return Ok blobClient
            | Error error ->
                return Error error
        }

    let private createAzureBlobSasUri (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (permission: BlobSasPermissions) =
        task {
            //logToConsole $"In createAzureBlobSasUri; fileVersion.RelativePath: {fileVersion.RelativePath}."
            let containerNameActorId = ActorId($"{repositoryDto.RepositoryId}")
            let containerNameActorProxy = ActorProxyFactory.CreateActorProxy<IContainerNameActor>(containerNameActorId, ActorName.ContainerName)
            let! containerName = containerNameActorProxy.GetContainerName()
            //logToConsole $"containerName: {containerName}."
            match containerName with
            | Ok containerName ->
                let! blobContainerClient = getContainerClient repositoryDto.StorageAccountName containerName
            
                let blobSasBuilder = BlobSasBuilder(permission, DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(Constants.SharedAccessSignatureExpiration)))
                blobSasBuilder.BlobName <- Path.Combine($"{fileVersion.RelativePath}", fileVersion.GetObjectFileName)
                blobSasBuilder.BlobContainerName <- containerName
                blobSasBuilder.StartsOn <- DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(Constants.SharedAccessSignatureExpiration))
                let sasUriParameters = blobSasBuilder.ToSasQueryParameters(sharedKeyCredential)
                return Ok $"{blobContainerClient.Uri}/{fileVersion.RelativePath}/{fileVersion.GetObjectFileName}?{sasUriParameters}"
            | Error error -> return Error error
        }

    let getReadSharedAccessSignature (repositoryDto: RepositoryDto) (fileVersion: FileVersion) =
        task {
            match repositoryDto.ObjectStorageProvider with
                | AzureBlobStorage ->
                    let! sas = createAzureBlobSasUri repositoryDto fileVersion (BlobSasPermissions.Read ||| BlobSasPermissions.List)
                    match sas with
                    | Ok sas -> return Ok (sas.ToString())
                    | Error error -> return Error error
                | AWSS3 ->
                    return Error "Not implemented"
                | GoogleCloudStorage ->
                    return Error "Not implemented"
                | ObjectStorageProvider.Unknown ->
                    return Error "Not implemented"
        }

    let getWriteSharedAccessSignature (repositoryDto: RepositoryDto) (fileVersion: FileVersion) =
        task {
            match repositoryDto.ObjectStorageProvider with
                | AWSS3 ->
                    return Uri("http://localhost:3500")
                | AzureBlobStorage ->
                    // Adding read permission to allow for calls to .ExistsAsync().
                    let! sas = createAzureBlobSasUri repositoryDto fileVersion (BlobSasPermissions.Create ||| BlobSasPermissions.Write ||| BlobSasPermissions.Move  ||| BlobSasPermissions.Tag ||| BlobSasPermissions.Read)
                    match sas with 
                    | Ok sas -> 
                        //logToConsole $"In Actor.Services.getWriteSharedAccessSignature; {sas}"
                        return Uri(sas)
                    | Error error -> 
                        //logToConsole $"In Actor.Services.getWriteSharedAccessSignature; {error}"
                        return Uri("http://localhost")
                | GoogleCloudStorage ->
                    return Uri("http://localhost:3500")
                | ObjectStorageProvider.Unknown ->
                    return Uri("http://localhost:3500")
        }
