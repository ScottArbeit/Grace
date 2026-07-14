# Docs And Contributing

Load this reference when updating `README.md`, `CONTRIBUTING.md`, `AGENTS.md`, process docs, Markdown, or HTML
documentation artifacts.

## Documentation Defaults

- Follow the root `AGENTS.md` Markdown guidance and the repo `.markdownlint.jsonc`.
- Use clear, friendly peer-engineer language for technical docs.
- Use product-manager clarity for product-facing docs.
- Keep docs close to the behavior they describe.
- Show PowerShell examples first, then bash / zsh when both are useful.
- Update nearby `AGENTS.md` files when future agents need new behavior, commands, architecture, or workflow context.
- Validate Markdown when practical:

```powershell
npx --yes markdownlint-cli2 "**/*.md"
git diff --check
```

## What To Update

Update docs when changes affect:

- public commands, options, or output
- environment variables or secrets
- build, test, validation, or deployment workflow
- public APIs, SDK behavior, CLI behavior, or HTTP routes
- authentication, authorization, storage, or runtime configuration
- agent workflow that future maintainers should inherit

## CONTRIBUTING Regeneration Checklist

When asked to regenerate or substantially revise `CONTRIBUTING.md`, inspect current repo evidence first:

- `AGENTS.md`
- `src/AGENTS.md`
- `scripts/bootstrap.ps1`
- `scripts/validate.ps1`
- `src/Grace.Shared/Constants.Shared.fs`
- `src/Grace.Aspire.AppHost/AGENTS.md`
- `src/Grace.Aspire.AppHost/Program.Aspire.AppHost.fs`
- `src/Grace.Aspire.AppHost/Properties/launchSettings.json`
- `README.md`
- existing `CONTRIBUTING.md`

Include:

- prerequisites
- bootstrap
- build
- test
- formatting
- running locally
- Aspire run modes
- configuration and environment variables
- secrets and user-secrets guidance
- PR and validation expectations
- docs impact expectations

Use current commands unless repo-local instructions changed them:

```powershell
pwsh ./scripts/bootstrap.ps1
dotnet build ./src/Grace.slnx
dotnet test ./src/Grace.slnx
pwsh ./scripts/validate.ps1 -Fast
pwsh ./scripts/validate.ps1 -Full
```

From `./src` for formatting:

```powershell
dotnet tool run fantomas --recurse .
```

## HTML Process Artifacts

For requested standalone HTML reports:

- Create one local `.html` file.
- Use inline CSS and JavaScript.
- Use inline SVG diagrams when helpful.
- Inspect with Playwright or a browser to ensure formatting and links work.
- JavaScript interactivity is welcomed, and can be helpful for complex reports. External libraries are allowed only if:
  - they are the latest versions
  - loaded from <https://cdnjs.cloudflare.com>
  - they don't require a build step.
- Keep the final chat response brief and include a clickable local path.
