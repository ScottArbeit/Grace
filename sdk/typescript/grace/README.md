# Grace TypeScript Node SDK

`@grace/sdk` exposes the first TypeScript Node facade for Grace API calls. The public compatibility promise lives on
`GraceClient`; generated raw-client artifacts stay internal unless a future diagnostic export explicitly says otherwise.

Browser TypeScript support is out of scope for this milestone.

## Install And Import

```powershell
npm install
npm run build
```

```bash
npm install
npm run build
```

```ts
import { GraceClient } from "@grace/sdk";

const grace = new GraceClient({
  auth: () => process.env.GRACE_TOKEN ?? "",
  baseUrl: process.env.GRACE_SERVER_URI ?? "http://localhost:5000",
  correlationId: () => crypto.randomUUID(),
});

const owner = await grace.request({
  path: "/owner/get",
  query: { ownerName: "scott" },
});

console.log(owner.body);
```

## Tier 2 Simple File Transfer

The TypeScript Node facade includes Tier 2 whole-file compatibility helpers:

- `uploadFile` reads one local file, computes its SHA-256 hash, asks Grace for a server-issued whole-file upload URI,
  and uploads the bytes to that URI.
- `downloadFile` asks Grace for a raw text whole-file download URI and writes the downloaded bytes to an existing output
  directory.

These helpers are intentionally simple. They do not perform manifest upload, ContentBlock transfer, or deduplication.

```ts
const upload = await grace.uploadFile({
  filePath: "C:/work/hello.txt",
  relativePath: "docs/hello.txt",
  repositoryName: "repo",
});

const download = await grace.downloadFile({
  fileVersion: upload.fileVersion,
  outputPath: "C:/work/downloaded-hello.txt",
  repositoryName: "repo",
});

console.log(download.bytesWritten);
```

Grace auth, API version, correlation, lifecycle, and client identity headers apply to the Grace API requests that issue
transfer URIs. The follow-up storage transfer uses only the server-issued URI and does not attach Grace bearer tokens or
client identity headers to the storage request.

## Transport Defaults

`GraceClient` sends these headers on every request:

- `X-Api-Version`: defaults to the released contract version `2023-10-01`.
- `X-Grace-Client-Type`: `TypeScriptNode`.
- `X-Grace-Client-Version`: the package version.
- `X-Correlation-Id`: included when configured globally or for an individual request.
- `Authorization`: included as a bearer token when `auth` is configured.

Set `apiVersion` to a released date version or an explicit server-supported alias when testing contract negotiation. The
facade does not use `edge` as its default.

## Errors And Lifecycle Diagnostics

Non-2xx responses throw `GraceError`. The error preserves:

- HTTP status and parsed response body.
- Transport correlation ID from `X-Correlation-Id`, when present.
- Grace error properties from the JSON error envelope.
- SDK lifecycle headers such as `X-Grace-Client-Support-Status`,
  `X-Grace-Client-Recommended-Version`, and `X-Grace-Client-Update-Url`.
- Upload-session lifecycle diagnostics when returned by storage endpoints.

## Tier 3 Protocol Vectors

The package implements the Grace Protocol v1 deterministic helpers needed by the published vector suite:

- BLAKE3 chunk, ContentBlock, and FileManifest address calculation.
- Canonical lowercase 64-hex address validation.
- `grace-contentblock-v1` compact block encoding and decoding.
- FileManifest reconstruction validation that rejects corrupt payloads, stale manifest addresses, block range gaps or
  reordering, missing ContentBlocks, mismatched ContentBlock addresses, wrong chunking suites, and file-content hash
  mismatches.
- Default manifest eligibility boundary decisions.

The supported vector suite is declared in `package.json` as `graceProtocol.vectorSuite = "grace-protocol-v1"`, with
the currently supported files:

- `content-addresses.v1.json`
- `manifest-validation.v1.json`
- `eligibility.v1.json`

The implementation is native TypeScript/Node and uses a JavaScript BLAKE3 dependency. It does not load .NET assemblies
or shell out to `dotnet` at runtime.

```ts
import {
  computeChunkAddress,
  decodeContentBlock,
  validateFileManifest,
} from "@grace/sdk";

const address = computeChunkAddress(new TextEncoder().encode("alpha chunk"));
const decoded = decodeContentBlock(compactBlockPayload);
const reconstructed = validateFileManifest(fileManifest, contentBlockPayloads);

console.log(address, decoded.address, reconstructed.bytes.byteLength);
```

Eligibility helpers expose the default policy boundary, but automatic manifest upload parity remains out of scope for
this package slice. Use Tier 2 `uploadFile` for simple whole-file transfer until a later Tier 4 integration slice wires
the protocol helpers into end-to-end storage workflows.

## Narrow Tier 4 Local Integration: Transfer Progress

The TypeScript Node facade now implements one selected Tier 4 local integration capability: sanitized local progress
milestones for the existing whole-file transfer helpers.

`uploadFile` and `downloadFile` accept `onProgress` callbacks. The callback receives milestone events when the Grace API
has issued a storage URI, when the local storage transfer starts, and when the local storage transfer completes.

```ts
await grace.uploadFile({
  filePath: "C:/work/hello.txt",
  onProgress: (event) => console.log(event.stage, event.bytesTransferred, event.totalBytes),
  relativePath: "docs/hello.txt",
  repositoryName: "repo",
});
```

Progress events are intentionally sanitized. They include the operation, milestone stage, repository-relative path,
byte counts, and final transfer status, but do not include local absolute file paths, storage URIs, bearer tokens, SAS
query strings, or auth headers. If a progress callback throws, the helper rejects and does not continue to the next
storage-transfer step.

This is a narrow Tier 4 claim. The package still does not claim manifest upload, ContentBlock transfer, direct-storage
deduplication, resumable upload/download, offline watch behavior, or hosted storage parity.

## Current Scope

This package is a TypeScript Node facade with Tier 1 API request support, Tier 2 simple whole-file transfer helpers, and
Tier 3 Grace Protocol v1 vector support. Its Tier 4 local integration scope is limited to sanitized local progress
milestones for the Tier 2 whole-file facade transfer helpers.
