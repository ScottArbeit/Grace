---
status: accepted
date: 2026-07-06
decision-makers:
  - Scott Arbeit
consulted:
  - Codex
---

# Use server-resolved Materialization Plans for Grace Cache artifacts

Grace Cache is an artifact service for materialized repository content. It is not repository authority. Grace Server
resolves repository meaning and authorization first, then Grace Cache serves immutable artifacts named by that resolved
plan when it has them locally.

This ADR records the baseline decision for the Grace Cache governance documentation epic. It is a documentation and
architecture baseline, not a claim that cache runtime behavior is already implemented.

## Context

Grace needs cache language that keeps repository authority with Grace Server while leaving room for efficient local or
nearby artifact delivery. Older cache wording described chunks, references, mirrors, and read-through behavior in ways
that could imply Grace Cache resolves branches, latest/current semantics, account access rules, or path permissions.

That split matters because Grace Cache may be close to CI workers or user machines, but proximity and artifact
possession do not create authority. Grace Server remains the source of truth for:

- repository, owner, organization, account, and caller context
- branch names, references, latest/current-style inputs, and the resolved DirectoryVersionId
- authentication, RBAC roles, path permissions, and authorization decisions
- the Materialization Plan that names the artifacts a caller may fetch

## Decision

Grace will use Materialization Plan terminology for cache-facing repository materialization.

A Materialization Plan is the server-resolved, immutable plan that names artifacts for one authorized DirectoryVersion
scope. Grace Server creates or issues that plan only after resolving repository meaning and authorization. Grace Cache
does not resolve branch names, references, latest/current semantics, account access, or ACL policy.

Grace Cache is an artifact service. It stores and serves immutable artifacts named by server-resolved Materialization
Plans when the caller presents a valid grant for the resolved scope.

The V1 required artifact set is:

- a target-root zip
- recursive metadata for the same DirectoryVersionId

Those artifacts must agree on the same DirectoryVersionId. A cache hit for only one artifact is a partial local presence
state, not proof that the plan is complete or authorized for a different caller.

## Deferred

The following designs are deferred and out of scope for this baseline:

- path-scoped recursive metadata artifacts
- baseline-plus-delta materialization
- cache-side branch, reference, or latest/current resolution
- cache-side account access or path-permission decisions
- treating cache artifact presence as repository authority

## Consequences

Documentation should describe Grace Cache in artifact-centric terms:

- Use `Materialization Plan` for the server-resolved plan.
- Use `Cache Artifact Store` and `Cache Artifact Metadata` for local cache state.
- Describe the current V1 artifact expectation as target-root zip plus recursive metadata for the same
  DirectoryVersionId.
- Mark path-scoped metadata and baseline-plus-delta materialization as deferred until a future ADR or issue accepts
  those behaviors.

Documentation must not describe Grace Cache as a mirror, storage-account substitute, or repository resolver. Chunk and
ContentBlock terminology remains correct for manifest-backed content-addressed storage, but it is not the primary Grace
Cache unit in the cache governance docs.
