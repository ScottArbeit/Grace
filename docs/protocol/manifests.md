# Manifest Verification

Grace accepts a `FileManifest` only when its bytes can be reconstructed from validated ContentBlock payloads and the
manifest identity matches that reconstruction contract.

Portable SDKs and caches should implement these checks before treating a manifest as valid.

## Validation Inputs

Manifest verification needs:

- The expected `ChunkingSuiteId`.
- The `FileManifest`.
- A set of physical ContentBlock payloads keyed by `ContentBlockAddress`.

The validator does not read storage by itself. Callers load the physical payloads they already have permission to use.

## Required Checks

Validate in this order:

1. The manifest exists and has a positive `Size`.
1. `ChunkingSuiteId` is present and equals the expected suite.
1. `FileContentHash`, `ManifestAddress`, and each `ContentBlockAddress` are canonical lowercase 64-hex addresses.
1. The manifest has at least one block.
1. Block ranges are ordered, positive, contiguous from offset zero, and cover exactly `Size`.
1. The manifest address equals the address recomputed from the manifest preimage.
1. Each referenced ContentBlock payload is present.
1. Each ContentBlock payload decodes as `grace-contentblock-v1`.
1. Each decoded ContentBlock address equals the expected `ContentBlockAddress`.
1. Each decoded ContentBlock logical payload length equals the manifest block range size.
1. Concatenated decoded payload bytes hash to `FileContentHash`.

The successful result is the reconstructed unencoded file bytes.

## Corruption And Tampering Rejection

Portable implementations must reject:

- Tampered payload bytes.
- Invalid compact block footer or trailer.
- Trailer checksum mismatch.
- Chunk address mismatch inside a compact block.
- ContentBlock address mismatch.
- Reordered manifest blocks.
- Gaps, overlaps, zero-sized ranges, and size mismatches.
- Stale manifests whose `ManifestAddress` no longer matches the reconstruction fields.
- File content hash mismatch.
- Wrong chunking suite.

The golden vectors include happy-path reconstruction and representative rejection cases. They are intentionally not
happy-path-only.

## Compact ContentBlock Payload

`grace-contentblock-v1` payloads are `chunk-bytes || trailer || footer`.

The data section is the logical chunk payloads concatenated in order. The trailer records the format magic, version,
flags, chunk count, physical offsets, chunk lengths, and chunk addresses. The footer records the trailer length, BLAKE3
checksum of the trailer, and footer magic.

Physical offsets and encoded lengths validate the payload; they do not affect `ContentBlockAddress` except through the
compact block format name in the address preimage.
