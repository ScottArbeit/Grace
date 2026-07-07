namespace Grace.Shared.Parameters

open Grace.Types.Visibility
open System

/// Contains visibility and ownership parameter helpers.
module Visibility =

    /// Carries visibility and ownership strings for public surfaces that explicitly implement those behaviors.
    type VisibilityOwnershipParameters() =
        /// Accepted visibility input for implemented public surfaces; deferred values such as security embargoes are rejected.
        member val public Visibility = String.Empty with get, set

        /// Accepted ownership input for implemented public surfaces; arbitrary contributor owner identifiers are rejected.
        member val public Ownership = String.Empty with get, set

        /// Attempts to parse the implemented visibility input without accepting deferred audience states.
        member this.TryParseVisibility() = ResourceVisibility.TryParsePublicInput this.Visibility

        /// Attempts to parse the implemented ownership input without accepting arbitrary contributor ids.
        member this.TryParseOwnership() = ResourceOwnership.TryParsePublicInput this.Ownership
