# Grace Cache storage tracer

## Purpose

Grace Cache currently contains one internal storage tracer: a Linux x64 caller can commit one immutable
`DirectoryVersionZip` artifact to a managed local root. The tracer exists to establish truthful filesystem and SQLite
durability before any Cache host, route, identity, enrollment, serving, or network behavior is considered.

## Supported world

- One process owns one SQLite database and one managed artifact root.
- Operations are serialized by the store lease.
- An artifact tuple contains kind, canonical identity, represented directory-version identity, lowercase SHA-256, and
  exact byte size.
- Only `DirectoryVersionZip` is accepted.
- A local reset is the explicit operator action for a `Complete` disagreement.

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

## Deliberate exclusions

This tracer has no public or hosted Cache surface. It does not include routes, listeners, identity, enrollment,
liveness, serving, network fill, recursive metadata, complete-root publication, coalescing, scheduling, durable retry,
reconciliation, or generalized recovery.
