namespace Grace.SDK

open Azure.Core.Pipeline
open Azure.Storage
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Azure.Storage.Blobs.Specialized
open FSharpPlus
open Grace.SDK.Common
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Storage
open NodaTime.Text
open System
open System.Buffers
open System.Collections.Generic
open System.Globalization
open System.IO
open System.IO.Compression
open System.IO.Enumeration
open System.Linq
open System.Net
open System.Net.Http.Json
open System.Threading.Tasks
open System.Text
open Grace.Shared.Parameters.Storage
open Azure

module Storage =

    let GetFileFromObjectStorage (fileVersion: FileVersion) correlationId =
        task {
            try
                match Current().ObjectStorageProvider with
                | AzureBlobStorage ->
                    // Get the URI to use when downloading the file. This includes a SAS token.
                    let httpClient = getHttpClient correlationId
                    let serviceUrl = $"{Current().ServerUri}/storage/getDownloadUri"
                    let jsonContent = createJsonContent fileVersion
                    let! response = httpClient.PostAsync(serviceUrl, jsonContent)
                    let! blobUriWithSasToken = response.Content.ReadAsStringAsync()
                    //logToConsole $"response.StatusCode: {response.StatusCode}; blobUriWithSasToken: {blobUriWithSasToken}"


                    let relativeDirectory =
                        if fileVersion.RelativeDirectory = Constants.RootDirectoryPath then
                            String.Empty
                        else
                            getNativeFilePath fileVersion.RelativeDirectory

                    let tempFilePath = Path.Combine(Path.GetTempPath(), relativeDirectory, fileVersion.GetObjectFileName)

                    let objectFilePath = Path.Combine(Current().ObjectDirectory, fileVersion.RelativePath, fileVersion.GetObjectFileName)

                    let tempFileInfo = FileInfo(tempFilePath)
                    let objectFileInfo = FileInfo(objectFilePath)
                    Directory.CreateDirectory(tempFileInfo.Directory.FullName) |> ignore
                    Directory.CreateDirectory(objectFileInfo.Directory.FullName) |> ignore
                    //logToConsole $"tempFilePath: {tempFilePath}; objectFilePath: {objectFilePath}"

                    // Download the file from object storage.
                    let blobClient = BlobClient(Uri(blobUriWithSasToken))
                    let! azureResponse = blobClient.DownloadToAsync(tempFilePath)

                    if not <| azureResponse.IsError then
                        File.Move(tempFilePath, objectFilePath, overwrite = true)
                        //if fileVersion.IsBinary then
                        //    File.Move(tempFilePath, objectFilePath, overwrite = true)
                        //else
                        //    use tempFileStream = tempFileInfo.OpenRead()
                        //    tempFileStream.Position <- 0
                        //    use gzStream = new GZipStream(tempFileStream, CompressionMode.Decompress, leaveOpen = false)
                        //    use fileWriter = objectFileInfo.OpenWrite()

                        //    do! gzStream.CopyToAsync(fileWriter)
                        //    logToConsole $"In GetFileFromObjectStorage: After CopyToAsync(). {fileVersion.RelativePath}"

                        //    do! fileWriter.FlushAsync()
                        //    logToConsole $"In GetFileFromObjectStorage: After FlushAsync(). {fileVersion.RelativePath}"

                        //    logToConsole $"After tempFileInfo.Delete(). {fileVersion.RelativePath}"
                        tempFileInfo.Delete()
                        return Ok(GraceReturnValue.Create "Retrieved all files from object storage." correlationId)
                    else
                        tempFileInfo.Delete()

                        let error = GraceError.Create (StorageError.getErrorMessage FailedCommunicatingWithObjectStorage) correlationId

                        error.Properties.Add("StatusCode", $"HTTP {azureResponse.Status}")
                        error.Properties.Add("ReasonPhrase", $"Reason: {azureResponse.ReasonPhrase}")
                        return Error error
                | AWSS3 -> return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                | GoogleCloudStorage -> return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
            with ex ->
                logToConsole $"Exception downloading {fileVersion.RelativePath}: {ex.Message}"
                return Error(GraceError.Create (StorageError.getErrorMessage ObjectStorageException) correlationId)
        }

    let GetUploadMetadataForFiles (parameters: GetUploadMetadataForFilesParameters) =
        task {
            let correlationId = parameters.CorrelationId

            try
                if parameters.FileVersions.Length > 0 then
                    match Current().ObjectStorageProvider with
                    | AzureBlobStorage ->
                        let httpClient = getHttpClient correlationId
                        let serviceUrl = $"{Current().ServerUri}/storage/getUploadMetadataForFiles"
                        let jsonContent = createJsonContent parameters
                        let! response = httpClient.PostAsync(serviceUrl, jsonContent)

                        if response.IsSuccessStatusCode then
                            let! uploadMetadata = response.Content.ReadFromJsonAsync<GraceReturnValue<List<UploadMetadata>>>(Constants.JsonSerializerOptions)

                            return Ok uploadMetadata
                        else
                            let! errorMessage = response.Content.ReadAsStringAsync()

                            let graceError = (GraceError.Create $"{StorageError.getErrorMessage FailedToGetUploadUrls}; {errorMessage}" correlationId)

                            let fileVersionList = StringBuilder()

                            for fileVersion in parameters.FileVersions do
                                fileVersionList.Append($"{fileVersion.RelativePath}; ") |> ignore

                            return Error graceError |> enhance "fileVersions" $"{fileVersionList}"
                    | AWSS3 -> return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                    | GoogleCloudStorage -> return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                    | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                else
                    return Error(GraceError.Create (StorageError.getErrorMessage FilesMustNotBeEmpty) correlationId)
            with ex ->
                let exceptionResponse = ExceptionResponse.Create ex
                return Error(GraceError.Create (exceptionResponse.ToString()) correlationId)
        }

    let storageTransferOptions = StorageTransferOptions(MaximumConcurrency = Constants.ParallelOptions.MaxDegreeOfParallelism)

    let SaveFileToObjectStorageWithMetadata
        (repositoryId: RepositoryId)
        (fileVersion: FileVersion)
        (blobUriWithSasToken: Uri)
        (metadata: Dictionary<string, string>)
        correlationId
        =
        task {
            try
                //logToConsole $"In SDK.Storage.SaveFileToObjectStorageWithMetadata: fileVersion.RelativePath: {fileVersion.RelativePath}."
                let fileInfo = FileInfo(Path.Combine(Current().RootDirectory, fileVersion.RelativePath))

                metadata.TryAdd("CorrelationId", correlationId) |> ignore
                metadata.TryAdd("OwnerId", $"{Current().OwnerId}") |> ignore
                metadata.TryAdd("OrganizationId", $"{Current().OrganizationId}") |> ignore
                metadata.TryAdd("RepositoryName", $"{Current().RepositoryName}") |> ignore
                metadata.TryAdd("RepositoryId", $"{Current().RepositoryId}") |> ignore
                metadata.TryAdd("Sha256Hash", fileVersion.Sha256Hash) |> ignore
                metadata.TryAdd("OriginalSize", $"{fileInfo.Length}") |> ignore

                match Current().ObjectStorageProvider with
                | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                | ObjectStorageProvider.AzureBlobStorage ->
                    try
                        // Creating an HttpClientTransport so we can use our custom HttpClientFactory here.
                        use transport = new HttpClientTransport(getHttpClient correlationId)

                        let blobClientOptions = BlobClientOptions(Transport = transport)
                        // I might regret this setting. Time will tell.
                        blobClientOptions.Retry.NetworkTimeout <- TimeSpan.FromMinutes(60.0)

                        let blockBlobClient = BlockBlobClient(blobUriWithSasToken, blobClientOptions)

                        let blobUploadOptions = BlobUploadOptions(Metadata = metadata, Tags = metadata, TransferOptions = storageTransferOptions)
                        // Setting IfNoneMatch = "*" tells Azure Storage not to upload the file if it already exists.
                        blobUploadOptions.Conditions <- new BlobRequestConditions(IfNoneMatch = ETag.All)

                        // Add well-known headers to the blob.
                        //   Content-Type: The MIME type of the file.
                        //   Cache-Control: The maximum amount of time the file should be cached by a browser or proxy server.
                        //   Content-Disposition: An attachment with the creation date of the file; setting this header tells a browser to use the SaveAs dialog, and to use correct name and original timestamp.
                        blobUploadOptions.HttpHeaders <-
                            BlobHttpHeaders(
                                ContentType = getContentType fileInfo (fileVersion.IsBinary),
                                CacheControl = Constants.BlobCacheControl,
                                ContentDisposition =
                                    $"""attachment; creation-date="{fileVersion.CreatedAt.ToString(InstantPattern.General.PatternText, CultureInfo.InvariantCulture)}" """
                            )

                        let objectFilePath =
                            $"{Current().ObjectDirectory}{Path.DirectorySeparatorChar}{fileVersion.RelativePath}{Path.DirectorySeparatorChar}{fileVersion.GetObjectFileName}"

                        let normalizedObjectFilePath = Path.GetFullPath(objectFilePath)

                        use fileStream = File.Open(normalizedObjectFilePath, fileStreamOptionsRead)

                        // We're setting Conditions in blobUploadOptions, so it won't overwrite existing files.
                        //   Unfortunately, it will still try to upload the file, which is a waste of time and bandwidth.
                        //   For larger files, I'm going to check if the file is already uploaded before trying to upload it.
                        //   This is a performance and cost optimization; each call to ExistsAsync() costs money.
                        //   By doing it this way, we trade some client upload bandwidth for saving time waiting for
                        //   ExistsAsync() to return false, plus we're saving the cost of the ExistsAsync() call.
                        let! status =
                            task {
                                // If the file is a binary file, stream it to Blob Storage without compressing it.
                                if fileVersion.IsBinary then
                                    if fileVersion.Size > 1024L * 1024L then
                                        // If the file is larger than 1 MB, check if it is already uploaded to Blob Storage.
                                        let! fileExists = blockBlobClient.ExistsAsync()

                                        if fileExists.Value = true then
                                            return int HttpStatusCode.Conflict
                                        else
                                            let! response = blockBlobClient.UploadAsync(fileStream, blobUploadOptions)
                                            return response.GetRawResponse().Status
                                    else
                                        let! response = blockBlobClient.UploadAsync(fileStream, blobUploadOptions)
                                        return response.GetRawResponse().Status
                                else
                                    // If the file is not a binary file, gzip it, and stream the compressed file to Blob Storage.
                                    blobUploadOptions.HttpHeaders.ContentEncoding <- "gzip"
                                    use memoryStream = new MemoryStream(64 * 1024) // Setting initial capacity larger than most files will need.
                                    use gzipStream = new GZipStream(memoryStream, CompressionLevel.SmallestSize, leaveOpen = false)
                                    do! fileStream.CopyToAsync(gzipStream, bufferSize = (64 * 1024))
                                    do! gzipStream.FlushAsync()
                                    memoryStream.Position <- 0

                                    if memoryStream.Length > 1024L * 1024L then
                                        // If the file is larger than 1 MB, check if it is already uploaded to Blob Storage.
                                        let! fileExists = blockBlobClient.ExistsAsync()

                                        if fileExists.Value = true then
                                            return int HttpStatusCode.Conflict
                                        else
                                            let! response = blockBlobClient.UploadAsync(memoryStream, blobUploadOptions)
                                            return response.GetRawResponse().Status
                                    else
                                        let! response = blockBlobClient.UploadAsync(memoryStream, blobUploadOptions)
                                        return response.GetRawResponse().Status
                            }

                        logToConsole $"In SaveFileToObjectStorageWithMetadata: {fileVersion.RelativePath}; upload status: {status}."

                        if status = int HttpStatusCode.Created then
                            let returnValue = GraceReturnValue.Create "File successfully saved to object storage." correlationId

                            returnValue.Properties.Add(nameof (Sha256Hash), $"{fileVersion.Sha256Hash}")
                            returnValue.Properties.Add(nameof (RelativePath), $"{fileVersion.RelativePath}")
                            returnValue.Properties.Add(nameof (RepositoryId), $"{repositoryId}")
                            return Ok returnValue
                        elif status = int HttpStatusCode.Conflict then
                            // If the file already exists in Blob Storage, we don't need to do anything.
                            let returnValue = GraceReturnValue.Create "File already exists in object storage." correlationId

                            returnValue.Properties.Add(nameof (Sha256Hash), $"{fileVersion.Sha256Hash}")
                            returnValue.Properties.Add(nameof (RelativePath), $"{fileVersion.RelativePath}")
                            returnValue.Properties.Add(nameof (RepositoryId), $"{repositoryId}")
                            return Ok returnValue
                        else
                            let error = (GraceError.Create $"Failed to upload file {normalizedObjectFilePath} to object storage." correlationId)
                            return Error error
                    with ex ->
                        if ex.Message.Contains("The specified blob already exists.") then
                            // If the file already exists in Blob Storage, we don't need to do anything.
                            let returnValue = GraceReturnValue.Create "File already exists in object storage." correlationId
                            returnValue.Properties.Add(nameof (Sha256Hash), $"{fileVersion.Sha256Hash}")
                            returnValue.Properties.Add(nameof (RelativePath), $"{fileVersion.RelativePath}")
                            returnValue.Properties.Add(nameof (RepositoryId), $"{repositoryId}")
                            return Ok returnValue
                        else
                            let exceptionResponse = ExceptionResponse.Create ex
                            return Error(GraceError.Create (exceptionResponse.ToString()) correlationId)
                | ObjectStorageProvider.AWSS3 -> return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                | ObjectStorageProvider.GoogleCloudStorage -> return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
            with ex ->
                let exceptionResponse = ExceptionResponse.Create ex
                return Error(GraceError.Create (exceptionResponse.ToString()) correlationId)
        }

    let SaveFileToObjectStorage (repositoryId: RepositoryId) (fileVersion: FileVersion) (blobUriWithSasToken: Uri) correlationId =
        SaveFileToObjectStorageWithMetadata repositoryId fileVersion blobUriWithSasToken (Dictionary<string, string>()) correlationId

    let GetUploadUri (parameters: GetUploadUriParameters) =
        task {
            try
                match Current().ObjectStorageProvider with
                | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) parameters.CorrelationId)
                | ObjectStorageProvider.AzureBlobStorage ->
                    let httpClient = getHttpClient parameters.CorrelationId
                    let serviceUrl = $"{Current().ServerUri}/storage/getUploadUri"
                    let jsonContent = createJsonContent parameters
                    let! response = httpClient.PostAsync(serviceUrl, jsonContent)
                    let! blobUriWithSasToken = response.Content.ReadAsStringAsync()
                    //logToConsole $"blobUriWithSasToken: {blobUriWithSasToken}"
                    return Ok(GraceReturnValue.Create blobUriWithSasToken parameters.CorrelationId)
                | ObjectStorageProvider.AWSS3 -> return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) parameters.CorrelationId)
                | ObjectStorageProvider.GoogleCloudStorage ->
                    return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) parameters.CorrelationId)
            with ex ->
                let exceptionResponse = ExceptionResponse.Create ex
                logToConsole $"exception: {exceptionResponse.ToString()}"
                return Error(GraceError.Create (exceptionResponse.ToString()) parameters.CorrelationId)
        }

    let GetDownloadUri (parameters: GetDownloadUriParameters) =
        task {
            try
                match Current().ObjectStorageProvider with
                | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) parameters.CorrelationId)
                | ObjectStorageProvider.AzureBlobStorage ->
                    let httpClient = getHttpClient parameters.CorrelationId
                    let serviceUrl = $"{Current().ServerUri}/storage/getDownloadUri"
                    let jsonContent = createJsonContent parameters
                    let! response = httpClient.PostAsync(serviceUrl, jsonContent)
                    let! blobUriWithSasToken = response.Content.ReadAsStringAsync()
                    //logToConsole $"blobUriWithSasToken: {blobUriWithSasToken}"
                    return Ok(GraceReturnValue.Create blobUriWithSasToken parameters.CorrelationId)
                | ObjectStorageProvider.AWSS3 -> return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) parameters.CorrelationId)
                | ObjectStorageProvider.GoogleCloudStorage ->
                    return Error(GraceError.Create (StorageError.getErrorMessage NotImplemented) parameters.CorrelationId)
            with ex ->
                let exceptionResponse = ExceptionResponse.Create ex
                logToConsole $"exception: {exceptionResponse.ToString()}"
                return Error(GraceError.Create (exceptionResponse.ToString()) parameters.CorrelationId)
        }
