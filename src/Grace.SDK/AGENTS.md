# Grace.SDK

Client APIs used by the CLI and other consumers to call the Grace server.

- Keep SDK methods thin and aligned with server routes and the DTOs in `Grace.Types`. Change the SDK, server, and CLI
  together when a contract changes.
- Grace is pre-production: change the public API directly when the issue calls for it, and update every consumer in the
  same change. No deprecation shims.
- Non-CLI clients must configure their client identity (`ClientIdentity.SDK.fs`) before making API calls.
- Notable transport, retry, and upload behavior is documented in `///` comments on the owning module.
- Test new behavior against faked transports or through server contract tests.
