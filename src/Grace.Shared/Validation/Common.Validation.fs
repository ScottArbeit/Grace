namespace Grace.Shared.Validation

open Grace.Shared
open Grace.Shared.Utilities
open System
open System.Collections.Generic
open System.Text.RegularExpressions
open System.Threading.Tasks

module Common =

    module Guid =

        /// Validates that a string is a valid Guid, and is not Guid.Empty.
        let isValidAndNotEmpty<'T> (s: string) (error: 'T) =
            if not <| String.IsNullOrEmpty(s) then
                let mutable guid = Guid.Empty

                if Guid.TryParse(s, &guid) && guid <> Guid.Empty then
                    Ok() |> returnValueTask
                else
                    Error error |> returnValueTask
            else
                Ok() |> returnValueTask

        /// Validates that a guid is not Guid.Empty.
        let isNotEmpty<'T> (guid: Guid) (error: 'T) =
            if guid = Guid.Empty then
                Error error |> returnValueTask
            else
                Ok() |> returnValueTask

    module Number =

        /// Validates that the given number is positive.
        let isPositiveOrZero<'T> (n: double) (error: 'T) = if n >= 0.0 then Ok () |> returnValueTask else Error error |> returnValueTask

        /// Validates that a number is found between the supplied lower and upper bounds.
        let isWithinRange<'T, 'U when 'T: comparison> (n: 'T) (lower: 'T) (upper: 'T) (error: 'U) =
            if lower <= n && n <= upper then
                Ok() |> returnValueTask
            else
                Error error |> returnValueTask

    module String =

        /// Checks that the provided string is a valid Grace name (i.e. it matches GraceNameRegex).
        let isValidGraceName<'T> (name: string) (error: 'T) =
            if String.IsNullOrEmpty(name) || Constants.GraceNameRegex.IsMatch(name) then
                Ok() |> returnValueTask
            else
                Error error |> returnValueTask

        /// Validates that a string is not empty or null.
        let isNotEmpty<'T> (s: string) (error: 'T) =
            if String.IsNullOrEmpty(s) then
                Error error |> returnValueTask
            else
                Ok() |> returnValueTask

        /// Validates that a string is either empty or a valid partial or full SHA-256 hash value.
        ///
        /// Regex: ^[0-9a-fA-F]{2,64}$
        let isEmptyOrValidSha256Hash<'T> (s: string) (error: 'T) =
            if String.IsNullOrEmpty(s) || Constants.Sha256Regex.IsMatch(s) then
                Ok() |> returnValueTask
            else
                Error error |> returnValueTask

        /// Validates that a string is a valid partial or full SHA-256 hash value.
        ///
        /// Regex: ^[0-9a-fA-F]{2,64}$
        let isValidSha256Hash<'T> (s: string) (error: 'T) =
            if Constants.Sha256Regex.IsMatch(s) then
                Ok() |> returnValueTask
            else
                Error error |> returnValueTask

        /// Validates that a string is no longer than the specified length.
        let maxLength (s: string) (maxLength: int) (error: 'T) =
            if s.Length > maxLength then
                Error error |> returnValueTask
            else
                Ok() |> returnValueTask

    module DiscriminatedUnion =

        /// Validates that a string is a member of the supplied discriminated union type.
        let isMemberOf<'T, 'U> (s: string) (error: 'U) =
            match Utilities.discriminatedUnionFromString<'T> (s) with
            | Some _ -> Ok() |> returnValueTask
            | None -> Error error |> returnValueTask

    module Input =

        /// Validates that we have a value for either the supplied id, or the supplied name.
        let eitherIdOrNameMustBeProvided<'T> id name (error: 'T) =
            if String.IsNullOrEmpty(id) && String.IsNullOrEmpty(name) then
                Error error |> returnValueTask
            else
                Ok() |> returnValueTask

        /// Validates that a list is non-empty.
        let listIsNonEmpty<'T, 'U> (list: IEnumerable<'T>) (error: 'U) =
            let xs = List<'T>(list)

            if xs.Count > 0 then Ok() |> returnValueTask else Error error |> returnValueTask

        /// Validates that one of the values passed in the array is not null, if it's a string, it's not empty, and if it's a Guid, it's not Guid.Empty.
        let oneOfTheseValuesMustBeProvided (values: Object array) (error: 'T) =
            match
                values
                |> Array.tryFind (fun value ->
                    match value with
                    | null -> false
                    | :? string as s -> not <| String.IsNullOrEmpty(s)
                    | :? Guid as g -> g <> Guid.Empty
                    | _ -> true)
            with
            | Some _ -> Ok() |> returnValueTask
            | None -> Error error |> returnValueTask
