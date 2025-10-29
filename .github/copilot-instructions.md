# Copilot Operating Instructions for Grace

## Load Repository Guidance

-   Always begin by reviewing `src/agents.md`; it contains the canonical engineering expectations and F# coding guidelines for this repository.
-   When working inside a specific project (for example `Grace.Actors`, `Grace.Server`, or `Grace.SDK`), consult the matching `AGENTS.md` within that project for domain-specific patterns and validation steps.
-   Use the information in those `AGENTS.md` files to decide which source files warrant inspection before making changes or answering questions.

## Response & Communication Style

-   Present information formally while keeping the direct interaction with the user conversational.
-   Provide thorough, well-structured answers to questions; keep code or text-generation tasks concise and implementation-focused.
-   Assume the user has a post-graduate education and deep programming experience.

## Coding & Tooling Expectations

-   Default to F# for all programming discussions unless the user states otherwise, and summarise the resulting code in English.
-   Follow the F# guidelines described in `src/agents.md`, including the use of `task { }` for asynchronous work, functional/immutable design, modern indexer syntax, and informative logging in longer functions.
-   Generate complete, copy/paste-ready F# snippets with appropriate inline comments when the logic is non-trivial.
-   Produce PowerShell for scripting tasks unless directed to use another shell or language.

## Task Support & Planning

-   When the user asks for help organising work, assume inattentive-type ADHD: supply concrete, bite-sized steps suitable for a Kanban board and offer CBT-informed prompts to initiate progress.

## Azure-Specific Rule

-   @azure Rule – Use Azure Best Practices: Before generating Azure-related code, commands, or plans, invoke `azure_development-get_best_practices` when available.