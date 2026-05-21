# Build versioning

Grace uses one build-version source in `src/Directory.Build.props`. Project files should not declare their own product
versions unless a future release process explicitly documents an exception.

## Version shape

The product base version is `0.2.0`.

Local builds default to a development prerelease version:

```text
0.2.0-dev.<yyyyMMddHHmmss>
```

CI builds pass a monotonic run number and use:

```text
0.2.0-ci.<run number>
```

When a source revision is available, `AssemblyInformationalVersion` appends short commit metadata:

```text
0.2.0-ci.1234+abcdef1
```

`AssemblyVersion` remains `0.2.0.0` for the `0.2.x` line. `FileVersion` stays numeric-only so Windows assembly metadata
remains valid. Container image tags use the exact prerelease build version without `+` metadata, for example
`0.2.0-ci.1234`, plus `latest` where Grace already publishes `latest`.

## CI inputs

GitHub Actions sets these MSBuild properties through job environment variables:

- `GraceBuildKind=ci`
- `GraceBuildNumber=${{ github.run_number }}`
- `GraceSourceRevisionId=${{ github.sha }}`

You can validate the CI version locally with explicit MSBuild properties.

PowerShell:

```powershell
dotnet build ./src/Grace.slnx --configuration Release -p:GraceBuildKind=ci -p:GraceBuildNumber=1234 -p:GraceSourceRevisionId=abcdef1234567890
```

bash / zsh:

```bash
dotnet build ./src/Grace.slnx --configuration Release -p:GraceBuildKind=ci -p:GraceBuildNumber=1234 -p:GraceSourceRevisionId=abcdef1234567890
```

## Runtime identity

Runtime code should read build identity through `Grace.Shared.BuildInfo` instead of reading `AssemblyName.Version` or
`FileVersionInfo` directly. The helper prefers `AssemblyInformationalVersion`, then falls back to product/file/assembly
metadata, and finally to a safe non-empty value.

Current runtime surfaces:

- CLI history records store the informational build identity in `graceVersion`.
- Grace Server startup output logs the informational build identity.
- The root Grace Server hello endpoint displays the same informational build identity.

`Constants.CurrentConfigurationVersion` remains the local configuration schema version. Do not change it as part of a
product build-version bump unless there is an actual configuration schema migration.

## Audit

Run the static audit after changing project or container metadata:

PowerShell:

```powershell
pwsh ./scripts/test-versioning.ps1
```

bash / zsh:

```bash
pwsh ./scripts/test-versioning.ps1
```
