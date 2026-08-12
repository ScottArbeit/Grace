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

/// Groups repository-independent static Cache enrollment and local status commands.
module CacheCommand =

    /// Holds the protected root with a test-only override for isolated command-path proof.
    let mutable private stateRoot = CacheIdentity.StateRoot

    /// Defines the internal operational dependencies used by cache enrollment before they are bound to the command action.
    type internal EnrollmentDependencies =
        {
            ResolveBearer: unit -> Task<Result<string option, string>>
            AfterAttemptCreated: unit -> unit
            CommitReady: (unit -> bool) -> bool
        }

    /// Binds enrollment to the existing normal credential provider and protected local identity implementation.
    let private productionDependencies =
        {
            ResolveBearer = (fun () -> Grace.CLI.Command.Auth.tryGetAccessToken ())
            AfterAttemptCreated = (fun () -> ())
            CommitReady = (fun commit -> commit ())
        }

    /// Holds the internal enrollment dependency record so serialized tests can prove root-command outcomes deterministically.
    let mutable private enrollmentDependencies = productionDependencies

    /// Redirects protected state for serialized command-path tests.
    let internal setStateRootForTests root = stateRoot <- root

    /// Restores the fixed Product V1 protected state root after a command-path test.
    let internal resetStateRootForTests () = stateRoot <- CacheIdentity.StateRoot

    /// Replaces internal enrollment dependencies for a serialized test and returns the prior record for exact restoration.
    let internal setEnrollmentDependenciesForTests dependencies =
        let previous = enrollmentDependencies
        enrollmentDependencies <- dependencies
        previous

    /// Reads the internal enrollment dependency record for serialized tests that override one behavior and preserve the others.
    let internal getEnrollmentDependenciesForTests () = enrollmentDependencies

    /// Restores normal enrollment dependencies after a serialized test.
    let internal resetEnrollmentDependenciesForTests () = enrollmentDependencies <- productionDependencies

    /// Defines explicit administrator-supplied enrollment options.
    module private Options =
        let displayName = new Option<string>("--display-name", Description = "Cache display name.", Arity = ArgumentArity.ExactlyOne)
        let endpoint = new Option<string>("--endpoint", Description = "Cache HTTPS endpoint.", Arity = ArgumentArity.ExactlyOne)

        let boundary = new Option<string>("--boundary", Description = "Enrollment boundary: owner or organization.", Arity = ArgumentArity.ExactlyOne)

        let ownerId = new Option<Guid>("--owner-id", Description = "Owner ID.", Arity = ArgumentArity.ExactlyOne)

        let organizationId =
            new Option<Guid>("--organization-id", Description = "Organization ID for an organization boundary.", Arity = ArgumentArity.ZeroOrOne)

        let repositoryIds = new Option<Guid array>("--repository-id", Description = "One or more repository IDs.", Arity = ArgumentArity.OneOrMore)

        let repositoryOrganizationId =
            new Option<Guid>("--repository-organization-id", Description = "Organization ID for every repository assignment.", Arity = ArgumentArity.ExactlyOne)

        let allowHttp = new Option<bool>("--allow-http", Description = "Allow the exact HTTP endpoint supplied by --endpoint.", Arity = ArgumentArity.Zero)
        let softwareVersion = new Option<string>("--software-version", DefaultValueFactory = (fun _ -> "Grace.Cache"), Description = "Cache software version.")
        let protocolVersion = new Option<string>("--protocol-version", DefaultValueFactory = (fun _ -> "v1"), Description = "Cache protocol version.")

    /// Builds a validated cache request from explicit arguments and one generated public key.
    let private requestFromArguments (parseResult: ParseResult) (publicKey: Grace.Cache.CacheIdentityPublicKey) =
        let correlationId = getCorrelationId parseResult
        let boundary = parseResult.GetValue(Options.boundary)
        let ownerId = parseResult.GetValue(Options.ownerId)
        let organizationId = parseResult.GetValue(Options.organizationId)
        let repositoryOrganizationId = parseResult.GetValue(Options.repositoryOrganizationId)

        let repositoryIds =
            parseResult.GetValue(Options.repositoryIds)
            |> Option.ofObj
            |> Option.defaultValue [||]

        let boundaryResult =
            match Option.ofObj boundary
                  |> Option.map (fun value -> value.Trim().ToLowerInvariant())
                with
            | Some "owner" when organizationId = Guid.Empty -> Ok(CacheBoundaryKind.Owner, None)
            | Some "organization" when
                organizationId <> Guid.Empty
                && organizationId = repositoryOrganizationId
                ->
                Ok(CacheBoundaryKind.Organization, Some organizationId)
            | _ -> Error(GraceError.Create "Cache boundary and organization inputs are invalid." correlationId)

        match boundaryResult with
        | Error error -> Error error
        | Ok (kind, organization) ->
            let request =
                {
                    Class = nameof CacheEnrollmentRequest
                    DisplayName =
                        parseResult.GetValue(Options.displayName)
                        |> Option.ofObj
                        |> Option.defaultValue String.Empty
                    BoundaryKind = kind
                    OwnerId = ownerId
                    OrganizationId = organization
                    RepositoryScopes =
                        List<CacheRepositoryScope>(
                            repositoryIds
                            |> Seq.map (fun repositoryId -> CacheRepositoryScope.Create(repositoryOrganizationId, repositoryId))
                        )
                    PublicKey = Grace.Types.CacheRegistration.CacheIdentityPublicKey.Create(publicKey.PublicKeyX, publicKey.PublicKeyY)
                    Endpoint =
                        parseResult.GetValue(Options.endpoint)
                        |> Option.ofObj
                        |> Option.defaultValue String.Empty
                    AllowHttpEndpoint = parseResult.GetValue(Options.allowHttp)
                    SoftwareVersion = parseResult.GetValue(Options.softwareVersion)
                    ProtocolVersion = parseResult.GetValue(Options.protocolVersion)
                    PrefetchSupported = false
                }

            match Lifecycle.validateEnrollmentRequest request with
            | Ok () -> Ok request
            | Error errors -> Error(GraceError.Create (String.concat " " errors) correlationId)

    /// Validates the configured standalone Grace Server URI without repository discovery.
    let private configuredServerUri parseResult =
        let value = Environment.GetEnvironmentVariable(Constants.EnvironmentVariables.GraceServerUri)

        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, uri when
            uri.Scheme = Uri.UriSchemeHttp
            || uri.Scheme = Uri.UriSchemeHttps
            ->
            Ok uri
        | _ ->
            Error(
                GraceError.Create
                    $"Set {Constants.EnvironmentVariables.GraceServerUri} to an absolute HTTP or HTTPS Grace Server URI before cache enrollment."
                    (getCorrelationId parseResult)
            )

    /// Resolves one normal Grace credential before cache-root mutation and returns its exact bearer value.
    let private resolveBearer (parseResult: ParseResult) (cancellationToken: CancellationToken) =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let! token = enrollmentDependencies.ResolveBearer()

            match token with
            | Ok (Some bearer) when not (String.IsNullOrWhiteSpace bearer) -> return Ok bearer
            | Ok _ -> return Error(GraceError.Create "Authentication required. Run 'grace authenticate login' and try again." (getCorrelationId parseResult))
            | Error message ->
                let classification =
                    let normalized = message.ToLowerInvariant()

                    if
                        normalized.Contains("expired")
                        || normalized.Contains("refresh")
                    then
                        "Stored interactive credentials are expired or unusable. Run 'grace authenticate login' and try again."
                    elif
                        normalized.Contains("secure")
                        || normalized.Contains("store")
                    then
                        "Interactive credential storage is unavailable. Use GRACE_TOKEN, configure M2M, or run 'grace authenticate login'."
                    elif
                        normalized.Contains("oidc")
                        || normalized.Contains("authentication is not configured")
                    then
                        "Interactive authentication configuration is unavailable. Set GRACE_SERVER_URI or use GRACE_TOKEN or M2M."
                    elif normalized.Contains("grace_token") then
                        "GRACE_TOKEN does not contain a valid Grace PAT."
                    elif
                        normalized.Contains("m2m")
                        || normalized.Contains("client_credentials")
                    then
                        "Machine-to-machine credential acquisition failed. Verify the configured M2M credential."
                    else
                        "Authentication could not provide a usable credential. Run 'grace authenticate login' or configure GRACE_TOKEN or M2M."

                return Error(GraceError.Create classification (getCorrelationId parseResult))
        }

    /// Renders only the approved redacted local identity facts for a cache command result.
    let private renderStatus (parseResult: ParseResult) (status: Grace.Cache.CacheIdentityStatus) =
        if parseResult |> json then
            let jsonStatus = JsonObject()
            jsonStatus["Class"] <- JsonValue.Create(status.Class)
            jsonStatus["Enrollment"] <- JsonValue.Create(status.Enrollment)
            jsonStatus["Key"] <- JsonValue.Create(status.Key)

            status.CacheId
            |> Option.iter (fun cacheId -> jsonStatus["CacheId"] <- JsonValue.Create(cacheId))

            status.Endpoint
            |> Option.iter (fun endpoint -> jsonStatus["Endpoint"] <- JsonValue.Create(endpoint))

            status.BoundaryKind
            |> Option.iter (fun boundaryKind -> jsonStatus["BoundaryKind"] <- JsonValue.Create(boundaryKind))

            status.RepositoryCount
            |> Option.iter (fun repositoryCount -> jsonStatus["RepositoryCount"] <- JsonValue.Create(repositoryCount))

            GraceReturnValue.Create jsonStatus (getCorrelationId parseResult)
            |> Ok
            |> renderOutput parseResult
        elif parseResult |> silent then
            0
        else
            let value = (string >> Markup.Escape)
            AnsiConsole.MarkupLine($"Class: {value status.Class}")
            AnsiConsole.MarkupLine($"Enrollment: {value status.Enrollment}")
            AnsiConsole.MarkupLine($"Key: {value status.Key}")

            status.CacheId
            |> Option.iter (fun cacheId -> AnsiConsole.MarkupLine($"CacheId: {cacheId:D}"))

            status.Endpoint
            |> Option.iter (fun endpoint -> AnsiConsole.MarkupLine($"Endpoint: {value endpoint}"))

            status.BoundaryKind
            |> Option.iter (fun boundary -> AnsiConsole.MarkupLine($"BoundaryKind: {value boundary}"))

            status.RepositoryCount
            |> Option.iter (fun count -> AnsiConsole.MarkupLine($"RepositoryCount: {count}"))

            0

    /// Reads status without networking, cleanup, repair, or repository-local state.
    let private statusHandler (parseResult: ParseResult) (cancellationToken: CancellationToken) =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let status = CacheIdentity.status stateRoot
            renderStatus parseResult status |> ignore
            return if status.Enrollment = "enrolled" then 0 else 1
        }

    /// Renders a redacted cache enrollment failure without exposing local state or transport details.
    let private renderEnrollmentFailure parseResult message = renderOutput parseResult (Error(GraceError.Create message (getCorrelationId parseResult)))

    /// Removes only this invocation's claimed attempt after a post-staging failure without masking the primary command outcome.
    let private completeAttempt claim (root: string) publicKey (completion: unit -> Task<int * bool>) =
        task {
            let mutable committed = false

            try
                let! exitCode, isCommitted = completion ()
                committed <- isCommitted
                return exitCode
            finally
                if not committed then
                    CacheIdentity.discardClaimedAttempt claim root (Some publicKey)
                    |> ignore
        }

    /// Enrolls exactly once after validation and one credential acquisition, then publishes ready only after local commit succeeds.
    let private enrollHandler (parseResult: ParseResult) (cancellationToken: CancellationToken) =
        task {
            let placeholder: Grace.Cache.CacheIdentityPublicKey = { PublicKeyX = String.replicate 43 "a"; PublicKeyY = String.replicate 43 "a" }

            match requestFromArguments parseResult placeholder with
            | Error error -> return renderOutput parseResult (Error error)
            | Ok _ ->
                match configuredServerUri parseResult with
                | Error error -> return renderOutput parseResult (Error error)
                | Ok serverUri ->
                    match CacheIdentity.inspectEnrollmentRoot stateRoot with
                    | Error CacheIdentityError.UnsupportedPlatform ->
                        return
                            renderOutput
                                parseResult
                                (Error(
                                    GraceError.Create
                                        "Cache enrollment is supported only on Linux with a protected Grace Cache state root."
                                        (getCorrelationId parseResult)
                                ))
                    | Error CacheIdentityError.StateUnavailable
                    | Ok CacheIdentityInspection.Invalid -> return renderEnrollmentFailure parseResult "The protected Grace Cache state root is invalid."
                    | Ok CacheIdentityInspection.Inaccessible ->
                        return renderEnrollmentFailure parseResult "The protected Grace Cache state root is inaccessible."
                    | Ok CacheIdentityInspection.Ready -> return renderEnrollmentFailure parseResult "The protected Grace Cache state root is already enrolled."
                    | Ok (CacheIdentityInspection.Missing
                    | CacheIdentityInspection.AttemptPresent) ->
                        let! bearer = resolveBearer parseResult cancellationToken

                        match bearer with
                        | Error error -> return renderOutput parseResult (Error error)
                        | Ok bearer ->
                            cancellationToken.ThrowIfCancellationRequested()

                            match CacheIdentity.tryAcquireEnrollmentClaim stateRoot with
                            | Error _ -> return renderEnrollmentFailure parseResult "Cache enrollment could not acquire exclusive local state."
                            | Ok claim ->
                                try
                                    let staleRecovery =
                                        match CacheIdentity.inspectEnrollmentRoot stateRoot with
                                        | Ok CacheIdentityInspection.AttemptPresent -> CacheIdentity.discardStaleAttempt claim stateRoot
                                        | Ok CacheIdentityInspection.Missing -> Ok()
                                        | _ -> Error CacheIdentityError.StateUnavailable

                                    match staleRecovery with
                                    | Error _ -> return renderEnrollmentFailure parseResult "A stale cache enrollment attempt could not be safely cleared."
                                    | Ok () ->
                                        cancellationToken.ThrowIfCancellationRequested()

                                        match CacheIdentity.createClaimedAttempt claim stateRoot with
                                        | Error _ ->
                                            return
                                                renderOutput
                                                    parseResult
                                                    (Error(
                                                        GraceError.Create
                                                            "The protected Grace Cache state root could not create an enrollment attempt."
                                                            (getCorrelationId parseResult)
                                                    ))
                                        | Ok publicKey ->
                                            return!
                                                completeAttempt claim stateRoot publicKey (fun () ->
                                                    task {
                                                        enrollmentDependencies.AfterAttemptCreated()
                                                        cancellationToken.ThrowIfCancellationRequested()

                                                        match requestFromArguments parseResult publicKey with
                                                        | Error error -> return renderOutput parseResult (Error error), false
                                                        | Ok request ->
                                                            match CacheIdentity.validateClaimedAttempt claim stateRoot publicKey with
                                                            | Error _ ->
                                                                return
                                                                    renderEnrollmentFailure
                                                                        parseResult
                                                                        "Cache enrollment local state changed before it could be sent.",
                                                                    false
                                                            | Ok () ->
                                                                let! response =
                                                                    CacheRegistration.Enroll(
                                                                        request,
                                                                        serverUri,
                                                                        bearer,
                                                                        getCorrelationId parseResult,
                                                                        cancellationToken
                                                                    )

                                                                match response with
                                                                | Error error -> return renderOutput parseResult (Error error), false
                                                                | Ok accepted ->
                                                                    cancellationToken.ThrowIfCancellationRequested()

                                                                    match accepted.ReturnValue.Status, accepted.ReturnValue.Registration with
                                                                    | CacheRegistrationRefreshStatus.Enrolled, Some registration ->
                                                                        let configuration: Grace.Cache.CacheAcceptedRegistration =
                                                                            {
                                                                                CacheId = registration.CacheId
                                                                                DisplayName = request.DisplayName
                                                                                BoundaryKind = request.BoundaryKind.ToString()
                                                                                OwnerId = request.OwnerId
                                                                                OrganizationId = request.OrganizationId
                                                                                RepositoryScopes =
                                                                                    request.RepositoryScopes
                                                                                    |> Seq.map
                                                                                        (fun (scope: Grace.Types.CacheRegistration.CacheRepositoryScope) ->
                                                                                            {
                                                                                                OrganizationId = scope.OrganizationId
                                                                                                RepositoryId = scope.RepositoryId
                                                                                            }: Grace.Cache.CacheAcceptedRepositoryScope)
                                                                                    |> Seq.toArray
                                                                                Endpoint = request.Endpoint
                                                                                ProtocolVersion = request.ProtocolVersion
                                                                                PublicKey = publicKey
                                                                            }

                                                                        match
                                                                            enrollmentDependencies.CommitReady (fun () ->
                                                                                CacheIdentity.commitClaimedReady claim stateRoot configuration
                                                                                |> Result.isOk)
                                                                            with
                                                                        | false ->
                                                                            return
                                                                                renderOutput
                                                                                    parseResult
                                                                                    (Error(
                                                                                        GraceError.Create
                                                                                            "Cache enrollment was accepted but protected ready state could not be committed."
                                                                                            (getCorrelationId parseResult)
                                                                                    )),
                                                                                false
                                                                        | true ->
                                                                            let status = CacheIdentity.status stateRoot
                                                                            let exitCode = renderStatus parseResult status
                                                                            return exitCode, status.Enrollment = "enrolled"
                                                                    | _ ->
                                                                        return
                                                                            renderOutput
                                                                                parseResult
                                                                                (Error(
                                                                                    GraceError.Create
                                                                                        "Cache enrollment did not return an accepted registration."
                                                                                        (getCorrelationId parseResult)
                                                                                )),
                                                                            false
                                                    })
                                finally
                                    CacheIdentity.releaseEnrollmentClaim claim
        }

    /// Runs the pure local cache status handler through System.CommandLine.
    type Status() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> = statusHandler parseResult cancellationToken

    /// Runs the static cache enrollment handler through System.CommandLine.
    type Enroll() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Task<int> =
            task {
                try
                    return! enrollHandler parseResult cancellationToken
                with
                | :? OperationCanceledException -> return renderEnrollmentFailure parseResult "Cache enrollment was cancelled."
                | _ -> return renderEnrollmentFailure parseResult "Cache enrollment failed before ready state could be published."
            }

    /// Builds the repository-independent `grace cache` command group.
    let Build =
        let cache = Command("cache", "Enroll and inspect a static Grace Cache identity.")

        let enroll =
            Command("enroll", "Enroll a static Linux Grace Cache identity.")
            |> addOption Options.displayName
            |> addOption Options.endpoint
            |> addOption Options.boundary
            |> addOption Options.ownerId
            |> addOption Options.organizationId
            |> addOption Options.repositoryIds
            |> addOption Options.repositoryOrganizationId
            |> addOption Options.allowHttp
            |> addOption Options.softwareVersion
            |> addOption Options.protocolVersion

        enroll.Action <- Enroll()
        cache.Subcommands.Add(enroll)
        let status = Command("status", "Report redacted local Grace Cache enrollment status.")
        status.Action <- Status()
        cache.Subcommands.Add(status)
        cache
