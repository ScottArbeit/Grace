namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Policy
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Review =
    /// Defines the change type for a modified path.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type PathChangeType =
        | Added
        | Modified
        | Deleted
        | Renamed

        static member GetKnownTypes() = GetKnownTypes<PathChangeType>()

    /// Represents a modified path in a deterministic analysis.
    [<GenerateSerializer>]
    type PathChange = { RelativePath: RelativePath; ChangeType: PathChangeType }

    /// Aggregated churn metrics for a delta.
    [<GenerateSerializer>]
    type ChurnMetrics = { LinesAdded: int; LinesRemoved: int; FilesChanged: int; RenamedCount: int }

    /// Indicates whether test evidence is available.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type TestEvidencePresence =
        | Unknown
        | Present
        | Missing

        static member GetKnownTypes() = GetKnownTypes<TestEvidencePresence>()

    /// Volatility signal representing reference creation activity.
    [<GenerateSerializer>]
    type VolatilitySignal = { ReferencesCreated: int; WindowDays: int }

    /// Deterministic triggers that can be fired by deterministic review analysis.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type DeterministicTrigger =
        | ChurnLinesExceeded
        | SensitivePathTouched
        | DependencyConfigChanged
        | ApiSurfaceSignalDetected
        | FileRewriteStorm
        | RenameStorm
        | GeneratedFilesChanged
        | BinaryFilesChanged
        | HighVolatility

        static member GetKnownTypes() = GetKnownTypes<DeterministicTrigger>()

    /// Deterministic analysis output.
    [<GenerateSerializer>]
    type DeterministicRiskProfile =
        {
            ReferenceId: ReferenceId
            PolicySnapshotId: PolicySnapshotId
            ChangedPaths: PathChange list
            Churn: ChurnMetrics
            SensitivePathsTouched: RelativePath list
            DependencyConfigChanges: RelativePath list
            ApiSurfaceSignals: RelativePath list
            TestEvidence: TestEvidencePresence
            GeneratedFiles: RelativePath list
            BinaryFiles: RelativePath list
            Volatility: VolatilitySignal option
            TriggersFired: DeterministicTrigger list
            IsNonTrivialSignal: bool
            NonTrivialTriggers: DeterministicTrigger list
            CreatedAt: Instant
        }

        static member Default =
            {
                ReferenceId = ReferenceId.Empty
                PolicySnapshotId = PolicySnapshotId String.Empty
                ChangedPaths = []
                Churn = { LinesAdded = 0; LinesRemoved = 0; FilesChanged = 0; RenamedCount = 0 }
                SensitivePathsTouched = []
                DependencyConfigChanges = []
                ApiSurfaceSignals = []
                TestEvidence = TestEvidencePresence.Unknown
                GeneratedFiles = []
                BinaryFiles = []
                Volatility = None
                TriggersFired = []
                IsNonTrivialSignal = false
                NonTrivialTriggers = []
                CreatedAt = Constants.DefaultTimestamp
            }

    /// Identifies the stage for an evidence set.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type EvidenceStage =
        | Triage
        | Deep

        static member GetKnownTypes() = GetKnownTypes<EvidenceStage>()

    /// Budget limits for evidence selection.
    [<GenerateSerializer>]
    type EvidenceBudget = { MaxFiles: int; MaxHunksPerFile: int; MaxLinesPerHunk: int; MaxTotalBytes: int; MaxTokens: int }

    /// A selected evidence slice.
    [<GenerateSerializer>]
    type EvidenceSlice = { RelativePath: RelativePath; StartLine: int; EndLine: int; Content: string; IsRedacted: bool }

    /// Reason for evidence selection.
    [<GenerateSerializer>]
    type EvidenceScoreReason = { Feature: string; Score: float }

    /// Summary of a selected evidence slice.
    [<GenerateSerializer>]
    type EvidenceSliceSummary = { RelativePath: RelativePath; StartLine: int; EndLine: int; Score: float; Reasons: EvidenceScoreReason list }

    /// Full evidence set for model input.
    [<GenerateSerializer>]
    type EvidenceSet = { Stage: EvidenceStage; Slices: EvidenceSlice list; Budget: EvidenceBudget; TotalBytes: int; EstimatedTokens: int }

    /// Summary of evidence selection for auditability.
    [<GenerateSerializer>]
    type EvidenceSetSummary =
        {
            Stage: EvidenceStage
            SelectedFiles: RelativePath list
            SliceSummaries: EvidenceSliceSummary list
            Budget: EvidenceBudget
            TotalBytes: int
            EstimatedTokens: int
            TopReasons: EvidenceScoreReason list
        }

    /// Receipt describing a model analysis run.
    [<GenerateSerializer>]
    type AnalysisReceipt =
        {
            AnalysisReceiptId: AnalysisReceiptId
            Stage: EvidenceStage
            PolicySnapshotId: PolicySnapshotId
            EvidenceHash: Sha256Hash
            EvidenceSummary: EvidenceSetSummary
            ModelId: string
            MaxTokens: int
            OutputHash: Sha256Hash
            TriggerReasons: string list
            CreatedAt: Instant
            Principal: UserId
        }

        static member Default =
            {
                AnalysisReceiptId = Guid.Empty
                Stage = EvidenceStage.Triage
                PolicySnapshotId = PolicySnapshotId String.Empty
                EvidenceHash = Sha256Hash String.Empty
                EvidenceSummary =
                    {
                        Stage = EvidenceStage.Triage
                        SelectedFiles = []
                        SliceSummaries = []
                        Budget = { MaxFiles = 0; MaxHunksPerFile = 0; MaxLinesPerHunk = 0; MaxTotalBytes = 0; MaxTokens = 0 }
                        TotalBytes = 0
                        EstimatedTokens = 0
                        TopReasons = []
                    }
                ModelId = String.Empty
                MaxTokens = 0
                OutputHash = Sha256Hash String.Empty
                TriggerReasons = []
                CreatedAt = Constants.DefaultTimestamp
                Principal = UserId String.Empty
            }

    /// The Id of a deterministic chapter.
    type ChapterId = Sha256Hash

    /// The Id of a review finding.
    type FindingId = Guid

    /// The Id of an analysis receipt.
    type AnalysisReceiptId = Guid

    /// Severity levels for review findings.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type FindingSeverity =
        | Info
        | Low
        | Medium
        | High
        | Critical

        static member GetKnownTypes() = GetKnownTypes<FindingSeverity>()

    /// Categories for review findings.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type FindingCategory =
        | Security
        | Performance
        | Api
        | Config
        | Tests
        | Behavior
        | Other of string

        static member GetKnownTypes() = GetKnownTypes<FindingCategory>()

    /// Resolution state for a finding.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type FindingResolutionState =
        | Open
        | Approved
        | Rejected
        | NeedsChanges
        | Deferred
        | Superseded

        static member GetKnownTypes() = GetKnownTypes<FindingResolutionState>()

    /// Evidence reference for a finding.
    [<GenerateSerializer>]
    type EvidenceReference = { RelativePath: RelativePath; StartLine: int; EndLine: int }

    /// Record describing a review finding.
    [<GenerateSerializer>]
    type Finding =
        {
            FindingId: FindingId
            Severity: FindingSeverity
            Category: FindingCategory
            Description: string
            Rationale: string
            RequiredActionType: string
            EvidenceReferences: EvidenceReference list
            ResolutionState: FindingResolutionState
            ResolvedBy: UserId option
            ResolvedAt: Instant option
            ResolutionNote: string option
        }

    /// Deterministic chapter representation.
    [<GenerateSerializer>]
    type Chapter =
        {
            ChapterId: ChapterId
            Title: string
            Summary: string
            Paths: RelativePath list
            FindingIds: FindingId list
            Evidence: EvidenceSliceSummary list
        }

    /// Review checkpoint for incremental review tracking.
    [<GenerateSerializer>]
    type ReviewCheckpoint =
        {
            ReviewCheckpointId: ReviewCheckpointId
            PromotionSetId: PromotionSetId option
            ReviewedUpToReferenceId: ReferenceId
            PolicySnapshotId: PolicySnapshotId
            Reviewer: UserId
            Timestamp: Instant
        }

    /// Summary of validation results included in the review notes.
    [<GenerateSerializer>]
    type ValidationSummary = { ValidationResultIds: ValidationResultId list; Summary: string }

    /// Primary review notes for a promotion set.
    [<GenerateSerializer>]
    type ReviewNotes =
        {
            Class: string
            ReviewNotesId: ReviewNotesId
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            PromotionSetId: PromotionSetId option
            PolicySnapshotId: PolicySnapshotId
            Summary: string
            Chapters: Chapter list
            Findings: Finding list
            ImpactMap: string
            EvidenceSetSummary: EvidenceSetSummary option
            ValidationSummary: ValidationSummary option
            EscalationReceiptIds: AnalysisReceiptId list
            CreatedAt: Instant
            UpdatedAt: Instant option
        }

        static member Default =
            {
                Class = nameof ReviewNotes
                ReviewNotesId = ReviewNotesId.Empty
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                PromotionSetId = None
                PolicySnapshotId = PolicySnapshotId String.Empty
                Summary = String.Empty
                Chapters = []
                Findings = []
                ImpactMap = String.Empty
                EvidenceSetSummary = None
                ValidationSummary = None
                EscalationReceiptIds = []
                CreatedAt = Constants.DefaultTimestamp
                UpdatedAt = None
            }

    /// Defines the commands for the Review actor.
    [<KnownType("GetKnownTypes")>]
    type ReviewCommand =
        | UpsertNotes of reviewNotes: ReviewNotes
        | ResolveFinding of findingId: FindingId * resolutionState: FindingResolutionState * resolvedBy: UserId * note: string option
        | AddCheckpoint of checkpoint: ReviewCheckpoint

        static member GetKnownTypes() = GetKnownTypes<ReviewCommand>()

    /// Defines the events for the Review actor.
    [<KnownType("GetKnownTypes")>]
    type ReviewEventType =
        | NotesUpserted of reviewNotes: ReviewNotes
        | FindingResolved of findingId: FindingId * resolutionState: FindingResolutionState * resolvedBy: UserId * note: string option
        | CheckpointAdded of checkpoint: ReviewCheckpoint

        static member GetKnownTypes() = GetKnownTypes<ReviewEventType>()

    /// Record that holds the event type and metadata for a Review event.
    type ReviewEvent =
        {
            /// The ReviewEventType case that describes the event.
            Event: ReviewEventType
            /// The EventMetadata for the event. EventMetadata includes the Timestamp, CorrelationId, Principal, and a Properties dictionary.
            Metadata: EventMetadata
        }
