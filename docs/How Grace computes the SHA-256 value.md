# Computing SHA-256 values for files and directories

## Introduction

Grace uses SHA-256 values beside BLAKE3 in file, directory, reference, diff, and lookup workflows. These values sit in
the version graph, but their current meaning depends on the object type. File SHA-256 values are byte hashes; they do
not by themselves identify stored `FileVersion` objects because a `FileVersion` also carries `RelativePath`.
Directory SHA-256 values use the same formal directory-version preimage shape as BLAKE3, with SHA-256 child hashes.
References store the referenced root directory hashes. These values are separate from content-addressed storage
identities such as `FileContentHash`, `ChunkAddress`, `ContentBlockAddress`, and `ManifestAddress`.

ADR [0006](adr/0006-blake3-and-sha256-version-hashes.md) makes BLAKE3 the default version-hash algorithm for new version
objects and retains SHA-256 for verification, comparison, lookup parity, and non-version SHA-256 uses. Grace is not in
production, so this page describes the current desired contract rather than a migration promise for old production data.

Hash values in Grace provide cryptographic evidence that the file, directory, and reference versions stored for a
reference match what was originally uploaded. When a user downloads a specific branch version, Grace should be able to
prove that the files and directory versions match the stored version graph. Grace Server maintenance routines should
also be able to verify stored versions.

Unlike Git commit history, Grace version hashes do not create a linkage chain between references. Each version hash is
specific to a stored version object, with no required connection to earlier or later references. This lets Grace delete
references and versions, such as saves that are no longer necessary, without rewriting history.

## Implementation

In ordinary usage, SHA-256 values are computed by Grace CLI and reused by Grace Server when uploading file and directory
versions and creating references.

Grace relies on the .NET implementation of
[SHA-256](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.sha256) in
`System.Security.Cryptography`.

.NET implementations of Grace clients can use the hashing implementation in Grace.Shared, which is used by both Grace
CLI and Grace Server. Implementations in other languages need to reproduce the same inputs and encoding.

### Files

The file SHA-256 value is computed from the file byte stream only. The file relative path and file length are not
appended to the file SHA-256 input. BLAKE3 file version hashes are computed from the same byte-only file stream.

When computing the SHA-256 value for a file, Grace reads from a stream and uses `IncrementalHash` so memory use stays
constant no matter how large the file is.

The file SHA-256 value is computed with this algorithm:

1. Create an `IncrementalHash` instance using the SHA-256 hash algorithm.
1. Read bytes from the file stream into a 64 KiB buffer.
1. Append each populated buffer span to the hash input until the stream is consumed.
1. Finalize the SHA-256 hash as a byte array.
1. Convert each byte to a two-character lowercase hexadecimal value.

For example, `byte[] { 0x43, 0x2a, 0x01, 0xfa }` is represented as `432a01fa`.

The file relative path still matters to Grace's domain model because a `FileVersion` says a specific relative path
contains specific file content in a repository version. Two files with the same bytes at different relative paths have
the same file byte hashes, but they are different `FileVersion` records because their `RelativePath` values differ. The
file SHA-256 calculation itself is byte-only.

### Directories

The directory SHA-256 value is path-sensitive. A `DirectoryVersion` represents a relative path and the sorted set of
child directory and file versions at that path.

The directory SHA-256 value is computed with this algorithm:

1. Create the `grace.directory-version.v1` UTF-8 preimage.
1. Include the directory's repository-relative path.
1. Include each child entry kind, child repository-relative path, child size, and same-algorithm child hash. The size
   field is serialized for every child entry, including directory entries.
1. Sort entries deterministically by normalized child path first, then by child kind only when two entries have the same
   normalized path, before finalizing the preimage.
1. Finalize the SHA-256 hash and convert it to lowercase hexadecimal text.

BLAKE3 directory version hashes use the same `grace.directory-version.v1` preimage shape with BLAKE3 child hashes. This
keeps directory version hashes sensitive to the directory path, child names, child kind, file size, and same-algorithm
child hashes without making the file byte hash include path data.

### Version Hash Transition

New `FileVersion`, `DirectoryVersion`, and `Reference` version objects use BLAKE3 as the default version-hash algorithm.
SHA-256 remains retained for verification, comparison, lookup parity, and non-version SHA-256 uses that intentionally
remain unchanged.

That transition does not change the meaning of CAS identities:

- `FileContentHash` identifies the complete unencoded bytes of a logical file.
- `ChunkAddress` identifies one `ContentChunk`.
- `ContentBlockAddress` identifies one `ContentBlock`.
- `ManifestAddress` identifies one `FileManifest`.

Small and regular `FileVersion` objects can continue to use `WholeFileContent`; this documentation does not make them
manifest-backed or StoragePool-wide deduped.

### Validation

Grace has verification workflows that recompute stored version hashes and compare them with values stored in Grace's
database. Documentation should describe only shipped commands and options as shipped behavior.
