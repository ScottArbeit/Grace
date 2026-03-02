namespace Grace.Shared.Parameters

open Orleans
open System

module Common =

    [<GenerateSerializer>]
    type CommonParameters() =
        member val public CorrelationId: string = String.Empty with get, set
        member val public Principal: string = String.Empty with get, set

    /// Base parameters for /agent/session endpoints.
    type AgentSessionParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set
        member val public AgentId = String.Empty with get, set
        member val public AgentDisplayName = String.Empty with get, set

    /// Parameters for /agent/session/start.
    ///
    /// OperationId is a caller-supplied idempotency token for deterministic replay handling.
    type StartAgentSessionParameters() =
        inherit AgentSessionParameters()
        member val public WorkItemIdOrNumber = String.Empty with get, set
        member val public PromotionSetId = String.Empty with get, set
        member val public Source = String.Empty with get, set
        member val public OperationId = String.Empty with get, set

    /// Parameters for /agent/session/stop.
    ///
    /// OperationId is a caller-supplied idempotency token for deterministic replay handling.
    type StopAgentSessionParameters() =
        inherit AgentSessionParameters()
        member val public SessionId = String.Empty with get, set
        member val public WorkItemIdOrNumber = String.Empty with get, set
        member val public StopReason = String.Empty with get, set
        member val public OperationId = String.Empty with get, set

    /// Parameters for /agent/session/status.
    type GetAgentSessionStatusParameters() =
        inherit AgentSessionParameters()
        member val public SessionId = String.Empty with get, set
        member val public WorkItemIdOrNumber = String.Empty with get, set

    /// Parameters for /agent/session/active.
    type GetActiveAgentSessionParameters() =
        inherit AgentSessionParameters()
        member val public WorkItemIdOrNumber = String.Empty with get, set

    /// Parameters for /agent/session/listActive.
    type ListActiveAgentSessionsParameters() =
        inherit AgentSessionParameters()
        member val public MaximumSessionCount = 25 with get, set
