# Repository Guidelines

## Project Structure & Module Organization
`Grace.sln` ties together the F# services, tooling, and shared libraries. Core HTTP and Orleans functionality lives in `Grace.Server`, while reusable contracts and helpers belong in `Grace.Shared`. The developer CLI sits in `Grace.CLI`, cloud-facing utilities in `Grace.SDK` and `Grace.Actors`, and serialization helpers in `CosmosSerializer`. Integration and regression tests reside under `Grace.Server.Tests`, and deployment manifests are collected in `AzureResources`, `OpenAPI`, and the Kubernetes YAML files at the repository root.

## Build, Test, and Development Commands
- `dotnet build --configuration Release` – validates the solution and mirrors CI settings.
- `dotnet test --no-build` – runs the NUnit/FsUnit suite in `Grace.Server.Tests`; add `--collect:"XPlat Code Coverage"` when you need coverage artifacts.
- `fantomas .` (or `dotnet tool run fantomas .` if installed as a local tool) – formats touched F# files per the repo defaults.
- Read the `instructions.md` file for specific guidance for each Grace project.

## Coding Style & Naming Conventions
Follow the Microsoft F# style guide: `PascalCase` for types, modules, and DU cases; `camelCase` for values and functions. Keep functions small, prefer immutability, and compose with helpers in `Grace.Shared` before adding new utilities. Order `open` statements alphabetically, align arguments with four-space indentation, and add `///` XML docs to public members. Use the `task { }` computation expression for async work and `Result<'T,'E>` (or discriminated unions) for recoverable errors.

## Testing Guidelines
Tests use NUnit with FsUnit assertions. Name modules and fixtures after the component under test (e.g., `RepositoryServerTests`) and structure assertions with Given/When/Then style comments when intent is not obvious. New behavior requires matching tests; for serialization updates, add round-trip cases for each serializer. Run `dotnet test --no-build` locally before every push to keep the suite green.

## Commit & Pull Request Guidelines
Adopt Conventional Commits (`feat:`, `fix:`, `chore:`, `docs:`) and keep each commit focused. Before pushing, ensure build, tests, and formatting succeed. PRs should summarize the change, call out risks or migration notes (especially for serialized contracts), list tests executed, and link tracking issues. Request reviewers familiar with the impacted area and highlight any follow-up tasks in the description.

## Security & Configuration Tips
Never commit secrets or live connection strings; prefer `.env` overrides for local runs and environment variables or secret stores elsewhere. Preserve structured logging with correlation IDs, and avoid logging PII. When touching serialization or storage code, double-check that migrations keep older data readable.
