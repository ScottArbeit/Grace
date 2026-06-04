# Manifest Eligibility

`ManifestEligibilityPolicy` decides whether a new file version is stored as repository-scoped `WholeFileContent` or as
manifest-backed `FileManifest` content.

The decision is a creation-time gate. Grace stores the resulting `FileContentReference` shape as the source of truth.
Later policy changes do not reinterpret old file versions.

## Default Policy

Grace Protocol v1 uses this default policy:

```json
{
  "class": "ManifestEligibilityPolicy",
  "thresholdBytes": 1048576,
  "binaryScanBytes": 8192
}
```

The default boundary is:

- Binary files enter the manifest-backed path when uncompressed size is greater than or equal to 1 MiB.
- Text files enter the manifest-backed path when the Grace-defined compressed size is greater than or equal to 1 MiB.
- Binary detection scans the first 8 KiB for a NUL byte.
- Files below the relevant threshold stay on `WholeFileContent`.

## Identity Separation

Eligibility affects which content reference shape a new file version uses. It does not participate in:

- `ChunkAddress`.
- `ContentBlockAddress`.
- `ManifestAddress`.
- The chunking suite.
- Manifest reconstruction.
- Physical ContentBlock payload validation.

This separation is important. Two repositories can choose different eligibility policies without changing the identity
of the same manifest-backed content.

## Boundary Vectors

The eligibility vectors document representative boundaries for portable SDKs:

- Small text content remains `WholeFileContent`.
- Binary content just below 1 MiB remains `WholeFileContent`.
- Binary content exactly at 1 MiB becomes `FileManifest`.
- Text content whose compressed size is below 1 MiB remains `WholeFileContent`.
- Text content whose compressed size is exactly 1 MiB becomes `FileManifest`.
- A NUL byte inside the first 8 KiB marks content as binary.
- A NUL byte after the scan window does not mark content as binary by itself.

Portable implementations should keep this as a selection rule, not a persisted second eligibility decision.
