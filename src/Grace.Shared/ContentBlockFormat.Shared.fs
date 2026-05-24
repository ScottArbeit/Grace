namespace Grace.Shared

open Grace.Types.Types
open System
open System.Buffers.Binary
open System.IO
open System.Text

/// Encodes and validates the physical bytes stored for a reusable ContentBlock.
///
/// ContentBlock v1 payloads are `chunk-bytes || trailer || footer`.
///
/// The data section is the logical chunk payloads concatenated in order. The trailer starts with `GCB1META`, a version,
/// reserved flags, and a chunk count. Each chunk record stores the source physical offset, logical chunk length, and the
/// 32-byte BLAKE3 chunk address. The footer stores the trailer length, BLAKE3 trailer checksum, and `GCB1END!`.
/// `ContentBlockAddress` is intentionally derived from the ordered chunk addresses plus `FormatName`; physical offsets
/// are validation/reader metadata and do not affect the logical block identity.
module ContentBlockFormat =
    [<Literal>]
    let FormatName = "grace-contentblock-v1"

    [<Literal>]
    let FooterLength = 44

    [<Literal>]
    let private AddressLength = 32

    [<Literal>]
    let private TrailerHeaderLength = 16

    [<Literal>]
    let private ChunkRecordLength = 44

    [<Literal>]
    let private Version = 1us

    let private trailerMagic = Encoding.ASCII.GetBytes("GCB1META")
    let private footerMagic = Encoding.ASCII.GetBytes("GCB1END!")

    type ContentBlockFormatError =
        | NullChunkSequence
        | NullPayload
        | EmptyBlock
        | InvalidChunk of index: int * reason: string
        | PayloadTooSmall
        | InvalidFooter
        | InvalidTrailer
        | TrailerChecksumMismatch
        | UnsupportedVersion of version: int
        | ChunkAddressMismatch of index: int * expected: ChunkAddress * actual: ChunkAddress
        | ContentBlockAddressMismatch of expected: ContentBlockAddress * actual: ContentBlockAddress

    type ContentBlockInputChunk = { PhysicalOffset: int64; Bytes: byte array }

    type ContentBlockChunk = { PhysicalOffset: int64; LogicalOffset: int64; Length: int; Address: ChunkAddress; Bytes: byte array }

    type EncodedContentBlock = { Address: ContentBlockAddress; Payload: byte array; Chunks: ContentBlockChunk array }

    type DecodedContentBlock = { Address: ContentBlockAddress; Payload: byte array; Chunks: ContentBlockChunk array }

    let createChunk physicalOffset bytes = { PhysicalOffset = physicalOffset; Bytes = bytes }

    let private copyRange (bytes: byte array) offset length =
        let copy = Array.zeroCreate<byte> length
        Array.Copy(bytes, offset, copy, 0, length)
        copy

    let private hashBytes (bytes: byte array) =
        ContentAddress.computeBlake3Hex bytes
        |> Convert.FromHexString

    let private bytesToChunkAddress (bytes: byte array) =
        Convert.ToHexString(bytes).ToLowerInvariant()
        |> ChunkAddress

    let private writeUInt16 (stream: MemoryStream) value =
        let bytes = Array.zeroCreate<byte> 2
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value)
        stream.Write(bytes, 0, bytes.Length)

    let private writeUInt32 (stream: MemoryStream) value =
        let bytes = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value)
        stream.Write(bytes, 0, bytes.Length)

    let private writeInt64 (stream: MemoryStream) value =
        let bytes = Array.zeroCreate<byte> 8
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value)
        stream.Write(bytes, 0, bytes.Length)

    let private readUInt16 (bytes: byte array) offset = BinaryPrimitives.ReadUInt16LittleEndian(ReadOnlySpan<byte>(bytes, offset, 2))

    let private readUInt32 (bytes: byte array) offset = BinaryPrimitives.ReadUInt32LittleEndian(ReadOnlySpan<byte>(bytes, offset, 4))

    let private readInt64 (bytes: byte array) offset = BinaryPrimitives.ReadInt64LittleEndian(ReadOnlySpan<byte>(bytes, offset, 8))

    let private sequenceEqual (expected: byte array) (actual: byte array) =
        expected.Length = actual.Length
        && Array.forall2 (=) expected actual

    let private buildTrailer (chunks: ContentBlockChunk array) =
        use stream = new MemoryStream()

        stream.Write(trailerMagic, 0, trailerMagic.Length)
        writeUInt16 stream Version
        writeUInt16 stream 0us
        writeUInt32 stream (uint32 chunks.Length)

        for chunk in chunks do
            writeInt64 stream chunk.PhysicalOffset
            writeUInt32 stream (uint32 chunk.Length)

            let addressBytes = Convert.FromHexString(chunk.Address)
            stream.Write(addressBytes, 0, addressBytes.Length)

        stream.ToArray()

    let private buildFooter (trailer: byte array) =
        use stream = new MemoryStream()

        writeUInt32 stream (uint32 trailer.Length)

        let trailerHash = hashBytes trailer
        stream.Write(trailerHash, 0, trailerHash.Length)
        stream.Write(footerMagic, 0, footerMagic.Length)
        stream.ToArray()

    let private toMetadata (chunks: ContentBlockInputChunk array) =
        let mutable logicalOffset = 0L
        let metadata = ResizeArray<ContentBlockChunk>()
        let mutable error = None
        let mutable index = 0

        while index < chunks.Length && Option.isNone error do
            let chunk = chunks[index]

            if chunk.PhysicalOffset < 0L then
                error <- Some(InvalidChunk(index, "Physical offset cannot be negative."))
            elif isNull chunk.Bytes then
                error <- Some(InvalidChunk(index, "Chunk bytes cannot be null."))
            elif chunk.Bytes.Length = 0 then
                error <- Some(InvalidChunk(index, "Chunk bytes cannot be empty."))
            else
                let bytes = Array.copy chunk.Bytes
                let address = ContentAddress.computeChunkAddress bytes

                metadata.Add({ PhysicalOffset = chunk.PhysicalOffset; LogicalOffset = logicalOffset; Length = bytes.Length; Address = address; Bytes = bytes })

                logicalOffset <- logicalOffset + int64 bytes.Length

            index <- index + 1

        match error with
        | Some error -> Error error
        | None -> Ok(metadata.ToArray())

    let encode (chunks: ContentBlockInputChunk seq) =
        if isNull (box chunks) then
            Error NullChunkSequence
        else
            let chunkArray = chunks |> Seq.toArray

            if chunkArray.Length = 0 then
                Error EmptyBlock
            else
                match toMetadata chunkArray with
                | Error error -> Error error
                | Ok metadata ->
                    let data =
                        metadata
                        |> Array.collect (fun chunk -> chunk.Bytes)

                    let trailer = buildTrailer metadata
                    let footer = buildFooter trailer

                    let payload = Array.concat [ data; trailer; footer ]

                    let address =
                        metadata
                        |> Array.map (fun chunk -> chunk.Address)
                        |> ContentAddress.computeContentBlockAddress FormatName

                    Ok({ Address = address; Payload = payload; Chunks = metadata }: EncodedContentBlock)

    let private parseTrailer (trailer: byte array) =
        if trailer.Length < TrailerHeaderLength then
            Error InvalidTrailer
        elif not (sequenceEqual trailerMagic trailer[0 .. trailerMagic.Length - 1]) then
            Error InvalidTrailer
        else
            let version = readUInt16 trailer 8

            if version <> Version then
                Error(UnsupportedVersion(int version))
            else
                let flags = readUInt16 trailer 10
                let chunkCount = readUInt32 trailer 12

                if flags <> 0us then
                    Error InvalidTrailer
                elif chunkCount = 0u then
                    Error EmptyBlock
                else
                    let recordBytes = trailer.Length - TrailerHeaderLength

                    if recordBytes % ChunkRecordLength <> 0 then
                        Error InvalidTrailer
                    else
                        let expectedChunkCount = recordBytes / ChunkRecordLength

                        if chunkCount <> uint32 expectedChunkCount then
                            Error InvalidTrailer
                        else
                            let records =
                                [|
                                    for index in 0 .. int chunkCount - 1 do
                                        let offset = TrailerHeaderLength + (index * ChunkRecordLength)
                                        let physicalOffset = readInt64 trailer offset
                                        let length = readUInt32 trailer (offset + 8)
                                        let addressBytes = copyRange trailer (offset + 12) AddressLength

                                        yield physicalOffset, length, bytesToChunkAddress addressBytes
                                |]

                            Ok records

    let decode (payload: byte array) =
        if isNull payload then
            Error NullPayload
        elif payload.Length < TrailerHeaderLength + FooterLength then
            Error PayloadTooSmall
        else
            let footerStart = payload.Length - FooterLength
            let footer = copyRange payload footerStart FooterLength
            let footerMagicStart = FooterLength - footerMagic.Length

            if not (sequenceEqual footerMagic footer[footerMagicStart .. FooterLength - 1]) then
                Error InvalidFooter
            else
                let trailerLength = readUInt32 footer 0

                if trailerLength < uint32 TrailerHeaderLength
                   || trailerLength > uint32 footerStart then
                    Error InvalidFooter
                else
                    let trailerStart = footerStart - int trailerLength
                    let trailer = copyRange payload trailerStart (int trailerLength)
                    let expectedTrailerHash = copyRange footer 4 AddressLength
                    let actualTrailerHash = hashBytes trailer

                    if not (sequenceEqual expectedTrailerHash actualTrailerHash) then
                        Error TrailerChecksumMismatch
                    else
                        match parseTrailer trailer with
                        | Error error -> Error error
                        | Ok records ->
                            let mutable logicalOffset = 0L
                            let decodedChunks = ResizeArray<ContentBlockChunk>()
                            let mutable error = None
                            let mutable index = 0

                            while index < records.Length && Option.isNone error do
                                let physicalOffset, length, expectedAddress = records[index]

                                if physicalOffset < 0L then
                                    error <- Some InvalidTrailer
                                elif length = 0u || length > uint32 Int32.MaxValue then
                                    error <- Some InvalidTrailer
                                elif logicalOffset + int64 length > int64 trailerStart then
                                    error <- Some InvalidTrailer
                                else
                                    let chunkBytes = copyRange payload (int logicalOffset) (int length)
                                    let actualAddress = ContentAddress.computeChunkAddress chunkBytes

                                    if actualAddress <> expectedAddress then
                                        error <- Some(ChunkAddressMismatch(index, expectedAddress, actualAddress))
                                    else
                                        decodedChunks.Add(
                                            {
                                                PhysicalOffset = physicalOffset
                                                LogicalOffset = logicalOffset
                                                Length = int length
                                                Address = actualAddress
                                                Bytes = chunkBytes
                                            }
                                        )

                                        logicalOffset <- logicalOffset + int64 length

                                index <- index + 1

                            match error with
                            | Some error -> Error error
                            | None when logicalOffset <> int64 trailerStart -> Error InvalidTrailer
                            | None ->
                                let chunks = decodedChunks.ToArray()

                                let address =
                                    chunks
                                    |> Array.map (fun chunk -> chunk.Address)
                                    |> ContentAddress.computeContentBlockAddress FormatName

                                Ok({ Address = address; Payload = copyRange payload 0 trailerStart; Chunks = chunks }: DecodedContentBlock)

    let validateAddress expectedAddress decodedBlock =
        if expectedAddress = decodedBlock.Address then
            Ok()
        else
            Error(ContentBlockAddressMismatch(expectedAddress, decodedBlock.Address))

    let validatePayloadAddress expectedAddress payload =
        decode payload
        |> Result.bind (validateAddress expectedAddress)
