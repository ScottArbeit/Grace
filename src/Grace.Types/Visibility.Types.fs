namespace Grace.Types

open Grace.Shared.Utilities
open Orleans
open System
open System.Runtime.Serialization

/// Contains shared visibility and ownership contracts.
module Visibility =

    /// Defines the repository, branch, reference, and promotion-set audience values that Grace implements today.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ResourceVisibility =
        | Public
        | Private

        /// Keeps existing repository contracts public unless a caller explicitly chooses private behavior.
        static member Default = ResourceVisibility.Public

        /// Parses only implemented public visibility inputs; deferred values such as SecurityEmbargoed stay rejected.
        static member TryParsePublicInput(value: string) =
            match value with
            | null -> None
            | _ ->
                match value.Trim() with
                | "" -> None
                | "Public" -> Some ResourceVisibility.Public
                | "Private" -> Some ResourceVisibility.Private
                | candidate when String.Equals(candidate, "Public", StringComparison.OrdinalIgnoreCase) -> Some ResourceVisibility.Public
                | candidate when String.Equals(candidate, "Private", StringComparison.OrdinalIgnoreCase) -> Some ResourceVisibility.Private
                | _ -> None

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ResourceVisibility>()

        /// Returns the public case name used in query parameters, JSON, and diagnostics.
        override this.ToString() = getDiscriminatedUnionCaseName this

    /// Defines which principal family owns a branch, reference, or promotion-set workflow.
    [<KnownType("GetKnownTypes"); GenerateSerializer>]
    type ResourceOwnership =
        | RepositoryOwned
        | ContributorOwned

        /// Keeps existing repository-created work owned by the repository until contributor-owned behavior is requested.
        static member Default = ResourceOwnership.RepositoryOwned

        /// Parses only implemented public ownership inputs and rejects arbitrary contributor owner identifiers.
        static member TryParsePublicInput(value: string) =
            match value with
            | null -> None
            | _ ->
                match value.Trim() with
                | "" -> None
                | "RepositoryOwned" -> Some ResourceOwnership.RepositoryOwned
                | "ContributorOwned" -> Some ResourceOwnership.ContributorOwned
                | candidate when String.Equals(candidate, "RepositoryOwned", StringComparison.OrdinalIgnoreCase) -> Some ResourceOwnership.RepositoryOwned
                | candidate when String.Equals(candidate, "ContributorOwned", StringComparison.OrdinalIgnoreCase) -> Some ResourceOwnership.ContributorOwned
                | _ -> None

        /// Returns known nested union types for serializers.
        static member GetKnownTypes() = GetKnownTypes<ResourceOwnership>()

        /// Returns the public case name used in query parameters, JSON, and diagnostics.
        override this.ToString() = getDiscriminatedUnionCaseName this
