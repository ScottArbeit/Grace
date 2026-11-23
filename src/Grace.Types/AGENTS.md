# Grace.Types

The canonical source for Grace's domain types, events, DTOs, and IDs.

- Use records and discriminated unions with clear intent. Keep defaults deterministic and complete.
- Keep MessagePack, Orleans, and System.Text.Json attributes accurate. Keep HTTP concerns out of these types.
- A contract change updates every consumer in the same change; see the propagation chain in `src/AGENTS.md`.
- Explain non-obvious schema choices in a short comment. Test changed behavior in `Grace.Types.Tests`.
