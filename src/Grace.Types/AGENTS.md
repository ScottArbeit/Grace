# Grace.Types

The canonical source for Grace's domain types, events, DTOs, and IDs. A change here usually reaches other projects; see
the propagation chain in `src/AGENTS.md`.

- Keep defaults deterministic and complete.
- Keep MessagePack, Orleans, and System.Text.Json attributes accurate. Keep HTTP concerns out of these types.
