# Grace.Aspire.AppHost Agent Notes

- AppHost reads the Grace.Server user-secrets ID and forwards selected auth settings to the `grace-server` project.
- Microsoft auth settings are expected under the raw keys (with `__`) in user-secrets or env vars: `grace__auth__microsoft__client_id`, `grace__auth__microsoft__client_secret`, `grace__auth__microsoft__tenant_id`, `grace__auth__microsoft__authority`, `grace__auth__microsoft__api_scope`.
- When troubleshooting missing auth providers in Aspire, check the `grace-server` environment list in the dashboard and the AppHost startup log line that summarizes forwarded auth keys.
