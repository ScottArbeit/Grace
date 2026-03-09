namespace Grace.Types.ExternalEvents

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
type AgentRuntimeLifecycleSource =
    {
        EventName: string
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
        LifecycleState: string
        Message: string option
        OperationId: string option
        WasIdempotentReplay: bool
        Source: string option
        Result: AgentWorkStoppedResult option
    }

[<GenerateSerializer>]
type CanonicalReplaySource =
    | RawEventSource of actorType: string * actorId: string * correlationId: CorrelationId
    | AgentRuntimeSource of runtime: AgentRuntimeLifecycleSource

    static member GetKnownTypes() = Grace.Shared.Utilities.GetKnownTypes<CanonicalReplaySource>()
