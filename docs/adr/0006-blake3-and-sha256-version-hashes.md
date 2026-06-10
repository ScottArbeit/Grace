---
status: accepted
date: 2026-06-10
decision-makers:
  - Scott Arbeit
consulted:
  - Codex
---

# Use BLAKE3 and SHA-256 version hashes

Grace will identify `FileVersion`, `DirectoryVersion`, and `Reference` version objects with version hashes that can
carry both BLAKE3 and SHA-256 values. BLAKE3 becomes the default version-hash algorithm for new version objects after
this ADR is implemented, while SHA-256 remains retained for compatibility, verification, and comparison with existing
stored data.

This ADR records the target model for epic #343. It does not claim that the dual-hash behavior has already shipped.

## Context

Grace currently stores SHA-256 values on version objects and uses those values in file, directory, reference, diff, and
lookup workflows. Existing code also computes the SHA-256 value for a file by hashing the file bytes only. The older
SHA-256 documentation incorrectly said that the file relative path and file length were also appended to the file hash.

Grace now also has content-addressed storage vocabulary:

- `FileContentHash` identifies the complete unencoded bytes of a logical file.
- `ChunkAddress` identifies one `ContentChunk`.
- `ContentBlockAddress` identifies a `ContentBlock`.
- `ManifestAddress` identifies a `FileManifest`.

Those CAS identities are path-independent content identities. They are not the same thing as version graph hashes for
`FileVersion`, `DirectoryVersion`, and `Reference` objects. Version graph hashes identify Grace version objects in the
repository graph, where a directory version is path-sensitive through its own relative path and ordered child entries.

The hash transition has to keep these concepts separate:

- A `FileVersion` says a specific relative path contains specific content in a repository version.
- A `DirectoryVersion` says a specific relative path contains a sorted set of child directory and file versions.
- A `Reference` names a saved repository root and points at a root `DirectoryVersion`.
- CAS addresses identify file content objects, chunks, blocks, and manifests.

## Decision

Grace will support version hashes as an explicit version-object concept rather than overloading any CAS address.

After implementation:

- New `FileVersion`, `DirectoryVersion`, and `Reference` version hash calculations use BLAKE3 by default.
- SHA-256 values remain retained where Grace needs compatibility with existing data, persisted fields, verification
  workflows, or transition tooling.
- A file's current SHA-256 value is computed from the file byte stream only.
- Directory version hashes remain path-sensitive by including the directory relative path and ordered child version
  hash inputs.
- File content CAS identities remain path-independent and byte/content based.

The implementation may introduce explicit DTO or storage fields for multiple version-hash algorithms, but this ADR does
not require a global rename of existing `Sha256Hash` or `FileContentHash` terms as part of the documentation slice.

## Non-Goals

This ADR does not:

- Globally rename `FileContentHash`.
- Redefine `FileContentHash` as a `FileVersion` hash.
- Redefine `ChunkAddress`, `ContentBlockAddress`, or `ManifestAddress` as version graph hashes.
- Introduce content identifiers with algorithm prefixes.
- Add a user-facing hash selection switch.
- Make small or regular `FileVersion` objects participate in StoragePool-wide chunk dedupe.

## Consequences

Documentation and contracts should use these terms consistently:

- Use `FileContentHash` for the path-independent hash of complete file bytes.
- Use `ChunkAddress`, `ContentBlockAddress`, and `ManifestAddress` for CAS addresses.
- Use version hash wording for `FileVersion`, `DirectoryVersion`, and `Reference` graph identity.
- Explain that directory version hashes are path-sensitive without implying that file byte hashes include the path.

During the epic, documentation can describe both the current SHA-256 behavior and the accepted future BLAKE3 plus
SHA-256 model. Until the implementation ships, docs must avoid saying that BLAKE3 version hashes are already present in
runtime behavior.
