# Content Addresses

Grace Protocol v1 uses lowercase 64-character hexadecimal BLAKE3 addresses for chunks, ContentBlocks, manifests, and
file content hashes. Uppercase hex, shortened values, non-hex characters, empty strings, and null values are invalid.

## ChunkAddress

`ChunkAddress` is the BLAKE3 hash of one logical chunk's unencoded bytes.

The hash excludes:

- Repository id.
- Path.
- Upload session id.
- Compression and transfer encoding.
- Storage provider, object key, ETag, or shard.
- Manifest eligibility policy.

## ContentBlockAddress

`ContentBlockAddress` is the BLAKE3 hash of this UTF-8 preimage:

```text
grace.cas.v1.content-block
format:<compact-block-format>
chunk-count:<count>
chunk:0:<chunk-address-0>
chunk:1:<chunk-address-1>
...
```

Each line ends with `\n`, including the final line. The chunk sequence is ordered. Reordering chunks changes the
address even when the same chunk addresses are present.

The ContentBlock address includes the compact block format name because decoders need the same logical payload
contract. It excludes physical offsets, encoded lengths, lifecycle metadata, active manifest counts, object keys, and
repository policy.

## ManifestAddress

`ManifestAddress` is the BLAKE3 hash of this UTF-8 preimage:

```text
grace.cas.v1.file-manifest
chunking-suite:<chunking-suite-id>
file-content-hash:<file-content-hash>
size:<total-unencoded-file-size>
block-count:<count>
block:0:<content-block-address-0>:<offset-0>:<size-0>
block:1:<content-block-address-1>:<offset-1>:<size-1>
...
```

Each line ends with `\n`, including the final line. Block ranges are ordered, positive, and contiguous from offset zero.
The final covered byte count must equal `size`.

`ManifestAddress` comes from the reconstruction contract. It excludes repository id, path, upload session id, actor id,
storage provider, object key, compression, transfer encoding, and the repository policy that selected the manifest path.

## Canonical Address Validation

Portable implementations should reject an address unless it matches this exact regular expression:

```text
^[0-9a-f]{64}$
```

The golden vectors include valid and invalid examples for this boundary.
