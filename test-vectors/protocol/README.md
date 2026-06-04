# Grace Protocol Golden Vectors

This folder contains deterministic Grace Protocol fixtures for portable implementations.

The fixtures are versioned JSON files:

- [content-addresses.v1.json](content-addresses.v1.json): BLAKE3 address, ContentBlock preimage, and Manifest preimage
  vectors.
- [manifest-validation.v1.json](manifest-validation.v1.json): compact block payload and manifest reconstruction vectors,
  including corruption and mismatch rejection cases.
- [eligibility.v1.json](eligibility.v1.json): manifest eligibility boundary vectors.

The vectors are compatibility fixtures. They do not declare any non-.NET SDK complete, and portable SDKs must not shell
out to .NET at runtime to satisfy them.

## Determinism Contract

For each fixture:

- `schemaVersion` identifies the fixture schema.
- `protocolVersion` identifies the Grace Protocol contract.
- `generatedBy` records the Grace helper surface that produced the expected values.
- Address values are lowercase 64-character hexadecimal BLAKE3 strings.
- Binary payloads are base64 encoded.

The `Grace.Types.Tests` protocol vector proof tests compare these files against the live deterministic helper functions.
