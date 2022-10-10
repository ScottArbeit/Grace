namespace Grace.Shared.Validation

open Grace.Shared
open System
open System.Collections.Generic
open System.Text.RegularExpressions
open Grace.Shared.Utilities

module Common =

    /// Validates that a string is a valid Grace name, as defined by the Regex in Grace.Shared.Constants.
    let stringIsValidGraceName<'T> (name: string) (error: 'T) =
        if String.IsNullOrEmpty(name) || Constants.GraceNameRegex.IsMatch(name) then
            Ok ()
        else
            Error error

    /// Validates that a string is a valid Guid and is Guid.Empty.
    let guidIsValidAndNotEmpty<'T> (s: string) (error: 'T) =
        if not <| String.IsNullOrEmpty(s) then
            let mutable guid = Guid.Empty
            if Guid.TryParse(s, &guid) && guid <> Guid.Empty then
                Ok ()
            else
                Error error
        else
            Ok ()
        
    /// Validates that a guid is not Guid.Empty.
    let guidIsNotEmpty<'T> (guid: Guid) (error: 'T) =
        if guid = Guid.Empty then
            Error error
        else
            Ok ()

    /// Validates that a string is not empty or null.
    let stringIsNotEmpty<'T> (s: string) (error: 'T) =
        if String.IsNullOrEmpty(s) then
            Error error
        else
            Ok ()

    /// Validates that a string is either empty or a valid partial or full SHA-256 hash value.
    ///
    /// Regex: ^[0-9a-fA-F]{2,64}$
    let stringIsEmptyOrValidSha256Hash<'T> (s:string) (error: 'T) =
        if String.IsNullOrEmpty(s) || Constants.Sha256Regex.IsMatch(s) then
            Ok ()
        else
            Error error

    /// Validates that a string is a valid partial or full SHA-256 hash value.
    ///
    /// Regex: ^[0-9a-fA-F]{2,64}$
    let stringIsValidSha256Hash<'T> (s:string) (error: 'T) =
        if Constants.Sha256Regex.IsMatch(s) then
            Ok ()
        else
            Error error

    /// Validates that a string is no longer than the specified length.
    let stringMaxLength (s: string) (maxLength: int) (error: 'T) =
        if s.Length > maxLength then
            Error error
        else
            Ok ()

    /// Validates that the given number is positive.
    let numberIsPositive<'T> (n: float) (error: 'T) =
        if n < 0.0 then
            Error error
        else
            Ok ()

    /// Validates that a number is found between the supplied lower and upper bounds.
    let numberIsWithinRange<'T, 'U when 'T : comparison> (n: 'T) (lower: 'T) (upper: 'T) (error: 'U) =
        if lower <= n && n <= upper then
            Ok ()
        else
            Error error
           
    /// Validates that a string is a member of the supplied discriminated union type.
    let isMemberOfDiscriminatedUnion<'T, 'U> (s: string) (error: 'U) =
        match Utilities.discriminatedUnionFromString<'T>(s) with
        | Some _ -> Ok ()
        | None -> Error error
        
    /// Validates that either the supplied id or name is not empty or null.
    let eitherIdOrNameMustBeProvided<'T> id name (error: 'T) =
        if String.IsNullOrEmpty(id) && String.IsNullOrEmpty(name) then
            Error error
        else
            Ok ()
    
    /// Validates that a list is non-empty.
    let listIsNonEmpty<'T, 'U> (list: IEnumerable<'T>) (error: 'U) =
        let xs = List<'T>(list)
        if xs.Count > 0 then
            Ok ()
        else
            Error error