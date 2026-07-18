namespace Grace.Types

open Grace.Types.Common
open NodaTime
open Orleans
open System
open System.Collections.Generic

/// Contains Grace Cache enrollment, identity, and registration lifecycle contracts.
module CacheRegistration =

    /// Identifies whether a Cache enrollment is administered for one Owner or one Organization.
    type CacheBoundaryKind =
        | Owner = 1
        | Organization = 2

    /// Identifies the operational health reported by a Cache during authenticated refresh.
    type CacheHealthStatus =
        | Healthy = 1
        | Unhealthy = 2

    /// Represents the only optional Cache software capability currently modeled by Grace.
    [<RequireQualifiedAccess>]
    module Capability =
        /// Allows a Cache to receive future prefetch policy work; read-through is mandatory for every current Cache.
        [<Literal>]
        let Prefetch = "prefetch"

    /// Provides server-owned Cache registration timing defaults.
    [<RequireQualifiedAccess>]
    module RegistrationLifetime =
        /// The active lifetime renewed by a healthy authenticated Cache refresh.
        let ActiveLifetime = Duration.FromHours 2
        /// The earliest normal interval at which a Cache may refresh its registration.
        let RefreshAfter = Duration.FromHours 1
        /// The default Cache host key-rotation schedule advertised by the server contract.
        let KeyRotationInterval = Duration.FromHours 4

        /// Supplies the default accepted cache identity-key rotation interval in minutes.
        [<Literal>]
        let DefaultRotationIntervalMinutes = 240

        /// Defines the inclusive lower bound for the cache rotation interval supplied by the machine runtime.
        [<Literal>]
        let MinimumRotationIntervalMinutes = 15

        /// Defines the inclusive upper bound for the cache rotation interval supplied by the machine runtime.
        [<Literal>]
        let MaximumRotationIntervalMinutes = 10080

    /// Carries the canonical public half of a Cache identity P-256 key. No private material appears in this contract.
    [<CLIMutable; GenerateSerializer>]
    type CacheIdentityPublicKey =
        {
            Class: string
            Algorithm: string
            Curve: string
            PublicKeyX: string
            PublicKeyY: string
        }
        /// Builds a canonical Cache identity public key from base64url P-256 coordinates.
        static member Create(publicKeyX: string, publicKeyY: string) =
            { Class = nameof CacheIdentityPublicKey; Algorithm = "ES256"; Curve = "P-256"; PublicKeyX = publicKeyX; PublicKeyY = publicKeyY }

    /// Identifies one stable repository assignment together with its organization needed to load its authoritative record.
    [<CLIMutable; GenerateSerializer>]
    type CacheRepositoryScope =
        {
            Class: string
            OrganizationId: OrganizationId
            RepositoryId: RepositoryId
        }
        /// Builds one explicit repository scope without permitting name-based or wildcard assignment.
        static member Create(organizationId: OrganizationId, repositoryId: RepositoryId) =
            { Class = nameof CacheRepositoryScope; OrganizationId = organizationId; RepositoryId = repositoryId }

    /// Carries the signed canonical statement that authenticates one Cache runtime operation.
    [<CLIMutable; GenerateSerializer>]
    type CacheRequestProofPayload =
        {
            Class: string
            CacheId: Guid
            Operation: string
            RequestDigest: string
            IssuedAt: Instant
        }
        /// Builds a protocol-millisecond Cache proof payload for one exact operation and request digest.
        static member Create(cacheId: Guid, operation: string, requestDigest: string, issuedAt: Instant) =
            {
                Class = nameof CacheRequestProofPayload
                CacheId = cacheId
                Operation = operation
                RequestDigest = requestDigest
                IssuedAt = Instant.FromUnixTimeMilliseconds(issuedAt.ToUnixTimeMilliseconds())
            }

    /// Carries a Cache identity signature over one canonical runtime-operation payload.
    [<CLIMutable; GenerateSerializer>]
    type SignedCacheRequestProof =
        {
            Class: string
            Payload: CacheRequestProofPayload
            Signature: string
        }
        /// Builds the Cache proof envelope without exposing private-key material.
        static member Create(payload: CacheRequestProofPayload, signature: string) =
            { Class = nameof SignedCacheRequestProof; Payload = payload; Signature = signature }

    /// Represents administrator-authenticated enrollment of one Cache for an explicit repository boundary.
    [<CLIMutable; GenerateSerializer>]
    type CacheEnrollmentRequest =
        {
            Class: string
            DisplayName: string
            BoundaryKind: CacheBoundaryKind
            OwnerId: OwnerId
            OrganizationId: OrganizationId option
            RepositoryScopes: List<CacheRepositoryScope>
            PublicKey: CacheIdentityPublicKey
            Endpoint: string
            AllowHttpEndpoint: bool
            Health: CacheHealthStatus
            SoftwareVersion: string
            ProtocolVersion: string
            PrefetchSupported: bool
        }

    /// Represents one cache-authenticated operational refresh. It cannot carry administration or identity fields.
    [<CLIMutable; GenerateSerializer>]
    type CacheRegistrationRefreshRequest =
        {
            Class: string
            CacheId: Guid
            Endpoint: string
            Health: CacheHealthStatus
            SoftwareVersion: string
            ProtocolVersion: string
            PrefetchSupported: bool
            ObservedAt: Instant
            Proof: SignedCacheRequestProof
        }

    /// Represents the administrator-authenticated replacement of the exact repository assignment set.
    [<CLIMutable; GenerateSerializer>]
    type CacheRepositoryAssignmentRequest = { Class: string; CacheId: Guid; RepositoryScopes: List<CacheRepositoryScope> }

    /// Represents the administrator-authenticated terminal revocation of one Cache identity.
    [<CLIMutable; GenerateSerializer>]
    type CacheRevocationRequest = { Class: string; CacheId: Guid }

    /// Represents active-key-proven submission of the one candidate Cache identity key that may be promoted by a later candidate proof.
    [<CLIMutable; GenerateSerializer>]
    type CacheKeyCandidateRequest =
        {
            Class: string
            CacheId: Guid
            CandidatePublicKey: CacheIdentityPublicKey
            RotationIntervalMinutes: int
            IsStartup: bool
            Proof: SignedCacheRequestProof
        }

    /// Represents the server-owned durable registration for one Cache identity.
    [<CLIMutable; GenerateSerializer>]
    type CacheRegistration =
        {
            Class: string
            CacheId: Guid
            DisplayName: string
            BoundaryKind: CacheBoundaryKind
            OwnerId: OwnerId
            OrganizationId: OrganizationId option
            RepositoryScopes: CacheRepositoryScope array
            ActivePublicKey: CacheIdentityPublicKey
            CandidatePublicKey: CacheIdentityPublicKey option
            Endpoint: string
            AllowHttpEndpoint: bool
            Health: CacheHealthStatus
            SoftwareVersion: string
            ProtocolVersion: string
            PrefetchSupported: bool
            EnrolledBy: string
            EnrolledAt: Instant
            LastRefreshedAt: Instant
            RefreshAfter: Instant
            ExpiresAt: Instant
            RotationIntervalMinutes: int
            LastRotatedAt: Instant option
            RotationDueAt: Instant
            RevokedAt: Instant option
        }

    /// Represents the outcome of a Cache lifecycle operation.
    type CacheRegistrationRefreshStatus =
        | Enrolled = 1
        | Refreshed = 2
        | RefreshNotDue = 3
        | Expired = 4
        | NotFound = 5
        | Revoked = 6
        | Updated = 7
        | CandidateAccepted = 8
        | EndpointMismatch = 9
        | RotationRetryAfter = 10

    /// Represents a server response for Cache enrollment and lifecycle operations.
    [<CLIMutable; GenerateSerializer>]
    type CacheRegistrationResult =
        {
            Class: string
            Status: CacheRegistrationRefreshStatus
            Registration: CacheRegistration option
            Message: string
            RetryAfterSeconds: int option
        }
        /// Builds a Cache lifecycle result without leaking private key material.
        static member Create(status, registration, message, ?retryAfterSeconds) =
            { Class = nameof CacheRegistrationResult; Status = status; Registration = registration; Message = message; RetryAfterSeconds = retryAfterSeconds }

    /// Represents the exact repository and optional prefetch capability required during plan selection.
    [<CLIMutable; GenerateSerializer>]
    type CacheRegistrationSelectionQuery =
        {
            Class: string
            RepositoryId: RepositoryId option
            RequirePrefetch: bool
        }
        /// Builds a current-Cache selection query from stable repository identity.
        static member Create(repositoryId: RepositoryId option, requirePrefetch: bool) =
            { Class = nameof CacheRegistrationSelectionQuery; RepositoryId = repositoryId; RequirePrefetch = requirePrefetch }

        /// Represents an unconstrained current-registration query.
        static member Current = CacheRegistrationSelectionQuery.Create(None, false)

    /// Represents all Cache registration records owned by the singleton Cache registration actor.
    [<CLIMutable; GenerateSerializer>]
    type CacheRegistrationState =
        {
            Class: string
            Registrations: CacheRegistration array
        }
        /// Represents the empty durable Cache registration state.
        static member Empty = { Class = nameof CacheRegistrationState; Registrations = Array.empty }

    /// Contains deterministic Cache registration validation and state transitions.
    module Lifecycle =
        /// Returns true when a Cache registration is active, healthy, unrevoked, and unexpired at server time.
        let isCurrentAt (now: Instant) (registration: CacheRegistration) =
            not (isNull (box registration))
            && registration.RevokedAt.IsNone
            && registration.Health = CacheHealthStatus.Healthy
            && match Uri.TryCreate(registration.Endpoint, UriKind.Absolute) with
               | true, endpoint when endpoint.Scheme = Uri.UriSchemeHttps -> not registration.AllowHttpEndpoint
               | true, endpoint when endpoint.Scheme = Uri.UriSchemeHttp -> registration.AllowHttpEndpoint
               | _ -> false
            && now < registration.ExpiresAt

        /// Validates an exact, non-empty, canonical, and duplicate-free Cache repository assignment set before authorization or mutation.
        let validateRepositoryScopes (repositoryScopes: CacheRepositoryScope seq) =
            let scopes =
                if isNull (box repositoryScopes) then
                    Array.empty
                else
                    repositoryScopes |> Seq.toArray

            if scopes.Length = 0 then
                Error "RepositoryScopes must include at least one repository."
            elif scopes
                 |> Array.exists (fun scope ->
                     isNull (box scope)
                     || scope.Class <> nameof CacheRepositoryScope
                     || scope.OrganizationId = Guid.Empty
                     || scope.RepositoryId = Guid.Empty) then
                Error "RepositoryScopes must contain canonical organization and repository ids."
            elif scopes
                 |> Array.map (fun scope -> scope.RepositoryId)
                 |> Array.distinct
                 |> Array.length
                 <> scopes.Length then
                Error "RepositoryScopes must not include duplicate repositories."
            else
                Ok()

        /// Returns true only when a Cache health value has a named public and durable meaning.
        let isDefinedHealth =
            function
            | CacheHealthStatus.Healthy
            | CacheHealthStatus.Unhealthy -> true
            | _ -> false

        /// Validates exact enrollment input before caller authorization or actor mutation.
        let validateEnrollmentRequest (request: CacheEnrollmentRequest) =
            let errors = ResizeArray<string>()

            if isNull (box request) then
                errors.Add("CacheEnrollmentRequest is required.")
            else
                if request.Class <> nameof CacheEnrollmentRequest then
                    errors.Add("Class must be CacheEnrollmentRequest.")

                if not (isDefinedHealth request.Health) then
                    errors.Add("Health must be Healthy or Unhealthy.")

                if String.IsNullOrWhiteSpace request.DisplayName then
                    errors.Add("DisplayName is required.")

                if request.OwnerId = Guid.Empty then errors.Add("OwnerId is required.")

                match request.BoundaryKind, request.OrganizationId with
                | CacheBoundaryKind.Owner, None -> ()
                | CacheBoundaryKind.Organization, Some organizationId when organizationId <> Guid.Empty -> ()
                | CacheBoundaryKind.Owner, Some _ -> errors.Add("Owner boundary must not include OrganizationId.")
                | _ -> errors.Add("Organization boundary requires OrganizationId.")

                match validateRepositoryScopes request.RepositoryScopes with
                | Error error -> errors.Add error
                | Ok () -> ()

                if isNull (box request.PublicKey) then errors.Add("PublicKey is required.")

                if String.IsNullOrWhiteSpace request.Endpoint then
                    errors.Add("Endpoint is required.")
                else
                    match Uri.TryCreate(request.Endpoint, UriKind.Absolute) with
                    | true, uri when
                        uri.Scheme = Uri.UriSchemeHttps
                        && not request.AllowHttpEndpoint
                        ->
                        ()
                    | true, uri when
                        uri.Scheme = Uri.UriSchemeHttp
                        && request.AllowHttpEndpoint
                        ->
                        ()
                    | true, uri when uri.Scheme = Uri.UriSchemeHttps -> errors.Add("AllowHttpEndpoint may be selected only for an HTTP Endpoint.")
                    | true, uri when uri.Scheme = Uri.UriSchemeHttp ->
                        errors.Add("Endpoint must be an absolute HTTPS URI unless AllowHttpEndpoint is explicitly selected.")
                    | _ -> errors.Add("Endpoint must be an absolute HTTP or HTTPS URI.")

                if String.IsNullOrWhiteSpace request.SoftwareVersion then
                    errors.Add("SoftwareVersion is required.")

                if String.IsNullOrWhiteSpace request.ProtocolVersion then
                    errors.Add("ProtocolVersion is required.")

            if errors.Count = 0 then Ok() else Error(List.ofSeq errors)

        /// Validates refresh input before proof admission and state mutation.
        let validateRefreshRequest (request: CacheRegistrationRefreshRequest) =
            let errors = ResizeArray<string>()

            if isNull (box request) then
                errors.Add("CacheRegistrationRefreshRequest is required.")
            else
                if request.Class
                   <> nameof CacheRegistrationRefreshRequest then
                    errors.Add("Class must be CacheRegistrationRefreshRequest.")

                if not (isDefinedHealth request.Health) then
                    errors.Add("Health must be Healthy or Unhealthy.")

                if request.CacheId = Guid.Empty then errors.Add("CacheId is required.")
                if isNull (box request.Proof) then errors.Add("Proof is required.")

                if String.IsNullOrWhiteSpace request.Endpoint then
                    errors.Add("Endpoint is required.")
                else
                    match Uri.TryCreate(request.Endpoint, UriKind.Absolute) with
                    | true, uri when
                        uri.Scheme = Uri.UriSchemeHttps
                        || uri.Scheme = Uri.UriSchemeHttp
                        ->
                        ()
                    | _ -> errors.Add("Endpoint must be an absolute HTTP or HTTPS URI.")

                if String.IsNullOrWhiteSpace request.SoftwareVersion then
                    errors.Add("SoftwareVersion is required.")

                if String.IsNullOrWhiteSpace request.ProtocolVersion then
                    errors.Add("ProtocolVersion is required.")

            if errors.Count = 0 then Ok() else Error(List.ofSeq errors)

        /// Inserts one immutable CacheId registration after the server has authenticated and authorized enrollment.
        let enroll (state: CacheRegistrationState) (cacheId: Guid) (request: CacheEnrollmentRequest) (enrolledBy: string) (now: Instant) =
            let current = if isNull (box state) then CacheRegistrationState.Empty else state

            let registration =
                {
                    Class = nameof CacheRegistration
                    CacheId = cacheId
                    DisplayName = request.DisplayName.Trim()
                    BoundaryKind = request.BoundaryKind
                    OwnerId = request.OwnerId
                    OrganizationId = request.OrganizationId
                    RepositoryScopes = request.RepositoryScopes |> Seq.toArray
                    ActivePublicKey = request.PublicKey
                    CandidatePublicKey = None
                    Endpoint = request.Endpoint.Trim()
                    AllowHttpEndpoint = request.AllowHttpEndpoint
                    Health = request.Health
                    SoftwareVersion = request.SoftwareVersion.Trim()
                    ProtocolVersion = request.ProtocolVersion.Trim()
                    PrefetchSupported = request.PrefetchSupported
                    EnrolledBy = enrolledBy
                    EnrolledAt = now
                    LastRefreshedAt = now
                    RefreshAfter = now.Plus RegistrationLifetime.RefreshAfter
                    ExpiresAt = now.Plus RegistrationLifetime.ActiveLifetime
                    RotationIntervalMinutes = RegistrationLifetime.DefaultRotationIntervalMinutes
                    LastRotatedAt = None
                    RotationDueAt = now.Plus(RegistrationLifetime.KeyRotationInterval)
                    RevokedAt = None
                }

            if current.Registrations
               |> Array.exists (fun existing -> existing.CacheId = cacheId) then
                current, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.NotFound, None, "CacheId already exists.")
            else
                { Class = nameof CacheRegistrationState; Registrations = Array.append current.Registrations [| registration |] },
                CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Enrolled, Some registration, "Cache enrollment is current.")

        /// Extends only operational facts for a current Cache after its existing registration has been loaded and proof-validated.
        let refresh (state: CacheRegistrationState) (request: CacheRegistrationRefreshRequest) (now: Instant) =
            let current = if isNull (box state) then CacheRegistrationState.Empty else state

            match current.Registrations
                  |> Array.tryFind (fun registration -> registration.CacheId = request.CacheId)
                with
            | None -> current, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.NotFound, None, "Cache registration was not found.")
            | Some registration when registration.RevokedAt.IsSome ->
                current, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Revoked, Some registration, "Cache registration is revoked.")
            | Some registration when now >= registration.ExpiresAt ->
                current,
                CacheRegistrationResult.Create(
                    CacheRegistrationRefreshStatus.Expired,
                    Some registration,
                    "Cache registration is expired and must enroll again."
                )
            | Some registration when request.ObservedAt <= registration.LastRefreshedAt ->
                current,
                CacheRegistrationResult.Create(
                    CacheRegistrationRefreshStatus.Expired,
                    Some registration,
                    "Cache refresh is stale and must not update registration state."
                )
            | Some registration when not (String.Equals(registration.Endpoint, request.Endpoint.Trim(), StringComparison.Ordinal)) ->
                current,
                CacheRegistrationResult.Create(
                    CacheRegistrationRefreshStatus.EndpointMismatch,
                    Some registration,
                    "Cache refresh Endpoint must exactly match the registered endpoint."
                )
            | Some registration when
                now < registration.RefreshAfter
                && request.Health = CacheHealthStatus.Unhealthy
                && registration.Health <> CacheHealthStatus.Unhealthy
                ->
                let unhealthy = { registration with Health = CacheHealthStatus.Unhealthy }

                let next =
                    {
                        Class = nameof CacheRegistrationState
                        Registrations =
                            current.Registrations
                            |> Array.map (fun existing -> if existing.CacheId = request.CacheId then unhealthy else existing)
                    }

                next,
                CacheRegistrationResult.Create(
                    CacheRegistrationRefreshStatus.Refreshed,
                    Some unhealthy,
                    "Cache health was downgraded immediately without extending operational freshness."
                )
            | Some registration when now < registration.RefreshAfter ->
                current,
                CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.RefreshNotDue, Some registration, "Cache registration refresh is not due yet.")
            | Some registration ->
                let refreshed =
                    { registration with
                        Health = request.Health
                        SoftwareVersion = request.SoftwareVersion.Trim()
                        ProtocolVersion = request.ProtocolVersion.Trim()
                        PrefetchSupported = request.PrefetchSupported
                        LastRefreshedAt = now
                        RefreshAfter = now.Plus RegistrationLifetime.RefreshAfter
                        ExpiresAt = now.Plus RegistrationLifetime.ActiveLifetime
                    }

                let next =
                    {
                        Class = nameof CacheRegistrationState
                        Registrations =
                            current.Registrations
                            |> Array.map (fun existing -> if existing.CacheId = request.CacheId then refreshed else existing)
                    }

                next, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Refreshed, Some refreshed, "Cache registration was refreshed.")

        /// Replaces the explicit repository assignment set without changing Cache identity or operational facts.
        let updateAssignments (state: CacheRegistrationState) (cacheId: Guid) (repositoryScopes: CacheRepositoryScope seq) =
            let current = if isNull (box state) then CacheRegistrationState.Empty else state

            let assignmentScopes =
                if isNull (box repositoryScopes) then
                    Array.empty
                else
                    repositoryScopes |> Seq.toArray

            if validateRepositoryScopes assignmentScopes
               |> Result.isError then
                current,
                CacheRegistrationResult.Create(
                    CacheRegistrationRefreshStatus.NotFound,
                    None,
                    "Repository assignments must contain one or more unique canonical repository scopes."
                )
            else
                match current.Registrations
                      |> Array.tryFind (fun registration -> registration.CacheId = cacheId)
                    with
                | None -> current, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.NotFound, None, "Cache registration was not found.")
                | Some registration ->
                    let updated = { registration with RepositoryScopes = assignmentScopes }

                    let next =
                        {
                            Class = nameof CacheRegistrationState
                            Registrations =
                                current.Registrations
                                |> Array.map (fun existing -> if existing.CacheId = cacheId then updated else existing)
                        }

                    next, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Updated, Some updated, "Cache repository assignments were updated.")

        /// Revokes one Cache so it can never receive new plan selection or grants.
        let revoke (state: CacheRegistrationState) (cacheId: Guid) (now: Instant) =
            let current = if isNull (box state) then CacheRegistrationState.Empty else state

            match current.Registrations
                  |> Array.tryFind (fun registration -> registration.CacheId = cacheId)
                with
            | None -> current, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.NotFound, None, "Cache registration was not found.")
            | Some registration when registration.RevokedAt.IsSome ->
                current, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Revoked, Some registration, "Cache registration is already revoked.")
            | Some registration ->
                let revoked = { registration with RevokedAt = Some now }

                let next =
                    {
                        Class = nameof CacheRegistrationState
                        Registrations =
                            current.Registrations
                            |> Array.map (fun existing -> if existing.CacheId = cacheId then revoked else existing)
                    }

                next, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Revoked, Some revoked, "Cache registration was revoked.")

        /// Accepts the exact canonical candidate public key once after proof by the currently active key.
        let submitCandidate (state: CacheRegistrationState) (request: CacheKeyCandidateRequest) (now: Instant) =
            let current = if isNull (box state) then CacheRegistrationState.Empty else state

            match current.Registrations
                  |> Array.tryFind (fun registration -> registration.CacheId = request.CacheId)
                with
            | None -> current, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.NotFound, None, "Cache registration was not found.")
            | Some registration when registration.RevokedAt.IsSome ->
                current, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Revoked, Some registration, "Cache registration is revoked.")
            | Some registration when now >= registration.ExpiresAt ->
                current,
                CacheRegistrationResult.Create(
                    CacheRegistrationRefreshStatus.Expired,
                    Some registration,
                    "Cache registration is expired and must enroll again."
                )
            | Some registration when
                request.RotationIntervalMinutes < RegistrationLifetime.MinimumRotationIntervalMinutes
                || request.RotationIntervalMinutes > RegistrationLifetime.MaximumRotationIntervalMinutes
                ->
                current,
                CacheRegistrationResult.Create(
                    CacheRegistrationRefreshStatus.NotFound,
                    Some registration,
                    "Cache rotation interval is outside the accepted range."
                )
            | Some registration when
                request.IsStartup
                && (registration.LastRotatedAt
                    |> Option.exists (fun lastRotated -> now < lastRotated.Plus(Duration.FromMinutes 1.0)))
                ->
                let retryAfter =
                    registration.LastRotatedAt
                    |> Option.map (fun lastRotated ->
                        max
                            1
                            (int
                                (lastRotated.Plus(Duration.FromMinutes 1.0) - now)
                                    .TotalSeconds))
                    |> Option.defaultValue 60

                current,
                CacheRegistrationResult.Create(
                    CacheRegistrationRefreshStatus.RotationRetryAfter,
                    Some registration,
                    "A completed startup rotation is not yet eligible for this CacheId.",
                    retryAfter
                )
            | Some registration when registration.CandidatePublicKey.IsSome ->
                let candidate = registration.CandidatePublicKey.Value

                if candidate = request.CandidatePublicKey then
                    current,
                    CacheRegistrationResult.Create(
                        CacheRegistrationRefreshStatus.CandidateAccepted,
                        Some registration,
                        "Cache identity candidate is already accepted."
                    )
                else
                    current,
                    CacheRegistrationResult.Create(
                        CacheRegistrationRefreshStatus.NotFound,
                        Some registration,
                        "A different Cache identity candidate remains unresolved."
                    )
            | Some registration ->
                let submitted =
                    { registration with CandidatePublicKey = Some request.CandidatePublicKey; RotationIntervalMinutes = request.RotationIntervalMinutes }

                let next =
                    {
                        Class = nameof CacheRegistrationState
                        Registrations =
                            current.Registrations
                            |> Array.map (fun existing -> if existing.CacheId = request.CacheId then submitted else existing)
                    }

                next, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.CandidateAccepted, Some submitted, "Cache identity candidate was accepted.")

        /// Promotes only the currently stored candidate after a proof by that candidate and advances the server-owned rotation schedule.
        let promoteCandidate (state: CacheRegistrationState) (request: CacheRegistrationRefreshRequest) (now: Instant) =
            let current = if isNull (box state) then CacheRegistrationState.Empty else state

            match current.Registrations
                  |> Array.tryFind (fun registration -> registration.CacheId = request.CacheId)
                with
            | None -> current, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.NotFound, None, "Cache registration was not found.")
            | Some registration when registration.RevokedAt.IsSome ->
                current, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Revoked, Some registration, "Cache registration is revoked.")
            | Some registration when now >= registration.ExpiresAt ->
                current,
                CacheRegistrationResult.Create(
                    CacheRegistrationRefreshStatus.Expired,
                    Some registration,
                    "Cache registration is expired and must enroll again."
                )
            | Some registration when registration.CandidatePublicKey.IsNone ->
                current, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.NotFound, Some registration, "Cache identity candidate was not found.")
            | Some registration ->
                let promoted =
                    { registration with
                        ActivePublicKey = registration.CandidatePublicKey.Value
                        CandidatePublicKey = None
                        Health = request.Health
                        SoftwareVersion = request.SoftwareVersion.Trim()
                        ProtocolVersion = request.ProtocolVersion.Trim()
                        PrefetchSupported = request.PrefetchSupported
                        LastRefreshedAt = now
                        RefreshAfter = now.Plus RegistrationLifetime.RefreshAfter
                        ExpiresAt = now.Plus RegistrationLifetime.ActiveLifetime
                        LastRotatedAt = Some now
                        RotationDueAt = now.Plus(Duration.FromMinutes(float registration.RotationIntervalMinutes))
                    }

                let next =
                    {
                        Class = nameof CacheRegistrationState
                        Registrations =
                            current.Registrations
                            |> Array.map (fun existing -> if existing.CacheId = request.CacheId then promoted else existing)
                    }

                next, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Refreshed, Some promoted, "Cache identity candidate was promoted.")

        /// Selects active healthy Cache registrations by exact durable repository assignment only.
        let selectEligible (state: CacheRegistrationState) (query: CacheRegistrationSelectionQuery) (now: Instant) =
            let current = if isNull (box state) then CacheRegistrationState.Empty else state

            current.Registrations
            |> Array.filter (fun registration ->
                isCurrentAt now registration
                && (isNull (box query)
                    || (match query.RepositoryId with
                        | None -> true
                        | Some repositoryId ->
                            registration.RepositoryScopes
                            |> Array.exists (fun scope -> scope.RepositoryId = repositoryId)))
                && (isNull (box query)
                    || not query.RequirePrefetch
                    || registration.PrefetchSupported))
