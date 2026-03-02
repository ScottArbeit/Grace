# Grace Issue Summary Prompt

Use this prompt to produce a complete GitHub issue body for the Grace repository.

## Role

You are preparing a high-rigor issue write-up that will be reviewed by humans and re-researched by other LLMs.
Write clearly, concretely, and thoroughly.

## Compliance Gate

Before writing, confirm and report:

1. Harness used.
2. Exact model used.
3. Reasoning or effort level used.
4. Whether this run used the latest generally available model from that provider.
5. Whether reasoning is at least equivalent to OpenAI `high`.

If items 4 or 5 are not true, stop and output only:
`NON-COMPLIANT: latest-model and high-reasoning requirements not met.`

## Runtime Metadata Script (Required)

Before drafting the issue body, run the metadata collection script and use its output as the source of truth for:
`harness`, `provider`, `model`, `reasoning_level`, `reasoning_level_equivalent`, `high_reasoning_asserted`,
`latest_model_asserted`, `metadata_source`, and `metadata_evidence`.

PowerShell:

```powershell
pwsh ./scripts/collect-runtime-metadata.ps1 -WorkspacePath <repo-root> -OutputFormat Yaml
```

bash / zsh:

```bash
pwsh ./scripts/collect-runtime-metadata.ps1 -WorkspacePath <repo-root> -OutputFormat Yaml
```

For auditability, prefer a second run with `-Verbose` and capture important discovery notes in section 3 (Prompt Log).

## Copy/Paste Snippet

Use one of these commands to print a ready-to-paste YAML block for the top of the issue body.
After pasting, add `prompt_count: <integer>` on its own line inside the same YAML block.

PowerShell:

```powershell
$meta = pwsh ./scripts/collect-runtime-metadata.ps1 -WorkspacePath . -OutputFormat Yaml
$metaText = $meta -join [Environment]::NewLine
(
  '```yaml' + [Environment]::NewLine + $metaText + [Environment]::NewLine +
  'prompt_count: <integer>' + [Environment]::NewLine + '```'
)
```

bash / zsh:

```bash
meta="$(pwsh ./scripts/collect-runtime-metadata.ps1 -WorkspacePath . -OutputFormat Yaml)"
printf '```yaml\n%s\nprompt_count: <integer>\n```\n' "$meta"
```

## Runtime Metadata Discovery (Required)

Collect run metadata before drafting the issue body.
Use this discovery order and stop at the first authoritative value found for each field
(`harness`, `provider`, `model`, `reasoning_level`):

1. Session/runtime status output exposed by the harness
   (for example `/status`, status line, run header, structured run metadata).
2. Effective runtime settings shown by the harness (active profile, launch flags, resolved settings).
3. Harness configuration files in user home and repository/workspace scopes.
4. Environment variables commonly used for model selection.
5. If still unknown: set value to `unknown` and explain exactly what was checked.

When reading config files, prefer keys like:

- `model`, `default_model`, `model_name`, `engine`, `deployment`, `reasoning_effort`, `reasoning`, `thinking`, `effort`
- profile-scoped overrides that supersede global defaults

Always include a short evidence line for each discovered field. Evidence must be:

- a file path and key name, or
- a runtime status field name

Do not claim a value without evidence.

## Reasoning-Level Normalization (Required)

Set `reasoning_level` to the provider-native setting (verbatim when available). Then compute
`reasoning_level_equivalent` as one of: `low`, `medium`, `high`, `xhigh`.

Normalization rules (use the first rule that applies):

1. If provider explicitly reports one of `low|medium|high|xhigh`, use it directly.
2. If provider reports text containing:
    - `minimal`, `light`, `fast` => `low`
    - `balanced`, `standard`, `normal`, `default` => `medium`
    - `deep`, `intensive`, `high` => `high`
    - `max`, `very-high`, `ultra`, `extended` => `xhigh`
3. If reasoning/thinking is boolean only:
    - disabled/off => `low`
    - enabled/on with no strength/budget detail => `medium` (conservative default)
4. If a numeric reasoning budget is available (tokens/steps):
    - `<= 2000` => `low`
    - `2001-8000` => `medium`
    - `8001-24000` => `high`
    - `> 24000` => `xhigh`
5. If none apply => `unknown` and mark compliance as false.

## Latest-Model Assertion Rule (Required)

Set `latest_model_asserted: true` only when you have explicit evidence from this same run that the selected model is the
latest generally available model for that provider.

Acceptable evidence:

- harness-provided statement that the active model is latest/current, or
- a provider source checked in-run with date and link

If that evidence is missing, set `latest_model_asserted: false`. Do not guess.

## Input You Should Receive

- Issue topic or problem statement.
- Relevant repository path or subsystem (if known).
- Any logs, stack traces, user reports, or screenshots.

If inputs are missing, proceed with best-effort repo research and explicitly list assumptions.

## Research Requirements

1. Inspect the repository before drafting recommendations.
2. Cite concrete evidence in the issue body.
3. Distinguish evidence from inference.
4. Prefer specific findings over general opinions.

For evidence, include:

- File paths.
- Symbols (types/functions/modules/classes).
- Behavioral observations.

Use evidence anchors for every major claim in sections 4 and 5.

Anchor format:

- Preferred: `path:line` or `path:startLine-endLine`
- Acceptable when lines are unavailable: `path` + symbol name

If a claim cannot be anchored, label it `INFERENCE` and include a one-line verification plan.

## Required Output Format

Output only Markdown for the issue body, using this exact section structure and headings.

### 0) Machine-Readable Metadata

Populate YAML metadata fields from `collect-runtime-metadata.ps1` output. Only `prompt_count` may be computed separately.

Start the issue body with this YAML block:

```yaml
harness: <tool/harness name>
provider: <model provider>
model: <exact model identifier>
reasoning_level: <provider-specific setting>
reasoning_level_equivalent: <OpenAI low|medium|high|xhigh equivalent>
latest_model_asserted: true|false
high_reasoning_asserted: true|false
prompt_count: <integer>
metadata_source: <status|runtime-settings|config-file|env|mixed|unknown>
metadata_evidence:
  harness: <short evidence>
  provider: <short evidence>
  model: <short evidence>
  reasoning_level: <short evidence>
generated_at_utc: <ISO-8601 UTC timestamp>
```

### 1) Submission Metadata

- Harness:
- Provider:
- Model:
- Reasoning Level:
- Reasoning Level Equivalent:
- Latest-Model Compliance:
- High-Reasoning Compliance:
- Metadata Source:
- Metadata Evidence:
- Timestamp (UTC):

### 2) Issue Introduction

Provide a concise introduction that explains:

- What the issue is.
- Why it matters.
- Who or what is affected.

### 3) Prompt Log

List every meaningful prompt used to produce this issue. Redact all secret values from the prompt.
Include the exact metadata script command(s) that were run before repo research.

For each prompt, include:

- Prompt ID (P1, P2, ...)
- Purpose
- Prompt text
- Notable impact on findings (1-2 bullets)

### 4) Repository Research Summary

Provide a detailed rundown of the repo research performed.

Include:

- Paths reviewed
- Key findings per path
- Current behavior as implemented today
- Evidence vs inference labels where needed
- Evidence anchors for each major claim

### 5) Recommendations

Break recommendations into these subsections.

#### 5.1 Feature-Level Updates

- What should change in behavior or UX.
- Expected outcomes.
- Evidence anchors for each major recommendation.

#### 5.2 Documentation Updates

- Which docs should be updated.
- What new guidance or corrections should be added.
- Evidence anchors for each major recommendation.

#### 5.3 Code-Level Updates

- Candidate files/components to modify.
- Suggested implementation direction.
- Test coverage additions or updates needed.
- Evidence anchors for each major recommendation.

### 6) Risks and Unknowns

- Technical risks.
- Assumptions.
- Open questions requiring maintainer input.

### 7) Proposed Acceptance Criteria

Provide a concrete checklist that maintainers can use to verify completion.

### 8) Human Accountability

End with this exact checklist item:

- [ ] I have personally reviewed this AI-assisted issue summary, verified the evidence anchors, and I stand behind it.

## Writing Constraints

- Be explicit, not generic.
- Prefer bullet lists with concrete details.
- Keep claims falsifiable and reviewable.
- Assume this text will be audited by additional LLMs, so optimize for clarity and traceability.
