# Grace PR Summary Prompt

Use this prompt to produce a complete GitHub pull request body for the Grace repository.

## Role

You are preparing a high-rigor PR summary that will be reviewed by humans and re-researched by other LLMs.
Write clearly, concretely, and thoroughly.

## Compliance Gate

Before writing, confirm and report:

1. Harness used.
2. Exact model used.
3. Reasoning or effort level used.
4. Whether this run used the latest generally available model from that provider.
5. Whether reasoning / effort is at least equivalent to Codex and Claude's `high`.

If items 4 or 5 are not true, stop and output only:
`NON-COMPLIANT: latest-model and high-reasoning requirements not met.`

## Input You Should Receive

- Branch or diff to summarize.
- Work item or issue reference (if applicable).
- Validation results (tests/build/lint/manual checks).

If any input is unavailable, state that explicitly and continue with what is verifiable.

## Analysis Requirements

1. Inspect the actual diff and changed files.
2. Summarize behavior-level change, not just line edits.
3. Map each changed file to what was accomplished.
4. Include evidence from validation results.

Use evidence anchors for every major claim in sections 4 through 7.

Anchor format:

- Preferred: `path:line` or `path:startLine-endLine`
- Acceptable when lines are unavailable: `path` + symbol name

If a claim cannot be anchored, label it `INFERENCE` and include a one-line verification plan.

## Required Output Format

Output only Markdown for the PR body, using this exact section structure and headings.

### 0) Machine-Readable Metadata

Start the PR body with this YAML block:

```yaml
harness: <tool/harness name>
provider: <model provider>
model: <exact model identifier>
reasoning_level: <provider-specific setting>
reasoning_level_equivalent: <OpenAI low|medium|high|xhigh equivalent>
latest_model_asserted: true|false
high_reasoning_asserted: true|false
prompt_count: <integer>
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
- Timestamp (UTC):

### 2) PR Introduction

Provide an introduction that summarizes:

- Overall purpose of the change.
- Related work item/issue (if applicable).
- Why this change is needed now.

### 3) Prompt Log

List every meaningful prompt used to produce this PR summary. Redact all secret values from the prompt.

For each prompt, include:

- Prompt ID (P1, P2, ...)
- Purpose
- Prompt text
- Notable impact on outcome (1-2 bullets)

### 4) Features Changed and Bugs Fixed

Provide a clear bullet list of:

- Features added or changed.
- Bugs fixed.
- Any behavior intentionally unchanged.
- Evidence anchors for each major claim.

### 5) File-by-File Change Summary

For each changed file, include:

- File path
- What changed
- Why that change was necessary
- Evidence anchor

### 6) Validation and Verification

Include exactly what was run and what happened:

- Build/test/lint commands
- Manual verification steps
- Results and any known gaps
- Evidence anchors for each major claim

### 7) Risks, Compatibility, and Follow-Ups

- Risks introduced or reduced
- Backward compatibility notes
- Follow-up work or deferred items
- Evidence anchors for each major claim

### 8) Human Accountability

End with this exact checklist item:

- [ ] I have personally reviewed this AI-assisted PR summary, verified the evidence anchors, and I stand behind it.

## Writing Constraints

- Be explicit, not generic.
- Avoid vague statements like "minor updates".
- Prefer concrete, reviewer-friendly language.
- Assume this text will be audited by additional LLMs, so optimize for clarity and traceability.
