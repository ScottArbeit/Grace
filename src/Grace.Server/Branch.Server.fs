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
open Grace.Types
open Grace.Types.Annotation
open Grace.Types.Authorization
open Grace.Types.Branch
open Grace.Types.Diff
open Grace.Types.Common
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
        (repositoryDto: Repository.RepositoryDto)
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
        (repositoryDto: Repository.RepositoryDto)
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
                        let! branchReferences = getReferences basedOnReference.RepositoryId basedOnReference.BranchId Int32.MaxValue correlationId

                        let basedOnHistoryWindow =
                            if branchReferences
                               |> Array.exists (fun branchReference -> branchReference.ReferenceId = basedOnReference.ReferenceId) then
                                branchReferences
                            else
                                Array.append branchReferences [| basedOnReference |]
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
                let repositoryActorProxy = Repository.CreateActorProxy graceIds.OrganizationId graceIds.RepositoryId correlationId
                let! repositoryDto = repositoryActorProxy.Get correlationId

                let referenceActorProxy = Reference.CreateActorProxy parameters.TargetReferenceId graceIds.RepositoryId correlationId
                let! targetReference = referenceActorProxy.Get correlationId

                if targetReference.ReferenceId = ReferenceId.Empty then
                    return Error(annotationError correlationId (BranchError.getErrorMessage BranchError.ReferenceIdDoesNotExist))
                elif targetReference.DeletedAt.IsSome then
                    return Error(annotationError correlationId "Annotation target Reference has been deleted.")
                elif targetReference.RepositoryId
                     <> graceIds.RepositoryId
                     || targetReference.BranchId <> graceIds.BranchId then
                    return Error(annotationError correlationId "Annotation target Reference must belong to the requested repository and branch.")
                else
                    let! targetContents = getReferenceContents repositoryDto.RepositoryId correlationId targetReference

                    match tryFindFileVersion normalizedPath targetContents with
                    | None -> return Error(annotationError correlationId $"Annotation target path '{normalizedPath}' was not found in the target Reference.")
                    | Some _ ->
                        let! branchReferences = getReferences targetReference.RepositoryId targetReference.BranchId Int32.MaxValue correlationId

                        let localHistoryWindow =
                            if branchReferences
                               |> Array.exists (fun referenceDto -> referenceDto.ReferenceId = targetReference.ReferenceId) then
                                branchReferences
                            else
                                Array.append branchReferences [| targetReference |]
                            |> orderedHistoryWindowWithSyntheticBoundaries targetReference.ReferenceId parameters.MaxReferences

                        let! references =
                            includeStoredBasedOnReferences
                                targetReference.RepositoryId
                                correlationId
                                parameters.MaxReferences
                                localHistoryWindow.SyntheticBasedOnByReferenceId
                                localHistoryWindow.References

                        match! buildEffectiveHistory context repositoryDto normalizedPath targetReference.ReferenceId references with
                        | Error error -> return Error error
                        | Ok history ->
                            let traversal = AnnotationLineCore.traverseEffectiveBranchHistory targetReference.ReferenceId parameters.MaxReferences history

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
                parameterDictionary.AddRange(getParametersAsDictionary parameters)

                // We know these Id's from ValidateIds.Middleware, so let's set them so we never have to resolve them again.
                parameters.OwnerId <- graceIds.OwnerIdString
                parameters.OrganizationId <- graceIds.OrganizationIdString
                parameters.RepositoryId <- graceIds.RepositoryIdString
                parameters.BranchId <- graceIds.BranchIdString

                /// Coordinates handle command processing for Grace Server.
                let handleCommand cmd =
                    task {
                        let actorProxy = Branch.CreateActorProxy graceIds.BranchId graceIds.RepositoryId correlationId

                        //logToConsole
                        //    $"In Branch.Server.processCommand: command: {commandName}; OwnerId: {graceIds.OwnerIdString}; OrganizationId: {graceIds.OrganizationIdString}; RepositoryId: {graceIds.RepositoryIdString}; BranchId: {graceIds.BranchIdString}."

                        match! actorProxy.Handle cmd (createMetadata context) with
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
                        let! result = handleCommand cmd
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
                            let parentBranchActorProxy = Branch.CreateActorProxy parentBranchId repositoryId parameters.CorrelationId

                            let! parentBranch = parentBranchActorProxy.Get parameters.CorrelationId

                            return
                                Create(
                                    graceIds.BranchId,
                                    (BranchName parameters.BranchName),
                                    parentBranchId,
                                    parentBranch.BasedOn.ReferenceId,
                                    graceIds.OwnerId,
                                    graceIds.OrganizationId,
                                    graceIds.RepositoryId,
                                    parameters.InitialPermissions
                                )
                        | None ->
                            return
                                Create(
                                    graceIds.BranchId,
                                    (BranchName parameters.BranchName),
                                    Constants.DefaultParentBranchId,
                                    ReferenceId.Empty, // This is fucked.
                                    graceIds.OwnerId,
                                    graceIds.OrganizationId,
                                    graceIds.RepositoryId,
                                    parameters.InitialPermissions
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

                        return DeleteLogical(parameters.Force, parameters.DeleteReason, parameters.ReassignChildBranches, newParentBranchIdOption)
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
                    /// Implements validations for the server request pipeline.
                    let validations (parameters: GetBranchParameters) = [||]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) = actorProxy.Get(getCorrelationId context)

                    let! parameters = context |> parse<GetBranchParameters>
                    let! result = processQuery context parameters validations 1 query

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
                    let! result = processQuery context parameters validations 1 query

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
                    let! parameters = context |> parse<BranchParameters>
                    let correlationId = getCorrelationId context
                    let parameterDictionary = getParametersAsDictionary parameters
                    let branchActorProxy = Branch.CreateActorProxy graceIds.BranchId graceIds.RepositoryId correlationId
                    let! parentBranch = branchActorProxy.GetParentBranch correlationId

                    let! result =
                        match parentBranch with
                        | Some parentBranchDto ->
                            context
                            |> result200Ok (
                                (GraceReturnValue.Create parentBranchDto correlationId)
                                    .enhance(parameterDictionary)
                                    .enhance(nameof OwnerId, graceIds.OwnerId)
                                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                    .enhance(nameof BranchId, graceIds.BranchId)
                                    .enhance ("Path", context.Request.Path.Value)
                            )
                        | None ->
                            context
                            |> result400BadRequest (
                                (GraceError.Create (BranchError.getErrorMessage BranchError.ParentBranchDoesNotExist) correlationId)
                                    .enhance(parameterDictionary)
                                    .enhance(nameof OwnerId, graceIds.OwnerId)
                                    .enhance(nameof OrganizationId, graceIds.OrganizationId)
                                    .enhance(nameof RepositoryId, graceIds.RepositoryId)
                                    .enhance(nameof BranchId, graceIds.BranchId)
                                    .enhance ("Path", context.Request.Path.Value)
                            )

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
                    let validations (parameters: GetReferenceParameters) =
                        [|
                            Guid.isValidAndNotEmptyGuid parameters.ReferenceId BranchError.InvalidReferenceId
                        |]

                    /// Implements query for the server request pipeline.
                    let query (context: HttpContext) maxCount (actorProxy: IBranchActor) =
                        task {
                            let referenceGuid = Guid.Parse(context.Items["ReferenceId"] :?> string)

                            let referenceActorProxy = Reference.CreateActorProxy referenceGuid repositoryId (getCorrelationId context)

                            let referenceDto = referenceActorProxy.Get(getCorrelationId context)
                            return referenceDto
                        }

                    let! parameters = context |> parse<GetReferenceParameters>
                    context.Items[ "ReferenceId" ] <- parameters.ReferenceId
                    let! result = processQuery context parameters validations 1 query

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
                            let! results = getReferences branchDto.RepositoryId branchDto.BranchId maxCount (getCorrelationId context)
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

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

                            return! getLatestReferenceByReferenceTypes referenceTypes graceIds.RepositoryId graceIds.BranchId
                        }

                    let! parameters =
                        context
                        |> parse<GetLatestReferencesByReferenceTypeParameters>

                    context.Items.Add("ReferenceTypes", serialize parameters.ReferenceTypes)
                    let! result = processQuery context parameters validations 1 query

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

                            let! references =
                                getReferencesByType referenceType.Value branchDto.RepositoryId branchDto.BranchId maxCount (getCorrelationId context)

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
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

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
                            let! results = getPromotions branchDto.RepositoryId branchDto.BranchId maxCount (getCorrelationId context)
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

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
                            let! results = getCommits branchDto.RepositoryId branchDto.BranchId maxCount (getCorrelationId context)
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

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
                            let! results = getCheckpoints branchDto.RepositoryId branchDto.BranchId maxCount (getCorrelationId context)
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

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
                            let! results = getSaves branchDto.RepositoryId branchDto.BranchId maxCount (getCorrelationId context)
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

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
                            let! results = getTags branchDto.RepositoryId branchDto.BranchId maxCount (getCorrelationId context)
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

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
                            let! results = getExternals branchDto.RepositoryId branchDto.BranchId maxCount (getCorrelationId context)
                            return results
                        }

                    let! parameters = context |> parse<GetReferencesParameters>
                    let! result = processQuery context parameters validations (parameters.MaxCount) query

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
                                let! latestReference = getLatestReference branchDto.RepositoryId branchDto.BranchId

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

                                let directoryActorProxy = DirectoryVersion.CreateActorProxy referenceDto.DirectoryId repositoryId correlationId

                                let! recursiveSize = directoryActorProxy.GetRecursiveSize correlationId
                                return recursiveSize
                            else
                                match!
                                    Services.getDirectoryVersionResolutionByHashQuery
                                        graceIds.RepositoryId
                                        listContentsParameters.Sha256Hash
                                        listContentsParameters.Blake3Hash
                                        correlationId
                                    with
                                | Services.UniqueMatch directoryVersion ->
                                    let directoryActorProxy = DirectoryVersion.CreateActorProxy directoryVersion.DirectoryVersionId repositoryId correlationId

                                    let! recursiveSize = directoryActorProxy.GetRecursiveSize correlationId
                                    return recursiveSize
                                | Services.NoMatches -> return Constants.InitialDirectorySize
                                | Services.AmbiguousMatches _ ->
                                    setAmbiguousBranchHashLookupError context listContentsParameters.Sha256Hash listContentsParameters.Blake3Hash correlationId

                                    return Constants.InitialDirectorySize
                        }

                    let! parameters = context |> parse<ListContentsParameters>
                    context.Items[ "ListContentsParameters" ] <- parameters
                    let! result = processQuery context parameters validations 1 query

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
                                let! latestReference = getLatestReference branchDto.RepositoryId branchDto.BranchId

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

                                let directoryActorProxy = DirectoryVersion.CreateActorProxy referenceDto.DirectoryId repositoryId correlationId

                                let! contents = directoryActorProxy.GetRecursiveDirectoryVersions listContentsParameters.ForceRecompute correlationId

                                return contents
                            else
                                match!
                                    Services.getRootDirectoryVersionResolutionByHashQuery
                                        graceIds.RepositoryId
                                        listContentsParameters.Sha256Hash
                                        listContentsParameters.Blake3Hash
                                        correlationId
                                    with
                                | Services.UniqueMatch directoryVersion ->
                                    let directoryActorProxy = DirectoryVersion.CreateActorProxy directoryVersion.DirectoryVersionId repositoryId correlationId

                                    let! contents = directoryActorProxy.GetRecursiveDirectoryVersions listContentsParameters.ForceRecompute correlationId

                                    return contents
                                | Services.NoMatches -> return Array.Empty<DirectoryVersion.DirectoryVersionDto>()
                                | Services.AmbiguousMatches _ ->
                                    setAmbiguousBranchHashLookupError context listContentsParameters.Sha256Hash listContentsParameters.Blake3Hash correlationId

                                    return Array.Empty<DirectoryVersion.DirectoryVersionDto>()
                        }

                    let! parameters = context |> parse<ListContentsParameters>
                    context.Items[ "ListContentsParameters" ] <- parameters
                    let! result = processQuery context parameters validations 1 query

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
                match! Services.getRootDirectoryVersionResolutionByHashQuery repositoryId parameters.Sha256Hash parameters.Blake3Hash correlationId with
                | Services.UniqueMatch directoryVersion -> return Some directoryVersion
                | Services.NoMatches -> return None
                | Services.AmbiguousMatches _ ->
                    setAmbiguousBranchHashLookupError context parameters.Sha256Hash parameters.Blake3Hash correlationId
                    return None
            elif
                not
                <| String.IsNullOrEmpty(parameters.ReferenceId)
            then
                return! getRootDirectoryVersionByReferenceId repositoryId (Guid.Parse(parameters.ReferenceId)) correlationId
            else
                let! branchDto = actorProxy.Get(getCorrelationId context)
                let! latestReference = getLatestReference branchDto.RepositoryId branchDto.BranchId

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

                let directoryIds =
                    directoryVersionDtos
                        .Select(fun dv -> dv.DirectoryVersion.DirectoryVersionId)
                        .ToList()

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

    /// Implements update parameters from reference for the server request pipeline.
    let private updateParametersFromReference
        (context: HttpContext)
        (parameters: GetBranchVersionParameters)
        (repositoryIdResolved: Guid)
        (correlationId: CorrelationId)
        =
        task {
            logToConsole $"In Branch.GetVersion: parameters.ReferenceId: {parameters.ReferenceId}"
            let referenceGuid = Guid.Parse(parameters.ReferenceId)
            let referenceActorProxy = Reference.CreateActorProxy referenceGuid repositoryIdResolved correlationId

            let! referenceDto = referenceActorProxy.Get(getCorrelationId context)
            logToConsole $"referenceDto.ReferenceId: {referenceDto.ReferenceId}"
            parameters.BranchId <- $"{referenceDto.BranchId}"
        }

    /// Implements update parameters from sha for the server request pipeline.
    let private updateParametersFromSha (parameters: GetBranchVersionParameters) (repositoryIdFromRoute: Guid) (graceIds: GraceIds) =
        task {
            logToConsole $"In Branch.GetVersion: parameters.Sha256Hash: {parameters.Sha256Hash}"

            let! referenceResult = getReferenceBySha256Hash repositoryIdFromRoute graceIds.BranchId parameters.Sha256Hash

            match referenceResult with
            | Some referenceDto ->
                logToConsole $"referenceDto.ReferenceId: {referenceDto.ReferenceId}"
                parameters.BranchId <- $"{referenceDto.BranchId}"
            | None ->
                // Reference Id was not found in the database.
                ()
        }

    /// Implements update parameters for repository id option for the server request pipeline.
    let private updateParametersForRepositoryIdOption
        (context: HttpContext)
        (graceIds: GraceIds)
        (repositoryIdFromRoute: Guid)
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
                updateParametersFromReference context parameters repositoryIdResolved correlationId
            elif not <| String.IsNullOrEmpty(parameters.Sha256Hash) then
                updateParametersFromSha parameters repositoryIdFromRoute graceIds
            else
                Task.FromResult(())

    /// Executes version lookup by validating inputs, querying the branch actor, and returning the materialized DTO.
    let private getVersionImpl (next: HttpFunc) (context: HttpContext) =
        task {
            let graceIds = getGraceIds context
            let correlationId = getCorrelationId context
            let repositoryIdFromRoute = graceIds.RepositoryId

            let! parameters = context |> parse<GetBranchVersionParameters>

            let! repositoryIdOption =
                resolveRepositoryId graceIds.OwnerId graceIds.OrganizationId parameters.RepositoryId parameters.RepositoryName parameters.CorrelationId

            do! updateParametersForRepositoryIdOption context graceIds repositoryIdFromRoute correlationId parameters repositoryIdOption

            // Now that we've populated BranchId for sure...
            context.Items.Add("GetVersionParameters", parameters)
            return! processQuery context parameters getVersionValidations 1 getVersionQuery
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
