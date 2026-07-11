namespace Grace.Server

open Giraffe
open Grace.Actors
open Grace.Actors.Constants
open Grace.Actors.Extensions.ActorProxy
open Grace.Actors.Interfaces
open Grace.Actors.Services
open Grace.Server.Security
open Grace.Server.Services
open Grace.Server.Validations
open Grace.Shared
open Grace.Shared.AnnotationLineCore
open Grace.Shared.Extensions
open Grace.Shared.Parameters.Branch
open Grace.Shared.Parameters.Visibility
open Grace.Types
open Grace.Types.Annotation
open Grace.Types.Authorization
open Grace.Types.Branch
open Grace.Types.Diff
open Grace.Types.Common
open Grace.Types.Repository
open Grace.Types.Visibility
open Grace.Shared.Utilities
open Grace.Shared.Validation.Common
open Grace.Shared.Validation.Errors
open Grace.Shared.Validation.Utilities
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Linq
open System.Text.Json
open System.Threading.Tasks

/// Contains Grace Server branch behavior and supporting helpers.
module Branch =

    let private branchHashLookupErrorItemKey = "BranchHashLookupError"
    let private callerSuppliedBranchIdItemKey = "CallerSuppliedBranchId"
    let private visibleReferenceInitialScanLimit = 32
    let private visibleReferenceMaxScanLimit = 1024

    /// Implements branch hash lookup description for the server request pipeline.
    let private branchHashLookupDescription (sha256Hash: Sha256Hash) (blake3Hash: Blake3Hash) =
        let sha256HashText = string sha256Hash
        let blake3HashText = string blake3Hash

        if
            not (String.IsNullOrWhiteSpace sha256HashText)
            && not (String.IsNullOrWhiteSpace blake3HashText)
        then
            $"SHA-256 prefix '{sha256HashText}' and BLAKE3 prefix '{blake3HashText}'"
        elif not (String.IsNullOrWhiteSpace blake3HashText) then
            $"BLAKE3 prefix '{blake3HashText}'"
        else
            $"SHA-256 prefix '{sha256HashText}'"

    /// Writes ambiguous branch hash lookup error onto the current response or server state.
    let private setAmbiguousBranchHashLookupError (context: HttpContext) sha256Hash blake3Hash correlationId =
        let graceError = GraceError.Create $"The supplied {branchHashLookupDescription sha256Hash blake3Hash} is ambiguous in repository scope." correlationId

        graceError.Properties.Add("Path", context.Request.Path.Value)
        context.Items[ branchHashLookupErrorItemKey ] <- graceError

    /// Gets try get branch hash lookup error data needed by the server flow.
    let private tryGetBranchHashLookupError (context: HttpContext) =
        let mutable item = null

        if context.Items.TryGetValue(branchHashLookupErrorItemKey, &item) then
            Some(item :?> GraceError)
        else
            None

    /// Represents validations used by Grace Server APIs and background services.
    type Validations<'T when 'T :> BranchParameters> = 'T -> ValueTask<Result<unit, BranchError>> array

    let activitySource = new ActivitySource("Branch")

    /// Implements branch logger for the server request pipeline.
    let private branchLogger () = ApplicationContext.loggerFactory.CreateLogger("Branch.Server")

    let log =
        { new ILogger with
            /// Delegates logging scope creation to the wrapped logger only when branch route logging is enabled.
            member _.BeginScope<'TState>(state: 'TState) = (branchLogger ()).BeginScope(state)
            /// Reports whether the wrapped logger will emit branch route diagnostics for the requested level.
            member _.IsEnabled(logLevel) = (branchLogger ()).IsEnabled(logLevel)

            /// Writes branch route diagnostics through the wrapped logger when the level is enabled.
            member _.Log(logLevel, eventId, state, ex, formatter) =
                (branchLogger ())
                    .Log(logLevel, eventId, state, ex, formatter)
        }

    /// Implements annotation error for the server request pipeline.
    let private annotationError correlationId message = GraceError.Create message correlationId

    /// Normalizes normalize annotation path data for stable server comparisons.
    let private normalizeAnnotationPath (path: string) = normalizeFilePath path

    /// Determines whether malformed annotation path.
    let private isMalformedAnnotationPath (path: string) =
        let normalized = normalizeAnnotationPath path

        String.IsNullOrWhiteSpace normalized
        || normalized.StartsWith("/", StringComparison.Ordinal)
        || System.IO.Path.IsPathRooted(path)
        || (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            |> Array.exists (fun segment -> segment = "." || segment = ".."))

    /// Converts server authentication data into annotation source reference.
    let private toAnnotationSourceReference (referenceDto: Reference.ReferenceDto) =
        { AnnotationSourceReference.Default with
            SourceReferenceId = $"{referenceDto.ReferenceId}"
            ReferenceId = referenceDto.ReferenceId
            ReferenceType = getDiscriminatedUnionCaseName referenceDto.ReferenceType
            ReferenceText = referenceDto.ReferenceText
            DirectoryVersionId = referenceDto.DirectoryId
            CreatedAt = Some referenceDto.CreatedAt
            CreatedBy = referenceDto.CreatedBy
        }

    /// Attempts to find file version and returns an option or result instead of throwing.
    let private tryFindFileVersion (path: RelativePath) (contents: DirectoryVersion.DirectoryVersionDto array) =
        let normalizedPath = normalizeAnnotationPath path

        contents
        |> Seq.collect (fun directoryVersionDto -> directoryVersionDto.DirectoryVersion.Files :> seq<FileVersion>)
        |> Seq.tryFind (fun fileVersion -> String.Equals(normalizeAnnotationPath fileVersion.RelativePath, normalizedPath, StringComparison.Ordinal))

    /// Reads reference metadata and directory contents needed to render branch history.
    let private getReferenceContents repositoryId correlationId (referenceDto: Reference.ReferenceDto) =
        task {
            let directoryActorProxy = DirectoryVersion.CreateActorProxy referenceDto.DirectoryId repositoryId correlationId
            return! directoryActorProxy.GetRecursiveDirectoryVersions false correlationId
        }

    /// Returns the retained directory root carried by a branch reference command.
    let private tryGetRetainingDirectoryId (command: BranchCommand) =
        match command with
        | BranchCommand.Assign (directoryId, _, _, _)
        | BranchCommand.Promote (directoryId, _, _, _)
        | BranchCommand.Commit (directoryId, _, _, _)
        | BranchCommand.Checkpoint (directoryId, _, _, _)
        | BranchCommand.Save (directoryId, _, _, _)
        | BranchCommand.Tag (directoryId, _, _, _)
        | BranchCommand.CreateExternal (directoryId, _, _, _) -> Some directoryId
        | _ -> None

    /// Validates all client-declared upload sessions against the exact retained file graph.
    let private validateRetainedUploadSessions (graceIds: GraceIds) correlationId (properties: Dictionary<string, string>) command =
        task {
            match properties.TryGetValue UploadSessionIdsProperty with
            | false, _ -> return Ok []
            | true, _ when
                tryGetRetainingDirectoryId command
                |> Option.isNone
                ->
                return Error(GraceError.Create "UploadSessionIds is only valid on a command that creates a retaining reference." correlationId)
            | true, canonicalIds ->
                let directoryId = tryGetRetainingDirectoryId command |> Option.get
                let directoryActor = DirectoryVersion.CreateActorProxy directoryId graceIds.RepositoryId correlationId
                let! contents = directoryActor.GetRecursiveDirectoryVersions false correlationId
                let sessions = ResizeArray<Grace.Types.UploadSession.UploadSessionDto>()
                let mutable validationError: GraceError option = None

                for uploadSessionIdText in canonicalIds.Split(',', StringSplitOptions.None) do
                    if validationError.IsNone then
                        let uploadSessionId = Guid.ParseExact(uploadSessionIdText, "N")

                        let uploadSessionActor =
                            Grace.Actors.Extensions.ActorProxy.UploadSession.CreateActorProxy uploadSessionId graceIds.RepositoryId correlationId

                        let! session = uploadSessionActor.Get correlationId

                        let finalizedLifecycle =
                            session.LifecycleState = Grace.Types.UploadSession.UploadSessionLifecycleState.Finalized
                            || session.LifecycleState = Grace.Types.UploadSession.UploadSessionLifecycleState.RetentionPending

                        if session.UploadSessionId <> uploadSessionId
                           || session.OwnerId <> graceIds.OwnerId
                           || session.OrganizationId <> graceIds.OrganizationId
                           || session.RepositoryId <> graceIds.RepositoryId then
                            validationError <- Some(GraceError.Create "UploadSessionIds contains a session outside the retaining command scope." correlationId)
                        elif not finalizedLifecycle then
                            validationError <-
                                Some(GraceError.Create $"Upload session {uploadSessionId:N} is not in an allowed finalized lifecycle." correlationId)
                        else
                            match tryFindFileVersion session.AuthorizedScope contents, session.FinalizedManifestAddress with
                            | Some fileVersion, Some manifestAddress when
                                fileVersion.Size = session.ExpectedSize
                                && fileVersion.ContentReference.ReferenceType = FileContentReferenceType.FileManifest
                                && fileVersion.ContentReference.Manifest
                                   |> Option.exists (fun manifest ->
                                       manifest.ManifestAddress = manifestAddress
                                       && manifest.Size = session.ExpectedSize)
                                ->
                                sessions.Add session
                            | _ ->
                                validationError <-
                                    Some(
                                        GraceError.Create
                                            $"Upload session {uploadSessionId:N} does not match the retained path, manifest address, and logical size."
                                            correlationId
                                    )

                match validationError with
                | Some error -> return Error error
                | None -> return Ok(List.ofSeq sessions)
        }

    /// Settles validated reservations after the retaining branch event is durable.
    let private settleRetainedUploadSessions (graceIds: GraceIds) correlationId directoryId sessions metadata =
        task {
            let billingAccount = Grace.Actors.Extensions.ActorProxy.BillingAccount.CreateActorProxy graceIds.OwnerId correlationId

            let mutable settlementError: GraceError option = None

            for session: Grace.Types.UploadSession.UploadSessionDto in sessions do
                if settlementError.IsNone then
                    let reservationId = $"upload-session-{session.UploadSessionId:N}-reserve"
                    let settlementId = $"{reservationId}-settle-{graceIds.BranchId:N}-{directoryId:N}"

                    let! result =
                        billingAccount.Handle
                            (Grace.Types.BillingAccount.BillingAccountCommand.Settle(settlementId, reservationId, graceIds.RepositoryId, graceIds.BranchId))
                            metadata

                    match result with
                    | Ok _ -> ()
                    | Error error -> settlementError <- Some error

            return
                match settlementError with
                | Some error -> Error error
                | None -> Ok()
        }

    /// Checks authorization needed for can read reference branch server behavior.
    let private canReadReferenceBranch (context: HttpContext) (referenceDto: Reference.ReferenceDto) =
        task {
            let principals = PrincipalMapper.getPrincipals context.User
            let claims = PrincipalMapper.getEffectiveClaims context.User
            let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()

            let resource = Resource.Branch(referenceDto.OwnerId, referenceDto.OrganizationId, referenceDto.RepositoryId, referenceDto.BranchId)

            match! evaluator.CheckAsync(principals, claims, Operation.BranchRead, resource) with
            | Allowed _ -> return true
            | Denied _ -> return false
        }

    /// Checks whether RBAC grants the requested operation over an authoritative Grace resource.
    let private canPerformOperation (context: HttpContext) operation resource =
        task {
            let principals = PrincipalMapper.getPrincipals context.User
            let claims = PrincipalMapper.getEffectiveClaims context.User
            let evaluator = context.RequestServices.GetRequiredService<IGracePermissionEvaluator>()

            match! evaluator.CheckAsync(principals, claims, operation, resource) with
            | Allowed _ -> return true
            | Denied _ -> return false
        }

    /// Checks whether RBAC already grants the caller elevated authority over the branch.
    let private canAdministerBranch (context: HttpContext) (branchDto: BranchDto) =
        let resource = Resource.Branch(branchDto.OwnerId, branchDto.OrganizationId, branchDto.RepositoryId, branchDto.BranchId)
        canPerformOperation context Operation.BranchAdmin resource

    /// Converts the authenticated HTTP principal into the reusable visibility audience facts.
    let private getVisibilityCallerAudience (context: HttpContext) (branchDto: BranchDto) =
        task {
            let callerUserId =
                PrincipalMapper.tryGetUserId context.User
                |> Option.map UserId

            let resource = Resource.Repository(branchDto.OwnerId, branchDto.OrganizationId, branchDto.RepositoryId)
            let! hasRepositoryAdministration = canPerformOperation context Operation.RepositoryAdmin resource

            return { VisibilityCallerAudience.Anonymous with UserId = callerUserId; HasRepositoryAdministration = hasRepositoryAdministration }
        }

    /// Projects branch durable visibility facts into the shared visibility helper resource shape.
    let private branchVisibilityResource (branchDto: BranchDto) =
        let creatorUserId = if branchDto.UserId = UserId String.Empty then None else Some branchDto.UserId

        { AuthorityKey = branchDto.BranchId; Visibility = branchDto.Visibility; Ownership = branchDto.Ownership; CreatorUserId = creatorUserId }

    /// Builds branch-scoped authority only after checking the exact branch resource being considered.
    let private getBranchResourceAudience (context: HttpContext) (branchDto: BranchDto) =
        task {
            let! hasBranchAdministration = canAdministerBranch context branchDto

            return { VisibilityResourceAudience.None with HasBranchAdministration = hasBranchAdministration }
        }

    /// Checks whether the already-authenticated caller can observe the stored branch visibility boundary.
    let internal canObserveBranch (context: HttpContext) (branchDto: BranchDto) =
        task {
            let! caller = getVisibilityCallerAudience context branchDto
            let! branchAudience = getBranchResourceAudience context branchDto
            let resource = branchVisibilityResource branchDto
            return VisibilityAuthorization.canObserveBranch caller (fun _ -> branchAudience) resource
        }

    /// Projects reference durable visibility facts into the shared visibility helper resource shape.
    let private referenceVisibilityResource (referenceDto: Reference.ReferenceDto) =
        {
            AuthorityKey = referenceDto.ReferenceId
            Visibility = referenceDto.Visibility
            Ownership = referenceDto.Ownership
            CreatorUserId = referenceDto.CreatorUserId
        }

    /// Checks branch and reference visibility before a reference can reach public read, list, hash, or traversal surfaces.
    let private canObserveReferenceInBranch (context: HttpContext) (branchDto: BranchDto) (referenceDto: Reference.ReferenceDto) =
        task {
            let! caller = getVisibilityCallerAudience context branchDto
            let! branchAudience = getBranchResourceAudience context branchDto
            let branchResource = branchVisibilityResource branchDto

            let branchObservable = VisibilityAuthorization.canObserveBranch caller (fun _ -> branchAudience) branchResource

            if not branchObservable then
                return false
            else
                let resource = referenceVisibilityResource referenceDto
                return VisibilityAuthorization.canObserveBranchReference caller (fun _ -> branchAudience) resource
        }

    /// Checks private-reference audience without treating branch administration alone as terminal reveal authority.
    let private canObserveReferenceForReveal (context: HttpContext) (branchDto: BranchDto) (referenceDto: Reference.ReferenceDto) =
        task {
            let! caller = getVisibilityCallerAudience context branchDto
            let resource = referenceVisibilityResource referenceDto
            return VisibilityAuthorization.canObserveReference caller (fun _ -> VisibilityResourceAudience.None) resource
        }

    /// Checks whether PromotionSet review metadata is safe to disclose because the selected reference is terminal.
    let internal isRevealablePromotionSetMetadataLink (referenceDto: Reference.ReferenceDto) promotionSetId =
        referenceDto.Links
        |> Seq.exists (function
            | ReferenceLinkType.PromotionSetTerminal terminalPromotionSetId when terminalPromotionSetId = promotionSetId -> true
            | _ -> false)

    /// Selects terminal PromotionSet authority from a reference reveal when it is the first public publication point.
    let internal tryGetTerminalPromotionSetForReveal (referenceDto: Reference.ReferenceDto) =
        referenceDto.Links
        |> Seq.tryPick (function
            | ReferenceLinkType.PromotionSetTerminal promotionSetId -> Some promotionSetId
            | _ -> None)

    /// Orders non-deleted references newest-first so public windows can refill past deleted rows before applying MaxCount.
    let internal activeReferencesLatestFirst (references: Reference.ReferenceDto seq) =
        references
            .Where(fun referenceDto -> referenceDto.DeletedAt.IsNone)
            .OrderByDescending(fun referenceDto -> referenceDto.CreatedAt)
            .ToArray()

    /// Applies hidden-as-missing behavior to a reference that must be observable from the already-selected branch.
    let private getObservableReferenceInBranchOrDefault (context: HttpContext) (branchDto: BranchDto) (referenceDto: Reference.ReferenceDto) =
        task {
            if referenceDto.ReferenceId = ReferenceId.Empty then
                return Reference.ReferenceDto.Default
            else
                let! isObservable = canObserveReferenceInBranch context branchDto referenceDto
                return if isObservable then referenceDto else Reference.ReferenceDto.Default
        }

    /// Filters references for one already-authorized branch without reloading branch authority for every returned row.
    let internal filterVisibleReferenceWindowForBranch (context: HttpContext) (branchDto: BranchDto) maxCount (references: Reference.ReferenceDto seq) =
        task {
            if maxCount = 0 then
                return Array.empty<Reference.ReferenceDto>
            else
                let visibleReferences = List<Reference.ReferenceDto>()
                let latestFirst = activeReferencesLatestFirst references

                let mutable index = 0

                let mutable keepScanning =
                    index < latestFirst.Length
                    && (maxCount < 0 || visibleReferences.Count < maxCount)

                while keepScanning do
                    let referenceDto = latestFirst[index]
                    let! isObservable = canObserveReferenceInBranch context branchDto referenceDto

                    if isObservable then visibleReferences.Add referenceDto

                    index <- index + 1

                    keepScanning <-
                        index < latestFirst.Length
                        && (maxCount < 0 || visibleReferences.Count < maxCount)

                return
                    visibleReferences
                        .OrderBy(fun referenceDto -> referenceDto.CreatedAt)
                        .ToArray()
        }

    /// Validates that a reveal will not disclose hidden reference or non-terminal promotion-review links.
    let private validateRevealLinks context branchDto repositoryId selectedReferenceId (referenceDto: Reference.ReferenceDto) correlationId =
        task {
            let linkArray = referenceDto.Links |> Seq.toArray
            let mutable index = 0
            let mutable error: GraceError option = None

            while index < linkArray.Length && error.IsNone do
                match linkArray[index] with
                | ReferenceLinkType.BasedOn basedOnReferenceId when basedOnReferenceId <> selectedReferenceId ->
                    let basedOnActorProxy = Reference.CreateActorProxy basedOnReferenceId repositoryId correlationId
                    let! basedOnReference = basedOnActorProxy.Get correlationId

                    if basedOnReference.ReferenceId = ReferenceId.Empty then
                        error <-
                            Some(
                                (GraceError.Create "Reference reveal cannot follow a missing BasedOn reference." correlationId)
                                    .enhance(nameof ReferenceId, selectedReferenceId)
                                    .enhance ("BasedOnReferenceId", basedOnReferenceId)
                            )
                    elif basedOnReference.Visibility
                         <> ResourceVisibility.Public then
                        error <-
                            Some(
                                (GraceError.Create "Reference reveal cannot disclose a private BasedOn reference." correlationId)
                                    .enhance(nameof ReferenceId, selectedReferenceId)
                                    .enhance ("BasedOnReferenceId", basedOnReferenceId)
                            )
                | ReferenceLinkType.IncludedInPromotionSet promotionSetId when not (isRevealablePromotionSetMetadataLink referenceDto promotionSetId) ->
                    error <-
                        Some(
                            (GraceError.Create "Reference reveal cannot disclose promotion review metadata in this slice." correlationId)
                                .enhance(nameof ReferenceId, selectedReferenceId)
                                .enhance (nameof PromotionSetId, promotionSetId)
                        )
                | ReferenceLinkType.PromotionSetTerminal promotionSetId ->
                    let! canObserveReferenceAudience = canObserveReferenceForReveal context branchDto referenceDto

                    if not canObserveReferenceAudience then
                        error <-
                            Some(
                                (GraceError.Create
                                    "Reference reveal requires private terminal audience before publishing PromotionSet terminal metadata."
                                    correlationId)
                                    .enhance(nameof ReferenceId, selectedReferenceId)
                                    .enhance (nameof PromotionSetId, promotionSetId)
                            )
                | _ -> ()

                index <- index + 1

            match error with
            | Some error -> return Error error
            | None -> return Ok()
        }

    /// Revalidates root graph completeness immediately before the durable reveal event is persisted.
    let private validateRevealGraph repositoryId (referenceDto: Reference.ReferenceDto) correlationId =
        task {
            let directoryActorProxy = DirectoryVersion.CreateActorProxy referenceDto.DirectoryId repositoryId correlationId
            let! directoryVersionDtos = directoryActorProxy.GetRecursiveDirectoryVersions true correlationId

            return
                Reference.validateRecursiveDirectoryVersionsComplete
                    referenceDto.DirectoryId
                    (directoryVersionDtos
                     |> Seq.map (fun directoryVersionDto -> directoryVersionDto.DirectoryVersion))
                    correlationId
                |> Result.map (fun _ -> ())
        }

    /// Creates the hidden-as-missing response used when a private contributor branch exists but is not observable.
    let internal hiddenBranchError context branchId =
        let error = GraceError.Create (BranchError.getErrorMessage BranchError.BranchIdDoesNotExist) (getCorrelationId context)

        branchId
        |> Option.iter (fun callerSuppliedBranchId ->
            error.enhance (nameof BranchId, callerSuppliedBranchId)
            |> ignore)

        error.enhance ("Path", context.Request.Path.Value)

    /// Returns a caller-supplied branch id without exposing ids resolved from branch-name-only requests.
    let private callerSuppliedBranchId (context: HttpContext) (parameters: #BranchParameters) =
        let mutable callerSuppliedBranchIdItem = null

        let branchId =
            if context.Items.TryGetValue(callerSuppliedBranchIdItemKey, &callerSuppliedBranchIdItem) then
                callerSuppliedBranchIdItem :?> string
            else
                parameters.BranchId

        if String.IsNullOrWhiteSpace branchId then None else Some(Guid.Parse branchId)

    /// Loads the branch selected by middleware and enforces hidden-as-missing visibility before route data is returned.
    let private requireObservableBranch (context: HttpContext) (parameters: #BranchParameters) =
        task {
            let graceIds = getGraceIds context
            let actorProxy = Branch.CreateActorProxy graceIds.BranchId graceIds.RepositoryId (getCorrelationId context)
            let! branchDto = actorProxy.Get(getCorrelationId context)
            let! isObservable = canObserveBranch context branchDto

            if isObservable then
                return Ok(actorProxy, branchDto)
            else
                return Error(hiddenBranchError context (callerSuppliedBranchId context parameters))
        }


    /// Validates an optional visibility input without accepting deferred values.
    let private visibilityInputIsImplemented (visibility: string) =
        ValueTask<Result<unit, BranchError>>(
            if String.IsNullOrWhiteSpace visibility
               || (tryParseVisibilityInput visibility).IsSome then
                Ok()
            else
                Error BranchError.InvalidReferenceType
        )

    /// Validates an optional ownership input without accepting arbitrary contributor owner identifiers.
    let private ownershipInputIsImplemented (ownership: string) =
        ValueTask<Result<unit, BranchError>>(
            if String.IsNullOrWhiteSpace ownership
               || (tryParseOwnershipInput ownership).IsSome then
                Ok()
            else
                Error BranchError.InvalidReferenceType
        )

    /// Resolves the branch visibility default from repository policy and explicit route input.
    let private resolveBranchVisibility (repositoryType: RepositoryType) (ownership: ResourceOwnership) (visibilityInput: string) =
        match tryParseVisibilityInput visibilityInput with
        | Some visibility -> visibility
        | None ->
            match ownership, repositoryType with
            | ResourceOwnership.ContributorOwned, _ -> ResourceVisibility.Private
            | _, RepositoryType.Public -> ResourceVisibility.Public
            | _, RepositoryType.Private -> ResourceVisibility.Private

    /// Resolves the branch ownership default from explicit route input.
    let private resolveBranchOwnership ownershipInput =
        tryParseOwnershipInput ownershipInput
        |> Option.defaultValue ResourceOwnership.RepositoryOwned

    /// Gets try get based on reference id data needed by the server flow.
    let tryGetBasedOnReferenceId (referenceDto: Reference.ReferenceDto) =
        referenceDto.Links
        |> Seq.tryPick (function
            | ReferenceLinkType.BasedOn referenceId when referenceId <> ReferenceId.Empty -> Some referenceId
            | _ -> None)

    /// Gets try get stored based on reference id data needed by the server flow.
    let private tryGetStoredBasedOnReferenceId (syntheticBasedOnByReferenceId: Dictionary<ReferenceId, ReferenceId>) (referenceDto: Reference.ReferenceDto) =
        match tryGetBasedOnReferenceId referenceDto with
        | Some basedOnReferenceId ->
            match syntheticBasedOnByReferenceId.TryGetValue referenceDto.ReferenceId with
            | true, syntheticBasedOnReferenceId when syntheticBasedOnReferenceId = basedOnReferenceId -> None
            | _ -> Some basedOnReferenceId
        | None -> None

    /// Implements materialize annotation document for the server request pipeline.
    let private materializeAnnotationDocument
        (context: HttpContext)
        (repositoryDto: Grace.Types.Repository.RepositoryDto)
        (path: RelativePath)
        (referenceDto: Reference.ReferenceDto)
        =
        task {
            let correlationId = getCorrelationId context
            let! contents = getReferenceContents repositoryDto.RepositoryId correlationId referenceDto

            match tryFindFileVersion path contents with
            | None -> return Ok None
            | Some fileVersion ->
                match! AnnotationMaterialization.materializeTargetText repositoryDto path fileVersion correlationId context.RequestAborted with
                | Ok materialized ->
                    let document =
                        { SourceReference = toAnnotationSourceReference referenceDto; Path = normalizeAnnotationPath path; Content = materialized.Bytes }

                    return Ok(Some document)
                | Error error -> return Error error
        }

    let internal unreadableAncestorBoundaryKind = "UnmaterializableAncestor"

    let internal annotationMaterializationBudgetBoundaryKind = "MaterializationBudgetExceeded"

    [<Literal>]
    let internal MaxRetainedAnnotationMaterializationBytes = 64L * 1024L * 1024L

    /// Attempts to reserve retained annotation bytes and returns an option or result instead of throwing.
    let internal tryReserveRetainedAnnotationBytes retainedBytes (document: AnnotationHistoryDocument) =
        let contentLength = if isNull document.Content then 0L else int64 document.Content.LongLength

        if contentLength > 0L
           && retainedBytes + contentLength > MaxRetainedAnnotationMaterializationBytes then
            Error annotationMaterializationBudgetBoundaryKind
        else
            Ok(retainedBytes + contentLength)

    /// Implements effective history document for the server request pipeline.
    let private effectiveHistoryDocument document basedOnReferenceId isAuthorized boundaryKind =
        { Document = document; BasedOnReferenceId = basedOnReferenceId; IsAuthorized = isAuthorized; BoundaryKind = boundaryKind }

    /// Implements empty annotation document for the server request pipeline.
    let private emptyAnnotationDocument path referenceDto =
        { SourceReference = toAnnotationSourceReference referenceDto; Path = normalizeAnnotationPath path; Content = Array.empty }

    /// Implements effective history from materialization result for the server request pipeline.
    let internal effectiveHistoryFromMaterializationResult path targetReferenceId referenceDto basedOnReferenceId materializationResult =
        match materializationResult with
        | Ok (Some document) -> Ok(effectiveHistoryDocument document basedOnReferenceId true None)
        | Ok None -> Ok(effectiveHistoryDocument (emptyAnnotationDocument path referenceDto) basedOnReferenceId true None)
        | Error materializationError when referenceDto.ReferenceId = targetReferenceId -> Error materializationError
        | Error _ -> Ok(effectiveHistoryDocument (emptyAnnotationDocument path referenceDto) basedOnReferenceId true (Some unreadableAncestorBoundaryKind))

    /// Materializes annotation history across references while preserving unreadable ancestor boundaries for the caller.
    let internal buildEffectiveHistory
        (context: HttpContext)
        (repositoryDto: Grace.Types.Repository.RepositoryDto)
        (path: RelativePath)
        (targetReferenceId: ReferenceId)
        (references: Reference.ReferenceDto array)
        =
        task {
            let basedOnByReferenceId =
                let result = Dictionary<ReferenceId, ReferenceId option>()
                let previousByBranchId = Dictionary<BranchId, ReferenceId>()

                references
                |> Array.filter (fun referenceDto -> referenceDto.DeletedAt.IsNone)
                |> Array.sortBy (fun referenceDto -> referenceDto.CreatedAt)
                |> Array.iter (fun referenceDto ->
                    let basedOnReferenceId =
                        match tryGetBasedOnReferenceId referenceDto with
                        | Some referenceId -> Some referenceId
                        | None ->
                            match previousByBranchId.TryGetValue referenceDto.BranchId with
                            | true, previousReferenceId -> Some previousReferenceId
                            | false, _ -> None

                    result[referenceDto.ReferenceId] <- basedOnReferenceId
                    previousByBranchId[referenceDto.BranchId] <- referenceDto.ReferenceId)

                result

            let referenceById =
                references
                |> Array.map (fun referenceDto -> referenceDto.ReferenceId, referenceDto)
                |> dict

            let effectiveHistoryByReferenceId = Dictionary<ReferenceId, EffectiveHistoryDocument>()
            let mutable retainedBytes = 0L
            let mutable currentReferenceId = Some targetReferenceId
            let mutable error: GraceError option = None
            let mutable boundaryReached = false
            let visited = HashSet<ReferenceId>()

            while currentReferenceId.IsSome
                  && error.IsNone
                  && not boundaryReached
                  && visited.Add currentReferenceId.Value do
                let referenceId = currentReferenceId.Value

                match referenceById.TryGetValue referenceId with
                | false, _ -> boundaryReached <- true
                | true, referenceDto ->
                    let basedOnReferenceId =
                        match basedOnByReferenceId.TryGetValue referenceDto.ReferenceId with
                        | true, referenceId -> referenceId
                        | false, _ -> None

                    let! isAuthorized = canReadReferenceBranch context referenceDto

                    if isAuthorized then
                        match! materializeAnnotationDocument context repositoryDto path referenceDto with
                        | materializationResult ->
                            match effectiveHistoryFromMaterializationResult path targetReferenceId referenceDto basedOnReferenceId materializationResult with
                            | Ok document ->
                                match tryReserveRetainedAnnotationBytes retainedBytes document.Document with
                                | Ok nextRetainedBytes ->
                                    retainedBytes <- nextRetainedBytes
                                    effectiveHistoryByReferenceId[referenceDto.ReferenceId] <- document
                                    currentReferenceId <- basedOnReferenceId

                                    if document.BoundaryKind.IsSome then boundaryReached <- true
                                | Error boundaryKind ->
                                    effectiveHistoryByReferenceId[referenceDto.ReferenceId] <- effectiveHistoryDocument
                                                                                                   (emptyAnnotationDocument path referenceDto)
                                                                                                   basedOnReferenceId
                                                                                                   true
                                                                                                   (Some boundaryKind)

                                    boundaryReached <- true
                            | Error materializationError -> error <- Some materializationError
                    else
                        effectiveHistoryByReferenceId[referenceDto.ReferenceId] <- effectiveHistoryDocument
                                                                                       (emptyAnnotationDocument path referenceDto)
                                                                                       basedOnReferenceId
                                                                                       false
                                                                                       None

                        boundaryReached <- true

            match error with
            | Some error -> return Error error
            | None ->
                return
                    Ok(
                        effectiveHistoryByReferenceId.Values
                        |> Seq.toArray
                    )
        }

    /// Adds based on link to the server request model.
    let private withBasedOnLink basedOnReferenceId (referenceDto: Reference.ReferenceDto) =
        match tryGetBasedOnReferenceId referenceDto with
        | Some _ -> referenceDto
        | None ->
            { referenceDto with
                Links =
                    referenceDto.Links
                    |> Seq.append [ ReferenceLinkType.BasedOn basedOnReferenceId ]
                    |> Array.ofSeq
            }

    /// Represents history window used by Grace Server APIs and background services.
    type internal HistoryWindow = { References: Reference.ReferenceDto array; SyntheticBasedOnByReferenceId: Dictionary<ReferenceId, ReferenceId> }

    /// Implements ordered history window with synthetic boundaries for the server request pipeline.
    let internal orderedHistoryWindowWithSyntheticBoundaries targetReferenceId maxReferences (references: Reference.ReferenceDto array) =
        let ordered =
            references
            |> Array.filter (fun referenceDto -> referenceDto.DeletedAt.IsNone)
            |> Array.sortBy (fun referenceDto -> referenceDto.CreatedAt)

        let syntheticBasedOnByReferenceId = Dictionary<ReferenceId, ReferenceId>()

        match ordered
              |> Array.tryFindIndex (fun referenceDto -> referenceDto.ReferenceId = targetReferenceId)
            with
        | None -> { References = ordered |> Array.truncate maxReferences; SyntheticBasedOnByReferenceId = syntheticBasedOnByReferenceId }
        | Some targetIndex ->
            let startIndex = max 0 (targetIndex - maxReferences + 1)
            let window = ordered[startIndex..targetIndex]

            if startIndex > 0 && window.Length > 0 then
                let basedOnReferenceId = ordered[startIndex - 1].ReferenceId

                window[0] <- window[0] |> withBasedOnLink basedOnReferenceId

                syntheticBasedOnByReferenceId[window[0].ReferenceId] <- basedOnReferenceId

            { References = window; SyntheticBasedOnByReferenceId = syntheticBasedOnByReferenceId }

    /// Implements ordered history window for the server request pipeline.
    let internal orderedHistoryWindow targetReferenceId maxReferences (references: Reference.ReferenceDto array) =
        (orderedHistoryWindowWithSyntheticBoundaries targetReferenceId maxReferences references)
            .References

    /// Implements include stored based on references for the server request pipeline.
    let private includeStoredBasedOnReferences
        (context: HttpContext)
        repositoryId
        correlationId
        maxReferences
        (syntheticBasedOnByReferenceId: Dictionary<ReferenceId, ReferenceId>)
        (references: Reference.ReferenceDto array)
        =
        task {
            let effectiveReferences = ResizeArray<Reference.ReferenceDto>(references)

            let knownReferenceIds =
                HashSet<ReferenceId>(
                    references
                    |> Array.map (fun referenceDto -> referenceDto.ReferenceId)
                )

            let requestedBasedOnReferenceIds = HashSet<ReferenceId>()
            let mutable index = 0

            while index < effectiveReferences.Count do
                let referenceDto = effectiveReferences[index]

                match tryGetStoredBasedOnReferenceId syntheticBasedOnByReferenceId referenceDto with
                | Some basedOnReferenceId when
                    not (knownReferenceIds.Contains basedOnReferenceId)
                    && requestedBasedOnReferenceIds.Add basedOnReferenceId
                    ->
                    let basedOnReferenceActorProxy = Reference.CreateActorProxy basedOnReferenceId repositoryId correlationId
                    let! basedOnReference = basedOnReferenceActorProxy.Get correlationId

                    if basedOnReference.ReferenceId <> ReferenceId.Empty
                       && basedOnReference.DeletedAt.IsNone
                       && basedOnReference.RepositoryId = repositoryId then
                        let basedOnBranchActorProxy = Branch.CreateActorProxy basedOnReference.BranchId basedOnReference.RepositoryId correlationId
                        let! basedOnBranch = basedOnBranchActorProxy.Get correlationId
                        let! observableBasedOnReference = getObservableReferenceInBranchOrDefault context basedOnBranch basedOnReference

                        if observableBasedOnReference.ReferenceId
                           <> ReferenceId.Empty then
                            let! branchReferences = getReferences basedOnReference.RepositoryId basedOnReference.BranchId Int32.MaxValue correlationId
                            let! visibleBranchReferences = filterVisibleReferenceWindowForBranch context basedOnBranch -1 branchReferences

                            let basedOnHistoryWindow =
                                if visibleBranchReferences
                                   |> Array.exists (fun branchReference -> branchReference.ReferenceId = basedOnReference.ReferenceId) then
                                    visibleBranchReferences
                                else
                                    Array.append visibleBranchReferences [| basedOnReference |]
                                |> orderedHistoryWindowWithSyntheticBoundaries basedOnReference.ReferenceId maxReferences

                            for syntheticBasedOn in basedOnHistoryWindow.SyntheticBasedOnByReferenceId do
                                syntheticBasedOnByReferenceId[syntheticBasedOn.Key] <- syntheticBasedOn.Value

                            let mutable basedOnHistoryIndex = 0

                            while basedOnHistoryIndex < basedOnHistoryWindow.References.Length do
                                let basedOnHistoryReference = basedOnHistoryWindow.References[basedOnHistoryIndex]

                                if not (knownReferenceIds.Contains basedOnHistoryReference.ReferenceId) then
                                    knownReferenceIds.Add basedOnHistoryReference.ReferenceId
                                    |> ignore

                                    effectiveReferences.Add basedOnHistoryReference

                                basedOnHistoryIndex <- basedOnHistoryIndex + 1
                | _ -> ()

                index <- index + 1

            return effectiveReferences.ToArray()
        }

    /// Validates an annotation request and prepares the repository, branch, reference, and file-path inputs for storage.
    let private buildAnnotationForRequest (context: HttpContext) (parameters: AnnotateParameters) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context

            match parameters.Validate() with
            | Error errors -> return Error(annotationError correlationId (String.Join(" ", errors)))
            | Ok () when String.IsNullOrWhiteSpace parameters.Path ->
                return Error(annotationError correlationId "Annotation Path must be a relative file path.")
            | Ok () when isMalformedAnnotationPath parameters.Path ->
                return Error(annotationError correlationId "Annotation Path must be a relative file path.")
            | Ok () when parameters.TargetReferenceId = ReferenceId.Empty ->
                return Error(annotationError correlationId (BranchError.getErrorMessage BranchError.InvalidReferenceId))
            | Ok () ->
                let normalizedPath = normalizeAnnotationPath parameters.Path

                let repositoryActorProxy =
                    Grace.Actors.Extensions.ActorProxy.Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId correlationId

                let! repositoryDto = repositoryActorProxy.Get correlationId

                let branchActorProxy = Branch.CreateActorProxy graceIds.BranchId graceIds.RepositoryId correlationId
                let! branchDto = branchActorProxy.Get correlationId
                let! isBranchObservable = canObserveBranch context branchDto

                if not isBranchObservable then
                    return Error(hiddenBranchError context (callerSuppliedBranchId context parameters))
                else
                    let referenceActorProxy = Reference.CreateActorProxy parameters.TargetReferenceId graceIds.RepositoryId correlationId
                    let! targetReference = referenceActorProxy.Get correlationId

                    if targetReference.ReferenceId <> ReferenceId.Empty
                       && (targetReference.RepositoryId
                           <> graceIds.RepositoryId
                           || targetReference.BranchId <> graceIds.BranchId) then
                        return Error(annotationError correlationId (BranchError.getErrorMessage BranchError.ReferenceIdDoesNotExist))
                    else
                        let! targetReference = getObservableReferenceInBranchOrDefault context branchDto targetReference

                        if targetReference.ReferenceId = ReferenceId.Empty then
                            return Error(annotationError correlationId (BranchError.getErrorMessage BranchError.ReferenceIdDoesNotExist))
                        elif targetReference.DeletedAt.IsSome then
                            return Error(annotationError correlationId "Annotation target Reference has been deleted.")
                        elif targetReference.RepositoryId
                             <> graceIds.RepositoryId
                             || targetReference.BranchId <> graceIds.BranchId then
                            return Error(annotationError correlationId (BranchError.getErrorMessage BranchError.ReferenceIdDoesNotExist))
                        else
                            let! targetContents = getReferenceContents repositoryDto.RepositoryId correlationId targetReference

                            match tryFindFileVersion normalizedPath targetContents with
                            | None ->
                                return Error(annotationError correlationId $"Annotation target path '{normalizedPath}' was not found in the target Reference.")
                            | Some _ ->
                                let! branchReferences = getReferences targetReference.RepositoryId targetReference.BranchId Int32.MaxValue correlationId
                                let! visibleBranchReferences = filterVisibleReferenceWindowForBranch context branchDto -1 branchReferences

                                let localHistoryWindow =
                                    if visibleBranchReferences
                                       |> Array.exists (fun referenceDto -> referenceDto.ReferenceId = targetReference.ReferenceId) then
                                        visibleBranchReferences
                                    else
                                        Array.append visibleBranchReferences [| targetReference |]
                                    |> orderedHistoryWindowWithSyntheticBoundaries targetReference.ReferenceId parameters.MaxReferences

                                let! references =
                                    includeStoredBasedOnReferences
                                        context
                                        targetReference.RepositoryId
                                        correlationId
                                        parameters.MaxReferences
                                        localHistoryWindow.SyntheticBasedOnByReferenceId
                                        localHistoryWindow.References

                                match! buildEffectiveHistory context repositoryDto normalizedPath targetReference.ReferenceId references with
                                | Error error -> return Error error
                                | Ok history ->
                                    let traversal =
                                        AnnotationLineCore.traverseEffectiveBranchHistory targetReference.ReferenceId parameters.MaxReferences history

                                    match
                                        AnnotationLineCore.buildAnnotationFromEffectiveHistoryTraversal
                                            (
                                                parameters.LineRange,
                                                targetReference.ReferenceId,
                                                normalizedPath,
                                                parameters.ReferenceTypes,
                                                parameters.MaxReferences,
                                                parameters.IncludeLineText,
                                                traversal
                                            )
                                        with
                                    | Error errors -> return Error(annotationError correlationId (String.Join(" ", errors)))
                                    | Ok annotation ->
                                        match Annotation.validate annotation with
                                        | Ok () -> return Ok annotation
                                        | Error errors -> return Error(annotationError correlationId (String.Join(" ", errors)))
        }

    /// Coordinates process command with post success processing for Grace Server.
    let processCommandWithPostSuccess<'T when 'T :> BranchParameters>
        (context: HttpContext)
        (validations: Validations<'T>)
        (command: 'T -> ValueTask<BranchCommand>)
        (postSuccess: unit -> Task<Result<unit, GraceError>>)
        =
        task {
            let startTime = getCurrentInstant ()
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = Dictionary<string, obj>()

            try
                let commandName = context.Items["Command"] :?> string
                use activity = activitySource.StartActivity("processCommand", ActivityKind.Server)
                let! parameters = context |> parse<'T>

                let canonicalProperties =
                    match canonicalizeClientProperties correlationId parameters.Properties with
                    | Ok properties -> properties
                    | Error error -> raise (ClientPropertiesValidationException error)

                parameters.Properties <- canonicalProperties
                parameterDictionary.AddRange(getParametersAsDictionary parameters)

                // We know these Id's from ValidateIds.Middleware, so let's set them so we never have to resolve them again.
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString
                parameters.BranchId <- graceIds.BranchIdString

                /// Coordinates handle command processing for Grace Server.
                let handleCommand cmd retainedUploadSessions =
                    task {
                        let actorProxy = Branch.CreateActorProxy graceIds.BranchId graceIds.RepositoryId correlationId

                        //logToConsole
                        //    $"In Branch.Server.processCommand: command: {commandName}; OwnerId: {graceIds.OwnerIdString}; OrganizationId: {graceIds.OrganizationIdString}; RepositoryId: {graceIds.RepositoryIdString}; BranchId: {graceIds.BranchIdString}."

                        let metadata = metadataWithClientProperties (createMetadata context) parameters.Properties

                        match! actorProxy.Handle cmd metadata with
                        | Ok graceReturnValue ->
                            graceReturnValue
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof BranchId, graceIds.BranchId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            let! settlement =
                                match tryGetRetainingDirectoryId cmd with
                                | Some directoryId -> settleRetainedUploadSessions graceIds correlationId directoryId retainedUploadSessions metadata
                                | None -> Task.FromResult(Ok())

                            match settlement with
                            | Error graceError ->
                                graceError
                                    .enhance(parameterDictionary)
                                    .enhance(nameof OwnerId, graceIds.OwnerId)
                                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                    .enhance(nameof BranchId, graceIds.BranchId)
                                    .enhance("Command", commandName)
                                    .enhance ("Path", context.Request.Path.Value)
                                |> ignore

                                return! context |> result500ServerError graceError
                            | Ok () ->
                                match! postSuccess () with
                                | Ok () -> return! context |> result200Ok graceReturnValue
                                | Error graceError ->
                                    graceError
                                        .enhance(parameterDictionary)
                                        .enhance(nameof OwnerId, graceIds.OwnerId)
                                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                        .enhance(nameof BranchId, graceIds.BranchId)
                                        .enhance("Command", commandName)
                                        .enhance ("Path", context.Request.Path.Value)
                                    |> ignore

                                    return! context |> result500ServerError graceError
                        | Error graceError ->
                            graceError
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof BranchId, graceIds.BranchId)
                                .enhance("Command", commandName)
                                .enhance ("Path", context.Request.Path.Value)
                            |> ignore

                            return! context |> result400BadRequest graceError
                    }

                let validationResults = validations parameters

                let! validationsPassed = validationResults |> allPass

                log.LogDebug(
                    "{CurrentInstant}: In Branch.Server.processCommand: validationsPassed: {validationsPassed}.",
                    getCurrentInstantExtended (),
                    validationsPassed
                )

                if validationsPassed then
                    let! cmd = command parameters

                    match tryGetBranchHashLookupError context with
                    | Some graceError -> return! context |> result400BadRequest graceError
                    | None ->
                        match! validateRetainedUploadSessions graceIds correlationId parameters.Properties cmd with
                        | Error graceError -> return! context |> result400BadRequest graceError
                        | Ok retainedUploadSessions ->
                            let! result = handleCommand cmd retainedUploadSessions
                            let duration = getDurationRightAligned_ms startTime

                            log.LogInformation(
                                "{CurrentInstant}: Node: {hostName}; Duration: {duration}; CorrelationId: {correlationId}; Finished {path}; Status code: {statusCode}; OwnerId: {ownerId}; OrganizationId: {organizationId}; RepositoryId: {repositoryId}; BranchId: {branchId}.",
                                getCurrentInstantExtended (),
                                getMachineName,
                                duration,
                                correlationId,
                                context.Request.Path,
                                context.Response.StatusCode,
                                graceIds.OwnerIdString,
                                graceIds.OrganizationIdString,
                                graceIds.RepositoryIdString,
                                graceIds.BranchIdString
                            )

                            return result
                else
                    let! error = validationResults |> getFirstError
                    let errorMessage = BranchError.getErrorMessage error

                    log.LogDebug("{CurrentInstant}: error: {error}", getCurrentInstantExtended (), errorMessage)

                    let graceError =
                        (GraceError.Create errorMessage correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof BranchId, graceIds.BranchId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with
            | ClientPropertiesValidationException graceError -> return! context |> result400BadRequest graceError
            | ex ->
                log.LogError(
                    ex,
                    "{CurrentInstant}: Exception in Branch.Server.processCommand. CorrelationId: {correlationId}.",
                    getCurrentInstantExtended (),
                    (getCorrelationId context)
                )

                let graceError =
                    (GraceError.CreateWithException ex String.Empty correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance(nameof BranchId, graceIds.BranchId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    /// Coordinates process command processing for Grace Server.
    let processCommand<'T when 'T :> BranchParameters> (context: HttpContext) (validations: Validations<'T>) (command: 'T -> ValueTask<BranchCommand>) =
        processCommandWithPostSuccess context validations command (fun () -> Task.FromResult(Ok()))

    /// Resolves resolve root directory version for reference command data from request or repository state.
    let private resolveRootDirectoryVersionForReferenceCommand repositoryId directoryVersionId sha256Hash blake3Hash correlationId =
        task {
            if directoryVersionId <> DirectoryVersionId.Empty then
                let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy directoryVersionId repositoryId correlationId
                let! directoryVersionDto = directoryVersionActorProxy.Get correlationId
                let directoryVersion = directoryVersionDto.DirectoryVersion
                let requestedSha256Hash = string sha256Hash
                let requestedBlake3Hash = string blake3Hash

                if directoryVersion.DirectoryVersionId = DirectoryVersionId.Empty then
                    return Services.NoMatches
                elif directoryVersion.RelativePath
                     <> Constants.RootDirectoryPath
                     && directoryVersion.RelativePath <> "/" then
                    return Services.NoMatches
                elif
                    not (String.IsNullOrEmpty requestedSha256Hash)
                    && not (
                        (string directoryVersion.Sha256Hash)
                            .StartsWith(requestedSha256Hash, StringComparison.OrdinalIgnoreCase)
                    )
                then
                    return Services.NoMatches
                elif
                    not (String.IsNullOrEmpty requestedBlake3Hash)
                    && not (
                        (string directoryVersion.Blake3Hash)
                            .StartsWith(requestedBlake3Hash, StringComparison.OrdinalIgnoreCase)
                    )
                then
                    return Services.NoMatches
                else
                    return Services.UniqueMatch directoryVersion
            elif
                not (String.IsNullOrEmpty(string blake3Hash))
                && not (String.IsNullOrEmpty(string sha256Hash))
            then
                return! Services.getRootDirectoryVersionResolutionByHashQuery repositoryId sha256Hash blake3Hash correlationId
            elif not (String.IsNullOrEmpty(string blake3Hash)) then
                return! Services.getRootDirectoryVersionResolutionByBlake3Hash repositoryId blake3Hash correlationId
            elif not (String.IsNullOrEmpty(string sha256Hash)) then
                return! Services.getRootDirectoryVersionResolutionBySha256Hash repositoryId sha256Hash correlationId
            else
                return Services.NoMatches
        }

    /// Resolves try resolve root directory version for hash query data from request or repository state.
    let private tryResolveRootDirectoryVersionForHashQuery repositoryId sha256Hash blake3Hash correlationId =
        task { return! getRootDirectoryVersionByHashQuery repositoryId sha256Hash blake3Hash correlationId }

    /// Resolves try resolve directory version for hash query data from request or repository state.
    let private tryResolveDirectoryVersionForHashQuery repositoryId sha256Hash blake3Hash correlationId =
        task { return! Services.getDirectoryVersionByHashQuery repositoryId sha256Hash blake3Hash correlationId }

    /// Implements reference command from root for the server request pipeline.
    let private referenceCommandFromRoot
        (context: HttpContext)
        (createCommand: DirectoryVersionId * Sha256Hash * Blake3Hash * ReferenceText -> BranchCommand)
        repositoryId
        directoryVersionId
        sha256Hash
        blake3Hash
        referenceText
        correlationId
        =
        task {
            match! resolveRootDirectoryVersionForReferenceCommand repositoryId directoryVersionId sha256Hash blake3Hash correlationId with
            | Services.UniqueMatch directoryVersion ->
                return createCommand (directoryVersion.DirectoryVersionId, directoryVersion.Sha256Hash, directoryVersion.Blake3Hash, referenceText)
            | Services.NoMatches -> return createCommand (directoryVersionId, sha256Hash, blake3Hash, referenceText)
            | Services.AmbiguousMatches _ ->
                setAmbiguousBranchHashLookupError context sha256Hash blake3Hash correlationId
                return createCommand (directoryVersionId, sha256Hash, blake3Hash, referenceText)
        }

    /// Validates validate reference root locator inputs before server processing continues.
    let validateReferenceRootLocator (parameters: CreateReferenceParameters) =
        Input.oneOfTheseValuesMustBeProvided
            [|
                parameters.DirectoryVersionId
                parameters.Sha256Hash
                parameters.Blake3Hash
            |]
            BranchError.EitherDirectoryVersionIdOrSha256HashRequired

    /// Coordinates process query processing for Grace Server.
    let processQuery<'T, 'U when 'T :> BranchParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        maxCount
        (query: QueryResult<IBranchActor, 'U>)
        =
        task {
            use activity = activitySource.StartActivity("processQuery", ActivityKind.Server)
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let parameterDictionary = getParametersAsDictionary parameters

            try
                let validationResults = validations parameters

                let! validationsPassed = validationResults |> allPass

                if validationsPassed then
                    // Get the actor proxy for the branch.
                    let branchGuid = Guid.Parse(graceIds.BranchIdString)
                    let repositoryId = Guid.Parse(graceIds.RepositoryIdString)
                    let actorProxy = Branch.CreateActorProxy branchGuid repositoryId correlationId

                    // Execute the query.
                    let! queryResult = query context maxCount actorProxy

                    match tryGetBranchHashLookupError context with
                    | Some graceError -> return! context |> result400BadRequest graceError
                    | None ->
                        // Wrap the query result in a GraceReturnValue.
                        let graceReturnValue =
                            (GraceReturnValue.Create queryResult correlationId)
                                .enhance(parameterDictionary)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof BranchId, graceIds.BranchId)
                                .enhance ("Path", context.Request.Path.Value)

                        return! context |> result200Ok graceReturnValue
                else
                    let! error = validationResults |> getFirstError

                    let graceError =
                        (GraceError.Create (BranchError.getErrorMessage error) correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof BranchId, graceIds.BranchId)
                            .enhance ("Path", context.Request.Path.Value)

                    return! context |> result400BadRequest graceError
            with
            | ex ->
                let graceError =
                    (GraceError.CreateWithException ex String.Empty correlationId)
                        .enhance(parameterDictionary)
                        .enhance(nameof OwnerId, graceIds.OwnerId)
                        .enhance(nameof OrganizationId, graceIds.OrganizationId)
                        .enhance(nameof RepositoryId, graceIds.RepositoryId)
                        .enhance(nameof BranchId, graceIds.BranchId)
                        .enhance ("Path", context.Request.Path.Value)

                return! context |> result500ServerError graceError
        }

    /// Runs the normal branch query pipeline only after the target branch is observable to the caller.
    let private processObservableQuery<'T, 'U when 'T :> BranchParameters>
        (context: HttpContext)
        (parameters: 'T)
        (validations: Validations<'T>)
        maxCount
        (query: QueryResult<IBranchActor, 'U>)
        =
        task {
            match! requireObservableBranch context parameters with
            | Ok _ -> return! processQuery context parameters validations maxCount query
            | Error error -> return! context |> result400BadRequest error
        }

    /// Loads the branch that owns a reference so inherited visibility is evaluated from the durable branch state.
    let private loadReferenceBranch (referenceDto: Reference.ReferenceDto) correlationId =
        task {
            let branchActorProxy = Branch.CreateActorProxy referenceDto.BranchId referenceDto.RepositoryId correlationId
            return! branchActorProxy.Get correlationId
        }

    /// Chooses the maximum bounded reference-history scan size for a public reference window.
    let internal visibleReferenceScanLimitCap maxCount =
        if maxCount < 0 then
            Int32.MaxValue
        else
            Math.Max(visibleReferenceMaxScanLimit, maxCount)

    /// Chooses the first bounded reference-history scan size for a public reference window.
    let internal initialVisibleReferenceScanLimit maxCount =
        if maxCount <= 0 then
            0
        else
            Math.Min(visibleReferenceScanLimitCap maxCount, Math.Max(visibleReferenceInitialScanLimit, maxCount))

    /// Chooses the next bounded reference-history scan size for a public reference window refill.
    let internal nextVisibleReferenceScanLimit maxCount currentLimit =
        let scanLimitCap = visibleReferenceScanLimitCap maxCount

        if currentLimit >= scanLimitCap then
            currentLimit
        else
            let doubled = if currentLimit > Int32.MaxValue / 2 then Int32.MaxValue else currentLimit * 2

            Math.Min(scanLimitCap, doubled)

    /// Reads and refills a bounded branch reference-history window before public max-count projection.
    let private getVisibleReferencesWithLoader
        (context: HttpContext)
        (branchDto: BranchDto)
        maxCount
        (loadReferences: int -> Task<Reference.ReferenceDto array>)
        =
        task {
            if maxCount <= 0 then
                return Array.empty<Reference.ReferenceDto>
            else
                let mutable queryLimit = initialVisibleReferenceScanLimit maxCount
                let mutable visibleReferences = Array.empty<Reference.ReferenceDto>
                let mutable lastFetchedCount = 0
                let mutable keepScanning = true

                while keepScanning do
                    let! references = loadReferences queryLimit
                    lastFetchedCount <- references.Length
                    let! filteredReferences = filterVisibleReferenceWindowForBranch context branchDto maxCount references
                    visibleReferences <- filteredReferences

                    let nextQueryLimit = nextVisibleReferenceScanLimit maxCount queryLimit

                    keepScanning <-
                        filteredReferences.Length < maxCount
                        && lastFetchedCount >= queryLimit
                        && nextQueryLimit > queryLimit

                    queryLimit <- nextQueryLimit

                return visibleReferences
        }

    /// Reads a branch reference window with hidden rows removed before max-count projection.
    let private getVisibleReferences (context: HttpContext) (branchDto: BranchDto) maxCount =
        task {
            return!
                getVisibleReferencesWithLoader context branchDto maxCount (fun queryLimit ->
                    getReferences branchDto.RepositoryId branchDto.BranchId queryLimit (getCorrelationId context))
        }

    /// Reads a typed branch reference window with hidden rows removed before max-count projection.
    let private getVisibleReferencesByType (context: HttpContext) (branchDto: BranchDto) referenceType maxCount =
        task {
            return!
                getVisibleReferencesWithLoader context branchDto maxCount (fun queryLimit ->
                    getReferencesByType referenceType branchDto.RepositoryId branchDto.BranchId queryLimit (getCorrelationId context))
        }

    /// Selects the latest reference that remains observable after inherited branch visibility is applied.
    let private getLatestVisibleReference (context: HttpContext) (branchDto: BranchDto) =
        task {
            let! references = getVisibleReferences context branchDto 1
            return references |> Array.tryLast
        }

    /// Builds latest reference buckets from visible rows only so hidden rows cannot occupy a requested type bucket.
    let private getLatestVisibleReferencesByTypes (context: HttpContext) (branchDto: BranchDto) (referenceTypes: ReferenceType array) =
        task {
            let buckets = Dictionary<ReferenceType, Reference.ReferenceDto>()

            let mutable index = 0

            while index < referenceTypes.Length do
                let referenceType = referenceTypes[index]
                let! references = getVisibleReferencesByType context branchDto referenceType 1

                references.FirstOrDefault(Reference.ReferenceDto.Default)
                |> fun referenceDto ->
                    if referenceDto.ReferenceId <> ReferenceId.Empty then
                        buckets[referenceType] <- referenceDto

                index <- index + 1

            return buckets :> IReadOnlyDictionary<ReferenceType, Reference.ReferenceDto>
        }

    /// Applies hidden-as-missing behavior to direct reference lookups after loading the reference's owning branch.
    let private getObservableReferenceOrDefault (context: HttpContext) (referenceDto: Reference.ReferenceDto) =
        task {
            if referenceDto.ReferenceId = ReferenceId.Empty then
                return Reference.ReferenceDto.Default
            else
                let! branchDto = loadReferenceBranch referenceDto (getCorrelationId context)
                let! isObservable = canObserveReferenceInBranch context branchDto referenceDto
                return if isObservable then referenceDto else Reference.ReferenceDto.Default
        }

    /// Checks whether an observable reference's root directory owns the requested directory version.
    let private referenceOwnsDirectoryVersion
        (repositoryId: RepositoryId)
        (correlationId: CorrelationId)
        (targetDirectoryVersionId: DirectoryVersionId)
        (referenceDto: Reference.ReferenceDto)
        =
        task {
            if referenceDto.DirectoryId = targetDirectoryVersionId then
                return true
            else
                let directoryActorProxy = DirectoryVersion.CreateActorProxy referenceDto.DirectoryId repositoryId correlationId
                let! directoryVersionDtos = directoryActorProxy.GetRecursiveDirectoryVersions false correlationId

                return directoryVersionDtos.Any(fun directoryVersionDto -> directoryVersionDto.DirectoryVersion.DirectoryVersionId = targetDirectoryVersionId)
        }

    /// Checks whether a directory version satisfies the caller's optional SHA-256 and BLAKE3 prefix evidence.
    let private directoryVersionMatchesHashQuery (sha256Hash: Sha256Hash) (blake3Hash: Blake3Hash) (directoryVersion: Grace.Types.Common.DirectoryVersion) =
        let requestedSha256Hash = string sha256Hash
        let requestedBlake3Hash = string blake3Hash

        let shaMatches =
            String.IsNullOrEmpty requestedSha256Hash
            || (string directoryVersion.Sha256Hash)
                .StartsWith(requestedSha256Hash, StringComparison.OrdinalIgnoreCase)

        let blake3Matches =
            String.IsNullOrEmpty requestedBlake3Hash
            || (string directoryVersion.Blake3Hash)
                .StartsWith(requestedBlake3Hash, StringComparison.OrdinalIgnoreCase)

        shaMatches && blake3Matches

    /// Checks whether a directory version is a root suitable for reference-root hash lookups.
    let private isReferenceRootDirectoryVersion (directoryVersion: Grace.Types.Common.DirectoryVersion) =
        directoryVersion.RelativePath = Constants.RootDirectoryPath
        || directoryVersion.RelativePath = "/"

    /// Resolves caller-supplied hash evidence only across directories owned by observable references in the requested branch.
    let private resolveObservableDirectoryVersionByHash
        (context: HttpContext)
        (branchDto: BranchDto)
        includeOnlyReferenceRoots
        (sha256Hash: Sha256Hash)
        (blake3Hash: Blake3Hash)
        =
        task {
            let correlationId = getCorrelationId context
            let! branchReferences = getReferences branchDto.RepositoryId branchDto.BranchId Int32.MaxValue correlationId
            let! visibleReferences = filterVisibleReferenceWindowForBranch context branchDto -1 branchReferences
            let matchesByDirectoryVersionId = Dictionary<DirectoryVersionId, Grace.Types.Common.DirectoryVersion>()
            let mutable referenceIndex = 0

            while referenceIndex < visibleReferences.Length do
                let referenceDto = visibleReferences[referenceIndex]
                let directoryActorProxy = DirectoryVersion.CreateActorProxy referenceDto.DirectoryId branchDto.RepositoryId correlationId
                let! directoryVersionDtos = directoryActorProxy.GetRecursiveDirectoryVersions false correlationId
                let mutable directoryIndex = 0

                while directoryIndex < directoryVersionDtos.Length do
                    let directoryVersion =
                        directoryVersionDtos[directoryIndex]
                            .DirectoryVersion

                    if (not includeOnlyReferenceRoots
                        || isReferenceRootDirectoryVersion directoryVersion)
                       && directoryVersionMatchesHashQuery sha256Hash blake3Hash directoryVersion then
                        matchesByDirectoryVersionId[directoryVersion.DirectoryVersionId] <- directoryVersion

                    directoryIndex <- directoryIndex + 1

                referenceIndex <- referenceIndex + 1

            let matches = matchesByDirectoryVersionId.Values.ToArray()

            match matches.Length with
            | 0 -> return Services.NoMatches
            | 1 -> return Services.UniqueMatch matches[0]
            | _ -> return Services.AmbiguousMatches matches
        }

    /// Finds an observable branch reference that owns a directory resolved by caller-supplied hash evidence.
    let private tryFindObservableDirectoryOwnerReference (context: HttpContext) (branchDto: BranchDto) (targetDirectoryVersionId: DirectoryVersionId) =
        task {
            let correlationId = getCorrelationId context

            let! branchReferences = getReferences branchDto.RepositoryId branchDto.BranchId Int32.MaxValue correlationId
            let! references = filterVisibleReferenceWindowForBranch context branchDto -1 branchReferences

            let mutable index = 0
            let mutable ownerReference = Reference.ReferenceDto.Default

            while index < references.Length
                  && ownerReference.ReferenceId = ReferenceId.Empty do
                let! ownsDirectory = referenceOwnsDirectoryVersion branchDto.RepositoryId correlationId targetDirectoryVersionId references[index]

                if ownsDirectory then ownerReference <- references[index]

                index <- index + 1

            if ownerReference.ReferenceId = ReferenceId.Empty then
                return None
            else
                return Some ownerReference
        }

    /// Resolves hash-addressed directory data only when the requested branch has an observable owning reference.
    let private tryResolveObservableDirectoryVersionByHash
        (context: HttpContext)
        (actorProxy: IBranchActor)
        (sha256Hash: Sha256Hash)
        (blake3Hash: Blake3Hash)
        includeOnlyReferenceRoots
        =
        task {
            let correlationId = getCorrelationId context
            let! branchDto = actorProxy.Get correlationId

            match! resolveObservableDirectoryVersionByHash context branchDto includeOnlyReferenceRoots sha256Hash blake3Hash with
            | Services.UniqueMatch directoryVersion ->
                let! ownerReference = tryFindObservableDirectoryOwnerReference context branchDto directoryVersion.DirectoryVersionId

                match ownerReference with
                | Some _ -> return Some directoryVersion
                | None -> return None
            | Services.NoMatches -> return None
            | Services.AmbiguousMatches _ ->
                setAmbiguousBranchHashLookupError context sha256Hash blake3Hash correlationId
                return None
        }

    /// Creates a new branch.
    let Create: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                /// Implements validations for the server request pipeline.
                let validations (parameters: CreateBranchParameters) =
                    [|
                        Guid.isValidAndNotEmptyGuid parameters.ParentBranchId BranchError.InvalidBranchId
                        String.isValidGraceName parameters.ParentBranchName BranchError.InvalidBranchName
                        Branch.branchExists
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            parameters.ParentBranchId
                            parameters.ParentBranchName
                            parameters.CorrelationId
                            BranchError.ParentBranchDoesNotExist
                        Branch.parentBranchAllowsPromotions
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            parameters.ParentBranchId
                            parameters.ParentBranchName
                            parameters.CorrelationId
                            BranchError.ParentBranchDoesNotAllowPromotions
                        Branch.branchNameDoesNotExist
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            parameters.BranchName
                            parameters.CorrelationId
                            BranchError.BranchNameAlreadyExists
                        visibilityInputIsImplemented parameters.Visibility
                        ownershipInputIsImplemented parameters.Ownership
                    |]

                /// Implements command for the server request pipeline.
                let command (parameters: CreateBranchParameters) =
                    task {
                        match! (resolveBranchId
                                    graceIds.OwnerId
                                    graceIds.OrganizationId
                                    graceIds.RepositoryId
                                    parameters.ParentBranchId
                                    parameters.ParentBranchName
                                    parameters.CorrelationId)
                            with
                        | Some parentBranchId ->
                            let repositoryId = Guid.Parse(parameters.RepositoryId)

                            let repositoryActorProxy =
                                Grace.Actors.Extensions.ActorProxy.Repository.CreateActorProxy
                                    graceIds.OrganizationId
                                    graceIds.RepositoryId
                                    parameters.CorrelationId

                            let! repositoryDto = repositoryActorProxy.Get parameters.CorrelationId
                            let ownership = resolveBranchOwnership parameters.Ownership
                            let visibility = resolveBranchVisibility repositoryDto.RepositoryType ownership parameters.Visibility

                            let creatorUserId =
                                PrincipalMapper.tryGetUserId context.User
                                |> Option.map UserId
                                |> Option.defaultValue (UserId String.Empty)

                            let parentBranchActorProxy = Branch.CreateActorProxy parentBranchId repositoryId parameters.CorrelationId

                            let! parentBranch = parentBranchActorProxy.Get parameters.CorrelationId

                            return
                                BranchCommand.Create(
                                    graceIds.BranchId,
                                    (BranchName parameters.BranchName),
                                    parentBranchId,
                                    parentBranch.BasedOn.ReferenceId,
                                    graceIds.OwnerId,
                                    graceIds.OrganizationId,
                                    graceIds.RepositoryId,
                                    parameters.InitialPermissions,
                                    visibility,
                                    ownership,
                                    creatorUserId
                                )
                        | None ->
                            return
                                BranchCommand.Create(
                                    graceIds.BranchId,
                                    (BranchName parameters.BranchName),
                                    Constants.DefaultParentBranchId,
                                    ReferenceId.Empty, // This is fucked.
                                    graceIds.OwnerId,
                                    graceIds.OrganizationId,
                                    graceIds.RepositoryId,
                                    parameters.InitialPermissions,
                                    ResourceVisibility.Private,
                                    ResourceOwnership.RepositoryOwned,
                                    UserId String.Empty
                                )
                    }
                    |> ValueTask<BranchCommand>

                /// Ensures creator admin before the handler returns success.
                let ensureCreatorAdmin () =
                    task {
                        let graceIds = getGraceIds context

                        match!
                            CreatorScopeAdminGrant.ensureCreatorAdminForCreatedScope
                                context
                                (Grace.Types.Authorization.Scope.Branch(graceIds.OwnerId, graceIds.OrganizationId, graceIds.RepositoryId, graceIds.BranchId))
                            with
                        | Ok _ -> return Ok()
                        | Error error -> return Error error
                    }

                context.Items.Add("Command", nameof Create)
                return! processCommandWithPostSuccess context validations command ensureCreatorAdmin
            }

    /// Rebases a branch on its parent branch.
    let Rebase: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                /// Implements validations for the server request pipeline.
                let validations (parameters: RebaseParameters) =
                    [|
                        Branch.referenceIdExists parameters.BasedOn graceIds.RepositoryId parameters.CorrelationId BranchError.ReferenceIdDoesNotExist
                        Branch.branchAllowsReferenceType
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            parameters.BranchId
                            parameters.BranchName
                            ReferenceType.Commit
                            parameters.CorrelationId
                            BranchError.CommitIsDisabled
                    |]

                /// Implements command for the server request pipeline.
                let command (parameters: RebaseParameters) =
                    BranchCommand.Rebase parameters.BasedOn
                    |> returnValueTask

                context.Items.Add("Command", nameof Rebase)
                return! processCommand context validations command
            }

    /// Assigns a specific directory version as the next promotion reference for a branch.
    let Assign: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context
                let repositoryId = Guid.Parse(graceIds.RepositoryIdString)

                /// Implements validations for the server request pipeline.
                let validations (parameters: AssignParameters) =
                    [|
                        String.isEmptyOrValidSha256HashPrefix parameters.Sha256Hash BranchError.InvalidSha256Hash
                        String.isEmptyOrValidBlake3HashPrefix parameters.Blake3Hash BranchError.InvalidBlake3Hash
                        Input.oneOfTheseValuesMustBeProvided
                            [|
                                parameters.DirectoryVersionId
                                parameters.Sha256Hash
                                parameters.Blake3Hash
                            |]
                            BranchError.EitherDirectoryVersionIdOrSha256HashRequired
                        Branch.branchAllowsAssign
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            parameters.BranchId
                            parameters.BranchName
                            parameters.CorrelationId
                            BranchError.AssignIsDisabled
                    |]

                /// Implements command for the server request pipeline.
                let command (parameters: AssignParameters) =
                    task {
                        match!
                            resolveRootDirectoryVersionForReferenceCommand
                                repositoryId
                                parameters.DirectoryVersionId
                                parameters.Sha256Hash
                                parameters.Blake3Hash
                                parameters.CorrelationId
                            with
                        | Services.UniqueMatch directoryVersion ->
                            return
                                Some(
                                    Assign(
                                        directoryVersion.DirectoryVersionId,
                                        directoryVersion.Sha256Hash,
                                        directoryVersion.Blake3Hash,
                                        ReferenceText parameters.Message
                                    )
                                )
                        | Services.NoMatches -> return None
                        | Services.AmbiguousMatches _ ->
                            setAmbiguousBranchHashLookupError context parameters.Sha256Hash parameters.Blake3Hash parameters.CorrelationId
                            return None
                    }

                let! parameters = context |> parse<AssignParameters>
                context.Items.Add("Command", nameof Assign)
                context.Items[ "AssignParameters" ] <- parameters

                context.Request.Body.Seek(0L, IO.SeekOrigin.Begin)
                |> ignore

                match! String.isEmptyOrValidSha256HashPrefix parameters.Sha256Hash BranchError.InvalidSha256Hash with
                | Error error ->
                    return!
                        context
                        |> result400BadRequest (GraceError.Create (BranchError.getErrorMessage error) (getCorrelationId context))
                | Ok () ->
                    match! String.isEmptyOrValidBlake3HashPrefix parameters.Blake3Hash BranchError.InvalidBlake3Hash with
                    | Error error ->
                        return!
                            context
                            |> result400BadRequest (GraceError.Create (BranchError.getErrorMessage error) (getCorrelationId context))
                    | Ok () ->
                        match! command parameters with
                        | Some command -> return! processCommand context validations (fun parameters -> ValueTask<BranchCommand>(command))
                        | None ->
                            match tryGetBranchHashLookupError context with
                            | Some graceError -> return! context |> result400BadRequest graceError
                            | None ->
                                return!
                                    context
                                    |> result400BadRequest (
                                        GraceError.Create (getErrorMessage BranchError.EitherDirectoryVersionIdOrSha256HashRequired) (getCorrelationId context)
                                    )
            }

    /// Creates a promotion reference in the parent of the specified branch, based on the most-recent commit.
    let Promote: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                /// Implements validations for the server request pipeline.
                let validations (parameters: CreateReferenceParameters) =
                    [|
                        String.isNotEmpty parameters.Message BranchError.MessageIsRequired
                        String.maxLength parameters.Message 2048 BranchError.StringIsTooLong
                        String.isEmptyOrValidSha256HashPrefix parameters.Sha256Hash BranchError.InvalidSha256Hash
                        String.isEmptyOrValidBlake3HashPrefix parameters.Blake3Hash BranchError.InvalidBlake3Hash
                        validateReferenceRootLocator parameters
                        Branch.branchAllowsReferenceType
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            parameters.BranchId
                            parameters.BranchName
                            ReferenceType.Promotion
                            parameters.CorrelationId
                            BranchError.PromotionIsDisabled
                    |]

                /// Implements command for the server request pipeline.
                let command (parameters: CreateReferenceParameters) =
                    referenceCommandFromRoot
                        context
                        Promote
                        graceIds.RepositoryId
                        parameters.DirectoryVersionId
                        parameters.Sha256Hash
                        parameters.Blake3Hash
                        (ReferenceText parameters.Message)
                        parameters.CorrelationId
                    |> ValueTask<BranchCommand>

                context.Items.Add("Command", nameof Promote)
                return! processCommand context validations command
            }

    /// Creates a commit reference pointing to the current root directory version in the branch.
    let Commit: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                /// Implements validations for the server request pipeline.
                let validations (parameters: CreateReferenceParameters) =
                    [|
                        String.isNotEmpty parameters.Message BranchError.MessageIsRequired
                        String.maxLength parameters.Message 2048 BranchError.StringIsTooLong
                        String.isEmptyOrValidSha256HashPrefix parameters.Sha256Hash BranchError.InvalidSha256Hash
                        String.isEmptyOrValidBlake3HashPrefix parameters.Blake3Hash BranchError.InvalidBlake3Hash
                        validateReferenceRootLocator parameters
                        Branch.branchAllowsReferenceType
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            parameters.BranchId
                            parameters.BranchName
                            ReferenceType.Commit
                            parameters.CorrelationId
                            BranchError.CommitIsDisabled
                    |]

                /// Implements command for the server request pipeline.
                let command (parameters: CreateReferenceParameters) =
                    referenceCommandFromRoot
                        context
                        BranchCommand.Commit
                        graceIds.RepositoryId
                        parameters.DirectoryVersionId
                        parameters.Sha256Hash
                        parameters.Blake3Hash
                        (ReferenceText parameters.Message)
                        parameters.CorrelationId
                    |> ValueTask<BranchCommand>

                context.Items.Add("Command", nameof Commit)
                return! processCommand context validations command
            }

    /// Creates a checkpoint reference pointing to the current root directory version in the branch.
    let Checkpoint: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                /// Implements validations for the server request pipeline.
                let validations (parameters: CreateReferenceParameters) =
                    [|
                        String.maxLength parameters.Message 2048 BranchError.StringIsTooLong
                        String.isEmptyOrValidSha256HashPrefix parameters.Sha256Hash BranchError.InvalidSha256Hash
                        String.isEmptyOrValidBlake3HashPrefix parameters.Blake3Hash BranchError.InvalidBlake3Hash
                        validateReferenceRootLocator parameters
                        Branch.branchAllowsReferenceType
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            parameters.BranchId
                            parameters.BranchName
                            ReferenceType.Checkpoint
                            parameters.CorrelationId
                            BranchError.CheckpointIsDisabled
                    |]

                /// Implements command for the server request pipeline.
                let command (parameters: CreateReferenceParameters) =
                    referenceCommandFromRoot
                        context
                        BranchCommand.Checkpoint
                        graceIds.RepositoryId
                        parameters.DirectoryVersionId
                        parameters.Sha256Hash
                        parameters.Blake3Hash
                        (ReferenceText parameters.Message)
                        parameters.CorrelationId
                    |> ValueTask<BranchCommand>

                context.Items.Add("Command", nameof Checkpoint)
                return! processCommand context validations command
            }

    /// Creates a save reference pointing to the current root directory version in the branch.
    /// Manifest-backed files are accepted only after repository-local manifest accounting is durably incremented;
    /// WholeFileContent save behavior continues to use the existing directory-version validation path.
    let Save: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                /// Implements validations for the server request pipeline.
                let validations (parameters: CreateReferenceParameters) =
                    [|
                        String.maxLength parameters.Message 4096 BranchError.StringIsTooLong
                        String.isEmptyOrValidSha256HashPrefix parameters.Sha256Hash BranchError.InvalidSha256Hash
                        String.isEmptyOrValidBlake3HashPrefix parameters.Blake3Hash BranchError.InvalidBlake3Hash
                        validateReferenceRootLocator parameters
                        Branch.branchAllowsReferenceType
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            parameters.BranchId
                            parameters.BranchName
                            ReferenceType.Save
                            parameters.CorrelationId
                            BranchError.SaveIsDisabled
                    |]

                /// Implements command for the server request pipeline.
                let command (parameters: CreateReferenceParameters) =
                    referenceCommandFromRoot
                        context
                        BranchCommand.Save
                        graceIds.RepositoryId
                        parameters.DirectoryVersionId
                        parameters.Sha256Hash
                        parameters.Blake3Hash
                        (ReferenceText parameters.Message)
                        parameters.CorrelationId
                    |> ValueTask<BranchCommand>

                context.Items.Add("Command", nameof Save)
                return! processCommand context validations command
            }

    /// Creates a tag reference pointing to the specified root directory version in the branch.
    let Tag: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                /// Implements validations for the server request pipeline.
                let validations (parameters: CreateReferenceParameters) =
                    [|
                        String.maxLength parameters.Message 2048 BranchError.StringIsTooLong
                        String.isEmptyOrValidSha256HashPrefix parameters.Sha256Hash BranchError.InvalidSha256Hash
                        String.isEmptyOrValidBlake3HashPrefix parameters.Blake3Hash BranchError.InvalidBlake3Hash
                        validateReferenceRootLocator parameters
                        Branch.branchAllowsReferenceType
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            parameters.BranchId
                            parameters.BranchName
                            ReferenceType.Tag
                            parameters.CorrelationId
                            BranchError.TagIsDisabled
                    |]

                /// Implements command for the server request pipeline.
                let command (parameters: CreateReferenceParameters) =
                    referenceCommandFromRoot
                        context
                        BranchCommand.Tag
                        graceIds.RepositoryId
                        parameters.DirectoryVersionId
                        parameters.Sha256Hash
                        parameters.Blake3Hash
                        (ReferenceText parameters.Message)
                        parameters.CorrelationId
                    |> ValueTask<BranchCommand>

                context.Items.Add("Command", nameof Tag)
                return! processCommand context validations command
            }

    /// Creates an external reference pointing to the specified root directory version in the branch.
    let CreateExternal: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                /// Implements validations for the server request pipeline.
                let validations (parameters: CreateReferenceParameters) =
                    [|
                        String.maxLength parameters.Message 2048 BranchError.StringIsTooLong
                        String.isEmptyOrValidSha256HashPrefix parameters.Sha256Hash BranchError.InvalidSha256Hash
                        String.isEmptyOrValidBlake3HashPrefix parameters.Blake3Hash BranchError.InvalidBlake3Hash
                        validateReferenceRootLocator parameters
                        Branch.branchAllowsReferenceType
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            parameters.BranchId
                            parameters.BranchName
                            ReferenceType.External
                            parameters.CorrelationId
                            BranchError.ExternalIsDisabled
                    |]

                /// Implements command for the server request pipeline.
                let command (parameters: CreateReferenceParameters) =
                    referenceCommandFromRoot
                        context
                        BranchCommand.CreateExternal
                        graceIds.RepositoryId
                        parameters.DirectoryVersionId
                        parameters.Sha256Hash
                        parameters.Blake3Hash
                        (ReferenceText parameters.Message)
                        parameters.CorrelationId
                    |> ValueTask<BranchCommand>

                context.Items.Add("Command", nameof CreateExternal)
                return! processCommand context validations command
            }


    /// Enables and disables `grace assign` commands in the provided branch.
    let EnableAssign: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                /// Implements validations for the server request pipeline.
                let validations (parameters: EnableFeatureParameters) = [||]

                /// Implements command for the server request pipeline.
                let command (parameters: EnableFeatureParameters) =
                    EnableAssign(parameters.Enabled)
                    |> returnValueTask

                context.Items.Add("Command", nameof EnableAssign)
                return! processCommand context validations command
            }

    /// Enables and disables promotion references in the provided branch.
    let EnablePromotion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                /// Implements validations for the server request pipeline.
                let validations (parameters: EnableFeatureParameters) = [||]

                /// Implements command for the server request pipeline.
                let command (parameters: EnableFeatureParameters) =
                    EnablePromotion(parameters.Enabled)
                    |> returnValueTask

                context.Items.Add("Command", nameof EnablePromotion)
                return! processCommand context validations command
            }

    /// Enables and disables commit references in the provided branch.
    let EnableCommit: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                /// Implements validations for the server request pipeline.
                let validations (parameters: EnableFeatureParameters) = [||]

                /// Implements command for the server request pipeline.
                let command (parameters: EnableFeatureParameters) =
                    EnableCommit(parameters.Enabled)
                    |> returnValueTask

                context.Items.Add("Command", nameof EnableCommit)
                return! processCommand context validations command
            }

    /// Enables and disables checkpoint references in the provided branch.
    let EnableCheckpoint: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                /// Implements validations for the server request pipeline.
                let validations (parameters: EnableFeatureParameters) = [||]

                /// Implements command for the server request pipeline.
                let command (parameters: EnableFeatureParameters) =
                    EnableCheckpoint(parameters.Enabled)
                    |> returnValueTask

                context.Items.Add("Command", nameof EnableCheckpoint)
                return! processCommand context validations command
            }

    /// Enables and disables save references in the provided branch.
    let EnableSave: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                /// Implements validations for the server request pipeline.
                let validations (parameters: EnableFeatureParameters) = [||]

                /// Implements command for the server request pipeline.
                let command (parameters: EnableFeatureParameters) = EnableSave(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof EnableSave)
                return! processCommand context validations command
            }

    /// Enables and disables tag references in the provided branch.
    let EnableTag: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                /// Implements validations for the server request pipeline.
                let validations (parameters: EnableFeatureParameters) = [||]

                /// Implements command for the server request pipeline.
                let command (parameters: EnableFeatureParameters) = EnableTag(parameters.Enabled) |> returnValueTask

                context.Items.Add("Command", nameof EnableTag)
                return! processCommand context validations command
            }

    /// Enables and disables external references in the provided branch.
    let EnableExternal: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                /// Implements validations for the server request pipeline.
                let validations (parameters: EnableFeatureParameters) = [||]

                /// Implements command for the server request pipeline.
                let command (parameters: EnableFeatureParameters) =
                    EnableExternal(parameters.Enabled)
                    |> returnValueTask

                context.Items.Add("Command", nameof EnableExternal)
                return! processCommand context validations command
            }

    /// Enables and disables auto-rebase for the provided branch.
    let EnableAutoRebase: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                /// Implements validations for the server request pipeline.
                let validations (parameters: EnableFeatureParameters) = [||]

                /// Implements command for the server request pipeline.
                let command (parameters: EnableFeatureParameters) =
                    EnableAutoRebase(parameters.Enabled)
                    |> returnValueTask

                context.Items.Add("Command", nameof EnableAutoRebase)
                return! processCommand context validations command
            }

    /// Sets the promotion mode for the provided branch.
    let SetPromotionMode: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                /// Implements validations for the server request pipeline.
                let validations (parameters: SetPromotionModeParameters) = [||]

                /// Implements command for the server request pipeline.
                let command (parameters: SetPromotionModeParameters) =
                    let promotionMode =
                        match parameters.PromotionMode.ToLowerInvariant() with
                        | "individualonly" -> BranchPromotionMode.IndividualOnly
                        | "grouponly" -> BranchPromotionMode.GroupOnly
                        | "hybrid" -> BranchPromotionMode.Hybrid
                        | _ -> BranchPromotionMode.IndividualOnly

                    SetPromotionMode(promotionMode) |> returnValueTask

                context.Items.Add("Command", nameof SetPromotionMode)
                return! processCommand context validations command
            }

    /// Deletes the provided branch.
    let Delete: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                /// Implements validations for the server request pipeline.
                let validations (parameters: DeleteBranchParameters) =
                    [| // If reassigning child branches, validate that at least one parent is provided OR let it default to the deleted branch's parent
                    // No validation needed here since None is valid (uses deleted branch's parent)
                    |]

                /// Implements command for the server request pipeline.
                let command (parameters: DeleteBranchParameters) =
                    task {
                        let! newParentBranchIdOption =
                            task {
                                if parameters.ReassignChildBranches then
                                    if not
                                       <| String.IsNullOrEmpty(parameters.NewParentBranchId)
                                       || not
                                          <| String.IsNullOrEmpty(parameters.NewParentBranchName) then
                                        match!
                                            resolveBranchId
                                                graceIds.OwnerId
                                                graceIds.OrganizationId
                                                graceIds.RepositoryId
                                                parameters.NewParentBranchId
                                                parameters.NewParentBranchName
                                                parameters.CorrelationId
                                            with
                                        | Some branchId -> return Some branchId
                                        | None -> return None
                                    else
                                        return None
                                else
                                    return None
                            }

                        return BranchCommand.DeleteLogical(parameters.Force, parameters.DeleteReason, parameters.ReassignChildBranches, newParentBranchIdOption)
                    }
                    |> ValueTask<BranchCommand>

                context.Items.Add("Command", nameof DeleteLogical)
                return! processCommand context validations command
            }

    /// Updates the parent branch of the provided branch.
    let UpdateParentBranch: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let graceIds = getGraceIds context

                /// Implements validations for the server request pipeline.
                let validations (parameters: UpdateParentBranchParameters) =
                    [|
                        Input.eitherIdOrNameMustBeProvided
                            parameters.NewParentBranchId
                            parameters.NewParentBranchName
                            BranchError.EitherBranchIdOrBranchNameRequired
                        Guid.isValidAndNotEmptyGuid parameters.NewParentBranchId BranchError.InvalidBranchId
                        String.isValidGraceName parameters.NewParentBranchName BranchError.InvalidBranchName
                        Branch.branchExists
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            parameters.NewParentBranchId
                            parameters.NewParentBranchName
                            parameters.CorrelationId
                            BranchError.ParentBranchDoesNotExist
                        Branch.parentBranchAllowsPromotions
                            graceIds.OwnerId
                            graceIds.OrganizationId
                            graceIds.RepositoryId
                            parameters.NewParentBranchId
                            parameters.NewParentBranchName
                            parameters.CorrelationId
                            BranchError.ParentBranchDoesNotAllowPromotions
                    |]

                /// Implements command for the server request pipeline.
                let command (parameters: UpdateParentBranchParameters) =
                    task {
                        match!
                            resolveBranchId
                                graceIds.OwnerId
                                graceIds.OrganizationId
                                graceIds.RepositoryId
                                parameters.NewParentBranchId
                                parameters.NewParentBranchName
                                parameters.CorrelationId
                            with
                        | Some newParentBranchId -> return BranchCommand.UpdateParentBranch newParentBranchId
                        | None ->
                            // This should never happen due to validations, log error for debugging
                            log.LogError(
                                "{CurrentInstant}: Failed to resolve new parent branch ID after validations passed. NewParentBranchId: {NewParentBranchId}, NewParentBranchName: {NewParentBranchName}",
                                getCurrentInstantExtended (),
                                parameters.NewParentBranchId,
                                parameters.NewParentBranchName
                            )
                            // Return Guid.Empty which will be caught by the actor or cause a validation error
                            return BranchCommand.UpdateParentBranch Guid.Empty
                    }
                    |> ValueTask<BranchCommand>

                context.Items.Add("Command", nameof UpdateParentBranch)
                return! processCommand context validations command
            }

    /// Gets details about the provided branch.
    let Get: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    let! parameters = context |> parse<GetBranchParameters>
                    let actorProxy = Branch.CreateActorProxy graceIds.BranchId graceIds.RepositoryId (getCorrelationId context)
                    let! branchDto = actorProxy.Get(getCorrelationId context)
                    let! isObservable = canObserveBranch context branchDto

                    let! result =
                        match isObservable with
                        | true ->
                            let graceReturnValue =
                                (GraceReturnValue.Create branchDto (getCorrelationId context))
                                    .enhance(getParametersAsDictionary parameters)
                                    .enhance(nameof OwnerId, graceIds.OwnerId)
                                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                    .enhance(nameof BranchId, graceIds.BranchId)
                                    .enhance ("Path", context.Request.Path.Value)

                            context |> result200Ok graceReturnValue
                        | false ->
                            context
                            |> result400BadRequest (hiddenBranchError context (callerSuppliedBranchId context parameters))

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty (getCorrelationId context))
            }

    /// Gets the events handled by this branch.
    let GetEvents: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: GetBranchParameters) = [||]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchEvents = actorProxy.GetEvents(getCorrelationId context)
                            return branchEvents.Select(fun branchEvent -> serialize branchEvent)
                        //return List<Events.Branch.BranchEvent>()
                        }

                    let! parameters = context |> parse<GetBranchParameters>
                    let! result = processObservableQuery context parameters validations 1 query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty (getCorrelationId context))
            }

    /// Gets details about the parent branch of the provided branch.
    let GetParentBranch: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: BranchParameters) = [||]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! parentBranchDto = actorProxy.GetParentBranch(getCorrelationId context)
                            return parentBranchDto
                        }

                    let! parameters = context |> parse<BranchParameters>
                    let! result = processObservableQuery context parameters validations 1 query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty (getCorrelationId context))
            }

    /// Gets details about the reference with the provided ReferenceId.
    let GetReference: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context
                let repositoryId = Guid.Parse(graceIds.RepositoryIdString)

                try
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: GetReferenceParameters) = [||]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let referenceGuid = Guid.Parse(context.Items["ReferenceId"] :?> string)

                            let referenceActorProxy = Reference.CreateActorProxy referenceGuid repositoryId (getCorrelationId context)

                            let! referenceDto = referenceActorProxy.Get(getCorrelationId context)
                            return! getObservableReferenceOrDefault context referenceDto
                        }

                    let! parameters = context |> parse<GetReferenceParameters>
                    context.Items[ "ReferenceId" ] <- parameters.ReferenceId
                    let! result = processObservableQuery context parameters validations 1 query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty (getCorrelationId context))
            }

    /// Gets details about multiple references in one API call.
    let GetReferences: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: GetReferencesParameters) =
                        [|
                            Number.isPositiveOrZero parameters.MaxCount BranchError.ValueMustBePositive
                        |]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) (maxCount: int) (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getVisibleReferences context branchDto maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processObservableQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty (getCorrelationId context))
            }

    /// Reveals one private reference after revalidating graph completeness and hidden-link safety.
    let RevealReference: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context
                let repositoryId = graceIds.RepositoryId
                let correlationId = getCorrelationId context

                try
                    let! parameters = context |> parse<RevealReferenceParameters>
                    let parameterDictionary = getParametersAsDictionary parameters

                    let badRequest message =
                        (GraceError.Create message correlationId)
                            .enhance(parameterDictionary)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof BranchId, graceIds.BranchId)
                            .enhance ("Path", context.Request.Path.Value)

                    if parameters.ReferenceId = ReferenceId.Empty then
                        return!
                            context
                            |> result400BadRequest (badRequest (ReferenceError.getErrorMessage ReferenceError.InvalidReferenceId))
                    elif String.IsNullOrWhiteSpace parameters.OperationId then
                        return!
                            context
                            |> result400BadRequest (badRequest "Reference reveal operationId is required.")
                    else
                        let referenceActorProxy = Reference.CreateActorProxy parameters.ReferenceId repositoryId correlationId
                        let! referenceDto = referenceActorProxy.Get correlationId

                        if referenceDto.ReferenceId = ReferenceId.Empty then
                            return!
                                context
                                |> result400BadRequest (badRequest (ReferenceError.getErrorMessage ReferenceError.ReferenceIdDoesNotExist))
                        elif referenceDto.RepositoryId <> repositoryId
                             || referenceDto.BranchId <> graceIds.BranchId then
                            return!
                                context
                                |> result400BadRequest (badRequest (ReferenceError.getErrorMessage ReferenceError.ReferenceIdDoesNotExist))
                        else
                            let branchActorProxy = Branch.CreateActorProxy graceIds.BranchId repositoryId correlationId
                            let! branchDto = branchActorProxy.Get correlationId

                            let revealPrincipal =
                                PrincipalMapper.tryGetUserId context.User
                                |> Option.defaultValue String.Empty

                            let metadata = EventMetadata.New correlationId revealPrincipal
                            metadata.Properties[ nameof RepositoryId ] <- $"{repositoryId}"
                            metadata.Properties[ nameof OwnerId ] <- $"{graceIds.OwnerId}"
                            metadata.Properties[ nameof OrganizationId ] <- $"{graceIds.OrganizationId}"
                            metadata.Properties[ nameof BranchId ] <- $"{graceIds.BranchId}"
                            metadata.Properties[ nameof ReferenceId ] <- $"{parameters.ReferenceId}"
                            metadata.Properties[ "ReferenceRevealOperationId" ] <- parameters.OperationId

                            referenceDto
                            |> tryGetTerminalPromotionSetForReveal
                            |> Option.iter (fun promotionSetId ->
                                metadata.Properties[ nameof PromotionSetId ] <- $"{promotionSetId}"
                                metadata.Properties[ "TerminalPromotionReferenceId" ] <- $"{parameters.ReferenceId}")

                            /// Runs the durable actor transition after route-local reveal preconditions have passed or replay is detected.
                            let executeReveal () =
                                task {
                                    match!
                                        referenceActorProxy.Handle
                                            (Grace.Types.Reference.ReferenceCommand.Reveal(parameters.OperationId, parameters.Reason))
                                            metadata
                                        with
                                    | Ok revealReturnValue ->
                                        let graceReturnValue =
                                            (GraceReturnValue.Create revealReturnValue.ReturnValue correlationId)
                                                .enhance(parameterDictionary)
                                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                                .enhance(nameof BranchId, graceIds.BranchId)
                                                .enhance(nameof ReferenceId, parameters.ReferenceId)
                                                .enhance ("Path", context.Request.Path.Value)

                                        return! context |> result200Ok graceReturnValue
                                    | Error error -> return! context |> result400BadRequest error
                                }

                            if referenceDto.Visibility = ResourceVisibility.Public
                               && referenceDto.LastRevealOperationId = Some parameters.OperationId then
                                return! executeReveal ()
                            else
                                match! validateRevealGraph repositoryId referenceDto correlationId with
                                | Error error -> return! context |> result400BadRequest error
                                | Ok () ->
                                    match! validateRevealLinks context branchDto repositoryId parameters.ReferenceId referenceDto correlationId with
                                    | Error error -> return! context |> result400BadRequest error
                                    | Ok () -> return! executeReveal ()
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty correlationId)
            }

    /// Annotates a server-known file from an existing reference in a branch.
    let Annotate: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                try
                    let! parameters = context |> parse<AnnotateParameters>
                    parameters.OwnerId <- graceIds.OwnerIdString
                    parameters.OrganizationId <- graceIds.OrganizationIdString
                    parameters.RepositoryId <- graceIds.RepositoryIdString
                    parameters.BranchId <- graceIds.BranchIdString

                    match! buildAnnotationForRequest context parameters with
                    | Ok annotation ->
                        let graceReturnValue =
                            (GraceReturnValue.Create annotation correlationId)
                                .enhance(getParametersAsDictionary parameters)
                                .enhance(nameof OwnerId, graceIds.OwnerId)
                                .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                .enhance(nameof BranchId, graceIds.BranchId)
                                .enhance ("Path", context.Request.Path.Value)

                        let duration_ms = getDurationRightAligned_ms startTime

                        log.LogInformation(
                            "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}; TargetReferenceId: {targetReferenceId}.",
                            getCurrentInstantExtended (),
                            getMachineName,
                            duration_ms,
                            correlationId,
                            context.Request.Path,
                            graceIds.BranchIdString,
                            parameters.TargetReferenceId
                        )

                        return! context |> result200Ok graceReturnValue
                    | Error graceError ->
                        graceError
                            .enhance(getParametersAsDictionary parameters)
                            .enhance(nameof OwnerId, graceIds.OwnerId)
                            .enhance(nameof OrganizationId, graceIds.OrganizationId)
                            .enhance(nameof RepositoryId, graceIds.RepositoryId)
                            .enhance(nameof BranchId, graceIds.BranchId)
                            .enhance ("Path", context.Request.Path.Value)
                        |> ignore

                        return! context |> result400BadRequest graceError
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty correlationId)
            }

    /// Gets a list of references, given a list of reference IDs.
    let GetLatestReferencesByReferenceTypes: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: GetLatestReferencesByReferenceTypeParameters) =
                        [|
                            Input.listIsNonEmpty parameters.ReferenceTypes BranchError.ReferenceTypeMustBeProvided
                        |]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) (maxCount: int) (actorProxy: IBranchActor) =
                        task {
                            let referenceTypes = context.Items["ReferenceTypes"] :?> ReferenceType array

                            log.LogDebug(
                                "In Repository.Server.GetLatestReferencesByReferenceTypes: ReferenceTypes: {referenceTypes}.",
                                serialize referenceTypes
                            )

                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            return! getLatestVisibleReferencesByTypes context branchDto referenceTypes
                        }

                    let! parameters =
                        context
                        |> parse<GetLatestReferencesByReferenceTypeParameters>

                    context.Items.Add("ReferenceTypes", parameters.ReferenceTypes)
                    let! result = processObservableQuery context parameters validations 1 query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; RepositoryId: {repositoryId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.RepositoryIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty (getCorrelationId context))
            }

    /// Retrieves the diffs between references in a branch by ReferenceType.
    let GetDiffsForReferenceType: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                try
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: GetDiffsForReferenceTypeParameters) =
                        [|
                            Number.isPositiveOrZero parameters.MaxCount BranchError.ValueMustBePositive
                            String.isNotEmpty parameters.ReferenceType BranchError.ReferenceTypeMustBeProvided
                            DiscriminatedUnion.isMemberOf<ReferenceType, BranchError> parameters.ReferenceType BranchError.InvalidReferenceType
                        |]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) (maxCount: int) (actorProxy: IBranchActor) =
                        task {
                            let diffDtos = ConcurrentBag<DiffDto>()
                            let! branchDto = actorProxy.Get correlationId

                            let referenceType =
                                (context.Items[nameof ReferenceType] :?> String)
                                |> discriminatedUnionFromString<ReferenceType>

                            let! references = getVisibleReferencesByType context branchDto referenceType.Value maxCount

                            let sortedRefs =
                                references
                                    .OrderByDescending(fun referenceDto -> referenceDto.CreatedAt)
                                    .ToList()

                            // Take pairs of references and get the diffs between them.
                            do!
                                Parallel.ForEachAsync(
                                    [ 0 .. sortedRefs.Count - 2 ],
                                    Constants.ParallelOptions,
                                    (fun i ct ->
                                        ValueTask(
                                            task {
                                                let diffActorProxy =
                                                    Diff.CreateActorProxy
                                                        sortedRefs[i].DirectoryId
                                                        sortedRefs[i + 1].DirectoryId
                                                        branchDto.OwnerId
                                                        branchDto.OrganizationId
                                                        branchDto.RepositoryId
                                                        correlationId

                                                let! diffDto = diffActorProxy.GetDiff correlationId
                                                diffDtos.Add(diffDto)
                                            }
                                        ))
                                )

                            return
                                (sortedRefs,
                                 diffDtos
                                     .OrderByDescending(fun diffDto ->
                                         Math.Max(diffDto.Directory1CreatedAt.ToUnixTimeMilliseconds(), diffDto.Directory2CreatedAt.ToUnixTimeMilliseconds()))
                                     .ToList())
                        }

                    let! parameters =
                        context
                        |> parse<GetDiffsForReferenceTypeParameters>

                    context.Items.Add(nameof ReferenceType, parameters.ReferenceType)
                    let! result = processObservableQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty (getCorrelationId context))
            }

    /// Gets the promotions in a branch.
    let GetPromotions: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: GetReferencesParameters) =
                        [|
                            Number.isPositiveOrZero parameters.MaxCount BranchError.ValueMustBePositive
                        |]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getVisibleReferencesByType context branchDto ReferenceType.Promotion maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processObservableQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty (getCorrelationId context))
            }

    /// Gets the commits in a branch.
    let GetCommits: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: GetReferencesParameters) =
                        [|
                            Number.isPositiveOrZero parameters.MaxCount BranchError.ValueMustBePositive
                        |]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getVisibleReferencesByType context branchDto ReferenceType.Commit maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processObservableQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty (getCorrelationId context))
            }

    /// Gets the checkpoints in a branch.
    let GetCheckpoints: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: GetReferencesParameters) =
                        [|
                            Number.isPositiveOrZero parameters.MaxCount BranchError.ValueMustBePositive
                        |]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getVisibleReferencesByType context branchDto ReferenceType.Checkpoint maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processObservableQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty (getCorrelationId context))
            }

    /// Gets the saves in a branch.
    let GetSaves: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: GetReferencesParameters) =
                        [|
                            Number.isPositiveOrZero parameters.MaxCount BranchError.ValueMustBePositive
                        |]


                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getVisibleReferencesByType context branchDto ReferenceType.Save maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processObservableQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty (getCorrelationId context))
            }

    /// Gets the tags in a branch.
    let GetTags: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: GetReferencesParameters) =
                        [|
                            Number.isPositiveOrZero parameters.MaxCount BranchError.ValueMustBePositive
                        |]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getVisibleReferencesByType context branchDto ReferenceType.Tag maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processObservableQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty (getCorrelationId context))
            }

    /// Gets the external references in a branch.
    let GetExternals: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context

                try
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: GetReferencesParameters) =
                        [|
                            Number.isPositiveOrZero parameters.MaxCount BranchError.ValueMustBePositive
                        |]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let! branchDto = actorProxy.Get(getCorrelationId context)
                            let! results = getVisibleReferencesByType context branchDto ReferenceType.External maxCount
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processObservableQuery context parameters validations (parameters.MaxCount) query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        (getCorrelationId context),
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty (getCorrelationId context))
            }

    /// Handles the Grace Server get recursive size request.
    let GetRecursiveSize: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                let repositoryId = graceIds.RepositoryId

                try
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: ListContentsParameters) =
                        [|
                            String.isEmptyOrValidSha256HashPrefix parameters.Sha256Hash BranchError.InvalidSha256Hash
                            String.isEmptyOrValidBlake3HashPrefix parameters.Blake3Hash BranchError.InvalidBlake3Hash
                            Guid.isValidAndNotEmptyGuid parameters.ReferenceId BranchError.InvalidReferenceId
                        |]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let listContentsParameters = context.Items["ListContentsParameters"] :?> ListContentsParameters

                            if
                                String.IsNullOrEmpty(listContentsParameters.ReferenceId)
                                && String.IsNullOrEmpty(listContentsParameters.Sha256Hash)
                                && String.IsNullOrEmpty(listContentsParameters.Blake3Hash)
                            then
                                // If we don't have a referenceId or sha256Hash, we'll get the contents of the most recent reference in the branch.
                                let! branchDto = actorProxy.Get correlationId
                                let! latestReference = getLatestVisibleReference context branchDto

                                match latestReference with
                                | Some latestReference ->
                                    let directoryActorProxy = DirectoryVersion.CreateActorProxy latestReference.DirectoryId branchDto.RepositoryId correlationId

                                    let! recursiveSize = directoryActorProxy.GetRecursiveSize(getCorrelationId context)
                                    return recursiveSize
                                | None -> return Constants.InitialDirectorySize
                            elif
                                not
                                <| String.IsNullOrEmpty(listContentsParameters.ReferenceId)
                            then
                                // We have a ReferenceId, so we'll get the DirectoryVersion from that reference.
                                let referenceGuid = Guid.Parse(listContentsParameters.ReferenceId)
                                let referenceActorProxy = Reference.CreateActorProxy referenceGuid repositoryId correlationId

                                let! referenceDto = referenceActorProxy.Get correlationId
                                let! referenceDto = getObservableReferenceOrDefault context referenceDto

                                if referenceDto.ReferenceId = ReferenceId.Empty then
                                    return Constants.InitialDirectorySize
                                else
                                    let directoryActorProxy = DirectoryVersion.CreateActorProxy referenceDto.DirectoryId repositoryId correlationId

                                    let! recursiveSize = directoryActorProxy.GetRecursiveSize correlationId
                                    return recursiveSize
                            else
                                match!
                                    tryResolveObservableDirectoryVersionByHash
                                        context
                                        actorProxy
                                        listContentsParameters.Sha256Hash
                                        listContentsParameters.Blake3Hash
                                        false
                                    with
                                | Some directoryVersion ->
                                    let directoryActorProxy = DirectoryVersion.CreateActorProxy directoryVersion.DirectoryVersionId repositoryId correlationId

                                    let! recursiveSize = directoryActorProxy.GetRecursiveSize correlationId
                                    return recursiveSize
                                | None -> return Constants.InitialDirectorySize
                        }

                    let! parameters = context |> parse<ListContentsParameters>
                    context.Items[ "ListContentsParameters" ] <- parameters
                    let! result = processObservableQuery context parameters validations 1 query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty correlationId)
            }

    /// Handles the Grace Server list contents request.
    let ListContents: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context
                let repositoryId = Guid.Parse(graceIds.RepositoryIdString)

                try
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: ListContentsParameters) =
                        [|
                            String.isEmptyOrValidSha256HashPrefix parameters.Sha256Hash BranchError.InvalidSha256Hash
                            String.isEmptyOrValidBlake3HashPrefix parameters.Blake3Hash BranchError.InvalidBlake3Hash
                            Guid.isValidAndNotEmptyGuid parameters.ReferenceId BranchError.InvalidReferenceId
                        |]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let listContentsParameters = context.Items["ListContentsParameters"] :?> ListContentsParameters

                            if
                                String.IsNullOrEmpty(listContentsParameters.ReferenceId)
                                && String.IsNullOrEmpty(listContentsParameters.Sha256Hash)
                                && String.IsNullOrEmpty(listContentsParameters.Blake3Hash)
                            then
                                // If we don't have a referenceId or sha256Hash, we'll get the contents of the most recent reference in the branch.
                                let! branchDto = actorProxy.Get correlationId
                                let! latestReference = getLatestVisibleReference context branchDto

                                match latestReference with
                                | Some latestReference ->
                                    let directoryActorProxy = DirectoryVersion.CreateActorProxy latestReference.DirectoryId graceIds.RepositoryId correlationId

                                    let! contents = directoryActorProxy.GetRecursiveDirectoryVersions listContentsParameters.ForceRecompute correlationId

                                    return contents
                                | None -> return Array.Empty<DirectoryVersion.DirectoryVersionDto>()
                            elif
                                not
                                <| String.IsNullOrEmpty(listContentsParameters.ReferenceId)
                            then
                                // We have a ReferenceId, so we'll get the DirectoryVersion from that reference.
                                let referenceGuid = Guid.Parse(listContentsParameters.ReferenceId)

                                let referenceActorProxy = Reference.CreateActorProxy referenceGuid repositoryId correlationId

                                let! referenceDto = referenceActorProxy.Get correlationId
                                let! referenceDto = getObservableReferenceOrDefault context referenceDto

                                if referenceDto.ReferenceId = ReferenceId.Empty then
                                    return Array.Empty<DirectoryVersion.DirectoryVersionDto>()
                                else
                                    let directoryActorProxy = DirectoryVersion.CreateActorProxy referenceDto.DirectoryId repositoryId correlationId

                                    let! contents = directoryActorProxy.GetRecursiveDirectoryVersions listContentsParameters.ForceRecompute correlationId

                                    return contents
                            else
                                match!
                                    tryResolveObservableDirectoryVersionByHash
                                        context
                                        actorProxy
                                        listContentsParameters.Sha256Hash
                                        listContentsParameters.Blake3Hash
                                        true
                                    with
                                | Some directoryVersion ->
                                    let directoryActorProxy = DirectoryVersion.CreateActorProxy directoryVersion.DirectoryVersionId repositoryId correlationId

                                    let! contents = directoryActorProxy.GetRecursiveDirectoryVersions listContentsParameters.ForceRecompute correlationId

                                    return contents
                                | None -> return Array.Empty<DirectoryVersion.DirectoryVersionDto>()
                        }

                    let! parameters = context |> parse<ListContentsParameters>
                    context.Items[ "ListContentsParameters" ] <- parameters
                    let! result = processObservableQuery context parameters validations 1 query

                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogInformation(
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Finished {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return result
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty correlationId)
            }

    /// Builds the validation checks for a version query based on the requested target type.
    let private getVersionValidations (parameters: GetBranchVersionParameters) =
        [|
            String.isEmptyOrValidSha256HashPrefix parameters.Sha256Hash BranchError.InvalidSha256Hash
            String.isEmptyOrValidBlake3HashPrefix parameters.Blake3Hash BranchError.InvalidBlake3Hash
            Guid.isValidAndNotEmptyGuid parameters.ReferenceId BranchError.InvalidReferenceId
        |]

    /// Resolves try resolve root directory version data from request or repository state.
    let private tryResolveRootDirectoryVersion
        (context: HttpContext)
        (parameters: GetBranchVersionParameters)
        (actorProxy: IBranchActor)
        (repositoryId: Guid)
        (correlationId: CorrelationId)
        =
        task {
            if
                not (String.IsNullOrEmpty(parameters.Sha256Hash))
                || not (String.IsNullOrEmpty(parameters.Blake3Hash))
            then
                return! tryResolveObservableDirectoryVersionByHash context actorProxy parameters.Sha256Hash parameters.Blake3Hash true
            elif
                not
                <| String.IsNullOrEmpty(parameters.ReferenceId)
            then
                let referenceActorProxy = Reference.CreateActorProxy (Guid.Parse(parameters.ReferenceId)) repositoryId correlationId
                let! referenceDto = referenceActorProxy.Get correlationId
                let! referenceDto = getObservableReferenceOrDefault context referenceDto

                if referenceDto.ReferenceId = ReferenceId.Empty then
                    return None
                else
                    return! getRootDirectoryVersionByReferenceId repositoryId referenceDto.ReferenceId correlationId
            else
                let! branchDto = actorProxy.Get(getCorrelationId context)
                let! latestReference = getLatestVisibleReference context branchDto

                match latestReference with
                | Some referenceDto -> return! getRootDirectoryVersionByReferenceId repositoryId referenceDto.ReferenceId correlationId
                | None -> return None
        }

    /// Converts version query parameters into the actor query and directory-version lookup inputs.
    let private getVersionQuery (context: HttpContext) _maxCount (actorProxy: IBranchActor) =
        task {
            let parameters = context.Items["GetVersionParameters"] :?> GetBranchVersionParameters
            let repositoryId = Guid.Parse(parameters.RepositoryId)
            let correlationId = getCorrelationId context

            let! rootDirectoryVersion = tryResolveRootDirectoryVersion context parameters actorProxy repositoryId correlationId

            match rootDirectoryVersion with
            | Some rootDirectoryVersion ->
                let directoryVersionActorProxy = DirectoryVersion.CreateActorProxy rootDirectoryVersion.DirectoryVersionId repositoryId correlationId

                let! directoryVersionDtos = directoryVersionActorProxy.GetRecursiveDirectoryVersions false correlationId

                let rootDirectoryId = rootDirectoryVersion.DirectoryVersionId
                let directoryIds = List<DirectoryVersionId>()
                directoryIds.Add(rootDirectoryId)

                directoryIds.AddRange(
                    directoryVersionDtos
                        .Select(fun dv -> dv.DirectoryVersion.DirectoryVersionId)
                        .Where(fun directoryVersionId -> directoryVersionId <> rootDirectoryId)
                )

                return directoryIds
            | None -> return List<DirectoryVersionId>()
        }

    /// Implements update parameters from branch for the server request pipeline.
    let private updateParametersFromBranch (parameters: GetBranchVersionParameters) (repositoryIdResolved: Guid) =
        task {
            logToConsole $"In Branch.GetVersion: parameters.BranchId: {parameters.BranchId}; parameters.BranchName: {parameters.BranchName}"

            let! resolvedBranchId =
                resolveBranchId
                    parameters.OwnerId
                    parameters.OrganizationId
                    repositoryIdResolved
                    parameters.BranchId
                    parameters.BranchName
                    parameters.CorrelationId

            match resolvedBranchId with
            | Some branchId -> parameters.BranchId <- $"{branchId}"
            | None -> () // This should never happen because it would get caught in validations.
        }

    /// Updates getVersion branch routing from ReferenceId only when that reference is observable to the caller.
    let private updateParametersFromObservableReference
        (context: HttpContext)
        (parameters: GetBranchVersionParameters)
        (repositoryIdResolved: Guid)
        (correlationId: CorrelationId)
        =
        task {
            let mutable referenceGuid = Guid.Empty

            if
                Guid.TryParse(parameters.ReferenceId, &referenceGuid)
                && referenceGuid <> Guid.Empty
            then
                let referenceActorProxy = Reference.CreateActorProxy referenceGuid repositoryIdResolved correlationId
                let! referenceDto = referenceActorProxy.Get correlationId

                if referenceDto.ReferenceId <> ReferenceId.Empty then
                    let branchActorProxy = Branch.CreateActorProxy referenceDto.BranchId referenceDto.RepositoryId correlationId
                    let! branchDto = branchActorProxy.Get correlationId
                    let! isObservable = canObserveReferenceInBranch context branchDto referenceDto

                    if isObservable then parameters.BranchId <- $"{referenceDto.BranchId}"
        }

    /// Implements update parameters for repository id option for the server request pipeline.
    let private updateParametersForRepositoryIdOption
        (context: HttpContext)
        (correlationId: CorrelationId)
        (parameters: GetBranchVersionParameters)
        (repositoryIdOption: Guid option)
        =
        match repositoryIdOption with
        | None -> Task.FromResult(())
        | Some repositoryIdResolved ->
            let repositoryIdString = $"{repositoryIdResolved}"
            parameters.RepositoryId <- repositoryIdString

            if not <| String.IsNullOrEmpty(parameters.BranchId)
               || not <| String.IsNullOrEmpty(parameters.BranchName) then
                updateParametersFromBranch parameters repositoryIdResolved
            elif
                not
                <| String.IsNullOrEmpty(parameters.ReferenceId)
            then
                updateParametersFromObservableReference context parameters repositoryIdResolved correlationId
            else
                Task.FromResult(())

    /// Executes version lookup by validating inputs, querying the branch actor, and returning the materialized DTO.
    let private getVersionImpl (next: HttpFunc) (context: HttpContext) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context

            let! parameters = context |> parse<GetBranchVersionParameters>
            context.Items[ callerSuppliedBranchIdItemKey ] <- parameters.BranchId

            let! repositoryIdOption =
                resolveRepositoryId graceIds.OwnerId graceIds.OrganizationId parameters.RepositoryId parameters.RepositoryName parameters.CorrelationId

            do! updateParametersForRepositoryIdOption context correlationId parameters repositoryIdOption

            // Now that we've populated BranchId for sure...
            context.Items.Add("GetVersionParameters", parameters)
            return! processObservableQuery context parameters getVersionValidations 1 getVersionQuery
        }

    /// Handles the Grace Server get version request.
    let GetVersion: HttpHandler =
        fun (next: HttpFunc) (context: HttpContext) ->
            task {
                let startTime = getCurrentInstant ()
                let graceIds = getGraceIds context
                let correlationId = getCorrelationId context

                try
                    return! getVersionImpl next context
                with
                | ex ->
                    let duration_ms = getDurationRightAligned_ms startTime

                    log.LogError(
                        ex,
                        "{CurrentInstant}: Node: {HostName}; Duration: {duration_ms}ms; CorrelationId: {correlationId}; Error in {path}; BranchId: {branchId}.",
                        getCurrentInstantExtended (),
                        getMachineName,
                        duration_ms,
                        correlationId,
                        context.Request.Path,
                        graceIds.BranchIdString
                    )

                    return!
                        context
                        |> result500ServerError (GraceError.CreateWithException ex String.Empty correlationId)
            }
