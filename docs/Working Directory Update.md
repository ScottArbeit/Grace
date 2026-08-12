# Working Directory Update

**Status:** Design-ready; implementation projections pending
**Quality contract:** Product V1
**Canonical source:** `docs/Working Directory Update.md`
**Evidence current through:** 2026-08-12, epic base `ff6305130c6bf18c6db0aa1096a251e64a4041d5`

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
| DEC-005 | Planning and verification cover complete tracked topology. | Empty directories, path-type transitions, ignored content, object verification, and complete-root proof are included. | #870 |
| DEC-006 | The lifecycle table is the sole ordering authority. | Cancellation, cleanup, publication/proof, terminal recording, outcomes, and retry follow `WDU-LC-*` rows only. | #881 |
| DEC-007 | The Branch module has one exact five-input seam. | Inputs are sealed phase, typed selection, exact target graph, immutable prepared content, and diagnostic correlation. | #870 |
| DEC-008 | Proof is split by stable boundary. | Real filesystem/SQLite tests activate lifecycle failures; built commands prove selectors and public projections. | #870–#872 |
| DEC-009 | #868 and PR #873 are superseded planning evidence. | Their review findings inform this table but their commits and competing prose are not implementation authority. | #881 |

No product decision remains open. Projection of this table into consumer issue bodies is required before the
specification can return to Plan-ready.

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
tests cite `WDU-LC-*` row IDs; they must not restate or reorder the transition sequence. Its `machineGrammar` closes the
predicate language; consumers such as #882 parse that grammar and must not infer aliases, wildcard behavior, or row
precedence.

```json
{
  "schema": "grace.wdu.branch-lifecycle/v1",
  "boundaries": {
    "firstWorkingTreeMutation": "first tracked working-path mutation",
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
      "trigger": ["afterSqliteLocalCompletion", "branchPublicationFails", "branchPublicationFailsAfterExactCleanup", "cancelAfterBranchPublicationBegins", "cancelAfterExactCleanupBegins", "cancelAfterFirstWorkingTreeMutation", "cancelAfterOwnedMarkerBeforeFirstWorkingTreeMutation", "cancelAfterTerminalRecordingBegins", "cancelBeforeFirstWorkingTreeMutation", "cancelImmediatelyBeforeFirstApplicableRetryWrite", "disallowedMarker", "exactCleanupAndTerminalSucceedAfterCurrentBranchProof", "exactCleanupAndTerminalSucceedAfterSelectedBranchProof", "exactCleanupFails", "exactCleanupPublicationAndTerminalSucceed", "exactSameOperationAdoption", "exactTerminalCompletionRegardlessOfInvocationCancellation", "failureAfterFirstWorkingTreeMutationBeforeSqliteLocalCompletion", "failureBeforeFirstWorkingTreeMutation", "finalPreMutationRereadCleanupFails", "finalPreMutationRereadMatches", "finalPreMutationRereadRejects", "firstWorkingTreeMutationBegins", "missingMarkerFreshAdmission", "ownedMarkerCleanupFailsBeforeFirstWorkingTreeMutation", "preLocalAdmissionRefused", "publicationAndTerminalSucceed", "terminalRecordingFailsAfterExactCleanupAndCurrentBranchProof", "terminalRecordingFailsAfterExactCleanupAndPublicationProof", "terminalRecordingFailsAfterExactCleanupAndSelectedBranchProof", "terminalRecordingFailsAfterPublicationProof", "terminalRecordingSucceedsAfterPublicationProof", "thirdBranchBlocksAfterExactCleanup", "thirdBranchBlocksFinalization", "verifiedRootReadyForSqliteLocalCompletion"],
      "marker": ["notApplicable", "missing", "exact", "differentOperation", "malformed", "unsupported", "unreadable", "exactCleanupFailed"],
      "selectionState": ["referencePrevious", "referenceSelected", "referenceThird", "directoryVersion"],
      "firstApplicableRetryWrite": ["none", "exactCleanup", "branchPublication", "terminalRecording"],
      "exitClass": ["success", "nonzero"]
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
      "example": "WDU-LC-100 expands five marker values times four selection states into 20 applicable cells."
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
    }
  },
  "rows": [
    {"id":"WDU-LC-200","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"missingMarkerFreshAdmission"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["createExactOwnedMarkerWithFreshAttemptToken","rereadCompleteLocalStatusAndMarker","buildFreshPlanFromCurrentTrackedGraph","discardEveryPriorPlan"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":null,"exitClass":null,"doctorGuidance":null,"resultingMarker":"exact","nextRows":["WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-211","WDU-LC-212"]},
    {"id":"WDU-LC-201","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"exactSameOperationAdoption"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["verifyMarkerSchema","verifyRepositoryAndLocalRootScope","verifyExactOperationIdentity","verifyExactTargetIdentity","replaceAttemptTokenWithFreshAttemptToken","rereadCompleteLocalStatusAndMarker","buildFreshPlanFromCurrentTrackedGraph","discardEveryPriorPlan"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":null,"exitClass":null,"doctorGuidance":null,"resultingMarker":"exact","nextRows":["WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-211","WDU-LC-212"]},
    {"id":"WDU-LC-202","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"differentOperation"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-203","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"malformed"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-204","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"unsupported"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-205","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"unreadable"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-206","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"preLocalAdmissionRefused"},"marker":{"kind":"one","value":"exactCleanupFailed"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["retainMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true},
    {"id":"WDU-LC-207","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"cancelAfterOwnedMarkerBeforeFirstWorkingTreeMutation"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanOnlyExactOwnedMarker"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-208","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"ownedMarkerCleanupFailsBeforeFirstWorkingTreeMutation"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["attemptCleanOnlyExactOwnedMarker","retainExactMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"exactCleanupFailed"},
    {"id":"WDU-LC-209","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"finalPreMutationRereadMatches"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["rereadAcceptedRevisionAndCompleteStatusFingerprint","rereadMarkerSchemaScopeOperationTargetAndAttemptToken","verifyFreshPlanAgainstReread","checkCancellationImmediatelyBeforeMutation"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-207","WDU-LC-208","WDU-LC-210"]},
    {"id":"WDU-LC-210","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"firstWorkingTreeMutationBegins"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["beginTrackedWorkingTreeMutation","ignoreCancellation","applyFreshPlan","verifyCompleteTargetRoot"],"workingFiles":"actualEvidence","branchIdentity":"unchanged","durableResult":null,"outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-002","WDU-LC-006"]},
    {"id":"WDU-LC-211","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"finalPreMutationRereadRejects"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["rejectStaleRevisionFingerprintMarkerOperationTargetOrPlan","cleanOnlyExactOwnedMarker"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-212","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"finalPreMutationRereadCleanupFails"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["rejectStaleRevisionFingerprintMarkerOperationTargetOrPlan","attemptCleanOnlyExactOwnedMarker","retainExactMarkerEvidence"],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"exactCleanupFailed"},

    {"id":"WDU-LC-001","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"cancelBeforeFirstWorkingTreeMutation"},"marker":{"kind":"aggregate","name":"none"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":[],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":false},
    {"id":"WDU-LC-005","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"failureBeforeFirstWorkingTreeMutation"},"marker":{"kind":"aggregate","name":"none"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":[],"workingFiles":"unchanged","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"Rejected","exitClass":"nonzero","doctorGuidance":false},
    {"id":"WDU-LC-002","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"failureAfterFirstWorkingTreeMutationBeforeSqliteLocalCompletion"},"marker":{"kind":"aggregate","name":"ownedOrNone"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":[],"workingFiles":"mayDiffer","branchIdentity":"unchanged","durableResult":"noCompletion","outcome":"UpdateIncomplete","exitClass":"nonzero","doctorGuidance":false},
    {"id":"WDU-LC-004","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"cancelAfterFirstWorkingTreeMutation"},"marker":{"kind":"aggregate","name":"actualEvidence"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["ignoreCancellation","continueByActualEvidence"],"workingFiles":"actualEvidence","branchIdentity":"actualEvidence","durableResult":null,"outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-002","WDU-LC-006"]},
    {"id":"WDU-LC-006","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"verifiedRootReadyForSqliteLocalCompletion"},"marker":{"kind":"aggregate","name":"postCompletionEvidence"},"selectionState":{"kind":"aggregate","name":"any"}},"firstApplicableRetryWrite":"none","requiredActions":["ignoreCancellation","recordSqliteLocalCompletion","inspectPostCompletionMarker"],"workingFiles":"verifiedTarget","branchIdentity":"unchanged","durableResult":"pending","outcome":null,"exitClass":null,"doctorGuidance":null,"nextRows":["WDU-LC-010","WDU-LC-011","WDU-LC-012","WDU-LC-013","WDU-LC-014","WDU-LC-015","WDU-LC-020","WDU-LC-021","WDU-LC-022","WDU-LC-023","WDU-LC-024","WDU-LC-025","WDU-LC-026","WDU-LC-027","WDU-LC-030","WDU-LC-031","WDU-LC-032","WDU-LC-033","WDU-LC-034","WDU-LC-035","WDU-LC-036","WDU-LC-037"]},
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
    {"id":"WDU-LC-026","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["proveCurrentBranchUnchanged","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false},
    {"id":"WDU-LC-027","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterPublicationProof"},"marker":{"kind":"one","value":"missing"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["proveCurrentBranchUnchanged","attemptTerminalRecording","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true},

    {"id":"WDU-LC-030","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","publishSelectedBranch","provePublication","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-031","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"branchPublicationFailsAfterExactCleanup"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","attemptPublishSelectedBranch","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"previousOrUnknown","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-032","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterExactCleanupAndPublicationProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referencePrevious"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","publishSelectedBranch","provePublication","attemptTerminalRecording","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-033","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","proveSelectedBranch","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-034","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterExactCleanupAndPublicationProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceSelected"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","proveSelectedBranch","attemptTerminalRecording","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"selected","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-035","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"referenceThird"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"third","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},
    {"id":"WDU-LC-036","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"afterSqliteLocalCompletion"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","proveCurrentBranchUnchanged","recordTerminal"],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"terminal","outcome":"Updated","exitClass":"success","doctorGuidance":false,"resultingMarker":"missing"},
    {"id":"WDU-LC-037","match":{"invocation":{"kind":"one","value":"initial"},"trigger":{"kind":"one","value":"terminalRecordingFailsAfterExactCleanupAndPublicationProof"},"marker":{"kind":"one","value":"exact"},"selectionState":{"kind":"one","value":"directoryVersion"}},"firstApplicableRetryWrite":"none","requiredActions":["cleanExactMarker","proveCurrentBranchUnchanged","attemptTerminalRecording","retainPending"],"workingFiles":"verifiedTarget","branchIdentity":"currentUnchanged","durableResult":"pending","outcome":"FinalizationIncomplete","exitClass":"nonzero","doctorGuidance":true,"resultingMarker":"missing"},

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

Rows `WDU-LC-200`–`WDU-LC-212` own pre-local marker admission, exact adoption, refusal, cleanup, fresh planning, and the
final pre-mutation reread. Rows `WDU-LC-010`–`WDU-LC-015` prohibit Branch publication for every disallowed marker
disposition. Rows
`WDU-LC-030`–`WDU-LC-037` require exact cleanup before publication or publication proof. Rows `WDU-LC-100`–
`WDU-LC-143` define retry; the selected `firstApplicableRetryWrite` is conditional, and cancellation is controlling only
immediately before that write begins. These statements identify row families and do not add another ordering contract.

## 5. Requirements and ownership

| ID | Requirement | Primary owner | Required lifecycle rows or proof |
| --- | --- | --- | --- |
| REQ-001 | One deep Branch transaction module | #870 | All `initial` rows and one reachable `run` seam. |
| REQ-002 | Exact typed target and operation identity | #869 | Persisted facts select the row predicates. |
| REQ-003 | Exact immutable prepared content | #837 | `WDU-LC-200`–`WDU-LC-210` fresh-plan and boundary proof. |
| REQ-004 | Stable repository/local-root serialization | #839 | Every non-replay row runs under the same lease scope. |
| REQ-005 | Typed, versioned marker evidence | #869 | `WDU-LC-200`–`WDU-LC-212`, `WDU-LC-010`–`WDU-LC-015`, and `WDU-LC-100`–`WDU-LC-116`. |
| REQ-006 | Fresh planning and local-content safety | #870 | `WDU-LC-200`–`WDU-LC-212` and `WDU-LC-001`–`WDU-LC-006`. |
| REQ-007 | Verified object-first application | #870 | Initial-run proof before SQLite local completion. |
| REQ-008 | Complete final-root verification | #870 | Required before every initial post-completion row. |
| REQ-009 | Canonical atomic local completion | #838 | Distinct boundary preceding `WDU-LC-010`–`WDU-LC-037`. |
| REQ-010 | Bounded pending and terminal completion | #838 | Every terminal row names `durableResult`; routing rows name only valid successors. |
| REQ-011 | Idempotent finalization and blocking | #871 | `WDU-LC-020`–`WDU-LC-037` and `WDU-LC-100`–`WDU-LC-143`. |
| REQ-012 | Truthful outcomes, exits, and Doctor guidance | #871 | Every terminal row names outcome, exit class, and guidance. |
| REQ-013 | Deterministic cancellation precedence | #870 | `WDU-LC-207`–`WDU-LC-210`, `WDU-LC-001`, `WDU-LC-003`, `WDU-LC-004`, `WDU-LC-101`, `WDU-LC-103`, `WDU-LC-110`, `WDU-LC-114`, `WDU-LC-120`, `WDU-LC-123`, `WDU-LC-140`, `WDU-LC-143`. |
| REQ-014 | Same-operation adoption replans freshly | #869 | `WDU-LC-200`–`WDU-LC-212`; no admission may reuse a prior plan. |
| REQ-015 | Branch-only Doctor recovery without file mutation | #842 | All `finalizationRetry` rows require `workingFiles: unchanged`; Watch proof remains #843. |
| REQ-016 | Caller-specific ordering | #871 | Branch order is exclusively the normative table; later callers consume it. |
| REQ-017 | Current public and contributor documentation | #846 | Final audit validates row references and removes competing sequences. |

Each row has one primary implementation owner even when companion issues prove a selector or recovery projection.

## 6. Proof, propagation, and readiness

Static validation parses the fenced JSON and its closed grammar, rejects unknown or malformed predicates, expands every
Cartesian cell, rejects overlap, and verifies row, outcome, marker, and cancellation coverage. It also asserts every
`FinalizationIncomplete` row has a nonzero exit and Doctor guidance. Issue #882 must consume these declarations without
inventing wildcard or precedence rules. Behavioral proof must use production-reachable persisted facts and
deterministic seams; source-string assertions, sleeps, and impossible hand-built states are insufficient.

<!-- grace:wdu-lifecycle-projection-plan:start -->
```json
{
  "schema": "grace.wdu.lifecycle-projection-plan/v1",
  "canonicalContentDigest": "901fe35f11362c73d7200151e84ee2827dfee965758cb8e42925167c26aee7f7",
  "canonicalRowCount": 67,
  "canonicalApplicabilityKeyCount": 254,
  "assignments": [
    {"artifact":"adr-0011","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-202","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-212","WDU-LC-006","WDU-LC-010","WDU-LC-015","WDU-LC-020","WDU-LC-023","WDU-LC-025","WDU-LC-026","WDU-LC-030","WDU-LC-033","WDU-LC-035","WDU-LC-036","WDU-LC-100","WDU-LC-101","WDU-LC-103","WDU-LC-110","WDU-LC-114","WDU-LC-120","WDU-LC-123","WDU-LC-130","WDU-LC-140","WDU-LC-143","WDU-LC-003"],"proof":"Record the lasting Branch transaction lifecycle and reference canonical rows without copying their order."},
    {"artifact":"epic-835","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-202","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-212","WDU-LC-006","WDU-LC-010","WDU-LC-015","WDU-LC-020","WDU-LC-030","WDU-LC-100","WDU-LC-101","WDU-LC-103","WDU-LC-110","WDU-LC-114","WDU-LC-120","WDU-LC-123","WDU-LC-130","WDU-LC-140","WDU-LC-143","WDU-LC-003"],"proof":"Keep the epic dependency and requirement packet aligned with the sole canonical lifecycle."},
    {"artifact":"issue-842","rowIds":["WDU-LC-100","WDU-LC-101","WDU-LC-102","WDU-LC-103","WDU-LC-104","WDU-LC-105","WDU-LC-106","WDU-LC-107","WDU-LC-108","WDU-LC-109","WDU-LC-115","WDU-LC-116","WDU-LC-110","WDU-LC-111","WDU-LC-112","WDU-LC-113","WDU-LC-114","WDU-LC-120","WDU-LC-121","WDU-LC-122","WDU-LC-123","WDU-LC-130","WDU-LC-140","WDU-LC-141","WDU-LC-142","WDU-LC-143","WDU-LC-003"],"proof":"Prove Branch-only Doctor retry, refusal, cancellation boundaries, terminal replay, and no working-file mutation."},
    {"artifact":"issue-843","rowIds":["WDU-LC-003","WDU-LC-100","WDU-LC-101","WDU-LC-103"],"proof":"Consume the completed Branch lifecycle while deferring the first real Watch producer and retry proof to this issue."},
    {"artifact":"issue-846","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-202","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-212","WDU-LC-006","WDU-LC-010","WDU-LC-015","WDU-LC-020","WDU-LC-030","WDU-LC-100","WDU-LC-101","WDU-LC-103","WDU-LC-110","WDU-LC-114","WDU-LC-120","WDU-LC-123","WDU-LC-130","WDU-LC-140","WDU-LC-143","WDU-LC-003"],"proof":"Audit the completed 17/9 packet, public outcomes, Doctor guidance, and removal of competing lifecycle paths."},
    {"artifact":"issue-869","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-202","WDU-LC-203","WDU-LC-204","WDU-LC-205","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-211","WDU-LC-212","WDU-LC-010","WDU-LC-011","WDU-LC-012","WDU-LC-013","WDU-LC-014","WDU-LC-015","WDU-LC-100"],"proof":"Persist typed selection and marker facts that select canonical admission, refusal, cleanup, and retry rows."},
    {"artifact":"issue-870","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-202","WDU-LC-203","WDU-LC-204","WDU-LC-205","WDU-LC-206","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-211","WDU-LC-212","WDU-LC-001","WDU-LC-005","WDU-LC-002","WDU-LC-004","WDU-LC-006","WDU-LC-010","WDU-LC-011","WDU-LC-012","WDU-LC-013","WDU-LC-014","WDU-LC-015","WDU-LC-026","WDU-LC-027","WDU-LC-036","WDU-LC-037","WDU-LC-100","WDU-LC-101","WDU-LC-102","WDU-LC-103","WDU-LC-115","WDU-LC-116","WDU-LC-140","WDU-LC-141","WDU-LC-142","WDU-LC-143","WDU-LC-003"],"proof":"Implement exact-root DirectoryVersion switching with fresh planning, mutation boundaries, retry, and current-Branch retention."},
    {"artifact":"issue-871","rowIds":["WDU-LC-020","WDU-LC-021","WDU-LC-022","WDU-LC-023","WDU-LC-024","WDU-LC-025","WDU-LC-030","WDU-LC-031","WDU-LC-032","WDU-LC-033","WDU-LC-034","WDU-LC-035","WDU-LC-100","WDU-LC-101","WDU-LC-102","WDU-LC-103","WDU-LC-104","WDU-LC-105","WDU-LC-106","WDU-LC-107","WDU-LC-108","WDU-LC-109","WDU-LC-110","WDU-LC-111","WDU-LC-112","WDU-LC-113","WDU-LC-114","WDU-LC-120","WDU-LC-121","WDU-LC-122","WDU-LC-123","WDU-LC-130","WDU-LC-003"],"proof":"Implement repeatable Reference previous, selected, and third-state finalization with truthful retry outcomes."},
    {"artifact":"issue-872","rowIds":["WDU-LC-200","WDU-LC-201","WDU-LC-207","WDU-LC-208","WDU-LC-209","WDU-LC-210","WDU-LC-211","WDU-LC-212","WDU-LC-001","WDU-LC-005","WDU-LC-002","WDU-LC-004","WDU-LC-006","WDU-LC-003"],"proof":"Prove Save and no-Save admission preserve one sealed baseline and action token through the final pre-mutation reread."}
  ]
}
```
<!-- grace:wdu-lifecycle-projection-plan:end -->

The following consumers remain pending projections rather than competing authorities:

- #869 persists selection and marker predicates.
- #870 proves initial-run and cancellation rows.
- #871 proves Reference and retry row families.
- #872 proves Save/no-Save admission reaches the same initial rows.
- #842 proves Branch-only retry rows without working-file mutation.
- #843–#845 later consume the transaction contract for Watch and Connect.
- #846 audits public output, Doctor guidance, row references, and absence of retired paths.

ADR 0011 and active issue bodies remain contextual consumers until their #882 projections cite row IDs. They must not be
treated as lifecycle ordering authority. This document returns to Plan-ready only after those projections validate
against the machine-readable block.

The bounded Product V1 residual risk remains interruption after working-tree mutation but before SQLite local
completion. `WDU-LC-002` requires truthful `UpdateIncomplete` and fresh revalidation; Grace does not guess, roll back,
or add a broader recovery system.
