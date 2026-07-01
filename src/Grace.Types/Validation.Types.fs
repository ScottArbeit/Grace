namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Artifact
open Grace.Types.Automation
open Grace.Types.Common
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

/// Contains validation helpers.
module Validation =

    /// Represents validation execution mode.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ValidationExecutionMode =
        | Synchronous
        | AsyncCallback

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ValidationExecutionMode>()

    /// Represents validation status.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ValidationStatus =
        | Pass
        | Fail
        | Block
        | Skipped

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ValidationStatus>()

    /// Represents the validation contract.
    [<GenerateSerializer>]
    type Validation = { Name: string; Version: string; ExecutionMode: ValidationExecutionMode; RequiredForApply: bool }

    /// Represents the validation set rule contract.
    [<GenerateSerializer>]
    type ValidationSetRule = { EventTypes: AutomationEventType list; BranchNameGlob: string }

    /// Represents validation set dto.
    [<GenerateSerializer>]
    type ValidationSetDto =
        {
            Class: string
            ValidationSetId: ValidationSetId
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            TargetBranchId: BranchId
            Rules: ValidationSetRule list
            Validations: Validation list
            OnBehalfOf: UserId list
            CreatedBy: UserId
            CreatedAt: Instant
            UpdatedAt: Instant option
            DeletedAt: Instant option
            DeleteReason: DeleteReason
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof ValidationSetDto
                ValidationSetId = ValidationSetId.Empty
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                TargetBranchId = BranchId.Empty
                Rules = []
                Validations = []
                OnBehalfOf = []
                CreatedBy = UserId String.Empty
                CreatedAt = Constants.DefaultTimestamp
                UpdatedAt = None
                DeletedAt = None
                DeleteReason = String.Empty
            }

    /// Represents validation set command.
    [<KnownType("GetKnownTypes")>]
    type ValidationSetCommand =
        | Create of validationSet: ValidationSetDto
        | Update of validationSet: ValidationSetDto
        | DeleteLogical of force: bool * deleteReason: DeleteReason

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ValidationSetCommand>()

    /// Represents validation set event type.
    [<KnownType("GetKnownTypes")>]
    type ValidationSetEventType =
        | Created of validationSet: ValidationSetDto
        | Updated of validationSet: ValidationSetDto
        | LogicalDeleted of force: bool * deleteReason: DeleteReason

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ValidationSetEventType>()

    /// Represents the validation set event contract.
    type ValidationSetEvent = { Event: ValidationSetEventType; Metadata: EventMetadata }

    /// Represents the validation output contract.
    [<GenerateSerializer>]
    type ValidationOutput = { Status: ValidationStatus; Summary: string; ArtifactIds: ArtifactId list }

    /// Represents validation result dto.
    [<GenerateSerializer>]
    type ValidationResultDto =
        {
            Class: string
            ValidationResultId: ValidationResultId
            OwnerId: OwnerId
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
            ValidationSetId: ValidationSetId option
            PromotionSetId: PromotionSetId option
            PromotionSetStepId: PromotionSetStepId option
            StepsComputationAttempt: int option
            ValidationName: string
            ValidationVersion: string
            Output: ValidationOutput
            OnBehalfOf: UserId list
            CreatedAt: Instant
            UpdatedAt: Instant option
        }

        /// Represents the deterministic default instance used when callers need an initialized contract value.
        static member Default =
            {
                Class = nameof ValidationResultDto
                ValidationResultId = ValidationResultId.Empty
                OwnerId = OwnerId.Empty
                OrganizationId = OrganizationId.Empty
                RepositoryId = RepositoryId.Empty
                ValidationSetId = None
                PromotionSetId = None
                PromotionSetStepId = None
                StepsComputationAttempt = None
                ValidationName = String.Empty
                ValidationVersion = String.Empty
                Output = { Status = ValidationStatus.Skipped; Summary = String.Empty; ArtifactIds = [] }
                OnBehalfOf = []
                CreatedAt = Constants.DefaultTimestamp
                UpdatedAt = None
            }

    /// Represents validation result command.
    [<KnownType("GetKnownTypes")>]
    type ValidationResultCommand =
        | Record of validationResult: ValidationResultDto

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ValidationResultCommand>()

    /// Represents validation result event type.
    [<KnownType("GetKnownTypes")>]
    type ValidationResultEventType =
        | Recorded of validationResult: ValidationResultDto

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ValidationResultEventType>()

    /// Represents the validation result event contract.
    type ValidationResultEvent = { Event: ValidationResultEventType; Metadata: EventMetadata }

    /// Contains validation set dto helpers.
    module ValidationSetDto =
        /// Carries optional validation fields that can be patched without replacing the full validation record.
        let UpdateDto (validationSetEvent: ValidationSetEvent) (current: ValidationSetDto) =
            let updated =
                match validationSetEvent.Event with
                | ValidationSetEventType.Created validationSet -> validationSet
                | ValidationSetEventType.Updated validationSet -> validationSet
                | ValidationSetEventType.LogicalDeleted (_, deleteReason) ->
                    { current with DeletedAt = Some(getCurrentInstant ()); DeleteReason = deleteReason }

            let onBehalfOf =
                updated.OnBehalfOf
                |> List.append [ UserId validationSetEvent.Metadata.Principal ]
                |> List.distinct

            { updated with OnBehalfOf = onBehalfOf; UpdatedAt = Some validationSetEvent.Metadata.Timestamp }

    /// Contains validation result dto helpers.
    module ValidationResultDto =
        /// Carries optional validation fields that can be patched without replacing the full validation record.
        let UpdateDto (validationResultEvent: ValidationResultEvent) (_current: ValidationResultDto) =
            match validationResultEvent.Event with
            | ValidationResultEventType.Recorded validationResult ->
                let onBehalfOf =
                    validationResult.OnBehalfOf
                    |> List.append [ UserId validationResultEvent.Metadata.Principal ]
                    |> List.distinct

                { validationResult with OnBehalfOf = onBehalfOf; UpdatedAt = Some validationResultEvent.Metadata.Timestamp }
