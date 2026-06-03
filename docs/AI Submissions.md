# AI Submissions for Grace

Grace welcomes AI-assisted contributions when they are transparent, reproducible, and technically strong.

## Why This Exists

Many projects ban AI-generated pull requests because low-quality submissions create review overhead.
Grace takes a different approach: AI assistance is welcomed, but only with clear disclosure, a high quality bar,
and standardized prompt outputs to make review as easy and as deterministic as possible.

## What We Invite

We want your contributions, both in issues and in pull requests. We just want you to run our prompts when you work on
those contributions to make review as streamlined as possible.

Please start with an issue for alignment on features and direction. Once the issue and new requirements are agreed on,
your contributions are welcomed. We don't want any "I have a huge change but didn't discuss it with anyone
first" contributions.

Use your preferred coding agent and provider to investigate issues, implement changes, and draft contribution materials.

For issues, use your agent to investigate the existing code and to develop an idea for a change. When you're ready
to submit the issue, you MUST use the [Grace issue summary](/prompts/Grace%20issue%20summary.md) prompt to create the
issue description.

For pull requests, when you're satisfied that your work is done and fully tested, you MUST use the [Grace pull request summary](/prompts/Grace%20pull%20request%20summary.md)
prompt with your coding agent to create the pull request description.

These standardized formats will help the maintainers of Grace keep up with everything you throw at us. (We hope.)

## Be Cool

Please treat the submission like professional engineering work product that you're proud of. ❤️

## Non-Negotiable Submission Rules

1. Use tooling that is capable of doing careful repository research, making focused changes, and producing verifiable
   evidence for reviewers.
2. Disclose the available run metadata for the AI assistance you used, including harness, provider, model, and
   reasoning or effort settings when the harness exposes them.
3. Use the required Grace issue and pull request template prompts to have your agent automatically create the submission
   package.
   - Issue description prompt: [Grace issue summary.md](/prompts/Grace%20issue%20summary.md)
   - Pull request description prompt: [Grace pull request summary.md](/prompts/Grace%20pull%20request%20summary.md)
4. Include the prompts used to produce the final result.
5. Assume your output will be re-researched and reviewed by other LLMs and humans.

Write-ups should:

- Be clear.
- Be explicit.
- Be thorough.
- Prefer evidence over vague claims.

## Runtime Metadata Guidance

Different harnesses expose different run metadata. Capture what is available from runtime status, settings, logs, or
configuration without guessing. If a value is unknown, say exactly what was checked and why the value could not be
confirmed.

## Quality Expectations

A compliant submission should make reviewer verification fast. The summary prompts are designed to let you create high-quality,
comprehensive submissions that validate that you've done the work correctly, and that communicate its value clearly to reviewers.

## Review and Enforcement

Submissions can be closed or requested for revision when:

1. Model/reasoning metadata is missing or unclear.
2. Prompt history is omitted.
3. The write-up is shallow, unverifiable, or inconsistent with the code.
