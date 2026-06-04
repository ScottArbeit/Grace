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
open Grace.Types.Common
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
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
open Grace.Types.ContentBlockMetadata
open Grace.Types.UploadSession
open Azure

module Storage =

    let internal blobAlreadyExistsErrorCode = "BlobAlreadyExists"

    let internal isExistingContentBlockUploadConflict (ex: RequestFailedException) =
        ex.Status = int HttpStatusCode.Conflict
        && String.Equals(ex.ErrorCode, blobAlreadyExistsErrorCode, StringComparison.OrdinalIgnoreCase)

    let private contentBlockPlacement contentBlockAddress etag = { ObjectKey = StorageKeys.contentBlockObjectKey contentBlockAddress; ETag = etag }

    /// Gets a file from object storage and saves it to the local object directory.
    let GetFileFromObjectStorage (getDownloadUriParameters: GetDownloadUriParameters) correlationId =
        task {
            let fileVersion = getDownloadUriParameters.FileVersion

            try
                match Current().ObjectStorageProvider with
                | AzureBlobStorage ->
                    // Get the URI to use when downloading the file. This includes a SAS token.
                    let httpClient = ClientIdentity.getHttpClient correlationId
                    do! Auth.addAuthorizationHeader httpClient
                    let serviceUrl = $"{Current().ServerUri}/storage/getDownloadUri"
                    let jsonContent = createJsonContent getDownloadUriParameters
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

                    Directory.CreateDirectory(tempFileInfo.Directory.FullName)
                    |> ignore

                    Directory.CreateDirectory(objectFileInfo.Directory.FullName)
                    |> ignore
                    //logToConsole $"tempFilePath: {tempFilePath}; objectFilePath: {objectFilePath}"

                    // Download the file from object storage.
                    let blobClient = BlobClient(Uri(blobUriWithSasToken))
                    let! azureResponse = blobClient.DownloadToAsync(tempFilePath)

                    if not <| azureResponse.IsError then
                        //File.Move(tempFilePath, objectFilePath, overwrite = true)
                        if fileVersion.IsBinary then
                            File.Move(tempFilePath, objectFilePath, overwrite = true)
                        else
                            use tempFileStream = tempFileInfo.OpenRead()
                            use gzStream = new GZipStream(tempFileStream, CompressionMode.Decompress, leaveOpen = false)
                            use fileWriter = objectFileInfo.OpenWrite()

                            do! gzStream.CopyToAsync(fileWriter)
                            //logToConsole $"In GetFileFromObjectStorage: After CopyToAsync(). {fileVersion.RelativePath}"

                            do! fileWriter.FlushAsync()
                        //logToConsole $"In GetFileFromObjectStorage: After FlushAsync(). {fileVersion.RelativePath}"

                        //logToConsole $"After tempFileInfo.Delete(). {fileVersion.RelativePath}"

                        tempFileInfo.Delete()
                        return Ok(GraceReturnValue.Create "Retrieved all files from object storage." correlationId)
                    else
                        tempFileInfo.Delete()

                        let error = GraceError.Create (getErrorMessage StorageError.FailedCommunicatingWithObjectStorage) correlationId

                        error.Properties.Add("StatusCode", $"HTTP {azureResponse.Status}")
                        error.Properties.Add("ReasonPhrase", $"Reason: {azureResponse.ReasonPhrase}")
                        return Error error
                | AWSS3 -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
                | GoogleCloudStorage -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
                | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
            with
            | ex ->
                logToConsole $"Exception downloading {fileVersion.GetObjectFileName}: {ex.Message}"
                return Error(GraceError.Create (getErrorMessage StorageError.ObjectStorageException) correlationId)
        }

    /// Gets upload metadata (including upload URLs with SAS tokens) for a list of files to be uploaded to object storage.
    let GetUploadMetadataForFiles (parameters: GetUploadMetadataForFilesParameters) =
        task {
            let correlationId = parameters.CorrelationId

            try
                if parameters.FileVersions.Length > 0 then
                    match Current().ObjectStorageProvider with
                    | AzureBlobStorage ->
                        let httpClient = ClientIdentity.getHttpClient correlationId
                        do! Auth.addAuthorizationHeader httpClient
                        let serviceUrl = $"{Current().ServerUri}/storage/getUploadMetadataForFiles"
                        let jsonContent = createJsonContent parameters
                        let! response = httpClient.PostAsync(serviceUrl, jsonContent)

                        if response.IsSuccessStatusCode then
                            let! uploadMetadata = response.Content.ReadFromJsonAsync<GraceReturnValue<List<UploadMetadata>>>(Constants.JsonSerializerOptions)

                            return Ok uploadMetadata
                        else
                            let! errorMessage = response.Content.ReadAsStringAsync()

                            let graceError = (GraceError.Create $"{getErrorMessage StorageError.FailedToGetUploadUrls}; {errorMessage}" correlationId)

                            let fileVersionList = StringBuilder()

                            for fileVersion in parameters.FileVersions do
                                fileVersionList.Append($"{fileVersion.RelativePath}; ")
                                |> ignore

                            return
                                Error graceError
                                |> enhance "fileVersions" $"{fileVersionList}"
                    | AWSS3 -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
                    | GoogleCloudStorage -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
                    | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
                else
                    return Error(GraceError.Create (getErrorMessage StorageError.FilesMustNotBeEmpty) correlationId)
            with
            | ex ->
                let exceptionResponse = ExceptionResponse.Create ex
                return Error(GraceError.Create (exceptionResponse.ToString()) correlationId)
        }

    let storageTransferOptions = StorageTransferOptions(MaximumConcurrency = Constants.ParallelOptions.MaxDegreeOfParallelism)

    /// Saves a file to object storage with the specified metadata.
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

                metadata[nameof CorrelationId] <- correlationId
                metadata[nameof OwnerId] <- $"{Current().OwnerId}"
                metadata[nameof OrganizationId] <- $"{Current().OrganizationId}"
                metadata[nameof RepositoryName] <- $"{Current().RepositoryName}"
                metadata[nameof RepositoryId] <- $"{Current().RepositoryId}"
                metadata[nameof Sha256Hash] <- fileVersion.Sha256Hash
                metadata["UncompressedSize"] <- $"{fileInfo.Length}"

                match Current().ObjectStorageProvider with
                | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
                | ObjectStorageProvider.AzureBlobStorage ->
                    try
                        // This client talks directly to Azure Blob Storage, not the Grace API, so it intentionally omits Grace client identity headers.
                        use transport = new HttpClientTransport(getHttpClient correlationId)

                        let blobClientOptions = BlobClientOptions(Transport = transport)
                        // I might regret this setting. Time will tell.
                        blobClientOptions.Retry.NetworkTimeout <- TimeSpan.FromMinutes(60.0)

                        let blockBlobClient = BlockBlobClient(blobUriWithSasToken, blobClientOptions)

                        let blobUploadOptions = BlobUploadOptions(Metadata = metadata, Tags = metadata, TransferOptions = storageTransferOptions)
                        // Setting IfNoneMatch = "*" tells Azure Storage not to upload the file if it already exists.
                        //blobUploadOptions.Conditions <- new BlobRequestConditions(IfNoneMatch = ETag.All)

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
                                    use gzipStream = new GZipStream(stream = memoryStream, compressionLevel = CompressionLevel.SmallestSize, leaveOpen = false)
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

                        //logToConsole $"In SaveFileToObjectStorageWithMetadata: {fileVersion.RelativePath}; upload status: {status}."

                        if status = int HttpStatusCode.Created then
                            let returnValue = GraceReturnValue.Create "File successfully saved to object storage." correlationId

                            returnValue.Properties.Add(nameof Sha256Hash, fileVersion.Sha256Hash)
                            returnValue.Properties.Add(nameof RelativePath, fileVersion.RelativePath)
                            returnValue.Properties.Add(nameof RepositoryId, repositoryId)
                            return Ok returnValue
                        elif status = int HttpStatusCode.Conflict then
                            // If the file already exists in Blob Storage, we don't need to do anything.
                            let returnValue = GraceReturnValue.Create "File already exists in object storage." correlationId

                            returnValue.Properties.Add(nameof Sha256Hash, fileVersion.Sha256Hash)
                            returnValue.Properties.Add(nameof RelativePath, fileVersion.RelativePath)
                            returnValue.Properties.Add(nameof RepositoryId, repositoryId)
                            return Ok returnValue
                        else
                            let error = (GraceError.Create $"Failed to upload file {normalizedObjectFilePath} to object storage." correlationId)
                            return Error error
                    with
                    | ex ->
                        if ex.Message.Contains("The specified blob already exists.") then
                            // If the file already exists in Blob Storage, we don't need to do anything.
                            let returnValue = GraceReturnValue.Create "File already exists in object storage." correlationId
                            returnValue.Properties.Add(nameof Sha256Hash, fileVersion.Sha256Hash)
                            returnValue.Properties.Add(nameof RelativePath, fileVersion.RelativePath)
                            returnValue.Properties.Add(nameof RepositoryId, repositoryId)
                            return Ok returnValue
                        else
                            let exceptionResponse = ExceptionResponse.Create ex
                            return Error(GraceError.Create (exceptionResponse.ToString()) correlationId)
                | ObjectStorageProvider.AWSS3 -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
                | ObjectStorageProvider.GoogleCloudStorage -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
            with
            | ex ->
                let exceptionResponse = ExceptionResponse.Create ex
                return Error(GraceError.Create (exceptionResponse.ToString()) correlationId)
        }

    /// Saves a file to object storage.
    let SaveFileToObjectStorage (repositoryId: RepositoryId) (fileVersion: FileVersion) (blobUriWithSasToken: Uri) correlationId =
        SaveFileToObjectStorageWithMetadata repositoryId fileVersion blobUriWithSasToken (Dictionary<string, string>()) correlationId

    let SaveContentBlockToObjectStorage (contentBlockAddress: ContentBlockAddress) (payload: byte array) (blobUriWithSasToken: Uri) correlationId =
        task {
            try
                match Current().ObjectStorageProvider with
                | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
                | ObjectStorageProvider.AzureBlobStorage ->
                    // This client talks directly to Azure Blob Storage, not the Grace API, so it intentionally omits Grace client identity headers.
                    use transport = new HttpClientTransport(getHttpClient correlationId)

                    let blobClientOptions = BlobClientOptions(Transport = transport)
                    blobClientOptions.Retry.NetworkTimeout <- TimeSpan.FromMinutes(60.0)

                    let blockBlobClient = BlockBlobClient(blobUriWithSasToken, blobClientOptions)
                    use payloadStream = new MemoryStream(payload, writable = false)
                    let! response = blockBlobClient.UploadAsync(payloadStream)

                    return Ok(GraceReturnValue.Create (contentBlockPlacement contentBlockAddress (Some(response.Value.ETag.ToString()))) correlationId)
                | ObjectStorageProvider.AWSS3 -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
                | ObjectStorageProvider.GoogleCloudStorage -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
            with
            | :? RequestFailedException as ex when isExistingContentBlockUploadConflict ex ->
                return Ok(GraceReturnValue.Create (contentBlockPlacement contentBlockAddress None) correlationId)
            | ex ->
                let exceptionResponse = ExceptionResponse.Create ex
                return Error(GraceError.Create (exceptionResponse.ToString()) correlationId)
        }

    let DownloadContentBlockFromObjectStorage (contentBlockAddress: ContentBlockAddress) (blobUriWithSasToken: Uri) correlationId =
        task {
            try
                match Current().ObjectStorageProvider with
                | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
                | ObjectStorageProvider.AzureBlobStorage ->
                    // This client talks directly to Azure Blob Storage, not the Grace API, so it intentionally omits Grace client identity headers.
                    use transport = new HttpClientTransport(getHttpClient correlationId)

                    let blobClientOptions = BlobClientOptions(Transport = transport)
                    blobClientOptions.Retry.NetworkTimeout <- TimeSpan.FromMinutes(60.0)

                    let blobClient = BlobClient(blobUriWithSasToken, blobClientOptions)
                    let! response = blobClient.DownloadContentAsync()

                    return Ok(GraceReturnValue.Create (response.Value.Content.ToArray()) correlationId)
                | ObjectStorageProvider.AWSS3 -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
                | ObjectStorageProvider.GoogleCloudStorage -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) correlationId)
            with
            | ex ->
                let exceptionResponse = ExceptionResponse.Create ex
                return Error(GraceError.Create $"Failed to download ContentBlock {contentBlockAddress}: {exceptionResponse.ToString()}" correlationId)
        }

    /// Gets an upload URI with a SAS token for uploading a file to object storage.
    let GetUploadUri (parameters: GetUploadUriParameters) =
        task {
            try
                match Current().ObjectStorageProvider with
                | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
                | ObjectStorageProvider.AzureBlobStorage ->
                    let httpClient = ClientIdentity.getHttpClient parameters.CorrelationId
                    do! Auth.addAuthorizationHeader httpClient
                    let serviceUrl = $"{Current().ServerUri}/storage/getUploadUri"
                    let jsonContent = createJsonContent parameters
                    let! response = httpClient.PostAsync(serviceUrl, jsonContent)
                    let! blobUriWithSasToken = response.Content.ReadAsStringAsync()
                    //logToConsole $"blobUriWithSasToken: {blobUriWithSasToken}"
                    return Ok(GraceReturnValue.Create blobUriWithSasToken parameters.CorrelationId)
                | ObjectStorageProvider.AWSS3 -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
                | ObjectStorageProvider.GoogleCloudStorage ->
                    return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
            with
            | ex ->
                let exceptionResponse = ExceptionResponse.Create ex
                logToConsole $"exception: {exceptionResponse.ToString()}"
                return Error(GraceError.Create (exceptionResponse.ToString()) parameters.CorrelationId)
        }

    /// Gets a download URI with a SAS token for downloading a file from object storage.
    let GetDownloadUri (parameters: GetDownloadUriParameters) =
        task {
            try
                match Current().ObjectStorageProvider with
                | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
                | ObjectStorageProvider.AzureBlobStorage ->
                    let httpClient = ClientIdentity.getHttpClient parameters.CorrelationId
                    do! Auth.addAuthorizationHeader httpClient
                    let serviceUrl = $"{Current().ServerUri}/storage/getDownloadUri"
                    let jsonContent = createJsonContent parameters
                    let! response = httpClient.PostAsync(serviceUrl, jsonContent)
                    let! blobUriWithSasToken = response.Content.ReadAsStringAsync()
                    //logToConsole $"blobUriWithSasToken: {blobUriWithSasToken}"
                    return Ok(GraceReturnValue.Create blobUriWithSasToken parameters.CorrelationId)
                | ObjectStorageProvider.AWSS3 -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
                | ObjectStorageProvider.GoogleCloudStorage ->
                    return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
            with
            | ex ->
                let exceptionResponse = ExceptionResponse.Create ex
                logToConsole $"exception: {exceptionResponse.ToString()}"
                return Error(GraceError.Create (exceptionResponse.ToString()) parameters.CorrelationId)
        }

    /// Gets an upload URI with a SAS token for uploading a ContentBlock payload to object storage.
    let GetContentBlockUploadUri (parameters: GetContentBlockUploadUriParameters) =
        task {
            try
                match Current().ObjectStorageProvider with
                | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
                | ObjectStorageProvider.AzureBlobStorage ->
                    let httpClient = ClientIdentity.getHttpClient parameters.CorrelationId
                    do! Auth.addAuthorizationHeader httpClient
                    let serviceUrl = $"{Current().ServerUri}/storage/getContentBlockUploadUri"
                    let jsonContent = createJsonContent parameters
                    let! response = httpClient.PostAsync(serviceUrl, jsonContent)
                    let! blobUriWithSasToken = response.Content.ReadAsStringAsync()
                    return Ok(GraceReturnValue.Create blobUriWithSasToken parameters.CorrelationId)
                | ObjectStorageProvider.AWSS3 -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
                | ObjectStorageProvider.GoogleCloudStorage ->
                    return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
            with
            | ex ->
                let exceptionResponse = ExceptionResponse.Create ex
                logToConsole $"exception: {exceptionResponse.ToString()}"
                return Error(GraceError.Create (exceptionResponse.ToString()) parameters.CorrelationId)
        }

    /// Gets a download URI with a SAS token for downloading a ContentBlock payload from object storage.
    let GetContentBlockDownloadUri (parameters: GetContentBlockDownloadUriParameters) =
        task {
            try
                match Current().ObjectStorageProvider with
                | ObjectStorageProvider.Unknown -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
                | ObjectStorageProvider.AzureBlobStorage ->
                    let httpClient = ClientIdentity.getHttpClient parameters.CorrelationId
                    do! Auth.addAuthorizationHeader httpClient
                    let serviceUrl = $"{Current().ServerUri}/storage/getContentBlockDownloadUri"
                    let jsonContent = createJsonContent parameters
                    let! response = httpClient.PostAsync(serviceUrl, jsonContent)
                    let! blobUriWithSasToken = response.Content.ReadAsStringAsync()
                    return Ok(GraceReturnValue.Create blobUriWithSasToken parameters.CorrelationId)
                | ObjectStorageProvider.AWSS3 -> return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
                | ObjectStorageProvider.GoogleCloudStorage ->
                    return Error(GraceError.Create (getErrorMessage StorageError.NotImplemented) parameters.CorrelationId)
            with
            | ex ->
                let exceptionResponse = ExceptionResponse.Create ex
                logToConsole $"exception: {exceptionResponse.ToString()}"
                return Error(GraceError.Create (exceptionResponse.ToString()) parameters.CorrelationId)
        }

    /// Discovers reusable ContentBlock candidates for key chunks without treating empty results as absence.
    let DiscoverContentBlocks (parameters: DiscoverContentBlocksParameters) =
        task {
            let correlationId = parameters.CorrelationId

            try
                let httpClient = ClientIdentity.getHttpClient correlationId
                do! Auth.addAuthorizationHeader httpClient
                let serviceUrl = $"{Current().ServerUri}/storage/discoverContentBlocks"
                let jsonContent = createJsonContent parameters
                let! response = httpClient.PostAsync(serviceUrl, jsonContent)

                if response.IsSuccessStatusCode then
                    let! discoveryResult = response.Content.ReadFromJsonAsync<GraceReturnValue<DiscoverContentBlocksResult>>(Constants.JsonSerializerOptions)

                    return Ok discoveryResult
                else
                    let! errorMessage = response.Content.ReadAsStringAsync()

                    return Error(GraceError.Create $"Failed to discover ContentBlocks; {errorMessage}" correlationId)
            with
            | ex ->
                let exceptionResponse = ExceptionResponse.Create ex
                logToConsole $"exception: {exceptionResponse.ToString()}"
                return Error(GraceError.Create (exceptionResponse.ToString()) correlationId)
        }

    let StartManifestUploadSession (parameters: StartManifestUploadSessionParameters) =
        Common.postServer<StartManifestUploadSessionParameters, UploadSessionDecision> (
            parameters |> Common.ensureCorrelationIdIsSet,
            "storage/startManifestUploadSession"
        )

    let IssueDedupeDiscovery (parameters: IssueDedupeDiscoveryParameters) =
        Common.postServer<IssueDedupeDiscoveryParameters, UploadSessionDecision> (parameters |> Common.ensureCorrelationIdIsSet, "storage/issueDedupeDiscovery")

    let ClaimReuseRanges (parameters: ClaimReuseRangesParameters) =
        Common.postServer<ClaimReuseRangesParameters, UploadSessionDecision> (parameters |> Common.ensureCorrelationIdIsSet, "storage/claimReuseRanges")

    let RegisterContentBlockUpload (parameters: RegisterContentBlockUploadParameters) =
        Common.postServer<RegisterContentBlockUploadParameters, UploadSessionDecision> (
            parameters |> Common.ensureCorrelationIdIsSet,
            "storage/registerContentBlockUpload"
        )

    let ConfirmContentBlockUpload (parameters: ConfirmContentBlockUploadParameters) =
        Common.postServer<ConfirmContentBlockUploadParameters, UploadSessionDecision> (
            parameters |> Common.ensureCorrelationIdIsSet,
            "storage/confirmContentBlockUpload"
        )

    let FinalizeManifestUpload (parameters: FinalizeManifestUploadParameters) =
        Common.postServer<FinalizeManifestUploadParameters, UploadSessionDecision> (
            parameters |> Common.ensureCorrelationIdIsSet,
            "storage/finalizeManifestUpload"
        )
