
# Continuous review

Continuous review is the system that tracks candidates for promotion, evaluates
risk, records review artifacts, and provides a control plane for gates and
required actions. It is designed to make review deterministic and auditable
while still allowing humans and agents to step in when needed.

At a high level, the flow is: policy defines expectations, Stage 0 analysis
records deterministic signals, candidates are enqueued for promotion, gates and
review packets provide evidence and findings, and checkpoints record how far
review has progressed.

## Big picture concepts

- A policy snapshot is an immutable, hashed set of rules for a target branch.
- A Stage 0 analysis captures deterministic signals for a reference.
- A promotion queue orders candidates that want to land on a target branch.
- An integration candidate represents one promotion attempt and tracks its
  status and required actions.
- Review packets summarize findings, evidence, and chaptering for a candidate.
- Review checkpoints record how far a reviewer has read through references.
- Gates are discrete checks (policy, review, build/test) that produce
  attestations.

## Key building blocks

### Policy snapshots

Policy snapshots are immutable rule bundles associated with a target branch. The
`PolicySnapshotId` is a SHA256 hash of the policy contents plus the parser
version. They are used by the queue, gates, Stage 0 analysis, and review
analysis to ensure deterministic inputs.

Current policy endpoints:

- `POST /policy/current` returns the active snapshot for a target branch.
- `POST /policy/acknowledge` records an acknowledgement for a snapshot.

### Stage 0 analysis

Stage 0 is the deterministic, non-model analysis layer. When a reference of type
Commit, Checkpoint, or Promotion is created, the server records a Stage 0
analysis with a deterministic risk profile. Today, that profile is the default
structure populated with the reference ID, policy snapshot ID (if available),
and a timestamp. It is persisted through the Stage0 actor and can be read
internally for later analysis.

### Promotion queue

Each target branch has a promotion queue that stores the list of candidate IDs
and the current queue state.

Queue state values:

- `Idle`
- `Running`
- `Paused`
- `Degraded`

Queues are created implicitly on first enqueue. Initialization requires a policy
snapshot ID; if the queue does not exist and you enqueue without a policy
snapshot, the server rejects the request.

### Integration candidates

An integration candidate is the unit of work that moves through continuous
review. It captures the target branch, optional work item, policy snapshot,
status, and any required actions, conflicts, gate attestations, and review
packet linkage.

Candidate status values:

- `Pending`
- `Ready`
- `Running`
- `Blocked`
- `Failed`
- `Succeeded`
- `Quarantined`
- `Canceled`

Candidates are created when you enqueue a candidate ID that does not yet exist.

### Gates and gate attestations

Gates are named checks with a definition (name, version, execution mode).
Running a gate produces a GateAttestation that records the result, timestamp,
and metadata. Gate attestations are stored in their own actor and referenced by
the candidate.

Built-in gates today:

- `policy` (synchronous): passes if a policy snapshot exists, blocks otherwise.
- `review` (synchronous): stub, currently always passes.
- `build-test` (async callback): stub, currently returns `Skipped`.

### Evidence selection

Evidence selection uses a deterministic budgeted selector to build slices from a
diff. It supports redaction patterns and scores slices based on change magnitude
and Stage 0 signals such as sensitive paths and dependency config changes. The
resulting EvidenceSetSummary is used for chaptering and model analysis receipts.

### Review packets

A review packet is the primary artifact for a candidate review. It contains
summary text, chapters, findings, optional evidence summaries, and optional gate
summaries. Chapters are built deterministically by grouping paths on the top-
level folder segment. The chapter ID is a SHA256 hash of the ordered path list.

Review packets are stored in the Review actor through `UpsertPacket` events.
There is no public endpoint for upserting packets yet, so review packets are
typically created by internal processes.

### Review checkpoints

Review checkpoints store how far a reviewer has read through references for a
candidate or promotion group. A checkpoint includes the reviewer, reviewed-up-to
reference ID, and policy snapshot ID.

### Required actions

Required actions are machine-readable tasks attached to a candidate, such as
`AcknowledgePolicyChange` or `ResolveConflict`. The server recomputes required
actions on demand and updates the candidate if the computed list differs from
the stored list.

The current required-actions logic adds:

- `AcknowledgePolicyChange` if the candidate has no policy snapshot ID.
- `ResolveConflict` if conflicts are present.
- `RunGate` if the candidate is `Blocked`.
- `FixGateFailure` if the candidate is `Failed`.

## End-to-end flow (today)

1) A reference (commit/checkpoint/promotion) is created.
2) Stage 0 analysis is recorded with a deterministic risk profile.
3) A candidate is enqueued for a target branch.
4) The promotion queue is initialized (if needed) with a policy snapshot ID.
5) The candidate can be queried, its required actions computed, and gates rerun
   on demand.
6) Review packets and findings can be read (if they exist) and findings can be
   resolved.
7) Review checkpoints can be recorded as reviewers progress.

Note: queue processing and gate execution orchestration are not currently
automatic in the server; they are expected to be driven by external services or
manual commands.

## API surface

### Queue and candidate endpoints

- `POST /queue/status`
- `POST /queue/enqueue`
- `POST /queue/pause`
- `POST /queue/resume`
- `POST /queue/dequeue`
- `POST /candidate/get`
- `POST /candidate/cancel`
- `POST /candidate/retry`
- `POST /candidate/required-actions`
- `POST /candidate/attestations`
- `POST /candidate/gate/rerun`

Example: enqueue a candidate.

```json
{
  "TargetBranchId": "8c6f3b1b-53b1-4a1b-84fd-9c8b5a4e2e11",
  "CandidateId": "4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c",
  "PolicySnapshotId": "e3b0c44298fc1c149afbf4c8996fb924",
  "WorkItemId": "f88b46e2-5c36-4b52-9e36-716f7d7a9a8b",
  "PromotionGroupId": "3d5c4d9a-0123-4567-89ab-987654321000",
  "CorrelationId": "corr-1001"
}
```

Example: fetch required actions for a candidate.

```json
{
  "CandidateId": "4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c",
  "CorrelationId": "corr-1002"
}
```

Example: rerun a gate.

```json
{
  "CandidateId": "4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c",
  "GateName": "policy",
  "CorrelationId": "corr-1003"
}
```

### Review endpoints

- `POST /review/packet`
- `POST /review/checkpoint`
- `POST /review/resolve`
- `POST /review/deepen` (stub, not implemented)

Example: open a review packet for a candidate.

```json
{
  "CandidateId": "4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c",
  "CorrelationId": "corr-2001"
}
```

Example: record a review checkpoint.

```json
{
  "CandidateId": "4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c",
  "ReviewedUpToReferenceId": "f12a0d31-0d5a-4a5f-a5a7-3d2c3a9f5b2c",
  "PolicySnapshotId": "e3b0c44298fc1c149afbf4c8996fb924",
  "CorrelationId": "corr-2002"
}
```

Example: resolve a finding.

```json
{
  "CandidateId": "4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c",
  "FindingId": "6e58b4de-7f3b-4a2b-9a6f-111111111111",
  "ResolutionState": "Approved",
  "Note": "Reviewed and acceptable.",
  "CorrelationId": "corr-2003"
}
```

### Policy endpoints

- `POST /policy/current`
- `POST /policy/acknowledge`

Example: acknowledge a policy snapshot.

```json
{
  "TargetBranchId": "8c6f3b1b-53b1-4a1b-84fd-9c8b5a4e2e11",
  "PolicySnapshotId": "e3b0c44298fc1c149afbf4c8996fb924",
  "Note": "Reviewed for Q1 rollout",
  "CorrelationId": "corr-3001"
}
```

## CLI examples

Queue a candidate (initialize queue with policy snapshot if needed):

```powershell
./grace queue enqueue `
  --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c `
  --branch main `
  --policy-snapshot-id e3b0c44298fc1c149afbf4c8996fb924 `
  --work f88b46e2-5c36-4b52-9e36-716f7d7a9a8b
```

Check queue status:

```powershell
./grace queue status --branch main
```

Pause and resume the queue:

```powershell
./grace queue pause --branch main
./grace queue resume --branch main
```

Retry a candidate:

```powershell
./grace queue retry `
  --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c `
  --branch main
```

Open a review packet:

```powershell
./grace review open --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c
```

Record a review checkpoint:

```powershell
./grace review checkpoint `
  --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c `
  --reference f12a0d31-0d5a-4a5f-a5a7-3d2c3a9f5b2c
```

Resolve a finding:

```powershell
./grace review resolve `
  --candidate 4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c `
  --finding-id 6e58b4de-7f3b-4a2b-9a6f-111111111111 `
  --approve `
  --note "Reviewed and acceptable."
```

## SDK examples

Fetch a candidate and required actions (F#):

```fsharp
open Grace.SDK
open Grace.Shared.Parameters.Queue

let candidateParams =
    CandidateParameters(
        CandidateId = "4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c",
        CorrelationId = "corr-1002"
    )
let! candidate = Candidate.Get(candidateParams)

let actionsParams =
    CandidateParameters(
        CandidateId = "4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c",
        CorrelationId = "corr-1002"
    )
let! actions = Candidate.RequiredActions(actionsParams)
```

Open a review packet (F#):

```fsharp
open Grace.SDK
open Grace.Shared.Parameters.Review

let reviewParams =
    GetReviewPacketParameters(
        CandidateId = "4fe3c2a9-35f0-4dc2-9f8b-4d3e2f1a0b9c",
        CorrelationId = "corr-2001"
    )
let! packet = Review.GetPacket(reviewParams)
```

## Deterministic chaptering and baseline drift

- Chaptering groups files by their top-level folder segment (e.g.,
  `src/api/foo.cs` and `src/api/bar.cs` live in the `src` chapter).
- Chapter IDs are a SHA256 hash of the sorted path list for that chapter.
- Baseline drift evaluation exists as a shared helper and compares churn totals
  against policy thresholds (`ChurnLines`, `FilesTouched`). It returns affected
  paths, chapters, and findings. The helper is not yet wired into queue-required
  actions.

## Current limitations and stubs

- Review packets cannot be created via public APIs yet; they are stored only
  through internal actor commands.
- `review/deepen` is a placeholder and returns a not-implemented error.
- `review inbox` and `review delta` CLI commands are stubs.
- Gate implementations are mostly stubbed (review/build-test) and return static
  results.
- Stage 0 analysis currently records default risk profiles; deeper deterministic
  analysis is not wired yet.
- Queue processing and automatic candidate state transitions are not implemented
  in the server.
- Promotion-group review packets are not supported by the CLI yet; it accepts
  `--promotion-group` but errors if used.
