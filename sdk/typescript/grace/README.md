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

These helpers are intentionally simple. They do not implement the manifest protocol, ContentBlock transfer,
deduplication, or Tier 3/Tier 4 behavior.

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

## Current Scope

This package is a TypeScript Node facade with Tier 1 API request support and Tier 2 simple whole-file transfer helpers.
Tier 3 manifest protocol behavior is intentionally not implemented here.
