namespace Grace.Shared.Validation

open Grace.Shared
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Repository
open System

module Repository =

    /// Checks that the visibility value provided exists in the RepositoryVisibility type.
    let visibilityIsValid (visibility: string) (error: RepositoryError) =
        match Utilities.discriminatedUnionFromString<RepositoryVisibility>(visibility) with
        | Some visibility -> Ok () |> returnValueTask
        | None -> Error error |> returnValueTask

    /// Checks that the number of days is between 0.0 and 65536.0.
    let daysIsValid (days: double) (error: RepositoryError) =
        if days < 0.0 || days >= 65536.0 then
            Error error |> returnValueTask
        else
            Ok () |> returnValueTask
