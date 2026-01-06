# Grace.Aspire.AppHost Agent Notes

- AppHost reads the Grace.Server user-secrets ID and forwards selected auth
  settings to the `grace-server` project.
- Auth0/OIDC settings are expected under the raw keys (with `__`) in user
  secrets or env vars: `grace__auth__oidc__authority`,
  `grace__auth__oidc__audience`.
- When troubleshooting missing auth providers in Aspire, check the
  `grace-server` environment list in the dashboard and the AppHost startup log
  line that summarizes forwarded auth keys.
