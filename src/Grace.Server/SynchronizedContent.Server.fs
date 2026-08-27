namespace Grace.Server

open Giraffe
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Server.Security
open Grace.Shared
open Grace.Shared.Parameters.SynchronizedContent
open Grace.Shared.Utilities
open Grace.Shared.Validation.SynchronizedContent
open Grace.Types.Common
open Grace.Types.Repository
open Grace.Types.SynchronizedContent
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
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
type SynchronizedTokenPayload = { Kind: string; RepositoryId: RepositoryId; Value: string; Offset: int; ExpiresAtUnixMilliseconds: int64 }

/// Implements opaque, expiring page and byte-grant tokens for the remote synchronized-content contract.
type SynchronizedOpaqueTokenCodec(secret: byte array) =

    do
        if isNull secret || secret.Length < 32 then
            invalidArg (nameof secret) "The synchronized token secret must contain at least 32 bytes."

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
                    let value = JsonSerializer.Deserialize<SynchronizedTokenPayload>(payload, Constants.JsonSerializerOptions)

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

/// Implements the complete remote-only synchronized-content HTTP application boundary.
module SynchronizedContent =

    [<Literal>]
    let private BootstrapTokenKind = "bootstrap"

    [<Literal>]
    let private DeltaTokenKind = "delta"

    [<Literal>]
    let private ReadGrantTokenKind = "read-grant"

    /// Returns the current authenticated principal binding used by durable preparations and receipts.
    let private principalId (context: HttpContext) =
        PrincipalMapper.tryGetUserId context.User
        |> Option.defaultValue (Services.createMetadata context).Principal

    /// Returns a required request service from the active HTTP scope.
    let private service<'T when 'T: not struct> (context: HttpContext) = context.RequestServices.GetRequiredService<'T>()

    /// Returns the synchronized repository grain for one exact repository.
    let private synchronizedActor (repositoryId: RepositoryId) = ApplicationContext.grainFactory.GetGrain<ISynchronizedContentRepositoryActor>(repositoryId)

    /// Wraps a successful synchronized result in Grace's public response envelope.
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
    let private mutationRequestHash repositoryId (parameters: SubmitSynchronizedMutationParameters) =
        let normalized =
            {|
                RepositoryId = repositoryId
                OperationId = parameters.OperationId
                RootConfigurationVersion = parameters.RootConfigurationVersion
                MutationKind = parameters.MutationKind
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

    /// Returns a public epoch token without exposing the private epoch identifier.
    let private publicEpoch (codec: ISynchronizedCursorCodec) repositoryId epoch = codec.Encode(repositoryId, epoch, 0L)

    /// Returns true when one version-controlled directory snapshot owns a live path at or below an exact synchronized root.
    let internal directoryVersionOwnsRoot normalizedRoot (directoryVersion: DirectoryVersion) =
        let ownsPath (path: string) =
            let normalizedPath = path.Replace('\\', '/').Trim('/')

            pathsEqual normalizedPath normalizedRoot
            || normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase)

        ownsPath (string directoryVersion.RelativePath)
        || directoryVersion.Files
           |> Seq.exists (fun file -> ownsPath (string file.RelativePath))

    /// Checks every selectable Reference root DirectoryVersion for a live path owned by the proposed synchronized root.
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

    /// Loads and revalidates one finalized principal-bound prepared content record immediately before actor submission.
    let private resolvePreparedContent (context: HttpContext) repositoryId operationId (preparedContentId: Nullable<Guid>) =
        task {
            if not preparedContentId.HasValue then
                return Ok(None, None)
            else
                let transferStore = service<ISynchronizedContentTransferStore> context

                match! transferStore.ReadPreparedAsync(repositoryId, preparedContentId.Value, context.RequestAborted) with
                | None -> return Error "The prepared content is missing or expired."
                | Some prepared when
                    prepared.Document.OperationId <> operationId
                    || prepared.Document.PrincipalId
                       <> principalId context
                    || prepared.Document.Content.ExpiresAt
                       <= SystemClock.Instance.GetCurrentInstant()
                    ->
                    return Error "The prepared content is missing or expired."
                | Some prepared ->
                    match prepared.Document.FinalizedManifest with
                    | None -> return Error "The prepared content upload is not finalized."
                    | Some manifest ->
                        let content =
                            {
                                ContentVersionId =
                                    contentVersionId
                                        repositoryId
                                        prepared.Document.Content.Blake3Hash
                                        prepared.Document.Content.Sha256Hash
                                        prepared.Document.Content.Size
                                Blake3Hash = prepared.Document.Content.Blake3Hash
                                Sha256Hash = prepared.Document.Content.Sha256Hash
                                Size = prepared.Document.Content.Size
                                CreatedAt =
                                    prepared.Document.Content.ExpiresAt
                                    - Duration.FromMinutes 15L
                            }

                        do!
                            transferStore.UpsertContentLocationAsync(
                                {
                                    id = $"content:{content.ContentVersionId:D}"
                                    RepositoryId = repositoryId
                                    Scope = "content"
                                    SchemaVersion = 1
                                    Content = content
                                    AuthorizedScope = prepared.Document.AuthorizedScope
                                    Manifest = manifest
                                },
                                context.RequestAborted
                            )

                        return Ok(Some content, Some prepared.Document.Content.ExpiresAt)
        }

    /// Reads or lists the exact current Repository-owned synchronization root configuration.
    let GetRoots: HttpHandler =
        fun _ context ->
            task {
                let! repository = repositoryState context
                return! ok context repository.SynchronizedRootConfiguration
            }

    /// Applies one exact-version root change without importing, deleting, or rewriting either ownership system.
    let private changeRoot addRootOperation : HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let correlationId = Services.getCorrelationId context
                let! repository = repositoryState context

                let! parameters =
                    if addRootOperation then
                        task {
                            let! value = context.BindJsonAsync<AddSynchronizedRootParameters>()
                            return value.ExpectedVersion, value.RootPath, value.OperationId
                        }
                    else
                        task {
                            let! value = context.BindJsonAsync<RemoveSynchronizedRootParameters>()
                            return value.ExpectedVersion, value.RootPath, value.OperationId
                        }

                let expectedVersion, rootPath, operationId = parameters
                let now = SystemClock.Instance.GetCurrentInstant()
                let newVersion = SynchronizedContentCoordinator.deterministicGuid ids.RepositoryId operationId "root-configuration"
                let store = service<ISynchronizedContentStore> context
                let! currentItems = store.ReadCurrentItemsAsync(ids.RepositoryId, context.RequestAborted)

                let! versionControlledRootEmpty =
                    if addRootOperation then
                        match normalizeRepositoryRelativePath rootPath with
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
                    if addRootOperation then
                        match versionControlledRootEmpty with
                        | false -> Error RootRejectionReason.OutgoingSystemNotEmpty
                        | true ->
                            Grace.Shared.Validation.SynchronizedContent.addRoot
                                expectedVersion
                                newVersion
                                rootPath
                                now
                                (principalId context)
                                repository.SynchronizedRootConfiguration
                    else
                        match normalizeRepositoryRelativePath rootPath with
                        | Error _ -> Error RootRejectionReason.UnsupportedPath
                        | Ok normalized when liveUnderRoot normalized -> Error RootRejectionReason.SlotOccupied
                        | Ok _ ->
                            Grace.Shared.Validation.SynchronizedContent.removeRoot
                                expectedVersion
                                newVersion
                                rootPath
                                now
                                (principalId context)
                                repository.SynchronizedRootConfiguration

                match outcome with
                | Error reason ->
                    let result =
                        {
                            OperationId = operationId
                            Outcome =
                                if reason = OutcomeKind.StalePolicy then
                                    OutcomeKind.StalePolicy
                                else
                                    OutcomeKind.Rejected
                            RootConfiguration = repository.SynchronizedRootConfiguration
                            ReasonCode = Some reason
                            RecordedAt = now
                        }

                    return! ok context result
                | Ok configuration ->
                    let actor = Repository.CreateActorProxy ids.OrganizationId ids.RepositoryId correlationId

                    match! actor.Handle (RepositoryCommand.SetSynchronizedRootConfiguration(configuration, operationId)) (Services.createMetadata context) with
                    | Error actorError -> return! context |> Services.result400BadRequest actorError
                    | Ok _ ->
                        let! current = actor.Get correlationId

                        let result =
                            {
                                OperationId = operationId
                                Outcome = OutcomeKind.Accepted
                                RootConfiguration = current.SynchronizedRootConfiguration
                                ReasonCode = None
                                RecordedAt = now
                            }

                        return! ok context result
            }

    /// Adds one exact-version synchronized root.
    let AddRoot: HttpHandler = changeRoot true

    /// Removes one exact-version synchronized root.
    let RemoveRoot: HttpHandler = changeRoot false

    /// Starts an existing immutable-content upload session bound to this repository, principal, operation, and descriptor.
    let PrepareContent: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let correlationId = Services.getCorrelationId context
                let! parameters = context.BindJsonAsync<PrepareSynchronizedContentParameters>()

                if parameters.OperationId = Guid.Empty
                   || not (isLowercaseHash parameters.Blake3Hash)
                   || not (isLowercaseHash parameters.Sha256Hash)
                   || parameters.Size <= 0L then
                    return! error StatusCodes.Status400BadRequest context "OperationId, lowercase hashes, and a positive size are required."
                else
                    let transferStore = service<ISynchronizedContentTransferStore> context
                    let preparedId = SynchronizedContentCoordinator.deterministicGuid ids.RepositoryId parameters.OperationId "prepared-content"

                    match! transferStore.ReadPreparedAsync(ids.RepositoryId, preparedId, context.RequestAborted) with
                    | Some existing when
                        existing.Document.OperationId
                        <> parameters.OperationId
                        || existing.Document.PrincipalId
                           <> principalId context
                        || existing.Document.Content.Blake3Hash
                           <> parameters.Blake3Hash
                        || existing.Document.Content.Sha256Hash
                           <> parameters.Sha256Hash
                        || existing.Document.Content.Size <> parameters.Size
                        ->
                        return! error StatusCodes.Status409Conflict context "The operation identity is already bound to another prepared descriptor."
                    | Some existing -> return! ok context existing.Document.Content
                    | None ->
                        let! repository = repositoryState context

                        let expiresAt =
                            SystemClock.Instance.GetCurrentInstant()
                            + Duration.FromMinutes 15L

                        let authorizedScope = $"synchronized/{preparedId:D}"

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

                        do!
                            transferStore.CreatePreparedAsync(
                                {
                                    id = $"prepared:{preparedId:D}"
                                    RepositoryId = ids.RepositoryId
                                    Scope = "prepared"
                                    SchemaVersion = 1
                                    PreparedContentId = preparedId
                                    OperationId = parameters.OperationId
                                    PrincipalId = principalId context
                                    Content = prepared
                                    UploadSessionId = preparedId
                                    AuthorizedScope = authorizedScope
                                    StoragePoolId = repository.StoragePoolId
                                    FinalizedManifest = None
                                },
                                context.RequestAborted
                            )

                        let uploadActor = UploadSession.CreateActorProxy preparedId ids.RepositoryId correlationId

                        let command =
                            Grace.Types.UploadSession.UploadSessionCommand.Start
                                {
                                    UploadSessionId = preparedId
                                    OwnerId = ids.OwnerId
                                    OrganizationId = ids.OrganizationId
                                    RepositoryId = ids.RepositoryId
                                    StoragePoolId = repository.StoragePoolId
                                    AuthorizedScope = authorizedScope
                                    FileContentHash = parameters.Blake3Hash
                                    ExpectedSize = parameters.Size
                                    ChunkingSuiteId = RabinChunking.SuiteName
                                    SamplingPolicySnapshot = JsonSerializer.Serialize(repository.ManifestEligibilityPolicy, Constants.JsonSerializerOptions)
                                    OperationId = $"synchronized-prepare:{parameters.OperationId:D}"
                                }

                        match! uploadActor.Handle command (Services.createMetadata context) with
                        | Error actorError -> return! context |> Services.result400BadRequest actorError
                        | Ok _ -> return! ok context prepared
            }

    /// Submits one validated mutation through the bounded repository actor.
    let SubmitMutation: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<SubmitSynchronizedMutationParameters>()

                match validateMutationShape parameters with
                | Error message -> return! error StatusCodes.Status400BadRequest context message
                | Ok () ->
                    match! resolvePreparedContent context ids.RepositoryId parameters.OperationId parameters.PreparedContentId with
                    | Error message -> return! error StatusCodes.Status410Gone context message
                    | Ok (preparedContent, preparedExpiresAt) ->
                        let command =
                            {
                                RepositoryId = ids.RepositoryId
                                OperationId = parameters.OperationId
                                RequestHash = mutationRequestHash ids.RepositoryId parameters
                                RootConfigurationVersion = parameters.RootConfigurationVersion
                                MutationKind = parameters.MutationKind
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

                        let actor = synchronizedActor ids.RepositoryId

                        let! receipt = actor.Submit command (principalId context) (Services.getCorrelationId context)

                        return! ok context receipt
            }

    /// Reads one deterministic operation receipt after repository authorization.
    let GetOperation: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<GetSynchronizedOperationParameters>()
                let store = service<ISynchronizedContentStore> context

                match! store.ReadReceiptAsync(ids.RepositoryId, parameters.OperationId, context.RequestAborted) with
                | None -> return! Services.result404NotFound context
                | Some receipt when receipt.Receipt.PrincipalId <> principalId context -> return! Services.result404NotFound context
                | Some receipt -> return! ok context receipt.Receipt
            }

    /// Reads one current synchronized item after authorization and repair.
    let GetItem: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<GetSynchronizedItemParameters>()

                let! _ =
                    synchronizedActor ids.RepositoryId
                    |> fun actor -> actor.GetStatus(Services.getCorrelationId context)

                let store = service<ISynchronizedContentStore> context

                match! store.ReadItemAsync(ids.RepositoryId, parameters.ItemId, context.RequestAborted) with
                | Some item -> return! ok context item.Item
                | None -> return! Services.result404NotFound context
            }

    /// Reads one current normalized namespace slot after authorization and repair.
    let GetNamespaceSlot: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<GetSynchronizedNamespaceSlotParameters>()
                let store = service<ISynchronizedContentStore> context

                match parameters.Parent, normalizeName parameters.Name with
                | None, _
                | _, Error _ -> return! error StatusCodes.Status400BadRequest context "A parent and portable name are required."
                | Some parent, Ok name ->
                    let! parentPath =
                        match parent.Kind, parent.RootPath, parent.ItemId with
                        | "root", Some rootPath, None ->
                            task {
                                return
                                    normalizeRepositoryRelativePath rootPath
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
                                            SlotVersion = SynchronizedContentCoordinator.initialSlotVersion ids.RepositoryId path
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
                    synchronizedActor ids.RepositoryId
                    |> fun actor -> actor.GetStatus(Services.getCorrelationId context)

                return! ok context status
            }

    /// Publishes or reuses the immutable current-state baseline for one caught-up boundary.
    let private ensureCurrentBaseline context repositoryId =
        task {
            let store = service<ISynchronizedContentStore> context
            let mutable completed = false
            let mutable manifest = Unchecked.defaultof<SynchronizedBaselineManifestDocument>

            while not completed do
                let! control = store.ReadControlAsync(repositoryId, context.RequestAborted)

                match control.Document.CurrentBaselineId, control.Document.CurrentBaselineCursor with
                | Some baselineId, Some cursor when cursor = control.Document.AppliedThrough ->
                    match! store.ReadBaselineAsync(repositoryId, baselineId, context.RequestAborted) with
                    | Some (existing, _) ->
                        manifest <- existing
                        completed <- true
                    | None -> invalidOp "The published synchronized baseline manifest is missing."
                | _ ->
                    let! current = store.ReadCurrentItemsAsync(repositoryId, context.RequestAborted)

                    let! published =
                        store.EnsureBaselineAsync(
                            repositoryId,
                            control.Document.AppliedThrough,
                            control.Document.CursorEpoch,
                            control.Document.RootConfiguration,
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
            let store = service<ISynchronizedContentStore> context
            let codec = service<ISynchronizedCursorCodec> context
            let tokenCodec = service<SynchronizedOpaqueTokenCodec> context

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

                let! control = store.ReadControlAsync(repositoryId, context.RequestAborted)

                return
                    Ok
                        {
                            BootstrapId = baselineId
                            BoundaryCursor = codec.Encode(repositoryId, manifest.CursorEpoch, manifest.BoundaryCursor)
                            CursorEpoch = publicEpoch codec repositoryId manifest.CursorEpoch
                            RootConfiguration = control.Document.RootConfiguration
                            Items = pageItems
                            NextPageToken = nextToken
                        }
        }

    /// Starts a bounded immutable baseline page sequence.
    let StartBootstrap: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<StartSynchronizedBootstrapParameters>()

                if not (pageSizeIsValid parameters.PageSize) then
                    return! error StatusCodes.Status400BadRequest context "PageSize must be between 1 and 2000."
                else
                    let! _ =
                        synchronizedActor ids.RepositoryId
                        |> fun actor -> actor.GetStatus(Services.getCorrelationId context)

                    let! manifest = ensureCurrentBaseline context ids.RepositoryId

                    match! baselinePage context ids.RepositoryId manifest.BaselineId 0 parameters.PageSize with
                    | Ok page -> return! ok context page
                    | Error status -> return! error status context "The synchronized baseline is unavailable."
            }

    /// Continues one exact immutable baseline page sequence.
    let ContinueBootstrap: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<ContinueSynchronizedBootstrapParameters>()
                let tokenCodec = service<SynchronizedOpaqueTokenCodec> context

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
                        | Error status -> return! error status context "The synchronized baseline is unavailable."
                    | _ -> return! error StatusCodes.Status410Gone context "The bootstrap page token is invalid or expired."
            }

    /// Reads ordered accepted mutations or a typed rebaseline instruction.
    let GetDeltas: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<GetSynchronizedDeltasParameters>()
                let store = service<ISynchronizedContentStore> context
                let codec = service<ISynchronizedCursorCodec> context
                let tokenCodec = service<SynchronizedOpaqueTokenCodec> context

                let! _ =
                    synchronizedActor ids.RepositoryId
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
                                    Mutations = Array.empty
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
                                    Mutations = Array.empty
                                    LastCursor = parameters.AfterCursor
                                    HasMore = false
                                    NextPageToken = None
                                    Rebaseline = Some rebaseline
                                }
                    | Some (_, cursor) ->
                        let! documents = store.ReadDeltasAsync(ids.RepositoryId, cursor, parameters.PageSize + 1, context.RequestAborted)
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
                                    Mutations =
                                        page
                                        |> Array.map (fun document -> document.Mutation)
                                    LastCursor = lastCursor
                                    HasMore = hasMore
                                    NextPageToken = nextToken
                                    Rebaseline = None
                                }
            }

    /// Creates one principal-bound, one-use grant after current item and retained content checks.
    let PrepareContentRead: HttpHandler =
        fun _ context ->
            task {
                let ids = Services.getGraceIds context
                let! parameters = context.BindJsonAsync<PrepareSynchronizedContentReadParameters>()
                let store = service<ISynchronizedContentStore> context
                let transferStore = service<ISynchronizedContentTransferStore> context

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
                        let grantId = Guid.NewGuid()

                        let expiresAt =
                            SystemClock.Instance.GetCurrentInstant()
                            + Duration.FromSeconds 60L

                        let tokenCodec = service<SynchronizedOpaqueTokenCodec> context

                        let token = tokenCodec.Encode(ReadGrantTokenKind, ids.RepositoryId, grantId.ToString("D"), 0, expiresAt)

                        do!
                            transferStore.CreateReadGrantAsync(
                                {
                                    id = $"grant:{grantId:D}"
                                    RepositoryId = ids.RepositoryId
                                    Scope = "grant"
                                    SchemaVersion = 1
                                    GrantId = grantId
                                    PrincipalId = principalId context
                                    ItemId = item.Item.ItemId
                                    Content = location.Content
                                    AuthorizedScope = location.AuthorizedScope
                                    Manifest = location.Manifest
                                    ExpiresAt = expiresAt
                                    ConsumedAt = None
                                },
                                context.RequestAborted
                            )

                        return! ok context { GrantId = token; DownloadPath = $"/sync/content/{token}"; Content = location.Content; ExpiresAt = expiresAt }
            }

    /// Redeems one opaque grant once and streams exact verified immutable bytes.
    let DownloadContent (token: string) : HttpHandler =
        fun _ context ->
            task {
                let tokenCodec = service<SynchronizedOpaqueTokenCodec> context
                let now = SystemClock.Instance.GetCurrentInstant()

                match tokenCodec.TryDecode(ReadGrantTokenKind, token, now) with
                | None -> return! Services.result404NotFound context
                | Some payload ->
                    match Guid.TryParse payload.Value with
                    | false, _ -> return! Services.result404NotFound context
                    | true, grantId ->
                        let transferStore = service<ISynchronizedContentTransferStore> context

                        match! transferStore.ReadReadGrantAsync(payload.RepositoryId, grantId, context.RequestAborted) with
                        | None -> return! Services.result404NotFound context
                        | Some grant when
                            grant.Document.ExpiresAt <= now
                            || grant.Document.ConsumedAt.IsSome
                            ->
                            return! Services.result404NotFound context
                        | Some grant ->
                            let consumed = { grant.Document with ConsumedAt = Some now }

                            match! transferStore.ConsumeReadGrantAsync(consumed, grant.ETag, context.RequestAborted) with
                            | PreconditionFailed -> return! Services.result404NotFound context
                            | Replaced _ ->
                                let repositoryActor = Repository.CreateActorProxy Guid.Empty payload.RepositoryId (Services.getCorrelationId context)

                                let! repository = repositoryActor.Get(Services.getCorrelationId context)

                                let fileVersion =
                                    FileVersion.CreateWithHashes
                                        (RelativePath $"synchronized/{grant.Document.Content.ContentVersionId:D}")
                                        (Sha256Hash grant.Document.Content.Sha256Hash)
                                        (Blake3Hash grant.Document.Content.Blake3Hash)
                                        String.Empty
                                        true
                                        grant.Document.Content.Size

                                fileVersion.ContentReference <- FileContentReference.FileManifest grant.Document.Manifest

                                match!
                                    NormalFileMaterialization.materializeBytes
                                        repository
                                        grant.Document.AuthorizedScope
                                        fileVersion
                                        (Services.getCorrelationId context)
                                        context.RequestAborted
                                    with
                                | Error _ -> return! Services.result404NotFound context
                                | Ok bytes ->
                                    context.Response.ContentLength <- int64 bytes.Length
                                    context.Response.Headers.ETag <- $"\"{grant.Document.Content.Blake3Hash}\""
                                    context.Response.Headers[ "X-Content-BLAKE3" ] <- grant.Document.Content.Blake3Hash
                                    context.Response.Headers[ "X-Content-SHA256" ] <- grant.Document.Content.Sha256Hash
                                    context.Response.ContentType <- "application/octet-stream"

                                    do!
                                        context
                                            .Response
                                            .Body
                                            .WriteAsync(bytes, context.RequestAborted)
                                            .AsTask()

                                    return Some context
            }
