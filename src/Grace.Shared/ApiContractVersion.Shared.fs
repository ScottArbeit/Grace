namespace Grace.Shared

open System
open System.Globalization

/// Contains api contract version helpers.
[<RequireQualifiedAccess>]
module ApiContractVersion =

    [<Literal>]
    let DateFormat = "yyyy-MM-dd"

    [<Literal>]
    let CurrentReleased = "2023-10-01"

    [<Literal>]
    let Latest = "latest"

    [<Literal>]
    let Edge = "edge"

    let CurrentReleasedDate = DateOnly(2023, 10, 1)

    let ReleasedVersions = [| CurrentReleased |]

    let ExplicitOverrideAliases = [| Latest; Edge |]

    let private comparer = StringComparer.OrdinalIgnoreCase

    /// Compares contract version strings case-insensitively after normalization.
    let private equals expected actual = comparer.Equals(expected, actual)

    /// Indicates whether a contract version is released rather than preview-only.
    let isReleased value =
        not (String.IsNullOrWhiteSpace value)
        && ReleasedVersions
           |> Array.exists (fun released -> equals released (value.Trim()))

    /// Recognizes aliases that mean the caller intentionally selected an explicit contract version.
    let isExplicitOverrideAlias value =
        not (String.IsNullOrWhiteSpace value)
        && ExplicitOverrideAliases
           |> Array.exists (fun alias -> equals alias (value.Trim()))

    /// Checks whether a contract version is accepted by the current shared API surface.
    let isSupported value = isReleased value || isExplicitOverrideAlias value

    /// Attempts to parse date.
    let private tryParseDate value =
        let mutable parsed = DateOnly.MinValue

        if DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, &parsed) then
            Some parsed
        else
            None

    /// Normalizes normalize.
    let normalize value =
        if String.IsNullOrWhiteSpace value then
            Error "API contract version is required."
        else
            let trimmed = value.Trim()

            if equals CurrentReleased trimmed then
                Ok CurrentReleased
            elif equals Latest trimmed then
                Ok Latest
            elif equals Edge trimmed then
                Ok Edge
            else
                match tryParseDate trimmed with
                | Some _ -> Error $"Unsupported API contract version '{trimmed}'. Supported released version: {CurrentReleased}."
                | None -> Error $"Invalid API contract version '{trimmed}'. Use {DateFormat}, '{Latest}', or '{Edge}'."
