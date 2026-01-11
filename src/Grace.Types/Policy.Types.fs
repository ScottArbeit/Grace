namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Policy =
    /// The Id of the policy snapshot (hash of contents + parser version).
    type PolicySnapshotId = Sha256Hash

    /// Controls token budgets for model analysis stages.
    [<GenerateSerializer>]
    type PolicyAnalysisSettings = { Enabled: bool; MaxTokens: int }

    /// Rules used to determine whether Stage 0 is non-trivial.
    [<GenerateSerializer>]
    type PolicyNonTrivialSignal = { ChurnLinesThreshold: int; TouchedSensitivePaths: bool; DependencyConfigChanges: bool; ApiSurfaceChanges: bool }

    /// Default policy values.
    [<GenerateSerializer>]
    type PolicyDefaults =
        {
            RequireHumanReview: bool
            DeepAnalysis: PolicyAnalysisSettings
            Triage: PolicyAnalysisSettings
            NonTrivialSignal: PolicyNonTrivialSignal
        }

    /// Rule for policy triggers.
    [<GenerateSerializer>]
    type PolicyRule =
        {
            PathMatches: string option
            FileMatches: string option
            ApiSurfaceChanged: bool option
            DependencyChanged: bool option
            ConfigChanged: bool option
        }

    /// Redaction configuration for evidence extraction.
    [<GenerateSerializer>]
    type PolicyRedaction = { Enabled: bool; Patterns: string list; DenylistPaths: string list }

    /// Baseline drift thresholds for re-acknowledgement.
    [<GenerateSerializer>]
    type BaselineDriftThreshold = { ChurnLines: int; FilesTouched: int }

    /// Policy rules for approvals.
    [<GenerateSerializer>]
    type PolicyApprovalRules = { BaselineDriftReackThreshold: BaselineDriftThreshold }

    /// Defines queue behavior after a failure.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type QueueFailureAction =
        | PauseQueue
        | Continue
        | QuarantineCandidate

        static member GetKnownTypes() = GetKnownTypes<QueueFailureAction>()

    /// Queue-related policy configuration.
    [<GenerateSerializer>]
    type PolicyQueueRules = { OnFailure: QueueFailureAction }

    /// Compiled policy ruleset.
    [<GenerateSerializer>]
    type PolicyRuleset =
        {
            Defaults: PolicyDefaults
            HumanReviewRequiredWhen: PolicyRule list
            DeepAnalysisRequiredWhen: PolicyRule list
            SensitivePaths: string list
            Redaction: PolicyRedaction
            ApprovalRules: PolicyApprovalRules
            Queue: PolicyQueueRules
        }

    /// Immutable snapshot of the compiled policy.
    [<GenerateSerializer>]
    type PolicySnapshot =
        {
            Class: string
            PolicySnapshotId: PolicySnapshotId
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            TargetBranchId: BranchId
            PolicyVersion: int
            ParserVersion: string
            SourceHash: Sha256Hash
            Rules: PolicyRuleset
            CreatedAt: Instant
        }

        static member Default =
            {
                Class = nameof PolicySnapshot
                PolicySnapshotId = Sha256Hash String.Empty
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                TargetBranchId = BranchId.Empty
                PolicyVersion = 1
                ParserVersion = String.Empty
                SourceHash = Sha256Hash String.Empty
                Rules =
                    {
                        Defaults =
                            {
                                RequireHumanReview = true
                                DeepAnalysis = { Enabled = true; MaxTokens = 12000 }
                                Triage = { Enabled = true; MaxTokens = 2500 }
                                NonTrivialSignal =
                                    { ChurnLinesThreshold = 80; TouchedSensitivePaths = true; DependencyConfigChanges = true; ApiSurfaceChanges = true }
                            }
                        HumanReviewRequiredWhen = []
                        DeepAnalysisRequiredWhen = []
                        SensitivePaths = []
                        Redaction = { Enabled = false; Patterns = []; DenylistPaths = [] }
                        ApprovalRules = { BaselineDriftReackThreshold = { ChurnLines = 50; FilesTouched = 5 } }
                        Queue = { OnFailure = QueueFailureAction.PauseQueue }
                    }
                CreatedAt = Constants.DefaultTimestamp
            }

    /// Acknowledgement of a policy snapshot.
    [<GenerateSerializer>]
    type PolicyAcknowledgement = { PolicySnapshotId: PolicySnapshotId; AcknowledgedBy: UserId; AcknowledgedAt: Instant; Note: string option }

    /// Defines the commands for the Policy actor.
    [<KnownType("GetKnownTypes")>]
    type PolicyCommand =
        | CreateSnapshot of policySnapshot: PolicySnapshot
        | Acknowledge of policySnapshotId: PolicySnapshotId * acknowledgedBy: UserId * note: string option

        static member GetKnownTypes() = GetKnownTypes<PolicyCommand>()

    /// Defines the events for the Policy actor.
    [<KnownType("GetKnownTypes")>]
    type PolicyEventType =
        | SnapshotCreated of policySnapshot: PolicySnapshot
        | Acknowledged of policySnapshotId: PolicySnapshotId * acknowledgedBy: UserId * note: string option

        static member GetKnownTypes() = GetKnownTypes<PolicyEventType>()

    /// Record that holds the event type and metadata for a Policy event.
    type PolicyEvent =
        {
            /// The PolicyEventType case that describes the event.
            Event: PolicyEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }
