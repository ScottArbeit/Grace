# Working Directory Update

**Status:** Implemented through merged Issue #923 / PR #1009; Issue #871 is the selected Reference finalization slice
**Quality contract:** Product V1
**Specification source:** `docs/Working Directory Update.md`
**Evidence current through:** 2026-08-22, `origin/main` `7f80ade9`, epic head `3c0c70d8`

## 1. Outcome and scope

Working Directory Update is the bounded local transaction used by `grace branch switch` to make a Grace-indexed working
directory and local SQLite state match one exact selected root. It verifies prepared objects, applies only the tracked
plan, proves the complete final root, commits local completion, and completes typed Branch finalization.

This replacement contract is Branch-only. Merged Issue #960 supplies the first public tracer: hash selection resolves
one exact `DirectoryVersion`, updates the working directory, retains the current Branch identity, and records terminal
completion atomically with verified local status. Merged Issue #922 extracts the selection-neutral local-application
stage through opaque `VerifiedLocalRoot`. Merged Issue #1005 makes BLAKE3 the sole WDU byte-equality check while retaining
SHA-256 selectors and metadata. Merged Issue #923 / PR #1009 supplies the five-input composition and persists a typed
Reference pending completion after verified local completion. Issue #871 now consumes that pending fact to publish the
selected Branch identity and record terminal completion. Watch, Connect, and Doctor remain deferred. Migration, rollback, journals,
automatic recovery, and multi-platform parity remain out of scope.

Working-directory mutation is currently distributed across caller implementations, so cleanup, finalization, and
failure behavior can drift apart. One lifecycle contract prevents Grace from publishing a selected Branch after marker
cleanup fails, keeps partial failures truthful and recoverable, and gives implementation workers one user-safety goal
when lower-level details are incomplete.

## 2. Accepted decisions

| ID | Decision | Lasting consequence | Owner |
| --- | --- | --- | --- |
| DEC-001 | Branch selection is typed as `Reference` or exact-root `DirectoryVersion`. | Reference may change Branch identity; DirectoryVersion retains the current Branch and has no Reference ID. | #869 |
| DEC-002 | Successful Save or no-Save admission seals the sole accepted baseline. | `AcceptedBranchPhase` carries SQLite revision and complete-status fingerprint through preparation. | #923 and #872 |
| DEC-003 | Marker evidence retains typed dispositions. | Missing, exact, different operation, malformed, unsupported, unreadable, and exact-cleanup-failed never collapse to a Boolean. | #869 |
| DEC-004 | Reference finalization is repeatable from persisted typed facts. | Previous Branch publishes once, selected Branch proves prior publication, and a third Branch retains pending. | #871 |
| DEC-005 | Planning and verification cover complete relevant tracked topology. | Every selected entry and tracked predecessor entry is compared; unrelated ignored/untracked content is preserved and excluded unless it is a destructive collision. | #898, #959, and #960 |
| DEC-006 | The lifecycle table is the sole ordering authority. | Cancellation, cleanup, publication/proof, terminal recording, outcomes, and retry follow `WDU-LC-*` rows only. | #881 |
| DEC-007 | The Branch module has one exact five-input seam. | Inputs are sealed phase, typed selection, exact target graph, immutable prepared content, and diagnostic correlation; internal `VerifiedLocalRoot` never becomes durable or public. | #960 |
| DEC-008 | Tests retain stable internal boundaries inside the public tracer. | Pure reconciliation, real filesystem application, atomic completion, and built-command selector tests remain independently reachable without adding enabling pull requests. | #960 |
| DEC-009 | #868 and PR #873 are superseded planning evidence. | Their review findings inform this table but their commits and competing prose are not implementation authority. | #881 |
| DEC-010 | `DirectoryVersion` completion is terminal in the verified-status SQLite transaction. | Hash-selected switching cannot create pending finalization; exact marker cleanup follows the terminal commit and cannot downgrade success. | #960 |
| DEC-011 | Merged Issue #923 keeps local completion private and typed. | `LocalCompletion` distinguishes `ReferencePending` from `DirectoryVersionTerminal`, carries the existing `Receipt`, and never crosses the WDU module interface. | #923 / PR #1009 |
| DEC-012 | The five inputs are the only replaceable Branch facts. | Cancellation remains invocation control and deterministic failure injection is a private test seam; neither becomes a sixth product fact, overload, context bag, or caller callback. | #923 / PR #1009 |
| DEC-013 | Merged Issue #923 owns the first no-Save `AcceptedBranchPhase` producer and exact target-graph value. | The phase seals accepted status, SQLite revision, and complete-status fingerprint before target preparation; Issue #872 later adds Save-enabled production without changing the five-input seam. | #923 / PR #1009 and #872 |

The post-Issue #1005 checkpoint makes these planning selections without changing the compiled lifecycle rows:

- Final local-application admission occurs after prepared-object publication. No revision, status, completion, marker,
  topology snapshot, or plan computed before publication may authorize the first working-tree mutation or zero-action
  `VerifiedLocalRoot`.
- The extracted local-application stage is selection-neutral, while completion is selection-specific. Merged Issue #923
  records Reference pending completion after opaque `VerifiedLocalRoot` and delegates DirectoryVersion to the merged
  terminal behavior unchanged.
- `LocalCompletion` is private non-durable evidence inside WDU. It distinguishes the persisted Reference-pending and
  DirectoryVersion-terminal results without exposing `VerifiedLocalRoot` or asking callers to reconstruct SQLite truth.
  Retry still reconstructs from persisted typed facts rather than this in-memory value.
- Issue #871 may consume only the persisted typed Reference pending fact. It must not expose `VerifiedLocalRoot`, local
  paths, status snapshots, database handles, mutation plans, finalizers, or callbacks.

No product or architecture decision remains open for the bounded Issue #871 slice. Its one outcome is repeatable
Reference completion from persisted facts. Save-enabled construction of the same phase remains Issue #872. Doctor
recovery, Watch, and Connect remain deferred.

## 3. Domain facts and interface

`AcceptedBranchPhase` is opaque and sealed immediately after successful Save or no-Save admission. It contains the
accepted complete status, SQLite revision, and complete-status fingerprint. Merged Issue #923 adds the no-Save
constructor used by the hash-selected producer. Issue #872 later adds Save-enabled construction without changing the
type or the run interface. Preparation carries the same phase unchanged.

`ResolvedTargetGraph` is an opaque target plus the exact target status, required object metadata, and prepared-manifest
identity. Construction rejects a selection, target, graph, or manifest mismatch before the value reaches WDU. Callers
cannot pass these pieces independently.

`WorkingDirectoryUpdate.run` accepts exactly five Branch facts:

1. Sealed `AcceptedBranchPhase`.
2. Typed `Reference` or exact-root `DirectoryVersion` selection.
3. Exact resolved target graph corresponding to that selection.
4. Immutable prepared content for that graph.
5. Diagnostic correlation.

The module derives configuration and paths, fresh scan input, operation identity, completion and marker facts, and typed
Branch finalization facts. It accepts no caller finalizer, callback, progress observer in place of correlation, status
graph, path or reader bundle, generic context bag, mutation plan, filesystem writer, or database handle. Retry
reconstructs solely from persisted typed operation facts.

Cancellation remains explicit invocation control. A private deterministic failure seam remains available to focused
tests. Neither is a caller-replaceable Branch fact and neither permits an overload that omits or substitutes one of the
five inputs.

Issue #922 supplies one private stage behind this interface. Its inputs are the held lease, exact owned marker attempt,
sealed phase and selection facts, exact target graph, and immutable prepared content already validated against the
manifest. It publishes required object-cache copies, performs final admission from freshly read local facts, derives one
fresh plan, applies it, verifies the complete relevant root, and returns only `Rejected`, `UpdateIncomplete`, or opaque
non-durable `VerifiedLocalRoot`. It neither writes SQLite completion nor decides Branch finalization.

The first working-tree mutation and SQLite local completion are distinct boundaries. For `Reference`, local completion
atomically writes verified status, object metadata, and a pending Branch operation. For `DirectoryVersion`, the same
transaction records verified status, object metadata, and terminal completion because Branch identity does not change.
It is never called merely “commit” in lifecycle evidence.

Issue #923 composes those paths behind a private five-input local-transaction seam:

- `DirectoryVersion` atomically retains the merged Issue #960 terminal transaction and returns private
  `DirectoryVersionTerminal(Receipt)` to the existing post-commit marker cleanup and `Updated` or `Unchanged` projection.
- `Reference` invokes the merged selection-neutral application, then atomically stores verified status, object metadata,
  and `BranchFinalization` pending facts. Success returns private `ReferencePending(Receipt)` for Issue #871 to consume
  inside WDU.
- `Rejected` remains possible only before mutation or `VerifiedLocalRoot`. A completion failure after
  `VerifiedLocalRoot` is `UpdateIncomplete` and retains exact marker evidence.

`LocalCompletion` never crosses into `Branch.CLI`, never becomes durable state, and never becomes retry input. The
Reference path is not wired into the public Branch command until Issue #871 consumes `ReferencePending` for repeatable
finalization. This keeps the epic branch safe and prevents a half-active Reference switch while allowing direct
real-filesystem and SQLite tests at the stable private seam.

## 4. Normative Branch lifecycle table

This fenced JSON block is the sole normative lifecycle ordering contract. Human prose, ADRs, implementation issues, and
tests cite `WDU-LC-*` row IDs; they must not restate or reorder the transition sequence. #889 checks its closed
structural grammar, graph, and canonical digest; consumers must not infer aliases, wildcard behavior, or row precedence.

<!-- grace:wdu-lifecycle-contract:start -->
```json
{
  "schema": "grace.wdu.branch-lifecycle/v1",
  "artifactIdentity": "issue-928",
  "canonicalContentDigest": "ccd29ba6b55dde396be5c1c9244958999e7cc8173c0e5e8243f1b57fe4bc3c92",
  "boundaries": {
    "firstWorkingTreeMutation": "first tracked working-path mutation",
    "verifiedLocalRoot": "opaque non-durable relevant-topology proof immediately before pending SQLite completion",
    "sqliteLocalCompletion": "atomic verified status, object metadata, and pending-operation write",
    "firstApplicableRetryWrite": "exactCleanup, branchPublication, or terminalRecording selected from persisted facts"
  },
  "retryAdmission": {
    "source": "exact persisted pending operation, selection, marker evidence, and current Branch evidence",
    "requiredActions": ["reconstructPersistedTypedFacts", "acquireLocalLease", "rereadMarkerAndCurrentBranch", "selectRowFromFreshEvidence"],
    "staleEvidenceAction": "retainPendingAndDisallowedEvidenceWithoutBranchPublication"
  },
  "doctorCommand": "grace doctor --repair-local-state",
  "order": [
    "sqliteLocalCompletion",
    "postCompletionMarkerInspection",
    "conditionalExactCleanup",
    "typedBranchPublicationOrProof",
    "terminalRecording"
  ],
  "machineGrammar": {
    "predicateAxes": ["invocation", "trigger", "marker", "selectionState"],
    "encoding": {
      "one": {"jsonShape":{"kind":"one","value":"<concrete-enum-member>"},"meaning":"one concrete value"},
      "set": {"jsonShape":{"kind":"set","values":["<concrete-enum-member>"]},"meaning":"nonempty duplicate-free union of concrete values"},
      "aggregate": {"jsonShape":{"kind":"aggregate","name":"<declared-axis-aggregate>"},"meaning":"exact declared expansion; aggregates cannot nest"}
    },
    "concreteEnums": {
      "invocation": ["initial", "terminalReplay", "finalizationRetry"],
      "trigger": ["afterSqliteLocalCompletion", "afterSqliteLocalCompletionBytesChanged", "afterSqliteLocalCompletionBytesUnchanged", "branchPublicationFails", "branchPublicationFailsAfterExactCleanup", "cancelAfterBranchPublicationBegins", "cancelAfterExactCleanupBegins", "cancelAfterFirstWorkingTreeMutation", "cancelAfterOwnedMarkerBeforeFirstWorkingTreeMutation", "cancelAfterTerminalRecordingBegins", "cancelBeforeFirstWorkingTreeMutation", "cancelImmediatelyBeforeFirstApplicableRetryWrite", "disallowedMarker", "exactCleanupAndTerminalSucceedAfterSelectedBranchProof", "exactCleanupFails", "exactCleanupPublicationAndTerminalSucceed", "exactSameOperationAdoption", "exactTerminalCompletionRegardlessOfInvocationCancellation", "failureAfterFirstWorkingTreeMutationBeforeVerifiedLocalRoot", "failureAfterVerifiedLocalRootBeforeSqliteLocalCompletion", "failureBeforeFirstWorkingTreeMutation", "finalPreMutationRereadCleanupFails", "finalPreMutationRereadMatches", "finalPreMutationRereadRejects", "firstWorkingTreeMutationBegins", "missingMarkerFreshAdmission", "ownedMarkerCleanupFailsBeforeFirstWorkingTreeMutation", "postTerminalExactCleanupFailsBytesChanged", "postTerminalExactCleanupFailsBytesUnchanged", "preLocalAdmissionRefused", "publicationAndTerminalSucceed", "terminalOwnedDifferentOperationAdmission", "terminalRecordingFailsAfterExactCleanupAndPublicationProof", "terminalRecordingFailsAfterExactCleanupAndSelectedBranchProof", "terminalRecordingFailsAfterPublicationProof", "terminalRecordingSucceedsAfterPublicationProof", "thirdBranchBlocksAfterExactCleanup", "thirdBranchBlocksFinalization", "verifiedDirectoryVersionRootReadyForSqliteTerminalCompletion", "verifiedRootReadyForSqliteLocalCompletion"],
      "marker": ["notApplicable", "missing", "exact", "differentOperation", "malformed", "unsupported", "unreadable", "exactCleanupFailed"],
      "selectionState": ["referencePrevious", "referenceSelected", "referenceThird", "directoryVersion"],
      "firstApplicableRetryWrite": ["none", "exactCleanup", "branchPublication", "terminalRecording"],
      "exitClass": ["success", "nonzero"],
      "admissionMode": ["freshMissingMarker", "adoptedExactOperation"],
      "reconciliationState": ["needsApply", "alreadySatisfied"]
    },
    "aggregates": {
      "marker": {
        "none": ["notApplicable"],
        "any": ["notApplicable", "missing", "exact", "differentOperation", "malformed", "unsupported", "unreadable", "exactCleanupFailed"],
        "ownedOrNone": ["notApplicable", "missing", "exact"],
        "actualEvidence": ["missing", "exact", "differentOperation", "malformed", "unsupported", "unreadable", "exactCleanupFailed"],
        "postCompletionEvidence": ["missing", "exact", "differentOperation", "malformed", "unsupported", "unreadable", "exactCleanupFailed"]
      },
      "selectionState": {
        "any": ["referencePrevious", "referenceSelected", "referenceThird", "directoryVersion"],
        "references": ["referencePrevious", "referenceSelected", "referenceThird"],
        "persisted": ["referencePrevious", "referenceSelected", "referenceThird", "directoryVersion"]
      }
    },
    "expansion": {
      "rule": "Resolve each match axis by its kind, then take the Cartesian product across all four axes.",
      "setMembers": "Set values are concrete members of that axis only; unknown values, empty sets, duplicates, and mixed shapes are invalid.",
      "aggregateMembers": "Aggregate names are valid only on the axis where declared; unknown names and aggregate tokens inside sets are invalid.",
      "example": "WDU-LC-100 expands five marker values times three Reference states into 15 applicable cells; WDU-LC-026/028 and WDU-LC-036/038 are the explicit DirectoryVersion bytesChanged split."
    },
    "overlap": {
      "applicabilityKey": ["invocation", "trigger", "marker", "selectionState"],
      "rule": "Expanded applicability keys must be disjoint; duplicate keys are invalid and there is no first-row-wins precedence.",
      "routing": "A routing row selects only its declared nextRows after its own key matches; nextRows do not create precedence."
    },
    "terminalReplay": {
      "row": "WDU-LC-003",
      "selectionExpansion": "persisted expands to all four concrete persisted selection states",
      "markerExpansion": "any expands to all recognized marker values because exact terminal SQLite evidence is authoritative",
      "effects": "No marker, working-file, Branch, completion, or retry write occurs; invocation cancellation is ignored and the outcome is Unchanged."
    },
    "rowVector": ["WDU-LC-200","WDU-LC-201","WDU-LC-213","WDU-LC-202","WDU-LC-203","WDU-LC-204","WDU-LC-205","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-211","WDU-LC-212","WDU-LC-001","WDU-LC-005","WDU-LC-002","WDU-LC-004","WDU-LC-006","WDU-LC-008","WDU-LC-007","WDU-LC-003","WDU-LC-010","WDU-LC-011","WDU-LC-012","WDU-LC-013","WDU-LC-014","WDU-LC-015","WDU-LC-020","WDU-LC-021","WDU-LC-022","WDU-LC-023","WDU-LC-024","WDU-LC-025","WDU-LC-026","WDU-LC-027","WDU-LC-028","WDU-LC-030","WDU-LC-031","WDU-LC-032","WDU-LC-033","WDU-LC-034","WDU-LC-035","WDU-LC-036","WDU-LC-037","WDU-LC-038","WDU-LC-100","WDU-LC-101","WDU-LC-102","WDU-LC-103","WDU-LC-104","WDU-LC-105","WDU-LC-106","WDU-LC-107","WDU-LC-108","WDU-LC-109","WDU-LC-110","WDU-LC-111","WDU-LC-112","WDU-LC-113","WDU-LC-114","WDU-LC-120","WDU-LC-121","WDU-LC-122","WDU-LC-123","WDU-LC-130"]
  },
  "machineMetadata": {
    "decisionIds": ["DEC-001","DEC-002","DEC-003","DEC-004","DEC-005","DEC-006","DEC-007","DEC-008","DEC-009","DEC-010"],
    "requirements": [
      {"id":"REQ-001","owner":"#960"},{"id":"REQ-002","owner":"#869"},{"id":"REQ-003","owner":"#837"},{"id":"REQ-004","owner":"#839"},{"id":"REQ-005","owner":"#869"},{"id":"REQ-006","owner":"#898"},{"id":"REQ-007","owner":"#960"},{"id":"REQ-008","owner":"#960"},{"id":"REQ-009","owner":"#838"},{"id":"REQ-010","owner":"#838"},{"id":"REQ-011","owner":"#871"},{"id":"REQ-012","owner":"#871"},{"id":"REQ-013","owner":"#960"},{"id":"REQ-014","owner":"#960"},{"id":"REQ-015","owner":"#842"},{"id":"REQ-016","owner":"#871"},{"id":"REQ-017","owner":"#846"},{"id":"REQ-018","owner":"#928"},{"id":"REQ-019","owner":"#960"}
    ],
    "artifacts": [
      {"id":"adr-0011","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-213","WDU-LC-202","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-212","WDU-LC-006","WDU-LC-008","WDU-LC-007","WDU-LC-010","WDU-LC-015","WDU-LC-020","WDU-LC-023","WDU-LC-025","WDU-LC-026","WDU-LC-027","WDU-LC-028","WDU-LC-030","WDU-LC-033","WDU-LC-035","WDU-LC-036","WDU-LC-037","WDU-LC-038","WDU-LC-100","WDU-LC-101","WDU-LC-103","WDU-LC-110","WDU-LC-114","WDU-LC-120","WDU-LC-123","WDU-LC-130","WDU-LC-003"]},
      {"id":"epic-835","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-213","WDU-LC-202","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-212","WDU-LC-006","WDU-LC-008","WDU-LC-007","WDU-LC-010","WDU-LC-015","WDU-LC-020","WDU-LC-026","WDU-LC-027","WDU-LC-028","WDU-LC-030","WDU-LC-036","WDU-LC-037","WDU-LC-038","WDU-LC-100","WDU-LC-101","WDU-LC-103","WDU-LC-110","WDU-LC-114","WDU-LC-120","WDU-LC-123","WDU-LC-130","WDU-LC-003"]},
      {"id":"issue-842","rowIds":["WDU-LC-100","WDU-LC-101","WDU-LC-102","WDU-LC-103","WDU-LC-104","WDU-LC-105","WDU-LC-106","WDU-LC-107","WDU-LC-108","WDU-LC-109","WDU-LC-110","WDU-LC-111","WDU-LC-112","WDU-LC-113","WDU-LC-114","WDU-LC-120","WDU-LC-121","WDU-LC-122","WDU-LC-123","WDU-LC-130","WDU-LC-003"]},
      {"id":"issue-843","rowIds":["WDU-LC-003","WDU-LC-100","WDU-LC-101","WDU-LC-103"]},
      {"id":"issue-846","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-213","WDU-LC-202","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-212","WDU-LC-006","WDU-LC-008","WDU-LC-007","WDU-LC-010","WDU-LC-015","WDU-LC-020","WDU-LC-026","WDU-LC-027","WDU-LC-028","WDU-LC-030","WDU-LC-036","WDU-LC-037","WDU-LC-038","WDU-LC-100","WDU-LC-101","WDU-LC-103","WDU-LC-110","WDU-LC-114","WDU-LC-120","WDU-LC-123","WDU-LC-130","WDU-LC-003"]},
      {"id":"issue-869","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-213","WDU-LC-202","WDU-LC-203","WDU-LC-204","WDU-LC-205","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-211","WDU-LC-212","WDU-LC-010","WDU-LC-011","WDU-LC-012","WDU-LC-013","WDU-LC-014","WDU-LC-015","WDU-LC-100"]},
      {"id":"issue-898","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-209","WDU-LC-210","WDU-LC-211","WDU-LC-212"]},
      {"id":"issue-928","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-209","WDU-LC-210","WDU-LC-002","WDU-LC-006","WDU-LC-008","WDU-LC-007"]},
      {"id":"issue-960","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-213","WDU-LC-202","WDU-LC-203","WDU-LC-204","WDU-LC-205","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-211","WDU-LC-212","WDU-LC-001","WDU-LC-005","WDU-LC-002","WDU-LC-004","WDU-LC-008","WDU-LC-007","WDU-LC-003","WDU-LC-026","WDU-LC-027","WDU-LC-028","WDU-LC-036","WDU-LC-037","WDU-LC-038"]},
      {"id":"issue-922","rowIds":["WDU-LC-209","WDU-LC-210","WDU-LC-002","WDU-LC-004","WDU-LC-006","WDU-LC-008","WDU-LC-007"]},
      {"id":"issue-923","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-202","WDU-LC-203","WDU-LC-204","WDU-LC-205","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-211","WDU-LC-212","WDU-LC-001","WDU-LC-005","WDU-LC-002","WDU-LC-004","WDU-LC-006","WDU-LC-007"]},
      {"id":"issue-900","rowIds":["WDU-LC-213","WDU-LC-008","WDU-LC-003","WDU-LC-026","WDU-LC-027","WDU-LC-028","WDU-LC-036","WDU-LC-037","WDU-LC-038"]},
      {"id":"issue-901","rowIds":["WDU-LC-001","WDU-LC-002","WDU-LC-004","WDU-LC-008","WDU-LC-007","WDU-LC-003","WDU-LC-026","WDU-LC-027","WDU-LC-028","WDU-LC-036","WDU-LC-037","WDU-LC-038"]},
      {"id":"issue-871","rowIds":["WDU-LC-020","WDU-LC-021","WDU-LC-022","WDU-LC-023","WDU-LC-024","WDU-LC-025","WDU-LC-030","WDU-LC-031","WDU-LC-032","WDU-LC-033","WDU-LC-034","WDU-LC-035","WDU-LC-100","WDU-LC-101","WDU-LC-102","WDU-LC-103","WDU-LC-104","WDU-LC-105","WDU-LC-106","WDU-LC-107","WDU-LC-108","WDU-LC-109","WDU-LC-110","WDU-LC-111","WDU-LC-112","WDU-LC-113","WDU-LC-114","WDU-LC-120","WDU-LC-121","WDU-LC-122","WDU-LC-123","WDU-LC-130","WDU-LC-003"]},
      {"id":"issue-872","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-211","WDU-LC-212","WDU-LC-001","WDU-LC-005","WDU-LC-002","WDU-LC-004","WDU-LC-006","WDU-LC-007","WDU-LC-003"]}
    ],
    "expectedCounts": {"decisionCount":10,"requirementCount":19,"artifactCount":15,"rowCount":66,"applicabilityKeyCount":244}
  },
  "rows": [
    {"id":"WDU-LC-200","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"missingMarkerFreshAdmission"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["createExactOwnedMarkerWithFreshAttemptToken","rereadCompleteLocalStatusAndMarker","buildFreshPlanFromCurrentTrackedGraph","reconcileFreshAdmissionAsNeedsApplyOnly","discardEveryPriorPlan"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":null,"exitClass":null,"doctorGuidance":null,"resultingMarker":"exact","nextRows":["WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-211","WDU-LC-212"]},
    {"id":"WDU-LC-201","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"exactSameOperationAdoption"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["verifyMarkerSchema","verifyRepositoryAndLocalRootScope","verifyExactOperationIdentity","verifyExactTargetIdentity","replaceAttemptTokenWithFreshAttemptToken","rereadCompleteLocalStatusAndMarker","buildFreshPlanFromCurrentTrackedGraph","reconcileExactAdoptionAsNeedsApplyOrAlreadySatisfied","discardEveryPriorPlan"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":null,"exitClass":null,"doctorGuidance":null,"resultingMarker":"exact","nextRows":["WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-211","WDU-LC-212"]},
    {"id":"WDU-LC-213","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalOwnedDifferentOperationAdmission"},"marker":{"kind":"one","value":"differentOperation"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["readPriorMarkerIdentity","requireTerminalSqliteOperationAndTargetMatchPriorMarker","requireCurrentStatusRootMatchesPriorMarkerTarget","replacePriorMarkerWithExactOwnedMarkerAndFreshAttemptToken","rereadCompleteLocalStatusAndMarker","buildFreshPlanFromCurrentTrackedGraph","reconcileFreshAdmissionAsNeedsApplyOnly","discardEveryPriorPlan"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"previousTerminalRetained","outcome":null,"exitClass":null,"doctorGuidance":null,"resultingMarker":"exact","nextRows":["WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-211","WDU-LC-212"]},
    {"id":"WDU-LC-202","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"differentOperation"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-203","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"malformed"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-204","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"unsupported"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-205","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"unreadable"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-206","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"exactCleanupFailed"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-207","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"cancelAfterOwnedMarkerBeforeFirstWorkingTreeMutation"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanOnlyExactOwnedMarker"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-208","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"ownedMarkerCleanupFailsBeforeFirstWorkingTreeMutation"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["attemptCleanOnlyExactOwnedMarker","retainExactMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"exactCleanupFailed"},
    {"id":"WDU-LC-209","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"finalPreMutationRereadMatches"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["rereadAcceptedRevisionAndCompleteStatusFingerprint","rereadMarkerSchemaScopeOperationTargetAndAttemptToken","verifyFreshPlanAgainstReread","compareCompleteRelevantTopologyWithPrefixAdvancedExpectedState","checkCancellationImmediatelyBeforeVerifiedLocalRootOrFirstMutation","routeZeroActionToVerifiedLocalRootOrMutatingPlanToFirstAction"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-207","WDU-LC-208","WDU-LC-210","WDU-LC-006","WDU-LC-008","WDU-LC-007"]},
    {"id":"WDU-LC-210","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"firstWorkingTreeMutationBegins"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["beginTrackedWorkingTreeMutation","ignoreCancellation","compareCompleteRelevantTopologyWithPrefixAdvancedExpectedStateBeforeEveryLaterAction","applyFreshPlan","verifyCompleteRelevantTrackedTopology","transitionToVerifiedLocalRoot"],"workingFiles":"actualEvidence","branchIdentity":"unchanged","durableResult":null,"outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-002","WDU-LC-006","WDU-LC-008","WDU-LC-007"]},
    {"id":"WDU-LC-211","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"finalPreMutationRereadRejects"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["rejectStaleRevisionFingerprintMarkerOperationTargetOrPlan","cleanOnlyExactOwnedMarker"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-212","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"finalPreMutationRereadCleanupFails"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["rejectStaleRevisionFingerprintMarkerOperationTargetOrPlan","attemptCleanOnlyExactOwnedMarker","retainExactMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"exactCleanupFailed"},

    {"id":"WDU-LC-001","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"cancelBeforeFirstWorkingTreeMutation"},"marker":{"kind":"aggregate","name":"none"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":[],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":false},
    {"id":"WDU-LC-005","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"failureBeforeFirstWorkingTreeMutation"},"marker":{"kind":"aggregate","name":"none"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":[],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":false},
    {"id":"WDU-LC-002","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"failureAfterFirstWorkingTreeMutationBeforeVerifiedLocalRoot"},"marker":{"kind":"aggregate","name":"ownedOrNone"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMutationEvidence"],"workingFiles":"mayDiffer","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"UpdateIncomplete","exitClass":"nonzero","doctorGuidance":false},
    {"id":"WDU-LC-004","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"cancelAfterFirstWorkingTreeMutation"},"marker":{"kind":"aggregate","name":"actualEvidence"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["ignoreCancellation","continueByActualEvidence"],"workingFiles":"actualEvidence","branchIdentity":"actualEvidence","durableResult":null,"outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-002","WDU-LC-006","WDU-LC-008","WDU-LC-007"]},
    {"id":"WDU-LC-006","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"verifiedRootReadyForSqliteLocalCompletion"},"marker":{"kind":"aggregate","name":"postCompletionEvidence"},"selectionState":{"kind":"aggregate","name":"references"}},"firstApplicableRetryWrite":"none","requiredActions":["ignoreCancellation","recordSqlitePendingLocalCompletion","returnLocalCompletionWithEphemeralBytesChanged","inspectPostCompletionMarker"],"workingFiles":"verifiedRelevantTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-010","WDU-LC-011","WDU-LC-012","WDU-LC-013","WDU-LC-014","WDU-LC-015","WDU-LC-020","WDU-LC-021","WDU-LC-022","WDU-LC-023","WDU-LC-024","WDU-LC-025","WDU-LC-030","WDU-LC-031","WDU-LC-032","WDU-LC-033","WDU-LC-034","WDU-LC-035"]},
    {"id":"WDU-LC-008","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"verifiedDirectoryVersionRootReadyForSqliteTerminalCompletion"},"marker":{"kind":"aggregate","name":"postCompletionEvidence"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["ignoreCancellation","proveCurrentBranchUnchanged","recordVerifiedStatusObjectMetadataAndTerminalCompletionAtomically","returnTerminalCompletionWithEphemeralBytesChanged","inspectPostCompletionMarker"],"workingFiles":"verifiedRelevantTarget","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-026","WDU-LC-027","WDU-LC-028","WDU-LC-036","WDU-LC-037","WDU-LC-038"]},
    {"id":"WDU-LC-007","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"failureAfterVerifiedLocalRootBeforeSqliteLocalCompletion"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["ignoreCancellation","retainExactMarkerEvidence"],"workingFiles":"verifiedRelevantTarget","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"UpdateIncomplete","exitClass":"nonzero","doctorGuidance":false},
    {"id":"WDU-LC-003","match":{"invocation":{"kind":"one","value":"terminalReplay"},"trigger":{"kind":"one","value":"exactTerminalCompletionRegardlessOfInvocationCancellation"},"marker":{"kind":"aggregate","name":"any"},"selectionState":{"kind":"aggregate","name":"persisted"}},"firstApplicableRetryWrite":"none","requiredActions":[],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"existingTerminal","outcome":"Unchanged","exitClass":"success","doctorGuidance":false},

    {"id":"WDU-LC-010","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"differentOperation"},"selectionState":{"kind":"aggregate","name":"references"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarker","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-011","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"malformed"},"selectionState":{"kind":"aggregate","name":"references"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarker","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-012","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"unsupported"},"selectionState":{"kind":"aggregate","name":"references"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarker","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-013","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"unreadable"},"selectionState":{"kind":"aggregate","name":"references"}},"firstApplicableRetryWrite":"none","requiredActions":["retainEvidence","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-014","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"exactCleanupFails"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"references"}},"firstApplicableRetryWrite":"none","requiredActions":["attemptCleanExactMarker","retainExactMarker","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"exactCleanupFailed"},
    {"id":"WDU-LC-015","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"exactCleanupFailed"},"selectionState":{"kind":"aggregate","name":"references"}},"firstApplicableRetryWrite":"none","requiredActions":["retainExactMarker","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},

    {"id":"WDU-LC-020","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["publishSelectedBranch","provePublication","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false},
    {"id":"WDU-LC-021","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"branchPublicationFails"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["attemptPublishSelectedBranch","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"previousOrUnknown","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-022","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterPublicationProof"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["publishSelectedBranch","provePublication","attemptTerminalRecording","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-023","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"none","requiredActions":["proveSelectedBranch","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false},
    {"id":"WDU-LC-024","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterPublicationProof"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"none","requiredActions":["proveSelectedBranch","attemptTerminalRecording","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-025","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceThird"}},"firstApplicableRetryWrite":"none","requiredActions":["retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"third","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-026","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletionBytesChanged"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":[],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false},
    {"id":"WDU-LC-027","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"postTerminalExactCleanupFailsBytesChanged"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["attemptCleanExactMarker","retainTerminalOwnedMarker"],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"exactCleanupFailed"},
    {"id":"WDU-LC-028","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletionBytesUnchanged"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":[],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":"Unchanged","exitClass":"success","doctorGuidance":false},

    {"id":"WDU-LC-030","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","publishSelectedBranch","provePublication","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-031","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"branchPublicationFailsAfterExactCleanup"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","attemptPublishSelectedBranch","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"previousOrUnknown","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-032","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterExactCleanupAndPublicationProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","publishSelectedBranch","provePublication","attemptTerminalRecording","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-033","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","proveSelectedBranch","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-034","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterExactCleanupAndPublicationProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","proveSelectedBranch","attemptTerminalRecording","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-035","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceThird"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"third","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-036","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletionBytesChanged"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker"],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-037","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"postTerminalExactCleanupFailsBytesUnchanged"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["attemptCleanExactMarker","retainTerminalOwnedMarker"],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":"Unchanged","exitClass":"success","doctorGuidance":false,"resultingMarker":"exactCleanupFailed"},
    {"id":"WDU-LC-038","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletionBytesUnchanged"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker"],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":"Unchanged","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},

    {"id":"WDU-LC-100","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"disallowedMarker"},"marker":{"kind":"set","values":["differentOperation","malformed","unsupported","unreadable","exactCleanupFailed"]},"selectionState":{"kind":"aggregate","name":"references"}},"firstApplicableRetryWrite":"none","requiredActions":["retainEvidence","retainPending"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-101","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"cancelImmediatelyBeforeFirstApplicableRetryWrite"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"references"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["retainExactMarker","retainPending"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-102","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"exactCleanupFails"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"references"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["attemptCleanExactMarker","retainExactMarker","retainPending"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"exactCleanupFailed"},
    {"id":"WDU-LC-103","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"cancelAfterExactCleanupBegins"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"references"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["ignoreCancellation","continueByActualEvidence","neverRepublishWithoutProof"],"workingFiles":"unchanged","branchIdentity":"actualEvidence","durableResult":null,"outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-102","WDU-LC-104","WDU-LC-105","WDU-LC-106","WDU-LC-107","WDU-LC-108","WDU-LC-109"]},
    {"id":"WDU-LC-104","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"exactCleanupPublicationAndTerminalSucceed"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["cleanExactMarker","publishSelectedBranch","provePublication","recordTerminal"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-105","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"branchPublicationFailsAfterExactCleanup"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["cleanExactMarker","attemptPublishSelectedBranch","retainPending"],"workingFiles":"unchanged","branchIdentity":"previousOrUnknown","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-106","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterExactCleanupAndPublicationProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["cleanExactMarker","publishSelectedBranch","provePublication","attemptTerminalRecording","retainPending"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-107","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"exactCleanupAndTerminalSucceedAfterSelectedBranchProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["cleanExactMarker","proveSelectedBranch","recordTerminal"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-108","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterExactCleanupAndSelectedBranchProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["cleanExactMarker","proveSelectedBranch","attemptTerminalRecording","retainPending"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-109","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"thirdBranchBlocksAfterExactCleanup"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceThird"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["cleanExactMarker","retainPending"],"workingFiles":"unchanged","branchIdentity":"third","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-110","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"cancelImmediatelyBeforeFirstApplicableRetryWrite"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"branchPublication","requiredActions":["retainPending"],"workingFiles":"unchanged","branchIdentity":"previous","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-111","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"publicationAndTerminalSucceed"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"branchPublication","requiredActions":["publishSelectedBranch","provePublication","recordTerminal"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false},
    {"id":"WDU-LC-112","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"branchPublicationFails"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"branchPublication","requiredActions":["attemptPublishSelectedBranch","retainPending"],"workingFiles":"unchanged","branchIdentity":"previousOrUnknown","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-113","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterPublicationProof"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"branchPublication","requiredActions":["publishSelectedBranch","provePublication","attemptTerminalRecording","retainPending"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-114","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"cancelAfterBranchPublicationBegins"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"branchPublication","requiredActions":["ignoreCancellation","continueByActualEvidence"],"workingFiles":"unchanged","branchIdentity":"actualEvidence","durableResult":null,"outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-111","WDU-LC-112","WDU-LC-113"]},

    {"id":"WDU-LC-120","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"cancelImmediatelyBeforeFirstApplicableRetryWrite"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"terminalRecording","requiredActions":["retainPending"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-121","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"terminalRecordingSucceedsAfterPublicationProof"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"terminalRecording","requiredActions":["proveSelectedBranch","recordTerminal"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false},
    {"id":"WDU-LC-122","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterPublicationProof"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"terminalRecording","requiredActions":["proveSelectedBranch","attemptTerminalRecording","retainPending"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-123","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"cancelAfterTerminalRecordingBegins"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"terminalRecording","requiredActions":["ignoreCancellation","continueByActualEvidence"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":null,"outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-121","WDU-LC-122"]},

    {"id":"WDU-LC-130","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"thirdBranchBlocksFinalization"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceThird"}},"firstApplicableRetryWrite":"none","requiredActions":["retainPending"],"workingFiles":"unchanged","branchIdentity":"third","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true}

  ]
}
```
<!-- grace:wdu-lifecycle-contract:end -->

Rows `WDU-LC-200`–`WDU-LC-213` own pre-local marker admission, exact adoption, terminal-owned residue replacement,
refusal, cleanup, fresh planning, and the final pre-mutation reread. `WDU-LC-006` records pending Reference completion;
`WDU-LC-008` records terminal `DirectoryVersion` completion atomically with verified status. Rows `WDU-LC-026`–
`WDU-LC-028` and `WDU-LC-036`–`WDU-LC-038` report the hash-selected outcome and perform best-effort exact marker
cleanup. Rows `WDU-LC-010`–`WDU-LC-015`, `WDU-LC-020`–`WDU-LC-025`, `WDU-LC-030`–`WDU-LC-035`, and
`WDU-LC-100`–`WDU-LC-130` apply only to Reference pending completion and finalization.

## 5. Requirements and ownership

The ordered requirement identifiers and their exact primary owners are machine-owned by the
`machineMetadata.requirements` array in the normative lifecycle block. It is the sole owner registry; this prose does
not restate the vector. The exact marker-delimited projections are regenerated from that array.

The requirement contract remains: one deep Branch transaction; typed selection and immutable content; one stable
local-root serialization boundary; marker, planning, verification, atomic local-completion, finalization, outcomes,
cancellation, recovery, ordering, documentation, relevant-topology, and verified-root proof. The compiler binds each
requirement to exactly one primary issue in its closed result.

### Serial supersession ownership

Closed Issue #870 and PR #895 are historical counterexamples only. They have no active requirement, lifecycle-row,
dependency, checklist, assignment, or primary-delivery role. Issue #960 replaces the horizontal DirectoryVersion chain:

| Existing result or former leaf | Exact active owner | Primary scope |
| --- | --- | --- |
| Collision-safe topology classification and immutable plan | #898 | The finite pre-mutation matrix; it neither acquires a lease nor changes files. |
| Lifecycle correction, relevant-topology definition, and assignment packet | #928 | DEC-005, exact-adoption reconciliation, zero-action routing, and verified-root failure classification. |
| Per-path transition algebra | #959 | The merged pure classifier remains the one transition table. |
| Hash-selected public tracer | #960 | Compose topology, apply under the WDU lease, verify the exact root, atomically record terminal SQLite completion, clean exact marker residue, and project `grace branch switch`. |
| Selection-neutral local application | #922 | Extract the merged tracer's object publication, final admission, prefix-checked application, and verified-root transition without changing public behavior. |
| Five-input composition and Reference pending completion | #923 | Add the missing opaque inputs, route DirectoryVersion through its merged terminal path, and atomically record Reference pending completion after `VerifiedLocalRoot`. |
| DirectoryVersion terminalization and hash wiring | #900 and #901 | Superseded by Issue #960 because terminalization is part of the verified-status transaction and the public command is the tracer boundary. |

The compiler result is the sole primary-requirement mapping. Issue #923 is the only selected next child after its issue
body is rewritten and the exact live packet passes.

### Selected Product V1 boundaries

The finite collision matrix is mandatory before mutation. Complete relevant topology includes every selected target
entry and every tracked predecessor entry that must be retained, replaced, or removed. A target file may replace only
an absent or verified tracked file, or a tracked directory whose complete tracked descendants are scheduled removals;
a target directory may replace only absence, a tracked directory, or a tracked file scheduled for removal. Unrelated
ignored or untracked content is excluded and preserved, including descendants of retained target directories. It rejects
only when it aliases or occupies a required target path, or lies in a subtree that the plan must remove or replace.
Tracked empty-directory removal is deepest-first, creation is shallowest-first, and case-insensitive duplicate or
ambiguous target shapes reject. Only tracked blockers named by the immutable plan may be removed.

Fresh marker admission classifies every selected requirement as `NeedsApply`; it does not accept unexpected target
bytes in place of accepted tracked identity. Exact same-operation adoption reconciles the current real relevant topology
into `NeedsApply` or `AlreadySatisfied`: a file is satisfied only when its bytes match the prepared BLAKE3 value.
SHA-256 remains retained metadata for selected snapshot comparisons rather than WDU byte equality. An already-removed
tracked entry or already-created target directory may also be satisfied. A mixed partial update skips
only satisfied requirements. Before the first action and every later action, the complete relevant topology must match
the prefix-advanced expected state. The final capture-to-filesystem-call race remains deferred Product V1 hardening.

The merged Issue #960 tracer validates revision, complete status, completion, and marker facts and derives its plan
before publishing prepared bytes into the object cache. Its per-action prefix checks prevent topology drift from winning,
but object publication can outlive the global facts used for admission. Issue #922 therefore makes publication a strict
barrier: publish and BLAKE3-verify every required object-cache copy, then reread the accepted SQLite revision,
complete-status fingerprint, completion state, and exact marker attempt under the same lease. Derive the only applicable
plan from those fresh facts. A mismatch is `Rejected` before mutation and cleans only the marker attempt owned by the
invocation. No pre-publication global snapshot or plan may cross this barrier.

`VerifiedLocalRoot` is opaque and non-durable. Cancellation controls through the transition into it, including a
zero-action plan. After that transition cancellation is non-controlling through SQLite completion. A failure after a
mutation but before `VerifiedLocalRoot` follows `WDU-LC-002`; a failure after `VerifiedLocalRoot` and before successful
SQLite completion follows `WDU-LC-007`, retains exact marker evidence for both zero and nonzero actions, and never
returns `Rejected`. Reference completion follows `WDU-LC-006` and records pending. DirectoryVersion completion follows
`WDU-LC-008` and atomically records verified status, required object metadata, and terminal operation facts.
`bytesChanged` remains ephemeral: true selects `Updated`, false selects `Unchanged`, and neither value is persisted.

For Issue #923, successful Reference pending completion produces private `ReferencePending(Receipt)` inside WDU. The
Issue #871 path consumes it during the original invocation and consumes the matching persisted pending facts after
restart. Only Issue #871 may project `Updated`, `Unchanged`, or `FinalizationIncomplete` after evaluating actual
finalization evidence. No caller finalizer or in-memory retry dependency is added.

The lease inventory is also finite. No-Save admission, hash-prefix resolution, target graph retrieval, object download,
and immutable preparation hold none of the Branch workflow, legacy materialization, or WDU leases. The sealed phase
handoff holds none. Only the WDU transaction holds `working-directory-update.lease` during local reread, mutation,
SQLite completion, and terminal outcome. Marker and sidecar files are evidence, never leases; no second lease is added.

DirectoryVersion cannot create pending finalization. After its terminal SQLite transaction, exact marker cleanup is
best effort. Cleanup failure leaves the marker but does not downgrade `Updated` or `Unchanged`. Exact replay returns
`Unchanged` without another write. A different operation may replace the leftover marker only when the terminal SQLite
operation and target match the marker exactly and current status still names that target root. Malformed, unreadable,
unsupported, unowned, or status-mismatched marker evidence still rejects without mutation.

SQLite completion is decisive durable truth. Terminal SQLite wins over stale marker or sidecar evidence; pending SQLite
requires the exact Reference retry path; markers and sidecars are readable evidence only and cannot create, downgrade,
or replace SQLite completion. Runtime tests invoke the real five-input transaction with real filesystem and SQLite facts.
Helper-only, source-string, sleep-based, and impossible-state fixtures do not establish this contract.

## 6. Proof, propagation, and readiness

The lifecycle compiler checks the structural grammar, graph, exact count vector, and content digest. The packet renderer
replaces only marker-delimited projections in the fifteen declared artifacts. These tools establish exact projection
freshness, while runtime tests remain responsible for outcomes, marker handling, cancellation, and Reference recovery.
Behavioral tests must use production-reachable persisted facts and deterministic seams; source-string assertions,
sleeps, and impossible hand-built states are insufficient.

<!-- grace:wdu-lifecycle-projection-plan:start -->
```json
{"schema":"grace.wdu.lifecycle-projection-plan/v1","compilerInput":"docs/Working Directory Update.md#normative-branch-lifecycle-table","publicationState":"issue-871-reference-finalization"}
```
<!-- grace:wdu-lifecycle-projection-plan:end -->

The following consumers carry generated projections rather than competing lifecycle sequences:

- #869 persists selection and marker predicates.
- #898 proves collision-safe planning without mutation.
- #928 supplies the compiled lifecycle and machine metadata.
- #959 supplies the finite per-path transition algebra.
- #960 owns the merged hash-selected public tracer through terminal DirectoryVersion completion.
- #922 owns the merged selection-neutral extraction through `VerifiedLocalRoot`.
- #1005 owns merged BLAKE3-only byte validation across every WDU application boundary.
- #923 / PR #1009 provide the merged five-input composition and Reference pending-completion slice.
- #900 and #901 are superseded by #960.
- #871 is selected to consume Reference pending completion during the original invocation and after restart.
- #872 proves Save/no-Save admission reaches the same initial rows.
- #842 proves Branch-only retry rows without working-file mutation.
- #843–#845 later consume the transaction contract for Watch and Connect.
- #846 audits public output, Doctor guidance, row references, and absence of retired paths.

ADR 0011 and the declared issue bodies remain contextual consumers. Their marker-delimited projections are rendered
from this revision without interpreting surrounding Markdown. Closed predecessor records remain packet artifacts only
where the machine metadata preserves their supersession context.

The bounded Product V1 residual risk remains interruption after working-tree mutation but before `VerifiedLocalRoot`,
and the final check-to-operation race after synchronous precondition validation. `WDU-LC-002` and `WDU-LC-007` require
truthful `UpdateIncomplete`; Grace does not guess, roll back, or add a broader recovery system.

### Issue #871 delivery contract

Issue #871 fits the Product V1 budget by consuming, but not extending, the merged pending-completion mechanism:

- One outcome: after verified local completion, publish the selected Branch identity and terminalize the matching SQLite
  pending row. A retry reconstructs only from that row and the durable Branch configuration.
- One primary invariant: Reference completion follows verified local completion, cleans only exact marker evidence
  before publication, and is repeatable without rewriting a working file or republishing an already selected Branch.
- No new durable lifecycle: the existing SQLite `Pending` and `Terminal` states and lifecycle rows remain unchanged.
- One source for each decision: SQLite selects the pending completion; disk Branch configuration classifies previous,
  selected, third, or unreadable identity; the lease serializes the completion effects.
- Existing algorithm evidence: Issues #960, #922, and merged Issue #923 / PR #1009 establish local application,
  completion, and pending facts. Issue #871 adds only their bounded publication and terminal-recording sequence.

Issue #871 must retain pending state with `FinalizationIncomplete` and Doctor guidance for disallowed marker evidence,
configuration read failure, third Branch identity, publication failure, or terminal-recording failure. Cancellation is
invocation control only until cleanup, publication, or terminal recording starts. Issue #872 remains responsible for
Save-enabled construction; Watch, Connect, and Doctor implementation remain deferred. Stop if delivery needs a new
persisted state, schema, configuration-write interface, retry file mutation, another lease, a caller finalizer, a second
transaction interface, changed Save behavior, or a DirectoryVersion semantic change.
