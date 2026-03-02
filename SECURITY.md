# Security Policy

This project is currently **Alpha** quality. Security hardening is ongoing and may not yet cover all threat models.

## Supported versions

Because this project is Alpha, only the **latest code on `main`** is supported.

## Reporting a vulnerability

If you believe you have found a security issue, please report it by opening a **GitHub Issue** and applying the **`Security`** label.

When filing a report, include as much of the following as you can:

- A clear description of the issue and expected vs actual behavior
- Steps to reproduce (minimal repro if possible)
- Affected components, versions, configuration, and environment details
- Impact assessment (what an attacker could do)
- Any proof-of-concept code or logs (redact secrets)

If the issue includes sensitive information (tokens, credentials, private URLs, etc.), **do not post secrets**. Remove/rotate them before submitting.

## What to expect

- We will triage security reports as part of normal development.
- Fixes will be prioritized alongside ongoing work based on severity and effort.
- We may ask for clarification or additional reproduction details.

## Security best practices for contributors

- Do not commit secrets (API keys, connection strings, certificates) to the repository.
- Prefer environment variables or a local secret store for development configuration.
- Keep dependencies updated and avoid introducing unnecessary new dependencies.
- If you introduce authentication/authorization or crypto-related changes, include tests.

## Disclosure

Please avoid public disclosure details (including exploit steps) until a fix is available.

