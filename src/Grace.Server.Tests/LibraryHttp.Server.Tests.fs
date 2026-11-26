namespace Grace.Server.Tests

open System
open System.IO
open System.Net
open Azure.Storage.Blobs.Specialized
open Grace.Server.Tests.Services
open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.Library
open Grace.Types.ContentBlockMetadata
open Grace.Types.UploadSession
open NUnit.Framework

/// Calls the real Library and Storage HTTP routes the way a client does, for hosted Library tests.
module LibraryHttp =

    /// Requires the real HTTP success envelope while retaining its original bytes for operator replay.
    let post<'T> (path: string) (parameters: obj) =
        task {
            use! response = Client.PostAsync(path, createJsonContent parameters)
            let! body = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), body)

            return
                (deserialize<GraceReturnValue<'T>> body)
                    .ReturnValue,
                body
        }

    /// Sets the explicit repository scope used by every Library operation.
    let inline scope repository (parameters: ^T) =
        (^T: (member set_OwnerId: string -> unit) (parameters, ownerId))
        (^T: (member set_OrganizationId: string -> unit) (parameters, organizationId))
        (^T: (member set_RepositoryId: string -> unit) (parameters, repository))

    /// Recovers the exact storage placement supplied in the server's upload URI.
    let placement (uri: Uri) etag : ContentBlockStoragePlacement =
        let segments =
            uri.AbsolutePath.Trim('/').Split('/')
            |> Array.map Uri.UnescapeDataString

        let pathStyle =
            uri.Host = "localhost"
            || (IPAddress.TryParse(uri.Host) |> fst)

        let account =
            uri.Fragment.TrimStart('#').Split('&')
            |> Array.tryPick (fun part ->
                let pair = part.Split('=', 2) in

                if pair.Length = 2 && pair[0] = "graceStorageAccount" then
                    Some(Uri.UnescapeDataString pair[1])
                else
                    None)
            |> Option.defaultWith (fun () -> if pathStyle then segments[0] else uri.Host.Split('.')[0])

        let index = if pathStyle && segments[0] = account then 1 else 0

        {
            StorageAccountName = account
            StorageContainerName = segments[index]
            ObjectKey = String.Join('/', segments |> Array.skip (index + 1))
            ETag = Some etag
        }

    /// Uploads real encoded bytes and finalizes their manifest through the ordinary Library and Storage routes.
    let prepare repository operation (payload: byte array) =
        task {
            let input = Parameters.Library.PrepareLibraryContentParameters()
            scope repository input
            input.OperationId <- operation
            input.Blake3Hash <- ContentAddress.computeBlake3Hex payload
            input.Sha256Hash <- Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData payload)
            input.Size <- int64 payload.Length
            let! prepared, _ = post<LibraryContentPreparationDto> "/libraries/content/prepare" input

            let block =
                match ContentBlockFormat.encode [ { PhysicalOffset = 0L; Bytes = payload } ] with
                | Ok block -> block
                | Error error -> failwithf "%A" error

            let register = Parameters.Storage.RegisterContentBlockUploadParameters()
            scope repository register
            register.UploadSessionId <- prepared.UploadSessionId
            register.AuthorizedScope <- prepared.AuthorizedScope
            register.OperationId <- string (Guid.NewGuid())
            register.ContentBlockAddress <- block.Address
            register.LogicalOffset <- 0L
            register.LogicalLength <- int64 payload.Length
            register.ExpectedPayloadLength <- int64 block.Payload.Length
            let! _, _ = post<UploadSessionDecision> "/storage/registerContentBlockUpload" register
            let upload = Parameters.Storage.GetContentBlockUploadUriParameters()
            scope repository upload
            upload.UploadSessionId <- prepared.UploadSessionId
            upload.AuthorizedScope <- prepared.AuthorizedScope
            upload.ContentBlockAddress <- block.Address
            use! response = Client.PostAsync("/storage/getContentBlockUploadUri", createJsonContent upload)
            let! uriText = response.Content.ReadAsStringAsync()
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), uriText)
            let uri = Uri uriText
            use stream = new MemoryStream(block.Payload, false)
            let! uploaded = BlockBlobClient(uri).UploadAsync(stream)
            let confirm = Parameters.Storage.ConfirmContentBlockUploadParameters()
            scope repository confirm
            confirm.UploadSessionId <- prepared.UploadSessionId
            confirm.AuthorizedScope <- prepared.AuthorizedScope
            confirm.OperationId <- string (Guid.NewGuid())
            confirm.ContentBlockAddress <- block.Address
            confirm.Payload <- block.Payload
            confirm.StoragePlacement <- placement uri (uploaded.Value.ETag.ToString())
            let! _, _ = post<UploadSessionDecision> "/storage/confirmContentBlockUpload" confirm

            let blocks =
                [
                    ContentBlock.Create(block.Address, 0L, int64 payload.Length)
                ]

            let manifest = FileManifest.Create("", RabinChunking.SuiteName, input.Blake3Hash, int64 payload.Length, prepared.StoragePoolId, blocks)
            let manifest = { manifest with ManifestAddress = ContentAddress.computeManifestAddressForManifest manifest }
            let finalize = Parameters.Storage.FinalizeManifestUploadParameters()
            scope repository finalize
            finalize.UploadSessionId <- prepared.UploadSessionId
            finalize.AuthorizedScope <- prepared.AuthorizedScope
            finalize.OperationId <- string (Guid.NewGuid())
            finalize.Manifest <- manifest
            let! _, _ = post<UploadSessionDecision> "/storage/finalizeManifestUpload" finalize
            return prepared
        }
