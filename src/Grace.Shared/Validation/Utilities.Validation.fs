namespace Grace.Shared.Validation

open FSharp.Control
open System.Threading.Tasks
open System

module Utilities =

    /// Returns the first validation that matches the predicate, or None if none match.
    let tryFind<'T> (predicate: 'T -> bool) (validations : ValueTask<'T> array) =
        task {
            return validations 
                   |> Seq.map (fun validation -> validation.Result)
                   |> Seq.tryFind predicate          
        }

    /// Retrieves the first error from a list of validations.
    let getFirstError (validations: ValueTask<Result<'T, 'TError>> array) =
        task {
            let! firstError = validations |> tryFind(fun validation -> Result.isError validation)
            return match firstError with
                   | Some result -> match result with | Ok _ -> None | Error error -> Some error   // This line can't return None, because we'll always have an error if we get here.
                   | None -> None
        }

    /// Checks if any of a list of validations fail.
    let anyFail validations =
        task {
            match! getFirstError validations with
            | Some _ -> return true
            | None -> return false
        }

    /// Checks that all validations in a list pass.
    let allPass validations =
        task {
            let! anyFail = anyFail validations
            return not anyFail
        }
