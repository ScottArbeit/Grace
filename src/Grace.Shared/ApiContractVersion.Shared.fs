namespace Grace.Shared

open System
open System.Globalization

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

    let private equals expected actual = comparer.Equals(expected, actual)

    let isReleased value =
        not (String.IsNullOrWhiteSpace value)
        && ReleasedVersions
           |> Array.exists (fun released -> equals released (value.Trim()))

    let isExplicitOverrideAlias value =
        not (String.IsNullOrWhiteSpace value)
        && ExplicitOverrideAliases
           |> Array.exists (fun alias -> equals alias (value.Trim()))

    let isSupported value = isReleased value || isExplicitOverrideAlias value

    let private tryParseDate value =
        let mutable parsed = DateOnly.MinValue

        if DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, &parsed) then
            Some parsed
        else
            None

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
