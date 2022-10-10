namespace Grace.Shared.Validation

open Grace.Shared
open Grace.Shared.Types
open Grace.Shared.Validation.Errors.Repository
open System

module Repository =

    let visibilityIsValid (visibility: string) (error: RepositoryError) =
        match Utilities.discriminatedUnionFromString<RepositoryVisibility>(visibility) with
        | Some visibility -> Ok ()
        | None -> Error error

    let daysIsValid (days: float) (error: RepositoryError) =
        if days < 0.0 || days >= 65536.0 then
            Error error
        else
            Ok ()