namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Events
open Grace.Types.Common
open Grace.Types.Webhooks
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for approval request keys, proxies, state, or workflow transitions.
module ApprovalRequest =

    /// Coordinates command name logic for the ApprovalRequest actor.
    let private commandName command =
        match command with
        | ApprovalRequestCommand.Create _ -> "Create"
        | ApprovalRequestCommand.RecordDecision _ -> "RecordDecision"
        | ApprovalRequestCommand.Expire -> "Expire"
        | ApprovalRequestCommand.Cancel -> "Cancel"
        | ApprovalRequestCommand.Supersede _ -> "Supersede"

    /// Applies events changes to the ApprovalRequest actor state.
    let private applyEvents (events: ApprovalRequestEvent list) (request: ApprovalRequest) =
        events
        |> List.fold (fun current event -> ApprovalRequest.UpdateDto event current) request

    /// Coordinates grace error logic for the ApprovalRequest actor.
    let private graceError correlationId message = GraceError.Create message correlationId

    /// Compares create-request fields to detect idempotent approval-request retries.
    let private createdRequestMatches (existing: ApprovalRequest) (candidate: ApprovalRequest) =
        existing.ApprovalPolicyId = candidate.ApprovalPolicyId
        && existing.ApprovalPolicyVersion = candidate.ApprovalPolicyVersion
        && existing.Subject = candidate.Subject
        && existing.Scope = candidate.Scope
        && existing.RequiredResponder = candidate.RequiredResponder

    /// Coordinates decision matches logic for the ApprovalRequest actor.
    let private decisionMatches (existing: ApprovalRequestDecision) (candidate: ApprovalRequestDecision) =
        existing.Decision = candidate.Decision
        && existing.DecidedBy = candidate.DecidedBy
        && existing.Reason = candidate.Reason
        && existing.ClientDecisionId = candidate.ClientDecisionId

    /// Coordinates ok logic for the ApprovalRequest actor.
    let private ok (request: ApprovalRequest) (events: ApprovalRequestEvent list) wasReplay message =
        Ok { Request = applyEvents events request; Events = events; WasIdempotentReplay = wasReplay; Message = message }

    /// Validates a ApprovalRequest command and derives the events needed for a state transition.
    let decideCommand (request: ApprovalRequest) (command: ApprovalRequestCommand) (metadata: EventMetadata) =
        let exists =
            request.ApprovalRequestId
            <> ApprovalRequestId.Empty

        match command with
        | ApprovalRequestCommand.Create candidate ->
            if candidate.ApprovalRequestId = ApprovalRequestId.Empty then
                Error(graceError metadata.CorrelationId "ApprovalRequestId must be a non-empty Guid.")
            elif isNull (box candidate.Scope) then
                Error(graceError metadata.CorrelationId "Approval request scope is required.")
            elif candidate.ApprovalPolicyId = ApprovalPolicyId.Empty then
                Error(graceError metadata.CorrelationId "ApprovalPolicyId must be a non-empty Guid.")
            elif String.IsNullOrWhiteSpace candidate.Subject then
                Error(graceError metadata.CorrelationId "Approval request subject is required.")
            elif String.IsNullOrWhiteSpace candidate.RequiredResponder then
                Error(graceError metadata.CorrelationId "Approval request responder selector is required.")
            elif exists && createdRequestMatches request candidate then
                ok request [] true "Approval request create command replayed."
            elif exists then
                Error(graceError metadata.CorrelationId "Approval request already exists with a different payload.")
            else
                let created = { candidate with Status = ApprovalRequestStatus.Pending; Decision = None; UpdatedAt = None; SupersededByApprovalRequestId = None }

                let event: ApprovalRequestEvent = { Event = ApprovalRequestEventType.Created created; Metadata = metadata }
                ok request [ event ] false "Approval request created."
        | ApprovalRequestCommand.RecordDecision decision ->
            if not exists then
                Error(graceError metadata.CorrelationId "Approval request must be created before recording a decision.")
            elif String.IsNullOrWhiteSpace decision.ClientDecisionId then
                Error(graceError metadata.CorrelationId "ClientDecisionId is required.")
            else
                match request.Decision with
                | Some existing when
                    existing.ClientDecisionId = decision.ClientDecisionId
                    && decisionMatches existing decision
                    ->
                    ok request [] true "Approval request decision replayed."
                | Some existing when existing.ClientDecisionId = decision.ClientDecisionId ->
                    Error(graceError metadata.CorrelationId "ClientDecisionId was already used with a different approval decision.")
                | Some _ -> Error(graceError metadata.CorrelationId $"Approval request is already {request.Status}.")
                | None when request.Status.IsTerminal -> Error(graceError metadata.CorrelationId $"Approval request is already {request.Status}.")
                | None ->
                    let event: ApprovalRequestEvent = { Event = ApprovalRequestEventType.DecisionRecorded decision; Metadata = metadata }
                    ok request [ event ] false "Approval request decision recorded."
        | ApprovalRequestCommand.Expire ->
            if not exists then
                Error(graceError metadata.CorrelationId "Approval request must be created before expiring.")
            elif request.Status = ApprovalRequestStatus.Expired then
                ok request [] true "Approval request expiration replayed."
            elif request.Status.IsTerminal then
                Error(graceError metadata.CorrelationId $"Approval request is already {request.Status}.")
            else
                let event: ApprovalRequestEvent = { Event = ApprovalRequestEventType.Expired; Metadata = metadata }
                ok request [ event ] false "Approval request expired."
        | ApprovalRequestCommand.Cancel ->
            if not exists then
                Error(graceError metadata.CorrelationId "Approval request must be created before cancelling.")
            elif request.Status = ApprovalRequestStatus.Cancelled then
                ok request [] true "Approval request cancellation replayed."
            elif request.Status.IsTerminal then
                Error(graceError metadata.CorrelationId $"Approval request is already {request.Status}.")
            else
                let event: ApprovalRequestEvent = { Event = ApprovalRequestEventType.Cancelled; Metadata = metadata }
                ok request [ event ] false "Approval request cancelled."
        | ApprovalRequestCommand.Supersede supersededByApprovalRequestId ->
            if not exists then
                Error(graceError metadata.CorrelationId "Approval request must be created before superseding.")
            elif supersededByApprovalRequestId = ApprovalRequestId.Empty then
                Error(graceError metadata.CorrelationId "SupersededByApprovalRequestId must be a non-empty Guid.")
            elif request.Status = ApprovalRequestStatus.Superseded
                 && request.SupersededByApprovalRequestId = Some supersededByApprovalRequestId then
                ok request [] true "Approval request supersede command replayed."
            elif request.Status.IsTerminal then
                Error(graceError metadata.CorrelationId $"Approval request is already {request.Status}.")
            else
                let event: ApprovalRequestEvent = { Event = ApprovalRequestEventType.Superseded supersededByApprovalRequestId; Metadata = metadata }
                ok request [ event ] false "Approval request superseded."

    /// Implements the Orleans grain for approval request actor.
    type ApprovalRequestActor([<PersistentState(StateName.ApprovalRequest, GraceActorStorage)>] state: IPersistentState<List<ApprovalRequestEvent>>) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("ApprovalRequest.Actor")
        let mutable request = Grace.Types.Webhooks.ApprovalRequest.Default
        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            if isNull state.State then state.State <- List<ApprovalRequestEvent>()

            request <-
                state.State
                |> Seq.fold (fun current event -> ApprovalRequest.UpdateDto event current) Grace.Types.Webhooks.ApprovalRequest.Default

            Task.CompletedTask

        /// Replays persisted ApprovalRequest events into an in-memory state snapshot.
        member private this.ApplyEvents(events: ApprovalRequestEvent list) =
            task {
                if isNull state.State then state.State <- List<ApprovalRequestEvent>()

                state.State.AddRange(events)
                do! state.WriteStateAsync()
                request <- applyEvents events request

                let mutable index = 0

                while index < events.Length do
                    let approvalRequestEvent = events[index]
                    do! publishGraceEvent (GraceEvent.ApprovalRequestEvent approvalRequestEvent) approvalRequestEvent.Metadata
                    index <- index + 1
            }

        /// Runs ApprovalRequest command decisions, applies emitted events, and persists the result.
        member private this.ProcessCommand (command: ApprovalRequestCommand) (metadata: EventMetadata) =
            task {
                this.correlationId <- metadata.CorrelationId
                RequestContext.Set(CurrentCommandProperty, commandName command)

                let currentRequest =
                    if isNull (box request) then
                        Grace.Types.Webhooks.ApprovalRequest.Default
                    else
                        request

                match decideCommand currentRequest command metadata with
                | Ok decision ->
                    if not decision.Events.IsEmpty then do! this.ApplyEvents decision.Events

                    let returnValue =
                        (GraceReturnValue.Create decision metadata.CorrelationId)
                            .enhance(nameof ApprovalRequestId, decision.Request.ApprovalRequestId)
                            .enhance(nameof RepositoryId, decision.Request.Scope.RepositoryId)
                            .enhance (nameof ApprovalRequestStatus, decision.Request.Status)

                    return Ok returnValue
                | Error error ->
                    log.LogWarning(
                        "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Rejected ApprovalRequest command {Command}. Error: {Error}",
                        getCurrentInstantExtended (),
                        getMachineName,
                        metadata.CorrelationId,
                        commandName command,
                        error.Error
                    )

                    return Error error
            }

        interface IHasRepositoryId with
            /// Returns the repository id recorded in this ApprovalRequest actor state.
            member this.GetRepositoryId correlationId =
                this.correlationId <- correlationId
                request.Scope.RepositoryId |> returnTask

        interface IApprovalRequestActor with
            /// Reports whether this ApprovalRequest actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (request.ApprovalRequestId
                 <> ApprovalRequestId.Empty)
                |> returnTask

            /// Returns the current ApprovalRequest actor state snapshot.
            member this.Get correlationId =
                this.correlationId <- correlationId

                if request.ApprovalRequestId = ApprovalRequestId.Empty then
                    None
                else
                    Some request
                |> returnTask

            /// Serializes the current ApprovalRequest actor state snapshot as JSON.
            member this.GetJson correlationId =
                this.correlationId <- correlationId

                if request.ApprovalRequestId = ApprovalRequestId.Empty then
                    None
                else
                    Some(serialize request)
                |> returnTask

            /// Returns the persisted ApprovalRequest event stream for replay or audit.
            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                (state.State :> IReadOnlyList<ApprovalRequestEvent>)
                |> returnTask

            /// Serializes the approval-request event history and current state as JSON.
            member this.GetHistoryJson correlationId =
                this.correlationId <- correlationId

                state.State
                |> Seq.filter (fun event -> isNull (box event) |> not)
                |> Seq.filter (fun event -> isNull (box event.Event) |> not)
                |> Seq.scan (fun current event -> ApprovalRequest.UpdateDto event current) Grace.Types.Webhooks.ApprovalRequest.Default
                |> Seq.skip 1
                |> Seq.toArray
                |> serialize
                |> returnTask

            /// Builds the stable Orleans grain key used to address a ApprovalRequest actor.
            member this.Create request metadata = this.ProcessCommand (ApprovalRequestCommand.Create request) metadata

            /// Creates an approval request using server-generated request and operation identifiers.
            member this.CreateGenerated
                (
                    approvalRequestId,
                    approvalPolicyId,
                    approvalPolicyVersion,
                    subject,
                    ownerId,
                    organizationId,
                    repositoryId,
                    targetBranchId,
                    promotionSetId,
                    stepsComputationAttempt,
                    requiredResponder,
                    createdBy,
                    metadata
                ) =
                let request =
                    { Grace.Types.Webhooks.ApprovalRequest.Default with
                        ApprovalRequestId = approvalRequestId
                        ApprovalPolicyId = approvalPolicyId
                        ApprovalPolicyVersion = approvalPolicyVersion
                        Subject = subject
                        Scope =
                            { ApprovalScope.Default with
                                OwnerId = ownerId
                                OrganizationId = organizationId
                                RepositoryId = repositoryId
                                TargetBranchId = targetBranchId
                                PromotionSetId = promotionSetId
                                StepsComputationAttempt = stepsComputationAttempt
                                ApprovalPolicyId = Some approvalPolicyId
                                ApprovalPolicyVersion = Some approvalPolicyVersion
                            }
                        RequiredResponder = requiredResponder
                        Status = ApprovalRequestStatus.Pending
                        CreatedBy = UserId createdBy
                        CreatedAt = metadata.Timestamp
                    }

                this.ProcessCommand (ApprovalRequestCommand.Create request) metadata

            /// Records an approval decision through the request actor idempotency path.
            member this.RecordDecision decision metadata = this.ProcessCommand (ApprovalRequestCommand.RecordDecision decision) metadata

            /// Records an approval decision using a generated decision id when the caller omitted one.
            member this.RecordDecisionGenerated(decision, decidedBy, reason, clientDecisionId, metadata) =
                match decision with
                | value when String.Equals(value, nameof ApprovalDecision.Approve, StringComparison.OrdinalIgnoreCase) ->
                    let requestDecision =
                        { ApprovalRequestDecision.Default with
                            Decision = ApprovalDecision.Approve
                            DecidedBy = UserId decidedBy
                            DecidedAt = metadata.Timestamp
                            Reason = reason
                            ClientDecisionId = clientDecisionId
                        }

                    this.ProcessCommand (ApprovalRequestCommand.RecordDecision requestDecision) metadata
                | value when String.Equals(value, nameof ApprovalDecision.Reject, StringComparison.OrdinalIgnoreCase) ->
                    let requestDecision =
                        { ApprovalRequestDecision.Default with
                            Decision = ApprovalDecision.Reject
                            DecidedBy = UserId decidedBy
                            DecidedAt = metadata.Timestamp
                            Reason = reason
                            ClientDecisionId = clientDecisionId
                        }

                    this.ProcessCommand (ApprovalRequestCommand.RecordDecision requestDecision) metadata
                | _ ->
                    GraceError.Create $"Approval decision '{decision}' is not valid." metadata.CorrelationId
                    |> Error
                    |> returnTask

            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata = this.ProcessCommand command metadata

    /// Implements the Orleans grain for approval request index actor.
    type ApprovalRequestIndexActor
        (
            [<PersistentState(StateName.ApprovalRequestIndex, GraceActorStorage)>] state: IPersistentState<List<ApprovalRequestIndexEvent>>
        ) =
        inherit Grain()

        let mutable requestIds = HashSet<ApprovalRequestId>()
        let mutable requests = Dictionary<ApprovalRequestId, ApprovalRequest>()
        let mutable requestJsonById = Dictionary<ApprovalRequestId, string>()
        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override _.OnActivateAsync(ct) =
            if isNull state.State then state.State <- List<ApprovalRequestIndexEvent>()

            requestIds <- HashSet<ApprovalRequestId>()
            requests <- Dictionary<ApprovalRequestId, ApprovalRequest>()
            requestJsonById <- Dictionary<ApprovalRequestId, string>()

            state.State
            |> Seq.iter (fun indexEvent ->
                if isNull (box indexEvent) |> not
                   && isNull (box indexEvent.Event) |> not then
                    match indexEvent.Event with
                    | ApprovalRequestIndexEventType.RequestAdded approvalRequestId -> requestIds.Add approvalRequestId |> ignore
                    | ApprovalRequestIndexEventType.RequestIndexed request ->
                        if isNull (box request) |> not
                           && request.ApprovalRequestId
                              <> ApprovalRequestId.Empty then
                            requestIds.Add request.ApprovalRequestId |> ignore
                            requests[request.ApprovalRequestId] <- request
                            requestJsonById[request.ApprovalRequestId] <- serialize request
                    | ApprovalRequestIndexEventType.RequestIndexedJson requestJson ->
                        if String.IsNullOrWhiteSpace requestJson |> not then
                            let request = deserialize<ApprovalRequest> requestJson

                            if request.ApprovalRequestId
                               <> ApprovalRequestId.Empty then
                                requestIds.Add request.ApprovalRequestId |> ignore
                                requests[request.ApprovalRequestId] <- request
                                requestJsonById[request.ApprovalRequestId] <- requestJson)

            Task.CompletedTask

        /// Adds an approval request id to the scope index if it is not already tracked.
        member private this.AddRequestCore (approvalRequestId: ApprovalRequestId) (eventMetadata: EventMetadata) =
            task {
                this.correlationId <- eventMetadata.CorrelationId

                if approvalRequestId = ApprovalRequestId.Empty then
                    return Error(GraceError.Create "ApprovalRequestId must be a non-empty Guid." eventMetadata.CorrelationId)
                elif requestIds.Contains approvalRequestId then
                    let returnValue = GraceReturnValue.Create (requestIds |> Seq.toArray) eventMetadata.CorrelationId
                    return Ok returnValue
                else
                    let event: ApprovalRequestIndexEvent = { Event = ApprovalRequestIndexEventType.RequestAdded approvalRequestId; Metadata = eventMetadata }
                    if isNull state.State then state.State <- List<ApprovalRequestIndexEvent>()

                    state.State.Add event
                    do! state.WriteStateAsync()
                    requestIds.Add approvalRequestId |> ignore
                    let returnValue = GraceReturnValue.Create (requestIds |> Seq.toArray) eventMetadata.CorrelationId
                    return Ok returnValue
            }

        /// Registers an approval request in the scope index while preserving retry idempotency.
        member private this.RegisterRequestCore (request: ApprovalRequest) (eventMetadata: EventMetadata) =
            task {
                this.correlationId <- eventMetadata.CorrelationId

                if
                    isNull (box request)
                    || request.ApprovalRequestId = ApprovalRequestId.Empty
                then
                    return Error(GraceError.Create "ApprovalRequestId must be a non-empty Guid." eventMetadata.CorrelationId)
                elif requests.ContainsKey request.ApprovalRequestId
                     && requests[request.ApprovalRequestId] = request then
                    let returnValue = GraceReturnValue.Create (requestIds |> Seq.toArray) eventMetadata.CorrelationId
                    return Ok returnValue
                else
                    let requestJson = serialize request
                    let event: ApprovalRequestIndexEvent = { Event = ApprovalRequestIndexEventType.RequestIndexedJson requestJson; Metadata = eventMetadata }
                    if isNull state.State then state.State <- List<ApprovalRequestIndexEvent>()

                    state.State.Add event
                    do! state.WriteStateAsync()
                    requestIds.Add request.ApprovalRequestId |> ignore
                    requests[request.ApprovalRequestId] <- request
                    requestJsonById[request.ApprovalRequestId] <- requestJson
                    let returnValue = GraceReturnValue.Create (requestIds |> Seq.toArray) eventMetadata.CorrelationId
                    return Ok returnValue
            }

        interface IApprovalRequestIndexActor with
            /// Routes a public actor command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                task {
                    this.correlationId <- metadata.CorrelationId

                    match command with
                    | ApprovalRequestIndexCommand.AddRequest approvalRequestId -> return! this.AddRequestCore approvalRequestId metadata
                }

            /// Adds an existing approval request id to the approval-scope index.
            member this.AddRequest(approvalRequestId, eventMetadata) = this.AddRequestCore approvalRequestId eventMetadata

            /// Registers an approval request object with the approval-scope index.
            member this.RegisterRequest(request, eventMetadata) = this.RegisterRequestCore request eventMetadata

            /// Registers an approval request in the scope index using generated metadata.
            member this.RegisterGeneratedRequest
                (
                    approvalRequestId,
                    approvalPolicyId,
                    approvalPolicyVersion,
                    subject,
                    ownerId,
                    organizationId,
                    repositoryId,
                    targetBranchId,
                    promotionSetId,
                    stepsComputationAttempt,
                    requiredResponder,
                    createdBy,
                    eventMetadata
                ) =
                let request =
                    { Grace.Types.Webhooks.ApprovalRequest.Default with
                        ApprovalRequestId = approvalRequestId
                        ApprovalPolicyId = approvalPolicyId
                        ApprovalPolicyVersion = approvalPolicyVersion
                        Subject = subject
                        Scope =
                            { ApprovalScope.Default with
                                OwnerId = ownerId
                                OrganizationId = organizationId
                                RepositoryId = repositoryId
                                TargetBranchId = targetBranchId
                                PromotionSetId = promotionSetId
                                StepsComputationAttempt = stepsComputationAttempt
                                ApprovalPolicyId = Some approvalPolicyId
                                ApprovalPolicyVersion = Some approvalPolicyVersion
                            }
                        RequiredResponder = requiredResponder
                        Status = ApprovalRequestStatus.Pending
                        CreatedBy = UserId createdBy
                        CreatedAt = eventMetadata.Timestamp
                    }

                this.RegisterRequestCore request eventMetadata

            /// Loads an approval request from its id through the scoped index.
            member this.GetRequest approvalRequestId correlationId =
                this.correlationId <- correlationId

                match requests.TryGetValue approvalRequestId with
                | true, request -> Some request
                | _ -> None
                |> returnTask

            /// Loads an approval request from the index and serializes it as JSON.
            member this.GetRequestJson approvalRequestId correlationId =
                this.correlationId <- correlationId

                match requestJsonById.TryGetValue approvalRequestId with
                | true, requestJson -> Some requestJson
                | _ -> None
                |> returnTask

            /// Returns the ApprovalRequest records tracked by this actor.
            member this.List correlationId =
                this.correlationId <- correlationId
                requestIds |> Seq.toArray |> returnTask
