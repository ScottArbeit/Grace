# Grace.Actors

Orleans grains that run Grace's stateful workflows. State changes are driven by domain events and records from
`Grace.Types`.

- Keep grain constructors light and use dependency injection. Put external service calls in helper modules.
- Make state transitions deterministic and safe to retry; update event application and state writes together.
- When grain interfaces or serialized types change, keep `Grace.Orleans.CodeGen` in sync.
- Actor-specific invariants (cleanup reminders, metadata versioning, workflow ownership) are documented in `///`
  comments on each actor. Read them before changing that actor.
- Add activation and idempotency tests for new grains or transitions, preferably through the server surface.
