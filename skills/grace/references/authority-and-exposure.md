# Authority and Exposure

Grace's rules for who may see or change what, and which source of truth a decision may trust.

## Identifiers are not authorization

IDs, hashes, object addresses, cache keys, event IDs, and caller-supplied DTO bodies identify things; they never grant
access unless a current product decision defines them as capabilities. For security-sensitive routes:

- load the stored object and authorize against its authoritative scope, not only the route scope;
- filter lists before counts, pagination, latest-item selection, and ambiguity decisions;
- return hidden resources as NotFound-equivalent where the product requires non-observability;
- authorize before materialization, SAS issuance, webhook delivery, search projection, or notification.

Done means an unauthorized caller cannot tell hidden from missing through counts, errors, hash ambiguity, events, or
generated plans, and a test shows it.

## Stale authority

Name the authority behind each side effect. The usual pairs:

- current request body versus durable stored state;
- current repository configuration versus recorded route or placement evidence;
- local Watch status or cache versus status re-read after acquiring the mutation marker;
- active lifecycle state versus retained retry or cleanup evidence;
- the authorized manifest, reference, or path versus caller-supplied bytes, hashes, object keys, or FileVersion payloads.

Revalidate immediately before the write, SAS issuance, materialization, publication, or cleanup. Tests show a stale
snapshot cannot overwrite newer state, mint object access, publish success, or drop or keep pending work wrongly.

## Roles and secrets

- Approval and webhook operations are deliberately conservative: `ApprovalPolicyManage`, `ApprovalRequestRead`,
  `ApprovalRequestRespond`, `WebhookManage`, `WebhookDeliveryRead`. Update role definitions and applies-to scope rules
  together, and check both scope roles and path permissions for path-sensitive behavior.
- Watch for cross-scope object IDs, stale branch or repository IDs, and mixed-case role IDs.
- TestAuth needs `GRACE_TESTING=1`; check it before assuming it is available.
- Webhook and approval-notification URLs must be public HTTPS. Loopback URLs need a Development host, server opt-in, and
  a per-request acknowledgement. CLI JSON output redacts destination URLs and signing secrets.
- Redact authorization headers, tokens, connection strings, SAS URLs, and credential-bearing URLs in logs and tests. In
  PowerShell scripts, pass SAS URLs through JSON files rather than inline `--settings`.
