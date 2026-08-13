# Working Directory Update

**Status:** Design-ready; the compiler contract becomes ready for #929 only after PR #930 merges
**Quality contract:** Product V1
**Canonical source:** `docs/Working Directory Update.md`
**Evidence current through:** 2026-08-12, epic base `771b6449c0fb5006880c8052ba7d2786dad5b818`

## 1. Outcome and scope

Working Directory Update is the bounded local transaction used by `grace switch` to make a Grace-indexed working
directory and local SQLite state match one exact selected root. It verifies prepared objects, applies only the tracked
plan, proves the complete final root, commits local completion, and completes typed Branch finalization.

This replacement contract is Branch-only. Watch and Connect consume the completed transaction later through #843–#845;
they do not add inputs or lifecycle rules here. Doctor issue #842 consumes Branch pending facts without changing working
files. Runtime, SQLite schema, public CLI, SDK, OpenAPI, generated artifacts, migration, rollback, journals, and automatic
recovery are unchanged by this planning-only slice.

Working-directory mutation is currently distributed across caller implementations, so cleanup, finalization, and
failure behavior can drift apart. One lifecycle contract prevents Grace from publishing a selected Branch after marker
cleanup fails, keeps partial failures truthful and recoverable, and gives implementation workers one user-safety goal
when lower-level details are incomplete.

## 2. Accepted decisions

| ID | Decision | Lasting consequence | Owner |
| --- | --- | --- | --- |
| DEC-001 | Branch selection is typed as `Reference` or exact-root `DirectoryVersion`. | Reference may change Branch identity; DirectoryVersion retains the current Branch and has no Reference ID. | #869 |
| DEC-002 | Successful Save or no-Save admission seals the sole accepted baseline. | `AcceptedBranchPhase` carries SQLite revision, full-status fingerprint, and action token through preparation. | #872 |
| DEC-003 | Marker evidence retains typed dispositions. | Missing, exact, different operation, malformed, unsupported, unreadable, and exact-cleanup-failed never collapse to a Boolean. | #869 |
| DEC-004 | Reference finalization is repeatable from persisted typed facts. | Previous Branch publishes once, selected Branch proves prior publication, and a third Branch retains pending. | #871 |
| DEC-005 | Planning and verification cover complete relevant tracked topology. | Every selected entry and tracked predecessor entry is compared; unrelated ignored/untracked content is preserved and excluded unless it is a destructive collision. | #928, #921, and #922 |
| DEC-006 | The lifecycle table is the sole ordering authority. | Cancellation, cleanup, publication/proof, terminal recording, outcomes, and retry follow `WDU-LC-*` rows only. | #881 |
| DEC-007 | The Branch module has one exact five-input seam. | Inputs are sealed phase, typed selection, exact target graph, immutable prepared content, and diagnostic correlation; internal `VerifiedLocalRoot` never becomes durable or public. | #923 and #901 |
| DEC-008 | Proof is split by stable boundary. | Pure reconciliation, real filesystem application, atomic pending completion, and built-command selector proof remain independently reachable. | #921–#923 and #901 |
| DEC-009 | #868 and PR #873 are superseded planning evidence. | Their review findings inform this table but their commits and competing prose are not implementation authority. | #881 |

No product decision remains open. The closed compiler contract is this PR's deliverable; #929 must render and publish
the exact 15-artifact packet after PR #930 merges. The live exact projection packet and the #921 implementation gate
remain pending #929.

## 3. Domain facts and interface

`AcceptedBranchPhase` is opaque and sealed immediately after successful Save or no-Save admission. It contains the
accepted SQLite revision, canonical complete-status fingerprint, and one public action token. Preparation carries the
same phase unchanged.

`WorkingDirectoryUpdate.run` accepts exactly five Branch facts:

1. Sealed `AcceptedBranchPhase`.
2. Typed `Reference` or exact-root `DirectoryVersion` selection.
3. Exact resolved target graph corresponding to that selection.
4. Immutable prepared content for that graph.
5. Diagnostic correlation.

The module derives canonical configuration and paths, fresh scan input, operation identity, completion and marker facts,
and typed Branch finalization facts. It accepts no caller finalizer, callback, progress observer in place of correlation,
status graph, path or reader bundle, generic context bag, mutation plan, filesystem writer, or database handle. Retry
reconstructs solely from persisted typed operation facts.

The first working-tree mutation and SQLite local completion are distinct boundaries. Local completion is the atomic
SQLite write of verified status, object metadata, and the pending Branch operation. It is never called merely “commit”
in lifecycle evidence.

## 4. Normative Branch lifecycle table

This fenced JSON block is the sole normative lifecycle ordering contract. Human prose, ADRs, implementation issues, and
tests cite `WDU-LC-*` row IDs; they must not restate or reorder the transition sequence. #889 checks its closed
structural grammar, graph, and canonical digest; consumers must not infer aliases, wildcard behavior, or row precedence.

<!-- grace:wdu-lifecycle-contract:start -->
```json
{
  "schema": "grace.wdu.branch-lifecycle/v1",
  "artifactIdentity": "issue-928",
  "canonicalContentDigest": "ae3a77e28886485b49361d8836f040691e9f99228919cef87fac19b42e989d73",
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
      "trigger": ["afterSqliteLocalCompletion", "afterSqliteLocalCompletionBytesChanged", "afterSqliteLocalCompletionBytesUnchanged", "branchPublicationFails", "branchPublicationFailsAfterExactCleanup", "cancelAfterBranchPublicationBegins", "cancelAfterExactCleanupBegins", "cancelAfterFirstWorkingTreeMutation", "cancelAfterOwnedMarkerBeforeFirstWorkingTreeMutation", "cancelAfterTerminalRecordingBegins", "cancelBeforeFirstWorkingTreeMutation", "cancelImmediatelyBeforeFirstApplicableRetryWrite", "disallowedMarker", "exactCleanupAndTerminalSucceedAfterCurrentBranchProof", "exactCleanupAndTerminalSucceedAfterSelectedBranchProof", "exactCleanupFails", "exactCleanupPublicationAndTerminalSucceed", "exactSameOperationAdoption", "exactTerminalCompletionRegardlessOfInvocationCancellation", "failureAfterFirstWorkingTreeMutationBeforeVerifiedLocalRoot", "failureAfterVerifiedLocalRootBeforeSqliteLocalCompletion", "failureBeforeFirstWorkingTreeMutation", "finalPreMutationRereadCleanupFails", "finalPreMutationRereadMatches", "finalPreMutationRereadRejects", "firstWorkingTreeMutationBegins", "missingMarkerFreshAdmission", "ownedMarkerCleanupFailsBeforeFirstWorkingTreeMutation", "preLocalAdmissionRefused", "publicationAndTerminalSucceed", "terminalRecordingFailsAfterExactCleanupAndCurrentBranchProof", "terminalRecordingFailsAfterExactCleanupAndPublicationProof", "terminalRecordingFailsAfterExactCleanupAndSelectedBranchProof", "terminalRecordingFailsAfterPublicationProof", "terminalRecordingSucceedsAfterPublicationProof", "thirdBranchBlocksAfterExactCleanup", "thirdBranchBlocksFinalization", "verifiedRootReadyForSqliteLocalCompletion"],
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
        "persisted": ["referencePrevious", "referenceSelected", "referenceThird", "directoryVersion"]
      }
    },
    "expansion": {
      "rule": "Resolve each match axis by its kind, then take the Cartesian product across all four axes.",
      "setMembers": "Set values are concrete members of that axis only; unknown values, empty sets, duplicates, and mixed shapes are invalid.",
      "aggregateMembers": "Aggregate names are valid only on the axis where declared; unknown names and aggregate tokens inside sets are invalid.",
      "example": "WDU-LC-100 expands five marker values times four selection states into 20 applicable cells; WDU-LC-026/028 and WDU-LC-036/038 are the explicit DirectoryVersion bytesChanged split."
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
    "rowVector": ["WDU-LC-200","WDU-LC-201","WDU-LC-202","WDU-LC-203","WDU-LC-204","WDU-LC-205","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-211","WDU-LC-212","WDU-LC-001","WDU-LC-005","WDU-LC-002","WDU-LC-004","WDU-LC-006","WDU-LC-007","WDU-LC-003","WDU-LC-010","WDU-LC-011","WDU-LC-012","WDU-LC-013","WDU-LC-014","WDU-LC-015","WDU-LC-020","WDU-LC-021","WDU-LC-022","WDU-LC-023","WDU-LC-024","WDU-LC-025","WDU-LC-026","WDU-LC-027","WDU-LC-028","WDU-LC-030","WDU-LC-031","WDU-LC-032","WDU-LC-033","WDU-LC-034","WDU-LC-035","WDU-LC-036","WDU-LC-037","WDU-LC-038","WDU-LC-100","WDU-LC-101","WDU-LC-102","WDU-LC-103","WDU-LC-104","WDU-LC-105","WDU-LC-106","WDU-LC-107","WDU-LC-108","WDU-LC-109","WDU-LC-115","WDU-LC-116","WDU-LC-110","WDU-LC-111","WDU-LC-112","WDU-LC-113","WDU-LC-114","WDU-LC-120","WDU-LC-121","WDU-LC-122","WDU-LC-123","WDU-LC-130","WDU-LC-140","WDU-LC-141","WDU-LC-142","WDU-LC-143"]
  },
  "machineMetadata": {
    "decisionIds": ["DEC-001","DEC-002","DEC-003","DEC-004","DEC-005","DEC-006","DEC-007","DEC-008","DEC-009"],
    "requirements": [
      {"id":"REQ-001","owner":"#923"},{"id":"REQ-002","owner":"#869"},{"id":"REQ-003","owner":"#837"},{"id":"REQ-004","owner":"#839"},{"id":"REQ-005","owner":"#869"},{"id":"REQ-006","owner":"#898"},{"id":"REQ-007","owner":"#922"},{"id":"REQ-008","owner":"#922"},{"id":"REQ-009","owner":"#838"},{"id":"REQ-010","owner":"#838"},{"id":"REQ-011","owner":"#871"},{"id":"REQ-012","owner":"#871"},{"id":"REQ-013","owner":"#900"},{"id":"REQ-014","owner":"#921"},{"id":"REQ-015","owner":"#842"},{"id":"REQ-016","owner":"#871"},{"id":"REQ-017","owner":"#846"},{"id":"REQ-018","owner":"#928"},{"id":"REQ-019","owner":"#923"}
    ],
    "artifacts": [
      {"id":"adr-0011","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-202","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-212","WDU-LC-006","WDU-LC-007","WDU-LC-010","WDU-LC-015","WDU-LC-020","WDU-LC-023","WDU-LC-025","WDU-LC-026","WDU-LC-028","WDU-LC-030","WDU-LC-033","WDU-LC-035","WDU-LC-036","WDU-LC-038","WDU-LC-100","WDU-LC-101","WDU-LC-103","WDU-LC-110","WDU-LC-114","WDU-LC-120","WDU-LC-123","WDU-LC-130","WDU-LC-140","WDU-LC-143","WDU-LC-003"]},
      {"id":"epic-835","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-202","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-212","WDU-LC-006","WDU-LC-007","WDU-LC-010","WDU-LC-015","WDU-LC-020","WDU-LC-030","WDU-LC-100","WDU-LC-101","WDU-LC-103","WDU-LC-110","WDU-LC-114","WDU-LC-120","WDU-LC-123","WDU-LC-130","WDU-LC-140","WDU-LC-143","WDU-LC-003"]},
      {"id":"issue-842","rowIds":["WDU-LC-100","WDU-LC-101","WDU-LC-102","WDU-LC-103","WDU-LC-104","WDU-LC-105","WDU-LC-106","WDU-LC-107","WDU-LC-108","WDU-LC-109","WDU-LC-115","WDU-LC-116","WDU-LC-110","WDU-LC-111","WDU-LC-112","WDU-LC-113","WDU-LC-114","WDU-LC-120","WDU-LC-121","WDU-LC-122","WDU-LC-123","WDU-LC-130","WDU-LC-140","WDU-LC-141","WDU-LC-142","WDU-LC-143","WDU-LC-003"]},
      {"id":"issue-843","rowIds":["WDU-LC-003","WDU-LC-100","WDU-LC-101","WDU-LC-103"]},
      {"id":"issue-846","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-202","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-212","WDU-LC-006","WDU-LC-007","WDU-LC-010","WDU-LC-015","WDU-LC-020","WDU-LC-026","WDU-LC-028","WDU-LC-030","WDU-LC-036","WDU-LC-038","WDU-LC-100","WDU-LC-101","WDU-LC-103","WDU-LC-110","WDU-LC-114","WDU-LC-120","WDU-LC-123","WDU-LC-130","WDU-LC-140","WDU-LC-143","WDU-LC-003"]},
      {"id":"issue-869","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-202","WDU-LC-203","WDU-LC-204","WDU-LC-205","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-211","WDU-LC-212","WDU-LC-010","WDU-LC-011","WDU-LC-012","WDU-LC-013","WDU-LC-014","WDU-LC-015","WDU-LC-100"]},
      {"id":"issue-898","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-209","WDU-LC-210","WDU-LC-211","WDU-LC-212"]},
      {"id":"issue-928","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-209","WDU-LC-210","WDU-LC-002","WDU-LC-006","WDU-LC-007"]},
      {"id":"issue-921","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-209","WDU-LC-210"]},
      {"id":"issue-922","rowIds":["WDU-LC-209","WDU-LC-210","WDU-LC-002","WDU-LC-004","WDU-LC-006","WDU-LC-007"]},
      {"id":"issue-923","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-202","WDU-LC-203","WDU-LC-204","WDU-LC-205","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-211","WDU-LC-212","WDU-LC-001","WDU-LC-005","WDU-LC-002","WDU-LC-004","WDU-LC-006","WDU-LC-007"]},
      {"id":"issue-900","rowIds":["WDU-LC-003","WDU-LC-010","WDU-LC-011","WDU-LC-012","WDU-LC-013","WDU-LC-014","WDU-LC-015","WDU-LC-026","WDU-LC-027","WDU-LC-028","WDU-LC-036","WDU-LC-037","WDU-LC-038","WDU-LC-100","WDU-LC-101","WDU-LC-102","WDU-LC-103","WDU-LC-115","WDU-LC-116","WDU-LC-140","WDU-LC-141","WDU-LC-142","WDU-LC-143"]},
      {"id":"issue-901","rowIds":["WDU-LC-001","WDU-LC-002","WDU-LC-004","WDU-LC-006","WDU-LC-007","WDU-LC-003","WDU-LC-140","WDU-LC-141","WDU-LC-142","WDU-LC-143"]},
      {"id":"issue-871","rowIds":["WDU-LC-020","WDU-LC-021","WDU-LC-022","WDU-LC-023","WDU-LC-024","WDU-LC-025","WDU-LC-030","WDU-LC-031","WDU-LC-032","WDU-LC-033","WDU-LC-034","WDU-LC-035","WDU-LC-100","WDU-LC-101","WDU-LC-102","WDU-LC-103","WDU-LC-104","WDU-LC-105","WDU-LC-106","WDU-LC-107","WDU-LC-108","WDU-LC-109","WDU-LC-110","WDU-LC-111","WDU-LC-112","WDU-LC-113","WDU-LC-114","WDU-LC-120","WDU-LC-121","WDU-LC-122","WDU-LC-123","WDU-LC-130","WDU-LC-003"]},
      {"id":"issue-872","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-211","WDU-LC-212","WDU-LC-001","WDU-LC-005","WDU-LC-002","WDU-LC-004","WDU-LC-006","WDU-LC-007","WDU-LC-003"]}
    ],
    "expectedCounts": {"decisionCount":9,"requirementCount":19,"artifactCount":15,"rowCount":70,"applicabilityKeyCount":260}
  },
  "rows": [
    {"id":"WDU-LC-200","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"missingMarkerFreshAdmission"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["createExactOwnedMarkerWithFreshAttemptToken","rereadCompleteLocalStatusAndMarker","buildFreshPlanFromCurrentTrackedGraph","reconcileFreshAdmissionAsNeedsApplyOnly","discardEveryPriorPlan"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":null,"exitClass":null,"doctorGuidance":null,"resultingMarker":"exact","nextRows":["WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-211","WDU-LC-212"]},
    {"id":"WDU-LC-201","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"exactSameOperationAdoption"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["verifyMarkerSchema","verifyRepositoryAndLocalRootScope","verifyExactOperationIdentity","verifyExactTargetIdentity","replaceAttemptTokenWithFreshAttemptToken","rereadCompleteLocalStatusAndMarker","buildFreshPlanFromCurrentTrackedGraph","reconcileExactAdoptionAsNeedsApplyOrAlreadySatisfied","discardEveryPriorPlan"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":null,"exitClass":null,"doctorGuidance":null,"resultingMarker":"exact","nextRows":["WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-211","WDU-LC-212"]},
    {"id":"WDU-LC-202","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"differentOperation"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-203","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"malformed"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-204","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"unsupported"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-205","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"unreadable"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-206","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"exactCleanupFailed"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-207","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"cancelAfterOwnedMarkerBeforeFirstWorkingTreeMutation"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanOnlyExactOwnedMarker"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-208","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"ownedMarkerCleanupFailsBeforeFirstWorkingTreeMutation"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["attemptCleanOnlyExactOwnedMarker","retainExactMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"exactCleanupFailed"},
    {"id":"WDU-LC-209","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"finalPreMutationRereadMatches"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["rereadAcceptedRevisionAndCompleteStatusFingerprint","rereadMarkerSchemaScopeOperationTargetAndAttemptToken","verifyFreshPlanAgainstReread","compareCompleteRelevantTopologyWithPrefixAdvancedExpectedState","checkCancellationImmediatelyBeforeVerifiedLocalRootOrFirstMutation","routeZeroActionToVerifiedLocalRootOrMutatingPlanToFirstAction"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-207","WDU-LC-208","WDU-LC-210","WDU-LC-006","WDU-LC-007"]},
    {"id":"WDU-LC-210","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"firstWorkingTreeMutationBegins"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["beginTrackedWorkingTreeMutation","ignoreCancellation","compareCompleteRelevantTopologyWithPrefixAdvancedExpectedStateBeforeEveryLaterAction","applyFreshPlan","verifyCompleteRelevantTrackedTopology","transitionToVerifiedLocalRoot"],"workingFiles":"actualEvidence","branchIdentity":"unchanged","durableResult":null,"outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-002","WDU-LC-006","WDU-LC-007"]},
    {"id":"WDU-LC-211","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"finalPreMutationRereadRejects"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["rejectStaleRevisionFingerprintMarkerOperationTargetOrPlan","cleanOnlyExactOwnedMarker"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-212","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"finalPreMutationRereadCleanupFails"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["rejectStaleRevisionFingerprintMarkerOperationTargetOrPlan","attemptCleanOnlyExactOwnedMarker","retainExactMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"exactCleanupFailed"},

    {"id":"WDU-LC-001","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"cancelBeforeFirstWorkingTreeMutation"},"marker":{"kind":"aggregate","name":"none"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":[],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":false},
    {"id":"WDU-LC-005","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"failureBeforeFirstWorkingTreeMutation"},"marker":{"kind":"aggregate","name":"none"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":[],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":false},
    {"id":"WDU-LC-002","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"failureAfterFirstWorkingTreeMutationBeforeVerifiedLocalRoot"},"marker":{"kind":"aggregate","name":"ownedOrNone"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMutationEvidence"],"workingFiles":"mayDiffer","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"UpdateIncomplete","exitClass":"nonzero","doctorGuidance":false},
    {"id":"WDU-LC-004","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"cancelAfterFirstWorkingTreeMutation"},"marker":{"kind":"aggregate","name":"actualEvidence"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["ignoreCancellation","continueByActualEvidence"],"workingFiles":"actualEvidence","branchIdentity":"actualEvidence","durableResult":null,"outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-002","WDU-LC-006","WDU-LC-007"]},
    {"id":"WDU-LC-006","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"verifiedRootReadyForSqliteLocalCompletion"},"marker":{"kind":"aggregate","name":"postCompletionEvidence"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["ignoreCancellation","recordSqliteLocalCompletion","returnLocalCompletionWithEphemeralBytesChanged","inspectPostCompletionMarker"],"workingFiles":"verifiedRelevantTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-010","WDU-LC-011","WDU-LC-012","WDU-LC-013","WDU-LC-014","WDU-LC-015","WDU-LC-020","WDU-LC-021","WDU-LC-022","WDU-LC-023","WDU-LC-024","WDU-LC-025","WDU-LC-026","WDU-LC-027","WDU-LC-028","WDU-LC-030","WDU-LC-031","WDU-LC-032","WDU-LC-033","WDU-LC-034","WDU-LC-035","WDU-LC-036","WDU-LC-037","WDU-LC-038"]},
    {"id":"WDU-LC-007","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"failureAfterVerifiedLocalRootBeforeSqliteLocalCompletion"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["ignoreCancellation","retainExactMarkerEvidence"],"workingFiles":"verifiedRelevantTarget","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"UpdateIncomplete","exitClass":"nonzero","doctorGuidance":false},
    {"id":"WDU-LC-003","match":{"invocation":{"kind":"one","value":"terminalReplay"},"trigger":{"kind":"one","value":"exactTerminalCompletionRegardlessOfInvocationCancellation"},"marker":{"kind":"aggregate","name":"any"},"selectionState":{"kind":"aggregate","name":"persisted"}},"firstApplicableRetryWrite":"none","requiredActions":[],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"existingTerminal","outcome":"Unchanged","exitClass":"success","doctorGuidance":false},

    {"id":"WDU-LC-010","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"differentOperation"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarker","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-011","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"malformed"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarker","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-012","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"unsupported"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarker","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-013","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"unreadable"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainEvidence","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-014","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"exactCleanupFails"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["attemptCleanExactMarker","retainExactMarker","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"exactCleanupFailed"},
    {"id":"WDU-LC-015","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"exactCleanupFailed"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainExactMarker","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},

    {"id":"WDU-LC-020","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["publishSelectedBranch","provePublication","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false},
    {"id":"WDU-LC-021","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"branchPublicationFails"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["attemptPublishSelectedBranch","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"previousOrUnknown","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-022","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterPublicationProof"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["publishSelectedBranch","provePublication","attemptTerminalRecording","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-023","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"none","requiredActions":["proveSelectedBranch","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false},
    {"id":"WDU-LC-024","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterPublicationProof"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"none","requiredActions":["proveSelectedBranch","attemptTerminalRecording","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-025","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceThird"}},"firstApplicableRetryWrite":"none","requiredActions":["retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"third","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-026","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletionBytesChanged"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["proveCurrentBranchUnchanged","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false},
    {"id":"WDU-LC-027","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterPublicationProof"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["proveCurrentBranchUnchanged","attemptTerminalRecording","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-028","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletionBytesUnchanged"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["proveCurrentBranchUnchanged","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":"Unchanged","exitClass":"success","doctorGuidance":false},

    {"id":"WDU-LC-030","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","publishSelectedBranch","provePublication","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-031","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"branchPublicationFailsAfterExactCleanup"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","attemptPublishSelectedBranch","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"previousOrUnknown","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-032","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterExactCleanupAndPublicationProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","publishSelectedBranch","provePublication","attemptTerminalRecording","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-033","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","proveSelectedBranch","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-034","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterExactCleanupAndPublicationProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","proveSelectedBranch","attemptTerminalRecording","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-035","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceThird"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"third","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-036","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletionBytesChanged"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","proveCurrentBranchUnchanged","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-037","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterExactCleanupAndPublicationProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","proveCurrentBranchUnchanged","attemptTerminalRecording","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-038","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletionBytesUnchanged"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","proveCurrentBranchUnchanged","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":"Unchanged","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},

    {"id":"WDU-LC-100","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"disallowedMarker"},"marker":{"kind":"set","values":["differentOperation","malformed","unsupported","unreadable","exactCleanupFailed"]},"selectionState":{"kind":"set","values":["referencePrevious","referenceSelected","referenceThird","directoryVersion"]}},"firstApplicableRetryWrite":"none","requiredActions":["retainEvidence","retainPending"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-101","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"cancelImmediatelyBeforeFirstApplicableRetryWrite"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"set","values":["referencePrevious","referenceSelected","referenceThird","directoryVersion"]}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["retainExactMarker","retainPending"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-102","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"exactCleanupFails"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"set","values":["referencePrevious","referenceSelected","referenceThird","directoryVersion"]}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["attemptCleanExactMarker","retainExactMarker","retainPending"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"exactCleanupFailed"},
    {"id":"WDU-LC-103","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"cancelAfterExactCleanupBegins"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"set","values":["referencePrevious","referenceSelected","referenceThird","directoryVersion"]}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["ignoreCancellation","continueByActualEvidence","neverRepublishWithoutProof"],"workingFiles":"unchanged","branchIdentity":"actualEvidence","durableResult":null,"outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-102","WDU-LC-104","WDU-LC-105","WDU-LC-106","WDU-LC-107","WDU-LC-108","WDU-LC-109","WDU-LC-115","WDU-LC-116"]},
    {"id":"WDU-LC-104","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"exactCleanupPublicationAndTerminalSucceed"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["cleanExactMarker","publishSelectedBranch","provePublication","recordTerminal"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-105","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"branchPublicationFailsAfterExactCleanup"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["cleanExactMarker","attemptPublishSelectedBranch","retainPending"],"workingFiles":"unchanged","branchIdentity":"previousOrUnknown","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-106","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterExactCleanupAndPublicationProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["cleanExactMarker","publishSelectedBranch","provePublication","attemptTerminalRecording","retainPending"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-107","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"exactCleanupAndTerminalSucceedAfterSelectedBranchProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["cleanExactMarker","proveSelectedBranch","recordTerminal"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-108","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterExactCleanupAndSelectedBranchProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["cleanExactMarker","proveSelectedBranch","attemptTerminalRecording","retainPending"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-109","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"thirdBranchBlocksAfterExactCleanup"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceThird"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["cleanExactMarker","retainPending"],"workingFiles":"unchanged","branchIdentity":"third","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-115","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"exactCleanupAndTerminalSucceedAfterCurrentBranchProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["cleanExactMarker","proveCurrentBranchUnchanged","recordTerminal"],"workingFiles":"unchanged","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-116","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterExactCleanupAndCurrentBranchProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"exactCleanup","requiredActions":["cleanExactMarker","proveCurrentBranchUnchanged","attemptTerminalRecording","retainPending"],"workingFiles":"unchanged","branchIdentity":"currentUnchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},

    {"id":"WDU-LC-110","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"cancelImmediatelyBeforeFirstApplicableRetryWrite"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"branchPublication","requiredActions":["retainPending"],"workingFiles":"unchanged","branchIdentity":"previous","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-111","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"publicationAndTerminalSucceed"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"branchPublication","requiredActions":["publishSelectedBranch","provePublication","recordTerminal"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false},
    {"id":"WDU-LC-112","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"branchPublicationFails"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"branchPublication","requiredActions":["attemptPublishSelectedBranch","retainPending"],"workingFiles":"unchanged","branchIdentity":"previousOrUnknown","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-113","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterPublicationProof"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"branchPublication","requiredActions":["publishSelectedBranch","provePublication","attemptTerminalRecording","retainPending"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-114","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"cancelAfterBranchPublicationBegins"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"branchPublication","requiredActions":["ignoreCancellation","continueByActualEvidence"],"workingFiles":"unchanged","branchIdentity":"actualEvidence","durableResult":null,"outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-111","WDU-LC-112","WDU-LC-113"]},

    {"id":"WDU-LC-120","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"cancelImmediatelyBeforeFirstApplicableRetryWrite"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"terminalRecording","requiredActions":["retainPending"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-121","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"terminalRecordingSucceedsAfterPublicationProof"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"terminalRecording","requiredActions":["proveSelectedBranch","recordTerminal"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false},
    {"id":"WDU-LC-122","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterPublicationProof"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"terminalRecording","requiredActions":["proveSelectedBranch","attemptTerminalRecording","retainPending"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-123","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"cancelAfterTerminalRecordingBegins"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"terminalRecording","requiredActions":["ignoreCancellation","continueByActualEvidence"],"workingFiles":"unchanged","branchIdentity":"selected","durableResult":null,"outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-121","WDU-LC-122"]},

    {"id":"WDU-LC-130","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"thirdBranchBlocksFinalization"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"referenceThird"}},"firstApplicableRetryWrite":"none","requiredActions":["retainPending"],"workingFiles":"unchanged","branchIdentity":"third","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},

    {"id":"WDU-LC-140","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"cancelImmediatelyBeforeFirstApplicableRetryWrite"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"terminalRecording","requiredActions":["retainPending"],"workingFiles":"unchanged","branchIdentity":"currentUnchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-141","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"terminalRecordingSucceedsAfterPublicationProof"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"terminalRecording","requiredActions":["proveCurrentBranchUnchanged","recordTerminal"],"workingFiles":"unchanged","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false},
    {"id":"WDU-LC-142","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterPublicationProof"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"terminalRecording","requiredActions":["proveCurrentBranchUnchanged","attemptTerminalRecording","retainPending"],"workingFiles":"unchanged","branchIdentity":"currentUnchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-143","match":{"invocation":{"kind":"one","value":"finalizationRetry"},"trigger":{"kind":"one","value":"cancelAfterTerminalRecordingBegins"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"terminalRecording","requiredActions":["ignoreCancellation","continueByActualEvidence"],"workingFiles":"unchanged","branchIdentity":"currentUnchanged","durableResult":null,"outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-141","WDU-LC-142"]}
  ]
}
```
<!-- grace:wdu-lifecycle-contract:end -->

Rows `WDU-LC-200`–`WDU-LC-212` own pre-local marker admission, exact adoption, refusal, cleanup, fresh planning, and the
final pre-mutation reread. Rows `WDU-LC-010`–`WDU-LC-015` prohibit Branch publication for every disallowed marker
disposition. Rows
`WDU-LC-030`–`WDU-LC-038` require exact cleanup before publication or publication proof. Rows `WDU-LC-100`–
`WDU-LC-143` define retry; the selected `firstApplicableRetryWrite` is conditional, and cancellation is controlling only
immediately before that write begins. These statements identify row families and do not add another ordering contract.

## 5. Requirements and ownership

The ordered requirement identifiers and their exact primary owners are machine-owned by the canonical
`machineMetadata.requirements` array in the normative lifecycle block. It is the sole owner registry: this prose does
not restate the vector. #929 renders that compiled data into its marker-delimited consumer projections after #930
merges.

The requirement contract remains: one deep Branch transaction; typed selection and immutable content; one stable
local-root serialization boundary; marker, planning, verification, atomic local-completion, finalization, outcomes,
cancellation, recovery, ordering, documentation, relevant-topology, and verified-root proof. The compiler binds each
requirement to exactly one primary issue in its closed result.

### Serial supersession ownership

Closed #870 and PR #895 are historical counterexamples only. They have no active requirement, lifecycle-row,
dependency, checklist, assignment, or primary-delivery role. The active Branch packet is serial:

| Former #870 delivery | Exact active owner | Primary scope |
| --- | --- | --- |
| Collision-safe topology classification and immutable plan | #898 | The finite pre-mutation matrix; it neither acquires a lease nor changes files. |
| Lifecycle correction, relevant-topology definition, and assignment packet | #928 | DEC-005, exact-adoption reconciliation, zero-action routing, and verified-root failure classification. |
| `WDU-LC-200`, `WDU-LC-201`, and relevant-topology/prefix reconciliation proof | #921 | Pure immutable `NeedsApply`/`AlreadySatisfied` requirements, including mixed partial exact adoption. |
| `WDU-LC-209`, `WDU-LC-210`, `WDU-LC-002`, and relevant-root proof | #922 | Held-lease application, cancellation until `VerifiedLocalRoot`, and truth after mutation. |
| `WDU-LC-006` and `WDU-LC-007` through the five-input seam | #923 | Opaque `VerifiedLocalRoot`, atomic pending completion, and ephemeral `LocalCompletion(..., bytesChanged)`. |
| `WDU-LC-003`, `WDU-LC-010`–`WDU-LC-015`, `WDU-LC-026`–`WDU-LC-028`, `WDU-LC-036`–`WDU-LC-038`, `WDU-LC-100`–`WDU-LC-103`, `WDU-LC-115`, `WDU-LC-116`, and `WDU-LC-140`–`WDU-LC-143` | #900 | DirectoryVersion terminalization and exact filesystem-free retry. |
| Hash-selected Branch orchestration and its public projection | #901 | Remote resolution and preparation outside local serialization, then the proven five-input call. |

The compiler result is the sole primary-requirement mapping. Issue #901 is the necessary production Branch consumer of
those completed facts; its direct public wiring is not a second primary delivery claim for a transaction requirement.

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
into `NeedsApply` or `AlreadySatisfied`: a file is satisfied only by exact SHA-256 and BLAKE3 identity, while an
already-removed tracked entry or already-created target directory may also be satisfied. A mixed partial update skips
only satisfied requirements. Before the first action and every later action, the complete relevant topology must match
the prefix-advanced expected state. The final capture-to-filesystem-call race remains deferred Product V1 hardening.

`VerifiedLocalRoot` is opaque and non-durable. Cancellation controls through the transition into it, including a
zero-action plan. After that transition cancellation is non-controlling through the pending SQLite write. A failure
after a mutation but before `VerifiedLocalRoot` follows `WDU-LC-002`; a failure after `VerifiedLocalRoot` and before
successful pending completion follows `WDU-LC-007`, retains exact marker evidence for both zero and nonzero actions,
and never returns `Rejected`. Successful completion follows `WDU-LC-006` and returns
`LocalCompletion(target, operation, bytesChanged)`; `bytesChanged` is ephemeral and adds no persisted shape. For
DirectoryVersion finalization, `afterSqliteLocalCompletionBytesChanged` selects `Updated` and
`afterSqliteLocalCompletionBytesUnchanged` selects `Unchanged`; Reference finalization remains on
`afterSqliteLocalCompletion` and does not infer this discriminator.

The lease inventory is also finite. No-Save admission, hash-prefix resolution, target graph retrieval, object download,
and immutable preparation hold none of the Branch workflow, legacy materialization, or WDU leases. The sealed phase
handoff holds none. Only the WDU transaction holds `working-directory-update.lease` during local reread, mutation,
SQLite completion, and terminal outcome. Marker and sidecar files are evidence, never leases; no second lease is added.

For DirectoryVersion retry, fresh pending SQLite and marker evidence select one first applicable write before any
cancellation observation: an exact marker selects exact-marker cleanup; a missing marker selects terminal SQLite
recording; foreign, malformed, unsupported, or unreadable marker evidence permits no write and retains pending; an
exact terminal SQLite result selects no write and returns `Unchanged`. Cancellation is controlling only immediately
before the selected cleanup or terminal-recording write. Once that write begins, cleanup and SQLite evidence determine
the result and cancellation is non-controlling.

SQLite completion is decisive durable truth. Terminal SQLite wins over stale marker or sidecar evidence; pending SQLite
requires the exact retry path; markers and sidecars are readable evidence only and cannot create, downgrade, or replace
SQLite completion. Production proof invokes the real five-input transaction and persisted retry entry with real
filesystem and SQLite facts. Helper-only, source-string, sleep-based, and impossible-state fixtures do not establish
this contract.

## 6. Proof, propagation, and readiness

Issue #889 checks the closed structural grammar, graph, and canonical digest. Historical #890 proof checked its then-current
projection boundary; it does not establish the replacement 15-artifact packet. #929 owns rendering and exact live-packet
comparison from this compiler result without inventing wildcard or precedence rules. Neither tool establishes lifecycle meaning;
normal review and later runtime proof remain responsible for the behavior of outcomes, marker handling, cancellation,
and Doctor guidance. Behavioral proof must use production-reachable persisted facts and deterministic seams;
source-string assertions, sleeps, and impossible hand-built states are insufficient.

<!-- grace:wdu-lifecycle-projection-plan:start -->
```json
{"schema":"grace.wdu.lifecycle-projection-plan/v1","compilerInput":"docs/Working Directory Update.md#normative-branch-lifecycle-table","publicationState":"pending-issue-929"}
```
<!-- grace:wdu-lifecycle-projection-plan:end -->

The following consumers carry generated projections rather than competing lifecycle sequences:

- #869 persists selection and marker predicates.
- #898 proves collision-safe planning without mutation.
- #928 supplies the closed compiler result; after PR #930 merges, #929 renders the publication packet.
- #921 proves pure relevant-topology reconciliation and mixed partial adoption.
- #922 proves held-lease application through opaque `VerifiedLocalRoot`.
- #923 proves initial-run through pending SQLite completion.
- #900 proves DirectoryVersion terminalization and retry cancellation precedence.
- #901 proves production hash-selector consumption of the completed transaction.
- #871 proves Reference and retry row families.
- #872 proves Save/no-Save admission reaches the same initial rows.
- #842 proves Branch-only retry rows without working-file mutation.
- #843–#845 later consume the transaction contract for Watch and Connect.
- #846 audits public output, Doctor guidance, row references, and absence of retired paths.

ADR 0011 and active issue bodies remain contextual consumers. Their replacement marker-delimited projections are pending
issue #929; surrounding Markdown or prose is not compiler input. The compiler result is ready for #929 only after PR #930
merges. The live exact projection packet and the #921 implementation gate remain pending that work; normal review and
later runtime proof establish lifecycle meaning.

The eventual exact projection packet contains only the fifteen artifacts declared by the compiler. It is not published
by this PR. Closed predecessor records are not packet artifacts.

The bounded Product V1 residual risk remains interruption after working-tree mutation but before `VerifiedLocalRoot`,
and the final check-to-operation race after synchronous precondition validation. `WDU-LC-002` and `WDU-LC-007` require
truthful `UpdateIncomplete`; Grace does not guess, roll back, or add a broader recovery system.
