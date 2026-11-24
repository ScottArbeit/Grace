# Grace.Actors

Orleans grains that run Grace's stateful workflows. State changes are driven by domain events and records from
`Grace.Types`.

- Make state transitions deterministic and safe to retry; update event application and state writes together.
- When grain interfaces or serialized types change, keep `Grace.Orleans.CodeGen` in sync.
- New grains and transitions need activation and idempotency tests.
