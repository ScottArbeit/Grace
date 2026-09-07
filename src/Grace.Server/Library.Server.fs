namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Parameters.Library
open Grace.Shared.Validation.Library
open Grace.Types.Common
open Grace.Types.DirectoryVersion
open Grace.Types.Library
open Grace.Types.Repository
open Grace.Types.UploadSession
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open NodaTime
open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks

/// Implements the authenticated HTTP boundary for repository-owned remote Libraries.
module Library =

    /// Returns the authenticated principal recorded by accepted operations.
    let private principalId (context: HttpContext) =
        PrincipalMapper.tryGetUserId context.User
        |> Option.defaultValue (Services.createMetadata context).Principal

    /// Returns one required service from the active HTTP request scope.
    let private service<'T when 'T: not struct> (context: HttpContext) = context.RequestServices.GetRequiredService<'T>()

    /// Returns the one repository-scoped Library actor.
    let private libraryActor (repositoryId: RepositoryId) = ApplicationContext.grainFactory.GetGrain<IRepositoryLibraryActor>(repositoryId)

    /// Wraps a successful Library value in Grace's public envelope.
    let private ok context value =
        context
        |> Services.result200Ok (GraceReturnValue.Create value (Services.getCorrelationId context))

    /// Returns one correlated public contract error.
    let private error statusCode context message =
        context
        |> Services.returnResult statusCode (GraceError.Create message (Services.getCorrelationId context))

    /// Reads the current repository aggregate used by upload and outgoing-system checks.
    let private repositoryState context =
        task {
            let ids = Services.getGraceIds context
            let actor = Repository.CreateActorProxy ids.OrganizationId ids.RepositoryId (Services.getCorrelationId context)
            return! actor.Get(Services.getCorrelationId context)
        }

    /// Captures the narrow actor-call authorization facts revalidated inside the serialized write turn.
    let private writeAuthorization context =
        let ids = Services.getGraceIds context

        {
            OwnerId = ids.OwnerId
            OrganizationId = ids.OrganizationId
            Principals =
                PrincipalMapper.getPrincipals context.User
                |> List.toArray
            EffectiveClaims =
                PrincipalMapper.getEffectiveClaims context.User
                |> Set.toArray
        }

    /// Computes stable catalog-operation identity independently from the resulting catalog version.
    let private catalogRequestHash repositoryId add expectedVersion libraryPath operationId =
        {|
            RepositoryId = repositoryId
            OperationId = operationId
            Operation = if add then "add" else "remove"
            ExpectedVersion = expectedVersion
            LibraryPath = libraryPath
        |}
        |> fun value -> JsonSerializer.SerializeToUtf8Bytes(value, Constants.JsonSerializerOptions)
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    /// Reports whether a committed version-control directory owns any entry below one proposed Library root.
    let internal directoryVersionOwnsRoot normalizedRoot (directoryVersion: DirectoryVersion) =
        let ownsPath (path: string) =
            let normalizedPath = path.Replace('\\', '/').Trim('/')

            pathsEqual normalizedPath normalizedRoot
            || normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase)

        directoryVersion.RelativePath
        |> string
        |> ownsPath
        || directoryVersion.Files
           |> Seq.exists (fun file -> file.RelativePath |> string |> ownsPath)

    /// Checks the current version-control root through existing Reference and DirectoryVersion actors.
    let private versionControlledRootIsEmpty (repository: RepositoryDto) normalizedRoot correlationId =
        task {
            let! branchIds =
                Grace.Actors.Services.getBranches repository.OwnerId repository.OrganizationId repository.RepositoryId Int32.MaxValue false correlationId

            let directoryVersionIds = Collections.Generic.HashSet<DirectoryVersionId>()

            for branch in branchIds do
                let! references = Grace.Actors.Services.getReferences repository.RepositoryId branch.BranchId Int32.MaxValue correlationId

                references
                |> Seq.filter (fun reference -> reference.DeletedAt.IsNone)
                |> Seq.iter (fun reference ->
                    directoryVersionIds.Add reference.DirectoryId
                    |> ignore)

            let mutable occupied = false

            for directoryVersionId in directoryVersionIds do
                if not occupied then
                    let actor = DirectoryVersion.CreateActorProxy directoryVersionId repository.RepositoryId correlationId
                    let! rootDirectory = actor.Get correlationId
                    let! descendants = actor.GetRecursiveDirectoryVersions false correlationId

                    occupied <-
                        directoryVersionOwnsRoot normalizedRoot rootDirectory.DirectoryVersion
                        || descendants
                           |> Array.exists (fun directory -> directoryVersionOwnsRoot normalizedRoot directory.DirectoryVersion)

            return not occupied
        }

    /// Reads the exact current Library catalog.
    let GetCatalog: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context

                let! catalog =
                    (libraryActor ids.RepositoryId)
                        .GetCatalog(Services.getCorrelationId context)

                return! ok context catalog
            }

    /// Lists configured roots in deterministic portable order.
    let ListLibraries: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context

                let! catalog =
                    (libraryActor ids.RepositoryId)
                        .GetCatalog(Services.getCorrelationId context)

                return!
                    ok
                        context
                        { catalog with
                            Libraries =
                                catalog.Libraries
                                |> Array.sortWith (fun left right -> StringComparer.OrdinalIgnoreCase.Compare(left, right))
                        }
            }

    /// Applies one actor-owned exact-version catalog add or remove.
    let private changeLibrary add : HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let correlationId = Services.getCorrelationId context

                let! expectedVersion, path, operationId =
                    if add then
                        task {
                            let! value = context.BindJsonAsync<AddLibraryParameters>()
                            return value.ExpectedVersion, value.LibraryPath, value.OperationId
                        }
                    else
                        task {
                            let! value = context.BindJsonAsync<RemoveLibraryParameters>()
                            return value.ExpectedVersion, value.LibraryPath, value.OperationId
                        }

                let! outgoingSystemEmpty =
                    if add then
                        match normalizeRepositoryRelativePath path with
                        | Error _ -> Task.FromResult true
                        | Ok normalized ->
                            task {
                                let! repository = repositoryState context
                                return! versionControlledRootIsEmpty repository normalized correlationId
                            }
                    else
                        Task.FromResult true

                let requestHash = catalogRequestHash ids.RepositoryId add expectedVersion path operationId

                match! (libraryActor ids.RepositoryId).ChangeCatalog
                           add
                           expectedVersion
                           path
                           operationId
                           requestHash
                           (principalId context)
                           (writeAuthorization context)
                           outgoingSystemEmpty
                           correlationId
                    with
                | Ok result -> return! ok context result
                | Error reason -> return! error StatusCodes.Status403Forbidden context reason
            }

    /// Adds one exact-version Library root.
    let AddLibrary: HttpHandler = changeLibrary true

    /// Removes one exact-version Library root.
    let RemoveLibrary: HttpHandler = changeLibrary false

    /// Starts or replays the repository-bound upload used by a later content change.
    let PrepareContent: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let correlationId = Services.getCorrelationId context
                let! parameters = context.BindJsonAsync<PrepareLibraryContentParameters>()

                if parameters.OperationId = Guid.Empty
                   || not (isLowercaseHash parameters.Blake3Hash)
                   || not (isLowercaseHash parameters.Sha256Hash)
                   || parameters.Size <= 0L then
                    return! error StatusCodes.Status400BadRequest context "OperationId, lowercase hashes, and a positive size are required."
                else
                    let uploadSessionId = Grace.Actors.LibraryDecision.deterministicGuid ids.RepositoryId parameters.OperationId "upload-session"
                    let! repository = repositoryState context

                    let expiresAt =
                        SystemClock.Instance.GetCurrentInstant()
                        + Duration.FromMinutes 15L

                    let authorizedScope = $"Library/{uploadSessionId:D}"

                    let start =
                        {
                            UploadSessionId = uploadSessionId
                            OwnerId = ids.OwnerId
                            OrganizationId = ids.OrganizationId
                            RepositoryId = ids.RepositoryId
                            StoragePoolId = repository.StoragePoolId
                            AuthorizedScope = authorizedScope
                            FileContentHash = parameters.Blake3Hash
                            ExpectedSize = parameters.Size
                            ChunkingSuiteId = RabinChunking.SuiteName
                            SamplingPolicySnapshot = JsonSerializer.Serialize(repository.ManifestEligibilityPolicy, Constants.JsonSerializerOptions)
                            OperationId = $"Library-prepare:{parameters.OperationId:D}"
                            LibraryPreparation =
                                Some
                                    {
                                        OperationId = parameters.OperationId
                                        PrincipalId = principalId context
                                        ExpectedSha256 = parameters.Sha256Hash
                                        ExpiresAt = expiresAt
                                    }
                        }

                    let! upload = (libraryActor ids.RepositoryId).PrepareContent start correlationId

                    return!
                        ok
                            context
                            {
                                UploadSessionId = upload.UploadSessionId
                                Blake3Hash = parameters.Blake3Hash
                                Sha256Hash = parameters.Sha256Hash
                                Size = parameters.Size
                                AuthorizedScope = upload.AuthorizedScope
                                StoragePoolId = upload.StoragePoolId
                                ExpiresAt = expiresAt
                            }
            }

    /// Submits one validated closed Library command through the repository actor.
    let SubmitChange: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<SubmitLibraryChangeParameters>()

                match validateChangeShape parameters with
                | Error message -> return! error StatusCodes.Status400BadRequest context message
                | Ok () ->
                    let command = toChangeCommand ids.RepositoryId parameters

                    match! (libraryActor ids.RepositoryId).Submit command (principalId context) (writeAuthorization context) (Services.getCorrelationId context)
                        with
                    | Ok receipt -> return! ok context receipt
                    | Error reason -> return! error StatusCodes.Status403Forbidden context reason
            }

    /// Reads one permanent item-operation result.
    let GetOperation: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<GetLibraryOperationParameters>()

                match! (libraryActor ids.RepositoryId).GetOperation parameters.OperationId (Services.getCorrelationId context) with
                | Some receipt -> return! ok context receipt
                | None -> return! Services.result404NotFound context
            }

    /// Reads one current Library item projection.
    let GetItem: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<GetLibraryItemParameters>()

                match! (libraryActor ids.RepositoryId).GetItem parameters.ItemId (Services.getCorrelationId context) with
                | Some item -> return! ok context item
                | None -> return! Services.result404NotFound context
            }

    /// Reads one exact parent/name slot, including a deterministic first-generation vacancy.
    let GetNamespaceSlot: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<GetLibraryNamespaceSlotParameters>()

                match parameters.Parent, normalizeName parameters.Name with
                | Some parent, Ok name ->
                    try
                        let! slot = (libraryActor ids.RepositoryId).GetSlot parent name (Services.getCorrelationId context)

                        return! ok context slot
                    with
                    | :? InvalidOperationException -> return! Services.result404NotFound context
                | _ -> return! error StatusCodes.Status400BadRequest context "A parent and portable name are required."
            }

    /// Returns content-free repository synchronization progress.
    let GetStatus: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context

                let! status =
                    (libraryActor ids.RepositoryId)
                        .GetStatus(Services.getCorrelationId context)

                return! ok context status
            }

    /// Starts one immutable manifest-last baseline transfer.
    let StartBootstrap: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<StartLibraryBootstrapParameters>()

                if not (pageSizeIsValid parameters.PageSize) then
                    return! error StatusCodes.Status400BadRequest context "PageSize must be between 1 and 2000."
                else
                    let! page = (libraryActor ids.RepositoryId).StartBootstrap parameters.PageSize (Services.getCorrelationId context)

                    return! ok context page
            }

    /// Continues one immutable baseline transfer from its signed page token.
    let ContinueBootstrap: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<ContinueLibraryBootstrapParameters>()

                if not (pageSizeIsValid parameters.PageSize) then
                    return! error StatusCodes.Status400BadRequest context "PageSize must be between 1 and 2000."
                else
                    match! (libraryActor ids.RepositoryId).ContinueBootstrap
                               parameters.BootstrapId
                               parameters.PageToken
                               parameters.PageSize
                               (Services.getCorrelationId context)
                        with
                    | Some page -> return! ok context page
                    | None -> return! error StatusCodes.Status410Gone context "The bootstrap page token is invalid or expired."
            }

    /// Reads committed ordered changes or a typed rebaseline response.
    let GetChanges: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<GetLibraryChangesParameters>()

                if not (pageSizeIsValid parameters.PageSize) then
                    return! error StatusCodes.Status400BadRequest context "PageSize must be between 1 and 2000."
                else
                    let! page =
                        (libraryActor ids.RepositoryId).GetChanges
                            parameters.AfterCursor
                            (Option.ofObj parameters.PageToken)
                            parameters.PageSize
                            (Services.getCorrelationId context)

                    return! ok context page
            }

    /// Mints a short-lived signed read for bytes proven by one accepted content revision.
    let PrepareContentRead: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let correlationId = Services.getCorrelationId context
                let! parameters = context.BindJsonAsync<PrepareLibraryContentReadParameters>()
                let actor = libraryActor ids.RepositoryId

                match! actor.GetAcceptedChange parameters.ContentRevision correlationId with
                | Some change when
                    change.Item.ItemId = parameters.ItemId
                    && change.Item.Content
                       |> Option.exists (fun content -> content.ContentVersionId = parameters.ContentVersionId)
                    && change.Item.ContentRevision = Some parameters.ContentRevision
                    ->
                    match! actor.GetContentLocation parameters.ContentVersionId correlationId with
                    | Some location ->
                        let expiresAt =
                            SystemClock.Instance.GetCurrentInstant()
                            + Duration.FromSeconds 60L

                        let token =
                            Grace.Actors.LibraryTokens.contentRead
                                (service<byte array> context)
                                ids.RepositoryId
                                parameters.ItemId
                                parameters.ContentVersionId
                                parameters.ContentRevision
                                (expiresAt.ToUnixTimeSeconds())

                        return! ok context { DownloadPath = $"/libraries/content/{token}"; Content = location.Content; ExpiresAt = expiresAt }
                    | None -> return! Services.result404NotFound context
                | _ -> return! Services.result404NotFound context
            }

    /// Streams immutable bytes after validating the repository, accepted revision, item, content, and expiry signature.
    let DownloadContent (token: string) : HttpHandler =
        fun _ context ->
            task {
                let now = SystemClock.Instance.GetCurrentInstant()

                match Grace.Actors.LibraryTokens.tryContentRead (service<byte array> context) (now.ToUnixTimeSeconds()) token with
                | None -> return! Services.result404NotFound context
                | Some (repositoryId, itemId, contentVersionId, contentRevision) ->
                    let actor = libraryActor repositoryId
                    let correlationId = Services.getCorrelationId context
                    let! accepted = actor.GetAcceptedChange contentRevision correlationId
                    let! contentLocation = actor.GetContentLocation contentVersionId correlationId

                    match accepted, contentLocation with
                    | Some change, Some location when
                        change.Item.ItemId = itemId
                        && change.Item.Content
                           |> Option.exists (fun content -> content.ContentVersionId = contentVersionId)
                        && change.Item.ContentRevision = Some contentRevision
                        ->
                        let repositoryActor = Repository.CreateActorProxy Guid.Empty repositoryId correlationId
                        let! repository = repositoryActor.Get correlationId

                        let fileVersion =
                            FileVersion.CreateWithHashes
                                (RelativePath $"Library/{location.Content.ContentVersionId:D}")
                                (Sha256Hash location.Content.Sha256Hash)
                                (Blake3Hash location.Content.Blake3Hash)
                                String.Empty
                                true
                                location.Content.Size

                        fileVersion.ContentReference <- FileContentReference.FileManifest location.Manifest

                        match! NormalFileMaterialization.materializeBytes repository location.AuthorizedScope fileVersion correlationId context.RequestAborted
                            with
                        | Error _ -> return! Services.result404NotFound context
                        | Ok bytes ->
                            context.Response.ContentLength <- int64 bytes.Length
                            context.Response.Headers.ETag <- $"\"{location.Content.Blake3Hash}\""
                            context.Response.Headers[ "X-Content-BLAKE3" ] <- location.Content.Blake3Hash
                            context.Response.Headers[ "X-Content-SHA256" ] <- location.Content.Sha256Hash
                            context.Response.ContentType <- "application/octet-stream"

                            do!
                                context
                                    .Response
                                    .Body
                                    .WriteAsync(bytes, context.RequestAborted)
                                    .AsTask()

                            return Some context
                    | _ -> return! Services.result404NotFound context
            }
