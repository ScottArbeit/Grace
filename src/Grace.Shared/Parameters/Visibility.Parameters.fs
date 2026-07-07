namespace Grace.Shared.Parameters

open Grace.Types.Visibility

/// Contains visibility and ownership parameter helpers.
module Visibility =

    /// Attempts to parse implemented visibility input without forcing route parameter types to change base classes.
    let tryParseVisibilityInput (visibility: string) = ResourceVisibility.TryParsePublicInput visibility

    /// Attempts to parse implemented ownership input without accepting arbitrary contributor owner identifiers.
    let tryParseOwnershipInput (ownership: string) = ResourceOwnership.TryParsePublicInput ownership
