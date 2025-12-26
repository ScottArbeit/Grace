namespace Grace.Server

open Azure.Core
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Azure.Storage.Sas
open Giraffe
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Services
open Grace.Server.ApplicationContext
open Grace.Server.Services
open Grace.Shared.Parameters.Storage
open Grace.Shared.Utilities
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Validation.Errors
open Grace.Types.Repository
open Grace.Types.Types
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
open System.Reflection.Metadata
open System.Net.Http.Json

module Storage =

    let log = ApplicationContext.loggerFactory.CreateLogger("Storage.Server")

    /// Gets the metadata stored in the object storage provider for the specified file.
    let getFileMetadata (repositoryDto: RepositoryDto) (fileVersion: FileVersion) (context: HttpContext) =
        task {
            match repositoryDto.ObjectStorageProvider with
            | AzureBlobStorage ->
                let! blobClient = getAzureBlobClientForFileVersion repositoryDto fileVersion (getCorrelationId context)
                let! azureResponse = blobClient.GetPropertiesAsync()
                let blobProperties = azureResponse.Value
                return Ok(blobProperties.Metadata :?> IReadOnlyDictionary<string, string>)
            | AWSS3 -> return Error(getErrorMessage StorageError.NotImplemented)
            | GoogleCloudStorage -> return Error(getErrorMessage StorageError.NotImplemented)
            | ObjectStorageProvider.Unknown ->
                logToConsole
                    $"Error: Unknown ObjectStorageProvider in getFileMetadata for repository {repositoryDto.RepositoryId} - {repositoryDto.RepositoryName}."

                logToConsole (sprintf "%A" repositoryDto)
                return Error(getErrorMessage StorageError.UnknownObjectStorageProvider)
        }

    /// Gets a download URI for the specified file version that can be used by a Grace client.
    let GetDownloadUri: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = (getCorrelationId context)
                let graceIds = getGraceIds context

                try
                    let! parameters = context.BindJsonAsync<GetDownloadUriParameters>()
                    let repositoryActor = Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId correlationId
                    let! repositoryDto = repositoryActor.Get correlationId

                    let! downloadUri = getUriWithReadSharedAccessSignatureForFileVersion repositoryDto parameters.FileVersion correlationId
                    context.SetStatusCode StatusCodes.Status200OK
                    //log.LogTrace("fileVersion: {fileVersion.RelativePath}; downloadUri: {downloadUri}", [| parameters.FileVersion.RelativePath, downloadUri |])
                    return! context.WriteStringAsync $"{downloadUri}"
                with ex ->
                    context.SetStatusCode StatusCodes.Status500InternalServerError
                    return! context.WriteTextAsync $"Error in {context.Request.Path} at {DateTime.Now.ToLongTimeString()}."
            }

    /// Gets an upload URI for the specified file version that can be used by a Grace client.
    let GetUploadUris: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context
                let uris = Dictionary<string, Uri>()

                try
                    let! parameters = context.BindJsonAsync<GetUploadUriParameters>()
                    let repositoryActor = Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId correlationId
                    let! repositoryDto = repositoryActor.Get correlationId

                    for fileVersion in parameters.FileVersions do
                        let! uploadUri = getUriWithWriteSharedAccessSignatureForFileVersion repositoryDto fileVersion correlationId
                        uris.Add(fileVersion.RelativePath, uploadUri)

                    if log.IsEnabled(LogLevel.Debug) then
                        let sb = stringBuilderPool.Get()

                        try
                            for kvp in uris do
                                sb.AppendLine($"fileVersion: {kvp.Key}; uploadUri: {kvp.Value}") |> ignore

                            log.LogDebug("In GetUploadUri(): Created {count} uri's for these files: {uploadUris}", uris.Count, sb.ToString())
                        finally
                            stringBuilderPool.Return(sb)

                    context.SetStatusCode StatusCodes.Status200OK
                    let jsonContent = JsonContent.Create(uris)
                    return! context.WriteJsonAsync jsonContent
                with ex ->
                    context.SetStatusCode StatusCodes.Status500InternalServerError
                    logToConsole $"Exception in GetUploadUri: {(ExceptionResponse.Create ex)}"

                    return! context.WriteTextAsync $"{getCurrentInstantExtended ()} Error in {context.Request.Path} at {DateTime.Now.ToLongTimeString()}."
            }

    /// Checks if a list of files already exists in object storage, and if any do not, return Uri's that the client can use to upload the file.
    let GetUploadMetadataForFiles: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let correlationId = getCorrelationId context
                let graceIds = getGraceIds context

                try
                    let! parameters = context.BindJsonAsync<GetUploadMetadataForFilesParameters>()

                    Activity.Current.SetTag("fileVersions.Count", $"{parameters.FileVersions.Length}")
                    |> ignore

                    if parameters.FileVersions.Length > 0 then
                        let repositoryActor = Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId correlationId
                        let! repositoryDto = repositoryActor.Get correlationId

                        let uploadMetadata = ConcurrentQueue<UploadMetadata>()

                        do!
                            Parallel.ForEachAsync(
                                parameters.FileVersions,
                                Constants.ParallelOptions,
                                (fun fileVersion ct ->
                                    ValueTask(
                                        task {
                                            //let! fileExists = fileExists repositoryDto fileVersion context

                                            //if not <| fileExists then
                                            let! blobUriWithSasToken =
                                                getUriWithWriteSharedAccessSignatureForFileVersion repositoryDto fileVersion correlationId

                                            uploadMetadata.Enqueue(
                                                { RelativePath = fileVersion.RelativePath
                                                  BlobUriWithSasToken = blobUriWithSasToken
                                                  Sha256Hash = fileVersion.Sha256Hash }
                                            )
                                        }
                                    ))
                            )

                        Activity.Current.SetTag("uploadMetadata.Count", $"{uploadMetadata.Count}")
                        |> ignore

                        context
                            .GetLogger()
                            .LogInformation(
                                $"{getCurrentInstantExtended ()} Received {parameters.FileVersions.Count} FileVersions; Returning {uploadMetadata.Count} uploadMetadata records."
                            )

                        return!
                            context
                            |> result200Ok (GraceReturnValue.Create (uploadMetadata.ToArray()) correlationId)
                    else
                        return!
                            context
                            |> result400BadRequest (GraceError.Create (getErrorMessage StorageError.FilesMustNotBeEmpty) correlationId)
                with ex ->
                    logToConsole $"Exception in GetUploadMetadataForFiles: {(ExceptionResponse.Create ex)}"

                    return!
                        context
                        |> result500ServerError (GraceError.Create (getErrorMessage StorageError.ObjectStorageException) correlationId)
            }

    /// Deletes all documents from Cosmos DB. After calling, the web connection will time-out, but the method will continue to run until Cosmos DB is empty.
    ///
    /// **** This method is implemented only in Debug configuration. It is a no-op in Release configuration. ****
    let DeleteAllFromCosmosDB: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
#if DEBUG
                let correlationId = getCorrelationId context
                let log = context.GetLogger()

                log.LogWarning("{CurrentInstant} Deleting all rows from Cosmos DB.", getCurrentInstantExtended ())

                let! failed = deleteAllFromCosmosDb ()

                if failed |> Seq.isEmpty then
                    log.LogWarning("{CurrentInstant} Succeeded deleting all rows from CosmosDB.", getCurrentInstantExtended ())

                    return!
                        context
                        |> result200Ok (GraceReturnValue.Create "Succeeded deleting all rows from Cosmos DB." (getCorrelationId context))
                else
                    let sb = stringBuilderPool.Get()

                    try
                        for fail in failed do
                            sb.AppendLine(fail) |> ignore

                        log.LogWarning(
                            "{CurrentInstant} Failed to delete all rows from Cosmos DB. Failures: {failedCount}.",
                            getCurrentInstantExtended (),
                            failed.Count
                        )

                        log.LogWarning(sb.ToString())

                        return!
                            context
                            |> result500ServerError (
                                GraceError.Create
                                    $"Failed to delete all rows from Cosmos DB. Failures: {failed.Count}.{Environment.NewLine}{sb.ToString()}"
                                    (getCorrelationId context)
                            )
                    finally
                        stringBuilderPool.Return(sb)
#else
                return! context |> result404NotFound
#endif
            }

    /// Deletes all reminders from Cosmos DB. After calling, the web connection will time-out, but the method will continue to run until Cosmos DB is empty.
    ///
    /// **** This method is implemented only in Debug configuration. It is a no-op in Release configuration. ****
    let DeleteAllRemindersFromCosmosDB: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
#if DEBUG
                let correlationId = getCorrelationId context
                let log = context.GetLogger()

                log.LogWarning("{CurrentInstant} Deleting all reminders from Cosmos DB.", getCurrentInstantExtended ())

                let! failed = deleteAllRemindersFromCosmosDb ()

                if failed |> Seq.isEmpty then
                    log.LogWarning("{CurrentInstant} Succeeded deleting all reminders from CosmosDB.", getCurrentInstantExtended ())

                    return!
                        context
                        |> result200Ok (GraceReturnValue.Create "Succeeded deleting all reminders from Cosmos DB." (getCorrelationId context))
                else
                    let sb = stringBuilderPool.Get()

                    try
                        for fail in failed do
                            sb.AppendLine(fail) |> ignore

                        log.LogWarning(
                            "{CurrentInstant} Failed to delete all reminders from Cosmos DB. Failures: {failedCount}.",
                            getCurrentInstantExtended (),
                            failed.Count
                        )

                        log.LogWarning(sb.ToString())

                        return!
                            context
                            |> result500ServerError (
                                GraceError.Create
                                    $"Failed to delete all reminders from Cosmos DB. Failures: {failed.Count}.{Environment.NewLine}{sb.ToString()}"
                                    (getCorrelationId context)
                            )
                    finally
                        stringBuilderPool.Return(sb)
#else
                return! context |> result404NotFound
#endif
            }
