namespace Grace.Shared.Validation

open FSharp.Control
open System.Threading.Tasks
open System

module Utilities =

    /// Returns the first validation that matches the predicate, or None if none match.
    let tryFindOld<'T> (predicate: 'T -> bool) (validations: ValueTask<'T> array) =
        task {
            match validations
                  |> Seq.tryFindIndex (fun validation -> predicate validation.Result)
                with
            | Some index -> return Some(validations[index].Result)
            | None -> return None
        }

    /// Returns the first validation that matches the predicate, or None if none match.
    let tryFind (predicate: 'T -> bool) (validations: ValueTask<'T> array) =
        task {
            let mutable i = 0
            let mutable first = -1

            while i < validations.Length && first = -1 do
                let! result = validations[i]
                if predicate result then first <- i
                i <- i + 1

            // Using .Result here is OK because it would already have been awaited in the while loop above.
            if first >= 0 then return Some(validations[first].Result) else return None
        }

    /// Retrieves the first error from a list of validations.
    let getFirstError (validations: ValueTask<Result<'T, 'TError>> array) =
        task {
            let! firstError =
                validations
                |> tryFind (fun validation -> Result.isError validation)

            return
                match firstError with
                | Some result ->
                    match result with
                    | Ok _ -> None
                    | Error error -> Some error // This line can't actually return None, because we'll always have an error if we get here.
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
