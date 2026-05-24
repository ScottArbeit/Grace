namespace Grace.Shared

open Grace.Types.Types
open System
open System.Collections.Generic
open System.IO

/// Pure FileManifest reconstruction and validation helpers.
///
/// Validation does not read storage. Callers provide the physical ContentBlock payloads they already loaded. The helper
/// rejects manifests whose block ranges are not ordered, positive, contiguous from offset zero, and exactly
/// size-covering; it also checks the pinned chunking suite, each ContentBlock payload address, the reconstructed file
/// content hash, and the stable ManifestAddress.
module ManifestValidation =
    type ManifestBlockPayload = { Address: ContentBlockAddress; Payload: byte array }

    type ManifestValidationError =
        | NullManifest
        | NullBlockPayloadSequence
        | NullBlockPayload of index: int
        | NullBlockPayloadBytes of address: ContentBlockAddress
        | EmptyManifest
        | InvalidManifestSize
        | InvalidChunkingSuiteId of chunkingSuiteId: ChunkingSuiteId
        | InvalidFileContentHash of fileContentHash: FileContentHash
        | InvalidManifestAddress of manifestAddress: ManifestAddress
        | NullContentBlock of index: int
        | InvalidContentBlockAddress of index: int * address: ContentBlockAddress
        | ChunkingSuiteMismatch of expected: ChunkingSuiteId * actual: ChunkingSuiteId
        | ManifestAddressMismatch of expected: ManifestAddress * actual: ManifestAddress
        | BlockRangeOutOfOrder of index: int
        | BlockRangeNotPositive of index: int
        | MissingContentBlockPayload of index: int * address: ContentBlockAddress
        | ContentBlockPayloadInvalid of address: ContentBlockAddress * error: ContentBlockFormat.ContentBlockFormatError
        | ContentBlockPayloadSizeMismatch of index: int * expected: int64 * actual: int64
        | FileContentHashMismatch of expected: FileContentHash * actual: FileContentHash
        | ManifestSizeMismatch of expected: int64 * actual: int64

    let createBlockPayload address payload = { Address = address; Payload = payload }

    let private copyBytes (bytes: byte array) =
        let copy = Array.zeroCreate<byte> bytes.Length
        Array.Copy(bytes, copy, bytes.Length)
        copy

    let private decodePayloads (payloads: ManifestBlockPayload seq) =
        if isNull (box payloads) then
            Error NullBlockPayloadSequence
        else
            let payloadArray = payloads |> Seq.toArray
            let decoded = Dictionary<ContentBlockAddress, ContentBlockFormat.DecodedContentBlock>()
            let mutable error = None
            let mutable index = 0

            while index < payloadArray.Length && Option.isNone error do
                let payload = payloadArray[index]

                if isNull (box payload) then
                    error <- Some(NullBlockPayload index)
                elif isNull payload.Payload then
                    error <- Some(NullBlockPayloadBytes payload.Address)
                else
                    match ContentBlockFormat.decode payload.Payload with
                    | Error decodeError -> error <- Some(ContentBlockPayloadInvalid(payload.Address, decodeError))
                    | Ok decodedBlock ->
                        match ContentBlockFormat.validateAddress payload.Address decodedBlock with
                        | Error addressError -> error <- Some(ContentBlockPayloadInvalid(payload.Address, addressError))
                        | Ok () -> decoded[payload.Address] <- decodedBlock

                index <- index + 1

            match error with
            | Some error -> Error error
            | None -> Ok decoded

    let private validateManifestShape expectedChunkingSuiteId (manifest: FileManifest) =
        if isNull (box manifest) then
            Error NullManifest
        elif String.IsNullOrWhiteSpace(expectedChunkingSuiteId) then
            Error(InvalidChunkingSuiteId expectedChunkingSuiteId)
        elif manifest.Size <= 0L then
            Error InvalidManifestSize
        elif isNull manifest.Blocks
             || manifest.Blocks.Count = 0 then
            Error EmptyManifest
        elif String.IsNullOrWhiteSpace(manifest.ChunkingSuiteId) then
            Error(InvalidChunkingSuiteId manifest.ChunkingSuiteId)
        elif not (ContentAddress.isValidAddress manifest.FileContentHash) then
            Error(InvalidFileContentHash manifest.FileContentHash)
        elif not (ContentAddress.isValidAddress manifest.ManifestAddress) then
            Error(InvalidManifestAddress manifest.ManifestAddress)
        elif manifest.ChunkingSuiteId
             <> expectedChunkingSuiteId then
            Error(ChunkingSuiteMismatch(expectedChunkingSuiteId, manifest.ChunkingSuiteId))
        else
            Ok()

    let private validateRanges (manifest: FileManifest) =
        let mutable expectedOffset = 0L
        let mutable error = None
        let mutable index = 0

        while index < manifest.Blocks.Count
              && Option.isNone error do
            let block = manifest.Blocks[index]

            if isNull (box block) then
                error <- Some(NullContentBlock index)
            elif not (ContentAddress.isValidAddress block.Address) then
                error <- Some(InvalidContentBlockAddress(index, block.Address))
            elif block.Size <= 0L then
                error <- Some(BlockRangeNotPositive index)
            elif block.Offset <> expectedOffset then
                error <- Some(BlockRangeOutOfOrder index)
            else
                expectedOffset <- expectedOffset + block.Size

            index <- index + 1

        match error with
        | Some error -> Error error
        | None when expectedOffset <> manifest.Size -> Error(ManifestSizeMismatch(manifest.Size, expectedOffset))
        | None -> Ok()

    let private validateManifestAddress (manifest: FileManifest) =
        let expectedAddress = ContentAddress.computeManifestAddressForManifest manifest

        if manifest.ManifestAddress <> expectedAddress then
            Error(ManifestAddressMismatch(expectedAddress, manifest.ManifestAddress))
        else
            Ok()

    let private reconstructBytes (manifest: FileManifest) (payloads: Dictionary<ContentBlockAddress, ContentBlockFormat.DecodedContentBlock>) =
        use stream = new MemoryStream()
        let mutable error = None
        let mutable index = 0

        while index < manifest.Blocks.Count
              && Option.isNone error do
            let block = manifest.Blocks[index]

            match payloads.TryGetValue(block.Address) with
            | false, _ -> error <- Some(MissingContentBlockPayload(index, block.Address))
            | true, decodedBlock ->
                let actualSize = int64 decodedBlock.Payload.Length

                if actualSize <> block.Size then
                    error <- Some(ContentBlockPayloadSizeMismatch(index, block.Size, actualSize))
                else
                    stream.Write(decodedBlock.Payload, 0, decodedBlock.Payload.Length)

            index <- index + 1

        match error with
        | Some error -> Error error
        | None -> Ok(stream.ToArray())

    /// Validates and reconstructs a manifest-backed file from supplied ContentBlock payloads.
    let validate expectedChunkingSuiteId (manifest: FileManifest) payloads =
        validateManifestShape expectedChunkingSuiteId manifest
        |> Result.bind (fun () -> validateRanges manifest)
        |> Result.bind (fun () -> validateManifestAddress manifest)
        |> Result.bind (fun () -> decodePayloads payloads)
        |> Result.bind (fun decodedPayloads -> reconstructBytes manifest decodedPayloads)
        |> Result.bind (fun reconstructedBytes ->
            let actualHash = FileContentHash(ContentAddress.computeBlake3Hex reconstructedBytes)

            if manifest.FileContentHash <> actualHash then
                Error(FileContentHashMismatch(manifest.FileContentHash, actualHash))
            else
                Ok(copyBytes reconstructedBytes))
