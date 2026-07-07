namespace Grace.Server.Security

open Grace.Types.Common
open Grace.Types.Visibility

/// Describes the caller facts that server visibility helpers need after route authentication and RBAC evaluation.
type VisibilityCallerAudience =
    {
        /// Authenticated Grace user id, when the route has one.
        UserId: UserId option
        /// Caller has the repository-level administrative audience that may observe contributor-owned hidden resources.
        HasRepositoryAdministration: bool
        /// Caller has the security-maintainer audience that may observe contributor-owned hidden resources.
        HasSecurityAdministration: bool
        /// Caller is an explicit reviewer for the resource being considered.
        IsExplicitReviewer: bool
        /// Caller is an explicit maintainer for the resource being considered.
        IsExplicitMaintainer: bool
    }

    /// Anonymous caller with no elevated or explicit resource audience.
    static member Anonymous =
        { UserId = None; HasRepositoryAdministration = false; HasSecurityAdministration = false; IsExplicitReviewer = false; IsExplicitMaintainer = false }

/// Carries the persisted visibility facts shared by branches, references, and promotion sets.
type VisibilityResource =
    {
        /// Public or private visibility recorded on the authoritative resource.
        Visibility: ResourceVisibility
        /// Repository-owned or contributor-owned visibility boundary recorded on the authoritative resource.
        Ownership: ResourceOwnership
        /// User that created or owns the contributor-owned resource, when known.
        CreatorUserId: UserId option
    }

/// Represents a route-specific missing response without forcing every route into the same 404 payload.
type RouteMissingModel<'Missing> =
    {
        /// Missing value that the owning route already returns for nonexistent resources.
        Missing: 'Missing
    }

/// Represents the route-aware result after applying hidden-as-missing visibility.
type HiddenAsMissingResult<'Resource, 'Missing> =
    /// The caller may observe the loaded resource.
    | Observable of 'Resource
    /// The route should return its existing missing-resource response.
    | RouteEquivalentMissing of RouteMissingModel<'Missing>

/// Describes the public list window after hidden candidates have been removed.
type VisibleWindow<'Resource> =
    {
        /// Visible items after the route limit is applied.
        Items: 'Resource list
        /// Count of visible candidates before route limit is applied.
        VisibleCount: int
        /// Latest visible candidate from the input order, when any exists.
        Latest: 'Resource option
        /// Indicates that more visible candidates exist beyond the returned item limit.
        HasMoreVisible: bool
    }

/// Contains reusable server-side helpers for Grace visibility authorization and no-oracle list shaping.
module VisibilityAuthorization =

    /// Determines whether the caller is part of the explicit or elevated audience for a contributor-owned hidden resource.
    let hasContributorAudience (caller: VisibilityCallerAudience) (resource: VisibilityResource) =
        let isCreator =
            match caller.UserId, resource.CreatorUserId with
            | Some callerUserId, Some creatorUserId -> callerUserId = creatorUserId
            | _ -> false

        isCreator
        || caller.IsExplicitReviewer
        || caller.IsExplicitMaintainer
        || caller.HasRepositoryAdministration
        || caller.HasSecurityAdministration

    /// Determines whether a caller may observe a branch after the route has loaded the authoritative branch facts.
    let canObserveBranch (caller: VisibilityCallerAudience) (branch: VisibilityResource) =
        match branch.Visibility, branch.Ownership with
        | ResourceVisibility.Public, _ -> true
        | _, ResourceOwnership.RepositoryOwned -> true
        | _, ResourceOwnership.ContributorOwned -> hasContributorAudience caller branch

    /// Determines whether a caller may observe a reference after the route has loaded its authoritative visibility facts.
    let canObserveReference (caller: VisibilityCallerAudience) (reference: VisibilityResource) = canObserveBranch caller reference

    /// Determines whether a caller may observe a promotion set after the route has loaded its authoritative visibility facts.
    let canObservePromotionSet (caller: VisibilityCallerAudience) (promotionSet: VisibilityResource) = canObserveBranch caller promotionSet

    /// Applies a route-specific missing model when an existing resource must be hidden from the caller.
    let hiddenAsMissing missingModel canObserve resource = if canObserve then Observable resource else RouteEquivalentMissing missingModel

    /// Applies branch visibility and returns the route's existing missing shape for hidden branches.
    let requireObservableBranch caller missingModel branch = hiddenAsMissing missingModel (canObserveBranch caller branch) branch

    /// Filters candidates before list limits, visible counts, and latest selection are calculated.
    let filterVisibleWindow limit canObserve candidates =
        let visible = candidates |> Seq.filter canObserve |> Seq.toList

        let limited: 'Resource list =
            match limit with
            | Some value when value > 0 -> visible |> List.truncate value
            | Some _ -> []
            | _ -> visible

        { Items = limited; VisibleCount = visible.Length; Latest = visible |> List.tryHead; HasMoreVisible = limited.Length < visible.Length }

    /// Counts only observable candidates so hidden rows cannot affect public count surfaces.
    let countVisible canObserve candidates = candidates |> Seq.filter canObserve |> Seq.length

    /// Selects the latest observable candidate from an already latest-ordered candidate sequence.
    let tryLatestVisible canObserve candidates = candidates |> Seq.tryFind canObserve

    /// Gates a projection so hidden resources cannot materialize into public DTOs, search rows, or event payloads.
    let gateProjection canObserve project resource = if canObserve resource then Some(project resource) else None

    /// Verifies that every resource on a traversal path remains observable before a downstream route follows it.
    let canTraverseRoot canObserve resources = resources |> Seq.forall canObserve
