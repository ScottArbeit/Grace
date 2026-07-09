namespace Grace.Actors

open Grace.Actors.Constants
open Grace.Actors.Context
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Shared.Utilities
open Grace.Types.Common
open Grace.Types.ContentOwnershipLedger
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open System
open System.Collections.Generic
open System.Threading.Tasks

/// Groups Orleans actor helpers for manifest-backed content ownership ledger keys and transitions.
module ContentOwnershipLedger =

    /// Coordinates primary key logic for the ContentOwnershipLedger actor.
    let primaryKey (storagePoolId: StoragePoolId) (manifestAddress: ManifestAddress) = $"{storagePoolId}|{manifestAddress}"

    /// Maps a ContentOwnershipLedger command case to the operation name used in idempotency and diagnostics.
    let commandName command =
        match command with
        | ContentOwnershipLedgerCommand.AddActiveUsage _ -> "AddActiveUsage"
        | ContentOwnershipLedgerCommand.RemoveActiveUsage _ -> "RemoveActiveUsage"
        | ContentOwnershipLedgerCommand.TransferAcceptedContent _ -> "TransferAcceptedContent"

    /// Extracts the stable operation id that lets command retries match previously emitted events.
    let operationId command =
        match command with
        | ContentOwnershipLedgerCommand.AddActiveUsage (operationId, _, _, _, _, _) -> operationId
        | ContentOwnershipLedgerCommand.RemoveActiveUsage (operationId, _, _, _, _) -> operationId
        | ContentOwnershipLedgerCommand.TransferAcceptedContent (operationId, _, _, _, _, _, _) -> operationId

    /// Coordinates command target logic for the ContentOwnershipLedger actor.
    let private commandTarget command =
        match command with
        | ContentOwnershipLedgerCommand.AddActiveUsage (_, repositoryId, storagePoolId, manifestAddress, _, _)
        | ContentOwnershipLedgerCommand.RemoveActiveUsage (_, repositoryId, storagePoolId, manifestAddress, _)
        | ContentOwnershipLedgerCommand.TransferAcceptedContent (_, repositoryId, storagePoolId, manifestAddress, _, _, _) ->
            repositoryId, storagePoolId, manifestAddress

    /// Coordinates event operation id logic for the ContentOwnershipLedger actor.
    let private eventOperationId ledgerEvent =
        match ledgerEvent.Event with
        | ContentOwnershipLedgerEventType.ActiveUsageAdded added -> added.OperationId
        | ContentOwnershipLedgerEventType.ActiveUsageRemoved removed -> removed.OperationId
        | ContentOwnershipLedgerEventType.AcceptedContentTransferred transferred -> transferred.OperationId

    /// Coordinates event command name logic for the ContentOwnershipLedger actor.
    let private eventCommandName ledgerEvent =
        match ledgerEvent.Event with
        | ContentOwnershipLedgerEventType.ActiveUsageAdded _ -> "AddActiveUsage"
        | ContentOwnershipLedgerEventType.ActiveUsageRemoved _ -> "RemoveActiveUsage"
        | ContentOwnershipLedgerEventType.AcceptedContentTransferred _ -> "TransferAcceptedContent"

    /// Builds the add-active-usage payload so replay comparisons ignore transient metadata.
    let private addedPayload operationId repositoryId storagePoolId manifestAddress activeUsageId ownerScope =
        {
            OperationId = operationId
            RepositoryId = repositoryId
            StoragePoolId = storagePoolId
            ManifestAddress = manifestAddress
            ActiveUsageId = activeUsageId
            OwnerScope = ownerScope
        }

    /// Builds the remove-active-usage payload so replay comparisons ignore transient metadata.
    let private removedPayload operationId repositoryId storagePoolId manifestAddress activeUsageId removedOwnerScope =
        {
            OperationId = operationId
            RepositoryId = repositoryId
            StoragePoolId = storagePoolId
            ManifestAddress = manifestAddress
            ActiveUsageId = activeUsageId
            RemovedOwnerScope = removedOwnerScope
        }

    /// Builds the accepted-transfer payload so replay comparisons reject operation-id reuse with different evidence.
    let private transferredPayload operationId repositoryId storagePoolId manifestAddress sourceOwnerScope targetOwnerScope acceptedEvidence movedUsageIds =
        {
            OperationId = operationId
            RepositoryId = repositoryId
            StoragePoolId = storagePoolId
            ManifestAddress = manifestAddress
            SourceOwnerScope = sourceOwnerScope
            TargetOwnerScope = targetOwnerScope
            AcceptedEvidence = acceptedEvidence
            TransferredActiveUsageIds = movedUsageIds
        }

    /// Checks whether an already-persisted event represents the same command payload.
    let private eventMatchesCommand ledgerEvent command =
        match ledgerEvent.Event, command with
        | ContentOwnershipLedgerEventType.ActiveUsageAdded existing,
          ContentOwnershipLedgerCommand.AddActiveUsage (operationId, repositoryId, storagePoolId, manifestAddress, activeUsageId, ownerScope) ->
            existing = addedPayload operationId repositoryId storagePoolId manifestAddress activeUsageId ownerScope
        | ContentOwnershipLedgerEventType.ActiveUsageRemoved existing,
          ContentOwnershipLedgerCommand.RemoveActiveUsage (operationId, repositoryId, storagePoolId, manifestAddress, activeUsageId) ->
            existing.OperationId = operationId
            && existing.RepositoryId = repositoryId
            && existing.StoragePoolId = storagePoolId
            && existing.ManifestAddress = manifestAddress
            && existing.ActiveUsageId = activeUsageId
        | ContentOwnershipLedgerEventType.AcceptedContentTransferred existing,
          ContentOwnershipLedgerCommand.TransferAcceptedContent (operationId,
                                                                 repositoryId,
                                                                 storagePoolId,
                                                                 manifestAddress,
                                                                 sourceOwnerScope,
                                                                 targetOwnerScope,
                                                                 acceptedEvidence) ->
            existing.OperationId = operationId
            && existing.RepositoryId = repositoryId
            && existing.StoragePoolId = storagePoolId
            && existing.ManifestAddress = manifestAddress
            && existing.SourceOwnerScope = sourceOwnerScope
            && existing.TargetOwnerScope = targetOwnerScope
            && existing.AcceptedEvidence = acceptedEvidence
        | _ -> false

    /// Attempts to find an applied operation in the durable event stream.
    let private tryFindAppliedOperation (events: seq<ContentOwnershipLedgerEvent>) operationId =
        events
        |> Seq.tryFind (fun ledgerEvent -> eventOperationId ledgerEvent = operationId)

    /// Applies event changes to the ContentOwnershipLedger actor state.
    let private applyEvents (events: ContentOwnershipLedgerEvent list) (ledger: ContentOwnershipLedgerDto) =
        events
        |> List.fold (fun current event -> ContentOwnershipLedgerDto.UpdateDto event current) ledger

    /// Coordinates ok decision logic for the ContentOwnershipLedger actor.
    let private okDecision ledger operationId events wasReplay message =
        Ok { Ledger = applyEvents events ledger; OperationId = operationId; Events = events; WasIdempotentReplay = wasReplay; Message = message }

    /// Coordinates Grace error logic for the ContentOwnershipLedger actor.
    let private graceError correlationId message = GraceError.Create message correlationId

    /// Coordinates target mismatch logic for the ContentOwnershipLedger actor.
    let private targetMismatch (ledger: ContentOwnershipLedgerDto) storagePoolId manifestAddress =
        (not (String.IsNullOrWhiteSpace ledger.StoragePoolId)
         && ledger.StoragePoolId <> storagePoolId)
        || (not (String.IsNullOrWhiteSpace ledger.ManifestAddress)
            && ledger.ManifestAddress <> manifestAddress)

    /// Coordinates expected primary key mismatch logic for the ContentOwnershipLedger actor.
    let private expectedPrimaryKeyMismatch expectedPrimaryKey storagePoolId manifestAddress =
        match expectedPrimaryKey with
        | Some expectedPrimaryKey -> not (String.Equals(expectedPrimaryKey, primaryKey storagePoolId manifestAddress, StringComparison.Ordinal))
        | None -> false

    /// Validates owner-scope fields before recording a usage or transfer decision.
    let private validateOwnerScope correlationId repositoryId ownerScope =
        match ownerScope with
        | ContentOwnershipOwnerScope.RepositoryOwned ownerRepositoryId when ownerRepositoryId = RepositoryId.Empty ->
            Some(graceError correlationId "ContentOwnershipLedger repository-owned scope requires a non-empty RepositoryId.")
        | ContentOwnershipOwnerScope.RepositoryOwned ownerRepositoryId when ownerRepositoryId <> repositoryId ->
            Some(graceError correlationId "ContentOwnershipLedger owner scope RepositoryId must match the command RepositoryId.")
        | ContentOwnershipOwnerScope.ContributorOwned (ownerRepositoryId, _) when ownerRepositoryId = RepositoryId.Empty ->
            Some(graceError correlationId "ContentOwnershipLedger contributor-owned scope requires a non-empty RepositoryId.")
        | ContentOwnershipOwnerScope.ContributorOwned (ownerRepositoryId, _) when ownerRepositoryId <> repositoryId ->
            Some(graceError correlationId "ContentOwnershipLedger owner scope RepositoryId must match the command RepositoryId.")
        | ContentOwnershipOwnerScope.ContributorOwned (_, creatorUserId) when String.IsNullOrWhiteSpace creatorUserId ->
            Some(graceError correlationId "ContentOwnershipLedger contributor-owned scope requires a non-empty creator user id.")
        | _ -> None

    /// Validates common target fields before recording a usage or transfer decision.
    let private validateTarget expectedPrimaryKey ledger repositoryId storagePoolId manifestAddress correlationId =
        if repositoryId = RepositoryId.Empty then
            Some(graceError correlationId "ContentOwnershipLedger command requires a non-empty RepositoryId.")
        elif String.IsNullOrWhiteSpace storagePoolId then
            Some(graceError correlationId "ContentOwnershipLedger command requires a non-empty StoragePoolId.")
        elif String.IsNullOrWhiteSpace manifestAddress then
            Some(graceError correlationId "ContentOwnershipLedger command requires a non-empty ManifestAddress.")
        elif expectedPrimaryKeyMismatch expectedPrimaryKey storagePoolId manifestAddress then
            Some(graceError correlationId "ContentOwnershipLedger command target does not match the grain key.")
        elif targetMismatch ledger storagePoolId manifestAddress then
            Some(graceError correlationId "ContentOwnershipLedger command target does not match the initialized ledger.")
        else
            None

    /// Validates accepted transfer evidence before recording a transfer decision.
    let private validateAcceptedEvidence correlationId evidence =
        if evidence.PromotionSetId = PromotionSetId.Empty then
            Some(graceError correlationId "ContentOwnershipLedger accepted transfer requires a non-empty PromotionSetId.")
        elif evidence.PromotionSetStepId = PromotionSetStepId.Empty then
            Some(graceError correlationId "ContentOwnershipLedger accepted transfer requires a non-empty PromotionSetStepId.")
        elif evidence.StepsComputationAttempt <= 0 then
            Some(graceError correlationId "ContentOwnershipLedger accepted transfer requires a positive steps computation attempt.")
        elif evidence.AppliedDirectoryVersionId = DirectoryVersionId.Empty then
            Some(graceError correlationId "ContentOwnershipLedger accepted transfer requires a non-empty applied DirectoryVersionId.")
        else
            None

    /// Reuses durable accepted-transfer evidence so future contributor references stay repository-owned.
    let private commandWithDurableAcceptedOwner events command =
        match command with
        | ContentOwnershipLedgerCommand.AddActiveUsage (operationId,
                                                        repositoryId,
                                                        storagePoolId,
                                                        manifestAddress,
                                                        activeUsageId,
                                                        ContentOwnershipOwnerScope.ContributorOwned _) ->
            let repositoryOwnerScope = ContentOwnershipOwnerScope.RepositoryOwned repositoryId

            let hasAcceptedRepositoryTransfer =
                events
                |> Seq.exists (fun ledgerEvent ->
                    match ledgerEvent.Event with
                    | ContentOwnershipLedgerEventType.AcceptedContentTransferred transferred ->
                        transferred.RepositoryId = repositoryId
                        && transferred.StoragePoolId = storagePoolId
                        && transferred.ManifestAddress = manifestAddress
                        && transferred.TargetOwnerScope = repositoryOwnerScope
                    | _ -> false)

            if hasAcceptedRepositoryTransfer then
                ContentOwnershipLedgerCommand.AddActiveUsage(operationId, repositoryId, storagePoolId, manifestAddress, activeUsageId, repositoryOwnerScope)
            else
                command
        | _ -> command

    let decideCommandForKey
        (expectedPrimaryKey: string option)
        (events: seq<ContentOwnershipLedgerEvent>)
        (ledger: ContentOwnershipLedgerDto)
        (command: ContentOwnershipLedgerCommand)
        (metadata: EventMetadata)
        : Result<ContentOwnershipLedgerDecision, GraceError>
        =
        let command = commandWithDurableAcceptedOwner events command
        let operationId = operationId command
        let repositoryId, storagePoolId, manifestAddress = commandTarget command

        if String.IsNullOrWhiteSpace operationId then
            Error(graceError metadata.CorrelationId "ContentOwnershipLedger command requires a non-empty operation id.")
        else
            match tryFindAppliedOperation events operationId with
            | Some ledgerEvent when
                eventCommandName ledgerEvent
                <> commandName command
                ->
                Error(graceError metadata.CorrelationId "ContentOwnershipLedger operation id was already used for a different command.")
            | Some ledgerEvent when not (eventMatchesCommand ledgerEvent command) ->
                Error(graceError metadata.CorrelationId "ContentOwnershipLedger operation id was already used with a different payload.")
            | Some _ -> okDecision ledger operationId [] true "Content ownership ledger command replayed."
            | None ->
                match validateTarget expectedPrimaryKey ledger repositoryId storagePoolId manifestAddress metadata.CorrelationId with
                | Some error -> Error error
                | None ->
                    match command with
                    | ContentOwnershipLedgerCommand.AddActiveUsage (operationId, repositoryId, storagePoolId, manifestAddress, activeUsageId, ownerScope) ->
                        if String.IsNullOrWhiteSpace activeUsageId then
                            Error(graceError metadata.CorrelationId "ContentOwnershipLedger add command requires a non-empty active usage id.")
                        else
                            match validateOwnerScope metadata.CorrelationId repositoryId ownerScope with
                            | Some error -> Error error
                            | None ->
                                let mutable existingOwnerScope = Unchecked.defaultof<ContentOwnershipOwnerScope>

                                if ledger.ActiveUsageOwners.TryGetValue(activeUsageId, &existingOwnerScope) then
                                    if existingOwnerScope = ownerScope then
                                        Error(
                                            graceError
                                                metadata.CorrelationId
                                                "ContentOwnershipLedger active usage id is already present for a different operation id."
                                        )
                                    else
                                        Error(
                                            graceError
                                                metadata.CorrelationId
                                                "ContentOwnershipLedger active usage id is already owned by a different owner scope."
                                        )
                                else
                                    let ledgerEvent =
                                        {
                                            Event =
                                                ContentOwnershipLedgerEventType.ActiveUsageAdded(
                                                    addedPayload operationId repositoryId storagePoolId manifestAddress activeUsageId ownerScope
                                                )
                                            Metadata = metadata
                                        }

                                    okDecision ledger operationId [ ledgerEvent ] false "Content ownership active usage added."
                    | ContentOwnershipLedgerCommand.RemoveActiveUsage (operationId, repositoryId, storagePoolId, manifestAddress, activeUsageId) ->
                        if String.IsNullOrWhiteSpace activeUsageId then
                            Error(graceError metadata.CorrelationId "ContentOwnershipLedger remove command requires a non-empty active usage id.")
                        else
                            let mutable ownerScope = Unchecked.defaultof<ContentOwnershipOwnerScope>

                            if ledger.ActiveUsageOwners.TryGetValue(activeUsageId, &ownerScope) then
                                let ledgerEvent =
                                    {
                                        Event =
                                            ContentOwnershipLedgerEventType.ActiveUsageRemoved(
                                                removedPayload operationId repositoryId storagePoolId manifestAddress activeUsageId ownerScope
                                            )
                                        Metadata = metadata
                                    }

                                okDecision ledger operationId [ ledgerEvent ] false "Content ownership active usage removed."
                            else
                                Error(graceError metadata.CorrelationId "ContentOwnershipLedger active usage id is not present.")
                    | ContentOwnershipLedgerCommand.TransferAcceptedContent (operationId,
                                                                             repositoryId,
                                                                             storagePoolId,
                                                                             manifestAddress,
                                                                             sourceOwnerScope,
                                                                             targetOwnerScope,
                                                                             acceptedEvidence) ->
                        match validateOwnerScope metadata.CorrelationId repositoryId sourceOwnerScope with
                        | Some error -> Error error
                        | None ->
                            match validateOwnerScope metadata.CorrelationId repositoryId targetOwnerScope with
                            | Some error -> Error error
                            | None ->
                                match validateAcceptedEvidence metadata.CorrelationId acceptedEvidence with
                                | Some error -> Error error
                                | None when sourceOwnerScope = targetOwnerScope ->
                                    Error(graceError metadata.CorrelationId "ContentOwnershipLedger accepted transfer requires different owner scopes.")
                                | None ->
                                    let movedUsageIds =
                                        ledger.ActiveUsageOwners
                                        |> Seq.choose (fun entry -> if entry.Value = sourceOwnerScope then Some entry.Key else None)
                                        |> Seq.sort
                                        |> Seq.toArray

                                    let ledgerEvent =
                                        {
                                            Event =
                                                ContentOwnershipLedgerEventType.AcceptedContentTransferred(
                                                    transferredPayload
                                                        operationId
                                                        repositoryId
                                                        storagePoolId
                                                        manifestAddress
                                                        sourceOwnerScope
                                                        targetOwnerScope
                                                        acceptedEvidence
                                                        movedUsageIds
                                                )
                                            Metadata = metadata
                                        }

                                    okDecision ledger operationId [ ledgerEvent ] false "Content ownership accepted transfer recorded."

    /// Validates a ContentOwnershipLedger command and derives the events needed for a state transition.
    let decideCommand events ledger command metadata = decideCommandForKey None events ledger command metadata

    /// Implements the Orleans grain for content ownership ledger actor.
    type ContentOwnershipLedgerActor
        (
            [<PersistentState(StateName.ContentOwnershipLedger, Grace.Shared.Constants.GraceActorStorage)>] state: IPersistentState<List<ContentOwnershipLedgerEvent>>
        ) =
        inherit Grain()

        let log = loggerFactory.CreateLogger("ContentOwnershipLedger.Actor")
        let mutable ledger = ContentOwnershipLedgerDto.Default

        /// Stores the correlation id used by this actor while reporting timings and errors.
        member val private correlationId: CorrelationId = String.Empty with get, set

        override this.OnActivateAsync(ct) =
            let activateStartTime = getCurrentInstant ()
            logActorActivation log this.IdentityString activateStartTime (getActorActivationMessage state.RecordExists)

            ledger <-
                state.State
                |> Seq.fold (fun dto event -> ContentOwnershipLedgerDto.UpdateDto event dto) ContentOwnershipLedgerDto.Default

            Task.CompletedTask

        /// Replays persisted ContentOwnershipLedger events into an in-memory state snapshot.
        member private this.ApplyEvents(events: ContentOwnershipLedgerEvent list) =
            task {
                state.State.AddRange(events)
                do! state.WriteStateAsync()
                ledger <- applyEvents events ledger
            }

        interface IContentOwnershipLedgerActor with
            /// Reports whether this ContentOwnershipLedger actor has persisted state.
            member this.Exists correlationId =
                this.correlationId <- correlationId

                (not (String.IsNullOrWhiteSpace ledger.StoragePoolId)
                 && not (String.IsNullOrWhiteSpace ledger.ManifestAddress))
                |> returnTask

            /// Returns the current ContentOwnershipLedger actor state snapshot.
            member this.Get correlationId =
                this.correlationId <- correlationId
                ledger |> returnTask

            /// Returns the persisted ContentOwnershipLedger event stream for replay or audit.
            member this.GetEvents correlationId =
                this.correlationId <- correlationId

                (state.State :> IReadOnlyList<ContentOwnershipLedgerEvent>)
                |> returnTask

            /// Routes a ledger command to the domain operation that validates and persists it.
            member this.Handle command metadata =
                task {
                    this.correlationId <- metadata.CorrelationId
                    RequestContext.Set(Grace.Shared.Constants.CurrentCommandProperty, commandName command)

                    match decideCommandForKey (Some(this.GetPrimaryKeyString())) state.State ledger command metadata with
                    | Ok decision ->
                        if not decision.Events.IsEmpty then do! this.ApplyEvents decision.Events

                        let returnValue =
                            (GraceReturnValue.Create decision metadata.CorrelationId)
                                .enhance(nameof StoragePoolId, decision.Ledger.StoragePoolId)
                                .enhance (nameof ManifestAddress, decision.Ledger.ManifestAddress)

                        return Ok returnValue
                    | Error error ->
                        log.LogWarning(
                            "{CurrentInstant}: Node: {HostName}; CorrelationId: {CorrelationId}; Rejected ContentOwnershipLedger command {Command}. Error: {Error}",
                            getCurrentInstantExtended (),
                            getMachineName,
                            metadata.CorrelationId,
                            commandName command,
                            error.Error
                        )

                        return Error error
                }
