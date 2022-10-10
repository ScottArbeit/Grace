namespace Grace.Shared.Validation

open Grace.Shared
open Grace.Shared.Types
open Grace.Shared.Validation.Errors.Connect
open System

module Connect =

    let saveDaysIsAPositiveNumber (saveDays: float) (error: ConnectError) =
        if saveDays < 0.0 then
            Error error
        else
            Ok ()

    let visibilityIsValid (visibility: string) (error: ConnectError) =
        match Utilities.discriminatedUnionFromString<RepositoryVisibility>(visibility) with
        | Some visibility -> Ok ()
        | None -> Error error
