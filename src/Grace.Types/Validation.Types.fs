namespace Grace.Types

open Grace.Shared
open Grace.Shared.Utilities
open Grace.Types.Artifact
open Grace.Types.Automation
open Grace.Types.Types
open NodaTime
open Orleans
open System
open System.Runtime.Serialization

module Validation =

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ValidationExecutionMode =
        | Synchronous
        | AsyncCallback

        static member GetKnownTypes() = GetKnownTypes<ValidationExecutionMode>()

    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ValidationStatus =
        | Pass
        | Fail
        | Block
        | Skipped

        static member GetKnownTypes() = GetKnownTypes<ValidationStatus>()

    [<GenerateSerializer>]
    type Validation = { Name: string; Version: string; ExecutionMode: ValidationExecutionMode; RequiredForApply: bool }

    [<GenerateSerializer>]
    type ValidationSetRule = { EventTypes: AutomationEventType list; BranchNameGlob: string }

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

    [<KnownType("GetKnownTypes")>]
    type ValidationSetCommand =
        | Create of validationSet: ValidationSetDto
        | Update of validationSet: ValidationSetDto
        | DeleteLogical of force: bool * deleteReason: DeleteReason

        static member GetKnownTypes() = GetKnownTypes<ValidationSetCommand>()

    [<KnownType("GetKnownTypes")>]
    type ValidationSetEventType =
        | Created of validationSet: ValidationSetDto
        | Updated of validationSet: ValidationSetDto
        | LogicalDeleted of force: bool * deleteReason: DeleteReason

        static member GetKnownTypes() = GetKnownTypes<ValidationSetEventType>()

    type ValidationSetEvent = { Event: ValidationSetEventType; Metadata: EventMetadata }

    [<GenerateSerializer>]
    type ValidationOutput = { Status: ValidationStatus; Summary: string; ArtifactIds: ArtifactId list }

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

    [<KnownType("GetKnownTypes")>]
    type ValidationResultCommand =
        | Record of validationResult: ValidationResultDto

        static member GetKnownTypes() = GetKnownTypes<ValidationResultCommand>()

    [<KnownType("GetKnownTypes")>]
    type ValidationResultEventType =
        | Recorded of validationResult: ValidationResultDto

        static member GetKnownTypes() = GetKnownTypes<ValidationResultEventType>()

    type ValidationResultEvent = { Event: ValidationResultEventType; Metadata: EventMetadata }

    module ValidationSetDto =
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

    module ValidationResultDto =
        let UpdateDto (validationResultEvent: ValidationResultEvent) (_current: ValidationResultDto) =
            match validationResultEvent.Event with
            | ValidationResultEventType.Recorded validationResult ->
                let onBehalfOf =
                    validationResult.OnBehalfOf
                    |> List.append [ UserId validationResultEvent.Metadata.Principal ]
                    |> List.distinct

                { validationResult with OnBehalfOf = onBehalfOf; UpdatedAt = Some validationResultEvent.Metadata.Timestamp }
