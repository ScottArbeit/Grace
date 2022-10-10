namespace Grace.Shared.Validation

open System.Threading.Tasks

module Utilities =

    /// <summary>
    /// Retrieves the first error from a list of validations.
    /// </summary>
    /// <param name="validations">A list of Result values.</param>
    /// <remarks>
    /// This function is written in procedural style to prevent having to call .Result on a Task. I tried Array.tryPick, but the chooser function there can't return a Task. Performance should be identical.
    /// </remarks>
    let getFirstError (validations: Task<Result<'T, 'TError>>[]) =
        task {
            let mutable firstError: 'TError option = None
            let mutable i: int = 0
            while firstError.IsNone && i < validations.Length do
                match! validations[i] with 
                | Ok _ -> ()
                | Error error -> firstError <- Some error
                i <- i + 1
            return firstError
        }

    /// <summary>
    /// Checks if any of a list of validations fail.
    ///</summary>
    /// <param name="validations">A list of Result values.</param>
    let haveError validations =
        task {
            let! validationResult = validations |> getFirstError
            return validationResult |> Option.isSome
        }

    /// <summary>
    /// Checks that all validations in a list pass.
    /// </summary>
    /// <param name="validations">A list of Result values.</param>
    let areValid validations =
        task {
            let! validationResult = validations |> getFirstError
            return validationResult |> Option.isNone
        }
