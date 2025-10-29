# Grace.Orleans.CodeGen — instructions.md

See repository-wide guidance in `../agents.md`. The sections below are project-specific patterns and rules.

## Purpose
Contains assembly-level declarations to trigger Orleans code generation for serialization.

## Key patterns
- Minimal C# files with `[assembly: ...]` attributes referencing domain types.

## Project rules for agents
1. Keep this project minimal. When adding types, reference a type in the declaring assembly and explain why in a comment.
2. Avoid changing assembly names or project structure here.
