# Plan

- Step 1: Set up work tracking and capture current routing/authz baseline. Status: Done.
- Step 2: Implement access endpoint authorization + role validation + access resource derivation. Status: Done.
- Step 3: Add full endpoint authorization classification and guardrails (including metrics and hub). Status: Done.
- Step 4: Implement bootstrap SystemAdmin seeding + RoleCatalog BranchAdmin alignment. Status: Done.
- Step 5: Add/update tests for auth coverage, access rules, bootstrap, and guardrails. Status: Done.
- Step 6: Run release build/test checkpoints and reconcile failures. Status: Done.
- Step 7: Final validation, documentation touch-ups, and close out bead/commit. Status: Done.

# Decision Log

- 2026-01-18 03:09:00: Initialized plan file and will refine steps as implementation proceeds.
- 2026-01-18 03:14:00: Used `bd create` with `--repo . --no-daemon` because the daemon routed to a planning repo without an issue prefix.
- 2026-01-18 03:33:00: Implemented endpoint security via a centralized `securityEntries` map + `secureHandler` to force explicit classification.
- 2026-01-18 03:34:00: Added `grace__metrics__allow_anonymous` with default auth-required behavior and warning log when anonymous metrics are enabled.
- 2026-01-18 03:36:00: Bootstrapped SystemAdmin seeding using env-configured principals and wired test host to pass bootstrap user ID.
