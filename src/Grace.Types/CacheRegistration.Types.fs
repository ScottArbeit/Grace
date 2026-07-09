namespace Grace.Types

open Grace.Types.Common
open Grace.Types.MaterializationPlan
open NodaTime
open Orleans
open System
open System.Collections.Generic

/// Contains Grace Cache service registration contracts and deterministic lifecycle helpers.
module CacheRegistration =

    /// Identifies a server-approved Grace Cache capability.
    [<RequireQualifiedAccess>]
    module Capability =

        /// Allows a registered Cache service to serve already-complete plan artifacts.
        [<Literal>]
        let ReadThrough = "read-through"

        /// Allows a registered Cache service to prefetch plan artifacts before a requester needs them.
        [<Literal>]
        let Prefetch = "prefetch"

    /// Provides the server-owned registration lifetime defaults for Grace Cache services.
    [<RequireQualifiedAccess>]
    module RegistrationLifetime =

        /// The default active lifetime for a Cache registration.
        let ActiveLifetime = Duration.FromHours 2

        /// The default interval after which a current Cache registration may be refreshed.
        let RefreshAfter = Duration.FromHours 1

    /// Represents the endpoint and requested service envelope supplied by a Grace Cache instance.
    [<CLIMutable; GenerateSerializer>]
    type CacheRegistrationRequest =
        {
            Class: string
            Endpoint: string
            RequestedScopes: List<string>
            RequestedCapabilities: List<string>
            RequestedExecutionModes: List<MaterializationExecutionMode>
        }

        /// Builds a registration request with defensive list copies for Orleans and JSON callers.
        static member Create
            (
                endpoint: string,
                requestedScopes: string seq,
                requestedCapabilities: string seq,
                requestedExecutionModes: MaterializationExecutionMode seq
            ) =
            {
                Class = nameof CacheRegistrationRequest
                Endpoint = endpoint
                RequestedScopes = List<string>(requestedScopes)
                RequestedCapabilities = List<string>(requestedCapabilities)
                RequestedExecutionModes = List<MaterializationExecutionMode>(requestedExecutionModes)
            }

    /// Represents a Grace Cache refresh request for the service principal carried by the authenticated token.
    [<CLIMutable; GenerateSerializer>]
    type CacheRegistrationRefreshRequest =
        {
            Class: string
        }

        /// Represents the default refresh request body.
        static member Default = { Class = nameof CacheRegistrationRefreshRequest }

    /// Represents a server-owned Grace Cache service registration persisted by the registration actor.
    [<CLIMutable; GenerateSerializer>]
    type CacheRegistration =
        {
            Class: string
            ServicePrincipalId: string
            Endpoint: string
            ApprovedScopes: string array
            ApprovedCapabilities: string array
            ApprovedExecutionModes: MaterializationExecutionMode array
            RegisteredAt: Instant
            LastRefreshedAt: Instant
            RefreshAfter: Instant
            ExpiresAt: Instant
            ReadThroughEnabled: bool
            PrefetchEnabled: bool
        }

        /// Builds a server-owned registration using approved scopes and capabilities only.
        static member Create
            (
                servicePrincipalId: string,
                endpoint: string,
                approvedScopes: string seq,
                approvedCapabilities: string seq,
                approvedExecutionModes: MaterializationExecutionMode seq,
                registeredAt: Instant,
                activeLifetime: Duration,
                refreshAfter: Duration
            ) =
            let capabilities =
                approvedCapabilities
                |> Seq.filter (String.IsNullOrWhiteSpace >> not)
                |> Seq.distinctBy (fun capability -> capability.ToLowerInvariant())
                |> Seq.toArray

            let hasCapability capability =
                capabilities
                |> Array.exists (fun approved -> String.Equals(approved, capability, StringComparison.OrdinalIgnoreCase))

            {
                Class = nameof CacheRegistration
                ServicePrincipalId = servicePrincipalId
                Endpoint = endpoint
                ApprovedScopes =
                    approvedScopes
                    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
                    |> Seq.distinct
                    |> Seq.toArray
                ApprovedCapabilities = capabilities
                ApprovedExecutionModes =
                    approvedExecutionModes
                    |> Seq.filter (fun mode -> Enum.IsDefined(typeof<MaterializationExecutionMode>, mode))
                    |> Seq.distinct
                    |> Seq.toArray
                RegisteredAt = registeredAt
                LastRefreshedAt = registeredAt
                RefreshAfter = registeredAt.Plus refreshAfter
                ExpiresAt = registeredAt.Plus activeLifetime
                ReadThroughEnabled = hasCapability Capability.ReadThrough
                PrefetchEnabled = hasCapability Capability.Prefetch
            }

    /// Represents the refresh result classification for a Grace Cache service registration.
    type CacheRegistrationRefreshStatus =
        | Refreshed = 1
        | RefreshNotDue = 2
        | Expired = 3
        | NotFound = 4

    /// Represents the server response for register and refresh operations.
    [<CLIMutable; GenerateSerializer>]
    type CacheRegistrationResult =
        {
            Class: string
            Status: CacheRegistrationRefreshStatus
            Registration: CacheRegistration option
            Message: string
        }

        /// Builds a registration operation response.
        static member Create(status: CacheRegistrationRefreshStatus, registration: CacheRegistration option, message: string) =
            { Class = nameof CacheRegistrationResult; Status = status; Registration = registration; Message = message }

    /// Represents a server-side eligibility query for current Cache registrations.
    [<CLIMutable; GenerateSerializer>]
    type CacheRegistrationSelectionQuery =
        {
            Class: string
            Scope: string option
            RequiredCapabilities: List<string>
            RequiredExecutionMode: MaterializationExecutionMode option
            RequireReadThrough: bool
            RequirePrefetch: bool
        }

        /// Builds a selection query for current registration eligibility.
        static member Create
            (
                scope: string option,
                requiredCapabilities: string seq,
                requiredExecutionMode: MaterializationExecutionMode option,
                requireReadThrough: bool,
                requirePrefetch: bool
            ) =
            {
                Class = nameof CacheRegistrationSelectionQuery
                Scope = scope
                RequiredCapabilities = List<string>(requiredCapabilities)
                RequiredExecutionMode = requiredExecutionMode
                RequireReadThrough = requireReadThrough
                RequirePrefetch = requirePrefetch
            }

        /// Represents an unconstrained current-registration query.
        static member Current = CacheRegistrationSelectionQuery.Create(None, Seq.empty, None, false, false)

    /// Represents all registration records owned by the Cache registration actor.
    [<CLIMutable; GenerateSerializer>]
    type CacheRegistrationState =
        {
            Class: string
            Registrations: CacheRegistration array
        }

        /// Represents an empty Cache registration actor state.
        static member Empty = { Class = nameof CacheRegistrationState; Registrations = Array.empty }

    /// Contains deterministic validation and state transitions for Grace Cache registrations.
    module Lifecycle =

        /// Returns true when a Cache registration is still eligible at the supplied server time.
        let isCurrentAt (now: Instant) (registration: CacheRegistration) =
            not (isNull (box registration))
            && now < registration.ExpiresAt

        /// Returns the server-owned registration lifetime defaults.
        let defaultLifetime () = RegistrationLifetime.ActiveLifetime, RegistrationLifetime.RefreshAfter

        /// Returns true when a registration request has a supported shape before identity approval is evaluated.
        let validateRequest (request: CacheRegistrationRequest) =
            let errors = ResizeArray<string>()

            if isNull (box request) then
                errors.Add("CacheRegistrationRequest is required.")
            else
                if request.Class <> nameof CacheRegistrationRequest then
                    errors.Add("Class must be CacheRegistrationRequest.")

                if String.IsNullOrWhiteSpace request.Endpoint then
                    errors.Add("Endpoint is required.")
                else
                    match Uri.TryCreate(request.Endpoint, UriKind.Absolute) with
                    | true, uri when uri.Scheme = Uri.UriSchemeHttps -> ()
                    | _ -> errors.Add("Endpoint must be an absolute HTTPS URI.")

                if isNull request.RequestedScopes
                   || request.RequestedScopes.Count = 0 then
                    errors.Add("RequestedScopes must include at least one scope.")
                elif request.RequestedScopes
                     |> Seq.exists String.IsNullOrWhiteSpace then
                    errors.Add("RequestedScopes must not include blank values.")

                if isNull request.RequestedCapabilities
                   || request.RequestedCapabilities.Count = 0 then
                    errors.Add("RequestedCapabilities must include at least one capability.")
                elif request.RequestedCapabilities
                     |> Seq.exists String.IsNullOrWhiteSpace then
                    errors.Add("RequestedCapabilities must not include blank values.")

                if isNull request.RequestedExecutionModes
                   || request.RequestedExecutionModes.Count = 0 then
                    errors.Add("RequestedExecutionModes must include at least one execution mode.")
                else
                    for mode in request.RequestedExecutionModes do
                        if not (Enum.IsDefined(typeof<MaterializationExecutionMode>, mode)) then
                            errors.Add($"Requested execution mode '{int mode}' is not supported.")

            if errors.Count = 0 then Ok() else Error(List.ofSeq errors)

        /// Inserts or replaces one service-principal registration without duplicating live records.
        let register
            (state: CacheRegistrationState)
            (servicePrincipalId: string)
            (request: CacheRegistrationRequest)
            (approvedScopes: string seq)
            (approvedCapabilities: string seq)
            (now: Instant)
            =
            let activeLifetime, refreshAfter = defaultLifetime ()

            let currentState = if isNull (box state) then CacheRegistrationState.Empty else state

            let registration =
                CacheRegistration.Create(
                    servicePrincipalId,
                    request.Endpoint.Trim(),
                    approvedScopes,
                    approvedCapabilities,
                    request.RequestedExecutionModes,
                    now,
                    activeLifetime,
                    refreshAfter
                )

            let remaining =
                currentState.Registrations
                |> Array.filter (fun existing -> not (String.Equals(existing.ServicePrincipalId, servicePrincipalId, StringComparison.Ordinal)))

            { Class = nameof CacheRegistrationState; Registrations = Array.append remaining [| registration |] },
            CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Refreshed, Some registration, "Cache registration is current.")

        /// Extends a current registration after RefreshAfter while preserving server-approved scopes and capabilities.
        let refresh (state: CacheRegistrationState) (servicePrincipalId: string) (now: Instant) =
            let activeLifetime, refreshAfter = defaultLifetime ()

            let currentState = if isNull (box state) then CacheRegistrationState.Empty else state

            match currentState.Registrations
                  |> Array.tryFind (fun registration -> String.Equals(registration.ServicePrincipalId, servicePrincipalId, StringComparison.Ordinal))
                with
            | None -> currentState, CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.NotFound, None, "Cache registration was not found.")
            | Some registration when not (isCurrentAt now registration) ->
                currentState,
                CacheRegistrationResult.Create(
                    CacheRegistrationRefreshStatus.Expired,
                    Some registration,
                    "Cache registration is expired and must register again."
                )
            | Some registration when now < registration.RefreshAfter ->
                currentState,
                CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.RefreshNotDue, Some registration, "Cache registration refresh is not due yet.")
            | Some registration ->
                let refreshed = { registration with LastRefreshedAt = now; RefreshAfter = now.Plus refreshAfter; ExpiresAt = now.Plus activeLifetime }

                let nextRegistrations =
                    currentState.Registrations
                    |> Array.map (fun existing ->
                        if String.Equals(existing.ServicePrincipalId, servicePrincipalId, StringComparison.Ordinal) then
                            refreshed
                        else
                            existing)

                { Class = nameof CacheRegistrationState; Registrations = nextRegistrations },
                CacheRegistrationResult.Create(CacheRegistrationRefreshStatus.Refreshed, Some refreshed, "Cache registration was refreshed.")

        /// Removes expired registrations from actor state using the supplied server time.
        let expire (state: CacheRegistrationState) (now: Instant) =
            let currentState = if isNull (box state) then CacheRegistrationState.Empty else state

            {
                Class = nameof CacheRegistrationState
                Registrations =
                    currentState.Registrations
                    |> Array.filter (isCurrentAt now)
            }

        /// Selects current registrations that satisfy server-owned scope, capability, and execution-mode requirements.
        let selectEligible (state: CacheRegistrationState) (query: CacheRegistrationSelectionQuery) (now: Instant) =
            let currentState = if isNull (box state) then CacheRegistrationState.Empty else state

            let requiredCapabilities =
                if
                    isNull (box query)
                    || isNull query.RequiredCapabilities
                then
                    Array.empty
                else
                    query.RequiredCapabilities
                    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
                    |> Seq.toArray

            let matchesCapability (registration: CacheRegistration) capability =
                registration.ApprovedCapabilities
                |> Array.exists (fun approved -> String.Equals(approved, capability, StringComparison.OrdinalIgnoreCase))

            let matchesScope (registration: CacheRegistration) =
                if isNull (box query) then
                    true
                else
                    match query.Scope with
                    | None -> true
                    | Some scope when String.IsNullOrWhiteSpace scope -> false
                    | Some scope ->
                        registration.ApprovedScopes
                        |> Array.exists (fun approved -> String.Equals(approved, scope, StringComparison.Ordinal))

            let matchesExecutionMode (registration: CacheRegistration) =
                if isNull (box query) then
                    true
                else
                    match query.RequiredExecutionMode with
                    | None -> true
                    | Some mode ->
                        registration.ApprovedExecutionModes
                        |> Array.contains mode

            let matchesReadThrough (registration: CacheRegistration) =
                isNull (box query)
                || not query.RequireReadThrough
                || registration.ReadThroughEnabled

            let matchesPrefetch (registration: CacheRegistration) =
                isNull (box query)
                || not query.RequirePrefetch
                || registration.PrefetchEnabled

            currentState.Registrations
            |> Array.filter (fun registration ->
                isCurrentAt now registration
                && matchesScope registration
                && matchesExecutionMode registration
                && matchesReadThrough registration
                && matchesPrefetch registration
                && (requiredCapabilities
                    |> Array.forall (matchesCapability registration)))
