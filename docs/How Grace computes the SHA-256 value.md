# Computing SHA-256 values for files and directories

## Introduction

Grace currently uses SHA-256 values in file, directory, reference, diff, and lookup workflows. These values sit in the
version graph, but their current meaning depends on the object type. Current file SHA-256 values are byte hashes; they
do not by themselves identify stored `FileVersion` objects because a `FileVersion` also carries `RelativePath`.
Directory SHA-256 values are computed from the directory's own path plus child version hashes. References store the
referenced root directory hash; Grace does not compute a separate SHA-256 preimage over the `Reference` object itself.
These values are separate from content-addressed storage identities such as `FileContentHash`, `ChunkAddress`,
`ContentBlockAddress`, and `ManifestAddress`.

ADR [0006](adr/0006-blake3-and-sha256-version-hashes.md) accepts a future model where BLAKE3 is the default version-hash
algorithm for new version objects and SHA-256 remains retained for compatibility, verification, and transition tooling.
That dual-hash behavior is the target for epic #343; this page still documents the current SHA-256 behavior where it is
useful for understanding today's code and stale data.

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

The current file SHA-256 value is computed from the file byte stream only. The file relative path and file length are not
appended to the file SHA-256 input.

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
the same current file SHA-256 value, but they are different `FileVersion` records because their `RelativePath` values
differ. The current file SHA-256 calculation itself is byte-only.

### Directories

The current directory SHA-256 value is path-sensitive. A `DirectoryVersion` represents a relative path and the sorted set
of child directory and file versions at that path.

The directory SHA-256 value is computed with this algorithm:

1. Convert the directory's repository-relative path to bytes using UTF-8 and append those bytes to the hash input.
1. Sort child directories by name with invariant culture.
1. Append each child directory SHA-256 value, in sorted order, as UTF-8 bytes.
1. Sort child files by name with invariant culture.
1. Append each child file SHA-256 value, in sorted order, as UTF-8 bytes.
1. Finalize the SHA-256 hash and convert it to lowercase hexadecimal text.

The current implementation uses child names for sorting, but it does not append those child names to the hash input. A
current directory SHA-256 preimage is the directory's own relative path followed by each child SHA-256 value. The future
`grace.directory-version.v1` preimage from ADR 0006 is planned to be child-name-aware; this page does not describe that
future behavior as shipped.

This keeps the current directory SHA-256 value sensitive to the directory path and ordered child hash sequence without
making the file byte hash include path data.

### Version Hash Transition

After ADR 0006 is implemented, new `FileVersion`, `DirectoryVersion`, and `Reference` version objects use BLAKE3 as the
default version-hash algorithm. SHA-256 remains retained where Grace needs compatibility with existing data, transition
checks, or verification flows.

That transition does not change the meaning of CAS identities:

- `FileContentHash` identifies the complete unencoded bytes of a logical file.
- `ChunkAddress` identifies one `ContentChunk`.
- `ContentBlockAddress` identifies one `ContentBlock`.
- `ManifestAddress` identifies one `FileManifest`.

Small and regular `FileVersion` objects can continue to use `WholeFileContent`; this documentation does not make them
manifest-backed or StoragePool-wide deduped.

### Validation

Grace has planned and in-progress verification workflows that recompute stored version hashes and compare them with
values stored in Grace's database. Documentation should describe only shipped commands and options as shipped behavior.
