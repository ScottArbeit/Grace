namespace Grace.Types.ExternalEvents

open Grace.Types.Events
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization


[<KnownType("GetKnownTypes"); GenerateSerializer>]
type AgentSessionLifecycleState =
    | Inactive
    | Active
    | Stopping
    | Stopped

    static member GetKnownTypes() = Grace.Shared.Utilities.GetKnownTypes<AgentSessionLifecycleState>()

[<GenerateSerializer>]
type AgentSessionInfo =
    {
        SessionId: string
        AgentId: string
        AgentDisplayName: string
        WorkItemIdOrNumber: string
        PromotionSetId: string
        Source: string
        LifecycleState: AgentSessionLifecycleState
        StartedAt: Instant option
        LastUpdatedAt: Instant option
        StoppedAt: Instant option
    }

    static member Default =
        {
            SessionId = String.Empty
            AgentId = String.Empty
            AgentDisplayName = String.Empty
            WorkItemIdOrNumber = String.Empty
            PromotionSetId = String.Empty
            Source = String.Empty
            LifecycleState = AgentSessionLifecycleState.Inactive
            StartedAt = None
            LastUpdatedAt = None
            StoppedAt = None
        }

[<GenerateSerializer>]
type AgentSessionOperationResult =
    {
        Session: AgentSessionInfo
        Message: string
        OperationId: string
        WasIdempotentReplay: bool
    }

    static member Default = { Session = AgentSessionInfo.Default; Message = String.Empty; OperationId = String.Empty; WasIdempotentReplay = false }

[<GenerateSerializer>]
type AgentSessionListResult =
    {
        Sessions: AgentSessionInfo list
        Count: int
        Message: string
    }

    static member Default = { Sessions = []; Count = 0; Message = String.Empty }

[<KnownType("GetKnownTypes"); GenerateSerializer>]
type WorkItemLocatorKind =
    | Id
    | Number
    | Opaque

    static member GetKnownTypes() = Grace.Shared.Utilities.GetKnownTypes<WorkItemLocatorKind>()

[<GenerateSerializer>]
type WorkItemLocator = { Raw: string; Kind: string }

[<GenerateSerializer>]
type AgentWorkStoppedResult = { Message: string; OperationId: string; WasIdempotentReplay: bool }

[<GenerateSerializer>]
type AgentRuntimeSourceContext =
    {
        OccurredAt: Instant
        CorrelationId: CorrelationId
        OwnerId: OwnerId
        OrganizationId: OrganizationId
        RepositoryId: RepositoryId
        SessionId: string
        AgentId: string
        BranchId: BranchId option
        WorkItemId: WorkItemId option
        WorkItemLocator: WorkItemLocator option
        PromotionSetId: PromotionSetId option
        LifecycleState: AgentSessionLifecycleState
    }

[<GenerateSerializer>]
type AgentWorkStartedSource =
    {
        Context: AgentRuntimeSourceContext
        Message: string option
        OperationId: string option
        WasIdempotentReplay: bool
        Source: string option
    }

[<GenerateSerializer>]
type AgentWorkStoppedSource = { Context: AgentRuntimeSourceContext; Result: AgentWorkStoppedResult }

[<KnownType("GetKnownTypes"); GenerateSerializer>]
type AgentRuntimeLifecycleSource =
    | WorkStarted of started: AgentWorkStartedSource
    | WorkStopped of stopped: AgentWorkStoppedSource

    static member GetKnownTypes() = Grace.Shared.Utilities.GetKnownTypes<AgentRuntimeLifecycleSource>()

module AgentRuntimeLifecycleSource =
    let context source =
        match source with
        | WorkStarted started -> started.Context
        | WorkStopped stopped -> stopped.Context

    let eventName source =
        match source with
        | WorkStarted _ -> CanonicalEventName.toString CanonicalEventName.AgentWorkStarted
        | WorkStopped _ -> CanonicalEventName.toString CanonicalEventName.AgentWorkStopped

[<KnownType("GetKnownTypes"); GenerateSerializer>]
type ReplaySourceKind =
    | RawGraceEvent
    | AgentRuntimeLifecycle
    | ValidationRequested

    static member GetKnownTypes() = Grace.Shared.Utilities.GetKnownTypes<ReplaySourceKind>()

module ReplaySourceKind =
    let toString sourceKind =
        match sourceKind with
        | ReplaySourceKind.RawGraceEvent -> "raw-grace-event"
        | ReplaySourceKind.AgentRuntimeLifecycle -> "agent-runtime-lifecycle"
        | ReplaySourceKind.ValidationRequested -> "validation-requested"

[<GenerateSerializer>]
type ValidationRequestedReplaySource =
    {
        OccurredAt: Instant
        CorrelationId: CorrelationId
        OwnerId: OwnerId
        OrganizationId: OrganizationId
        RepositoryId: RepositoryId
        ValidationSetId: ValidationSetId
        SourceEventId: string
        SourceEventName: string
        TargetBranchId: BranchId
        TargetBranchName: BranchName
        PromotionSetId: PromotionSetId option
        StepsComputationAttempt: int option
        MatchedRules: MatchedRule list
        ValidationsSummary: ValidationsSummary
    }

[<GenerateSerializer>]
type DurableReplaySourceRecord =
    {
        EventId: string
        EventName: string
        EventVersion: int
        SourceKind: ReplaySourceKind
        ActorType: string
        ActorId: string
        CorrelationId: CorrelationId
        RecordedAt: Instant
        RawGraceEvent: GraceEvent option
        RuntimeSource: AgentRuntimeLifecycleSource option
        ValidationRequestedSource: ValidationRequestedReplaySource option
    }
