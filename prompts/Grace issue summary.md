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

### 2) Issue Introduction

Provide a concise introduction that explains:

- What the issue is.
- Why it matters.
- Who or what is affected.

### 3) Prompt Log

List every meaningful prompt used to produce this issue. Redact all secret values from the prompt.

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
