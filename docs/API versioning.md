# API Versioning

Grace keeps these version identifiers separate:

- HTTP API contract version: a date-based `X-Api-Version` value such as `2023-10-01`.
- SDK package version: the SemVer version of a released SDK package.
- Product build version: the build identity shown by CLI and server diagnostics.
- Protocol vector suite version: the version for protocol compatibility fixtures.

Released SDK clients send the current released HTTP API contract version by default. A client may opt into `latest` or
`edge`, but those aliases are explicit overrides for development and compatibility testing, not release defaults.

The static OpenAPI document uses the HTTP API contract version in `info.version`. When the HTTP contract changes, update
the shared API contract version constant and the OpenAPI version together so generated clients and runtime defaults stay
aligned.
