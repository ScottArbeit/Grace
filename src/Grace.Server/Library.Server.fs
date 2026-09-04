namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Parameters.Library
open Grace.Shared.Utilities
open Grace.Shared.Validation.Library
open Grace.Types.Common
open Grace.Types.Repository
open Grace.Types.Library
open Grace.Types.UploadSession
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open NodaTime
open Orleans
open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// Holds the private payload protected by one opaque synchronization token.
[<CLIMutable>]
type LibraryTokenPayload = { Kind: string; RepositoryId: RepositoryId; Value: string; Offset: int; ExpiresAtUnixMilliseconds: int64 }

/// Implements opaque, expiring page and byte-grant tokens for the remote library contract.
type LibraryOpaqueTokenCodec(secret: byte array) =

    do
        if isNull secret || secret.Length < 32 then
            invalidArg (nameof secret) "The Library token secret must contain at least 32 bytes."

    /// Encodes bytes with the URL-safe base64 alphabet and no padding.
    let encodeBase64Url (bytes: byte array) =
        Convert
            .ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')

    /// Decodes URL-safe base64 without accepting malformed padding.
    let tryDecodeBase64Url (value: string) =
        try
            let padded =
                value.Replace('-', '+').Replace('_', '/')
                + String.replicate ((4 - value.Length % 4) % 4) "="

            Some(Convert.FromBase64String padded)
        with
        | :? FormatException -> None

    /// Protects one repository-bound value, offset, kind, and expiry.
    member _.Encode(kind, repositoryId, value, offset, expiresAt: Instant) =
        let payload =
            { Kind = kind; RepositoryId = repositoryId; Value = value; Offset = offset; ExpiresAtUnixMilliseconds = expiresAt.ToUnixTimeMilliseconds() }
            |> fun token -> JsonSerializer.SerializeToUtf8Bytes(token, Constants.JsonSerializerOptions)

        use hmac = new HMACSHA256(secret)
        let signature = hmac.ComputeHash payload
        encodeBase64Url (Array.append payload signature)

    /// Validates one opaque token and returns its private payload while it remains current.
    member _.TryDecode(expectedKind, token, now: Instant) =
        match tryDecodeBase64Url token with
        | Some bytes when bytes.Length > 32 ->
            let payload = bytes[0 .. bytes.Length - 33]
            let suppliedSignature = bytes[bytes.Length - 32 ..]

            use hmac = new HMACSHA256(secret)
            let expectedSignature = hmac.ComputeHash payload

            if not (CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature)) then
                None
            else
                try
                    let value = JsonSerializer.Deserialize<LibraryTokenPayload>(payload, Constants.JsonSerializerOptions)

                    if isNull (box value)
                       || value.Kind <> expectedKind
                       || value.ExpiresAtUnixMilliseconds
                          <= now.ToUnixTimeMilliseconds() then
                        None
                    else
                        Some value
                with
                | :? JsonException -> None
        | _ -> None

/// Implements the complete remote-only library HTTP application boundary.
module Library =

    [<Literal>]
    let private BootstrapTokenKind = "bootstrap"

    [<Literal>]
    let private DeltaTokenKind = "change page"

    [<Literal>]
    let private ReadGrantTokenKind = "read-grant"

    /// Returns the current authenticated principal binding used by durable preparations and receipts.
    let private principalId (context: HttpContext) =
        PrincipalMapper.tryGetUserId context.User
        |> Option.defaultValue (Services.createMetadata context).Principal

    /// Returns a required request service from the active HTTP scope.
    let private service<'T when 'T: not struct> (context: HttpContext) = context.RequestServices.GetRequiredService<'T>()

    /// Returns the Library repository grain for one exact repository.
    let private LibraryActor (repositoryId: RepositoryId) = ApplicationContext.grainFactory.GetGrain<IRepositoryLibraryActor>(repositoryId)

    /// Wraps a successful Library result in Grace's public response envelope.
    let private ok context value =
        context
        |> Services.result200Ok (GraceReturnValue.Create value (Services.getCorrelationId context))

    /// Returns one correlated public contract error.
    let private error statusCode context message =
        context
        |> Services.returnResult statusCode (GraceError.Create message (Services.getCorrelationId context))

    /// Reads the current Repository actor state and exact root configuration.
    let private repositoryState context =
        task {
            let ids = Services.getGraceIds context
            let actor = Repository.CreateActorProxy ids.OrganizationId ids.RepositoryId (Services.getCorrelationId context)
            return! actor.Get(Services.getCorrelationId context)
        }

    /// Derives one stable RFC-4122 identifier from exact content identity material.
    let private contentVersionId repositoryId blake3Hash sha256Hash size =
        let bytes =
            Encoding.UTF8.GetBytes($"{repositoryId:D}:{blake3Hash}:{sha256Hash}:{size}")
            |> SHA256.HashData

        let value = bytes[0..15]
        value[6] <- (value[6] &&& 0x0Fuy) ||| 0x50uy
        value[8] <- (value[8] &&& 0x3Fuy) ||| 0x80uy
        Guid value

    /// Computes the stable normalized request hash used for operation identity checks.
    let private changeRequestHash repositoryId (parameters: SubmitLibraryChangeParameters) =
        let normalized =
            {|
                RepositoryId = repositoryId
                OperationId = parameters.OperationId
                LibraryCatalogVersion = parameters.LibraryCatalogVersion
                ChangeKind = parameters.ChangeKind
                ItemKind = parameters.ItemKind
                ItemId = if parameters.ItemId.HasValue then Some parameters.ItemId.Value else None
                NamespacePrecondition = parameters.NamespacePrecondition
                ContentPrecondition = parameters.ContentPrecondition
                CreationSlotExpectation = parameters.CreationSlotExpectation
                DestinationParent = parameters.DestinationParent
                DestinationName = Option.ofObj parameters.DestinationName
                PreparedContentId =
                    if parameters.PreparedContentId.HasValue then
                        Some parameters.PreparedContentId.Value
                    else
                        None
            |}

        normalized
        |> fun value -> JsonSerializer.SerializeToUtf8Bytes(value, Constants.JsonSerializerOptions)
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    /// Computes the stable request identity for one catalog operation independently of later catalog versions.
    let private catalogRequestHash repositoryId addLibraryOperation expectedVersion libraryPath operationId =
        {|
            RepositoryId = repositoryId
            OperationId = operationId
            Operation = if addLibraryOperation then "add" else "remove"
            ExpectedVersion = expectedVersion
            LibraryPath = libraryPath
        |}
        |> fun value -> JsonSerializer.SerializeToUtf8Bytes(value, Constants.JsonSerializerOptions)
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

    /// Returns a public epoch token without exposing the private epoch identifier.
    let private publicEpoch (codec: ILibraryCursorCodec) repositoryId epoch = codec.Encode(repositoryId, epoch, 0L)

    /// Builds a coarse wake only for receipts that represent one accepted repository change.
    let internal libraryAvailableFromReceipt
        (codec: ILibraryCursorCodec)
        (control: LibraryControlDocument)
        (receipt: LibraryOperationReceiptDto)
        correlationId
        =
        match receipt.Change, receipt.Cursor with
        | Some _, Some cursor ->
            LibraryContentAvailable.Create(
                control.RepositoryId,
                publicEpoch codec control.RepositoryId control.CursorEpoch,
                cursor,
                receipt.LibraryCatalogVersion,
                receipt.RecordedAt,
                correlationId
            )
            |> Some
        | _ -> None

    /// Attempts the post-commit wake after durable completion without changing the recorded operation result.
    let private tryNotifyLibraryContentAvailable context repositoryId (receipt: LibraryOperationReceiptDto) =
        task {
            try
                match receipt.Change, receipt.Cursor with
                | Some _, Some _ ->
                    let store = service<ILibraryStore> context
                    let codec = service<ILibraryCursorCodec> context
                    let! control = store.ReadControlAsync(repositoryId, context.RequestAborted)

                    match libraryAvailableFromReceipt codec control.Document receipt (Services.getCorrelationId context) with
                    | Some payload ->
                        let hubContext = context.RequestServices.GetService<IHubContext<Notification.NotificationHub, Notification.IGraceClientConnection>>()
                        let! _ = Notification.notifyLibraryContentAvailableClients hubContext payload
                        return ()
                    | None -> return ()
                | _ -> return ()
            with
            | ex ->
                let loggerFactory = context.RequestServices.GetService<ILoggerFactory>()

                if not <| isNull loggerFactory then
                    loggerFactory
                        .CreateLogger("Library.Server")
                        .LogWarning(
                            ex,
                            "Best-effort library wake preparation failed for RepositoryId: {RepositoryId}; OperationId: {OperationId}.",
                            repositoryId,
                            receipt.OperationId
                        )
        }

    /// Returns true when one version-controlled directory snapshot owns a live path at or below an exact Library.
    let internal directoryVersionOwnsRoot normalizedRoot (directoryVersion: DirectoryVersion) =
        let ownsPath (path: string) =
            let normalizedPath = path.Replace('\\', '/').Trim('/')

            pathsEqual normalizedPath normalizedRoot
            || normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase)

        ownsPath (string directoryVersion.RelativePath)
        || directoryVersion.Files
           |> Seq.exists (fun file -> ownsPath (string file.RelativePath))

    /// Checks every selectable Reference root DirectoryVersion for a live path owned by the proposed Library.
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
                    let actor = Grace.Actors.Extensions.ActorProxy.DirectoryVersion.CreateActorProxy directoryVersionId repository.RepositoryId correlationId

                    let! rootDirectory = actor.Get correlationId
                    let! descendants = actor.GetRecursiveDirectoryVersions false correlationId

                    occupied <-
                        directoryVersionOwnsRoot normalizedRoot rootDirectory.DirectoryVersion
                        || descendants
                           |> Array.exists (fun directory -> directoryVersionOwnsRoot normalizedRoot directory.DirectoryVersion)

            return not occupied
        }

    /// Loads and revalidates finalized Library preparation facts owned by the existing upload-session actor.
    let private resolvePreparedContent (context: HttpContext) repositoryId operationId (preparedContentId: Nullable<Guid>) =
        task {
            let transferStore = service<ILibraryTransferStore> context

            if not preparedContentId.HasValue then
                return Ok(None, None)
            else
                let actor = UploadSession.CreateActorProxy preparedContentId.Value repositoryId (Services.getCorrelationId context)
                let correlationId = Services.getCorrelationId context
                let! currentSession = actor.Get correlationId

                let! session =
                    task {
                        if currentSession.UploadSessionId = UploadSessionId.Empty then
                            let! events = actor.GetEvents correlationId

                            return
                                events
                                |> Seq.fold (fun dto event -> UploadSessionDto.UpdateDto event dto) UploadSessionDto.Default
                        else
                            return currentSession
                    }

                match session.LibraryPreparation with
                | None -> return Error "The prepared content is missing or expired."
                | Some preparation when
                    preparation.OperationId <> operationId
                    || preparation.PrincipalId <> principalId context
                    || preparation.Content.ExpiresAt
                       <= SystemClock.Instance.GetCurrentInstant()
                    ->
                    return Error "The prepared content is missing or expired."
                | Some preparation ->
                    match session.FinalizedManifest with
                    | None -> return Error "The prepared content upload is not finalized."
                    | Some manifest ->
                        let content =
                            {
                                ContentVersionId =
                                    contentVersionId repositoryId preparation.Content.Blake3Hash preparation.Content.Sha256Hash preparation.Content.Size
                                Blake3Hash = preparation.Content.Blake3Hash
                                Sha256Hash = preparation.Content.Sha256Hash
                                Size = preparation.Content.Size
                                CreatedAt =
                                    preparation.Content.ExpiresAt
                                    - Duration.FromMinutes 15L
                            }

                        let! retainedLocation =
                            transferStore.UpsertContentLocationAsync(
                                {
                                    id = $"content:{content.ContentVersionId:D}"
                                    RepositoryId = repositoryId
                                    RecordKind = "content"
                                    RecordKey = $"content:{content.ContentVersionId:D}"
                                    SchemaVersion = 1
                                    Content = content
                                    AuthorizedScope = string session.AuthorizedScope
                                    Manifest = manifest
                                },
                                context.RequestAborted
                            )

                        return Ok(Some retainedLocation.Content, Some preparation.Content.ExpiresAt)
        }

    /// Reads the exact current actor-owned Library catalog.
    let GetCatalog: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context

                let! catalog =
                    (LibraryActor ids.RepositoryId)
                        .GetCatalog(Services.getCorrelationId context)

                return! ok context catalog
            }

    /// Lists the current normalized Libraries in deterministic path order.
    let ListLibraries: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context

                let! configuration =
                    (LibraryActor ids.RepositoryId)
                        .GetCatalog(Services.getCorrelationId context)

                return!
                    ok
                        context
                        { configuration with
                            Libraries =
                                configuration.Libraries
                                |> Array.sortWith (fun left right -> StringComparer.OrdinalIgnoreCase.Compare(left, right))
                        }
            }

    /// Applies one exact-version root change without importing, deleting, or rewriting either ownership system.
    let private changeLibrary addLibraryOperation : HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let correlationId = Services.getCorrelationId context
                let! repository = repositoryState context

                let! parameters =
                    if addLibraryOperation then
                        task {
                            let! value = context.BindJsonAsync<AddLibraryParameters>()
                            return value.ExpectedVersion, value.LibraryPath, value.OperationId
                        }
                    else
                        task {
                            let! value = context.BindJsonAsync<RemoveLibraryParameters>()
                            return value.ExpectedVersion, value.LibraryPath, value.OperationId
                        }

                let expectedVersion, libraryPath, operationId = parameters
                let now = SystemClock.Instance.GetCurrentInstant()
                let newVersion = LibraryCoordinator.deterministicGuid ids.RepositoryId operationId "library-catalog"
                let requestHash = catalogRequestHash ids.RepositoryId addLibraryOperation expectedVersion libraryPath operationId
                let libraryActor = LibraryActor ids.RepositoryId
                do! libraryActor.Repair correlationId
                let! currentConfiguration = libraryActor.GetCatalog correlationId

                let store = service<ILibraryStore> context

                let! replayResult =
                    task {
                        match! store.ReadCatalogOperationAsync(ids.RepositoryId, operationId, context.RequestAborted) with
                        | Some existing when existing.RequestHash = requestHash -> return Some existing.Result
                        | Some _ ->
                            return
                                Some
                                    {
                                        OperationId = operationId
                                        Outcome = OutcomeKind.Rejected
                                        LibraryCatalog = currentConfiguration
                                        ReasonCode = Some RejectionReason.OperationIdentityMismatch
                                        RecordedAt = now
                                    }
                        | None -> return None
                    }

                /// Evaluates and records a previously unseen catalog operation against the current repository state.
                let executeNewOperation () =
                    task {
                        let! currentItems = store.ReadCurrentItemsAsync(ids.RepositoryId, context.RequestAborted)

                        let! versionControlledRootEmpty =
                            if addLibraryOperation then
                                match normalizeRepositoryRelativePath libraryPath with
                                | Ok normalizedRoot -> versionControlledRootIsEmpty repository normalizedRoot correlationId
                                | Error _ -> task { return true }
                            else
                                task { return true }

                        let liveUnderRoot normalizedRoot =
                            currentItems
                            |> Array.exists (fun document ->
                                document.Item.State = "live"
                                && document.Item.Namespace
                                   |> Option.exists (fun namespaceValue ->
                                       pathsEqual namespaceValue.NormalizedPath normalizedRoot
                                       || namespaceValue.NormalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase)))

                        let outcome =
                            if addLibraryOperation then
                                match versionControlledRootEmpty with
                                | false -> Error CatalogRejectionReason.OutgoingSystemNotEmpty
                                | true ->
                                    Grace.Shared.Validation.Library.addLibrary
                                        expectedVersion
                                        newVersion
                                        libraryPath
                                        now
                                        (principalId context)
                                        currentConfiguration
                            else
                                match normalizeRepositoryRelativePath libraryPath with
                                | Error _ -> Error CatalogRejectionReason.UnsupportedPath
                                | Ok normalized when liveUnderRoot normalized -> Error CatalogRejectionReason.SlotOccupied
                                | Ok _ ->
                                    Grace.Shared.Validation.Library.removeLibrary
                                        expectedVersion
                                        newVersion
                                        libraryPath
                                        now
                                        (principalId context)
                                        currentConfiguration

                        let proposedResult =
                            match outcome with
                            | Error reason ->
                                {
                                    OperationId = operationId
                                    Outcome =
                                        if reason = OutcomeKind.StalePolicy then
                                            OutcomeKind.StalePolicy
                                        else
                                            OutcomeKind.Rejected
                                    LibraryCatalog = currentConfiguration
                                    ReasonCode = Some reason
                                    RecordedAt = now
                                }
                            | Ok configuration ->
                                {
                                    OperationId = operationId
                                    Outcome = OutcomeKind.Accepted
                                    LibraryCatalog = configuration
                                    ReasonCode = None
                                    RecordedAt = now
                                }

                        let! result = libraryActor.SetCatalog requestHash proposedResult correlationId
                        return! ok context result
                    }

                match replayResult with
                | Some replay -> return! ok context replay
                | None -> return! executeNewOperation ()
            }

    /// Adds one exact-version Library.
    let AddLibrary: HttpHandler = changeLibrary true

    /// Removes one exact-version Library.
    let RemoveLibrary: HttpHandler = changeLibrary false

    /// Wraps the byte-equivalent upload-session start facts for a Library content preparation.
    let internal preparedUploadSessionCommand (start: StartUploadSession) = UploadSessionCommand.Start start

    /// Starts or verifies the exact upload session described by one durable content preparation.
    let private startPreparedUploadSession context (start: StartUploadSession) =
        let correlationId = Services.getCorrelationId context
        let uploadActor = UploadSession.CreateActorProxy start.UploadSessionId start.RepositoryId correlationId
        let command = preparedUploadSessionCommand start

        uploadActor.Handle command (Services.createMetadata context)

    /// Starts an existing immutable-content upload session bound to this repository, principal, operation, and descriptor.
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
                    let preparedId = LibraryCoordinator.deterministicGuid ids.RepositoryId parameters.OperationId "prepared-content"
                    let uploadActor = UploadSession.CreateActorProxy preparedId ids.RepositoryId correlationId
                    let! exists = uploadActor.Exists correlationId

                    if exists then
                        let! session = uploadActor.Get correlationId

                        match session.LibraryPreparation with
                        | Some preparation when
                            preparation.OperationId = parameters.OperationId
                            && preparation.PrincipalId = principalId context
                            && preparation.Content.Blake3Hash = parameters.Blake3Hash
                            && preparation.Content.Sha256Hash = parameters.Sha256Hash
                            && preparation.Content.Size = parameters.Size
                            ->
                            let start: StartUploadSession =
                                {
                                    UploadSessionId = session.UploadSessionId
                                    OwnerId = session.OwnerId
                                    OrganizationId = session.OrganizationId
                                    RepositoryId = session.RepositoryId
                                    StoragePoolId = session.StoragePoolId
                                    AuthorizedScope = session.AuthorizedScope
                                    FileContentHash = session.FileContentHash
                                    ExpectedSize = session.ExpectedSize
                                    ChunkingSuiteId = session.ChunkingSuiteId
                                    SamplingPolicySnapshot = session.SamplingPolicySnapshot
                                    OperationId = $"Library-prepare:{preparation.OperationId:D}"
                                    LibraryPreparation = Some preparation
                                }

                            match! startPreparedUploadSession context start with
                            | Error actorError -> return! context |> Services.result400BadRequest actorError
                            | Ok _ -> return! ok context preparation.Content
                        | _ -> return! error StatusCodes.Status409Conflict context "The operation identity is already bound to another prepared descriptor."
                    else
                        let! repository = repositoryState context

                        let expiresAt =
                            SystemClock.Instance.GetCurrentInstant()
                            + Duration.FromMinutes 15L

                        let authorizedScope = $"Library/{preparedId:D}"

                        let uploadInstructions =
                            JsonSerializer.Serialize(
                                {|
                                    UploadSessionId = preparedId
                                    AuthorizedScope = authorizedScope
                                    StoragePoolId = repository.StoragePoolId
                                    StartPath = "/storage/startManifestUploadSession"
                                    UploadPath = "/storage/getContentBlockUploadUri"
                                    FinalizePath = "/storage/finalizeManifestUpload"
                                |},
                                Constants.JsonSerializerOptions
                            )

                        let prepared =
                            {
                                PreparedContentId = preparedId
                                Blake3Hash = parameters.Blake3Hash
                                Sha256Hash = parameters.Sha256Hash
                                Size = parameters.Size
                                UploadRequired = true
                                UploadInstructions = Some uploadInstructions
                                ExpiresAt = expiresAt
                            }

                        let samplingPolicySnapshot = JsonSerializer.Serialize(repository.ManifestEligibilityPolicy, Constants.JsonSerializerOptions)

                        let preparation = { OperationId = parameters.OperationId; PrincipalId = principalId context; Content = prepared }

                        let start: StartUploadSession =
                            {
                                UploadSessionId = preparedId
                                OwnerId = ids.OwnerId
                                OrganizationId = ids.OrganizationId
                                RepositoryId = ids.RepositoryId
                                StoragePoolId = repository.StoragePoolId
                                AuthorizedScope = authorizedScope
                                FileContentHash = prepared.Blake3Hash
                                ExpectedSize = prepared.Size
                                ChunkingSuiteId = RabinChunking.SuiteName
                                SamplingPolicySnapshot = samplingPolicySnapshot
                                OperationId = $"Library-prepare:{parameters.OperationId:D}"
                                LibraryPreparation = Some preparation
                            }

                        match! startPreparedUploadSession context start with
                        | Error actorError -> return! context |> Services.result400BadRequest actorError
                        | Ok _ -> return! ok context prepared
            }

    /// Submits one validated change through the bounded repository actor.
    let SubmitChange: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<SubmitLibraryChangeParameters>()

                match validateChangeShape parameters with
                | Error message -> return! error StatusCodes.Status400BadRequest context message
                | Ok () ->
                    let requestHash = changeRequestHash ids.RepositoryId parameters
                    let store = service<ILibraryStore> context
                    let! existingReceipt = store.ReadReceiptAsync(ids.RepositoryId, parameters.OperationId, context.RequestAborted)

                    let! preparedResult =
                        match existingReceipt with
                        | Some _ -> Task.FromResult(Ok(None, None))
                        | None -> resolvePreparedContent context ids.RepositoryId parameters.OperationId parameters.PreparedContentId

                    match preparedResult with
                    | Error message -> return! error StatusCodes.Status410Gone context message
                    | Ok (preparedContent, preparedExpiresAt) ->
                        let command =
                            {
                                RepositoryId = ids.RepositoryId
                                OperationId = parameters.OperationId
                                RequestHash = requestHash
                                LibraryCatalogVersion = parameters.LibraryCatalogVersion
                                ChangeKind = parameters.ChangeKind
                                ItemKind = parameters.ItemKind
                                ItemId = if parameters.ItemId.HasValue then Some parameters.ItemId.Value else None
                                NamespacePrecondition = parameters.NamespacePrecondition
                                ContentPrecondition = parameters.ContentPrecondition
                                CreationSlotExpectation = parameters.CreationSlotExpectation
                                DestinationParent = parameters.DestinationParent
                                DestinationName = Option.ofObj parameters.DestinationName
                                PreparedContentId =
                                    if parameters.PreparedContentId.HasValue then
                                        Some parameters.PreparedContentId.Value
                                    else
                                        None
                                PreparedContent = preparedContent
                                PreparedContentExpiresAt = preparedExpiresAt
                            }

                        let actor = LibraryActor ids.RepositoryId

                        let authorization =
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

                        let! submitResult = actor.Submit command (principalId context) authorization (Services.getCorrelationId context)

                        match submitResult.Receipt, submitResult.ForbiddenReason with
                        | Some receipt, None ->
                            do! tryNotifyLibraryContentAvailable context ids.RepositoryId receipt
                            return! ok context receipt
                        | _ -> return! error StatusCodes.Status403Forbidden context "Forbidden."
            }

    /// Reads one deterministic operation receipt after repository authorization.
    let GetOperation: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<GetLibraryOperationParameters>()
                let store = service<ILibraryStore> context

                do!
                    (LibraryActor ids.RepositoryId)
                        .Repair(Services.getCorrelationId context)

                match! store.ReadReceiptAsync(ids.RepositoryId, parameters.OperationId, context.RequestAborted) with
                | None -> return! Services.result404NotFound context
                | Some receipt when receipt.Receipt.PrincipalId <> principalId context -> return! Services.result404NotFound context
                | Some receipt -> return! ok context receipt.Receipt
            }

    /// Reads one current Library item after authorization and repair.
    let GetItem: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<GetLibraryItemParameters>()

                let! _ =
                    LibraryActor ids.RepositoryId
                    |> fun actor -> actor.GetStatus(Services.getCorrelationId context)

                let store = service<ILibraryStore> context

                match! store.ReadItemAsync(ids.RepositoryId, parameters.ItemId, context.RequestAborted) with
                | Some item -> return! ok context item.Item
                | None -> return! Services.result404NotFound context
            }

    /// Reads one current normalized namespace slot after authorization and repair.
    let GetNamespaceSlot: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<GetLibraryNamespaceSlotParameters>()
                let store = service<ILibraryStore> context

                match parameters.Parent, normalizeName parameters.Name with
                | None, _
                | _, Error _ -> return! error StatusCodes.Status400BadRequest context "A parent and portable name are required."
                | Some parent, Ok name ->
                    let! parentPath =
                        match parent.Kind, parent.LibraryPath, parent.ItemId with
                        | "root", Some libraryPath, None ->
                            task {
                                return
                                    normalizeRepositoryRelativePath libraryPath
                                    |> Result.toOption
                            }
                        | "item", None, Some parentId ->
                            task {
                                match! store.ReadItemAsync(ids.RepositoryId, parentId, context.RequestAborted) with
                                | Some item ->
                                    return
                                        item.Item.Namespace
                                        |> Option.map (fun value -> value.NormalizedPath)
                                | None -> return None
                            }
                        | _ -> task { return None }

                    match parentPath with
                    | None -> return! Services.result404NotFound context
                    | Some path ->
                        let normalizedPath =
                            normalizeRepositoryRelativePath $"{path}/{name}"
                            |> Result.toOption

                        match normalizedPath with
                        | None -> return! error StatusCodes.Status400BadRequest context "The namespace path is invalid."
                        | Some path ->
                            match! store.ReadSlotAsync(ids.RepositoryId, path, context.RequestAborted) with
                            | Some slot -> return! ok context slot.Slot
                            | None ->
                                return!
                                    ok
                                        context
                                        {
                                            Parent = parent
                                            Name = name
                                            NormalizedPath = path
                                            SlotVersion = LibraryCoordinator.initialSlotVersion ids.RepositoryId path
                                            State = "vacant"
                                            OccupantItemId = None
                                        }
            }

    /// Returns content-free status from the bounded repository actor.
    let GetStatus: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context

                let! status =
                    LibraryActor ids.RepositoryId
                    |> fun actor -> actor.GetStatus(Services.getCorrelationId context)

                return! ok context status
            }

    /// Publishes or reuses the immutable current-state baseline for one caught-up boundary.
    let private ensureCurrentBaseline context repositoryId =
        task {
            let store = service<ILibraryStore> context
            let mutable completed = false
            let mutable manifest = Unchecked.defaultof<LibraryBaselineManifestDocument>

            while not completed do
                let! control = store.ReadControlAsync(repositoryId, context.RequestAborted)

                match control.Document.CurrentBaselineId, control.Document.CurrentBaselineCursor with
                | Some baselineId, Some cursor when cursor = control.Document.AppliedThrough ->
                    match! store.ReadBaselineAsync(repositoryId, baselineId, context.RequestAborted) with
                    | Some (existing, _) when
                        existing.BoundaryCursor = control.Document.AppliedThrough
                        && existing.CursorEpoch = control.Document.CursorEpoch
                        && existing.LibraryCatalogVersion = control.Document.LibraryCatalog.Version
                        ->
                        manifest <- existing
                        completed <- true
                    | Some _ ->
                        let replacement =
                            { control.Document with
                                CurrentBaselineId = None
                                CurrentBaselineCursor = None
                                UpdatedAt = SystemClock.Instance.GetCurrentInstant()
                            }

                        match! store.ReplaceControlAsync(replacement, control.ETag, context.RequestAborted) with
                        | Replaced _ -> ()
                        | PreconditionFailed -> ()
                    | None -> invalidOp "The published Library baseline manifest is missing."
                | _ ->
                    let! current = store.ReadCurrentItemsAsync(repositoryId, context.RequestAborted)

                    let! published =
                        store.EnsureBaselineAsync(
                            repositoryId,
                            control.Document.AppliedThrough,
                            control.Document.CursorEpoch,
                            control.Document.LibraryCatalog,
                            current |> Array.map (fun item -> item.Item),
                            context.RequestAborted
                        )

                    let replacement =
                        { control.Document with
                            CurrentBaselineId = Some published.BaselineId
                            CurrentBaselineCursor = Some published.BoundaryCursor
                            ProjectionWatermarks =
                                { control.Document.ProjectionWatermarks with
                                    Baselines = max control.Document.ProjectionWatermarks.Baselines published.BoundaryCursor
                                }
                            UpdatedAt = SystemClock.Instance.GetCurrentInstant()
                        }

                    match! store.ReplaceControlAsync(replacement, control.ETag, context.RequestAborted) with
                    | Replaced _ ->
                        manifest <- published
                        completed <- true
                    | PreconditionFailed -> ()

            return manifest
        }

    /// Builds one public immutable baseline page from a protected page offset.
    let private baselinePage context repositoryId baselineId offset pageSize =
        task {
            let store = service<ILibraryStore> context
            let codec = service<ILibraryCursorCodec> context
            let tokenCodec = service<LibraryOpaqueTokenCodec> context

            match! store.ReadBaselineAsync(repositoryId, baselineId, context.RequestAborted) with
            | None -> return Error StatusCodes.Status404NotFound
            | Some (manifest, items) ->
                let pageItems =
                    items
                    |> Array.skip offset
                    |> Array.truncate pageSize

                let nextOffset = offset + pageItems.Length

                let nextToken =
                    if nextOffset < items.Length then
                        tokenCodec.Encode(
                            BootstrapTokenKind,
                            repositoryId,
                            baselineId.ToString("D"),
                            nextOffset,
                            SystemClock.Instance.GetCurrentInstant()
                            + Duration.FromMinutes 30L
                        )
                        |> Some
                    else
                        None

                return
                    Ok
                        {
                            BootstrapId = baselineId
                            BoundaryCursor = codec.Encode(repositoryId, manifest.CursorEpoch, manifest.BoundaryCursor)
                            CursorEpoch = publicEpoch codec repositoryId manifest.CursorEpoch
                            LibraryCatalog = manifest.LibraryCatalog
                            Items = pageItems
                            NextPageToken = nextToken
                        }
        }

    /// Starts a bounded immutable baseline page sequence.
    let StartBootstrap: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<StartLibraryBootstrapParameters>()

                if not (pageSizeIsValid parameters.PageSize) then
                    return! error StatusCodes.Status400BadRequest context "PageSize must be between 1 and 2000."
                else
                    let! _ =
                        LibraryActor ids.RepositoryId
                        |> fun actor -> actor.GetStatus(Services.getCorrelationId context)

                    let! manifest = ensureCurrentBaseline context ids.RepositoryId

                    match! baselinePage context ids.RepositoryId manifest.BaselineId 0 parameters.PageSize with
                    | Ok page -> return! ok context page
                    | Error status -> return! error status context "The Library baseline is unavailable."
            }

    /// Continues one exact immutable baseline page sequence.
    let ContinueBootstrap: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<ContinueLibraryBootstrapParameters>()
                let tokenCodec = service<LibraryOpaqueTokenCodec> context

                if not (pageSizeIsValid parameters.PageSize) then
                    return! error StatusCodes.Status400BadRequest context "PageSize must be between 1 and 2000."
                else
                    match tokenCodec.TryDecode(BootstrapTokenKind, parameters.PageToken, SystemClock.Instance.GetCurrentInstant()) with
                    | Some token when
                        token.RepositoryId = ids.RepositoryId
                        && token.Value = parameters.BootstrapId.ToString("D")
                        ->
                        match! baselinePage context ids.RepositoryId parameters.BootstrapId token.Offset parameters.PageSize with
                        | Ok page -> return! ok context page
                        | Error status -> return! error status context "The Library baseline is unavailable."
                    | _ -> return! error StatusCodes.Status410Gone context "The bootstrap page token is invalid or expired."
            }

    /// Reads ordered accepted changes or a typed rebaseline instruction.
    let GetChanges: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<GetLibraryChangesParameters>()
                let store = service<ILibraryStore> context
                let codec = service<ILibraryCursorCodec> context
                let tokenCodec = service<LibraryOpaqueTokenCodec> context

                let! _ =
                    LibraryActor ids.RepositoryId
                    |> fun actor -> actor.GetStatus(Services.getCorrelationId context)

                let! control = store.ReadControlAsync(ids.RepositoryId, context.RequestAborted)

                if not (pageSizeIsValid parameters.PageSize) then
                    return! error StatusCodes.Status400BadRequest context "PageSize must be between 1 and 2000."
                else
                    let decodedCursor =
                        if String.IsNullOrWhiteSpace parameters.PageToken then
                            codec.TryDecode(ids.RepositoryId, parameters.AfterCursor)
                        else
                            tokenCodec.TryDecode(DeltaTokenKind, parameters.PageToken, SystemClock.Instance.GetCurrentInstant())
                            |> Option.bind (fun token ->
                                match Int64.TryParse token.Value with
                                | true, cursor when token.RepositoryId = ids.RepositoryId -> Some(control.Document.CursorEpoch, cursor)
                                | _ -> None)

                    match decodedCursor with
                    | None ->
                        let rebaseline =
                            {
                                Reason = "cursorInvalid"
                                CurrentEpoch = publicEpoch codec ids.RepositoryId control.Document.CursorEpoch
                                ServiceFloorCursor = codec.Encode(ids.RepositoryId, control.Document.CursorEpoch, control.Document.ReplayFloor)
                                RecommendedBootstrap = true
                            }

                        return!
                            ok
                                context
                                {
                                    Outcome = OutcomeKind.RebaselineRequired
                                    CursorEpoch = rebaseline.CurrentEpoch
                                    Changes = Array.empty
                                    LastCursor = parameters.AfterCursor
                                    HasMore = false
                                    NextPageToken = None
                                    Rebaseline = Some rebaseline
                                }
                    | Some (epoch, cursor) when
                        epoch <> control.Document.CursorEpoch
                        || cursor < control.Document.ReplayFloor - 1L
                        ->
                        let reason =
                            if epoch <> control.Document.CursorEpoch then
                                "cursorEpochChanged"
                            else
                                "cursorBelowServiceFloor"

                        let rebaseline =
                            {
                                Reason = reason
                                CurrentEpoch = publicEpoch codec ids.RepositoryId control.Document.CursorEpoch
                                ServiceFloorCursor = codec.Encode(ids.RepositoryId, control.Document.CursorEpoch, control.Document.ReplayFloor)
                                RecommendedBootstrap = true
                            }

                        return!
                            ok
                                context
                                {
                                    Outcome = OutcomeKind.RebaselineRequired
                                    CursorEpoch = rebaseline.CurrentEpoch
                                    Changes = Array.empty
                                    LastCursor = parameters.AfterCursor
                                    HasMore = false
                                    NextPageToken = None
                                    Rebaseline = Some rebaseline
                                }
                    | Some (_, cursor) ->
                        let! documents = store.ReadChangesAsync(ids.RepositoryId, cursor, parameters.PageSize + 1, context.RequestAborted)
                        let page = documents |> Array.truncate parameters.PageSize
                        let hasMore = documents.Length > page.Length
                        let lastPosition = if page.Length = 0 then cursor else page[page.Length - 1].Cursor
                        let lastCursor = codec.Encode(ids.RepositoryId, control.Document.CursorEpoch, lastPosition)

                        let nextToken =
                            if hasMore then
                                tokenCodec.Encode(
                                    DeltaTokenKind,
                                    ids.RepositoryId,
                                    string lastPosition,
                                    0,
                                    SystemClock.Instance.GetCurrentInstant()
                                    + Duration.FromMinutes 30L
                                )
                                |> Some
                            else
                                None

                        return!
                            ok
                                context
                                {
                                    Outcome = OutcomeKind.Accepted
                                    CursorEpoch = publicEpoch codec ids.RepositoryId control.Document.CursorEpoch
                                    Changes =
                                        page
                                        |> Array.map (fun document -> document.Change)
                                    LastCursor = lastCursor
                                    HasMore = hasMore
                                    NextPageToken = nextToken
                                    Rebaseline = None
                                }
            }

    /// Creates one short-lived signed read URI after current item and retained content checks.
    let PrepareContentRead: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<PrepareLibraryContentReadParameters>()
                let store = service<ILibraryStore> context
                let transferStore = service<ILibraryTransferStore> context

                do!
                    (LibraryActor ids.RepositoryId)
                        .Repair(Services.getCorrelationId context)

                match! store.ReadItemAsync(ids.RepositoryId, parameters.ItemId, context.RequestAborted) with
                | None -> return! Services.result404NotFound context
                | Some item when
                    item.Item.State <> "live"
                    || item.Item.Content
                       |> Option.forall (fun content ->
                           content.ContentVersionId
                           <> parameters.ContentVersionId)
                    ->
                    return! Services.result404NotFound context
                | Some item ->
                    match! transferStore.ReadContentLocationAsync(ids.RepositoryId, parameters.ContentVersionId, context.RequestAborted) with
                    | None -> return! Services.result404NotFound context
                    | Some location ->
                        let expiresAt =
                            SystemClock.Instance.GetCurrentInstant()
                            + Duration.FromSeconds 60L

                        let tokenCodec = service<LibraryOpaqueTokenCodec> context

                        let tokenValue = $"{item.Item.ItemId:D}:{location.Content.ContentVersionId:D}"
                        let token = tokenCodec.Encode(ReadGrantTokenKind, ids.RepositoryId, tokenValue, 0, expiresAt)

                        return! ok context { GrantId = token; DownloadPath = $"/libraries/content/{token}"; Content = location.Content; ExpiresAt = expiresAt }
            }

    /// Redeems one short-lived signed read URI and streams exact verified immutable bytes.
    let DownloadContent (token: string) : HttpHandler =
        fun _ context ->
            task {
                let tokenCodec = service<LibraryOpaqueTokenCodec> context
                let now = SystemClock.Instance.GetCurrentInstant()

                match tokenCodec.TryDecode(ReadGrantTokenKind, token, now) with
                | None -> return! Services.result404NotFound context
                | Some payload ->
                    let identities = payload.Value.Split(':', 2, StringSplitOptions.None)

                    match identities with
                    | [| itemIdentity; contentIdentity |] ->
                        match Guid.TryParse itemIdentity, Guid.TryParse contentIdentity with
                        | (false, _), _
                        | _, (false, _) -> return! Services.result404NotFound context
                        | (true, itemId), (true, contentVersionId) ->
                            let store = service<ILibraryStore> context
                            let transferStore = service<ILibraryTransferStore> context

                            do!
                                (LibraryActor payload.RepositoryId)
                                    .Repair(Services.getCorrelationId context)

                            match! store.ReadItemAsync(payload.RepositoryId, itemId, context.RequestAborted) with
                            | None -> return! Services.result404NotFound context
                            | Some item when
                                item.Item.State <> "live"
                                || item.Item.Content
                                   |> Option.forall (fun content -> content.ContentVersionId <> contentVersionId)
                                ->
                                return! Services.result404NotFound context
                            | Some _ ->
                                match! transferStore.ReadContentLocationAsync(payload.RepositoryId, contentVersionId, context.RequestAborted) with
                                | None -> return! Services.result404NotFound context
                                | Some location ->
                                    let repositoryActor = Repository.CreateActorProxy Guid.Empty payload.RepositoryId (Services.getCorrelationId context)

                                    let! repository = repositoryActor.Get(Services.getCorrelationId context)

                                    let fileVersion =
                                        FileVersion.CreateWithHashes
                                            (RelativePath $"Library/{location.Content.ContentVersionId:D}")
                                            (Sha256Hash location.Content.Sha256Hash)
                                            (Blake3Hash location.Content.Blake3Hash)
                                            String.Empty
                                            true
                                            location.Content.Size

                                    fileVersion.ContentReference <- FileContentReference.FileManifest location.Manifest

                                    match!
                                        NormalFileMaterialization.materializeBytes
                                            repository
                                            location.AuthorizedScope
                                            fileVersion
                                            (Services.getCorrelationId context)
                                            context.RequestAborted
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
