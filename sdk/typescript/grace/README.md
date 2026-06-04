# Grace TypeScript Node SDK

`@grace/sdk` exposes the first TypeScript Node facade for Grace Tier 1 API calls. The public compatibility promise lives
on `GraceClient`; generated raw-client artifacts stay internal unless a future diagnostic export explicitly says
otherwise.

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

This package is a TypeScript Node Tier 1 facade. Tier 2 file transfer and Tier 3 protocol behavior are intentionally not
implemented here.
