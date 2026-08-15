---
status: accepted
date: 2026-07-03
decision-makers:
  - Scott Arbeit
consulted:
  - Codex
---

# Treat empty directories as versioned structure

Grace treats a non-ignored empty directory as meaningful repository structure. An empty directory is represented by a
`LocalDirectoryVersion` or `DirectoryVersion` whose child directory list and file list are both empty. That directory
version still has a relative path, version hashes, size, created timestamp, and last-write timestamp, and parent
directory versions reference it by `DirectoryVersionId`.

## Context

WS2 of the Grace Watch incremental sync epic needs explicit empty-directory semantics before later leaves harden
running watch application, durable journal replay, and platform watcher behavior. Without this contract, Grace could
accidentally treat directories as file-derived implementation detail and drop a directory-only change when no uploaded
file path forces status application.

The current local model already has the right structural concepts:

- `GraceStatus.Index` stores directory versions independently from files.
- Local status persistence stores directory rows separately from directory-file rows.
- The local object cache stores directory version rows and child relationships separately from cached file rows.
- Materialization creates directories from directory-version DTOs before it processes file contents.

Grace is not in production, so this decision states the current model. It does not create migration or compatibility
behavior for older data.

## Decision

A non-ignored empty directory is versioned repository structure.

The empty-directory contract is:

- A directory version with no child directories and no files is valid.
- Parent directory versions preserve empty child directories through their `Directories` list.
- Local status persistence must write and read the empty directory version as a directory row.
- The local object cache may store an empty directory version without child or file rows for that directory.
- Current-state scans should report a non-ignored empty directory that exists in the final path state.
- Current-state scans should not report ignored empty directories.
- Materialization should create an empty directory from a directory-version DTO even when no file versions exist below
  that directory.

This ADR does not change public server, OpenAPI, SDK, or CLI output contracts. It records a local model and
current-state proof point for WS2.1.

## Deferred WS2 behavior

WS2.1 deliberately does not implement the full running-mode watch apply behavior for empty-directory-only event streams.
Later WS2 leaves own these cases:

- A stale parent or subtree delete followed by empty-directory recreation before apply.
- A non-ignored empty directory that exists in final path state after an earlier delete observation saw its parent
  disappear.
- Empty-directory-only replacement or rename preserving the final empty directory add when no uploaded file paths exist
  to force file-derived status application.

Those cases require watch event ordering and application rules beyond the WS2.1 proof slice. They should build on this
contract instead of weakening it.

## Consequences

Tests and future implementation should assert empty directories as directory-version nodes, not as side effects of file
uploads. A proof that only uploads file versions is insufficient for empty-directory semantics.

Ignore processing remains authoritative. Ignored empty directories are untracked and should not become directory
versions, status differences, object-cache rows, or watch apply work.

Future WS2 watch work should use final path state to suppress stale deletes without losing a final non-ignored empty
directory add. When a later change implements running-mode empty-directory application, it should preserve public CLI
JSON, stdout, and stderr behavior unless that issue explicitly changes the CLI contract.
