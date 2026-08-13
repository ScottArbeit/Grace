namespace Grace.CLI.Command

open Grace.Cache
open Grace.CLI.Common
open Grace.SDK
open Grace.Shared
open Grace.Types.CacheRegistration
open Grace.Types.Common
open Spectre.Console
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

/// Groups repository-independent Grace Cache enrollment and local status commands.
module CacheCommand =

    /// Holds the protected state root with a test-only override for isolated root-command proof.
    let mutable private stateRoot = CacheIdentity.StateRoot

    /// Redirects the inspected protected state root for serialized CLI tests.
    let internal setStateRootForTests root = stateRoot <- root

    /// Restores the fixed Product V1 protected state root after a serialized CLI test.
    let internal resetStateRootForTests () = stateRoot <- CacheIdentity.StateRoot

    /// Builds the JSON value that excludes ready-only facts from non-ready status documents.
    let private statusValue (status: CacheIdentityStatus) =
        let value = JsonObject()
        value["Class"] <- JsonValue.Create(status.Class)
        value["Enrollment"] <- JsonValue.Create(status.Enrollment)
        value["Key"] <- JsonValue.Create(status.Key)

        status.CacheId
        |> Option.iter (fun cacheId -> value["CacheId"] <- JsonValue.Create(cacheId))

        status.Endpoint
        |> Option.iter (fun endpoint -> value["Endpoint"] <- JsonValue.Create(endpoint))

        status.BoundaryKind
        |> Option.iter (fun boundaryKind -> value["BoundaryKind"] <- JsonValue.Create(boundaryKind))

        status.RepositoryCount
        |> Option.iter (fun repositoryCount -> value["RepositoryCount"] <- JsonValue.Create(repositoryCount))

        value

    /// Writes approved human status facts before the shared renderer supplies verbose metadata.
    let private renderHumanStatus (status: CacheIdentityStatus) =
        let escape = Markup.Escape
        AnsiConsole.MarkupLine($"Class: {escape status.Class}")
        AnsiConsole.MarkupLine($"Enrollment: {escape status.Enrollment}")
        AnsiConsole.MarkupLine($"Key: {escape status.Key}")

        status.CacheId
        |> Option.iter (fun cacheId -> AnsiConsole.MarkupLine($"CacheId: {cacheId:D}"))

        status.Endpoint
        |> Option.iter (fun endpoint -> AnsiConsole.MarkupLine($"Endpoint: {escape endpoint}"))

        status.BoundaryKind
        |> Option.iter (fun boundaryKind -> AnsiConsole.MarkupLine($"BoundaryKind: {escape boundaryKind}"))

        status.RepositoryCount
        |> Option.iter (fun repositoryCount -> AnsiConsole.MarkupLine($"RepositoryCount: {repositoryCount}"))

    /// Reads only protected local identity state and lets the shared renderer determine output and selector failures before the domain exit.
    let private statusHandler (parseResult: ParseResult) (cancellationToken: CancellationToken) =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let status = CacheIdentity.status stateRoot

            if
                not (hasSelect parseResult)
                && parseResult |> hasOutput
            then
                renderHumanStatus status

            let renderExitCode =
                GraceReturnValue.Create (statusValue status) (getCorrelationId parseResult)
                |> Ok
                |> renderOutput parseResult

            return
                if renderExitCode <> 0 then renderExitCode
                elif status.Enrollment = "enrolled" then 0
                else 1
        }

    /// Invokes the pure local Cache status command through System.CommandLine.
    type Status() =
        inherit AsynchronousCommandLineAction()

        /// Executes the status projection without accessing repository or SDK state.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> = statusHandler parseResult cancellationToken

    /// Defines parser-optional administrator operands whose presence is checked only after typed introspection routing.
    module private EnrollOptions =
        let ownerId = Option<Guid>("--owner-id", Description = "Owner GUID.")

        let organizationId = Option<Guid>("--organization-id", Description = "Optional organization GUID selecting the organization boundary.")

        let repositories =
            Option<string array>("--repository", Description = "Repository assignment as organization-GUID/repository-GUID.", Arity = ArgumentArity.ZeroOrMore)

        let endpoint = Option<string>("--endpoint", Description = "Absolute HTTP or HTTPS Cache endpoint.")
        let displayName = Option<string>("--display-name", DefaultValueFactory = (fun _ -> "Grace Cache"), Description = "Cache display name.")
        let allowHttp = Option<bool>("--allow-http", Description = "Permit an HTTP endpoint.")

    /// Identifies the deterministic points at which focused root-dispatch tests may coordinate a single enrollment.
    type internal EnrollmentPhase =
        | CredentialResolved
        | AttemptStaged
        | TransportStarted
        | BeforeReadyCommit

    /// Supplies the internal collaborators for one fresh Cache command graph.
    type internal Dependencies =
        {
            StateRoot: string
            ResolveBearer: CancellationToken -> Task<Result<string, string>>
            SendEnrollment: CacheEnrollmentRequest -> Uri -> string -> string -> CancellationToken -> Task<CacheEnrollmentTransportOutcome>
            CommitReady: CacheIdentity.CacheEnrollmentClaim -> string -> CacheAcceptedRegistration -> Result<unit, CacheIdentityError>
            OnPhase: EnrollmentPhase -> Task<unit>
        }

    /// Parses one canonical organization/repository assignment without names, empty IDs, or ambiguous separators.
    let private tryRepositoryScope (value: string) =
        if String.IsNullOrWhiteSpace(value) then
            None
        else
            match value.Split('/', StringSplitOptions.None) with
            | [| organization; repository |] ->
                match Guid.TryParseExact(organization, "D"), Guid.TryParseExact(repository, "D") with
                | (true, organizationId), (true, repositoryId) when
                    organizationId <> Guid.Empty
                    && repositoryId <> Guid.Empty
                    ->
                    Some(CacheRepositoryScope.Create(organizationId, repositoryId))
                | _ -> None
            | _ -> None

    /// Builds the exact server request from the approved derived-boundary grammar and staged public key.
    let private requestFromArguments (parseResult: ParseResult) (publicKey: Grace.Cache.CacheIdentityPublicKey) =
        let correlationId = getCorrelationId parseResult
        let ownerId = parseResult.GetValue(EnrollOptions.ownerId)
        let parsedOrganizationId = parseResult.GetValue(EnrollOptions.organizationId)
        let hasExplicitOrganizationId = not (isNull (parseResult.GetResult(EnrollOptions.organizationId)))
        let organizationId = if parsedOrganizationId = Guid.Empty then None else Some parsedOrganizationId
        let endpoint = parseResult.GetValue(EnrollOptions.endpoint)
        let allowHttp = parseResult.GetValue(EnrollOptions.allowHttp)

        let displayName =
            parseResult.GetValue(EnrollOptions.displayName)
            |> Option.ofObj
            |> Option.defaultValue ""
            |> fun value -> value.Trim()

        let repositoryValues =
            parseResult.GetValue(EnrollOptions.repositories)
            |> Option.ofObj
            |> Option.defaultValue Array.empty

        let scopes = repositoryValues |> Array.map tryRepositoryScope

        if ownerId = Guid.Empty then
            Error(GraceError.Create "--owner-id must be a non-empty GUID." correlationId)
        elif hasExplicitOrganizationId
             && parsedOrganizationId = Guid.Empty then
            Error(GraceError.Create "--organization-id must be a non-empty GUID when supplied." correlationId)
        elif repositoryValues.Length = 0
             || scopes |> Array.exists Option.isNone then
            Error(GraceError.Create "At least one --repository value must be organization-GUID/repository-GUID." correlationId)
        elif String.IsNullOrWhiteSpace(displayName) then
            Error(GraceError.Create "--display-name must not be empty." correlationId)
        else
            match endpoint
                  |> Option.ofObj
                  |> Option.bind (fun value ->
                      match Uri.TryCreate(value, UriKind.Absolute) with
                      | true, uri -> Some uri
                      | _ -> None)
                with
            | None -> Error(GraceError.Create "--endpoint must be an absolute HTTP or HTTPS URI." correlationId)
            | Some uri when
                uri.Scheme <> Uri.UriSchemeHttp
                && uri.Scheme <> Uri.UriSchemeHttps
                ->
                Error(GraceError.Create "--endpoint must be an absolute HTTP or HTTPS URI." correlationId)
            | Some uri when uri.Scheme = Uri.UriSchemeHttp && not allowHttp -> Error(GraceError.Create "HTTP --endpoint requires --allow-http." correlationId)
            | Some uri ->
                let request =
                    {
                        Class = nameof CacheEnrollmentRequest
                        DisplayName = displayName
                        BoundaryKind =
                            if organizationId.IsSome then
                                CacheBoundaryKind.Organization
                            else
                                CacheBoundaryKind.Owner
                        OwnerId = ownerId
                        OrganizationId = organizationId
                        RepositoryScopes = List<CacheRepositoryScope>(scopes |> Array.choose id)
                        PublicKey = Grace.Types.CacheRegistration.CacheIdentityPublicKey.Create(publicKey.PublicKeyX, publicKey.PublicKeyY)
                        Endpoint = uri.AbsoluteUri
                        AllowHttpEndpoint = allowHttp
                        SoftwareVersion = "Grace.Cache"
                        ProtocolVersion = "v1"
                        PrefetchSupported = false
                    }

                match Lifecycle.validateEnrollmentRequest request with
                | Ok () -> Ok request
                | Error errors -> Error(GraceError.Create (String.concat " " errors) correlationId)

    /// Reads the selected server URI without repository configuration or invocation-history access.
    let private configuredServerUri parseResult =
        match Uri.TryCreate(Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri), UriKind.Absolute) with
        | true, uri when
            uri.Scheme = Uri.UriSchemeHttp
            || uri.Scheme = Uri.UriSchemeHttps
            ->
            Ok uri
        | _ ->
            Error(GraceError.Create ($"Set {Constants.EnvironmentVariables.GraceServerUri} to an absolute HTTP or HTTPS URI.") (getCorrelationId parseResult))

    /// Checks a required string before later response comparison accesses the value.
    let private hasRequiredText (value: string) = not (String.IsNullOrWhiteSpace(value))

    /// Compares immutable request facts and the staged P-256 public key before accepted state can be published.
    let private matchesAcceptedRegistration (request: CacheEnrollmentRequest) (registration: CacheRegistration) =
        not (isNull (box registration))
        && not (isNull (box request))
        && not (isNull (box request.PublicKey))
        && hasRequiredText registration.Class
        && registration.Class = nameof CacheRegistration
        && registration.CacheId <> Guid.Empty
        && registration.Health = CacheHealthStatus.Unhealthy
        && hasRequiredText registration.EnrolledBy
        && hasRequiredText registration.DisplayName
        && hasRequiredText registration.Endpoint
        && hasRequiredText registration.SoftwareVersion
        && hasRequiredText registration.ProtocolVersion
        && registration.EnrolledAt
           <= registration.LastRefreshedAt
        && registration.LastRefreshedAt < registration.RefreshAfter
        && registration.RefreshAfter < registration.ExpiresAt
        && registration.RevokedAt.IsNone
        && registration.DisplayName = request.DisplayName
        && registration.BoundaryKind = request.BoundaryKind
        && registration.OwnerId = request.OwnerId
        && registration.OrganizationId = request.OrganizationId
        && registration.Endpoint = request.Endpoint
        && registration.AllowHttpEndpoint = request.AllowHttpEndpoint
        && registration.SoftwareVersion = request.SoftwareVersion
        && registration.ProtocolVersion = request.ProtocolVersion
        && registration.PrefetchSupported = request.PrefetchSupported
        && not (isNull (box registration.PublicKey))
        && hasRequiredText registration.PublicKey.Class
        && hasRequiredText registration.PublicKey.Algorithm
        && hasRequiredText registration.PublicKey.Curve
        && hasRequiredText registration.PublicKey.PublicKeyX
        && hasRequiredText registration.PublicKey.PublicKeyY
        && registration.PublicKey.Class = request.PublicKey.Class
        && registration.PublicKey.Algorithm = request.PublicKey.Algorithm
        && registration.PublicKey.Curve = request.PublicKey.Curve
        && registration.PublicKey.PublicKeyX = request.PublicKey.PublicKeyX
        && registration.PublicKey.PublicKeyY = request.PublicKey.PublicKeyY
        && not (isNull registration.RepositoryScopes)
        && registration.RepositoryScopes.Length = request.RepositoryScopes.Count
        && registration.RepositoryScopes
           |> Array.forall (fun scope ->
               not (isNull (box scope))
               && hasRequiredText scope.Class
               && scope.OrganizationId <> Guid.Empty
               && scope.RepositoryId <> Guid.Empty)
        && Array.forall2
            (fun (actual: CacheRepositoryScope) (expected: CacheRepositoryScope) ->
                actual.Class = expected.Class
                && actual.OrganizationId = expected.OrganizationId
                && actual.RepositoryId = expected.RepositoryId)
            registration.RepositoryScopes
            (request.RepositoryScopes |> Seq.toArray)

    /// Converts accepted server facts to the redacted private-ready representation.
    let private acceptedRegistration (registration: CacheRegistration) : CacheAcceptedRegistration =
        {
            CacheId = registration.CacheId
            DisplayName = registration.DisplayName
            BoundaryKind = string registration.BoundaryKind
            OwnerId = registration.OwnerId
            OrganizationId = registration.OrganizationId
            RepositoryScopes =
                registration.RepositoryScopes
                |> Array.map (fun scope -> { OrganizationId = scope.OrganizationId; RepositoryId = scope.RepositoryId })
            Endpoint = registration.Endpoint
            ProtocolVersion = registration.ProtocolVersion
            PublicKey = { PublicKeyX = registration.PublicKey.PublicKeyX; PublicKeyY = registration.PublicKey.PublicKeyY }
        }

    /// Writes the redacted ready status through the shared output contract after one successful publication.
    let private renderEnrollmentSuccess parseResult root =
        let status = CacheIdentity.status root

        if
            not (hasSelect parseResult)
            && parseResult |> hasOutput
        then
            renderHumanStatus status

        let renderExitCode =
            GraceReturnValue.Create (statusValue status) (getCorrelationId parseResult)
            |> Ok
            |> renderOutput parseResult

        if renderExitCode <> 0 then renderExitCode
        elif status.Enrollment = "enrolled" then 0
        else -1

    /// Runs the bounded one-bearer, one-claim, one-attempt, one-POST enrollment transition.
    let private enrollTransition (dependencies: Dependencies) (parseResult: ParseResult) (cancellationToken: CancellationToken) =
        task {
            let correlationId = getCorrelationId parseResult
            let placeholder: Grace.Cache.CacheIdentityPublicKey = { PublicKeyX = String.replicate 43 "a"; PublicKeyY = String.replicate 43 "a" }
            let mutable transportStarted = false

            match requestFromArguments parseResult placeholder, configuredServerUri parseResult with
            | Error error, _
            | _, Error error -> return renderOutput parseResult (Error error)
            | Ok _, Ok serverUri ->
                match CacheIdentity.inspectEnrollmentRoot dependencies.StateRoot with
                | Error _ -> return renderOutput parseResult (Error(GraceError.Create "The protected Grace Cache state root is unavailable." correlationId))
                | Ok CacheIdentityInspection.Ready ->
                    return renderOutput parseResult (Error(GraceError.Create "The protected Grace Cache state root is already enrolled." correlationId))
                | Ok (CacheIdentityInspection.Invalid
                | CacheIdentityInspection.Inaccessible) ->
                    return renderOutput parseResult (Error(GraceError.Create "The protected Grace Cache state root is invalid or inaccessible." correlationId))
                | Ok (CacheIdentityInspection.Missing
                | CacheIdentityInspection.AttemptPresent) ->
                    try
                        let! bearerResult = dependencies.ResolveBearer cancellationToken

                        match bearerResult with
                        | Error message -> return renderOutput parseResult (Error(GraceError.Create message correlationId))
                        | Ok bearer when String.IsNullOrWhiteSpace(bearer) ->
                            return
                                renderOutput
                                    parseResult
                                    (Error(GraceError.Create "Authentication required. Run 'grace authenticate login' and try again." correlationId))
                        | Ok bearer ->
                            do! dependencies.OnPhase CredentialResolved
                            cancellationToken.ThrowIfCancellationRequested()

                            match CacheIdentity.tryAcquireEnrollmentClaim dependencies.StateRoot with
                            | Error _ ->
                                return renderOutput parseResult (Error(GraceError.Create "Cache enrollment could not acquire its local claim." correlationId))
                            | Ok claim ->
                                let mutable stagedKey: Grace.Cache.CacheIdentityPublicKey option = None
                                let mutable committed = false

                                try
                                    match CacheIdentity.discardStaleAttempt claim dependencies.StateRoot with
                                    | Error _ ->
                                        return
                                            renderOutput
                                                parseResult
                                                (Error(GraceError.Create "A stale Cache enrollment attempt could not be safely cleared." correlationId))
                                    | Ok () ->
                                        cancellationToken.ThrowIfCancellationRequested()

                                        match CacheIdentity.createClaimedAttempt claim dependencies.StateRoot with
                                        | Error _ ->
                                            return
                                                renderOutput
                                                    parseResult
                                                    (Error(GraceError.Create "Cache enrollment could not stage its protected identity." correlationId))
                                        | Ok publicKey ->
                                            stagedKey <- Some publicKey
                                            do! dependencies.OnPhase AttemptStaged
                                            cancellationToken.ThrowIfCancellationRequested()

                                            match requestFromArguments parseResult publicKey,
                                                  CacheIdentity.validateClaimedAttempt claim dependencies.StateRoot publicKey
                                                with
                                            | Error error, _ -> return renderOutput parseResult (Error error)
                                            | _, Error _ ->
                                                return
                                                    renderOutput
                                                        parseResult
                                                        (Error(GraceError.Create "Cache enrollment local state changed before the request." correlationId))
                                            | Ok request, Ok () ->
                                                do! dependencies.OnPhase TransportStarted
                                                cancellationToken.ThrowIfCancellationRequested()
                                                transportStarted <- true
                                                let! outcome = dependencies.SendEnrollment request serverUri bearer correlationId cancellationToken

                                                match outcome with
                                                | Rejected error
                                                | Indeterminate error -> return renderOutput parseResult (Error error)
                                                | Accepted response when
                                                    not (isNull (box response))
                                                    && not (isNull (box response.ReturnValue))
                                                    ->
                                                    match response.ReturnValue.Class, response.ReturnValue.Status, response.ReturnValue.Registration with
                                                    | resultClass, CacheRegistrationRefreshStatus.Enrolled, Some registration when
                                                        resultClass = nameof CacheRegistrationResult
                                                        && matchesAcceptedRegistration request registration
                                                        ->
                                                        do! dependencies.OnPhase BeforeReadyCommit
                                                        cancellationToken.ThrowIfCancellationRequested()

                                                        match CacheIdentity.validateClaimedAttempt claim dependencies.StateRoot publicKey with
                                                        | Error _ ->
                                                            return
                                                                renderOutput
                                                                    parseResult
                                                                    (Error(
                                                                        GraceError.Create
                                                                            "Cache enrollment local state changed before ready publication."
                                                                            correlationId
                                                                    ))
                                                        | Ok () ->
                                                            match dependencies.CommitReady claim dependencies.StateRoot (acceptedRegistration registration) with
                                                            | Ok () ->
                                                                committed <- true
                                                                return renderEnrollmentSuccess parseResult dependencies.StateRoot
                                                            | Error _ ->
                                                                return
                                                                    renderOutput
                                                                        parseResult
                                                                        (Error(
                                                                            GraceError.Create
                                                                                "Cache enrollment was accepted but local ready state could not be committed."
                                                                                correlationId
                                                                        ))
                                                    | _ ->
                                                        return
                                                            renderOutput
                                                                parseResult
                                                                (Error(
                                                                    GraceError.Create
                                                                        "Cache enrollment response did not strictly accept the staged identity."
                                                                        correlationId
                                                                ))
                                                | Accepted _ ->
                                                    return
                                                        renderOutput
                                                            parseResult
                                                            (Error(
                                                                GraceError.Create
                                                                    "Cache enrollment response did not strictly accept the staged identity."
                                                                    correlationId
                                                            ))
                                finally
                                    if not committed then
                                        CacheIdentity.discardClaimedAttempt claim dependencies.StateRoot stagedKey

                                    CacheIdentity.releaseEnrollmentClaim claim
                    with
                    | :? OperationCanceledException ->
                        let message =
                            if transportStarted then
                                "Cache enrollment outcome is unknown after transport started."
                            else
                                "Cache enrollment was cancelled."

                        return renderOutput parseResult (Error(GraceError.Create message correlationId))
        }

    /// Invokes the one-shot cache enrollment action through the token-aware root graph.
    type internal Enroll(dependencies: Dependencies) =
        inherit AsynchronousCommandLineAction()

        /// Executes the bounded enrollment transition.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            enrollTransition dependencies parseResult cancellationToken

    /// Creates the production collaborators for a fresh Cache command graph.
    let internal productionDependencies () =
        {
            StateRoot = stateRoot
            ResolveBearer =
                fun cancellationToken ->
                    task {
                        cancellationToken.ThrowIfCancellationRequested()
                        let! bearer = Auth.tryGetAccessTokenForCacheEnrollment ()
                        cancellationToken.ThrowIfCancellationRequested()

                        let normalized: Result<string, string> =
                            match bearer with
                            | Ok (Some value) when not (String.IsNullOrWhiteSpace(value)) -> Result.Ok value
                            | Error message -> Result.Error message
                            | Ok (Some _) -> Result.Error "Authentication required. Run 'grace authenticate login' and try again."
                            | Ok None -> Result.Error "Authentication required. Run 'grace authenticate login' and try again."

                        return normalized
                    }
            SendEnrollment =
                (fun request serverUri bearer correlationId cancellationToken ->
                    CacheRegistration.Enroll(request, serverUri, bearer, correlationId, cancellationToken))
            CommitReady = CacheIdentity.commitClaimedReady
            OnPhase = (fun _ -> Task.FromResult(()))
        }

    /// Creates a fresh Cache command graph for one production root or focused root-dispatch test.
    let internal create (dependencies: Dependencies) =
        let cache = Command("cache", "Inspect a local Grace Cache identity.")
        let status = Command("status", "Report redacted local Grace Cache enrollment status.")
        let enroll = Command("enroll", "Enroll one protected Grace Cache identity.")
        status.Action <- Status()
        enroll.Options.Add(EnrollOptions.ownerId)
        enroll.Options.Add(EnrollOptions.organizationId)
        enroll.Options.Add(EnrollOptions.repositories)
        enroll.Options.Add(EnrollOptions.endpoint)
        enroll.Options.Add(EnrollOptions.displayName)
        enroll.Options.Add(EnrollOptions.allowHttp)
        enroll.Action <- Enroll(dependencies)
        cache.Subcommands.Add(status)
        cache.Subcommands.Add(enroll)
        cache

    /// Retains a production Cache graph for existing parser-focused callers.
    let Build = create (productionDependencies ())
