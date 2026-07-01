namespace Grace.Shared.Validation

open Grace.Shared
open Grace.Types.Common
open Grace.Shared.Validation.Errors
open System

/// Contains connect helpers.
module Connect =

    /// Validates that SaveDays is positive before repository connection settings are accepted.
    let saveDaysIsAPositiveNumber (saveDays: double) (error: ConnectError) = if saveDays < 0.0 then Error error else Ok()

    /// Validates that repository visibility names match the supported Grace visibility values.
    let visibilityIsValid (visibility: string) (error: ConnectError) =
        match Utilities.discriminatedUnionFromString<RepositoryType> (visibility) with
        | Some visibility -> Ok()
        | None -> Error error
