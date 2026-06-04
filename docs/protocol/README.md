# Grace Protocol

This folder defines the portable Grace Protocol contract for content-addressed storage. It is written for SDK authors,
cache implementers, and reviewers who need deterministic behavior without reading the .NET implementation.

Grace Protocol v1 covers:

- [Content addresses](addresses.md): lowercase BLAKE3 address format, ContentBlock preimages, and FileManifest
  preimages.
- [Manifest verification](manifests.md): how clients and caches reconstruct bytes and reject corruption or tampering.
- [Manifest eligibility](eligibility.md): when Grace stores a file as repository-scoped `WholeFileContent` versus
  manifest-backed `FileManifest` content.
- [Golden vectors](../../test-vectors/protocol/README.md): versioned JSON fixtures that portable implementations can
  use to verify deterministic behavior.

The protocol docs describe portable behavior. They do not declare TypeScript, Python, Rust, or other non-.NET SDKs
complete. Implementations in those languages should treat the golden vectors as compatibility fixtures, not as runtime
permission to shell out to .NET.

## Versioning

Every protocol fixture has a `schemaVersion` field. Grace Protocol v1 uses the current address families:

- `grace.cas.v1.content-block`
- `grace.cas.v1.file-manifest`
- `grace-contentblock-v1`

Changing address preimages, compact block payload validation, reconstruction semantics, or manifest eligibility
boundaries requires updating the docs and vectors together.

## Identity Boundaries

Grace keeps repository meaning separate from content identity:

- `WholeFileContent` is repository-scoped and does not participate in StoragePool-wide dedupe.
- `ContentBlockAddress` is derived from compact block format version plus the ordered logical chunk addresses.
- `ManifestAddress` is derived from the ordered reconstruction contract.
- Repository id, file path, upload session id, actor id, storage provider, object key, and eligibility policy are not
  part of `ContentBlockAddress` or `ManifestAddress`.

The server remains authoritative for authorization, physical range presence, range claims, and final manifest
acceptance. Dedupe discovery is an optimization and must not be treated as a content-existence oracle.
