# Feature Map

Features that span several projects, and where each one lives. Project ownership in general is in `src/AGENTS.md` and
the project `AGENTS.md` files.

- **Authorization:** `src/Grace.Shared/Authorization.Shared.fs` (role catalog, effective operations),
  `src/Grace.Server/Security/`, `src/Grace.Authorization.Tests`.
- **Webhooks and approvals:** `src/Grace.Types/Webhooks.Types.fs`, `src/Grace.Shared/Parameters/Approval.Parameters.fs`,
  `src/Grace.Shared/Parameters/Webhook.Parameters.fs`, `src/Grace.Server/Approval*.fs`, `src/Grace.Server/Webhook*.fs`.
- **Candidate review:** `src/Grace.Types/Review.Types.fs`, `src/Grace.Server/Review*.fs`,
  `src/Grace.CLI/Command/Review.CLI.fs`, `src/Grace.CLI/Command/Candidate.CLI.fs`.
- **Work items, queue, and promotion:** `WorkItem`, `Queue`, `Policy`, and `PromotionSet` files in `Grace.Types`,
  `Grace.Server`, and `Grace.Actors` (plus `PromotionQueue.Actor.fs`). A change to one should keep policy snapshots,
  candidate identity, review notes, queue state, promotion-set state, work-item links, and CLI report output aligned.
- **Manifest-backed storage:** `src/Grace.Types/UploadSession.Types.fs`, `ContentBlockMetadata.Types.fs`,
  `ManifestContributionWorkflow.Types.fs`, the matching actors in `src/Grace.Actors`, and `src/Grace.SDK/Storage.SDK.fs`
  with `LocalPlanner.SDK.fs`.
- **Libraries:** `src/Grace.Types/Library.Types.fs`, `src/Grace.Actors/*Library*.Actor.fs`, `src/Grace.CLI/Library/`,
  `src/Grace.CLI/Command/Library.CLI.fs`.
- **Static OpenAPI:** `src/OpenAPI`. Keep it aligned with public routes; internal workflow and test seams such as
  `/approval/request/_seedGenerated` stay out of it.
