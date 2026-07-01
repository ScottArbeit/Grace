namespace Grace.Server

open Azure
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Grace.Shared
open Orleans
open Orleans.Runtime
open Orleans.Storage
open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks

/// Contains Grace Server diff blob grain storage behavior and supporting helpers.
module DiffBlobGrainStorage =

    /// Implements safe blob name for the server request pipeline.
    let safeBlobName (stateName: string) (grainId: GrainId) =
        let rawName = $"{stateName}-{grainId}"
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawName))
        $"{stateName}-{Convert.ToHexString(hash).ToLowerInvariant()}.json"

    /// Implements legacy blob name for the server request pipeline.
    let legacyBlobName (stateName: string) (grainId: GrainId) = $"{stateName}-{grainId}.json"

    /// Implements candidate blob names for the server request pipeline.
    let candidateBlobNames stateName grainId =
        [
            safeBlobName stateName grainId
            legacyBlobName stateName grainId
        ]
        |> List.distinct

    /// Implements etag for write target for the server request pipeline.
    let etagForWriteTarget readBlobName writeBlobName etag =
        if String.Equals(readBlobName, writeBlobName, StringComparison.Ordinal) then
            etag
        else
            null

/// Represents diff blob grain storage used by Grace Server APIs and background services.
type DiffBlobGrainStorage(storageName: string, blobServiceClient: BlobServiceClient, containerName: string) =

    /// Resolves the blob container that stores Orleans grain state diffs.
    let getContainer () = blobServiceClient.GetBlobContainerClient(containerName)

    /// Determines whether not found or invalid resource name.
    let isNotFoundOrInvalidResourceName (ex: RequestFailedException) =
        ex.Status = 404
        || String.Equals(ex.ErrorCode, "BlobNotFound", StringComparison.OrdinalIgnoreCase)
        || String.Equals(ex.ErrorCode, "ContainerNotFound", StringComparison.OrdinalIgnoreCase)
        || String.Equals(ex.ErrorCode, "InvalidResourceName", StringComparison.OrdinalIgnoreCase)
        || String.Equals(ex.ErrorCode, "OutOfRangeInput", StringComparison.OrdinalIgnoreCase)

    /// Implements reset state for the server request pipeline.
    let resetState (grainState: IGrainState<'T>) =
        grainState.ETag <- null
        grainState.RecordExists <- false
        grainState.State <- Unchecked.defaultof<'T>

    /// Implements serialize state for the server request pipeline.
    let serializeState (state: 'T) = JsonSerializer.SerializeToUtf8Bytes(state, Constants.JsonSerializerOptions)

    /// Implements deserialize state for the server request pipeline.
    let deserializeState (content: BinaryData) : 'T = content.ToObjectFromJson<'T>(Constants.JsonSerializerOptions)

    /// Attempts to download and returns an option or result instead of throwing.
    let tryDownload (container: BlobContainerClient) (blobName: string) =
        task {
            try
                let blob = container.GetBlobClient(blobName)
                let! response = blob.DownloadContentAsync()
                return Some(response.Value.Content, response.Value.Details.ETag.ToString(), blobName)
            with
            | :? RequestFailedException as ex when isNotFoundOrInvalidResourceName ex -> return None
        }

    interface IGrainStorage with
        /// Reads Orleans grain state from the diff blob store and hydrates the storage context.
        member _.ReadStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) : Task =
            task {
                let container = getContainer ()
                let candidates = DiffBlobGrainStorage.candidateBlobNames stateName grainId
                let mutable found = None
                let mutable index = 0

                while found.IsNone && index < candidates.Length do
                    let! downloaded = tryDownload container candidates[index]
                    found <- downloaded
                    index <- index + 1

                match found with
                | Some (content, etag, blobName) ->
                    let writeBlobName = DiffBlobGrainStorage.safeBlobName stateName grainId
                    grainState.ETag <- DiffBlobGrainStorage.etagForWriteTarget blobName writeBlobName etag
                    grainState.RecordExists <- true
                    grainState.State <- deserializeState content
                | None -> resetState grainState
            }
            :> Task

        /// Persists Orleans grain state into the diff blob store for the active grain identity.
        member _.WriteStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) : Task =
            task {
                let container = getContainer ()
                let! _ = container.CreateIfNotExistsAsync(PublicAccessType.None)

                let blobName = DiffBlobGrainStorage.safeBlobName stateName grainId
                let blob = container.GetBlobClient(blobName)

                let conditions =
                    if String.IsNullOrWhiteSpace grainState.ETag then
                        BlobRequestConditions(IfNoneMatch = ETag.All)
                    else
                        BlobRequestConditions(IfMatch = ETag(grainState.ETag))

                let uploadOptions = BlobUploadOptions(HttpHeaders = BlobHttpHeaders(ContentType = "application/json"), Conditions = conditions)

                let! response = blob.UploadAsync(BinaryData(serializeState grainState.State), uploadOptions)
                grainState.ETag <- response.Value.ETag.ToString()
                grainState.RecordExists <- true
            }
            :> Task

        /// Deletes stored Orleans grain state for the active grain identity.
        member _.ClearStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) : Task =
            task {
                let container = getContainer ()
                let candidates = DiffBlobGrainStorage.candidateBlobNames stateName grainId
                let mutable index = 0

                while index < candidates.Length do
                    try
                        let blob = container.GetBlobClient(candidates[index])
                        let! _ = blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots)
                        ()
                    with
                    | :? RequestFailedException as ex when isNotFoundOrInvalidResourceName ex -> ()

                    index <- index + 1

                resetState grainState
            }
            :> Task

    /// Exposes the configured Orleans storage provider name for this grain storage implementation.
    member _.StorageName = storageName
