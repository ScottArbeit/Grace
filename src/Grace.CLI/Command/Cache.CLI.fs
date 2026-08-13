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

    /// Defines only the administrator-supplied enrollment grammar; protocol and software facts remain implementation-derived.
    module private EnrollOptions =
        let ownerId = Option<Guid>("--owner-id", Description = "Owner GUID.", Required = true)
        let organizationId = Option<Guid>("--organization-id", Description = "Optional organization GUID selecting the organization boundary.")

        let repositories =
            Option<string array>(
                "--repository",
                Description = "Repository assignment as organization-GUID/repository-GUID.",
                Arity = ArgumentArity.OneOrMore,
                Required = true
            )

        let endpoint = Option<string>("--endpoint", Description = "Absolute HTTP or HTTPS Cache endpoint.", Required = true)
        let displayName = Option<string>("--display-name", DefaultValueFactory = (fun _ -> "Grace Cache"), Description = "Cache display name.")
        let allowHttp = Option<bool>("--allow-http", Description = "Permit an HTTP endpoint.")

    /// Parses one canonical organization/repository assignment without accepting names, empty IDs, or ambiguous separators.
    let private tryRepositoryScope (value: string) =
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

    /// Builds the exact server request from the approved derived-boundary grammar and the current staged public key.
    let private requestFromArguments (parseResult: ParseResult) (publicKey: Grace.Cache.CacheIdentityPublicKey) =
        let correlationId = getCorrelationId parseResult
        let ownerId = parseResult.GetValue(EnrollOptions.ownerId)
        let parsedOrganizationId = parseResult.GetValue(EnrollOptions.organizationId)
        let hasExplicitOrganizationId = not (isNull (parseResult.GetResult("--organization-id")))
        let organizationId = if parsedOrganizationId = Guid.Empty then None else Some parsedOrganizationId
        let endpoint = parseResult.GetValue(EnrollOptions.endpoint)
        let allowHttp = parseResult.GetValue(EnrollOptions.allowHttp)

        let displayName =
            (parseResult.GetValue(EnrollOptions.displayName))
                .Trim()

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
        elif scopes |> Array.exists Option.isNone then
            Error(GraceError.Create "Each --repository value must be organization-GUID/repository-GUID." correlationId)
        else
            match Uri.TryCreate(endpoint, UriKind.Absolute) with
            | false, _ -> Error(GraceError.Create "--endpoint must be an absolute HTTP or HTTPS URI." correlationId)
            | true, uri when
                uri.Scheme <> Uri.UriSchemeHttp
                && uri.Scheme <> Uri.UriSchemeHttps
                ->
                Error(GraceError.Create "--endpoint must be an absolute HTTP or HTTPS URI." correlationId)
            | true, uri when uri.Scheme = Uri.UriSchemeHttp && not allowHttp -> Error(GraceError.Create "HTTP --endpoint requires --allow-http." correlationId)
            | true, uri ->
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

    /// Reads the selected server URI without consulting repository configuration or local invocation history.
    let private configuredServerUri parseResult =
        match Uri.TryCreate(Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri), UriKind.Absolute) with
        | true, uri when
            uri.Scheme = Uri.UriSchemeHttp
            || uri.Scheme = Uri.UriSchemeHttps
            ->
            Ok uri
        | _ ->
            Error(GraceError.Create ($"Set {Constants.EnvironmentVariables.GraceServerUri} to an absolute HTTP or HTTPS URI.") (getCorrelationId parseResult))

    /// Requires the existing credential precedence to produce one concrete bearer before cache-root effects begin.
    let private resolveBearer (parseResult: ParseResult) (cancellationToken: CancellationToken) =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let! result = Grace.CLI.Command.Auth.tryGetAccessToken ()
            cancellationToken.ThrowIfCancellationRequested()

            match result with
            | Ok (Some bearer) when not (String.IsNullOrWhiteSpace bearer) -> return Ok bearer
            | Ok _ -> return Error(GraceError.Create "Authentication required. Run 'grace authenticate login' and try again." (getCorrelationId parseResult))
            | Error message -> return Error(GraceError.Create message (getCorrelationId parseResult))
        }

    /// Compares every immutable request fact and staged P-256 public key before server acceptance can publish ready state.
    let private matchesAcceptedRegistration (request: CacheEnrollmentRequest) (registration: CacheRegistration) =
        not (isNull (box registration))
        && registration.Class = nameof CacheRegistration
        && registration.CacheId <> Guid.Empty
        && registration.Health = CacheHealthStatus.Unhealthy
        && not (String.IsNullOrWhiteSpace registration.EnrolledBy)
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
        && registration.PublicKey.Class = request.PublicKey.Class
        && registration.PublicKey.Algorithm = request.PublicKey.Algorithm
        && registration.PublicKey.Curve = request.PublicKey.Curve
        && registration.PublicKey.PublicKeyX = request.PublicKey.PublicKeyX
        && registration.PublicKey.PublicKeyY = request.PublicKey.PublicKeyY
        && registration.RepositoryScopes.Length = request.RepositoryScopes.Count
        && Array.forall2
            (fun (actual: CacheRepositoryScope) (expected: CacheRepositoryScope) ->
                actual.Class = expected.Class
                && actual.OrganizationId = expected.OrganizationId
                && actual.RepositoryId = expected.RepositoryId)
            registration.RepositoryScopes
            (request.RepositoryScopes |> Seq.toArray)

    /// Converts one strictly accepted server registration into the redacted local-ready representation without retaining server lifecycle fields.
    let private acceptedRegistration (registration: CacheRegistration) : Grace.Cache.CacheAcceptedRegistration =
        {
            CacheId = registration.CacheId
            DisplayName = registration.DisplayName
            BoundaryKind = string registration.BoundaryKind
            OwnerId = registration.OwnerId
            OrganizationId = registration.OrganizationId
            RepositoryScopes =
                registration.RepositoryScopes
                |> Array.map (fun (scope: CacheRepositoryScope) -> { OrganizationId = scope.OrganizationId; RepositoryId = scope.RepositoryId })
            Endpoint = registration.Endpoint
            ProtocolVersion = registration.ProtocolVersion
            PublicKey = { PublicKeyX = registration.PublicKey.PublicKeyX; PublicKeyY = registration.PublicKey.PublicKeyY }
        }

    /// Emits one redacted enrollment success value through the same renderer and status projection used by cache.status.
    let private renderEnrollmentSuccess parseResult =
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

        if renderExitCode <> 0 then renderExitCode
        elif status.Enrollment = "enrolled" then 0
        else -1

    /// Executes the one-shot enrollment transition without retries, reconciliation, repository configuration, or invocation history.
    let private enrollTransition (parseResult: ParseResult) (cancellationToken: CancellationToken) =
        task {
            let placeholder: Grace.Cache.CacheIdentityPublicKey = { PublicKeyX = String.replicate 43 "a"; PublicKeyY = String.replicate 43 "a" }

            match requestFromArguments parseResult placeholder, configuredServerUri parseResult with
            | Error error, _
            | _, Error error -> return renderOutput parseResult (Error error)
            | Ok _, Ok serverUri ->
                match CacheIdentity.inspectEnrollmentRoot stateRoot with
                | Error _ ->
                    return
                        renderOutput
                            parseResult
                            (Error(GraceError.Create "The protected Grace Cache state root is unavailable." (getCorrelationId parseResult)))
                | Ok CacheIdentityInspection.Ready ->
                    return
                        renderOutput
                            parseResult
                            (Error(GraceError.Create "The protected Grace Cache state root is already enrolled." (getCorrelationId parseResult)))
                | Ok (CacheIdentityInspection.Invalid
                | CacheIdentityInspection.Inaccessible) ->
                    return
                        renderOutput
                            parseResult
                            (Error(GraceError.Create "The protected Grace Cache state root is invalid or inaccessible." (getCorrelationId parseResult)))
                | Ok (CacheIdentityInspection.Missing
                | CacheIdentityInspection.AttemptPresent) ->
                    let! bearerResult = resolveBearer parseResult cancellationToken

                    match bearerResult with
                    | Error error -> return renderOutput parseResult (Error error)
                    | Ok bearer ->
                        cancellationToken.ThrowIfCancellationRequested()

                        match CacheIdentity.tryAcquireEnrollmentClaim stateRoot with
                        | Error _ ->
                            return
                                renderOutput
                                    parseResult
                                    (Error(GraceError.Create "Cache enrollment could not acquire its local claim." (getCorrelationId parseResult)))
                        | Ok claim ->
                            let mutable stagedKey: Grace.Cache.CacheIdentityPublicKey option = None
                            let mutable committed = false

                            try
                                let staleRecovery =
                                    match CacheIdentity.inspectEnrollmentRoot stateRoot with
                                    | Ok CacheIdentityInspection.AttemptPresent -> CacheIdentity.discardStaleAttempt claim stateRoot
                                    | Ok CacheIdentityInspection.Missing -> Ok()
                                    | _ -> Error CacheIdentityError.StateUnavailable

                                match staleRecovery with
                                | Error _ ->
                                    return
                                        renderOutput
                                            parseResult
                                            (Error(
                                                GraceError.Create "A stale Cache enrollment attempt could not be safely cleared." (getCorrelationId parseResult)
                                            ))
                                | Ok () ->
                                    cancellationToken.ThrowIfCancellationRequested()

                                    match CacheIdentity.createClaimedAttempt claim stateRoot with
                                    | Error _ ->
                                        return
                                            renderOutput
                                                parseResult
                                                (Error(
                                                    GraceError.Create "Cache enrollment could not stage its protected identity." (getCorrelationId parseResult)
                                                ))
                                    | Ok publicKey ->
                                        stagedKey <- Some publicKey
                                        cancellationToken.ThrowIfCancellationRequested()

                                        match requestFromArguments parseResult publicKey, CacheIdentity.validateClaimedAttempt claim stateRoot publicKey with
                                        | Error error, _ -> return renderOutput parseResult (Error error)
                                        | _, Error _ ->
                                            return
                                                renderOutput
                                                    parseResult
                                                    (Error(
                                                        GraceError.Create
                                                            "Cache enrollment local state changed before the request."
                                                            (getCorrelationId parseResult)
                                                    ))
                                        | Ok request, Ok () ->
                                            let! response =
                                                CacheRegistration.Enroll(request, serverUri, bearer, getCorrelationId parseResult, cancellationToken)

                                            match response with
                                            | Error error -> return renderOutput parseResult (Error error)
                                            | Ok response ->
                                                match response.ReturnValue.Class, response.ReturnValue.Status, response.ReturnValue.Registration with
                                                | resultClass, CacheRegistrationRefreshStatus.Enrolled, Some registration when
                                                    resultClass = nameof CacheRegistrationResult
                                                    && matchesAcceptedRegistration request registration
                                                    ->
                                                    match CacheIdentity.commitClaimedReady claim stateRoot (acceptedRegistration registration) with
                                                    | Ok () ->
                                                        committed <- true
                                                        return renderEnrollmentSuccess parseResult
                                                    | Error _ ->
                                                        return
                                                            renderOutput
                                                                parseResult
                                                                (Error(
                                                                    GraceError.Create
                                                                        "Cache enrollment was accepted but local ready state could not be committed."
                                                                        (getCorrelationId parseResult)
                                                                ))
                                                | _ ->
                                                    return
                                                        renderOutput
                                                            parseResult
                                                            (Error(
                                                                GraceError.Create
                                                                    "Cache enrollment response did not strictly accept the staged identity."
                                                                    (getCorrelationId parseResult)
                                                            ))
                            finally
                                if not committed then
                                    CacheIdentity.discardClaimedAttempt claim stateRoot stagedKey

                                CacheIdentity.releaseEnrollmentClaim claim
        }

    /// Projects cancellation from every enrollment phase through the redacted Cache output contract after claimed cleanup completes.
    let private enrollHandler (parseResult: ParseResult) (cancellationToken: CancellationToken) =
        task {
            try
                return! enrollTransition parseResult cancellationToken
            with
            | :? OperationCanceledException ->
                return renderOutput parseResult (Error(GraceError.Create "Cache enrollment was cancelled." (getCorrelationId parseResult)))
        }

    /// Invokes the one-shot cache enrollment action through repository-independent root dispatch.
    type Enroll() =
        inherit AsynchronousCommandLineAction()

        /// Executes the bounded enrollment transition.
        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> = enrollHandler parseResult cancellationToken

    /// Builds the repository-independent Cache command group.
    let Build =
        let cache = Command("cache", "Inspect a local Grace Cache identity.")
        let enroll = Command("enroll", "Enroll one protected Grace Cache identity.")
        enroll.Options.Add(EnrollOptions.ownerId)
        enroll.Options.Add(EnrollOptions.organizationId)
        enroll.Options.Add(EnrollOptions.repositories)
        enroll.Options.Add(EnrollOptions.endpoint)
        enroll.Options.Add(EnrollOptions.displayName)
        enroll.Options.Add(EnrollOptions.allowHttp)
        enroll.Action <- Enroll()
        cache.Subcommands.Add(enroll)
        let status = Command("status", "Report redacted local Grace Cache enrollment status.")
        status.Action <- Status()
        cache.Subcommands.Add(status)
        cache
