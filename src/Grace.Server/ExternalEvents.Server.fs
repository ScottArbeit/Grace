namespace Grace.Server

open Grace.Shared.Utilities
open Grace.Types.Artifact
open Grace.Types.Branch
open Grace.Types.DirectoryVersion
open Grace.Types.Events
open Grace.Types.ExternalEvents
open Grace.Types.Organization
open Grace.Types.Owner
open Grace.Types.Policy
open Grace.Types.PromotionSet
open Grace.Types.Queue
open Grace.Types.Reference
open Grace.Types.Repository
open Grace.Types.Review
open Grace.Types.Types
open Grace.Types.Validation
open Grace.Types.WorkItem
open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

module ExternalEvents =

    type CanonicalBuildFailureReason =
        | MissingActorIdentity of string
        | MissingRequiredField of string
        | InvalidPayload of string
        | OversizeDocument of int

    type CanonicalBuildFailure = { RawEventCase: string; CorrelationId: CorrelationId; Reason: CanonicalBuildFailureReason }

    type CanonicalBuildOutcome =
        | Published of CanonicalExternalEventEnvelope
        | InternalOnly of string
        | Failed of CanonicalBuildFailure

    let private tryGetString (propertyName: string) (metadata: EventMetadata) : string option =
        match metadata.Properties.TryGetValue(propertyName) with
        | true, value when String.IsNullOrWhiteSpace value |> not -> Some value
        | _ -> Option.None

    let private tryGetGuid (propertyName: string) (metadata: EventMetadata) : Guid option =
        match tryGetString propertyName metadata with
        | Some value ->
            let mutable parsed = Guid.Empty

            if
                Guid.TryParse(value, &parsed)
                && parsed <> Guid.Empty
            then
                Some parsed
            else
                Option.None
        | Option.None -> Option.None

    let private actorGuidFromMetadata (metadata: EventMetadata) = tryGetGuid "ActorId" metadata

    let private ownerIdFromMetadata (metadata: EventMetadata) =
        tryGetGuid (nameof OwnerId) metadata
        |> Option.defaultValue OwnerId.Empty

    let private organizationIdFromMetadata (metadata: EventMetadata) =
        tryGetGuid (nameof OrganizationId) metadata
        |> Option.defaultValue OrganizationId.Empty

    let private repositoryIdFromMetadata (metadata: EventMetadata) =
        tryGetGuid (nameof RepositoryId) metadata
        |> Option.defaultValue RepositoryId.Empty

    let private branchIdOptionFromMetadata (metadata: EventMetadata) = tryGetGuid (nameof BranchId) metadata

    let private toKebabCase (value: string) =
        let builder = StringBuilder()

        value
        |> Seq.iteri (fun index current ->
            if Char.IsUpper(current) && index > 0 then builder.Append('-') |> ignore

            builder.Append(Char.ToLowerInvariant(current))
            |> ignore)

        builder.ToString()

    let private enumName value =
        value
        |> getDiscriminatedUnionCaseName
        |> toKebabCase

    let private validationStatusToString (status: ValidationStatus) =
        match status with
        | ValidationStatus.Pass -> "pass"
        | ValidationStatus.Fail -> "fail"
        | ValidationStatus.Block -> "block"
        | ValidationStatus.Skipped -> "skipped"

    let private validationModeToString (mode: ValidationExecutionMode) =
        match mode with
        | ValidationExecutionMode.Synchronous -> "synchronous"
        | ValidationExecutionMode.AsyncCallback -> "async-callback"

    let private queueFailureActionToString (action: QueueFailureAction) =
        match action with
        | QueueFailureAction.PauseQueue -> "pause-queue"
        | QueueFailureAction.Continue -> "continue"
        | QueueFailureAction.QuarantinePromotionSet -> "quarantine-promotion-set"

    let private conflictStatusToString (status: StepConflictStatus) =
        match status with
        | StepConflictStatus.NoConflicts -> "no-conflicts"
        | StepConflictStatus.AutoResolved -> "auto-resolved"
        | StepConflictStatus.BlockedPendingReview -> "blocked-pending-review"
        | StepConflictStatus.Failed -> "failed"

    let private artifactTypeToString (artifactType: ArtifactType) =
        match artifactType with
        | ArtifactType.AgentSummary -> "agent-summary"
        | ArtifactType.ConflictReport -> "conflict-report"
        | ArtifactType.Prompt -> "prompt"
        | ArtifactType.ValidationOutput -> "validation-output"
        | ArtifactType.ReviewNotes -> "review-notes"
        | ArtifactType.Other kind when String.IsNullOrWhiteSpace kind -> "other"
        | ArtifactType.Other kind -> toKebabCase kind

    let private createObject () = JsonObject()

    let private asElement (node: JsonNode) = JsonSerializer.SerializeToElement(node, CanonicalExternalEventEnvelope.canonicalJsonSerializerOptions)

    let private addGuid (name: string) (value: Guid) (node: JsonObject) = if value <> Guid.Empty then node[name] <- JsonValue.Create(value)

    let private addGuidOption (name: string) (value: Guid option) (node: JsonObject) =
        value
        |> Option.iter (fun current -> addGuid name current node)

    let private addString (name: string) (value: string) (node: JsonObject) =
        if String.IsNullOrWhiteSpace value |> not then
            node[name] <- JsonValue.Create(value)

    let private addStringOption (name: string) (value: string option) (node: JsonObject) =
        value
        |> Option.iter (fun current -> addString name current node)

    let private addBool (name: string) (value: bool) (node: JsonObject) = node[name] <- JsonValue.Create(value)
    let private addInt (name: string) (value: int) (node: JsonObject) = node[name] <- JsonValue.Create(value)
    let private addInt64 (name: string) (value: int64) (node: JsonObject) = node[name] <- JsonValue.Create(value)
    let private addSingle (name: string) (value: single) (node: JsonObject) = node[name] <- JsonValue.Create(value)

    let private changePayload (idName: string) (entityId: Guid) (changeKind: string) (changedNode: JsonObject) =
        let payload = createObject ()
        addGuid idName entityId payload
        addString "changeKind" changeKind payload
        payload["changed"] <- changedNode
        payload

    let private conflictResolutionPolicyNode (policy: ConflictResolutionPolicy) =
        let node = createObject ()

        match policy with
        | ConflictResolutionPolicy.NoConflicts () -> addString "kind" "no-conflicts" node
        | ConflictResolutionPolicy.ConflictsAllowed threshold ->
            addString "kind" "conflicts-allowed" node
            node["confidenceThreshold"] <- JsonValue.Create(threshold)

        node

    let private linkSummaryNode (link: ReferenceLinkType) =
        let node = createObject ()

        match link with
        | ReferenceLinkType.BasedOn referenceId ->
            addString "kind" "based-on" node
            addGuid "referenceId" referenceId node
        | ReferenceLinkType.IncludedInPromotionSet promotionSetId ->
            addString "kind" "included-in-promotion-set" node
            addGuid "promotionSetId" promotionSetId node
        | ReferenceLinkType.PromotionSetTerminal promotionSetId ->
            addString "kind" "promotion-set-terminal" node
            addGuid "promotionSetId" promotionSetId node

        node

    let private linksSummaryNode (links: ReferenceLinkType seq) =
        let linksList = links |> Seq.toList
        let node = createObject ()
        addInt "count" linksList.Length node
        let linkNodes = JsonArray()

        linksList
        |> List.iter (fun link -> linkNodes.Add(linkSummaryNode link))

        node["links"] <- linkNodes
        node

    let private rulesSummaryNode (rules: ValidationSetRule list) =
        let node = createObject ()
        addInt "ruleCount" rules.Length node
        let ruleNodes = JsonArray()

        rules
        |> List.iter (fun rule ->
            let ruleNode = createObject ()
            let eventNames = JsonArray()

            rule.EventNames
            |> List.iter (fun eventName -> eventNames.Add(JsonValue.Create(eventName)))

            ruleNode["eventNames"] <- eventNames
            addString "branchNameGlob" rule.BranchNameGlob ruleNode
            ruleNodes.Add(ruleNode))

        node["rules"] <- ruleNodes
        node

    let private validationsSummaryNode (validations: Validation list) =
        let node = createObject ()
        addInt "validationCount" validations.Length node
        let validationNodes = JsonArray()

        validations
        |> List.iter (fun validation ->
            let validationNode = createObject ()
            addString "name" validation.Name validationNode
            addString "version" validation.Version validationNode
            addString "executionMode" (validationModeToString validation.ExecutionMode) validationNode
            addBool "requiredForApply" validation.RequiredForApply validationNode
            validationNodes.Add(validationNode))

        node["validations"] <- validationNodes
        node

    let private ruleSummaryNode (snapshot: PolicySnapshot) =
        let node = createObject ()
        addInt "humanReviewRuleCount" snapshot.Rules.HumanReviewRequiredWhen.Length node
        addInt "deepAnalysisRuleCount" snapshot.Rules.DeepAnalysisRequiredWhen.Length node
        addInt "sensitivePathCount" snapshot.Rules.SensitivePaths.Length node
        addBool "redactionEnabled" snapshot.Rules.Redaction.Enabled node
        addInt "redactionPatternCount" snapshot.Rules.Redaction.Patterns.Length node
        addInt "denylistPathCount" snapshot.Rules.Redaction.DenylistPaths.Length node
        addBool "defaultRequireHumanReview" snapshot.Rules.Defaults.RequireHumanReview node
        addBool "deepAnalysisDefaultEnabled" snapshot.Rules.Defaults.DeepAnalysis.Enabled node
        addInt "deepAnalysisMaxTokens" snapshot.Rules.Defaults.DeepAnalysis.MaxTokens node
        addBool "triageDefaultEnabled" snapshot.Rules.Defaults.Triage.Enabled node
        addInt "triageMaxTokens" snapshot.Rules.Defaults.Triage.MaxTokens node
        addString "queueOnFailure" (queueFailureActionToString snapshot.Rules.Queue.OnFailure) node

        let drift = createObject ()
        addInt "churnLines" snapshot.Rules.ApprovalRules.BaselineDriftReackThreshold.ChurnLines drift
        addInt "filesTouched" snapshot.Rules.ApprovalRules.BaselineDriftReackThreshold.FilesTouched drift
        node["baselineDriftReackThreshold"] <- drift
        node

    let private promotionPointerNode (pointer: PromotionPointer) =
        let node = createObject ()
        addGuid "branchId" pointer.BranchId node
        addGuid "referenceId" pointer.ReferenceId node
        addGuid "directoryVersionId" pointer.DirectoryVersionId node
        node

    let private promotionPointersNode (promotionPointers: PromotionPointer list) =
        let node = createObject ()
        addInt "count" promotionPointers.Length node
        let pointerNodes = JsonArray()

        promotionPointers
        |> List.iter (fun pointer -> pointerNodes.Add(promotionPointerNode pointer))

        node["promotionPointers"] <- pointerNodes
        node

    let private stepsSummaryNode (steps: PromotionSetStep list) (includedCount: int) =
        let actualCount = Math.Max(0, Math.Min(includedCount, steps.Length))
        let node = createObject ()
        addInt "stepCount" steps.Length node

        let conflictCounts = createObject ()

        addInt
            "noConflicts"
            (steps
             |> List.filter (fun step -> step.ConflictStatus = StepConflictStatus.NoConflicts)
             |> List.length)
            conflictCounts

        addInt
            "autoResolved"
            (steps
             |> List.filter (fun step -> step.ConflictStatus = StepConflictStatus.AutoResolved)
             |> List.length)
            conflictCounts

        addInt
            "blockedPendingReview"
            (steps
             |> List.filter (fun step -> step.ConflictStatus = StepConflictStatus.BlockedPendingReview)
             |> List.length)
            conflictCounts

        addInt
            "failed"
            (steps
             |> List.filter (fun step -> step.ConflictStatus = StepConflictStatus.Failed)
             |> List.length)
            conflictCounts

        node["conflictCounts"] <- conflictCounts
        addBool "truncated" (actualCount < steps.Length) node

        let stepNodes = JsonArray()

        steps
        |> List.truncate actualCount
        |> List.iter (fun step ->
            let stepNode = createObject ()
            addGuid "stepId" step.StepId stepNode
            addInt "order" step.Order stepNode

            let originalPromotion = createObject ()
            addGuid "branchId" step.OriginalPromotion.BranchId originalPromotion
            addGuid "referenceId" step.OriginalPromotion.ReferenceId originalPromotion
            addGuid "directoryVersionId" step.OriginalPromotion.DirectoryVersionId originalPromotion
            stepNode["originalPromotion"] <- originalPromotion

            addGuidOption
                "originalBasePromotionReferenceId"
                (if step.OriginalBasePromotionReferenceId = Guid.Empty then
                     Option.None
                 else
                     Option.Some step.OriginalBasePromotionReferenceId)
                stepNode

            addGuidOption
                "originalBaseDirectoryVersionId"
                (if step.OriginalBaseDirectoryVersionId = Guid.Empty then
                     Option.None
                 else
                     Option.Some step.OriginalBaseDirectoryVersionId)
                stepNode

            addGuidOption
                "computedAgainstBaseDirectoryVersionId"
                (if step.ComputedAgainstBaseDirectoryVersionId = Guid.Empty then
                     Option.None
                 else
                     Option.Some step.ComputedAgainstBaseDirectoryVersionId)
                stepNode

            addGuidOption
                "appliedDirectoryVersionId"
                (if step.AppliedDirectoryVersionId = Guid.Empty then
                     Option.None
                 else
                     Option.Some step.AppliedDirectoryVersionId)
                stepNode

            addString "conflictStatus" (conflictStatusToString step.ConflictStatus) stepNode
            addGuidOption "conflictSummaryArtifactId" step.ConflictSummaryArtifactId stepNode
            stepNodes.Add(stepNode))

        node["steps"] <- stepNodes
        node

    let private reviewNotesSummaryNode (notes: ReviewNotes) =
        let node = createObject ()
        addGuid "reviewNotesId" notes.ReviewNotesId node
        addGuid "ownerId" notes.OwnerId node
        addGuid "organizationId" notes.OrganizationId node
        addGuid "repositoryId" notes.RepositoryId node
        addGuidOption "promotionSetId" notes.PromotionSetId node
        addString "policySnapshotId" notes.PolicySnapshotId node
        addString "summary" notes.Summary node
        addInt "chapterCount" notes.Chapters.Length node
        addInt "findingCount" notes.Findings.Length node

        addInt
            "unresolvedFindingCount"
            (notes.Findings
             |> List.filter (fun finding -> finding.ResolutionState = FindingResolutionState.Open)
             |> List.length)
            node

        addBool "evidenceSummaryPresent" notes.EvidenceSetSummary.IsSome node

        addStringOption
            "validationSummary"
            (notes.ValidationSummary
             |> Option.map (fun summary -> summary.Summary))
            node

        node

    let private fail rawEventCase correlationId reason = Failed { RawEventCase = rawEventCase; CorrelationId = correlationId; Reason = reason }

    let private buildEnvelope rawEventCase eventName occurredAt correlationId ownerId organizationId repositoryId actorType actorId payload =
        match
            CanonicalExternalEventEnvelope.tryCreate
                eventName
                occurredAt
                correlationId
                ownerId
                organizationId
                repositoryId
                { ActorType = actorType; ActorId = actorId }
                (asElement payload)
            with
        | Error reason -> fail rawEventCase correlationId (MissingActorIdentity reason)
        | Ok envelope ->
            let size = CanonicalExternalEventEnvelope.measureSize envelope

            if size.ByteCount
               >= CanonicalExternalEventEnvelope.MaxDocumentBytes then
                fail rawEventCase correlationId (OversizeDocument size.ByteCount)
            else
                Published envelope

    let private publish rawEventCase eventName (metadata: EventMetadata) ownerId organizationId repositoryId actorType actorId payload =
        buildEnvelope rawEventCase eventName metadata.Timestamp metadata.CorrelationId ownerId organizationId repositoryId actorType actorId payload

    let private publishGuid rawEventCase eventName metadata ownerId organizationId repositoryId actorType (actorId: Guid) payload =
        publish rawEventCase eventName metadata ownerId organizationId repositoryId actorType $"{actorId}" payload

    let private requireGuidFromMetadata fieldName rawEventCase (metadata: EventMetadata) =
        match tryGetGuid fieldName metadata with
        | Some current when current <> Guid.Empty -> Ok current
        | _ -> Error(fail rawEventCase metadata.CorrelationId (MissingRequiredField fieldName))

    let private requireBranchId rawEventCase (metadata: EventMetadata) =
        match
            branchIdOptionFromMetadata metadata
            |> Option.orElse (tryGetGuid "TargetBranchId" metadata)
            with
        | Some branchId when branchId <> Guid.Empty -> Ok branchId
        | _ -> Error(fail rawEventCase metadata.CorrelationId (MissingRequiredField(nameof BranchId)))

    let private requireQueueBranchId rawEventCase (metadata: EventMetadata) =
        match
            branchIdOptionFromMetadata metadata
            |> Option.orElse (actorGuidFromMetadata metadata)
            with
        | Some branchId when branchId <> Guid.Empty -> Ok branchId
        | _ -> Error(fail rawEventCase metadata.CorrelationId (MissingRequiredField(nameof BranchId)))

    let private buildStepsUpdatedEnvelope
        rawEventCase
        metadata
        ownerId
        organizationId
        repositoryId
        promotionSetId
        targetBranchId
        computedAgainstTerminalReferenceId
        steps
        =
        let buildForCount includedCount =
            let payload = createObject ()
            addGuid "promotionSetId" promotionSetId payload
            addGuid "targetBranchId" targetBranchId payload
            addGuid "computedAgainstTerminalReferenceId" computedAgainstTerminalReferenceId payload
            payload["stepsSummary"] <- stepsSummaryNode steps includedCount

            publishGuid
                rawEventCase
                CanonicalEventName.PromotionSetStepsUpdated
                metadata
                ownerId
                organizationId
                repositoryId
                "PromotionSet"
                promotionSetId
                payload

        match buildForCount steps.Length with
        | Published _ as published -> published
        | Failed { Reason = OversizeDocument _ } ->
            let mutable low = 0
            let mutable high = if steps.IsEmpty then 0 else steps.Length - 1
            let mutable bestFit: CanonicalBuildOutcome option = Option.None

            while low <= high do
                let mid = low + ((high - low) / 2)

                match buildForCount mid with
                | Published _ as published ->
                    bestFit <- Some published
                    low <- mid + 1
                | Failed { Reason = OversizeDocument _ } -> high <- mid - 1
                | other ->
                    bestFit <- Some other
                    low <- high + 1

            match bestFit with
            | Some outcome -> outcome
            | Option.None -> buildForCount 0
        | other -> other

    let private buildOwnerEvent (ownerEvent: OwnerEvent) =
        let rawEventCase = RawEventCase.owner (getDiscriminatedUnionCaseName ownerEvent.Event)

        let ownerId =
            actorGuidFromMetadata ownerEvent.Metadata
            |> Option.orElse (tryGetGuid (nameof OwnerId) ownerEvent.Metadata)
            |> Option.defaultValue OwnerId.Empty

        match ownerEvent.Event with
        | OwnerEventType.Created (createdOwnerId, ownerName) ->
            let payload = createObject ()
            addGuid "ownerId" createdOwnerId payload
            addString "ownerName" ownerName payload

            publishGuid
                rawEventCase
                CanonicalEventName.OwnerCreated
                ownerEvent.Metadata
                createdOwnerId
                OrganizationId.Empty
                RepositoryId.Empty
                "Owner"
                createdOwnerId
                payload
        | OwnerEventType.NameSet ownerName ->
            let changed = createObject ()
            addString "ownerName" ownerName changed

            publishGuid
                rawEventCase
                CanonicalEventName.OwnerUpdated
                ownerEvent.Metadata
                ownerId
                OrganizationId.Empty
                RepositoryId.Empty
                "Owner"
                ownerId
                (changePayload "ownerId" ownerId "name-set" changed)
        | OwnerEventType.TypeSet ownerType ->
            let changed = createObject ()
            addString "ownerType" (enumName ownerType) changed

            publishGuid
                rawEventCase
                CanonicalEventName.OwnerUpdated
                ownerEvent.Metadata
                ownerId
                OrganizationId.Empty
                RepositoryId.Empty
                "Owner"
                ownerId
                (changePayload "ownerId" ownerId "type-set" changed)
        | OwnerEventType.SearchVisibilitySet searchVisibility ->
            let changed = createObject ()
            addString "searchVisibility" (enumName searchVisibility) changed

            publishGuid
                rawEventCase
                CanonicalEventName.OwnerUpdated
                ownerEvent.Metadata
                ownerId
                OrganizationId.Empty
                RepositoryId.Empty
                "Owner"
                ownerId
                (changePayload "ownerId" ownerId "search-visibility-set" changed)
        | OwnerEventType.DescriptionSet description ->
            let changed = createObject ()
            addString "description" description changed

            publishGuid
                rawEventCase
                CanonicalEventName.OwnerUpdated
                ownerEvent.Metadata
                ownerId
                OrganizationId.Empty
                RepositoryId.Empty
                "Owner"
                ownerId
                (changePayload "ownerId" ownerId "description-set" changed)
        | OwnerEventType.LogicalDeleted (force, deleteReason) ->
            let payload = createObject ()
            addGuid "ownerId" ownerId payload
            addBool "force" force payload
            addString "deleteReason" deleteReason payload
            publishGuid rawEventCase CanonicalEventName.OwnerDeleted ownerEvent.Metadata ownerId OrganizationId.Empty RepositoryId.Empty "Owner" ownerId payload
        | OwnerEventType.PhysicalDeleted -> InternalOnly rawEventCase
        | OwnerEventType.Undeleted ->
            let payload = createObject ()
            addGuid "ownerId" ownerId payload

            publishGuid
                rawEventCase
                CanonicalEventName.OwnerRestored
                ownerEvent.Metadata
                ownerId
                OrganizationId.Empty
                RepositoryId.Empty
                "Owner"
                ownerId
                payload

    let private buildOrganizationEvent (organizationEvent: OrganizationEvent) =
        let rawEventCase = RawEventCase.organization (getDiscriminatedUnionCaseName organizationEvent.Event)
        let ownerId = ownerIdFromMetadata organizationEvent.Metadata

        let organizationId =
            actorGuidFromMetadata organizationEvent.Metadata
            |> Option.orElse (tryGetGuid (nameof OrganizationId) organizationEvent.Metadata)
            |> Option.defaultValue OrganizationId.Empty

        match organizationEvent.Event with
        | OrganizationEventType.Created (createdOrganizationId, organizationName, createdOwnerId) ->
            let payload = createObject ()
            addGuid "organizationId" createdOrganizationId payload
            addString "organizationName" organizationName payload
            addGuid "ownerId" createdOwnerId payload

            publishGuid
                rawEventCase
                CanonicalEventName.OrganizationCreated
                organizationEvent.Metadata
                createdOwnerId
                createdOrganizationId
                RepositoryId.Empty
                "Organization"
                createdOrganizationId
                payload
        | OrganizationEventType.NameSet organizationName ->
            let changed = createObject ()
            addString "organizationName" organizationName changed

            publishGuid
                rawEventCase
                CanonicalEventName.OrganizationUpdated
                organizationEvent.Metadata
                ownerId
                organizationId
                RepositoryId.Empty
                "Organization"
                organizationId
                (changePayload "organizationId" organizationId "name-set" changed)
        | OrganizationEventType.TypeSet organizationType ->
            let changed = createObject ()
            addString "organizationType" (enumName organizationType) changed

            publishGuid
                rawEventCase
                CanonicalEventName.OrganizationUpdated
                organizationEvent.Metadata
                ownerId
                organizationId
                RepositoryId.Empty
                "Organization"
                organizationId
                (changePayload "organizationId" organizationId "type-set" changed)
        | OrganizationEventType.SearchVisibilitySet searchVisibility ->
            let changed = createObject ()
            addString "searchVisibility" (enumName searchVisibility) changed

            publishGuid
                rawEventCase
                CanonicalEventName.OrganizationUpdated
                organizationEvent.Metadata
                ownerId
                organizationId
                RepositoryId.Empty
                "Organization"
                organizationId
                (changePayload "organizationId" organizationId "search-visibility-set" changed)
        | OrganizationEventType.DescriptionSet description ->
            let changed = createObject ()
            addString "description" description changed

            publishGuid
                rawEventCase
                CanonicalEventName.OrganizationUpdated
                organizationEvent.Metadata
                ownerId
                organizationId
                RepositoryId.Empty
                "Organization"
                organizationId
                (changePayload "organizationId" organizationId "description-set" changed)
        | OrganizationEventType.LogicalDeleted (force, deleteReason) ->
            let payload = createObject ()
            addGuid "organizationId" organizationId payload
            addBool "force" force payload
            addString "deleteReason" deleteReason payload

            publishGuid
                rawEventCase
                CanonicalEventName.OrganizationDeleted
                organizationEvent.Metadata
                ownerId
                organizationId
                RepositoryId.Empty
                "Organization"
                organizationId
                payload
        | OrganizationEventType.PhysicalDeleted -> InternalOnly rawEventCase
        | OrganizationEventType.Undeleted ->
            let payload = createObject ()
            addGuid "organizationId" organizationId payload

            publishGuid
                rawEventCase
                CanonicalEventName.OrganizationRestored
                organizationEvent.Metadata
                ownerId
                organizationId
                RepositoryId.Empty
                "Organization"
                organizationId
                payload

    let private buildRepositoryEvent (repositoryEvent: RepositoryEvent) =
        let rawEventCase = RawEventCase.repository (getDiscriminatedUnionCaseName repositoryEvent.Event)
        let ownerId = ownerIdFromMetadata repositoryEvent.Metadata
        let organizationId = organizationIdFromMetadata repositoryEvent.Metadata

        let repositoryId =
            actorGuidFromMetadata repositoryEvent.Metadata
            |> Option.orElse (tryGetGuid (nameof RepositoryId) repositoryEvent.Metadata)
            |> Option.defaultValue RepositoryId.Empty

        let publishUpdated changeKind changed =
            publishGuid
                rawEventCase
                CanonicalEventName.RepositoryUpdated
                repositoryEvent.Metadata
                ownerId
                organizationId
                repositoryId
                "Repository"
                repositoryId
                (changePayload "repositoryId" repositoryId changeKind changed)

        match repositoryEvent.Event with
        | RepositoryEventType.Created (repositoryName, createdRepositoryId, createdOwnerId, createdOrganizationId, objectStorageProvider) ->
            let payload = createObject ()
            addGuid "repositoryId" createdRepositoryId payload
            addString "repositoryName" repositoryName payload
            addGuid "ownerId" createdOwnerId payload
            addGuid "organizationId" createdOrganizationId payload
            addString "objectStorageProvider" (enumName objectStorageProvider) payload

            publishGuid
                rawEventCase
                CanonicalEventName.RepositoryCreated
                repositoryEvent.Metadata
                createdOwnerId
                createdOrganizationId
                createdRepositoryId
                "Repository"
                createdRepositoryId
                payload
        | RepositoryEventType.Initialized ->
            let payload = createObject ()
            addGuid "repositoryId" repositoryId payload

            publishGuid
                rawEventCase
                CanonicalEventName.RepositoryInitialized
                repositoryEvent.Metadata
                ownerId
                organizationId
                repositoryId
                "Repository"
                repositoryId
                payload
        | RepositoryEventType.ObjectStorageProviderSet objectStorageProvider ->
            let changed = createObject ()
            addString "objectStorageProvider" (enumName objectStorageProvider) changed
            publishUpdated "object-storage-provider-set" changed
        | RepositoryEventType.StorageAccountNameSet storageAccountName ->
            let changed = createObject ()
            addString "storageAccountName" storageAccountName changed
            publishUpdated "storage-account-name-set" changed
        | RepositoryEventType.StorageContainerNameSet storageContainerName ->
            let changed = createObject ()
            addString "storageContainerName" storageContainerName changed
            publishUpdated "storage-container-name-set" changed
        | RepositoryEventType.RepositoryTypeSet repositoryType ->
            let changed = createObject ()
            addString "repositoryType" (enumName repositoryType) changed
            publishUpdated "repository-type-set" changed
        | RepositoryEventType.RepositoryStatusSet repositoryStatus ->
            let changed = createObject ()
            addString "repositoryStatus" (enumName repositoryStatus) changed
            publishUpdated "repository-status-set" changed
        | RepositoryEventType.AllowsLargeFilesSet allowsLargeFiles ->
            let changed = createObject ()
            addBool "allowsLargeFiles" allowsLargeFiles changed
            publishUpdated "allows-large-files-set" changed
        | RepositoryEventType.AnonymousAccessSet anonymousAccess ->
            let changed = createObject ()
            addBool "anonymousAccess" anonymousAccess changed
            publishUpdated "anonymous-access-set" changed
        | RepositoryEventType.RecordSavesSet recordSaves ->
            let changed = createObject ()
            addBool "recordSaves" recordSaves changed
            publishUpdated "record-saves-set" changed
        | RepositoryEventType.DefaultServerApiVersionSet defaultServerApiVersion ->
            let changed = createObject ()
            addString "defaultServerApiVersion" defaultServerApiVersion changed
            publishUpdated "default-server-api-version-set" changed
        | RepositoryEventType.DefaultBranchNameSet defaultBranchName ->
            let changed = createObject ()
            addString "defaultBranchName" defaultBranchName changed
            publishUpdated "default-branch-name-set" changed
        | RepositoryEventType.LogicalDeleteDaysSet duration ->
            let changed = createObject ()
            addSingle "logicalDeleteDays" duration changed
            publishUpdated "logical-delete-days-set" changed
        | RepositoryEventType.SaveDaysSet duration ->
            let changed = createObject ()
            addSingle "saveDays" duration changed
            publishUpdated "save-days-set" changed
        | RepositoryEventType.CheckpointDaysSet duration ->
            let changed = createObject ()
            addSingle "checkpointDays" duration changed
            publishUpdated "checkpoint-days-set" changed
        | RepositoryEventType.DirectoryVersionCacheDaysSet duration ->
            let changed = createObject ()
            addSingle "directoryVersionCacheDays" duration changed
            publishUpdated "directory-version-cache-days-set" changed
        | RepositoryEventType.DiffCacheDaysSet duration ->
            let changed = createObject ()
            addSingle "diffCacheDays" duration changed
            publishUpdated "diff-cache-days-set" changed
        | RepositoryEventType.NameSet repositoryName ->
            let changed = createObject ()
            addString "repositoryName" repositoryName changed
            publishUpdated "name-set" changed
        | RepositoryEventType.DescriptionSet description ->
            let changed = createObject ()
            addString "description" description changed
            publishUpdated "description-set" changed
        | RepositoryEventType.ConflictResolutionPolicySet policy ->
            let changed = createObject ()
            changed["conflictResolutionPolicy"] <- conflictResolutionPolicyNode policy
            publishUpdated "conflict-resolution-policy-set" changed
        | RepositoryEventType.LogicalDeleted (force, deleteReason) ->
            let payload = createObject ()
            addGuid "repositoryId" repositoryId payload
            addBool "force" force payload
            addString "deleteReason" deleteReason payload

            publishGuid
                rawEventCase
                CanonicalEventName.RepositoryDeleted
                repositoryEvent.Metadata
                ownerId
                organizationId
                repositoryId
                "Repository"
                repositoryId
                payload
        | RepositoryEventType.PhysicalDeleted -> InternalOnly rawEventCase
        | RepositoryEventType.Undeleted ->
            let payload = createObject ()
            addGuid "repositoryId" repositoryId payload

            publishGuid
                rawEventCase
                CanonicalEventName.RepositoryRestored
                repositoryEvent.Metadata
                ownerId
                organizationId
                repositoryId
                "Repository"
                repositoryId
                payload

    let private buildBranchEvent (branchEvent: BranchEvent) =
        let rawEventCase = RawEventCase.branch (getDiscriminatedUnionCaseName branchEvent.Event)
        let ownerId = ownerIdFromMetadata branchEvent.Metadata
        let organizationId = organizationIdFromMetadata branchEvent.Metadata
        let repositoryId = repositoryIdFromMetadata branchEvent.Metadata

        let branchId =
            actorGuidFromMetadata branchEvent.Metadata
            |> Option.orElse (tryGetGuid (nameof BranchId) branchEvent.Metadata)
            |> Option.defaultValue BranchId.Empty

        let publishUpdated changeKind changed =
            publishGuid
                rawEventCase
                CanonicalEventName.BranchUpdated
                branchEvent.Metadata
                ownerId
                organizationId
                repositoryId
                "Branch"
                branchId
                (changePayload "branchId" branchId changeKind changed)

        match branchEvent.Event with
        | BranchEventType.Created (createdBranchId,
                                   branchName,
                                   parentBranchId,
                                   basedOnReferenceId,
                                   createdOwnerId,
                                   createdOrganizationId,
                                   createdRepositoryId,
                                   initialPermissions) ->
            let payload = createObject ()
            addGuid "branchId" createdBranchId payload
            addString "branchName" branchName payload
            addGuid "parentBranchId" parentBranchId payload
            addGuid "basedOnReferenceId" basedOnReferenceId payload
            let permissions = JsonArray()

            initialPermissions
            |> Seq.iter (fun permission -> permissions.Add(JsonValue.Create(enumName permission)))

            payload["initialPermissions"] <- permissions

            publishGuid
                rawEventCase
                CanonicalEventName.BranchCreated
                branchEvent.Metadata
                createdOwnerId
                createdOrganizationId
                createdRepositoryId
                "Branch"
                createdBranchId
                payload
        | BranchEventType.Rebased basedOnReferenceId ->
            let changed = createObject ()
            addGuid "basedOnReferenceId" basedOnReferenceId changed

            publishGuid
                rawEventCase
                CanonicalEventName.BranchRebased
                branchEvent.Metadata
                ownerId
                organizationId
                repositoryId
                "Branch"
                branchId
                (changePayload "branchId" branchId "rebased" changed)
        | BranchEventType.NameSet branchName ->
            let changed = createObject ()
            addString "branchName" branchName changed
            publishUpdated "name-set" changed
        | BranchEventType.EnabledAssign enabled ->
            let changed = createObject ()
            addBool "assignEnabled" enabled changed
            publishUpdated "assign-enabled-set" changed
        | BranchEventType.EnabledPromotion enabled ->
            let changed = createObject ()
            addBool "promotionEnabled" enabled changed
            publishUpdated "promotion-enabled-set" changed
        | BranchEventType.EnabledCommit enabled ->
            let changed = createObject ()
            addBool "commitEnabled" enabled changed
            publishUpdated "commit-enabled-set" changed
        | BranchEventType.EnabledCheckpoint enabled ->
            let changed = createObject ()
            addBool "checkpointEnabled" enabled changed
            publishUpdated "checkpoint-enabled-set" changed
        | BranchEventType.EnabledSave enabled ->
            let changed = createObject ()
            addBool "saveEnabled" enabled changed
            publishUpdated "save-enabled-set" changed
        | BranchEventType.EnabledTag enabled ->
            let changed = createObject ()
            addBool "tagEnabled" enabled changed
            publishUpdated "tag-enabled-set" changed
        | BranchEventType.EnabledExternal enabled ->
            let changed = createObject ()
            addBool "externalEnabled" enabled changed
            publishUpdated "external-enabled-set" changed
        | BranchEventType.EnabledAutoRebase enabled ->
            let changed = createObject ()
            addBool "autoRebaseEnabled" enabled changed
            publishUpdated "auto-rebase-enabled-set" changed
        | BranchEventType.PromotionModeSet promotionMode ->
            let changed = createObject ()
            addString "promotionMode" (enumName promotionMode) changed
            publishUpdated "promotion-mode-set" changed
        | BranchEventType.ReferenceRemoved referenceId ->
            let changed = createObject ()
            addGuid "referenceId" referenceId changed
            publishUpdated "reference-removed" changed
        | BranchEventType.ParentBranchUpdated parentBranchId ->
            let changed = createObject ()
            addGuid "parentBranchId" parentBranchId changed
            publishUpdated "parent-branch-updated" changed
        | BranchEventType.LogicalDeleted (force, deleteReason, reassignedChildBranches, childrenReassignedTo) ->
            let payload = createObject ()
            addGuid "branchId" branchId payload
            addBool "force" force payload
            addString "deleteReason" deleteReason payload
            addBool "reassignedChildBranches" reassignedChildBranches payload
            addGuidOption "childrenReassignedTo" childrenReassignedTo payload
            publishGuid rawEventCase CanonicalEventName.BranchDeleted branchEvent.Metadata ownerId organizationId repositoryId "Branch" branchId payload
        | BranchEventType.PhysicalDeleted -> InternalOnly rawEventCase
        | BranchEventType.Undeleted ->
            let payload = createObject ()
            addGuid "branchId" branchId payload
            publishGuid rawEventCase CanonicalEventName.BranchRestored branchEvent.Metadata ownerId organizationId repositoryId "Branch" branchId payload
        | BranchEventType.Assigned _
        | BranchEventType.Promoted _
        | BranchEventType.Committed _
        | BranchEventType.Checkpointed _
        | BranchEventType.Saved _
        | BranchEventType.Tagged _
        | BranchEventType.ExternalCreated _ -> InternalOnly rawEventCase

    let private buildReferenceEvent (referenceEvent: ReferenceEvent) =
        let rawEventCase = RawEventCase.reference (getDiscriminatedUnionCaseName referenceEvent.Event)
        let ownerId = ownerIdFromMetadata referenceEvent.Metadata
        let organizationId = organizationIdFromMetadata referenceEvent.Metadata
        let repositoryId = repositoryIdFromMetadata referenceEvent.Metadata

        match referenceEvent.Event with
        | ReferenceEventType.Created (createdReferenceId,
                                      createdOwnerId,
                                      createdOrganizationId,
                                      createdRepositoryId,
                                      branchId,
                                      directoryVersionId,
                                      sha256Hash,
                                      referenceType,
                                      referenceText,
                                      links) ->
            let terminalPromotionSetId =
                links
                |> Seq.tryPick (function
                    | ReferenceLinkType.PromotionSetTerminal promotionSetId -> Some promotionSetId
                    | _ -> Option.None)

            match referenceType, terminalPromotionSetId with
            | ReferenceType.Promotion, Some promotionSetId ->
                let payload = createObject ()
                addGuid "promotionSetId" promotionSetId payload
                addGuid "targetBranchId" branchId payload
                addGuid "terminalPromotionReferenceId" createdReferenceId payload
                addGuid "ownerId" createdOwnerId payload
                addGuid "organizationId" createdOrganizationId payload
                addGuid "repositoryId" createdRepositoryId payload

                publishGuid
                    rawEventCase
                    CanonicalEventName.PromotionSetApplied
                    referenceEvent.Metadata
                    createdOwnerId
                    createdOrganizationId
                    createdRepositoryId
                    "Reference"
                    createdReferenceId
                    payload
            | _ ->
                let payload = createObject ()
                addGuid "referenceId" createdReferenceId payload
                addGuid "branchId" branchId payload
                addGuid "directoryVersionId" directoryVersionId payload
                addString "sha256Hash" sha256Hash payload
                addString "referenceType" (enumName referenceType) payload
                addString "referenceText" referenceText payload
                payload["linksSummary"] <- linksSummaryNode links

                publishGuid
                    rawEventCase
                    CanonicalEventName.ReferenceCreated
                    referenceEvent.Metadata
                    createdOwnerId
                    createdOrganizationId
                    createdRepositoryId
                    "Reference"
                    createdReferenceId
                    payload
        | _ ->
            match requireGuidFromMetadata "ActorId" rawEventCase referenceEvent.Metadata with
            | Error outcome -> outcome
            | Ok referenceId ->
                match referenceEvent.Event with
                | ReferenceEventType.LinkAdded link ->
                    let changed = createObject ()
                    changed["link"] <- linkSummaryNode link

                    publishGuid
                        rawEventCase
                        CanonicalEventName.ReferenceUpdated
                        referenceEvent.Metadata
                        ownerId
                        organizationId
                        repositoryId
                        "Reference"
                        referenceId
                        (changePayload "referenceId" referenceId "link-added" changed)
                | ReferenceEventType.LinkRemoved link ->
                    let changed = createObject ()
                    changed["link"] <- linkSummaryNode link

                    publishGuid
                        rawEventCase
                        CanonicalEventName.ReferenceUpdated
                        referenceEvent.Metadata
                        ownerId
                        organizationId
                        repositoryId
                        "Reference"
                        referenceId
                        (changePayload "referenceId" referenceId "link-removed" changed)
                | ReferenceEventType.LogicalDeleted (force, deleteReason) ->
                    let payload = createObject ()
                    addGuid "referenceId" referenceId payload
                    addBool "force" force payload
                    addString "deleteReason" deleteReason payload

                    publishGuid
                        rawEventCase
                        CanonicalEventName.ReferenceDeleted
                        referenceEvent.Metadata
                        ownerId
                        organizationId
                        repositoryId
                        "Reference"
                        referenceId
                        payload
                | ReferenceEventType.Undeleted ->
                    let payload = createObject ()
                    addGuid "referenceId" referenceId payload

                    publishGuid
                        rawEventCase
                        CanonicalEventName.ReferenceRestored
                        referenceEvent.Metadata
                        ownerId
                        organizationId
                        repositoryId
                        "Reference"
                        referenceId
                        payload
                | ReferenceEventType.PhysicalDeleted -> InternalOnly rawEventCase
                | ReferenceEventType.Created _ -> InternalOnly rawEventCase

    let private buildDirectoryVersionEvent (directoryVersionEvent: DirectoryVersionEvent) =
        let rawEventCase = RawEventCase.directoryVersion (getDiscriminatedUnionCaseName directoryVersionEvent.Event)
        InternalOnly rawEventCase

    let private buildWorkItemEvent (workItemEvent: WorkItemEvent) =
        let rawEventCase = RawEventCase.workItem (getDiscriminatedUnionCaseName workItemEvent.Event)
        let ownerId = ownerIdFromMetadata workItemEvent.Metadata
        let organizationId = organizationIdFromMetadata workItemEvent.Metadata
        let repositoryId = repositoryIdFromMetadata workItemEvent.Metadata

        let updateEvent workItemId changeKind changed =
            publishGuid
                rawEventCase
                CanonicalEventName.WorkItemUpdated
                workItemEvent.Metadata
                ownerId
                organizationId
                repositoryId
                "WorkItem"
                workItemId
                (changePayload "workItemId" workItemId changeKind changed)

        match workItemEvent.Event with
        | WorkItemEventType.Created (createdWorkItemId, workItemNumber, createdOwnerId, createdOrganizationId, createdRepositoryId, title, description) ->
            let payload = createObject ()
            addGuid "workItemId" createdWorkItemId payload
            addInt64 "workItemNumber" workItemNumber payload
            addString "title" title payload
            addString "description" description payload

            publishGuid
                rawEventCase
                CanonicalEventName.WorkItemCreated
                workItemEvent.Metadata
                createdOwnerId
                createdOrganizationId
                createdRepositoryId
                "WorkItem"
                createdWorkItemId
                payload
        | _ ->
            match requireGuidFromMetadata "ActorId" rawEventCase workItemEvent.Metadata with
            | Error outcome -> outcome
            | Ok workItemId ->
                match workItemEvent.Event with
                | WorkItemEventType.TitleSet value ->
                    let changed = createObject ()
                    addString "title" value changed
                    updateEvent workItemId "title-set" changed
                | WorkItemEventType.DescriptionSet value ->
                    let changed = createObject ()
                    addString "description" value changed
                    updateEvent workItemId "description-set" changed
                | WorkItemEventType.StatusSet value ->
                    let changed = createObject ()
                    addString "status" (enumName value) changed
                    updateEvent workItemId "status-set" changed
                | WorkItemEventType.ParticipantAdded value ->
                    let changed = createObject ()
                    addString "participantId" value changed
                    updateEvent workItemId "participant-added" changed
                | WorkItemEventType.ParticipantRemoved value ->
                    let changed = createObject ()
                    addString "participantId" value changed
                    updateEvent workItemId "participant-removed" changed
                | WorkItemEventType.TagAdded value ->
                    let changed = createObject ()
                    addString "tag" value changed
                    updateEvent workItemId "tag-added" changed
                | WorkItemEventType.TagRemoved value ->
                    let changed = createObject ()
                    addString "tag" value changed
                    updateEvent workItemId "tag-removed" changed
                | WorkItemEventType.ConstraintsSet value ->
                    let changed = createObject ()
                    addString "constraints" value changed
                    updateEvent workItemId "constraints-set" changed
                | WorkItemEventType.NotesSet value ->
                    let changed = createObject ()
                    addString "notes" value changed
                    updateEvent workItemId "notes-set" changed
                | WorkItemEventType.ArchitecturalNotesSet value ->
                    let changed = createObject ()
                    addString "architecturalNotes" value changed
                    updateEvent workItemId "architectural-notes-set" changed
                | WorkItemEventType.MigrationNotesSet value ->
                    let changed = createObject ()
                    addString "migrationNotes" value changed
                    updateEvent workItemId "migration-notes-set" changed
                | WorkItemEventType.ExternalRefAdded value ->
                    let changed = createObject ()
                    addString "externalRef" value changed
                    updateEvent workItemId "external-ref-added" changed
                | WorkItemEventType.ExternalRefRemoved value ->
                    let changed = createObject ()
                    addString "externalRef" value changed
                    updateEvent workItemId "external-ref-removed" changed
                | WorkItemEventType.BranchLinked value ->
                    let changed = createObject ()
                    addGuid "branchId" value changed
                    updateEvent workItemId "branch-linked" changed
                | WorkItemEventType.BranchUnlinked value ->
                    let changed = createObject ()
                    addGuid "branchId" value changed
                    updateEvent workItemId "branch-unlinked" changed
                | WorkItemEventType.ReferenceLinked value ->
                    let changed = createObject ()
                    addGuid "referenceId" value changed
                    updateEvent workItemId "reference-linked" changed
                | WorkItemEventType.ReferenceUnlinked value ->
                    let changed = createObject ()
                    addGuid "referenceId" value changed
                    updateEvent workItemId "reference-unlinked" changed
                | WorkItemEventType.ArtifactLinked value ->
                    let changed = createObject ()
                    addGuid "artifactId" value changed
                    updateEvent workItemId "artifact-linked" changed
                | WorkItemEventType.ArtifactUnlinked value ->
                    let changed = createObject ()
                    addGuid "artifactId" value changed
                    updateEvent workItemId "artifact-unlinked" changed
                | WorkItemEventType.PromotionSetLinked value ->
                    let changed = createObject ()
                    addGuid "promotionSetId" value changed
                    updateEvent workItemId "promotion-set-linked" changed
                | WorkItemEventType.PromotionSetUnlinked value ->
                    let changed = createObject ()
                    addGuid "promotionSetId" value changed
                    updateEvent workItemId "promotion-set-unlinked" changed
                | WorkItemEventType.ReviewNotesLinked value ->
                    let changed = createObject ()
                    addGuid "reviewNotesId" value changed
                    updateEvent workItemId "review-notes-linked" changed
                | WorkItemEventType.ReviewNotesUnlinked value ->
                    let changed = createObject ()
                    addGuid "reviewNotesId" value changed
                    updateEvent workItemId "review-notes-unlinked" changed
                | WorkItemEventType.ReviewCheckpointLinked value ->
                    let changed = createObject ()
                    addGuid "reviewCheckpointId" value changed
                    updateEvent workItemId "review-checkpoint-linked" changed
                | WorkItemEventType.ReviewCheckpointUnlinked value ->
                    let changed = createObject ()
                    addGuid "reviewCheckpointId" value changed
                    updateEvent workItemId "review-checkpoint-unlinked" changed
                | WorkItemEventType.ValidationResultLinked value ->
                    let changed = createObject ()
                    addGuid "validationResultId" value changed
                    updateEvent workItemId "validation-result-linked" changed
                | WorkItemEventType.ValidationResultUnlinked value ->
                    let changed = createObject ()
                    addGuid "validationResultId" value changed
                    updateEvent workItemId "validation-result-unlinked" changed
                | WorkItemEventType.Created _ -> InternalOnly rawEventCase

    let private buildPolicyEvent (policyEvent: PolicyEvent) =
        let rawEventCase = RawEventCase.policy (getDiscriminatedUnionCaseName policyEvent.Event)
        let ownerId = ownerIdFromMetadata policyEvent.Metadata
        let organizationId = organizationIdFromMetadata policyEvent.Metadata
        let repositoryId = repositoryIdFromMetadata policyEvent.Metadata

        match policyEvent.Event with
        | PolicyEventType.SnapshotCreated snapshot ->
            let payload = createObject ()
            addString "policySnapshotId" snapshot.PolicySnapshotId payload
            addGuid "targetBranchId" snapshot.TargetBranchId payload
            addInt "policyVersion" snapshot.PolicyVersion payload
            addString "parserVersion" snapshot.ParserVersion payload
            addString "sourceHash" snapshot.SourceHash payload
            payload["ruleSummary"] <- ruleSummaryNode snapshot

            publish
                rawEventCase
                CanonicalEventName.PolicySnapshotCreated
                policyEvent.Metadata
                snapshot.OwnerId
                snapshot.OrganizationId
                snapshot.RepositoryId
                "Policy"
                snapshot.PolicySnapshotId
                payload
        | PolicyEventType.Acknowledged (policySnapshotId, acknowledgedBy, note) ->
            let payload = createObject ()
            addString "policySnapshotId" policySnapshotId payload
            addString "acknowledgedBy" acknowledgedBy payload
            addStringOption "note" note payload

            publish
                rawEventCase
                CanonicalEventName.PolicyAcknowledged
                policyEvent.Metadata
                ownerId
                organizationId
                repositoryId
                "Policy"
                policySnapshotId
                payload

    let private buildQueueEvent (queueEvent: PromotionQueueEvent) =
        let rawEventCase = RawEventCase.queue (getDiscriminatedUnionCaseName queueEvent.Event)
        let ownerId = ownerIdFromMetadata queueEvent.Metadata
        let organizationId = organizationIdFromMetadata queueEvent.Metadata
        let repositoryId = repositoryIdFromMetadata queueEvent.Metadata

        match queueEvent.Event with
        | PromotionQueueEventType.Initialized (targetBranchId, policySnapshotId) ->
            let payload = createObject ()
            addGuid "targetBranchId" targetBranchId payload
            addString "policySnapshotId" policySnapshotId payload
            publishGuid rawEventCase CanonicalEventName.QueueInitialized queueEvent.Metadata ownerId organizationId repositoryId "Queue" targetBranchId payload
        | _ ->
            match requireQueueBranchId rawEventCase queueEvent.Metadata with
            | Error outcome -> outcome
            | Ok targetBranchId ->
                let publishQueue eventName payload =
                    publishGuid rawEventCase eventName queueEvent.Metadata ownerId organizationId repositoryId "Queue" targetBranchId payload

                match queueEvent.Event with
                | PromotionQueueEventType.PromotionSetEnqueued promotionSetId ->
                    let payload = createObject ()
                    addGuid "targetBranchId" targetBranchId payload
                    addGuid "promotionSetId" promotionSetId payload
                    publishQueue CanonicalEventName.QueuePromotionSetEnqueued payload
                | PromotionQueueEventType.PromotionSetDequeued promotionSetId ->
                    let payload = createObject ()
                    addGuid "targetBranchId" targetBranchId payload
                    addGuid "promotionSetId" promotionSetId payload
                    publishQueue CanonicalEventName.QueuePromotionSetDequeued payload
                | PromotionQueueEventType.RunningPromotionSetSet _ -> InternalOnly rawEventCase
                | PromotionQueueEventType.Paused ->
                    let payload = createObject ()
                    addGuid "targetBranchId" targetBranchId payload
                    publishQueue CanonicalEventName.QueuePaused payload
                | PromotionQueueEventType.Resumed ->
                    let payload = createObject ()
                    addGuid "targetBranchId" targetBranchId payload
                    publishQueue CanonicalEventName.QueueResumed payload
                | PromotionQueueEventType.Degraded ->
                    let payload = createObject ()
                    addGuid "targetBranchId" targetBranchId payload
                    publishQueue CanonicalEventName.QueueDegraded payload
                | PromotionQueueEventType.PolicySnapshotUpdated policySnapshotId ->
                    let payload = createObject ()
                    addGuid "targetBranchId" targetBranchId payload
                    addString "policySnapshotId" policySnapshotId payload
                    publishQueue CanonicalEventName.QueuePolicySnapshotUpdated payload
                | PromotionQueueEventType.Initialized _ -> InternalOnly rawEventCase

    let private buildPromotionSetEvent (promotionSetEvent: PromotionSetEvent) =
        let rawEventCase = RawEventCase.promotionSet (getDiscriminatedUnionCaseName promotionSetEvent.Event)
        let ownerId = ownerIdFromMetadata promotionSetEvent.Metadata
        let organizationId = organizationIdFromMetadata promotionSetEvent.Metadata
        let repositoryId = repositoryIdFromMetadata promotionSetEvent.Metadata

        match promotionSetEvent.Event with
        | PromotionSetEventType.Created (createdPromotionSetId, createdOwnerId, createdOrganizationId, createdRepositoryId, targetBranchId) ->
            let payload = createObject ()
            addGuid "promotionSetId" createdPromotionSetId payload
            addGuid "targetBranchId" targetBranchId payload

            publishGuid
                rawEventCase
                CanonicalEventName.PromotionSetCreated
                promotionSetEvent.Metadata
                createdOwnerId
                createdOrganizationId
                createdRepositoryId
                "PromotionSet"
                createdPromotionSetId
                payload
        | PromotionSetEventType.Applied _ -> InternalOnly rawEventCase
        | _ ->
            match requireGuidFromMetadata "ActorId" rawEventCase promotionSetEvent.Metadata, requireBranchId rawEventCase promotionSetEvent.Metadata with
            | Ok promotionSetId, Ok targetBranchId ->
                let publishPromotionSet eventName payload =
                    publishGuid rawEventCase eventName promotionSetEvent.Metadata ownerId organizationId repositoryId "PromotionSet" promotionSetId payload

                match promotionSetEvent.Event with
                | PromotionSetEventType.InputPromotionsUpdated promotionPointers ->
                    let payload = createObject ()
                    addGuid "promotionSetId" promotionSetId payload
                    addGuid "targetBranchId" targetBranchId payload
                    payload["changed"] <- promotionPointersNode promotionPointers
                    publishPromotionSet CanonicalEventName.PromotionSetInputsUpdated payload
                | PromotionSetEventType.RecomputeStarted computedAgainstTerminal ->
                    let payload = createObject ()
                    addGuid "promotionSetId" promotionSetId payload
                    addGuid "targetBranchId" targetBranchId payload
                    addGuid "computedAgainstTerminalReferenceId" computedAgainstTerminal payload
                    publishPromotionSet CanonicalEventName.PromotionSetRecomputeStarted payload
                | PromotionSetEventType.StepsUpdated (steps, computedAgainstTerminal) ->
                    buildStepsUpdatedEnvelope
                        rawEventCase
                        promotionSetEvent.Metadata
                        ownerId
                        organizationId
                        repositoryId
                        promotionSetId
                        targetBranchId
                        computedAgainstTerminal
                        steps
                | PromotionSetEventType.RecomputeFailed (reason, computedAgainstTerminal) ->
                    let payload = createObject ()
                    addGuid "promotionSetId" promotionSetId payload
                    addGuid "targetBranchId" targetBranchId payload
                    addGuid "computedAgainstTerminalReferenceId" computedAgainstTerminal payload
                    addString "reason" reason payload
                    publishPromotionSet CanonicalEventName.PromotionSetRecomputeFailed payload
                | PromotionSetEventType.Blocked (reason, artifactId) ->
                    let payload = createObject ()
                    addGuid "promotionSetId" promotionSetId payload
                    addGuid "targetBranchId" targetBranchId payload
                    addString "reason" reason payload
                    addGuidOption "artifactId" artifactId payload
                    publishPromotionSet CanonicalEventName.PromotionSetBlocked payload
                | PromotionSetEventType.ApplyStarted ->
                    let payload = createObject ()
                    addGuid "promotionSetId" promotionSetId payload
                    addGuid "targetBranchId" targetBranchId payload
                    publishPromotionSet CanonicalEventName.PromotionSetApplyStarted payload
                | PromotionSetEventType.ApplyFailed reason ->
                    let payload = createObject ()
                    addGuid "promotionSetId" promotionSetId payload
                    addGuid "targetBranchId" targetBranchId payload
                    addString "reason" reason payload
                    publishPromotionSet CanonicalEventName.PromotionSetApplyFailed payload
                | PromotionSetEventType.LogicalDeleted (force, deleteReason) ->
                    let payload = createObject ()
                    addGuid "promotionSetId" promotionSetId payload
                    addBool "force" force payload
                    addString "deleteReason" deleteReason payload
                    publishPromotionSet CanonicalEventName.PromotionSetDeleted payload
                | PromotionSetEventType.Created _
                | PromotionSetEventType.Applied _ -> InternalOnly rawEventCase
            | Error outcome, _ -> outcome
            | _, Error outcome -> outcome

    let private buildValidationSetEvent (validationSetEvent: ValidationSetEvent) =
        let rawEventCase = RawEventCase.validationSet (getDiscriminatedUnionCaseName validationSetEvent.Event)
        let ownerId = ownerIdFromMetadata validationSetEvent.Metadata
        let organizationId = organizationIdFromMetadata validationSetEvent.Metadata
        let repositoryId = repositoryIdFromMetadata validationSetEvent.Metadata

        match validationSetEvent.Event with
        | ValidationSetEventType.Created validationSet
        | ValidationSetEventType.Updated validationSet ->
            let payload = createObject ()
            addGuid "validationSetId" validationSet.ValidationSetId payload
            addGuid "targetBranchId" validationSet.TargetBranchId payload
            payload["rulesSummary"] <- rulesSummaryNode validationSet.Rules
            payload["validationsSummary"] <- validationsSummaryNode validationSet.Validations

            let eventName =
                match validationSetEvent.Event with
                | ValidationSetEventType.Created _ -> CanonicalEventName.ValidationSetCreated
                | _ -> CanonicalEventName.ValidationSetUpdated

            publishGuid
                rawEventCase
                eventName
                validationSetEvent.Metadata
                validationSet.OwnerId
                validationSet.OrganizationId
                validationSet.RepositoryId
                "ValidationSet"
                validationSet.ValidationSetId
                payload
        | ValidationSetEventType.LogicalDeleted (force, deleteReason) ->
            match requireGuidFromMetadata "ActorId" rawEventCase validationSetEvent.Metadata with
            | Error outcome -> outcome
            | Ok validationSetId ->
                let payload = createObject ()
                addGuid "validationSetId" validationSetId payload
                addBool "force" force payload
                addString "deleteReason" deleteReason payload

                publishGuid
                    rawEventCase
                    CanonicalEventName.ValidationSetDeleted
                    validationSetEvent.Metadata
                    ownerId
                    organizationId
                    repositoryId
                    "ValidationSet"
                    validationSetId
                    payload

    let private buildValidationResultEvent (validationResultEvent: ValidationResultEvent) =
        let rawEventCase = RawEventCase.validationResult (getDiscriminatedUnionCaseName validationResultEvent.Event)

        match validationResultEvent.Event with
        | ValidationResultEventType.Recorded validationResult ->
            let payload = createObject ()
            addGuid "validationResultId" validationResult.ValidationResultId payload
            addString "status" (validationStatusToString validationResult.Output.Status) payload
            addString "summary" validationResult.Output.Summary payload
            addString "validationName" validationResult.ValidationName payload
            addString "validationVersion" validationResult.ValidationVersion payload
            addGuidOption "validationSetId" validationResult.ValidationSetId payload
            addGuidOption "promotionSetId" validationResult.PromotionSetId payload
            addGuidOption "promotionSetStepId" validationResult.PromotionSetStepId payload

            validationResult.StepsComputationAttempt
            |> Option.iter (fun attempt -> addInt "stepsComputationAttempt" attempt payload)

            let artifactIds = JsonArray()

            validationResult.Output.ArtifactIds
            |> List.iter (fun artifactId -> artifactIds.Add(JsonValue.Create(artifactId)))

            payload["artifactIds"] <- artifactIds

            publishGuid
                rawEventCase
                CanonicalEventName.ValidationResultRecorded
                validationResultEvent.Metadata
                validationResult.OwnerId
                validationResult.OrganizationId
                validationResult.RepositoryId
                "ValidationResult"
                validationResult.ValidationResultId
                payload

    let private buildReviewEvent (reviewEvent: ReviewEvent) =
        let rawEventCase = RawEventCase.review (getDiscriminatedUnionCaseName reviewEvent.Event)
        let ownerId = ownerIdFromMetadata reviewEvent.Metadata
        let organizationId = organizationIdFromMetadata reviewEvent.Metadata
        let repositoryId = repositoryIdFromMetadata reviewEvent.Metadata
        let reviewNotesId = tryGetGuid (nameof ReviewNotesId) reviewEvent.Metadata

        match reviewEvent.Event with
        | ReviewEventType.NotesUpserted notes ->
            publishGuid
                rawEventCase
                CanonicalEventName.ReviewNotesUpserted
                reviewEvent.Metadata
                notes.OwnerId
                notes.OrganizationId
                notes.RepositoryId
                "ReviewNotes"
                notes.ReviewNotesId
                (reviewNotesSummaryNode notes)
        | ReviewEventType.FindingResolved (findingId, resolutionState, resolvedBy, note) ->
            let payload = createObject ()
            addGuid "findingId" findingId payload
            addString "resolutionState" (enumName resolutionState) payload
            addString "resolvedBy" resolvedBy payload
            addStringOption "note" note payload
            addGuidOption "reviewNotesId" reviewNotesId payload

            publishGuid
                rawEventCase
                CanonicalEventName.ReviewFindingResolved
                reviewEvent.Metadata
                ownerId
                organizationId
                repositoryId
                "ReviewFinding"
                findingId
                payload
        | ReviewEventType.CheckpointAdded checkpoint ->
            let payload = createObject ()
            addGuid "reviewCheckpointId" checkpoint.ReviewCheckpointId payload
            addGuid "reviewedUpToReferenceId" checkpoint.ReviewedUpToReferenceId payload
            addString "policySnapshotId" checkpoint.PolicySnapshotId payload
            addString "reviewer" checkpoint.Reviewer payload
            addGuidOption "promotionSetId" checkpoint.PromotionSetId payload

            publishGuid
                rawEventCase
                CanonicalEventName.ReviewCheckpointAdded
                reviewEvent.Metadata
                ownerId
                organizationId
                repositoryId
                "ReviewCheckpoint"
                checkpoint.ReviewCheckpointId
                payload

    let private buildArtifactEvent (artifactEvent: ArtifactEvent) =
        let rawEventCase = RawEventCase.artifact (getDiscriminatedUnionCaseName artifactEvent.Event)

        match artifactEvent.Event with
        | ArtifactEventType.Created artifact ->
            let payload = createObject ()
            addGuid "artifactId" artifact.ArtifactId payload
            addString "artifactType" (artifactTypeToString artifact.ArtifactType) payload
            addString "mimeType" artifact.MimeType payload
            addInt64 "size" artifact.Size payload
            addString "blobPath" artifact.BlobPath payload
            addStringOption "sha256" artifact.Sha256 payload

            publishGuid
                rawEventCase
                CanonicalEventName.ArtifactCreated
                artifactEvent.Metadata
                artifact.OwnerId
                artifact.OrganizationId
                artifact.RepositoryId
                "Artifact"
                artifact.ArtifactId
                payload

    let buildGraceEvent (graceEvent: GraceEvent) =
        match graceEvent with
        | GraceEvent.OwnerEvent ownerEvent -> buildOwnerEvent ownerEvent
        | GraceEvent.OrganizationEvent organizationEvent -> buildOrganizationEvent organizationEvent
        | GraceEvent.RepositoryEvent repositoryEvent -> buildRepositoryEvent repositoryEvent
        | GraceEvent.BranchEvent branchEvent -> buildBranchEvent branchEvent
        | GraceEvent.ReferenceEvent referenceEvent -> buildReferenceEvent referenceEvent
        | GraceEvent.DirectoryVersionEvent directoryVersionEvent -> buildDirectoryVersionEvent directoryVersionEvent
        | GraceEvent.WorkItemEvent workItemEvent -> buildWorkItemEvent workItemEvent
        | GraceEvent.PolicyEvent policyEvent -> buildPolicyEvent policyEvent
        | GraceEvent.QueueEvent queueEvent -> buildQueueEvent queueEvent
        | GraceEvent.PromotionSetEvent promotionSetEvent -> buildPromotionSetEvent promotionSetEvent
        | GraceEvent.ValidationSetEvent validationSetEvent -> buildValidationSetEvent validationSetEvent
        | GraceEvent.ValidationResultEvent validationResultEvent -> buildValidationResultEvent validationResultEvent
        | GraceEvent.ReviewEvent reviewEvent -> buildReviewEvent reviewEvent
        | GraceEvent.ArtifactEvent artifactEvent -> buildArtifactEvent artifactEvent
