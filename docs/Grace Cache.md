# Grace Cache local artifact path

## Purpose

Grace Cache supports one deliberately local path on Linux x64. A producer can commit one immutable
`DirectoryVersionZip` artifact to a managed local root, and one local development caller can read a pre-existing
verified artifact over HTTP on `127.0.0.1`.

## Supported world

- One process owns one SQLite database and one managed artifact root.
- Operations are serialized by the store lease.
- An artifact tuple contains kind, canonical identity, represented directory-version identity, lowercase SHA-256, and
  exact byte size.
- Only `DirectoryVersionZip` is accepted.
- A local reset is the explicit operator action for a `Complete` disagreement.
- `Cache__DatabasePath` and `Cache__ManagedRoot` must name pre-existing readable locations. Cache and AppHost do not
  invent or create them.

## Lifecycle

The only durable lifecycle is `Absent -> Staging -> Complete`.

1. Stage bytes below the managed root.
1. Write, close, and verify exact size and SHA-256.
1. Persist the exact `Staging` tuple and operation identity in SQLite.
1. Publish the deterministic opaque final path without replacement.
1. Persist the exact `Complete` tuple and operation identity transition.
1. Recheck final bytes and SQLite before reporting success.

The commit point is the exact `Complete` SQLite transaction after verified final-file publication. `Staging` is never a
hit. On a fresh store instance, verified `Staging` plus final bytes becomes `Complete`; other staging residue is cleaned
to `Absent`. A `Complete` row that disagrees with its final file fails closed and retains its record for local reset.

## Local read boundary

`GET /directory-version-zips/{directoryVersionId}` requires `canonicalIdentity`, lowercase `sha256`, and `size` query
values. It returns `application/zip` only when the exact supplied tuple matches a SQLite `Complete` row and the managed
final file independently verifies to that row's exact size and SHA-256. Malformed tuples return 400. Missing,
ineligible, conflicting, corrupt, or byte-disagreeing state returns 404 without mutation.

The host exposes no write route and binds only to loopback. It does not include identity, enrollment, liveness, network
fill, recursive metadata, complete-root publication, coalescing, scheduling, retry, reconciliation, or generalized
recovery.
