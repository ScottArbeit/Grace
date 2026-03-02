namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Parameters.Validation
open Grace.Types.Validation

/// Client API for validation set endpoints.
type ValidationSet() =

    /// Creates a validation set.
    static member public Create(parameters: CreateValidationSetParameters) =
        postServer<CreateValidationSetParameters, string> (parameters |> ensureCorrelationIdIsSet, "validation-set/create")

    /// Gets a validation set by id.
    static member public Get(parameters: GetValidationSetParameters) =
        postServer<GetValidationSetParameters, ValidationSetDto> (parameters |> ensureCorrelationIdIsSet, "validation-set/get")

    /// Updates a validation set.
    static member public Update(parameters: UpdateValidationSetParameters) =
        postServer<UpdateValidationSetParameters, string> (parameters |> ensureCorrelationIdIsSet, "validation-set/update")

    /// Logically deletes a validation set.
    static member public Delete(parameters: DeleteValidationSetParameters) =
        postServer<DeleteValidationSetParameters, string> (parameters |> ensureCorrelationIdIsSet, "validation-set/delete")
