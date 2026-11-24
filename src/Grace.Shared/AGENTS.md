# Grace.Shared

Reusable helpers, parameters, and constants used across Grace.

- Keep helpers small and pure; note any side effect in the helper's `///` comment. Changing a widely used helper's
  meaning means updating its callers in the same change.
- Environment variable names live in `Constants.Shared.fs`; use them instead of raw strings.
