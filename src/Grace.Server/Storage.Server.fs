namespace Grace.Server

open Azure.Core
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Azure.Storage.Sas
open Dapr.Actors.Client
open Giraffe
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Server.Services
open Grace.Shared.Dto.Repository
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Validation.Errors.Storage
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Linq
open System.Threading.Tasks
open System.IO
open System.Text
open Azure.Storage
open System.Diagnostics

module Storage =

    /// Checks if a file version exists in the object storage provider.
    let fileExists (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (context: HttpContext) =
        task {
            match repositoryDto.ObjectStorageProvider with
                | AzureBlobStorage ->
                    let! blobClient = getAzureBlobClient repositoryDto fileVersion (getCorrelationId context)
                    match blobClient with
                    | Ok blobClient ->
                        let! azureResponse = blobClient.ExistsAsync()
                        return azureResponse.Value
                    | Error error -> 
                        return false
                | AWSS3 ->
                    return false
                | GoogleCloudStorage ->
                    return false
                | ObjectStorageProvider.Unknown ->
                    logToConsole $"Error: Unknown ObjectStorageProvider in fileExists for repository {repositoryDto.RepositoryId} - {repositoryDto.RepositoryName}."
                    logToConsole (sprintf "%A" repositoryDto)
                    return false
        }

    /// Gets the metadata stored in the object storage provider for the specified file.
    let getFileMetadata (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (context: HttpContext) =
        task {
            match repositoryDto.ObjectStorageProvider with
                | AzureBlobStorage ->
                    let! blobClient = getAzureBlobClient repositoryDto fileVersion (getCorrelationId context)
                    match blobClient with
                    | Ok blobClient ->
                        let! azureResponse = blobClient.GetPropertiesAsync()
                        let blobProperties = azureResponse.Value
                        return Ok (blobProperties.Metadata :?> IReadOnlyDictionary<string, string>)
                    | Error error -> return Error error
                | AWSS3 -> return Error (StorageError.getErrorMessage NotImplemented)
                | GoogleCloudStorage -> return Error (StorageError.getErrorMessage NotImplemented)
                | ObjectStorageProvider.Unknown ->
                    logToConsole $"Error: Unknown ObjectStorageProvider in getFileMetadata for repository {repositoryDto.RepositoryId} - {repositoryDto.RepositoryName}."
                    logToConsole (sprintf "%A" repositoryDto)
                    return Error (StorageError.getErrorMessage StorageError.UnknownObjectStorageProvider)
        }

    /// Gets a download URI for the specified file version that can be used by a Grace client.
    let GetDownloadUri: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let! fileVersion = context.BindJsonAsync<FileVersion>()
                    let repositoryActor = Repository.getActorProxy $"{fileVersion.RepositoryId}"
                    let! repositoryDto = repositoryActor.Get (getCorrelationId context)
                    match! getReadSharedAccessSignature repositoryDto fileVersion (getCorrelationId context) with
                    | Ok downloadUri ->
                        context.SetStatusCode StatusCodes.Status200OK
                        //context.GetLogger().LogTrace("fileVersion: {fileVersion.RelativePath}; downloadUri: {downloadUri}", [| fileVersion.RelativePath, downloadUri |])
                        logToConsole $"fileVersion: {fileVersion.RelativePath}; downloadUri: {downloadUri}"
                        return! context.WriteStringAsync $"{downloadUri}"
                    | Error error ->
                        context.SetStatusCode StatusCodes.Status500InternalServerError
                        logToConsole $"Error generating download Uri: fileVersion: {fileVersion.RelativePath}; Error: {error}"
                        return! context.WriteStringAsync $"Error creating download uri for {fileVersion.GetObjectFileName}."
                with ex ->
                    context.SetStatusCode StatusCodes.Status500InternalServerError
                    return! context.WriteTextAsync $"Error in {context.Request.Path} at {DateTime.Now.ToLongTimeString()}."
            }

    /// Gets an upload URI for the specified file version that can be used by a Grace client.
    let GetUploadUri: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let! fileVersion = context.BindJsonAsync<FileVersion>()
                    let repositoryActor = Repository.getActorProxy $"{fileVersion.RepositoryId}"
                    let! repositoryDto = repositoryActor.Get (getCorrelationId context)
                    let! uploadUri = getWriteSharedAccessSignature repositoryDto fileVersion (getCorrelationId context)
                    context.SetStatusCode StatusCodes.Status200OK
                    log.LogDebug("In GetUploadUri(): fileVersion.RelativePath: {relativePath}; uploadUri: {uploadUri}", fileVersion.RelativePath, uploadUri)
                    return! context.WriteStringAsync $"{uploadUri}"
                with ex ->
                    context.SetStatusCode StatusCodes.Status500InternalServerError
                    logToConsole $"Exception in GetUploadUri: {(createExceptionResponse ex)}"
                    return! context.WriteTextAsync $"{getCurrentInstantExtended()} Error in {context.Request.Path} at {DateTime.Now.ToLongTimeString()}."
            }

    /// Checks if a list of files already exists in object storage, and if any do not, return a URL that the client can use to upload the file.
    let FilesExistInObjectStorage: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                try
                    let! fileVersions = context.BindJsonAsync<List<FileVersion>>()
                    Activity.Current.SetTag("fileVersions.Count", $"{fileVersions.Count}") |> ignore
                    if fileVersions.Count > 0 then
                        let repositoryActor = Repository.getActorProxy $"{fileVersions[0].RepositoryId}"
                        let! repositoryDto = repositoryActor.Get (getCorrelationId context)

                        let uploadMetadata = ConcurrentQueue<UploadMetadata>()
                        do! Parallel.ForEachAsync(fileVersions, Constants.ParallelOptions, (fun fileVersion ct ->
                            ValueTask(task {
                                let! fileExists = fileExists repositoryDto fileVersion context
                                if not <| fileExists then
                                    let! blobUriWithSasToken = getWriteSharedAccessSignature repositoryDto fileVersion (getCorrelationId context)
                                    uploadMetadata.Enqueue({BlobUriWithSasToken = blobUriWithSasToken; Sha256Hash = fileVersion.Sha256Hash})
                            })))
                        Activity.Current.SetTag("uploadMetadata.Count", $"{uploadMetadata.Count}") |> ignore
                        context.GetLogger().LogInformation($"{getCurrentInstantExtended()} Received {fileVersions.Count} FileVersions; Returning {uploadMetadata.Count} uploadMetadata records.")
                        return! context |> result200Ok (GraceReturnValue.Create (uploadMetadata.ToList()) (getCorrelationId context))
                    else
                        return! context |> result400BadRequest (GraceError.Create (StorageError.getErrorMessage FilesMustNotBeEmpty) (getCorrelationId context))
                with ex ->
                    return! context |> result500ServerError (GraceError.Create (StorageError.getErrorMessage ObjectStorageException) (getCorrelationId context))
            }

    /// Deletes all documents from Cosmos DB. After calling, the web connection will time-out, but the method will continue to run until Cosmos DB is empty.
    ///
    /// **** This method is implemented only in Debug configuration. It is a no-op in Release configuration. ****
    let DeleteAllFromCosmosDB: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
            #if DEBUG
                context.GetLogger().LogWarning("{CurrentInstant} Deleting all rows from Cosmos DB.", getCurrentInstantExtended())
                let! failed = Services.deleteAllFromCosmosDB()
                if failed.Count = 0 then    
                    context.GetLogger().LogWarning("{CurrentInstant} Deleted all rows from Cosmos DB.", getCurrentInstantExtended())
                    return! context |> result200Ok (GraceReturnValue.Create "Deleted all rows from Cosmos DB." (getCorrelationId context))
                else
                    let sb = StringBuilder()
                    for fail in failed do sb.AppendLine(fail) |> ignore
                    return! context |> result500ServerError (GraceError.Create $"Failed to delete all rows from Cosmos DB. Failures: {failed.Count}.{Environment.NewLine}{sb.ToString()}" (getCorrelationId context))
            #else
                return! context |> result404NotFound
            #endif
            }
