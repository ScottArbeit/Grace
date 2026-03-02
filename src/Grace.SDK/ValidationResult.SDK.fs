namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Validation
open Grace.Types.Validation

/// Client API for validation result endpoints.
type ValidationResult() =

    /// Records a validation result.
    static member public Record(parameters: RecordValidationResultParameters) =
        postServer<RecordValidationResultParameters, ValidationResultDto> (parameters |> ensureCorrelationIdIsSet, "validation-result/record")
