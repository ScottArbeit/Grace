# Grace.Shared

Reusable helpers, parameters, and constants used across Grace.

- Keep helpers small, pure, and grouped in modules. Note side effects in the helper's `///` comment.
- Changing a widely used helper's meaning means updating its callers in the same change.
- Auth helpers and parameters live in `Grace.Shared.Parameters.Auth` and `Grace.Types.PersonalAccessToken`; keep
  token parsing and formatting pure.
- Environment variable names live in `Constants.Shared.fs`; use them instead of raw strings.
