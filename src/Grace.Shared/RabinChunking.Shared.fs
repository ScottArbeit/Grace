namespace Grace.Shared

open Grace.Types.Types
open System

/// Pure content-defined chunking for the pinned grace-rabin-blake3-64k-v1 suite.
module RabinChunking =
    /// Stable suite identifier used by clients and manifests that implement this exact chunking recipe.
    [<Literal>]
    let SuiteName = "grace-rabin-blake3-64k-v1"

    /// Minimum natural chunk size. Boundary candidates before this size are ignored.
    [<Literal>]
    let MinimumChunkSize = 8 * 1024

    /// Target average chunk size. The boundary mask is derived from this power-of-two target.
    [<Literal>]
    let TargetChunkSize = 64 * 1024

    /// Maximum chunk size. A cut is forced here when no natural Rabin boundary appears first.
    [<Literal>]
    let MaximumChunkSize = 128 * 1024

    /// Rolling Rabin window size in bytes.
    [<Literal>]
    let WindowSize = 64

    /// Reduction polynomial for the 64-bit rolling Rabin fingerprint.
    [<Literal>]
    let Polynomial = 0x3DA3358B4DC173UL

    /// One logical chunk produced by the pinned suite.
    type Chunk =
        {
            /// Byte offset of the chunk in the original payload.
            Offset: int64

            /// Chunk length in bytes.
            Length: int

            /// Lowercase 64-hex BLAKE3 address of this chunk payload.
            Address: ChunkAddress
        }

    let private cutMask = uint64 TargetChunkSize - 1UL
    let private cutValue = cutMask
    let private topFingerprintBit = 0x8000000000000000UL

    let private appendByte (fingerprint: uint64) (value: byte) =
        let mutable candidate = fingerprint
        let input = uint64 value

        for bit in 7..-1..0 do
            let reduce = candidate &&& topFingerprintBit <> 0UL
            candidate <- (candidate <<< 1) ||| ((input >>> bit) &&& 1UL)

            if reduce then candidate <- candidate ^^^ Polynomial

        candidate

    let private outgoingByteTable =
        Array.init 256 (fun value ->
            let mutable fingerprint = appendByte 0UL (byte value)

            for _ in 1..WindowSize do
                fingerprint <- appendByte fingerprint 0uy

            fingerprint)

    let private isNaturalBoundary fingerprint =
        fingerprint <> 0UL
        && (fingerprint &&& cutMask) = cutValue

    let private findChunkEnd (bytes: byte array) start =
        let limit = Math.Min(bytes.Length, start + MaximumChunkSize)
        let mutable fingerprint = 0UL
        let mutable windowBytes = 0
        let mutable index = start
        let mutable chunkEnd = limit
        let mutable foundNaturalBoundary = false

        while index < limit && not foundNaturalBoundary do
            let nextByte = bytes[index]

            if windowBytes < WindowSize then
                fingerprint <- appendByte fingerprint nextByte
                windowBytes <- windowBytes + 1
            else
                let outgoingByte = bytes[index - WindowSize]

                fingerprint <-
                    (appendByte fingerprint nextByte)
                    ^^^ outgoingByteTable[int outgoingByte]

            let chunkLength = index - start + 1

            if chunkLength >= MinimumChunkSize
               && isNaturalBoundary fingerprint then
                chunkEnd <- index + 1
                foundNaturalBoundary <- true

            index <- index + 1

        chunkEnd

    let private copyRange (bytes: byte array) offset length =
        let chunkBytes = Array.zeroCreate<byte> length
        Array.Copy(bytes, offset, chunkBytes, 0, length)
        chunkBytes

    /// Chunks bytes with grace-rabin-blake3-64k-v1.
    ///
    /// Natural boundaries are checked only after the 8 KiB minimum by evaluating the current 64-byte Rabin window with
    /// polynomial 0x3DA3358B4DC173. A boundary is selected when the non-zero fingerprint matches the 64 KiB target mask;
    /// otherwise a cut is forced at 128 KiB. Each returned chunk is addressed with lowercase BLAKE3.
    let chunkBytes (bytes: byte array) : Chunk array =
        if isNull bytes then nullArg (nameof bytes)

        let chunks = ResizeArray<Chunk>()
        let mutable offset = 0

        while offset < bytes.Length do
            let chunkEnd = findChunkEnd bytes offset
            let chunkLength = chunkEnd - offset
            let chunkPayload = copyRange bytes offset chunkLength

            chunks.Add({ Offset = int64 offset; Length = chunkLength; Address = ContentAddress.computeChunkAddress chunkPayload })

            offset <- chunkEnd

        chunks.ToArray()
