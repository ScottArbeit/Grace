---
status: accepted
date: 2026-06-10
decision-makers:
  - Scott Arbeit
consulted:
  - Codex
---

# Use BLAKE3 and SHA-256 version hashes

Grace carries BLAKE3 and SHA-256 values on `FileVersion`, `DirectoryVersion`, and `Reference` version objects for
lookup, display, and verification. BLAKE3 is the default version-hash algorithm for new version lookup surfaces, while
SHA-256 remains retained for verification, comparison, lookup parity, and non-version uses that intentionally stay
SHA-256.

This ADR records the accepted model targeted by epic #343 and partially implemented on the epic branch. Follow-ups #394
and #395 track the remaining release-contract gaps for BLAKE3 display and lookup parity before the final mainline
release. Grace is not in production, so the SHA-256 retention in this ADR is a current contract and verification choice,
not a promise to migrate or preserve old production data.

## Context

Before epic #343, Grace stored SHA-256 values on version objects and used those values in file, directory, reference,
diff, and lookup workflows. Existing code also computed the SHA-256 value for a file by hashing the file bytes only. The
older SHA-256 documentation incorrectly said that the file relative path and file length were also appended to the file
hash.

Grace now also has content-addressed storage vocabulary:

- `FileContentHash` identifies the complete unencoded bytes of a logical file.
- `ChunkAddress` identifies one `ContentChunk`.
- `ContentBlockAddress` identifies a `ContentBlock`.
- `ManifestAddress` identifies a `FileManifest`.

Those CAS identities are path-independent content identities. They are not the same thing as version hashes used for
version lookup and display. Current file version hashes are byte-only, directory version hashes identify the formal
directory preimage, and reference hashes are the referenced root DirectoryVersion hashes validated by reference
commands.

The hash transition has to keep these concepts separate:

- A `FileVersion` says a specific relative path contains specific content in a repository version.
- A `DirectoryVersion` says a specific relative path contains a sorted set of child directory and file versions.
- A `Reference` names a saved repository root and points at a root `DirectoryVersion`.
- CAS addresses identify file content objects, chunks, blocks, and manifests.

## Decision

Grace supports version hashes as an explicit version-object concept rather than overloading any CAS address.

After epic #343:

- New `FileVersion`, `DirectoryVersion`, and `Reference` version hash calculations use BLAKE3 by default.
- SHA-256 values remain retained where Grace needs persisted fields, verification workflows, comparison, lookup parity,
  or non-version SHA-256 uses such as security and payload integrity.
- A file's current SHA-256 value is computed from the file byte stream only.
- Directory version hashes use the formal `grace.directory-version.v1` preimage. The preimage includes the directory
  relative path, child entry kind, child relative path, child size for every entry, and same-algorithm child hashes.
  Entries are sorted by normalized child path first, then by child kind as the tiebreaker.
- File content CAS identities remain path-independent and byte/content based.

Epic #343 introduced explicit fields for multiple version-hash algorithms. It did not require a global rename of
existing `Sha256Hash` or `FileContentHash` terms.

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
- Use version hash wording for lookup and display without describing byte-only file hashes or referenced root
  DirectoryVersion hashes as unique object identities.
- Explain that directory version hashes are path-sensitive without implying that file byte hashes include the path.

After the epic, documentation should describe BLAKE3 plus SHA-256 as the current version-hash contract and should keep
CAS identities separate from version graph hashes.
