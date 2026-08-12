namespace Grace.CLI.Command

open Grace.Cache
open Grace.CLI.Common
open Grace.SDK
open Grace.Shared.Parameters
open Grace.Types.CacheRegistration
open Grace.Types.Common
open Spectre.Console
open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Threading
open System.Threading.Tasks

/// Groups static Cache enrollment and private local status commands.
module CacheCommand =

    /// Defines cache command line options.
    module private Options =
        let displayName = new Option<string>("--display-name", Required = true, Description = "Cache display name.", Arity = ArgumentArity.ExactlyOne)
        let endpoint = new Option<string>("--endpoint", Required = true, Description = "Cache HTTPS endpoint.", Arity = ArgumentArity.ExactlyOne)

        let boundary =
            new Option<string>("--boundary", Required = true, Description = "Enrollment boundary: owner or organization.", Arity = ArgumentArity.ExactlyOne)

        let ownerId = new Option<Guid>("--owner-id", Required = true, Description = "Owner ID.", Arity = ArgumentArity.ExactlyOne)

        let organizationId =
            new Option<Guid>(
                "--organization-id",
                Required = false,
                Description = "Organization ID for an organization boundary.",
                Arity = ArgumentArity.ZeroOrOne
            )

        let repositoryIds =
            new Option<Guid []>("--repository-id", Required = true, Description = "One or more repository IDs.", Arity = ArgumentArity.OneOrMore)

        let repositoryOrganizationId =
            new Option<Guid>(
                "--repository-organization-id",
                Required = true,
                Description = "Organization ID that owns every --repository-id supplied for this enrollment.",
                Arity = ArgumentArity.ExactlyOne
            )

        let allowHttp =
            new Option<bool>(
                "--allow-http",
                Required = false,
                Description = "Allow the exact HTTP endpoint supplied by --endpoint.",
                Arity = ArgumentArity.Zero
            )

        let softwareVersion =
            new Option<string>("--software-version", Required = false, DefaultValueFactory = (fun _ -> "Grace.Cache"), Description = "Cache software version.")

        let protocolVersion =
            new Option<string>("--protocol-version", Required = false, DefaultValueFactory = (fun _ -> "v1"), Description = "Cache protocol version.")

    /// Builds one authenticated SDK enrollment request from the generated local public key and explicit administrator inputs.
    let private enrollmentParameters (parseResult: ParseResult) (identity: Grace.Cache.CacheIdentityPublicKey) =
        let boundary = parseResult.GetValue(Options.boundary)
        let ownerId = parseResult.GetValue(Options.ownerId)
        let organizationId = parseResult.GetValue(Options.organizationId)
        let repositoryOrganizationId = parseResult.GetValue(Options.repositoryOrganizationId)

        match boundary.ToLowerInvariant() with
        | "owner" when
            organizationId = Guid.Empty
            && repositoryOrganizationId <> Guid.Empty
            ->
            let parameters: Grace.Shared.Parameters.CacheRegistration.EnrollCacheParameters = Grace.Shared.Parameters.CacheRegistration.EnrollCacheParameters()
            parameters.DisplayName <- parseResult.GetValue(Options.displayName)
            parameters.BoundaryKind <- CacheBoundaryKind.Owner
            parameters.OwnerId <- ownerId

            parameters.RepositoryScopes <-
                List<CacheRepositoryScope>(
                    parseResult.GetValue(Options.repositoryIds)
                    |> Seq.map (fun repositoryId -> CacheRepositoryScope.Create(repositoryOrganizationId, repositoryId))
                )

            parameters.PublicKey <- Grace.Types.CacheRegistration.CacheIdentityPublicKey.Create(identity.PublicKeyX, identity.PublicKeyY)
            parameters.Endpoint <- parseResult.GetValue(Options.endpoint)
            parameters.AllowHttpEndpoint <- parseResult.GetValue(Options.allowHttp)
            parameters.SoftwareVersion <- parseResult.GetValue(Options.softwareVersion)
            parameters.ProtocolVersion <- parseResult.GetValue(Options.protocolVersion)
            parameters.CorrelationId <- getCorrelationId parseResult
            Ok(parameters, None)
        | "organization" when
            organizationId <> Guid.Empty
            && organizationId = repositoryOrganizationId
            ->
            let parameters: Grace.Shared.Parameters.CacheRegistration.EnrollCacheParameters = Grace.Shared.Parameters.CacheRegistration.EnrollCacheParameters()
            parameters.DisplayName <- parseResult.GetValue(Options.displayName)
            parameters.BoundaryKind <- CacheBoundaryKind.Organization
            parameters.OwnerId <- ownerId
            parameters.OrganizationId <- Some organizationId

            parameters.RepositoryScopes <-
                List<CacheRepositoryScope>(
                    parseResult.GetValue(Options.repositoryIds)
                    |> Seq.map (fun repositoryId -> CacheRepositoryScope.Create(organizationId, repositoryId))
                )

            parameters.PublicKey <- Grace.Types.CacheRegistration.CacheIdentityPublicKey.Create(identity.PublicKeyX, identity.PublicKeyY)
            parameters.Endpoint <- parseResult.GetValue(Options.endpoint)
            parameters.AllowHttpEndpoint <- parseResult.GetValue(Options.allowHttp)
            parameters.SoftwareVersion <- parseResult.GetValue(Options.softwareVersion)
            parameters.ProtocolVersion <- parseResult.GetValue(Options.protocolVersion)
            parameters.CorrelationId <- getCorrelationId parseResult
            Ok(parameters, Some organizationId)
        | _ -> Error(GraceError.Create "Cache boundary and organization inputs are invalid." (getCorrelationId parseResult))

    /// Converts the SDK parameter object into the public request shape validated before local staging and server send.
    let private enrollmentRequest (parameters: Grace.Shared.Parameters.CacheRegistration.EnrollCacheParameters) =
        {
            Class = parameters.Class
            DisplayName = parameters.DisplayName
            BoundaryKind = parameters.BoundaryKind
            OwnerId = parameters.OwnerId
            OrganizationId = parameters.OrganizationId
            RepositoryScopes = parameters.RepositoryScopes
            PublicKey = parameters.PublicKey
            Endpoint = parameters.Endpoint
            AllowHttpEndpoint = parameters.AllowHttpEndpoint
            SoftwareVersion = parameters.SoftwareVersion
            ProtocolVersion = parameters.ProtocolVersion
            PrefetchSupported = parameters.PrefetchSupported
        }

    /// Rejects malformed enrollment input before local key staging or authenticated server transport.
    let private validateEnrollmentParameters parseResult parameters =
        match Lifecycle.validateEnrollmentRequest (enrollmentRequest parameters) with
        | Ok () -> Ok parameters
        | Error errors -> Error(GraceError.Create (String.concat " " errors) (getCorrelationId parseResult))

    /// Executes local status observation without sending a server request or changing filesystem state.
    let private statusHandler (parseResult: ParseResult) (cancellationToken: CancellationToken) =
        task {
            cancellationToken.ThrowIfCancellationRequested()
            let status = CacheIdentity.status CacheIdentity.StateRoot cancellationToken
            let result = GraceReturnValue.Create status (getCorrelationId parseResult)
            renderOutput parseResult (Ok result) |> ignore
            return if status.Enrollment = "enrolled" then 0 else 1
        }

    /// Executes one static enrollment attempt and commits local ready state only after the server accepts it.
    let private enrollHandler (parseResult: ParseResult) (cancellationToken: CancellationToken) =
        task {
            cancellationToken.ThrowIfCancellationRequested()

            let placeholderPublicKey = Grace.Types.CacheRegistration.CacheIdentityPublicKey.Create("validated", "validated")

            match enrollmentParameters parseResult { PublicKeyX = placeholderPublicKey.PublicKeyX; PublicKeyY = placeholderPublicKey.PublicKeyY } with
            | Error error -> return renderOutput parseResult (Error error)
            | Ok (prevalidatedParameters, _) ->
                match validateEnrollmentParameters parseResult prevalidatedParameters with
                | Error error -> return renderOutput parseResult (Error error)
                | Ok _ ->
                    match CacheIdentity.validateStateRoot CacheIdentity.StateRoot cancellationToken with
                    | Error message -> return renderOutput parseResult (Error(GraceError.Create message (getCorrelationId parseResult)))
                    | Ok () ->
                        CacheIdentity.cleanupStaleStaging CacheIdentity.StateRoot cancellationToken

                        match CacheIdentity.prepare CacheIdentity.StateRoot cancellationToken with
                        | Error message -> return renderOutput parseResult (Error(GraceError.Create message (getCorrelationId parseResult)))
                        | Ok prepared ->
                            match enrollmentParameters parseResult prepared.PublicKey with
                            | Error error ->
                                CacheIdentity.discard prepared cancellationToken
                                return renderOutput parseResult (Error error)
                            | Ok (parameters, organizationId) ->
                                match validateEnrollmentParameters parseResult parameters with
                                | Error error ->
                                    CacheIdentity.discard prepared cancellationToken
                                    return renderOutput parseResult (Error error)
                                | Ok parameters ->
                                    match! Grace.SDK.CacheRegistration.Enroll(parameters, cancellationToken) with
                                    | Error error ->
                                        CacheIdentity.discard prepared cancellationToken
                                        return renderOutput parseResult (Error error)
                                    | Ok response ->
                                        match response.ReturnValue.Registration with
                                        | None ->
                                            CacheIdentity.discard prepared cancellationToken

                                            return
                                                renderOutput
                                                    parseResult
                                                    (Error(GraceError.Create "Cache enrollment did not return a registration." (getCorrelationId parseResult)))
                                        | Some registration ->
                                            let configuration =
                                                CacheIdentity.ReadyConfiguration.create
                                                    registration.CacheId
                                                    parameters.Endpoint
                                                    (parameters
                                                        .BoundaryKind
                                                        .ToString()
                                                        .ToLowerInvariant())
                                                    parameters.OwnerId
                                                    organizationId
                                                    (parameters.RepositoryScopes
                                                     |> Seq.map (fun scope -> scope.RepositoryId))
                                                    parameters.DisplayName
                                                    parameters.ProtocolVersion
                                                    prepared.PublicKey

                                            match CacheIdentity.commitReady prepared configuration cancellationToken with
                                            | Ok () -> return renderOutput parseResult (Ok response)
                                            | Error message ->
                                                CacheIdentity.discard prepared cancellationToken
                                                return renderOutput parseResult (Error(GraceError.Create message (getCorrelationId parseResult)))
        }

    /// Runs the cache status action when System.CommandLine dispatches the parsed command.
    type Status() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task { return! statusHandler parseResult cancellationToken }

    /// Runs the cache enrollment action when System.CommandLine dispatches the parsed command.
    type Enroll() =
        inherit AsynchronousCommandLineAction()

        override _.InvokeAsync(parseResult: ParseResult, cancellationToken: CancellationToken) : Tasks.Task<int> =
            task { return! enrollHandler parseResult cancellationToken }

    /// Builds the `grace cache` command group and its static enrollment/status subcommands.
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
