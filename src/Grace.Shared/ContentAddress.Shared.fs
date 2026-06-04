namespace Grace.Shared

open Blake3
open Grace.Types.Common
open System
open System.Text
open System.Text.RegularExpressions

module ContentAddress =
    [<Literal>]
    let AddressHexLength = 64

    [<Literal>]
    let private ContentBlockPreimageHeader = "grace.cas.v1.content-block"

    [<Literal>]
    let private FileManifestPreimageHeader = "grace.cas.v1.file-manifest"

    let private lowercaseAddressRegex =
        Regex(
            "^[0-9a-f]{64}$",
            RegexOptions.Compiled
            ||| RegexOptions.CultureInvariant
        )

    let private appendLine (builder: StringBuilder) (value: string) = builder.Append(value).Append('\n') |> ignore

    let private ensureNonNegative name value =
        if value < 0L then
            invalidArg name "Content address preimage values cannot be negative."

    let private requireValidAddress name (address: string) =
        if lowercaseAddressRegex.IsMatch(address) then
            address
        else
            invalidArg name "Content addresses must be lowercase 64-character hexadecimal BLAKE3 values."

    let private requireNonEmptySequence name values =
        if isNull (box values) then nullArg name

        let valuesArray = values |> Seq.toArray

        if valuesArray.Length = 0 then
            invalidArg name "Content address preimages require at least one entry."

        valuesArray

    /// Computes the lowercase 64-hex BLAKE3 address for a byte payload.
    let computeBlake3Hex (bytes: byte array) =
        if isNull bytes then nullArg (nameof bytes)

        Hasher.Hash(bytes).ToString().ToLowerInvariant()

    /// Returns true when the supplied address is exactly lowercase 64-hex.
    let isValidAddress (address: string) =
        not (String.IsNullOrEmpty(address))
        && lowercaseAddressRegex.IsMatch(address)

    /// Computes the content address for one physical chunk payload.
    let computeChunkAddress (bytes: byte array) : ChunkAddress = ChunkAddress(computeBlake3Hex bytes)

    /// Builds the path-independent preimage for a content block from its ordered chunk addresses.
    let contentBlockPreimage (blockFormat: string) (chunkAddresses: ChunkAddress seq) =
        if String.IsNullOrWhiteSpace(blockFormat) then
            invalidArg (nameof blockFormat) "Content block format is required."

        let chunks = requireNonEmptySequence (nameof chunkAddresses) chunkAddresses
        let builder = StringBuilder()

        appendLine builder ContentBlockPreimageHeader
        appendLine builder $"format:{blockFormat}"
        appendLine builder $"chunk-count:{chunks.Length}"

        chunks
        |> Array.iteri (fun index address -> appendLine builder $"chunk:{index}:{requireValidAddress (nameof chunkAddresses) address}")

        builder.ToString()

    /// Computes the content address for a content block preimage.
    let computeContentBlockAddress blockFormat chunkAddresses : ContentBlockAddress =
        contentBlockPreimage blockFormat chunkAddresses
        |> System.Text.Encoding.UTF8.GetBytes
        |> computeBlake3Hex
        |> ContentBlockAddress

    /// Builds the path-independent preimage for a manifest from its content hash and ordered logical blocks.
    let manifestPreimage (chunkingSuiteId: ChunkingSuiteId) (fileContentHash: FileContentHash) (size: int64) (blocks: ContentBlock seq) =
        if String.IsNullOrWhiteSpace(chunkingSuiteId) then
            invalidArg (nameof chunkingSuiteId) "Chunking suite id is required."

        let fileContentHash = requireValidAddress (nameof fileContentHash) fileContentHash

        ensureNonNegative (nameof size) size

        let blockArray = requireNonEmptySequence (nameof blocks) blocks
        let builder = StringBuilder()

        appendLine builder FileManifestPreimageHeader
        appendLine builder $"chunking-suite:{chunkingSuiteId}"
        appendLine builder $"file-content-hash:{fileContentHash}"
        appendLine builder $"size:{size}"
        appendLine builder $"block-count:{blockArray.Length}"

        blockArray
        |> Array.iteri (fun index block ->
            if isNull (box block) then nullArg (nameof blocks)

            ensureNonNegative (nameof block.Offset) block.Offset
            ensureNonNegative (nameof block.Size) block.Size

            let address = requireValidAddress (nameof block.Address) block.Address
            appendLine builder $"block:{index}:{address}:{block.Offset}:{block.Size}")

        builder.ToString()

    /// Computes the manifest address from its path-independent logical block preimage.
    let computeManifestAddress chunkingSuiteId fileContentHash size blocks : ManifestAddress =
        manifestPreimage chunkingSuiteId fileContentHash size blocks
        |> System.Text.Encoding.UTF8.GetBytes
        |> computeBlake3Hex
        |> ManifestAddress

    /// Computes the manifest address from the stable fields carried by a FileManifest.
    let computeManifestAddressForManifest (manifest: FileManifest) : ManifestAddress =
        if isNull (box manifest) then nullArg (nameof manifest)

        computeManifestAddress manifest.ChunkingSuiteId manifest.FileContentHash manifest.Size manifest.Blocks
