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

module DiffBlobGrainStorage =

    let safeBlobName (stateName: string) (grainId: GrainId) =
        let rawName = $"{stateName}-{grainId}"
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawName))
        $"{stateName}-{Convert.ToHexString(hash).ToLowerInvariant()}.json"

    let legacyBlobName (stateName: string) (grainId: GrainId) = $"{stateName}-{grainId}.json"

    let candidateBlobNames stateName grainId =
        [
            safeBlobName stateName grainId
            legacyBlobName stateName grainId
        ]
        |> List.distinct

type DiffBlobGrainStorage(storageName: string, blobServiceClient: BlobServiceClient, containerName: string) =

    let getContainer () = blobServiceClient.GetBlobContainerClient(containerName)

    let isNotFoundOrInvalidResourceName (ex: RequestFailedException) =
        ex.Status = 404
        || String.Equals(ex.ErrorCode, "BlobNotFound", StringComparison.OrdinalIgnoreCase)
        || String.Equals(ex.ErrorCode, "ContainerNotFound", StringComparison.OrdinalIgnoreCase)
        || String.Equals(ex.ErrorCode, "InvalidResourceName", StringComparison.OrdinalIgnoreCase)
        || String.Equals(ex.ErrorCode, "OutOfRangeInput", StringComparison.OrdinalIgnoreCase)

    let resetState (grainState: IGrainState<'T>) =
        grainState.ETag <- null
        grainState.RecordExists <- false
        grainState.State <- Unchecked.defaultof<'T>

    let serializeState (state: 'T) = JsonSerializer.SerializeToUtf8Bytes(state, Constants.JsonSerializerOptions)

    let deserializeState (content: BinaryData) : 'T = content.ToObjectFromJson<'T>(Constants.JsonSerializerOptions)

    let tryDownload (container: BlobContainerClient) (blobName: string) =
        task {
            try
                let blob = container.GetBlobClient(blobName)
                let! response = blob.DownloadContentAsync()
                return Some(response.Value.Content, response.Value.Details.ETag.ToString())
            with
            | :? RequestFailedException as ex when isNotFoundOrInvalidResourceName ex -> return None
        }

    interface IGrainStorage with
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
                | Some (content, etag) ->
                    grainState.ETag <- etag
                    grainState.RecordExists <- true
                    grainState.State <- deserializeState content
                | None -> resetState grainState
            }
            :> Task

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

    member _.StorageName = storageName
