namespace Grace.SDK

open Grace.Shared
open Grace.Types.Types
open System
open System.Collections.Generic
open System.IO

/// Pure local planning for manifest-backed file uploads.
module LocalPlanner =
    let DefaultBlockFormat = ContentBlockFormat.FormatName

    type Options =
        {
            EligibilityPolicy: ManifestEligibilityPolicy
            ChunkingSuiteId: ChunkingSuiteId
            BlockFormat: string
        }

        static member Default =
            {
                EligibilityPolicy = ManifestEligibilityPolicy.Default
                ChunkingSuiteId = ChunkingSuiteId RabinChunking.SuiteName
                BlockFormat = DefaultBlockFormat
            }

    type FallbackUploadPlan = { Bytes: byte array }

    type LocalChunkPlan = { ChunkIndex: int; Offset: int64; Length: int; Address: ChunkAddress; IsKeyChunk: bool }

    type KeyChunkPlan = { ChunkIndex: int; Offset: int64; Length: int; Address: ChunkAddress }

    type ContentBlockPlan =
        {
            BlockIndex: int
            Offset: int64
            Size: int64
            Address: ContentBlockAddress
            ChunkIndexes: int array
            ChunkAddresses: ChunkAddress array
            KeyChunkAddress: ChunkAddress
            DuplicateOfBlockIndex: int option
        }

    type ContentBlockUploadPlan = { BlockIndex: int; BlockAddress: ContentBlockAddress; Bytes: byte array }

    type LocalFilePlan =
        {
            ReferenceType: FileContentReferenceType
            FileContentHash: FileContentHash
            ExpectedSize: int64
            ChunkingSuiteId: ChunkingSuiteId
            ManifestAddress: ManifestAddress option
            Chunks: LocalChunkPlan array
            KeyChunks: KeyChunkPlan array
            Blocks: ContentBlockPlan array
            ContentBlockUploads: ContentBlockUploadPlan array
            FallbackUpload: FallbackUploadPlan option
        }

    let private copyRange (bytes: byte array) offset length =
        let copied = Array.zeroCreate<byte> length
        Array.Copy(bytes, offset, copied, 0, length)
        copied

    let private fallbackPlan (options: Options) (fileContentHash: FileContentHash) expectedSize bytes =
        {
            ReferenceType = FileContentReferenceType.WholeFileContent
            FileContentHash = fileContentHash
            ExpectedSize = expectedSize
            ChunkingSuiteId = options.ChunkingSuiteId
            ManifestAddress = None
            Chunks = Array.empty
            KeyChunks = Array.empty
            Blocks = Array.empty
            ContentBlockUploads = Array.empty
            FallbackUpload = Some { Bytes = Array.copy bytes }
        }

    let private ensureSupportedChunkingSuite (options: Options) =
        if options.ChunkingSuiteId
           <> ChunkingSuiteId RabinChunking.SuiteName then
            invalidArg "ChunkingSuiteId" $"Only {RabinChunking.SuiteName} is supported by the local planner."

    let private planManifest (options: Options) (fileContentHash: FileContentHash) expectedSize (bytes: byte array) =
        ensureSupportedChunkingSuite options

        let firstChunkIndexesByAddress = Dictionary<ChunkAddress, int>()
        let firstBlockIndexesByAddress = Dictionary<ContentBlockAddress, int>()
        let uploadPlans = ResizeArray<ContentBlockUploadPlan>()

        let chunks =
            RabinChunking.chunkBytes bytes
            |> Array.mapi (fun index chunk ->
                let isKeyChunk =
                    if firstChunkIndexesByAddress.ContainsKey(chunk.Address) then
                        false
                    else
                        firstChunkIndexesByAddress[chunk.Address] <- index
                        true

                { ChunkIndex = index; Offset = chunk.Offset; Length = chunk.Length; Address = chunk.Address; IsKeyChunk = isKeyChunk })

        let blocks =
            chunks
            |> Array.mapi (fun index chunk ->
                let blockAddress = ContentAddress.computeContentBlockAddress options.BlockFormat [| chunk.Address |]

                let duplicateOfBlockIndex =
                    if firstBlockIndexesByAddress.ContainsKey(blockAddress) then
                        Some firstBlockIndexesByAddress[blockAddress]
                    else
                        firstBlockIndexesByAddress[blockAddress] <- index

                        uploadPlans.Add({ BlockIndex = index; BlockAddress = blockAddress; Bytes = copyRange bytes (int chunk.Offset) chunk.Length })

                        None

                {
                    BlockIndex = index
                    Offset = chunk.Offset
                    Size = int64 chunk.Length
                    Address = blockAddress
                    ChunkIndexes = [| chunk.ChunkIndex |]
                    ChunkAddresses = [| chunk.Address |]
                    KeyChunkAddress = chunk.Address
                    DuplicateOfBlockIndex = duplicateOfBlockIndex
                })

        let contentBlocks =
            blocks
            |> Array.map (fun block -> ContentBlock.Create(block.Address, block.Offset, block.Size))

        let manifestAddress =
            if contentBlocks.Length = 0 then
                None
            else
                Some(ContentAddress.computeManifestAddress options.ChunkingSuiteId fileContentHash expectedSize contentBlocks)

        let keyChunks =
            chunks
            |> Array.choose (fun chunk ->
                if chunk.IsKeyChunk then
                    Some { ChunkIndex = chunk.ChunkIndex; Offset = chunk.Offset; Length = chunk.Length; Address = chunk.Address }
                else
                    None)

        {
            ReferenceType = FileContentReferenceType.FileManifest
            FileContentHash = fileContentHash
            ExpectedSize = expectedSize
            ChunkingSuiteId = options.ChunkingSuiteId
            ManifestAddress = manifestAddress
            Chunks = chunks
            KeyChunks = keyChunks
            Blocks = blocks
            ContentBlockUploads = uploadPlans.ToArray()
            FallbackUpload = None
        }

    /// Analyzes local bytes and returns the deterministic upload plan without contacting Grace Server.
    let analyzeBytes (options: Options) (bytes: byte array) =
        ArgumentNullException.ThrowIfNull(bytes)

        if String.IsNullOrWhiteSpace(options.BlockFormat) then
            invalidArg (nameof options.BlockFormat) "Content block format is required."

        let fileContentHash = FileContentHash(ContentAddress.computeBlake3Hex bytes)
        let expectedSize = int64 bytes.Length

        match bytes.Length, ManifestEligibility.evaluateContentReferenceType options.EligibilityPolicy bytes with
        | 0, _ -> fallbackPlan options fileContentHash expectedSize bytes
        | _, FileContentReferenceType.WholeFileContent -> fallbackPlan options fileContentHash expectedSize bytes
        | _, FileContentReferenceType.FileManifest -> planManifest options fileContentHash expectedSize bytes
        | _, unexpected -> invalidOp $"Unsupported file content reference type: {unexpected}."

    /// Analyzes a local file and returns the deterministic upload plan without contacting Grace Server.
    let analyzeFile options filePath =
        if String.IsNullOrWhiteSpace(filePath) then
            invalidArg (nameof filePath) "File path is required."

        File.ReadAllBytes(filePath)
        |> analyzeBytes options
