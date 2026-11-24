# Grace.SDK

Client APIs used by the CLI and other consumers to call the Grace server.

- Keep SDK methods thin and aligned with server routes and the DTOs in `Grace.Types`.
- Non-CLI clients must configure their client identity (`ClientIdentity.SDK.fs`) before making API calls.
- Manifest-backed upload is on by default for eligible large files. Whole-file upload is the fallback only for ineligible
  files or recoverable manifest-upload failures, so an eligible file is never uploaded twice.
