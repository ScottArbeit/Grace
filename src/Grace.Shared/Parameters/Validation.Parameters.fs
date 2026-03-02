namespace Grace.Shared.Parameters

open Grace.Shared.Parameters.Common
open Grace.Types.Validation
open System

module Validation =

    /// Base parameters for validation-set and validation-result endpoints.
    type ValidationParameters() =
        inherit CommonParameters()
        member val public OwnerId = String.Empty with get, set
        member val public OwnerName = String.Empty with get, set
        member val public OrganizationId = String.Empty with get, set
        member val public OrganizationName = String.Empty with get, set
        member val public RepositoryId = String.Empty with get, set
        member val public RepositoryName = String.Empty with get, set

    /// Parameters for /validation-set/create.
    type CreateValidationSetParameters() =
        inherit ValidationParameters()
        member val public ValidationSetId = String.Empty with get, set
        member val public TargetBranchId = String.Empty with get, set
        member val public Rules: ValidationSetRule list = [] with get, set
        member val public Validations: Validation list = [] with get, set

    /// Parameters for /validation-set/get.
    type GetValidationSetParameters() =
        inherit ValidationParameters()
        member val public ValidationSetId = String.Empty with get, set

    /// Parameters for /validation-set/update.
    type UpdateValidationSetParameters() =
        inherit ValidationParameters()
        member val public ValidationSetId = String.Empty with get, set
        member val public TargetBranchId = String.Empty with get, set
        member val public Rules: ValidationSetRule list = [] with get, set
        member val public Validations: Validation list = [] with get, set

    /// Parameters for /validation-set/delete.
    type DeleteValidationSetParameters() =
        inherit ValidationParameters()
        member val public ValidationSetId = String.Empty with get, set
        member val public Force = false with get, set
        member val public DeleteReason = String.Empty with get, set

    /// Parameters for /validation-result/record.
    type RecordValidationResultParameters() =
        inherit ValidationParameters()
        member val public ValidationResultId = String.Empty with get, set
        member val public ValidationSetId = String.Empty with get, set
        member val public PromotionSetId = String.Empty with get, set
        member val public PromotionSetStepId = String.Empty with get, set
        member val public StepsComputationAttempt = -1 with get, set
        member val public ValidationName = String.Empty with get, set
        member val public ValidationVersion = String.Empty with get, set
        member val public Status = String.Empty with get, set
        member val public Summary = String.Empty with get, set
        member val public ArtifactIds: string array = [||] with get, set
