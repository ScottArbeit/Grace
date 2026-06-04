---
status: accepted
date: 2026-06-03
decision-makers:
  - Scott Arbeit
consulted:
  - Codex
---

# Use generated raw SDK clients behind handwritten language facades

Grace will treat OpenAPI as the authoritative HTTP contract, generate raw clients from it, and expose handwritten
language-native SDK facades as the supported public SDKs. Grace Protocol docs and golden vectors, not the .NET runtime,
define cross-language local protocol semantics.

## Context

Grace needs a stronger SDK and OpenAPI architecture as it grows beyond the current .NET-centered SDK. The OpenAPI
contract should be precise enough to generate raw clients across .NET, TypeScript Node, Python, Rust, and future
languages. At the same time, Grace SDK users need idiomatic, stable language APIs with Grace-native behavior around
auth, base URLs, correlation, client identity, errors, retries, file transfer, and local manifest protocol work.

Generated clients are useful proof of the HTTP contract, but their generated method names, model layout, and package
shape can change when a generator changes or the OpenAPI projection improves. Treating that generated shape as the main
public SDK would make Grace's public compatibility promise depend on generator internals.

Grace also has local protocol behavior that is not fully captured by route descriptions alone. Content addresses,
ContentBlock format, FileManifest reconstruction, manifest eligibility, corruption rejection, dedupe participation, and
manifest upload/download semantics need portable protocol docs and golden vectors so non-.NET SDKs can implement them
without depending on the F#/.NET runtime.

## Decision

Grace will use generated raw clients as internal generated implementation modules behind handwritten SDK facades.

OpenAPI owns the Grace Server HTTP wire contract. The handwritten SDK facade owns the supported public SDK API and the
SDK package compatibility promise. Generated raw clients must be deterministic, validated, compile/import cleanly, and
able to support Grace transport policy, but their exact generated shape is not the stable public SDK contract.

SDK packages use language-native SemVer for facade compatibility. HTTP API contract versions use date-based identifiers
and are selected explicitly by clients through the server API version mechanism. Released SDK facades default to a
pinned API contract version rather than silently using `latest` or `edge`.

Grace Protocol docs and golden vectors define cross-language local protocol semantics for protocol-capable SDKs. The
.NET implementation may remain the reference implementation and vector producer, but TypeScript, Python, Rust, browser,
and future SDKs must not depend on the F#/.NET implementation at runtime.

Grace will start multi-language SDK work in the monorepo with extraction-compatible package boundaries. TypeScript Node
is the first non-.NET SDK target. Browser TypeScript is out of scope for the first SDK milestone. Python remains an
early target, and Rust is included in the generator prototype matrix before any final generator choice.

## Rejected Alternatives

### Publish generated raw clients as the main public SDK

Grace will not make generated raw clients the main public SDK surface. This would expose generator naming, modeling, and
packaging choices as user-facing compatibility promises. It would make generator replacement or regeneration much more
expensive and would not provide the Grace-native ergonomics expected from a supported SDK.

### Publish standalone stable raw-client packages first

Grace will not initially publish separate stable raw generated-client packages such as a dedicated generated TypeScript
or Python client. Raw access may exist for diagnostics or advanced use, but the initial package promise is facade-first.
Separate raw-client packages can be reconsidered only if there is clear user value and the compatibility limits can be
made explicit.

### Require non-.NET SDKs to depend on F#/.NET at runtime

Grace will not require TypeScript, Python, Rust, browser, or future SDKs to load, shell out to, or otherwise depend on
the F#/.NET implementation at runtime for local protocol behavior. Cross-language protocol behavior must be documented
and proven through golden vectors.

### Treat OpenAPI generation as a replacement for protocol docs and vectors

Grace will not treat generated HTTP clients as proof of ContentBlock, FileManifest, chunking, dedupe, eligibility,
corruption rejection, or local upload/download protocol correctness. OpenAPI proves the HTTP contract. Grace Protocol
docs and vectors prove portable local protocol semantics.

## Consequences

This decision gives Grace a clear compatibility boundary:

- OpenAPI is the authoritative HTTP contract.
- The handwritten facade is the supported SDK API.
- SDK package SemVer tracks facade compatibility.
- Date-based API contract versions track wire compatibility.
- Protocol vector suite versions track local protocol compatibility.
- Generated raw clients can be regenerated or replaced without automatically breaking public SDK users.

The cost is more deliberate SDK engineering. Each language needs a facade, package-surface tests, lifecycle handling,
and documentation. Protocol-capable SDKs also need protocol docs, vectors, and language-specific vector tests before
they can claim manifest protocol parity.

The decision also makes OpenAPI quality work more important. Generator evaluation should wait until the OpenAPI
contract is strong enough to judge generators fairly, including operation IDs, tags, response shapes, error shapes,
media types, transport headers, lifecycle headers, examples, source/derived freshness, and generator-specific
compatibility projections.
