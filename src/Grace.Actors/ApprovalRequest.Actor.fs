namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared
open Grace.Shared.Constants
open Grace.Shared.Utilities
open Grace.Types.Events
open Grace.Types.Types
open Grace.Types.Webhooks
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

module ApprovalRequest =

    let private commandName command =
        match command with
        | ApprovalRequestCommand.Create _ -> "Create"
        | ApprovalRequestCommand.RecordDecision _ -> "RecordDecision"
        | ApprovalRequestCommand.Expire -> "Expire"
        | ApprovalRequestCommand.Cancel -> "Cancel"
        | ApprovalRequestCommand.Supersede _ -> "Supersede"

    let private applyEvents (events: ApprovalRequestEvent list) (request: ApprovalRequest) =
        events
        |> List.fold (fun current event -> ApprovalRequest.UpdateDto event current) request

    let private graceError correlationId message = GraceError.Create message correlationId

    let private createdRequestMatches (existing: ApprovalRequest) (candidate: ApprovalRequest) =
        existing.ApprovalPolicyId = candidate.ApprovalPolicyId
        && existing.ApprovalPolicyVersion = candidate.ApprovalPolicyVersion
        && existing.Subject = candidate.Subject
        && existing.Scope = candidate.Scope
        && existing.RequiredResponder = candidate.RequiredResponder

    let private decisionMatches (existing: ApprovalRequestDecision) (candidate: ApprovalRequestDecision) =
        existing.Decision = candidate.Decision
        && existing.DecidedBy = candidate.DecidedBy
        && existing.Reason = candidate.Reason
        && existing.ClientDecisionId = candidate.ClientDecisionId

    let private ok (request: ApprovalRequest) (events: ApprovalRequestEvent list) wasReplay message =
        Ok { Request = applyEvents events request; Events = events; WasIdempotentReplay = wasReplay; Message = message }

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

    type ApprovalRequestActor([<PersistentState(StateName.ApprovalRequest, GraceActorStorage)>] state: IPersistentState<List<ApprovalRequestEvent>>) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("ApprovalRequest.Actor")
        let mutable request = Grace.Types.Webhooks.ApprovalRequest.Default
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            if isNull state.State then state.State <- List<ApprovalRequestEvent>()

            request <-
                state.State
                |> Seq.fold (fun current event -> ApprovalRequest.UpdateDto event current) Grace.Types.Webhooks.ApprovalRequest.Default

            Task.CompletedTask

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
            member this.GetRepositoryId correlationId =
                this.correlationId <- correlationId
                request.Scope.RepositoryId |> returnTask

        interface IApprovalRequestActor with
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (request.ApprovalRequestId
                 <> ApprovalRequestId.Empty)
                |> returnTask

            member this.Get correlationId =
                this.correlationId <- correlationId

                if request.ApprovalRequestId = ApprovalRequestId.Empty then
                    None
                else
                    Some request
                |> returnTask

            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                (state.State :> IReadOnlyList<ApprovalRequestEvent>)
                |> returnTask

            member this.Create request metadata = this.ProcessCommand (ApprovalRequestCommand.Create request) metadata

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

            member this.RecordDecision decision metadata = this.ProcessCommand (ApprovalRequestCommand.RecordDecision decision) metadata

            member this.RecordDecisionGenerated(decision, decidedBy, reason, clientDecisionId, metadata) =
                let requestDecision =
                    { ApprovalRequestDecision.Default with
                        Decision = decision
                        DecidedBy = UserId decidedBy
                        DecidedAt = metadata.Timestamp
                        Reason = reason
                        ClientDecisionId = clientDecisionId
                    }

                this.ProcessCommand (ApprovalRequestCommand.RecordDecision requestDecision) metadata

            member this.Handle command metadata = this.ProcessCommand command metadata

    type ApprovalRequestIndexActor
        (
            [<PersistentState(StateName.ApprovalRequestIndex, GraceActorStorage)>] state: IPersistentState<List<ApprovalRequestIndexEvent>>
        ) =
        inherit Grain()

        let mutable requestIds = HashSet<ApprovalRequestId>()
        member val private correlationId: CorrelationId = String.Empty with get, set

        override _.OnActivateAsync(ct) =
            if isNull state.State then state.State <- List<ApprovalRequestIndexEvent>()

            requestIds <- HashSet<ApprovalRequestId>()

            state.State
            |> Seq.iter (fun indexEvent ->
                match indexEvent.Event with
                | ApprovalRequestIndexEventType.RequestAdded approvalRequestId -> requestIds.Add approvalRequestId |> ignore)

            Task.CompletedTask

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

        interface IApprovalRequestIndexActor with
            member this.Handle command metadata =
                task {
                    this.correlationId <- metadata.CorrelationId

                    match command with
                    | ApprovalRequestIndexCommand.AddRequest approvalRequestId -> return! this.AddRequestCore approvalRequestId metadata
                }

            member this.AddRequest(approvalRequestId, eventMetadata) = this.AddRequestCore approvalRequestId eventMetadata

            member this.List correlationId =
                this.correlationId <- correlationId
                requestIds |> Seq.toArray |> returnTask
